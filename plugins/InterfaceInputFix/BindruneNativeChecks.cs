// Test-only: compiled into the isolated probe, never shipped in a game plugin.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using Bindrune;
using Bindrune.Discovery;
using Bindrune.UI;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimModPack.InterfaceInputFix
{
    public static class BindruneNativeChecks
    {
        private static int checks, requests;
        private static bool CountInput(bool __0) { requests += __0 ? 1 : -1; return false; }
        private static readonly BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        public static string Run()
        {
            checks = 0;
            string isolated = Environment.GetEnvironmentVariable("VMP_QOL_SMOKE_ROOT");
            Check(!String.IsNullOrEmpty(isolated) && Path.GetFullPath(Paths.BepInExRootPath).StartsWith(Path.GetFullPath(isolated), StringComparison.OrdinalIgnoreCase), "isolated BepInEx root required");
            Check(Player.m_localPlayer == null, "no player inventories touched");
            Check(Chainloader.PluginInfos["isimp.Bindrune"].Metadata.Version == new System.Version("0.5.0"), "exact vendor version loaded");
            Type compat = typeof(Plugin).Assembly.GetType("ValheimModPack.InterfaceInputFix.BindruneCompat", true);
            Check((bool)compat.GetProperty("Ready", All).GetValue(null, null), "compatibility initialized");
            BindRegistry.Refresh();
            Check(BindRegistry.All.Count > 70, "game and mod bindings discovered");
            string[] ids = {
                "cfg:randyknapp.mods.equipmentandquickslots:Hotkeys:Quick slot hotkey 1",
                "cfg:valheimmodpack.chestsearch:General:OpenShortcut",
                "cfg:valheimmodpack.expeditionloadouts:Controls:OpenShortcut",
                "cfg:valheimmodpack.expeditionloadouts:Controls:LargeAmountModifier",
                "cfg:valheimmodpack.confirmpinremoval:Controls:OpenHistory",
                "cfg:valheimmodpack.confirmpinremoval:Controls:QuickPin",
                "cfg:valheimmodpack.confirmpinremoval:Controls:PlaceOnMapModifier",
                "cfg:valheimmodpack.confirmpinremoval:Controls:RenamePinModifier",
                "cfg:valheimmodpack.confirmpinremoval:Controls:AcceptSuggestion",
                "cfg:valheimmodpack.confirmpinremoval:Controls:NextSuggestion",
                "cfg:valheimmodpack.confirmpinremoval:Controls:DismissSuggestion",
                "cfg:valheimmodpack.confirmpinremoval:Controls:ClearDeathPins",
                "cfg:valheimmodpack.portalfinder:Controls:FindNearestToPlayer",
                "cfg:valheimmodpack.portalfinder:Controls:SelectMapPoint",
                "cfg:valheimmodpack.inventoryadmin:Controls:OpenInventoryAdmin",
                "cfg:valheimmodpack.nordicradio:Controls:OpenPersonalAudio",
                "cfg:valheimmodpack.eaqsquickstackbridge:Controls:SortShortcut" };
            bool restart = Environment.GetEnvironmentVariable("VMP_BIND_RESTART") == "1";
            foreach (string id in ids)
            {
                var entry = BindRegistry.All.SingleOrDefault(b => b.Id == id);
                Check(entry != null && entry.Editable && entry.Handle is ConfigEntryBase, "editable " + id);
                var value = (ConfigEntryBase)entry.Handle;
                if (!restart)
                    Check(BindWriter.Apply(entry, new KeyCombo(KeyCode.U, new[] { KeyCode.LeftControl })) == null, "write personal combo " + id);
                var shortcut = (KeyboardShortcut)value.BoxedValue;
                Check(shortcut.MainKey == KeyCode.U && shortcut.Modifiers.Contains(KeyCode.LeftControl), "live or restarted combo " + id);
            }
            Check(File.Exists(Path.Combine(Paths.BepInExRootPath, "bindrune.keys")), "personal override persisted outside configs");
            var quick = Chainloader.PluginInfos["goldenrevolver.quick_stack_store"].Instance.Config;
            var sort = quick[new ConfigDefinition("4 - Sorting", "SortKeybind")];
            Check(((KeyboardShortcut)sort.BoxedValue).MainKey == KeyCode.U, "bridge updates real sorter");
            quick.Save();
            string saved = File.ReadAllText(quick.ConfigFilePath);
            Check(saved.Contains("SortKeybind = None") && saved.Contains("DisplaySortButtons = OnlyContainerButton"), "saving bindings keeps disk inventory safety guard");
            Check(((KeyboardShortcut)sort.BoxedValue).MainKey == KeyCode.U, "serialization does not change live sorting");
            var disabled = BindRegistry.All.Single(b => b.Id == "cfg:goldenrevolver.quick_stack_store:4 - Sorting:SortKeybind");
            Check(!disabled.Editable && BindWriter.Apply(disabled, new KeyCombo(KeyCode.K, null)) != null, "unsafe alias cannot override bridge");
            var unsafeStore = BindRegistry.All.Single(b => b.Id == "cfg:goldenrevolver.quick_stack_store:3 - Store and Take All:StoreAllKeybind");
            Check(!unsafeStore.Editable, "disabled inventory operations stay disabled");
            Check(BindRegistry.All.Any(b => b.OwnerGuid == "org.bepinex.plugins.valheim_plus" && b.Editable), "Valheim Plus keys discovered");
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                Check(BindRegistry.All.Any(b => b.OwnerGuid == "advize.PlantEasily" && b.Editable), "PlantEasily keys discovered");
            bool hasBackpacks = Chainloader.PluginInfos.ContainsKey("vapok.mods.adventurebackpacks");
            Check(BindRegistry.All.Any(b => b.OwnerGuid == "vapok.mods.adventurebackpacks" && b.Editable) == hasBackpacks,
                "backpack key discovery matches the copied optional plugin");
            MapKeyChecks();

            var test = new Harmony("valheimmodpack.bindrune.nativeprobe");
            test.Patch(AccessTools.Method(typeof(GUIManager), "BlockInput"), prefix: new HarmonyMethod(typeof(BindruneNativeChecks), "CountInput"));
            try
            {
                // Prove the vendor lifecycle defect without borrowing any real input requests.
                compat.GetMethod("Dispose", All).Invoke(null, null);
                requests = 3; BindrunePanel.Close(true);
                Check(requests == 2, "vendor baseline reproduced: closed panel consumes a foreign request");
                compat.GetMethod("Install", All).Invoke(null, new object[] { new Action<Exception>(e => { throw new Exception("Adapter error", e); }) });
                requests = 3;
                BindrunePanel.Close(true); BindrunePanel.Close(true);
                Check(requests == 3, "duplicate close preserves foreign input requests");
                BindrunePanel.Open(); BindrunePanel.Open();
                Check(BindrunePanel.IsOpen && requests == 4, "repeated open owns exactly one input request");
                BindrunePanel.Close(true); BindrunePanel.Close(true);
                Check(!BindrunePanel.IsOpen && requests == 3, "close frees exactly its own request");
                BindrunePanel.Open();
                GameObject panel = (GameObject)typeof(BindrunePanel).GetField("_root", All).GetValue(null);
                panel.SetActive(false); compat.GetMethod("Tick", All).Invoke(null, null);
                Check(!BindrunePanel.IsOpen && requests == 3, "external hide releases own request");
                SettingsChecks(compat);
            }
            finally { BindrunePanel.Close(true); test.UnpatchSelf(); }
            return "Bindrune PASS " + checks + ": real discovery/rebind/personal persistence, guarded sorting, actual panel lifecycle; restart=" + restart + "; registry=" + BindRegistry.All.Count + "."
                + (hasBackpacks ? "" : "\nSKIP: Adventure Backpacks binding checks; optional plugin is absent.");
        }
        private static void MapKeyChecks()
        {
            var map = Chainloader.PluginInfos["valheimmodpack.confirmpinremoval"].Instance;
            var method = map.GetType().Assembly.GetType("ValheimModPack.PinRemoval.MapControls").GetMethod("Matches", All);
            var down = new System.Collections.Generic.HashSet<KeyCode>();
            var held = new System.Collections.Generic.HashSet<KeyCode>();
            Func<KeyboardShortcut, bool> match = key => (bool)method.Invoke(null, new object[] { key, new Func<KeyCode, bool>(down.Contains), new Func<KeyCode, bool>(held.Contains) });
            down.Add(KeyCode.U); held.Add(KeyCode.U); held.Add(KeyCode.RightControl);
            Check(match(new KeyboardShortcut(KeyCode.U, KeyCode.LeftControl)), "remapped main key with right Control");
            Check(!match(new KeyboardShortcut(KeyCode.P, KeyCode.LeftControl)), "old key no longer triggers");
            Check(!match(KeyboardShortcut.Empty), "unbound action cannot trigger");
            held.Add(KeyCode.LeftAlt);
            Check(!match(new KeyboardShortcut(KeyCode.U, KeyCode.LeftControl)), "extra modifier rejected");
            Check(match(new KeyboardShortcut(KeyCode.U, KeyCode.LeftControl, KeyCode.LeftAlt)), "multi-modifier combination works");
            down.Clear(); held.Clear(); down.Add(KeyCode.RightShift); held.Add(KeyCode.RightShift);
            Check(match(new KeyboardShortcut(KeyCode.LeftShift)), "map modifier preserves right Shift");
            Check(!match(new KeyboardShortcut(KeyCode.LeftAlt)), "different map modifier does not trigger");
        }
        private static void SettingsChecks(Type compat)
        {
            var startup = UnityEngine.Object.FindObjectOfType<FejdStartup>();
            Check(startup != null, "native main menu available for settings fixture");
            var prefab = (GameObject)AccessTools.Field(typeof(FejdStartup), "m_settingsPrefab").GetValue(startup);
            var parent = new GameObject("BindruneInactiveSettingsFixture"); parent.SetActive(false);
            var instanceField = AccessTools.Field(typeof(Settings), "m_instance");
            object previous = instanceField.GetValue(null);
            try
            {
                var clone = UnityEngine.Object.Instantiate(prefab, parent.transform);
                var settings = clone.GetComponent<Settings>();
                Check(settings != null, "real settings prefab cloned without Awake or PlayerPrefs writes");
                compat.GetMethod("SettingsReady", All).Invoke(null, new object[] { settings });
                var added = clone.GetComponentsInChildren<Button>(true).Single(b => b.name == "ValheimModPack.BindruneSettings");
                Check(added != null && added.interactable, "settings entry created with native Jotunn button");
                var panel = (GameObject)AccessTools.Field(typeof(Settings), "m_settingsPanel").GetValue(settings);
                var ownBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(panel.transform, added.transform);
                foreach (var native in clone.GetComponentsInChildren<Button>(true))
                {
                    if (native == added || native.transform.IsChildOf(added.transform)) continue;
                    var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(panel.transform, native.transform);
                    bool overlap = ownBounds.min.x < bounds.max.x && ownBounds.max.x > bounds.min.x && ownBounds.min.y < bounds.max.y && ownBounds.max.y > bounds.min.y;
                    Check(!overlap, "settings button does not cover native button " + native.name + " " + bounds);
                }
                instanceField.SetValue(null, settings);
                added.onClick.Invoke();
                Check(BindrunePanel.IsOpen && (bool)AccessTools.Field(typeof(Settings), "m_navigationBlocked").GetValue(settings), "settings button opens panel and protects native navigation");
                Check(!(bool)compat.GetMethod("SettingsInput", All).Invoke(null, null), "settings keyboard ignored under modal");
                BindrunePanel.Close(true);
                Check(!(bool)AccessTools.Field(typeof(Settings), "m_navigationBlocked").GetValue(settings), "closing restores settings navigation");
            }
            finally
            {
                BindrunePanel.Close(true);
                UnityEngine.Object.DestroyImmediate(parent);
                instanceField.SetValue(null, previous);
            }
        }
        private static void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
    }
}
