// Test-only native API regression; compiled into the isolated probe only.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ValheimModPack.WorldCharacters
{
    public static class WorldCharactersAdministrationInputNativeChecks
    {
        private static int checks;
        private static void Check(bool value, string reason)
        { if (!value) throw new InvalidOperationException("World Characters input: " + reason); checks++; }

        public static string Run()
        {
            checks = 0;
            foreach (string method in new[] { "GetButton", "GetButtonDown", "GetButtonUp" })
                Hook(typeof(ZInput), method, new[] { typeof(string) });
            foreach (string method in new[] { "Update", "FixedUpdate" }) Hook(typeof(ZInput), method, new[] { typeof(float) });
            Hook(typeof(PlayerController), "TakeInput", new[] { typeof(bool) });
            Hook(typeof(Player), "TakeInput", Type.EmptyTypes);
            Hook(typeof(Player), "StartGuardianPower", Type.EmptyTypes);

            object native = AccessTools.Field(typeof(ZInput), "m_instance").GetValue(null);
            Check(native != null, "native ZInput exists in isolated menu");
            var buttons = AccessTools.Field(typeof(ZInput), "m_buttons").GetValue(native) as IDictionary;
            Check(buttons != null && buttons.Contains("GP") && buttons.Contains("JoyButtonB"), "guardian and controller Cancel native definitions available");
            object guardian = buttons["GP"], cancel = buttons["JoyButtonB"];
            Type type = guardian.GetType();
            var fields = new List<FieldInfo>();
            foreach (string name in new[] { "m_heldDynamic", "m_heldFixed", "m_wasPressedDynamic", "m_wasPressedFixed", "m_pressedDynamic", "m_pressedFixed", "m_releasedDynamic", "m_releasedFixed" })
            {
                FieldInfo field = AccessTools.Field(type, name);
                Check(field != null && field.FieldType == typeof(bool), "actual native input field " + name);
                fields.Add(field);
            }
            var originals = new Dictionary<object, bool[]>();
            foreach (DictionaryEntry pair in buttons)
                originals[pair.Value] = fields.Select(f => (bool)f.GetValue(pair.Value)).ToArray();
            try
            {
                foreach (FieldInfo field in fields) { field.SetValue(guardian, true); field.SetValue(cancel, true); }
                Type cache = typeof(Plugin).Assembly.GetType("ValheimModPack.WorldCharacters.GameplayInputCache", true);
                cache.GetMethod("ConsumeAll", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
                foreach (string name in new[] { "m_pressedDynamic", "m_pressedFixed", "m_releasedDynamic", "m_releasedFixed" })
                    Check(!(bool)AccessTools.Field(type, name).GetValue(guardian), "native consumed edge " + name);
                Check((bool)AccessTools.Field(type, "m_heldDynamic").GetValue(guardian)
                    && (bool)AccessTools.Field(type, "m_heldFixed").GetValue(guardian), "physically held input retained in both phases");
                Check((bool)AccessTools.Field(type, "m_wasPressedDynamic").GetValue(guardian)
                    && (bool)AccessTools.Field(type, "m_wasPressedFixed").GetValue(guardian), "held transitions reconciled in both phases");
                foreach (FieldInfo field in fields) Check((bool)field.GetValue(cancel), "Cancel cache retained: " + field.Name);
            }
            finally
            {
                foreach (var pair in originals)
                    for (int i = 0; i < fields.Count; ++i) fields[i].SetValue(pair.Key, pair.Value[i]);
            }
            return "PASS: " + checks + " World Characters native input assertions.";
        }

        private static void Hook(Type type, string method, Type[] arguments)
        {
            MethodInfo target = AccessTools.Method(type, method, arguments);
            Check(target != null, "native method " + type.Name + "." + method);
            Patches patches = Harmony.GetPatchInfo(target);
            Check(patches != null && patches.Owners.Contains(Plugin.Id), "World Characters owns native hook " + type.Name + "." + method);
        }
    }
}
