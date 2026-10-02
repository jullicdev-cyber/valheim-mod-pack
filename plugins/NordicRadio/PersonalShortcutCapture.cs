using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace ValheimModPack.NordicRadio
{
    // Keep ownership of the opening stroke through key-up, including the release
    // frame. Otherwise closing a modal while F is held can activate its game action.
    internal sealed class PersonalShortcutCapture
    {
        private KeyCode key;
        private int releaseFrame = -1;
        private KeyCode observedKey;
        private bool wasHeld, wasDown;
        private int pressFrame = -1;
        internal void Claim(KeyCode main) { key = main; releaseFrame = -1; }
        internal void Reset() { key = KeyCode.None; releaseFrame = -1; }
        internal void Prime(KeyboardShortcut shortcut, int frame, Func<KeyCode, bool> held)
        {
            observedKey = shortcut.MainKey; wasHeld = Held(observedKey, held);
            wasDown = wasHeld;
            pressFrame = wasHeld ? frame : -1;
        }
        internal bool Blocked(int frame, Func<KeyCode, bool> held)
        {
            if (key == KeyCode.None) return false;
            if (releaseFrame >= 0 && frame > releaseFrame) { Reset(); return false; }
            if (!Held(key, held)) releaseFrame = frame;
            return true;
        }
        internal bool Pressed(KeyboardShortcut shortcut, int frame, Func<KeyCode, bool> down, Func<KeyCode, bool> held)
        {
            bool mainHeld = Held(shortcut.MainKey, held);
            bool mainDown = Held(shortcut.MainKey, down);
            // Native ButtonDef caches survive InputSystem update phases. Its GP
            // pressed flag may still be true after wasPressedThisFrame expired.
            // Observe the physical transition too, including disallowed UI, so
            // a held key cannot become a new shortcut just by closing that UI.
            bool sameKey = observedKey == shortcut.MainKey;
            bool edge = !sameKey ? mainDown : !wasHeld && (mainHeld || (mainDown && !wasDown));
            observedKey = shortcut.MainKey; wasHeld = mainHeld; wasDown = mainDown;
            if (shortcut.MainKey == KeyCode.None || !edge || pressFrame == frame) return false;
            pressFrame = frame;
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
