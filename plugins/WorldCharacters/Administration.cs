using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;

namespace ValheimModPack.WorldCharacters
{
    public enum AdministrationDecision { Approve, Fresh, Reject }

    public sealed class AdministrationRequest
    {
        public readonly string Id, Name, Owner, Fingerprint, Error;
        public readonly long Character, World;
        public readonly int ItemsCount;
        public readonly bool Approved;
        public bool IsActionable { get { return World != 0 && !Approved && Error.Length == 0 && Fingerprint.Length != 0; } }
        public AdministrationRequest(string id, string name, string owner, long character, long world,
            int itemsCount, string fingerprint, string error, bool approved)
        {
            Id = id ?? ""; Name = name ?? ""; Owner = owner ?? ""; Character = character; World = world;
            ItemsCount = itemsCount; Fingerprint = fingerprint ?? ""; Error = error ?? ""; Approved = approved;
        }
    }
    public sealed class AdministrationItem
    {
        public readonly int Prefab, Count, Quality, X, Y;
        public readonly bool Equipment;
        public readonly string BackpackPath, Details;
        public AdministrationItem(int prefab, int count, int quality, int x, int y, bool equipment, string backpackPath, string details)
        { Prefab = prefab; Count = count; Quality = quality; X = x; Y = y; Equipment = equipment; BackpackPath = backpackPath ?? ""; Details = details ?? ""; }
    }
    public sealed class AdministrationView
    {
        public readonly long World, Generation;
        public readonly ReadOnlyCollection<AdministrationRequest> Requests;
        public readonly string SelectedId;
        public readonly ReadOnlyCollection<AdministrationItem> Items;
        public readonly bool Busy;
        public readonly string Error, Notice;
        public AdministrationView(long world, long generation, IEnumerable<AdministrationRequest> requests,
            string selectedId, IEnumerable<AdministrationItem> items, bool busy, string error, string notice)
        {
            World = world; Generation = generation;
            Requests = new List<AdministrationRequest>(requests ?? new AdministrationRequest[0]).AsReadOnly();
            SelectedId = selectedId ?? ""; Items = new List<AdministrationItem>(items ?? new AdministrationItem[0]).AsReadOnly();
            Busy = busy; Error = error ?? ""; Notice = notice ?? "";
        }
    }

    // No Unity objects, delegates or RPCs on this worker. The game thread supplies
    // the local-host eligibility and world identity; each queued job captures an
    // epoch. Context switches invalidate admission without waiting for file I/O.
    public sealed class AdministrationSession : IDisposable
    {
        private sealed class Context
        {
            public readonly long World, Generation;
            public readonly bool Eligible;
            public Context(long world, long generation, bool eligible) { World = world; Generation = generation; Eligible = eligible; }
        }
        private readonly StateStore store;
        private readonly object sync = new object();
        private readonly ManualResetEvent idle = new ManualResetEvent(true);
        private Context context = new Context(0, 0, false);
        private AdministrationView view = new AdministrationView(0, 0, null, "", null, false, "", "");
        private bool outstanding, disposed;
        public const int MaximumRequests = 256, MaximumScannedRequests = 1024, MaximumItems = 4096;
        public AdministrationSession(StateStore stateStore) { if (stateStore == null) throw new ArgumentNullException("stateStore"); store = stateStore; }

