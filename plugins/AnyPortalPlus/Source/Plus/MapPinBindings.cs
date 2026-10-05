// AnyPortal+ additions, 2026-10-06. GPL-3.0, see ../../LICENSE.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace XPortal.Plus
{
    // A binding owns a particular local saved pin, never all pins with a portal name.
    internal sealed class MapPinBinding
    {
        internal string PortalId, Name, Author;
        internal int Icon;
        internal float X, Y, Z;
        internal long Owner;
        internal MapPinBinding Copy() { return (MapPinBinding)MemberwiseClone(); }
        internal void Validate()
        {
            if (!ValidId(PortalId) || Name == null || Name.Length > 256 || Author == null || Author.Length > 256
                || !ValidIcon(Icon) || !Finite(X) || !Finite(Y) || !Finite(Z))
                throw new InvalidDataException("Invalid AnyPortal+ map pin binding");
        }
        internal bool SamePin(MapPinBinding other)
        {
            return other != null && Icon == other.Icon && X == other.X && Y == other.Y && Z == other.Z
                && Owner == other.Owner && String.Equals(Name, other.Name, StringComparison.Ordinal)
                && String.Equals(Author, other.Author, StringComparison.Ordinal);
        }
        internal bool SameBinding(MapPinBinding other)
        { return other != null && String.Equals(PortalId, other.PortalId, StringComparison.Ordinal) && SamePin(other); }
        internal static bool ValidIcon(int value) { return value >= 0 && value <= 3 || value == 6; }
        internal static bool Finite(float value)
        { return !Single.IsNaN(value) && !Single.IsInfinity(value) && Math.Abs(value) <= 100000f; }
        private static bool ValidId(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length > 64) return false;
            string[] parts = value.Split(':'); long owner; uint id;
            return parts.Length == 2 && Int64.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out owner)
                && UInt32.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out id)
                && owner != 0 && id != 0
                && value == owner.ToString(CultureInfo.InvariantCulture) + ":" + id.ToString(CultureInfo.InvariantCulture);
        }
    }

    internal static class MapPinCleanupPolicy
    {
        internal static void ProtectLiveLocations(ISet<string> liveIds, IList<MapPinBinding> bindings, IList<MapPinBinding> livePositions)
        {
            // Native world loading can remap ZDOIDs. An authoritative portal at
            // the old location means its map pin must be retained conservatively.
            foreach (MapPinBinding binding in bindings)
            {
                if (liveIds.Contains(binding.PortalId)) continue;
                foreach (MapPinBinding position in livePositions)
                {
                    if (position == null || !MapPinBinding.Finite(position.X) || !MapPinBinding.Finite(position.Y) || !MapPinBinding.Finite(position.Z)) continue;
                    double dx = (double)position.X - binding.X, dy = (double)position.Y - binding.Y, dz = (double)position.Z - binding.Z;
                    if (dx * dx + dy * dy + dz * dz <= .01) { liveIds.Add(binding.PortalId); break; }
                }
            }
        }
        internal static bool SameContext(long expectedWorld, long expectedCharacter, long world, long character, bool complete)
        { return complete && world != 0 && character != 0 && world == expectedWorld && character == expectedCharacter; }

        internal static List<MapPinBinding> Select(IList<MapPinBinding> bindings, ISet<string> livePortalIds,
            IList<MapPinBinding> localSavedPins, bool complete)
        {
            var selected = new List<MapPinBinding>();
            if (!complete || bindings == null || livePortalIds == null || localSavedPins == null) return selected;
            foreach (MapPinBinding binding in bindings)
            {
                if (livePortalIds.Contains(binding.PortalId)) continue;
                int matches = 0;
                foreach (MapPinBinding pin in localSavedPins) if (binding.SamePin(pin)) matches++;
                // Duplicates are ambiguous after a reload: preserving both is safer than removing a manual copy.
                if (matches != 1) continue;
                bool alsoLive = false;
                foreach (MapPinBinding other in bindings)
                    if (other.SamePin(binding) && livePortalIds.Contains(other.PortalId)) { alsoLive = true; break; }
                if (!alsoLive) selected.Add(binding.Copy());
            }
            return selected;
        }
    }

    internal sealed class MapPinBindingsStore
    {
        internal const int MaximumBindings = 4096, MaximumBytes = 4 * 1024 * 1024;
        private const int Magic = 0x504D5041, Version = 1;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly string file;
        private readonly long world, character;
        internal MapPinBindingsStore(string path, long worldId, long characterId)
        {
            if (worldId == 0 || characterId == 0 || String.IsNullOrEmpty(path)) throw new ArgumentException("Map pin context required");
            file = path; world = worldId; character = characterId;
        }
        internal List<MapPinBinding> Load()
        {
            if (!File.Exists(file)) return new List<MapPinBinding>();
            var info = new FileInfo(file);
            if (info.Length < 60 || info.Length > MaximumBytes) throw new InvalidDataException("AnyPortal+ map pin file size invalid; original preserved");
            byte[] bytes = File.ReadAllBytes(file);
            if (bytes.Length < 60 || bytes.Length > MaximumBytes) throw new InvalidDataException("AnyPortal+ map pin file size changed");
            int payloadLength = bytes.Length - 32; byte[] hash;
            using (var sha = SHA256.Create()) hash = sha.ComputeHash(bytes, 0, payloadLength);
            for (int i = 0; i < hash.Length; i++) if (hash[i] != bytes[payloadLength + i]) throw new InvalidDataException("AnyPortal+ map pin checksum failed; original preserved");
            using (var stream = new MemoryStream(bytes, 0, payloadLength, false))
            using (var reader = new BinaryReader(stream, Utf8))
            {
                if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version || reader.ReadInt64() != world || reader.ReadInt64() != character)
                    throw new InvalidDataException("AnyPortal+ map pin file belongs to another world/character/version");
                int count = reader.ReadInt32();
                if (count < 0 || count > MaximumBindings) throw new InvalidDataException("AnyPortal+ map pin count invalid");
                var records = new List<MapPinBinding>(count); var ids = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < count; i++)
                {
                    var record = new MapPinBinding { PortalId = ReadText(reader, 64), Name = ReadText(reader, 1024), Icon = reader.ReadInt32(),
                        X = reader.ReadSingle(), Y = reader.ReadSingle(), Z = reader.ReadSingle(), Owner = reader.ReadInt64(), Author = ReadText(reader, 1024) };
                    record.Validate(); if (!ids.Add(record.PortalId)) throw new InvalidDataException("Duplicate AnyPortal+ map pin binding");
                    records.Add(record);
                }
                if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected AnyPortal+ map pin data");
                return records;
            }
        }
        internal void Save(IList<MapPinBinding> records)
        {
            if (records == null || records.Count > MaximumBindings) throw new InvalidDataException("AnyPortal+ map pin limit exceeded");
            byte[] payload; var ids = new HashSet<string>(StringComparer.Ordinal);
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Utf8))
            {
                writer.Write(Magic); writer.Write(Version); writer.Write(world); writer.Write(character); writer.Write(records.Count);
                foreach (MapPinBinding record in records)
                {
                    if (record == null) throw new InvalidDataException("Missing AnyPortal+ map pin binding");
                    record.Validate(); if (!ids.Add(record.PortalId)) throw new InvalidDataException("Duplicate AnyPortal+ map pin binding");
                    WriteText(writer, record.PortalId, 64); WriteText(writer, record.Name, 1024); writer.Write(record.Icon);
                    writer.Write(record.X); writer.Write(record.Y); writer.Write(record.Z); writer.Write(record.Owner); WriteText(writer, record.Author, 1024);
                }
                writer.Flush(); payload = stream.ToArray();
            }
            if (payload.Length + 32 > MaximumBytes) throw new InvalidDataException("AnyPortal+ map pin file limit exceeded");
            string parent = Path.GetDirectoryName(file); if (!String.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var sha = SHA256.Create())
                { output.Write(payload, 0, payload.Length); byte[] hash = sha.ComputeHash(payload); output.Write(hash, 0, hash.Length); output.Flush(true); }
                if (File.Exists(file)) File.Replace(temporary, file, null); else File.Move(temporary, file);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private static string ReadText(BinaryReader reader, int limit)
        {
            int count = reader.ReadInt32(); if (count < 0 || count > limit) throw new InvalidDataException("Map pin text length invalid");
            byte[] bytes = reader.ReadBytes(count); if (bytes.Length != count) throw new EndOfStreamException(); return Utf8.GetString(bytes);
        }
        private static void WriteText(BinaryWriter writer, string value, int limit)
        { byte[] bytes = Utf8.GetBytes(value); if (bytes.Length > limit) throw new InvalidDataException("Map pin text limit exceeded"); writer.Write(bytes.Length); writer.Write(bytes); }
    }
}
