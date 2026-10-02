// Pure tests: no game process, Unity, world scans or saved games are touched.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using ValheimModPack.PinRemoval;

public sealed class Localization
{
    public static Localization instance;
    public string GetSelectedLanguage() { return "English"; }
    public string Localize(string token) { return token; }
}
internal static class SuggestionPolicyTests
{
    private static int checks;
    private static readonly SuggestionOptions options = new SuggestionOptions();
    private static void Check(bool value, string label) { checks++; if (!value) throw new Exception(label); }
    private static NearbyPinObservation Observation(string id, double x = 2, string objectKey = "")
    { return new NearbyPinObservation { PresetIds = new[] { id }, ObjectKey = objectKey, X = x }; }
    private static Dictionary<string, string> Labels(List<PinPreset> presets, string language)
    {
        var result = new Dictionary<string, string>();
        var text = new PinPresetLocalization(() => language, token => token);
        foreach (PinPreset preset in presets) result.Add(preset.Id, text.Name(preset));
        return result;
    }
    private static List<PinSuggestion> Select(List<PinPreset> presets, IEnumerable<NearbyPinObservation> observations,
        IEnumerable<ExistingSuggestedPin> pins = null, SuggestionOptions chosenOptions = null, string language = "English", ISet<string> disabled = null)
    { return SuggestionPolicy.Select(presets, Labels(presets, language), observations, pins, 0, 0, 0, chosenOptions ?? options, disabled); }

