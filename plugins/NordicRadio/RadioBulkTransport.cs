using System;
using System.IO;

namespace ValheimModPack.NordicRadio
{
    // Bulk frames use a separate connection. Recovery must fragment and ACK them
    // below the game's ZDO queue budget; never enqueue a complete bulk frame there.
    // All transport calls happen on the game thread; native networking queues asynchronously.
    internal interface IRadioBulkTransport : IDisposable
    {
        void Poll(Action<long, byte[]> receive);
        bool TrySend(long peer, byte[] data, int maxQueuedBytes);
        void Reset();
    }

    // Optional health signal, independent of per-send flow control. A connected
    // channel can legitimately refuse more bytes while either queue is busy.
    internal interface IRadioTransportHealth
    {
        bool IsAvailable(long peer);
    }

    internal interface IRadioTransportConnecting
    {
        bool IsConnecting(long peer);
    }

    // In recovery a complete chunk may take longer than the service timeout.
    // Report only new bytes of the requested song, never duplicate fragments or metadata.
    internal interface IRadioChunkProgress
    {
        float GetChunkProgress(long peer, string id);
    }

    internal static class RadioBulkFrame
    {
        internal const int HeaderSize = 28;
        private const int Magic = 0x33524D56; // VMR3; independent of the gameplay RPC.

        internal static byte[] Encode(long world, long sender, long target, byte[] payload)
        {
            if (payload == null || payload.Length < 2 || payload.Length > RadioProtocol.MaxPacket)
                throw new InvalidDataException("Invalid radio bulk payload size");
            using (var stream = new MemoryStream(HeaderSize + payload.Length))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Magic); writer.Write(world); writer.Write(sender); writer.Write(target);
                writer.Write(payload); return stream.ToArray();
            }
        }

        internal static byte[] Decode(byte[] frame, long world, long sender, long target)
        {
            if (frame == null || frame.Length < HeaderSize + 2 || frame.Length > HeaderSize + RadioProtocol.MaxPacket) return null;
            using (var stream = new MemoryStream(frame, false))
            using (var reader = new BinaryReader(stream))
            {
                if (reader.ReadInt32() != Magic || reader.ReadInt64() != world
                    || reader.ReadInt64() != sender || reader.ReadInt64() != target) return null;
                return reader.ReadBytes(frame.Length - HeaderSize);
            }
        }
    }
}
