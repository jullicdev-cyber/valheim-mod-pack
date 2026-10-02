using System;
using System.Collections.Generic;
using System.Reflection;
using ValheimModPack.PinRemoval;

internal static class MapActionTests
{
    private static int checks, renamed, deletionRequested, errors;
    private static FieldInfo pins = typeof(Minimap).GetField("m_pins", BindingFlags.Instance | BindingFlags.NonPublic);
    private static void Check(bool value, string description)
    { if (!value) throw new Exception(description); checks++; }
    private static void Reset()
    {
        Minimap.instance = new Minimap(); Player.m_localPlayer = new Player(); ZNet.instance = new ZNet();
        TextInput.instance = null; TextInput.Visible = false; Chat.instance = null; Menu.Visible = false;
        Console.Visible = false; UnifiedPopup.Visible = false; InventoryGui.Visible = false;
        UnityEngine.Input.Escape = false; ZInput.Cancel = false; PinActionMenuView.FailShow = PinActionMenuView.FailHide = false;
        renamed = deletionRequested = errors = 0;
    }
    private static Minimap.PinData Pin(Minimap.PinType type, bool save)
    { var result = new Minimap.PinData { m_type = type, m_save = save, m_name = "pin" }; Minimap.instance.Pins.Add(result); return result; }
    private static PinActionController Actions()
    { return new PinActionController(pins, p => p.m_name, p => { Check(!PinActionMenuView.Last.IsVisible, "release lease before rename"); renamed++; },
        p => { Check(!PinActionMenuView.Last.IsVisible, "release lease before deletion confirmation"); deletionRequested++; }, e => errors++); }
    private static void ChoiceTests()
    {
        Reset(); var pin = Pin(Minimap.PinType.Icon1, true); var menu = Actions();
        menu.Show(Minimap.instance, pin); Check(menu.IsOpen, "saved pin opens actions");
        Check(renamed == 0 && deletionRequested == 0 && Minimap.instance.Pins.Contains(pin), "opening never alters pin");
        Action rename = PinActionMenuView.Last.Rename; rename(); rename();
        Check(renamed == 1 && !menu.IsOpen && Minimap.instance.Pins.Contains(pin), "rename once and retains pin");
        menu.Show(Minimap.instance, pin); Action delete = PinActionMenuView.Last.Delete; delete(); delete();
        Check(deletionRequested == 1 && Minimap.instance.Pins.Contains(pin), "delete requests confirmation only once");
        menu.Show(Minimap.instance, pin); rename(); Check(menu.IsOpen && renamed == 1, "old callback cannot close new request");
        PinActionMenuView.Last.Cancel(); Check(!menu.IsOpen, "cancel closes actions"); menu.Dispose();

        foreach (Action mutate in new Action[] {
            () => ZNet.instance.World++, () => Player.m_localPlayer.Id++, () => ZNet.instance = new ZNet(),
            () => Player.m_localPlayer = new Player(), () => Minimap.instance = new Minimap(),
            () => Player.m_localPlayer.Safe = false, () => Minimap.instance.m_mode = Minimap.MapMode.Small,
            () => Minimap.instance.Pins.Clear(), () => pin.m_save = false,
            () => Menu.Visible = true, () => InventoryGui.Visible = true, () => Chat.instance = new Chat { Focus = true },
            () => TextInput.instance = new TextInput { m_panel = new UnityEngine.GameObject() } })
        {
            Reset(); pin = Pin(Minimap.PinType.Icon1, true); menu = Actions(); menu.Show(Minimap.instance, pin);
            Action callback = PinActionMenuView.Last.Delete; mutate(); callback();
            Check(deletionRequested == 0 && !menu.IsOpen, "stale/dead/modal context cannot delete"); menu.Dispose();
        }
        Reset(); pin = Pin(Minimap.PinType.Icon1, true); menu = Actions(); menu.Show(Minimap.instance, pin);
        TextInput.Visible = true; menu.Tick(true); Check(menu.IsOpen, "own GUI lease does not invalidate menu");
        menu.Tick(false); Check(!menu.IsOpen, "external controller exclusion closes menu"); menu.Dispose();
        Reset(); menu = Actions(); pin = Pin(Minimap.PinType.Icon1, false); menu.Show(Minimap.instance, pin);
        Check(!menu.IsOpen, "unsaved system pins excluded"); pin.m_save = true; TextInput.Visible = true;
        menu.Show(Minimap.instance, pin); Check(!menu.IsOpen, "foreign input lease blocks opening");
        TextInput.Visible = false; menu.Show(Minimap.instance, pin); UnityEngine.Input.Escape = true; menu.Tick(true);
        Check(!menu.IsOpen, "Escape closes actions"); UnityEngine.Input.Escape = false;
        menu.Show(Minimap.instance, pin); ZInput.Cancel = true; menu.Tick(true); Check(!menu.IsOpen, "controller Cancel closes actions"); menu.Dispose();
        Reset(); menu = Actions(); pin = Pin(Minimap.PinType.Icon1, true); PinActionMenuView.FailShow = true;
        menu.Show(Minimap.instance, pin); Check(!menu.IsOpen && !PinActionMenuView.Last.IsVisible && errors == 1, "failed view cleans request and retains pin"); menu.Dispose();
        Reset(); menu = Actions(); pin = Pin(Minimap.PinType.Icon1, true); menu.Show(Minimap.instance, pin);
        PinActionMenuView.FailHide = true; PinActionMenuView.Last.Delete();
        Check(!menu.IsOpen && deletionRequested == 0 && errors >= 1 && Minimap.instance.Pins.Contains(pin), "input release failure refuses action and is contained");
        menu.Dispose(); PinActionMenuView.FailHide = false;
    }
    private static DeathPinController Deaths(PinHistoryController history)
    { return new DeathPinController(pins, history, e => errors++); }
    private static void DeathTests()
    {
        Reset(); var one = Pin(Minimap.PinType.Death, true); var two = Pin(Minimap.PinType.Death, true); two.m_ownerID = 99;
        var unsaved = Pin(Minimap.PinType.Death, false); var normal = Pin(Minimap.PinType.Icon3, true); var bed = Pin(Minimap.PinType.Bed, false);
        var history = new PinHistoryController(); var death = Deaths(history); death.Tick(true); death.Open();
        Check(death.IsOpen && WoodDialogView.Last.Count == "2" && history.Calls == 0, "confirm count is saved local death pins only");
        Action confirm = WoodDialogView.Last.Confirm; confirm(); confirm();
        Check(history.Calls == 1 && !death.IsOpen && !Minimap.instance.Pins.Contains(one) && !Minimap.instance.Pins.Contains(two), "bulk death removal commits once");
        Check(Minimap.instance.Pins.Contains(unsaved) && Minimap.instance.Pins.Contains(normal) && Minimap.instance.Pins.Contains(bed), "other pins and dynamic pins retained"); death.Dispose();
        Reset(); history = new PinHistoryController(); death = Deaths(history); death.Tick(true); death.Open();
        Check(!death.IsOpen && history.Calls == 0 && Player.m_localPlayer.Messages == 1, "empty map reports no death pins"); death.Dispose();
        foreach (Action<Minimap.PinData> mutate in new Action<Minimap.PinData>[] {
            p => ZNet.instance.World++, p => Player.m_localPlayer.Id++, p => ZNet.instance = new ZNet(),
            p => Player.m_localPlayer = new Player(), p => Minimap.instance = new Minimap(),
            p => p.m_type = Minimap.PinType.Icon0, p => p.m_save = false, p => Minimap.instance.Pins.Remove(p),
            p => Player.m_localPlayer.Safe = false, p => Minimap.instance.m_mode = Minimap.MapMode.Small })
        {
            Reset(); one = Pin(Minimap.PinType.Death, true); history = new PinHistoryController(); death = Deaths(history);
            death.Tick(true); death.Open(); confirm = WoodDialogView.Last.Confirm; mutate(one); confirm();
            Check(history.Calls == 0 && !death.IsOpen, "bulk confirmation invalidated by changed pin/player/world/map"); death.Dispose();
        }
        Reset(); one = Pin(Minimap.PinType.Death, true); history = new PinHistoryController { Fail = true }; death = Deaths(history);
        death.Tick(true); death.Open(); WoodDialogView.Last.Confirm();
        Check(history.Calls == 1 && Minimap.instance.Pins.Contains(one) && errors == 1 && !death.IsOpen, "history failure preserves all pins"); death.Dispose();
        Reset(); one = Pin(Minimap.PinType.Death, true); history = new PinHistoryController(); death = Deaths(history);
        death.Tick(true); death.Open(); TextInput.Visible = true; death.Tick(true);
        Check(death.IsOpen, "own confirmation lease is valid"); WoodDialogView.Last.Cancel();
        Check(!death.IsOpen && history.Calls == 0 && Minimap.instance.Pins.Contains(one), "bulk cancel preserves pins");
        TextInput.Visible = false; death.Tick(true); death.Open(); confirm = WoodDialogView.Last.Confirm;
        death.Tick(false); confirm(); Check(history.Calls == 0 && !death.IsOpen, "external close invalidates pending confirmation");
        death.Tick(true); death.Open(); var later = Pin(Minimap.PinType.Death, true); WoodDialogView.Last.Confirm();
        Check(history.Deleted.Count == 1 && Minimap.instance.Pins.Contains(later), "snapshot excludes pins added after request"); death.Dispose();
        Reset(); one = Pin(Minimap.PinType.Death, true); history = new PinHistoryController(); death = Deaths(history);
        int shortcut = 0; death.OpenShortcut = () => ++shortcut == 1; death.Tick(true);
        Check(death.IsOpen && shortcut == 1 && history.Calls == 0, "hotkey opens confirmation instead of deleting");
        confirm = WoodDialogView.Last.Confirm; death.Dispose(); confirm();
        Check(history.Calls == 0 && Minimap.instance.Pins.Contains(one), "disposal invalidates callbacks");
        Reset(); for (int i = 0; i < 500; i++) Pin(Minimap.PinType.Death, true);
        normal = Pin(Minimap.PinType.Icon1, true); history = new PinHistoryController(); death = Deaths(history);
        death.Tick(true); death.Open(); for (int i = 0; i < 20; i++) death.Tick(true);
        Check(death.IsOpen && WoodDialogView.Last.Count == "500", "large batch remains valid without altering map");
        WoodDialogView.Last.Confirm();
        Check(history.Calls == 1 && history.Deleted.Count == 500 && Minimap.instance.Pins.Count == 1 && Minimap.instance.Pins.Contains(normal), "five hundred deaths removed in one history transaction"); death.Dispose();
    }
    public static int Main()
    {
        try { ChoiceTests(); DeathTests(); System.Console.WriteLine("OK: " + checks + " action/death-pin controller checks"); return 0; }
        catch (Exception error) { System.Console.Error.WriteLine(error); return 1; }
    }
}
