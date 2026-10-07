using System;
using System.Collections.Generic;
using ValheimModPack.PartyPrison;

internal static class CombatPolicyTests
{
    private static int checks;
    private static void Check(bool value, string label) { ++checks; if (!value) throw new Exception(label); }
    private static void Reject(Action action, string label)
    { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } Check(rejected, label); }
    public static int Main()
    {
        try {
            var families = new HashSet<string>();
            for (int family = 0; family < CombatCatalog.FamilyCount; ++family) {
                families.Add(CombatCatalog.Get(family, 0).MobPrefab);
                for (int difficulty = 0; difficulty < CombatCatalog.DifficultyCount; ++difficulty) {
                    PrisonCombatLoadout choice = CombatCatalog.Get(family, difficulty);
                    Check(choice.MobLevel == difficulty + 1 && choice.MobLevel <= 3, "native star difficulty");
                    Check(choice.WaveCount >= 2 && choice.WaveCount <= 4, "bounded per-wave enemy count");
                    Check(choice.GearQuality == difficulty + 1, "equipment follows difficulty");
                    var gear = new HashSet<string>(choice.GearPrefabs);
                    Check(gear.Count == choice.GearPrefabs.Length, "no duplicate equipment definitions");
                    Check(gear.Contains("ArrowWood") && choice.GearPrefabs.Length <= 12, "supplies and bounded chest stock");
                    Check(choice.RussianName.Length > 0 && choice.EnglishName.Length > 0, "visible mob family names");
                    Check(choice.FoodSources.Length == 4 && choice.FoodPrefabs.Length == 4, "four food choices in every enemy and grade loadout");
                    Check(new HashSet<string>(choice.FoodSources).Count == 4, "four different food types rather than duplicate portions");
                    Check(choice.FoodServings == difficulty + 1 && choice.FoodServings <= 3, "bounded portions scale with enemy grade");
                    for (int food = 0; food < 4; ++food)
                        Check(choice.FoodPrefabs[food] == CombatCatalog.FoodPrefab(choice.FoodSources[food]) && CombatCatalog.IsFoodPrefab(choice.FoodPrefabs[food]), "food uses a registered isolated loan prefab");
                    Check(choice.GearPrefabs.Length + choice.FoodServings * 4 <= 32, "full kit fits empty native iron chest");
                    string saved = choice.GearPrefabs[0]; choice.GearPrefabs[0] = "changed";
                    Check(CombatCatalog.Get(family, difficulty).GearPrefabs[0] == saved, "callers cannot alter catalog equipment");
                    string savedFood = choice.FoodSources[0]; choice.FoodSources[0] = "changed"; choice.FoodPrefabs[0] = "changed";
                    Check(CombatCatalog.Get(family, difficulty).FoodSources[0] == savedFood
                        && CombatCatalog.Get(family, difficulty).FoodPrefabs[0] == CombatCatalog.FoodPrefab(savedFood), "callers cannot mutate registered food definitions");
                }
            }
            Check(families.SetEquals(new[] { "Greyling", "Greydwarf", "Draugr", "Skeleton", "Hatchling", "Wolf" }), "the six requested mob families use canonical native prefabs");
            string[] sources = CombatCatalog.AllFoodSources();
            Check(new HashSet<string>(sources).Count == sources.Length && sources.Length <= 24, "food registration is unique and bounded");
            Check(!CombatCatalog.IsFoodPrefab("Sausages") && !CombatCatalog.IsFoodPrefab("vmp_prison_food_arbitrary")
                && !CombatCatalog.IsFoodPrefab(null), "personal foods and arbitrary client names are never classified as loan foods");
            Check(CombatCatalog.Get(0, 0).FoodSources[0] == "CookedMeat" && CombatCatalog.Get(1, 1).FoodSources[0] == "DeerStew"
                && CombatCatalog.Get(2, 1).FoodSources[0] == "Sausages" && CombatCatalog.Get(5, 1).FoodSources[0] == "WolfMeatSkewer",
                "food recipes follow meadows, forest, swamp and mountain enemy origins");
            Reject(() => CombatCatalog.Get(-1, 0), "negative family rejected");
            Reject(() => CombatCatalog.Get(6, 0), "arbitrary prefab index rejected");
            Reject(() => CombatCatalog.Get(0, -1), "negative difficulty rejected");
            Reject(() => CombatCatalog.Get(0, 3), "unsupported mob level rejected");
            const long world = 6789;
            string token = new string('1', 32), nextToken = new string('2', 32), tag = PrisonGearPolicy.Tag(world, token, 3);
            long restoredWorld; string restoredToken; int restoredRevision;
            Check(PrisonGearPolicy.TryParse(tag, out restoredWorld, out restoredToken, out restoredRevision)
                && restoredWorld == world && restoredToken == token && restoredRevision == 3, "gear provenance roundtrip");
            Check(!PrisonGearPolicy.ShouldRemove(tag, null, 1, world, token, 3), "current kit remains usable");
            Check(!PrisonGearPolicy.ShouldRemove(tag, null, 1, world, token, 2), "fresh kit arriving before its new state snapshot remains usable");
            Check(PrisonGearPolicy.ShouldRemove(tag, null, 1, world, token, 4), "previous kit removed after loadout change");
            Check(PrisonGearPolicy.ShouldRemove(tag, null, 1, world, nextToken, 1), "previous sentence kit removed");
            Check(PrisonGearPolicy.ShouldRemove(tag, null, 1, world, "", 0), "loan armor removed on release");
            Check(!PrisonGearPolicy.ShouldRemove(tag, null, 1, world + 1, "", 0), "other world provenance preserved");
            foreach (int maxStack in new[] { 2, 10, 20, 50, 100, 999 })
                Check(!PrisonGearPolicy.ShouldRemove(tag, token, maxStack, world, "", 0), "mixed arrows and farm-resource stacks never removed");
            Check(!PrisonGearPolicy.ShouldRemove(null, null, 1, world, "", 0), "personal equipment and farm drops remain");
            Check(PrisonGearPolicy.ShouldRemove(null, token, 1, world, "", 0), "previous stock-only gear migrates on release");
            Check(PrisonGearPolicy.ShouldRemove(null, token, 1, world, token, 1), "previous stock-only gear replaced by revisioned loadout");
            Check(!PrisonGearPolicy.ShouldRemove(null, "visitor deposit", 1, world, "", 0), "unrecognized custom data never deletes visitor deposits");
            foreach (string invalid in new[] { "", "garbage", "0:" + token + ":1", "1:" + token + ":0", "1:" + token + ":-1", "1:token:1", "1:" + token + ":1:extra" })
                Check(!PrisonGearPolicy.TryParse(invalid, out restoredWorld, out restoredToken, out restoredRevision), "malformed provenance rejected");
            Reject(() => PrisonGearPolicy.Tag(0, token, 1), "no world provenance rejected");
            Reject(() => PrisonGearPolicy.Tag(world, "token", 1), "invalid sentence provenance rejected");
            Reject(() => PrisonGearPolicy.Tag(world, new string('0', 32), 1), "empty GUID provenance rejected");
            Reject(() => PrisonGearPolicy.Tag(world, token, 0), "invalid kit revision rejected");
            Console.WriteLine("PASS: " + checks + " arena mob-catalog, difficulty and resource-preserving gear provenance checks."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
