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
        var gear = NewPreset("Equipment"); gear.Items[0] = new PresetItem { Prefab="ShieldBanded", Quality=3, Variant=2, WorldLevel=1, Count=1 };
        var gearStore = new PresetStore(root, 16); gearStore.Save(gear);
        var gearRead = new PresetStore(root, 16);
        Check(gearRead.Presets[0].Items[0].Variant==2 && gearRead.Presets[0].Items[0].WorldLevel==1,
            "gear variant and world level survive save/reload");
        Check(gearRead.Presets[0].Copy().Items[0].Variant==2 && gearRead.Presets[0].Copy().Items[0].WorldLevel==1,
            "draft copy retains full item identity");
        gear.Items.Add(new PresetItem { Prefab="ShieldBanded",Quality=3,Variant=3,WorldLevel=1,Count=1 });
        gearStore.Save(gear);
        Check(gearStore.Presets[0].Items.Count==2,"different variants may be separate targets");
        invalid=gear.Copy(); invalid.Items[0].Variant=-1; rejected=false;
        try { gearStore.Save(invalid); } catch(InvalidDataException) { rejected=true; }
        Check(rejected,"negative variant rejected");
        invalid=gear.Copy(); invalid.Items[0].WorldLevel=1001; rejected=false;
        try { gearStore.Save(invalid); } catch(InvalidDataException) { rejected=true; }
        Check(rejected,"unbounded world level rejected");
        string legacy = Path.Combine(root,"0000000000000011.json");
        File.WriteAllText(legacy,"{\"Version\":1,\"Presets\":[{\"Id\":\""+Guid.NewGuid().ToString("N")+"\",\"Name\":\"Legacy\",\"Items\":[{\"Prefab\":\"ArrowWood\",\"Quality\":1,\"Count\":100}]}]}");
        var legacyStore=new PresetStore(root,17);
        Check(!legacyStore.ReadOnly&&legacyStore.Presets[0].Items[0].Variant==0&&legacyStore.Presets[0].Items[0].WorldLevel==0,
            "version one targets migrate to ordinary variant/world zero");
        legacyStore.Save(legacyStore.Presets[0]);
        Check(File.ReadAllText(legacy).Contains("\"Version\":2")&&File.Exists(legacy+".bak"),
            "first write upgrades schema with previous version backup");
        var originalWriter = new PresetStore(root, 18); originalWriter.Save(NewPreset("Initial"));
        var staleWriter = new PresetStore(root, 18);
        originalWriter.Save(NewPreset("Fresh update"));
        string concurrentPath = Path.Combine(root, "0000000000000012.json");
        byte[] authoritative = File.ReadAllBytes(concurrentPath), backup = File.ReadAllBytes(concurrentPath + ".bak");
        rejected = false; try { staleWriter.Save(NewPreset("Stale overwrite")); } catch (IOException) { rejected = true; }
        Check(rejected && staleWriter.ReadOnly && staleWriter.Presets.Count == 1, "stale writer refuses to overwrite another writer's saved presets");
        Check(Convert.ToBase64String(authoritative) == Convert.ToBase64String(File.ReadAllBytes(concurrentPath))
            && Convert.ToBase64String(backup) == Convert.ToBase64String(File.ReadAllBytes(concurrentPath + ".bak")), "external update and previous backup preserved byte-for-byte");
        var freshWriter = new PresetStore(root, 19); var otherWriter = new PresetStore(root, 19);
        otherWriter.Save(NewPreset("New character data"));
        rejected = false; try { freshWriter.Save(NewPreset("Empty snapshot")); } catch (IOException) { rejected = true; }
        Check(rejected && freshWriter.ReadOnly && new PresetStore(root, 19).Presets[0].Name == "New character data", "a concurrently created first file is preserved");
        var removalWriter = new PresetStore(root, 20); removalWriter.Save(NewPreset("Before external removal"));
        File.Delete(Path.Combine(root, "0000000000000014.json"));
        rejected = false; try { removalWriter.Save(NewPreset("Restore stale data")); } catch (IOException) { rejected = true; }
        Check(rejected && removalWriter.ReadOnly && !File.Exists(Path.Combine(root, "0000000000000014.json")), "external deletion is not silently undone");
        var invalidExternal = new PresetStore(root, 21); invalidExternal.Save(NewPreset("Before corruption"));
        string invalidPath = Path.Combine(root, "0000000000000015.json"); File.WriteAllText(invalidPath, "broken externally");
        rejected = false; try { invalidExternal.Remove(invalidExternal.Presets[0].Id); } catch (IOException) { rejected = true; }
        Check(rejected && invalidExternal.ReadOnly && File.ReadAllText(invalidPath) == "broken externally", "delete refuses to overwrite externally damaged data");
        Check(Directory.GetFiles(root, "*.tmp-*").Length == 0, "failed optimistic writes clean temporary files");
        var blockedWriter = new PresetStore(root, 22); var blockedPlan = NewPreset("Before I/O failure"); blockedWriter.Save(blockedPlan);
        string blockedPath = Path.Combine(root, "0000000000000016.json"); byte[] beforeBlocked = File.ReadAllBytes(blockedPath);
        blockedPlan.Name = "After retry";
        using (var locked = new FileStream(blockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            rejected = false; try { blockedWriter.Save(blockedPlan); } catch (IOException) { rejected = true; }
            Check(rejected && blockedWriter.Presets[0].Name == "Before I/O failure", "replacement I/O failure does not commit in-memory draft");
            Check(Convert.ToBase64String(beforeBlocked) == Convert.ToBase64String(File.ReadAllBytes(blockedPath)), "replacement I/O failure preserves original file");
        }
        blockedWriter.Save(blockedPlan);
        Check(new PresetStore(root, 22).Presets[0].Name == "After retry", "retry after transient I/O failure remains possible");
        Check(Directory.GetFiles(root, "*.tmp-*").Length == 0, "replacement failure and retry leave no temp files");
        Console.WriteLine("OK: " + count + " preset persistence checks. Test files: " + root); return 0;
    }
}
