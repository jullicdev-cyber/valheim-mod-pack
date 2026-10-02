using System;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;
using ValheimModPack.NordicRadio;
internal static class PersonalAudioTests
{
    private static int checks;
    private static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }
    private static void Frame(params KeyCode[] keys)
    {
        Time.frameCount++; Time.unscaledTime += .02f;
        Input.Down.Clear(); Input.Held.Clear();
        foreach (KeyCode key in keys) { Input.Down.Add(key); Input.Held.Add(key); }
    }
    private static PersonalAudioControls Start(out PersonalAudioWindow window, out ConfigEntry<KeyboardShortcut> shortcut)
    {
        Player.m_localPlayer = new Player(); ZNet.instance = new ZNet(); ZInput.ResetFixture();
        InventoryGui.Visible = false; InventoryGui.HiddenUntil = 0; StoreGui.Visible = Hud.Visible = PlayerCustomizaton.Visible = false;
        Menu.Visible = UnifiedPopup.Visible = TextInput.Visible = Console.Visible = ZInput.s_IsRebindActive = false;
        Chat.instance = null; Minimap.instance = null; UnityEngine.EventSystems.EventSystem.current = null;
        Frame(); window = new PersonalAudioWindow(); shortcut = new ConfigEntry<KeyboardShortcut> { Value = new KeyboardShortcut(KeyCode.F8, KeyCode.LeftControl) };
        var controls = new PersonalAudioControls(new Plugin(), shortcut, window); controls.Tick(); return controls;
    }
    private static int Main()
    {
        try
        {
            PersonalAudioWindow window; ConfigEntry<KeyboardShortcut> shortcut;
            foreach (bool fixedTick in new[] { false, true })
            {
                using (var controls = Start(out window, out shortcut))
                {
                    ZInput.CachedButtons = true;
                    Frame(KeyCode.F8, KeyCode.RightControl); Input.Down.Clear();
                    ZInput.Button("GP").Press(); ZInput.Button("Forward").Press(); ZInput.Button("JoyButtonB").Press();
                    if (fixedTick) ZInput.FixedUpdate(.02f); else ZInput.Update(.02f);
                    Check(!ZInput.Button("GP").Pressed && !ZInput.Button("GP").FixedPressed, "native phase drains guardian action before consumer");
                    Check(ZInput.Button("Forward").Held, "native edge drain preserves physical movement hold");
                    Check(ZInput.GetButtonDown("JoyButtonB"), "controller cancel is preserved");
                    Check(!Player.m_localPlayer.TakeInput() && !Player.m_localPlayer.StartGuardianPower(), "local native input and direct guardian call are gated");
                    var other = new Player(); Check(other.StartGuardianPower(), "remote player input remains unaffected");
                    controls.Tick(); Check(!window.IsVisible, "opening deferred until next rendered frame");
                    Time.frameCount++; Time.unscaledTime += .02f; controls.Tick(); Check(window.IsVisible && window.Opens == 1, "Ctrl+F8 opens without any radio item or inventory");
                    window.Hide(); Input.Held.Remove(KeyCode.RightControl); Check(!ZInput.GetButtonDown("GP"), "closing while main key held cannot leak guardian action");
                    Input.Held.Clear(); Input.Down.Clear(); ZInput.Button("GP").Release(); ZInput.Update(.02f);
                    Check(!ZInput.GetButtonUp("GP") && !ZInput.Button("GP").FixedReleased, "captured release is drained in both phases");
                    Frame(); Check(Player.m_localPlayer.TakeInput(), "ordinary input restores after key-up frame");
                    Frame(KeyCode.F); ZInput.Button("GP").Press(); ZInput.Update(.02f); Player.m_localPlayer.NativeUpdate();
                    Check(Player.m_localPlayer.GuardianStarts == 1, "fresh unrelated plain F still activates ordinary boss ability");
                }
            }
            using (var controls = Start(out window, out shortcut))
            {
                Frame(KeyCode.F8, KeyCode.LeftControl); Check(!ZInput.GetButtonDown("GP"), "first consumer claims opening stroke before plugin update");
                Time.frameCount++; controls.Tick(); Check(window.IsVisible, "first-consumer capture opens modal");
                Frame(); controls.Tick(); Frame(KeyCode.F8, KeyCode.RightControl); controls.Tick();
                Check(!window.IsVisible && window.Opens == 1, "same shortcut closes visible personal window");
                Check(!ZInput.GetButtonDown("GP"), "closing shortcut remains consumed");
            }
            using (var controls = Start(out window, out shortcut))
            {
                shortcut.Value = new KeyboardShortcut(KeyCode.F11, KeyCode.RightControl, KeyCode.RightShift);
                Check(PersonalAudioControls.Label(shortcut.Value) == "Ctrl+Shift+F11", "actual rebind caption uses normalized modifier order");
                Frame(KeyCode.F8, KeyCode.LeftControl); Check(ZInput.GetButtonDown("GP"), "old binding no longer intercepts gameplay");
                Frame(); controls.Tick(); Frame(KeyCode.F11, KeyCode.LeftControl); Check(ZInput.GetButtonDown("GP"), "missing configured modifier does not capture");
                Frame(); controls.Tick(); Frame(KeyCode.F11, KeyCode.LeftControl, KeyCode.LeftShift); Check(!ZInput.GetButtonDown("GP"), "rebound chord accepts opposite modifier sides");
                Time.frameCount++; controls.Tick(); Check(window.IsVisible, "rebinding takes effect without restart");
            }
            foreach (KeyCode extra in new[] { KeyCode.LeftShift, KeyCode.RightAlt, KeyCode.LeftCommand })
            {
                using (var controls = Start(out window, out shortcut))
                { Frame(KeyCode.F8, KeyCode.LeftControl, extra); Check(ZInput.GetButtonDown("GP"), "unconfigured extra modifier leaves gameplay alone"); controls.Tick(); Check(!window.IsVisible, "extra modifier cannot open personal modal"); }
            }
            using (var controls = Start(out window, out shortcut))
            {
                shortcut.Value = new KeyboardShortcut(KeyCode.None);
                Check(PersonalAudioControls.Label(shortcut.Value) == "", "unbound shortcut has no invented caption");
                Frame(KeyCode.F8, KeyCode.LeftControl); Check(ZInput.GetButtonDown("GP"), "unbound controls leave native input untouched");
            }
            using (var controls = Start(out window, out shortcut))
            {
                Menu.Visible = true; Frame(KeyCode.F8, KeyCode.LeftControl); ZInput.Update(.02f);
                Menu.Visible = false; Time.frameCount++; controls.Tick(); Check(!window.IsVisible, "closing rejected UI while keys held cannot invent fresh shortcut");
                Frame(); controls.Tick(); Frame(KeyCode.F8, KeyCode.LeftControl); ZInput.Update(.02f); Time.frameCount++; controls.Tick();
                Check(window.IsVisible, "fresh press works after rejected held stroke releases");
            }
            using (var controls = Start(out window, out shortcut))
            {
                Frame(KeyCode.F11, KeyCode.LeftControl); shortcut.Value = new KeyboardShortcut(KeyCode.F11, KeyCode.LeftControl); ZInput.Update(.02f);
                Time.frameCount++; controls.Tick(); Check(!window.IsVisible, "rebind to held key requires release");
                Frame(); controls.Tick(); Frame(KeyCode.F11, KeyCode.RightControl); ZInput.Update(.02f); Time.frameCount++; controls.Tick(); Check(window.IsVisible, "rebound fresh physical press opens correctly");
            }
            Action[] blocked = {
                () => Menu.Visible = true, () => TextInput.Visible = true, () => InventoryGui.Visible = true,
                () => StoreGui.Visible = true, () => Hud.Visible = true, () => PlayerCustomizaton.Visible = true,
                () => UnifiedPopup.Visible = true, () => Console.Visible = true, () => ZInput.s_IsRebindActive = true,
                () => Chat.instance = new Chat { Focus = true }, () => Minimap.instance = new Minimap { m_mode = Minimap.MapMode.Large },
                () => Player.m_localPlayer.Dead = true, () => Player.m_localPlayer.Teleporting = true,
                () => Player.m_localPlayer.Sleeping = true, () => Player.m_localPlayer.Cutscene = true
            };
            foreach (var block in blocked)
            {
                using (var controls = Start(out window, out shortcut))
                { block(); Frame(KeyCode.F8, KeyCode.LeftControl); Check(ZInput.GetButtonDown("GP"), "disallowed context is not claimed"); Time.frameCount++; controls.Tick(); Check(!window.IsVisible, "disallowed context stays closed"); }
            }
            foreach (bool worldChanged in new[] { false, true })
            {
                using (var controls = Start(out window, out shortcut))
                {
                    Frame(KeyCode.F8, KeyCode.LeftControl); ZInput.GetButtonDown("GP");
                    if (worldChanged) ZNet.instance = new ZNet(); else Player.m_localPlayer = new Player();
                    Time.frameCount++; controls.Tick(); Check(!window.IsVisible, "pending personal modal cannot migrate to another player or world");
                }
            }
            System.Console.WriteLine("PASS " + checks + " personal radio shortcut/gate checks (actual controls/capture/cache with simulated native boundaries)"); return 0;
        }
        catch (Exception error) { System.Console.Error.WriteLine(error); return 1; }
    }
}
