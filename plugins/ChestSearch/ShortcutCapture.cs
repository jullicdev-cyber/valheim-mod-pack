using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace ValheimModPack.ChestSearch
{
    // Keep ownership of the opening stroke through key-up, including the release
    // frame. Otherwise closing a modal while F is held can activate its game action.
    internal sealed class ShortcutCapture
    {
        private KeyCode key;
        private int releaseFrame = -1;
        internal void Claim(KeyCode main) { key = main; releaseFrame = -1; }
        internal void Reset() { key = KeyCode.None; releaseFrame = -1; }
        internal bool Blocked(int frame, Func<KeyCode, bool> held)
        {
            if (key == KeyCode.None) return false;
            if (releaseFrame >= 0 && frame > releaseFrame) { Reset(); return false; }
            if (!Held(key, held)) releaseFrame = frame;
            return true;
        }
        internal static bool Pressed(KeyboardShortcut shortcut, Func<KeyCode, bool> down, Func<KeyCode, bool> held)
        {
            if (shortcut.MainKey == KeyCode.None || !Held(shortcut.MainKey, down)) return false;
            var allowed = new HashSet<int>();
            int mainGroup = Group(shortcut.MainKey); if (mainGroup != 0) allowed.Add(mainGroup);
            foreach (KeyCode modifier in shortcut.Modifiers)
            {
                if (!Held(modifier, held)) return false;
                int group = Group(modifier); if (group != 0) allowed.Add(group);
            }
            foreach (KeyCode modifier in new[] { KeyCode.LeftControl, KeyCode.LeftShift, KeyCode.LeftAlt, KeyCode.LeftCommand })
                if (Held(modifier, held) && !allowed.Contains(Group(modifier))) return false;
            return true;
        }
        private static int Group(KeyCode key)
        {
            if (key == KeyCode.LeftControl || key == KeyCode.RightControl) return 1;
            if (key == KeyCode.LeftShift || key == KeyCode.RightShift) return 2;
            if (key == KeyCode.LeftAlt || key == KeyCode.RightAlt || key == KeyCode.AltGr) return 3;
            if (key == KeyCode.LeftCommand || key == KeyCode.RightCommand || key == KeyCode.LeftWindows || key == KeyCode.RightWindows) return 4;
            return 0;
        }
        private static bool Held(KeyCode key, Func<KeyCode, bool> read)
        {
            switch (Group(key))
            {
                case 1: return read(KeyCode.LeftControl) || read(KeyCode.RightControl);
                case 2: return read(KeyCode.LeftShift) || read(KeyCode.RightShift);
                case 3: return read(KeyCode.LeftAlt) || read(KeyCode.RightAlt) || read(KeyCode.AltGr);
                case 4: return read(KeyCode.LeftCommand) || read(KeyCode.RightCommand) || read(KeyCode.LeftWindows) || read(KeyCode.RightWindows);
                default: return read(key);
            }
        }
    }
}
