using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Steamworks;
using UnityEngine;

namespace ValheimModPack.NordicRadio
{
    // SteamNetworkingMessages uses its own implicit connection. Valheim uses an
    // explicit SteamNetworkingSockets connection; never put MP3 blocks in that queue.
    internal sealed class SteamRadioTransport : IRadioBulkTransport
    {
        internal const int Channel = 0x564D52; // Dedicated NordicRadio channel.
        private readonly Action<string> log;
        private readonly IntPtr[] incoming = new IntPtr[8];
        private readonly HashSet<ulong> opened = new HashSet<ulong>();
        private Callback<SteamNetworkingMessagesSessionRequest_t> requests;
        private bool disposed;
        private float nextLog, nextMaintenance;

        internal SteamRadioTransport(Action<string> log) { this.log = log ?? delegate { }; }

        private static ulong SteamId(ZNetPeer peer)
        {
            var socket = peer == null || !peer.IsReady() ? null : peer.m_socket as ZSteamSocket;
            return socket == null || !socket.IsConnected() ? 0 : socket.GetPeerID().m_SteamID;
        }

        private static bool Allowed(ZNetPeer peer)
        {
            ZNet net = ZNet.instance;
            return net != null && net.GetWorld() != null && peer != null && peer.IsReady()
                && (net.IsServer() || ReferenceEquals(peer, net.GetServerPeer()));
        }

        private static ZNetPeer FindPeer(ulong steamId)
        {
            if (steamId == 0 || ZNet.instance == null) return null;
            foreach (ZNetPeer peer in ZNet.instance.GetConnectedPeers())
                if (Allowed(peer) && SteamId(peer) == steamId) return peer;
            return null;
        }

        private bool EnsureStarted()
        {
            if (disposed) return false;
            if (requests != null) return true;
            if (ZNet.instance == null || ZNet.instance.GetWorld() == null) return false;
            bool steam = false;
            foreach (ZNetPeer peer in ZNet.instance.GetConnectedPeers())
                if (Allowed(peer) && SteamId(peer) != 0) { steam = true; break; }
            if (!steam) return false;
            // Steam is initialized by Valheim. Do not initialize or pump its callbacks here.
            requests = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnRequest);
            return true;
        }

        private void OnRequest(SteamNetworkingMessagesSessionRequest_t request)
        {
            try
            {
                ulong id = request.m_identityRemote.GetSteamID64();
                if (disposed || FindPeer(id) == null || (opened.Count >= 64 && !opened.Contains(id))) return;
                if (SteamNetworkingMessages.AcceptSessionWithUser(ref request.m_identityRemote)) opened.Add(id);
            }
            catch (Exception error) { Report(error); }
        }

        public bool TrySend(long peerUid, byte[] data, int maxQueuedBytes)
        {
            try
            {
                if (data == null || data.Length < 2 || data.Length > RadioProtocol.MaxPacket || !EnsureStarted()) return false;
                ZNetPeer peer = ZNet.instance.GetPeer(peerUid);
                if (!Allowed(peer)) return false;
                ulong id = SteamId(peer);
                if (id == 0 || (opened.Count >= 64 && !opened.Contains(id))) return false;
                // Yield to gameplay when its OWN queue is already busy. In normal
                // operation our data never increments that queue in the first place.
                if (data[1] == (byte)RadioMessageKind.Chunk && peer.m_socket.GetSendQueueSize() > 4096) return false;
                var identity = new SteamNetworkingIdentity(); identity.SetSteamID64(id);
                SteamNetConnectionInfo_t info; SteamNetConnectionRealTimeStatus_t status;
                SteamNetworkingMessages.GetSessionConnectionInfo(ref identity, out info, out status);
                long queued = Math.Max(0, status.m_cbPendingReliable) + (long)Math.Max(0, status.m_cbPendingUnreliable)
                    + Math.Max(0, status.m_cbSentUnackedReliable);
                int size = data.Length + RadioBulkFrame.HeaderSize;
                // A large playlist may exceed a user's small queue setting: allow
                // one only when the bulk queue is empty, never bypass the bound for audio.
                int limit = data[1] == (byte)RadioMessageKind.Library ? Math.Max(size, maxQueuedBytes) : maxQueuedBytes;
                if (queued + size > limit) return false;
                byte[] frame = RadioBulkFrame.Encode(ZNet.instance.GetWorldUID(), ZNet.GetUID(), peerUid, data);
                GCHandle pin = GCHandle.Alloc(frame, GCHandleType.Pinned);
                try
                {
                    EResult result = SteamNetworkingMessages.SendMessageToUser(ref identity, pin.AddrOfPinnedObject(),
                        (uint)frame.Length, Constants.k_nSteamNetworkingSend_ReliableNoNagle
                        | Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession, Channel);
                    if (result != EResult.k_EResultOK) { Report(new InvalidOperationException("Send returned " + result)); return false; }
                    opened.Add(id); return true;
                }
                finally { pin.Free(); }
            }
            catch (Exception error) { Report(error); return false; }
        }

