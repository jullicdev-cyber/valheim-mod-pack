using System;
using System.Collections.Generic;

namespace ValheimModPack.PinRemoval
{
    // The runtime supplies only nearby, loaded and actually visible objects. This
    // model deliberately has no Unity, ZDO, ZoneSystem or world-generation API.
    public sealed class NearbyPinObservation
    {
        public string[] PresetIds;
        public string ObjectKey;
        public double X, Y, Z;
    }
    public sealed class ExistingSuggestedPin
    {
        public string BuiltinPresetId, RawName, DisplayName;
        public int Icon;
        public double X, Y, Z;
    }
    public sealed class PinSuggestion
    {
        public PinPreset Preset;
        public string Name, ObjectKey;
        public double X, Y, Z, DistanceSquared;
        public int Priority;
    }
    public sealed class SuggestionOptions
    {
        public double NearbyRadius = 32;
        public double ResourceClusterRadius = 40;
        public double PortalClusterRadius = 8;
        public double StructureClusterRadius = 30;
        public double DungeonClusterRadius = 20;
        public double BaseClusterRadius = 60;
        public int MaximumSuggestions = 6;
    }

    public static class SuggestionPolicy
    {
        private static readonly string[] Empty = new string[0];
        private const string DungeonId = "default.dungeon";
        private static readonly Dictionary<string, PinPreset> defaults = DefaultMap();
        private static readonly Dictionary<string, string[]> prefabs = PrefabMap();
        private static readonly Dictionary<string, string[]> locations = LocationMap();
        private static readonly Dictionary<string, string[]> aliases = Aliases();

        // Exact names verified in the installed Valheim 1.0.16 SoftRef
        // manifest_extended. Dropped items, VFX, interior DG_* generators and
        // arbitrary name substrings are intentionally not recognized.
        public static string[] MatchPrefab(string prefabName) { return Match(prefabs, prefabName); }

        // Names verified from _ZoneSystem and LocationList MonoBehaviour assets
        // in SoftRef/Bundles/d59cfac (m_name, m_enable=1). m_prefabName can contain
        // an obsolete copied value, e.g. BearCave says Runestone_Ashlands.
        // A runtime caller must supply a LOADED location instance, never its
        // unvisited world-generation record or an interior DG_* generator.
        public static string[] MatchLocation(string locationName) { return Match(locations, locationName); }

        public static List<PinSuggestion> Select(IEnumerable<PinPreset> currentPresets,
            IDictionary<string, string> currentLabels, IEnumerable<NearbyPinObservation> observations,
            IEnumerable<ExistingSuggestedPin> existingPins, double playerX, double playerY, double playerZ,
            SuggestionOptions options, ISet<string> disabledPresetIds = null)
        {
            var result = new List<PinSuggestion>();
            if (!Finite(playerX) || !Finite(playerY) || !Finite(playerZ) || observations == null || currentPresets == null)
                return result;
            options = options ?? new SuggestionOptions();
            double nearby = Radius(options.NearbyRadius, 32);
            int maximum = options.MaximumSuggestions < 1 ? 1 : Math.Min(options.MaximumSuggestions, 32);
            var available = new Dictionary<string, PinPreset>(StringComparer.Ordinal);
            foreach (PinPreset preset in currentPresets)
            {
                if (preset == null || !preset.Builtin || preset.Id == null || !defaults.ContainsKey(preset.Id)
                    || (disabledPresetIds != null && disabledPresetIds.Contains(preset.Id))
                    || preset.Icon < 0 || preset.Icon > 1024 || String.IsNullOrWhiteSpace(preset.Name)) continue;
                if (!available.ContainsKey(preset.Id)) available.Add(preset.Id, preset.Copy());
            }
            var pins = new List<ExistingSuggestedPin>();
            if (existingPins != null)
                foreach (ExistingSuggestedPin pin in existingPins)
                    if (pin != null && Position(pin.X, pin.Y, pin.Z)) pins.Add(pin);
            var candidates = new List<PinSuggestion>();
            foreach (NearbyPinObservation observation in observations)
            {
                if (observation == null || observation.PresetIds == null || !Position(observation.X, observation.Y, observation.Z)) continue;
                double distance = Distance(observation.X, observation.Y, observation.Z, playerX, playerY, playerZ);
                // Three-dimensional range prevents seeing exterior deposits from
                // their dungeon coordinates merely because their map XZ overlap.
                if (distance > nearby * nearby) continue;
                PinPreset selected = null;
                foreach (string id in observation.PresetIds)
                {
                    PinPreset preset;
                    if (id != null && available.TryGetValue(id, out preset)
                        && (selected == null || Priority(id) > Priority(selected.Id))) selected = preset;
                }
                if (selected == null) continue;
                string name;
                if (currentLabels == null || !currentLabels.TryGetValue(selected.Id, out name) || String.IsNullOrWhiteSpace(name)) name = selected.Name;
                candidates.Add(new PinSuggestion { Preset = selected.Copy(), Name = name,
                    ObjectKey = observation.ObjectKey ?? "", X = observation.X, Y = observation.Y, Z = observation.Z,
                    DistanceSquared = distance, Priority = Priority(selected.Id) });
            }
            candidates.Sort(Compare);
            foreach (PinSuggestion candidate in candidates)
            {
                if (AlreadyMarked(candidate, pins, available, currentLabels, options) || AlreadySuggested(candidate, result, options)) continue;
                result.Add(candidate);
                if (result.Count >= maximum) break;
            }
            return result;
        }

