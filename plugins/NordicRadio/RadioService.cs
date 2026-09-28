using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using BepInEx;
using UnityEngine;

namespace ValheimModPack.NordicRadio
{
    public sealed class RadioService : IDisposable
    {
        private readonly Action<string> log;
        private readonly RadioLibrary library;
        private readonly IRadioBulkTransport bulk;
        private readonly HashSet<long> pendingLibraries = new HashSet<long>();
        private readonly Dictionary<long, double> libraryRetryAt = new Dictionary<long, double>();
        private readonly Queue<Action> completions = new Queue<Action>();
        private readonly List<TrackInfo> tracks = new List<TrackInfo>();
        private readonly Dictionary<string, LibraryEntry> sources = new Dictionary<string, LibraryEntry>(StringComparer.Ordinal);
        private readonly Dictionary<ZDOID, RadioSnapshot> states = new Dictionary<ZDOID, RadioSnapshot>();
        private readonly Dictionary<ZDOID, PortableLease> portables = new Dictionary<ZDOID, PortableLease>();
        private readonly Dictionary<ZDOID, double> watched = new Dictionary<ZDOID, double>();
        private readonly Dictionary<long, Dictionary<ZDOID, double>> subscribers = new Dictionary<long, Dictionary<ZDOID, double>>();
        private readonly Dictionary<long, UploadQueue> chunkRequests = new Dictionary<long, UploadQueue>();
        private readonly Queue<long> chunkOrder = new Queue<long>();
        private readonly HashSet<long> scheduledUploads = new HashSet<long>();
        private readonly Dictionary<long, double> greetings = new Dictionary<long, double>();
        private readonly Dictionary<long, RadioTokenBucket> peerBudgets = new Dictionary<long, RadioTokenBucket>();
        private readonly List<string> downloads = new List<string>();
        private readonly HashSet<string> waiting = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, double> wanted = new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly Dictionary<string, double> prefetched = new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> statuses = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, double> retryAfter = new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly System.Random random = new System.Random();
        private readonly RadioTokenBucket upload = new RadioTokenBucket(1024 * 1024, 48 * 1024);
        private ZRoutedRpc registeredRpc;
        private ZRoutedRpc sessionRpc;
        private long sessionWorld;
        private long sessionServer;
        private volatile int generation;
        private int scanning;
        private int uploadWorkers;
        private volatile bool disposed;
        private bool receivedLibrary;
        private double nextHello;
        private double sessionStarted;
        private double nextMaintenance;
        private double nextState;
        private double nextErrorLog;
        private double clockGame;
        private double clockReal;
        private bool clockSet;
        private bool needsScan;
        private Download active;
        private bool checkingCache;
        private string libraryStatus = "";
        private ZDOID localPortable;
        private string localPortableToken = "";
        private double nextPortablePresence;
        private static readonly int PrefabHash = "vmp_skald_horn".GetStableHashCode();

        private sealed class PortableLease
        {
            public long Peer;
            public string Token;
            public double Expires;
        }

        private sealed class UploadQueue
        {
            public string Id;
            public readonly Queue<RadioMessage> Requests = new Queue<RadioMessage>();
            public readonly HashSet<long> Offsets = new HashSet<long>();
            public bool Reading;
            public RadioMessage Ready;
        }

        private sealed class Download : IDisposable
        {
            public TrackInfo Track;
            public RadioDownloadWriter Writer;
            public readonly Dictionary<long, double> Requested = new Dictionary<long, double>();
            public readonly Dictionary<long, byte[]> Received = new Dictionary<long, byte[]>();
            public long NextRequest;
            public long Offset;
            public bool Writing;
            public double StartedAt, LastProgress;
            public int Retries;
            public void Dispose()
            {
                if (Writer != null) Writer.Dispose();
                Requested.Clear(); Received.Clear();
            }
        }

        internal RadioService(BaseUnityPlugin host, string dataRoot, Action<string> log, IRadioBulkTransport bulk)
        {
            this.log = log ?? delegate { };
            if (bulk == null) throw new ArgumentNullException("bulk");
            this.bulk = bulk;
            library = new RadioLibrary(dataRoot);
        }

        public bool IsHost { get { return !disposed && ZNet.instance != null && ZNet.instance.IsServer(); } }
        public int UploadKiBPerSecond
        {
            get { return (int)(upload.Rate / 1024); }
            set { upload.Rate = Math.Max(64, Math.Min(4096, value)) * 1024; }
        }
        public string LibraryStatus { get { return libraryStatus; } }
        private int downloadWindow = 8, maxQueuedKiB = 256;
        public int DownloadWindow { get { return downloadWindow; } set { downloadWindow = Math.Max(1, Math.Min(8, value)); } }
        public int MaxQueuedKiB { get { return maxQueuedKiB; } set { maxQueuedKiB = Math.Max(32, Math.Min(256, value)); } }
        public bool DownloadEnabled = true;
        public IList<TrackInfo> Tracks { get { return tracks.AsReadOnly(); } }
        private static double Realtime { get { return Time.realtimeSinceStartup; } }
        private static double Now { get { return ZNet.instance == null ? 0 : ZNet.instance.GetTimeSeconds(); } }

