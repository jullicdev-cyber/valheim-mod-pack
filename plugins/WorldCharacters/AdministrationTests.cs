using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using ValheimModPack.WorldCharacters;

internal static class AdministrationTests
{
    private static int passed;
    private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); ++passed; }
    private static void Refuse(Action action, string name)
    {
        try { action(); }
        catch (InvalidOperationException) { ++passed; return; }
        catch (InvalidDataException) { ++passed; return; }
        catch (ArgumentException) { ++passed; return; }
        throw new Exception("Did not refuse: " + name);
    }
    private static void Idle(AdministrationSession service)
    {
        DateTime limit = DateTime.UtcNow.AddSeconds(8);
        while (service.GetSnapshot().Busy)
        { if (DateTime.UtcNow > limit) throw new Exception("Worker did not complete."); Thread.Sleep(1); }
    }
    private static byte[] Inventory(int prefab, int count, int quality, string backpack, bool nativeEquipped = false)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(109); writer.Write((ushort)1); writer.Write(100); writer.Write((byte)7); writer.Write((byte)6);
            writer.Write((byte)0); writer.Write((byte)(4 | 8 | 64 | 128 | (nativeEquipped ? 2 : 0))); writer.Write((ushort)quality); writer.Write((ushort)count);
            writer.Write(prefab); writer.Write((byte)(backpack == null ? 2 : 3));
            writer.Write("eaqs_slot"); writer.Write(nativeEquipped ? "" : "head"); writer.Write("EpicLoot"); writer.Write("retained enchantment");
            if (backpack != null) { writer.Write("vapok.mods.adventurebackpacks#AdventureBackpacks.Components.BackpackComponent"); writer.Write(backpack); }
            writer.Write((byte)0); writer.Flush(); return stream.ToArray();
        }
    }
    private static CharacterState Candidate(long world, long character, string name, int count, bool nested, bool nativeEquipped = false)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(33); for (int i = 0; i < 4; ++i) writer.Write(25f); writer.Write("GP_Eikthyr"); writer.Write(0f);
            writer.Write(Inventory(123456, count, 3, nested ? Convert.ToBase64String(Inventory(654321, 12, 2, null)) : null, nativeEquipped));
            writer.Write(0); writer.Flush();
            return new CharacterState { World = world, Character = character, Owner = "Steam_76561198000000001", Name = name,
                Build = "legacy-build-is-explicitly-reviewed", Player = stream.ToArray() };
        }
    }
    private static object StoreGate(StateStore store) { return typeof(StateStore).GetField("sync", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(store); }
    private static void Select(AdministrationSession service, string id)
    { Check(service.Refresh(), "refresh accepted"); Idle(service); Check(service.Inspect(id), "inspection accepted"); Idle(service); Check(service.GetSnapshot().SelectedId == id, "selected request published"); }
    private static void Decisions(string root)
    {
        using (var store = new StateStore(root))
        using (var service = new AdministrationSession(store))
        {
            CharacterState a = Candidate(11, 44, "Викинг", 10, true), b = Candidate(22, 45, "Foreign", 5, false);
            string aId = store.Propose(a), bId = store.Propose(b);
            Check(!service.Refresh() && !service.Inspect(aId) && !service.Decide(aId, "x", AdministrationDecision.Approve), "client and no-world actions are refused");
            service.SetContext(11, true); Check(service.Refresh(), "local-host refresh accepted"); Idle(service);
            AdministrationView snapshot = service.GetSnapshot();
            Check(snapshot.Requests.Count == 1 && snapshot.Requests[0].Id == aId, "requests filtered to current world");
            Check(snapshot.Requests[0].ItemsCount == 2 && snapshot.Requests[0].IsActionable, "nested inventory included in reviewed count");
            Check(ReferenceEquals(snapshot, service.GetSnapshot()), "frame snapshot reads reuse immutable DTO without disk or cloning");
            bool immutable = false; try { ((IList<AdministrationRequest>)snapshot.Requests).Clear(); } catch (NotSupportedException) { immutable = true; }
            Check(immutable, "request snapshot cannot be mutated by UI");
            Check(service.Decide(aId, snapshot.Requests[0].Fingerprint, AdministrationDecision.Approve), "uninspected decision queued for safe rejection"); Idle(service);
            Check(store.Read(11, a.Owner, a.Character) == null && service.GetSnapshot().Error.Length != 0, "uninspected candidate never imported");
            Check(service.Inspect(aId), "inspection queued"); Idle(service); snapshot = service.GetSnapshot();
            Check(snapshot.Items.Count == 2 && snapshot.Items[0].Prefab == 123456 && snapshot.Items[1].Prefab == 654321, "native prefab hashes projected for icon lookup");
            Check(snapshot.Items[0].Count == 10 && snapshot.Items[0].Quality == 3 && snapshot.Items[0].Equipment, "quantity quality and equipment preserved");
            Check(snapshot.Items[0].X == 7 && snapshot.Items[0].Y == 6 && snapshot.Items[1].BackpackPath.Length != 0, "hidden EAQS coordinates and nested backpack group preserved");
            Check(snapshot.Items[0].Details.Contains("EpicLoot") && snapshot.Items[0].BackpackPath.Length == 0, "metadata indicated without retaining payloads");
            Check(service.Decide(aId, "not-the-reviewed-fingerprint", AdministrationDecision.Approve), "wrong fingerprint decision queued"); Idle(service);
            Check(store.Read(11, a.Owner, a.Character) == null, "wrong fingerprint cannot authorize import");
            string fingerprint = service.GetSnapshot().Requests[0].Fingerprint;
            Check(service.Decide(aId, fingerprint, AdministrationDecision.Approve), "reviewed approval accepted"); Idle(service);
            CharacterState imported = store.Read(11, a.Owner, a.Character);
            Check(imported != null && imported.Revision == 1 && imported.Player.SequenceEqual(a.Player), "full character imported byte for byte once");
            Check(imported.Build == a.Build, "legacy build metadata remains explicit and unchanged");
            Check(service.GetSnapshot().Requests.Count == 0 && store.PendingIds().Contains(aId), "approved proposal retained as backup but hidden from active queue");
            Refuse(() => store.ApplyAdministrationDecision(11, aId, fingerprint, false, false, () => true), "atomic helper prevents overwriting progress");
            Refuse(() => store.ApplyAdministrationDecision(11, bId, StateCodec.Hash(StateCodec.Encode(b)), false, false, () => true), "atomic helper rejects cross-world request");
            CharacterState fresh = Candidate(11, 46, "Fresh", 7, true); string freshId = store.Propose(fresh); Select(service, freshId);
            Check(service.Decide(freshId, service.GetSnapshot().Requests.Single(r => r.Id == freshId).Fingerprint, AdministrationDecision.Fresh), "fresh decision accepted"); Idle(service);
            Check(store.Read(11, fresh.Owner, fresh.Character).Player.Length == 0 && store.Pending(freshId).Player.SequenceEqual(fresh.Player), "fresh resets active character but preserves reviewed import backup");
            CharacterState rejected = Candidate(11, 47, "Rejected", 9, false); string rejectedId = store.Propose(rejected); Select(service, rejectedId);
            Check(service.Decide(rejectedId, service.GetSnapshot().Requests.Single(r => r.Id == rejectedId).Fingerprint, AdministrationDecision.Reject), "rejection accepted"); Idle(service);
            Check(!store.PendingIds().Contains(rejectedId) && store.Read(11, rejected.Owner, rejected.Character) == null, "rejection archives proposal without importing character");
            Check(Directory.GetFiles(Path.Combine(root, "pending"), rejectedId + ".wchar.rejected-*").Length == 1, "rejection evidence retained");
            CharacterState equipped = Candidate(11, 48, "Native armour", 1, false, true); string equippedId = store.Propose(equipped); Select(service, equippedId);
            Check(NativeInventory.ReadPlayer(equipped.Player)[0].Equipped && service.GetSnapshot().Items[0].Equipment,
                "native equipped flag displayed even without EAQS slot metadata");
            Refuse(() => store.ApplyAdministrationDecision(11, bId, "x", true, true, () => true), "ambiguous decision denied");
            Check(!service.Decide(bId, "x", (AdministrationDecision)999), "undefined decision denied immediately");
        }
    }
    private static void StaleAndContext(string root)
    {
        using (var store = new StateStore(root))
        using (var service = new AdministrationSession(store))
        {
            CharacterState candidate = Candidate(11, 51, "Stale", 10, false); string id = store.Propose(candidate);
            service.SetContext(11, true); Select(service, id); string before = service.GetSnapshot().Requests[0].Fingerprint;
            store.RejectProposal(id); candidate.Player = Candidate(11, 51, "Stale", 99, false).Player; store.Propose(candidate);
            Check(service.Decide(id, before, AdministrationDecision.Approve), "stale reviewed identity decision queued"); Idle(service);
            Check(store.Read(11, candidate.Owner, candidate.Character) == null && service.GetSnapshot().Error.Contains("changed after inspection"), "same identity resubmission cannot approve unreviewed content");
            Check(service.Refresh(), "refresh changed request"); Idle(service);
            Check(service.GetSnapshot().SelectedId.Length == 0 && service.GetSnapshot().Items.Count == 0, "refresh invalidates changed inspection");
            Select(service, id); string fingerprint = service.GetSnapshot().Requests[0].Fingerprint;
            object gate = StoreGate(store);
            Monitor.Enter(gate);
            try
            {
                Check(service.Decide(id, fingerprint, AdministrationDecision.Approve), "queued decision accepted before context switch");
                Check(!service.Refresh() && !service.Inspect(id), "bounded worker refuses overlapping jobs");
                long generation = service.GetSnapshot().Generation; service.SetContext(22, true);
                Check(service.GetSnapshot().Generation == generation + 1 && service.GetSnapshot().Requests.Count == 0, "world switch invalidates displayed rows immediately without waiting for disk");
            }
            finally { Monitor.Exit(gate); }
            Idle(service);
            Check(store.Read(11, candidate.Owner, candidate.Character) == null, "queued obsolete-world decision cannot mutate original world");
            Check(service.GetSnapshot().World == 22 && service.GetSnapshot().Error.Length == 0 && service.GetSnapshot().Requests.Count == 0, "obsolete completion cannot contaminate new-world UI");
            service.SetContext(11, true); Select(service, id);
            Monitor.Enter(gate);
            try { Check(service.Decide(id, fingerprint, AdministrationDecision.Reject), "queued rejection accepted"); service.SetContext(11, false); }
            finally { Monitor.Exit(gate); }
            Idle(service); Check(store.PendingIds().Contains(id), "loss of local host privilege cancels queued rejection");
            Check(!service.Refresh(), "ineligible host context remains closed");
            service.SetContext(11, true);
            store.ApplyAdministrationDecision(11, id, fingerprint, false, false, delegate { service.SetContext(22, true); return true; });
            Check(store.Read(11, candidate.Owner, candidate.Character) != null && store.Read(22, candidate.Owner, candidate.Character) == null, "already admitted atomic decision finishes only in captured world");
            Check(service.GetSnapshot().World == 22, "admitted commit cannot change new UI context");
            CharacterState reviewed = Candidate(11, 52, "Exact reviewed bytes", 12, false);
            string exactId = store.Propose(reviewed), exactFingerprint = StateCodec.Hash(StateCodec.Encode(reviewed));
            store.ApplyAdministrationDecision(11, exactId, exactFingerprint, false, false, delegate
            {
                File.WriteAllBytes(Path.Combine(root, "pending", exactId + ".wchar"), StateCodec.Encode(Candidate(11, 52, "Exact reviewed bytes", 999, false)));
                return true;
            });
            Check(store.Read(11, reviewed.Owner, reviewed.Character).Player.SequenceEqual(reviewed.Player), "admitted approval commits verified bytes without re-reading externally replaced proposal");
        }
    }
    private static void Corruption(string root)
    {
        using (var store = new StateStore(root))
        using (var service = new AdministrationSession(store))
        {
            CharacterState candidate = Candidate(11, 71, "Corrupt", 1, false); string id = store.Propose(candidate);
            File.WriteAllBytes(Path.Combine(root, "pending", id + ".wchar"), new byte[] { 1, 2, 3 });
            service.SetContext(11, true); Check(service.Refresh(), "refresh corruption diagnostics"); Idle(service);
            AdministrationRequest row = service.GetSnapshot().Requests[0];
            Check(row.World == 0 && !row.IsActionable && row.Error.Length != 0, "corrupt unknown-world proposal remains visible and nonactionable");
            Check(service.Inspect(id), "corrupt row inspection safely queued"); Idle(service);
            Check(service.GetSnapshot().SelectedId.Length == 0 && service.GetSnapshot().Error.Length != 0, "corrupt proposal never becomes selected reviewed request");
            CharacterState mismatch = Candidate(11, 72, "Wrong identity", 1, false); string mismatchId = store.Propose(mismatch);
            File.WriteAllBytes(Path.Combine(root, "pending", mismatchId + ".wchar"), StateCodec.Encode(Candidate(11, 73, "Substitution", 2, false)));
            Check(service.Refresh(), "refresh mismatched identity"); Idle(service);
            Check(service.GetSnapshot().Requests.Single(r => r.Id == mismatchId).Error.Length != 0, "valid envelope with mismatched identity is nonactionable");
            CharacterState backup = Candidate(11, 74, "Backup only", 1, false); string backupId = store.Propose(backup);
            string fingerprint = StateCodec.Hash(StateCodec.Encode(backup));
            File.WriteAllBytes(Path.Combine(root, "characters", backupId + ".wchar.bak"), StateCodec.Encode(backup));
            Refuse(() => store.ApplyAdministrationDecision(11, backupId, fingerprint, false, false, () => true), "backup-only existing character remains protected");
            Check(service.Refresh(), "refresh existing recovery state"); Idle(service);
            Check(!service.GetSnapshot().Requests.Any(r => r.Id == backupId), "backup-only character cannot be mistaken for a pending first join");
            Refuse(() => store.ApplyAdministrationDecision(11, "../../escape", fingerprint, false, false, () => true), "full immutable request ID required");
            CharacterState denied = Candidate(11, 75, "Denied", 1, false); string deniedId = store.Propose(denied);
            Refuse(() => store.ApplyAdministrationDecision(11, deniedId, StateCodec.Hash(StateCodec.Encode(denied)), false, false, () => false), "atomic admission revalidates host before persistence");
            Check(store.Read(11, denied.Owner, denied.Character) == null && store.PendingIds().Contains(deniedId), "denied admission leaves all evidence untouched");
        }
    }
    private static void Bounded(string root)
    {
        using (var store = new StateStore(root))
        using (var service = new AdministrationSession(store))
        {
            for (int i = 1; i <= AdministrationSession.MaximumRequests + 1; ++i) store.Propose(Candidate(11, 1000 + i, "Row" + i, 1, false));
            service.SetContext(11, true); Check(service.Refresh(), "bounded refresh"); Idle(service);
            Check(service.GetSnapshot().Requests.Count == AdministrationSession.MaximumRequests && service.GetSnapshot().Notice.Length != 0, "reviewed row and retained memory bounds enforced");
            Check(service.GetSnapshot().Requests.Select(r => r.Name).SequenceEqual(service.GetSnapshot().Requests.Select(r => r.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)), "request rows stable ordered");
            long generation = service.GetSnapshot().Generation; service.SetContext(11, true);
            Check(service.GetSnapshot().Generation == generation, "per-frame same-context update does not invalidate or allocate a new view");
        }
    }
    private static int Main(string[] args)
    {
        try { Decisions(args[0] + "-decisions"); StaleAndContext(args[0] + "-context"); Corruption(args[0] + "-corruption"); Bounded(args[0] + "-bounded");
            Console.WriteLine("PASS: " + passed + " World Characters administration assertions (host admission, stale review, world isolation, retained imports, nested inventory, corruption and worker bounds)."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
