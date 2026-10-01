using System;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;
using ValheimModPack.ExpeditionLoadouts;
internal static class HotkeyTests
{
    private static int checks;
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    private static void Call(Plugin plugin, string name) { typeof(Plugin).GetMethod(name, (BindingFlags)60).Invoke(plugin, null); }
    private static object Field(Plugin plugin, string name) { return typeof(Plugin).GetField(name, (BindingFlags)60).GetValue(plugin); }
    private static void Frame(params KeyCode[] keys) { Time.frameCount++; Time.unscaledTime += .02f; Input.Down.Clear(); Input.Held.Clear(); foreach (var key in keys) { Input.Down.Add(key); Input.Held.Add(key); } }
    private static Plugin Start()
    {
        Player.m_localPlayer = new Player(); ZNet.instance = new ZNet(); InventoryGui.Visible = false; InventoryGui.HiddenUntil = 0;
        Menu.Visible = TextInput.Visible = UnifiedPopup.Visible = Console.Visible = ZInput.s_IsRebindActive = false;
        StoreGui.Visible = Hud.Visible = PlayerCustomizaton.Visible = false;
        ZInput.ResetFixture();
        Jotunn.Managers.GUIManager.CustomGUIFront = new GameObject();
        Chat.instance = null; Minimap.instance = null; UnityEngine.EventSystems.EventSystem.current = null;
        Frame(); var plugin = new Plugin(); Call(plugin, "Awake"); return plugin;
    }
    private static void Finish(Plugin plugin)
    {
        Call(plugin, "OnDestroy");
        Check(Jotunn.Managers.GUIManager.InputBlocks == 0, "Shutdown releases the modal's input block exactly once");
        Check(plugin.Service.Disposes == 1, "Shutdown preserves service disposal");
    }
    private static void OpenFrames(Plugin plugin) { Call(plugin, "LateUpdate"); Frame(); Call(plugin, "Update"); Call(plugin, "LateUpdate"); Frame(); Call(plugin, "LateUpdate"); }
    private static int Main()
    {
        try
        {
            var cachedPlugin = Start(); var cachedWindow = (LoadoutWindow)Field(cachedPlugin, "window");
            ZInput.CachedButtons = true;
            Frame(KeyCode.L, KeyCode.LeftControl); Input.Down.Clear();
            ZInput.Button("GP").Press(); ZInput.Button("GP").Tick();
            Check(!ZInput.GetKeyDown(KeyCode.L) && ZInput.Button("GP").Pressed, "Fixture separates expired raw edge from the native GP cache");
            Player.m_localPlayer.NativeUpdate();
            Check(Player.m_localPlayer.GuardianStarts == 0, "Cached native GP is claimed before Player.Update without plugin Update");
            OpenFrames(cachedPlugin); Check(cachedWindow.IsVisible, "A physical shortcut still opens when its raw edge expired"); Finish(cachedPlugin);
            foreach (bool fixedTick in new[] { false, true })
            {
                cachedPlugin = Start(); cachedWindow = (LoadoutWindow)Field(cachedPlugin, "window"); ZInput.CachedButtons = true;
                Frame(KeyCode.L, KeyCode.RightControl); Input.Down.Clear();
                ZInput.Button("GP").Press(); ZInput.Button("Forward").Press(); ZInput.Button("JoyButtonB").Press();
                if (fixedTick) ZInput.FixedUpdate(.02f); else ZInput.Update(.02f);
                Check(Field(cachedPlugin, "pendingPlayer") != null, "The native input tick claims before any game consumer or plugin Update");
                Check(!ZInput.Button("GP").Pressed && !ZInput.Button("GP").FixedPressed, "Claim drains guardian presses from both native phases");
                Check(ZInput.Button("Forward").Held, "Draining button edges retains physically held movement");
                Check(ZInput.GetButtonDown("JoyButtonB") && ZInput.Button("JoyButtonB").FixedPressed, "Draining gameplay caches preserves controller Cancel in both phases");
                Player.m_localPlayer.NativeUpdate();
                Check(Player.m_localPlayer.GuardianStarts == 0, "Vanilla guardian path is blocked after either native tick");
                Check(!Player.m_localPlayer.StartGuardianPower(), "Direct local guardian calls also respect pending loadouts");
                var remote = new Player(); Check(remote.StartGuardianPower() && remote.GuardianStarts == 1, "Local modal does not suppress a different player");
                Call(cachedPlugin, "LateUpdate");
                Time.frameCount++; Time.unscaledTime += .02f; Call(cachedPlugin, "LateUpdate");
                Check(cachedWindow.IsVisible, "Native tick capture opens while the original keys remain held");
                cachedWindow.Hide(); Input.Held.Remove(KeyCode.RightControl); Input.Down.Clear();
                Check(!Player.m_localPlayer.TakeInput() && !ZInput.GetButtonDown("GP"), "Closing the modal and releasing Ctrl first retain the held L stroke");
                Input.Held.Clear(); ZInput.Button("GP").Release(); ZInput.Update(.02f);
                Check(!ZInput.Button("GP").Released && !ZInput.Button("GP").FixedReleased, "The captured key release is drained in both native phases");
                Check(!ZInput.GetButtonUp("GP"), "Release cannot leak after closing the modal");
                Frame();
                Check(Player.m_localPlayer.TakeInput() && ZInput.GetButton("Forward"), "Input and held movement restore after the release frame without pressing movement again");
                Player.m_localPlayer.NativeUpdate(); Check(Player.m_localPlayer.GuardianStarts == 0, "The buffered GP cannot activate on the next frame before Game.Update");
                Frame(KeyCode.L); ZInput.Button("GP").Press(); ZInput.Update(.02f); Player.m_localPlayer.NativeUpdate();
                Check(Player.m_localPlayer.GuardianStarts == 1, "A fresh plain L invokes vanilla guardian power exactly once"); Finish(cachedPlugin);
            }
            foreach (bool inventoryOpen in new[] { false, true })
            {
                cachedPlugin = Start(); cachedWindow = (LoadoutWindow)Field(cachedPlugin, "window"); ZInput.CachedButtons = true;
                InventoryGui.Visible = inventoryOpen; Frame(KeyCode.L, KeyCode.LeftControl); Input.Down.Clear();
                ZInput.Button("GP").Press(); ZInput.Update(.02f); Player.m_localPlayer.NativeUpdate(); OpenFrames(cachedPlugin);
                Check(cachedWindow.IsVisible && Player.m_localPlayer.GuardianStarts == 0, "The expired-edge shortcut is safe from gameplay and open inventory");
                ZInput.Button("GP").Press(); ZInput.Update(.02f);
                Check(!ZInput.Button("GP").Pressed && !ZInput.Button("GP").FixedPressed, "New buffered presses during the modal are drained before it closes");
                cachedWindow.Hide(); Frame(); Player.m_localPlayer.NativeUpdate();
                Check(Player.m_localPlayer.GuardianStarts == 0, "Closing the modal cannot resurrect a consumed GP press"); Finish(cachedPlugin);
            }
            cachedPlugin = Start(); Menu.Visible = true; Frame(KeyCode.L, KeyCode.LeftControl);
            ZInput.Update(.02f); Menu.Visible = false; Time.frameCount++; Time.unscaledTime += .02f;
            // Some InputSystem phases keep the raw edge set across this boundary.
            ZInput.Update(.02f); Call(cachedPlugin, "Update");
            Check(Field(cachedPlugin, "pendingPlayer") == null, "Closing rejected UI while its key remains held does not create a delayed shortcut");
            Frame(); ZInput.Update(.02f); Frame(KeyCode.L, KeyCode.LeftControl); Input.Down.Clear(); ZInput.Update(.02f);
            Check(Field(cachedPlugin, "pendingPlayer") != null, "A fresh physical press works after a rejected UI stroke is released"); Finish(cachedPlugin);
            cachedPlugin = Start(); Frame(KeyCode.L, KeyCode.LeftControl, KeyCode.LeftShift); ZInput.Update(.02f);
            Input.Held.Remove(KeyCode.LeftShift); Time.frameCount++; ZInput.Update(.02f);
            Check(Field(cachedPlugin, "pendingPlayer") == null, "Releasing an extra modifier cannot turn a rejected held stroke into a shortcut"); Finish(cachedPlugin);
            cachedPlugin = Start(); var primedBinding = (ConfigEntry<KeyboardShortcut>)Field(cachedPlugin, "shortcut");
            Frame(KeyCode.G, KeyCode.LeftControl); primedBinding.Value = new KeyboardShortcut(KeyCode.G, KeyCode.LeftControl); ZInput.Update(.02f);
            Check(Field(cachedPlugin, "pendingPlayer") == null, "Rebinding to a currently held key requires releasing that stroke");
            Frame(); ZInput.Update(.02f); Frame(KeyCode.G, KeyCode.RightControl); Input.Down.Clear(); ZInput.Update(.02f);
            Check(Field(cachedPlugin, "pendingPlayer") != null, "Bindrune rebind accepts a fresh physical press even when the raw edge expired"); Finish(cachedPlugin);
            var plugin = Start(); var window = (LoadoutWindow)Field(plugin, "window");
            Frame(KeyCode.L, KeyCode.LeftControl); Call(plugin, "Update"); OpenFrames(plugin);
            Check(window.IsVisible, "Ctrl+L must open loadouts without first opening inventory"); Finish(plugin);
            Check(plugin.Store != null && plugin.Store.PlayerId == 123 && plugin.Service.Ticks > 0,
                "Gameplay opening initializes the character's presets and keeps transfer ticks");
            foreach (bool controllerFirst in new[] { false, true })
            {
                plugin = Start(); window = (LoadoutWindow)Field(plugin, "window");
                Frame(KeyCode.L, KeyCode.RightControl);
                bool allowed = controllerFirst ? new PlayerController().TakeInput() : ZInput.GetButtonDown("GP");
                Check(!allowed, "The first game consumer captures the shortcut before plugin Update");
                Check(!ZInput.GetButton("GP") && !ZInput.GetButtonUp("GP"), "Held and release actions are consumed too");
                Check(!ZInput.GetButtonDown("Jump") && !ZInput.GetButtonDown("Crouch"), "Other gameplay actions do not leak on opening frame");
                Check(!new PlayerController().TakeInput(), "Vanilla controller follows its zero-controls path");
                Check(ZInput.GetButtonDown("JoyButtonB"), "Controller Cancel remains available");
                Call(plugin, "Update"); OpenFrames(plugin);
                Check(window.IsVisible && window.Opens == 1, "Right Ctrl works with either execution order and opens once");
                Check(!ZInput.GetButtonDown("GP"), "Game action suppressed while modal is visible");
                window.Hide(); Frame(KeyCode.L);
                Check(ZInput.GetButtonDown("GP") && new PlayerController().TakeInput(), "Plain L works after modal closes and original stroke was released");
                Finish(plugin);
            }
            plugin = Start(); window = (LoadoutWindow)Field(plugin, "window");
            InventoryGui.Visible = true; Frame(KeyCode.L, KeyCode.LeftControl);
            Check(!ZInput.GetButtonDown("Inventory"), "Opening from inventory also captures before native inventory toggles");
            Call(plugin, "LateUpdate"); Check(!window.IsVisible, "Inventory hide animation completes before search input activates");
            Frame(); Call(plugin, "LateUpdate"); Check(!window.IsVisible, "No early modal on first hide frame");
            Frame(); Call(plugin, "LateUpdate"); Check(window.IsVisible, "Modal opens after both hide frames");
            Finish(plugin);

            plugin = Start(); window = (LoadoutWindow)Field(plugin, "window");
            Frame(KeyCode.L, KeyCode.LeftControl); ZInput.GetButtonDown("GP");
            Call(plugin, "LateUpdate"); Time.frameCount++; Time.unscaledTime += .02f; Input.Down.Clear(); Call(plugin, "LateUpdate");
            Check(window.IsVisible, "Can open while original shortcut remains held"); window.Hide(); Input.Held.Remove(KeyCode.LeftControl);
            Check(!ZInput.GetButton("GP"), "Releasing Ctrl first cannot leak the still-held L");
            Input.Held.Clear(); Check(!ZInput.GetButtonUp("GP"), "Captured key-up stays consumed for its frame");
            Frame(); Check(ZInput.GetButton("GP"), "Control is restored on following frame"); Finish(plugin);

            plugin = Start(); window = (LoadoutWindow)Field(plugin, "window");
            var binding = (ConfigEntry<KeyboardShortcut>)Field(plugin, "shortcut");
            binding.Value = new KeyboardShortcut(KeyCode.G, KeyCode.LeftControl, KeyCode.LeftShift);
            Frame(KeyCode.L, KeyCode.LeftControl); Check(ZInput.GetButtonDown("GP"), "Old shortcut no longer intercepts after rebind");
            Frame(KeyCode.G, KeyCode.RightControl); Check(ZInput.GetButtonDown("GP"), "Rebound shortcut requires every modifier");
            Frame(); ZInput.Update(.02f);
            Frame(KeyCode.G, KeyCode.RightControl, KeyCode.RightShift); Check(!ZInput.GetButtonDown("Use"), "Rebound key accepts either modifier side and consumes its game action");
            OpenFrames(plugin); Check(window.IsVisible, "Bindrune-style config change takes effect without restart"); Finish(plugin);
            foreach (KeyCode extra in new[] { KeyCode.LeftShift, KeyCode.RightAlt, KeyCode.LeftCommand })
            {
                plugin = Start(); Frame(KeyCode.L, KeyCode.LeftControl, extra);
                Check(ZInput.GetButtonDown("GP"), "Unconfigured extra modifiers do not open or consume loadouts"); Finish(plugin);
            }
            plugin = Start(); binding = (ConfigEntry<KeyboardShortcut>)Field(plugin, "shortcut");
            binding.Value = new KeyboardShortcut(KeyCode.None); Frame(KeyCode.L, KeyCode.LeftControl);
            Check(ZInput.GetButtonDown("GP"), "Unbound shortcut leaves gameplay alone"); Finish(plugin);
            plugin = Start(); binding = (ConfigEntry<KeyboardShortcut>)Field(plugin, "shortcut");
            binding.Value = new KeyboardShortcut(KeyCode.Space); Frame(KeyCode.Space);
            Check(!ZInput.GetButtonDown("Jump"), "Rebinding to a plain game key consumes its action"); Finish(plugin);

            Action[] blockedContexts = {
                () => Menu.Visible = true, () => TextInput.Visible = true, () => UnifiedPopup.Visible = true,
                () => Console.Visible = true, () => Chat.instance = new Chat { Focus = true },
                () => Minimap.instance = new Minimap { m_mode = Minimap.MapMode.Large },
                () => Player.m_localPlayer.Dead = true, () => Player.m_localPlayer.Teleporting = true,
                () => Player.m_localPlayer.Sleeping = true, () => Player.m_localPlayer.Cutscene = true,
                () => Player.m_localPlayer = null, () => ZNet.instance = null, () => ZInput.s_IsRebindActive = true,
                () => Player.m_localPlayer.Id = 0, () => Jotunn.Managers.GUIManager.CustomGUIFront = null,
                () => StoreGui.Visible = true, () => Hud.Visible = true, () => PlayerCustomizaton.Visible = true,
                () => {
                    var selected = new GameObject(); selected.Components.Add(new UnityEngine.UI.InputField { isFocused = true });
                    UnityEngine.EventSystems.EventSystem.current = new UnityEngine.EventSystems.EventSystem { currentSelectedGameObject = selected };
                },
                () => {
                    var selected = new GameObject(); selected.Components.Add(new TMPro.TMP_InputField { isFocused = true });
                    UnityEngine.EventSystems.EventSystem.current = new UnityEngine.EventSystems.EventSystem { currentSelectedGameObject = selected };
                }
            };
            foreach (var block in blockedContexts)
            {
                plugin = Start(); window = (LoadoutWindow)Field(plugin, "window"); block(); Frame(KeyCode.L, KeyCode.LeftControl);
                Check(ZInput.GetButtonDown("GP"), "Other UI/dead/disconnected/rebind contexts are not claimed");
                Call(plugin, "Update"); OpenFrames(plugin); Check(!window.IsVisible, "Loadouts stay closed in disallowed context"); Finish(plugin);
            }
            foreach (bool switchWorld in new[] { false, true })
            {
                plugin = Start(); Frame(KeyCode.L, KeyCode.LeftControl); ZInput.GetButtonDown("GP");
                if (switchWorld) ZNet.instance = new ZNet(); else Player.m_localPlayer = new Player();
                Input.Down.Clear(); Check(ZInput.GetButton("GP"), "Changing player/world clears pending input ownership");
                OpenFrames(plugin); Check(!((LoadoutWindow)Field(plugin, "window")).IsVisible, "Stale request never opens in new context"); Finish(plugin);
            }
            plugin = Start(); Frame(KeyCode.L, KeyCode.LeftControl); ZInput.GetButtonDown("GP");
            Player.m_localPlayer.Id = 456; Input.Down.Clear();
            Check(ZInput.GetButton("GP") && plugin.Store.PlayerId == 456,
                "Changing character ID on the same player object refreshes presets before native input");
            OpenFrames(plugin); Check(!((LoadoutWindow)Field(plugin, "window")).IsVisible,
                "The old character's pending request cannot open after an ID change"); Finish(plugin);

            foreach (bool tmp in new[] { false, true })
            {
                plugin = Start(); window = (LoadoutWindow)Field(plugin, "window");
                var selected = new GameObject();
                if (tmp) selected.Components.Add(new TMPro.TMP_InputField { isFocused = true, isActiveAndEnabled = false });
                else selected.Components.Add(new UnityEngine.UI.InputField { isFocused = true, isActiveAndEnabled = false });
                UnityEngine.EventSystems.EventSystem.current = new UnityEngine.EventSystems.EventSystem { currentSelectedGameObject = selected };
                Frame(KeyCode.L, KeyCode.LeftControl); Check(!ZInput.GetButtonDown("GP"), "Disabled text fields do not steal gameplay shortcut ownership");
                OpenFrames(plugin); Check(window.IsVisible, "Gameplay opening remains possible with a stale disabled text selection"); Finish(plugin);
            }

            plugin = Start(); window = (LoadoutWindow)Field(plugin, "window");
            Frame(KeyCode.L, KeyCode.LeftControl); ZInput.GetButtonDown("GP");
            TextInput.Visible = true; Call(plugin, "LateUpdate"); Frame(); Call(plugin, "Update");
            Check(!window.IsVisible && Field(plugin, "pendingPlayer") == null,
                "A text modal appearing after the opening stroke cancels the pending request"); Finish(plugin);

            plugin = Start(); window = (LoadoutWindow)Field(plugin, "window");
            Frame(KeyCode.L, KeyCode.LeftControl); Call(plugin, "Update"); OpenFrames(plugin);
            Check(Jotunn.Managers.GUIManager.InputBlocks == 1, "An open preset window owns one input block");
            window.Hide(); window.Hide(); Check(Jotunn.Managers.GUIManager.InputBlocks == 0,
                "Repeated hide cannot release another modal's input block"); Finish(plugin);
            plugin = Start(); Frame(KeyCode.L, KeyCode.LeftControl); ZInput.GetButtonDown("GP");
            Frame(KeyCode.Escape); Call(plugin, "Update"); Call(plugin, "LateUpdate"); Frame();
            Check(ZInput.GetButton("GP") && Field(plugin, "pendingPlayer") == null, "Escape cancels pending request without input leak"); Finish(plugin);
            plugin = Start(); Frame(KeyCode.L, KeyCode.LeftControl); ZInput.GetButtonDown("GP");
            Frame(); Time.unscaledTime += 2; Call(plugin, "Update"); Call(plugin, "LateUpdate");
            Check(Field(plugin, "pendingPlayer") == null, "Request timeout cancels safely"); Frame();
            Check(ZInput.GetButton("GP"), "Timeout restores gameplay after captured key releases"); Finish(plugin);
            plugin = Start(); Frame(KeyCode.L, KeyCode.LeftControl); ZInput.GetButtonDown("GP");
            plugin.isActiveAndEnabled = false; Call(plugin, "OnDisable");
            Check(ZInput.GetButton("GP"), "Disabled plugin cannot retain input ownership"); Finish(plugin);
            Check(HarmonyLib.Harmony.Prefixes.Count == 0, "Shutdown removes only this fixture's hooks");
            System.Console.WriteLine("PASS " + checks + " expedition-loadouts input integration checks."); return 0;
        }
        catch (Exception error) { System.Console.Error.WriteLine(error); return 1; }
    }
}
