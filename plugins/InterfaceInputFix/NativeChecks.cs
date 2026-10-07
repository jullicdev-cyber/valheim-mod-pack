using System;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;

namespace ValheimModPack.InterfaceInputFix
{
    // Compiled only into the supervised probe, never the shipped plugin.
    public static class NativeChecks
    {
        private static int requests, checks;
        private static bool CountInput(bool __0) { requests += __0 ? 1 : -1; return false; }
        private static bool IgnoreFocus() { return false; }
        public static string Run()
        {
            checks = 0;
            var plugin = (Plugin)Chainloader.PluginInfos[Plugin.Id].Instance;
            Assert(plugin.PortalPatched, "version-checked portal integration active");
            bool hasBackpacks = Chainloader.PluginInfos.ContainsKey("vapok.mods.adventurebackpacks");
            Assert(plugin.BackpackPatched == hasBackpacks, "backpack integration matches the copied optional plugin");
            Type portalType = Chainloader.PluginInfos["yay.spikehimself.xportal"].Instance.GetType().Assembly
                .GetType("XPortal.UI.PortalConfigurationPanel", true);
            var show = AccessTools.Method(portalType, "SetActive");
            var hide = AccessTools.Method(portalType, "Hide");
            var dispose = AccessTools.Method(portalType, "Dispose");
            var focus = AccessTools.Method(portalType, "ActivateInputField");
            var block = AccessTools.Method(typeof(GUIManager), "BlockInput");
            Assert(HasPatch(show) && HasPatch(hide) && HasPatch(dispose), "real XPortal methods patched");
            Assert(HasPatch(AccessTools.Method(typeof(InventoryGui), "Hide")) == hasBackpacks,
                "optional backpack close hook matches plugin availability");
            if (hasBackpacks)
            {
                Type backpackType = Chainloader.PluginInfos["vapok.mods.adventurebackpacks"].Instance.GetType().Assembly
                    .GetType("AdventureBackpacks.Patches.InventoryGuiPatches", true);
                Assert(AccessTools.Method(backpackType, "HideBackpack", new[] { typeof(InventoryGui) }) != null
                    && AccessTools.Field(backpackType, "BackpackIsOpen").FieldType == typeof(bool), "actual backpack API shape");
            }
            var test = new Harmony("valheimmodpack.interfaceinputfix.nativeprobe");
            object instance = Activator.CreateInstance(portalType, true);
            var panel = new GameObject("InterfaceInputFix.IsolatedNativeProbe"); panel.SetActive(false);
            AccessTools.Field(portalType, "mainPanel").SetValue(instance, panel);
            try
            {
                test.Patch(block, prefix: new HarmonyMethod(typeof(NativeChecks), "CountInput"));
                test.Patch(focus, prefix: new HarmonyMethod(typeof(NativeChecks), "IgnoreFocus"));
                requests = 2;
                show.Invoke(instance, new object[] { true });
                show.Invoke(instance, new object[] { true });
                Assert(panel.activeSelf && requests == 3, "two real SetActive calls own only one input request");
                hide.Invoke(instance, new object[] { false, null });
                Assert(!panel.activeSelf && requests == 2, "one real Hide preserves two foreign requests");
                hide.Invoke(instance, new object[] { false, null });
                Assert(requests == 2, "duplicate real Hide retains foreign baseline");
                show.Invoke(instance, new object[] { true });
                hide.Invoke(instance, new object[] { true, null });
                show.Invoke(instance, new object[] { true });
                var lease = (InputLease)AccessTools.Field(typeof(Plugin), "portalLease").GetValue(plugin);
                Assert(!lease.CloseDue(Time.frameCount + 3) && panel.activeSelf, "reopen cancels real delayed Hide");
                dispose.Invoke(instance, null);
                Assert(requests == 2, "real Dispose frees its request before panel destruction");
            }
            finally
            {
                try { hide.Invoke(instance, new object[] { false, null }); dispose.Invoke(instance, null); }
                finally { test.UnpatchSelf(); if (panel != null) UnityEngine.Object.DestroyImmediate(panel); }
            }
            return "InterfaceInputFix native PASS: " + checks + " assertions; real patched XPortal lifecycle with isolated panel/counter (no player inventory mutated)."
                + (hasBackpacks ? " Backpack API and Harmony hook verified." : "\nSKIP: Adventure Backpacks API checks; optional plugin is absent.")
                + "\n" + ValheimPlusEditingNativeChecks.Run();
        }
        private static bool HasPatch(MethodInfo method)
        {
            var info = Harmony.GetPatchInfo(method);
            return info != null && info.Owners.Contains(Plugin.Id);
        }
        private static void Assert(bool valid, string label) { checks++; if (!valid) throw new InvalidOperationException(label); }
    }
}
