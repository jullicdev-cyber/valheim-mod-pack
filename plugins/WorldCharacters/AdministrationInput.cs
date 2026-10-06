using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimModPack.WorldCharacters
{
    // Administration consumes the opening stroke before native Update or
    // FixedUpdate can turn it into a guardian power or another game action.
    public sealed partial class Plugin
    {
        private ConfigEntry<KeyboardShortcut> adminInputShortcut;
        private readonly ShortcutCapture adminInputCapture = new ShortcutCapture();
        private Player adminInputPlayer;
        private ZNet adminInputNetwork;
        private bool adminInputPending;
        private int adminInputOpenAfterFrame;
        private float adminInputDeadline;
        private static readonly Func<KeyCode, bool> adminReadHeld = AdminKeyHeld, adminReadDown = AdminKeyDown;

        private static bool AdminKeyHeld(KeyCode key) { return ZInput.GetKey(key, false); }
        private static bool AdminKeyDown(KeyCode key) { return ZInput.GetKeyDown(key, false); }

        private void InitializeAdminInput(ConfigEntry<KeyboardShortcut> entry)
        {
            adminInputShortcut = entry;
            adminInputShortcut.SettingChanged += AdminShortcutChanged;
            adminInputCapture.Prime(entry.Value, Time.frameCount, adminReadHeld);
        }

        private void ClearAdminInputCapture()
        {
            adminInputPending = false;
            adminInputCapture.Reset();
            if (adminInputShortcut != null) adminInputCapture.Prime(adminInputShortcut.Value, Time.frameCount, adminReadHeld);
            adminInputPlayer = null; adminInputNetwork = null;
        }

        private void AdminShortcutChanged(object sender, EventArgs args)
        { ClearAdminInputCapture(); }

        private void ResetAdminInput()
        { ClearAdminInputCapture(); }

        private void PollAdminShortcut()
        {
            if (adminInputShortcut == null || administrationWindow == null || !isActiveAndEnabled) return;
            if (!ReferenceEquals(adminInputPlayer, null) && (!ReferenceEquals(adminInputPlayer, Player.m_localPlayer)
                || !ReferenceEquals(adminInputNetwork, ZNet.instance)))
            {
                administrationWindow.Hide(); ClearAdminInputCapture();
            }
            bool consumed = adminInputCapture.Blocked(Time.frameCount, adminReadHeld);
            // Observe transitions even while text, menus or another modal has
            // focus. Closing a window while holding the opening key is not a new press.
            bool pressed = adminInputCapture.Pressed(adminInputShortcut.Value, Time.frameCount, adminReadDown, adminReadHeld);
            if (adminInputPending || consumed || !pressed || ZInput.s_IsRebindActive
                || (!administrationWindow.IsVisible && !CanOpenAdminWindow())) return;
            adminInputCapture.Claim(adminInputShortcut.Value.MainKey);
            GameplayInputCache.ConsumeAll();
            adminInputPlayer = Player.m_localPlayer; adminInputNetwork = ZNet.instance;
            adminInputPending = true; adminInputOpenAfterFrame = Time.frameCount + 1;
            adminInputDeadline = Time.unscaledTime + 1;
        }

        // Called by Plugin.Update after PollAdminShortcut. Construction is
        // deferred out of native input prefixes and never needs a live inventory.
        private void ProcessAdminPendingOpen()
        {
            if (!adminInputPending) return;
            if (!isActiveAndEnabled || !ReferenceEquals(adminInputPlayer, Player.m_localPlayer)
                || !ReferenceEquals(adminInputNetwork, ZNet.instance) || Time.unscaledTime >= adminInputDeadline
                || Input.GetKeyDown(KeyCode.Escape)) { adminInputPending = false; return; }
            if (Time.frameCount < adminInputOpenAfterFrame) return;
            adminInputPending = false;
            if (administrationWindow.IsVisible) administrationWindow.Hide();
            else if (CanOpenAdminWindow()) administrationWindow.Show();
        }

        private bool AdminBlocksGameplay()
        {
            if (adminInputShortcut == null || administrationWindow == null || !isActiveAndEnabled) return false;
            PollAdminShortcut();
            return administrationWindow.IsVisible || adminInputPending
                || adminInputCapture.Blocked(Time.frameCount, adminReadHeld);
        }

        private void DisposeAdminInput()
        {
            if (adminInputShortcut != null) adminInputShortcut.SettingChanged -= AdminShortcutChanged;
            adminInputShortcut = null; adminInputPending = false;
            adminInputCapture.Reset(); adminInputPlayer = null; adminInputNetwork = null;
        }

        private static bool BeforeAdminGameButton(string __0, ref bool __result)
        {
            if (__0 == "JoyButtonB" || Instance == null || !Instance.AdminBlocksGameplay()) return true;
            GameplayInputCache.Consume(__0); __result = false; return false;
        }

        [HarmonyPatch(typeof(ZInput), "GetButton", new[] { typeof(string) })]
        private static class AdminHeldInputPatch
        { [HarmonyPriority(Priority.First)] private static bool Prefix(string __0, ref bool __result) { return BeforeAdminGameButton(__0, ref __result); } }
        [HarmonyPatch(typeof(ZInput), "GetButtonDown", new[] { typeof(string) })]
        private static class AdminDownInputPatch
        { [HarmonyPriority(Priority.First)] private static bool Prefix(string __0, ref bool __result) { return BeforeAdminGameButton(__0, ref __result); } }
        [HarmonyPatch(typeof(ZInput), "GetButtonUp", new[] { typeof(string) })]
        private static class AdminUpInputPatch
        { [HarmonyPriority(Priority.First)] private static bool Prefix(string __0, ref bool __result) { return BeforeAdminGameButton(__0, ref __result); } }
        [HarmonyPatch(typeof(ZInput), "Update", new[] { typeof(float) })]
        private static class AdminDynamicInputPatch
        { [HarmonyPriority(Priority.First)] private static void Postfix() { if (Instance != null && Instance.AdminBlocksGameplay()) GameplayInputCache.ConsumeAll(); } }
        [HarmonyPatch(typeof(ZInput), "FixedUpdate", new[] { typeof(float) })]
        private static class AdminFixedInputPatch
        { [HarmonyPriority(Priority.First)] private static void Postfix() { if (Instance != null && Instance.AdminBlocksGameplay()) GameplayInputCache.ConsumeAll(); } }
        [HarmonyPatch(typeof(PlayerController), "TakeInput", new[] { typeof(bool) })]
        private static class AdminControllerInputPatch
        {
            [HarmonyPriority(Priority.First)] private static bool Prefix(Player ___m_character, ref bool __result)
            {
                if (Instance == null || !ReferenceEquals(___m_character, Player.m_localPlayer) || !Instance.AdminBlocksGameplay()) return true;
                __result = false; return false;
            }
        }
        [HarmonyPatch(typeof(Player), "StartGuardianPower", new Type[] { })]
        private static class AdminGuardianPowerInputPatch
        {
            [HarmonyPriority(Priority.First)] private static bool Prefix(Player __instance, ref bool __result)
            {
                if (Instance == null || !ReferenceEquals(__instance, Player.m_localPlayer) || !Instance.AdminBlocksGameplay()) return true;
                __result = false; return false;
            }
        }
    }
}
