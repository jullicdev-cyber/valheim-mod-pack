using System;
using System.IO;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;
using WC = ValheimModPack.WorldCharacters.Plugin;

namespace ValheimModPack.InventoryAdmin
{
    internal static partial class RootServiceTests
    {
        private static List<PlayerLocation> CapturedLocations(Plugin plugin)
        { return (List<PlayerLocation>)Call(plugin, "CaptureLocations"); }
        private static PlayerLocation FindLocation(List<PlayerLocation> locations, long peer)
        { return locations.Find(p => p.PeerId == peer); }
        private static IDictionary LocationSubscriptions(Plugin plugin)
        { return (IDictionary)Get(plugin, "locationSubscriptions"); }
        private static PlayerLocationCache LocationCache(Plugin plugin)
        { return (PlayerLocationCache)Get(plugin, "locationCache"); }
        private static List<PlayerLocation> CurrentLocations(Plugin plugin)
        { return LocationCache(plugin).Snapshot(Time.realtimeSinceStartup); }
        private static string LocationToken(Plugin plugin)
        { return (string)Get(plugin, "locationToken"); }
        private static void LocationSnapshot(Plugin plugin, string token, long sequence, IList<PlayerLocation> locations)
        { Dispatch(plugin, 0, 21, w => { Text(w, token); w.Write(sequence); Bytes(w, PlayerLocationCodec.Encode(locations)); }); }

        // Drain actual chunked packets through a separate receiver, so assertions
        // cover the transport send boundary instead of only queue bookkeeping.
        private static List<int> SentLocationKinds(ZRpc rpc)
        {
            var receiver = new WireTransport(); var kinds = new List<int>();
            foreach (ZPackage package in rpc.Sent)
            {
                byte[] complete = receiver.Receive(rpc, new ZPackage(package.GetArray()));
                if (complete == null) continue;
                using (var stream = new MemoryStream(complete, false)) using (var reader = new BinaryReader(stream))
                { reader.ReadInt32(); reader.ReadInt64(); kinds.Add(reader.ReadInt32()); }
            }
            return kinds;
        }

        private static void Locations(Plugin plugin)
        {
            ZNetPeer bob = ZNet.instance.Peers.Find(p => p.m_uid == BobPeer), alice = ZNet.instance.Peers.Find(p => p.m_uid == AlicePeer);
            var tracking = (ConfigEntry<bool>)Get(plugin, "mapTracking"); var store = (PermissionStore)Get(plugin, "permissions");
            var wire = (WireTransport)Get(plugin, "transport");
            bool previousTracking = tracking.Value, previousAliceAdmin = store.IsAdministrator(999, Alice), previousBobAdmin = store.IsAdministrator(999, Bob);
            bool previousServer = ZNet.instance.Server; long previousWorld = ZNet.instance.World;
            ZDOMan previousZdos = ZDOMan.instance; Vector3 previousHostPosition = Player.m_localPlayer.transform.position;
            bool previousHostDead = Player.m_localPlayer.Dead; ZDOID previousCharacter = bob.m_characterID;
            Vector3 previousRefPos = bob.m_refPos; bool previousPublicRefPos = bob.m_publicRefPos, previousConnected = bob.m_rpc.Connected;
            try
            {
                wire.Clear(); alice.m_rpc.Sent.Clear(); bob.m_rpc.Sent.Clear(); Call(plugin, "SetTrackingPlayers", false);
                LocationSubscriptions(plugin).Clear(); ZDOMan.instance = new ZDOMan();
                Player.m_localPlayer.transform.position = new Vector3(30, 20, 40); Player.m_localPlayer.Dead = false;
                bob.m_characterID = new ZDOID { UserID = BobPeer, ID = 1 }; bob.m_refPos = new Vector3(900, 5000, -1000); bob.m_publicRefPos = false;
                var zdo = new ZDO { Position = bob.m_refPos, Character = bob.m_playerID };
                ZDOMan.instance.Zdos.Add(bob.m_characterID, zdo);
                CaptureLocationChecks(plugin, bob, zdo);
                LocationNameChecks(plugin, bob);
                SubscriptionLocationChecks(plugin, alice, bob, store, wire);
                GuardedLocationTransportChecks();
                LocationShortcutChecks(plugin);
                ClientLocationChecks(plugin, store, wire);
            }
            finally
            {
                ZNet.instance.Server = previousServer; ZNet.instance.World = previousWorld;
                ZDOMan.instance = previousZdos; Player.m_localPlayer.transform.position = previousHostPosition; Player.m_localPlayer.Dead = previousHostDead;
                bob.m_characterID = previousCharacter; bob.m_refPos = previousRefPos; bob.m_publicRefPos = previousPublicRefPos; bob.m_rpc.Connected = previousConnected;
                Call(plugin, "EnsureSession"); Call(plugin, "SetTrackingPlayers", false); wire.Clear();
                store = (PermissionStore)Get(plugin, "permissions");
                if (store != null) { store.SetAdministrator(999, true, Alice, previousAliceAdmin); store.SetAdministrator(999, true, Bob, previousBobAdmin); }
                tracking.Value = previousTracking;
            }
        }

