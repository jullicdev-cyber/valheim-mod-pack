using System;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace ValheimModPack.PortalFinder
{
    internal static class Shortcut
    {
        internal static bool Pressed(KeyboardShortcut shortcut) { return Matches(shortcut, Input.GetKeyDown, Input.GetKey); }
        internal static bool Held(KeyboardShortcut shortcut) { return Matches(shortcut, Input.GetKey, Input.GetKey); }
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
