using System;
using System.Reflection;
using HarmonyLib;
using ValheimModPack.PinRemoval;
public static class HostTests
{
    private static int checks;
    private static void Check(bool value, string message)
    { checks++; if (!value) throw new Exception(message); }
    private static object Call(string name, object instance, params object[] args)
    { return typeof(Plugin).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).Invoke(instance, args); }
    private static void Click(Minimap map)
    { Check((bool)Call("BeforeRemoveUnderPointer", null, map) == false, "Never fall through to vanilla removal"); }
    public static void Main()
    {
        var plugin = new Plugin(); Call("Awake", plugin);
        Check(plugin.Logger.Errors.Count == 0, "Startup succeeds");
        Check(Harmony.Patched.Contains("RemovePinUnderPointer") && Harmony.Patched.Contains("OnDestroy"), "Deletion and logout hooks installed");
        var map = new Minimap(); Minimap.instance = map;
        var player = new Player(); Player.m_localPlayer = player;
        var a = new Minimap.PinData(); var b = new Minimap.PinData(); map.Add(a); map.Add(b);
        var view = WoodDialogView.Last;
        Click(map); Check(!view.IsVisible, "Blank map does not open dialog");
        map.Cursor = a; a.m_save = false; Click(map);
        Check(!view.IsVisible, "Dynamic unsaved marker untouched"); a.m_save = true;
        Click(map); Check(view.IsVisible && map.Deleted == 0 && map.NameInputClosed == 1, "Right-click opens dialog and closes rename UI");
        map.Cursor = b; Click(map); view.Yes();
        Check(!map.Contains(a) && map.Contains(b) && map.Deleted == 1, "Mouse movement or repeated click cannot retarget deletion");
        Click(map); UnityEngine.Input.Escape = true; Call("Update", plugin); UnityEngine.Input.Escape = false;
        view.Yes(); Check(map.Contains(b) && !view.IsVisible, "Escape preserves pin");
        Click(map); ZInput.Cancel = true; Call("Update", plugin); ZInput.Cancel = false;
        Check(!view.IsVisible && map.Contains(b), "Controller cancel preserves pin");
        Click(map); UnifiedPopup.Visible = true; Call("Update", plugin);
        Check(!view.IsVisible && UnifiedPopup.Visible, "Other popup is not closed");
        Click(map); Check(!view.IsVisible, "Do not open over another popup"); UnifiedPopup.Visible = false;
        Click(map); map.m_mode = Minimap.MapMode.Small; Call("Update", plugin); view.Yes();
        Check(!view.IsVisible && map.Contains(b), "Closing map cancels request"); map.m_mode = Minimap.MapMode.Large;
        Click(map); player.Dead = true; Call("Update", plugin);
        Check(!view.IsVisible && map.Contains(b), "Death cancels request"); player.Dead = false;
        Click(map); Call("BeforePlayerDestroyed", null, new Player());
        Check(view.IsVisible, "Remote player destruction must not close local dialog");
        Call("BeforePlayerDestroyed", null, player); view.Yes();
        Check(!view.IsVisible && map.Contains(b), "Local logout cancels before destruction");
        Click(map); Player.m_localPlayer = new Player(); view.Yes();
        Check(map.Contains(b), "Final confirmation rechecks player identity"); Player.m_localPlayer = player;
        Click(map); Minimap.instance = new Minimap(); Call("Update", plugin);
        Check(!view.IsVisible && map.Contains(b), "World/map replacement cancels request"); Minimap.instance = map;
        Click(map); view.IsVisible = false; Call("Update", plugin); view.Yes();
        Check(map.Contains(b), "Externally destroyed UI cannot confirm");
        map.FailSelection = true; Click(map);
        Check(!view.IsVisible && map.Contains(b) && plugin.Logger.Errors.Count == 1, "Selection error blocks removal"); map.FailSelection = false;
        Click(map); Call("OnDisable", plugin); view.Yes();
        Check(!view.IsVisible && map.Contains(b), "Disabling cancels request");
        plugin.isActiveAndEnabled = false; Click(map); Check(!view.IsVisible, "Disabled plugin cannot open UI");
        Call("OnDestroy", plugin); Check(Harmony.Patched.Count == 0, "Unload removes patches");
        AccessTools.MissingMethod = "GetClosestPinToCursor";
        plugin = new Plugin(); Call("Awake", plugin); view = WoodDialogView.Last;
        Check(Harmony.Patched.Contains("RemovePinUnderPointer"), "Selection API failure retains deletion guard");
        Click(map); Check(!view.IsVisible && map.Contains(b), "Failed startup does not delete");
        Call("Update", plugin); Call("Update", plugin);
        Check(player.Warnings == 1 && plugin.Logger.Errors.Count == 1, "Startup failure reported once");
        Call("OnDestroy", plugin); AccessTools.MissingMethod = null;
        Console.WriteLine("OK: " + checks + " real-plugin integration assertions with host doubles. Unity rendering not tested.");
    }
}
