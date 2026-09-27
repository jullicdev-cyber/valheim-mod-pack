using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ValheimModPack;
public struct Vector2i
{
    public int x, y;
    public Vector2i(int x, int y) { this.x = x; this.y = y; }
    public override int GetHashCode() { return x * 397 ^ y; }
    public override bool Equals(object other) { return other is Vector2i && ((Vector2i)other).x == x && ((Vector2i)other).y == y; }
}
public static class FavoriteStateTests
{
    private const long Id = 42;
    private static int checks;
    private static string directory;
    private static FavoriteStateLink link;
    private static Quick quick;
    private static Azu azu;
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    private static string Primary { get { return Path.Combine(directory, "QuickStackStore_player_42.dat"); } }
    private static string Legacy { get { return Path.Combine(directory, "AzuAutoStore_player_42.dat"); } }
    public sealed class Quick
    {
        public HashSet<Vector2i> Slots = new HashSet<Vector2i>();
        public HashSet<string> Items = new HashSet<string>(), Trash = new HashSet<string>();
        public static Quick Get(long id)
        {
            link.ObserveBeforeGet(id);
            if (quick == null)
            {
                quick = new Quick();
                using (var create = new FileStream(Primary, FileMode.OpenOrCreate, FileAccess.ReadWrite)) { }
                Read(Primary, quick.Slots, quick.Items, quick.Trash);
            }
            link.Bind(id, quick, null); return quick;
        }
        public void Save() { Write(Primary, Slots, Items, Trash); link.AfterQuickSave(this); }
    }
    public sealed class Azu
    {
        public HashSet<Vector2i> Slots = new HashSet<Vector2i>();
        public HashSet<string> Items = new HashSet<string>();
        public static Azu Get(long id)
        {
            link.ObserveBeforeGet(id);
            if (azu == null) { azu = new Azu(); Read(Primary, azu.Slots, azu.Items, null); Read(Legacy, azu.Slots, azu.Items, null); }
            link.Bind(id, null, azu); return azu;
        }
        public void Save() { link.SaveAzu(this); }
    }
    private static void Reload()
    {
        quick = null; azu = null;
        link = new FavoriteStateLink(directory, typeof(Quick).GetMethod("Get"), typeof(Azu).GetMethod("Get"), typeof(Quick).GetMethod("Save"),
            typeof(Quick).GetField("Slots"), typeof(Quick).GetField("Items"), typeof(Azu).GetField("Slots"), typeof(Azu).GetField("Items"));
    }
    // Small disk fixture represents the vendors' three-list vs two-list distinction.
    // Actual vendor serialization is exercised by NativeChecks, not reimplemented here.
    private static void Write(string path, HashSet<Vector2i> slots, HashSet<string> items, HashSet<string> trash)
    {
        using (var file = File.Create(path)) using (var writer = new BinaryWriter(file))
        {
            writer.Write(slots.Count); foreach (var cell in slots) { writer.Write(cell.x); writer.Write(cell.y); }
            writer.Write(items.Count); foreach (var item in items) writer.Write(item);
            if (trash != null) { writer.Write(trash.Count); foreach (var item in trash) writer.Write(item); }
        }
    }
    private static void Read(string path, HashSet<Vector2i> slots, HashSet<string> items, HashSet<string> trash)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) return;
        using (var file = File.OpenRead(path)) using (var reader = new BinaryReader(file))
        {
            int count = reader.ReadInt32(); for (int i = 0; i < count; i++) slots.Add(new Vector2i(reader.ReadInt32(), reader.ReadInt32()));
            count = reader.ReadInt32(); for (int i = 0; i < count; i++) items.Add(reader.ReadString());
            if (trash != null && file.Position < file.Length) { count = reader.ReadInt32(); for (int i = 0; i < count; i++) trash.Add(reader.ReadString()); }
        }
    }
    public static void Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "vmp-favorite-state-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            foreach (bool quickFirst in new[] { true, false })
            {
                directory = Path.Combine(root, quickFirst ? "quick-first" : "azu-first"); Directory.CreateDirectory(directory);
                Write(Legacy, new HashSet<Vector2i> { new Vector2i(2, 2) }, new HashSet<string> { "old-wood" }, null);
                byte[] legacyOriginal = File.ReadAllBytes(Legacy); Reload();
                if (quickFirst) Quick.Get(Id); else Azu.Get(Id);
                Check(quick.Items.Contains("old-wood") && quick.Slots.Contains(new Vector2i(2, 2)), "Missing primary imports legacy for either first reader");
                Check(ReferenceEquals(quick.Slots, azu.Slots) && ReferenceEquals(quick.Items, azu.Items), "Both mods share exact mutable sets");
                Check(File.Exists(Primary) && new FileInfo(Primary).Length > 0, "Migration writes a canonical file after OpenOrCreate");
                quick.Trash.Add("discarded-trophy"); quick.Items.Remove("old-wood"); quick.Slots.Clear(); quick.Save();
                Check(!azu.Items.Contains("old-wood") && azu.Slots.Count == 0, "Alt unfavorite is immediately visible to Azu K");
                azu.Items.Add("new-stone"); azu.Save();
                Check(quick.Items.Contains("new-stone"), "Z toggle immediately updates Quick Stack live state");
                for (int restart = 0; restart < 2; restart++)
                {
                    Reload(); if (quickFirst) Quick.Get(Id); else Azu.Get(Id);
                    Check(!quick.Items.Contains("old-wood") && !azu.Items.Contains("old-wood") && quick.Slots.Count == 0, "Two full reloads never resurrect stale mirror marks");
                    Check(quick.Items.Contains("new-stone") && quick.Trash.Contains("discarded-trophy"), "Z save preserves canonical favorite changes and third-list trash flags");
                }
                Check(Convert.ToBase64String(File.ReadAllBytes(Legacy)) == Convert.ToBase64String(legacyOriginal), "Legacy backup file is preserved byte-for-byte");
                quick.Slots = new HashSet<Vector2i>(); quick.Items = new HashSet<string>(); quick.Trash.Clear(); quick.Save();
                Check(ReferenceEquals(quick.Items, azu.Items) && ReferenceEquals(quick.Slots, azu.Slots), "Quick Stack reset rebinds replacement HashSets");
                byte[] beforeDetached = File.ReadAllBytes(Primary); azu.Items = new HashSet<string> { "stale" }; bool failed = false;
                try { azu.Save(); } catch { failed = true; }
                Check(failed && Convert.ToBase64String(beforeDetached) == Convert.ToBase64String(File.ReadAllBytes(Primary)), "Detached cache cannot overwrite canonical data");
            }
            directory = Path.Combine(root, "existing-empty"); Directory.CreateDirectory(directory);
            File.WriteAllBytes(Primary, new byte[0]); Write(Legacy, new HashSet<Vector2i> { new Vector2i(1, 1) }, new HashSet<string> { "stale" }, null);
            Reload(); Azu.Get(Id); Check(quick.Items.Count == 0 && azu.Items.Count == 0 && quick.Slots.Count == 0, "Existing zero-byte primary is intentional canonical empty, never triggers legacy import");
            directory = Path.Combine(root, "existing-nonempty"); Directory.CreateDirectory(directory);
            Write(Primary, new HashSet<Vector2i>(), new HashSet<string> { "canonical" }, new HashSet<string> { "trash" });
            Write(Legacy, new HashSet<Vector2i>(), new HashSet<string> { "stale" }, null);
            Reload(); Quick.Get(Id); Check(quick.Items.SetEquals(new[] { "canonical" }) && azu.Items.SetEquals(new[] { "canonical" }), "Existing canonical file takes precedence over native Azu's mirror union");
            bool detached = false;
            try { link.SaveAzu(new Azu()); } catch { detached = true; }
            Check(detached, "Unknown Azu config cannot write through canonical serializer");
            Console.WriteLine("OK: " + checks + " production favorite state sharing, migration and repeated-reload assertions.");
        }
        finally
        {
            string resolved = Path.GetFullPath(root), parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("vmp-favorite-state-", StringComparison.Ordinal)) Directory.Delete(resolved, true);
        }
    }
}
