using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimModPack.InterfaceInputFix
{
    // Compatibility for the exact packaged version; the vendor DLL stays intact.
    internal static class BindruneCompat
    {
        private static Harmony patches;
        private static FieldInfo root;
        private static MethodInfo open, close, holdsKeyboard;
        private static InputLease lease;
        private static Action<Exception> report;
        private static Settings settings;
        private static GameObject button;
        private static bool ownsNavigation;
        private static readonly FieldInfo navigationBlocked = AccessTools.Field(typeof(Settings), "m_navigationBlocked");
        private static readonly FieldInfo settingsPanel = AccessTools.Field(typeof(Settings), "m_settingsPanel");
        internal static bool Ready { get { return patches != null; } }
        internal static bool IsOpen { get { return root != null && (GameObject)root.GetValue(null) != null; } }

        internal static void Install(Action<Exception> log)
        {
            PluginInfo info;
            if (!Chainloader.PluginInfos.TryGetValue("isimp.Bindrune", out info)) return;
            if (info.Metadata.Version != new System.Version("0.5.0")) throw new NotSupportedException("Bindrune adapter requires 0.5.0");
            report = log; lease = new InputLease(GUIManager.BlockInput);
            var assembly = info.Instance.GetType().Assembly;
            var panel = assembly.GetType("Bindrune.UI.BindrunePanel", true);
            root = AccessTools.Field(panel, "_root");
            open = AccessTools.Method(panel, "Open", Type.EmptyTypes);
            close = AccessTools.Method(panel, "Close", new[] { typeof(bool) });
            holdsKeyboard = AccessTools.PropertyGetter(panel, "HoldsKeyboard");
            if (root == null || root.FieldType != typeof(GameObject) || open == null || close == null
                || holdsKeyboard == null || navigationBlocked == null || settingsPanel == null)
                throw new MissingMemberException("Bindrune panel API changed");
            var owner = new Harmony(Plugin.Id + ".bindrune");
            try
            {
                owner.Patch(open, prefix: new HarmonyMethod(typeof(BindruneCompat), "BeforeOpen"),
                    transpiler: new HarmonyMethod(typeof(BindruneCompat), "OwnInput"),
                    finalizer: new HarmonyMethod(typeof(BindruneCompat), "OpenFinished"));
                owner.Patch(close, transpiler: new HarmonyMethod(typeof(BindruneCompat), "OwnInput"),
                    finalizer: new HarmonyMethod(typeof(BindruneCompat), "CloseFinished"));
                owner.Patch(AccessTools.Method(typeof(GUIManager), "ResetInputBlock"), postfix: new HarmonyMethod(typeof(BindruneCompat), "AfterReset"));
                owner.Patch(AccessTools.Method(typeof(Settings), "Awake"), postfix: new HarmonyMethod(typeof(BindruneCompat), "SettingsReady"));
                owner.Patch(AccessTools.Method(typeof(Settings), "Update"), prefix: new HarmonyMethod(typeof(BindruneCompat), "SettingsInput"));
                owner.Patch(AccessTools.Method(assembly.GetType("Bindrune.Discovery.BindRegistry", true), "Refresh"),
                    postfix: new HarmonyMethod(typeof(BindruneCompat), "ProtectBindings"));
                patches = owner;
            }
            catch { owner.UnpatchSelf(); throw; }
        }
        private static IEnumerable<CodeInstruction> OwnInput(IEnumerable<CodeInstruction> instructions)
        {
            var source = AccessTools.Method(typeof(GUIManager), "BlockInput", new[] { typeof(bool) });
            var replacement = AccessTools.Method(typeof(BindruneCompat), "Block");
            int found = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(source)) { instruction.opcode = OpCodes.Call; instruction.operand = replacement; found++; }
                yield return instruction;
            }
            if (found != 1) throw new MissingMethodException("Expected exactly one Bindrune input request per lifecycle method");
        }
        private static void Block(bool value) { if (value) lease.Show(); else lease.Close(); }
        private static bool BeforeOpen()
        {
            if (IsOpen) return false;
            // Jotunn ignores input leases at the main menu, so also inspect native UI.
            if (TextInput.IsVisible() || UnifiedPopup.IsVisible() || global::Console.IsVisible()
                || (Chat.instance != null && Chat.instance.HasFocus())) return false;
            var current = Settings.instance;
            if (current != null && (bool)navigationBlocked.GetValue(current)) return false;
            return true;
        }
        private static Exception OpenFinished(Exception __exception)
        {
            if (__exception != null)
            {
                try { close.Invoke(null, new object[] { true }); } catch { lease.Close(); }
                report(__exception); return __exception;
            }
            if (IsOpen && Settings.instance != null && !ownsNavigation)
            { settings = Settings.instance; settings.BlockNavigation(true); ownsNavigation = true; }
            return null;
        }
        private static Exception CloseFinished(Exception __exception)
        {
            lease.Close(); RestoreNavigation(); return __exception;
        }
        private static void AfterReset() { if (lease != null) lease.Forget(); }
        private static bool SettingsInput() { return !(bool)holdsKeyboard.Invoke(null, null); }
        private static void RestoreNavigation()
        {
            if (!ownsNavigation) return;
            ownsNavigation = false;
            if (settings != null) settings.BlockNavigation(false);
            settings = null;
        }
        private static void SettingsReady(Settings __instance)
        {
            try
            {
                if (__instance == null) return;
                var parent = (GameObject)settingsPanel.GetValue(__instance);
                if (parent == null) return;
                button = GUIManager.Instance.CreateButton(
                    Localization.instance != null && Localization.instance.GetSelectedLanguage() == "Russian" ? "Клавиши модов" : "Mod keys",
                    parent.transform, new Vector2(.5f, 0), new Vector2(.5f, 0), new Vector2(0, 36), 176, 36);
                button.name = "ValheimModPack.BindruneSettings";
                button.GetComponent<Button>().onClick.AddListener(() => { if (!IsOpen) open.Invoke(null, null); });
            }
            catch (Exception e) { report(e); }
        }
        // The pack disabled these commands because their inventory behavior has not
        // been made safe for EAQS. Rebinding must not silently enable them again.
        private static void ProtectBindings()
        {
            var assembly = open.DeclaringType.Assembly;
            var registry = assembly.GetType("Bindrune.Discovery.BindRegistry", true);
            var list = (IEnumerable)registry.GetProperty("All").GetValue(null, null);
            foreach (object entry in list)
            {
                var type = entry.GetType();
                string owner = (string)type.GetField("OwnerGuid").GetValue(entry);
                string label = (string)type.GetField("Label").GetValue(entry);
                if (owner != "goldenrevolver.quick_stack_store") continue;
                if (label == "SortKeybind" || label == "QuickStackKeybind" || label == "RestockKeybind"
                    || label == "StoreAllKeybind" || label == "TakeAllKeybind" || label == "QuickTrashKeybind" || label == "TrashKeybind")
                {
                    type.GetField("Editable").SetValue(entry, false);
                    type.GetField("ReadOnlyReason").SetValue(entry, label == "SortKeybind"
                        ? "Use EAQS Quick Stack Bridge / Controls / SortShortcut (защищённая сортировка)."
                        : "Disabled in this pack for EAQS compatibility. Use AzuAutoStore for quick unload.");
                }
            }
        }
        internal static void Tick()
        {
            if (!Ready) return;
            var panel = (GameObject)root.GetValue(null);
            if ((lease.Held && (panel == null || !panel.activeInHierarchy))
                || (ownsNavigation && (settings == null || !settings.gameObject.activeInHierarchy))) close.Invoke(null, new object[] { true });
        }
        internal static void Dispose()
        {
            try { if (Ready) close.Invoke(null, new object[] { true }); }
            finally
            {
                if (lease != null) lease.Close(); RestoreNavigation();
                if (button != null) UnityEngine.Object.Destroy(button);
                if (patches != null) patches.UnpatchSelf(); patches = null;
            }
        }
    }
}
