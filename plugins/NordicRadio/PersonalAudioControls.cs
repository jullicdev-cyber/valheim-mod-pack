using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ValheimModPack.NordicRadio
{
    // Capture before native input queries, and open the modal on the following
    // rendered frame. A remapped shortcut cannot leak its main key to gameplay.
    internal sealed class PersonalAudioControls : IDisposable
    {
        private static PersonalAudioControls active;
        private readonly Plugin plugin;
        private readonly ConfigEntry<KeyboardShortcut> shortcut;
        private readonly PersonalAudioWindow window;
        private readonly Harmony patches;
        private readonly PersonalShortcutCapture capture = new PersonalShortcutCapture();
        private Player observedPlayer, pendingPlayer;
        private ZNet observedNetwork, pendingNetwork;
        private int openAfterFrame;
        private float deadline;
        private bool disposed;
        private static readonly Func<KeyCode, bool> readHeld = KeyHeld, readDown = KeyDown;
        internal PersonalAudioControls(Plugin plugin, ConfigEntry<KeyboardShortcut> shortcut, PersonalAudioWindow window)
        {
            if (active != null) throw new InvalidOperationException("Personal radio input gate already exists");
            this.plugin = plugin; this.shortcut = shortcut; this.window = window;
            patches = new Harmony(Plugin.Id + ".personal-audio-input");
            try
            {
                capture.Prime(shortcut.Value, Time.frameCount, readHeld); shortcut.SettingChanged += Changed; active = this;
                foreach (string method in new[] { "GetButton", "GetButtonDown", "GetButtonUp" })
                    patches.Patch(AccessTools.Method(typeof(ZInput), method, new[] { typeof(string) }),
                        prefix: new HarmonyMethod(typeof(PersonalAudioControls), "BeforeGameButton") { priority = Priority.First });
                patches.Patch(AccessTools.Method(typeof(PlayerController), "TakeInput", new[] { typeof(bool) }),
                    prefix: new HarmonyMethod(typeof(PersonalAudioControls), "BeforeControllerInput") { priority = Priority.First });
                foreach (string method in new[] { "TakeInput", "StartGuardianPower" })
                    patches.Patch(AccessTools.Method(typeof(Player), method, Type.EmptyTypes),
                        prefix: new HarmonyMethod(typeof(PersonalAudioControls), "BeforePlayerInput") { priority = Priority.First });
                foreach (string method in new[] { "Update", "FixedUpdate" })
                    patches.Patch(AccessTools.Method(typeof(ZInput), method, new[] { typeof(float) }),
                        postfix: new HarmonyMethod(typeof(PersonalAudioControls), "AfterNativeInput") { priority = Priority.First });
                patches.Patch(AccessTools.Method(typeof(GUIManager), "ResetInputBlock"),
                    postfix: new HarmonyMethod(typeof(PersonalAudioControls), "AfterGlobalInputReset"));
            }
            catch { Dispose(); throw; }
        }
        internal static string Label(KeyboardShortcut value)
        {
            if (value.MainKey == KeyCode.None) return "";
            var parts = new List<string>();
            foreach (KeyCode key in value.Modifiers.Select(Family).Distinct().OrderBy(Order).ThenBy(k => (int)k)) parts.Add(KeyLabel(key));
            string main = KeyLabel(value.MainKey); if (!parts.Contains(main)) parts.Add(main);
            return String.Join("+", parts.ToArray());
        }
        private static KeyCode Family(KeyCode key)
        {
            if (key == KeyCode.RightControl) return KeyCode.LeftControl;
            if (key == KeyCode.RightShift) return KeyCode.LeftShift;
            if (key == KeyCode.RightAlt || key == KeyCode.AltGr) return KeyCode.LeftAlt;
            if (key == KeyCode.RightCommand || key == KeyCode.LeftWindows || key == KeyCode.RightWindows) return KeyCode.LeftCommand;
            return key;
        }
        private static int Order(KeyCode key)
        { return key == KeyCode.LeftControl ? 0 : key == KeyCode.LeftShift ? 1 : key == KeyCode.LeftAlt ? 2 : key == KeyCode.LeftCommand ? 3 : 4; }
        private static string KeyLabel(KeyCode key)
        {
            key = Family(key);
            if (key == KeyCode.LeftControl) return "Ctrl"; if (key == KeyCode.LeftShift) return "Shift";
            if (key == KeyCode.LeftAlt) return "Alt"; if (key == KeyCode.LeftCommand) return "Cmd";
            return key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9 ? ((int)key - (int)KeyCode.Alpha0).ToString() : key.ToString();
        }
        private static bool KeyHeld(KeyCode key) { return ZInput.GetKey(key, false); }
        private static bool KeyDown(KeyCode key) { return ZInput.GetKeyDown(key, false); }
        internal void Tick()
        {
            if (disposed) return;
            try
            {
                Poll();
                if (pendingPlayer == null) return;
                if (!ReferenceEquals(pendingPlayer, Player.m_localPlayer) || !ReferenceEquals(pendingNetwork, ZNet.instance)
                    || Time.unscaledTime >= deadline || Input.GetKeyDown(KeyCode.Escape) || !CanOpen()) { ClearPending(); return; }
                if (Time.frameCount < openAfterFrame) return;
                ClearPending(); window.Show();
            }
            catch (Exception error) { ClearPending(); window.Hide(); plugin.Report(error); }
        }
        private void Poll()
        {
            if (disposed || !plugin.isActiveAndEnabled) return;
            if (!ReferenceEquals(observedPlayer, Player.m_localPlayer) || !ReferenceEquals(observedNetwork, ZNet.instance))
            {
                ClearPending(); window.Hide(); capture.Reset(); capture.Prime(shortcut.Value, Time.frameCount, readHeld);
                observedPlayer = Player.m_localPlayer; observedNetwork = ZNet.instance; return;
            }
            bool consumed = capture.Blocked(Time.frameCount, readHeld);
            bool pressed = capture.Pressed(shortcut.Value, Time.frameCount, readDown, readHeld);
            if (consumed || !pressed || pendingPlayer != null) return;
            if (window.IsVisible)
            {
                capture.Claim(shortcut.Value.MainKey); PersonalGameplayInputCache.ConsumeAll(); window.Hide(); return;
            }
            if (!CanOpen()) return;
            capture.Claim(shortcut.Value.MainKey); PersonalGameplayInputCache.ConsumeAll();
            pendingPlayer = Player.m_localPlayer; pendingNetwork = ZNet.instance;
            openAfterFrame = Time.frameCount + 1; deadline = Time.unscaledTime + 1;
        }
        private bool CanOpen()
        {
            var player = Player.m_localPlayer;
            if (player == null || ZNet.instance == null || player.IsDead() || player.IsTeleporting() || player.IsSleeping() || player.InCutscene()
                || GUIManager.CustomGUIFront == null || ZInput.s_IsRebindActive || TextInput.IsVisible() || Menu.IsVisible() || InventoryGui.IsVisible()
                || StoreGui.IsVisible() || Hud.IsPieceSelectionVisible() || PlayerCustomizaton.IsBarberGuiVisible() || UnifiedPopup.IsVisible()
                || global::Console.IsVisible() || (Chat.instance != null && Chat.instance.HasFocus())
                || (Minimap.instance != null && Minimap.instance.m_mode == Minimap.MapMode.Large)) return false;
            if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null)
            {
                var selected = EventSystem.current.currentSelectedGameObject;
                var field = selected.GetComponentInParent<InputField>(); if (field != null && field.isActiveAndEnabled && field.isFocused) return false;
                foreach (var component in selected.GetComponentsInParent<Component>())
                {
                    if (component == null || !component.gameObject.activeInHierarchy) continue;
                    Type type = component.GetType(); while (type != null && type.Name != "TMP_InputField") type = type.BaseType;
                    if (type == null) continue;
                    var behaviour = component as Behaviour; if (behaviour != null && !behaviour.isActiveAndEnabled) continue;
                    var focused = component.GetType().GetProperty("isFocused"); if (focused != null && (bool)focused.GetValue(component, null)) return false;
                }
            }
            return true;
        }
        private bool BlocksGameplay()
        {
            try { Poll(); return !disposed && plugin.isActiveAndEnabled && (window.IsVisible || pendingPlayer != null || capture.Blocked(Time.frameCount, readHeld)); }
            catch (Exception error) { ClearPending(); capture.Reset(); plugin.Report(error); return false; }
        }
        private static bool BeforeGameButton(string __0, ref bool __result)
        {
            if (__0 == "JoyButtonB" || active == null || !active.BlocksGameplay()) return true;
            PersonalGameplayInputCache.Consume(__0); __result = false; return false;
        }
        private static void AfterNativeInput() { if (active != null && active.BlocksGameplay()) PersonalGameplayInputCache.ConsumeAll(); }
        private static void AfterGlobalInputReset()
        {
            if (active == null) return;
            active.ClearPending(); active.window.HandleInputReset();
        }
        private static bool BeforeControllerInput(Player ___m_character, ref bool __result)
        {
            if (active == null || ___m_character == null || !ReferenceEquals(___m_character, Player.m_localPlayer) || !active.BlocksGameplay()) return true;
            __result = false; return false;
        }
        private static bool BeforePlayerInput(Player __instance, ref bool __result)
        {
            if (active == null || !ReferenceEquals(__instance, Player.m_localPlayer) || !active.BlocksGameplay()) return true;
            __result = false; return false;
        }
        private void ClearPending() { pendingPlayer = null; pendingNetwork = null; }
        private void Changed(object sender, EventArgs args)
        { ClearPending(); capture.Reset(); capture.Prime(shortcut.Value, Time.frameCount, readHeld); }
        internal void Reset()
        { ClearPending(); capture.Reset(); observedPlayer = null; observedNetwork = null; window.Hide(); }
        public void Dispose()
        {
            if (disposed) return; disposed = true; shortcut.SettingChanged -= Changed;
            if (ReferenceEquals(active, this)) active = null;
            Reset(); patches.UnpatchSelf();
        }
    }
}
