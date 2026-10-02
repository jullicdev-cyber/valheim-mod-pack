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
    private static void RawClick(Minimap map)
    { Check((bool)Call("BeforeRemoveUnderPointer", null, map) == false, "Never fall through to vanilla removal"); }
    private static void Click(Minimap map)
    {
        RawClick(map);
        if (PinActionController.Last.IsOpen) PinActionController.Last.ChooseDelete();
    }
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
        RawClick(map);
        Check(PinActionController.Last.IsOpen && !view.IsVisible && map.Deleted == 0 && map.NameInputClosed == 1,
            "Right-click opens Rename/Delete/Cancel without deleting or confirming");
        Call("Update", plugin);
        Check(PinActionController.Last.IsOpen && !QuickPinController.Last.LastAllowed && !PinHistoryController.Last.LastAllowed,
            "Choice menu survives its own modal guards and excludes other pin windows");
        PinActionController.Last.ChooseDelete();
        Check(view.IsVisible && !PinActionController.Last.IsOpen && map.Deleted == 0, "Delete choice opens existing separate confirmation");
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
        Click(map); Chat.instance = new Chat { Focus = true }; view.Yes();
        Check(!view.IsVisible && map.Contains(b), "Confirmation rejects newly focused chat before next Update"); Chat.instance = null;
        Click(map); global::Console.Visible = true; Call("Update", plugin); view.Yes();
        Check(!view.IsVisible && map.Contains(b), "Console opening cancels pending deletion"); global::Console.Visible = false;
        Click(map); InventoryGui.Visible = true; Call("Update", plugin); view.Yes();
        Check(!view.IsVisible && map.Contains(b), "Inventory opening cancels pending deletion"); InventoryGui.Visible = false;
        Click(map); ++ZNet.instance.World; view.Yes();
        Check(!view.IsVisible && map.Contains(b), "Changed world UID rejects deletion even if player and map objects were reused");
        Click(map); ZNet.instance = new ZNet { World = ZNet.instance.World }; view.Yes();
        Check(!view.IsVisible && map.Contains(b), "Replaced network session rejects deletion in an identically numbered world");
        Click(map); ++player.Id; view.Yes();
        Check(!view.IsVisible && map.Contains(b), "Changed character ID rejects deletion even if player object was reused");
        RawClick(map); PinActionController.Last.ChooseRename();
        Check(QuickPinController.Last.Renamed == 1 && map.Contains(b) && !view.IsVisible && !PinActionController.Last.IsOpen,
            "Rename choice delegates to existing quick-pin rename window without confirmation or deletion");
        Call("Update", plugin); Check(QuickPinController.Last.IsBusy, "Rename window survives its own modal guard");
        QuickPinController.Last.Close();
        RawClick(map); ++ZNet.instance.World; PinActionController.Last.ChooseDelete();
        Check(!view.IsVisible && map.Contains(b), "Stale choice cannot start a new deletion request after world UID changes");
        Call("Update", plugin); DeathPinController.Last.Open(); Call("Update", plugin);
        Check(DeathPinController.Last.IsOpen && !QuickPinController.Last.LastAllowed && !PinHistoryController.Last.LastAllowed
            && !PinSuggestionController.Last.LastAllowed, "Death confirmation survives its own guard and excludes other actions");
        Check(DeathPinController.Last.ShortcutLabel() == "Ctrl+Shift+Delete", "Death launcher receives current configurable hotkey label");
        Call("BeforePlayerDestroyed", null, new Player()); Check(DeathPinController.Last.IsOpen, "Remote destruction preserves death dialog");
        Call("BeforePlayerDestroyed", null, player); Check(!DeathPinController.Last.IsOpen, "Local destruction closes death dialog");
        PinSuggestionController.Last.IsVisible = true; Call("Update", plugin);
        Check(PinSuggestionController.Last.IsVisible && PinSuggestionController.Last.LastAllowed && QuickPinController.Last.LastAllowed,
            "Suggestion HUD is non-modal and does not exclude ordinary quick presets");
        TextInput.ForeignVisible = true; Click(map);
        Check(!view.IsVisible, "Foreign Jotunn input window blocks opening deletion prompt"); TextInput.ForeignVisible = false;
        Click(map); Call("OnDisable", plugin); view.Yes();
        Check(!view.IsVisible && map.Contains(b), "Disabling cancels request");
        Check(!PinSuggestionController.Last.IsVisible && !DeathPinController.Last.IsOpen && !PinActionController.Last.IsOpen,
            "Disabling closes all newly integrated components");
        plugin.isActiveAndEnabled = false; Click(map); Check(!view.IsVisible, "Disabled plugin cannot open UI");
        Call("OnDestroy", plugin); Check(Harmony.Patched.Count == 0, "Unload removes patches");
        AccessTools.MissingMethod = "GetClosestPinToCursor";
        plugin = new Plugin(); Call("Awake", plugin); view = WoodDialogView.Last;
        Check(Harmony.Patched.Contains("RemovePinUnderPointer"), "Selection API failure retains deletion guard");
        Click(map); Check(!view.IsVisible && map.Contains(b), "Failed startup does not delete");
        Call("Update", plugin); Call("Update", plugin);
        Check(player.Warnings == 1 && plugin.Logger.Errors.Count == 1, "Startup failure reported once");
        Call("OnDestroy", plugin); AccessTools.MissingMethod = null;
        System.Console.WriteLine("OK: " + checks + " real-plugin integration assertions with host doubles. Unity rendering not tested.");
    }
}
