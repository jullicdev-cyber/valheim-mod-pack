using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using WC = ValheimModPack.WorldCharacters.Plugin;

namespace ValheimModPack.InventoryAdmin
{
    public sealed partial class Plugin
    {
        private void UpdatePlayerMap()
        {
            // Optional display failure must not stop inventory checkpoints or
            // leave the transport queue blocked behind a new map request.
            try { UpdateLocations(); mapOverlay.Tick(); }
            catch (Exception e)
            {
                locationCache.Clear(); mapOverlay.Clear();
                if (Time.realtimeSinceStartup >= locationErrorAt)
                {
                    Logger.LogWarning("Player map tracking unavailable: " + e.Message);
                    locationErrorAt = Time.realtimeSinceStartup + 10;
                }
            }
        }
        private void SetTrackingPlayers(bool enabled)
        {
            if (!CanUse()) return;
            string previous = locationToken;
            mapTracking.Value = enabled; ClearLocations(); locationRefresh = 0; locationSnapshot = 0;
            if (!enabled && previous.Length != 0)
            {
                try { ToServer(20, w => { Text(w, previous); w.Write(false); }); }
                catch (Exception e) { Logger.LogWarning("Player tracking unsubscribe: " + e.Message); }
            }
        }
        private void ClearLocations()
        {
            locationToken = ""; locationCache.SetAccess(false, false); locationCache.SetContext(0, "");
            if (IsHost) locationSubscriptions.Remove(ZNet.GetUID());
            if (mapOverlay != null) mapOverlay.Clear();
        }
        private void UpdateLocations()
        {
            float now = Time.realtimeSinceStartup;
            bool enabled = CanUse() && mapTracking.Value;
            if (!enabled) { if (locationToken.Length != 0) ClearLocations(); }
            else
            {
                if (locationToken.Length == 0)
                {
                    locationToken = Guid.NewGuid().ToString("N"); locationCache.SetContext(world, locationToken);
                    locationCache.SetAccess(true, true); locationRefresh = 0;
                }
                if (now >= locationRefresh)
                {
                    string token = locationToken;
                    byte[] request = Packet(20, w => { Text(w, token); w.Write(true); });
                    if (IsHost) Dispatch(ZNet.GetUID(), request);
                    else
                    {
                        ZNetPeer server = network.GetServerPeer(); ZNet session = network; long id = world;
                        if (server != null && server.IsReady() && server.m_rpc.IsConnected())
                            transport.SendGuarded(server.m_rpc, request, "location-subscribe", () => network == session && world == id
                                && ReferenceEquals(server, network.GetServerPeer()) && locationToken == token && CanUse() && mapTracking.Value);
                    }
                    locationRefresh = now + LocationRefreshSeconds;
                }
            }
            if (!IsHost) return;
            foreach (var pair in locationSubscriptions.ToArray())
            {
                ZNetPeer recipient = pair.Key == ZNet.GetUID() ? null : Peer(pair.Key);
                if (!Allowed(pair.Key) || pair.Value.Until <= now || pair.Key != ZNet.GetUID() && (recipient == null || !ReferenceEquals(recipient.m_rpc, pair.Value.Rpc)))
                {
                    locationSubscriptions.Remove(pair.Key);
                    if (recipient != null) Send(pair.Key, 22, w => Text(w, pair.Value.Token));
                }
            }
            if (now < locationSnapshot || locationSubscriptions.Count == 0) return;
            locationSnapshot = now + LocationSnapshotSeconds;
            byte[] positions = PlayerLocationCodec.Encode(CaptureLocations()); long sequence = ++locationSequence;
            foreach (var pair in locationSubscriptions.ToArray())
            {
                long viewer = pair.Key, id = world; LocationSubscription subscription = pair.Value; ZNet session = network;
                byte[] packet = Packet(21, w => { Text(w, subscription.Token); w.Write(sequence); PolicyBinary.WriteBytes(w, positions, 16384); });
                if (viewer == ZNet.GetUID()) { Dispatch(0, packet); continue; }
                ZNetPeer recipient = Peer(viewer); if (recipient == null) continue;
                transport.SendGuarded(recipient.m_rpc, packet, "player-locations", () =>
                {
                    LocationSubscription current; float sentAt = Time.realtimeSinceStartup;
                    return network == session && world == id && Allowed(viewer) && ReferenceEquals(Peer(viewer), recipient)
                        && ReferenceEquals(recipient.m_rpc, subscription.Rpc) && locationSubscriptions.TryGetValue(viewer, out current)
                        && ReferenceEquals(current, subscription) && current.Until > sentAt && sentAt >= now && sentAt - now < 4;
                });
            }
        }
        private static string LocationName(string name)
        {
            // Keep a full 128-player roster below one atomic 16KiB frame.
            var clean = new StringBuilder(64); int bytes = 0;
            name = name ?? "";
            for (int i = 0; i < name.Length; ++i)
            {
                char value = name[i];
                if (Char.IsControl(value) || Char.IsLowSurrogate(value)) continue;
                if (Char.IsHighSurrogate(value))
                {
                    if (i + 1 == name.Length || !Char.IsLowSurrogate(name[i + 1])) continue;
                    if (bytes + 4 > 64) break;
                    clean.Append(value); clean.Append(name[++i]); bytes += 4; continue;
                }
                int width = value < 128 ? 1 : value < 2048 ? 2 : 3;
                if (bytes + width > 64) break;
                clean.Append(value); bytes += width;
            }
            return clean.ToString();
        }
        private static bool ValidPosition(Vector3 value)
        {
            return !Single.IsNaN(value.x) && !Single.IsInfinity(value.x) && Math.Abs(value.x) <= 100000
                && !Single.IsNaN(value.y) && !Single.IsInfinity(value.y) && Math.Abs(value.y) <= 100000
                && !Single.IsNaN(value.z) && !Single.IsInfinity(value.z) && Math.Abs(value.z) <= 100000;
        }
        private void RememberPosition(ZRpc rpc)
        {
            ZNetPeer peer;
            if (!IsHost || !peers.TryGetValue(rpc, out peer) || !Ready(peer) || peer.m_characterID.IsNone()) return;
            positionSamples[rpc] = new PositionSample { Character = peer.m_characterID, At = Time.realtimeSinceStartup };
        }
        private List<PlayerLocation> CaptureLocations()
        {
            var result = new List<PlayerLocation>();
            var characters = new HashSet<long>();
            if (!IsHost) return result;
            if (CanUse() && !Player.m_localPlayer.IsDead() && ValidPosition(Player.m_localPlayer.transform.position))
            {
                Vector3 point = Player.m_localPlayer.transform.position; long character = Player.m_localPlayer.GetPlayerID();
                if (character != 0)
                {
                    characters.Add(character);
                    result.Add(new PlayerLocation { PeerId = ZNet.GetUID(), CharacterId = character,
                        Name = LocationName(Player.m_localPlayer.GetPlayerName()), X = point.x, Y = point.y, Z = point.z });
                }
            }
            foreach (ZNetPeer peer in peers.Values)
            {
                if (result.Count >= PlayerLocationCodec.MaximumPlayers) break;
                PositionSample sample; float now = Time.realtimeSinceStartup;
                if (!Ready(peer) || peer.m_characterID.IsNone() || !positionSamples.TryGetValue(peer.m_rpc, out sample)
                    || !sample.Character.Equals(peer.m_characterID) || now < sample.At || now - sample.At >= PlayerLocationCache.FreshnessSeconds) continue;
                ZDO characterZdo = ZDOMan.instance == null ? null : ZDOMan.instance.GetZDO(peer.m_characterID);
                long character = WC.GetAdministrativeCharacter(peer.m_uid);
                if (character == 0 || characterZdo == null || characterZdo.GetLong(ZDOVars.s_playerID, 0) != character
                    || characterZdo.GetBool(ZDOVars.s_dead, false) || !ValidPosition(peer.m_refPos) || !characters.Add(character)) continue;
                Vector3 point = peer.m_refPos;
                result.Add(new PlayerLocation { PeerId = peer.m_uid, CharacterId = character,
                    Name = LocationName(peer.m_playerName), X = point.x, Y = point.y, Z = point.z });
            }
            return result;
        }
    }
}
