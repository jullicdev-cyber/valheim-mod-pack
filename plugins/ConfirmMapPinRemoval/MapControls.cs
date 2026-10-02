using System;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace ValheimModPack.PinRemoval
{
    // Plain BepInEx entries are discoverable by Bindrune and config managers.
    internal sealed class MapControls
    {
        internal readonly ConfigEntry<KeyboardShortcut> History, Quick, Place, Rename;
        internal readonly ConfigEntry<KeyboardShortcut> Suggestion, NextSuggestion, DismissSuggestion, ClearDeathPins;
        internal readonly ConfigEntry<bool> SuggestionEnabled;
        internal readonly ConfigEntry<float> DetectionRadius, ResourceRadius, PortalRadius;
        internal MapControls(ConfigFile config)
        {
            History = config.Bind("Controls", "OpenHistory", new KeyboardShortcut(KeyCode.H, KeyCode.LeftControl),
                "Open pin history while the large map is visible / История меток на большой карте.");
            Quick = config.Bind("Controls", "QuickPin", new KeyboardShortcut(KeyCode.P, KeyCode.LeftControl),
                "Open presets at your position / Пресеты метки в текущей позиции.");
            Place = config.Bind("Controls", "PlaceOnMapModifier", new KeyboardShortcut(KeyCode.LeftShift),
                "Hold this key or combination and left-click the map / Удерживать при ЛКМ по карте для выбора пресета.");
            Rename = config.Bind("Controls", "RenamePinModifier", new KeyboardShortcut(KeyCode.LeftAlt),
                "Hold this key or combination and left-click a saved pin / Удерживать при ЛКМ по метке для переименования.");
            Suggestion = config.Bind("Controls", "AcceptSuggestion", new KeyboardShortcut(KeyCode.G, KeyCode.LeftControl),
                "Place the suggested nearby marker / Поставить предложенную метку рядом с обнаруженным объектом.");
            NextSuggestion = config.Bind("Controls", "NextSuggestion", new KeyboardShortcut(KeyCode.G, KeyCode.LeftControl, KeyCode.LeftShift),
                "Choose the next nearby suggestion / Следующее предложение метки.");
            DismissSuggestion = config.Bind("Controls", "DismissSuggestion", new KeyboardShortcut(KeyCode.G, KeyCode.LeftControl, KeyCode.LeftAlt),
                "Hide this suggestion for two minutes / Скрыть это предложение на две минуты.");
            ClearDeathPins = config.Bind("Controls", "ClearDeathPins", new KeyboardShortcut(KeyCode.Delete, KeyCode.LeftControl, KeyCode.LeftShift),
                "Confirm removing all death markers on the large map / Удалить все метки смерти на большой карте с подтверждением.");
            SuggestionEnabled = config.Bind("Suggestions", "Enabled", true, "Show unobtrusive suggestions for the default presets / Предлагать стандартные метки рядом с объектами.");
            DetectionRadius = config.Bind("Suggestions", "DetectionRadius", 20f, new ConfigDescription("Nearby visible objects, metres / Видимые объекты поблизости, метры.", new AcceptableValueRange<float>(5f, 40f)));
            ResourceRadius = config.Bind("Suggestions", "ResourceDuplicateRadius", 40f, new ConfigDescription("Group resources and suppress existing markers, metres / Радиус группы ресурсов и проверки дублей.", new AcceptableValueRange<float>(5f, 100f)));
            PortalRadius = config.Bind("Suggestions", "PortalDuplicateRadius", 8f, new ConfigDescription("Suppress nearby portal markers, metres / Радиус проверки уже отмеченных порталов.", new AcceptableValueRange<float>(1f, 20f)));
        }
        internal static string Label(KeyboardShortcut key)
        {
            if (key.MainKey == KeyCode.None) return "";
            var labels = new System.Collections.Generic.List<string>();
            foreach (KeyCode modifier in key.Modifiers)
            {
                string label = KeyLabel(modifier);
                if (!labels.Contains(label)) labels.Add(label);
            }
            string main = KeyLabel(key.MainKey);
            if (!labels.Contains(main)) labels.Add(main);
            return String.Join("+", labels.ToArray());
        }
        private static string KeyLabel(KeyCode key)
        {
            key = Family(key);
            if (key == KeyCode.LeftControl) return "Ctrl";
            if (key == KeyCode.LeftShift) return "Shift";
            if (key == KeyCode.LeftAlt) return "Alt";
            if (key == KeyCode.LeftCommand) return "Cmd";
            if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9) return ((int)key - (int)KeyCode.Alpha0).ToString();
            return key.ToString();
        }
        internal static bool Down(KeyboardShortcut key)
        { return Matches(key, Input.GetKeyDown, Input.GetKey); }
        internal static bool Held(KeyboardShortcut key)
        { return Matches(key, Input.GetKey, Input.GetKey); }
        // Treat left/right modifiers alike, but reject extra modifier families.
        // KeyCode.None must never turn a normal map click into a shortcut.
        internal static bool Matches(KeyboardShortcut key, Func<KeyCode, bool> trigger, Func<KeyCode, bool> held)
        {
            if (key.MainKey == KeyCode.None || !Either(key.MainKey, trigger)) return false;
            var required = key.Modifiers.ToArray();
            foreach (var modifier in required) if (!Either(modifier, held)) return false;
            foreach (var pair in new[] { KeyCode.LeftControl, KeyCode.LeftShift, KeyCode.LeftAlt, KeyCode.LeftCommand })
            {
                bool wanted = Family(key.MainKey) == pair || required.Any(k => Family(k) == pair);
                if (Either(pair, held) != wanted) return false;
            }
            return true;
        }
        private static KeyCode Family(KeyCode key)
        {
            if (key == KeyCode.RightControl) return KeyCode.LeftControl;
            if (key == KeyCode.RightShift) return KeyCode.LeftShift;
            if (key == KeyCode.RightAlt) return KeyCode.LeftAlt;
            if (key == KeyCode.RightCommand) return KeyCode.LeftCommand;
            return key;
        }
        private static bool Either(KeyCode key, Func<KeyCode, bool> check)
        {
            key = Family(key);
            return check(key) || (key == KeyCode.LeftControl && check(KeyCode.RightControl))
                || (key == KeyCode.LeftShift && check(KeyCode.RightShift))
                || (key == KeyCode.LeftAlt && check(KeyCode.RightAlt))
                || (key == KeyCode.LeftCommand && check(KeyCode.RightCommand));
        }
    }
}
