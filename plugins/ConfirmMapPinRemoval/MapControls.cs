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
