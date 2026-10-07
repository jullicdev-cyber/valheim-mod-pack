// Optional graphical menu probe. Compiled separately; never shipped in XPortal.dll.
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ValheimModPack.AnyPortalPlus
{
    public static class PortalUiNativeChecks
    {
        private static int checks;
        private static Sprite fixtureIcon;
        private static object diagnosticPanel;
        private static bool fixturePopupVisible;
        private static int inputRequests;
        private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static void Check(bool value, string message)
        {
            checks++;
            if (!value) throw new InvalidOperationException("AnyPortal+ native UI: " + message + "; " + ViewState());
        }
        private static string ViewState()
        {
            if (diagnosticPanel == null) return "panel not constructed";
            try
            {
                var search = Get(diagnosticPanel, "searchInputField") as InputField;
                var group = Get(diagnosticPanel, "groupByBiomeToggle") as Toggle;
                var sort = Get(diagnosticPanel, "sortDropdown") as Dropdown;
                var rows = Get(diagnosticPanel, "rows") as Button[];
                var detail = new System.Text.StringBuilder("search='").Append(search == null ? "<null>" : search.text)
                    .Append("' grouped=").Append(group == null ? "<null>" : group.isOn.ToString())
                    .Append(" rebuilding=").Append(Get(diagnosticPanel, "rebuilding"))
                    .Append(" page=").Append(Get(diagnosticPanel, "page"))
                    .Append(" sort=").Append(sort == null ? -1 : sort.value)
                    .Append(" entries=").Append(((IList)Get(diagnosticPanel, "entries")).Count)
                    .Append(" displayRows=").Append(((IList)Get(diagnosticPanel, "displayRows")).Count);
                if (rows != null) for (int i = 0; i < Math.Min(3, rows.Length); i++) if (rows[i] != null)
                    detail.Append(" row").Append(i).Append("[active=").Append(rows[i].gameObject.activeSelf)
                        .Append(" interactable=").Append(rows[i].interactable)
                        .Append(" text='").Append(rows[i].GetComponentInChildren<Text>().text).Append("']");
                return detail.ToString();
            }
            catch (Exception error) { return "state diagnostic unavailable: " + error.Message; }
        }
        private static object Get(object value, string field) { return value.GetType().GetField(field, Flags).GetValue(value); }
        private static void Set(object value, string field, object result) { value.GetType().GetField(field, Flags).SetValue(value, result); }
        private static void Call(object value, string method, params object[] arguments) { value.GetType().GetMethod(method, Flags).Invoke(value, arguments); }
        private static bool FixtureSprite(int icon, ref Sprite __result) { __result = icon < 0 ? null : fixtureIcon; return false; }
        private static bool FixturePopup(ref bool __result) { __result = fixturePopupVisible; return false; }
        private static bool FixtureInputLease(bool __0) { inputRequests += __0 ? 1 : -1; return false; }
        private static bool FixtureRegistryRefresh(object __instance) { return !ReferenceEquals(__instance, diagnosticPanel); }
        private static void Controls(object panel, bool enabled, string scenario)
        {
            foreach (string field in new[] { "portalNameInputField", "searchInputField", "sortDropdown", "iconDropdown", "defaultPortalToggle", "groupByBiomeToggle", "noneButton", "cancelButton", "cleanupButton" })
                Check(((Selectable)Get(panel, field)).IsInteractable() == enabled, scenario + ": effective native interaction for " + field);
        }
        public static string Run()
        {
            diagnosticPanel = null;
            string isolated = System.Environment.GetEnvironmentVariable("VMP_ANYPORTAL_UI_PROBE_ROOT")
                ?? System.Environment.GetEnvironmentVariable("VMP_QOL_SMOKE_ROOT");
            Check(!String.IsNullOrEmpty(isolated) && Path.GetFullPath(Paths.BepInExRootPath).StartsWith(Path.GetFullPath(isolated) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "isolated BepInEx root is required");
            Check(Player.m_localPlayer == null, "probe does not run in a world");
            if (GUIManager.CustomGUIFront == null || GUIManager.Instance.AveriaSerif == null || GUIManager.IsHeadless())
                return "SKIP: AnyPortal+ UI probe requires the graphical Jotunn menu canvas.";
            checks = 0;
            var assembly = Chainloader.PluginInfos["yay.spikehimself.xportal"].Instance.GetType().Assembly;
            Type panelType = assembly.GetType("XPortal.UI.PortalConfigurationPanel", true);
            Type portalType = assembly.GetType("XPortal.KnownPortal", true);
            Type entryType = assembly.GetType("XPortal.Plus.PortalEntry", true);
            Type markersType = assembly.GetType("XPortal.Plus.PlusMapMarkers", true);
            Check(AccessTools.Method(typeof(Minimap), "GetSprite", new[] { typeof(Minimap.PinType) }) != null, "native map icon API still exists");
            var harmony = new Harmony("valheimmodpack.anyportalplus.uiprobe");
            var texture = new Texture2D(8, 8); fixtureIcon = Sprite.Create(texture, new Rect(0, 0, 8, 8), new Vector2(.5f, .5f));
            object panel = Activator.CreateInstance(panelType, true);
            diagnosticPanel = panel;
            GameObject foreignGroup = null, baseline = null;
            GameObject previousSelection = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
            try
            {
                harmony.Patch(AccessTools.Method(markersType, "GetIconSprite"), prefix: new HarmonyMethod(typeof(PortalUiNativeChecks), "FixtureSprite"));
                harmony.Patch(AccessTools.Method(typeof(UnifiedPopup), "IsVisible"), prefix: new HarmonyMethod(typeof(PortalUiNativeChecks), "FixturePopup"));
                harmony.Patch(AccessTools.Method(typeof(GUIManager), "BlockInput"), prefix: new HarmonyMethod(typeof(PortalUiNativeChecks), "FixtureInputLease"));
                // ConfigurePortal runs its real modal/opening lifecycle. Only
                // registry refresh is supplied by our detached fixture below;
                // no live network, player, portal registry or world is created.
                harmony.Patch(AccessTools.Method(panelType, "RefreshRegistry"), prefix: new HarmonyMethod(typeof(PortalUiNativeChecks), "FixtureRegistryRefresh"));
                fixturePopupVisible = false; inputRequests = 7;
                Call(panel, "InitialiseUI");
                var main = (GameObject)Get(panel, "mainPanel"); Check(main != null && !main.activeSelf, "native wood panel constructs without opening gameplay input");
                RectTransform rect = main.GetComponent<RectTransform>(); Check(rect.rect.width >= 979 && rect.rect.height >= 859, "requested native panel dimensions");
                // Reproduce the installed native priority arbitration, including
                // its write to a parent CanvasGroup and actual Unity Selectables.
                foreignGroup = new GameObject("AnyPortalPlus.ForeignGroup", typeof(RectTransform), typeof(CanvasGroup), typeof(UIGroupHandler));
                UIGroupHandler foreign = foreignGroup.GetComponent<UIGroupHandler>(); foreign.m_groupPriority = Int32.MaxValue - 1;
                baseline = new GameObject("AnyPortalPlus.OldDefaultGroup", typeof(RectTransform), typeof(CanvasGroup), typeof(UIGroupHandler));
                var baselineButton = new GameObject("Button", typeof(RectTransform), typeof(Button)); baselineButton.transform.SetParent(baseline.transform, false);
                Check(baselineButton.GetComponent<Button>().IsInteractable(), "default native control begins locally enabled");
                MethodInfo groupUpdate = AccessTools.Method(typeof(UIGroupHandler), "Update");
                groupUpdate.Invoke(baseline.GetComponent<UIGroupHandler>(), null);
                Check(!baseline.GetComponent<CanvasGroup>().interactable && !baselineButton.GetComponent<Button>().IsInteractable(),
                    "installed UIGroupHandler reproduces the old disabled-parent bug after a competing group updates");
                object origin = Known(portalType, new ZDOID(1L, 1), "Current", 1, -1, Vector3.zero);
                Call(panel, "ConfigurePortal", origin); Call(panel, "Show");
                groupUpdate.Invoke(foreign, null);
                Check(main.activeInHierarchy && main.GetComponent<UIGroupHandler>() == null && inputRequests == 8,
                    "real opening has one canvas owner and one counted input lease under InterfaceInputFix");
                Controls(panel, true, "open beside a higher-priority native group");
                fixturePopupVisible = true; Call(panel, "UpdateModalInput"); groupUpdate.Invoke(foreign, null);
                Controls(panel, false, "foreign confirmation suspends the portal");
                if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(foreignGroup);
                Call(panel, "ActivateInputField", false, null);
                Check(EventSystem.current == null || EventSystem.current.currentSelectedGameObject == foreignGroup,
                    "delayed focus respects the visible foreign popup");
                Call(panel, "Hide", false, null); Call(panel, "ConfigurePortal", origin);
                Controls(panel, false, "real close and reopen while the confirmation is still visible");
                Check(inputRequests == 8, "popup close and reopen retain one lease without consuming foreign requests");
                fixturePopupVisible = false; Call(panel, "UpdateModalInput");
                Controls(panel, true, "foreign confirmation closes");
                main.GetComponent<CanvasGroup>().interactable = false;
                Call(panel, "Hide", false, null); Call(panel, "ConfigurePortal", origin);
                Controls(panel, true, "real ConfigurePortal restores a stale disabled canvas before Show is intercepted");
                Check(inputRequests == 8, "repeated real ConfigurePortal preserves the foreign input baseline");
                // Jotunn styles Unity's DefaultControls toggle, which starts checked.
                // Establish the requested view explicitly before testing row count.
                var grouping = (Toggle)Get(panel, "groupByBiomeToggle"); grouping.isOn = false;
                ((Dropdown)Get(panel, "sortDropdown")).value = 0;
                Check(!grouping.isOn, "native fixture explicitly starts with an ungrouped portal view");
                // Only this fixture canvas is open, with its input lease counted
                // above instead of changing the menu's shared input requests.
                Check(main.activeInHierarchy, "isolated native portal canvas is visible while events are tested");
                var icons = (Dropdown)Get(panel, "iconDropdown");
                Check(icons.options.Count == 6 && icons.options[0].image == null, "None plus five map-icon options");
                Check(icons.itemImage != null && icons.captionImage != null, "dropdown has actual Unity option and caption images");
                for (int i = 1; i < icons.options.Count; i++) Check(icons.options[i].image == fixtureIcon, "map-icon sprite reaches option " + i);
                Set(panel, "thisPortal", origin); Set(panel, "selectedTargetId", new ZDOID(1L, 2));
                var entries = (IList)Get(panel, "entries"); var portals = (IDictionary)Get(panel, "portalsById");
                portals.Add(new ZDOID(1L, 1).ToString(), origin);
                for (uint id = 2; id <= 25; id++)
                {
                    var identity = new ZDOID(1L, id); string name = id == 2 ? "<b>Selected</b>" : "Portal " + id;
                    int biome = id < 16 ? 8 : 16; int icon = id == 2 ? 6 : -1; Vector3 point = new Vector3(id * 10, 0, 0);
                    object known = Known(portalType, identity, name, biome, icon, point); portals.Add(identity.ToString(), known);
                    object entry = Activator.CreateInstance(entryType); Set(entry, "Id", identity.ToString()); Set(entry, "Name", name);
                    Set(entry, "Biome", id < 16 ? "BlackForest" : "Plains"); Set(entry, "Icon", icon);
                    Set(entry, "X", (double)point.x); Set(entry, "Y", 0d); Set(entry, "Z", 0d); entries.Add(entry);
                }
                Call(panel, "RebuildList", true);
                var rows = (Button[])Get(panel, "rows"); Check(rows.Length == 10, "bounded reused Unity rows");
                Check(!rows[0].GetComponentInChildren<Text>().supportRichText, "portal names cannot inject rich markup");
                var search = (InputField)Get(panel, "searchInputField"); search.text = "Selected";
                Check(rows[0].gameObject.activeSelf && !rows[1].gameObject.activeSelf, "real native input event filters visible rows");
                Check(rows[0].GetComponentInChildren<Text>().text.Contains("<b>Selected</b>"), "literal markup remains plain portal text");
                var rowIcons = (Image[])Get(panel, "rowIcons"); Check(rowIcons[0].gameObject.activeSelf && rowIcons[0].sprite == fixtureIcon, "portal row displays its separate image");
                Check((ZDOID)Get(panel, "selectedTargetId") == new ZDOID(1L, 2), "native search event preserves selected identity");
                search.text = "Nothing matches"; Check(((Text)Get(panel, "emptyLabel")).gameObject.activeSelf, "native empty-results caption is visible");
                search.text = ""; grouping.isOn = true;
                search.text = "Selected";
                Check(rows[0].gameObject.activeSelf && !rows[0].interactable && rows[1].gameObject.activeSelf && rows[1].interactable && !rows[2].gameObject.activeSelf,
                    "grouped native search shows exactly its biome heading and one matching portal");
                search.text = "";
                ((Button)Get(panel, "nextButton")).onClick.Invoke(); Check((int)Get(panel, "page") == 1, "native page button advances list");
                Check(!rows[0].interactable, "continued grouped page starts with nonselectable biome heading");
                search.text = "Selected"; Check((int)Get(panel, "page") == 0, "search resets native paging");
                Check(((InputField)Get(panel, "portalNameInputField")).textComponent.supportRichText == false, "name editor is plain text");
                Check(((Dropdown)Get(panel, "sortDropdown")).options.Count == 3, "all three sort modes available");
                Call(panel, "Hide", false, null);
                Check(!main.activeSelf && inputRequests == 7, "native fixture closure releases only its counted input lease");
                return "AnyPortal+ native UI PASS: " + checks + " checks (isolated menu; real Unity controls; registry and worlds untouched).";
            }
            finally
            {
                GameObject main = Get(panel, "mainPanel") as GameObject;
                try
                {
                    Call(panel, "Hide", false, null); Call(panel, "Dispose");
                }
                finally
                {
                    harmony.UnpatchSelf();
                    if (main != null) UnityEngine.Object.DestroyImmediate(main);
                    if (baseline != null) UnityEngine.Object.DestroyImmediate(baseline);
                    if (foreignGroup != null) UnityEngine.Object.DestroyImmediate(foreignGroup);
                    if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(previousSelection);
                }
                diagnosticPanel = null;
                if (fixtureIcon != null) UnityEngine.Object.DestroyImmediate(fixtureIcon); fixtureIcon = null;
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }
        private static object Known(Type type, ZDOID id, string name, int biome, int icon, Vector3 location)
        {
            // No constructor runs against the live registry or world generator.
            object value = FormatterServices.GetUninitializedObject(type);
            type.GetProperty("Id").SetValue(value, id, null); type.GetProperty("Name").SetValue(value, name, null);
            type.GetProperty("Location").SetValue(value, location, null); type.GetProperty("Target").SetValue(value, ZDOID.None, null);
            type.GetProperty("Colour").SetValue(value, "#ffffff", null); type.GetProperty("Biome").SetValue(value, biome, null);
            type.GetProperty("Icon").SetValue(value, icon, null); return value;
        }
    }
}
