using System;
using System.Globalization;

namespace ValheimModPack.PartyPrison
{
    public sealed class PrisonCombatLoadout
    {
        public readonly string MobPrefab, RussianName, EnglishName;
        public readonly int MobLevel, WaveCount, GearQuality;
        public readonly string[] GearPrefabs;
        public readonly string[] FoodSources, FoodPrefabs;
        public readonly string ArrowSource, ArrowPrefab;
        public readonly int FoodServings;
        public readonly int EmeticServings = CombatCatalog.EmeticServings;
        internal PrisonCombatLoadout(string prefab, string russian, string english, string[] gear, string[] foods, string arrows, int difficulty)
        {
            MobPrefab = prefab; RussianName = russian; EnglishName = english;
            MobLevel = difficulty + 1; WaveCount = difficulty + 2; GearQuality = difficulty + 1;
            ArrowSource = arrows; ArrowPrefab = CombatCatalog.ArrowPrefab(arrows);
            GearPrefabs = new string[gear.Length + 1]; Array.Copy(gear, GearPrefabs, gear.Length);
            GearPrefabs[gear.Length] = ArrowPrefab;
            FoodSources = (string[])foods.Clone(); FoodPrefabs = new string[foods.Length];
            for (int i = 0; i < foods.Length; ++i) FoodPrefabs[i] = CombatCatalog.FoodPrefab(foods[i]);
            // Four choices, each in its own nonstackable loan serving. Native
            // food limits and benefits remain unchanged; harder fights stock
            // enough portions for more than one attempt.
            FoodServings = difficulty + 1;
        }
    }

    // The wire carries two bounded indices, never a client-provided prefab name.
    public static class CombatCatalog
    {
        public const int FamilyCount = 6, DifficultyCount = 3;
        public const string EmeticSource = "Pukeberries", EmeticPrefab = "vmp_prison_emetic_Pukeberries";
        public const int EmeticServings = 3;
        private static readonly string[] Mobs = { "Greyling", "Greydwarf", "Draugr", "Skeleton", "Hatchling", "Wolf" };
        private static readonly string[] Russian = { "Грейдлинги", "Грейдворфы", "Драугры", "Скелеты", "Драконы", "Волки" };
        private static readonly string[] English = { "Greylings", "Greydwarfs", "Draugr", "Skeletons", "Drakes", "Wolves" };
        private static readonly string[][] Gear = {
            new[] { "ArmorLeatherChest", "ArmorLeatherLegs", "HelmetLeather", "Club", "ShieldWood", "Bow" },
            new[] { "ArmorLeatherChest", "ArmorLeatherLegs", "HelmetLeather", "SwordBronze", "MaceBronze", "AxeBronze", "ShieldBronzeBuckler", "BowFineWood" },
            new[] { "ArmorBronzeChest", "ArmorBronzeLegs", "HelmetBronze", "SwordIron", "MaceIron", "ShieldBanded", "BowHuntsman" },
            new[] { "ArmorBronzeChest", "ArmorBronzeLegs", "HelmetBronze", "SwordBronze", "MaceBronze", "ShieldBronzeBuckler", "BowFineWood" },
            new[] { "ArmorIronChest", "ArmorIronLegs", "HelmetIron", "CapeWolf", "SwordIron", "ShieldBanded", "BowHuntsman" },
            new[] { "ArmorWolfChest", "ArmorWolfLegs", "HelmetDrake", "SwordSilver", "ShieldSilver", "BowDraugrFang" }
        };
        private static readonly string[] Arrows = { "ArrowWood", "ArrowFlint", "ArrowIron", "ArrowFlint", "ArrowObsidian", "ArrowObsidian" };

        // Entries 0 and 1 favor health; 2 and 3 favor stamina. The weak
        // loadout uses the previous biome's available recipes, then upgrades
        // to the enemy's biome. The highest tier adds a third serving.
        private static readonly string[][][] Food = {
            new[] {
                new[] { "CookedMeat", "NeckTailGrilled", "Raspberry", "Honey" },
                new[] { "CookedDeerMeat", "CookedMeat", "Raspberry", "Honey" },
                new[] { "CookedDeerMeat", "CookedMeat", "Raspberry", "Honey" }
            },
            ForestFoods(),
            new[] {
                new[] { "DeerStew", "MinceMeatSauce", "CarrotSoup", "QueensJam" },
                new[] { "Sausages", "BlackSoup", "TurnipStew", "ShocklateSmoothie" },
                new[] { "Sausages", "BlackSoup", "TurnipStew", "ShocklateSmoothie" }
            },
            ForestFoods(), MountainFoods(), MountainFoods()
        };
        private static readonly System.Collections.Generic.HashSet<string> FoodNames = MakeFoodNames();

