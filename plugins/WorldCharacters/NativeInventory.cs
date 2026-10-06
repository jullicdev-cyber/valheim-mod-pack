using System;
using System.IO;
using System.Text;
using System.Collections.Generic;

namespace ValheimModPack.WorldCharacters
{
    public sealed class StoredItem
    {
        public int Prefab, Count, Quality, Variant, X, Y;
        public bool Equipped;
        public readonly SortedDictionary<string, string> Custom = new SortedDictionary<string, string>(StringComparer.Ordinal);
        public string Identity()
        {
            using (var stream = new MemoryStream())
            using (var w = new BinaryWriter(stream))
            {
                w.Write(Prefab); w.Write(Count); w.Write(Quality); w.Write(Variant);
                foreach (var pair in Custom)
                {
                    // EAQS reassigns these bookkeeping fields while equipping. All other mod data must survive.
                    if (pair.Key == "eaqs_slot" || pair.Key == "eaqs_player" || pair.Key == "eaqs_parked" || pair.Key == "eaqs_weaponshield") continue;
                    // Epic Loot 0.14.13 lazily adds an EMPTY magic component to ordinary equipment on load.
                    // Absence and that exact empty marker are equivalent. Non-empty enchantments stay protected.
                    if (pair.Key == "randyknapp.mods.epicloot#EpicLoot.MagicItemComponent" && pair.Value == "") continue;
                    w.Write(pair.Key); w.Write(pair.Value);
                }
                w.Flush(); return StateCodec.Hash(stream.ToArray());
            }
        }
    }

    public static class NativeInventory
    {
        public static List<StoredItem> ReadPlayer(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes, false))
            using (var r = new BinaryReader(stream, new UTF8Encoding(false, true)))
            {
                // Deliberately fail closed on future formats instead of guessing offsets.
                if (r.ReadInt32() != 33) throw new InvalidDataException("Unsupported Player.Save version (expected 33 / Valheim 1.0.17).");
                for (int i = 0; i < 4; ++i) Finite(r.ReadSingle());
                NativeString(r, 4096); Finite(r.ReadSingle());
                return ReadInventory(r);
            }
        }
        public static List<StoredItem> ReadInventory(BinaryReader r)
        {
            int version = r.ReadInt32();
            if (version != 108 && version != 109) throw new InvalidDataException("Unsupported inventory format.");
            int count = r.ReadUInt16();
            if (count > 2048) throw new InvalidDataException("Too many inventory items.");
            var result = new List<StoredItem>(); var slots = new HashSet<int>();
            for (int i = 0; i < count; ++i)
            {
                r.ReadInt32(); // durability in hundredths
                var item = new StoredItem { X = r.ReadByte(), Y = r.ReadByte() };
                r.ReadByte(); int flags = r.ReadByte();
                item.Equipped = (flags & 2) != 0;
                item.Quality = (flags & 4) != 0 ? r.ReadUInt16() : 1;
                item.Count = (flags & 8) != 0 ? r.ReadUInt16() : 1;
                item.Variant = (flags & 16) != 0 ? r.ReadInt32() : 0;
                if ((flags & 32) != 0) { r.ReadInt64(); NativeString(r, 4096); }
                item.Prefab = (flags & 64) != 0 ? r.ReadInt32() : 0;
                int custom = (flags & 128) != 0 ? NumItems(r) : 0;
                if (custom > 1024) throw new InvalidDataException("Too many item metadata fields.");
                for (int k = 0; k < custom; ++k)
                {
                    string key = NativeString(r, 4096), value = NativeString(r, StateCodec.MaximumPlayerBytes);
                    if (item.Custom.ContainsKey(key)) throw new InvalidDataException("Duplicate metadata key.");
                    item.Custom.Add(key, value);
                }
                if (version >= 109) r.ReadByte();
                if (item.Prefab == 0 || item.Count < 1 || item.Quality < 1 || !slots.Add(item.Y * 256 + item.X))
                    throw new InvalidDataException("Missing prefab, invalid count, or overlapping inventory slots.");
                result.Add(item);
            }
            return result;
        }
        public static void RequireSameItems(List<StoredItem> expected, List<StoredItem> actual)
        {
            var counts = new Dictionary<string, int>();
            foreach (var i in expected) { string key = i.Identity(); int n; counts.TryGetValue(key, out n); counts[key] = n + 1; }
            foreach (var i in actual)
            {
                string key = i.Identity(); int n;
                if (!counts.TryGetValue(key, out n) || n == 0) throw new InvalidDataException("Loaded inventory differs from saved items or their mod data.");
                counts[key] = n - 1;
            }
            foreach (int n in counts.Values) if (n != 0) throw new InvalidDataException("Items were lost while loading; server save is protected.");
        }
        public static IEnumerable<StoredItem> IncludingBackpacks(IEnumerable<StoredItem> items, int depth = 0)
        {
            if (depth > 4) throw new InvalidDataException("Backpack nesting exceeds the supported depth.");
            foreach (StoredItem item in items)
            {
                yield return item;
                foreach (var field in item.Custom)
                {
                    if (field.Key.IndexOf("AdventureBackpacks.Components.BackpackComponent", StringComparison.Ordinal) < 0 || field.Value.Length == 0) continue;
                    byte[] payload;
                    try { payload = Convert.FromBase64String(field.Value); }
                    catch (FormatException e) { throw new InvalidDataException("Invalid Adventure Backpacks inventory.", e); }
                    using (var stream = new MemoryStream(payload))
                    using (var reader = new BinaryReader(stream))
                    {
                        List<StoredItem> children = ReadInventory(reader);
                        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing backpack inventory data.");
                        foreach (StoredItem child in IncludingBackpacks(children, depth + 1)) yield return child;
                    }
                }
            }
        }
        private static void Finite(float v) { if (Single.IsNaN(v) || Single.IsInfinity(v)) throw new InvalidDataException("Non-finite player stat."); }
        private static int NumItems(BinaryReader r) { int n = r.ReadByte(); return (n & 128) != 0 ? ((n & 127) << 8) | r.ReadByte() : n; }
        private static string NativeString(BinaryReader r, int limit)
        {
            uint count = 0; int shift = 0;
            while (true)
            {
                byte b = r.ReadByte(); if (shift == 28 && (b & 240) != 0) throw new InvalidDataException("Invalid string length.");
                count |= (uint)(b & 127) << shift;
                if ((b & 128) == 0) break;
                shift += 7; if (shift > 28) throw new InvalidDataException("Invalid string length.");
            }
            if (count > limit || count > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException("Oversized string.");
            return new UTF8Encoding(false, true).GetString(r.ReadBytes((int)count));
        }
    }
}