        public void Poll(Action<long, byte[]> receive)
        {
            try
            {
                if (!EnsureStarted()) return;
                int count = SteamNetworkingMessages.ReceiveMessagesOnChannel(Channel, incoming, incoming.Length);
                // Every pointer returned by Steam must be released, even if one
                // receiver fails. At most eight messages are decoded in one frame.
                for (int i = 0; i < count; i++)
                {
                    IntPtr pointer = incoming[i]; incoming[i] = IntPtr.Zero;
                    if (pointer == IntPtr.Zero) continue;
                    try
                    {
                        SteamNetworkingMessage_t message = SteamNetworkingMessage_t.FromIntPtr(pointer);
                        if (message.m_nChannel != Channel || message.m_cbSize < RadioBulkFrame.HeaderSize + 2
                            || message.m_cbSize > RadioProtocol.MaxPacket + RadioBulkFrame.HeaderSize || message.m_pData == IntPtr.Zero) continue;
                        ZNetPeer peer = FindPeer(message.m_identityPeer.GetSteamID64());
                        if (peer == null) continue;
                        var frame = new byte[message.m_cbSize]; Marshal.Copy(message.m_pData, frame, 0, frame.Length);
                        byte[] data = RadioBulkFrame.Decode(frame, ZNet.instance.GetWorldUID(), peer.m_uid, ZNet.GetUID());
                        if (data != null) receive(peer.m_uid, data);
                    }
                    catch (Exception error) { Report(error); }
                    finally { SteamNetworkingMessage_t.Release(pointer); }
                }
                if (Time.realtimeSinceStartup >= nextMaintenance)
                {
                    nextMaintenance = Time.realtimeSinceStartup + 5;
                    foreach (ulong id in new List<ulong>(opened)) if (FindPeer(id) == null) Close(id);
                }
            }
            catch (Exception error) { Report(error); }
        }

        private void Close(ulong id)
        {
            opened.Remove(id);
            try
            {
                var identity = new SteamNetworkingIdentity(); identity.SetSteamID64(id);
                // Other mods may use other Messages channels. Never close their session.
                SteamNetworkingMessages.CloseChannelWithUser(ref identity, Channel);
            }
            catch (Exception error) { Report(error); }
        }

        public void Reset()
        {
            if (requests != null) { requests.Dispose(); requests = null; }
            foreach (ulong id in new List<ulong>(opened)) Close(id);
            nextMaintenance = 0;
        }

        public void Dispose() { if (disposed) return; disposed = true; Reset(); }
        private void Report(Exception error)
        {
            if (Time.realtimeSinceStartup < nextLog) return;
            nextLog = Time.realtimeSinceStartup + 10;
            log("Steam music channel: " + error.Message);
        }
    }
}
