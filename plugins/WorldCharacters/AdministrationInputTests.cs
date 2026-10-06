using System;
using BepInEx.Configuration;
using UnityEngine;
using ValheimModPack.WorldCharacters;

internal static class AdministrationInputTests
{
    private static int checks;
    private static void Check(bool value, string reason) { checks++; if (!value) throw new Exception(reason); }
    private static void Frame(params KeyCode[] keys)
    {
        Time.frameCount++; Time.unscaledTime += .02f; Input.Held.Clear(); Input.Down.Clear();
        foreach (KeyCode key in keys) { Input.Held.Add(key); Input.Down.Add(key); }
    }
    private static Plugin Start()
    { Frame(); ZInput.Reset(); Player.m_localPlayer = new Player(); ZNet.instance = new ZNet(); return new Plugin(); }
    private static void Next(Plugin plugin) { Time.frameCount++; Time.unscaledTime += .02f; Input.Down.Clear(); plugin.Tick(); }
    private static int Main()
    {
        try
        {
            foreach (bool fixedInput in new[] { false, true })
            {
                Plugin plugin = Start(); Frame(KeyCode.F6, KeyCode.RightControl); Input.Down.Clear();
                ZInput.Button("GP").Press(); ZInput.Button("Forward").Press(); ZInput.Button("JoyButtonB").Press();
                if (fixedInput) ZInput.FixedUpdate(.02f); else ZInput.Update(.02f);
                Check(plugin.Blocks() && !plugin.administrationWindow.IsVisible, "Physical opening stroke captured before Plugin.Update; UI deferred");
                Check(!ZInput.Button("GP").m_pressedDynamic && !ZInput.Button("GP").m_pressedFixed, "Opening drains cached powers in both native input phases");
                Check(ZInput.Button("Forward").m_heldDynamic, "Held movement is retained for after close");
                Check(ZInput.GetButtonDown("JoyButtonB") && ZInput.Button("JoyButtonB").m_pressedFixed, "Controller Cancel preserved in both native input phases");
                Check(!Player.m_localPlayer.StartGuardianPower() && Player.m_localPlayer.GuardianStarts == 0, "Direct local power blocked");
                var other = new Player(); Check(other.StartGuardianPower() && other.GuardianStarts == 1, "Another player unaffected");
                Next(plugin); Check(plugin.administrationWindow.IsVisible && plugin.administrationWindow.Opens == 1, "Right Ctrl opens exactly once after deferred construction");
                Check(!new PlayerController().TakeInput() && !ZInput.GetButton("GP"), "Modal blocks controller and held game actions");
                plugin.administrationWindow.Hide(); Input.Held.Remove(KeyCode.RightControl);
                Check(plugin.Blocks() && !Player.m_localPlayer.StartGuardianPower(), "Releasing modifier first cannot leak main key");
                Input.Held.Clear(); ZInput.Button("GP").Release(); ZInput.Update(.02f);
                Check(plugin.Blocks() && !ZInput.GetButtonUp("GP"), "Release frame consumed");
                Next(plugin); Check(!plugin.Blocks() && new PlayerController().TakeInput(), "Input restores after release frame");
                Check(ZInput.GetButton("Forward"), "Movement resumes without releasing physical movement key");
                plugin.End();
            }
            foreach (bool firstController in new[] { false, true })
            {
                Plugin plugin = Start(); Frame(KeyCode.F6, KeyCode.LeftControl); ZInput.Button("GP").Press();
                Check(!(firstController ? new PlayerController().TakeInput() : ZInput.GetButtonDown("GP")), "First native consumer captures before plugin Update");
                Next(plugin); Check(plugin.administrationWindow.IsVisible, "Captured native consumer opens modal"); plugin.End();
            }
            {
                Plugin plugin = Start(); plugin.OtherModal = true; Frame(KeyCode.F6, KeyCode.LeftControl); ZInput.Update(.02f);
                plugin.OtherModal = false; Next(plugin); Check(!plugin.administrationWindow.IsVisible && !plugin.Blocks(), "Closing competing UI with held key does not open a delayed modal");
                Frame(); plugin.Tick(); Frame(KeyCode.F6, KeyCode.LeftControl); plugin.Tick(); Next(plugin);
                Check(plugin.administrationWindow.IsVisible, "Fresh stroke opens after competing UI closes"); plugin.End();
            }
            {
                Plugin plugin = Start(); Frame(KeyCode.F6, KeyCode.LeftControl, KeyCode.LeftShift); plugin.Tick(); Input.Held.Remove(KeyCode.LeftShift); Next(plugin);
                Check(!plugin.administrationWindow.IsVisible && !plugin.Blocks(), "Extra modifiers rejected even when released while main key remains held"); plugin.End();
            }
            {
                Plugin plugin = Start(); Frame(KeyCode.F, KeyCode.LeftControl); plugin.Binding.Value = new KeyboardShortcut(KeyCode.F, KeyCode.LeftControl); ZInput.Update(.02f); Next(plugin);
                Check(!plugin.administrationWindow.IsVisible, "Binding to a currently held key requires release");
                Frame(); plugin.Tick(); Frame(KeyCode.F, KeyCode.RightControl); Input.Down.Clear(); ZInput.Button("GP").Press(); ZInput.FixedUpdate(.02f);
                Check(!Player.m_localPlayer.StartGuardianPower(), "Rebound CtrlF suppresses power before plugin Update"); Next(plugin);
                Check(plugin.administrationWindow.IsVisible, "Rebound shortcut opens on fresh physical edge"); plugin.End();
            }
            {
                Plugin plugin = Start(); Frame(KeyCode.F6, KeyCode.LeftControl); plugin.Tick(); ZNet.instance = new ZNet(); Next(plugin);
                Check(!plugin.administrationWindow.IsVisible && !plugin.Blocks(), "World/network switch cancels queued open"); plugin.End();
            }
            {
                Plugin plugin = Start(); Frame(KeyCode.F6, KeyCode.LeftControl); plugin.Tick(); Player.m_localPlayer = new Player(); Next(plugin);
                Check(!plugin.administrationWindow.IsVisible && !plugin.Blocks(), "Local player replacement cancels queued open"); plugin.End();
            }
            {
                Plugin plugin = Start(); plugin.Allowed = false; Frame(KeyCode.F6, KeyCode.LeftControl); plugin.Tick(); Next(plugin);
                Check(!plugin.administrationWindow.IsVisible && !plugin.Blocks(), "Guest cannot claim administration shortcut"); plugin.End();
            }
            {
                Plugin plugin = Start(); Frame(KeyCode.F6, KeyCode.LeftControl); plugin.Tick(); Frame(KeyCode.Escape); plugin.Tick();
                Check(!plugin.administrationWindow.IsVisible, "Escape cancels pending open"); plugin.End();
            }
            {
                Plugin plugin = Start(); Frame(KeyCode.F6, KeyCode.LeftControl); plugin.Tick(); plugin.SessionReset(); Next(plugin);
                Check(!plugin.administrationWindow.IsVisible && !plugin.Blocks(), "Explicit session reset removes input capture"); plugin.End();
            }
            Console.WriteLine("PASS: " + checks + " World Characters administration input assertions."); return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL after " + checks + " assertions: " + error); return 1; }
    }
}
