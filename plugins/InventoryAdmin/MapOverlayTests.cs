// Runs without starting Valheim. Native signatures are also compiled by Build.ps1.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimModPack.InventoryAdmin
{
    public static class MapOverlayTests
    {
        private static int checks;
        private static void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException(message); checks++; }
        private static PlayerLocation Location(long peer, long character, string name, float x)
        { return new PlayerLocation { PeerId = peer, CharacterId = character, Name = name, X = x, Y = 20, Z = 30 }; }
        private static Minimap.PinData Named(Minimap map, string name)
        { foreach (var pin in map.Pins) if (pin.m_name == name) return pin; return null; }
        public static int Main()
        {
            try { Run(); System.Console.WriteLine("PASS: " + checks + " administrator map overlay checks."); return 0; }
            catch (Exception error) { System.Console.Error.WriteLine(error); return 1; }
        }
        private static void Run()
        {
            Player.m_localPlayer = new Player(); ZNet.instance = new ZNet(); Minimap.instance = new Minimap();
            bool allowed = true, tracking = true;
            var frame = new List<PlayerLocation> { Location(1, 101, "Self", 1), Location(2, 102, "Hidden", 2) };
            var overlay = new AdminMapOverlay(() => allowed, () => tracking, () => frame);
            var map = Minimap.instance;
            var persistent = map.AddPin(new Vector3(5, 6, 7), Minimap.PinType.Icon0, "My saved pin", true, false, 99);
            overlay.Tick(); var hidden = Named(map, "Hidden");
            Check(map.Pins.Count == 2 && hidden != null, "Only the remote hidden player gets an extra marker.");
            Check(!hidden.m_save && hidden.m_type == Minimap.PinType.Player && hidden.m_ownerID == 0, "Supplemental player pin is temporary and locally visible.");
            Check(Named(map, "Self") == null, "Local native directional marker has no duplicate.");
            int filterCalls = CensorShittyWords.Calls;
            for (int i = 0; i < 10; i++) overlay.Tick();
            Check(CensorShittyWords.Calls == filterCalls, "Unchanged frames reuse native-filtered display names.");
            map.Dirty = false; frame[1].X = 40; overlay.Tick();
            Check(Object.ReferenceEquals(hidden, Named(map, "Hidden")) && hidden.m_pos.x == 40 && map.Dirty, "Movement updates existing marker and requests native repaint.");
            Check(CensorShittyWords.Calls == filterCalls, "Movement also reuses the native-filtered display name.");
            Check(overlay.CanFind(2) && overlay.Find(2) && map.m_mode == Minimap.MapMode.Large
                && map.ShownPosition == new Vector3(40, 20, 30), "Find centers native large map on current coordinates.");
            Check(!overlay.CanFind(999) && !overlay.Find(999), "Unknown peer cannot be located.");
            frame[1].CharacterId = 202; overlay.Tick();
            Check(!Object.ReferenceEquals(hidden, Named(map, "Hidden")) && map.Removed.Contains(hidden), "Character changes replace the previous character marker.");
            Check(CensorShittyWords.Calls == filterCalls + 1, "New character obtains a new filtered display name.");
            hidden = Named(map, "Hidden"); frame[1].Name = "Renamed"; overlay.Tick();
            Check(Named(map, "Hidden") == null && Named(map, "Renamed") != null && map.Removed.Contains(hidden)
                && CensorShittyWords.Calls == filterCalls + 2, "Changing the original player name refreshes its filtered marker label.");
            frame[1].Name = "Hidden"; overlay.Tick();
            hidden = Named(map, "Hidden"); map.RemovePin(hidden); overlay.Tick();
            Check(!Object.ReferenceEquals(hidden, Named(map, "Hidden")) && map.Pins.Count == 2, "Native removal or clear can be repaired from current frame.");
            frame.Clear(); overlay.Tick();
            Check(map.Pins.Count == 1 && Object.ReferenceEquals(map.Pins[0], persistent), "A roster disconnect removes only owned pins.");

            frame.Add(Location(2, 202, "Duplicate", 2)); frame.Add(Location(3, 203, "Duplicate", 3));
            var nativePublic = map.AddPublicPlayer(2, "Duplicate", new Vector3(8, 9, 10)); overlay.Tick();
            Check(map.Pins.Count == 3, "Public native player marker is not duplicated despite identical player names.");
            Check(nativePublic.m_pos == new Vector3(8, 9, 10) && !map.Removed.Contains(nativePublic), "Native public marker is neither moved nor removed.");
            tracking = false; overlay.Tick();
            Check(map.Pins.Count == 2 && map.Pins.Contains(persistent) && map.Pins.Contains(nativePublic), "Tracking off removes only supplemental markers.");
            Check(!overlay.CanFind(3) && !overlay.Find(3), "Tracking off prevents map lookup.");
            tracking = true; overlay.Tick(); allowed = false; overlay.Tick();
            Check(map.Pins.Count == 2 && !overlay.CanFind(3), "Permission revocation removes supplemental markers and lookup.");
            allowed = true; overlay.Tick(); Player.m_localPlayer = null; overlay.Tick();
            Check(map.Pins.Count == 2, "Logout removes supplemental markers even when tracking remains enabled.");
            Player.m_localPlayer = new Player(); overlay.Tick(); Game.m_noMap = true; overlay.Tick();
            Check(map.Pins.Count == 2 && !overlay.Find(3), "World no-map mode is respected and clears owned markers.");
            Game.m_noMap = false; overlay.Tick();
            var secondMap = new Minimap(); Minimap.instance = secondMap; overlay.Tick();
            Check(map.Pins.Count == 2 && secondMap.Pins.Count == 2, "New map receives new owned markers and old map is cleaned.");
            overlay.Clear(); overlay.Clear();
            Check(secondMap.Pins.Count == 0 && persistent.m_pos == new Vector3(5, 6, 7) && persistent.m_save
                && !map.Removed.Contains(persistent), "Idempotent clear preserves saved user pins and their coordinates.");
            frame.Clear(); frame.Add(Location(4, 204, "<color=red>Name</color>", 4)); overlay.Tick();
            Check(secondMap.Pins[0].m_name.IndexOf('<') < 0 && secondMap.Pins[0].m_name.IndexOf('>') < 0,
                "Player names cannot inject map label rich text.");
            frame[0].X = Single.NaN; overlay.Tick();
            Check(secondMap.Pins.Count == 0 && !overlay.CanFind(4), "Invalid coordinates cannot create or retain a marker.");

            var cache = new PlayerLocationCache(); string token = "11111111111111111111111111111111";
            cache.SetContext(1, token); cache.SetAccess(true, true);
            frame.Clear(); frame.Add(Location(5, 205, "Fresh", 5));
            Check(cache.TryReplace(1, token, 1, frame, 1), "Fresh map fixture frame is accepted.");
            double now = 1; overlay = new AdminMapOverlay(() => true, () => true, () => cache.Snapshot(now));
            overlay.Tick(); Check(secondMap.Pins.Count == 1 && overlay.CanFind(5), "Fresh server cache renders and supports locate.");
            now = 9; overlay.Tick();
            Check(secondMap.Pins.Count == 0 && !overlay.CanFind(5) && !overlay.Find(5), "Eight-second expiration removes map markers and locate coordinates.");
        }
    }
}
