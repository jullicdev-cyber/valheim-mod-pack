using System;
using ValheimModPack.PartyPrison;

internal static class WaveProgressionPolicyTests
{
    private static int checks;
    private static void Check(bool value, string label)
    { ++checks; if (!value) throw new Exception(label); }
    private static void Reject<T>(Action action, string label) where T : Exception
    {
        bool rejected = false;
        try { action(); } catch (T) { rejected = true; }
        Check(rejected, label);
    }
    private static bool Same(ArenaWaveProgress left, ArenaWaveProgress right)
    {
        return left.Completed == right.Completed && left.Expected == right.Expected && left.Spawned == right.Spawned
            && left.LastSerial == right.LastSerial && left.ActiveSerial == right.ActiveSerial;
    }
    private static ArenaWaveProgress SpawnedWave(ArenaWaveProgress prior, int count)
    {
        ArenaWaveProgress next = ArenaWaveProgressionPolicy.Start(prior, count);
        for (int i = 0; i < count; ++i) next = ArenaWaveProgressionPolicy.SpawnOne(next);
        return next;
    }

    private static void GradeBoundaries()
    {
        for (int baseline = 0; baseline < 3; ++baseline)
            for (int completed = 0; completed <= 20; ++completed) {
                int expected = Math.Min(2, baseline + completed / 4);
                Check(ArenaWaveProgressionPolicy.Difficulty(baseline, completed) == expected,
                    "difficulty changes after four completed waves and caps at hard");
                Check(ArenaWaveProgressionPolicy.WavesUntilUpgrade(baseline, completed)
                    == (expected == 2 ? 0 : 4 - completed % 4), "remaining waves reset on an upgrade and stop at hard");
            }
        Check(ArenaWaveProgressionPolicy.Difficulty(0, 3) == 0 && ArenaWaveProgressionPolicy.Difficulty(0, 4) == 1,
            "the fifth wave follows the easy to medium boundary");
        Check(ArenaWaveProgressionPolicy.Difficulty(0, 7) == 1 && ArenaWaveProgressionPolicy.Difficulty(0, 8) == 2,
            "the ninth wave follows the medium to hard boundary");
        Check(ArenaWaveProgressionPolicy.Difficulty(1, 4) == 2 && ArenaWaveProgressionPolicy.Difficulty(2, 0) == 2,
            "a medium or hard manual baseline has the correct remaining progression");
        for (int baseline = 0; baseline < 3; ++baseline) {
            Check(ArenaWaveProgressionPolicy.Difficulty(baseline, Int32.MaxValue) == 2,
                "large completion counts cannot overflow difficulty");
            Check(ArenaWaveProgressionPolicy.WavesUntilUpgrade(baseline, Int32.MaxValue) == 0,
                "large completion counts stay capped at hard");
        }
        Reject<ArgumentException>(() => ArenaWaveProgressionPolicy.Difficulty(-1, 0), "reject negative baseline");
        Reject<ArgumentException>(() => ArenaWaveProgressionPolicy.Difficulty(3, 0), "reject unavailable baseline");
        Reject<ArgumentException>(() => ArenaWaveProgressionPolicy.Difficulty(0, -1), "reject negative completed counter");
        Reject<ArgumentException>(() => ArenaWaveProgressionPolicy.WavesUntilUpgrade(3, 0), "upgrade countdown validates baseline");
        Reject<ArgumentException>(() => ArenaWaveProgressionPolicy.WavesUntilUpgrade(0, -1), "upgrade countdown validates counter");
    }

