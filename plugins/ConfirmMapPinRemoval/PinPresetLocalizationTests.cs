// Compile only in Test-PinPresetLocalization.ps1; no game/Unity code is run.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using ValheimModPack.PinRemoval;

public sealed class Localization
{
    public static Localization instance;
    public string GetSelectedLanguage() { return "English"; }
    public string Localize(string token) { return token; }
}
internal static class PinPresetLocalizationTests
{
    private static int count;
    private static void Check(bool valid, string message)
    { count++; if (!valid) throw new Exception(message); }
    private static PinPreset Find(List<PinPreset> entries, string key)
    { return entries.Find(p => p.LocalizationKey == key); }
    public static void Main()
    {
        string selected = "Russian";
        var stock = new Dictionary<string, string>(StringComparer.Ordinal);
        var labels = new PinPresetLocalization(() => selected, token => {
            string translated; return stock.TryGetValue(selected + token, out translated) ? translated : "[" + token.Substring(1) + "]";
        });
        var presets = PinPresetCatalog.Defaults();
        Check(presets.Count >= 15 && presets.Count <= 25, "catalog size");
        var ids = new HashSet<string>();
        foreach (var preset in presets)
        {
            Check(preset.Builtin && ids.Add(preset.Id) && Regex.IsMatch(preset.Id, @"^default\.[a-z0-9_.-]+$"), "stable unique default ID");
            Check(PinPresetCatalog.IsStockIcon(preset.Icon), "manual stock icon only");
            Check(labels.Name(preset).Length > 0 && labels.Name(preset) != preset.LocalizationKey, "default key resolves");
            Check(labels.Get(preset.Id) == labels.Get(preset.LocalizationKey), "stable default ID alias resolves");
        }
        foreach (int forbidden in new[] { -1, 4, 5, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, Int32.MaxValue })
            Check(!PinPresetCatalog.IsStockIcon(forbidden), "special map icon excluded");
        foreach (string required in new[] { "TrollCave", "BearDen", "Raspberries", "Blueberries", "Mushrooms", "Dungeon", "SurtlingSpawn" })
            Check(Find(presets, required) != null, "requested default present");
        Check(labels.Get("TrollCave") == "Пещера тролля", "missing native label uses Russian fallback");
        stock["French$location_forestcave"] = "Grotte de troll";
        selected = "French";
        Check(labels.Language == "French" && labels.Name(Find(presets, "TrollCave")) == "Grotte de troll", "native selected-language translation");
        Check(labels.Get("default.trollcave") == "Grotte de troll", "archive ID alias follows selected language");
        Check(labels.Matches(Find(presets, "TrollCave"), "  GROTTE troll "), "search selected language with normalized terms");
        Check(labels.Matches(Find(presets, "TrollCave"), "пещера тролля"), "Russian alias available in French");
        Check(labels.Matches(Find(presets, "TrollCave"), "troll cave"), "English alias available in French");
        Check(!labels.Matches(Find(presets, "TrollCave"), "troll silver"), "all terms required");
        Check(labels.Get("Dungeon") == "Donjon", "generic preset names translated into selected French");
        Check(labels.Get("default.surtlingspawn") == "Apparition de Surtlings", "generic ID alias uses selected language");
        selected = "German";
        Check(labels.Get("TarPit") == "Teergrube", "German generic preset translated");
        foreach (string supported in new[] { "Swedish", "French", "Italian", "German", "Spanish", "Romanian", "Bulgarian", "Macedonian",
            "Finnish", "Danish", "Norwegian", "Icelandic", "Turkish", "Lithuanian", "Czech", "Hungarian", "Slovak", "Polish", "Dutch",
            "Portuguese_European", "Portuguese_Brazilian", "Chinese", "Chinese_Trad", "Japanese", "Korean", "Hindi", "Thai", "Croatian",
            "Georgian", "Greek", "Serbian", "Ukrainian", "Latvian" })
        {
            selected = supported;
            Check(labels.Get("Dungeon") != "Dungeon" && labels.Get("TarPit") != "Tar Pit" && labels.Get("FulingVillage") != "Fuling Village",
                "generic labels covered for " + supported);
        }
        selected = "Abenaki"; Check(labels.Get("Dungeon") == "Dungeon", "rare unverified translation honestly falls back to English");
        selected = "FutureLanguage"; Check(labels.Get("Dungeon") == "Dungeon", "future language fallback");
        selected = "Russian";
        Check(labels.Language == "Russian" && labels.Get("Dungeon") == "Подземелье", "language changes without cache reset");
        Check(labels.Get("default.trollcave") == "Пещера тролля", "archive ID alias updates without cache reset");
        Check(labels.Matches(Find(presets, "Flax"), "ЛЕН"), "yo and case normalization");
        Check(labels.Matches(Find(presets, "InfestedMine"), "зараженный"), "yo matches e");
        Check(labels.SearchAliases(Find(presets, "Raspberries")).Contains("Малина"), "public UI alias API");
        var custom = new PinPreset { Id = "custom.one", Name = "$item_iron", LocalizationKey = "", Icon = 3 };
        stock["Russian$item_iron"] = "Железо";
        Check(labels.Name(custom) == "$item_iron", "custom dollar name stays literal");
        selected = "English";
        Check(labels.Name(custom) == "$item_iron", "custom name unchanged by language");
        custom.LocalizationKey = "UnknownFutureKey"; custom.Name = "My place";
        Check(labels.Name(custom) == "My place", "unknown future key preserves saved fallback");
        custom.Name = "  hello\n \u202eworld\t ";
        Check(labels.Name(custom) == "hello world", "control/bidi/whitespace sanitation");
        custom.Name = new string('a', 95) + Char.ConvertFromUtf32(0x1f332);
        Check(labels.Name(custom).Length == 95, "UTF16 truncation does not split a surrogate pair");
        Check(!labels.Matches(null, "") && labels.Name(null) == "", "null data safe");
        Check(labels.Matches(Find(presets, "Portal"), ""), "empty search matches");
        foreach (string key in new[] { "presets_title", "presets_search", "presets_empty", "presets_close", "presets_add", "presets_edit",
            "presets_delete", "presets_choose_point", "presets_place_here", "presets_page", "presets_new_title", "presets_edit_title",
            "presets_name", "presets_icon", "presets_save", "presets_cancel", "presets_name_required", "presets_icon_required", "presets_rename_title",
            "presets_builtin", "presets_custom", "presets_selection_hint", "place_point", "place_here", "quick_pins", "icon", "read_only",
            "save_failed", "delete_preset", "delete_preset_body", "delete", "choose_point_hint", "pin_created" })
        {
            selected = "English"; Check(labels.Get(key) != key, "English UI key resolves");
            selected = "Russian"; Check(labels.Get(key) != key, "Russian UI key resolves");
        }
        Check(String.Format(labels.Get("presets_page"), 1, 4) == "1 / 4", "page format contract");
        Check(String.Format(labels.Get("delete_preset_body"), "Дом").Contains("Дом"), "delete confirmation format contract");
        var otherDefaults = PinPresetCatalog.Defaults(); presets[0].Name = "changed";
        Check(otherDefaults[0].Name == "Troll Cave", "default instances are not shared");
        Check(new PinPresetLocalization().Get("BearDen") == "Bear Cave", "no native localization singleton fallback");
        string directory = Path.Combine(Path.GetTempPath(), "vmp-preset-catalog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PinPresetStore(directory, 123, otherDefaults, PinPresetCatalog.IsStockIcon);
            Check(!store.ReadOnly && store.Presets.Count == 25, "actual persistence validates catalog");
            store = new PinPresetStore(directory, 123, PinPresetCatalog.Defaults(), PinPresetCatalog.IsStockIcon);
            Check(!store.ReadOnly && store.Presets.Count == 25, "catalog survives persistence roundtrip");
            store.Delete("default.trollcave");
            Check(store.Find("default.trollcave") == null && labels.Get("default.trollcave") == "Пещера тролля", "deleting preset preserves archive binding label");
        }
        finally
        {
            string resolved = Path.GetFullPath(directory), temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("vmp-preset-catalog-", StringComparison.Ordinal)
                && Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
        Console.WriteLine("OK: " + count + " production preset catalog/localization assertions.");
    }
}