        public void SetContext(long world, bool eligible)
        {
            lock (sync)
            {
                if (disposed) return;
                eligible = eligible && world != 0;
                if (context.World == world && context.Eligible == eligible) return;
                context = new Context(world, context.Generation + 1, eligible);
                view = new AdministrationView(world, context.Generation, null, "", null, outstanding, "", "");
            }
        }
        public AdministrationView GetSnapshot() { lock (sync) return view; }
        public bool Refresh() { return Queue(delegate(Context token, AdministrationView before) { return List(token, before, ""); }); }
        public bool Inspect(string id)
        {
            return Queue(delegate(Context token, AdministrationView before)
            {
                AdministrationRequest row = Find(before.Requests, id);
                if (row == null || !row.IsActionable || row.World != token.World) throw new InvalidOperationException("Select a valid request in the current world.");
                CharacterState candidate = ReadCandidate(token, id);
                AdministrationRequest current = Describe(id, candidate);
                List<AdministrationItem> items = Items(candidate);
                var rows = new List<AdministrationRequest>(before.Requests);
                for (int i = 0; i < rows.Count; ++i) if (rows[i].Id == id) rows[i] = current;
                return new AdministrationView(token.World, token.Generation, rows, id, items, false, "", "");
            });
        }
        public bool Decide(string id, string fingerprint, AdministrationDecision decision)
        {
            if (decision != AdministrationDecision.Approve && decision != AdministrationDecision.Fresh && decision != AdministrationDecision.Reject)
                return FailImmediate("Unknown administration decision.");
            return Queue(delegate(Context token, AdministrationView before)
            {
                AdministrationRequest reviewed = Find(before.Requests, id);
                if (before.SelectedId != id || reviewed == null || !reviewed.IsActionable || reviewed.World != token.World
                    || reviewed.Fingerprint != fingerprint)
                    throw new InvalidOperationException("Inspect the current request before making a decision.");
                store.ApplyAdministrationDecision(token.World, id, fingerprint,
                    decision == AdministrationDecision.Fresh, decision == AdministrationDecision.Reject,
                    delegate { return Current(token); });
                string notice = decision == AdministrationDecision.Reject ? "Request rejected; the player can submit again by reconnecting."
                    : decision == AdministrationDecision.Fresh ? "Approved with a new character; the player can reconnect."
                    : "Approved with the reviewed progress; the player can reconnect.";
                try { return List(token, new AdministrationView(token.World, token.Generation, null, "", null, false, "", ""), notice); }
                catch (Exception error)
                {
                    // A failed directory refresh cannot turn a durably accepted
                    // decision into an apparent failed import. Remove that row
                    // immediately and preserve the successful outcome.
                    var remaining = new List<AdministrationRequest>();
                    foreach (AdministrationRequest row in before.Requests) if (row.Id != id) remaining.Add(row);
                    return new AdministrationView(token.World, token.Generation, remaining, "", null, false, SafeError(error), notice);
                }
            });
        }
        private bool Queue(Func<Context, AdministrationView, AdministrationView> operation)
        {
            Context token; AdministrationView before;
            lock (sync)
            {
                if (disposed || !context.Eligible || outstanding) return false;
                token = context; before = view; outstanding = true; idle.Reset();
                view = new AdministrationView(before.World, before.Generation, before.Requests, before.SelectedId, before.Items, true, "", before.Notice);
            }
            try
            {
                if (!ThreadPool.QueueUserWorkItem(delegate
                {
                    AdministrationView result = null;
                    try { if (Current(token)) result = operation(token, before); }
                    catch (Exception e)
                    {
                        result = new AdministrationView(token.World, token.Generation, before.Requests, before.SelectedId,
                            before.Items, false, SafeError(e), "");
                    }
                    finally
                    {
                        lock (sync)
                        {
                            if (!disposed && ReferenceEquals(context, token) && result != null) view = result;
                            else if (!disposed) view = new AdministrationView(view.World, view.Generation, view.Requests,
                                view.SelectedId, view.Items, false, view.Error, view.Notice);
                            outstanding = false; idle.Set();
                        }
                    }
                })) throw new InvalidOperationException("The administration worker could not be started.");
                return true;
            }
            catch (Exception e)
            {
                lock (sync)
                {
                    outstanding = false; idle.Set();
                    if (!disposed && ReferenceEquals(context, token)) view = new AdministrationView(before.World, before.Generation,
                        before.Requests, before.SelectedId, before.Items, false, SafeError(e), "");
                }
                return false;
            }
        }
        private bool Current(Context token) { lock (sync) return !disposed && ReferenceEquals(context, token) && token.Eligible; }
        private bool FailImmediate(string error)
        {
            lock (sync)
            {
                if (!disposed && context.Eligible && !outstanding) view = new AdministrationView(view.World, view.Generation,
                    view.Requests, view.SelectedId, view.Items, false, error, "");
            }
            return false;
        }
        private CharacterState ReadCandidate(Context token, string id)
        {
            if (!Current(token)) throw new InvalidOperationException("The host session changed; open the window again.");
            CharacterState candidate = store.Pending(id);
            if (candidate.World != token.World || StateCodec.Key(candidate.World, candidate.Owner, candidate.Character) != id)
                throw new InvalidDataException("Request belongs to another world or has an invalid identity.");
            if (store.HasStoredState(candidate.World, candidate.Owner, candidate.Character))
                throw new InvalidOperationException("Character already exists; approval cannot overwrite progress.");
            return candidate;
        }
        private AdministrationView List(Context token, AdministrationView before, string notice)
        {
            var requests = new List<AdministrationRequest>(); int scanned = 0; bool truncated = false;
            foreach (string id in store.PendingIds())
            {
                if (!Current(token)) throw new InvalidOperationException("The host session changed; open the window again.");
                if (++scanned > MaximumScannedRequests || requests.Count >= MaximumRequests) { truncated = true; break; }
                CharacterState candidate = null;
                try
                {
                    candidate = store.Pending(id);
                    if (candidate.World != token.World) continue;
                    if (StateCodec.Key(candidate.World, candidate.Owner, candidate.Character) != id)
                        throw new InvalidDataException("Request identity mismatch.");
                    if (store.HasStoredState(candidate.World, candidate.Owner, candidate.Character)) continue;
                    requests.Add(Describe(id, candidate));
                }
                catch (Exception e)
                {
                    // Corrupt files have no trustworthy world identity. Display
                    // a diagnostic-only row; it cannot be inspected or approved.
                    requests.Add(new AdministrationRequest(id, candidate == null ? "" : candidate.Name,
                        candidate == null ? "" : candidate.Owner, candidate == null ? 0 : candidate.Character,
                        candidate == null ? 0 : candidate.World, 0, "", SafeError(e), false));
                }
            }
            requests.Sort(delegate(AdministrationRequest a, AdministrationRequest b)
            { int result = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name); return result != 0 ? result : StringComparer.Ordinal.Compare(a.Id, b.Id); });
            AdministrationRequest selected = Find(requests, before.SelectedId), previous = Find(before.Requests, before.SelectedId);
            bool retained = selected != null && previous != null && selected.IsActionable && selected.Fingerprint == previous.Fingerprint;
            if (truncated) notice = "The request list was limited; handle these requests, then refresh.";
            return new AdministrationView(token.World, token.Generation, requests, retained ? before.SelectedId : "",
                retained ? (IEnumerable<AdministrationItem>)before.Items : null, false, "", notice);
        }
        private static AdministrationRequest Describe(string id, CharacterState state)
        {
            int count = 0;
            if (state.Player.Length != 0)
                foreach (StoredItem item in NativeInventory.IncludingBackpacks(NativeInventory.ReadPlayer(state.Player)))
                    if (++count > MaximumItems) throw new InvalidDataException("The proposal has too many items to review safely.");
            return new AdministrationRequest(id, state.Name, state.Owner, state.Character, state.World, count,
                StateCodec.Hash(StateCodec.Encode(state)), "", false);
        }
        private static List<AdministrationItem> Items(CharacterState state)
        {
            var result = new List<AdministrationItem>();
            if (state.Player.Length != 0) AppendItems(NativeInventory.ReadPlayer(state.Player), "", 0, result);
            return result;
        }
        private static void AppendItems(IEnumerable<StoredItem> items, string backpack, int depth, List<AdministrationItem> result)
        {
            if (depth > 4) throw new InvalidDataException("Backpack nesting exceeds the supported depth.");
            foreach (StoredItem item in items)
            {
                if (result.Count >= MaximumItems) throw new InvalidDataException("The proposal has too many items to review safely.");
                string slot; bool equipment = item.Equipped || (item.Custom.TryGetValue("eaqs_slot", out slot) && slot.Length != 0);
                var keys = new List<string>(item.Custom.Keys);
                result.Add(new AdministrationItem(item.Prefab, item.Count, item.Quality, item.X, item.Y, equipment, backpack,
                    "variant=" + item.Variant + (keys.Count == 0 ? "" : "; metadata=" + String.Join(", ", keys.ToArray()))));
                foreach (var field in item.Custom)
                {
                    if (field.Key.IndexOf("AdventureBackpacks.Components.BackpackComponent", StringComparison.Ordinal) < 0 || field.Value.Length == 0) continue;
                    byte[] payload;
                    try { payload = Convert.FromBase64String(field.Value); }
                    catch (FormatException e) { throw new InvalidDataException("Invalid Adventure Backpacks inventory.", e); }
                    using (var stream = new MemoryStream(payload, false))
                    using (var reader = new BinaryReader(stream))
                    {
                        var children = NativeInventory.ReadInventory(reader);
                        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing backpack inventory data.");
                        string path = backpack.Length == 0 ? item.Prefab + "@" + item.X + "," + item.Y : backpack + "/" + item.Prefab + "@" + item.X + "," + item.Y;
                        AppendItems(children, path, depth + 1, result);
                    }
                }
            }
        }
        private static AdministrationRequest Find(IEnumerable<AdministrationRequest> requests, string id)
        { if (String.IsNullOrEmpty(id)) return null; foreach (var row in requests) if (row.Id == id) return row; return null; }
        private static string SafeError(Exception error)
        {
            if (error is FileNotFoundException || error is DirectoryNotFoundException) return "The request is no longer available; refresh the list.";
            if (error is UnauthorizedAccessException || (error is IOException && !(error is InvalidDataException))) return "The request could not be read or written. Check storage access.";
            if (error is InvalidDataException || error is InvalidOperationException || error is ArgumentException)
                return error.Message.Length <= 240 ? error.Message : error.Message.Substring(0, 240);
            return "The request could not be processed safely.";
        }
        public void Dispose()
        {
            lock (sync) { if (disposed) return; disposed = true; context = new Context(0, context.Generation + 1, false); }
            idle.WaitOne(); idle.Dispose();
        }
    }
}
