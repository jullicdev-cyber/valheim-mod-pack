using System;
namespace ValheimModPack.InterfaceInputFix
{
    internal static class Tests
    {
        private static int tests;
        private static void Check(bool valid, string label) { tests++; if (!valid) throw new Exception(label); }
        public static int Main()
        {
            int count = 3, changes = 0;
            var lease = new InputLease(value => { count += value ? 1 : -1; changes++; });
            lease.Close(); Check(count == 3 && changes == 0, "closed window owns nothing");
            lease.Show(); Check(lease.Held && count == 4, "one show adds one request");
            lease.Show(); Check(count == 4 && changes == 1, "duplicate show is idempotent");
            lease.Close(); Check(count == 3 && !lease.Held, "one close releases exactly one request");
            lease.Close(); Check(count == 3, "duplicate close preserves foreign requests");
            lease.Show(); lease.ScheduleClose(12); Check(!lease.CloseDue(11), "close preserves two-frame delay");
            Check(lease.CloseDue(12), "close due once"); Check(!lease.CloseDue(12), "queued callback consumed once"); lease.Close();
            lease.Show(); lease.ScheduleClose(20); lease.Show(); Check(!lease.CloseDue(30) && lease.Held, "reopen cancels old delayed hide");
            lease.ScheduleClose(32); lease.ScheduleClose(34); Check(lease.CloseDue(32), "duplicate close does not postpone first request"); lease.Close();
            lease.ScheduleClose(40); Check(!lease.CloseDue(40), "hidden window cannot schedule a close");
            for (int i = 0; i < 100; i++) { lease.Show(); lease.Show(); lease.Close(); lease.Close(); }
            Check(count == 3, "one hundred cycles retain foreign baseline");
            lease.Show(); count = 0; lease.Forget(); lease.Close(); Check(count == 0 && !lease.Held, "actual global GUI reset forgets old ownership without decrement");
            count = 5; lease.Show(); lease.Close(); Check(count == 5, "new GUI baseline retained after reset");
            int failures = 0; var partial = new InputLease(value => { if (value) { failures++; throw new Exception("after increment"); } failures--; });
            try { partial.Show(); } catch { }
            Check(failures == 0 && !partial.Held, "acquire exception balances own partial request");
            var release = new InputLease(value => { if (!value) throw new Exception("after release"); });
            release.Show(); try { release.Close(); } catch { } release.Close(); Check(!release.Held, "release exception does not retain ownership");
            int callbacks = 0; var guard = new ReentryGuard();
            guard.Run(() => { callbacks++; guard.Run(() => callbacks++); }); Check(callbacks == 1, "backpack recursive InventoryGui.Hide suppressed");
            try { guard.Run(() => { throw new Exception("native failure"); }); } catch { }
            guard.Run(() => callbacks++); Check(callbacks == 2, "reentry guard released after exception");
            Console.WriteLine("InterfaceInputFix: " + tests + " actual-source lifecycle assertions passed."); return 0;
        }
    }
}
