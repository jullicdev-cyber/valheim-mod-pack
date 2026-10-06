using System;
using System.IO;
using System.Text;

namespace ValheimModPack.PartyPrison
{
    // Complete messages are bounded before the transport fragments them.
    // Custody inventory blobs may be up to 4MiB; command authority stays host-side.
    internal static class PrisonProtocol
    {
        internal const int Version = 2;
        internal const int MaximumBlobBytes = 4 * 1024 * 1024;
        internal const int MaximumBytes = MaximumBlobBytes + 8192;
        internal const int State = 1, Heartbeat = 2, ReleaseAck = 3, Wave = 4,
            InventoryOffer = 5, InventoryClear = 6, InventoryCleared = 7,
            WithdrawalRequest = 8, WithdrawalGrant = 9, WithdrawalAck = 10;
        internal static void WriteHeader(BinaryWriter writer, long world, int kind)
        {
            if (writer == null) throw new ArgumentNullException("writer");
            if (world == 0 || kind < State || kind > WithdrawalAck) throw new InvalidDataException("Invalid prison protocol world or message kind.");
            if (writer.BaseStream.Position != 0) throw new InvalidDataException("Prison header must begin the message.");
            writer.Write(Version); writer.Write(world); writer.Write(kind);
        }
        internal static int ReadHeader(BinaryReader reader, long expectedWorld)
        {
            if (reader == null) throw new ArgumentNullException("reader");
            if (expectedWorld == 0 || reader.BaseStream.Position != 0 || reader.BaseStream.Length < 16 || reader.BaseStream.Length > MaximumBytes)
                throw new InvalidDataException("Invalid prison message size, world or header position.");
            if (reader.ReadInt32() != Version || reader.ReadInt64() != expectedWorld)
                throw new InvalidDataException("Prison protocol/world mismatch.");
            int kind = reader.ReadInt32();
            if (kind < State || kind > WithdrawalAck) throw new InvalidDataException("Unknown prison message kind.");
            return kind;
        }
        internal static void Blob(BinaryWriter writer, byte[] bytes)
        {
            if (writer == null) throw new ArgumentNullException("writer");
            if (bytes == null) throw new ArgumentNullException("bytes");
            if (bytes.Length > MaximumBlobBytes) throw new InvalidDataException("Prison inventory blob too large.");
            writer.Write(bytes.Length); writer.Write(bytes);
        }
        internal static byte[] Blob(BinaryReader reader)
        {
            if (reader == null) throw new ArgumentNullException("reader");
            int count = reader.ReadInt32();
            if (count < 0 || count > MaximumBlobBytes || count > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("Invalid prison inventory blob length.");
            byte[] bytes = reader.ReadBytes(count);
            if (bytes.Length != count) throw new InvalidDataException("Truncated prison inventory blob.");
            return bytes;
        }
        internal static void Text(BinaryWriter writer, string text)
        {
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(text ?? "");
            if (bytes.Length > 1024) throw new InvalidDataException("Prison text too long.");
            writer.Write(bytes.Length); writer.Write(bytes);
        }
        internal static string Text(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > 1024 || count > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("Invalid prison text length.");
            return new UTF8Encoding(false, true).GetString(reader.ReadBytes(count));
        }
        internal static bool Flag(BinaryReader reader)
        { byte value = reader.ReadByte(); if (value > 1) throw new InvalidDataException("Invalid prison flag."); return value == 1; }
        internal static void End(BinaryReader reader)
        { if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Trailing prison data."); }
        internal static void Point(BinaryWriter writer, PrisonPoint value)
        { writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z); }
        internal static PrisonPoint Point(BinaryReader reader)
        {
            var value = new PrisonPoint { X = reader.ReadDouble(), Y = reader.ReadDouble(), Z = reader.ReadDouble() };
            if (!Finite(value.X) || !Finite(value.Y) || !Finite(value.Z)) throw new InvalidDataException("Invalid prison position.");
            return value;
        }
        private static bool Finite(double v) { return SentencePolicy.IsFinite(v) && Math.Abs(v) <= 1000000; }
        internal static void Region(BinaryWriter writer, PrisonRegion region)
        {
            writer.Write(region != null); if (region == null) return;
            Point(writer, region.Center); writer.Write(region.Radius); writer.Write(region.HalfHeight);
            Point(writer, region.CellSpawn); Point(writer, region.ArenaSpawn);
        }
        internal static PrisonRegion Region(BinaryReader reader)
        {
            if (!Flag(reader)) return null;
            var value = new PrisonRegion { Center = Point(reader), Radius = reader.ReadDouble(), HalfHeight = reader.ReadDouble(), CellSpawn = Point(reader), ArenaSpawn = Point(reader) };
            if (!Finite(value.Radius) || !Finite(value.HalfHeight) || value.Radius < 6 || value.Radius > 100 || value.HalfHeight < 4 || value.HalfHeight > 100
                || !value.Contains(value.CellSpawn) || !value.Contains(value.ArenaSpawn)) throw new InvalidDataException("Invalid prison boundary.");
            return value;
        }
        internal static void Sentence(BinaryWriter writer, SentenceState value)
        {
            writer.Write(value != null); if (value == null) return;
            Text(writer, value.AccountId); Text(writer, value.SentenceId); Text(writer, value.PlayerName); Text(writer, value.Reason);
            writer.Write(value.RemainingSeconds); Point(writer, value.ReturnPosition); writer.Write(value.PendingRelease); writer.Write(value.Revision);
        }
        internal static SentenceState Sentence(BinaryReader reader)
        {
            if (!Flag(reader)) return null;
            var value = new SentenceState { AccountId = Text(reader), SentenceId = Text(reader), PlayerName = Text(reader), Reason = Text(reader), RemainingSeconds = reader.ReadDouble(),
                ReturnPosition = Point(reader), PendingRelease = Flag(reader), Revision = reader.ReadInt64() };
            Guid token;
            if (value.AccountId.Length == 0 || value.SentenceId.Length != 32 || !Guid.TryParseExact(value.SentenceId, "N", out token)
                || value.Revision < 1 || Double.IsNaN(value.RemainingSeconds) || Double.IsInfinity(value.RemainingSeconds)
                || value.RemainingSeconds < 0 || value.RemainingSeconds > SentencePolicy.MaximumDurationSeconds || value.PendingRelease != (value.RemainingSeconds == 0))
                throw new InvalidDataException("Invalid prison sentence.");
            SentencePolicy.RequireSentence(value); return value;
        }
    }
}