        private static System.Collections.Generic.HashSet<string> MakeFoodNames()
        {
            var names = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (string[][] family in Food) foreach (string[] tier in family) foreach (string source in tier) names.Add(FoodPrefab(source));
            return names;
        }

        private static string[][] ForestFoods()
        {
            return new[] {
                new[] { "CookedDeerMeat", "CookedMeat", "Honey", "Blueberries" },
                new[] { "DeerStew", "MinceMeatSauce", "CarrotSoup", "QueensJam" },
                new[] { "DeerStew", "MinceMeatSauce", "CarrotSoup", "QueensJam" }
            };
        }

        private static string[][] MountainFoods()
        {
            return new[] {
                new[] { "Sausages", "BlackSoup", "TurnipStew", "ShocklateSmoothie" },
                new[] { "WolfMeatSkewer", "CookedWolfMeat", "OnionSoup", "Eyescream" },
                new[] { "WolfMeatSkewer", "SerpentStew", "OnionSoup", "Eyescream" }
            };
        }

        public static string FoodPrefab(string source)
        {
            if (String.IsNullOrEmpty(source)) throw new ArgumentException("Food source is missing.");
            return "vmp_prison_food_" + source;
        }

        public static bool IsFoodPrefab(string name)
        {
            return !String.IsNullOrEmpty(name) && FoodNames.Contains(name);
        }

        public static string ArrowPrefab(string source)
        {
            if (String.IsNullOrEmpty(source)) throw new ArgumentException("Arrow source is missing.");
            return "vmp_prison_arrow_" + source;
        }

        public static bool IsArrowPrefab(string name)
        {
            return name == "vmp_prison_arrow_ArrowWood" || name == "vmp_prison_arrow_ArrowFlint"
                || name == "vmp_prison_arrow_ArrowIron" || name == "vmp_prison_arrow_ArrowObsidian";
        }

        public static string[] AllArrowSources() { return new[] { "ArrowWood", "ArrowFlint", "ArrowIron", "ArrowObsidian" }; }

        public static string[] AllFoodSources()
        {
            var names = new System.Collections.Generic.SortedSet<string>(StringComparer.Ordinal);
            foreach (string[][] family in Food) foreach (string[] tier in family) foreach (string source in tier) names.Add(source);
            var result = new string[names.Count]; names.CopyTo(result); return result;
        }

        public static PrisonCombatLoadout Get(int family, int difficulty)
        {
            if (family < 0 || family >= FamilyCount || difficulty < 0 || difficulty >= DifficultyCount)
                throw new ArgumentOutOfRangeException("family", "Выберите один из шести видов мобов и сложность 1–3.");
            return new PrisonCombatLoadout(Mobs[family], Russian[family], English[family], Gear[family], Food[family][difficulty], Arrows[family], difficulty);
        }

        public static string Name(int family, bool russian) { return russian ? Get(family, 0).RussianName : Get(family, 0).EnglishName; }
    }

    public static class PrisonGearPolicy
    {
        public static string Tag(long world, string token, int revision)
        {
            Guid id;
            if (world == 0 || revision < 1 || !Guid.TryParseExact(token, "N", out id) || id == Guid.Empty) throw new ArgumentException("Invalid prison gear provenance.");
            return world.ToString(CultureInfo.InvariantCulture) + ":" + id.ToString("N") + ":" + revision.ToString(CultureInfo.InvariantCulture);
        }

        public static bool TryParse(string value, out long world, out string token, out int revision)
        {
            world = 0; token = ""; revision = 0;
            if (String.IsNullOrEmpty(value)) return false;
            string[] parts = value.Split(':'); Guid id;
            if (parts.Length != 3 || !Int64.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out world) || world == 0
                || !Guid.TryParseExact(parts[1], "N", out id) || id == Guid.Empty || !Int32.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out revision) || revision < 1)
                return false;
            token = id.ToString("N"); return true;
        }

        // Ordinary supplies may already contain personal loot. Only explicitly
        // isolated arena ammo prefabs can opt into stackable expiration.
        public static bool ShouldRemove(string gear, string legacy, int maximumStack, long world, string activeToken, int activeRevision)
        { return ShouldRemove(gear, legacy, maximumStack, world, activeToken, activeRevision, false); }

        public static bool ShouldRemove(string gear, string legacy, int maximumStack, long world, string activeToken, int activeRevision, bool isolatedSupply)
        {
            if (maximumStack != 1 && !isolatedSupply || world == 0) return false;
            long taggedWorld; string taggedToken; int taggedRevision;
            if (!String.IsNullOrEmpty(gear)) return TryParse(gear, out taggedWorld, out taggedToken, out taggedRevision) && taggedWorld == world
                && (String.IsNullOrEmpty(activeToken) || taggedToken != activeToken || taggedRevision < activeRevision);
            Guid oldToken;
            return !String.IsNullOrEmpty(legacy) && Guid.TryParseExact(legacy, "N", out oldToken);
        }
    }
}
