using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimModPack.WorldCharacters
{
    [BepInPlugin(Id, "World Characters", Version)]
    [BepInIncompatibility("org.bepinex.plugins.servercharacters")]
    public sealed partial class Plugin : BaseUnityPlugin
    {
        public const string Id = "valheimmodpack.worldcharacters", Version = "1.0.4";
        private const string RpcName = "VMP_WorldCharacters_v1";
        private const int SnapshotQueueCapacity = 64;
        internal static Plugin Instance;
        private StateStore store;
        private SnapshotWriter writer;
        private int saveGeneration;
        private long produced, localDurable;
        private byte[] cachedWorldData;
        private double lastCaptureMs, lastWriteMs;
        private int lastPacketBytes;
        private float nextSlowWarning;
        private Harmony harmony;
        private string fingerprint;
        private readonly Dictionary<ZRpc, Link> links = new Dictionary<ZRpc, Link>();
        private readonly HashSet<string> leases = new HashSet<string>();
        private readonly List<ZRpc> disconnect = new List<ZRpc>();
        private ConfigEntry<int> interval;
        private ConfigEntry<bool> protectSolo;
        private readonly LocalProtectionPolicy protection = new LocalProtectionPolicy();
        private CharacterSession localHost;
        private Link server;
        private PlayerProfile protectedProfile;
        private CharacterState offered;
        private bool ready, failed, firstLoadSeen, saving, continuingLogout, loadCompleted, exitOnUpdate;
        private Player loadedPlayer;
        private float nextSave, closingAt, lastMapCapture;
        private bool closing, logoutSave, logoutScene;
        private long sent, acknowledged;
        private string message = "";
        private readonly object checkpointLock = new object();
        private byte[] checkpointCandidate;
        private long checkpointWorld;
        [ThreadStatic] private static byte[] writingCheckpoint;
        [ThreadStatic] private static long writingWorld;

        private sealed class Link
        {
            public ZNetPeer Peer;
            public CharacterState Hello;
            public CharacterSession Session;
            public string Token;
            public float Created, LastMessage;
            public bool SentHello, Rejected;
            public long World;
            public readonly SnapshotRateLimit SnapshotBudget = new SnapshotRateLimit(SnapshotQueueCapacity);
        }
        private void Awake()
        {
            Instance = this;
            interval = Config.Bind("Saving", "SnapshotSeconds", 5, new ConfigDescription("Server snapshot interval. Minimum 2 seconds.", new AcceptableValueRange<int>(2, 30)));
            protectSolo = Config.Bind("General", "ProtectSoloWorlds", false, "Enroll new local worlds without Start Server. Existing protected characters stay protected in solo play. Takes effect when entering a world.");
            string dataRoot = Path.Combine(Path.GetDirectoryName(Paths.BepInExRootPath), "ValheimModpack", "WorldCharacters");
            store = new StateStore(dataRoot);
            writer = new SnapshotWriter(SnapshotQueueCapacity);
            harmony = new Harmony(Id); harmony.PatchAll(typeof(Plugin).Assembly);
            new Terminal.ConsoleCommand("wc", "World Characters: status, timings, pending, inspect/approve/fresh/reject <id>", (Terminal.ConsoleEvent)Command);
            Logger.LogInfo("World Characters ready. Server data: " + dataRoot);
        }
        private string Fingerprint { get { return fingerprint ?? (fingerprint = GameState.BuildFingerprint()); } }
        private static byte[] PacketBytes(ZPackage p)
        {
            int length = p.ReadInt();
            if (length < 0 || length > StateCodec.MaximumBytes || length > p.Size() - p.GetPos()) throw new InvalidDataException("Invalid packet field length.");
            byte[] result = new byte[length]; Buffer.BlockCopy(p.GetArray(), p.GetPos(), result, 0, length);
            p.SetPos(p.GetPos() + length); return result;
        }
        private bool Hosting
        {
            get
            {
                if (!ZNet.instance || !ZNet.instance.IsServer()) return false;
                try
                {
                    PlayerProfile profile = Game.instance ? Game.instance.GetPlayerProfile() : null;
                    return protection.Resolve(ZNet.instance, ZNet.instance.GetWorldUID(), profile == null ? 0 : profile.GetPlayerID(),
                        ZNet.IsOpenServer(), protectSolo.Value, store);
                }
                catch (Exception e)
                {
                    Fail("Cannot determine protected character state: " + e.Message);
                    return true; // Never bypass protection because a state directory is unreadable.
                }
            }
        }
        private bool Managed { get { return protectedProfile != null; } }
        internal bool BlockInput { get { return Managed && (!ready || failed || closing); } }
        private void Update()
        {
            DrainWrites();
            if (exitOnUpdate)
            {
                exitOnUpdate = false;
                if (Game.instance) { continuingLogout = true; try { Game.instance.Logout(false, true); } finally { continuingLogout = false; } }
            }
            foreach (ZRpc rpc in disconnect.ToArray())
            {
                disconnect.Remove(rpc);
                Link rejected; ZNetPeer peer = links.TryGetValue(rpc, out rejected) ? rejected.Peer : null;
                if (peer != null) ZNet.instance.Disconnect(peer);
            }
            float now = Time.realtimeSinceStartup;
            foreach (Link link in links.Values.ToArray())
            {
                if (link.Rejected) continue;
                if (Hosting && ((link.Session == null && now - link.Created > 120)
                    || (link.Session != null && !link.Session.Loaded && now - link.Created > 120)
                    || (link.Session != null && link.Session.Loaded && !link.Session.Closed && now - link.LastMessage > 90)))
                    Reject(link, "Character handshake or snapshot timed out.");
            }
            if (Managed && !Hosting && !ready && !failed && server != null && now - server.Created > 120)
                Fail("The server did not provide a World Characters save. Install the same modpack on the host.");
            if (ready && !failed && !closing && now >= nextSave)
            {
                nextSave = now + interval.Value;
                try { Publish(false); } catch (Exception e) { Fail("Character save failed: " + e.Message); }
            }
            // A final capture may still be queued and not yet sent. Never let
            // acknowledged >= sent bypass its local durable write.
            bool locallySaved = localHost != null ? localHost.LastSequence == localHost.AcceptedSequence : localDurable >= produced;
            if (closing && locallySaved && (localHost != null || acknowledged >= produced || now - closingAt > 8))
            {
                if (acknowledged < produced && localHost == null) Logger.LogWarning("Logout snapshot was not acknowledged. Recovery copy retained; it is NOT automatically imported.");
                closing = false; ready = false; continuingLogout = true;
                try { if (Game.instance) Game.instance.Logout(logoutSave, logoutScene); }
                finally { continuingLogout = false; }
            }
        }
        private void DrainWrites()
        {
            if (writer == null) return;
            try { writer.Drain(); }
            catch (Exception e) { Fail("Saved snapshot completion failed: " + e.Message); }
        }
        private void FlushWrites()
        {
            if (writer == null) return;
            writer.Barrier(); DrainWrites();
        }
        private void SavedTiming(double milliseconds)
        {
            lastWriteMs = milliseconds;
            if (milliseconds >= 50 && Time.realtimeSinceStartup >= nextSlowWarning)
            {
                nextSlowWarning = Time.realtimeSinceStartup + 30;
                Logger.LogWarning("Background snapshot took " + milliseconds.ToString("F1") + " ms. Game-thread capture: " + lastCaptureMs.ToString("F1") + " ms.");
            }
        }
        private static long RetainedBytes(CharacterState state)
        { return 4L * (state.Player.Length + state.WorldData.Length + 512); }
        private static bool Connected(Link link)
        { return link != null && !link.Rejected && link.Peer != null && link.Peer.m_rpc != null && link.Peer.m_rpc.IsConnected(); }
        private void OnGUI()
        {
            if (String.IsNullOrEmpty(message)) return;
            var rect = new Rect(Math.Max(10, (Screen.width - 720) / 2), 30, Math.Min(720, Screen.width - 20), 180);
            var style = new GUIStyle(GUI.skin.box) { wordWrap = true, alignment = TextAnchor.MiddleCenter, fontSize = 16 };
            GUI.Box(rect, "World Characters\n\n" + message + "\n\nПодробности: BepInEx/LogOutput.log", style);
            if (!Game.instance && GUI.Button(new Rect(rect.x + rect.width / 2 - 70, rect.yMax + 6, 140, 30), "Закрыть")) message = "";
        }
        private void Fail(string reason)
        {
            if (failed) return;
            failed = true; ready = false; message = reason; Logger.LogError(reason);
            if (server != null) Reject(server, reason);
            // Never destroy Game/Player in the middle of its load stack. Input/update gates close immediately.
            exitOnUpdate = true;
        }
        private static string Account(ZNetPeer peer)
        {
            if (peer.m_socket == null || peer.m_socket.GetType().Name != "ZSteamSocket")
                throw new InvalidOperationException("World Characters 1.0 supports Steam connections only. Disable crossplay on the host.");
            string value = peer.m_socket.GetHostName(); ulong id;
            if (value.Length != 17 || !UInt64.TryParse(value, out id) || id == 0) throw new InvalidDataException("Invalid authenticated Steam identity.");
            return "Steam_" + value;
        }
        private void Register(ZNetPeer peer)
        {
            var link = new Link { Peer = peer, Created = Time.realtimeSinceStartup, LastMessage = Time.realtimeSinceStartup };
            links.Add(peer.m_rpc, link);
            peer.m_rpc.Register<ZPackage>(RpcName, Receive);
            if (!ZNet.instance.IsServer())
            {
                server = link; protectedProfile = Game.instance.GetPlayerProfile();
                ready = failed = firstLoadSeen = false; sent = acknowledged = 0; message = "";
            }
            else if (Hosting)
            {
                Send(link, 0, p => { p.Write(Version); p.Write(Fingerprint); p.Write(ZNet.instance.GetWorldUID()); });
            }
        }
        private static void Send(Link link, int kind, Action<ZPackage> write)
        {
            var p = new ZPackage(); p.Write(kind); write(p); link.Peer.m_rpc.Invoke(RpcName, p);
        }
        private void Reject(Link link, string reason)
        {
            if (link.Rejected) return;
            link.Rejected = true; Logger.LogWarning(reason);
            try { Send(link, 9, p => p.Write(reason)); } catch (Exception) { }
            disconnect.Add(link.Peer.m_rpc);
        }
        // Sent immediately before vanilla PeerInfo; that message still validates the game's password/authentication.
        private bool BeforeSendPeerInfo(ZRpc rpc)
        {
            Link link;
            if (!links.TryGetValue(rpc, out link)) return false;
            try
            {
                if (!ZNet.instance.IsServer())
                {
                    if (link.SentHello) return true;
                    link.SentHello = true;
                    PlayerProfile profile = Game.instance.GetPlayerProfile();
                    if (link.World == 0) throw new InvalidDataException("Host did not announce a protected world. Install the same modpack on the host.");
                    // Include the existing map, bed and logout position for THIS world during migration.
                    CharacterState initial = GameState.FromProfile(profile, link.World, "candidate", Fingerprint, false);
                    byte[] bytes = StateCodec.Encode(initial);
                    Send(link, 1, p => { p.Write(Version); p.Write(Fingerprint); p.Write(bytes); });
                    return true;
                }
                if (!Hosting) { Reject(link, "This host has not enabled a protected multiplayer world."); return false; }
                if (link.Hello == null) { Reject(link, "Install the same World Characters modpack on every player PC."); return false; }
                // This call occurs from vanilla RPC_PeerInfo after the Steam/password checks and peer UID assignment.
                if (!link.Peer.IsReady()) throw new InvalidOperationException("Vanilla peer authentication did not complete.");
                string owner = Account(link.Peer); long world = ZNet.instance.GetWorldUID();
                CharacterState candidate = link.Hello.Copy(); candidate.Owner = owner; candidate.World = world; candidate.Build = Fingerprint;
                if (link.Hello.World != world) throw new InvalidDataException("Client joined a different world than announced.");
                CharacterState state = store.Read(world, owner, candidate.Character);
                if (state == null)
                {
                    string id = store.Propose(candidate);
                    Reject(link, "Первый вход требует одобрения хоста. Запрос: " + id + ". Хост: wc inspect / wc approve или wc fresh.");
                    return false;
                }
                string key = StateCodec.Key(world, owner, candidate.Character);
                if (!leases.Add(key)) throw new InvalidOperationException("This character already has an active connection.");
                link.Session = new CharacterSession(state); link.Created = Time.realtimeSinceStartup;
                Send(link, 2, p => { p.Write(link.Session.Token); p.Write(StateCodec.Encode(state)); });
                return true;
            }
            catch (Exception e) { Reject(link, e.Message); return false; }
        }
        private bool BeforePeerInfo(ZRpc rpc)
        {
            Link link;
            if (!links.TryGetValue(rpc, out link) || link.Rejected) return false;
            if (ZNet.instance.IsServer())
            {
                if (link.Hello == null) { Reject(link, "Missing World Characters handshake. Install the same modpack."); return false; }
                return true;
            }
            if (offered == null || String.IsNullOrEmpty(link.Token))
            { Fail("Host did not supply a protected character. Connection cancelled before spawning."); return false; }
            return true;
        }
        private void Receive(ZRpc rpc, ZPackage p)
        {
            Link link;
            if (!links.TryGetValue(rpc, out link) || link.Rejected) return;
            try
            {
                if (p.Size() > StateCodec.MaximumBytes + 1024) throw new InvalidDataException("Oversized protocol packet.");
                int kind = p.ReadInt();
                if (kind == 9)
                {
                    string reason = p.ReadString(); if (reason.Length > 2048) reason = "Peer rejected the character session.";
                    if (!ZNet.instance.IsServer()) Fail(reason); else Reject(link, "Client rejected restore: " + reason);
                    return;
                }
                if (ZNet.instance.IsServer()) ReceiveServer(link, kind, p); else ReceiveClient(link, kind, p);
                if (p.GetPos() != p.Size()) throw new InvalidDataException("Trailing protocol bytes.");
            }
            catch (Exception e)
            {
                if (ZNet.instance && ZNet.instance.IsServer()) Reject(link, "World Characters: " + e.Message);
                else Fail("World Characters: " + e.Message);
            }
        }
        private void ReceiveServer(Link link, int kind, ZPackage p)
        {
            if (kind == 1)
            {
                if (link.Hello != null || link.Session != null) throw new InvalidDataException("Duplicate hello.");
                if (p.ReadString() != Version || p.ReadString() != Fingerprint) throw new InvalidDataException("Modpack DLLs or game version differ from the host.");
                link.Hello = StateCodec.Decode(PacketBytes(p));
                return;
            }
            if (link.Session == null) throw new InvalidDataException("No approved character session.");
            if (kind == 3)
            {
                if (link.Session.Loaded) throw new InvalidDataException("Duplicate load acknowledgement.");
                link.Session.MarkLoaded(p.ReadString()); link.LastMessage = Time.realtimeSinceStartup; return;
            }
            if (kind == 4)
            {
                string token = p.ReadString(); long sequence = p.ReadLong(); bool final = p.ReadBool(); bool includesMap = p.ReadBool();
                // Durable completions can arrive together after a slow disk or
                // shutdown barrier. Allow the bounded backlog, while retaining
                // the five-per-second sustained limit for an active session.
                if (!link.SnapshotBudget.TryTake(Time.realtimeSinceStartup))
                    throw new InvalidDataException("Too many character snapshots.");
                byte[] packet = PacketBytes(p);
                CharacterState update = StateCodec.Decode(packet);
                if (!includesMap) update.WorldData = link.Session.RetainAcceptedMap(update.WorldData);
                update.Build = Fingerprint;
                long retained = RetainedBytes(update);
                // Admission is exclusively on the Unity thread. Capacity cannot
                // grow between this check and enqueue; workers only release it.
                if (!writer.CanEnqueue(retained)) throw new IOException("Snapshot writer is overloaded; last durable character retained.");
                CharacterState next = link.Session.Stage(token, sequence, update, final);
                int generation = saveGeneration;
                if (!writer.TryEnqueue(retained, () => { store.Save(next, next.Revision - 1); return next; }, (result, error, elapsed) =>
                {
                    if (generation != saveGeneration) return;
                    SavedTiming(elapsed);
                    if (error != null) { Reject(link, "Character disk save failed; previous revision retained: " + error.Message); return; }
                    try
                    {
                        link.Session.Committed((CharacterState)result, sequence, final);
                        if (Connected(link)) Send(link, 5, reply => { reply.Write(sequence); reply.Write(next.Revision); });
                    }
                    catch (Exception e) { Reject(link, "Character commit failed: " + e.Message); }
                })) throw new IOException("Snapshot writer stopped accepting saves.");
                lastPacketBytes = packet.Length;
                link.LastMessage = Time.realtimeSinceStartup;
                return;
            }
            if (kind == 6) { Reject(link, "Client reports incomplete character load."); return; }
            throw new InvalidDataException("Unexpected client message.");
        }
        private void ReceiveClient(Link link, int kind, ZPackage p)
        {
            if (!ReferenceEquals(link, server)) throw new InvalidDataException("Restore came from a non-server peer.");
            if (kind == 0)
            {
                if (link.World != 0 || link.SentHello || p.ReadString() != Version || p.ReadString() != Fingerprint)
                    throw new InvalidDataException("Host modpack or protocol mismatch.");
                link.World = p.ReadLong(); if (link.World == 0) throw new InvalidDataException("Invalid world UID.");
                return;
            }
            if (kind == 2)
            {
                if (offered != null || ready) throw new InvalidDataException("Duplicate server restore.");
                link.Token = p.ReadString(); if (link.Token.Length != 32) throw new InvalidDataException("Invalid session token.");
                CharacterState state = StateCodec.Decode(PacketBytes(p));
                CharacterState original = GameState.FromProfile(protectedProfile, state.World, state.Owner, Fingerprint, false);
                store.Archive("before-restore", original);
                GameState.Apply(protectedProfile, state); offered = state; return;
            }
            if (kind == 5)
            {
                long sequence = p.ReadLong(), revision = p.ReadLong();
                if (sequence <= acknowledged || sequence > sent || offered == null || revision <= offered.Revision)
                    throw new InvalidDataException("Invalid save acknowledgement.");
                acknowledged = sequence; offered.Revision = revision; return;
            }
            throw new InvalidDataException("Unexpected server message.");
        }
        // The host has no connection to itself. Restore before the initial respawn chooses its saved position.
        private bool PrepareHost()
        {
            if (!Hosting || Managed) return !failed;
            if (failed) return false;
            try
            {
                protectedProfile = Game.instance.GetPlayerProfile(); long world = ZNet.instance.GetWorldUID();
                CharacterState candidate = GameState.FromProfile(protectedProfile, world, "local-host", Fingerprint, false);
                offered = store.Read(world, candidate.Owner, candidate.Character);
                if (offered == null)
                {
                    store.Archive("before-restore", candidate);
                    offered = candidate.Copy(); offered.Revision = 1; store.Save(offered, 0);
                    Logger.LogWarning("Trusted local host imported for world " + world + ". Remote characters require explicit approval.");
                }
                localHost = new CharacterSession(offered); GameState.Apply(protectedProfile, offered);
                return true;
            }
            catch (Exception e) { Fail("Cannot restore host character: " + e.Message); return false; }
        }
        private bool BeforeLoad(PlayerProfile profile)
        {
            if (!Managed && Hosting && !PrepareHost()) return false;
            if (!ReferenceEquals(profile, protectedProfile)) return true;
            if (failed || offered == null) { Fail("Character load attempted without an approved state."); return false; }
            if (!firstLoadSeen)
            {
                firstLoadSeen = true;
                if (ZNet.instance.GetWorldUID() != offered.World) { Fail("Host world UID differs from the restored state."); return false; }
                try { GameState.RequireSavedPrefabs(offered); }
                catch (Exception e) { Fail(e.Message); return false; }
                // Respawns later use the game's current state (including death), never the initial join snapshot.
                GameState.Apply(profile, offered);
            }
            return true;
        }
        private void Spawned(Player player)
        {
            if (!Managed || player != Player.m_localPlayer || ready || failed) return;
            try
            {
                if (!firstLoadSeen || !loadCompleted || (offered.Player.Length > 0 && loadedPlayer != player))
                    throw new InvalidOperationException("Original character loader did not complete.");
                GameState.VerifyLoaded(player, offered);
                if (localHost != null) localHost.MarkLoaded(localHost.Token);
                else Send(server, 3, p => p.Write(server.Token));
                cachedWorldData = (byte[])offered.WorldData.Clone();
                ready = true; nextSave = Time.realtimeSinceStartup + interval.Value;
                Logger.LogInfo("Restored protected character " + offered.Name + " world=" + offered.World + " revision=" + offered.Revision);
            }
            catch (Exception e) { Fail("Restore validation failed. Previous save retained: " + e.Message); }
        }
        private void Publish(bool final, bool saveMap = false, bool mandatory = false)
        {
            if (!ready || failed || saving || (closing && !final)) return;
            if (localHost != null && localHost.AcceptedFinal) return;
            // Keep at most one ordinary snapshot in flight per local character.
            // Death, native saves and logout still capture every mandatory state.
            if (!final && !mandatory && (localHost != null ? localHost.AcceptedSequence > localHost.LastSequence : produced > acknowledged)) return;
            // During death/respawn the local Player temporarily does not exist. Its last native
            // profile snapshot is sent by the SavePlayerData hook before destruction instead.
            if (!Player.m_localPlayer && !final) return;
            saving = true;
            var captureClock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (!writer.CanEnqueue(512))
                {
                    if (!final && !mandatory) return;
                    FlushWrites();
                    if (failed) throw new IOException("Previous character save failed.");
                }
                bool includesMap = final || saveMap || lastMapCapture <= 0 || Time.realtimeSinceStartup - lastMapCapture >= 60;
                if (includesMap && Minimap.instance) { Minimap.instance.SaveMapData(); lastMapCapture = Time.realtimeSinceStartup; }
                CharacterState update = GameState.FromProfile(protectedProfile, offered.World, offered.Owner, Fingerprint, Player.m_localPlayer != null, includesMap);
                if (includesMap) cachedWorldData = update.WorldData;
                byte[] mapSource = cachedWorldData ?? new byte[0];
                long retained = RetainedBytes(update) + (includesMap ? 0 : 4L * mapSource.Length);
                if (!writer.CanEnqueue(retained))
                {
                    if (!final && !mandatory) return;
                    FlushWrites();
                    if (failed || !writer.CanEnqueue(retained)) throw new IOException("Snapshot writer is overloaded; previous saved character retained.");
                }
                int generation = saveGeneration;
                if (localHost != null)
                {
                    if (!includesMap) update.WorldData = StateCodec.RetainMap(update.WorldData, mapSource);
                    CharacterSession session = localHost;
                    long sequence = session.AcceptedSequence + 1;
                    CharacterState next = session.Stage(session.Token, sequence, update, final);
                    if (!writer.TryEnqueue(retained, () => { store.Save(next, next.Revision - 1); return next; }, (result, error, elapsed) =>
                    {
                        if (generation != saveGeneration) return;
                        SavedTiming(elapsed);
                        if (error != null) { Fail("Host character disk save failed; earlier revision retained: " + error.Message); return; }
                        session.Committed((CharacterState)result, sequence, final); offered = session.State;
                    })) throw new IOException("Snapshot writer stopped accepting saves.");
                }
                else
                {
                    if (server == null || server.Rejected) throw new IOException("Server connection is unavailable.");
                    Link link = server;
                    long sequence = produced + 1;
                    if (!writer.TryEnqueue(retained, () =>
                    {
                        // Recovery contains the last captured map, but position-only
                        // RPCs avoid copying that map on the Unity thread.
                        CharacterState recovery = update.Copy();
                        if (!includesMap) recovery.WorldData = StateCodec.RetainMap(update.WorldData, mapSource);
                        store.Archive("client-recovery", recovery);
                        return StateCodec.Encode(update);
                    }, (result, error, elapsed) =>
                    {
                        if (generation != saveGeneration || !ReferenceEquals(server, link)) return;
                        SavedTiming(elapsed);
                        if (error != null) { Fail("Local recovery save failed: " + error.Message); return; }
                        localDurable = sequence;
                        byte[] packet = (byte[])result; lastPacketBytes = packet.Length;
                        if (!Connected(link)) return;
                        try
                        {
                            Send(link, 4, p => { p.Write(link.Token); p.Write(sequence); p.Write(final); p.Write(includesMap); p.Write(packet); });
                            sent = sequence;
                        }
                        catch (Exception e) { Fail("Saved snapshot could not reach host: " + e.Message); }
                    })) throw new IOException("Snapshot writer stopped accepting saves.");
                    produced = sequence;
                }
            }
            finally { captureClock.Stop(); lastCaptureMs = captureClock.Elapsed.TotalMilliseconds; saving = false; }
        }
        private bool BeforeLogout(bool save, bool changeScene)
        {
            if (continuingLogout || !ready || failed) return true;
            if (!closing)
            {
                try { Publish(true); closing = true; closingAt = Time.realtimeSinceStartup; logoutSave = save; logoutScene = changeScene; }
                catch (Exception e) { Logger.LogError("Final snapshot failed: " + e); return true; }
            }
            return false;
        }
        private void Removed(ZNetPeer peer)
        {
            // Lease release and reconnect must observe all admitted durable writes.
            FlushWrites();
            Link link;
            if (!links.TryGetValue(peer.m_rpc, out link)) return;
            if (link.Session != null)
            {
                CharacterState s = link.Session.State;
                leases.Remove(StateCodec.Key(s.World, s.Owner, s.Character));
                if (!link.Session.Closed) Logger.LogWarning("Unclean disconnect: " + s.Name + "; last server revision=" + s.Revision);
            }
            links.Remove(peer.m_rpc);
        }
        private void ResetSession()
        {
            FlushWrites(); ++saveGeneration;
            protection.Clear();
            links.Clear(); leases.Clear(); disconnect.Clear(); localHost = null; server = null; offered = null; protectedProfile = null;
            ready = failed = firstLoadSeen = saving = closing = continuingLogout = loadCompleted = exitOnUpdate = false;
            loadedPlayer = null; sent = acknowledged = 0; lastMapCapture = 0;
            produced = localDurable = 0; cachedWorldData = null;
        }
        private void Command(Terminal.ConsoleEventArgs args)
        {
            try
            {
                if (args.Length < 2 || args[1] == "status" || args[1] == "timings")
                {
                    args.Context.AddString("World Characters " + Version + ": protected=" + Managed + " loaded=" + ready + " failed=" + failed + " pending saves=" + (produced - acknowledged));
                    args.Context.AddString("Capture=" + lastCaptureMs.ToString("F2") + " ms; background write=" + lastWriteMs.ToString("F2") + " ms; packet=" + lastPacketBytes + " bytes; queue=" + writer.PendingCount + " jobs / " + writer.PendingBytes + " bytes."); return;
                }
                if (!Hosting) throw new InvalidOperationException("Run administration commands on the local host or dedicated server console.");
                if (args[1] == "pending")
                {
                    foreach (string id in store.PendingIds())
                    {
                        var s = store.Pending(id);
                        if (s.World != ZNet.instance.GetWorldUID() || store.Read(s.World, s.Owner, s.Character) != null) continue;
                        args.Context.AddString(id.Substring(0, 12) + " | " + s.Name + " | " + s.Owner + " | items=" + GameState.Items(s).Count);
                    }
                    return;
                }
                if (args.Length != 3 || args[2].Length < 8) throw new ArgumentException("Use wc inspect/approve/fresh/reject <request ID, at least 8 characters>.");
                string[] matches = store.PendingIds().Where(id => id.StartsWith(args[2], StringComparison.Ordinal)).ToArray();
                if (matches.Length != 1) throw new ArgumentException("Request ID is missing or ambiguous.");
                string request = matches[0];
                CharacterState candidate = store.Pending(request);
                if (candidate.World != ZNet.instance.GetWorldUID()) throw new InvalidOperationException("Request belongs to another world.");
                if (args[1] == "inspect")
                {
                    args.Context.AddString(candidate.Name + " | " + candidate.Owner + " | world=" + candidate.World);
                    foreach (var item in NativeInventory.IncludingBackpacks(GameState.Items(candidate))) args.Context.AddString(GameState.ItemName(item.Prefab) + " x" + item.Count + " quality=" + item.Quality + " metadata=" + String.Join(",", item.Custom.Keys.ToArray()));
                    args.Context.AddString("approve imports ALL progress and backpack contents; fresh starts with default items and skills."); return;
                }
                if (args[1] == "reject") { store.RejectProposal(request); args.Context.AddString("Request rejected. Player can remove unwanted items and submit again by reconnecting."); return; }
                if (args[1] != "approve" && args[1] != "fresh") throw new ArgumentException("Unknown command.");
                store.Approve(request, args[1] == "fresh");
                args.Context.AddString("Approved " + candidate.Name + ". The player can reconnect.");
            }
            catch (Exception e) { args.Context.AddString("World Characters: " + e.Message); }
        }
        private void OnDestroy()
        {
            if (writer != null) { FlushWrites(); writer.Dispose(); }
            if (harmony != null) harmony.UnpatchSelf(); if (store != null) store.Dispose(); if (Instance == this) Instance = null;
        }

        // PrepareSave runs on the main thread after any previous save thread has joined.
        // Capture accepted character revisions at the same boundary as the world snapshot.
        [HarmonyPatch(typeof(ZDOMan), "PrepareSave")]
        private static class CheckpointPreparePatch
        {
            private static void Prefix()
            {
                if (!Instance.Hosting) return;
                try
                {
                    // Game.Shutdown saves the character BEFORE ZNetScene.Shutdown
                    // resets its ZDO, then saves the world. Use that committed state
                    // here instead of trying to serialize the dismantled Player again.
                    if (!Game.instance || !Game.instance.IsShuttingDown()) Instance.Publish(false, false, true);
                    Instance.FlushWrites();
                    if (Instance.failed) throw new IOException("Character writer failed; checkpoint not committed.");
                    long world = ZNet.instance.GetWorldUID(); byte[] bytes = Instance.store.CaptureCheckpoint(world);
                    lock (Instance.checkpointLock) { Instance.checkpointWorld = world; Instance.checkpointCandidate = bytes; }
                }
                catch (Exception e) { Instance.Logger.LogError("Could not prepare character checkpoint: " + e); lock (Instance.checkpointLock) Instance.checkpointCandidate = null; }
            }
        }
        [HarmonyPatch(typeof(ZNet), "SaveWorldThread")]
        private static class WorldWritePatch
        {
            private static void Prefix()
            {
                lock (Instance.checkpointLock)
                { writingCheckpoint = Instance.checkpointCandidate; writingWorld = Instance.checkpointWorld; Instance.checkpointCandidate = null; }
            }
            private static Exception Finalizer(Exception __exception) { writingCheckpoint = null; writingWorld = 0; return __exception; }
        }
        [HarmonyPatch(typeof(SaveSystem), "EndSave")]
        private static class WorldCommitPatch
        {
            private static void Postfix(bool __1)
            {
                if (!__1 || writingCheckpoint == null) return;
                try { Instance.store.CommitCheckpoint(writingWorld, writingCheckpoint); Instance.Logger.LogInfo("World Characters checkpoint saved for world " + writingWorld); }
                catch (Exception e) { Instance.Logger.LogError("World saved, character checkpoint failed; retain earlier backups: " + e); }
                writingCheckpoint = null;
            }
        }

        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        private static class ConnectPatch { private static void Prefix(ZNetPeer peer) { Instance.Register(peer); } }
        [HarmonyPatch(typeof(ZNet), "SendPeerInfo")]
        private static class SendPatch { private static bool Prefix(ZRpc rpc) { return Instance.BeforeSendPeerInfo(rpc); } }
        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
        private static class PeerPatch { private static bool Prefix(ZRpc rpc) { return Instance.BeforePeerInfo(rpc); } }
        [HarmonyPatch(typeof(ZNet), "Disconnect")]
        private static class DisconnectPatch { private static void Prefix(ZNetPeer peer) { Instance.Removed(peer); } }
        [HarmonyPatch(typeof(Game), "RequestRespawn")]
        private static class RespawnPatch { private static bool Prefix() { return Instance.PrepareHost(); } }
        [HarmonyPatch(typeof(PlayerProfile), "LoadPlayerData")]
        private static class LoadPatch
        {
            [HarmonyPriority(Priority.First)] private static bool Prefix(PlayerProfile __instance) { return Instance.BeforeLoad(__instance); }
            [HarmonyPriority(Priority.Last)] private static void Postfix(PlayerProfile __instance, bool __runOriginal)
            { if (ReferenceEquals(__instance, Instance.protectedProfile) && !Instance.ready) Instance.loadCompleted = __runOriginal; }
            private static Exception Finalizer(Exception __exception)
            { if (__exception != null && Instance.Managed) Instance.Fail("Character loader threw: " + __exception.Message); return __exception; }
        }
        [HarmonyPatch(typeof(Player), "Load")]
        private static class PlayerLoadPatch
        {
            [HarmonyPriority(Priority.Last)] private static void Postfix(Player __instance, bool __runOriginal)
            { if (__runOriginal && Instance.Managed && !Instance.ready) Instance.loadedPlayer = __instance; }
        }
        [HarmonyPatch(typeof(Player), "Update")]
        private static class PlayerUpdatePatch
        {
            private static bool Prefix(Player __instance)
            { return __instance != Player.m_localPlayer || !Instance.Managed || (Instance.ready && !Instance.failed && !Instance.closing); }
        }
        [HarmonyPatch(typeof(Player), "OnSpawned")]
        private static class SpawnPatch { [HarmonyPriority(Priority.Last)] private static void Postfix(Player __instance) { Instance.Spawned(__instance); } }
        [HarmonyPatch(typeof(Game), "SavePlayerProfile")]
        private static class SavePatch
        {
            private static bool Prefix(ref float ___m_saveTimer)
            {
                if (!Instance.Managed) return true;
                // Native SavePlayerProfile resets this timer. Skipping the
                // original without resetting it triggers autosave every frame.
                ___m_saveTimer = 0;
                try
                {
                    Instance.Publish(false, true, true);
                    if (Game.instance && Game.instance.IsShuttingDown()) Instance.FlushWrites();
                } catch (Exception e) { Instance.Fail(e.Message); }
                return false;
            }
        }
        [HarmonyPatch(typeof(PlayerProfile), "SavePlayerData")]
        private static class NativeSavePatch
        {
            private static void Postfix(PlayerProfile __instance)
            {
                if (!ReferenceEquals(__instance, Instance.protectedProfile) || !Instance.ready || Instance.saving || Instance.closing) return;
                try { Instance.Publish(false, false, true); } catch (Exception e) { Instance.Fail(e.Message); }
            }
        }
        [HarmonyPatch(typeof(PlayerProfile), "SavePlayerToDisk")]
        private static class DiskPatch
        {
            private static bool Prefix(PlayerProfile __instance, ref bool __result)
            { if (!ReferenceEquals(__instance, Instance.protectedProfile)) return true; __result = true; return false; }
        }
        [HarmonyPatch(typeof(Player), "TakeInput")]
        private static class InputPatch { private static bool Prefix(ref bool __result) { if (!Instance.BlockInput) return true; __result = false; return false; } }
        [HarmonyPatch(typeof(Game), "Logout")]
        private static class LogoutPatch { private static bool Prefix(bool save, bool changeToStartScene) { return Instance.BeforeLogout(save, changeToStartScene); } }
        [HarmonyPatch(typeof(Game), "OnDestroy")]
        private static class EndPatch { private static void Postfix() { Instance.ResetSession(); } }
    }
}