        private static void LocationNameChecks(Plugin plugin, ZNetPeer bob)
        {
            string previousName = bob.m_playerName, previousHostName = Player.m_localPlayer.Name;
            var added = new List<ZNetPeer>(); var utf8 = new UTF8Encoding(false, true); var emoji = new StringBuilder();
            for (int i = 0; i < 1000; ++i) emoji.Append("\ud83d\ude00");
            string longEmoji = emoji.ToString();
            try
            {
                foreach (string oddName in new[] { "\0Good\u0007Name", "\ud800A\udc00", longEmoji, "\0\ud800X\udc00" + longEmoji })
                {
                    bob.m_playerName = oddName; Player.m_localPlayer.Name = oddName; Call(plugin, "RememberPosition", bob.m_rpc);
                    List<PlayerLocation> captured = CapturedLocations(plugin);
                    PlayerLocation remote = FindLocation(captured, BobPeer), host = FindLocation(captured, ZNet.Uid);
                    Check(remote != null && host != null, "malformed or very long display name cannot omit otherwise valid player coordinates");
                    Check(utf8.GetByteCount(remote.Name) <= 64 && utf8.GetByteCount(host.Name) <= 64,
                        "native names fit 64 UTF-8 bytes without malformed surrogate encoding");
                    bool controls = false; foreach (char c in remote.Name) if (Char.IsControl(c)) controls = true;
                    Check(!controls && PlayerLocationCodec.Decode(PlayerLocationCodec.Encode(captured)).Count == captured.Count,
                        "malformed name controls and surrogate input cannot abort complete location frame encoding");
                }
                bob.m_playerName = longEmoji; Player.m_localPlayer.Name = longEmoji;
                for (int i = 0; i < 126; ++i)
                {
                    long peerId = 10000 + i, characterId = 20000 + i;
                    var peer = new ZNetPeer { m_uid = peerId, m_playerID = characterId, m_playerName = longEmoji,
                        m_socket = new UnsupportedServiceTestSocket(), m_characterID = new ZDOID { UserID = peerId, ID = 1 },
                        m_refPos = new Vector3(i, 5000, -i), m_publicRefPos = false };
                    ZNet.instance.Peers.Add(peer); added.Add(peer); WC.Durable[peerId] = 50;
                    WC.ApprovedOwners[peer] = "Steam_" + (76561198000000100UL + (ulong)i).ToString(); WC.ApprovedConnections[peer.m_rpc] = peer;
                    Call(plugin, "Register", peer); ZDOMan.instance.Zdos.Add(peer.m_characterID, new ZDO { Position = peer.m_refPos, Character = characterId });
                    Call(plugin, "RememberPosition", peer.m_rpc);
                }
                List<PlayerLocation> maximum = CapturedLocations(plugin);
                Check(maximum.Count == 128, "native capture supports the full authenticated 128-player roster");
                bool allNamesFull = true; foreach (PlayerLocation location in maximum) if (utf8.GetByteCount(location.Name) != 64) allNamesFull = false;
                Check(allNamesFull, "worst-size native packet fixture fills every bounded display name with complete emoji pairs");
                byte[] positions = PlayerLocationCodec.Encode(maximum);
                byte[] actualPacket = (byte[])Call(plugin, "Packet", 21, (Action<BinaryWriter>)(w => { Text(w, Id()); w.Write(1L); Bytes(w, positions); }));
                Check(actualPacket.Length < 16384, "actual version/world/token/sequence envelope and 128 worst-case names fit one atomic location transport frame");
            }
            finally
            {
                bob.m_playerName = previousName; Player.m_localPlayer.Name = previousHostName;
                foreach (ZNetPeer peer in added)
                {
                    ZNet.instance.Peers.Remove(peer); ((IDictionary)Get(plugin, "peers")).Remove(peer.m_rpc); ((IDictionary)Get(plugin, "positionSamples")).Remove(peer.m_rpc);
                    WC.Durable.Remove(peer.m_uid); WC.ApprovedOwners.Remove(peer); WC.ApprovedConnections.Remove(peer.m_rpc); ZDOMan.instance.Zdos.Remove(peer.m_characterID);
                }
            }
        }

