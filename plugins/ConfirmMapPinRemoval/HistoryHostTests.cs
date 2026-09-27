using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ValheimModPack.PinRemoval;
public static class HistoryHostTests
{
    private static int checks;
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    private static object Call(string name, object instance, params object[] args)
    { return typeof(PinHistoryController).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance).Invoke(instance, args); }
    private static void Open(PinHistoryController controller)
    { Input.Down.Add(KeyCode.H); Input.Held.Add(KeyCode.LeftControl); controller.Tick(true); Input.Down.Clear(); Input.Held.Clear(); }
    private static Minimap.PinData Add(Minimap map, string name, float x)
    { return map.AddPin(new Vector3(x, 2, 3), Minimap.PinType.Icon1, name, true, true, 0, Splatform.PlatformUserID.None); }
    public static void Main()
    {
        string folder = Path.Combine(Path.GetTempPath(), "vmp-pin-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); BepInEx.Paths.GameRootPath = folder;
        var errors = new List<Exception>(); PinHistoryController controller = null;
        try
        {
            controller = new PinHistoryController(new Harmony(), AccessTools.Field(typeof(Minimap), "m_pins"), errors.Add);
            controller.Tick(true); Check(!controller.IsOpen, "No history without world/player");
            var map = new Minimap(); Minimap.instance = map; var owner = new Player(); Player.m_localPlayer = owner; ZNet.instance = new ZNet();
            var pin = Add(map, "Тест", 1); controller.Tick(true); Open(controller);
            var view = PinHistoryWindow.Last; Check(view.IsVisible && view.Rows.Count == 0, "Ctrl+H opens empty history on large map");
            view.IsVisible = false; controller.Tick(true);
            Check(!view.OwnsInputBlock, "Controller releases vanished history panel input while map context stays valid");
            Open(controller); Check(view.IsVisible && view.OwnsInputBlock, "History can reopen after vanished panel cleanup");
            view.Tab(false); Check(view.Rows.Count == 1 && view.Rows[0].Details.Contains("неизвестно") && view.Rows[0].Details.Contains("неизвестен"), "Existing pin never gets fabricated author/date");
            view.Cancel(); Check(!controller.IsOpen, "Close callback closes owned window");
            var created = Add(map, "Новая", 4);
            Call("AfterCreated", null, map, created);
            Open(controller); view.Tab(false);
            Check(view.Rows[0].Title == "Новая" && view.Rows[0].Details.Contains("Скальд") && !view.Rows[0].Details.Contains("неизвестно"), "Actual creation hook records creator and UTC time");
            controller.Close(); controller.Remove(map, created);
            Check(!map.Contains(created), "Confirmed removal reaches game after journal save");
            Open(controller); Check(view.Rows.Count == 1 && view.Rows[0].Enabled, "Deleted pin appears recoverable");
            var restore = view.Rows[0].Activate; int added = map.Added; restore(); restore();
            Check(map.Added == added + 1 && !view.Rows[0].Enabled && view.Rows[0].ActionLabel == "Восстановлена", "Duplicate clicks add exactly one pin and disable recovery");
            controller.Close(); map.FailRemove = true;
            try { controller.Remove(map, pin); } catch { }
            map.FailRemove = false; Open(controller);
            Check(view.Rows[0].ActionLabel == "Уже на карте" && !view.Rows[0].Enabled && map.Contains(pin), "Failure inside game deletion never produces duplicate on recovery");
            view.Tab(false); var shared = Add(map, "Общая", 5); shared.m_ownerID = 777; shared.m_author = new Splatform.PlatformUserID("Steam_555");
            ZNet.instance.Players.Add(new ZNet.PlayerInfo { m_name = "Друг", m_userInfo = new ZNet.UserInfo { m_id = shared.m_author } });
            view.Tab(false); bool found = false;
            foreach (var row in view.Rows) if (row.Title == "Общая") found = row.Details.Contains("Друг") && row.Details.Contains("неизвестно");
            Check(found, "Shared pin uses actual author ID lookup and never guesses creation date");
            ZNet.instance.Players.Clear(); view.Tab(false); found = false;
            foreach (var row in view.Rows) if (row.Title == "Общая") found = row.Details.Contains("Steam_555");
            Check(found, "Offline shared author retains verifiable platform identity");
            controller.Close(); controller.Remove(map, shared); Open(controller); restore = view.Rows[0].Activate;
            ZNet.instance.World = 101; controller.Tick(true); Open(controller); int previousAdded = map.Added; restore();
            Check(map.Added == previousAdded && view.Rows.Count == 0, "Stale restore callback cannot write to another world");
            ZNet.instance.World = 100; controller.Tick(true); Open(controller);
            Check(view.Rows.Count == 3 && view.Rows[0].Enabled, "Returning to original world loads durable history");
            owner.Id = 201; controller.Tick(true); Open(controller); Check(view.Rows.Count == 0, "Different character gets separate history");
            owner.Id = 200; controller.Tick(true); Open(controller); Check(view.Rows.Count == 3, "Original character restores its own archive");
            map.m_mode = Minimap.MapMode.Small; controller.Tick(true); Check(!controller.IsOpen, "Closing large map releases history UI");
            Open(controller); Check(!controller.IsOpen, "Shortcut does not open on minimap");
            map.m_mode = Minimap.MapMode.Large; Minimap.Naming = true; Open(controller); Check(!controller.IsOpen, "Shortcut ignored during pin naming"); Minimap.Naming = false;
            controller.Tick(false); Call("Open", controller); Check(!controller.IsOpen, "Cannot overlap deletion confirmation");
            Open(controller); owner.Dead = true; controller.Tick(true); Check(!controller.IsOpen, "Death closes history"); owner.Dead = false;
            Open(controller); owner.Sleeping = true; controller.Tick(true); Check(!controller.IsOpen, "Sleep closes history"); owner.Sleeping = false;
            Open(controller); owner.Teleporting = true; controller.Tick(true); Check(!controller.IsOpen, "Teleport closes history"); owner.Teleporting = false;
            Open(controller); owner.Cutscene = true; controller.Tick(true); Check(!controller.IsOpen, "Cutscene closes history"); owner.Cutscene = false;
            Open(controller); Menu.Visible = true; controller.Tick(true); Check(!controller.IsOpen, "Game menu closes history"); Menu.Visible = false;
            Open(controller); global::Console.Visible = true; controller.Tick(true); Check(!controller.IsOpen, "Console closes history"); global::Console.Visible = false;
            Open(controller); Chat.instance = new Chat { Focus = true }; controller.Tick(true); Check(!controller.IsOpen, "Chat focus closes history"); Chat.instance = null;
            Open(controller); UnifiedPopup.Visible = true; controller.Tick(true); Check(!controller.IsOpen, "Foreign popup closes own window"); UnifiedPopup.Visible = false;
            Open(controller); Input.Down.Add(KeyCode.Escape); controller.Tick(true); Input.Down.Clear(); Check(!controller.IsOpen, "Escape closes history");
            Open(controller); ZInput.Cancel = true; controller.Tick(true); ZInput.Cancel = false; Check(!controller.IsOpen, "Controller B closes history");
            controller.Close(); var translated = Add(map, "Пещера тролля", 99);
            Call("RememberQuickPin", null, translated, "default.trollcave");
            Check((string)Call("PresetFor", null, translated) == "default.trollcave", "Quick placement records a stable preset binding");
            controller.Remove(map, translated); Open(controller); view.Rows[0].Activate();
            var livePins = (List<Minimap.PinData>)AccessTools.Field(typeof(Minimap), "m_pins").GetValue(map);
            translated = livePins.Find(p => p.m_pos.x == 99);
            Check(translated != null && (string)Call("PresetFor", null, translated) == "default.trollcave", "History restoration retains translated preset binding");
            Call("RememberRename", null, translated, translated.m_name);
            Check((string)Call("PresetFor", null, translated) == "", "Explicit same-text rename becomes literal");
            controller.Close(); controller.Remove(map, translated); Open(controller); view.Rows[0].Activate();
            translated = livePins.Find(p => p.m_pos.x == 99);
            Check(translated != null && (string)Call("PresetFor", null, translated) == "", "Restoration retains explicit literal-name override");
            Open(controller); Player.m_localPlayer = null; controller.Tick(true); Check(!controller.IsOpen, "Logout clears window and context");
            Player.m_localPlayer = owner; Open(controller); controller.Dispose(); Check(!controller.IsOpen, "Plugin disposal releases UI");
            Check(errors.Count == 0, "All expected state transitions complete without errors");
            System.Console.WriteLine("OK: " + checks + " production map-history controller assertions with host doubles.");
        }
        finally
        {
            if (controller != null) controller.Dispose();
            string resolved = Path.GetFullPath(folder), parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("vmp-pin-host-", StringComparison.Ordinal)) Directory.Delete(resolved, true);
        }
    }
}
