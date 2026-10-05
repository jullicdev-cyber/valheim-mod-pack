using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace ValheimModPack.InventoryAdmin
{
    // The location provider owns authorization, session identity and the eight-second
    // freshness limit. These pins exist only in the viewing administrator's map.
    public sealed class AdminMapOverlay
    {
        private static readonly FieldInfo PlayerPinsField = typeof(Minimap).GetField("m_playerPins", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo PlayerInfoField = typeof(Minimap).GetField("m_tempPlayerInfo", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo AllPinsField = typeof(Minimap).GetField("m_pins", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo PinsDirtyField = typeof(Minimap).GetField("m_pinUpdateRequired", BindingFlags.Instance | BindingFlags.NonPublic);
        private readonly Func<bool> canUse, tracking;
        private readonly Func<IList<PlayerLocation>> locations;
        private readonly Dictionary<long, OwnedPin> owned = new Dictionary<long, OwnedPin>();
        private readonly HashSet<long> visible = new HashSet<long>(), publicPlayers = new HashSet<long>();
        private readonly List<long> remove = new List<long>();
        private Minimap map;
        private ZNet network;
        private Player player;
        private sealed class OwnedPin
        {
            internal long CharacterId;
            internal string RawName, DisplayName;
            internal Minimap.PinData Pin;
        }

        public AdminMapOverlay(Func<bool> canUse, Func<bool> tracking, Func<IList<PlayerLocation>> locations)
        {
            if (canUse == null || tracking == null || locations == null) throw new ArgumentNullException("bindings");
            this.canUse = canUse; this.tracking = tracking; this.locations = locations;
        }

        private bool Active()
        {
            return canUse() && tracking() && ZNet.instance != null && Player.m_localPlayer != null
                && Minimap.instance != null && !Game.m_noMap;
        }

        public void Tick()
        {
            if (!Active()) { Clear(); return; }
            if (!ReferenceEquals(map, Minimap.instance) || !ReferenceEquals(network, ZNet.instance)
                || !ReferenceEquals(player, Player.m_localPlayer))
            {
                Clear(); map = Minimap.instance; network = ZNet.instance; player = Player.m_localPlayer;
            }
            var current = locations();
            if (current == null || current.Count == 0) { ClearPins(); return; }
            ReadPublicPlayers(); visible.Clear();
            var allPins = AllPinsField == null ? null : AllPinsField.GetValue(map) as List<Minimap.PinData>;
            foreach (var location in current)
            {
                // The local player already has Valheim's own directional marker.
                if (!Valid(location) || location.PeerId == ZNet.GetUID() || !visible.Add(location.PeerId)) continue;
                if (publicPlayers.Contains(location.PeerId)) { Remove(location.PeerId); continue; }
                string rawName = location.Name ?? "";
                OwnedPin entry;
                if (owned.TryGetValue(location.PeerId, out entry)
                    && (entry.CharacterId != location.CharacterId || !String.Equals(entry.RawName, rawName, StringComparison.Ordinal)
                        || entry.Pin.m_name != entry.DisplayName
                        || entry.Pin.m_shouldDelete || (allPins != null && !allPins.Contains(entry.Pin))))
                { Remove(location.PeerId); entry = null; }
                if (entry == null)
                {
                    // Movement and repaints reuse the detached display name. Native
                    // UGC filtering runs when the character or source name changes.
                    string name = PinName(location);
                    entry = new OwnedPin { CharacterId = location.CharacterId, RawName = rawName, DisplayName = name,
                        Pin = map.AddPin(Position(location), Minimap.PinType.Player, name, false, false) };
                    owned[location.PeerId] = entry;
                }
                Vector3 position = Position(location);
                if (entry.Pin.m_pos != position)
                {
                    entry.Pin.m_pos = position;
                    if (PinsDirtyField != null) PinsDirtyField.SetValue(map, true);
                }
            }
            remove.Clear();
            foreach (long peerId in owned.Keys) if (!visible.Contains(peerId)) remove.Add(peerId);
            foreach (long peerId in remove) Remove(peerId);
        }

        private void ReadPublicPlayers()
        {
            publicPlayers.Clear();
            if (PlayerPinsField == null || PlayerInfoField == null) return;
            var pins = PlayerPinsField.GetValue(map) as List<Minimap.PinData>;
            var info = PlayerInfoField.GetValue(map) as List<ZNet.PlayerInfo>;
            // UpdatePlayerPins builds these two lists in the same order. Pins have
            // ownerID zero, so matching by pin names would confuse identical names.
            if (pins == null || info == null || pins.Count != info.Count) return;
            for (int i = 0; i < pins.Count; i++)
            {
                if (pins[i] == null || pins[i].m_save || pins[i].m_shouldDelete || pins[i].m_type != Minimap.PinType.Player) continue;
                long peerId = info[i].m_characterID.UserID;
                if (peerId != 0 && info[i].m_publicPosition) publicPlayers.Add(peerId);
            }
        }

        private static bool Valid(PlayerLocation location)
        {
            return location != null && location.PeerId != 0 && location.CharacterId != 0
                && Finite(location.X) && Finite(location.Y) && Finite(location.Z);
        }
        private static bool Finite(float value) { return !Single.IsNaN(value) && !Single.IsInfinity(value); }
        private static Vector3 Position(PlayerLocation location) { return new Vector3(location.X, location.Y, location.Z); }
        private static string PinName(PlayerLocation location)
        {
            string value = CensorShittyWords.FilterUGC(location.Name ?? "", UGCType.CharacterName, location.PeerId);
            var name = new StringBuilder(64);
            foreach (char c in value ?? "")
            {
                if (name.Length >= 64) break;
                if (!Char.IsControl(c)) name.Append(c == '<' ? '\u2039' : c == '>' ? '\u203a' : c);
            }
            return name.ToString();
        }

        private PlayerLocation FindLocation(long peerId)
        {
            if (!Active() || peerId == 0) return null;
            var current = locations();
            if (current != null) foreach (var location in current)
                if (Valid(location) && location.PeerId == peerId) return location;
            return null;
        }
        public bool CanFind(long peerId) { return FindLocation(peerId) != null; }
        public bool Find(long peerId)
        {
            var location = FindLocation(peerId);
            if (location == null) return false;
            // Public native API also gives the map a short input delay, preventing
            // the click that closed our modal from creating a map pin.
            Minimap.instance.ShowPointOnMap(Position(location));
            return Minimap.instance.m_mode == Minimap.MapMode.Large;
        }

        private void Remove(long peerId)
        {
            OwnedPin entry;
            if (!owned.TryGetValue(peerId, out entry)) return;
            owned.Remove(peerId);
            if (map != null && entry.Pin != null) map.RemovePin(entry.Pin);
        }
        private void ClearPins()
        {
            if (map != null) foreach (var entry in owned.Values) if (entry.Pin != null) map.RemovePin(entry.Pin);
            owned.Clear(); visible.Clear(); publicPlayers.Clear(); remove.Clear();
        }
        public void Clear()
        {
            ClearPins(); map = null; network = null; player = null;
        }
    }
}
