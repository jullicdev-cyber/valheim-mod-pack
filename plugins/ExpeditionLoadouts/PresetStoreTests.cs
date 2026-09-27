using System;
using System.IO;
using System.Text;
using ValheimModPack.ExpeditionLoadouts;
internal static class PresetStoreTests
{
    private static int count;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); ++count; }
    private static LoadoutPreset NewPreset(string name)
    {
        var p = new LoadoutPreset { Id = Guid.NewGuid().ToString("N"), Name = name };
        p.Items.Add(new PresetItem { Prefab = "ArrowWood", Quality = 1, Count = 100 }); return p;
    }
    public static int Main(string[] args)
    {
        string root = Path.Combine(args[0], "preset-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new PresetStore(root, 11);
        Check(store.Presets.Count == 0 && !store.ReadOnly, "new character starts empty");
        var plan = NewPreset("В море"); store.Save(plan);
        plan.Items[0].Count = 90;
        Check(store.Presets[0].Items[0].Count == 100, "caller cannot mutate saved plan through reference");
        var read = new PresetStore(root, 11);
        Check(read.Presets[0].Name == "В море" && read.Presets[0].Items[0].Count == 100, "Cyrillic and targets persist");
        Check(new PresetStore(root, 12).Presets.Count == 0, "plans isolated per character");
        store.Save(plan);
        Check(store.Presets.Count == 1 && new PresetStore(root, 11).Presets[0].Items[0].Count == 90, "update preserves identity");
        Check(File.Exists(Path.Combine(root, "000000000000000b.json.bak")), "previous document retained for recovery");
        var invalid = plan.Copy(); invalid.Items[0].Count = Int32.MaxValue;
        bool rejected = false; try { store.Save(invalid); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && store.Presets[0].Items[0].Count == 90, "invalid count rejected without changing state");
        invalid = plan.Copy(); invalid.Items.Add(invalid.Items[0].Copy());
        rejected = false; try { store.Save(invalid); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "duplicate item targets rejected");
        invalid = plan.Copy(); invalid.Id = "../escape";
        rejected = false; try { store.Save(invalid); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "invalid preset id rejected");
        Check(PresetStore.SafeName("\n  В море\u200b\t ") == "В море", "control and hidden characters removed");
        store.Remove(plan.Id);
        Check(new PresetStore(root, 11).Presets.Count == 0, "explicit deletion persists");
        for (int i = 0; i < PresetStore.MaxPresets; i++) store.Save(NewPreset("Набор " + i));
        rejected = false; try { store.Save(NewPreset("Overflow")); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && store.Presets.Count == PresetStore.MaxPresets, "bounded number of presets");
        string broken = Path.Combine(root, "000000000000000d.json");
        File.WriteAllText(broken, "{broken", Encoding.UTF8);
        var corrupt = new PresetStore(root, 13);
        Check(corrupt.ReadOnly && !String.IsNullOrEmpty(corrupt.LoadError), "corrupt file enters read-only mode");
        rejected = false; try { corrupt.Save(NewPreset("Oops")); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected && File.ReadAllText(broken) == "{broken", "corrupt file is preserved");
        string future = Path.Combine(root, "000000000000000e.json");
        File.WriteAllText(future, "{\"Version\":9,\"Presets\":[]}");
        Check(new PresetStore(root, 14).ReadOnly, "future schema not overwritten");
        string huge = Path.Combine(root, "000000000000000f.json");
        File.WriteAllText(huge, new string(' ', 131073));
        Check(new PresetStore(root, 15).ReadOnly, "oversized file bounded before deserialize");
        Console.WriteLine("OK: " + count + " preset persistence checks. Test files: " + root); return 0;
    }
}
