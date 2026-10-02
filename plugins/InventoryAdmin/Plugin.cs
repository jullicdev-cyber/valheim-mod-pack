using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using WC = ValheimModPack.WorldCharacters.Plugin;

namespace ValheimModPack.InventoryAdmin
{
    [BepInPlugin(Id, "Inventory Admin", Version)]
    [BepInDependency("com.jotunn.jotunn", "2.30.2")]
    [BepInDependency("valheimmodpack.worldcharacters", "1.0.4")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "valheimmodpack.inventoryadmin", Version = "1.0.1";
        private static Plugin active;
        private Harmony harmony;
        private ConfigEntry<KeyboardShortcut> shortcut;
        private readonly WireTransport transport = new WireTransport();
        private readonly Dictionary<ZRpc, ZNetPeer> peers = new Dictionary<ZRpc, ZNetPeer>();
        private readonly Dictionary<long, bool> welcomes = new Dictionary<long, bool>();
        private readonly Dictionary<long, float> requestTimes = new Dictionary<long, float>();
        private readonly Dictionary<string, ViewRequest> reads = new Dictionary<string, ViewRequest>();
        private readonly Dictionary<string, ServedView> served = new Dictionary<string, ServedView>();
        private readonly Dictionary<string, CapturedInventory> captures = new Dictionary<string, CapturedInventory>();
        private readonly Dictionary<string, LocalOperation> localOperations = new Dictionary<string, LocalOperation>();
        private readonly Dictionary<long, string> playerNames = new Dictionary<long, string>();
        private PermissionStore permissions;
        private TransactionJournal journal;
        private AdminWindow window;
        private InventoryView displayed;
        private ServerOperation operation;
        private ZNet network;
        private long world;
        private bool authorized;
        private string ownOwner = "", playerRequest = "", viewRequest = "";
        private KeyCode claimed;
        private int released = -1;
        private sealed class ViewRequest { internal long Viewer, Source; internal float At; }
        private sealed class ServedView { internal long Viewer; internal InventoryView View; internal float At; }
        private sealed class ServerOperation { internal InventoryTransaction Record; internal long Actor, Source, Destination, SourceBaseline, DestinationBaseline; internal int Phase; internal float At; internal bool RemoveSent, DeliverySent; }
        private sealed class LocalOperation
        {
            internal string Id, View;
            internal MutationRequest Request;
            internal byte[] Bytes;
            internal long Character, Sequence;
            internal bool Destination, Reported, Changed;
            internal float At;
        }
        private static string T(string ru, string en)
        { return Localization.instance != null && Localization.instance.GetSelectedLanguage() == "Russian" ? ru : en; }
        private void Awake()
        {
            active = this;
            shortcut = Config.Bind("Controls", "OpenInventoryAdmin", new KeyboardShortcut(KeyCode.F9, KeyCode.LeftControl),
                "Open player inventory administration. Host or assigned administrator only. Rebind in Bindrune.");
            window = new AdminWindow(new AdminUiBindings { CanUse = CanUse, IsHost = () => IsHost, LocalPeerId = () => ZNet.GetUID(),
                ShortcutLabel = () => Label(shortcut.Value), Translate = T, RequestPlayers = RequestPlayers, RequestInventory = RequestInventory,
                Delete = (v, i, n) => RequestMutation(v, i, n, InventoryOperation.Delete), Take = (v, i, n) => RequestMutation(v, i, n, InventoryOperation.Take),
                SetAdmin = RequestRole, Error = e => Logger.LogError(e), OnClosed = () => { displayed = null; viewRequest = ""; } });
            harmony = new Harmony(Id); harmony.PatchAll(typeof(Plugin).Assembly);
            new Terminal.ConsoleCommand("ia", "Inventory Admin: open, pending, retry <transaction>, recover <transaction> CONFIRM-NOT-DELIVERED", (Terminal.ConsoleEvent)Command);
        }
        private bool IsHost { get { return ZNet.instance != null && ZNet.instance.IsServer(); } }
        private bool CanUse()
        { return ZNet.instance != null && Player.m_localPlayer != null && WC.AdministrativeReady && (IsHost ? permissions != null && journal != null : authorized); }
        private bool CanOpen()
        {
            return CanUse() && !Player.m_localPlayer.IsTeleporting() && !Player.m_localPlayer.InCutscene()
                && !InventoryGui.IsVisible() && !Menu.IsVisible() && !Console.IsVisible()
                && (Chat.instance == null || !Chat.instance.HasFocus()) && !TextInput.IsVisible() && !UnifiedPopup.IsVisible();
        }
        private void EnsureSession()
        {
            ZNet current = ZNet.instance; long id = current == null ? 0 : current.GetWorldUID();
            if (network == current && world == id) return;
            ResetSession(); network = current; world = id;
            if (current == null || id == 0) return;
            if (IsHost)
            {
                string root = Path.Combine(Path.GetDirectoryName(Paths.BepInExRootPath), "ValheimModpack", "InventoryAdmin");
                try { permissions = new PermissionStore(root); journal = new TransactionJournal(Path.Combine(root, "transactions")); ownOwner = "local-host"; }
                catch { if (permissions != null) permissions.Dispose(); permissions = null; journal = null; throw; }
            }
            foreach (ZNetPeer peer in current.GetPeers()) Register(peer);
        }
        private void Update()
        {
            try
            {
                EnsureSession(); transport.Tick();
                if (network == null || world == 0) return;
                if (IsHost) UpdateHost();
                UpdateLocalOperations(); window.Tick();
                RefreshClaimed();
                if (CanUse() && ShortcutDown(shortcut.Value) && (window.IsVisible || CanOpen()))
                {
                    claimed = shortcut.Value.MainKey; released = -1;
                    GameplayInputCache.ConsumeAll();
                    if (window.IsVisible) window.Hide(); else window.Show();
                }
            }
            catch (Exception e) { Logger.LogError(e); if (window != null) window.SetStatus(e.Message); }
        }
        private void UpdateHost()
        {
            foreach (ZNetPeer peer in peers.Values.ToArray())
            {
                if (!Ready(peer)) continue;
                bool allowed = Allowed(peer.m_uid); bool old;
                if (!welcomes.TryGetValue(peer.m_uid, out old) || old != allowed)
                { Send(peer.m_uid, 1, w => { w.Write(allowed); Text(w, Account(peer)); }); welcomes[peer.m_uid] = allowed; }
            }
            if (operation != null && Time.realtimeSinceStartup - operation.At > 120)
                FailOperation(T("Операция не завершена. Проверь журнал /ia pending.", "Operation incomplete. Check /ia pending."), operation.RemoveSent || operation.DeliverySent);
            foreach (string id in reads.Where(p => Time.realtimeSinceStartup - p.Value.At > 30).Select(p => p.Key).ToArray())
            { ViewRequest request = reads[id]; reads.Remove(id); Result(request.Viewer, id, false, T("Игрок не ответил. Повтори просмотр.", "Player did not respond. Retry the view.")); }
            foreach (string id in served.Where(p => Time.realtimeSinceStartup - p.Value.At > 90).Select(p => p.Key).ToArray()) served.Remove(id);
        }
        private static string Account(ZNetPeer peer)
        {
            string value = WC.GetAdministrativeOwner(peer);
            PermissionPolicy.RequireSteamOwner(value); return value;
        }
        private static bool ApprovedPeer(ZNetPeer peer)
        {
            if (peer == null || peer.m_rpc == null || !peer.m_rpc.IsConnected() || !peer.IsReady()
                || peer.m_uid == 0 || peer.m_uid == ZNet.GetUID() || !WC.IsAdministrativePeerReady(peer.m_uid)) return false;
            try { Account(peer); return true; }
            catch (InvalidDataException) { return false; }
        }
        private bool Ready(ZNetPeer peer)
        {
            if (!ApprovedPeer(peer)) return false;
            foreach (ZNetPeer other in peers.Values)
                if (!ReferenceEquals(other, peer) && other.m_uid == peer.m_uid && ApprovedPeer(other)) return false;
            return true;
        }
        private ZNetPeer Peer(long id)
        { return peers.Values.FirstOrDefault(p => p.m_uid == id && Ready(p)); }
        private PlayerInfo PeerInfo(long id)
        {
            if (id == ZNet.GetUID())
            {
                if (!WC.AdministrativeReady) throw new InvalidOperationException("Host character unavailable.");
                return new PlayerInfo { PeerId = id, CharacterId = Player.m_localPlayer.GetPlayerID(), Name = Player.m_localPlayer.GetPlayerName(),
                    OwnerId = "local-host", IsAdmin = true, InventoryAvailable = true };
            }
            ZNetPeer peer = Peer(id); if (peer == null) throw new InvalidOperationException("Player disconnected or character not loaded.");
            string owner = Account(peer);
            return new PlayerInfo { PeerId = id, CharacterId = WC.GetAdministrativeCharacter(id), Name = peer.m_playerName, OwnerId = owner,
                IsAdmin = permissions.IsAdministrator(world, owner), InventoryAvailable = true };
        }
        private bool Allowed(long id)
        {
            if (!IsHost || world == 0 || permissions == null) return false;
            if (id == ZNet.GetUID()) return true;
            ZNetPeer peer = Peer(id); return peer != null && PermissionPolicy.CanInspect(world, false, Account(peer), permissions);
        }
        private void RequireAllowed(long id)
        { if (!Allowed(id)) throw new UnauthorizedAccessException(T("Нет прав на управление инвентарями.", "No inventory administration permission.")); }
        private void Register(ZNetPeer peer)
        {
            if (peer == null || peer.m_rpc == null || peers.ContainsKey(peer.m_rpc)) return;
            peers.Add(peer.m_rpc, peer); peer.m_rpc.Register<ZPackage>(WireTransport.RpcName, Receive);
        }
        private void Receive(ZRpc rpc, ZPackage package)
        {
            try
            {
                ZNetPeer peer;
                if (!peers.TryGetValue(rpc, out peer) || !peer.IsReady() || peer.m_uid == 0) return;
                if (IsHost) { if (!Ready(peer)) return; }
                else if (!ReferenceEquals(peer, ZNet.instance.GetServerPeer())) return;
                byte[] bytes = transport.Receive(rpc, package); if (bytes == null) return;
                Dispatch(IsHost ? peer.m_uid : 0, bytes);
            }
            catch (Exception e)
            {
                transport.Remove(rpc); Logger.LogWarning("Inventory channel rejected: " + e.Message);
                ZNetPeer sender;
                if (IsHost && peers.TryGetValue(rpc, out sender) && Ready(sender) && Allowed(sender.m_uid)) Result(sender.m_uid, "", false, e.Message);
            }
        }
        private byte[] Packet(int kind, Action<BinaryWriter> fields)
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            { writer.Write(1); writer.Write(world); writer.Write(kind); fields(writer); writer.Flush(); return stream.ToArray(); }
        }
        private void Send(long peer, int kind, Action<BinaryWriter> fields)
        {
            byte[] bytes = Packet(kind, fields);
            if (peer == ZNet.GetUID()) { Dispatch(0, bytes); return; }
            ZNetPeer target = Peer(peer); if (target == null) throw new IOException("Player connection unavailable.");
            transport.Send(target.m_rpc, bytes);
        }
        private void ToServer(int kind, Action<BinaryWriter> fields)
        {
            byte[] bytes = Packet(kind, fields);
            if (IsHost) { Dispatch(ZNet.GetUID(), bytes); return; }
            ZNetPeer serverPeer = ZNet.instance.GetServerPeer();
            if (serverPeer == null || !serverPeer.IsReady()) throw new IOException("Host connection unavailable.");
            transport.Send(serverPeer.m_rpc, bytes);
        }
        private void Dispatch(long actor, byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes, false)) using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                if (reader.ReadInt32() != 1 || reader.ReadInt64() != world) throw new InvalidDataException("Inventory world/protocol mismatch.");
                int kind = reader.ReadInt32();
                if (actor != 0) ServerMessage(actor, kind, reader); else ClientMessage(kind, reader);
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing inventory message bytes.");
            }
        }
        private static void Text(BinaryWriter writer, string value) { PolicyBinary.WriteString(writer, value ?? "", 2048); }
        private static string Text(BinaryReader reader) { return PolicyBinary.ReadString(reader, 2048); }
        private static void Bytes(BinaryWriter writer, byte[] value) { PolicyBinary.WriteBytes(writer, value, InventoryCodec.MaximumItemBytes); }
        private static byte[] Bytes(BinaryReader reader) { return PolicyBinary.ReadBytes(reader, InventoryCodec.MaximumItemBytes); }
        private static string Token(BinaryReader reader) { string id = Text(reader); PolicyBinary.RequireToken(id); return id; }
        private static void End(BinaryReader reader)
        { if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Trailing inventory message bytes."); }
        private static bool Flag(BinaryReader reader)
        { byte value = reader.ReadByte(); if (value > 1) throw new InvalidDataException("Invalid boolean flag."); return value == 1; }
        private void RequestPlayers()
        { playerRequest = Guid.NewGuid().ToString("N"); ToServer(2, w => Text(w, playerRequest)); }
        private void RequestInventory(long target)
        {
            displayed = null; viewRequest = Guid.NewGuid().ToString("N");
            ToServer(4, w => { Text(w, viewRequest); w.Write(target); });
        }
        private void RequestMutation(string view, string itemId, int count, InventoryOperation action)
        {
            if (displayed == null || displayed.RequestId != view || !CanUse()) return;
            ItemInfo item = displayed.Items.FirstOrDefault(i => i.SlotId == itemId); if (item == null) return;
            var request = new MutationRequest { RequestId = Guid.NewGuid().ToString("N"), ViewRequestId = view, SlotId = itemId,
                Fingerprint = item.Fingerprint, TargetPeerId = displayed.TargetPeerId, TargetCharacter = displayed.TargetCharacter,
                Revision = displayed.Revision, Count = count, Operation = action };
            InventoryCodec.RequireCurrentSelection(displayed, request); window.SetBusy(true);
            try { ToServer(8, w => Bytes(w, InventoryCodec.EncodeMutationRequest(request))); }
            catch { window.SetBusy(false); throw; }
        }
        private void RequestRole(long target, bool enabled)
        { ToServer(18, w => { w.Write(target); w.Write(enabled); }); }
        private void Rate(long actor)
        {
            float last;
            if (requestTimes.TryGetValue(actor, out last) && Time.realtimeSinceStartup - last < .2f) throw new InvalidOperationException("Too many inventory requests.");
            requestTimes[actor] = Time.realtimeSinceStartup;
        }
        private void ServerMessage(long actor, int kind, BinaryReader r)
        {
            if (!IsHost) throw new UnauthorizedAccessException("Server message on client.");
            if (kind == 2)
            {
                string id = Token(r); End(r); RequireAllowed(actor);
                var players = new List<PlayerInfo>();
                if (WC.AdministrativeReady) players.Add(PeerInfo(ZNet.GetUID()));
                foreach (ZNetPeer peer in peers.Values) if (Ready(peer)) players.Add(PeerInfo(peer.m_uid));
                Send(actor, 3, w => { Text(w, id); Bytes(w, InventoryCodec.EncodePlayers(players)); }); return;
            }
            if (kind == 4)
            {
                string id = Token(r); long target = r.ReadInt64(); End(r); RequireAllowed(actor); Rate(actor); PeerInfo(target);
                if (reads.Count >= 32) throw new InvalidOperationException("Too many pending inventory views.");
                reads.Add(id, new ViewRequest { Viewer = actor, Source = target, At = Time.realtimeSinceStartup });
                Send(target, 5, w => Text(w, id)); return;
            }
            if (kind == 6)
            {
                string id = Token(r); bool success = Flag(r); string error = Text(r); byte[] bytes = Bytes(r); End(r);
                ViewRequest request; if (!reads.TryGetValue(id, out request) || request.Source != actor) return;
                reads.Remove(id); if (!Allowed(request.Viewer)) return;
                if (!success) { Result(request.Viewer, id, false, error); return; }
                InventoryView view = InventoryCodec.DecodeInventoryView(bytes); PlayerInfo source = PeerInfo(actor);
                if (view.RequestId != id || view.TargetPeerId != actor || view.TargetCharacter != source.CharacterId || view.TargetOwner != source.OwnerId)
                    throw new InvalidDataException("Inventory owner mismatch.");
                if (served.Count >= 64) served.Clear();
                served[id] = new ServedView { Viewer = request.Viewer, View = view, At = Time.realtimeSinceStartup };
                Send(request.Viewer, 7, w => Bytes(w, bytes)); return;
            }
            if (kind == 8)
            {
                MutationRequest request = InventoryCodec.DecodeMutationRequest(Bytes(r));
                End(r);
                try { RequireAllowed(actor); Rate(actor); BeginOperation(actor, request); }
                catch (Exception e) { Result(actor, request.RequestId, false, e.Message); }
                return;
            }
            if (kind == 10)
            {
                string id = Token(r); bool success = Flag(r); string error = Text(r); byte[] blob = Bytes(r); End(r);
                if (!Matches(id, actor, true) || operation.Phase != 9) return;
                if (!success) { FailOperation(error, operation.RemoveSent || operation.DeliverySent); return; }
                try
                {
                ItemDrop.ItemData prepared = NativeAdapter.ReadBlob(blob);
                ServedView selected; ItemInfo expected;
                if (!served.TryGetValue(operation.Record.Request.ViewRequestId, out selected)
                    || (expected = selected.View.Items.FirstOrDefault(i => i.SlotId == operation.Record.Request.SlotId)) == null
                    || prepared.m_stack != operation.Record.Request.Count || prepared.m_dropPrefab.name != expected.Prefab
                    || prepared.m_quality != expected.Quality || prepared.m_variant != expected.Variant || Math.Abs(prepared.m_durability - expected.Durability) > .011f)
                { FailOperation("Prepared item differs from the selected inventory item.", false); return; }
                operation.Record = journal.SetPrepared(id, blob);
                if (!Allowed(operation.Actor)) { FailOperation("Administrator permission revoked.", false); return; }
                if (operation.Record.Request.Operation == InventoryOperation.Take)
                { operation.Phase = 11; Send(operation.Destination, 11, w => { Text(w, id); Bytes(w, blob); }); }
                else RemoveSource();
                }
                catch (Exception e) { FailOperation(e.Message, operation != null && (operation.RemoveSent || operation.DeliverySent)); }
                return;
            }
            if (kind == 12)
            {
                string id = Token(r); bool success = Flag(r); string error = Text(r); End(r);
                if (!Matches(id, actor, false) || operation.Phase != 11) return;
                if (!success)
                {
                    if (operation.Record.Stage == InventoryTransactionStage.SourceRemoved) SuspendDelivery(error);
                    else FailOperation(error, false);
                    return;
                }
                if (operation.Record.Stage == InventoryTransactionStage.SourceRemoved) ApplyDestination(); else RemoveSource(); return;
            }
            if (kind == 14 || kind == 16)
            {
                string id = Token(r); bool success = Flag(r); bool ambiguous = Flag(r); long sequence = r.ReadInt64(); string error = Text(r); End(r);
                if (!Matches(id, actor, kind == 14) || operation.Phase != (kind == 14 ? 13 : 15)) return;
                if (!success)
                {
                    if (kind == 16 && !ambiguous && operation.Record.Stage == InventoryTransactionStage.SourceRemoved) SuspendDelivery(error);
                    else FailOperation(error, ambiguous || operation.Record.Stage == InventoryTransactionStage.SourceRemoved);
                    return;
                }
                PlayerInfo participant = PeerInfo(actor);
                if (participant.CharacterId != (kind == 14 ? operation.Record.SourceCharacter : operation.Record.DestinationCharacter)
                    || sequence <= (kind == 14 ? operation.SourceBaseline : operation.DestinationBaseline) || WC.GetAdministrativeDurableSequence(actor) < sequence)
                { FailOperation("Character disk checkpoint not verified.", true); return; }
                if (kind == 14)
                {
                    operation.Record = journal.MarkSourceRemoved(id);
                    if (operation.Record.Request.Operation == InventoryOperation.Delete) CompleteOperation(); else ApplyDestination();
                }
                else { operation.Record = journal.MarkDestinationApplied(id); CompleteOperation(); }
                return;
            }
            if (kind == 18)
            {
                long target = r.ReadInt64(); bool enabled = Flag(r); End(r);
                RequireAllowed(actor); if (actor != ZNet.GetUID()) throw new UnauthorizedAccessException("Only the host can assign administrators.");
                PlayerInfo info = PeerInfo(target); if (info.OwnerId == "local-host") throw new InvalidOperationException("Host permissions are permanent.");
                permissions.SetAdministrator(world, true, info.OwnerId, enabled); welcomes.Remove(target);
                Result(actor, "", true, T("Права администратора обновлены.", "Administrator permissions updated.")); return;
            }
            throw new InvalidDataException("Unexpected inventory client message.");
        }
        private void BeginOperation(long actor, MutationRequest request)
        {
            if (operation != null) throw new InvalidOperationException(T("Другая операция ещё выполняется.", "Another operation is still running."));
            ServedView view;
            if (!served.TryGetValue(request.ViewRequestId, out view) || view.Viewer != actor || Time.realtimeSinceStartup - view.At > 90)
                throw new InvalidOperationException(T("Обнови просмотр инвентаря.", "Refresh the inventory view."));
            InventoryCodec.RequireCurrentSelection(view.View, request);
            PlayerInfo source = PeerInfo(request.TargetPeerId), administrator = PeerInfo(actor);
            if (request.Operation == InventoryOperation.Take && source.PeerId == actor)
                throw new InvalidOperationException(T("Это уже твой предмет.", "This item is already yours."));
            if (source.CharacterId != request.TargetCharacter) throw new InvalidOperationException("Character changed.");
            InventoryTransaction record = journal.Begin(world, administrator.OwnerId, source.OwnerId, source.CharacterId,
                request.Operation == InventoryOperation.Take ? administrator.OwnerId : "", request.Operation == InventoryOperation.Take ? administrator.CharacterId : 0, request);
            if (record.Stage != InventoryTransactionStage.Submitted) throw new InvalidOperationException("Operation already recorded; it will not be replayed.");
            operation = new ServerOperation { Record = record, Actor = actor, Source = source.PeerId, Destination = actor, Phase = 9, At = Time.realtimeSinceStartup };
            Send(source.PeerId, 9, w => { Text(w, record.Id); Bytes(w, InventoryCodec.EncodeMutationRequest(request)); });
        }
        private bool Matches(string id, long actor, bool source)
        { return operation != null && operation.Record.Id == id && (source ? operation.Source : operation.Destination) == actor; }
        private void RemoveSource()
        {
            if (!Allowed(operation.Actor)) { FailOperation("Administrator permission revoked.", false); return; }
            operation.RemoveSent = true;
            operation.SourceBaseline = WC.GetAdministrativeDurableSequence(operation.Source); operation.Phase = 13;
            string id = operation.Record.Id; Send(operation.Source, 13, w => Text(w, id));
        }
        private void ApplyDestination()
        {
            if (!Allowed(operation.Actor)) { FailOperation("Administrator permission revoked; item retained in escrow.", true); return; }
            operation.DeliverySent = true; string id = operation.Record.Id; byte[] blob = operation.Record.ItemBytes;
            operation.DestinationBaseline = WC.GetAdministrativeDurableSequence(operation.Destination); operation.Phase = 15;
            Send(operation.Destination, 15, w => { Text(w, id); Bytes(w, blob); });
        }
        private void CompleteOperation()
        {
            InventoryTransaction record = journal.Commit(operation.Record.Id); long actor = operation.Actor;
            operation = null;
            Result(actor, record.Id, true, T("Операция сохранена.", "Operation saved."));
        }
        private void FailOperation(string reason, bool uncertain)
        {
            if (operation == null) return;
            string id = operation.Record.Id; long actor = operation.Actor;
            if (uncertain || operation.Record.Stage >= InventoryTransactionStage.SourceRemoved) journal.RequireRecovery(id, reason);
            else journal.Abort(id, reason);
            Logger.LogWarning("Inventory transaction " + id + ": " + reason); operation = null;
            Result(actor, id, false, reason + (uncertain ? T(" Журнал сохранён; хост: /ia pending.", " Journal retained; host: /ia pending.") : ""));
        }
        private void SuspendDelivery(string reason)
        {
            string id = operation.Record.Id; long actor = operation.Actor; operation = null;
            Result(actor, id, false, reason + T(" Предмет сохранён в хранилище; хост: /ia retry ", " Item retained in escrow; host: /ia retry ") + id);
        }
        private void Result(long recipient, string id, bool success, string message)
        {
            try { Send(recipient, 17, w => { Text(w, id); w.Write(success); Text(w, message.Length > 1800 ? message.Substring(0, 1800) : message); }); }
            catch (Exception e) { Logger.LogWarning("Inventory result delivery: " + e.Message); }
        }
        private void ClientMessage(int kind, BinaryReader r)
        {
            if (kind == 1)
            { bool allow = Flag(r); string owner = Text(r); End(r); PermissionPolicy.RequireSteamOwner(owner); authorized = allow; ownOwner = owner; if (!authorized) window.Hide(); return; }
            if (kind == 3)
            {
                string id = Token(r); List<PlayerInfo> players = InventoryCodec.DecodePlayers(Bytes(r)); End(r); if (id != playerRequest || !CanUse()) return;
                playerNames.Clear(); foreach (PlayerInfo player in players) playerNames[player.PeerId] = player.Name;
                window.SetPlayers(players.Select(p => new AdminPlayerView { PeerId = p.PeerId, Name = p.Name, IsAdmin = p.IsAdmin }).ToList()); return;
            }
            if (kind == 5)
            {
                string id = Token(r); End(r);
                try
                {
                    if (!WC.AdministrativeReady) throw new InvalidOperationException("Character not ready.");
                    if (captures.Count >= 32) captures.Clear();
                    CapturedInventory capture = NativeAdapter.BuildView(Player.m_localPlayer, ZNet.GetUID(), ownOwner,
                        Player.m_localPlayer.GetPlayerID(), id); captures[id] = capture;
                    byte[] bytes = InventoryCodec.EncodeInventoryView(capture.View);
                    ToServer(6, w => { Text(w, id); w.Write(true); Text(w, ""); Bytes(w, bytes); });
                }
                catch (Exception e) { ToServer(6, w => { Text(w, id); w.Write(false); Text(w, e.Message); Bytes(w, new byte[0]); }); }
                return;
            }
            if (kind == 7)
            {
                InventoryView view = InventoryCodec.DecodeInventoryView(Bytes(r)); End(r); if (view.RequestId != viewRequest || !CanUse()) return;
                displayed = view; string name; playerNames.TryGetValue(view.TargetPeerId, out name);
                var visible = new AdminInventoryView { PeerId = view.TargetPeerId, Name = name ?? "", SnapshotToken = view.RequestId };
                foreach (ItemInfo item in view.Items)
                {
                    GameObject prefab = ObjectDB.instance == null ? null : ObjectDB.instance.GetItemPrefab(item.Prefab);
                    ItemDrop drop = prefab == null ? null : prefab.GetComponent<ItemDrop>();
                    visible.Items.Add(new AdminItemView { ItemToken = item.SlotId, Prefab = item.Prefab,
                        Name = Localization.instance == null ? item.Name : Localization.instance.Localize(item.Name), Count = item.Stack,
                        Quality = item.Quality, SlotX = item.X, SlotY = item.Y, Durability = item.Durability,
                        MaxDurability = MaxDurability(drop, item.Quality), Equipped = item.Equipped, Group = item.Container,
                        Icon = drop == null ? null : drop.m_itemData.GetIcon() });
                }
                window.SetSnapshot(visible); return;
            }
            if (kind == 9)
            {
                string id = Token(r); MutationRequest request = InventoryCodec.DecodeMutationRequest(Bytes(r)); End(r);
                try
                {
                    if (id != request.RequestId || !WC.AdministrativeReady || localOperations.ContainsKey(id) || localOperations.Count >= 8192) throw new InvalidOperationException("Stale, repeated or excessive operation.");
                    CapturedInventory capture; if (!captures.TryGetValue(request.ViewRequestId, out capture)) throw new InvalidOperationException("Inventory view expired.");
                    InventoryCodec.RequireCurrentSelection(capture.View, request);
                    byte[] bytes = NativeAdapter.Prepare(capture, request.SlotId, request.Fingerprint, request.Count);
                    localOperations.Add(id, new LocalOperation { Id = id, View = request.ViewRequestId, Request = request, Bytes = bytes,
                        Character = Player.m_localPlayer.GetPlayerID(), At = Time.realtimeSinceStartup });
                    ToServer(10, w => { Text(w, id); w.Write(true); Text(w, ""); Bytes(w, bytes); });
                }
                catch (Exception e) { ToServer(10, w => { Text(w, id); w.Write(false); Text(w, e.Message); Bytes(w, new byte[0]); }); }
                return;
            }
            if (kind == 11)
            {
                string id = Token(r); byte[] bytes = Bytes(r); End(r);
                try
                {
                    if (!CanUse() || !NativeAdapter.CanAdd(Player.m_localPlayer, bytes)) throw new InvalidOperationException(T("Нужна свободная обычная ячейка; закрой инвентарь.", "A free ordinary slot is required; close the inventory."));
                    LocalOperation existing;
                    if (localOperations.TryGetValue(id, out existing))
                    {
                        if (!existing.Destination || existing.Changed) throw new InvalidOperationException("Destination operation already recorded.");
                        localOperations.Remove(id); // Explicit negative preflight never applied an item.
                    }
                    if (localOperations.Count >= 8192) throw new InvalidOperationException("Local operation limit exceeded.");
                    localOperations[id] = new LocalOperation { Id = id, Bytes = bytes, Destination = true,
                        Character = Player.m_localPlayer.GetPlayerID(), At = Time.realtimeSinceStartup };
                    ToServer(12, w => { Text(w, id); w.Write(true); Text(w, ""); });
                }
                catch (Exception e) { ToServer(12, w => { Text(w, id); w.Write(false); Text(w, e.Message); }); }
                return;
            }
            if (kind == 13)
            {
                string id = Token(r); End(r); LocalOperation local;
                if (!localOperations.TryGetValue(id, out local) || local.Destination || local.Changed) return;
                bool changed = false;
                try
                {
                    if (!WC.AdministrativeReady || Player.m_localPlayer.GetPlayerID() != local.Character) throw new InvalidOperationException("Character unavailable.");
                    CapturedInventory capture; if (!captures.TryGetValue(local.View, out capture)) throw new InvalidOperationException("View expired.");
                    byte[] check = NativeAdapter.Prepare(capture, local.Request.SlotId, local.Request.Fingerprint, local.Request.Count);
                    if (InventoryCodec.Fingerprint(check) != InventoryCodec.Fingerprint(local.Bytes)) throw new InvalidOperationException("Prepared item changed.");
                    changed = true; local.Changed = true;
                    NativeAdapter.Remove(capture, local.Request.SlotId, local.Request.Fingerprint, local.Request.Count);
                    local.Sequence = WC.RequestAdministrativeSave();
                }
                catch (Exception e) { ReportCheckpoint(local, false, changed, e.Message); local.Reported = true; local.Bytes = new byte[0]; }
                return;
            }
            if (kind == 15)
            {
                string id = Token(r); byte[] bytes = Bytes(r); End(r); LocalOperation local;
                if (!localOperations.TryGetValue(id, out local) || !local.Destination || local.Changed) return;
                bool changed = false;
                try
                {
                    if (!CanUse() || Player.m_localPlayer.GetPlayerID() != local.Character || InventoryCodec.Fingerprint(bytes) != InventoryCodec.Fingerprint(local.Bytes))
                        throw new InvalidOperationException("Destination context changed.");
                    if (!NativeAdapter.CanAdd(Player.m_localPlayer, bytes)) throw new InvalidOperationException("Destination has no free ordinary slot.");
                    changed = true; local.Changed = true; NativeAdapter.Add(Player.m_localPlayer, bytes);
                    local.Sequence = WC.RequestAdministrativeSave();
                }
                catch (Exception e) { ReportCheckpoint(local, false, changed, e.Message); local.Reported = true; local.Bytes = new byte[0]; }
                return;
            }
            if (kind == 17)
            {
                string id = Text(r); bool success = Flag(r); string message = Text(r); End(r);
                window.SetBusy(false); window.SetStatus(message);
                if (!String.IsNullOrEmpty(id) && id != viewRequest && CanUse() && window.IsVisible && displayed != null) RequestInventory(displayed.TargetPeerId);
                if (id == "" && CanUse() && window.IsVisible) RequestPlayers();
                return;
            }
            throw new InvalidDataException("Unexpected inventory host message.");
        }
        private void ReportCheckpoint(LocalOperation local, bool success, bool ambiguous, string error)
        {
            ToServer(local.Destination ? 16 : 14, w => { Text(w, local.Id); w.Write(success); w.Write(ambiguous); w.Write(local.Sequence); Text(w, error); });
        }
        private static float MaxDurability(ItemDrop drop, int quality)
        { if (drop == null) return 0; ItemDrop.ItemData item = drop.m_itemData.Clone(); item.m_quality = quality; return item.GetMaxDurability(); }
        private void UpdateLocalOperations()
        {
            foreach (LocalOperation local in localOperations.Values.ToArray())
            {
                if (!local.Reported && local.Changed && local.Sequence > 0 && WC.IsAdministrativeSaveDurable(local.Sequence))
                { ReportCheckpoint(local, true, false, ""); local.Reported = true; local.Bytes = new byte[0]; }
                else if (!local.Reported && Time.realtimeSinceStartup - local.At > 120)
                { ReportCheckpoint(local, false, local.Changed, "Administrative checkpoint timed out."); local.Reported = true; local.Bytes = new byte[0]; }
            }
        }
        private void Removed(ZNetPeer peer)
        {
            if (peer == null || peer.m_rpc == null) return;
            transport.Remove(peer.m_rpc); peers.Remove(peer.m_rpc); welcomes.Remove(peer.m_uid);
            if (IsHost && operation != null && (operation.Source == peer.m_uid || operation.Actor == peer.m_uid))
                FailOperation("Participant disconnected; inspect retained transaction.", operation.RemoveSent || operation.DeliverySent);
            if (!IsHost) { authorized = false; window.Hide(); }
        }
        private void ResetSession()
        {
            if (window != null) window.Hide();
            if (operation != null && journal != null) { try { journal.RequireRecovery(operation.Record.Id, "World session ended."); } catch (Exception e) { Logger.LogError(e); } }
            operation = null; if (permissions != null) permissions.Dispose(); if (journal != null) journal.Dispose(); permissions = null; journal = null;
            transport.Clear(); peers.Clear(); welcomes.Clear(); requestTimes.Clear(); reads.Clear(); served.Clear(); captures.Clear(); localOperations.Clear();
            playerNames.Clear();
            displayed = null; authorized = false; ownOwner = ""; playerRequest = viewRequest = ""; network = null; world = 0;
        }
        private void Command(Terminal.ConsoleEventArgs args)
        {
            try
            {
                EnsureSession();
                if (args.Length < 2 || args[1] == "open") { if (CanUse()) window.Show(); else args.Context.AddString(T("Нет прав либо персонаж ещё не загружен.", "Permission denied or character not loaded.")); return; }
                if (!IsHost) throw new UnauthorizedAccessException("Host only.");
                if (args[1] == "pending")
                {
                    foreach (InventoryTransaction item in journal.Unresolved()) args.Context.AddString(item.Id + " " + item.LastCertainStage + " " + item.SourceOwner + " → " + item.DestinationOwner + " " + item.Note);
                    return;
                }
                if ((args[1] == "retry" || args[1] == "recover") && args.Length >= 3)
                {
                    if (operation != null) throw new InvalidOperationException("Another administration operation is still running.");
                    InventoryTransaction record = journal.Get(args[2]);
                    if (record.World != world) throw new InvalidOperationException("Transaction belongs to a different world.");
                    if (args[1] == "recover")
                    {
                        if (args.Length != 4 || args[3] != "CONFIRM-NOT-DELIVERED") throw new InvalidOperationException("Inspect the source and receiver before confirming: ia recover <id> CONFIRM-NOT-DELIVERED");
                        record = journal.ResumeDeliveryAfterReview(record.Id, true, true, "Host explicitly reviewed destination inventory and confirmed the item was not delivered.");
                    }
                    if (record.World != world || record.Stage != InventoryTransactionStage.SourceRemoved || record.Request.Operation != InventoryOperation.Take)
                        throw new InvalidOperationException("Only a confirmed source removal can resume delivery.");
                    var destinations = peers.Values.Where(Ready).Select(p => PeerInfo(p.m_uid)).ToList(); if (WC.AdministrativeReady) destinations.Add(PeerInfo(ZNet.GetUID()));
                    PlayerInfo destination = destinations.FirstOrDefault(p => p.OwnerId == record.DestinationOwner && p.CharacterId == record.DestinationCharacter);
                    if (destination == null || !Allowed(destination.PeerId)) throw new InvalidOperationException("Original receiving administrator must be connected with the same character.");
                    operation = new ServerOperation { Record = record, Actor = destination.PeerId, Destination = destination.PeerId, Source = record.Request.TargetPeerId, Phase = 11, At = Time.realtimeSinceStartup };
                    Send(destination.PeerId, 11, w => { Text(w, record.Id); Bytes(w, record.ItemBytes); });
                    return;
                }
                args.Context.AddString("ia open | ia pending; unresolved transfers require host review.");
            }
            catch (Exception e) { args.Context.AddString(e.Message); }
        }
        private static KeyCode Family(KeyCode key)
        {
            if (key == KeyCode.RightControl) return KeyCode.LeftControl; if (key == KeyCode.RightShift) return KeyCode.LeftShift;
            if (key == KeyCode.RightAlt) return KeyCode.LeftAlt; if (key == KeyCode.RightCommand) return KeyCode.LeftCommand; return key;
        }
        private void RefreshClaimed()
        {
            if (claimed == KeyCode.None) return;
            if (!ZInput.GetKey(claimed, false) && released < 0) released = Time.frameCount;
            if (released >= 0 && Time.frameCount > released) { claimed = KeyCode.None; released = -1; }
        }
        private static bool Held(KeyCode key)
        {
            key = Family(key); return ZInput.GetKey(key, false) || key == KeyCode.LeftControl && ZInput.GetKey(KeyCode.RightControl, false)
                || key == KeyCode.LeftShift && ZInput.GetKey(KeyCode.RightShift, false) || key == KeyCode.LeftAlt && ZInput.GetKey(KeyCode.RightAlt, false)
                || key == KeyCode.LeftCommand && ZInput.GetKey(KeyCode.RightCommand, false);
        }
        private static bool ShortcutHeld(KeyboardShortcut key)
        {
            if (key.MainKey == KeyCode.None || !Held(key.MainKey) || key.Modifiers.Any(k => !Held(k))) return false;
            foreach (KeyCode family in new[] { KeyCode.LeftControl, KeyCode.LeftShift, KeyCode.LeftAlt, KeyCode.LeftCommand })
                if (Held(family) != (Family(key.MainKey) == family || key.Modifiers.Any(k => Family(k) == family))) return false;
            return true;
        }
        private static bool ShortcutDown(KeyboardShortcut key)
        { return ShortcutHeld(key) && ZInput.GetKeyDown(key.MainKey, false); }
        private static string Label(KeyboardShortcut key)
        {
            if (key.MainKey == KeyCode.None) return T("Клавиша не назначена", "Unbound");
            Func<KeyCode, string> label = k => Family(k) == KeyCode.LeftControl ? "Ctrl" : Family(k) == KeyCode.LeftShift ? "Shift"
                : Family(k) == KeyCode.LeftAlt ? "Alt" : Family(k) == KeyCode.LeftCommand ? "Cmd" : k.ToString();
            return String.Join("+", key.Modifiers.Select(Family).Distinct().OrderBy(k => k == KeyCode.LeftControl ? 0 : k == KeyCode.LeftShift ? 1 : k == KeyCode.LeftAlt ? 2 : k == KeyCode.LeftCommand ? 3 : 4)
                .Select(label).Concat(new[] { label(key.MainKey) }).ToArray());
        }
        private static bool BlockNative(string button, ref bool result)
        {
            if (active == null || button == "JoyButtonB") return true;
            if (active.window.IsVisible || active.claimed != KeyCode.None || active.CanOpen() && ShortcutHeld(active.shortcut.Value))
            { GameplayInputCache.Consume(button); result = false; return false; }
            return true;
        }
        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        private static class ConnectionPatch { private static void Postfix(ZNetPeer peer) { if (active != null) active.Register(peer); } }
        [HarmonyPatch(typeof(ZNet), "Disconnect")]
        private static class DisconnectPatch { private static void Prefix(ZNetPeer peer) { if (active != null) active.Removed(peer); } }
        [HarmonyPatch(typeof(ZInput), "GetButtonDown")]
        private static class DownPatch { private static bool Prefix(string name, ref bool __result) { return BlockNative(name, ref __result); } }
        [HarmonyPatch(typeof(ZInput), "GetButton")]
        private static class HeldPatch { private static bool Prefix(string name, ref bool __result) { return BlockNative(name, ref __result); } }
        [HarmonyPatch(typeof(ZInput), "GetButtonUp")]
        private static class UpPatch { private static bool Prefix(string name, ref bool __result) { return BlockNative(name, ref __result); } }
        [HarmonyPatch(typeof(Jotunn.Managers.GUIManager), "ResetInputBlock")]
        private static class ResetInputPatch { private static void Postfix() { if (active != null && active.window != null) active.window.HandleInputReset(); } }
        private void OnDestroy() { ResetSession(); if (window != null) window.Hide(); if (harmony != null) harmony.UnpatchSelf(); if (active == this) active = null; }
    }
}
