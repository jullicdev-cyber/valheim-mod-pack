using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace ValheimModPack.PinRemoval
{
    // Independent sidecar protocol. Never changes or appends to Valheim's shared-map format.
    internal static class SharedPinCodec
    {
        internal const int MaximumRecords = 512, MaximumBytes = 128 * 1024;
        private const int Magic = 0x53504D31, Format = 1;
        internal sealed class Payload
        {
            internal long World;
            internal byte[] MapHash;
            internal List<PinRecord> Records = new List<PinRecord>();
        }
        internal static byte[] Hash(byte[] bytes) { using (var sha = SHA256.Create()) return sha.ComputeHash(bytes); }
        internal static bool SameHash(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != 32 || b.Length != 32) return false;
            int difference = 0; for (int i = 0; i < 32; i++) difference |= a[i] ^ b[i]; return difference == 0;
        }
        internal static byte[] Encode(Payload value)
        {
            if (value.World == 0 || value.MapHash == null || value.MapHash.Length != 32 || value.Records.Count > MaximumRecords)
                throw new InvalidDataException("Invalid shared pin metadata header");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic); writer.Write(Format); writer.Write(value.World); writer.Write(value.MapHash); writer.Write(value.Records.Count);
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (PinRecord record in value.Records)
                {
                    Validate(record); if (!keys.Add(record.Key)) throw new InvalidDataException("Duplicate shared pin metadata");
                    writer.Write(record.Type); writer.Write(record.Owner); Text(writer, record.Author, 256);
                    writer.Write(record.X); writer.Write(record.Y); writer.Write(record.Z);
                    writer.Write(record.CreatedUtc); Text(writer, record.CreatorName, 128);
                }
                writer.Flush(); if (stream.Length > MaximumBytes) throw new InvalidDataException("Shared pin metadata exceeds limit");
                return stream.ToArray();
            }
        }
        internal static Payload Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 52 || bytes.Length > MaximumBytes) throw new InvalidDataException("Invalid shared pin metadata size");
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                if (reader.ReadInt32() != Magic || reader.ReadInt32() != Format) throw new InvalidDataException("Unknown shared pin metadata version");
                var result = new Payload { World = reader.ReadInt64(), MapHash = reader.ReadBytes(32) };
                if (result.World == 0 || result.MapHash.Length != 32) throw new InvalidDataException("Invalid shared pin metadata identity");
                int count = reader.ReadInt32(); if (count < 0 || count > MaximumRecords) throw new InvalidDataException("Shared pin count limit");
                var keys = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < count; i++)
                {
                    var record = new PinRecord { Type = reader.ReadInt32(), Owner = reader.ReadInt64(), Author = Text(reader, 256),
                        X = reader.ReadSingle(), Y = reader.ReadSingle(), Z = reader.ReadSingle(), CreatedUtc = reader.ReadInt64(), CreatorName = Text(reader, 128) };
                    Validate(record); if (!keys.Add(record.Key)) throw new InvalidDataException("Duplicate shared pin metadata");
                    result.Records.Add(record);
                }
                if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected shared pin metadata bytes");
                return result;
            }
        }
        private static void Validate(PinRecord record)
        {
            record.Validate();
            if (record.CreatedUtc == 0 || record.Owner == 0 || String.IsNullOrEmpty(record.Author))
                throw new InvalidDataException("Shared metadata needs an actual author and creation date");
        }
        private static void Text(BinaryWriter writer, string value, int limit)
        {
            if (value == null || value.Length > limit) throw new InvalidDataException("Shared text length");
            byte[] encoded = Encoding.UTF8.GetBytes(value); writer.Write(encoded.Length); writer.Write(encoded);
        }
        private static string Text(BinaryReader reader, int limit)
        {
            int count = reader.ReadInt32(); if (count < 0 || count > limit * 4) throw new InvalidDataException("Shared text byte length");
            byte[] bytes = reader.ReadBytes(count); if (bytes.Length != count) throw new EndOfStreamException();
            string text = new UTF8Encoding(false, true).GetString(bytes); if (text.Length > limit) throw new InvalidDataException("Shared text length"); return text;
        }

        // Read-only, bounded parser for the current vanilla SharedMap v3. Unknown formats disable metadata only.
        // The map itself continues through the unchanged native MapTable methods.
        internal static HashSet<string> ReadVanillaKeys(byte[] compressed)
        {
            const int maxMapBytes = 20 * 1024 * 1024;
            if (compressed == null || compressed.Length == 0 || compressed.Length > 4 * 1024 * 1024) throw new InvalidDataException("Shared map size");
            byte[] bytes;
            using (var input = new MemoryStream(compressed, false))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                var buffer = new byte[8192]; int read;
                while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
                { if (output.Length + read > maxMapBytes) throw new InvalidDataException("Expanded shared map size"); output.Write(buffer, 0, read); }
                bytes = output.ToArray();
            }
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                if (reader.ReadInt32() != 3) throw new InvalidDataException("Unsupported native shared map version");
                int explored = reader.ReadInt32();
                if (explored < 0 || explored > 16 * 1024 * 1024 || stream.Length - stream.Position < explored + 4L) throw new InvalidDataException("Explored map bounds");
                stream.Position += explored; // v3 writes one Boolean byte per explored pixel.
                int count = reader.ReadInt32(); if (count < 0 || count > 10000) throw new InvalidDataException("Native shared pin count");
                var keys = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < count; i++)
                {
                    var pin = new PinRecord { Owner = reader.ReadInt64(), Name = NativeText(reader, 256),
                        X = reader.ReadSingle(), Y = reader.ReadSingle(), Z = reader.ReadSingle(), Type = reader.ReadInt32(), Checked = reader.ReadBoolean(), Author = NativeText(reader, 256) };
                    pin.Validate(); keys.Add(pin.Key);
                }
                if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected native shared map bytes");
                return keys;
            }
        }
        private static string NativeText(BinaryReader reader, int limit)
        {
            // BinaryWriter/ZPackage strings use a 7-bit byte count. Check it before allocating.
            int count = 0, shift = 0;
            for (int i = 0; i < 5; i++)
            {
                byte current = reader.ReadByte();
                if (i == 4 && (current & 0xf0) != 0) throw new InvalidDataException("Native string size overflow");
                count |= (current & 0x7f) << shift;
                if ((current & 0x80) == 0)
                {
                    if (count < 0 || count > limit * 4) throw new InvalidDataException("Native string size limit");
                    byte[] bytes = reader.ReadBytes(count); if (bytes.Length != count) throw new EndOfStreamException();
                    string result = new UTF8Encoding(false, true).GetString(bytes); if (result.Length > limit) throw new InvalidDataException("Native text length"); return result;
                }
                shift += 7;
            }
            throw new InvalidDataException("Native string size prefix");
        }
        internal static List<PinRecord> Merge(IEnumerable<PinRecord> previous, IEnumerable<PinRecord> incoming, HashSet<string> present)
        {
            var all = new Dictionary<string, PinRecord>(StringComparer.Ordinal);
            foreach (IEnumerable<PinRecord> source in new[] { previous, incoming })
                foreach (PinRecord record in source)
                {
                    PinRecord known;
                    if (record.CreatedUtc > 0 && present.Contains(record.Key)
                        && (!all.TryGetValue(record.Key, out known) || record.CreatedUtc > known.CreatedUtc)) all[record.Key] = record.Copy();
                }
            var result = new List<PinRecord>(all.Values);
            result.Sort((a, b) => b.CreatedUtc.CompareTo(a.CreatedUtc));
            if (result.Count > MaximumRecords) result.RemoveRange(MaximumRecords, result.Count - MaximumRecords);
            return result;
        }
    }
}
