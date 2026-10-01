using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Managers;
using Jotunn.Utils;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ValheimModPack.ExpeditionLoadouts
{
    [BepInPlugin(Id, "Expedition Loadouts", Version)]
    [BepInDependency("com.jotunn.jotunn", "2.30.2")]
    [BepInDependency("randyknapp.mods.equipmentandquickslots", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("goldenrevolver.quick_stack_store", BepInDependency.DependencyFlags.SoftDependency)]
    [NetworkCompatibility(CompatibilityLevel.NotEnforced, VersionStrictness.None)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "valheimmodpack.expeditionloadouts";
        public const string Version = "1.2.1";
        public ChestService Service { get; private set; }
        public PresetStore Store { get; private set; }
        private ConfigEntry<KeyboardShortcut> shortcut;
        private ConfigEntry<KeyboardShortcut> largeStep;
        internal string LargeStepLabel { get { return largeStep.Value.ToString(); } }
        internal bool LargeStepHeld
        {
            get
            {
                var key = largeStep.Value;
                // Preserve either Shift for the default while allowing full rebinding.
                return key.IsPressed() || (key.Equals(new KeyboardShortcut(KeyCode.LeftShift))
                    && Input.GetKey(KeyCode.RightShift) && !Input.GetKey(KeyCode.LeftControl) && !Input.GetKey(KeyCode.RightControl)
                    && !Input.GetKey(KeyCode.LeftAlt) && !Input.GetKey(KeyCode.RightAlt));
            }
        }
        private ConfigEntry<float> radius;
        private LoadoutWindow window;
        private Player owner;
        private long ownerId;
        private ZNet network;
        private Player pendingPlayer;
        private ZNet pendingNetwork;
        private long pendingPlayerId;
        private bool waitingForInventory;
        private int openAfterFrame;
        private float openDeadline;
        private bool ready;
        private static Plugin active;
        private Harmony inputPatches;
        private readonly ShortcutCapture capture = new ShortcutCapture();
        private static readonly Func<KeyCode, bool> readHeld = KeyHeld, readDown = KeyDown;
        private string directory;
        public float Radius { get { return Single.IsNaN(radius.Value) ? 10 : Mathf.Clamp(radius.Value, 1, 30); } }

        private void Awake()
        {
            shortcut = Config.Bind("Controls", "OpenShortcut", new KeyboardShortcut(KeyCode.L, KeyCode.LeftControl),
                "Open expedition loadouts in gameplay or inventory. Modifier sides are interchangeable; opening input is consumed. Rebind with Bindrune.");
            shortcut.SettingChanged += ShortcutChanged;
            capture.Prime(shortcut.Value, Time.frameCount, readHeld);
            largeStep = Config.Bind("Controls", "LargeAmountModifier", new KeyboardShortcut(KeyCode.LeftShift),
                "Hold while clicking +/- to change the target by 10 / Удерживать при нажатии +/- для шага 10.");
            radius = Config.Bind("Storage", "Radius", 10f, new ConfigDescription("Radius of loaded accessible chests, in metres.", new AcceptableValueRange<float>(1, 30)));
            directory = Path.Combine(Directory.GetParent(BepInEx.Paths.BepInExRootPath).FullName, "ValheimModpack/ExpeditionLoadouts");
            try
            {
                Service = new ChestService(Logger);
                window = new LoadoutWindow(this);
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
                Logger.LogInfo("Expedition Loadouts " + Version + " ready. Ctrl + L opens in gameplay or inventory.");
            }
            catch (Exception error) { Logger.LogError("Loadout initialization failed: " + error); Shutdown(); enabled = false; }
        }

        private void RefreshContext()
        {
            Player player = Player.m_localPlayer;
            long playerId = player == null ? 0 : player.GetPlayerID();
            if (ReferenceEquals(player, owner) && ReferenceEquals(ZNet.instance, network) && playerId == ownerId) return;
            bool previousContext = owner != null || network != null || ownerId != 0;
            window.Hide(); Service.Cancel(); ClearPending(); capture.Reset(); Store = null;
            if (previousContext) capture.Prime(shortcut.Value, Time.frameCount, readHeld);
            owner = player; ownerId = playerId; network = ZNet.instance;
            if (owner != null && network != null && ownerId != 0)
            {
                Store = new PresetStore(directory, ownerId);
                if (Store.ReadOnly) Logger.LogWarning("Preset file preserved after load error: " + Store.LoadError);
            }
        }

        private void Update()
        {
            if (!ready) return;
            try
            {
                RefreshContext();
                window.Tick();
                Service.Tick();
                if (!ValidPlayer(owner)) { ClearPending(); capture.Reset(); if (window.IsVisible) window.Hide(); return; }
                PollShortcut();
            }
            catch (Exception error)
            {
                Logger.LogError("Loadout update failed; operation cancelled: " + error);
                ClearPending(); capture.Reset(); if (window != null) window.Hide(); if (Service != null) Service.Cancel();
            }
        }

        private static bool KeyHeld(KeyCode key) { return ZInput.GetKey(key, false); }
        private static bool KeyDown(KeyCode key) { return ZInput.GetKeyDown(key, false); }

        private void PollShortcut()
        {
            if (!ready || !isActiveAndEnabled) return;
            // Native input can run before Update, including the first frame after
            // joining a world. Establish the character's store before claiming it.
            RefreshContext();
            bool consumed = capture.Blocked(Time.frameCount, readHeld);
            bool pressed = capture.Pressed(shortcut.Value, Time.frameCount, readDown, readHeld);
            if (window.IsVisible || pendingPlayer != null || consumed
                || !pressed || !CanOpen()) return;
            capture.Claim(shortcut.Value.MainKey);
            GameplayInputCache.ConsumeAll();
            pendingPlayer = owner; pendingNetwork = network; pendingPlayerId = ownerId;
            waitingForInventory = false; openAfterFrame = Time.frameCount + 1;
            openDeadline = Time.unscaledTime + 1.5f;
        }

        private bool BlocksGameplay()
        {
            try
            {
                PollShortcut();
                return ready && isActiveAndEnabled
                    && (window.IsVisible || pendingPlayer != null || capture.Blocked(Time.frameCount, readHeld));
            }
            catch (Exception error) { ClearPending(); capture.Reset(); Error(error); return false; }
        }

        private static bool BeforeGameButton(string __0, ref bool __result)
        {
            // Preserve the modal's controller Cancel button.
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
            // Player.Update has its own input gate, independent of the native
            // controller's FixedUpdate. Guard direct local guardian-power calls too.
            if (active == null || !ReferenceEquals(__instance, Player.m_localPlayer) || !active.BlocksGameplay()) return true;
            __result = false; return false;
        }

        private static bool BeforePlayerInput(Player ___m_character, ref bool __result)
        {
            // Capture before native FixedUpdate reads controls, even when our
            // Update has not run. Vanilla then uses its zero-controls path.
            if (active == null || ___m_character == null || !ReferenceEquals(___m_character, Player.m_localPlayer)
                || !active.BlocksGameplay()) return true;
            __result = false; return false;
        }

        private void LateUpdate()
        {
            if (ReferenceEquals(pendingPlayer, null)) return;
            try
            {
                if (!ready || !ReferenceEquals(pendingPlayer, Player.m_localPlayer)
                    || !ReferenceEquals(pendingNetwork, ZNet.instance) || pendingPlayer.GetPlayerID() != pendingPlayerId
                    || Time.unscaledTime >= openDeadline || Input.GetKeyDown(KeyCode.Escape) || !CanOpen())
                { ClearPending(); return; }
                if (!waitingForInventory)
                {
                    // Inventory remains visible for two native updates after
                    // Hide; focus the new modal only after its hotkeys stop.
                    if (InventoryGui.IsVisible())
                    { InventoryGui.instance.Hide(); openAfterFrame = Time.frameCount + 2; }
                    waitingForInventory = true;
                }
                if (Time.frameCount < openAfterFrame || InventoryGui.IsVisible()) return;
                ClearPending(); window.Show();
            }
            catch (Exception error) { ClearPending(); if (window != null) window.Hide(); if (Service != null) Service.Cancel(); Error(error); }
        }

        private void ClearPending()
        {
            pendingPlayer = null; pendingNetwork = null; pendingPlayerId = 0; waitingForInventory = false;
        }

        private void ShortcutChanged(object sender, EventArgs args)
        {
            ClearPending(); capture.Reset(); capture.Prime(shortcut.Value, Time.frameCount, readHeld);
        }

        internal static bool ValidPlayer(Player player)
        {
            return player != null && ReferenceEquals(player, Player.m_localPlayer) && player.GetPlayerID() != 0 && ZNet.instance != null
                && !player.IsDead() && !player.IsTeleporting() && !player.InCutscene() && !player.IsSleeping();
        }
        private bool CanOpen()
        {
            // Jotunn reports its input lock through TextInput.IsVisible. Check this
            // before taking our own lock, never from the already-open window.
            if (!ValidPlayer(Player.m_localPlayer) || Store == null || GUIManager.CustomGUIFront == null || ZInput.s_IsRebindActive
                || StoreGui.IsVisible() || Hud.IsPieceSelectionVisible() || PlayerCustomizaton.IsBarberGuiVisible()
                || UnifiedPopup.IsVisible() || Menu.IsVisible() || TextInput.IsVisible() || global::Console.IsVisible()
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
        { ClearPending(); capture.Reset(); if (window != null) window.Hide(); if (Service != null) Service.Cancel(); }
        private void Shutdown()
        {
            ready = false; OnDisable();
            if (shortcut != null) shortcut.SettingChanged -= ShortcutChanged;
            if (inputPatches != null) inputPatches.UnpatchSelf();
            if (ReferenceEquals(active, this)) active = null;
        }
        private void OnDestroy()
        { Shutdown(); if (Service != null) Service.Dispose(); }
    }
}
