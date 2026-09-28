using System;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;
using ValheimModPack.ChestSearch;
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
        Chat.instance = null; Minimap.instance = null; UnityEngine.EventSystems.EventSystem.current = null;
        Frame(); var plugin = new Plugin(); Call(plugin, "Awake"); return plugin;
    }
    private static void Finish(Plugin plugin) { Call(plugin, "OnDestroy"); }
    private static void OpenFrames(Plugin plugin) { Call(plugin, "LateUpdate"); Frame(); Call(plugin, "Update"); Call(plugin, "LateUpdate"); Frame(); Call(plugin, "LateUpdate"); }
    private static int Main()
    {
        try
        {
            var plugin = Start(); var window = (SearchWindow)Field(plugin, "window");
            Frame(KeyCode.F, KeyCode.LeftControl); Call(plugin, "Update"); OpenFrames(plugin);
            Check(window.IsVisible, "Ctrl+F must open search without first opening inventory"); Finish(plugin);
            foreach (bool controllerFirst in new[] { false, true })
            {
                plugin = Start(); window = (SearchWindow)Field(plugin, "window");
                Frame(KeyCode.F, KeyCode.RightControl);
                bool allowed = controllerFirst ? new PlayerController().TakeInput() : ZInput.GetButtonDown("GP");
                Check(!allowed, "The first game consumer captures the shortcut before plugin Update");
                Check(!ZInput.GetButton("GP") && !ZInput.GetButtonUp("GP"), "Held and release actions are consumed too");
                Check(!ZInput.GetButtonDown("Jump") && !ZInput.GetButtonDown("Crouch"), "Other gameplay actions do not leak on opening frame");
                Check(!new PlayerController().TakeInput(), "Vanilla controller follows its zero-controls path");
                Check(ZInput.GetButtonDown("JoyButtonB"), "Controller Cancel remains available");
                Call(plugin, "Update"); OpenFrames(plugin);
                Check(window.IsVisible && window.Opens == 1, "Right Ctrl works with either execution order and opens once");
                Check(!ZInput.GetButtonDown("GP"), "Game action suppressed while modal is visible");
                window.Hide(); Frame(KeyCode.F);
                Check(ZInput.GetButtonDown("GP") && new PlayerController().TakeInput(), "Plain F works after modal closes and original stroke was released");
                Finish(plugin);
            }
            plugin = Start(); window = (SearchWindow)Field(plugin, "window");
            InventoryGui.Visible = true; Frame(KeyCode.F, KeyCode.LeftControl);
            Check(!ZInput.GetButtonDown("Inventory"), "Opening from inventory also captures before native inventory toggles");
            Call(plugin, "LateUpdate"); Check(!window.IsVisible, "Inventory hide animation completes before search input activates");
            Frame(); Call(plugin, "LateUpdate"); Check(!window.IsVisible, "No early modal on first hide frame");
            Frame(); Call(plugin, "LateUpdate"); Check(window.IsVisible, "Modal opens after both hide frames");
            Finish(plugin);

            plugin = Start(); window = (SearchWindow)Field(plugin, "window");
            Frame(KeyCode.F, KeyCode.LeftControl); ZInput.GetButtonDown("GP");
            Call(plugin, "LateUpdate"); Time.frameCount++; Time.unscaledTime += .02f; Input.Down.Clear(); Call(plugin, "LateUpdate");
            Check(window.IsVisible, "Can open while original shortcut remains held"); window.Hide(); Input.Held.Remove(KeyCode.LeftControl);
            Check(!ZInput.GetButton("GP"), "Releasing Ctrl first cannot leak the still-held F");
            Input.Held.Clear(); Check(!ZInput.GetButtonUp("GP"), "Captured key-up stays consumed for its frame");
            Frame(); Check(ZInput.GetButton("GP"), "Control is restored on following frame"); Finish(plugin);

            plugin = Start(); window = (SearchWindow)Field(plugin, "window");
            var binding = (ConfigEntry<KeyboardShortcut>)Field(plugin, "shortcut");
            binding.Value = new KeyboardShortcut(KeyCode.G, KeyCode.LeftControl, KeyCode.LeftShift);
            Frame(KeyCode.F, KeyCode.LeftControl); Check(ZInput.GetButtonDown("GP"), "Old shortcut no longer intercepts after rebind");
            Frame(KeyCode.G, KeyCode.RightControl); Check(ZInput.GetButtonDown("GP"), "Rebound shortcut requires every modifier");
            Frame(KeyCode.G, KeyCode.RightControl, KeyCode.RightShift); Check(!ZInput.GetButtonDown("Use"), "Rebound key accepts either modifier side and consumes its game action");
            OpenFrames(plugin); Check(window.IsVisible, "Bindrune-style config change takes effect without restart"); Finish(plugin);
            foreach (KeyCode extra in new[] { KeyCode.LeftShift, KeyCode.RightAlt, KeyCode.LeftCommand })
            {
                plugin = Start(); Frame(KeyCode.F, KeyCode.LeftControl, extra);
                Check(ZInput.GetButtonDown("GP"), "Unconfigured extra modifiers do not open or consume search"); Finish(plugin);
            }
            plugin = Start(); binding = (ConfigEntry<KeyboardShortcut>)Field(plugin, "shortcut");
            binding.Value = new KeyboardShortcut(KeyCode.None); Frame(KeyCode.F, KeyCode.LeftControl);
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
                () => StoreGui.Visible = true, () => Hud.Visible = true, () => PlayerCustomizaton.Visible = true,
                () => {
                    var selected = new GameObject(); selected.Components.Add(new UnityEngine.UI.InputField { isFocused = true });
                    UnityEngine.EventSystems.EventSystem.current = new UnityEngine.EventSystems.EventSystem { currentSelectedGameObject = selected };
                }
            };
            foreach (var block in blockedContexts)
            {
                plugin = Start(); window = (SearchWindow)Field(plugin, "window"); block(); Frame(KeyCode.F, KeyCode.LeftControl);
                Check(ZInput.GetButtonDown("GP"), "Other UI/dead/disconnected/rebind contexts are not claimed");
                Call(plugin, "Update"); OpenFrames(plugin); Check(!window.IsVisible, "Search stays closed in disallowed context"); Finish(plugin);
            }
            foreach (bool switchWorld in new[] { false, true })
            {
                plugin = Start(); Frame(KeyCode.F, KeyCode.LeftControl); ZInput.GetButtonDown("GP");
                if (switchWorld) ZNet.instance = new ZNet(); else Player.m_localPlayer = new Player();
                Input.Down.Clear(); Check(ZInput.GetButton("GP"), "Changing player/world clears pending input ownership");
                OpenFrames(plugin); Check(!((SearchWindow)Field(plugin, "window")).IsVisible, "Stale request never opens in new context"); Finish(plugin);
            }
            plugin = Start(); Frame(KeyCode.F, KeyCode.LeftControl); ZInput.GetButtonDown("GP");
            Frame(KeyCode.Escape); Call(plugin, "Update"); Call(plugin, "LateUpdate"); Frame();
            Check(ZInput.GetButton("GP") && Field(plugin, "pendingPlayer") == null, "Escape cancels pending request without input leak"); Finish(plugin);
            plugin = Start(); Frame(KeyCode.F, KeyCode.LeftControl); ZInput.GetButtonDown("GP");
            Frame(); Time.unscaledTime += 2; Call(plugin, "Update"); Call(plugin, "LateUpdate");
            Check(Field(plugin, "pendingPlayer") == null, "Request timeout cancels safely"); Frame();
            Check(ZInput.GetButton("GP"), "Timeout restores gameplay after captured key releases"); Finish(plugin);
            plugin = Start(); Frame(KeyCode.F, KeyCode.LeftControl); ZInput.GetButtonDown("GP");
            plugin.isActiveAndEnabled = false; Call(plugin, "OnDisable");
            Check(ZInput.GetButton("GP"), "Disabled plugin cannot retain input ownership"); Finish(plugin);
            Check(HarmonyLib.Harmony.Prefixes.Count == 0, "Shutdown removes only this fixture's hooks");
            System.Console.WriteLine("PASS " + checks + " chest-search input integration checks."); return 0;
        }
        catch (Exception error) { System.Console.Error.WriteLine(error); return 1; }
    }
}
