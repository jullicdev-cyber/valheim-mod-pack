using System;
using System.Text;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimModPack.ChestSearch
{
    // Optional entry point for the pack's supervised native probe. It only reads
    // APIs, localizations, an isolated UI tree and already loaded containers.
    // UI checks do not create a player, open a world or acquire an input lock.
    public static class NativeChecks
    {
        public static string Run()
        {
            var reader = new NativeChestReader();
            var warnings = new StringBuilder();
            var names = new NameIndex(message => warnings.Append(message).Append("; "));
            names.EnsureLoaded();
            int matched = 0;
            if (ObjectDB.instance != null)
            {
                var iron = ObjectDB.instance.GetItemPrefab("Iron");
                ItemDrop item = iron == null ? null : iron.GetComponent<ItemDrop>();
                if (item != null)
                {
                    if (!names.Matches(SearchText.Terms("Iron"), item.m_itemData)) throw new InvalidOperationException("English Iron query failed");
                    if (!names.Matches(SearchText.Terms("железо"), item.m_itemData)) throw new InvalidOperationException("Russian Iron query failed");
                    matched = 2;
                }
            }
            int readable = 0;
            if (Player.m_localPlayer != null)
            {
                foreach (Container chest in UnityEngine.Object.FindObjectsByType<Container>(FindObjectsSortMode.None))
                {
                    ChestSnapshot snapshot; ReadFailure failure;
                    if (reader.TryRead(chest, Player.m_localPlayer, 30, out snapshot, out failure)) readable++;
                }
            }
            return "ChestSearch API PASS; aliases=" + names.AliasCount + "; native item queries=" + matched
                + "; accessible fresh loaded chests=" + readable + "; native UI checks=" + CheckUI()
                + "; input hook checks=" + CheckInputHooks() + "; warnings=" + warnings;
        }
        private static bool ranOriginal;
        private static void ObserveOriginal(bool __runOriginal) { ranOriginal = __runOriginal; }
        private static int CheckInputHooks()
        {
            var plugin = (Plugin)Chainloader.PluginInfos[Plugin.Id].Instance;
            var originalWindow = Get(plugin, "window");
            var window = new SearchWindow(plugin);
            var observer = new Harmony(Plugin.Id + ".native-input-probe"); int checks = 0;
            try
            {
                foreach (string name in new[] { "GetButton", "GetButtonDown", "GetButtonUp" })
                {
                    var target = AccessTools.Method(typeof(ZInput), name, new[] { typeof(string) });
                    Assert(Harmony.GetPatchInfo(target).Owners.Contains(Plugin.Id + ".input"), name + " hooked", ref checks);
                    observer.Patch(target, prefix: new HarmonyMethod(typeof(NativeChecks), "ObserveOriginal") { priority = Priority.Last });
                }
                Assert(Harmony.GetPatchInfo(AccessTools.Method(typeof(PlayerController), "TakeInput", new[] { typeof(bool) }))
                    .Owners.Contains(Plugin.Id + ".input"), "controller input hooked before FixedUpdate", ref checks);
                foreach (string name in new[] { "TakeInput", "StartGuardianPower" })
                    Assert(Harmony.GetPatchInfo(AccessTools.Method(typeof(Player), name, Type.EmptyTypes))
                        .Owners.Contains(Plugin.Id + ".input"), "local Player." + name + " guarded", ref checks);
                foreach (string name in new[] { "Update", "FixedUpdate" })
                    Assert(Harmony.GetPatchInfo(AccessTools.Method(typeof(ZInput), name, new[] { typeof(float) }))
                        .Owners.Contains(Plugin.Id + ".input"), "native ZInput." + name + " sampled", ref checks);
                Call(window, "BuildVisuals"); Set(plugin, "window", window);
                ranOriginal = true; Assert(!ZInput.GetButton("Jump") && !ranOriginal, "held gameplay input consumed", ref checks);
                ranOriginal = true; Assert(!ZInput.GetButtonDown("Jump") && !ranOriginal, "pressed gameplay input consumed", ref checks);
                ranOriginal = true; Assert(!ZInput.GetButtonUp("Jump") && !ranOriginal, "released gameplay input consumed", ref checks);
                ranOriginal = false; ZInput.GetButtonDown("JoyButtonB"); Assert(ranOriginal, "controller cancel preserved", ref checks);
                window.Hide(); ranOriginal = false; ZInput.GetButtonDown("Jump"); Assert(ranOriginal, "normal native input restored after close", ref checks);
                return checks;
            }
            finally { window.Hide(); Set(plugin, "window", originalWindow); observer.UnpatchSelf(); }
        }
        private static int CheckUI()
        {
            var plugin = (Plugin)Chainloader.PluginInfos[Plugin.Id].Instance;
            var window = new SearchWindow(plugin);
            string oldQuery = plugin.LastQuery; bool oldEmpty = plugin.ShowWithoutInput, oldDesc = plugin.SortDescending;
            ItemSort oldSort = plugin.SortOrder; int checks = 0;
            try
            {
                plugin.LastQuery = ""; plugin.ShowWithoutInput = true; plugin.SortOrder = ItemSort.Name; plugin.SortDescending = false;
                Call(window, "BuildVisuals");
                var report = new SearchReport();
                var ironPrefab = ObjectDB.instance.GetItemPrefab("Iron");
                var iron = ironPrefab.GetComponent<ItemDrop>();
                Sprite icon = iron.m_itemData.GetIcon();
                for (int i = 0; i < 9; i++) report.Items.Add(new ItemSearchResult
                { Key = "fixture" + i, Name = "Item " + i, Icon = icon, Quantity = 10 - i, ChestCount = i + 1, NearestDistance = 9 - i });
                Set(window, "report", report); Call(window, "Repaint");
                var toggle = (Toggle)Get(window, "showAll");
                Assert(toggle.isOn && toggle.graphic != null, "show-without-input checkbox", ref checks);
                var icons = (Image[])Get(window, "icons");
                foreach (var image in icons) Assert(image.enabled && image.sprite == icon && image.preserveAspect, "inventory sprite displayed", ref checks);
                var button = (Button)Get(window, "highlight");
                Assert(!button.interactable, "highlight disabled without explicit selection", ref checks);
                Call(window, "Select", 2);
                Assert(button.interactable && (string)Get(window, "selectedKey") == "fixture2", "row selection enables explicit action", ref checks);
                for (int mode = 0; mode < 4; mode++)
                {
                    plugin.SortOrder = (ItemSort)mode;
                    for (int desc = 0; desc < 2; desc++)
                    {
                        plugin.SortDescending = desc != 0; Call(window, "Repaint");
                        Assert((string)Get(window, "selectedKey") == "fixture2", "selection survives sorting", ref checks);
                    }
                }
                Set(window, "page", 1); Call(window, "Repaint");
                var rows = (Button[])Get(window, "rows");
                Assert(rows[2].interactable && !rows[3].interactable && !icons[3].enabled, "last page clears obsolete rows and icons", ref checks);
                report.Items.RemoveAll(item => item.Key == "fixture2"); Call(window, "Repaint");
                Assert(!button.interactable && Get(window, "selectedKey") == null, "removed item cannot be highlighted", ref checks);
                window.Hide(); window.Hide();
                Assert(!window.IsVisible, "idempotent close", ref checks);
                for (int i = 0; i < 3; i++)
                { Call(window, "BuildVisuals"); window.Hide(); Assert(!window.IsVisible, "repeated open/close", ref checks); }
                return checks;
            }
            finally
            {
                window.Hide(); plugin.LastQuery = oldQuery; plugin.ShowWithoutInput = oldEmpty;
                plugin.SortOrder = oldSort; plugin.SortDescending = oldDesc;
            }
        }
        private static object Get(object value, string field)
        { return value.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(value); }
        private static void Set(object value, string field, object content)
        { value.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(value, content); }
        private static void Call(object value, string method, params object[] args)
        { value.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(value, args); }
        private static void Assert(bool condition, string name, ref int checks)
        { if (!condition) throw new InvalidOperationException("ChestSearch UI: " + name); checks++; }
    }
}