        private static List<byte[]> ReceivedLocationTransportMessages(ZRpc rpc)
        {
            var receiver = new WireTransport(); var result = new List<byte[]>();
            foreach (ZPackage packet in rpc.Sent)
            {
                byte[] complete = receiver.Receive(rpc, new ZPackage(packet.GetArray()));
                if (complete != null) result.Add(complete);
            }
            return result;
        }

        private static void GuardedLocationTransportChecks()
        {
            var wire = new WireTransport(); var first = new ZRpc(); var second = new ZRpc(); wire.Clear();
            wire.SendGuarded(first, new byte[] { 1 }, "locations", () => false);
            wire.SendGuarded(first, new byte[] { 2, 2 }, "locations", () => true);
            wire.SendGuarded(second, new byte[] { 3, 3, 3 }, "locations", () => true);
            wire.SendGuarded(first, new byte[] { 4, 4, 4, 4 }, "other-key", () => true);
            Time.realtimeSinceStartup += .1f; wire.Tick();
            List<byte[]> firstReceived = ReceivedLocationTransportMessages(first), secondReceived = ReceivedLocationTransportMessages(second);
            Check(firstReceived.Count == 2 && firstReceived[0].Length == 2 && firstReceived[0][0] == 2 && firstReceived[1][0] == 4,
                "guarded queue sends newest data and guard for a key while preserving distinct keys");
            Check(secondReceived.Count == 1 && secondReceived[0][0] == 3, "guarded coalescing never replaces another connection's same-key snapshot");
            Check((long)Get(wire, "queuedBytes") == 0, "coalesced guarded sends release exact byte budget");
            wire.Clear(); first.Sent.Clear(); bool allowed = true;
            wire.SendGuarded(first, new byte[] { 1 }, "locations", () => allowed); allowed = false;
            Time.realtimeSinceStartup += .1f; wire.Tick();
            Check(first.Sent.Count == 0 && (long)Get(wire, "queuedBytes") == 0, "permission change after enqueue discards guarded frame before actual send");
            wire.SendGuarded(first, new byte[] { 1 }, "locations", delegate { throw new InvalidOperationException("Permission lookup became unavailable."); });
            Time.realtimeSinceStartup += .1f; wire.Tick();
            Check(first.Sent.Count == 0 && (long)Get(wire, "queuedBytes") == 0, "throwing guard fails closed and releases queued frame budget");
            wire.SendGuarded(first, new byte[] { 1 }, "locations", () => true); first.Connected = false;
            Time.realtimeSinceStartup += .1f; wire.Tick();
            Check(first.Sent.Count == 0 && (long)Get(wire, "queuedBytes") == 0, "disconnected recipient cannot receive queued guarded locations");
            first.Connected = true; string activeToken = Id(), frameToken = activeToken;
            wire.SendGuarded(first, new byte[] { 1 }, "locations", () => activeToken == frameToken); activeToken = Id();
            Time.realtimeSinceStartup += .1f; wire.Tick();
            Check(first.Sent.Count == 0 && (long)Get(wire, "queuedBytes") == 0, "viewer token replacement invalidates previously queued location snapshot");
            byte[] ordinary = new byte[40000]; for (int i = 0; i < ordinary.Length; ++i) ordinary[i] = (byte)(i % 251);
            wire.Clear(); wire.Send(first, ordinary);
            wire.SendGuarded(first, new byte[] { 1 }, "locations", delegate { throw new InvalidOperationException("Expired"); });
            wire.SendGuarded(first, new byte[] { 9, 9 }, "new-subscription", () => true);
            Time.realtimeSinceStartup += .2f; wire.Tick(); Time.realtimeSinceStartup += .1f; wire.Tick();
            firstReceived = ReceivedLocationTransportMessages(first);
            Check(firstReceived.Count == 2 && InventoryCodec.Fingerprint(firstReceived[0]) == InventoryCodec.Fingerprint(ordinary) && firstReceived[1][0] == 9,
                "discarding guarded messages preserves every ordinary fragmented byte and later valid guarded delivery");
        }

