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
        private readonly Queue<Action> completions = new Queue<Action>();
        private readonly List<TrackInfo> tracks = new List<TrackInfo>();
        private readonly Dictionary<string, LibraryEntry> sources = new Dictionary<string, LibraryEntry>(StringComparer.Ordinal);
        private readonly Dictionary<ZDOID, RadioSnapshot> states = new Dictionary<ZDOID, RadioSnapshot>();
        private readonly Dictionary<ZDOID, PortableLease> portables = new Dictionary<ZDOID, PortableLease>();
        private readonly Dictionary<ZDOID, double> watched = new Dictionary<ZDOID, double>();
        private readonly Dictionary<long, Dictionary<ZDOID, double>> subscribers = new Dictionary<long, Dictionary<ZDOID, double>>();
        private readonly Dictionary<long, RadioMessage> chunkRequests = new Dictionary<long, RadioMessage>();
        private readonly Queue<long> chunkOrder = new Queue<long>();
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
        private volatile bool disposed;
        private bool receivedLibrary;
        private double nextHello;
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

        private sealed class Download : IDisposable
        {
            public TrackInfo Track;
            public string Temporary;
            public FileStream File;
            public SHA256 Hash;
            public long Offset;
            public double SentAt;
            public int Retries;
            public void Dispose()
            {
                // A full disk can make flushing Dispose throw as well as Write.
                // Always clear ownership and still remove the incomplete download.
                if (File != null)
                {
                    FileStream file = File; File = null;
                    try { file.Dispose(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
                if (Hash != null) { Hash.Dispose(); Hash = null; }
                if (Temporary != null) { try { if (System.IO.File.Exists(Temporary)) System.IO.File.Delete(Temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } Temporary = null; }
            }
        }

        public RadioService(BaseUnityPlugin host, string dataRoot, Action<string> log)
        {
            this.log = log ?? delegate { };
            library = new RadioLibrary(dataRoot);
        }

        public bool IsHost { get { return !disposed && ZNet.instance != null && ZNet.instance.IsServer(); } }
        public int UploadKiBPerSecond
        {
            get { return (int)(upload.Rate / 1024); }
            set { upload.Rate = Math.Max(64, Math.Min(4096, value)) * 1024; }
        }
        public string LibraryStatus { get { return libraryStatus; } }
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
                if (IsHost) { needsScan = true; RefreshLibrary(); }
            }
            double realtime = Realtime;
            if (IsHost)
            {
                if (needsScan && scanning == 0) RefreshLibrary();
                CorrectClock(realtime);
                ExpirePortables(realtime);
                ServeChunks(realtime);
                if (realtime >= nextState) { nextState = realtime + 1; AdvanceAndBroadcast(); }
            }
            else
            {
                if (!receivedLibrary && realtime >= nextHello) { nextHello = realtime + 5; Send(server, new RadioMessage { Kind = RadioMessageKind.Hello }); }
                ProcessDownload(realtime);
            }
            if (realtime >= nextMaintenance) { nextMaintenance = realtime + 5; Maintain(realtime); }
        }

        public void ResetSession()
        {
            generation++;
            if (active != null) { active.Dispose(); active = null; }
            tracks.Clear(); sources.Clear(); states.Clear(); watched.Clear(); subscribers.Clear(); chunkRequests.Clear(); chunkOrder.Clear();
            portables.Clear(); localPortable = new ZDOID(); localPortableToken = ""; nextPortablePresence = 0;
            greetings.Clear(); peerBudgets.Clear(); downloads.Clear(); waiting.Clear(); wanted.Clear(); prefetched.Clear(); statuses.Clear(); retryAfter.Clear();
            checkingCache = false; receivedLibrary = false; nextHello = 0; nextState = 0; nextMaintenance = 0;
            sessionRpc = null; sessionWorld = 0; sessionServer = 0; libraryStatus = ""; clockSet = false; needsScan = false;
        }

        public void Dispose() { if (disposed) return; disposed = true; ResetSession(); lock (completions) completions.Clear(); }

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
            if (disposed || IsHost || !RadioProtocol.ValidId(id) || FindTrack(id) == null) return;
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

        private void Send(long peer, RadioMessage message)
        {
            if (disposed || ZRoutedRpc.instance == null || peer == 0) return;
            try { ZRoutedRpc.instance.InvokeRoutedRPC(peer, RadioProtocol.RpcName, new ZPackage(RadioProtocol.Encode(message))); }
            catch (Exception exception) { LogError(exception); }
        }

        private void OnMessage(long sender, ZPackage package)
        {
            if (disposed || sessionRpc == null || package == null || package.Size() > RadioProtocol.MaxPacket) return;
            try
            {
                if (IsHost)
                {
                    ZNetPeer peer = ZNet.instance.GetPeer(sender);
                    if (peer == null || !peer.IsReady()) return;
                    RadioTokenBucket budget;
                    if (!peerBudgets.TryGetValue(sender, out budget)) { if (peerBudgets.Count >= 64) return; peerBudgets.Add(sender, budget = new RadioTokenBucket(80, 100)); }
                    if (!budget.Take(1, Realtime)) return;
                    HandleHost(sender, RadioProtocol.Decode(package.GetArray()));
                }
                else if (sender == sessionServer && ZNet.instance.GetServerPeer() != null && ZNet.instance.GetServerPeer().m_uid == sender)
                    HandleClient(RadioProtocol.Decode(package.GetArray()));
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
                if (!chunkRequests.ContainsKey(sender))
                {
                    if (chunkRequests.Count >= 64) return;
                    chunkOrder.Enqueue(sender);
                }
                chunkRequests[sender] = message; return;
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
                if (watched.ContainsKey(id) && (!states.TryGetValue(id, out previous) || message.State.Revision >= previous.Revision || (previous.Revision == Int32.MaxValue && message.State.Revision == 0))) states[id] = message.State;
            }
            else if (message.Kind == RadioMessageKind.Chunk) ReceiveChunk(message);
            else if (message.Kind == RadioMessageKind.Error && active != null && message.Id == active.Track.Id) FailDownload(message.Command);
        }

        private void SendLibrary(long peer) { Send(peer, new RadioMessage { Kind = RadioMessageKind.Library, Tracks = new List<TrackInfo>(tracks) }); }
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
            for (int i = 0; i < 3 && chunkOrder.Count > 0; i++)
            {
                long peer = chunkOrder.Peek();
                RadioMessage request;
                if (!chunkRequests.TryGetValue(peer, out request)) { chunkOrder.Dequeue(); continue; }
                if (!upload.Take(RadioProtocol.ChunkSize + 128, realtime)) break;
                chunkOrder.Dequeue(); chunkRequests.Remove(peer);
                if (!HasNearbyWatch(peer)) continue;
                LibraryEntry entry;
                if (!sources.TryGetValue(request.Id, out entry)) continue;
                try
                {
                    string path = GetTrackPath(request.Id);
                    if (path == null) throw new IOException("Host file changed; refresh the playlist");
                    int length = (int)Math.Min(RadioProtocol.ChunkSize, entry.Track.Size - request.Offset);
                    byte[] data = new byte[length];
                    using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        file.Position = request.Offset;
                        int read = 0;
                        while (read < length) { int part = file.Read(data, read, length - read); if (part == 0) throw new EndOfStreamException(); read += part; }
                    }
                    Send(peer, new RadioMessage { Kind = RadioMessageKind.Chunk, Id = request.Id, Offset = request.Offset, Data = data });
                }
                catch (Exception exception) { Send(peer, new RadioMessage { Kind = RadioMessageKind.Error, Id = request.Id, Command = "Host MP3 unavailable; refresh the playlist" }); LogError(exception); }
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
                if (realtime - active.SentAt > 10)
                {
                    if (++active.Retries > 3) FailDownload("Host did not send music (move closer and retry)");
                    else AskChunk();
                }
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
            ThreadPool.QueueUserWorkItem(delegate
            {
                string cached = null; string error = null;
                try { cached = library.ValidateCache(track); } catch (Exception exception) { error = exception.Message; }
                Post(delegate
                {
                    if (expected != generation) return;
                    checkingCache = false;
                    if (cached != null) { waiting.Remove(id); statuses[id] = "Ready"; return; }
                    if (error != null) log("Music cache: " + error);
                    try
                    {
                        if (FindTrack(id) == null || (!Wanted(id, Realtime, false) && !Wanted(id, Realtime, true))) { waiting.Remove(id); return; }
                        var protectedIds = new HashSet<string>(StringComparer.Ordinal);
                        foreach (RadioSnapshot state in states.Values) if (!String.IsNullOrEmpty(state.TrackId)) protectedIds.Add(state.TrackId);
                        string temporary = library.BeginDownload(track, protectedIds);
                        var download = new Download { Track = track, Temporary = temporary };
                        try { download.File = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536); download.Hash = SHA256.Create(); }
                        catch { download.Dispose(); throw; }
                        active = download; AskChunk();
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

        private void AskChunk()
        {
            if (active == null) return;
            active.SentAt = Realtime;
            Send(sessionServer, new RadioMessage { Kind = RadioMessageKind.ChunkRequest, Id = active.Track.Id, Offset = active.Offset });
            statuses[active.Track.Id] = "Downloading " + (active.Offset * 100 / active.Track.Size) + "%";
        }

        private void ReceiveChunk(RadioMessage message)
        {
            if (active == null || message.Id != active.Track.Id || message.Offset != active.Offset) return;
            try
            {
                int expected = (int)Math.Min(RadioProtocol.ChunkSize, active.Track.Size - active.Offset);
                if (message.Data.Length != expected) throw new InvalidDataException("Unexpected MP3 chunk length");
                active.File.Write(message.Data, 0, message.Data.Length);
                active.Hash.TransformBlock(message.Data, 0, message.Data.Length, message.Data, 0);
                active.Offset += message.Data.Length; active.Retries = 0;
                if (active.Offset < active.Track.Size) { AskChunk(); return; }
                active.Hash.TransformFinalBlock(new byte[0], 0, 0);
                if (RadioLibrary.Hex(active.Hash.Hash) != active.Track.Id) throw new InvalidDataException("MP3 checksum mismatch");
                active.File.Flush(); active.File.Dispose(); active.File = null;
                library.CommitDownload(active.Track.Id, active.Temporary); active.Temporary = null;
                string id = active.Track.Id; active.Dispose(); active = null; waiting.Remove(id); retryAfter.Remove(id); statuses[id] = "Ready";
            }
            catch (Exception exception) { FailDownload(exception.Message); LogError(exception); }
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