        // Spatial clusters stay anchored at the nearest observed object, rather
        // than extending through a chain of berries across the whole forest.
        private static bool AlreadySuggested(PinSuggestion candidate, List<PinSuggestion> suggestions, SuggestionOptions options)
        {
            foreach (PinSuggestion other in suggestions)
            {
                if (candidate.ObjectKey.Length != 0 && String.Equals(candidate.ObjectKey, other.ObjectKey, StringComparison.Ordinal)) return true;
                if (SameType(candidate.Preset.Id, other.Preset.Id)
                    && MapDistance(candidate.X, candidate.Z, other.X, other.Z) <= Square(Cluster(candidate.Preset.Id, options))) return true;
            }
            return false;
        }
        private static bool AlreadyMarked(PinSuggestion candidate, List<ExistingSuggestedPin> pins,
            Dictionary<string, PinPreset> available, IDictionary<string, string> labels, SuggestionOptions options)
        {
            foreach (ExistingSuggestedPin pin in pins)
            {
                if (MapDistance(candidate.X, candidate.Z, pin.X, pin.Z) > Square(Cluster(candidate.Preset.Id, options))) continue;
                if (!String.IsNullOrEmpty(pin.BuiltinPresetId))
                {
                    // A stable metadata binding remains authoritative after any
                    // language change, personal rename or icon edit.
                    if (SameType(candidate.Preset.Id, pin.BuiltinPresetId)) return true;
                    continue;
                }
                if (MatchesLabel(candidate.Preset.Id, pin, available, labels)) return true;
                if (IsDungeon(candidate.Preset.Id) && MatchesLabel(DungeonId, pin, available, labels)) return true;
                if (candidate.Preset.Id == DungeonId)
                    foreach (string id in defaults.Keys)
                        if (IsDungeon(id) && MatchesLabel(id, pin, available, labels)) return true;
            }
            return false;
        }
        private static bool MatchesLabel(string id, ExistingSuggestedPin pin, Dictionary<string, PinPreset> available, IDictionary<string, string> labels)
        {
            PinPreset original;
            if (!defaults.TryGetValue(id, out original)) return false;
            PinPreset current;
            available.TryGetValue(id, out current);
            if (pin.Icon != original.Icon && (current == null || pin.Icon != current.Icon)) return false;
            string raw = Normalize(pin.RawName), display = Normalize(pin.DisplayName);
            if (raw.Length == 0 && display.Length == 0) return false;
            if (current != null && EqualLabel(current.Name, raw, display)) return true;
            string localized;
            if (labels != null && labels.TryGetValue(id, out localized) && EqualLabel(localized, raw, display)) return true;
            foreach (string alias in aliases[id]) if (EqualLabel(alias, raw, display)) return true;
            return false;
        }
        private static bool EqualLabel(string name, string raw, string display)
        { string normalized = Normalize(name); return normalized.Length > 0 && (normalized == raw || normalized == display); }
        private static string Normalize(string name) { return PinPresetLocalization.Normalize(name); }
        private static int Compare(PinSuggestion a, PinSuggestion b)
        {
            int comparison = b.Priority.CompareTo(a.Priority);
            if (comparison == 0) comparison = a.DistanceSquared.CompareTo(b.DistanceSquared);
            if (comparison == 0) comparison = String.CompareOrdinal(a.Preset.Id, b.Preset.Id);
            if (comparison == 0) comparison = String.CompareOrdinal(a.ObjectKey, b.ObjectKey);
            if (comparison == 0) comparison = a.X.CompareTo(b.X);
            if (comparison == 0) comparison = a.Z.CompareTo(b.Z);
            return comparison == 0 ? a.Y.CompareTo(b.Y) : comparison;
        }
        private static bool SameType(string a, string b)
        { return a == b || (a == DungeonId && IsDungeon(b)) || (b == DungeonId && IsDungeon(a)); }
        private static bool IsDungeon(string id)
        {
            return id == "default.trollcave" || id == "default.bearden" || id == "default.burialchambers"
                || id == "default.crypt" || id == "default.mountaincave" || id == "default.infestedmine";
        }
        private static int Priority(string id)
        {
            if (id == "default.haldor" || id == "default.hildir") return 100;
            if (id == "default.portal") return 95;
            if (IsDungeon(id)) return 90;
            if (id == DungeonId) return 85;
            if (id == "default.surtlingspawn" || id == "default.fulingvillage") return 80;
            if (id == "default.base") return 75;
            if (id == "default.silver" || id == "default.iron" || id == "default.copper" || id == "default.tin") return 65;
            if (id == "default.dragonegg" || id == "default.tarpit") return 60;
            return 40;
        }
        private static double Cluster(string id, SuggestionOptions options)
        {
            if (id == "default.portal") return Radius(options.PortalClusterRadius, 8);
            if (id == "default.base") return Radius(options.BaseClusterRadius, 60);
            if (id == DungeonId || IsDungeon(id)) return Radius(options.DungeonClusterRadius, 20);
            if (id == "default.haldor" || id == "default.hildir" || id == "default.surtlingspawn" || id == "default.fulingvillage")
                return Radius(options.StructureClusterRadius, 30);
            return Radius(options.ResourceClusterRadius, 40);
        }
        private static double Radius(double value, double fallback) { return Finite(value) && value > 0 ? Math.Min(value, 256) : fallback; }
        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        private static bool Position(double x, double y, double z) { return Finite(x) && Finite(y) && Finite(z); }
        private static double Square(double value) { return value * value; }
        private static double MapDistance(double ax, double az, double bx, double bz) { return Square(ax - bx) + Square(az - bz); }
        private static double Distance(double ax, double ay, double az, double bx, double by, double bz)
        { return MapDistance(ax, az, bx, bz) + Square(ay - by); }
        private static string[] Match(Dictionary<string, string[]> catalog, string name)
        {
            string[] ids;
            if (name == null || !catalog.TryGetValue(name, out ids)) return Empty;
            return (string[])ids.Clone();
        }
        private static Dictionary<string, PinPreset> DefaultMap()
        {
            var result = new Dictionary<string, PinPreset>(StringComparer.Ordinal);
            foreach (PinPreset preset in PinPresetCatalog.Defaults()) result.Add(preset.Id, preset);
            return result;
        }
        private static Dictionary<string, string[]> Aliases()
        {
            var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var english = new PinPresetLocalization(() => "English", token => token);
            var russian = new PinPresetLocalization(() => "Russian", token => token);
            foreach (PinPreset preset in defaults.Values)
                result.Add(preset.Id, new[] { preset.Name, english.Name(preset), russian.Name(preset) });
            return result;
        }
        private static Dictionary<string, string[]> PrefabMap()
        {
            var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
            Add(result, "Raspberries", "RaspberryBush");
            Add(result, "Blueberries", "BlueberryBush");
            Add(result, "Cloudberries", "CloudberryBush");
            Add(result, "Mushrooms", "Pickable_Mushroom", "Pickable_Mushroom_yellow", "Pickable_Mushroom_blue",
                "Pickable_Mushroom_JotunPuffs", "Pickable_Mushroom_Magecap", "Pickable_SmokePuff");
            Add(result, "Copper", "MineRock_Copper", "rock4_copper", "rock4_copper_frac");
            Add(result, "Tin", "MineRock_Tin");
            Add(result, "Iron", "mudpile", "mudpile_frac", "mudpile2", "mudpile2_frac");
            Add(result, "Silver", "silvervein", "silvervein_frac", "rock3_silver", "rock3_silver_frac");
            Add(result, "Portal", "portal", "portal_wood", "portal_stone");
            // The runtime additionally verifies a placed player Bed component;
            // decorative goblin/dvergr/ruin beds do not establish a player base.
            Add(result, "Base", "bed", "piece_bed02", "ashwood_bed");
            Add(result, "TarPit", "Pickable_Tar", "Pickable_TarBig");
            Add(result, "Haldor", "Haldor"); Add(result, "Hildir", "Hildir");
            Add(result, "Boars", "Boar");
            Add(result, "DragonEgg", "Pickable_DragonEgg");
            Add(result, "Flax", "Pickable_Flax_Wild", "Pickable_Flax");
            return result;
        }
        private static Dictionary<string, string[]> LocationMap()
        {
            var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
            AddDungeon(result, "TrollCave", "TrollCave02");
            AddDungeon(result, "BearDen", "BearCave");
            AddDungeon(result, "BurialChambers", "Crypt2", "Crypt3", "Crypt4", "Hildir_crypt");
            AddDungeon(result, "Crypt", "SunkenCrypt4");
            AddDungeon(result, "MountainCave", "MountainCave02", "Hildir_cave");
            AddDungeon(result, "InfestedMine", "Mistlands_DvergrTownEntrance1", "Mistlands_DvergrTownEntrance2");
            Add(result, "Dungeon", "Hildir_plainsfortress");
            Add(result, "SurtlingSpawn", "FireHole");
            Add(result, "TarPit", "TarPit1", "TarPit2", "TarPit3");
            Add(result, "FulingVillage", "GoblinCamp2");
            Add(result, "Haldor", "Vendor_BlackForest");
            Add(result, "Hildir", "Hildir_camp");
            return result;
        }
        private static void Add(Dictionary<string, string[]> catalog, string key, params string[] names)
        { foreach (string name in names) catalog.Add(name, new[] { "default." + key.ToLowerInvariant() }); }
        private static void AddDungeon(Dictionary<string, string[]> catalog, string key, params string[] names)
        { foreach (string name in names) catalog.Add(name, new[] { "default." + key.ToLowerInvariant(), DungeonId }); }
    }
}
