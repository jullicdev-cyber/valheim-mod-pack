using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
namespace ValheimModPack.PinRemoval
{
    // This format belongs to this plugin, never to Valheim's character or world saves.
    public sealed class PinRecord
    {
        public string Id = "", Name = "", Author = "", CreatorName = "";
        public string PresetKey = "", BoundName = "";
        public int Type;
        public float X, Y, Z, WorldSize;
        public long Owner, CreatedUtc, DeletedUtc, RestoredUtc;
        public bool Checked, DoubleSize, Animate;
        public PinRecord Copy() { return (PinRecord)MemberwiseClone(); }
        public string Key
        {
            get
            {
                return Type + ":" + Owner.ToString(CultureInfo.InvariantCulture) + ":" + Author + ":"
                    + X.ToString("R", CultureInfo.InvariantCulture) + ":" + Y.ToString("R", CultureInfo.InvariantCulture)
                    + ":" + Z.ToString("R", CultureInfo.InvariantCulture);
            }
        }
        public void Validate()
        {
            if (Name == null || Name.Length > 256 || Author == null || Author.Length > 256
                || CreatorName == null || CreatorName.Length > 128 || Type < 0 || Type > 1024
                || !Finite(X) || !Finite(Y) || !Finite(Z) || !Finite(WorldSize) || WorldSize < 0
                || !ValidTime(CreatedUtc) || !ValidTime(DeletedUtc) || !ValidTime(RestoredUtc)
                || !ValidPresetKey(PresetKey) || BoundName == null || BoundName.Length > 256)
                throw new InvalidDataException("Invalid map pin history record");
        }
        private static bool ValidPresetKey(string value)
        {
            if (value == null || value.Length > 96) return false;
            if (value.Length == 0) return true;
            Guid guid;
            if (Guid.TryParseExact(value, "N", out guid)) return guid != Guid.Empty && value == guid.ToString("N");
            if (!value.StartsWith("default.", StringComparison.Ordinal) || value.Length <= 8) return false;
            for (int i = 8; i < value.Length; i++)
            { char c = value[i]; if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '_' && c != '-' && c != '.') return false; }
            return true;
        }
        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value) && Math.Abs(value) <= 1000000; }
        private static bool ValidTime(long value) { return value >= 0 && value <= DateTime.MaxValue.Ticks; }
    }
    public enum RestoreResult { Restored, AlreadyRestored, AlreadyPresent, Missing }
    public sealed class PinArchive
    {
        public const int MaximumDeleted = 500, MaximumMetadata = 5000, MaximumFileBytes = 4 * 1024 * 1024;
        private const int Magic = 0x50494E48, Format = 2;
        private readonly string file;
        public readonly long World, Character;
        private readonly List<PinRecord> deleted = new List<PinRecord>();
        private readonly Dictionary<string, PinRecord> metadata = new Dictionary<string, PinRecord>(StringComparer.Ordinal);
        public IList<PinRecord> Deleted { get { return deleted.AsReadOnly(); } }
        public PinArchive(string file, long world, long character)
        {
            if (world == 0 || character == 0) throw new ArgumentException("A loaded world and character are required");
            this.file = file; World = world; Character = character;
            if (File.Exists(file)) Load();
        }
        public void Enrich(PinRecord pin)
        {
            PinRecord known;
            if (metadata.TryGetValue(pin.Key, out known))
            {
                pin.CreatedUtc = known.CreatedUtc; pin.CreatorName = known.CreatorName;
                pin.PresetKey = known.BoundName == pin.Name ? known.PresetKey : "";
                // Empty key + matching baseline is an explicit local literal-name override.
                pin.BoundName = known.BoundName == pin.Name ? known.BoundName : "";
            }
        }
        public string PresetFor(PinRecord current)
        {
            if (current == null) return "";
            PinRecord known;
            return metadata.TryGetValue(current.Key, out known) && known.BoundName == current.Name ? known.PresetKey : "";
        }
        public void BindPreset(PinRecord pin, string presetKey)
        {
            if (pin == null) throw new ArgumentNullException("pin");
            pin = pin.Copy(); Enrich(pin);
            pin.PresetKey = presetKey ?? "";
            pin.BoundName = pin.Name;
            pin.Validate(); SaveMetadata(pin);
        }
        public void RememberCreation(PinRecord pin, string creator, long utc)
        {
            pin = pin.Copy(); pin.CreatorName = creator ?? ""; pin.CreatedUtc = utc; pin.Validate();
            SaveMetadata(pin);
        }
        private void SaveMetadata(PinRecord pin)
        {
            PinRecord previous; bool existed = metadata.TryGetValue(pin.Key, out previous);
            metadata[pin.Key] = pin;
            PinRecord evicted = null;
            if (metadata.Count > MaximumMetadata)
            {
                foreach (var value in metadata.Values)
                    if (value.Key != pin.Key && (evicted == null || value.CreatedUtc < evicted.CreatedUtc)) evicted = value;
                if (evicted != null) metadata.Remove(evicted.Key);
            }
            try { Save(); }
            catch { if (existed) metadata[pin.Key] = previous; else metadata.Remove(pin.Key);
                if (evicted != null) metadata[evicted.Key] = evicted; throw; }
        }
        public void ImportCreationMetadata(IEnumerable<PinRecord> records)
        {
            var previous = new Dictionary<string, PinRecord>(metadata, StringComparer.Ordinal);
            bool changed = false;
            try
            {
                foreach (PinRecord record in records)
                {
                    record.Validate(); if (record.CreatedUtc == 0) continue;
                    PinRecord existing;
                    // Own/adopted pins keep their recorded origin. Shared pins may be recreated at the
                    // same native identity; newer known origins win, and stale relays cannot roll them back.
                    bool existed = metadata.TryGetValue(record.Key, out existing);
                    if (existed && existing.CreatedUtc != 0 && (record.Owner == 0 || record.CreatedUtc <= existing.CreatedUtc))
                    {
                        bool backfill = record.Owner != 0 && record.CreatedUtc == existing.CreatedUtc
                            && existing.PresetKey.Length == 0 && existing.BoundName.Length == 0
                            && existing.Name == record.Name && record.PresetKey.Length != 0 && record.BoundName == record.Name;
                        if (!backfill) continue;
                        PinRecord enriched = existing.Copy(); enriched.PresetKey = record.PresetKey; enriched.BoundName = record.BoundName;
                        metadata[record.Key] = enriched; changed = true; continue;
                    }
                    PinRecord replacement = record.Copy();
                    if (existed && existing.PresetKey.Length == 0 && existing.BoundName.Length != 0 && existing.BoundName == record.Name)
                    { replacement.PresetKey = ""; replacement.BoundName = existing.BoundName; }
                    metadata[record.Key] = replacement; changed = true;
                }
                while (metadata.Count > MaximumMetadata)
                {
                    PinRecord oldest = null;
                    foreach (PinRecord record in metadata.Values) if (oldest == null || record.CreatedUtc < oldest.CreatedUtc) oldest = record;
                    metadata.Remove(oldest.Key);
                }
                if (changed) Save();
            }
            catch { metadata.Clear(); foreach (var entry in previous) metadata.Add(entry.Key, entry.Value); throw; }
        }
        public PinRecord RecordBeforeDelete(PinRecord pin, long utc)
        {
            return RecordManyBeforeDelete(new[] { pin }, utc)[0];
        }
        // Stage and validate the complete batch before changing any state. One
        // atomic replacement commits the retained recovery history before the
        // game is allowed to remove even its first pin. As with repeated single
        // deletions, only the latest MaximumDeleted snapshots are retained.
        public IList<PinRecord> RecordManyBeforeDelete(IEnumerable<PinRecord> pins, long utc)
        {
            if (pins == null) throw new ArgumentNullException("pins");
            if (utc <= 0 || utc > DateTime.MaxValue.Ticks) throw new ArgumentOutOfRangeException("utc");
            var staged = new List<PinRecord>();
            foreach (PinRecord source in pins)
            {
                if (source == null) throw new ArgumentException("Deletion batch contains a missing pin", "pins");
                PinRecord pin = source.Copy(); Enrich(pin); pin.Id = Guid.NewGuid().ToString("N");
                pin.DeletedUtc = utc; pin.RestoredUtc = 0; pin.Validate();
                staged.Add(pin);
            }
            if (staged.Count == 0) return staged.AsReadOnly();
            var previous = new List<PinRecord>(deleted);
            deleted.AddRange(staged);
            if (deleted.Count > MaximumDeleted) deleted.RemoveRange(0, deleted.Count - MaximumDeleted);
            try { Save(); }
            catch { deleted.Clear(); deleted.AddRange(previous); throw; }
            return staged.AsReadOnly();
        }
        public RestoreResult Restore(string id, Func<PinRecord, bool> exists, Action<PinRecord> add, long utc)
        {
            PinRecord entry = deleted.Find(value => value.Id == id);
            if (entry == null) return RestoreResult.Missing;
            if (entry.RestoredUtc != 0) return RestoreResult.AlreadyRestored;
            if (utc <= 0 || utc > DateTime.MaxValue.Ticks) throw new ArgumentOutOfRangeException("utc");
            bool present = exists(entry.Copy());
            if (!present) add(entry.Copy());
            entry.RestoredUtc = utc;
            // On a disk error retain the recoverable record. The live pin is checked on every attempt.
            try { Save(); } catch { entry.RestoredUtc = 0; throw; }
            return present ? RestoreResult.AlreadyPresent : RestoreResult.Restored;
        }
        private void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file)));
            byte[] payload;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic); writer.Write(Format); writer.Write(World); writer.Write(Character);
                writer.Write(deleted.Count); foreach (var entry in deleted) Write(writer, entry);
                writer.Write(metadata.Count); foreach (var entry in metadata.Values) Write(writer, entry);
                writer.Flush(); payload = stream.ToArray();
            }
            if (payload.Length + 32 > MaximumFileBytes) throw new InvalidDataException("Map history limit exceeded");
            byte[] digest; using (var hash = SHA256.Create()) digest = hash.ComputeHash(payload);
            string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(payload, 0, payload.Length); stream.Write(digest, 0, digest.Length); stream.Flush(true); }
                if (File.Exists(file)) File.Replace(temporary, file, file + ".bak");
                else File.Move(temporary, file);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private void Load()
        {
            var info = new FileInfo(file);
            if (info.Length < 64 || info.Length > MaximumFileBytes) throw new InvalidDataException("Invalid map history size");
            byte[] bytes = File.ReadAllBytes(file);
            byte[] digest; using (var hash = SHA256.Create()) digest = hash.ComputeHash(bytes, 0, bytes.Length - 32);
            for (int i = 0; i < digest.Length; i++)
                if (digest[i] != bytes[bytes.Length - 32 + i]) throw new InvalidDataException("Map history checksum failed; original file preserved");
            using (var stream = new MemoryStream(bytes, 0, bytes.Length - 32))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                int magic = reader.ReadInt32(), format = reader.ReadInt32();
                if (magic != Magic || (format != 1 && format != Format)
                    || reader.ReadInt64() != World || reader.ReadInt64() != Character)
                    throw new InvalidDataException("Map history belongs to a different world/character or version");
                int count = ReadCount(reader, MaximumDeleted); var ids = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < count; i++)
                {
                    var entry = Read(reader, format); Guid parsed;
                    if (!Guid.TryParseExact(entry.Id, "N", out parsed) || !ids.Add(entry.Id) || entry.DeletedUtc == 0)
                        throw new InvalidDataException("Invalid map deletion ID");
                    deleted.Add(entry);
                }
                count = ReadCount(reader, MaximumMetadata);
                for (int i = 0; i < count; i++) { var entry = Read(reader, format); metadata.Add(entry.Key, entry); }
                if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected map history data");
            }
        }
        private static int ReadCount(BinaryReader reader, int limit)
        { int count = reader.ReadInt32(); if (count < 0 || count > limit) throw new InvalidDataException("Map history count limit"); return count; }
        private static void WriteText(BinaryWriter writer, string value)
        { byte[] bytes = Encoding.UTF8.GetBytes(value ?? ""); writer.Write(bytes.Length); writer.Write(bytes); }
        private static string ReadText(BinaryReader reader, int limit)
        {
            int length = ReadCount(reader, limit * 4); byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            string value = new UTF8Encoding(false, true).GetString(bytes);
            if (value.Length > limit) throw new InvalidDataException("Map history text too long"); return value;
        }
        private static void Write(BinaryWriter writer, PinRecord entry)
        {
            entry.Validate(); WriteText(writer, entry.Id); WriteText(writer, entry.Name); WriteText(writer, entry.Author); WriteText(writer, entry.CreatorName);
            writer.Write(entry.Type); writer.Write(entry.X); writer.Write(entry.Y); writer.Write(entry.Z); writer.Write(entry.WorldSize);
            writer.Write(entry.Owner); writer.Write(entry.CreatedUtc); writer.Write(entry.DeletedUtc); writer.Write(entry.RestoredUtc);
            writer.Write(entry.Checked); writer.Write(entry.DoubleSize); writer.Write(entry.Animate);
            WriteText(writer, entry.PresetKey); WriteText(writer, entry.BoundName);
        }
        private static PinRecord Read(BinaryReader reader, int format)
        {
            var entry = new PinRecord { Id = ReadText(reader, 32), Name = ReadText(reader, 256), Author = ReadText(reader, 256), CreatorName = ReadText(reader, 128),
                Type = reader.ReadInt32(), X = reader.ReadSingle(), Y = reader.ReadSingle(), Z = reader.ReadSingle(), WorldSize = reader.ReadSingle(),
                Owner = reader.ReadInt64(), CreatedUtc = reader.ReadInt64(), DeletedUtc = reader.ReadInt64(), RestoredUtc = reader.ReadInt64(),
                Checked = reader.ReadBoolean(), DoubleSize = reader.ReadBoolean(), Animate = reader.ReadBoolean() };
            if (format >= 2) { entry.PresetKey = ReadText(reader, 96); entry.BoundName = ReadText(reader, 256); }
            entry.Validate(); return entry;
        }
    }
}
