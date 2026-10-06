using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace ValheimModPack.PartyPrison
{
    internal static class PolicyTests
    {
        private const string Alice = "Steam_76561198000000001", Bob = "Steam_76561198000000002";
        private const string Crossplay = "PlayFab_0123456789ABCDEF";
        private static int assertions;

        private static void Check(bool value, string description)
        { ++assertions; if (!value) throw new Exception("FAIL: " + description); }

        private static void Reject(Action action, string description)
        {
            ++assertions;
            try { action(); }
            catch (ArgumentException) { return; }
            catch (InvalidDataException) { return; }
            catch (InvalidOperationException) { return; }
            catch (UnauthorizedAccessException) { return; }
            catch (IOException) { return; }
            throw new Exception("FAIL: accepted " + description);
        }

        private static PrisonRegion Region()
        {
            return new PrisonRegion { Center = new PrisonPoint(100, 30, -200), Radius = 18, HalfHeight = 14,
                CellSpawn = new PrisonPoint(94, 19, -200), ArenaSpawn = new PrisonPoint(106, 19, -200) };
        }

        private static SentenceState Sentence(SentenceStore store, string account, double seconds)
        { return store.Impose(true, account, "Игрок", "Нарушение правил сервера", seconds, new PrisonPoint(-100, 10, 200)); }

        public static int Main(string[] args)
        {
            try
            {
                if (args.Length != 1) throw new ArgumentException("Expected fixture state directory.");
                Directory.CreateDirectory(args[0]);
                Geometry(); Identity();
                OnlineTime(Path.Combine(args[0], "online"));
                HostAuthority(Path.Combine(args[0], "authority"));
                ReleaseRecovery(Path.Combine(args[0], "release"));
                EmergencyRelease(Path.Combine(args[0], "emergency"));
                WorldIsolation(Path.Combine(args[0], "worlds"));
                Corruption(Path.Combine(args[0], "corruption"));
                WriteFailures(Path.Combine(args[0], "write"));
                Console.WriteLine("PartyPrison sentence/persistence checks passed: " + assertions);
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        private static void Geometry()
        {
            PrisonRegion prison = Region(); SentencePolicy.RequireRegion(prison);
            Check(prison.Contains(prison.Center), "center is contained");
            Check(prison.Contains(new PrisonPoint(118, 44, -200)), "exact horizontal and vertical boundary is contained");
            Check(!prison.Contains(new PrisonPoint(118.001, 30, -200)), "horizontal escape is outside");
            Check(!prison.Contains(new PrisonPoint(100, 44.001, -200)), "vertical escape is outside");
            Check(!prison.Contains(new PrisonPoint(118, 30, -182)), "diagonal is outside cylinder");
            Check(!prison.Contains(new PrisonPoint(Double.NaN, 30, -200)), "NaN position rejected");
            Check(!prison.Contains(new PrisonPoint(100, Double.PositiveInfinity, -200)), "infinite position rejected");
            foreach (double radius in new[] { 0, 5.99, 100.01, Double.NaN, Double.PositiveInfinity })
            {
                double value = radius; Reject(delegate { var wrong = Region(); wrong.Radius = value; SentencePolicy.RequireRegion(wrong); }, "invalid radius");
            }
            foreach (double height in new[] { 0, 3.99, 100.01, Double.NaN, Double.NegativeInfinity })
            {
                double value = height; Reject(delegate { var wrong = Region(); wrong.HalfHeight = value; SentencePolicy.RequireRegion(wrong); }, "invalid height");
            }
            Reject(delegate { var wrong = Region(); wrong.CellSpawn.X = 1000; SentencePolicy.RequireRegion(wrong); }, "cell outside prison");
            Reject(delegate { var wrong = Region(); wrong.ArenaSpawn.Y = 1000; SentencePolicy.RequireRegion(wrong); }, "arena outside prison");
            Reject(delegate { var wrong = Region(); wrong.Center.X = Double.NaN; SentencePolicy.RequireRegion(wrong); }, "nonfinite prison center");
        }

        private static void Identity()
        {
            SentencePolicy.RequireAccountId(Alice); SentencePolicy.RequireAccountId(Crossplay);
            foreach (string account in new[] { null, "", "Игрок", "local-host", "Steam_00000000000000000", "Steam_7656119800000000x",
                "steam_76561198000000001", "Steam_76561198000000001/../", "PlayFab_0000000000000000", "PlayFab_0123456789abcdef", "PlayFab_ABC" })
            {
                string value = account; Reject(delegate { SentencePolicy.RequireAccountId(value); }, "untrusted or malformed account");
            }
            foreach (double time in new[] { 0, -1, Double.NaN, Double.PositiveInfinity, SentencePolicy.MaximumDurationSeconds + 1 })
            {
                double value = time; Reject(delegate { SentencePolicy.RequireDuration(value); }, "invalid duration");
            }
            SentencePolicy.RequireDuration(1); SentencePolicy.RequireDuration(SentencePolicy.MaximumDurationSeconds);
        }

        private static void OnlineTime(string root)
        {
            string token;
            using (var store = new SentenceStore(root, 41))
            {
                Reject(delegate { Sentence(store, Alice, 30); }, "sentence before prison configured");
                PrisonRegion input = Region(); store.SetRegion(true, input); input.Center.X = 3000;
                Check(store.Region.Center.X == 100, "configured region copied from caller");
                PrisonRegion returned = store.Region; returned.CellSpawn.Y = 1000;
                Check(store.Region.CellSpawn.Y == 19, "region getter cannot mutate host geometry");
                SentenceState first = Sentence(store, Alice, 30); token = first.SentenceId;
                Sentence(store, Bob, 30); Sentence(store, Crossplay, 30);
                first.RemainingSeconds = 0; first.AccountId = Bob;
                Check(store.Find(Alice).RemainingSeconds == 30, "returned sentence cannot modify durable state");
                var list = store.All(); list[0].RemainingSeconds = 999; list[0] = null;
                Check(store.Find(Alice).RemainingSeconds == 30, "listing is defensive");
                store.TickOnline(new[] { Alice, Alice }, 1.25);
                Check(store.Find(Alice).RemainingSeconds == 28.75 && store.Find(Alice).Revision == 2, "duplicate active roster entries counted once");
                Check(store.Find(Bob).RemainingSeconds == 30 && store.Find(Crossplay).RemainingSeconds == 30, "offline accounts receive no credit");
                Check(store.TickOnline(new string[0], 5).Length == 0 && store.Find(Alice).RemainingSeconds == 28.75, "empty verified roster does not tick");
                Check(store.TickOnline(new[] { Alice }, 0).Length == 0 && store.Find(Alice).Revision == 2, "zero tick is a no-op");
                foreach (double tick in new[] { -1, 5.01, Double.NaN, Double.PositiveInfinity })
                {
                    double value = tick; Reject(delegate { store.TickOnline(new[] { Alice }, value); }, "pause or malformed tick");
                }
                Reject(delegate { store.TickOnline(new[] { Alice, "Игрок" }, 1); }, "partially invalid authenticated roster");
                Check(store.Find(Alice).RemainingSeconds == 28.75, "invalid roster cannot partly reduce a sentence");
                Reject(delegate { Sentence(store, Alice, 10); }, "duplicate active sentence");
                Reject(delegate { store.SetRegion(true, Region()); }, "moving prison while account detained");
                SentenceState[] changed = store.TickOnline(new[] { Alice, Crossplay }, 0.75);
                Check(changed.Length == 2 && store.Find(Alice).RemainingSeconds == 28 && store.Find(Crossplay).RemainingSeconds == 29.25,
                    "verified online accounts alone get fractional credit");
                changed[0].RemainingSeconds = 0;
                Check(store.Find(Alice).RemainingSeconds == 28, "tick results are defensive");
                Reject(delegate { using (var competing = new SentenceStore(root, 41)) { } }, "second host process for same world");
            }
            using (var store = new SentenceStore(root, 41))
            {
                Check(store.Find(Alice).RemainingSeconds == 28 && store.Find(Alice).SentenceId == token, "restart preserves remaining time and token without offline discount");
                Check(store.Find(Bob).RemainingSeconds == 30 && store.Region.Center.X == 100, "restart preserves offline account and prison region");
            }
        }

        private static void HostAuthority(string root)
        {
            using (var store = new SentenceStore(root, 42))
            {
                Reject(delegate { store.SetRegion(false, Region()); }, "nonhost prison configuration");
                Check(store.Region == null, "nonhost configuration unchanged");
                store.SetRegion(true, Region());
                Reject(delegate { store.Impose(false, Alice, "Игрок", "Причина", 60, new PrisonPoint()); }, "nonhost sentencing");
                Check(store.Find(Alice) == null, "unauthorized sentence absent");
                Sentence(store, Alice, 60);
                Reject(delegate { store.RequestRelease(false, Alice); }, "nonhost release");
                Check(!store.Find(Alice).PendingRelease, "unauthorized release remains denied");
                Reject(delegate { store.Impose(true, Bob, "bad\nname", "reason", 30, new PrisonPoint()); }, "name control injection");
                Reject(delegate { store.Impose(true, Bob, "name", new string('x', 301), 30, new PrisonPoint()); }, "oversized reason");
                Reject(delegate { store.Impose(true, Bob, "name", "reason", 30, new PrisonPoint(Double.NaN, 0, 0)); }, "nonfinite return point");
                Reject(delegate { store.Impose(true, Bob, "name", "reason", 30, new PrisonPoint(1000001, 0, 0)); }, "unbounded return point");
                Reject(delegate { store.Impose(true, Bob, "\ud800", "reason", 30, new PrisonPoint()); }, "unpaired surrogate label");
                Check(store.Find(Bob) == null, "invalid sentence data leaves account free");
            }
        }

        private static void ReleaseRecovery(string root)
        {
            string token;
            using (var store = new SentenceStore(root, 43))
            {
                store.SetRegion(true, Region()); SentenceState state = Sentence(store, Alice, 2); token = state.SentenceId;
                Check(!store.AcknowledgeRelease(Alice, token), "client cannot acknowledge an active sentence");
                store.TickOnline(new[] { Alice }, 5);
                state = store.Find(Alice);
                Check(state.PendingRelease && state.RemainingSeconds == 0 && state.ReturnPosition.X == -100,
                    "expiry persists release intent and return point instead of removing sentence");
                Check(store.TickOnline(new[] { Alice }, 1).Length == 0 && store.Find(Alice).Revision == 2, "pending release does not tick again");
                Reject(delegate { store.SetRegion(true, Region()); }, "moving prison with unacknowledged release");
                Reject(delegate { Sentence(store, Alice, 10); }, "resentence before release acknowledgment");
                Check(!store.AcknowledgeRelease(Bob, token), "other account cannot acknowledge release");
                Check(!store.AcknowledgeRelease(Alice, Guid.NewGuid().ToString("N")), "wrong sentence token cannot acknowledge release");
            }
            using (var store = new SentenceStore(root, 43))
            {
                Check(store.Find(Alice).PendingRelease && store.Find(Alice).SentenceId == token, "host restart preserves unacknowledged release");
                long revision = store.Find(Alice).Revision;
                Check(store.RequestRelease(true, Alice).Revision == revision, "duplicate release request is idempotent");
                Check(store.AcknowledgeRelease(Alice, token) && store.Find(Alice) == null, "authenticated matching release ACK durably clears sentence");
                SentenceState newSentence = Sentence(store, Alice, 10);
                Check(newSentence.SentenceId != token && !store.AcknowledgeRelease(Alice, token), "delayed old ACK cannot erase new sentence");
                store.RequestRelease(true, Alice);
                Check(!store.AcknowledgeRelease(Alice, token) && store.Find(Alice).PendingRelease, "old ACK cannot erase even a new pending release");
                Check(store.AcknowledgeRelease(Alice, newSentence.SentenceId), "new pending release acknowledged");
                Check(!store.AcknowledgeRelease(Alice, newSentence.SentenceId), "duplicate ACK is a no-op");
                store.SetRegion(true, Region());
                SentenceState manual = Sentence(store, Bob, 100);
                Check(store.RequestRelease(true, Bob).PendingRelease, "host can pardon before sentence expiry");
                Check(store.AcknowledgeRelease(Bob, manual.SentenceId), "manual release follows same durable ACK protocol");
            }
            using (var store = new SentenceStore(root, 43)) Check(store.All().Length == 0, "acknowledged releases persist after restart");
        }

        private static void WorldIsolation(string root)
        {
            Reject(delegate { using (var invalid = new SentenceStore(root, 0)) { } }, "uninitialized world");
            using (var first = new SentenceStore(root, 100))
            using (var second = new SentenceStore(root, -200))
            {
                first.SetRegion(true, Region()); Sentence(first, Alice, 30);
                second.SetRegion(true, Region()); Sentence(second, Alice, 60);
                first.TickOnline(new[] { Alice }, 5);
                Check(first.Find(Alice).RemainingSeconds == 25 && second.Find(Alice).RemainingSeconds == 60,
                    "same authenticated account has independent world-scoped sentence");
                Check(first.StatePath != second.StatePath, "world scope uses distinct files and process locks");
            }
            using (var first = new SentenceStore(root, 100))
            using (var second = new SentenceStore(root, -200))
                Check(first.Find(Alice).RemainingSeconds == 25 && second.Find(Alice).RemainingSeconds == 60, "world isolation survives restart");
        }

        private static void Corruption(string root)
        {
            byte[] valid; string file;
            using (var store = new SentenceStore(root, 44))
            { store.SetRegion(true, Region()); Sentence(store, Alice, 60); file = store.StatePath; }
            valid = File.ReadAllBytes(file);
            for (int i = 0; i < valid.Length; ++i)
            {
                int length = i;
                File.WriteAllBytes(file, Prefix(valid, length));
                Reject(delegate { using (var invalid = new SentenceStore(root, 44)) { } }, "truncated durable state " + length);
                Check(new FileInfo(file).Length == length, "corrupt state preserved for recovery");
            }
            byte[] damaged = (byte[])valid.Clone(); damaged[damaged.Length - 1] ^= 1;
            File.WriteAllBytes(file, damaged);
            Reject(delegate { using (var invalid = new SentenceStore(root, 44)) { } }, "checksum mismatch");
            Check(Same(File.ReadAllBytes(file), damaged), "checksum failure never overwrites damaged file");
            byte[] body = Prefix(valid, valid.Length - 32);
            Corrupt(root, file, body, 4, BitConverter.GetBytes(3), "future document version");
            Corrupt(root, file, body, 8, BitConverter.GetBytes((long)999), "copied state from other world");
            Corrupt(root, file, body, 16, new byte[] { 2 }, "noncanonical region boolean");
            Corrupt(root, file, body, 41, BitConverter.GetBytes(Double.NaN), "nonfinite serialized radius");
            Corrupt(root, file, body, 57, BitConverter.GetBytes(2000.0), "serialized cell outside region");
            Corrupt(root, file, body, 105, BitConverter.GetBytes(Int32.MaxValue), "unbounded count before allocation");
            Corrupt(root, file, body, 109, BitConverter.GetBytes(Int32.MaxValue), "unbounded string before allocation");
            byte[] trailing = new byte[body.Length + 1]; Buffer.BlockCopy(body, 0, trailing, 0, body.Length);
            File.WriteAllBytes(file, Seal(trailing));
            Reject(delegate { using (var invalid = new SentenceStore(root, 44)) { } }, "trailing resealed data");
            File.WriteAllBytes(file, valid);
            using (var store = new SentenceStore(root, 44)) Check(store.Find(Alice).RemainingSeconds == 60, "administrator can restore original valid file after fail closed load");
        }

        private static void Corrupt(string root, string file, byte[] validBody, int offset, byte[] replacement, string description)
        {
            byte[] body = (byte[])validBody.Clone(); Buffer.BlockCopy(replacement, 0, body, offset, replacement.Length);
            byte[] malicious = Seal(body); File.WriteAllBytes(file, malicious);
            Reject(delegate { using (var invalid = new SentenceStore(root, 44)) { } }, description);
            Check(Same(File.ReadAllBytes(file), malicious), description + " file preserved");
        }

        private static void EmergencyRelease(string root)
        {
            string token;
            using (var store = new SentenceStore(root, 72))
            {
                store.SetRegion(true, Region()); SentenceState admitted = Sentence(store, Alice, 60); token = admitted.SentenceId;
                Reject(delegate { store.RequestEmergencyRelease(false, Alice); }, "nonhost emergency release");
                Check(!store.Find(Alice).EmergencyRelease && !store.Find(Alice).PendingRelease, "unauthorized emergency does not change confinement");
                SentenceState released = store.RequestEmergencyRelease(true, Alice);
                Check(released.PendingRelease && released.EmergencyRelease && released.RemainingSeconds == 0 && released.Revision == 2, "emergency release works before an inventory offer exists");
                Check(store.RequestEmergencyRelease(true, Alice).Revision == 2, "repeated emergency button is idempotent");
                Check(store.TickOnline(new[] { Alice }, 1).Length == 0, "emergency releases do not resume their timers");
                Check(!store.AcknowledgeRelease(Alice, Guid.NewGuid().ToString("N")), "stale release receipt cannot erase emergency sentence");
            }
            using (var store = new SentenceStore(root, 72))
            {
                Check(store.Find(Alice).EmergencyRelease && store.Find(Alice).SentenceId == token, "offline emergency cleanup decision survives restart");
                Check(store.AcknowledgeRelease(Alice, token) && store.Find(Alice) == null, "authenticated durable release receipt closes emergency sentence");
                SentenceState second = Sentence(store, Alice, 90);
                Check(!second.EmergencyRelease && second.SentenceId != token, "next sentence starts with independent release flags");
                store.RequestRelease(true, Alice);
                Check(store.RequestEmergencyRelease(true, Alice).EmergencyRelease, "a stuck pending regular release can be promoted to emergency");
            }
            string legacy = Path.Combine(root, "legacy"); string legacyPath;
            using (var store = new SentenceStore(legacy, 73))
            { store.SetRegion(true, Region()); Sentence(store, Alice, 30); legacyPath = store.StatePath; }
            byte[] current = File.ReadAllBytes(legacyPath), body = Prefix(current, current.Length - 33);
            Buffer.BlockCopy(BitConverter.GetBytes(1), 0, body, 4, 4); File.WriteAllBytes(legacyPath, Seal(body));
            using (var store = new SentenceStore(legacy, 73))
            {
                Check(!store.Find(Alice).EmergencyRelease && store.Find(Alice).RemainingSeconds == 30, "version 1 sentence loads without an invented emergency flag");
                Check(store.RequestEmergencyRelease(true, Alice).EmergencyRelease, "legacy stuck sentence upgrades with durable emergency release");
            }
        }

        private static void WriteFailures(string root)
        {
            string changed = Path.Combine(root, "outside-change");
            using (var store = new SentenceStore(changed, 45))
            {
                store.SetRegion(true, Region()); Sentence(store, Alice, 60);
                byte[] unexpected = File.ReadAllBytes(store.StatePath); unexpected[1] ^= 1; File.WriteAllBytes(store.StatePath, unexpected);
                Reject(delegate { store.TickOnline(new[] { Alice }, 1); }, "external disk corruption before tick commit");
                Check(store.Faulted && Same(File.ReadAllBytes(store.StatePath), unexpected), "external edit is preserved and store enters fail closed mode");
                Reject(delegate { store.Find(Alice); }, "faulted store cannot expose stale admission decision");
                Check(Directory.GetFiles(changed, "*.tmp-*").Length == 0, "failed commit cleans staging file");
            }
            string removed = Path.Combine(root, "outside-delete");
            using (var store = new SentenceStore(removed, 46))
            {
                store.SetRegion(true, Region()); Sentence(store, Alice, 60); File.Delete(store.StatePath);
                Reject(delegate { store.RequestRelease(true, Alice); }, "external state deletion before release");
                Check(store.Faulted && !File.Exists(store.StatePath), "external deletion never silently clears or regenerates state");
            }
            string unavailable = Path.Combine(root, "unavailable-file");
            using (var store = new SentenceStore(unavailable, 47))
            {
                Directory.CreateDirectory(store.StatePath);
                Reject(delegate { store.SetRegion(true, Region()); }, "directory at durable state path");
                Check(store.Faulted && Directory.GetFiles(unavailable, "*.tmp-*").Length == 0, "unusable state path fails closed and leaves no staging files");
            }
            string locked = Path.Combine(root, "locked-state");
            using (var store = new SentenceStore(locked, 48))
            {
                store.SetRegion(true, Region()); SentenceState original = Sentence(store, Alice, 60);
                byte[] before = File.ReadAllBytes(store.StatePath);
                // Windows denies File.Replace while a reader has no delete-sharing.
                using (var hold = new FileStream(store.StatePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Reject(delegate { store.TickOnline(new[] { Alice }, 1); }, "atomic replacement unavailable");
                Check(!store.Faulted && store.Find(Alice).RemainingSeconds == 60 && store.Find(Alice).Revision == original.Revision,
                    "failed atomic replacement grants no memory time credit");
                Check(Same(File.ReadAllBytes(store.StatePath), before), "failed replacement preserves prior durable sentence");
                Check(Directory.GetFiles(locked, "*.tmp-*").Length == 0, "replacement failure cleans staging file");
                Check(store.TickOnline(new[] { Alice }, 1).Length == 1 && store.Find(Alice).RemainingSeconds == 59, "transient write failure can retry safely");
            }
        }

        private static byte[] Prefix(byte[] source, int count)
        { var result = new byte[count]; Buffer.BlockCopy(source, 0, result, 0, count); return result; }
        private static byte[] Seal(byte[] body)
        {
            byte[] result = new byte[body.Length + 32]; Buffer.BlockCopy(body, 0, result, 0, body.Length);
            using (var hash = SHA256.Create()) Buffer.BlockCopy(hash.ComputeHash(body), 0, result, body.Length, 32);
            return result;
        }
        private static bool Same(byte[] left, byte[] right)
        { if (left.Length != right.Length) return false; for (int i = 0; i < left.Length; ++i) if (left[i] != right[i]) return false; return true; }
    }
}
