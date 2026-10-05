using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;
using WC = ValheimModPack.WorldCharacters.Plugin;

namespace ValheimModPack.InventoryAdmin
{
    public sealed partial class Plugin
    {
        private ConfigEntry<KeyboardShortcut> radiusShortcut;
        private GroupRadiusStore radiusStore;
        private GroupRadiusState radiusState;
        private GroupRadiusAdminView radiusView;
        private float radiusReceivedAt, radiusViewAt, radiusPulseAt, radiusBroadcastAt, radiusAdminRequestAt, radiusNoticeAt;
        private long radiusSequence, radiusReceivedSequence, radiusEpoch;
        private int radiusBroadcastCursor;
        private float radiusGraceUntil;
        private float radiusGraceRadius;
        private float radiusLeaderSampleAt, radiusErrorAt;
        private bool radiusWasActive, radiusHadLeader;
        private Vector3 radiusLastLeader;
        private string radiusLastLeaderOwner = "";
        private long radiusGraceEpoch = -1, radiusGraceGeneration = -1;
        private readonly Dictionary<ZRpc, RadiusSample> radiusSamples = new Dictionary<ZRpc, RadiusSample>();
        private readonly Dictionary<long, float> radiusRequestTimes = new Dictionary<long, float>();
        private sealed class RadiusSample { internal Vector3 Position; internal bool Alive, Valid; internal float At; internal long Character; }

