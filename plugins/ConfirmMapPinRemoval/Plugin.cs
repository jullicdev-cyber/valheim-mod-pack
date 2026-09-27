using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;
namespace ValheimModPack.PinRemoval
{
    [BepInPlugin(Id, "Confirm Map Pin Removal", "1.3.0")]
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
        private PinHistoryController history;
        private QuickPinController quick;
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
                history = new PinHistoryController(harmony, pins, error => Logger.LogError(error));
                quick = new QuickPinController(harmony, history, pins, error => Logger.LogError(error));
                Logger.LogInfo("Map pin confirmation and history ready. Open the large map and choose Map pins, or press Ctrl+H.");
                Logger.LogInfo("Quick presets: Ctrl+P in gameplay, Shift+click on map, Alt+click a saved pin to rename.");
            }
            catch (Exception error) { failed = true; Logger.LogError(error); }
        }
        private static bool BeforeRemoveUnderPointer(Minimap __instance)
        {
            if (plugin == null) return false;
            try
            {
                if (!plugin.isActiveAndEnabled || plugin.failed || plugin.dialog == null
                    || plugin.dialog.IsOpen || (plugin.history != null && plugin.history.IsOpen)
                    || (plugin.quick != null && plugin.quick.IsBusy) || UnifiedPopup.IsVisible()) return false;
                var player = Player.m_localPlayer;
                if (!PinHistoryController.SafePlayer(player) || Menu.IsVisible() || __instance.m_mode != Minimap.MapMode.Large) return false;
                var pin = (Minimap.PinData)plugin.closest.Invoke(__instance, null);
                if (pin == null || !pin.m_save) return false;
                plugin.hideNameInput.Invoke(__instance, new object[] { false });
                plugin.dialog.Open(pin, plugin.quick == null ? pin.m_name : plugin.quick.DisplayName(pin),
                    candidate => __instance != null && ReferenceEquals(Minimap.instance, __instance)
                        && __instance.m_mode == Minimap.MapMode.Large && !UnifiedPopup.IsVisible()
                        && ReferenceEquals(Player.m_localPlayer, player) && PinHistoryController.SafePlayer(player) && !Menu.IsVisible() && candidate.m_save
                        && ((List<Minimap.PinData>)plugin.pins.GetValue(__instance)).Contains(candidate),
                    candidate => plugin.history.Remove(__instance, candidate));
            }
            catch (Exception error) { plugin.Logger.LogError("Pin removal blocked: " + error); }
            return false;
        }
        private void Update()
        {
            if (!failed && history != null)
            {
                try
                {
                    if (quick != null) quick.Tick(dialog != null && !dialog.IsOpen && !history.IsOpen);
                    history.Tick(dialog != null && !dialog.IsOpen && (quick == null || !quick.IsBusy));
                }
                catch (Exception error) { history.Close(); if (quick != null) quick.Close(); Logger.LogError(error); }
            }
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
            { plugin.dialog.Cancel(); if (plugin.history != null) plugin.history.Close(); if (plugin.quick != null) plugin.quick.Close(); }
        }
        private void OnDisable() { if (dialog != null) dialog.Cancel(); if (history != null) history.Close(); if (quick != null) quick.Close(); }
        private void OnDestroy()
        {
            try { if (dialog != null) dialog.Cancel(); if (view != null) view.Hide(); if (quick != null) quick.Dispose(); if (history != null) history.Dispose(); }
            finally { if (harmony != null) harmony.UnpatchSelf(); plugin = null; }
        }
    }
}