    private static void SuccessfulWaves()
    {
        ArenaWaveProgress progress = new ArenaWaveProgress();
        for (int wave = 1; wave <= 20; ++wave) {
            ArenaWaveProgress before = progress.Copy();
            int grade = ArenaWaveProgressionPolicy.Difficulty(0, progress.Completed);
            int expected = grade + 2;
            ArenaWaveProgress started = ArenaWaveProgressionPolicy.Start(progress, expected);
            Check(Same(progress, before), "starting a wave cannot mutate the published prior state");
            Check(!ReferenceEquals(progress, started) && started.ActiveSerial == wave && started.LastSerial == wave
                && started.Expected == expected && started.Spawned == 0 && started.Completed == wave - 1,
                "new wave serial and expected count are published independently");
            Check(ArenaWaveProgressionPolicy.Difficulty(0, started.Completed) == (wave <= 4 ? 0 : wave <= 8 ? 1 : 2),
                "each queued wave uses the correct automatic difficulty");
            for (int mob = 0; mob < expected; ++mob) {
                before = started.Copy();
                ArenaWaveProgress next = ArenaWaveProgressionPolicy.SpawnOne(started);
                Check(Same(started, before) && !ReferenceEquals(started, next) && next.Spawned == mob + 1,
                    "one successful native spawn advances a detached state");
                started = next;
                if (started.Spawned < expected) {
                    ArenaWaveProgress partial = ArenaWaveProgressionPolicy.Complete(started, 0, false);
                    Check(Same(partial, started), "killed early spawns do not finish a partially created wave");
                }
            }
            Reject<InvalidOperationException>(() => ArenaWaveProgressionPolicy.SpawnOne(started), "completed spawn queue cannot create an extra mob");
            Check(Same(ArenaWaveProgressionPolicy.Complete(started, 1, false), started), "one living wave mob prevents a completion credit");
            Check(Same(ArenaWaveProgressionPolicy.Complete(started, 0, true), started), "a pending queue prevents a completion credit");
            before = started.Copy();
            progress = ArenaWaveProgressionPolicy.Complete(started, 0, false);
            Check(Same(started, before) && progress.Completed == wave && progress.ActiveSerial == 0
                && progress.LastSerial == wave && progress.Expected == 0 && progress.Spawned == 0,
                "a fully spawned defeated wave earns exactly one credit and becomes idle");
            ArenaWaveProgress duplicate = ArenaWaveProgressionPolicy.Complete(progress, 0, false);
            Check(!ReferenceEquals(duplicate, progress) && Same(duplicate, progress), "repeated host ticks cannot count the same wave again");
        }
    }

    private static void DefeatAndReconnect()
    {
        ArenaWaveProgress history = new ArenaWaveProgress { Completed = 3, LastSerial = 5 };
        for (int expected = 1; expected <= ArenaWaveProgressionPolicy.MaximumMobs; ++expected)
            for (int spawned = 0; spawned <= expected; ++spawned) {
                ArenaWaveProgress active = ArenaWaveProgressionPolicy.Start(history, expected);
                for (int i = 0; i < spawned; ++i) active = ArenaWaveProgressionPolicy.SpawnOne(active);
                ArenaWaveProgress before = active.Copy();
                ArenaWaveProgress cancelled = ArenaWaveProgressionPolicy.Cancel(active);
                Check(Same(active, before) && cancelled.Completed == 3 && cancelled.LastSerial == 6
                    && cancelled.ActiveSerial == 0 && cancelled.Expected == 0 && cancelled.Spawned == 0,
                    "defeat or forced cleanup preserves previous wins without crediting the cancelled wave");
                Check(Same(ArenaWaveProgressionPolicy.Complete(cancelled, 0, false), cancelled),
                    "zero living mobs after cleanup cannot become a false victory");
                ArenaWaveProgress resumed = ArenaWaveProgressionPolicy.Resume(active);
                Check(!ReferenceEquals(active, resumed) && Same(active, before), "reconnect reconciliation is detached from native saved progress");
                if (spawned == expected) {
                    Check(Same(resumed, active), "a fully spawned saved wave survives reconnect or a cell visit");
                    Check(ArenaWaveProgressionPolicy.Complete(resumed, 1, false).Completed == 3,
                        "a resumed living wave earns no credit");
                    Check(ArenaWaveProgressionPolicy.Complete(resumed, 0, false).Completed == 4,
                        "a fully defeated resumed wave earns one credit");
                }
                else {
                    Check(Same(resumed, cancelled), "an incomplete native spawn queue cannot resume after host restart");
                    Check(ArenaWaveProgressionPolicy.Complete(resumed, 0, false).Completed == 3,
                        "an incomplete saved wave never earns a victory from missing mobs");
                }
                ArenaWaveProgress following = ArenaWaveProgressionPolicy.Start(cancelled, expected);
                Check(following.ActiveSerial == 7 && following.Completed == 3,
                    "retry uses a new serial so orphaned mobs cannot join the replacement wave");
            }
        ArenaWaveProgress idle = ArenaWaveProgressionPolicy.Resume(history);
        Check(Same(idle, history) && !ReferenceEquals(idle, history), "idle saved wins survive reconnect");
        ArenaWaveProgress manualReset = new ArenaWaveProgress { LastSerial = history.LastSerial };
        Check(ArenaWaveProgressionPolicy.Difficulty(0, manualReset.Completed) == 0
            && ArenaWaveProgressionPolicy.Start(manualReset, 2).ActiveSerial == 6,
            "a manual opponent choice resets wins while preserving serial identity within the sentence");
        Check(ArenaWaveProgressionPolicy.Start(new ArenaWaveProgress(), 2).ActiveSerial == 1,
            "a new sentence with its own token can start serial numbering again");
        ArenaWaveProgress saturated = SpawnedWave(new ArenaWaveProgress { Completed = Int32.MaxValue, LastSerial = Int32.MaxValue }, 1);
        ArenaWaveProgress capped = ArenaWaveProgressionPolicy.Complete(saturated, 0, false);
        Check(capped.Completed == Int32.MaxValue && capped.ActiveSerial == 0,
            "completed counter saturates while the wave still becomes idle");
        Check(Same(ArenaWaveProgressionPolicy.Complete(capped, 0, false), capped), "saturated counters retain completion idempotence");
    }

