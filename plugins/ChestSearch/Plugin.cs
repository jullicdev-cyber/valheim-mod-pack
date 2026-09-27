using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using Jotunn.Managers;
using Jotunn.Utils;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ValheimModPack.ChestSearch
{
    [BepInPlugin(Id, "Chest Search", Version)]
    [BepInDependency("com.jotunn.jotunn", "2.30.2")]
    [NetworkCompatibility(CompatibilityLevel.NotEnforced, VersionStrictness.None)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "valheimmodpack.chestsearch";
        public const string Version = "1.1.0";
        public NativeChestReader Reader { get; private set; }
        public SearchService Search { get; private set; }
        public string LastQuery = "";
        public bool ShowWithoutInput;
        public ItemSort SortOrder = ItemSort.Name;
        public bool SortDescending;
        private ConfigEntry<KeyboardShortcut> shortcut;
        private ConfigEntry<float> radius, markerSeconds;
        private NameIndex names;
        private SearchWindow window;
        private readonly List<ChestMarker> markers = new List<ChestMarker>();
        private Player pendingPlayer;
        private ZNet pendingNetwork;
        private bool waitingForInventory;
        private float openDeadline;
        private int openAfterFrame;
        private bool ready;
        private float nextError;
        public float Radius { get { return SafeSetting(radius.Value, 5, 80, 30); } }
        public float MarkerSeconds { get { return SafeSetting(markerSeconds.Value, 5, 60, 20); } }
        private static float SafeSetting(float value, float min, float max, float fallback)
        {
            return Single.IsNaN(value) || Single.IsInfinity(value) ? fallback : Mathf.Clamp(value, min, max);
        }
        public bool Russian { get { return Localization.instance != null && Localization.instance.GetSelectedLanguage() == "Russian"; } }
        public string T(string ru, string en) { return Russian ? ru : en; }

        private void Awake()
        {
            try
            {
                shortcut = Config.Bind("General", "OpenShortcut", new KeyboardShortcut(KeyCode.F, KeyCode.LeftControl), "Open chest search while the inventory is visible. Default Ctrl+F also accepts RightControl.");
                radius = Config.Bind("General", "SearchRadius", 30f, new ConfigDescription("Loaded ordinary player-built chests within this distance (metres).", new AcceptableValueRange<float>(5, 80)));
                markerSeconds = Config.Bind("General", "MarkerSeconds", 20f, new ConfigDescription("Lifetime of the local chest marker (seconds).", new AcceptableValueRange<float>(5, 60)));
                Reader = new NativeChestReader();
                names = new NameIndex(message => Logger.LogWarning(message));
                Search = new SearchService(Reader, names);
                window = new SearchWindow(this);
                Localization.OnLanguageChange += names.Clear;
                ready = true;
                Logger.LogInfo("ChestSearch " + Version + " ready. Ctrl+F in inventory; item icons, sorting and explicit multi-chest highlighting.");
            }
            catch (Exception error) { Logger.LogError("ChestSearch initialization failed: " + error); Shutdown(); }
        }
        private void Update()
        {
            if (!ready) return;
            try
            {
                window.Tick(); foreach (var marker in markers) marker.Tick();
                if (window.IsVisible || pendingPlayer != null || !InventoryGui.IsVisible() || !CanOpen()) return;
                bool pressed = shortcut.Value.IsDown();
                if (shortcut.Value.Equals(new KeyboardShortcut(KeyCode.F, KeyCode.LeftControl))
                    && Input.GetKeyDown(KeyCode.F) && Input.GetKey(KeyCode.RightControl)
                    && !Input.GetKey(KeyCode.LeftAlt) && !Input.GetKey(KeyCode.RightAlt)) pressed = true;
                if (!pressed) return;
                pendingPlayer = Player.m_localPlayer; pendingNetwork = ZNet.instance; waitingForInventory = false;
                openDeadline = Time.unscaledTime + 1;
            }
            catch (Exception error) { if (window != null) window.Hide(); Report(error); }
        }
        private void LateUpdate()
        {
            if (ReferenceEquals(pendingPlayer, null)) return;
            Player requested = pendingPlayer; ZNet requestedNetwork = pendingNetwork;
            try
            {
                if (!ready || !ReferenceEquals(requested, Player.m_localPlayer) || !ReferenceEquals(requestedNetwork, ZNet.instance)
                    || Time.unscaledTime >= openDeadline || Input.GetKeyDown(KeyCode.Escape) || !CanOpen())
                { ClearPending(); return; }
                if (!waitingForInventory)
                {
                    if (!InventoryGui.IsVisible()) { ClearPending(); return; }
                    // IsVisible stays true for two native updates after Hide().
                    // Do not focus our text box while inventory hotkeys still run.
                    InventoryGui.instance.Hide(); waitingForInventory = true;
                    openAfterFrame = Time.frameCount + 2;
                    return;
                }
                if (Time.frameCount < openAfterFrame || InventoryGui.IsVisible()) return;
                ClearPending();
                window.Show();
            }
            catch (Exception error) { ClearPending(); if (window != null) window.Hide(); Report(error); }
        }
        private void ClearPending() { pendingPlayer = null; pendingNetwork = null; waitingForInventory = false; }
        private bool CanOpen()
        {
            var player = Player.m_localPlayer;
            if (player == null || ZNet.instance == null || player.IsDead() || player.IsTeleporting() || player.InCutscene()
                || player.IsSleeping() || UnifiedPopup.IsVisible() || Menu.IsVisible()
                || TextInput.IsVisible() || global::Console.IsVisible() || (Chat.instance != null && Chat.instance.HasFocus())
                || (Minimap.instance != null && Minimap.instance.m_mode == Minimap.MapMode.Large)) return false;
            if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null)
            {
                GameObject selected = EventSystem.current.currentSelectedGameObject;
                var field = selected.GetComponentInParent<InputField>();
                if (field != null && field.isActiveAndEnabled && field.gameObject.activeInHierarchy && field.isFocused) return false;
                // Native Valheim text fields use TMP; avoid a direct optional TMP dependency.
                foreach (var component in selected.GetComponentsInParent<Component>())
                {
                    if (component == null || !component.gameObject.activeInHierarchy) continue;
                    Type inputType = component.GetType();
                    while (inputType != null && inputType.Name != "TMP_InputField") inputType = inputType.BaseType;
                    if (inputType == null) continue;
                    var behaviour = component as Behaviour; if (behaviour != null && !behaviour.isActiveAndEnabled) continue;
                    var focused = component.GetType().GetProperty("isFocused");
                    if (focused != null && (bool)focused.GetValue(component, null)) return false;
                }
            }
            return true;
        }
        public bool Locate(SearchResult result)
        {
            ClearMarkers();
            var marker = new ChestMarker(this);
            bool found = marker.Show(result);
            if (!found) return false;
            markers.Add(marker);
            if (window != null) window.Hide();
            return true;
        }
        public int LocateAll(ItemSearchResult item)
        {
            if (item == null || String.IsNullOrEmpty(item.Key)) return 0;
            ClearMarkers();
            // The backend caps searches at 256 chests. Only an explicit click
            // creates markers; typing, sorting and selecting rows never do.
            foreach (var chest in item.Chests)
            {
                var marker = new ChestMarker(this);
                if (marker.Show(chest, item.Key)) markers.Add(marker);
                else marker.Clear();
            }
            int count = markers.Count;
            if (count > 0 && window != null) window.Hide();
            return count;
        }
        public void ClearMarkers()
        {
            foreach (var marker in markers) marker.Clear();
            markers.Clear();
        }
        internal void Report(Exception error)
        {
            if (Time.unscaledTime < nextError) return;
            nextError = Time.unscaledTime + 5;
            Logger.LogError(error);
        }
        private void OnDisable()
        {
            ClearPending();
            if (window != null) window.Hide();
            ClearMarkers();
        }
        private void Shutdown()
        {
            ready = false; OnDisable();
            if (names != null) Localization.OnLanguageChange -= names.Clear;
        }
        private void OnDestroy() { Shutdown(); }
    }
}
