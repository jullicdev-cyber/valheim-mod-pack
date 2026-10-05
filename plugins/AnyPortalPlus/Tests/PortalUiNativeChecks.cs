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
using UnityEngine.UI;

namespace ValheimModPack.AnyPortalPlus
{
    public static class PortalUiNativeChecks
    {
        private static int checks;
        private static Sprite fixtureIcon;
        private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException("AnyPortal+ native UI: " + message); }
        private static object Get(object value, string field) { return value.GetType().GetField(field, Flags).GetValue(value); }
        private static void Set(object value, string field, object result) { value.GetType().GetField(field, Flags).SetValue(value, result); }
        private static void Call(object value, string method, params object[] arguments) { value.GetType().GetMethod(method, Flags).Invoke(value, arguments); }
        private static bool FixtureSprite(int icon, ref Sprite __result) { __result = icon < 0 ? null : fixtureIcon; return false; }
        public static string Run()
        {
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
            try
            {
                harmony.Patch(AccessTools.Method(markersType, "GetIconSprite"), prefix: new HarmonyMethod(typeof(PortalUiNativeChecks), "FixtureSprite"));
                Call(panel, "InitialiseUI");
                var main = (GameObject)Get(panel, "mainPanel"); Check(main != null && !main.activeSelf, "native wood panel constructs without opening gameplay input");
                RectTransform rect = main.GetComponent<RectTransform>(); Check(rect.rect.width >= 979 && rect.rect.height >= 859, "requested native panel dimensions");
                var icons = (Dropdown)Get(panel, "iconDropdown");
                Check(icons.options.Count == 6 && icons.options[0].image == null, "None plus five map-icon options");
                Check(icons.itemImage != null && icons.captionImage != null, "dropdown has actual Unity option and caption images");
                for (int i = 1; i < icons.options.Count; i++) Check(icons.options[i].image == fixtureIcon, "map-icon sprite reaches option " + i);
                object origin = Known(portalType, new ZDOID(1L, 1), "Current", 1, -1, Vector3.zero);
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
                search.text = ""; ((Toggle)Get(panel, "groupByBiomeToggle")).isOn = true;
                ((Button)Get(panel, "nextButton")).onClick.Invoke(); Check((int)Get(panel, "page") == 1, "native page button advances list");
                Check(!rows[0].interactable, "continued grouped page starts with nonselectable biome heading");
                search.text = "Selected"; Check((int)Get(panel, "page") == 0, "search resets native paging");
                Check(((InputField)Get(panel, "portalNameInputField")).textComponent.supportRichText == false, "name editor is plain text");
                Check(((Dropdown)Get(panel, "sortDropdown")).options.Count == 3, "all three sort modes available");
                return "AnyPortal+ native UI PASS: " + checks + " checks (isolated menu; real Unity controls; registry and worlds untouched).";
            }
            finally
            {
                Call(panel, "Dispose"); harmony.UnpatchSelf();
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