    private static void InvalidStatesAndTransitions()
    {
        var invalid = new[] {
            new ArenaWaveProgress { Completed = -1 },
            new ArenaWaveProgress { LastSerial = -1 },
            new ArenaWaveProgress { ActiveSerial = -1 },
            new ArenaWaveProgress { Expected = -1 },
            new ArenaWaveProgress { Expected = 9 },
            new ArenaWaveProgress { Spawned = -1 },
            new ArenaWaveProgress { Spawned = 1 },
            new ArenaWaveProgress { Expected = 1 },
            new ArenaWaveProgress { Expected = 1, Spawned = 1 },
            new ArenaWaveProgress { ActiveSerial = 1, LastSerial = 1 },
            new ArenaWaveProgress { ActiveSerial = 1, LastSerial = 2, Expected = 1 },
            new ArenaWaveProgress { ActiveSerial = 2, LastSerial = 1, Expected = 1 },
            new ArenaWaveProgress { ActiveSerial = 1, LastSerial = 1, Expected = 1, Spawned = 2 }
        };
        foreach (ArenaWaveProgress state in invalid) {
            Reject<ArgumentException>(() => ArenaWaveProgressionPolicy.Validate(state), "reject a corrupt saved state");
            Reject<ArgumentException>(() => ArenaWaveProgressionPolicy.Start(state, 1), "start validates the complete saved state");
            Reject<ArgumentException>(() => ArenaWaveProgressionPolicy.SpawnOne(state), "native spawn transition validates the saved state");
            Reject<ArgumentException>(() => ArenaWaveProgressionPolicy.Cancel(state), "cancellation validates the saved state");
            Reject<ArgumentException>(() => ArenaWaveProgressionPolicy.Complete(state, 0, false), "completion validates the saved state");
            Reject<ArgumentException>(() => ArenaWaveProgressionPolicy.Resume(state), "resume validates the saved state");
        }
        Reject<ArgumentNullException>(() => ArenaWaveProgressionPolicy.Validate(null), "validate rejects a missing state");
        Reject<ArgumentNullException>(() => ArenaWaveProgressionPolicy.Start(null, 1), "start rejects a missing state");
        Reject<ArgumentNullException>(() => ArenaWaveProgressionPolicy.SpawnOne(null), "spawn rejects a missing state");
        Reject<ArgumentNullException>(() => ArenaWaveProgressionPolicy.Cancel(null), "cancel rejects a missing state");
        Reject<ArgumentNullException>(() => ArenaWaveProgressionPolicy.Complete(null, 0, false), "completion rejects a missing state");
        Reject<ArgumentNullException>(() => ArenaWaveProgressionPolicy.Resume(null), "resume rejects a missing state");
        ArenaWaveProgress idle = new ArenaWaveProgress();
        Reject<ArgumentException>(() => ArenaWaveProgressionPolicy.Start(idle, 0), "reject an empty wave");
        Reject<ArgumentException>(() => ArenaWaveProgressionPolicy.Start(idle, 9), "reject a wave beyond the native mob limit");
        Reject<InvalidOperationException>(() => ArenaWaveProgressionPolicy.SpawnOne(idle), "no mob can spawn outside an active wave");
        Reject<ArgumentException>(() => ArenaWaveProgressionPolicy.Complete(idle, -1, false), "reject a negative live mob count even when idle");
        ArenaWaveProgress active = ArenaWaveProgressionPolicy.Start(idle, 1);
        Reject<InvalidOperationException>(() => ArenaWaveProgressionPolicy.Start(active, 1), "cannot overwrite a live wave");
        var exhausted = new ArenaWaveProgress { LastSerial = Int64.MaxValue };
        Reject<InvalidOperationException>(() => ArenaWaveProgressionPolicy.Start(exhausted, 1), "serial overflow never wraps into an old wave identity");
        Check(exhausted.LastSerial == Int64.MaxValue && exhausted.ActiveSerial == 0, "serial exhaustion leaves saved progress unchanged");
    }

    public static int Main()
    {
        try {
            GradeBoundaries(); SuccessfulWaves(); DefeatAndReconnect(); InvalidStatesAndTransitions();
            Console.WriteLine("PASS: " + checks + " arena wave progression policy checks."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
