using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace ValheimModPack.PortalFinder
{
    internal static class Shortcut
    {
        internal static bool Pressed(KeyboardShortcut shortcut) { return Matches(shortcut, Input.GetKeyDown, Input.GetKey); }
        internal static bool Held(KeyboardShortcut shortcut) { return Matches(shortcut, Input.GetKey, Input.GetKey); }
        internal static string Label(KeyboardShortcut shortcut)
        {
            if (shortcut.MainKey == KeyCode.None) return "";
            var parts = new List<string>();
            // BepInEx stores modifiers in numeric enum order (Shift before Ctrl).
            // Keep the familiar display order independent of the stored binding.
            foreach (KeyCode modifier in shortcut.Modifiers.Select(Family).Distinct().OrderBy(ModifierOrder).ThenBy(k => (int)k))
            {
                string part = KeyLabel(modifier);
                if (!parts.Contains(part)) parts.Add(part);
            }
            string main = KeyLabel(shortcut.MainKey);
            if (!parts.Contains(main)) parts.Add(main);
            return String.Join("+", parts.ToArray());
        }
        private static int ModifierOrder(KeyCode key)
        {
            if (key == KeyCode.LeftControl) return 0;
            if (key == KeyCode.LeftShift) return 1;
            if (key == KeyCode.LeftAlt) return 2;
            if (key == KeyCode.LeftCommand) return 3;
            return 4;
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
        private static bool Matches(KeyboardShortcut shortcut, Func<KeyCode, bool> trigger, Func<KeyCode, bool> held)
        {
            if (shortcut.MainKey == KeyCode.None || !Either(shortcut.MainKey, trigger)) return false;
            var required = shortcut.Modifiers.ToArray();
            foreach (KeyCode modifier in required) if (!Either(modifier, held)) return false;
            foreach (KeyCode family in new[] { KeyCode.LeftControl, KeyCode.LeftShift, KeyCode.LeftAlt, KeyCode.LeftCommand })
                if (Either(family, held) != (Family(shortcut.MainKey) == family || required.Any(k => Family(k) == family))) return false;
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
        private static bool Either(KeyCode key, Func<KeyCode, bool> read)
        {
            key = Family(key);
            return read(key) || (key == KeyCode.LeftControl && read(KeyCode.RightControl))
                || (key == KeyCode.LeftShift && read(KeyCode.RightShift))
                || (key == KeyCode.LeftAlt && read(KeyCode.RightAlt))
                || (key == KeyCode.LeftCommand && read(KeyCode.RightCommand));
        }
    }
}
