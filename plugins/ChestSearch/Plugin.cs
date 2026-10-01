using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
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
        public const string Version = "1.2.1";
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
        private static Plugin active;
        private Harmony inputPatches;
        private readonly ShortcutCapture capture = new ShortcutCapture();
        private Player capturePlayer;
        private ZNet captureNetwork;
        private static readonly Func<KeyCode, bool> readHeld = KeyHeld, readDown = KeyDown;
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
                shortcut = Config.Bind("General", "OpenShortcut", new KeyboardShortcut(KeyCode.F, KeyCode.LeftControl), "Open chest search in gameplay or inventory. Modifier sides are interchangeable; the opening shortcut consumes gameplay input. Rebind with Bindrune.");
                shortcut.SettingChanged += ShortcutChanged;
                capture.Prime(shortcut.Value, Time.frameCount, readHeld);
                radius = Config.Bind("General", "SearchRadius", 30f, new ConfigDescription("Loaded ordinary player-built chests within this distance (metres).", new AcceptableValueRange<float>(5, 80)));
                markerSeconds = Config.Bind("General", "MarkerSeconds", 20f, new ConfigDescription("Lifetime of the local chest marker (seconds).", new AcceptableValueRange<float>(5, 60)));
                Reader = new NativeChestReader();
                names = new NameIndex(message => Logger.LogWarning(message));
                Search = new SearchService(Reader, names);
                window = new SearchWindow(this);
                Localization.OnLanguageChange += names.Clear;
                active = this;
                inputPatches = new Harmony(Id + ".input");
                foreach (string method in new[] { "GetButton", "GetButtonDown", "GetButtonUp" })
                    inputPatches.Patch(AccessTools.Method(typeof(ZInput), method, new[] { typeof(string) }),
                        prefix: new HarmonyMethod(typeof(Plugin), "BeforeGameButton") { priority = Priority.First });
                inputPatches.Patch(AccessTools.Method(typeof(PlayerController), "TakeInput", new[] { typeof(bool) }),
                    prefix: new HarmonyMethod(typeof(Plugin), "BeforePlayerInput") { priority = Priority.First });
                foreach (string method in new[] { "TakeInput", "StartGuardianPower" })
                    inputPatches.Patch(AccessTools.Method(typeof(Player), method, Type.EmptyTypes),
                        prefix: new HarmonyMethod(typeof(Plugin), "BeforeLocalPlayerInput") { priority = Priority.First });
                foreach (string method in new[] { "Update", "FixedUpdate" })
                    inputPatches.Patch(AccessTools.Method(typeof(ZInput), method, new[] { typeof(float) }),
                        postfix: new HarmonyMethod(typeof(Plugin), "AfterNativeInput") { priority = Priority.First });
                ready = true;
                Logger.LogInfo("ChestSearch " + Version + " ready. Search shortcut works in gameplay and inventory; opening input is consumed.");
            }
            catch (Exception error) { Logger.LogError("ChestSearch initialization failed: " + error); Shutdown(); }
        }
        private void Update()
        {
            if (!ready) return;
            try
            {
                window.Tick(); foreach (var marker in markers) marker.Tick();
                PollShortcut();
            }
            catch (Exception error) { ClearPending(); if (window != null) window.Hide(); Report(error); }
        }
        private static bool KeyHeld(KeyCode key) { return ZInput.GetKey(key, false); }
        private static bool KeyDown(KeyCode key) { return ZInput.GetKeyDown(key, false); }
        private void PollShortcut()
        {
            if (!ready || !isActiveAndEnabled) return;
            if (!ReferenceEquals(capturePlayer, null) && (!ReferenceEquals(capturePlayer, Player.m_localPlayer)
                || !ReferenceEquals(captureNetwork, ZNet.instance)))
            { capture.Reset(); capture.Prime(shortcut.Value, Time.frameCount, readHeld); ClearPending(); window.Hide(); capturePlayer = null; captureNetwork = null; }
            bool consumed = capture.Blocked(Time.frameCount, readHeld);
            bool pressed = capture.Pressed(shortcut.Value, Time.frameCount, readDown, readHeld);
            if (window.IsVisible || pendingPlayer != null || consumed
                || !pressed || !CanOpen()) return;
            capture.Claim(shortcut.Value.MainKey);
            GameplayInputCache.ConsumeAll();
            capturePlayer = pendingPlayer = Player.m_localPlayer; captureNetwork = pendingNetwork = ZNet.instance;
            waitingForInventory = false; openAfterFrame = Time.frameCount + 1; openDeadline = Time.unscaledTime + 1;
        }
        private bool BlocksGameplay()
        {
            try
            {
                PollShortcut();
                return ready && isActiveAndEnabled && (window.IsVisible || pendingPlayer != null || capture.Blocked(Time.frameCount, readHeld));
            }
            catch (Exception error) { ClearPending(); capture.Reset(); Report(error); return false; }
        }
        private static bool BeforeGameButton(string __0, ref bool __result)
        {
            // The modal must still receive its controller Cancel button.
            if (__0 == "JoyButtonB" || active == null || !active.BlocksGameplay()) return true;
            GameplayInputCache.Consume(__0);
            __result = false; return false;
        }
        private static void AfterNativeInput()
        {
            if (active != null && active.BlocksGameplay()) GameplayInputCache.ConsumeAll();
        }
        private static bool BeforeLocalPlayerInput(Player __instance, ref bool __result)
        {
            // Player.Update has its own input gate and GP branch, independent
            // of PlayerController.FixedUpdate. Also guard direct local GP calls.
            if (active == null || !ReferenceEquals(__instance, Player.m_localPlayer) || !active.BlocksGameplay()) return true;
            __result = false; return false;
        }
        private static bool BeforePlayerInput(Player ___m_character, ref bool __result)
        {
            // Capture before FixedUpdate reads any controls, even if this plugin's
            // Update has not run. Returning false here lets vanilla zero SetControls.
            if (active == null || ___m_character == null || !ReferenceEquals(___m_character, Player.m_localPlayer)
                || !active.BlocksGameplay()) return true;
            __result = false; return false;
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
                    // IsVisible stays true for two native updates after Hide().
                    // Do not focus our text box while inventory hotkeys still run.
                    if (InventoryGui.IsVisible())
                    { InventoryGui.instance.Hide(); openAfterFrame = Time.frameCount + 2; }
                    waitingForInventory = true;
                }
                if (Time.frameCount < openAfterFrame || InventoryGui.IsVisible()) return;
                ClearPending();
                window.Show();
            }
            catch (Exception error) { ClearPending(); if (window != null) window.Hide(); Report(error); }
        }
        private void ClearPending() { pendingPlayer = null; pendingNetwork = null; waitingForInventory = false; }
        private void ShortcutChanged(object sender, EventArgs args)
        {
            ClearPending(); capture.Reset(); capture.Prime(shortcut.Value, Time.frameCount, readHeld);
            capturePlayer = null; captureNetwork = null;
        }
        private bool CanOpen()
        {
            var player = Player.m_localPlayer;
            if (player == null || ZNet.instance == null || player.IsDead() || player.IsTeleporting() || player.InCutscene()
                || player.IsSleeping() || UnifiedPopup.IsVisible() || Menu.IsVisible()
                || GUIManager.CustomGUIFront == null || ZInput.s_IsRebindActive
                || StoreGui.IsVisible() || Hud.IsPieceSelectionVisible() || PlayerCustomizaton.IsBarberGuiVisible()
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
            capture.Reset(); capturePlayer = null; captureNetwork = null;
            if (window != null) window.Hide();
            ClearMarkers();
        }
        private void Shutdown()
        {
            ready = false; OnDisable();
            if (shortcut != null) shortcut.SettingChanged -= ShortcutChanged;
            if (names != null) Localization.OnLanguageChange -= names.Clear;
            if (inputPatches != null) inputPatches.UnpatchSelf();
            if (ReferenceEquals(active, this)) active = null;
        }
        private void OnDestroy() { Shutdown(); }
    }
}