    public static void Main(string[] args)
    {
        List<PinPreset> defaults = PinPresetCatalog.Defaults();
        Check(defaults.Count == 25, "Exactly 25 stable default identities covered");
        foreach (PinPreset preset in defaults)
        {
            NearbyPinObservation observation = Observation(preset.Id);
            var result = Select(defaults, new[] { observation });
            Check(result.Count == 1 && result[0].Preset.Id == preset.Id && result[0].X == 2, "All default presets can be selected: " + preset.Id);
            result[0].Preset.Name = "Mutated returned copy";
            Check(preset.Name != "Mutated returned copy", "Caller preset cannot be modified by result: " + preset.Id);
            var removed = defaults.FindAll(p => p.Id != preset.Id);
            Check(Select(removed, new[] { observation }).Count == 0, "Deleted preset is never resurrected: " + preset.Id);
            Check(Select(defaults, new[] { observation }, disabled: new HashSet<string> { preset.Id }).Count == 0, "Disabled preset is not suggested: " + preset.Id);
            foreach (string language in new[] { "Russian", "English" })
            {
                string translated = Labels(defaults, language)[preset.Id];
                var pin = new ExistingSuggestedPin { RawName = "  " + translated.ToUpperInvariant() + "  ", Icon = preset.Icon, X = 3, Y = 1000 };
                Check(Select(defaults, new[] { observation }, new[] { pin }, language: language == "Russian" ? "English" : "Russian").Count == 0,
                    "RU/EN legacy label suppressed after language change: " + preset.Id + " " + language);
                pin.Icon = preset.Icon == 3 ? 0 : 3;
                Check(Select(defaults, new[] { observation }, new[] { pin }).Count == 1, "Name alone is insufficient: " + preset.Id);
                pin.BuiltinPresetId = preset.Id; pin.RawName = "Manually renamed";
                Check(Select(defaults, new[] { observation }, new[] { pin }, language: "Japanese").Count == 0,
                    "Bound metadata survives every name/icon/language change: " + preset.Id);
                pin.BuiltinPresetId = "default.unknown";
                Check(Select(defaults, new[] { observation }, new[] { pin }).Count == 1, "Wrong metadata never guesses by label: " + preset.Id);
            }
            var edited = defaults.ConvertAll(p => p.Copy());
            PinPreset change = edited.Find(p => p.Id == preset.Id);
            change.Name = "My " + preset.Id; change.LocalizationKey = ""; change.Icon = 0;
            result = Select(edited, new[] { observation }, language: "Russian");
            Check(result.Count == 1 && result[0].Name == change.Name && result[0].Preset.Icon == 0,
                "Personal name and icon are respected for every builtin: " + preset.Id);
            var customOnly = new List<PinPreset> { new PinPreset { Id = "aabb", Name = preset.Name, Icon = preset.Icon, Builtin = false, LocalizationKey = "" } };
            Check(Select(customOnly, new[] { observation }).Count == 0, "Similar custom name never creates a detection rule: " + preset.Id);
        }
        Catalog();
        Clusters(defaults);
        Dungeons(defaults);
        Malformed(defaults);
        if (args.Length == 2) VerifyNativeAssets(args[0], args[1]);
        Console.WriteLine("PASS: " + checks + " nearby pin policy checks" + (args.Length == 2 ? "; installed native catalog verified" : "; native asset check not requested"));
    }
    private static void Catalog()
    {
        var covered = new HashSet<string>();
        foreach (string field in new[] { "prefabs", "locations" })
        {
            var map = (Dictionary<string, string[]>)typeof(SuggestionPolicy).GetField(field, BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            foreach (var entry in map)
            {
                string[] actual = field == "prefabs" ? SuggestionPolicy.MatchPrefab(entry.Key) : SuggestionPolicy.MatchLocation(entry.Key);
                Check(actual.Length == entry.Value.Length && actual[0] == entry.Value[0], "Confirmed catalog match: " + entry.Key);
                foreach (string id in actual) covered.Add(id);
                actual[0] = "Corrupted";
                string[] again = field == "prefabs" ? SuggestionPolicy.MatchPrefab(entry.Key) : SuggestionPolicy.MatchLocation(entry.Key);
                Check(again[0] != "Corrupted", "Catalog arrays cannot be mutated: " + entry.Key);
            }
        }
        foreach (PinPreset preset in PinPresetCatalog.Defaults()) Check(covered.Contains(preset.Id), "Every default has a verified object or location rule: " + preset.Id);
        foreach (string spoof in new[] { null, "", "RaspberryBush(Clone)", "FakeRaspberryBush", "RaspberryBushFX", "raspberrybush", "IronScrap", "Mushroom", "DragonEgg",
            "Boar_piggy", "Spawner_Boar", "Surtling", "goblin_bed", "dvergrprops_bed", "bed02", "DG_ForestCrypt", "Crypt", "portal_connected" })
            Check(SuggestionPolicy.MatchPrefab(spoof).Length == 0, "Ambiguous item/spawner/effect excluded: " + spoof);
        foreach (string spoof in new[] { null, "TrollCave", "TrollCave02(Clone)", "SunkenCrypt1", "SunkenCrypt3", "MountainCave01", "DG_Cave", "DG_GoblinCamp", "Runestone_Ashlands", "NorthVillage" })
            Check(SuggestionPolicy.MatchLocation(spoof).Length == 0, "Disabled/unrelated/generator location excluded: " + spoof);
    }
    private static void Clusters(List<PinPreset> presets)
    {
        var a = Observation("default.raspberries", 2, "berry2");
        var b = Observation("default.raspberries", 1, "berry1");
        var c = Observation("default.raspberries", 30, "berry30");
        var result = Select(presets, new[] { a, c, b });
        Check(result.Count == 1 && result[0].ObjectKey == "berry1", "Nearest berry is stable cluster anchor, independent of scan order");
        var near = new SuggestionOptions { NearbyRadius = 120, ResourceClusterRadius = 40 };
        result = Select(presets, new[] { Observation("default.raspberries", 1), Observation("default.raspberries", 35), Observation("default.raspberries", 70) }, chosenOptions: near);
        Check(result.Count == 2 && result[1].X == 70, "No transitive berry chain hides a separate distant patch");
        Check(Select(presets, new[] { Observation("default.raspberries", 1), Observation("default.blueberries", 1) }).Count == 2, "Different berry types remain independent");
        var portal = Observation("default.portal", 1);
        var existing = new ExistingSuggestedPin { BuiltinPresetId = "default.portal", X = 9 };
        Check(Select(presets, new[] { portal }, new[] { existing }).Count == 0, "Portal duplicate boundary inclusive at eight metres");
        existing.X = 9.001;
        Check(Select(presets, new[] { portal }, new[] { existing }).Count == 1, "Adjacent independent portal beyond tight radius remains suggestible");
        Check(Select(presets, new[] { portal, Observation("default.portal", 10) }).Count == 2, "Two nearby portals are not merged using berry radius");
        result = Select(presets, new[] { Observation("default.mushrooms", 1), Observation("default.haldor", 20), Observation("default.portal", 10) },
            chosenOptions: new SuggestionOptions { MaximumSuggestions = 2 });
        Check(result.Count == 2 && result[0].Preset.Id == "default.haldor" && result[1].Preset.Id == "default.portal", "High-value nearby discoveries outrank resource noise");
        result = Select(presets, new[] { Observation("default.portal", 2, "same"), Observation("default.raspberries", 3, "same") });
        Check(result.Count == 1 && result[0].Preset.Id == "default.portal", "One loaded object cannot yield unrelated duplicate recommendations");
        var elevated = Observation("default.silver", 1); elevated.Y = 100;
        Check(Select(presets, new[] { elevated }).Count == 0, "Underground/exterior XZ coincidence cannot leak distant objects");
        Check(Select(presets, new[] { Observation("default.copper", 32) }).Count == 1
            && Select(presets, new[] { Observation("default.copper", 32.01) }).Count == 0, "Nearby range boundary inclusive");
    }
    private static void Dungeons(List<PinPreset> presets)
    {
        var observation = new NearbyPinObservation { PresetIds = SuggestionPolicy.MatchLocation("TrollCave02"), X = 2 };
        var result = Select(presets, new[] { observation });
        Check(result.Count == 1 && result[0].Preset.Id == "default.trollcave", "Specific dungeon wins over generic fallback");
        var removed = presets.FindAll(p => p.Id != "default.trollcave");
        Check(Select(removed, new[] { observation })[0].Preset.Id == "default.dungeon", "Deleted specific preset leaves only existing enabled generic fallback");
        removed.RemoveAll(p => p.Id == "default.dungeon");
        Check(Select(removed, new[] { observation }).Count == 0, "Deleted dungeon categories do not reappear");
        foreach (string id in new[] { "default.trollcave", "default.dungeon" })
        {
            var marked = new ExistingSuggestedPin { BuiltinPresetId = id, X = 3 };
            Check(Select(presets, new[] { observation }, new[] { marked }).Count == 0, "Specific/generic bound marker suppresses either duplicate: " + id);
            Check(Select(presets.FindAll(p => p.Id != "default.trollcave"), new[] { observation }, new[] { marked }).Count == 0,
                "Specific marker suppresses generic after preset deletion: " + id);
        }
        var different = new ExistingSuggestedPin { BuiltinPresetId = "default.crypt", X = 3 };
        Check(Select(presets, new[] { observation }, new[] { different }).Count == 1, "A different specific dungeon is not mistaken for this entrance");
        var legacy = new ExistingSuggestedPin { RawName = "Подземелье", Icon = 6, X = 3 };
        Check(Select(presets, new[] { observation }, new[] { legacy }).Count == 0, "Legacy generic Russian dungeon marker suppresses specific offer");
        legacy.RawName = "Пещера тролля";
        Check(Select(presets.FindAll(p => p.Id != "default.trollcave"), new[] { observation }, new[] { legacy }).Count == 0, "Removed specific preset's old label still prevents generic duplicate");
    }
    private static void Malformed(List<PinPreset> presets)
    {
        var observation = Observation("default.copper");
        var badOptions = new SuggestionOptions { NearbyRadius = Double.NaN, ResourceClusterRadius = -10, MaximumSuggestions = Int32.MaxValue };
        Check(Select(presets, new[] { observation }, chosenOptions: badOptions).Count == 1, "Malformed options fall back to finite bounded defaults");
        foreach (double bad in new[] { Double.NaN, Double.PositiveInfinity, Double.NegativeInfinity, Double.MaxValue })
        {
            observation.X = bad;
            Check(Select(presets, new[] { observation }).Count == 0, "Nonfinite or overflowing observations never appear");
        }
        Check(SuggestionPolicy.Select(presets, null, new[] { Observation("default.copper") }, null, Double.NaN, 0, 0, null).Count == 0,
            "Invalid local-player location does not show suggestions");
        Check(Select(presets, new NearbyPinObservation[] { null, new NearbyPinObservation(), Observation("default.unknown") }).Count == 0,
            "Null and unknown observations are ignored");
        var pin = new ExistingSuggestedPin { RawName = "Copper Deposit at the north of forest", Icon = 2 };
        Check(Select(presets, new[] { Observation("default.copper") }, new[] { pin }).Count == 1, "Substring similarity cannot suppress an unrelated manual label");
        pin.RawName = "Unreadable metadata wrapper"; pin.DisplayName = "Медная руда";
        Check(Select(presets, new[] { Observation("default.copper") }, new[] { pin }).Count == 0, "Readable displayed label handles wrapped raw names");
        var unknown = presets.ConvertAll(p => p.Copy());
        unknown.Add(new PinPreset { Id = "default.fake", Name = "Fake", Icon = 3, Builtin = true, LocalizationKey = "" });
        Check(Select(unknown, new[] { Observation("default.fake") }).Count == 0, "An arbitrary default-like ID cannot extend detection rules");
    }
    private static void VerifyNativeAssets(string manifestPath, string locationPath)
    {
        string manifest = File.ReadAllText(manifestPath);
        var prefabs = (Dictionary<string, string[]>)typeof(SuggestionPolicy).GetField("prefabs", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        foreach (string name in prefabs.Keys)
            Check(Regex.IsMatch(manifest, @"path in bundle: [^\r\n]*/" + Regex.Escape(name) + @"\.prefab(?:\r?\n|$)"), "Native prefab really exists: " + name);
        var serializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        var documents = (Dictionary<string, object>)serializer.DeserializeObject(File.ReadAllText(locationPath));
        var enabledLocations = new HashSet<string>();
        foreach (object document in documents.Values)
        {
            var data = (Dictionary<string, object>)document;
            foreach (object item in (object[])data["m_locations"])
            {
                var entry = (Dictionary<string, object>)item;
                if (Convert.ToInt32(entry["m_enable"]) == 1) enabledLocations.Add((string)entry["m_name"]);
            }
        }
        var locations = (Dictionary<string, string[]>)typeof(SuggestionPolicy).GetField("locations", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        foreach (string name in locations.Keys) Check(enabledLocations.Contains(name), "Native enabled location really exists: " + name);
    }
}
