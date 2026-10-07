using System;
using System.Collections.Generic;
using ValheimModPack.PartyPrison;

internal static class CombatPolicyTests
{
    private static int checks;
    private static void Check(bool value, string label) { ++checks; if (!value) throw new Exception(label); }
    private static void Reject(Action action, string label)
    { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } Check(rejected, label); }
    private sealed class RepeatingRandom : Random
    {
        private readonly int[] values;
        private int offset;
        internal RepeatingRandom(params int[] values) { this.values = values; }
        public override int Next(int maximum) { return values[offset++ % values.Length] % maximum; }
    }
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
                    Check(gear.Contains(choice.ArrowPrefab) && CombatCatalog.IsArrowPrefab(choice.ArrowPrefab)
                        && !gear.Contains(choice.ArrowSource) && choice.GearPrefabs.Length <= 12, "isolated issued supplies and bounded chest stock");
                    Check(choice.RussianName.Length > 0 && choice.EnglishName.Length > 0, "visible mob family names");
                    Check(choice.FoodSources.Length == 4 && choice.FoodPrefabs.Length == 4, "four food choices in every enemy and grade loadout");
                    Check(new HashSet<string>(choice.FoodSources).Count == 4, "four different food types rather than duplicate portions");
                    Check(choice.FoodServings == difficulty + 1 && choice.FoodServings <= 3, "bounded portions scale with enemy grade");
                    for (int food = 0; food < 4; ++food)
                        Check(choice.FoodPrefabs[food] == CombatCatalog.FoodPrefab(choice.FoodSources[food]) && CombatCatalog.IsFoodPrefab(choice.FoodPrefabs[food]), "food uses a registered isolated loan prefab");
                    Check(choice.EmeticServings == 3, "every enemy and grade kit includes three temporary pukeberries");
                    Check(choice.GearPrefabs.Length + choice.FoodServings * 4 + choice.EmeticServings <= 24, "full kit including emetics fits the native cell chest");
                    string saved = choice.GearPrefabs[0]; choice.GearPrefabs[0] = "changed";
                    Check(CombatCatalog.Get(family, difficulty).GearPrefabs[0] == saved, "callers cannot alter catalog equipment");
                    string savedFood = choice.FoodSources[0]; choice.FoodSources[0] = "changed"; choice.FoodPrefabs[0] = "changed";
                    Check(CombatCatalog.Get(family, difficulty).FoodSources[0] == savedFood
                        && CombatCatalog.Get(family, difficulty).FoodPrefabs[0] == CombatCatalog.FoodPrefab(savedFood), "callers cannot mutate registered food definitions");
                }
            }
            Check(families.SetEquals(new[] { "Greyling", "Greydwarf", "Draugr", "Skeleton", "Hatchling", "Wolf" }), "the six requested mob families use canonical native prefabs");
            Check(CombatCatalog.NativeFamilyCount == 6 && CombatCatalog.MixedFamily == 6 && CombatCatalog.FamilyCount == 7,
                "mixed mode extends the six existing wire indices without renumbering them");
            Check(new HashSet<string>(CombatCatalog.AllMobPrefabs()).SetEquals(families), "all mixed-wave choices resolve to the existing canonical native enemy set");
            string[] arrowSources = { "ArrowWood", "ArrowFlint", "ArrowIron", "ArrowFlint", "ArrowObsidian", "ArrowObsidian", "ArrowObsidian" };
            for (int family = 0; family < CombatCatalog.FamilyCount; ++family)
                for (int difficulty = 0; difficulty < CombatCatalog.DifficultyCount; ++difficulty)
                    Check(CombatCatalog.Get(family, difficulty).ArrowSource == arrowSources[family], "arrows follow the enemy's native biome at all grades");
            Check(new HashSet<string>(CombatCatalog.AllArrowSources()).SetEquals(arrowSources), "four canonical arrow sources registered once");
            for (int difficulty = 0; difficulty < CombatCatalog.DifficultyCount; ++difficulty) {
                PrisonCombatLoadout mixed = CombatCatalog.Get(CombatCatalog.MixedFamily, difficulty), mountain = CombatCatalog.Get(5, difficulty);
                Check(mixed.IsMixed && !mountain.IsMixed && mixed.RussianName == "Смешанные" && mixed.EnglishName == "Mixed enemies",
                    "mixed choice has a visible distinct mode identity");
                Check(new HashSet<string>(mixed.GearPrefabs).SetEquals(mountain.GearPrefabs)
                    && new HashSet<string>(mixed.FoodSources).SetEquals(mountain.FoodSources) && mixed.ArrowSource == mountain.ArrowSource,
                    "mixed waves receive native mountain armor, silver weapons, bow, food and obsidian ammunition");
                var observed = new HashSet<string>(); var random = new Random(100 + difficulty);
                for (int attempt = 0; attempt < 128; ++attempt) {
                    string[] wave = CombatCatalog.GetWaveMobPrefabs(mixed, random); var kinds = new HashSet<string>(wave);
                    Check(wave.Length == difficulty + 2 && kinds.Count >= 2, "every mixed wave has its bounded size and at least two enemy types");
                    foreach (string name in wave) Check(families.Contains(name), "mixed waves never accept arbitrary native prefab names");
                    observed.UnionWith(kinds);
                }
                Check(observed.SetEquals(families), "seeded mixed waves include every one of the six enemy types");
                string[] repeated = CombatCatalog.GetWaveMobPrefabs(mixed, new RepeatingRandom(0));
                Check(new HashSet<string>(repeated).Count == 2 && repeated[0] == "Greyling" && repeated[repeated.Length - 1] == "Greydwarf",
                    "even an identical random roll is adjusted into a genuinely mixed wave");
                Random first = new Random(678), second = new Random(678);
                for (int attempt = 0; attempt < 8; ++attempt)
                    Check(String.Join(",", CombatCatalog.GetWaveMobPrefabs(mixed, first)) == String.Join(",", CombatCatalog.GetWaveMobPrefabs(mixed, second)),
                        "wave species selection is deterministic with the same injected random source");
            }
            string[] duplicates = CombatCatalog.GetWaveMobPrefabs(CombatCatalog.Get(CombatCatalog.MixedFamily, 2), new RepeatingRandom(0, 0, 1, 1));
            Check(duplicates[0] == duplicates[1] && duplicates[2] == duplicates[3] && duplicates[0] != duplicates[2],
                "mixed waves permit repeated enemy types instead of requiring a fixed permutation");
            for (int family = 0; family < CombatCatalog.NativeFamilyCount; ++family)
                for (int difficulty = 0; difficulty < CombatCatalog.DifficultyCount; ++difficulty) {
                    PrisonCombatLoadout normal = CombatCatalog.Get(family, difficulty);
                    string[] wave = CombatCatalog.GetWaveMobPrefabs(normal, new Random(321));
                    Check(!normal.IsMixed && wave.Length == normal.WaveCount && new HashSet<string>(wave).SetEquals(new[] { normal.MobPrefab }),
                        "the six existing modes still generate their selected homogeneous native enemy family");
                }
            string[] exposedMobs = CombatCatalog.AllMobPrefabs(); exposedMobs[0] = "changed";
            Check(CombatCatalog.AllMobPrefabs()[0] == "Greyling", "callers cannot mutate the native mixed enemy registry");
            Reject(() => CombatCatalog.GetWaveMobPrefabs(null, new Random()), "missing loadout rejected");
            Reject(() => CombatCatalog.GetWaveMobPrefabs(CombatCatalog.Get(0, 0), null), "missing random source rejected");
            Check(!CombatCatalog.IsArrowPrefab("ArrowWood") && !CombatCatalog.IsArrowPrefab("vmp_prison_arrow_arbitrary")
                && !CombatCatalog.IsArrowPrefab(null), "ordinary arrows and arbitrary prefabs cannot opt into supply expiration");
            Check(CombatCatalog.EmeticSource == "Pukeberries" && CombatCatalog.EmeticPrefab != CombatCatalog.EmeticSource
                && !CombatCatalog.IsFoodPrefab(CombatCatalog.EmeticPrefab) && !CombatCatalog.IsArrowPrefab(CombatCatalog.EmeticPrefab),
                "isolated emetics are not falsely classified as a food buff or arrow type");
            string[] sources = CombatCatalog.AllFoodSources();
            Check(new HashSet<string>(sources).Count == sources.Length && sources.Length <= 24, "food registration is unique and bounded");
            Check(!CombatCatalog.IsFoodPrefab("Sausages") && !CombatCatalog.IsFoodPrefab("vmp_prison_food_arbitrary")
                && !CombatCatalog.IsFoodPrefab(null), "personal foods and arbitrary client names are never classified as loan foods");
            Check(CombatCatalog.Get(0, 0).FoodSources[0] == "CookedMeat" && CombatCatalog.Get(1, 1).FoodSources[0] == "DeerStew"
                && CombatCatalog.Get(2, 1).FoodSources[0] == "Sausages" && CombatCatalog.Get(5, 1).FoodSources[0] == "WolfMeatSkewer",
                "food recipes follow meadows, forest, swamp and mountain enemy origins");
            Reject(() => CombatCatalog.Get(-1, 0), "negative family rejected");
            Reject(() => CombatCatalog.Get(7, 0), "arbitrary prefab index rejected");
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
            Check(!PrisonGearPolicy.ShouldRemove(tag, null, 100, world, token, 3, true), "current isolated arrow stack is usable");
            Check(!PrisonGearPolicy.ShouldRemove(tag, null, 100, world, token, 2, true), "future isolated arrows survive state ordering");
            Check(PrisonGearPolicy.ShouldRemove(tag, null, 100, world, token, 4, true), "old isolated arrows expire on opponent change");
            Check(PrisonGearPolicy.ShouldRemove(tag, null, 100, world, nextToken, 1, true), "previous sentence isolated arrows expire");
            Check(PrisonGearPolicy.ShouldRemove(tag, null, 100, world, "", 0, true), "all current-world issued arrows expire on release");
            Check(!PrisonGearPolicy.ShouldRemove(tag, null, 100, world + 1, "", 0, true), "other-world issued arrows are preserved");
            Check(!PrisonGearPolicy.ShouldRemove(null, null, 100, world, "", 0, true), "an untagged supply cannot become a removable loan");
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
