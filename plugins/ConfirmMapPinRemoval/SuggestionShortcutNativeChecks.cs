// Optional probe of the real ZInput cache. Excluded from the release plugin.
// This changes cached flags temporarily, never bindings or physical input.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ValheimModPack.PinRemoval
{
    public static class SuggestionShortcutNativeChecks
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static readonly string[] StateNames = new[]
        {
            "m_heldDynamic", "m_heldFixed", "m_pressedDynamic", "m_pressedFixed",
            "m_wasPressedDynamic", "m_wasPressedFixed", "m_releasedDynamic", "m_releasedFixed"
        };

        public static string Run()
        {
            object input = Field(typeof(ZInput), "m_instance").GetValue(null);
            if (input == null) return "SKIP: suggestion cache checks require an existing native ZInput.";
            IDictionary buttons = Field(typeof(ZInput), "m_buttons").GetValue(input) as IDictionary;
            if (buttons == null || buttons.Count == 0) return "SKIP: suggestion cache checks require populated native buttons.";
            object power = Button(buttons, "GP"), crouch = Button(buttons, "Crouch"), jump = Button(buttons, "Jump");
            if (power == null || crouch == null || jump == null)
                throw new InvalidOperationException("Native suggestion cache probe requires GP, Crouch and Jump buttons.");

            Type buttonType = power.GetType();
            var state = new FieldInfo[StateNames.Length];
            for (int i = 0; i < state.Length; ++i)
            {
                state[i] = Field(buttonType, StateNames[i]);
                if (state[i].FieldType != typeof(bool)) throw new InvalidOperationException("Unexpected native cache field: " + StateNames[i]);
            }
            MethodInfo actionPath = Method(buttonType, "GetActionPath", typeof(bool));
            MethodInfo keyPath = Method(typeof(ZInput), "KeyCodeToPath", typeof(KeyCode), typeof(bool));
            string powerPath = Path(actionPath, power), crouchPath = Path(actionPath, crouch);
            KeyCode powerKey = ResolveKey(keyPath, powerPath), crouchKey = ResolveKey(keyPath, crouchPath);
            if (powerKey == KeyCode.None || crouchKey == KeyCode.None)
                return "SKIP: GP/Crouch have no resolvable native keyboard bindings (GP=" + powerPath + ", Crouch=" + crouchPath + ").";

            // Include the actual Crouch key if the player rebound it. LeftControl
            // also verifies modifier-family paths without changing that binding.
            KeyCode[] keys = new[] { powerKey, KeyCode.LeftControl, crouchKey };
            HashSet<string> claimedPaths = ClaimedPaths(keyPath, keys);
            if (claimedPaths.Contains(Path(actionPath, jump)))
                return "SKIP: the native Jump binding overlaps GP/Crouch; it cannot serve as an unrelated control.";

            Type cacheType = typeof(SuggestionShortcutGate).Assembly.GetType("ValheimModPack.PinRemoval.SuggestionButtonCache", true);
            object cache = Activator.CreateInstance(cacheType, true);
            MethodInfo consume = Method(cacheType, "Consume", typeof(IEnumerable<KeyCode>));
            MethodInfo consumeNamed = Method(cacheType, "Consume", typeof(string), typeof(IEnumerable<KeyCode>));
            PropertyInfo cacheInput = cacheType.GetProperty("Input", All);
            if (cacheInput == null || !ReferenceEquals(cacheInput.GetValue(cache, null), input))
                throw new InvalidOperationException("Production suggestion cache must target the current native ZInput instance.");

            // Consume may touch several native actions sharing one keyboard key.
            // Save every button so even those actions are restored after failures.
            var saved = new Dictionary<object, bool[]>();
            foreach (DictionaryEntry pair in buttons)
                if (pair.Value != null && !saved.ContainsKey(pair.Value)) saved.Add(pair.Value, Read(state, pair.Value));
            int checks = 0;
            object gamepad = Button(buttons, "JoyButtonB"), hotbar = Button(buttons, "HotbarUse");
            bool hotbarUnbound = hotbar != null && String.IsNullOrEmpty(Path(actionPath, hotbar));
            try
            {
                Seed(state, power, true, false);
                Seed(state, crouch, false, true);
                Seed(state, jump, true, false);
                bool[] jumpBefore = Read(state, jump);
                bool[] gamepadBefore = null, hotbarBefore = null;
                if (gamepad != null)
                {
                    Check(!claimedPaths.Contains(Path(actionPath, gamepad)), "Native JoyButtonB is unrelated to the keyboard chord", ref checks);
                    Seed(state, gamepad, false, true); gamepadBefore = Read(state, gamepad);
                }
                if (hotbar != null && !claimedPaths.Contains(Path(actionPath, hotbar)))
                { Seed(state, hotbar, true, false); hotbarBefore = Read(state, hotbar); }

                // Enumerates the real dictionary, including the virtual HotbarUse
                // action whose GetActionPath may index an absent binding zero.
                consume.Invoke(cache, new object[] { keys });
                CheckDrained(state, power, true, false, "GP", ref checks);
                CheckDrained(state, crouch, false, true, "Crouch", ref checks);
                Check(Equal(jumpBefore, Read(state, jump)), "Unrelated Jump cache remains intact in both input phases", ref checks);
                if (gamepadBefore != null)
                    Check(Equal(gamepadBefore, Read(state, gamepad)), "Gamepad JoyButtonB cache remains intact", ref checks);
                if (hotbarBefore != null)
                    Check(Equal(hotbarBefore, Read(state, hotbar)), "Unrelated/virtual HotbarUse cache remains intact without an exception", ref checks);

                Seed(state, power, false, true);
                Check((bool)consumeNamed.Invoke(cache, new object[] { "GP", keys }), "Named GP interception matches its actual native binding", ref checks);
                CheckDrained(state, power, false, true, "Named GP", ref checks);
                Check(!(bool)consumeNamed.Invoke(cache, new object[] { "Jump", keys }), "Named unrelated Jump query remains available", ref checks);
                Check(Equal(jumpBefore, Read(state, jump)), "Named interception preserves unrelated cached Jump edges", ref checks);
                if (gamepadBefore != null)
                    Check(!(bool)consumeNamed.Invoke(cache, new object[] { "JoyButtonB", keys }), "Named gamepad query remains available", ref checks);
                if (hotbarUnbound)
                    Check(!(bool)consumeNamed.Invoke(cache, new object[] { "HotbarUse", keys }), "Unbound virtual HotbarUse is safely ignored by named interception", ref checks);

                return "PASS: " + checks + " native suggestion cache assertions; " + buttons.Count + " buttons saved/restored; GP=" + powerKey
                    + " (" + powerPath + "), Crouch=" + crouchKey + " (" + crouchPath + "), HotbarUse="
                    + (hotbar == null ? "absent" : hotbarUnbound ? "unbound virtual" : "bound")
                    + ". Physical keyboard input and player actions were not simulated.";
            }
            finally
            {
                Exception restoreError = null;
                foreach (KeyValuePair<object, bool[]> pair in saved)
                    for (int i = 0; i < state.Length; ++i)
                        try { state[i].SetValue(pair.Key, pair.Value[i]); }
                        catch (Exception error) { if (restoreError == null) restoreError = error; }
                if (restoreError != null) throw new InvalidOperationException("Native suggestion probe could not restore every cached button flag.", restoreError);
            }
        }

        private static object Button(IDictionary buttons, string name)
        { return buttons.Contains(name) ? buttons[name] : null; }

        private static FieldInfo Field(Type type, string name)
        {
            FieldInfo field = type.GetField(name, All);
            if (field == null) throw new MissingFieldException(type.FullName, name);
            return field;
        }

        private static MethodInfo Method(Type type, string name, params Type[] arguments)
        {
            MethodInfo method = type.GetMethod(name, All, null, arguments, null);
            if (method == null) throw new MissingMethodException(type.FullName, name);
            return method;
        }

        private static string Path(MethodInfo actionPath, object button)
        {
            try { return actionPath.Invoke(button, new object[] { true }) as string ?? String.Empty; }
            catch (TargetInvocationException error)
            {
                if (error.InnerException is IndexOutOfRangeException || error.InnerException is ArgumentOutOfRangeException
                    || error.InnerException is NullReferenceException) return String.Empty;
                throw;
            }
        }

        private static KeyCode ResolveKey(MethodInfo keyPath, string path)
        {
            if (String.IsNullOrEmpty(path)) return KeyCode.None;
            foreach (KeyCode key in Enum.GetValues(typeof(KeyCode)))
                if (ZInput.IsKeyCodeValid(key) && String.Equals(path,
                    keyPath.Invoke(null, new object[] { key, false }) as string, StringComparison.OrdinalIgnoreCase)) return key;
            if (String.Equals(path, "<Keyboard>/ctrl", StringComparison.OrdinalIgnoreCase)) return KeyCode.LeftControl;
            if (String.Equals(path, "<Keyboard>/shift", StringComparison.OrdinalIgnoreCase)) return KeyCode.LeftShift;
            if (String.Equals(path, "<Keyboard>/alt", StringComparison.OrdinalIgnoreCase)) return KeyCode.LeftAlt;
            if (String.Equals(path, "<Keyboard>/meta", StringComparison.OrdinalIgnoreCase)) return KeyCode.LeftCommand;
            return KeyCode.None;
        }

        private static HashSet<string> ClaimedPaths(MethodInfo keyPath, IEnumerable<KeyCode> keys)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyCode key in keys)
            {
                paths.Add(keyPath.Invoke(null, new object[] { key, false }) as string ?? String.Empty);
                if (key == KeyCode.LeftControl || key == KeyCode.RightControl)
                {
                    paths.Add(keyPath.Invoke(null, new object[] { KeyCode.LeftControl, false }) as string ?? String.Empty);
                    paths.Add(keyPath.Invoke(null, new object[] { KeyCode.RightControl, false }) as string ?? String.Empty);
                    paths.Add("<Keyboard>/ctrl");
                }
            }
            return paths;
        }

        private static bool[] Read(FieldInfo[] fields, object button)
        {
            var result = new bool[fields.Length];
            for (int i = 0; i < result.Length; ++i) result[i] = (bool)fields[i].GetValue(button);
            return result;
        }

        private static void Seed(FieldInfo[] fields, object button, bool dynamicHeld, bool fixedHeld)
        {
            bool[] values = new[] { dynamicHeld, fixedHeld, true, true, !dynamicHeld, !fixedHeld, true, true };
            for (int i = 0; i < fields.Length; ++i) fields[i].SetValue(button, values[i]);
        }

        private static void CheckDrained(FieldInfo[] fields, object button, bool dynamicHeld, bool fixedHeld, string name, ref int checks)
        {
            bool[] actual = Read(fields, button);
            Check(!actual[2] && !actual[3] && !actual[6] && !actual[7], name + " press/release edges are drained in dynamic and fixed phases", ref checks);
            Check(actual[0] == dynamicHeld && actual[1] == fixedHeld, name + " held state is preserved", ref checks);
            Check(actual[4] == dynamicHeld && actual[5] == fixedHeld, name + " prior press state is aligned to held state in both phases", ref checks);
        }

        private static bool Equal(bool[] left, bool[] right)
        {
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; ++i) if (left[i] != right[i]) return false;
            return true;
        }

        private static void Check(bool condition, string message, ref int checks)
        {
            if (!condition) throw new InvalidOperationException("Native suggestion cache check failed: " + message);
            ++checks;
        }
    }
}
