using System;

namespace ValheimModPack.PartyPrison
{
    /// <summary>A sentence's saved arena run; live mobs carry its active serial.</summary>
    public sealed class ArenaWaveProgress
    {
        public int Completed, Expected, Spawned;
        public long LastSerial, ActiveSerial;

        public ArenaWaveProgress Copy()
        {
            return new ArenaWaveProgress {
                Completed = Completed, Expected = Expected, Spawned = Spawned,
                LastSerial = LastSerial, ActiveSerial = ActiveSerial
            };
        }
    }

    /// <summary>Transitions are detached copies: native ZDO publication stays host-side.</summary>
    public static class ArenaWaveProgressionPolicy
    {
        public const int WavesPerUpgrade = 4, MaximumMobs = 8;

        public static void Validate(ArenaWaveProgress progress)
        {
            if (progress == null) throw new ArgumentNullException("progress");
            if (progress.Completed < 0 || progress.LastSerial < 0 || progress.ActiveSerial < 0
                || progress.Expected < 0 || progress.Expected > MaximumMobs
                || progress.Spawned < 0 || progress.Spawned > progress.Expected)
                throw new ArgumentException("Invalid arena wave progress.", "progress");
            if (progress.ActiveSerial == 0) {
                if (progress.Expected != 0 || progress.Spawned != 0)
                    throw new ArgumentException("An idle arena run cannot retain pending spawns.", "progress");
            }
            else if (progress.Expected == 0 || progress.ActiveSerial != progress.LastSerial)
                throw new ArgumentException("An active arena wave must use the current serial and a bounded spawn count.", "progress");
        }

        public static int Difficulty(int baseDifficulty, int completed)
        {
            ValidateGrade(baseDifficulty, completed);
            return Math.Min(2, baseDifficulty + completed / WavesPerUpgrade);
        }

        public static int WavesUntilUpgrade(int baseDifficulty, int completed)
        {
            if (Difficulty(baseDifficulty, completed) == 2) return 0;
            return WavesPerUpgrade - completed % WavesPerUpgrade;
        }

        private static void ValidateGrade(int baseDifficulty, int completed)
        {
            if (baseDifficulty < 0 || baseDifficulty > 2) throw new ArgumentOutOfRangeException("baseDifficulty");
            if (completed < 0) throw new ArgumentOutOfRangeException("completed");
        }

        public static ArenaWaveProgress Start(ArenaWaveProgress prior, int expected)
        {
            Validate(prior);
            if (expected < 1 || expected > MaximumMobs) throw new ArgumentOutOfRangeException("expected");
            if (prior.ActiveSerial != 0) throw new InvalidOperationException("An arena wave is already active.");
            if (prior.LastSerial == Int64.MaxValue) throw new InvalidOperationException("The arena wave serial is exhausted.");
            ArenaWaveProgress next = prior.Copy();
            next.LastSerial = prior.LastSerial + 1; next.ActiveSerial = next.LastSerial;
            next.Expected = expected; next.Spawned = 0;
            return next;
        }

        public static ArenaWaveProgress SpawnOne(ArenaWaveProgress prior)
        {
            Validate(prior);
            if (prior.ActiveSerial == 0 || prior.Spawned >= prior.Expected)
                throw new InvalidOperationException("No pending arena mob can be spawned.");
            ArenaWaveProgress next = prior.Copy(); ++next.Spawned;
            return next;
        }

        /// <summary>Defeat and cancelled spawns never erase earlier completed waves.</summary>
        public static ArenaWaveProgress Cancel(ArenaWaveProgress prior)
        {
            Validate(prior);
            ArenaWaveProgress next = prior.Copy();
            next.ActiveSerial = 0; next.Expected = next.Spawned = 0;
            return next;
        }

        /// <summary>Only a completely spawned, defeated wave earns one durable credit.</summary>
        public static ArenaWaveProgress Complete(ArenaWaveProgress prior, int aliveCurrent, bool pendingCurrent)
        {
            Validate(prior);
            if (aliveCurrent < 0) throw new ArgumentOutOfRangeException("aliveCurrent");
            if (prior.ActiveSerial == 0 || prior.Spawned != prior.Expected || pendingCurrent || aliveCurrent != 0)
                return prior.Copy();
            ArenaWaveProgress next = Cancel(prior);
            if (next.Completed < Int32.MaxValue) ++next.Completed;
            return next;
        }

        /// <summary>After reload there is no in-memory queue to finish a partial wave.</summary>
        public static ArenaWaveProgress Resume(ArenaWaveProgress prior)
        {
            Validate(prior);
            return prior.ActiveSerial != 0 && prior.Spawned != prior.Expected ? Cancel(prior) : prior.Copy();
        }
    }
}
