using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace ValheimModPack.NordicRadio
{
    // Valheim accepts every incoming Steam socket callback without checking the
    // listener. Protect only our exact handles before opening a second listener.
    // Keep the tiny filter installed for the process lifetime: queued callbacks
    // may still be dispatched after this transport is reset or disposed.
    internal static class RadioSocketCallbackGuard
    {
        private static readonly HashSet<uint> connections = new HashSet<uint>();
        private static readonly HashSet<uint> listeners = new HashSet<uint>();
        private static readonly Dictionary<uint, float> retiredConnections = new Dictionary<uint, float>();
        private static readonly Dictionary<uint, float> retiredListeners = new Dictionary<uint, float>();
        private static bool installed;

        internal static void Install()
        {
            if (installed) return;
            var target = AccessTools.Method(typeof(ZSteamSocket), "OnStatusChanged", new[] { typeof(SteamNetConnectionStatusChangedCallback_t) });
            if (target == null) throw new MissingMethodException("ZSteamSocket.OnStatusChanged changed; music socket disabled");
            var harmony = new Harmony("valheimmodpack.nordicradio.socket-callback");
            try
            {
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(RadioSocketCallbackGuard), "AllowGameCallback") { priority = Priority.First });
                installed = true;
            }
            catch { harmony.UnpatchSelf(); throw; }
        }

        internal static void AddConnection(HSteamNetConnection handle) { connections.Add(handle.m_HSteamNetConnection); }
        internal static void AddListener(HSteamListenSocket handle) { listeners.Add(handle.m_HSteamListenSocket); }
        internal static void RetireConnection(HSteamNetConnection handle)
        { connections.Remove(handle.m_HSteamNetConnection); Retire(retiredConnections, handle.m_HSteamNetConnection); }
        internal static void RetireListener(HSteamListenSocket handle)
        { listeners.Remove(handle.m_HSteamListenSocket); Retire(retiredListeners, handle.m_HSteamListenSocket); }
        private static void Retire(Dictionary<uint, float> items, uint handle)
        {
            if (handle == 0) return;
            foreach (uint id in new List<uint>(items.Keys)) if (items[id] < Time.realtimeSinceStartup) items.Remove(id);
            // Only a bounded number of short-lived callback tombstones survives reset.
            if (items.Count >= 512) return;
            items[handle] = Time.realtimeSinceStartup + 30;
        }
        private static bool Retired(Dictionary<uint, float> items, uint handle)
        {
            float until;
            if (handle == 0 || !items.TryGetValue(handle, out until)) return false;
            if (until >= Time.realtimeSinceStartup) return true;
            items.Remove(handle); return false;
        }
        internal static bool AllowGameCallback(SteamNetConnectionStatusChangedCallback_t __0)
        {
            return !connections.Contains(__0.m_hConn.m_HSteamNetConnection)
                && !listeners.Contains(__0.m_info.m_hListenSocket.m_HSteamListenSocket)
                && !Retired(retiredConnections, __0.m_hConn.m_HSteamNetConnection)
                && !Retired(retiredListeners, __0.m_info.m_hListenSocket.m_HSteamListenSocket);
        }
    }

    // Explicit P2P socket on a dedicated virtual port, never Valheim's port 0.
    // It has a separate reliable queue and does not alter global Steam limits.
    internal sealed class SteamSocketRadioTransport : IRadioBulkTransport, IRadioTransportHealth, IRadioTransportConnecting
    {
        // Steam virtual ports are 0..65535; Valve recommends application ports
        // below 1000. Port 0 belongs to Valheim. Failure to bind leaves recovery.
        internal const int VirtualPort = 742;
        internal const byte Probe = 252, ProbeReply = 253;
        private readonly Action<string> log;
        private readonly IntPtr[] incoming = new IntPtr[8];
        private readonly Dictionary<long, Link> links = new Dictionary<long, Link>();
        private readonly Dictionary<long, float> retryAfter = new Dictionary<long, float>();
        private Callback<SteamNetConnectionStatusChangedCallback_t> changed;
        private HSteamListenSocket listener = HSteamListenSocket.Invalid;
        private HSteamNetPollGroup pollGroup = HSteamNetPollGroup.Invalid;
        private ZNet session;
        private long world, localUid;
        private bool host, disposed;
        private float nextMaintain, nextStart, nextLog;
        private sealed class Link
        {
            internal long Uid;
            internal ulong SteamId;
            internal HSteamNetConnection Handle;
            internal float Created, LastReceived, NextProbe;
            internal bool Ready;
        }

        internal SteamSocketRadioTransport(Action<string> log) { this.log = log ?? delegate { }; }
        private static ulong SteamId(ZNetPeer peer)
        {
            var socket = peer == null || !peer.IsReady() ? null : peer.m_socket as ZSteamSocket;
            return socket == null || !socket.IsConnected() ? 0 : socket.GetPeerID().m_SteamID;
        }
        private bool Current()
        {
            return !disposed && session != null && ReferenceEquals(session, ZNet.instance)
                && session.GetWorld() != null && session.GetWorldUID() == world && ZNet.GetUID() == localUid && session.IsServer() == host;
        }
        private bool Allowed(ZNetPeer peer)
        { return Current() && peer != null && peer.IsReady() && (host || ReferenceEquals(peer, session.GetServerPeer())); }
        private ZNetPeer FindPeer(ulong id)
        {
            if (id == 0 || !Current()) return null;
            foreach (ZNetPeer peer in session.GetConnectedPeers()) if (Allowed(peer) && SteamId(peer) == id) return peer;
            return null;
        }
        private bool EnsureStarted()
        {
            if (disposed) return false;
            if (session != null && !Current()) Reset();
            if (changed != null) return true;
            ZNet net = ZNet.instance;
            if (Time.realtimeSinceStartup < nextStart || net == null || net.GetWorld() == null) return false;
            bool steam = false;
            foreach (ZNetPeer peer in net.GetConnectedPeers())
                if (peer.IsReady() && (net.IsServer() || ReferenceEquals(peer, net.GetServerPeer())) && SteamId(peer) != 0) { steam = true; break; }
            if (!steam) return false;
            nextStart = Time.realtimeSinceStartup + 10;
            RadioSocketCallbackGuard.Install();
            session = net; world = net.GetWorldUID(); localUid = ZNet.GetUID(); host = net.IsServer();
            try
            {
                pollGroup = SteamNetworkingSockets.CreatePollGroup();
                if (pollGroup == HSteamNetPollGroup.Invalid) throw new InvalidOperationException("Cannot create music poll group");
                changed = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnChanged);
                if (host)
                {
                    listener = SteamNetworkingSockets.CreateListenSocketP2P(VirtualPort, 0, null);
                    if (listener == HSteamListenSocket.Invalid) throw new InvalidOperationException("Cannot open music virtual port");
                    RadioSocketCallbackGuard.AddListener(listener);
                }
                log("Separate Steam music socket initialized.");
                return true;
            }
            catch { Reset(); nextStart = Time.realtimeSinceStartup + 10; throw; }
        }
        private Link FindLink(HSteamNetConnection handle)
        { foreach (Link link in links.Values) if (link.Handle == handle) return link; return null; }
        private Link AddLink(ZNetPeer peer, HSteamNetConnection handle)
        {
            var link = new Link { Uid = peer.m_uid, SteamId = SteamId(peer), Handle = handle, Created = Time.realtimeSinceStartup };
            links.Add(link.Uid, link);
            RadioSocketCallbackGuard.AddConnection(handle);
            if (!SteamNetworkingSockets.SetConnectionPollGroup(handle, pollGroup))
            { Close(link, "Music poll group failed"); return null; }
            return link;
        }
        private void OnChanged(SteamNetConnectionStatusChangedCallback_t data)
        {
            try
            {
                Link link = FindLink(data.m_hConn);
                bool incomingConnection = listener != HSteamListenSocket.Invalid && data.m_info.m_hListenSocket == listener;
                if (link == null && !incomingConnection) return;
                if (!Current()) { Reject(data.m_hConn, "World session ended"); return; }
                if (link == null && data.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting)
                {
                    ZNetPeer peer = FindPeer(data.m_info.m_identityRemote.GetSteamID64());
                    if (!host || peer == null || links.Count >= 64 || links.ContainsKey(peer.m_uid))
                    { Reject(data.m_hConn, "Music requires a unique current game peer"); return; }
                    link = AddLink(peer, data.m_hConn);
                    if (link == null) return;
                    if (SteamNetworkingSockets.AcceptConnection(data.m_hConn) != EResult.k_EResultOK)
                    { Close(link, "Music connection rejected by Steam"); return; }
                }
                if (link == null) return;
                if (data.m_info.m_identityRemote.GetSteamID64() != link.SteamId || FindPeer(link.SteamId) == null)
                { Close(link, "Music peer identity changed"); return; }
                if (data.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected)
                { link.NextProbe = 0; log("Steam music socket connected; checking world session delivery."); }
                else if (data.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer
                    || data.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally)
                {
                    Report(new InvalidOperationException("Connection ended " + data.m_info.m_eEndReason + ": " + data.m_info.m_szEndDebug));
                    Close(link, "Music reconnect");
                }
            }
            catch (Exception error) { Report(error); }
        }
        private void Maintain()
        {
            if (Time.realtimeSinceStartup < nextMaintain) return;
            nextMaintain = Time.realtimeSinceStartup + 1;
            foreach (Link link in new List<Link>(links.Values))
            {
                ZNetPeer peer = session.GetPeer(link.Uid);
                if (!Allowed(peer) || SteamId(peer) != link.SteamId)
                { Close(link, "Game peer disconnected"); continue; }
                SteamNetConnectionInfo_t info;
                if (!SteamNetworkingSockets.GetConnectionInfo(link.Handle, out info)
                    || info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer
                    || info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally)
                { Close(link, "Music connection closed"); continue; }
                if (Time.realtimeSinceStartup - (link.LastReceived > 0 ? link.LastReceived : link.Created) > 30)
                { Close(link, "Music delivery timed out"); continue; }
                if (info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected
                    && Time.realtimeSinceStartup >= link.NextProbe)
                {
                    link.NextProbe = Time.realtimeSinceStartup + (link.Ready ? 10 : 2);
                    SendFrame(link, new byte[] { 2, Probe });
                }
            }
            foreach (long uid in new List<long>(retryAfter.Keys))
                if (session.GetPeer(uid) == null) retryAfter.Remove(uid);
            if (host) return;
            ZNetPeer server = session.GetServerPeer();
            if (!Allowed(server) || SteamId(server) == 0 || links.ContainsKey(server.m_uid) || links.Count >= 64) return;
            float retry;
            if (retryAfter.TryGetValue(server.m_uid, out retry) && Time.realtimeSinceStartup < retry) return;
            retryAfter[server.m_uid] = Time.realtimeSinceStartup + 2;
            var identity = new SteamNetworkingIdentity(); identity.SetSteamID64(SteamId(server));
            HSteamNetConnection connection = SteamNetworkingSockets.ConnectP2P(ref identity, VirtualPort, 0, null);
            if (connection != HSteamNetConnection.Invalid) AddLink(server, connection);
        }
        private bool GetReady(long uid, out Link link)
        {
            link = null;
            if (!Current() || !links.TryGetValue(uid, out link) || !link.Ready) return false;
            ZNetPeer peer = session.GetPeer(uid);
            if (!Allowed(peer) || SteamId(peer) != link.SteamId || Time.realtimeSinceStartup - link.LastReceived > 25) return false;
            SteamNetConnectionInfo_t info;
            return SteamNetworkingSockets.GetConnectionInfo(link.Handle, out info)
                && info.m_identityRemote.GetSteamID64() == link.SteamId
                && info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected;
        }
        public bool IsAvailable(long peer)
        { try { Link link; return GetReady(peer, out link); } catch (Exception error) { Report(error); return false; } }
        public bool IsConnecting(long uid)
        {
            try
            {
                Link link;
                if (!Current()) return false;
                ZNetPeer peer = session.GetPeer(uid);
                if (!Allowed(peer) || SteamId(peer) == 0) return false;
                // Before the first incoming callback the client may still be
                // finding a NAT/relay route. The wrapper bounds this grace.
                if (!links.TryGetValue(uid, out link)) return host && listener != HSteamListenSocket.Invalid;
                if (SteamId(peer) != link.SteamId || Time.realtimeSinceStartup - link.Created > 30) return false;
                SteamNetConnectionInfo_t info;
                return SteamNetworkingSockets.GetConnectionInfo(link.Handle, out info) && info.m_identityRemote.GetSteamID64() == link.SteamId
                    && (info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting
                        || info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_FindingRoute
                        || (!link.Ready && info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected));
            }
            catch (Exception error) { Report(error); return false; }
        }
        public bool TrySend(long peerUid, byte[] data, int maxQueuedBytes)
        {
            try
            {
                Link link;
                if (data == null || data.Length < 2 || data.Length > RadioProtocol.MaxPacket || !EnsureStarted() || !GetReady(peerUid, out link)) return false;
                ZNetPeer peer = session.GetPeer(peerUid);
                if (data[1] == (byte)RadioMessageKind.Chunk && peer.m_socket.GetSendQueueSize() > 4096) return false;
                var status = new SteamNetConnectionRealTimeStatus_t(); var lanes = new SteamNetConnectionRealTimeLaneStatus_t();
                if (SteamNetworkingSockets.GetConnectionRealTimeStatus(link.Handle, ref status, 0, ref lanes) != EResult.k_EResultOK) return false;
                long queued = Math.Max(0, status.m_cbPendingReliable) + (long)Math.Max(0, status.m_cbPendingUnreliable) + Math.Max(0, status.m_cbSentUnackedReliable);
                int size = data.Length + RadioBulkFrame.HeaderSize;
                maxQueuedBytes = Math.Max(32 * 1024, Math.Min(256 * 1024, maxQueuedBytes));
                int limit = data[1] == (byte)RadioMessageKind.Library ? Math.Max(size, maxQueuedBytes) : maxQueuedBytes;
                if (queued + size > limit) return false;
                return SendFrame(link, data);
            }
            catch (Exception error) { Report(error); return false; }
        }
        private bool SendFrame(Link link, byte[] data)
        {
            if (!Current()) return false;
            byte[] frame = RadioBulkFrame.Encode(world, localUid, link.Uid, data);
            GCHandle pin = GCHandle.Alloc(frame, GCHandleType.Pinned);
            try
            {
                long number;
                EResult result = SteamNetworkingSockets.SendMessageToConnection(link.Handle, pin.AddrOfPinnedObject(),
                    (uint)frame.Length, Constants.k_nSteamNetworkingSend_ReliableNoNagle, out number);
                if (result == EResult.k_EResultOK) return true;
                Report(new InvalidOperationException("Send returned " + result)); return false;
            }
            finally { pin.Free(); }
        }
        public void Poll(Action<long, byte[]> receive)
        {
            try
            {
                if (!EnsureStarted()) return;
                Maintain();
                int count = SteamNetworkingSockets.ReceiveMessagesOnPollGroup(pollGroup, incoming, incoming.Length);
                for (int i = 0; i < count; i++)
                {
                    IntPtr pointer = incoming[i]; incoming[i] = IntPtr.Zero;
                    if (pointer == IntPtr.Zero) continue;
                    try
                    {
                        SteamNetworkingMessage_t message = SteamNetworkingMessage_t.FromIntPtr(pointer);
                        Link link = FindLink(message.m_conn);
                        if (link == null || !Current() || message.m_cbSize < RadioBulkFrame.HeaderSize + 2
                            || message.m_cbSize > RadioBulkFrame.HeaderSize + RadioProtocol.MaxPacket || message.m_pData == IntPtr.Zero) continue;
                        ZNetPeer peer = session.GetPeer(link.Uid);
                        if (!Allowed(peer) || SteamId(peer) != link.SteamId || message.m_identityPeer.GetSteamID64() != link.SteamId) continue;
                        byte[] frame = new byte[message.m_cbSize]; Marshal.Copy(message.m_pData, frame, 0, frame.Length);
                        byte[] data = RadioBulkFrame.Decode(frame, world, link.Uid, localUid);
                        if (data == null) continue;
                        link.LastReceived = Time.realtimeSinceStartup;
                        if (!link.Ready) { link.Ready = true; log("Separate Steam music socket delivery confirmed."); }
                        if (data.Length == 2 && data[0] == 2 && data[1] == Probe) SendFrame(link, new byte[] { 2, ProbeReply });
                        else if (!(data.Length == 2 && data[0] == 2 && data[1] == ProbeReply)) receive(link.Uid, data);
                    }
                    catch (Exception error) { Report(error); }
                    finally { SteamNetworkingMessage_t.Release(pointer); }
                }
            }
            catch (Exception error) { Report(error); }
        }
        private void Reject(HSteamNetConnection handle, string reason)
        {
            RadioSocketCallbackGuard.RetireConnection(handle);
            SteamNetworkingSockets.CloseConnection(handle, 1000, reason, false);
        }
        private void Close(Link link, string reason)
        {
            links.Remove(link.Uid); retryAfter[link.Uid] = Time.realtimeSinceStartup + 2;
            Reject(link.Handle, reason);
        }
        public void Reset()
        {
            if (changed != null) { changed.Dispose(); changed = null; }
            foreach (Link link in new List<Link>(links.Values))
                try { Close(link, "Music session reset"); } catch (Exception error) { Report(error); }
            links.Clear(); retryAfter.Clear();
            if (listener != HSteamListenSocket.Invalid)
            {
                RadioSocketCallbackGuard.RetireListener(listener);
                try { SteamNetworkingSockets.CloseListenSocket(listener); } catch (Exception error) { Report(error); }
                listener = HSteamListenSocket.Invalid;
            }
            if (pollGroup != HSteamNetPollGroup.Invalid)
            {
                try { SteamNetworkingSockets.DestroyPollGroup(pollGroup); } catch (Exception error) { Report(error); }
                pollGroup = HSteamNetPollGroup.Invalid;
            }
            session = null; nextMaintain = nextStart = 0;
        }
        public void Dispose() { if (disposed) return; disposed = true; Reset(); }
        private void Report(Exception error)
        {
            if (Time.realtimeSinceStartup < nextLog) return;
            nextLog = Time.realtimeSinceStartup + 10; log("Steam music socket: " + error.Message);
        }
    }
}
