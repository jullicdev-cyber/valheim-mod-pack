using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Globalization;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using Jotunn.Utils;
using WC = ValheimModPack.WorldCharacters.Plugin;

namespace ValheimModPack.PartyPrison
{
    [BepInPlugin(Id, "Party Prison", Version)]
    [BepInDependency("com.jotunn.jotunn", "2.30.2")]
    [BepInDependency("valheimmodpack.worldcharacters", "1.0.4")]
    [BepInDependency("valheimmodpack.inventoryadmin", BepInDependency.DependencyFlags.SoftDependency)]
    [NetworkCompatibility(CompatibilityLevel.EveryoneMustHaveMod, VersionStrictness.Patch)]
    public sealed partial class Plugin : BaseUnityPlugin
    {
        public const string Id = "valheimmodpack.partyprison", Version = "1.1.3";
        internal const string LoanKey = "VMP_PP_Loan", InmateKey = "VMP_PP_Inmate";
        private const string RpcName = PrisonWire.RpcName;
        internal static Plugin Active;
        private Harmony harmony;
        private ConfigEntry<KeyboardShortcut> shortcut;
        private PrisonWindow window;
        private SentenceStore store;
        private ZNet network;
        private long world, sequence, receivedSequence;
        private PrisonRegion region;
        private SentenceState localSentence;
        private bool receivedState, releaseTeleport, releaseArrived, fatalStore, defeatReturn;
        private long releaseSave;
        private float nextHost, lastHostTick, nextHeartbeat, nextArmory, nextWave, nextEnforce, nextNotice, nextMobCleanup;
        private string notice = "", lastError = "";
        private readonly Dictionary<ZRpc, ZNetPeer> peers = new Dictionary<ZRpc, ZNetPeer>();
        private readonly Dictionary<ZRpc, Presence> presences = new Dictionary<ZRpc, Presence>();
        private readonly Dictionary<ZRpc, float> samples = new Dictionary<ZRpc, float>();
        private readonly Dictionary<ZRpc, float> requests = new Dictionary<ZRpc, float>();
        private readonly Dictionary<string, long> releaseBaselines = new Dictionary<string, long>();
        private sealed class Presence { internal string Token; internal float At; internal long Character; }
        internal bool Hosting { get { return network != null && network.IsServer(); } }
        internal bool Confined { get { return localSentence != null && region != null && (!localSentence.PendingRelease || !LegacyLayout && localCustodyStage < (int)CustodyStage.Released); } }
        internal bool PrisonActive { get { return localSentence != null || localCustodyStage == (int)CustodyStage.Released || localRecovery.Length != 0; } }
        internal bool AwaitingState { get { return network != null && !Hosting && !receivedState && WC.AdministrativeReady; } }
        internal PrisonRegion Region { get { return region; } }
        internal static PrisonPoint Point(Vector3 p) { return new PrisonPoint(p.x, p.y, p.z); }
        internal static Vector3 Vector(PrisonPoint p) { return new Vector3((float)p.X, (float)p.Y, (float)p.Z); }
        internal static string T(string ru, string en)
        { return Localization.instance != null && Localization.instance.GetSelectedLanguage() == "Russian" ? ru : en; }
        private void Awake()
        {
            Active = this;
            shortcut = Config.Bind("Controls", "OpenPrison", new KeyboardShortcut(KeyCode.F12, KeyCode.LeftControl), "Prison host controls / prisoner activities. Rebind in Bindrune.");
            window = new PrisonWindow(new PrisonUiBindings { IsHost = () => Hosting, CanUse = CanUse, Players = Roster,
                LocalSentence = () => localSentence == null ? null : localSentence.Copy(), Impose = Impose, Release = Release,
                Build = BuildPrison, Wave = RequestWave, Move = Move, Kit = GiveKit, CanFight = () => FightReady, CustodyStatus = CustodyStatus,
                Notice = () => notice, Translate = T, Error = e => Report(e.Message) });
            harmony = new Harmony(Id); harmony.PatchAll(typeof(Plugin).Assembly);
            new Terminal.ConsoleCommand("prison", "Prison: open, build, players, jail <account/name> <minutes> [reason], release <account/name>, cell, arena, wave <0/1/2>", (Terminal.ConsoleEvent)Command);
            Logger.LogInfo("Party Prison ready; host-only sentences, mandatory identical clients.");
        }
        private bool CanUse()
        { return network != null && Player.m_localPlayer != null && WC.AdministrativeReady && (Hosting && store != null && !fatalStore || localSentence != null || localCustodyStage == (int)CustodyStage.Released); }
        private void Session()
        {
            ZNet current = ZNet.instance; long id = current == null ? 0 : current.GetWorldUID();
            if (network == current && world == id) return;
            Reset(); network = current; world = id;
            if (current == null || id == 0) return;
            if (Hosting)
            {
                try { string directory = Path.Combine(Path.GetDirectoryName(BepInEx.Paths.BepInExRootPath), "ValheimModpack", "PartyPrison"); store = new SentenceStore(directory, id); custody = new CustodyStore(directory, id); withdrawal = new CustodyWithdrawal(directory, id); region = store.Region; receivedState = true; }
                catch (Exception e) { fatalStore = true; Report("Prison state cannot be opened; admissions blocked: " + e.Message); }
            }
            foreach (ZNetPeer peer in current.GetPeers()) Register(peer);
            nextHost = lastHostTick = Time.realtimeSinceStartup; nextHeartbeat = 0;
        }
        private void Reset()
        {
            if (window != null) window.Hide();
            if (store != null) { store.Dispose(); store = null; }
            ResetCustody(); ResetWithdrawal(); wire.Clear();
            peers.Clear(); presences.Clear(); samples.Clear(); requests.Clear(); releaseBaselines.Clear();
            region = null; localSentence = null; network = null; world = sequence = receivedSequence = 0;
            receivedState = releaseTeleport = releaseArrived = fatalStore = defeatReturn = false; releaseSave = 0;
            nextArmory = nextWave = nextEnforce = nextMobCleanup = 0; notice = lastError = "";
        }
        private void Update()
        {
            try
            {
                Session(); if (network == null || world == 0) return;
                foreach (ZNetPeer peer in network.GetPeers()) Register(peer);
                wire.Tick();
                if (Hosting) HostTick();
                if (region != null) ArenaBuilder.EnforceOwnedMobs(region);
                if (WC.AdministrativeReady)
                {
                    ProgressCustody();
                    ProgressWithdrawal();
                    if (localSentence != null && localSentence.PendingRelease && (LegacyLayout || localCustodyStage >= (int)CustodyStage.Deposited)) FinishRelease();
                    else if (Confined) Enforce(false);
                    else if (receivedState) RemoveLoans();
                    if (!Hosting && Time.realtimeSinceStartup >= nextHeartbeat)
                    {
                        nextHeartbeat = Time.realtimeSinceStartup + 1;
                        bool inside = FightReady && !Player.m_localPlayer.IsTeleporting() && ArenaBuilder.ContainsConfinement(region, Player.m_localPlayer.transform.position);
                        ToHost(PrisonProtocol.Heartbeat, w => { PrisonProtocol.Text(w, inside ? localSentence.SentenceId : ""); w.Write(Player.m_localPlayer.GetPlayerID()); });
                    }
                }
                window.Tick();
                if (CanUse() && shortcut.Value.IsDown() && !InventoryGui.IsVisible() && !Menu.IsVisible() && !Console.IsVisible()
                    && !ZInput.s_IsRebindActive && (Chat.instance == null || !Chat.instance.HasFocus()))
                { if (window.IsVisible) window.Hide(); else if (!TextInput.IsVisible() && !UnifiedPopup.IsVisible()) window.Show(); }
            }
            catch (Exception e) { Report(e.Message); }
        }
        private void Report(string message)
        {
            notice = message;
            if (lastError == message && Time.realtimeSinceStartup < nextNotice) return;
            lastError = message; nextNotice = Time.realtimeSinceStartup + 10; Logger.LogWarning(message);
        }
        private bool Ready(ZNetPeer peer)
        { return peer != null && peer.IsReady() && peer.m_rpc != null && peer.m_rpc.IsConnected() && WC.GetAdministrativeOwner(peer).Length != 0; }
        private void Register(ZNetPeer peer)
        {
            if (peer == null || peer.m_rpc == null || peers.ContainsKey(peer.m_rpc)) return;
            peers.Add(peer.m_rpc, peer); peer.m_rpc.Register<ZPackage>(RpcName, Receive);
        }
        internal void RememberPosition(ZRpc rpc)
        { ZNetPeer peer; if (Hosting && peers.TryGetValue(rpc, out peer) && Ready(peer)) samples[rpc] = Time.realtimeSinceStartup; }
        internal void RemovePeer(ZNetPeer peer)
        {
            if (peer == null || peer.m_rpc == null) return;
            peers.Remove(peer.m_rpc); presences.Remove(peer.m_rpc); samples.Remove(peer.m_rpc); requests.Remove(peer.m_rpc);
            wire.Remove(peer.m_rpc); custodySent.Remove(peer.m_rpc);
            string suffix = ":" + peer.m_uid; foreach (string key in releaseBaselines.Keys.Where(k => k.EndsWith(suffix, StringComparison.Ordinal)).ToArray()) releaseBaselines.Remove(key);
        }
        private bool Position(ZNetPeer peer, out PrisonPoint position)
        {
            position = new PrisonPoint(); float at; long character = WC.GetAdministrativeCharacter(peer.m_uid);
            if (!Ready(peer) || character == 0 || peer.m_characterID.IsNone() || !samples.TryGetValue(peer.m_rpc, out at)
                || Time.realtimeSinceStartup - at > 4 || Time.realtimeSinceStartup < at || ZDOMan.instance == null) return false;
            ZDO zdo = ZDOMan.instance.GetZDO(peer.m_characterID);
            if (zdo == null || zdo.GetLong(ZDOVars.s_playerID, 0) != character || zdo.GetBool(ZDOVars.s_dead, false)) return false;
            position = Point(peer.m_refPos); return SentencePolicy.IsFinitePoint(position);
        }
        private void HostTick()
        {
            float now = Time.realtimeSinceStartup; if (now < nextHost) return;
            double delta = Math.Min(2, Math.Max(0, now - lastHostTick)); lastHostTick = now; nextHost = now + 1;
            if (fatalStore || store == null || store.Faulted || custody == null || custody.Faulted || withdrawal == null || withdrawal.Faulted)
            {
                fatalStore = true;
                foreach (ZNetPeer peer in network.GetPeers().ToArray()) if (peer.IsReady()) network.Disconnect(peer);
                return;
            }
            HostCustodyTick();
            var online = new List<string>();
            foreach (ZNetPeer peer in peers.Values.ToArray())
            {
                if (!Ready(peer)) continue;
                string owner = WC.GetAdministrativeOwner(peer); SentenceState sentence = store.Find(owner); Presence presence; PrisonPoint position;
                if (sentence != null && !sentence.PendingRelease && presences.TryGetValue(peer.m_rpc, out presence)
                    && presence.Token == sentence.SentenceId && presence.Character == WC.GetAdministrativeCharacter(peer.m_uid)
                    && HostFightReady(sentence) && now >= presence.At && now - presence.At <= 2.5f && Position(peer, out position) && region != null && ArenaBuilder.ContainsConfinement(region, Vector(position))) online.Add(owner);
            }
            try { store.TickOnline(online, delta); }
            catch (Exception e) { fatalStore = true; Report("Prison saving failed; inmates stay confined and admissions stop: " + e.Message); return; }
            ++sequence;
            foreach (ZNetPeer peer in peers.Values.ToArray())
            {
                if (!Ready(peer)) continue;
                SentenceState sentence = store.Find(WC.GetAdministrativeOwner(peer));
                if (sentence != null && sentence.PendingRelease)
                {
                    string key = sentence.SentenceId + ":" + peer.m_uid;
                    if (!releaseBaselines.ContainsKey(key)) releaseBaselines[key] = WC.GetAdministrativeDurableSequence(peer.m_uid);
                }
                Send(peer.m_rpc, PrisonProtocol.State, w => { w.Write(sequence); PrisonProtocol.Region(w, region); PrisonProtocol.Sentence(w, sentence); WriteCustodyState(w, WC.GetAdministrativeOwner(peer), sentence); });
            }
            AutoWaves();
            if (region != null && now >= nextMobCleanup)
            { nextMobCleanup = now + 5; ArenaBuilder.CleanupMobs(region, !store.All().Any(s => !s.PendingRelease)); }
        }
        private byte[] Packet(int kind, Action<BinaryWriter> write)
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                PrisonProtocol.WriteHeader(writer, world, kind); write(writer); writer.Flush();
                if (stream.Length > PrisonProtocol.MaximumBytes) throw new InvalidDataException("Oversized prison message.");
                return stream.ToArray();
            }
        }
        private void Send(ZRpc rpc, int kind, Action<BinaryWriter> write)
        { if (rpc != null && rpc.IsConnected()) { byte[] bytes = Packet(kind, write); if (kind == PrisonProtocol.State) wire.SendGuarded(rpc, bytes, "state", () => rpc.IsConnected()); else wire.Send(rpc, bytes); } }
        private void ToHost(int kind, Action<BinaryWriter> write)
        { ZNetPeer peer = network.GetServerPeer(); if (peer != null && peer.IsReady()) Send(peer.m_rpc, kind, write); }
        private void Receive(ZRpc rpc, ZPackage package)
        {
            try
            {
                ZNetPeer peer;
                if (network == null || !peers.TryGetValue(rpc, out peer) || !peer.IsReady()) return;
                if (Hosting) { if (!Ready(peer) || fatalStore || store == null) return; }
                else if (!ReferenceEquals(peer, network.GetServerPeer())) return;
                byte[] bytes = wire.Receive(rpc, package); if (bytes == null) return;
                using (var stream = new MemoryStream(bytes, false)) using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    int kind = PrisonProtocol.ReadHeader(reader, world);
                    if (Hosting) ServerMessage(peer, kind, reader); else ClientMessage(kind, reader);
                }
            }
            catch (Exception e) { Report("Prison channel: " + e.Message); }
        }
        private void ServerMessage(ZNetPeer peer, int kind, BinaryReader reader)
        {
            string owner = WC.GetAdministrativeOwner(peer);
            if (ServerWithdrawalMessage(peer, kind, reader)) return;
            if (ServerCustodyMessage(peer, kind, reader)) return;
            if (kind == PrisonProtocol.Heartbeat)
            {
                string token = PrisonProtocol.Text(reader); long character = reader.ReadInt64(); PrisonProtocol.End(reader);
                if (character != WC.GetAdministrativeCharacter(peer.m_uid)) return;
                float previous; if (requests.TryGetValue(peer.m_rpc, out previous) && Time.realtimeSinceStartup - previous < .5f) return;
                requests[peer.m_rpc] = Time.realtimeSinceStartup;
                SentenceState state = store.Find(owner);
                if (state != null && !state.PendingRelease && token == state.SentenceId)
                    presences[peer.m_rpc] = new Presence { Token = token, At = Time.realtimeSinceStartup, Character = character };
                else presences.Remove(peer.m_rpc);
                return;
            }
            if (kind == PrisonProtocol.ReleaseAck)
            {
                string token = PrisonProtocol.Text(reader); long durable = reader.ReadInt64(); PrisonProtocol.End(reader);
                SentenceState state = store.Find(owner); long baseline; PrisonPoint position;
                string key = token + ":" + peer.m_uid;
                if (state != null && state.PendingRelease && state.SentenceId == token && releaseBaselines.TryGetValue(key, out baseline)
                    && durable > Math.Max(0, baseline) && WC.GetAdministrativeDurableSequence(peer.m_uid) >= durable
                    && Position(peer, out position) && (LegacyLayout ? Vector3.Distance(Vector(position), Vector(state.ReturnPosition)) <= 4 : CompleteHostRelease(owner, token)))
                { store.AcknowledgeRelease(owner, token); releaseBaselines.Remove(key); }
                return;
            }
            if (kind == PrisonProtocol.Wave)
            {
                int tier = reader.ReadInt32(); PrisonProtocol.End(reader); SentenceState state = store.Find(owner); PrisonPoint position;
                if (state == null || !HostFightReady(state) || !Position(peer, out position) || !ArenaBuilder.ContainsConfinement(region, Vector(position))) return;
                if (tier < 0 || tier > 2) return; waveTier = tier; return;
            }
            throw new UnauthorizedAccessException("Clients cannot issue prison administration commands.");
        }
        private void ClientMessage(int kind, BinaryReader reader)
        {
            if (ClientWithdrawalMessage(kind, reader) || ClientCustodyMessage(kind, reader)) return;
            if (kind != PrisonProtocol.State) throw new InvalidDataException("Unknown prison state message.");
            long serial = reader.ReadInt64(); PrisonRegion nextRegion = PrisonProtocol.Region(reader); SentenceState state = PrisonProtocol.Sentence(reader);
            int layoutVersion = reader.ReadInt32(), stage = reader.ReadInt32(); string account = PrisonProtocol.Text(reader), token = PrisonProtocol.Text(reader), hash = PrisonProtocol.Text(reader), recovery = PrisonProtocol.Text(reader); PrisonProtocol.End(reader);
            if (serial <= receivedSequence) return;
            if (state != null && nextRegion == null) throw new InvalidDataException("Sentence without a prison.");
            if (localSentence != null && state == null && !releaseArrived) throw new InvalidDataException("Unconfirmed prison release.");
            receivedSequence = serial; receivedState = true; region = nextRegion;
            if (localSentence == null || state == null || localSentence.SentenceId != state.SentenceId)
            { releaseArrived = false; releaseSave = 0; }
            localSentence = state;
            if (layoutVersion < 0 || layoutVersion > 2) throw new InvalidDataException("Unknown prison layout version."); localLayoutVersion = layoutVersion;
            ReadCustodyState(stage, account, token, hash, recovery);
        }
        internal bool CellSafe(PrisonPoint point)
        {
            if (!LegacyLayout) return ArenaBuilder.IsInsideCell(region, Vector(point));
            if (region == null || !ArenaBuilder.ContainsRoom(region, Vector(point))) return false;
            Vector3 right = Vector(region.Center) - Vector(region.CellSpawn); right.y = 0;
            right = right.sqrMagnitude < .1f ? Vector3.right : right.normalized;
            Vector3 delta = Vector(point) - Vector(region.Center);
            return Vector3.Dot(delta, right) <= -5 && Math.Abs(Vector3.Dot(delta, Vector3.Cross(right, Vector3.up))) <= 3;
        }
        internal void Enforce(bool defeated)
        {
            Player player = Player.m_localPlayer;
            if (!Confined || player == null || !WC.AdministrativeReady) return;
            ZNetView view = player.GetComponent<ZNetView>(); if (view != null && view.IsValid() && view.IsOwner()) view.GetZDO().Set(InmateKey, true);
            if (defeated) { defeatReturn = true; player.SetHealth(player.GetMaxHealth()); notice = T("Вы проиграли бой и возвращены в камеру. Срок продолжается, добыча сохранена.", "Defeated: returned to the cell. Time continues; loot preserved."); }
            if ((defeatReturn || !ArenaBuilder.ContainsConfinement(region, player.transform.position)) && !player.IsTeleporting() && (defeatReturn || Time.realtimeSinceStartup >= nextEnforce))
            { nextEnforce = Time.realtimeSinceStartup + 2; if (player.TeleportTo(Vector(region.CellSpawn), Quaternion.identity, true)) defeatReturn = false; }
            foreach (ItemDrop.ItemData item in player.GetInventory().GetAllItems()) if (IsLoan(item))
            {
                item.m_durability = item.GetMaxDurability();
                if (item.m_dropPrefab != null && item.m_dropPrefab.name == "ArrowWood" && item.m_stack < 100) item.m_stack = 100;
            }
        }
        private void FinishRelease()
        {
            Player player = Player.m_localPlayer; if (player == null || !WC.AdministrativeReady) return;
            if (!releaseArrived)
            {
                if (player.IsTeleporting()) return;
                Vector3 destination = Vector(localSentence.ReturnPosition);
                if (LegacyLayout && Vector3.Distance(player.transform.position, destination) > 4)
                {
                    releaseTeleport = true;
                    try { player.TeleportTo(destination, Quaternion.identity, true); } finally { releaseTeleport = false; }
                    return;
                }
                RemoveLoans(); if (!LegacyLayout) CustodyInventory.ClearReceipt(player, world, localSentence.SentenceId); MarkReleased(player); releaseArrived = true;
                notice = T("Срок окончен. Решётка откроется после сохранения; заберите вещи из четырёх сундуков.", "Sentence completed. The grille opens after saving; collect your belongings from the four chests.");
            }
            if (releaseSave == 0) releaseSave = WC.RequestAdministrativeSave();
            if (WC.IsAdministrativeSaveDurable(releaseSave))
                ToHost(PrisonProtocol.ReleaseAck, w => { PrisonProtocol.Text(w, localSentence.SentenceId); w.Write(releaseSave); });
        }
        internal bool AllowTeleport(Vector3 target)
        { return releaseTeleport || !Confined || ArenaBuilder.ContainsConfinement(region, target); }
        private static void MarkReleased(Player player)
        { var view = player.GetComponent<ZNetView>(); if (view != null && view.IsValid() && view.IsOwner()) view.GetZDO().Set(InmateKey, false); }
        internal static bool IsLoan(ItemDrop.ItemData item)
        { return item != null && item.m_customData != null && item.m_customData.ContainsKey(LoanKey); }
        private void RemoveLoans()
        {
            Player player = Player.m_localPlayer; if (player == null) return;
            bool changed = false;
            foreach (ItemDrop.ItemData item in player.GetInventory().GetAllItems().ToArray()) if (IsLoan(item))
            { player.UnequipItem(item, false); player.GetInventory().RemoveItem(item); changed = true; }
            if (changed && localSentence == null && WC.AdministrativeReady) WC.RequestAdministrativeSave();
            if (!Confined) MarkReleased(player);
        }
        private List<PrisonPlayerRow> Roster()
        {
            var rows = new List<PrisonPlayerRow>(); if (!Hosting || store == null || fatalStore) return rows;
            foreach (ZNetPeer peer in peers.Values) if (Ready(peer))
            {
                string owner = WC.GetAdministrativeOwner(peer); SentenceState state = store.Find(owner);
                rows.Add(new PrisonPlayerRow { AccountId = owner, Name = peer.m_playerName, Online = true, Sentenced = state != null, RemainingSeconds = state == null ? 0 : state.RemainingSeconds });
            }
            foreach (SentenceState state in store.All()) if (!rows.Any(r => r.AccountId == state.AccountId))
                rows.Add(new PrisonPlayerRow { AccountId = state.AccountId, Name = state.PlayerName, Online = false, Sentenced = true, RemainingSeconds = state.RemainingSeconds });
            return rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        private void RequireHost()
        { if (!Hosting || store == null || fatalStore || store.Faulted || !WC.AdministrativeReady) throw new UnauthorizedAccessException(T("Управлять тюрьмой может только хост с загруженным персонажем.", "Only the host with a loaded protected character can manage the prison.")); }
        private void Impose(string account, double minutes, string reason)
        {
            RequireHost(); ZNetPeer peer = peers.Values.FirstOrDefault(p => Ready(p) && WC.GetAdministrativeOwner(p) == account); PrisonPoint position;
            if (region == null || ArenaBuilder.LayoutVersion(region) < ArenaBuilder.CurrentLayoutVersion) throw new InvalidOperationException(T("Сначала создайте новую тюрьму возле алтарей.", "Build the new prison near the altars first."));
            if (custody.HasOutstanding || store.All().Length != 0) throw new InvalidOperationException(T("Четыре сундука заняты. Дождитесь освобождения и возврата всех вещей.", "The four chests are occupied. Wait for release and collection of all belongings."));
            CustodyInventory.RequireEmptyChests(Chests());
            if (new UTF8Encoding(false, true).GetByteCount(reason ?? "") > 1024) throw new InvalidOperationException(T("Сократите причину наказания.", "Shorten the sentence reason."));
            if (peer == null || !Position(peer, out position)) throw new InvalidOperationException(T("Игрок или его свежие координаты недоступны. Повторите через несколько секунд.", "Player or fresh position unavailable. Retry in a few seconds."));
            if (region != null && region.Contains(position)) throw new InvalidOperationException(T("Назначайте срок игроку снаружи тюрьмы, чтобы сохранить место возвращения.", "Sentence the player outside the prison to preserve a return location."));
            store.Impose(true, account, peer.m_playerName, reason ?? "", minutes * 60, position);
            notice = T("Срок назначен: ", "Sentence imposed: ") + peer.m_playerName; nextHost = Time.realtimeSinceStartup;
        }
        private void Release(string account)
        { RequireHost(); store.RequestRelease(true, account); notice = T("Освобождение назначено. Отключённый игрок будет освобождён при входе.", "Release requested. An offline player will be released on reconnect."); nextHost = Time.realtimeSinceStartup; }
        private void BuildPrison()
        { BuildPrisonCore(true); }

        private void BuildPrisonCore(bool levelGround)
        {
            RequireHost(); if (region != null && ArenaBuilder.LayoutVersion(region) >= ArenaBuilder.CurrentLayoutVersion) throw new InvalidOperationException(T("Тюрьма этого мира уже создана.", "This world's prison is already configured."));
            if (custody.HasOutstanding) throw new InvalidOperationException("Collect all stored belongings before rebuilding.");
            if (store.All().Length != 0) throw new InvalidOperationException("Release all prisoners first.");
            PrisonRegion old = region;
            string clearFailure = null;
            ArenaBuilder.BuildNearAltars(levelGround, delegate(PrisonRegion created) { store.SetRegion(true, created); }, message => clearFailure = message);
            region = store.Region; nextHost = Time.realtimeSinceStartup;
            if (old != null) ArenaBuilder.RemoveStructure(old); ArenaBuilder.SetExitLocked(region, false); network.Save(true, false, false);
            notice = levelGround
                ? T("Площадка расчищена и выровнена. Тюрьма создана возле алтарей: камера, арена и четыре железных сундука.", "Site cleared and levelled. Prison built near the altars: cell, arena and four iron chests.")
                : T("Тюрьма создана возле алтарей: камера, арена и четыре железных сундука.", "Prison built near the altars: cell, arena and four iron chests.");
            if (clearFailure != null) Report(clearFailure);
        }
        private void Move(bool arena)
        {
            if (!FightReady || Player.m_localPlayer == null) return;
            window.Hide(); Player.m_localPlayer.TeleportTo(Vector(arena ? region.ArenaSpawn : region.CellSpawn), Quaternion.identity, true);
        }
        private void RequestWave(int tier)
        { if (tier < 0 || tier > 2) return; if (Hosting) { RequireHost(); waveTier = tier; } else if (FightReady) ToHost(PrisonProtocol.Wave, w => w.Write(tier)); notice = T("Выбрана сложность следующих волн.", "Difficulty selected for subsequent waves."); }
        private void SpawnWave(int tier)
        {
            if (tier < 0 || tier > 2 || region == null) throw new InvalidOperationException("Choose wave 0, 1 or 2.");
            if (Time.realtimeSinceStartup < nextWave) throw new InvalidOperationException(T("Следующая волна станет доступна через 15 секунд.", "The next wave is available after 15 seconds."));
            if (ArenaBuilder.LiveMobCount(region) != 0) throw new InvalidOperationException(T("Сначала победите текущую волну.", "Defeat the current wave first."));
            if (!store.All().Any(s => !s.PendingRelease)) throw new InvalidOperationException(T("В тюрьме нет заключённых.", "There are no inmates."));
            string[] mobs = { "Greydwarf", "Skeleton", "Draugr" }; ArenaBuilder.SpawnWave(region, mobs[tier], 3, 1);
            nextWave = Time.realtimeSinceStartup + 15; notice = T("Волна мобов началась.", "A mob wave has started.");
        }
        private string Resolve(string value)
        {
            var matches = Roster().Where(r => r.AccountId == value || String.Equals(r.Name, value, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException(T("Игрок не найден или имя неоднозначно. Используйте ID из /prison players.", "Player missing or name ambiguous. Use ID from /prison players."));
            return matches[0].AccountId;
        }
        private void Command(Terminal.ConsoleEventArgs args)
        {
            try
            {
                Session(); string[] words = args.Args;
                if (words.Length < 2 || words[1] == "open") { if (CanUse()) window.Show(); else throw new UnauthorizedAccessException(T("Тюрьма доступна хосту и заключённым.", "Prison controls are available to the host and inmates.")); return; }
                if (words[1] == "cell") { Move(false); return; }
                if (words[1] == "arena") { Move(true); return; }
                if (words[1] == "kit") { GiveKit(); return; }
                if (words[1] == "wave") { int tier; if (words.Length != 3 || !Int32.TryParse(words[2], out tier)) throw new InvalidOperationException("prison wave <0/1/2>"); RequestWave(tier); return; }
                RequireHost();
                if (words[1] == "build" && words.Length == 2) { BuildPrison(); args.Context.AddString(notice); return; }
                if (words[1] == "players") { foreach (var row in Roster()) args.Context.AddString(row.Name + " | " + row.AccountId + " | " + (row.Online ? "online" : "offline") + " | " + Math.Ceiling(row.RemainingSeconds) + "s"); return; }
                if (words[1] == "release" && words.Length == 3) { Release(Resolve(words[2])); args.Context.AddString(notice); return; }
                if (words[1] == "jail" && words.Length >= 4)
                { double minutes; if (!Double.TryParse(words[3], NumberStyles.Float, CultureInfo.InvariantCulture, out minutes)) throw new InvalidOperationException("prison jail <account> <minutes> [reason]"); Impose(Resolve(words[2]), minutes, String.Join(" ", words.Skip(4).ToArray())); args.Context.AddString(notice); return; }
                args.Context.AddString("prison open | build | players | jail <account> <minutes> [reason] | release <account> | cell | arena | wave <0/1/2>");
            }
            catch (Exception e) { args.Context.AddString(e.Message); Report(e.Message); }
        }
        internal void ResetInput() { if (window != null) window.HandleInputReset(); }
        internal bool WindowVisible { get { return window != null && window.IsVisible; } }
        private void OnDestroy() { Reset(); if (harmony != null) harmony.UnpatchSelf(); if (Active == this) Active = null; }
    }
}
