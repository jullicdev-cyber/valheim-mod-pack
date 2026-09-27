using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace ValheimModPack.ExpeditionLoadouts
{
    [DataContract]
    public sealed class PresetItem
    {
        [DataMember] public string Prefab;
        [DataMember] public int Quality;
        [DataMember] public int Variant;
        [DataMember] public int WorldLevel;
        [DataMember] public int Count;
        public PresetItem Copy() { return new PresetItem { Prefab = Prefab, Quality = Quality, Variant = Variant, WorldLevel = WorldLevel, Count = Count }; }
    }

    [DataContract]
    public sealed class LoadoutPreset
    {
        [DataMember] public string Id;
        [DataMember] public string Name;
        [DataMember] public List<PresetItem> Items = new List<PresetItem>();
        public LoadoutPreset Copy()
        {
            var result = new LoadoutPreset { Id = Id, Name = Name };
            foreach (var item in Items) result.Items.Add(item.Copy());
            return result;
        }
    }

    [DataContract]
    public sealed class PresetDocument
    {
        [DataMember] public int Version = 2;
        [DataMember] public List<LoadoutPreset> Presets = new List<LoadoutPreset>();
    }

    // Personal plans only. No inventory contents or game saves are written here.
    public sealed class PresetStore
    {
        public const int MaxPresets = 20;
        public const int MaxItems = 64;
        public const int MaxCount = 9999;
        private const int MaxBytes = 131072;
        private readonly string path;
        public bool ReadOnly { get; private set; }
        public string LoadError { get; private set; }
        public List<LoadoutPreset> Presets { get; private set; }

        public PresetStore(string directory, long characterId)
        {
            path = Path.Combine(Path.GetFullPath(directory), characterId.ToString("x16") + ".json");
            Presets = new List<LoadoutPreset>();
            if (!File.Exists(path)) return;
            try
            {
                var info = new FileInfo(path);
                if (info.Length > MaxBytes) throw new InvalidDataException("Preset file is too large.");
                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length > MaxBytes) throw new InvalidDataException("Preset file is too large.");
                using (var stream = new MemoryStream(bytes))
                {
                    var document = (PresetDocument)Serializer().ReadObject(stream);
                    Validate(document);
                    Presets = document.Presets;
                }
            }
            catch (Exception error)
            {
                // Preserve a damaged/unknown file for recovery instead of overwriting it.
                ReadOnly = true;
                LoadError = error.Message;
            }
        }

        public void Save(LoadoutPreset preset)
        {
            if (ReadOnly) throw new InvalidOperationException("Preset file could not be read; existing file was preserved.");
            if (preset == null) throw new ArgumentNullException("preset");
            var updated = CopyAll();
            int index = updated.FindIndex(x => x.Id == preset.Id);
            if (index < 0) updated.Add(preset.Copy()); else updated[index] = preset.Copy();
            Write(updated);
        }

        public void Remove(string id)
        {
            if (ReadOnly) throw new InvalidOperationException("Preset file could not be read; existing file was preserved.");
            var updated = CopyAll();
            if (updated.RemoveAll(x => x.Id == id) > 0) Write(updated);
        }

        private List<LoadoutPreset> CopyAll()
        {
            var result = new List<LoadoutPreset>();
            foreach (var preset in Presets) result.Add(preset.Copy());
            return result;
        }

        private void Write(List<LoadoutPreset> updated)
        {
            var document = new PresetDocument { Presets = updated };
            Validate(document);
            byte[] bytes;
            using (var stream = new MemoryStream())
            {
                Serializer().WriteObject(stream, document);
                bytes = stream.ToArray();
            }
            if (bytes.Length > MaxBytes) throw new InvalidDataException("Preset file is too large.");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
                else File.Move(temporary, path);
                Presets = updated;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static DataContractJsonSerializer Serializer()
        { return new DataContractJsonSerializer(typeof(PresetDocument)); }

        private static void Validate(PresetDocument document)
        {
            if (document == null || (document.Version != 1 && document.Version != 2) || document.Presets == null || document.Presets.Count > MaxPresets)
                throw new InvalidDataException("Unsupported preset document.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var preset in document.Presets)
            {
                Guid guid;
                if (preset == null || preset.Id == null || !Guid.TryParseExact(preset.Id, "N", out guid) || !ids.Add(preset.Id)
                    || String.IsNullOrWhiteSpace(preset.Name) || preset.Name.Length > 60 || preset.Name != SafeName(preset.Name)
                    || preset.Items == null || preset.Items.Count > MaxItems)
                    throw new InvalidDataException("Invalid or duplicate preset.");
                var items = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in preset.Items)
                {
                    if (item == null || String.IsNullOrEmpty(item.Prefab) || item.Prefab.Length > 128
                        || item.Prefab.IndexOfAny(new[] {'\r','\n','\0','/','\\'}) >= 0 || item.Quality < 1 || item.Quality > 100
                        || item.Variant < 0 || item.Variant > 4095 || item.WorldLevel < 0 || item.WorldLevel > 1000
                        || item.Count < 1 || item.Count > MaxCount || !items.Add(item.Prefab + "\0" + item.Quality + "\0" + item.Variant + "\0" + item.WorldLevel))
                        throw new InvalidDataException("Invalid or duplicate preset item.");
                }
            }
        }

        public static string SafeName(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            var text = new StringBuilder();
            foreach (char c in value)
                if (!Char.IsControl(c) && Char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format) text.Append(c);
            string result = text.ToString().Trim();
            if (result.Length > 60) result = result.Substring(0, Char.IsHighSurrogate(result[59]) ? 59 : 60);
            return result;
        }
    }
}