        public void Tick()
        {
            if (disposed) return;
            DrainCompletions();
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            ZNet net = ZNet.instance;
            if (rpc == null || net == null)
            {
                if (sessionRpc != null) ResetSession();
                return;
            }
            if (!System.Object.ReferenceEquals(registeredRpc, rpc))
            {
                rpc.Register<ZPackage>(RadioProtocol.RpcName, OnMessage);
                registeredRpc = rpc;
            }
            long server = net.IsServer() ? ZNet.GetUID() : (net.GetServerPeer() == null ? 0 : net.GetServerPeer().m_uid);
            if (server == 0) return;
            if (net.GetWorld() == null) return;
            long world = net.GetWorldUID();
            if (!System.Object.ReferenceEquals(sessionRpc, rpc) || sessionWorld != world || sessionServer != server)
            {
                ResetSession(); sessionRpc = rpc; sessionWorld = world; sessionServer = server;
                sessionStarted = Realtime;
                if (IsHost) { needsScan = true; RefreshLibrary(); }
                else libraryStatus = "Connecting Steam music channel...";
            }
            bulk.Poll(OnBulkMessage);
            double realtime = Realtime;
            if (IsHost)
            {
                if (needsScan && scanning == 0) RefreshLibrary();
                CorrectClock(realtime);
                ExpirePortables(realtime);
                FlushLibraries();
                ServeChunks(realtime);
                if (realtime >= nextState) { nextState = realtime + 1; AdvanceAndBroadcast(); }
            }
            else
            {
                if (!receivedLibrary && realtime - sessionStarted > 30)
                    libraryStatus = "Waiting for host playlist; music connection recovery is in progress.";
                // A reliable bulk send can still be lost when its connection dies.
                // Periodically resync metadata after recovery, not just on first join.
                if (realtime >= nextHello) { nextHello = realtime + (receivedLibrary ? 30 : 5); Send(server, new RadioMessage { Kind = RadioMessageKind.Hello }); }
                ProcessDownload(realtime);
            }
            if (realtime >= nextMaintenance) { nextMaintenance = realtime + 5; Maintain(realtime); }
        }

        public void ResetSession()
        {
            generation++;
            bulk.Reset(); pendingLibraries.Clear(); libraryRetryAt.Clear();
            if (active != null) { active.Dispose(); active = null; }
            tracks.Clear(); sources.Clear(); states.Clear(); watched.Clear(); subscribers.Clear(); chunkRequests.Clear(); chunkOrder.Clear();
            scheduledUploads.Clear();
            portables.Clear(); localPortable = new ZDOID(); localPortableToken = ""; nextPortablePresence = 0;
            greetings.Clear(); peerBudgets.Clear(); downloads.Clear(); waiting.Clear(); wanted.Clear(); prefetched.Clear(); statuses.Clear(); retryAfter.Clear();
            checkingCache = false; receivedLibrary = false; nextHello = 0; nextState = 0; nextMaintenance = 0;
            sessionRpc = null; sessionWorld = 0; sessionServer = 0; libraryStatus = ""; clockSet = false; needsScan = false;
        }

        public void Dispose() { if (disposed) return; disposed = true; ResetSession(); bulk.Dispose(); lock (completions) completions.Clear(); }