        private static void LocationShortcutChecks(Plugin plugin)
        {
            var mapKey = (ConfigEntry<KeyboardShortcut>)Get(plugin, "mapShortcut"); KeyboardShortcut previous = mapKey.Value;
            var tracking = (ConfigEntry<bool>)Get(plugin, "mapTracking"); var window = (AdminWindow)Get(plugin, "window");
            try
            {
                window.Hide(); mapKey.Value = new KeyboardShortcut(KeyCode.F10, KeyCode.LeftControl);
                Keys(new[] { KeyCode.F10, KeyCode.RightControl }, KeyCode.F10);
                Check(StaticBool("ShortcutDown", mapKey.Value), "right Ctrl triggers configured Ctrl+F10 tracking shortcut");
                object[] nativeArgs = new object[] { "Map", true };
                bool runOriginal = (bool)typeof(Plugin).GetMethod("BlockNative", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, nativeArgs);
                Check(!runOriginal && !(bool)nativeArgs[1], "correct tracking shortcut consumes overlapping vanilla gameplay input before map action");
                Keys(new[] { KeyCode.F10, KeyCode.LeftControl, KeyCode.LeftShift }, KeyCode.F10);
                Check(!StaticBool("ShortcutDown", mapKey.Value), "extra Shift does not toggle Ctrl+F10 player tracking");
                mapKey.Value = new KeyboardShortcut(KeyCode.F8, KeyCode.LeftAlt); bool oldTracking = tracking.Value;
                Keys(new[] { KeyCode.F8, KeyCode.RightAlt }, KeyCode.F8); Time.frameCount++; Call(plugin, "Update");
                Check(tracking.Value != oldTracking && (KeyCode)Get(plugin, "claimed") == KeyCode.F8, "actual update honors rebound tracking shortcut and claims configured main key");
            }
            finally
            {
                mapKey.Value = previous; Keys(new KeyCode[0]); Call(plugin, "SetTrackingPlayers", false);
                Time.frameCount++; Call(plugin, "Update"); Time.frameCount++; Call(plugin, "Update");
            }
        }

