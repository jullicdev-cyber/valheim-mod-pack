using System;
using System.IO;
using System.Text;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using WC = ValheimModPack.WorldCharacters.Plugin;

namespace ValheimModPack.InventoryAdmin
{
    internal static partial class RootServiceTests
    {
        private static void RadiusSet(Plugin plugin, string field, object value)
        { typeof(Plugin).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(plugin, value); }
        private static GroupRadiusStore RadiusStore(Plugin plugin) { return (GroupRadiusStore)Get(plugin, "radiusStore"); }
        private static GroupRadiusSettings RadiusCurrent(Plugin plugin) { return RadiusStore(plugin).Snapshot(999); }
        private static List<PlayerInfo> RadiusRoster(Plugin plugin) { return (List<PlayerInfo>)Call(plugin, "RadiusPlayers"); }
        private static GroupRadiusState RadiusCapture(Plugin plugin, GroupRadiusSettings settings)
        { return (GroupRadiusState)Call(plugin, "CaptureGroupRadius", settings, RadiusRoster(plugin), Time.realtimeSinceStartup); }
        private static GroupRadiusAdminView RadiusView(Plugin plugin) { return (GroupRadiusAdminView)Call(plugin, "GetGroupRadiusView"); }
        private static GroupRadiusFrame RadiusMotionFrame(Plugin plugin) { return (GroupRadiusFrame)Call(plugin, "GroupRadiusFrameForLocalPlayer"); }
        private static void RadiusEdit(Plugin plugin, long actor, byte action, long revision, Action<BinaryWriter> fields)
        {
            ((WireTransport)Get(plugin, "transport")).Clear(); Time.realtimeSinceStartup += 1;
            Dispatch(plugin, actor, 33, w => { w.Write(action); w.Write(revision); fields(w); });
        }
        private static void RadiusPulse(Plugin plugin, long actor, long character, bool alive, bool valid, Vector3 point)
        { Dispatch(plugin, actor, 30, w => { w.Write(character); w.Write(alive); w.Write(valid); w.Write(point.x); w.Write(point.y); w.Write(point.z); }); }
        private static void RadiusReceiveState(Plugin plugin, long sequence, GroupRadiusState state)
        { RadiusReceiveState(plugin, sequence, state, 0); }
        private static void RadiusReceiveState(Plugin plugin, long sequence, GroupRadiusState state, float age)
        { Dispatch(plugin, 0, 34, w => { w.Write(sequence); w.Write(age); Bytes(w, GroupRadiusCodec.EncodeState(state)); }); }
        private static GroupRadiusState RadiusWireState(ZRpc rpc)
        {
            GroupRadiusState state = null;
            foreach (byte[] bytes in ReceivedLocationTransportMessages(rpc))
            {
                using (var stream = new MemoryStream(bytes, false)) using (var reader = new BinaryReader(stream))
                {
                    reader.ReadInt32(); reader.ReadInt64(); if (reader.ReadInt32() != 34) continue;
                    reader.ReadInt64(); reader.ReadSingle(); state = GroupRadiusCodec.DecodeState(PolicyBinary.ReadBytes(reader, 16300));
                }
            }
            return state;
        }
        private static GroupRadiusState RadiusClientState(long generation)
        {
            return new GroupRadiusState { World = 999, Generation = generation, Enabled = true, Radius = 500,
                LeaderOwner = Alice, LeaderName = "Alice", LeaderX = 1000, LeaderY = 20, LeaderZ = 0,
                LeaderValid = true, LeaderAlive = true, LeaderEpoch = 20, ApprovedConnectedCount = 3 };
        }
        private static void GroupRadiusService(Plugin plugin)
        {
            var permissions = (PermissionStore)Get(plugin, "permissions"); var wire = (WireTransport)Get(plugin, "transport");
            ZNetPeer alice = ZNet.instance.Peers.Find(p => p.m_uid == AlicePeer), bob = ZNet.instance.Peers.Find(p => p.m_uid == BobPeer);
            bool oldAliceAdmin = permissions.IsAdministrator(999, Alice), oldBobAdmin = permissions.IsAdministrator(999, Bob);
            bool oldServer = ZNet.instance.Server, oldReady = WC.AdministrativeReady, oldDead = Player.m_localPlayer.Dead;
            Vector3 oldPoint = Player.m_localPlayer.transform.position;
            try
            {
                wire.Clear(); alice.m_rpc.Sent.Clear(); bob.m_rpc.Sent.Clear();
                ZNet.instance.Server = true; WC.AdministrativeReady = true; Player.m_localPlayer.Dead = false;
                Player.m_localPlayer.transform.position = new Vector3(0, 20, 0);
                permissions.SetAdministrator(999, true, Alice, true); permissions.SetAdministrator(999, true, Bob, false);
                RadiusAuthorizationChecks(plugin, bob);
                RadiusStableAccountChecks(plugin, alice, bob);
                RadiusLeaderChecks(plugin, alice, bob);
                RadiusGuardChecks(plugin, alice, bob, permissions, wire);
                RadiusCapacityChecks(plugin, alice, bob, wire);
                RadiusRefreshChecks();
                RadiusOptionalFailureChecks(plugin, alice, bob, wire);
                RadiusClientChecks(plugin, alice, bob, wire);
                RadiusCorruptionChecks(plugin);
            }
            finally
            {
                ZNet.instance.Server = oldServer; WC.AdministrativeReady = oldReady; Player.m_localPlayer.Dead = oldDead;
                Player.m_localPlayer.transform.position = oldPoint; GroupRadiusMotion.LogicalValid = true;
                permissions.SetAdministrator(999, true, Alice, oldAliceAdmin); permissions.SetAdministrator(999, true, Bob, oldBobAdmin);
                wire.Clear(); alice.m_rpc.Sent.Clear(); bob.m_rpc.Sent.Clear();
            }
        }
        private static void RadiusAuthorizationChecks(Plugin plugin, ZNetPeer bob)
        {
            long generation = RadiusCurrent(plugin).Generation;
            Check(!RadiusCurrent(plugin).Enabled && RadiusCurrent(plugin).LeaderOwner == "local-host", "group radius starts disabled and host-led");
            Reject(delegate { Dispatch(plugin, BobPeer, 31, w => { }); }, "ordinary player reads radius administration view");
            Reject(delegate { RadiusEdit(plugin, BobPeer, 0, generation, w => { w.Write(true); w.Write(750f); }); }, "ordinary player changes radius settings");
            Reject(delegate { RadiusEdit(plugin, BobPeer, 1, generation, w => w.Write(BobPeer)); }, "ordinary player appoints own leader account");
            Reject(delegate { RadiusEdit(plugin, BobPeer, 2, generation, w => { w.Write(BobPeer); w.Write(true); }); }, "ordinary player exempts own account");
            Check(RadiusCurrent(plugin).Generation == generation && !RadiusCurrent(plugin).Enabled && RadiusCurrent(plugin).ExemptOwners.Count == 0,
                "unauthorized radius commands have no persistence or permission effects");
            byte[] trailing = Packet(33, w => { w.Write((byte)0); w.Write(generation); w.Write(true); w.Write(750f); }, true);
            Reject(delegate { Call(plugin, "Dispatch", ZNet.Uid, trailing); }, "radius edit trailing bytes");
            Reject(delegate { Dispatch(plugin, ZNet.Uid, 33, w => { w.Write((byte)0); w.Write(generation); w.Write((byte)2); w.Write(750f); }); }, "noncanonical radius edit boolean");
            Reject(delegate { Dispatch(plugin, ZNet.Uid, 33, w => { w.Write((byte)2); w.Write(generation); w.Write(BobPeer); w.Write((byte)2); }); }, "noncanonical exemption boolean");
            Reject(delegate { Dispatch(plugin, ZNet.Uid, 33, w => { w.Write((byte)7); w.Write(generation); }); }, "unknown radius edit action");
            foreach (float invalid in new[] { Single.NaN, Single.PositiveInfinity, 49.9f, 10001f })
            {
                RadiusEdit(plugin, ZNet.Uid, 0, generation, w => { w.Write(true); w.Write(invalid); });
                Check(RadiusCurrent(plugin).Generation == generation && !RadiusCurrent(plugin).Enabled, "invalid radius edit returns error without changing settings");
            }
            RadiusEdit(plugin, ZNet.Uid, 1, generation, w => w.Write(999999L));
            Check(RadiusCurrent(plugin).Generation == generation && RadiusCurrent(plugin).LeaderOwner == "local-host", "disconnected target cannot appoint an owner");
            RadiusEdit(plugin, ZNet.Uid, 0, -1, w => { w.Write(true); w.Write(750f); });
            Check(RadiusCurrent(plugin).Generation == generation, "negative edit revision cannot persist");
            RadiusEdit(plugin, AlicePeer, 0, generation, w => { w.Write(true); w.Write(500f); });
            Check(RadiusCurrent(plugin).Enabled && RadiusCurrent(plugin).Generation == generation + 1, "assigned administrator can enable group radius");
            RadiusEdit(plugin, ZNet.Uid, 0, generation, w => { w.Write(false); w.Write(1000f); });
            Check(RadiusCurrent(plugin).Enabled && RadiusCurrent(plugin).Radius == 500 && RadiusCurrent(plugin).Generation == generation + 1,
                "stale host revision loses CAS and cannot overwrite another administrator");
            Check(RadiusView(plugin).Revision == generation + 1, "CAS conflict refreshes editor with current persisted revision");
        }
        private static void RadiusStableAccountChecks(Plugin plugin, ZNetPeer alice, ZNetPeer bob)
        {
            long revision = RadiusCurrent(plugin).Generation;
            RadiusEdit(plugin, AlicePeer, 1, revision, w => w.Write(AlicePeer));
            Check(RadiusCurrent(plugin).LeaderOwner == Alice && RadiusCurrent(plugin).ExemptOwners.Count == 0,
                "leader selection resolves authenticated account without automatically exempting administrators");
            IServiceTestSocket original = bob.m_socket; bob.m_socket = new UnsupportedServiceTestSocket { HostName = Alice.Substring(6), ThrowOnRead = true };
            try { RadiusEdit(plugin, AlicePeer, 2, RadiusCurrent(plugin).Generation, w => { w.Write(BobPeer); w.Write(true); }); }
            finally { bob.m_socket = original; }
            Check(RadiusCurrent(plugin).ExemptOwners.Contains(Bob) && !RadiusCurrent(plugin).ExemptOwners.Contains(Alice),
                "exemption resolves approved account independently of names and decorated socket claims");
            RadiusEdit(plugin, ZNet.Uid, 2, RadiusCurrent(plugin).Generation, w => { w.Write(ZNet.Uid); w.Write(true); });
            Check(RadiusCurrent(plugin).ExemptOwners.Contains("local-host"), "host can be explicitly exempt when a guest is leader");
            Call(plugin, "SendGroupRadiusView", ZNet.Uid); var view = RadiusView(plugin); view.ExemptPeers.Clear();
            Check(RadiusView(plugin).ExemptPeers.Contains(BobPeer) && RadiusView(plugin).ExemptPeers.Contains(ZNet.Uid), "radius editor snapshots cannot mutate cached exemptions");
            var reconnect = new ZNetPeer { m_uid = 92, m_playerID = 202, m_playerName = "Bob renamed", m_socket = new UnsupportedServiceTestSocket { ThrowOnRead = true } };
            try
            {
                bob.m_rpc.Connected = false; ZNet.instance.Peers.Remove(bob); Call(plugin, "Removed", bob);
                ZNet.instance.Peers.Add(reconnect); WC.Durable[92] = 50; WC.ApprovedOwners[reconnect] = Bob; WC.ApprovedConnections[reconnect.m_rpc] = reconnect;
                Call(plugin, "Register", reconnect); Call(plugin, "SendGroupRadiusView", ZNet.Uid);
                Check(RadiusView(plugin).ExemptPeers.Contains(92) && !RadiusView(plugin).ExemptPeers.Contains(BobPeer),
                    "renamed reconnected character with new peer UID retains stable account exemption");
                Check(RadiusStore(plugin).Snapshot(1000).ExemptOwners.Count == 0 && !RadiusStore(plugin).Snapshot(1000).Enabled,
                    "radius settings and exemptions do not leak into another world");
            }
            finally
            {
                ZNet.instance.Peers.Remove(reconnect); Call(plugin, "Removed", reconnect); WC.Durable.Remove(92);
                WC.ApprovedOwners.Remove(reconnect); WC.ApprovedConnections.Remove(reconnect.m_rpc);
                bob.m_rpc.Connected = true; ZNet.instance.Peers.Add(bob); Call(plugin, "Register", bob);
            }
            RadiusEdit(plugin, ZNet.Uid, 2, RadiusCurrent(plugin).Generation, w => { w.Write(ZNet.Uid); w.Write(false); });
        }
        private static void RadiusLeaderChecks(Plugin plugin, ZNetPeer alice, ZNetPeer bob)
        {
            ((IDictionary)Get(plugin, "radiusSamples")).Clear(); GroupRadiusSettings settings = RadiusCurrent(plugin);
            Check(!RadiusCapture(plugin, settings).LeaderValid, "guest leader without a received logical sample does not activate at origin");
            RadiusPulse(plugin, AlicePeer, alice.m_playerID, true, true, new Vector3(100, 5000, 200));
            GroupRadiusState state = RadiusCapture(plugin, settings); long epoch = state.LeaderEpoch;
            Check(state.LeaderValid && state.LeaderAlive && state.LeaderX == 100 && state.LeaderY == 5000 && state.LeaderZ == 200,
                "received logical leader coordinate and dungeon height survive capture");
            Check(state.ApprovedConnectedCount == 3 && epoch > 0, "captured state uses all approved connections and starts leader epoch");
            Time.realtimeSinceStartup += .01f; RadiusPulse(plugin, AlicePeer, alice.m_playerID, true, true, new Vector3(9000, 0, 9000));
            Check(RadiusCapture(plugin, settings).LeaderX == 100, "pulse flooding does not overwrite the accepted sample or renew its clock");
            Reject(delegate { RadiusPulse(plugin, AlicePeer, alice.m_playerID + 1, true, true, new Vector3(1, 2, 3)); }, "pulse character identity mismatch");
            Reject(delegate { RadiusPulse(plugin, AlicePeer, alice.m_playerID, false, true, new Vector3(1, 2, 3)); }, "valid sample claimed from dead leader");
            foreach (float invalid in new[] { Single.NaN, Single.PositiveInfinity, 100001f })
                Reject(delegate { RadiusPulse(plugin, AlicePeer, alice.m_playerID, true, true, new Vector3(invalid, 20, 0)); }, "invalid logical pulse coordinate");
            Reject(delegate { Call(plugin, "Dispatch", AlicePeer, Packet(30, w => { w.Write(alice.m_playerID); w.Write(true); w.Write(true); w.Write(1f); w.Write(2f); w.Write(3f); }, true)); }, "pulse trailing bytes");
            Check(RadiusCapture(plugin, settings).LeaderX == 100, "malformed pulses cannot partially replace valid leader sample");
            Time.realtimeSinceStartup += 2;
            Check(!RadiusCapture(plugin, settings).LeaderValid, "host cannot broadcast stale leader pulse beyond 1.5 second TTL");
            RadiusPulse(plugin, AlicePeer, alice.m_playerID, true, true, new Vector3(100, 5000, 200)); state = RadiusCapture(plugin, settings);
            Check(state.LeaderEpoch > epoch, "fresh leader recovery receives a new epoch after unavailable center"); epoch = state.LeaderEpoch;
            Time.realtimeSinceStartup += 1; RadiusPulse(plugin, AlicePeer, alice.m_playerID, true, true, new Vector3(110, 5000, 200)); state = RadiusCapture(plugin, settings);
            Check(state.LeaderEpoch == epoch, "ordinary leader walking does not generate portal grace epoch");
            Time.realtimeSinceStartup += 1; RadiusPulse(plugin, AlicePeer, alice.m_playerID, true, true, new Vector3(1000, 5000, 200)); state = RadiusCapture(plugin, settings);
            Check(state.LeaderEpoch > epoch, "large leader displacement increments grace epoch");
            Player.m_localPlayer.Dead = true; WC.AdministrativeReady = false;
            try
            {
                List<PlayerInfo> roster = RadiusRoster(plugin);
                Check(roster.Count == 3 && roster.Find(p => p.OwnerId == "local-host") != null, "dead protected local host remains an approved connected participant");
                state = RadiusCapture(plugin, settings); Check(state.LeaderValid && state.ApprovedConnectedCount == 3, "dead host does not turn live guest-led party into solo game");
                var hostSettings = settings.Clone(); hostSettings.LeaderOwner = "local-host";
                Check(!RadiusCapture(plugin, hostSettings).LeaderAlive && !RadiusCapture(plugin, hostSettings).LeaderValid, "dead host selected as leader suspends the center");
            }
            finally { Player.m_localPlayer.Dead = false; WC.AdministrativeReady = true; }
            Time.realtimeSinceStartup += 1; RadiusPulse(plugin, AlicePeer, alice.m_playerID, false, false, Vector3.zero);
            Check(!RadiusCapture(plugin, settings).LeaderValid && RadiusRoster(plugin).Count == 3, "dead guest leader loses center without leaving approved connection count");
            Time.realtimeSinceStartup += 1; RadiusPulse(plugin, AlicePeer, alice.m_playerID, true, true, new Vector3(1000, 20, 0));
            long originalCharacter = alice.m_playerID; alice.m_playerID++;
            try { Check(!RadiusCapture(plugin, settings).LeaderValid, "new character cannot inherit earlier character's pulse"); }
            finally { alice.m_playerID = originalCharacter; }
            Call(plugin, "Removed", alice);
            Check(!((IDictionary)Get(plugin, "radiusSamples")).Contains(alice.m_rpc), "disconnect removes exact RPC logical sample");
            Call(plugin, "Register", alice);
        }
        private static void RadiusGuardChecks(Plugin plugin, ZNetPeer alice, ZNetPeer bob, PermissionStore permissions, WireTransport wire)
        {
            wire.Clear(); alice.m_rpc.Sent.Clear(); bob.m_rpc.Sent.Clear(); Time.realtimeSinceStartup += 1;
            Dispatch(plugin, AlicePeer, 31, w => { });
            Check((long)Get(wire, "queuedBytes") > 0, "authorized radius editor response can wait in paced transport");
            permissions.SetAdministrator(999, true, Alice, false); Time.realtimeSinceStartup += .2f; wire.Tick();
            Check(!SentLocationKinds(alice.m_rpc).Contains(32), "administrator revoked after queueing cannot receive private exemption editor view");
            permissions.SetAdministrator(999, true, Alice, true); wire.Clear(); alice.m_rpc.Sent.Clear(); bob.m_rpc.Sent.Clear();
            Time.realtimeSinceStartup += 1; RadiusPulse(plugin, AlicePeer, alice.m_playerID, true, true, new Vector3(1000, 20, 0));
            Call(plugin, "BroadcastGroupRadius", RadiusCurrent(plugin), Time.realtimeSinceStartup);
            Time.realtimeSinceStartup += 1.5f; wire.Tick();
            Check(!SentLocationKinds(alice.m_rpc).Contains(34) && !SentLocationKinds(bob.m_rpc).Contains(34), "expired queued radius state does not leave transport after freshness deadline");
            wire.Clear(); alice.m_rpc.Sent.Clear(); bob.m_rpc.Sent.Clear(); Time.realtimeSinceStartup += 1;
            RadiusPulse(plugin, AlicePeer, alice.m_playerID, true, true, new Vector3(1000, 20, 0)); Time.realtimeSinceStartup += 1.4f;
            Call(plugin, "BroadcastGroupRadius", RadiusCurrent(plugin), Time.realtimeSinceStartup); Time.realtimeSinceStartup += .2f; wire.Tick();
            Check(!SentLocationKinds(alice.m_rpc).Contains(34) && !SentLocationKinds(bob.m_rpc).Contains(34),
                "recent rebroadcast cannot extend lifetime of original almost-stale guest pulse in queue");
            wire.Clear(); alice.m_rpc.Sent.Clear(); bob.m_rpc.Sent.Clear(); Time.realtimeSinceStartup += 1;
            RadiusPulse(plugin, AlicePeer, alice.m_playerID, true, true, new Vector3(1000, 20, 0)); Call(plugin, "BroadcastGroupRadius", RadiusCurrent(plugin), Time.realtimeSinceStartup);
            Time.realtimeSinceStartup += .25f; RadiusPulse(plugin, AlicePeer, alice.m_playerID, true, true, new Vector3(1010, 20, 0));
            Call(plugin, "BroadcastGroupRadius", RadiusCurrent(plugin), Time.realtimeSinceStartup); Time.realtimeSinceStartup += .1f; wire.Tick();
            GroupRadiusState bobState = RadiusWireState(bob.m_rpc), aliceState = RadiusWireState(alice.m_rpc);
            Check(bobState != null && bobState.OwnExempt && !bobState.OwnIsLeader && bobState.LeaderX == 1010,
                "ordinary participant receives only own exemption and newest coalesced center");
            Check(aliceState != null && aliceState.OwnIsLeader && !aliceState.OwnExempt, "appointed administrator leader exemption is explicit leader status only");
            Check(SentLocationKinds(bob.m_rpc).Count == 1 && bob.m_rpc.Sent[0].Size() < 16384, "radius snapshot is one bounded atomic message per recipient");
            foreach (byte[] packet in ReceivedLocationTransportMessages(bob.m_rpc))
            {
                using (var stream = new MemoryStream(packet, false)) using (var reader = new BinaryReader(stream))
                { reader.ReadInt32(); reader.ReadInt64(); if (reader.ReadInt32() != 34) continue; reader.ReadInt64(); float age = reader.ReadSingle();
                    Check(age > .09f && age < .11f, "state envelope refresh includes real paced queue delay in sample age"); }
            }
            wire.Clear(); bob.m_rpc.Sent.Clear(); Call(plugin, "BroadcastGroupRadius", RadiusCurrent(plugin), Time.realtimeSinceStartup);
            long durable = WC.Durable[BobPeer]; WC.Durable.Remove(BobPeer);
            try { Time.realtimeSinceStartup += .1f; wire.Tick(); Check(!SentLocationKinds(bob.m_rpc).Contains(34), "unapproved recipient cannot receive previously queued radius state"); }
            finally { WC.Durable[BobPeer] = durable; }
            wire.Clear(); alice.m_rpc.Sent.Clear(); bob.m_rpc.Sent.Clear(); Call(plugin, "BroadcastGroupRadius", RadiusCurrent(plugin), Time.realtimeSinceStartup);
            RadiusSet(plugin, "world", 1000L);
            try { Time.realtimeSinceStartup += .1f; wire.Tick(); Check(alice.m_rpc.Sent.Count == 0 && bob.m_rpc.Sent.Count == 0, "world change invalidates queued state frames"); }
            finally { RadiusSet(plugin, "world", 999L); }
            wire.Clear();
        }
        private static void RadiusCapacityChecks(Plugin plugin, ZNetPeer alice, ZNetPeer bob, WireTransport wire)
        {
            var added = new List<ZNetPeer>();
            try
            {
                wire.Clear(); alice.m_rpc.Sent.Clear(); bob.m_rpc.Sent.Clear(); RadiusSet(plugin, "radiusBroadcastCursor", 0);
                for (int i = 0; i < 125; ++i)
                {
                    var peer = new ZNetPeer { m_uid = 11000 + i, m_playerID = 21000 + i, m_playerName = "Radius participant " + i,
                        m_socket = new UnsupportedServiceTestSocket { ThrowOnRead = true } };
                    added.Add(peer); ZNet.instance.Peers.Add(peer); WC.Durable[peer.m_uid] = 50;
                    WC.ApprovedOwners[peer] = "Steam_" + (76561198000001000UL + (ulong)i).ToString(); WC.ApprovedConnections[peer.m_rpc] = peer;
                    Call(plugin, "Register", peer);
                }
                Check(RadiusRoster(plugin).Count == 128, "complete approved radius roster includes the maximum 128 participants");
                for (int pass = 0; pass < 8; ++pass)
                {
                    if (pass == 0) Time.realtimeSinceStartup += .25f;
                    RadiusPulse(plugin, AlicePeer, alice.m_playerID, true, true, new Vector3(1000, 20, 0));
                    Call(plugin, "BroadcastGroupRadius", RadiusCurrent(plugin), Time.realtimeSinceStartup);
                    for (int frame = 0; frame < 15; ++frame) { Time.realtimeSinceStartup += 1f / 60f; wire.Tick(); }
                }
                bool everyParticipantReceived = SentLocationKinds(alice.m_rpc).Contains(34) && SentLocationKinds(bob.m_rpc).Contains(34);
                foreach (ZNetPeer peer in added) if (!SentLocationKinds(peer.m_rpc).Contains(34)) everyParticipantReceived = false;
                Check(everyParticipantReceived, "rotating recipient order under 64-message capacity eventually delivers radius state to all 128 participants");
                GroupRadiusState last = RadiusWireState(added[added.Count - 1].m_rpc);
                Check(last != null && last.ApprovedConnectedCount == 128 && !last.OwnExempt && !last.OwnIsLeader,
                    "last roster participant receives bounded state with correct count and own flags");
            }
            finally
            {
                foreach (ZNetPeer peer in added)
                {
                    ZNet.instance.Peers.Remove(peer); Call(plugin, "Removed", peer); WC.Durable.Remove(peer.m_uid);
                    WC.ApprovedOwners.Remove(peer); WC.ApprovedConnections.Remove(peer.m_rpc);
                }
                wire.Clear(); alice.m_rpc.Sent.Clear(); bob.m_rpc.Sent.Clear();
            }
        }
        private static void RadiusRefreshChecks()
        {
            var wire = new WireTransport(); var rpc = new ZRpc(); int refreshed = 0;
            wire.SendGuarded(rpc, new byte[] { 1 }, "denied", () => false, () => { ++refreshed; return new byte[] { 2 }; });
            Time.realtimeSinceStartup += .1f; wire.Tick();
            Check(refreshed == 0 && rpc.Sent.Count == 0 && (long)Get(wire, "queuedBytes") == 0, "refresh factory cannot run or disclose data after guard denies authorization");
            wire.SendGuarded(rpc, new byte[] { 1 }, "size", () => true, () => new byte[] { 2, 3 });
            wire.SendGuarded(rpc, new byte[] { 1 }, "throw", () => true, delegate { throw new IOException("Stale source."); });
            wire.SendGuarded(rpc, new byte[] { 1 }, "null", () => true, () => null);
            wire.SendGuarded(rpc, new byte[] { 1, 1 }, "okay", () => true, () => new byte[] { 7, 8 });
            Time.realtimeSinceStartup += .1f; wire.Tick();
            List<byte[]> received = ReceivedLocationTransportMessages(rpc);
            Check(received.Count == 1 && received[0].Length == 2 && received[0][0] == 7 && received[0][1] == 8,
                "invalid refreshed frames are discarded while following same-size refresh delivers actual send-time bytes");
            Check((long)Get(wire, "queuedBytes") == 0, "throwing null and wrong-sized refreshes release exact queue byte budget");
            rpc.Sent.Clear(); wire.SendGuarded(rpc, new byte[] { 1 }, "replace", () => true, () => new byte[] { 2 });
            wire.SendGuarded(rpc, new byte[] { 3 }, "replace", () => true, () => new byte[] { 4 });
            Time.realtimeSinceStartup += .1f; wire.Tick(); received = ReceivedLocationTransportMessages(rpc);
            Check(received.Count == 1 && received[0][0] == 4, "coalescing replaces the refresh factory together with frame and guard");
        }
        private static void RadiusOptionalFailureChecks(Plugin plugin, ZNetPeer alice, ZNetPeer bob, WireTransport wire)
        {
            wire.Clear(); Time.realtimeSinceStartup += 1; RadiusPulse(plugin, AlicePeer, alice.m_playerID, true, true, new Vector3(1000, 20, 0));
            for (int i = 0; i < 64; ++i) wire.Send(bob.m_rpc, new byte[] { 0x5a });
            int warnings = plugin.Logger.Errors.FindAll(s => s.StartsWith("Group radius temporarily paused:", StringComparison.Ordinal)).Count;
            RadiusSet(plugin, "radiusBroadcastAt", 0f); Call(plugin, "UpdateGroupRadiusSafely");
            Check(Get(plugin, "radiusState") != null && (long)Get(wire, "queuedBytes") == 64,
                "optional recipient capacity failure preserves queued inventory bytes and continues local radius delivery");
            GroupRadiusStore original = RadiusStore(plugin);
            string failedRoot = Path.Combine(Path.GetDirectoryName(BepInEx.Paths.BepInExRootPath), "closed-radius-test-store");
            var closed = new GroupRadiusStore(failedRoot); closed.Dispose(); RadiusSet(plugin, "radiusStore", closed);
            try
            {
                RadiusSet(plugin, "radiusBroadcastAt", 0f); Call(plugin, "UpdateGroupRadiusSafely");
                RadiusSet(plugin, "radiusBroadcastAt", 0f); Call(plugin, "UpdateGroupRadiusSafely");
                Check(Get(plugin, "radiusState") == null && (long)Get(wire, "queuedBytes") == 64,
                    "optional radius settings failure pauses local state without losing queued inventory bytes");
            }
            finally { RadiusSet(plugin, "radiusStore", original); }
            Check(plugin.Logger.Errors.FindAll(s => s.StartsWith("Group radius temporarily paused:", StringComparison.Ordinal)).Count == warnings + 1,
                "optional group radius failures are contained and their repeated warning is throttled");
            wire.Clear();
        }
        private static void RadiusClientChecks(Plugin plugin, ZNetPeer alice, ZNetPeer bob, WireTransport wire)
        {
            ZNet.instance.Server = false; RadiusSet(plugin, "radiusState", null); RadiusSet(plugin, "radiusView", null);
            RadiusSet(plugin, "radiusReceivedSequence", 0L); RadiusSet(plugin, "radiusWasActive", false);
            Dispatch(plugin, 0, 1, w => { w.Write(false); Text(w, Bob); });
            var state = RadiusClientState(RadiusCurrent(plugin).Generation); long sequence = 1000;
            byte[] packet = Packet(34, w => { w.Write(sequence); w.Write(0f); Bytes(w, GroupRadiusCodec.EncodeState(state)); }, false);
            Call(plugin, "Receive", bob.m_rpc, Frame(Id(), packet.Length, 0, packet));
            Check(Get(plugin, "radiusState") == null, "client rejects radius frame arriving from non-server peer before acceptance");
            Time.realtimeSinceStartup += 1; Call(plugin, "Receive", alice.m_rpc, Frame(Id(), packet.Length, 0, packet));
            GroupRadiusFrame frame = RadiusMotionFrame(plugin); float receivedAt = (float)Get(plugin, "radiusReceivedAt");
            Check(frame != null && frame.Active && frame.GraceUntil == Time.realtimeSinceStartup + 10 && !(bool)Get(plugin, "authorized"),
                "ordinary participant receives active radius and ten second outside grace without inventory permission");
            state.LeaderX = 1500; Time.realtimeSinceStartup += .5f; RadiusReceiveState(plugin, sequence, state);
            Check(((GroupRadiusState)Get(plugin, "radiusState")).LeaderX == 1000 && (float)Get(plugin, "radiusReceivedAt") == receivedAt,
                "replayed sequence cannot change center or renew freshness");
            state.Generation--; RadiusReceiveState(plugin, ++sequence, state);
            Check((long)Get(plugin, "radiusReceivedSequence") == 1000 && (float)Get(plugin, "radiusReceivedAt") == receivedAt,
                "older enabled settings generation cannot overwrite active state or renew clock"); state.Generation++;
            Reject(delegate { Call(plugin, "Dispatch", 0L, Packet(34, w => { w.Write(sequence + 1); w.Write(0f); Bytes(w, GroupRadiusCodec.EncodeState(state)); }, true)); }, "client state trailing bytes");
            Check(((GroupRadiusState)Get(plugin, "radiusState")).LeaderX == 1000, "malformed radius state cannot partially alter current center");
            var wrongWorld = state.Clone(); wrongWorld.World = 1000;
            Reject(delegate { RadiusReceiveState(plugin, sequence + 1, wrongWorld); }, "radius state inner world mismatch");
            foreach (float age in new[] { Single.NaN, Single.PositiveInfinity, -.01f, 1.5f, 10000f })
                Reject(delegate { RadiusReceiveState(plugin, sequence + 1, state, age); }, "invalid authenticated source sample age");
            Time.realtimeSinceStartup = receivedAt + 1.5f;
            Check(!RadiusMotionFrame(plugin).Active, "client boundary suspends exactly when state reaches 1.5 second TTL");
            Time.realtimeSinceStartup += 1; state.LeaderX = 1000; RadiusReceiveState(plugin, ++sequence, state); frame = RadiusMotionFrame(plugin);
            float grace = frame.GraceUntil; Check(frame.Active && grace == Time.realtimeSinceStartup + 10, "fresh state reactivation outside circle starts grace after outage");
            for (int i = 0; i < 10; ++i) { Time.realtimeSinceStartup += 1; state.LeaderX += 1; RadiusReceiveState(plugin, ++sequence, state); frame = RadiusMotionFrame(plugin); }
            Check(frame.Active && frame.GraceUntil == grace && Time.realtimeSinceStartup == grace, "ordinary leader updates do not extend ten second grace");
            Time.realtimeSinceStartup += 1; RadiusReceiveState(plugin, ++sequence, state, 1.25f);
            float agedReceipt = (float)Get(plugin, "radiusReceivedAt");
            Check(agedReceipt == Time.realtimeSinceStartup - 1.25f && RadiusMotionFrame(plugin).Active, "client freshness uses original sample age rather than rebroadcast receipt time");
            Time.realtimeSinceStartup += .25f;
            Check(!RadiusMotionFrame(plugin).Active, "aged guest state expires when original pulse reaches TTL even after fresh rebroadcast");
            state.LeaderEpoch++; state.LeaderX += 1000; Time.realtimeSinceStartup += .25f; RadiusReceiveState(plugin, ++sequence, state); frame = RadiusMotionFrame(plugin);
            Check(frame.GraceUntil == Time.realtimeSinceStartup + 10, "leader portal epoch grants fresh grace to outside participant");
            state.Generation++; state.Radius = 100; Time.realtimeSinceStartup += .25f; RadiusReceiveState(plugin, ++sequence, state); frame = RadiusMotionFrame(plugin);
            Check(frame.GraceUntil == Time.realtimeSinceStartup + 10, "radius shrink grants outside participant ten second adjustment grace");
            state.OwnExempt = true; Time.realtimeSinceStartup += .25f; RadiusReceiveState(plugin, ++sequence, state);
            Check(!RadiusMotionFrame(plugin).Active && RadiusMotionFrame(plugin).Exempt, "explicit own exemption suspends client enforcement immediately");
            state.OwnExempt = false; state.OwnIsLeader = true; RadiusReceiveState(plugin, ++sequence, state);
            Check(!RadiusMotionFrame(plugin).Active && RadiusMotionFrame(plugin).Exempt, "leader is free without being stored as explicit exemption");
            state.OwnIsLeader = false; state.LeaderAlive = false; RadiusReceiveState(plugin, ++sequence, state); Check(!RadiusMotionFrame(plugin).Active, "dead leader disables client boundary");
            state.LeaderAlive = true; state.ApprovedConnectedCount = 1; RadiusReceiveState(plugin, ++sequence, state); Check(!RadiusMotionFrame(plugin).Active, "solo approved participant has no client boundary");
            state.ApprovedConnectedCount = 3; state.Enabled = false; RadiusReceiveState(plugin, ++sequence, state); Check(!RadiusMotionFrame(plugin).Active, "host disable immediately removes client boundary");
            var failDisabled = state.Clone(); failDisabled.Generation = 0; RadiusReceiveState(plugin, ++sequence, failDisabled);
            Check(((GroupRadiusState)Get(plugin, "radiusState")).Generation == 0 && (long)Get(plugin, "radiusReceivedSequence") == sequence && !RadiusMotionFrame(plugin).Active,
                "authenticated corrupt settings fallback with lower generation is accepted immediately when disabled");
            state.Enabled = true; RadiusReceiveState(plugin, ++sequence, state); Call(plugin, "Removed", alice);
            Check(Get(plugin, "radiusState") == null && (long)Get(plugin, "radiusReceivedSequence") == 0 && !RadiusMotionFrameExists(plugin),
                "server disconnect clears state and cannot retain a travel restriction");
            ZNet.instance.Server = true; Call(plugin, "Register", alice);
            string directory = Path.Combine(Path.GetDirectoryName(BepInEx.Paths.BepInExRootPath), "ValheimModpack", "InventoryAdmin");
            Call(plugin, "InitializeGroupRadius", directory); RadiusSet(plugin, "radiusView", null); RadiusSet(plugin, "radiusState", null); wire.Clear();
        }
        private static bool RadiusMotionFrameExists(Plugin plugin) { return RadiusMotionFrame(plugin) != null; }
        private static void RadiusCorruptionChecks(Plugin plugin)
        {
            Call(plugin, "SendGroupRadiusView", ZNet.Uid); GroupRadiusAdminView before = RadiusView(plugin);
            Check(before != null && before.Revision > 0 && !before.ReadOnly, "runtime corruption fixture starts from editable persisted settings");
            string directory = Path.Combine(Path.GetDirectoryName(BepInEx.Paths.BepInExRootPath), "ValheimModpack", "InventoryAdmin");
            string path = Path.Combine(directory, "group-radius", "999.dat"); byte[] clean = File.ReadAllBytes(path), damaged = (byte[])clean.Clone();
            damaged[10] ^= 1; File.WriteAllBytes(path, damaged);
            try
            {
                RadiusEdit(plugin, ZNet.Uid, 0, before.Revision, w => { w.Write(true); w.Write(750f); });
                Check(RadiusStore(plugin).IsCorrupt(999) && !RadiusCurrent(plugin).Enabled, "runtime settings damage detected during edit fails disabled");
                GroupRadiusAdminView view = RadiusView(plugin);
                Check(view != null && view.ReadOnly && !view.Enabled && view.Notice.Length != 0,
                    "corrupt fallback with lower generation replaces prior editable view with readonly diagnostic");
                Check(EqualRadiusBytes(File.ReadAllBytes(path), damaged), "network edit cannot overwrite externally damaged settings bytes");
                RadiusEdit(plugin, ZNet.Uid, 2, 0, w => { w.Write(BobPeer); w.Write(false); });
                Check(EqualRadiusBytes(File.ReadAllBytes(path), damaged), "further host edit leaves corrupt file untouched");
            }
            finally
            {
                Call(plugin, "ResetGroupRadius"); File.WriteAllBytes(path, clean); Call(plugin, "InitializeGroupRadius", directory);
            }
        }
        private static bool EqualRadiusBytes(byte[] left, byte[] right)
        { if (left.Length != right.Length) return false; for (int i = 0; i < left.Length; ++i) if (left[i] != right[i]) return false; return true; }
    }
}
