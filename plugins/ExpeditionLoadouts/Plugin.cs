using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using Jotunn.Utils;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ValheimModPack.ExpeditionLoadouts
{
    [BepInPlugin(Id, "Expedition Loadouts", "1.1.1")]
    [BepInDependency("com.jotunn.jotunn", "2.30.2")]
    [BepInDependency("randyknapp.mods.equipmentandquickslots", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("goldenrevolver.quick_stack_store", BepInDependency.DependencyFlags.SoftDependency)]
    [NetworkCompatibility(CompatibilityLevel.NotEnforced, VersionStrictness.None)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "valheimmodpack.expeditionloadouts";
        public ChestService Service { get; private set; }
        public PresetStore Store { get; private set; }
        private ConfigEntry<KeyboardShortcut> shortcut;
        private ConfigEntry<float> radius;
        private LoadoutWindow window;
        private Player owner;
        private long ownerId;
        private ZNet network;
        private int pendingFrame = -1;
        private float pendingUntil;
        private string directory;
        public float Radius { get { return Single.IsNaN(radius.Value) ? 10 : Mathf.Clamp(radius.Value, 1, 30); } }

        private void Awake()
        {
            shortcut = Config.Bind("Controls", "OpenShortcut", new KeyboardShortcut(KeyCode.L, KeyCode.LeftControl),
                "Open expedition loadouts while the inventory is open.");
            radius = Config.Bind("Storage", "Radius", 10f, new ConfigDescription("Radius of loaded accessible chests, in metres.", new AcceptableValueRange<float>(1, 30)));
            directory = Path.Combine(Directory.GetParent(BepInEx.Paths.BepInExRootPath).FullName, "ValheimModpack/ExpeditionLoadouts");
            try
            {
                Service = new ChestService(Logger);
                window = new LoadoutWindow(this);
                Logger.LogInfo("Expedition Loadouts ready. Open inventory, then Left Ctrl + L.");
            }
            catch (Exception error) { Logger.LogError("Loadout initialization failed: " + error); enabled = false; }
        }

        private void Update()
        {
            try
            {
                Player player = Player.m_localPlayer;
                long playerId = player == null ? 0 : player.GetPlayerID();
                if (!ReferenceEquals(player, owner) || !ReferenceEquals(ZNet.instance, network) || playerId != ownerId)
                {
                    window.Hide(); Service.Cancel(); pendingFrame = -1; Store = null;
                    owner = player; ownerId = playerId; network = ZNet.instance;
                    if (owner != null && network != null && ownerId != 0)
                    {
                        Store = new PresetStore(directory, ownerId);
                        if (Store.ReadOnly) Logger.LogWarning("Preset file preserved after load error: " + Store.LoadError);
                    }
                }
                window.Tick();
                Service.Tick();
                if (!ValidPlayer(player)) { pendingFrame = -1; if (window.IsVisible) window.Hide(); return; }
                if (pendingFrame >= 0 && Time.frameCount > pendingFrame)
                {
                    // Hide animates over several InventoryGui updates. Do not
                    // consume the request on the first still-visible frame.
                    if (Time.unscaledTime > pendingUntil || Input.GetKeyDown(KeyCode.Escape) || !CanOpen()) pendingFrame = -1;
                    else if (!InventoryGui.IsVisible() && Store != null)
                    { pendingFrame = -1; window.Show(); }
                }
                if (pendingFrame < 0 && InventoryGui.IsVisible() && !window.IsVisible && CanOpen() && shortcut.Value.IsDown())
                {
                    InventoryGui.instance.Hide(); pendingFrame = Time.frameCount; pendingUntil = Time.unscaledTime + 1.5f;
                }
            }
            catch (Exception error)
            {
                Logger.LogError("Loadout update failed; operation cancelled: " + error);
                pendingFrame = -1; if (window != null) window.Hide(); if (Service != null) Service.Cancel();
            }
        }

        internal static bool ValidPlayer(Player player)
        {
            return player != null && ReferenceEquals(player, Player.m_localPlayer) && player.GetPlayerID() != 0 && ZNet.instance != null
                && !player.IsDead() && !player.IsTeleporting() && !player.InCutscene() && !player.IsSleeping();
        }
        private static bool CanOpen()
        {
            // Jotunn reports its input lock through TextInput.IsVisible. Check this
            // before taking our own lock, never from the already-open window.
            if (UnifiedPopup.IsVisible() || Menu.IsVisible() || TextInput.IsVisible() || global::Console.IsVisible()
                || (Chat.instance != null && Chat.instance.HasFocus())
                || (Minimap.instance != null && Minimap.instance.m_mode == Minimap.MapMode.Large)) return false;
            if (EventSystem.current == null || EventSystem.current.currentSelectedGameObject == null) return true;
            var selected = EventSystem.current.currentSelectedGameObject;
            if (!selected.activeInHierarchy) return true;
            var field = selected.GetComponentInParent<InputField>();
            if (field != null && field.isActiveAndEnabled && field.gameObject.activeInHierarchy && field.isFocused) return false;
            foreach (var component in selected.GetComponentsInParent<Component>())
            {
                var behaviour = component as Behaviour;
                if (behaviour == null || !behaviour.isActiveAndEnabled || !component.gameObject.activeInHierarchy) continue;
                Type inputType = component.GetType();
                while (inputType != null && inputType.Name != "TMP_InputField") inputType = inputType.BaseType;
                if (inputType == null) continue;
                var focused = component.GetType().GetProperty("isFocused");
                if (focused != null && (bool)focused.GetValue(component, null)) return false;
            }
            return true;
        }
        internal void Error(Exception error) { Logger.LogError(error); }
        private void OnDisable()
        { pendingFrame = -1; if (window != null) window.Hide(); if (Service != null) Service.Cancel(); }
        private void OnDestroy()
        { OnDisable(); if (Service != null) Service.Dispose(); }
    }
}
