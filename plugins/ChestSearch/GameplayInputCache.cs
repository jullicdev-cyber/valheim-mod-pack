using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;

namespace ValheimModPack.ChestSearch
{
    // Valheim 1.0.16 maintains separate dynamic/fixed button edges. Returning
    // false from GetButtonDown alone leaves those edges buffered until Tick.
    // Discard consumed edges, retaining held movement and controller Cancel.
    internal static class GameplayInputCache
    {
        private static readonly FieldInfo instance = AccessTools.Field(typeof(ZInput), "m_instance");
        private static readonly FieldInfo buttons = AccessTools.Field(typeof(ZInput), "m_buttons");
        private static readonly Type buttonType = typeof(ZInput).GetNestedType("ButtonDef", BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo[] held = Fields("m_heldDynamic", "m_heldFixed");
        private static readonly FieldInfo[] wasPressed = Fields("m_wasPressedDynamic", "m_wasPressedFixed");
        private static readonly FieldInfo[] edges = Fields("m_pressedDynamic", "m_pressedFixed", "m_releasedDynamic", "m_releasedFixed");
        private static FieldInfo[] Fields(params string[] names)
        {
            var values = new FieldInfo[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                values[i] = AccessTools.Field(buttonType, names[i]);
                if (values[i] == null) throw new MissingFieldException(buttonType.FullName, names[i]);
            }
            return values;
        }
        private static IDictionary Buttons()
        {
            object input = instance.GetValue(null);
            return input == null ? null : buttons.GetValue(input) as IDictionary;
        }
        internal static void ConsumeAll()
        {
            IDictionary values = Buttons(); if (values == null) return;
            foreach (DictionaryEntry pair in values)
                if ((string)pair.Key != "JoyButtonB") ConsumeState(pair.Value);
        }
        internal static void Consume(string name)
        {
            if (name == "JoyButtonB") return;
            IDictionary values = Buttons();
            if (values != null && values.Contains(name)) ConsumeState(values[name]);
        }
        private static void ConsumeState(object button)
        {
            foreach (FieldInfo edge in edges) edge.SetValue(button, false);
            for (int i = 0; i < held.Length; i++) wasPressed[i].SetValue(button, held[i].GetValue(button));
        }
    }
}
