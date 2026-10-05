using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ValheimModPack.InventoryAdmin
{
    // Mod messages are segmented and paced independently of the vanilla game
    // socket. Even a backpack payload cannot be enqueued as one enormous RPC.
    internal sealed class WireTransport
    {
        internal const string RpcName = "VMP_InventoryAdmin_v1";
        private const int Chunk = 16384, Maximum = InventoryCodec.MaximumItemBytes + 65536;
        private sealed class Outgoing { internal ZRpc Rpc; internal byte[] Bytes; internal string Id = Guid.NewGuid().ToString("N"); internal int Offset; internal string Key; internal Func<bool> Guard; }
        private sealed class Incoming { internal string Id; internal byte[] Bytes; internal int Offset; internal float Started; }
        private readonly Queue<Outgoing> queue = new Queue<Outgoing>();
        private readonly Dictionary<ZRpc, Incoming> incoming = new Dictionary<ZRpc, Incoming>();
        private float budgetTime;
        private double credit;
        private long queuedBytes;
        private long incomingBytes;
        internal void Send(ZRpc rpc, byte[] bytes)
        {
            if (rpc == null || !rpc.IsConnected()) throw new IOException("Inventory channel disconnected.");
            if (bytes == null || bytes.Length == 0 || bytes.Length > Maximum || queue.Count >= 64 || queuedBytes + bytes.Length > 16 * 1024 * 1024)
                throw new IOException("Inventory channel capacity exceeded.");
            queue.Enqueue(new Outgoing { Rpc = rpc, Bytes = bytes }); queuedBytes += bytes.Length;
        }
        // Private, short-lived data must be authorized when it actually leaves
        // the queue. Atomic frames allow cancellation without stranding a
        // partially reconstructed inventory message on the receiver.
        internal void SendGuarded(ZRpc rpc, byte[] bytes, string key, Func<bool> guard)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > Chunk || String.IsNullOrEmpty(key) || guard == null)
                throw new InvalidDataException("Guarded messages must fit a single frame.");
            foreach (Outgoing pending in queue)
                if (ReferenceEquals(pending.Rpc, rpc) && pending.Key == key)
                {
                    if (queuedBytes + bytes.Length - pending.Bytes.Length > 16 * 1024 * 1024)
                        throw new IOException("Inventory channel capacity exceeded.");
                    queuedBytes += bytes.Length - pending.Bytes.Length;
                    pending.Bytes = bytes; pending.Guard = guard; return;
                }
            Send(rpc, bytes);
            Outgoing[] messages = queue.ToArray();
            messages[messages.Length - 1].Key = key; messages[messages.Length - 1].Guard = guard;
        }
        internal void Tick()
        {
            float now = Time.realtimeSinceStartup;
            credit = Math.Min(65536, credit + Math.Max(0, now - budgetTime) * 256 * 1024); budgetTime = now;
            for (int i = 0; i < 4 && queue.Count != 0; ++i)
            {
                Outgoing message = queue.Peek();
                bool allowed = message.Rpc.IsConnected();
                if (allowed && message.Guard != null) { try { allowed = message.Guard(); } catch { allowed = false; } }
                if (!allowed) { queue.Dequeue(); queuedBytes -= message.Bytes.Length; continue; }
                int length = Math.Min(Chunk, message.Bytes.Length - message.Offset);
                if (credit < length) break;
                var packet = new ZPackage(); packet.Write(1); packet.Write(message.Id); packet.Write(message.Bytes.Length); packet.Write(message.Offset);
                byte[] bytes = new byte[length]; Buffer.BlockCopy(message.Bytes, message.Offset, bytes, 0, length); packet.Write(bytes);
                message.Rpc.Invoke(RpcName, packet); message.Offset += length; credit -= length;
                if (message.Offset == message.Bytes.Length) { queue.Dequeue(); queuedBytes -= message.Bytes.Length; }
            }
            var expired = new List<ZRpc>();
            foreach (var pair in incoming) if (!pair.Key.IsConnected() || now - pair.Value.Started > 120) expired.Add(pair.Key);
            foreach (var rpc in expired) Remove(rpc);
        }
        internal byte[] Receive(ZRpc rpc, ZPackage packet)
        {
            if (packet.Size() > Chunk + 128 || packet.ReadInt() != 1) throw new InvalidDataException("Invalid inventory channel frame.");
            string id = packet.ReadString(); int total = packet.ReadInt(), offset = packet.ReadInt();
            if (id.Length != 32 || total < 1 || total > Maximum || offset < 0 || offset >= total) throw new InvalidDataException("Invalid inventory chunk.");
            int length = packet.ReadInt();
            if (length < 1 || length > Chunk || length > total - offset || length != packet.Size() - packet.GetPos()) throw new InvalidDataException("Invalid inventory chunk length.");
            Incoming pending;
            if (offset == 0)
            {
                if (incoming.ContainsKey(rpc) || incoming.Count >= 128 || incomingBytes + total > 16 * 1024 * 1024) throw new InvalidDataException("Concurrent inventory message or receive capacity exceeded.");
                pending = new Incoming { Id = id, Bytes = new byte[total], Started = Time.realtimeSinceStartup }; incoming.Add(rpc, pending);
                incomingBytes += total;
            }
            else if (!incoming.TryGetValue(rpc, out pending)) throw new InvalidDataException("Missing inventory message prefix.");
            if (pending.Id != id || pending.Bytes.Length != total || pending.Offset != offset) throw new InvalidDataException("Out of order inventory chunk.");
            Buffer.BlockCopy(packet.GetArray(), packet.GetPos(), pending.Bytes, offset, length); pending.Offset += length;
            if (pending.Offset != total) return null;
            Remove(rpc); return pending.Bytes;
        }
        internal void Remove(ZRpc rpc) { Incoming message; if (incoming.TryGetValue(rpc, out message)) { incomingBytes -= message.Bytes.Length; incoming.Remove(rpc); } }
        internal void Clear() { queue.Clear(); incoming.Clear(); queuedBytes = incomingBytes = 0; credit = 0; budgetTime = Time.realtimeSinceStartup; }
    }
}
