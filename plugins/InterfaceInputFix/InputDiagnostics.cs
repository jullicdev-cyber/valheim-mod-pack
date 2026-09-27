using System;
using System.Text;
using HarmonyLib;
using BepInEx.Bootstrap;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ValheimModPack.InterfaceInputFix
{
    public static class InputDiagnostics
    {
        public static string Read()
        {
            var output = new StringBuilder("Input gates: inventory=").Append(InventoryGui.IsVisible())
                .Append(" textInput=").Append(TextInput.IsVisible()).Append(" jotunnBlocked=").Append(ReadStatic("InputBlocked"))
                .Append(" jotunnRequests=").Append(ReadStatic("InputBlockRequests"))
                .Append(" menu=").Append(Menu.IsVisible()).Append(" popup=").Append(UnifiedPopup.IsVisible())
                .Append(" console=").Append(global::Console.IsVisible())
                .Append(" chat=").Append(Chat.instance != null && Chat.instance.HasFocus())
                .Append(" map=").Append(Minimap.IsOpen()).Append(" mapText=").Append(Minimap.InTextInput());
            var instanceField = AccessTools.Field(typeof(TextInput), "m_instance");
            var textInput = instanceField == null ? null : instanceField.GetValue(null);
            if (textInput != null)
            {
                var visible = AccessTools.Field(typeof(TextInput), "m_visibleFrame");
                var panel = AccessTools.Field(typeof(TextInput), "m_panel");
                output.Append(" nativeTextFrame=").Append(visible == null ? "unavailable" : visible.GetValue(textInput));
                var panelObject = panel == null ? null : panel.GetValue(textInput);
                GameObject panelGo = panelObject as GameObject;
                var component = panelObject as Component;
                if (component != null) panelGo = component.gameObject;
                output.Append(" nativeTextPanel=").Append(panelGo != null && panelGo.activeInHierarchy);
            }
            BepInEx.PluginInfo backpack;
            if (Chainloader.PluginInfos.TryGetValue("vapok.mods.adventurebackpacks", out backpack))
            {
                Type type = backpack.Instance.GetType().Assembly.GetType("AdventureBackpacks.Patches.InventoryGuiPatches");
                var field = type == null ? null : AccessTools.Field(type, "BackpackIsOpen");
                output.Append(" backpackFlag=").Append(field == null ? "unavailable" : field.GetValue(null));
            }
            GameObject selected = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
            output.Append(" selectionActive=").Append(selected != null && selected.activeInHierarchy);
            if (selected != null)
                foreach (var behaviour in selected.GetComponentsInParent<MonoBehaviour>(true))
                {
                    if (behaviour == null) continue;
                    var focused = behaviour.GetType().GetProperty("isFocused");
                    if (focused != null && focused.PropertyType == typeof(bool))
                        output.Append(" focus[").Append(behaviour.GetType().Name).Append("]=").Append(focused.GetValue(behaviour, null))
                            .Append(" enabled=").Append(behaviour.isActiveAndEnabled);
                }
            return output.ToString();
        }
        private static object ReadStatic(string name)
        {
            var field = AccessTools.Field(typeof(GUIManager), name); return field == null ? "unavailable" : field.GetValue(null);
        }
    }
}
