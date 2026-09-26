using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;
namespace ValheimModPack.PinRemoval
{
    [BepInPlugin(Id, "Confirm Map Pin Removal", "1.1.0")]
    [BepInDependency("com.jotunn.jotunn", "2.30.2")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "valheimmodpack.confirmpinremoval";
        private Harmony harmony;
        private static Plugin plugin;
        private MethodInfo closest;
        private MethodInfo hideNameInput;
        private FieldInfo pins;
        private WoodDialogView view;
        private RemovalDialog<Minimap.PinData> dialog;
        private bool failed;
        private bool warned;
        private void Awake()
        {
            plugin = this;
            try
            {
                view = new WoodDialogView();
                dialog = new RemovalDialog<Minimap.PinData>(view, error => Logger.LogError(error));
                var target = AccessTools.Method(typeof(Minimap), "RemovePinUnderPointer", Type.EmptyTypes);
                if (target == null) throw new MissingMethodException("Valheim pointer-removal method changed");
                harmony = new Harmony(Id);
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(Plugin), "BeforeRemoveUnderPointer"));
                closest = AccessTools.Method(typeof(Minimap), "GetClosestPinToCursor", Type.EmptyTypes);
                hideNameInput = AccessTools.Method(typeof(Minimap), "HidePinTextInput", new[] { typeof(bool) });
                pins = AccessTools.Field(typeof(Minimap), "m_pins");
                if (closest == null || closest.ReturnType != typeof(Minimap.PinData)
                    || hideNameInput == null || pins == null || pins.FieldType != typeof(List<Minimap.PinData>))
                    throw new MissingMemberException("Map selection API changed; pointer deletion blocked");
                var shutdown = AccessTools.Method(typeof(Player), "OnDestroy", Type.EmptyTypes);
                if (shutdown == null) throw new MissingMethodException("Player cleanup API changed");
                harmony.Patch(shutdown, prefix: new HarmonyMethod(typeof(Plugin), "BeforePlayerDestroyed"));
                Logger.LogInfo("Map pin confirmation ready: Jotunn wood panel, Cancel/Delete, Escape cancels.");
            }
            catch (Exception error) { failed = true; Logger.LogError(error); }
        }
        private static bool BeforeRemoveUnderPointer(Minimap __instance)
        {
            if (plugin == null) return false;
            try
            {
                if (!plugin.isActiveAndEnabled || plugin.failed || plugin.dialog == null
                    || plugin.dialog.IsOpen || UnifiedPopup.IsVisible()) return false;
                var player = Player.m_localPlayer;
                if (player == null || player.IsDead() || __instance.m_mode != Minimap.MapMode.Large) return false;
                var pin = (Minimap.PinData)plugin.closest.Invoke(__instance, null);
                if (pin == null || !pin.m_save) return false;
                plugin.hideNameInput.Invoke(__instance, new object[] { false });
                plugin.dialog.Open(pin, pin.m_name,
                    candidate => __instance != null && ReferenceEquals(Minimap.instance, __instance)
                        && __instance.m_mode == Minimap.MapMode.Large && !UnifiedPopup.IsVisible()
                        && player != null && ReferenceEquals(Player.m_localPlayer, player) && !player.IsDead() && candidate.m_save
                        && ((List<Minimap.PinData>)plugin.pins.GetValue(__instance)).Contains(candidate),
                    candidate => __instance.RemovePin(candidate));
            }
            catch (Exception error) { plugin.Logger.LogError("Pin removal blocked: " + error); }
            return false;
        }
        private void Update()
        {
            if (failed && !warned && Player.m_localPlayer != null)
            {
                warned = true;
                Player.m_localPlayer.Message(MessageHud.MessageType.Center,
                    "Confirm Map Pin Removal: ошибка запуска. Проверьте BepInEx/LogOutput.log.", 0, null);
            }
            if (dialog == null || !dialog.IsOpen) return;
            try
            {
                dialog.ValidateContext();
                if (!dialog.IsOpen) return;
                if (!view.IsVisible || UnifiedPopup.IsVisible() || Input.GetKeyDown(KeyCode.Escape)
                    || ZInput.GetButtonDown("JoyButtonB")) dialog.Cancel();
            }
            catch (Exception error) { dialog.Cancel(); Logger.LogError(error); }
        }
        private static void BeforePlayerDestroyed(Player __instance)
        {
            if (plugin != null && ReferenceEquals(__instance, Player.m_localPlayer) && plugin.dialog != null)
                plugin.dialog.Cancel();
        }
        private void OnDisable() { if (dialog != null) dialog.Cancel(); }
        private void OnDestroy()
        {
            try { if (dialog != null) dialog.Cancel(); if (view != null) view.Hide(); }
            finally { if (harmony != null) harmony.UnpatchSelf(); plugin = null; }
        }
    }
}
