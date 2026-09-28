using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ValheimModPack.NordicRadio
{
    // Recovery path only. A bulk packet is NEVER inserted into the game queue.
    // 3 acknowledged-in-flight fragments plus framing occupy < 3 KiB. Each send
    // additionally reserves room below the game's 8 KiB ZDO starvation boundary.
    internal sealed class RoutedRadioTransport : IRadioBulkTransport
    {
        internal const string RpcName = "VMP_RadioRecovery_1";
        internal const int FragmentSize = 768, Window = 3, QueueCeiling = 4096;
        private const int HeaderSize = 41, MaxFrame = RadioProtocol.MaxPacket + RadioBulkFrame.HeaderSize;
        private readonly Dictionary<long, Peer> peers = new Dictionary<long, Peer>();
        private ZRoutedRpc registered;
        private Action<long, byte[]> receiver;
        private long world, local, sequence;
        private int cursor;
        private bool disposed;
        private sealed class Outgoing
        {
            internal long Id;
            internal byte[] Frame;
            internal int Sent, Acked, Retries;
            internal float Progress;
        }
        private sealed class Incoming
        {
            internal long Id;
            internal int Total, Offset;
            internal byte[] Frame;
            internal float Progress;
        }
        private sealed class Peer
        {
            internal readonly Queue<Outgoing> Out = new Queue<Outgoing>();
            internal int Bytes;
            internal Incoming In;
            internal bool AckPending;
        }
        private static float Now { get { return Time.realtimeSinceStartup; } }
        private static ZNetPeer Allowed(long uid)
        {
            ZNet net = ZNet.instance;
            var peer = net == null ? null : net.GetPeer(uid);
            return net != null && net.GetWorld() != null && peer != null && peer.IsReady()
                && (net.IsServer() || ReferenceEquals(peer, net.GetServerPeer())) ? peer : null;
        }
        private bool Current()
        {
            return !disposed && registered != null && ReferenceEquals(registered, ZRoutedRpc.instance)
                && ZNet.instance != null && ZNet.instance.GetWorld() != null
                && world == ZNet.instance.GetWorldUID() && local == ZNet.GetUID();
        }
        private Peer GetPeer(long uid)
        {
            Peer peer;
            if (!peers.TryGetValue(uid, out peer))
            {
                if (peers.Count >= 64) return null;
                peers.Add(uid, peer = new Peer());
            }
            return peer;
        }
        public bool TrySend(long uid, byte[] data, int maxQueuedBytes)
        {
            if (!Current() || Allowed(uid) == null || data == null || data.Length < 2 || data.Length > RadioProtocol.MaxPacket) return false;
            Peer peer = GetPeer(uid);
            int size = data.Length + RadioBulkFrame.HeaderSize;
            int limit = Math.Max(size, Math.Min(256 * 1024, Math.Max(1024, maxQueuedBytes)));
            if (peer == null) return false;
            foreach (Outgoing pending in peer.Out)
            {
                if (pending.Frame.Length != size) continue;
                bool equal = true;
                for (int i = 0; i < data.Length; i++) if (pending.Frame[i + RadioBulkFrame.HeaderSize] != data[i]) { equal = false; break; }
                if (equal) return true;
            }
            if (peer.Out.Count >= 8 || peer.Bytes + size > limit) return false;
            peer.Out.Enqueue(new Outgoing { Id = ++sequence, Frame = RadioBulkFrame.Encode(world, local, uid, data), Progress = Now });
            peer.Bytes += size;
            return true;
        }
        public void Poll(Action<long, byte[]> receive)
        {
            if (disposed || ZNet.instance == null || ZNet.instance.GetWorld() == null || ZRoutedRpc.instance == null) return;
            long currentWorld = ZNet.instance.GetWorldUID(), currentLocal = ZNet.GetUID();
            if (world != currentWorld || local != currentLocal || !ReferenceEquals(registered, ZRoutedRpc.instance)) peers.Clear();
            world = currentWorld; local = currentLocal; receiver = receive;
            if (!ReferenceEquals(registered, ZRoutedRpc.instance))
            { registered = ZRoutedRpc.instance; registered.Register<ZPackage>(RpcName, OnMessage); }
            var ids = new List<long>(peers.Keys);
            int sends = 0;
            for (int i = 0; i < ids.Count; i++)
            {
                long uid = ids[(cursor + i) % ids.Count];
                ZNetPeer connected = Allowed(uid);
                if (connected == null) { peers.Remove(uid); continue; }
                Peer peer = peers[uid];
                if (peer.In != null && Now - peer.In.Progress > 90) { peer.In = null; peer.AckPending = false; }
                if (sends >= 8) continue;
                if (peer.AckPending && peer.In != null && Send(connected, 2, peer.In.Id, peer.In.Total, peer.In.Offset, null, 0))
                { peer.AckPending = false; sends++; }
                if (peer.Out.Count == 0 || sends >= 8) continue;
                Outgoing outgoing = peer.Out.Peek();
                if (Now - outgoing.Progress > 10)
                {
                    if (++outgoing.Retries > 3)
                    { peer.Bytes -= peer.Out.Dequeue().Frame.Length; if (peer.Out.Count > 0) peer.Out.Peek().Progress = Now; continue; }
                    outgoing.Sent = outgoing.Acked; outgoing.Progress = Now;
                }
                int count = Math.Min(FragmentSize, outgoing.Frame.Length - outgoing.Sent);
                if (count <= 0 || outgoing.Sent - outgoing.Acked + count > Window * FragmentSize) continue;
                if (Send(connected, 1, outgoing.Id, outgoing.Frame.Length, outgoing.Sent, outgoing.Frame, count))
                { outgoing.Sent += count; sends++; }
            }
            if (ids.Count > 0) cursor = (cursor + 1) % ids.Count;
        }
        private bool Send(ZNetPeer peer, byte kind, long id, int total, int offset, byte[] frame, int count)
        {
            // 128 bytes are reserved for ZRoutedRpc/ZRpc/package framing. This
            // check applies to acks too; congestion delays radio, never gameplay.
            if (peer.m_socket == null || peer.m_socket.GetSendQueueSize() + HeaderSize + count + 128 > QueueCeiling) return false;
            using (var stream = new MemoryStream(HeaderSize + count))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(kind); writer.Write(world); writer.Write(local); writer.Write(peer.m_uid);
                writer.Write(id); writer.Write(total); writer.Write(offset);
                if (count > 0) writer.Write(frame, offset, count);
                registered.InvokeRoutedRPC(peer.m_uid, RpcName, new ZPackage(stream.ToArray()));
                return true;
            }
        }
        private void OnMessage(long sender, ZPackage package)
        {
            if (!Current() || Allowed(sender) == null || package == null || package.Size() < HeaderSize || package.Size() > HeaderSize + FragmentSize) return;
            using (var stream = new MemoryStream(package.GetArray(), false))
            using (var reader = new BinaryReader(stream))
            {
                byte kind = reader.ReadByte();
                if (reader.ReadInt64() != world || reader.ReadInt64() != sender || reader.ReadInt64() != local) return;
                long id = reader.ReadInt64(); int total = reader.ReadInt32(), offset = reader.ReadInt32();
                int size = (int)(stream.Length - stream.Position);
                if (id <= 0 || total < RadioBulkFrame.HeaderSize + 2 || total > MaxFrame || offset < 0 || offset > total) return;
                if (kind == 2)
                {
                    Peer peer;
                    if (size != 0 || !peers.TryGetValue(sender, out peer) || peer.Out.Count == 0) return;
                    Outgoing outgoing = peer.Out.Peek();
                    if (id != outgoing.Id || total != outgoing.Frame.Length || offset <= outgoing.Acked || offset > outgoing.Sent
                        || (offset != total && offset % FragmentSize != 0)) return;
                    outgoing.Acked = offset; outgoing.Progress = Now; outgoing.Retries = 0;
                    if (offset == total)
                    { peer.Bytes -= peer.Out.Dequeue().Frame.Length; if (peer.Out.Count > 0) peer.Out.Peek().Progress = Now; }
                    return;
                }
                if (kind != 1 || offset % FragmentSize != 0 || size != Math.Min(FragmentSize, total - offset) || size == 0) return;
                Peer target = GetPeer(sender); if (target == null) return;
                Incoming incoming = target.In;
                if (incoming == null || id > incoming.Id)
                {
                    if (offset != 0) return;
                    target.In = incoming = new Incoming { Id = id, Total = total, Frame = new byte[total], Progress = Now };
                }
                if (id != incoming.Id || total != incoming.Total) return;
                if (offset == incoming.Offset && incoming.Frame != null)
                {
                    reader.Read(incoming.Frame, offset, size); incoming.Offset += size; incoming.Progress = Now;
                    if (incoming.Offset == total)
                    {
                        byte[] data = RadioBulkFrame.Decode(incoming.Frame, world, sender, local);
                        incoming.Frame = null; target.AckPending = true;
                        if (data != null && receiver != null) receiver(sender, data);
                    }
                }
                // Duplicate fragments receive cumulative ACKs without redelivery.
                target.AckPending = true;
            }
        }
        public void Reset() { peers.Clear(); receiver = null; world = local = 0; cursor = 0; }
        public void Dispose() { disposed = true; Reset(); }
    }

    internal sealed class RecoveringRadioTransport : IRadioBulkTransport
    {
        private readonly IRadioBulkTransport steam, routed;
        private readonly Action<string> log;
        private readonly Dictionary<long, float> waiting = new Dictionary<long, float>();
        private readonly HashSet<long> notified = new HashSet<long>();
        internal RecoveringRadioTransport(IRadioBulkTransport steam, IRadioBulkTransport routed, Action<string> log)
        { this.steam = steam; this.routed = routed; this.log = log ?? delegate { }; }
        public void Poll(Action<long, byte[]> receive)
        {
            // Once recovery is selected, keep one ordered route until reconnect.
            // Otherwise a delayed old playlist from one route could overwrite a
            // newer playlist from the other after Steam recovers.
            steam.Poll((uid, data) => { if (!notified.Contains(uid)) receive(uid, data); });
            routed.Poll((uid, data) =>
            {
                // Once the host reached us through recovery, requests can use
                // the same path immediately instead of waiting another 10 s.
                if (waiting.Count < 64 || waiting.ContainsKey(uid)) waiting[uid] = Time.realtimeSinceStartup - 10;
                SelectRecovery(uid);
                receive(uid, data);
            });
            foreach (long uid in new List<long>(waiting.Keys))
                if (ZNet.instance == null || ZNet.instance.GetPeer(uid) == null) { waiting.Remove(uid); notified.Remove(uid); }
        }
        public bool TrySend(long peer, byte[] data, int maxQueuedBytes)
        {
            if (notified.Contains(peer)) return routed.TrySend(peer, data, maxQueuedBytes);
            if (steam.TrySend(peer, data, maxQueuedBytes)) { waiting.Remove(peer); notified.Remove(peer); return true; }
            float started;
            if (!waiting.TryGetValue(peer, out started))
            { if (waiting.Count >= 64) return false; waiting.Add(peer, started = Time.realtimeSinceStartup); }
            if (Time.realtimeSinceStartup - started < 10) return false;
            bool sent = routed.TrySend(peer, data, maxQueuedBytes);
            if (sent) SelectRecovery(peer);
            return sent;
        }
        private void SelectRecovery(long peer)
        {
            if (notified.Count < 64 && notified.Add(peer))
                log("Steam music delivery unavailable or congested; using acknowledged 768-byte recovery fragments with a 4 KiB game-queue ceiling until reconnect.");
        }
        public void Reset() { waiting.Clear(); notified.Clear(); steam.Reset(); routed.Reset(); }
        public void Dispose() { Reset(); steam.Dispose(); routed.Dispose(); }
    }
}
