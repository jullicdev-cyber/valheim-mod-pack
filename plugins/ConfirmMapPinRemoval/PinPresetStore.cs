using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace ValheimModPack.PinRemoval
{
    [DataContract]
    public sealed class PinPreset
    {
        [DataMember(IsRequired = true, Order = 0)] public string Id;
        [DataMember(IsRequired = true, Order = 1)] public string Name;
        [DataMember(IsRequired = true, Order = 2)] public string LocalizationKey;
        [DataMember(IsRequired = true, Order = 3)] public int Icon;
        [DataMember(IsRequired = true, Order = 4)] public bool Builtin;
        public PinPreset Copy()
        {
            return new PinPreset { Id = Id, Name = Name, LocalizationKey = LocalizationKey, Icon = Icon, Builtin = Builtin };
        }
    }

    [DataContract]
    internal sealed class PinPresetDocument
    {
        [DataMember(IsRequired = true, Order = 0)] internal int Version;
        [DataMember(IsRequired = true, Order = 1)] internal long CharacterId;
        [DataMember(IsRequired = true, Order = 2)] internal List<PinPreset> Presets;
    }

    /// <summary>
    /// Personal labels/icons only: no map coordinates, world identifiers, or pin instances.
    /// A valid file is the seed-once marker, including a file with an empty preset list.
    /// </summary>
    public sealed class PinPresetStore
    {
        public const int MaximumPresets = 128, MaximumNameLength = 96, MaximumFileBytes = 256 * 1024;
        private const int SchemaVersion = 1;
        private readonly long characterId;
        private readonly Func<int, bool> validIcon;
        private List<PinPreset> presets = new List<PinPreset>();
        private byte[] diskHash;
        public string FilePath { get; private set; }
        public bool ReadOnly { get; private set; }
        public string LoadError { get; private set; }
        public IList<PinPreset> Presets { get { return Copy(presets).AsReadOnly(); } }

        public PinPresetStore(string directory, long characterId, IEnumerable<PinPreset> defaults)
            : this(directory, characterId, defaults, null) { }

        public PinPresetStore(string directory, long characterId, IEnumerable<PinPreset> defaults, Func<int, bool> isValidIcon)
        {
            if (String.IsNullOrWhiteSpace(directory)) throw new ArgumentException("A preset directory is required.", "directory");
            if (characterId == 0) throw new ArgumentOutOfRangeException("characterId");
            this.characterId = characterId;
            validIcon = isValidIcon;
            FilePath = Path.Combine(Path.GetFullPath(directory), characterId.ToString(CultureInfo.InvariantCulture) + ".json");
            LoadError = "";
            try
            {
                if (File.Exists(FilePath))
                {
                    byte[] bytes = ReadBounded(FilePath);
                    PinPresetDocument document;
                    using (var stream = new MemoryStream(bytes, false)) document = (PinPresetDocument)Serializer().ReadObject(stream);
                    Validate(document);
                    presets = Copy(document.Presets);
                    diskHash = Hash(bytes);
                }
                else
                {
                    var seeds = new List<PinPreset>();
                    if (defaults != null)
                        foreach (PinPreset preset in defaults)
                        {
                            if (preset == null || !preset.Builtin) throw new InvalidDataException("Default presets must have a stable built-in identity.");
                            seeds.Add(preset.Copy());
                            if (seeds.Count > MaximumPresets) throw new InvalidDataException("Too many default pin presets.");
                        }
                    Write(seeds);
                }
            }
            catch (Exception error)
            {
                // Never replace an unreadable or newer file with a fresh set of defaults.
                ReadOnly = true;
                LoadError = error.Message;
            }
        }

        public PinPreset Find(string id)
        {
            PinPreset value = presets.Find(p => String.Equals(p.Id, id, StringComparison.Ordinal));
            return value == null ? null : value.Copy();
        }

        public PinPreset Add(string name, int icon)
        {
            EnsureWritable();
            var value = new PinPreset { Id = Guid.NewGuid().ToString("N"), Name = SafeName(name), LocalizationKey = "", Icon = icon, Builtin = false };
            var next = Copy(presets); next.Add(value);
            Write(next);
            return value.Copy();
        }

        // rename=false is an icon-only edit: preserve both the fallback name and localization binding.
        public bool Update(string id, string name, int icon, bool rename = true)
        {
            EnsureWritable();
            var next = Copy(presets);
            PinPreset value = next.Find(p => String.Equals(p.Id, id, StringComparison.Ordinal));
            if (value == null) return false;
            value.Icon = icon;
            if (rename) { value.Name = SafeName(name); value.LocalizationKey = ""; }
            Write(next);
            return true;
        }

        public bool Delete(string id)
        {
            EnsureWritable();
            var next = Copy(presets);
            if (next.RemoveAll(p => String.Equals(p.Id, id, StringComparison.Ordinal)) == 0) return false;
            Write(next);
            return true;
        }

        public static string SafeName(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            var text = new StringBuilder();
            bool pendingSpace = false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (Char.IsWhiteSpace(c)) { if (text.Length > 0) pendingSpace = true; continue; }
                if (Char.IsControl(c) || Char.GetUnicodeCategory(c) == UnicodeCategory.Format) continue;
                if (Char.IsLowSurrogate(c)) continue;
                bool pair = Char.IsHighSurrogate(c);
                if (pair && (i + 1 >= value.Length || !Char.IsLowSurrogate(value[i + 1]))) continue;
                int length = (pendingSpace ? 1 : 0) + (pair ? 2 : 1);
                if (text.Length + length > MaximumNameLength) break;
                if (pendingSpace) { text.Append(' '); pendingSpace = false; }
                text.Append(c); if (pair) text.Append(value[++i]);
            }
            return text.ToString();
        }

        public static string CleanName(string value) { return SafeName(value); }

        private void EnsureWritable()
        {
            if (ReadOnly) throw new InvalidOperationException("Pin preset file is read-only after a load conflict or error. The existing file was preserved.");
        }

        private void Write(List<PinPreset> next)
        {
            EnsureWritable();
            var document = new PinPresetDocument { Version = SchemaVersion, CharacterId = characterId, Presets = next };
            Validate(document);
            byte[] bytes;
            using (var stream = new MemoryStream())
            { Serializer().WriteObject(stream, document); bytes = stream.ToArray(); }
            if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("Pin preset file is too large.");
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                EnsureUnchangedOnDisk();
                if (diskHash == null) File.Move(temporary, FilePath);
                else File.Replace(temporary, FilePath, FilePath + ".bak");
                presets = Copy(next);
                diskHash = Hash(bytes);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private void EnsureUnchangedOnDisk()
        {
            bool exists = File.Exists(FilePath);
            bool unchanged = diskHash == null ? !exists : exists && SameHash(diskHash, Hash(ReadBounded(FilePath)));
            if (unchanged) return;
            ReadOnly = true;
            LoadError = "The pin preset file changed outside this window. Reopen the character to reload it.";
            throw new IOException(LoadError);
        }

        private void Validate(PinPresetDocument document)
        {
            if (document == null || document.Version != SchemaVersion || document.CharacterId != characterId
                || document.Presets == null || document.Presets.Count > MaximumPresets)
                throw new InvalidDataException("Unsupported or mismatched pin preset document.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (PinPreset preset in document.Presets)
            {
                if (preset == null || !ValidId(preset.Id, preset.Builtin) || !ids.Add(preset.Id)
                    || String.IsNullOrWhiteSpace(preset.Name) || preset.Name.Length > MaximumNameLength || preset.Name != SafeName(preset.Name)
                    || preset.LocalizationKey == null || preset.LocalizationKey.Length > 128
                    || (!preset.Builtin && preset.LocalizationKey.Length != 0) || !ValidLocalizationKey(preset.LocalizationKey)
                    || preset.Icon < 0 || preset.Icon > 1024 || (validIcon != null && !validIcon(preset.Icon)))
                    throw new InvalidDataException("Invalid or duplicate pin preset.");
            }
        }

        private static bool ValidId(string id, bool builtin)
        {
            if (String.IsNullOrEmpty(id)) return false;
            if (!builtin)
            {
                Guid guid;
                return id.Length == 32 && Guid.TryParseExact(id, "N", out guid) && guid != Guid.Empty && id == guid.ToString("N");
            }
            if (!id.StartsWith("default.", StringComparison.Ordinal) || id.Length <= 8 || id.Length > 96) return false;
            for (int i = 8; i < id.Length; i++)
            { char c = id[i]; if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '_' && c != '-' && c != '.') return false; }
            return true;
        }

        private static bool ValidLocalizationKey(string key)
        {
            foreach (char c in key)
                if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9')
                    && c != '_' && c != '-' && c != '.' && c != '$') return false;
            return true;
        }
        private static DataContractJsonSerializer Serializer()
        { return new DataContractJsonSerializer(typeof(PinPresetDocument), new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = 2048 }); }
        private static List<PinPreset> Copy(IEnumerable<PinPreset> source)
        { var result = new List<PinPreset>(); foreach (PinPreset value in source) result.Add(value.Copy()); return result; }
        private static byte[] ReadBounded(string file)
        {
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length <= 0 || stream.Length > MaximumFileBytes) throw new InvalidDataException("Pin preset file size is invalid.");
                var bytes = new byte[(int)stream.Length]; int total = 0;
                while (total < bytes.Length) { int read = stream.Read(bytes, total, bytes.Length - total); if (read == 0) throw new EndOfStreamException(); total += read; }
                return bytes;
            }
        }
        private static byte[] Hash(byte[] bytes) { using (var hash = SHA256.Create()) return hash.ComputeHash(bytes); }
        private static bool SameHash(byte[] left, byte[] right)
        { if (left.Length != right.Length) return false; for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false; return true; }
    }
}