        public void RefreshLibrary()
        {
            if (!IsHost || Interlocked.CompareExchange(ref scanning, 1, 0) != 0) return;
            needsScan = false;
            int expected = generation;
            libraryStatus = "Scanning MP3 files...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<LibraryEntry> result = null; string error = null;
                var messages = new List<string>();
                try { result = library.Scan(delegate(string message) { if (messages.Count < 32) messages.Add(message); }, delegate { return disposed || expected != generation; }); }
                catch (Exception exception) { error = exception.Message; }
                Interlocked.Exchange(ref scanning, 0);
                Post(delegate
                {
                    if (expected != generation || !IsHost) return;
                    foreach (string message in messages) log(message);
                    if (error != null) { libraryStatus = "MP3 scan failed: " + error; log(libraryStatus); return; }
                    tracks.Clear(); sources.Clear();
                    foreach (LibraryEntry entry in result) { sources.Add(entry.Track.Id, entry); tracks.Add(entry.Track); }
                    libraryStatus = tracks.Count + " MP3 tracks";
                    foreach (RadioSnapshot state in states.Values)
                        if (state.TrackId.Length > 0 && !sources.ContainsKey(state.TrackId)) { state.TrackId = ""; state.Playing = false; state.Offset = 0; state.Revision++; }
                    BroadcastLibrary();
                });
            });
        }

        public void SetDuration(string id, float seconds)
        {
            if (!IsHost || !RadioProtocol.Finite(seconds) || seconds <= 0 || seconds > 86400) return;
            TrackInfo track = FindTrack(id);
            if (track != null && Math.Abs(track.Duration - seconds) > 0.1) { track.Duration = seconds; BroadcastLibrary(); }
        }

        public void Watch(ZDOID id)
        {
            if (disposed || sessionRpc == null || id.IsNone()) return;
            double now = Realtime;
            double last;
            if (watched.TryGetValue(id, out last) && now - last < 1.5) return;
            if (!watched.ContainsKey(id) && watched.Count >= 64) return;
            watched[id] = now;
            var message = Key(RadioMessageKind.Watch, id);
            if (IsHost) HandleHost(sessionServer, message); else Send(sessionServer, message);
        }

        // The caller checks its exact inventory item continuously. This heartbeat
        // lets the host stop a portable source even if its owner disconnects abruptly.
        public void SetPortable(ZDOID characterId, string token)
        {
            if (disposed || sessionRpc == null || characterId.IsNone()) return;
            token = token ?? "";
            if (token.Length != 0 && !RadioProtocol.ValidPortableToken(token)) return;
            double now = Realtime;
            if (localPortable.Equals(characterId) && localPortableToken == token && now < nextPortablePresence) return;
            if (!localPortable.IsNone() && !localPortable.Equals(characterId) && localPortableToken.Length != 0)
            {
                var release = Key(RadioMessageKind.PortablePresence, localPortable);
                if (IsHost) HandleHost(sessionServer, release); else Send(sessionServer, release);
            }
            localPortable = characterId; localPortableToken = token; nextPortablePresence = now + 2;
            var message = Key(RadioMessageKind.PortablePresence, characterId); message.Id = token;
            if (IsHost) HandleHost(sessionServer, message); else Send(sessionServer, message);
        }

        public RadioSnapshot GetState(ZDOID id)
        {
            RadioSnapshot state;
            return states.TryGetValue(id, out state) ? state.Copy() : null;
        }

        public void Command(ZDOID id, string command, string trackId, float value)
        {
            if (disposed || sessionRpc == null || !RadioProtocol.ValidCommand(command) || !RadioProtocol.Finite(value) || value < 0 || value > 1) return;
            if (!String.IsNullOrEmpty(trackId) && !RadioProtocol.ValidId(trackId)) return;
            var message = Key(RadioMessageKind.Command, id); message.Command = command; message.Id = trackId ?? ""; message.Value = value;
            if (IsHost) HandleHost(sessionServer, message); else Send(sessionServer, message);
        }

        public string GetTrackPath(string id)
        {
            if (!RadioProtocol.ValidId(id)) return null;
            if (IsHost)
            {
                LibraryEntry entry;
                if (!sources.TryGetValue(id, out entry)) return null;
                try { var info = new FileInfo(entry.Path); return info.Exists && info.Length == entry.Track.Size && info.LastWriteTimeUtc.Ticks == entry.Modified && (info.Attributes & FileAttributes.ReparsePoint) == 0 ? entry.Path : null; }
                catch (IOException) { return null; }
            }
            try { return library.GetVerified(id); } catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; }
        }

        public void RequestTrack(string id)
        {
            if (disposed || IsHost || !DownloadEnabled || !RadioProtocol.ValidId(id) || FindTrack(id) == null) return;
            double now = Realtime;
            wanted[id] = now;
            QueueDownload(id);
            // Prefetch the predictable next track; shuffle cannot predict until host selects it.
            bool shuffle = false;
            foreach (RadioSnapshot state in states.Values) if (state.Playing && state.TrackId == id && state.Shuffle) shuffle = true;
            if (!shuffle && tracks.Count > 1)
            {
                for (int i = 0; i < tracks.Count; i++) if (tracks[i].Id == id)
                {
                    string next = tracks[(i + 1) % tracks.Count].Id;
                    prefetched[next] = now; QueueDownload(next); break;
                }
            }
        }

        private void QueueDownload(string id)
        {
            if (GetTrackPath(id) != null || waiting.Contains(id)) return;
            double retry;
            if (retryAfter.TryGetValue(id, out retry) && Realtime < retry) return;
            if (waiting.Count >= 16) return;
            waiting.Add(id); downloads.Add(id); statuses[id] = "Queued";
        }

        public string Status(string id)
        {
            if (GetTrackPath(id) != null) return "Ready";
            string value;
            return id != null && statuses.TryGetValue(id, out value) ? value : "Waiting for music";
        }

        private void Post(Action action) { lock (completions) { if (!disposed) completions.Enqueue(action); } }
        private void DrainCompletions()
        {
            for (int i = 0; i < 16; i++)
            {
                Action action;
                lock (completions) { if (completions.Count == 0) break; action = completions.Dequeue(); }
                try { action(); } catch (Exception exception) { LogError(exception); }
            }
        }

        private TrackInfo FindTrack(string id) { foreach (TrackInfo track in tracks) if (track.Id == id) return track; return null; }
        private static RadioMessage Key(RadioMessageKind kind, ZDOID id) { return new RadioMessage { Kind = kind, Owner = id.UserID, Object = id.ID }; }

        private static bool UsesBulk(RadioMessageKind kind)
        {
            return kind == RadioMessageKind.Library || kind == RadioMessageKind.Chunk
                || kind == RadioMessageKind.ChunkRequest || kind == RadioMessageKind.Error;
        }

        private bool Send(long peer, RadioMessage message)
        {
            if (disposed || sessionRpc == null || peer == 0) return false;
            try
            {
                byte[] data = RadioProtocol.Encode(message);
                if (UsesBulk(message.Kind)) return bulk.TrySend(peer, data, maxQueuedKiB * 1024);
                if (data.Length > 1024) throw new InvalidDataException("Oversized radio control packet");
                sessionRpc.InvokeRoutedRPC(peer, RadioProtocol.RpcName, new ZPackage(data)); return true;
            }
            catch (Exception exception) { LogError(exception); }
            return false;
        }

        private void OnMessage(long sender, ZPackage package)
        {
            if (package == null || package.Size() > 1024) return;
            ReceiveMessage(sender, package.GetArray(), false);
        }

        private void OnBulkMessage(long sender, byte[] data) { ReceiveMessage(sender, data, true); }

        private void ReceiveMessage(long sender, byte[] data, bool isBulk)
        {
            if (disposed || sessionRpc == null || data == null || data.Length < 2 || data.Length > RadioProtocol.MaxPacket
                || UsesBulk((RadioMessageKind)data[1]) != isBulk) return;
            try
            {
                if (IsHost)
                {
                    ZNetPeer peer = ZNet.instance.GetPeer(sender);
                    if (peer == null || !peer.IsReady()) return;
                    RadioTokenBucket budget;
                    if (!peerBudgets.TryGetValue(sender, out budget)) { if (peerBudgets.Count >= 64) return; peerBudgets.Add(sender, budget = new RadioTokenBucket(80, 100)); }
                    if (!budget.Take(1, Realtime)) return;
                    HandleHost(sender, RadioProtocol.Decode(data));
                }
                else if (sender == sessionServer && ZNet.instance.GetServerPeer() != null && ZNet.instance.GetServerPeer().m_uid == sender)
                    HandleClient(RadioProtocol.Decode(data));
            }
            catch (Exception exception) { LogError(exception); }
        }

        private bool IsOwnerCharacter(long sender, ZDOID id)
        {
            if (ZNet.instance == null || ZDOMan.instance == null || ZDOMan.instance.GetZDO(id) == null) return false;
            if (sender == sessionServer)
            {
                Player player = Player.m_localPlayer;
                ZNetView view = player == null || player.IsDead() ? null : player.GetComponent<ZNetView>();
                ZDO character = view == null || !view.IsValid() ? null : view.GetZDO();
                return character != null && character.m_uid.Equals(id);
            }
            ZNetPeer peer = ZNet.instance.GetPeer(sender);
            return peer != null && peer.IsReady() && !peer.m_characterID.IsNone() && peer.m_characterID.Equals(id);
        }

        private bool ValidateRadio(long sender, ZDOID id, float distance, bool command = false)
        {
            if (ZDOMan.instance == null || ZNet.instance == null) return false;
            ZDO radio = ZDOMan.instance.GetZDO(id);
            if (radio == null) return false;
            PortableLease portable;
            bool isPortable = portables.TryGetValue(id, out portable);
            if (isPortable)
            {
                if (portable.Expires <= Realtime || !IsOwnerCharacter(portable.Peer, id)
                    || (command && sender != portable.Peer)) return false;
            }
            else if (radio.GetPrefab() != PrefabHash) return false;
            Vector3 position;
            if (sender == sessionServer)
            {
                if (Player.m_localPlayer == null || Player.m_localPlayer.IsDead()) return false;
                position = Player.m_localPlayer.transform.position;
            }
            else
            {
                ZNetPeer peer = ZNet.instance.GetPeer(sender);
                if (peer == null || !peer.IsReady() || peer.m_characterID.IsNone()) return false;
                ZDO character = ZDOMan.instance.GetZDO(peer.m_characterID);
                if (character == null) return false;
                position = character.GetPosition();
            }
            return (radio.GetPosition() - position).sqrMagnitude <= distance * distance;
        }

        private void HandlePortable(long sender, ZDOID id, string token)
        {
            if ((token.Length != 0 && !RadioProtocol.ValidPortableToken(token)) || !IsOwnerCharacter(sender, id)) return;
            PortableLease previous;
            bool exists = portables.TryGetValue(id, out previous);
            if (token.Length == 0) { if (exists && previous.Peer == sender) StopPortable(id); return; }
            if (exists && (previous.Peer != sender || previous.Token != token || previous.Expires <= Realtime))
            {
                StopPortable(id); exists = false;
            }
            if (!exists)
            {
                var stale = new List<ZDOID>();
                foreach (var pair in portables) if (pair.Value.Peer == sender) stale.Add(pair.Key);
                foreach (ZDOID other in stale) StopPortable(other);
                if (portables.Count >= 64) return;
                portables.Add(id, previous = new PortableLease { Peer = sender, Token = token });
            }
            previous.Expires = Realtime + 6;
        }

        private void StopPortable(ZDOID id)
        {
            portables.Remove(id);
            RadioSnapshot state;
            if (!states.TryGetValue(id, out state)) return;
            state.Offset = (float)Math.Min(86400, state.Position(Now)); state.Playing = false;
            state.Revision = state.Revision == Int32.MaxValue ? 0 : state.Revision + 1;
            // A stopped source no longer passes ValidateRadio. Existing listeners
            // still need its final state, including when its character was removed.
            foreach (var peer in subscribers)
            {
                double last; ZNetPeer connected = ZNet.instance == null ? null : ZNet.instance.GetPeer(peer.Key);
                if (connected != null && connected.IsReady() && peer.Value.TryGetValue(id, out last) && Realtime - last < 12)
                    SendState(peer.Key, id, state);
            }
        }

        private void ExpirePortables(double realtime)
        {
            if (portables.Count == 0) return;
            var expired = new List<ZDOID>();
            foreach (var pair in portables)
                if (pair.Value.Expires <= realtime || !IsOwnerCharacter(pair.Value.Peer, pair.Key)) expired.Add(pair.Key);
            foreach (ZDOID id in expired) StopPortable(id);
        }

        private void HandleHost(long sender, RadioMessage message)
        {
            double realtime = Realtime;
            if (message.Kind == RadioMessageKind.PortablePresence)
            {
                HandlePortable(sender, new ZDOID(message.Owner, message.Object), message.Id); return;
            }
            if (message.Kind == RadioMessageKind.Hello)
            {
                double last;
                if (greetings.TryGetValue(sender, out last) && realtime - last < 2) return;
                greetings[sender] = realtime; SendLibrary(sender); return;
            }
            if (message.Kind == RadioMessageKind.ChunkRequest)
            {
                LibraryEntry entry;
                if (!sources.TryGetValue(message.Id, out entry) || message.Offset >= entry.Track.Size || !HasNearbyWatch(sender)) return;
                UploadQueue pending;
                if (!chunkRequests.TryGetValue(sender, out pending))
                {
                    if (chunkRequests.Count >= 64) return;
                    pending = new UploadQueue { Id = message.Id }; chunkRequests.Add(sender, pending);
                    if (scheduledUploads.Add(sender)) chunkOrder.Enqueue(sender);
                }
                else if (pending.Id != message.Id) chunkRequests[sender] = pending = new UploadQueue { Id = message.Id };
                if (pending.Offsets.Count >= 8 || !pending.Offsets.Add(message.Offset)) return;
                pending.Requests.Enqueue(message); return;
            }
            if (message.Kind != RadioMessageKind.Watch && message.Kind != RadioMessageKind.Command) return;
            ZDOID id = new ZDOID(message.Owner, message.Object);
            if (!ValidateRadio(sender, id, message.Kind == RadioMessageKind.Watch ? RadioProtocol.WatchDistance : 8,
                message.Kind == RadioMessageKind.Command)) return;
            RadioSnapshot state;
            if (!states.TryGetValue(id, out state))
            {
                if (states.Count >= 256) return;
                states.Add(id, state = new RadioSnapshot());
            }
            if (message.Kind == RadioMessageKind.Watch)
            {
                if (sender != sessionServer)
                {
                    Dictionary<ZDOID, double> subscription;
                    if (!subscribers.TryGetValue(sender, out subscription))
                    {
                        if (subscribers.Count >= 64) return;
                        subscribers.Add(sender, subscription = new Dictionary<ZDOID, double>());
                    }
                    if (subscription.Count >= 64 && !subscription.ContainsKey(id)) return;
                    subscription[id] = realtime; SendState(sender, id, state);
                }
            }
            else if (RadioProtocol.Apply(state, tracks, message.Command, message.Id, message.Value, Now, random)) BroadcastState(id, state);
        }

        private bool HasNearbyWatch(long peer)
        {
            Dictionary<ZDOID, double> subscription;
            if (!subscribers.TryGetValue(peer, out subscription)) return false;
            foreach (var pair in subscription) if (Realtime - pair.Value <= 12 && ValidateRadio(peer, pair.Key, RadioProtocol.WatchDistance)) return true;
            return false;
        }

        private void HandleClient(RadioMessage message)
        {
            if (message.Kind == RadioMessageKind.Library)
            {
                tracks.Clear();
                foreach (TrackInfo track in message.Tracks) { track.Title = RadioLibrary.SafeTitle(track.Title); tracks.Add(track); }
                receivedLibrary = true; libraryStatus = tracks.Count + " MP3 tracks";
                if (active != null && FindTrack(active.Track.Id) == null) FailDownload("Track removed by host");
            }
            else if (message.Kind == RadioMessageKind.State)
            {
                ZDOID id = new ZDOID(message.Owner, message.Object);
                RadioSnapshot previous;
                if (watched.ContainsKey(id) && (!states.TryGetValue(id, out previous) || message.State.Revision >= previous.Revision || (previous.Revision == Int32.MaxValue && message.State.Revision == 0)))
                {
                    states[id] = message.State;
                    if (message.State.TrackId.Length > 0 && FindTrack(message.State.TrackId) == null)
                    { receivedLibrary = false; nextHello = Math.Min(nextHello, Realtime + 1); }
                    // Begin fetching while approaching a subscribed playing source, before its 150 m audio boundary.
                    if (message.State.Playing) RequestTrack(message.State.TrackId);
                }
            }
            else if (message.Kind == RadioMessageKind.Chunk) ReceiveChunk(message);
            else if (message.Kind == RadioMessageKind.Error && active != null && message.Id == active.Track.Id) FailDownload(message.Command);
        }

        private void SendLibrary(long peer) { if (pendingLibraries.Count < 64) pendingLibraries.Add(peer); }
        private void FlushLibraries()
        {
            int attempts = 0;
            foreach (long uid in new List<long>(pendingLibraries))
            {
                ZNetPeer peer = ZNet.instance.GetPeer(uid);
                if (peer == null || !peer.IsReady()) { pendingLibraries.Remove(uid); libraryRetryAt.Remove(uid); continue; }
                double retry;
                if (libraryRetryAt.TryGetValue(uid, out retry) && Realtime < retry) continue;
                libraryRetryAt[uid] = Realtime + 1;
                if (Send(uid, new RadioMessage { Kind = RadioMessageKind.Library, Tracks = new List<TrackInfo>(tracks) }))
                { pendingLibraries.Remove(uid); libraryRetryAt.Remove(uid); }
                // Bound failed attempts too: an unavailable peer must not cause
                // a large playlist to be re-encoded every frame or starve peers.
                if (++attempts >= 2) break;
            }
        }
        private void BroadcastLibrary()
        {
            if (ZNet.instance == null) return;
            foreach (ZNetPeer peer in ZNet.instance.GetConnectedPeers()) if (peer.IsReady()) SendLibrary(peer.m_uid);
        }
        private void SendState(long peer, ZDOID id, RadioSnapshot state) { var message = Key(RadioMessageKind.State, id); message.State = state; Send(peer, message); }
        private void BroadcastState(ZDOID id, RadioSnapshot state)
        {
            foreach (var peer in subscribers)
            {
                double last;
                if (peer.Value.TryGetValue(id, out last) && Realtime - last < 12 && ValidateRadio(peer.Key, id, RadioProtocol.WatchDistance)) SendState(peer.Key, id, state);
            }
        }

        private void AdvanceAndBroadcast()
        {
            double now = Now;
            foreach (var pair in states)
            {
                RadioSnapshot state = pair.Value;
                TrackInfo track = FindTrack(state.TrackId);
                if (state.Playing && track != null && track.Duration > 0 && state.Position(now) >= track.Duration)
                {
                    bool last = tracks.Count > 0 && tracks[tracks.Count - 1].Id == state.TrackId;
                    if (!state.Repeat && !state.Shuffle && last) { state.Offset = 0; state.Playing = false; state.Revision++; }
                    else RadioProtocol.Apply(state, tracks, "next", "", 0, now, random);
                }
                BroadcastState(pair.Key, state);
            }
        }

        private void ServeChunks(double realtime)
        {
            int visits = chunkOrder.Count, sent = 0;
            for (int i = 0; i < visits && sent < 3 && chunkOrder.Count > 0; i++)
            {
                long peer = chunkOrder.Dequeue();
                scheduledUploads.Remove(peer);
                UploadQueue pending;
                if (!chunkRequests.TryGetValue(peer, out pending)) continue;
                if (!pending.Reading && pending.Ready == null && pending.Requests.Count == 0)
                { chunkRequests.Remove(peer); continue; }
                if (!HasNearbyWatch(peer)) { chunkRequests.Remove(peer); continue; }
                if (scheduledUploads.Add(peer)) chunkOrder.Enqueue(peer);
                if (pending.Ready != null && sent < 3)
                {
                    RadioMessage reply = pending.Ready;
                    // End this rotation when the global budget is spent, so the same first peers cannot starve others.
                    int cost = (reply.Data == null ? 0 : reply.Data.Length) + 256;
                    if (!upload.Take(cost, realtime)) break;
                    if (!Send(peer, reply)) { upload.Refund(cost); continue; }
                    pending.Ready = null; pending.Offsets.Remove(reply.Offset);
                    if (reply.Kind == RadioMessageKind.Error) { pending.Requests.Clear(); pending.Offsets.Clear(); }
                    sent++;
                }
                if (pending.Reading || pending.Ready != null || pending.Requests.Count == 0 || uploadWorkers >= 8) continue;
                RadioMessage request = pending.Requests.Dequeue();
                LibraryEntry entry;
                if (!sources.TryGetValue(request.Id, out entry)) { pending.Offsets.Remove(request.Offset); continue; }
                pending.Reading = true;
                Interlocked.Increment(ref uploadWorkers);
                int expectedGeneration = generation;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    byte[] bytes = null; Exception failure = null;
                    try { bytes = RadioTransfer.ReadChunk(entry, request.Offset); }
                    catch (Exception error) { failure = error; }
                    finally { Interlocked.Decrement(ref uploadWorkers); }
                    Post(delegate
                    {
                        UploadQueue current;
                        if (expectedGeneration != generation || !chunkRequests.TryGetValue(peer, out current) || !ReferenceEquals(current, pending)) return;
                        pending.Reading = false;
                        pending.Ready = failure == null
                            ? new RadioMessage { Kind = RadioMessageKind.Chunk, Id = request.Id, Offset = request.Offset, Data = bytes }
                            : new RadioMessage { Kind = RadioMessageKind.Error, Id = request.Id, Offset = request.Offset, Command = "Host MP3 unavailable; refresh the playlist" };
                        if (failure != null) LogError(failure);
                    });
                });
            }
        }

        private void ProcessDownload(double realtime)
        {
            // Drop superseded songs and prioritize a newly selected current song over prefetch.
            for (int i = downloads.Count - 1; i >= 0; i--)
                if (!Wanted(downloads[i], realtime, false) && !Wanted(downloads[i], realtime, true)) { waiting.Remove(downloads[i]); downloads.RemoveAt(i); }
            if (active != null)
            {
                bool urgent = false;
                foreach (string pending in downloads) if (Wanted(pending, realtime, false)) urgent = true;
                if ((!Wanted(active.Track.Id, realtime, false) && !Wanted(active.Track.Id, realtime, true)) || (!Wanted(active.Track.Id, realtime, false) && urgent))
                {
                    string obsolete = active.Track.Id; active.Dispose(); active = null; waiting.Remove(obsolete); statuses[obsolete] = "Queued";
                }
            }
            if (active != null)
            {
                if (realtime - active.LastProgress > 10 && !active.Writing)
                {
                    if (++active.Retries > 3) FailDownload("Steam music channel unavailable (move closer or reconnect)");
                    else
                    {
                        active.LastProgress = realtime;
                        foreach (long offset in new List<long>(active.Requested.Keys))
                        {
                            active.Requested[offset] = realtime;
                            Send(sessionServer, new RadioMessage { Kind = RadioMessageKind.ChunkRequest, Id = active.Track.Id, Offset = offset });
                        }
                    }
                }
                if (active != null) { WriteReceived(); FillWindow(); }
                return;
            }
            if (checkingCache || downloads.Count == 0) return;
            int selected = 0;
            for (int i = 0; i < downloads.Count; i++) if (Wanted(downloads[i], realtime, false)) { selected = i; break; }
            string id = downloads[selected]; downloads.RemoveAt(selected);
            TrackInfo track = FindTrack(id);
            if (track == null) { waiting.Remove(id); return; }
            checkingCache = true; statuses[id] = "Checking cache";
            int expected = generation;
            var protectedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (RadioSnapshot state in states.Values) if (!String.IsNullOrEmpty(state.TrackId)) protectedIds.Add(state.TrackId);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string cached = null; string error = null;
                try { cached = library.ValidateCache(track) ?? library.FindLocalTrack(track, delegate { return disposed || expected != generation; }); }
                catch (Exception exception) { error = exception.Message; }
                Post(delegate
                {
                    if (expected != generation) return;
                    checkingCache = false;
                    if (cached != null) { waiting.Remove(id); statuses[id] = "Ready"; return; }
                    if (error != null) log("Music cache: " + error);
                    try
                    {
                        if (FindTrack(id) == null || (!Wanted(id, Realtime, false) && !Wanted(id, Realtime, true))) { waiting.Remove(id); return; }
                        active = new Download { Track = track, Writer = new RadioDownloadWriter(library, track, protectedIds), StartedAt = Realtime, LastProgress = Realtime };
                        FillWindow();
                    }
                    catch (Exception exception) { waiting.Remove(id); statuses[id] = "Download failed: " + exception.Message; retryAfter[id] = Realtime + 30; LogError(exception); }
                });
            });
        }

        private bool Wanted(string id, double now, bool prefetch)
        {
            double last;
            return (prefetch ? prefetched : wanted).TryGetValue(id, out last) && now - last < 3;
        }

        private void CorrectClock(double realtime)
        {
            double now = Now;
            if (!clockSet) { clockGame = now; clockReal = realtime; clockSet = true; return; }
            double elapsed = realtime - clockReal;
            double shift = (now - clockGame) - elapsed;
            if (Math.Abs(shift) > 0.5)
            {
                // Sleeping advances world time much faster than audio time. Keep the
                // song position continuous, then publish its rebased world timestamp.
                foreach (var pair in states) if (pair.Value.Playing)
                {
                    pair.Value.StartedAt += shift; pair.Value.Revision++;
                    BroadcastState(pair.Key, pair.Value);
                }
                clockGame = now; clockReal = realtime;
            }
            else if (elapsed >= 1) { clockGame = now; clockReal = realtime; }
        }

        private void FillWindow()
        {
            if (active == null) return;
            while (active.NextRequest < active.Track.Size && active.NextRequest - active.Offset < (long)downloadWindow * RadioProtocol.ChunkSize)
            {
                long offset = active.NextRequest;
                if (!Send(sessionServer, new RadioMessage { Kind = RadioMessageKind.ChunkRequest, Id = active.Track.Id, Offset = offset })) break;
                active.NextRequest += RadioProtocol.ChunkSize;
                active.Requested.Add(offset, Realtime);
            }
            double rate = active.Offset / Math.Max(0.5, Realtime - active.StartedAt) / 1024;
            statuses[active.Track.Id] = "Downloading " + (active.Offset * 100 / active.Track.Size) + "% (" + ((int)rate) + " KiB/s)";
        }

        private void ReceiveChunk(RadioMessage message)
        {
            if (active == null || message.Id != active.Track.Id || !active.Requested.ContainsKey(message.Offset)) return;
            try
            {
                int expected = (int)Math.Min(RadioProtocol.ChunkSize, active.Track.Size - message.Offset);
                if (message.Data.Length != expected) throw new InvalidDataException("Unexpected MP3 chunk length");
                active.Requested.Remove(message.Offset); active.Received.Add(message.Offset, message.Data);
                active.LastProgress = Realtime; active.Retries = 0;
                WriteReceived();
            }
            catch (Exception exception) { FailDownload(exception.Message); LogError(exception); }
        }

        private void WriteReceived()
        {
            if (active == null || active.Writing || !active.Received.ContainsKey(active.Offset)) return;
            Download target = active;
            var blocks = new List<byte[]>(); long end = target.Offset; byte[] block;
            while (target.Received.TryGetValue(end, out block))
            { target.Received.Remove(end); blocks.Add(block); end += block.Length; }
            target.Writing = true;
            int expectedGeneration = generation;
            try
            {
                target.Writer.Write(target.Offset, blocks.ToArray(), delegate(long offset, bool finished, Exception error)
                {
                    Post(delegate
                    {
                        if (expectedGeneration != generation || !ReferenceEquals(active, target)) return;
                        target.Writing = false;
                        if (error != null) { FailDownload(error.Message); LogError(error); return; }
                        target.Offset = offset; target.LastProgress = Realtime;
                        if (finished)
                        {
                            string id = target.Track.Id; target.Dispose(); active = null;
                            waiting.Remove(id); retryAfter.Remove(id); statuses[id] = "Ready";
                        }
                        else { WriteReceived(); FillWindow(); }
                    });
                });
            }
            catch (Exception error) { FailDownload(error.Message); LogError(error); }
        }

        private void FailDownload(string reason)
        {
            if (active == null) return;
            string id = active.Track.Id; active.Dispose(); active = null;
            waiting.Remove(id); statuses[id] = "Download failed: " + reason; retryAfter[id] = Realtime + 30;
        }

        private void Maintain(double realtime)
        {
            var stale = new List<ZDOID>();
            foreach (var pair in watched) if (realtime - pair.Value > 12) stale.Add(pair.Key);
            foreach (ZDOID id in stale) { watched.Remove(id); if (!IsHost) states.Remove(id); }
            if (IsHost)
            {
                var gone = new List<long>();
                foreach (var peer in subscribers)
                {
                    if (ZNet.instance.GetPeer(peer.Key) == null) { gone.Add(peer.Key); continue; }
                    stale.Clear();
                    foreach (var pair in peer.Value) if (realtime - pair.Value > 12 || !ValidateRadio(peer.Key, pair.Key, RadioProtocol.WatchDistance)) stale.Add(pair.Key);
                    foreach (ZDOID id in stale) peer.Value.Remove(id);
                    if (peer.Value.Count == 0) gone.Add(peer.Key);
                }
                foreach (long peer in gone) { subscribers.Remove(peer); chunkRequests.Remove(peer); greetings.Remove(peer); peerBudgets.Remove(peer); }
                gone.Clear();
                foreach (long peer in peerBudgets.Keys) if (ZNet.instance.GetPeer(peer) == null) gone.Add(peer);
                foreach (long peer in gone) peerBudgets.Remove(peer);
                gone.Clear();
                foreach (long peer in greetings.Keys) if (ZNet.instance.GetPeer(peer) == null) gone.Add(peer);
                foreach (long peer in gone) greetings.Remove(peer);
                stale.Clear();
                foreach (var pair in states) if (ZDOMan.instance == null || ZDOMan.instance.GetZDO(pair.Key) == null) stale.Add(pair.Key);
                foreach (ZDOID id in stale) states.Remove(id);
            }
        }

        private void LogError(Exception exception)
        {
            double now = Realtime;
            if (now < nextErrorLog) return;
            nextErrorLog = now + 5;
            log("NordicRadio network: " + exception.GetType().Name + ": " + exception.Message);
        }
    }
}
