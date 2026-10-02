// Optional isolated-engine probe. Not compiled into either released mod.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ValheimModPack.PinRemoval
{
    public static class MapLauncherNativeChecks
    {
        private sealed class Binding
        {
            internal object Entry, Original;
            internal PropertyInfo Value;
            internal void Set(KeyCode main, params KeyCode[] modifiers)
            { Value.SetValue(Entry, Activator.CreateInstance(Original.GetType(), new object[] { main, modifiers }), null); }
            internal void Restore() { Value.SetValue(Entry, Original, null); }
        }
        private sealed class ConfigState
        {
            internal object Config;
            internal PropertyInfo Save;
            internal bool OriginalSave;
            internal readonly List<Binding> Bindings = new List<Binding>();
            internal ConfigState(object plugin)
            {
                Config = plugin.GetType().GetProperty("Config", BindingFlags.Public | BindingFlags.Instance).GetValue(plugin, null);
                Save = Config.GetType().GetProperty("SaveOnConfigSet"); OriginalSave = (bool)Save.GetValue(Config, null);
                Save.SetValue(Config, false, null);
            }
            internal Binding Find(string key)
            {
                foreach (object pair in (IEnumerable)Config)
                {
                    object entry = pair.GetType().GetProperty("Value").GetValue(pair, null);
                    object definition = entry.GetType().GetProperty("Definition").GetValue(entry, null);
                    string section = (string)definition.GetType().GetProperty("Section").GetValue(definition, null);
                    string name = (string)definition.GetType().GetProperty("Key").GetValue(definition, null);
                    if (section != "Controls" || name != key) continue;
                    var result = new Binding { Entry = entry, Value = entry.GetType().GetProperty("BoxedValue") };
                    result.Original = result.Value.GetValue(entry, null); Bindings.Add(result); return result;
                }
                throw new MissingMemberException("Configured map shortcut is absent: " + key);
            }
            internal void Restore()
            {
                try { foreach (Binding binding in Bindings) binding.Restore(); }
                finally { Save.SetValue(Config, OriginalSave, null); }
            }
        }
        private sealed class LauncherState
        {
            internal readonly object Owner;
            internal readonly FieldInfo Field;
            internal readonly GameObject Original;
            internal readonly bool WasActive;
            private readonly Text[] OriginalLabels;
            private readonly string[] OriginalCaptions;
            internal LauncherState(object owner, string field)
            {
                Owner = owner; Field = owner.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance);
                Original = (GameObject)Field.GetValue(owner); WasActive = Original != null && Original.activeSelf;
                OriginalLabels = Original == null ? new Text[0] : Original.GetComponentsInChildren<Text>(true);
                OriginalCaptions = new string[OriginalLabels.Length];
                for (int i = 0; i < OriginalLabels.Length; ++i) OriginalCaptions[i] = OriginalLabels[i].text;
            }
            internal GameObject Current { get { return (GameObject)Field.GetValue(Owner); } }
            internal void Restore()
            {
                GameObject current = Current;
                if (Original == null && current != null) UnityEngine.Object.DestroyImmediate(current);
                Field.SetValue(Owner, Original);
                if (Original != null)
                {
                    for (int i = 0; i < OriginalLabels.Length; ++i)
                        if (OriginalLabels[i] != null) OriginalLabels[i].text = OriginalCaptions[i];
                    Original.SetActive(WasActive);
                }
            }
        }
        private static int checks;
        private static void Check(bool value, string message)
        { checks++; if (!value) throw new InvalidOperationException("Map launcher native check: " + message); }
        private static object Field(object instance, string name)
        { return instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance); }
        private static void Call(object instance, string method, params object[] args)
        { instance.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(instance, args); }
        private static object Plugin(Assembly assembly, string type, string field)
        { return assembly.GetType(type, true).GetField(field, BindingFlags.NonPublic | BindingFlags.Static).GetValue(null); }
        private static Assembly FindAssembly(string type)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) if (assembly.GetType(type, false) != null) return assembly;
            return null;
        }
        private static Text Label(GameObject button, string description, string hotkey)
        {
            Check(button != null, description + " button exists");
            Text text = button.GetComponentInChildren<Text>(true);
            Check(text != null && text.font != null, description + " has actual Jotunn legacy Text and font");
            Check(button.activeSelf && text.enabled && text.color.a > .95f, description + " has an enabled, opaque caption");
            Check(text.text.IndexOf('\n') > 0, description + " separates action and shortcut into two lines");
            if (hotkey != null) Check(text.text.EndsWith("[" + hotkey + "]", StringComparison.Ordinal), description + " shows the current shortcut: " + hotkey);
            else Check(text.text.IndexOf('[') < 0 && !String.IsNullOrWhiteSpace(text.text.Substring(text.text.IndexOf('\n') + 1)), description + " explicitly labels an unbound shortcut");
            Check(!text.supportRichText && text.horizontalOverflow == HorizontalWrapMode.Wrap
                && text.resizeTextForBestFit && text.resizeTextMinSize >= 14 && text.resizeTextMaxSize <= 17,
                description + " uses bounded wrapping plain-text font sizing");
            Check(text.alignment == TextAnchor.MiddleCenter, description + " centers both caption lines");
            var rect = button.GetComponent<RectTransform>();
            Check(rect.rect.width >= 419 && rect.rect.height >= 49, description + " has room for the full action and shortcut");
            Check(text.rectTransform.rect.width >= 419 && text.rectTransform.rect.height >= 49, description + " label rectangle matches the button");
            Check(text.preferredHeight <= text.rectTransform.rect.height + .5f, description + " native font layout fits vertically");
            Check(button.GetComponent<Button>() != null && button.GetComponent<Button>().interactable, description + " remains mouse-operable");
            return text;
        }
        public static string Run()
        {
            if (GUIManager.CustomGUIFront == null) return "SKIP: map launcher checks require Jotunn canvas.";
            // GUIManager.Init deliberately skips InitializeAssets in headless mode.
            // A manually created canvas cannot establish real font metrics there.
            if (GUIManager.Instance.AveriaSerifBold == null) return "SKIP: map launcher font/layout checks require graphical Valheim; Jotunn omits fonts in headless mode.";
            if (Player.m_localPlayer != null) return "SKIP: map launcher checks run only in the isolated menu, without an active character.";
            checks = 0;
            Assembly mapAssembly = typeof(PinArchive).Assembly;
            Assembly portalAssembly = FindAssembly("ValheimModPack.PortalFinder.Plugin");
            Check(portalAssembly != null, "packaged Portal Finder is loaded");
            object mapPlugin = Plugin(mapAssembly, "ValheimModPack.PinRemoval.Plugin", "plugin");
            object portalPlugin = Plugin(portalAssembly, "ValheimModPack.PortalFinder.Plugin", "active");
            Check(mapPlugin != null && portalPlugin != null, "both actual packaged plugin instances initialized");
            object history = Field(mapPlugin, "history"), quick = Field(mapPlugin, "quick"), deaths = Field(mapPlugin, "deaths");
            Check(history != null && quick != null && deaths != null, "actual map launcher controllers initialized");
            int baseline = (int)typeof(GUIManager).GetField("InputBlockRequests", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            GameObject focus = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
            var launchers = new List<LauncherState> {
                new LauncherState(history, "launcher"), new LauncherState(quick, "launcher"), new LauncherState(deaths, "launcher"),
                new LauncherState(portalPlugin, "ownButton"), new LauncherState(portalPlugin, "pointButton"), new LauncherState(portalPlugin, "captionObject") };
            object originalCaption = Field(portalPlugin, "caption");
            FieldInfo armed = portalPlugin.GetType().GetField("armed", BindingFlags.NonPublic | BindingFlags.Instance);
            bool originalArmed = (bool)armed.GetValue(portalPlugin);
            ConfigState mapConfig = null, portalConfig = null;
            try
            {
                mapConfig = new ConfigState(mapPlugin); portalConfig = new ConfigState(portalPlugin);
                Binding historyKey = mapConfig.Find("OpenHistory"), quickKey = mapConfig.Find("QuickPin"), deathKey = mapConfig.Find("ClearDeathPins");
                Binding ownKey = portalConfig.Find("FindNearestToPlayer"), pointKey = portalConfig.Find("SelectMapPoint");
                historyKey.Set(KeyCode.F6, KeyCode.LeftControl); quickKey.Set(KeyCode.F7, KeyCode.LeftControl);
                deathKey.Set(KeyCode.F8, KeyCode.LeftControl, KeyCode.LeftShift);
                ownKey.Set(KeyCode.F9, KeyCode.LeftControl); pointKey.Set(KeyCode.F10, KeyCode.LeftControl, KeyCode.LeftShift);
                Action render = delegate {
                    Call(history, "SetLauncherVisible", true); Call(quick, "SetLauncher", true); Call(deaths, "SetLauncher", true);
                    Call(portalPlugin, "EnsureButtons"); Call(portalPlugin, "RefreshText"); Call(portalPlugin, "SetVisible", true);
                    Canvas.ForceUpdateCanvases(); };
                render();
                for (int i = 0; i < 5; ++i)
                {
                    var a = launchers[i].Current.GetComponent<RectTransform>();
                    for (int j = i + 1; j < 5; ++j)
                    {
                        var b = launchers[j].Current.GetComponent<RectTransform>();
                        Check(Mathf.Abs(a.anchoredPosition.y - b.anchoredPosition.y) >= (a.rect.height + b.rect.height) * .5f,
                            "map launcher rows " + i + " and " + j + " do not overlap");
                    }
                }
                Label(launchers[0].Current, "history", "Ctrl+F6"); Label(launchers[1].Current, "presets", "Ctrl+F7");
                Label(launchers[2].Current, "death cleanup", "Ctrl+Shift+F8");
                Text ownText = Label(launchers[3].Current, "portal nearest to player", "Ctrl+F9");
                Text pointText = Label(launchers[4].Current, "portal nearest to map point", "Ctrl+Shift+F10");
                string portalWord = Localization.instance != null && Localization.instance.GetSelectedLanguage() == "Russian" ? "портал" : "portal";
                Check(ownText.text.IndexOf(portalWord, StringComparison.OrdinalIgnoreCase) >= 0
                    && pointText.text.IndexOf(portalWord, StringComparison.OrdinalIgnoreCase) >= 0, "portal actions clearly name portals");
                string beforeArming = pointText.text;
                armed.SetValue(portalPlugin, true); Call(portalPlugin, "RefreshText"); Canvas.ForceUpdateCanvases();
                Check(pointText.text != beforeArming && pointText.text.IndexOf("Ctrl+Shift+F10", StringComparison.Ordinal) >= 0,
                    "armed point selection changes its action caption and retains its current shortcut");
                Label(launchers[4].Current, "cancel portal point selection", "Ctrl+Shift+F10"); armed.SetValue(portalPlugin, false);
                historyKey.Set(KeyCode.F11, KeyCode.LeftAlt); quickKey.Set(KeyCode.F12, KeyCode.LeftAlt);
                deathKey.Set(KeyCode.Delete, KeyCode.LeftAlt, KeyCode.LeftShift); ownKey.Set(KeyCode.J, KeyCode.LeftAlt); pointKey.Set(KeyCode.J, KeyCode.LeftAlt, KeyCode.LeftShift);
                render();
                Label(launchers[0].Current, "rebound history", "Alt+F11"); Label(launchers[1].Current, "rebound presets", "Alt+F12");
                Label(launchers[2].Current, "rebound death cleanup", "Shift+Alt+Delete"); Label(launchers[3].Current, "rebound portal near player", "Alt+J");
                Label(launchers[4].Current, "rebound portal near point", "Shift+Alt+J");
                foreach (Binding key in new[] { historyKey, quickKey, deathKey, ownKey, pointKey }) key.Set(KeyCode.None);
                render();
                for (int i = 0; i < 5; ++i) Label(launchers[i].Current, "unbound map action " + i, null);

                // Check language-dependent text without writing PlatformPrefs.
                Type presentation = mapAssembly.GetType("ValheimModPack.PinRemoval.PinLauncherLabel", true);
                MethodInfo apply = presentation.GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Static);
                Check(apply != null, "shared pin launcher presentation helper is packaged");
                apply.Invoke(null, new object[] { launchers[1].Current, "Quick map presets", "Ctrl+P", "Not assigned" });
                string beforeLanguage = launchers[1].Current.GetComponentInChildren<Text>(true).text;
                apply.Invoke(null, new object[] { launchers[1].Current, "Пресеты меток", "Ctrl+P", "Не назначено" });
                Canvas.ForceUpdateCanvases();
                Text localized = Label(launchers[1].Current, "localized presets", "Ctrl+P");
                Check(localized.text != beforeLanguage && localized.text.StartsWith("Пресеты меток\n", StringComparison.Ordinal), "caption refresh retains current key after supplied language changes");
                Check((int)typeof(GUIManager).GetField("InputBlockRequests", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null) == baseline,
                    "launcher rendering and rebinding acquire no input lease");
                Check(EventSystem.current == null || EventSystem.current.currentSelectedGameObject == focus, "launcher rendering preserves keyboard focus");
                return "PASS: " + checks + " native actual-map-launcher rebind/unbound/font/layout/portal-label assertions.";
            }
            finally
            {
                try { if (mapConfig != null) mapConfig.Restore(); }
                finally
                {
                    try { if (portalConfig != null) portalConfig.Restore(); }
                    finally
                    {
                        armed.SetValue(portalPlugin, originalArmed);
                        try { foreach (LauncherState state in launchers) state.Restore(); }
                        finally
                        {
                            portalPlugin.GetType().GetField("caption", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(portalPlugin, originalCaption);
                            if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != focus) EventSystem.current.SetSelectedGameObject(focus);
                            Canvas.ForceUpdateCanvases();
                        }
                    }
                }
            }
        }
    }
}
