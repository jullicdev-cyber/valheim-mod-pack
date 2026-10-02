using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;
namespace ValheimModPack.PinRemoval
{
    [BepInPlugin(Id, "Confirm Map Pin Removal", "1.5.1")]
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
        private PinActionController actions;
        private DeathPinController deaths;
        private PinSuggestionController suggestions;
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
                var controls = new MapControls(Config);
                history.OpenShortcut = () => MapControls.Down(controls.History.Value);
                history.ShortcutLabel = () => MapControls.Label(controls.History.Value);
                quick.OpenShortcut = () => MapControls.Down(controls.Quick.Value);
                quick.ShortcutLabel = () => MapControls.Label(controls.Quick.Value);
                quick.PlaceModifier = () => MapControls.Held(controls.Place.Value);
                quick.RenameModifier = () => MapControls.Held(controls.Rename.Value);
                actions = new PinActionController(pins, quick.DisplayName, quick.Rename, RequestDelete, error => Logger.LogError(error));
                deaths = new DeathPinController(pins, history, error => Logger.LogError(error));
                deaths.OpenShortcut = () => MapControls.Down(controls.ClearDeathPins.Value);
                deaths.ShortcutLabel = () => MapControls.Label(controls.ClearDeathPins.Value);
                suggestions = new PinSuggestionController(quick, history, controls, error => Logger.LogError(error));
                Logger.LogInfo("Map actions ready: right-click a saved pin to rename it or request confirmed deletion.");
                Logger.LogInfo("Quick presets and nearby suggestions are configurable in Bindrune. Death-pin cleanup asks for confirmation.");
            }
            catch (Exception error) { failed = true; Logger.LogError(error); }
        }
        private static bool BeforeRemoveUnderPointer(Minimap __instance)
        {
            if (plugin == null) return false;
            try
            {
                if (!plugin.isActiveAndEnabled || plugin.failed || plugin.dialog == null || plugin.actions == null
                    || plugin.dialog.IsOpen || (plugin.history != null && plugin.history.IsOpen)
                    || (plugin.quick != null && plugin.quick.IsBusy) || plugin.actions.IsOpen
                    || (plugin.deaths != null && plugin.deaths.IsOpen) || UnifiedPopup.IsVisible()) return false;
                var player = Player.m_localPlayer;
                var network = ZNet.instance;
                if (!RemovalContext(__instance, player) || TextInput.IsVisible() || network == null) return false;
                long world = network.GetWorldUID(), character = player.GetPlayerID();
                if (world == 0 || character == 0) return false;
                var pin = (Minimap.PinData)plugin.closest.Invoke(__instance, null);
                if (!plugin.CurrentPin(__instance, pin)) return false;
                plugin.hideNameInput.Invoke(__instance, new object[] { false });
                plugin.actions.Show(__instance, pin);
            }
            catch (Exception error) { plugin.Logger.LogError("Pin removal blocked: " + error); }
            return false;
        }
        internal bool CurrentPin(Minimap map, Minimap.PinData pin)
        {
            return pins != null && map != null && ReferenceEquals(map, Minimap.instance) && pin != null && pin.m_save
                && ((List<Minimap.PinData>)pins.GetValue(map)).Contains(pin);
        }
        internal void RequestDelete(Minimap.PinData pin)
        {
            if (failed || !isActiveAndEnabled || !ReferenceEquals(plugin, this) || dialog == null || dialog.IsOpen
                || history == null || history.IsOpen || (quick != null && quick.IsBusy)
                || (deaths != null && deaths.IsOpen) || (actions != null && actions.IsOpen)) return;
            var map = Minimap.instance; var player = Player.m_localPlayer; var network = ZNet.instance;
            if (!RemovalContext(map, player) || TextInput.IsVisible() || network == null || !CurrentPin(map, pin)) return;
            long world = network.GetWorldUID(), character = player.GetPlayerID();
            if (world == 0 || character == 0) return;
            var requestedPlugin = this;
            dialog.Open(pin, quick == null ? pin.m_name : quick.DisplayName(pin),
                candidate => ReferenceEquals(plugin, requestedPlugin) && isActiveAndEnabled && !failed
                    && RemovalContext(map, player) && ReferenceEquals(ZNet.instance, network)
                    && network.GetWorldUID() == world && player.GetPlayerID() == character && CurrentPin(map, candidate),
                candidate => history.Remove(map, candidate));
        }
        private static bool RemovalContext(Minimap map, Player player)
        {
            // TextInput.IsVisible includes our own Jotunn input lease after Show;
            // validate the native input panel here and check the shared lock only before opening.
            return map != null && ReferenceEquals(Minimap.instance, map) && map.m_mode == Minimap.MapMode.Large
                && ReferenceEquals(Player.m_localPlayer, player) && PinHistoryController.SafePlayer(player)
                && !UnifiedPopup.IsVisible() && !Menu.IsVisible() && !global::Console.IsVisible() && !InventoryGui.IsVisible()
                && (Chat.instance == null || !Chat.instance.HasFocus())
                && (TextInput.instance == null || TextInput.instance.m_panel == null || !TextInput.instance.m_panel.activeInHierarchy);
        }
        private void Update()
        {
            if (!failed && history != null)
            {
                try
                {
                    bool noConfirmation = dialog != null && !dialog.IsOpen;
                    if (actions != null) actions.Tick(noConfirmation && !history.IsOpen && (quick == null || !quick.IsBusy)
                        && (deaths == null || !deaths.IsOpen));
                    if (deaths != null) deaths.Tick(noConfirmation && !history.IsOpen && (quick == null || !quick.IsBusy)
                        && (actions == null || !actions.IsOpen));
                    if (quick != null) quick.Tick(noConfirmation && !history.IsOpen && (actions == null || !actions.IsOpen)
                        && (deaths == null || !deaths.IsOpen));
                    history.Tick(noConfirmation && (quick == null || !quick.IsBusy) && (actions == null || !actions.IsOpen)
                        && (deaths == null || !deaths.IsOpen));
                    if (suggestions != null) suggestions.Tick(noConfirmation && !history.IsOpen && (quick == null || !quick.IsBusy)
                        && (actions == null || !actions.IsOpen) && (deaths == null || !deaths.IsOpen));
                }
                catch (Exception error) { CloseModals(); Logger.LogError(error); }
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
            if (plugin != null && ReferenceEquals(__instance, Player.m_localPlayer)) plugin.CloseModals();
        }
        private void Cleanup(Action action)
        { try { action(); } catch (Exception error) { Logger.LogError(error); } }
        internal void CloseModals()
        {
            if (dialog != null) Cleanup(dialog.Cancel);
            if (actions != null) Cleanup(actions.Close);
            if (deaths != null) Cleanup(deaths.Close);
            if (suggestions != null) Cleanup(suggestions.Close);
            if (history != null) Cleanup(history.Close);
            if (quick != null) Cleanup(quick.Close);
        }
        private void OnDisable() { CloseModals(); }
        private void OnDestroy()
        {
            try
            {
                CloseModals();
                if (view != null) Cleanup(view.Hide);
                if (suggestions != null) Cleanup(suggestions.Dispose);
                if (actions != null) Cleanup(actions.Dispose);
                if (deaths != null) Cleanup(deaths.Dispose);
                if (quick != null) Cleanup(quick.Dispose);
                if (history != null) Cleanup(history.Dispose);
            }
            finally { if (harmony != null) harmony.UnpatchSelf(); if (ReferenceEquals(plugin, this)) plugin = null; }
        }
    }
}