        private void BindGroupRadius()
        {
            radiusShortcut = Config.Bind("Controls", "OpenGroupRadius", new KeyboardShortcut(KeyCode.F11, KeyCode.LeftControl),
                "Open group radius administration. Rebind in Bindrune. Only the host and appointed administrators can edit it.");
            GroupRadiusMotion.Bind(GroupRadiusFrameForLocalPlayer);
        }
        private void InitializeGroupRadius(string directory)
        {
            try { radiusStore = new GroupRadiusStore(directory); radiusStore.Snapshot(world); }
            catch (Exception error) { if (radiusStore != null) radiusStore.Dispose(); radiusStore = null; Logger.LogWarning("Group radius disabled: " + error.Message); }
        }
        private void ResetGroupRadius()
        {
            if (radiusStore != null) radiusStore.Dispose(); radiusStore = null;
            radiusState = null; radiusView = null; radiusSamples.Clear(); radiusRequestTimes.Clear();
            radiusReceivedAt = radiusViewAt = radiusPulseAt = radiusBroadcastAt = radiusAdminRequestAt = radiusNoticeAt = 0;
            radiusSequence = radiusReceivedSequence = radiusEpoch = 0; radiusGraceUntil = radiusGraceRadius = 0;
            radiusBroadcastCursor = 0;
            radiusLeaderSampleAt = radiusErrorAt = 0;
            radiusWasActive = radiusHadLeader = false; radiusLastLeaderOwner = "";
            radiusGraceEpoch = radiusGraceGeneration = -1;
            GroupRadiusMotion.ResetWorld();
        }
        private static bool RadiusPoint(Vector3 point)
        { return GroupRadiusPolicy.Coordinate(point.x) && GroupRadiusPolicy.Coordinate(point.y) && GroupRadiusPolicy.Coordinate(point.z); }
        private GroupRadiusSettings RadiusSettings()
        { return radiusStore == null ? new GroupRadiusSettings() : radiusStore.Snapshot(world); }
        private void UpdateGroupRadiusSafely()
        {
            try { UpdateGroupRadius(); }
            catch (Exception error)
            {
                radiusState = null; radiusWasActive = false;
                if (Time.realtimeSinceStartup >= radiusErrorAt)
                { radiusErrorAt = Time.realtimeSinceStartup + 5; Logger.LogWarning("Group radius temporarily paused: " + error.Message); }
            }
        }
        private void UpdateGroupRadius()
        {
            float now = Time.realtimeSinceStartup;
            if (IsHost)
            {
                foreach (ZRpc rpc in radiusSamples.Keys.Where(rpc => !peers.ContainsKey(rpc) || !rpc.IsConnected()).ToArray()) radiusSamples.Remove(rpc);
                if (now >= radiusBroadcastAt)
                {
                    GroupRadiusSettings settings = RadiusSettings();
                    radiusBroadcastAt = now + (settings.Enabled ? .25f : 1f);
                    BroadcastGroupRadius(settings, now);
                }
            }
            else if (WC.AdministrativeReady && now >= radiusPulseAt)
            {
                radiusPulseAt = now + (radiusState == null || radiusState.Enabled ? .25f : 1f);
                Vector3 point = Vector3.zero; Player player = Player.m_localPlayer;
                bool alive = player != null && !player.IsDead();
                bool valid = alive && GroupRadiusMotion.TryGetLogicalPosition(player, out point);
                if (!valid) point = Vector3.zero;
                long character = player == null ? 0 : player.GetPlayerID();
                byte[] packet = Packet(30, w => { w.Write(character); w.Write(alive); w.Write(valid); w.Write(point.x); w.Write(point.y); w.Write(point.z); });
                ZNetPeer server = network.GetServerPeer(); ZNet session = network; long id = world;
                if (server != null && server.IsReady())
                    transport.SendGuarded(server.m_rpc, packet, "group-radius-pulse",
                        () => ReferenceEquals(network, session) && world == id && WC.AdministrativeReady && ReferenceEquals(server, network.GetServerPeer())
                            && Time.realtimeSinceStartup - now < 1.5f);
            }
            if (CanUse() && window.IsVisible && now >= radiusAdminRequestAt)
            { radiusAdminRequestAt = now + 2; RequestPlayers(); }
            GroupRadiusMotion.Tick();
        }
        private List<PlayerInfo> RadiusPlayers()
        {
            var result = new List<PlayerInfo>();
            // A dead but still connected host counts toward party size. The
            // inventory readiness predicate deliberately excludes dead players.
            if (Player.m_localPlayer != null && WC.GetAdministrativeDurableSequence(ZNet.GetUID()) >= 0)
                result.Add(new PlayerInfo { PeerId = ZNet.GetUID(), CharacterId = Player.m_localPlayer.GetPlayerID(),
                    Name = Player.m_localPlayer.GetPlayerName(), OwnerId = "local-host", IsAdmin = true,
                    InventoryAvailable = WC.AdministrativeReady });
            foreach (ZNetPeer peer in peers.Values) if (Ready(peer)) result.Add(PeerInfo(peer.m_uid));
            if (result.Count > GroupRadiusPolicy.MaximumPlayers) throw new InvalidDataException("Too many group radius players.");
            return result;
        }
        private GroupRadiusState CaptureGroupRadius(GroupRadiusSettings settings, List<PlayerInfo> players, float now)
        {
            radiusLeaderSampleAt = now;
            var state = new GroupRadiusState { World = world, Generation = settings.Generation, Enabled = settings.Enabled,
                Radius = settings.Radius, LeaderOwner = settings.LeaderOwner, ApprovedConnectedCount = players.Count };
            PlayerInfo leader = players.FirstOrDefault(p => p.OwnerId == settings.LeaderOwner);
            if (leader != null)
            {
                state.LeaderName = LocationName(leader.Name);
                Vector3 point = Vector3.zero; bool alive = false, valid = false;
                if (leader.PeerId == ZNet.GetUID())
                {
                    Player player = Player.m_localPlayer; alive = player != null && !player.IsDead();
                    valid = alive && GroupRadiusMotion.TryGetLogicalPosition(player, out point);
                }
                else
                {
                    ZNetPeer peer = Peer(leader.PeerId); RadiusSample sample;
                    if (peer != null && radiusSamples.TryGetValue(peer.m_rpc, out sample) && sample.Character == leader.CharacterId
                        && now >= sample.At && now - sample.At < GroupRadiusPolicy.FreshnessSeconds)
                    { alive = sample.Alive; valid = sample.Valid && alive; point = sample.Position; if (valid) radiusLeaderSampleAt = sample.At; }
                }
                if (valid && RadiusPoint(point))
                {
                    state.LeaderValid = state.LeaderAlive = true; state.LeaderX = point.x; state.LeaderY = point.y; state.LeaderZ = point.z;
                    bool jump = !radiusHadLeader || radiusLastLeaderOwner != settings.LeaderOwner
                        || GroupRadiusPolicy.DistanceXZ(point.x, point.z, radiusLastLeader.x, radiusLastLeader.z) > Math.Max(50, settings.Radius * .25f);
                    if (jump) radiusEpoch = checked(radiusEpoch + 1);
                    radiusHadLeader = true; radiusLastLeaderOwner = settings.LeaderOwner; radiusLastLeader = point;
                }
                else radiusHadLeader = false;
            }
            else { state.LeaderName = T("Ведущий не подключён", "Leader is offline"); radiusHadLeader = false; }
            state.LeaderEpoch = radiusEpoch;
            return state;
        }
        private byte[] GroupRadiusStatePacket(long sequence, float age, byte[] bytes)
        { return Packet(34, w => { w.Write(sequence); w.Write(age); PolicyBinary.WriteBytes(w, bytes, 16300); }); }
        private void BroadcastGroupRadius(GroupRadiusSettings settings, float now)
        {
            List<PlayerInfo> players = RadiusPlayers(); GroupRadiusState basis = CaptureGroupRadius(settings, players, now);
            float sampledAt = radiusLeaderSampleAt;
            long sequence = checked(++radiusSequence); ZNet session = network; long id = world;
            int first = players.Count == 0 ? 0 : radiusBroadcastCursor % players.Count;
            radiusBroadcastCursor = players.Count == 0 ? 0 : (first + 16) % players.Count;
            for (int index = 0; index < players.Count; ++index)
            {
                PlayerInfo participant = players[(first + index) % players.Count];
                GroupRadiusState state = basis.Clone();
                state.OwnIsLeader = participant.OwnerId == settings.LeaderOwner;
                state.OwnExempt = settings.ExemptOwners.Contains(participant.OwnerId);
                byte[] bytes = GroupRadiusCodec.EncodeState(state);
                byte[] packet = GroupRadiusStatePacket(sequence, basis.LeaderValid ? Math.Max(0, now - sampledAt) : 0, bytes);
                if (participant.PeerId == ZNet.GetUID()) { Dispatch(0, packet); continue; }
                ZNetPeer recipient = Peer(participant.PeerId); if (recipient == null) continue;
                long peerId = participant.PeerId; ZRpc rpc = recipient.m_rpc;
                try
                {
                    transport.SendGuarded(rpc, packet, "group-radius-state", () => ReferenceEquals(network, session) && world == id
                        && ReferenceEquals(Peer(peerId), recipient) && Ready(recipient) && Time.realtimeSinceStartup - now < 1.5f
                        && (!basis.LeaderValid || Time.realtimeSinceStartup - sampledAt < GroupRadiusPolicy.FreshnessSeconds),
                        () => GroupRadiusStatePacket(sequence, basis.LeaderValid ? Math.Max(0, Time.realtimeSinceStartup - sampledAt) : 0, bytes));
                }
                catch (IOException) { } // Optional state expires safely; the next pass rotates recipients under queue pressure.
            }
        }
        private GroupRadiusFrame GroupRadiusFrameForLocalPlayer()
        {
            float now = Time.realtimeSinceStartup;
            bool valid = network != null && ReferenceEquals(network, ZNet.instance) && world != 0 && WC.AdministrativeReady
                && radiusState != null && radiusState.World == world && GroupRadiusPolicy.Active(radiusState, radiusReceivedAt, now);
            if (valid && (!radiusWasActive || radiusGraceEpoch != radiusState.LeaderEpoch
                || radiusGraceGeneration != radiusState.Generation && radiusState.Radius < radiusGraceRadius))
            {
                Vector3 logical;
                bool outside = GroupRadiusMotion.TryGetLogicalPosition(Player.m_localPlayer, out logical)
                    && GroupRadiusPolicy.DistanceXZ(logical.x, logical.z, radiusState.LeaderX, radiusState.LeaderZ) > radiusState.Radius;
                radiusGraceUntil = outside ? now + 10 : now; radiusGraceEpoch = radiusState.LeaderEpoch;
            }
            if (valid) { radiusGraceGeneration = radiusState.Generation; radiusGraceRadius = radiusState.Radius; }
            radiusWasActive = valid;
            if (radiusState == null) return null;
            var frame = new GroupRadiusFrame { Active = valid, GraceUntil = radiusGraceUntil, Exempt = radiusState.OwnExempt || radiusState.OwnIsLeader,
                Radius = radiusState.Radius, LeaderPosition = new Vector3(radiusState.LeaderX, radiusState.LeaderY, radiusState.LeaderZ),
                Sequence = radiusReceivedSequence, Notice = RadiusNotice };
            return frame;
        }
        private void RadiusNotice(string message)
        {
            float now = Time.realtimeSinceStartup;
            if (now < radiusNoticeAt || Player.m_localPlayer == null) return;
            radiusNoticeAt = now + 3;
            Player.m_localPlayer.Message(MessageHud.MessageType.Center, message);
        }
        private GroupRadiusAdminView GetGroupRadiusView()
        {
            if (!CanUse() || radiusView == null || Time.realtimeSinceStartup < radiusViewAt || Time.realtimeSinceStartup - radiusViewAt > 8) return null;
            return new GroupRadiusAdminView { Enabled = radiusView.Enabled, Radius = radiusView.Radius, LeaderPeerId = radiusView.LeaderPeerId,
                LeaderName = radiusView.LeaderName, Revision = radiusView.Revision, ReadOnly = radiusView.ReadOnly,
                Notice = radiusView.Notice, ExemptPeers = new HashSet<long>(radiusView.ExemptPeers) };
        }
        private void RequestGroupRadiusView()
        {
            if (!CanUse()) return;
            ToServer(31, w => { });
        }
        private void SendGroupRadiusView(long actor)
        {
            RequireAllowed(actor);
            GroupRadiusSettings settings = RadiusSettings(); List<PlayerInfo> players = RadiusPlayers();
            PlayerInfo leader = players.FirstOrDefault(p => p.OwnerId == settings.LeaderOwner);
            bool readOnly = radiusStore == null || radiusStore.IsCorrupt(world);
            string notice = readOnly ? T("Файл настроек радиуса недоступен или повреждён. Ограничение отключено; файл не перезаписывается.",
                "Radius settings are unavailable or damaged. Restriction is disabled; the file is not overwritten.") : "";
            byte[] packet = Packet(32, w => {
                w.Write(settings.Generation); w.Write(settings.Enabled && !readOnly); w.Write(settings.Radius);
                w.Write(leader == null ? 0 : leader.PeerId); Text(w, leader == null ? T("Ведущий не подключён", "Leader is offline") : LocationName(leader.Name));
                w.Write(readOnly); Text(w, notice); w.Write(players.Count);
                foreach (PlayerInfo player in players) { w.Write(player.PeerId); w.Write(settings.ExemptOwners.Contains(player.OwnerId)); }
            });
            if (actor == ZNet.GetUID()) { Dispatch(0, packet); return; }
            ZNetPeer recipient = Peer(actor); if (recipient == null) return;
            ZNet session = network; long id = world;
            transport.SendGuarded(recipient.m_rpc, packet, "group-radius-admin", () => ReferenceEquals(network, session) && world == id
                && ReferenceEquals(Peer(actor), recipient) && Allowed(actor));
        }
        private void RequestGroupRadiusSettings(long revision, bool enabled, float radius)
        {
            GroupRadiusPolicy.RequireRadius(radius);
            ToServer(33, w => { w.Write((byte)0); w.Write(revision); w.Write(enabled); w.Write(radius); });
        }
        private void RequestGroupRadiusLeader(long revision, long target)
        { ToServer(33, w => { w.Write((byte)1); w.Write(revision); w.Write(target); }); }
        private void RequestGroupRadiusExemption(long revision, long target, bool exempt)
        { ToServer(33, w => { w.Write((byte)2); w.Write(revision); w.Write(target); w.Write(exempt); }); }
        private bool GroupRadiusServerMessage(long actor, int kind, BinaryReader reader)
        {
            if (kind == 30)
            {
                long character = reader.ReadInt64(); bool alive = Flag(reader), valid = Flag(reader);
                var point = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()); End(reader);
                ZNetPeer peer = Peer(actor);
                if (peer == null || character == 0 || WC.GetAdministrativeCharacter(actor) != character || !RadiusPoint(point) || valid && !alive)
                    throw new InvalidDataException("Invalid group radius player sample.");
                RadiusSample previous; float now = Time.realtimeSinceStartup;
                if (radiusSamples.TryGetValue(peer.m_rpc, out previous) && now >= previous.At && now - previous.At < .1f) return true;
                radiusSamples[peer.m_rpc] = new RadiusSample { At = now, Character = character, Alive = alive, Valid = valid, Position = point };
                return true;
            }
            if (kind == 31)
            {
                End(reader); RequireAllowed(actor); float last;
                if (radiusRequestTimes.TryGetValue(actor, out last) && Time.realtimeSinceStartup - last < .15f) return true;
                radiusRequestTimes[actor] = Time.realtimeSinceStartup; SendGroupRadiusView(actor); return true;
            }
            if (kind != 33) return false;
            byte action = reader.ReadByte(); long revision = reader.ReadInt64();
            bool enabled = false, exempt = false; float radius = 500; long target = 0;
            if (action == 0) { enabled = Flag(reader); radius = reader.ReadSingle(); }
            else if (action == 1) target = reader.ReadInt64();
            else if (action == 2) { target = reader.ReadInt64(); exempt = Flag(reader); }
            else throw new InvalidDataException("Invalid group radius edit.");
            End(reader);
            try
            {
                RequireAllowed(actor); Rate(actor);
                if (radiusStore == null || radiusStore.IsCorrupt(world)) throw new IOException("Group radius settings are unavailable; no changes were saved.");
                GroupRadiusSettings next = radiusStore.Snapshot(world);
                if (action == 0) { GroupRadiusPolicy.RequireRadius(radius); next.Enabled = enabled; next.Radius = radius; }
                else
                {
                    PlayerInfo participant = PeerInfo(target);
                    if (action == 1) next.LeaderOwner = participant.OwnerId;
                    else if (exempt) next.ExemptOwners.Add(participant.OwnerId); else next.ExemptOwners.Remove(participant.OwnerId);
                }
                GroupRadiusSettings current;
                if (!radiusStore.TryUpdate(world, revision, next, out current))
                    throw new InvalidOperationException(T("Настройки уже изменил другой администратор. Проверь обновлённые значения.", "Another administrator changed these settings. Review the refreshed values."));
                radiusBroadcastAt = 0;
                foreach (PlayerInfo player in RadiusPlayers()) if (Allowed(player.PeerId)) SendGroupRadiusView(player.PeerId);
                Send(actor, 35, w => { w.Write(true); Text(w, T("Настройки радиуса сохранены.", "Radius settings saved.")); });
            }
            catch (Exception error)
            {
                if (Allowed(actor)) { SendGroupRadiusView(actor); Send(actor, 35, w => { w.Write(false); Text(w, error.Message); }); }
                else throw;
            }
            return true;
        }
        private bool GroupRadiusClientMessage(int kind, BinaryReader reader)
        {
            if (kind == 34)
            {
                long sequence = reader.ReadInt64(); float age = reader.ReadSingle(); byte[] bytes = PolicyBinary.ReadBytes(reader, 16300); End(reader);
                GroupRadiusState state = GroupRadiusCodec.DecodeState(bytes);
                if (state.World != world || sequence <= 0 || Single.IsNaN(age) || Single.IsInfinity(age) || age < 0 || age >= GroupRadiusPolicy.FreshnessSeconds)
                    throw new InvalidDataException("Wrong group radius state context or age.");
                if (!WC.AdministrativeReady || sequence <= radiusReceivedSequence
                    || radiusState != null && state.Generation < radiusState.Generation && state.Enabled) return true;
                radiusState = state; radiusReceivedSequence = sequence; radiusReceivedAt = Time.realtimeSinceStartup - age; return true;
            }
            if (kind == 32)
            {
                var view = new GroupRadiusAdminView { Revision = reader.ReadInt64(), Enabled = Flag(reader), Radius = reader.ReadSingle(),
                    LeaderPeerId = reader.ReadInt64(), LeaderName = Text(reader), ReadOnly = Flag(reader), Notice = Text(reader) };
                GroupRadiusPolicy.RequireRadius(view.Radius); int count = reader.ReadInt32();
                if (view.Revision < 0 || count < 0 || count > 128) throw new InvalidDataException("Invalid group radius editor view.");
                var seen = new HashSet<long>();
                for (int i = 0; i < count; ++i) { long peer = reader.ReadInt64(); bool exempt = Flag(reader); if (peer == 0 || !seen.Add(peer)) throw new InvalidDataException("Invalid group radius peer roster."); if (exempt) view.ExemptPeers.Add(peer); }
                End(reader);
                if (!CanUse() || radiusView != null && view.Revision < radiusView.Revision && !view.ReadOnly) return true;
                radiusView = view; radiusViewAt = Time.realtimeSinceStartup; return true;
            }
            if (kind == 35)
            { bool success = Flag(reader); string message = Text(reader); End(reader); if (CanUse()) window.SetStatus(message); return true; }
            return false;
        }
    }
}