        private static void MapFailureAvailabilityChecks(Plugin plugin)
        {
            var wire = (WireTransport)Get(plugin, "transport"); ZRpc server = ZNet.instance.GetServerPeer().m_rpc;
            IDictionary operations = (IDictionary)Get(plugin, "localOperations"); string checkpointId = Id();
            Type operationType = typeof(Plugin).GetNestedType("LocalOperation", BindingFlags.NonPublic);
            object checkpoint = Activator.CreateInstance(operationType, true);
            operationType.GetField("Id", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(checkpoint, checkpointId);
            operationType.GetField("Changed", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(checkpoint, true);
            operationType.GetField("Destination", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(checkpoint, true);
            operationType.GetField("Sequence", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(checkpoint, 1L);
            operationType.GetField("At", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(checkpoint, Time.realtimeSinceStartup);
            try
            {
                wire.Clear(); server.Sent.Clear(); operations.Add(checkpointId, checkpoint);
                for (int i = 0; i < 64; ++i) wire.Send(server, new byte[] { 0x5a });
                plugin.GetType().GetField("locationRefresh", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(plugin, 0f);
                int warnings = plugin.Logger.Errors.FindAll(s => s.StartsWith("Player map tracking unavailable:", StringComparison.Ordinal)).Count;
                Call(plugin, "UpdatePlayerMap"); Call(plugin, "UpdatePlayerMap");
                Check(CurrentLocations(plugin).Count == 0 && (long)Get(wire, "queuedBytes") == 64,
                    "optional map send capacity failure clears positions while preserving the queued ordinary messages");
                Check(plugin.Logger.Errors.FindAll(s => s.StartsWith("Player map tracking unavailable:", StringComparison.Ordinal)).Count == warnings + 1,
                    "repeated map capacity failure is contained and its warning is throttled");
                Keys(new KeyCode[0]);
                for (int i = 0; i < 20; ++i) { Time.realtimeSinceStartup += .1f; Time.frameCount++; Call(plugin, "Update"); }
                Check((long)Get(wire, "queuedBytes") == 0 && server.Sent.Count >= 64,
                    "actual update drains a saturated transport queue before map retries can stall it");
                bool deliveredCheckpoint = false; int ordinaryMessages = 0;
                foreach (byte[] bytes in ReceivedLocationTransportMessages(server))
                {
                    if (bytes.Length == 1 && bytes[0] == 0x5a) { ++ordinaryMessages; continue; }
                    if (bytes.Length < 16) continue;
                    using (var stream = new MemoryStream(bytes, false)) using (var reader = new BinaryReader(stream))
                    { reader.ReadInt32(); reader.ReadInt64(); if (reader.ReadInt32() == 16) deliveredCheckpoint = true; }
                }
                Check(ordinaryMessages == 64 && deliveredCheckpoint && (bool)operationType.GetField("Reported", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(checkpoint),
                    "map send failure leaves ordinary deliveries intact and local durable inventory checkpoint continues through actual update");
            }
            finally { operations.Remove(checkpointId); wire.Clear(); server.Sent.Clear(); }
        }

        private static void CaptureLocationChecks(Plugin plugin, ZNetPeer bob, ZDO zdo)
        {
            Check(FindLocation(CapturedLocations(plugin), BobPeer) == null, "matching remote character without received position is omitted instead of pinned at origin");
            Call(plugin, "RememberPosition", bob.m_rpc);
            PlayerLocation hidden = FindLocation(CapturedLocations(plugin), BobPeer);
            Check(hidden != null && hidden.CharacterId == 102 && hidden.Name == "Bob" && hidden.X == 900 && hidden.Y == 5000 && hidden.Z == -1000,
                "authenticated hidden player is tracked with received coordinates and matching dungeon character");
            Check(!bob.m_publicRefPos, "tracking retains the player's public map visibility setting");
            PlayerLocation host = FindLocation(CapturedLocations(plugin), ZNet.Uid);
            Check(host != null && host.CharacterId == 100 && host.X == 30 && host.Z == 40, "local host has a real transform location");
            zdo.Dead = true;
            Check(FindLocation(CapturedLocations(plugin), BobPeer) == null, "dead remote character cannot retain a location pin");
            zdo.Dead = false; zdo.Character = 9999;
            Check(FindLocation(CapturedLocations(plugin), BobPeer) == null, "ZDO player identity mismatch cannot borrow another character location");
            zdo.Character = 102;
            ZDOID receivedCharacter = bob.m_characterID;
            bob.m_characterID = new ZDOID { UserID = BobPeer, ID = 2 };
            ZDOMan.instance.Zdos.Add(bob.m_characterID, new ZDO { Position = bob.m_refPos, Character = 102 });
            Check(FindLocation(CapturedLocations(plugin), BobPeer) == null, "replacement character ZDO cannot inherit old character's received-position sample");
            ZDOMan.instance.Zdos.Remove(bob.m_characterID); bob.m_characterID = receivedCharacter;
            ZDOMan.instance.Zdos.Remove(bob.m_characterID);
            Check(FindLocation(CapturedLocations(plugin), BobPeer) == null, "missing remote character ZDO is omitted");
            ZDOMan.instance.Zdos.Add(bob.m_characterID, zdo);
            bob.m_refPos = new Vector3(Single.NaN, 5000, 0); zdo.Position = bob.m_refPos; Call(plugin, "RememberPosition", bob.m_rpc);
            Check(FindLocation(CapturedLocations(plugin), BobPeer) == null, "NaN received remote position is not published");
            bob.m_refPos = new Vector3(0, 5000, 100001); zdo.Position = bob.m_refPos; Call(plugin, "RememberPosition", bob.m_rpc);
            Check(FindLocation(CapturedLocations(plugin), BobPeer) == null, "unbounded received remote position is not published");
            bob.m_refPos = new Vector3(900, 5000, -1000); zdo.Position = bob.m_refPos; Call(plugin, "RememberPosition", bob.m_rpc);
            bob.m_rpc.Connected = false;
            Check(FindLocation(CapturedLocations(plugin), BobPeer) == null, "disconnected peer immediately disappears from authoritative location roster");
            bob.m_rpc.Connected = true; Call(plugin, "RememberPosition", bob.m_rpc); Time.realtimeSinceStartup += 13;
            Check(FindLocation(CapturedLocations(plugin), BobPeer) == null, "remote position stops publishing after player stops sending vanilla position updates");
            Call(plugin, "RememberPosition", bob.m_rpc); Player.m_localPlayer.Dead = true;
            Check(FindLocation(CapturedLocations(plugin), ZNet.Uid) == null, "dead local host character is omitted");
            Player.m_localPlayer.Dead = false;
        }

        private static void SubscriptionLocationChecks(Plugin plugin, ZNetPeer alice, ZNetPeer bob, PermissionStore store, WireTransport wire)
        {
            store.SetAdministrator(999, true, Bob, false); store.SetAdministrator(999, true, Alice, true); Time.realtimeSinceStartup += 1;
            Reject(delegate { Dispatch(plugin, BobPeer, 20, w => { Text(w, Id()); w.Write(true); }); }, "ordinary player's location subscription");
            Check(!LocationSubscriptions(plugin).Contains(BobPeer), "ordinary player cannot allocate a location subscription");
            string token = Id(); Time.realtimeSinceStartup += 1;
            Reject(delegate { Call(plugin, "Dispatch", AlicePeer, Packet(20, w => { Text(w, token); w.Write(true); }, true)); }, "location subscription with trailing bytes");
            Reject(delegate { Dispatch(plugin, AlicePeer, 20, w => { Text(w, token); w.Write((byte)2); }); }, "location subscription noncanonical boolean");
            Check(!LocationSubscriptions(plugin).Contains(AlicePeer), "malformed subscription has no lease or queued position response");
            wire.Clear(); alice.m_rpc.Sent.Clear();
            Dispatch(plugin, AlicePeer, 20, w => { Text(w, token); w.Write(true); });
            Check(LocationSubscriptions(plugin).Contains(AlicePeer), "assigned administrator receives location subscription lease");
            Time.realtimeSinceStartup += 1; Call(plugin, "UpdateLocations");
            Check(((long)Get(wire, "queuedBytes")) > 0, "administrator location response can wait in paced transport queue");
            store.SetAdministrator(999, true, Alice, false); Time.realtimeSinceStartup += 1; wire.Tick();
            Check(!SentLocationKinds(alice.m_rpc).Contains(21), "permission revoked after snapshot queued blocks coordinates at actual send boundary");
            Call(plugin, "UpdateLocations");
            Check(!LocationSubscriptions(plugin).Contains(AlicePeer), "revoked permission removes live location subscription");
            wire.Clear(); alice.m_rpc.Sent.Clear(); store.SetAdministrator(999, true, Alice, true); Time.realtimeSinceStartup += 1;
            token = Id(); Dispatch(plugin, AlicePeer, 20, w => { Text(w, token); w.Write(true); }); Call(plugin, "UpdateLocations");
            Check(((long)Get(wire, "queuedBytes")) > 0, "lease expiration fixture queues actual administrator location response");
            Time.realtimeSinceStartup += 13; wire.Tick(); Call(plugin, "UpdateLocations");
            Check(!LocationSubscriptions(plugin).Contains(AlicePeer), "administrator subscription expires without lease renewal");
            Check(!SentLocationKinds(alice.m_rpc).Contains(21), "expired queued location snapshot is never sent after lease expiration");
            wire.Clear(); Time.realtimeSinceStartup += 1; token = Id(); Dispatch(plugin, AlicePeer, 20, w => { Text(w, token); w.Write(true); });
            Time.realtimeSinceStartup += 11; Dispatch(plugin, AlicePeer, 20, w => { Text(w, token); w.Write(true); });
            Time.realtimeSinceStartup += 2; Call(plugin, "UpdateLocations");
            Check(LocationSubscriptions(plugin).Contains(AlicePeer), "authenticated heartbeat renews subscription beyond original lease deadline");
            string olderToken = token; Time.realtimeSinceStartup += 1; token = Id(); Dispatch(plugin, AlicePeer, 20, w => { Text(w, token); w.Write(true); });
            Time.realtimeSinceStartup += 1; Dispatch(plugin, AlicePeer, 20, w => { Text(w, olderToken); w.Write(false); });
            Check(LocationSubscriptions(plugin).Contains(AlicePeer), "late unsubscribe for older token cannot remove replacement subscription");
            Time.realtimeSinceStartup += 1; Dispatch(plugin, AlicePeer, 20, w => { Text(w, token); w.Write(false); });
            Check(!LocationSubscriptions(plugin).Contains(AlicePeer), "administrator explicit unsubscribe drops lease immediately");
            wire.Clear(); alice.m_rpc.Sent.Clear();
        }

        private static void ClientLocationChecks(Plugin plugin, PermissionStore store, WireTransport wire)
        {
            ZNet.instance.Server = false; Time.realtimeSinceStartup += 1;
            Dispatch(plugin, 0, 1, w => { w.Write(true); Text(w, Alice); }); Call(plugin, "SetTrackingPlayers", true); Call(plugin, "UpdateLocations");
            string token = LocationToken(plugin); Check(token.Length == 32, "authorized client tracking creates subscription token");
            var roster = new List<PlayerLocation> { new PlayerLocation { PeerId = BobPeer, CharacterId = 102, Name = "Bob", X = 20, Y = 5000, Z = 30 } };
            LocationSnapshot(plugin, token, 1, roster);
            Check(CurrentLocations(plugin).Count == 1 && CurrentLocations(plugin)[0].Y == 5000, "client accepts authorized complete location snapshot for active token");
            roster[0].X = 99; LocationSnapshot(plugin, Id(), 2, roster);
            Check(CurrentLocations(plugin)[0].X == 20, "different subscription token cannot overwrite client location cache");
            LocationSnapshot(plugin, token, 1, roster);
            Check(CurrentLocations(plugin)[0].X == 20, "replayed sequence cannot overwrite current location cache");
            byte[] wrongWorld = Packet(21, w => { Text(w, token); w.Write(2L); Bytes(w, PlayerLocationCodec.Encode(roster)); }, false); wrongWorld[4] = 1;
            Reject(delegate { Call(plugin, "Dispatch", 0L, wrongWorld); }, "location snapshot from wrong world");
            Check(CurrentLocations(plugin)[0].X == 20, "wrong world is rejected before existing client cache changes");
            Reject(delegate { Call(plugin, "Dispatch", 0L, Packet(21, w => { Text(w, token); w.Write(2L); Bytes(w, PlayerLocationCodec.Encode(roster)); }, true)); }, "location snapshot trailing bytes");
            Check(CurrentLocations(plugin)[0].X == 20, "malformed complete frame cannot partially mutate client cache");
            Dispatch(plugin, 0, 22, w => Text(w, Id()));
            Check(CurrentLocations(plugin).Count == 1, "clear for older or unrelated subscription does not erase active locations");
            Dispatch(plugin, 0, 1, w => { w.Write(false); Text(w, Alice); });
            Check(CurrentLocations(plugin).Count == 0, "host permission revocation immediately removes client player locations");
            LocationSnapshot(plugin, token, 2, roster);
            Check(CurrentLocations(plugin).Count == 0, "late snapshot cannot repopulate cache after revocation");
            Dispatch(plugin, 0, 1, w => { w.Write(true); Text(w, Alice); }); Call(plugin, "UpdateLocations");
            string renewed = LocationToken(plugin);
            Check(renewed.Length == 32 && renewed != token, "regained permission creates distinct subscription context");
            LocationSnapshot(plugin, token, 3, roster);
            Check(CurrentLocations(plugin).Count == 0, "regained permission cannot accept queued old-subscription coordinates");
            LocationSnapshot(plugin, renewed, 1, roster);
            ZNetPeer unavailableServer = ZNet.instance.GetServerPeer(); bool wasReady = unavailableServer.Ready; unavailableServer.Ready = false;
            try { Call(plugin, "SetTrackingPlayers", false); }
            finally { unavailableServer.Ready = wasReady; }
            Check(CurrentLocations(plugin).Count == 0 && !((ConfigEntry<bool>)Get(plugin, "mapTracking")).Value,
                "client tracking switch removes positions and saves disabled setting even when unsubscribe server is unavailable");
            LocationSnapshot(plugin, renewed, 2, roster);
            Check(CurrentLocations(plugin).Count == 0, "late snapshot cannot repopulate disabled viewer");
            Call(plugin, "SetTrackingPlayers", true); Call(plugin, "UpdateLocations"); string toggled = LocationToken(plugin);
            Check(toggled != renewed && toggled.Length == 32, "turning tracking back on starts fresh subscription token");
            LocationSnapshot(plugin, toggled, 1, roster); Time.realtimeSinceStartup += 8;
            Check(CurrentLocations(plugin).Count == 0, "client loses positions after missing host snapshots for eight seconds");
            LocationSnapshot(plugin, toggled, 2, roster); Dispatch(plugin, 0, 22, w => Text(w, toggled));
            Check(CurrentLocations(plugin).Count == 0, "host clear packet removes matching active subscription positions");
            Call(plugin, "UpdateLocations"); toggled = LocationToken(plugin); LocationSnapshot(plugin, toggled, 3, roster);
            Check(CurrentLocations(plugin).Count == 1, "world reset fixture begins with populated authorized client positions");
            MapFailureAvailabilityChecks(plugin); LocationSnapshot(plugin, toggled, 4, roster);
            ZNet.instance.World = 1000; Call(plugin, "EnsureSession");
            Check(CurrentLocations(plugin).Count == 0, "actual world session reset clears received positions");
            Reject(delegate { Call(plugin, "Dispatch", 0L, Packet(21, w => { Text(w, toggled); w.Write(4L); Bytes(w, PlayerLocationCodec.Encode(roster)); }, false)); }, "late old-world packet after session reset");
            Check(CurrentLocations(plugin).Count == 0, "late earlier-world packet cannot recreate map pins");
            ZNet.instance.Server = true; ZNet.instance.World = 999; Call(plugin, "EnsureSession"); wire.Clear();
        }
    }
}
