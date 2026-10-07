using System;
using UnityEngine;

namespace ValheimModPack.PartyPrison
{
    public static partial class ArenaBuilder
    {
        public const string MobSentenceKey = "VMP_PP_MobSentence", MobRunRevisionKey = "VMP_PP_MobRunRevision", MobWaveSerialKey = "VMP_PP_MobWaveSerial";
        private const string WaveTokenKey = "VMP_PP_WaveToken", WaveRevisionKey = "VMP_PP_WaveRevision",
            WaveCompletedKey = "VMP_PP_WaveCompleted", WaveLastKey = "VMP_PP_WaveLast", WaveActiveKey = "VMP_PP_WaveActive",
            WaveExpectedKey = "VMP_PP_WaveExpected", WaveSpawnedKey = "VMP_PP_WaveSpawned";
        private static ZDO waveProgressKit;
        private static ZDOMan waveProgressManager;
        private static string waveProgressToken = "";
        private static int waveProgressRevision;

        private static ArenaWaveProgress ReadWaveProgressZdo(ZDO kit)
        {
            var state = new ArenaWaveProgress {
                Completed = kit.GetInt(WaveCompletedKey, 0), LastSerial = kit.GetLong(WaveLastKey, 0),
                ActiveSerial = kit.GetLong(WaveActiveKey, 0), Expected = kit.GetInt(WaveExpectedKey, 0), Spawned = kit.GetInt(WaveSpawnedKey, 0)
            };
            ArenaWaveProgressionPolicy.Validate(state); return state;
        }
        private static void WriteWaveProgressZdo(ZDO kit, ArenaWaveProgress state)
        {
            ArenaWaveProgressionPolicy.Validate(state);
            kit.Set(WaveCompletedKey, state.Completed); kit.Set(WaveLastKey, state.LastSerial);
            kit.Set(WaveActiveKey, state.ActiveSerial); kit.Set(WaveExpectedKey, state.Expected); kit.Set(WaveSpawnedKey, state.Spawned);
            // Native world autosaves persist this alongside the mob ZDOs. Do
            // not stall the whole server with a world save at every wave.
            if (ZDOMan.instance != null) ZDOMan.instance.ForceSendZDO(kit.m_uid);
        }
        private static bool WaveRunMatches(ZDO kit, string token, int revision)
        { return kit != null && kit.GetString(WaveTokenKey, "") == token && kit.GetInt(WaveRevisionKey, 0) == revision; }

        public static ArenaWaveProgress GetWaveProgress(PrisonRegion region, string token, int revision)
        {
            RequireHost(); CustodyStore.Token(token);
            if (revision < 1) throw new ArgumentOutOfRangeException("revision");
            ZDO kit = GetKitZdo(region);
            if (kit == null || kit.GetString(KitTokenKey, "") != token || kit.GetInt(KitRevisionKey, 0) != revision)
                throw new InvalidOperationException("Сундук текущего набора ещё не загружен.");
            if (!WaveRunMatches(kit, token, revision)) {
                kit.Set(WaveTokenKey, token); kit.Set(WaveRevisionKey, revision);
                WriteWaveProgressZdo(kit, new ArenaWaveProgress());
            }
            ArenaWaveProgress state = ReadWaveProgressZdo(kit);
            bool sessionChanged = waveProgressManager != ZDOMan.instance || !ReferenceEquals(waveProgressKit, kit)
                || waveProgressToken != token || waveProgressRevision != revision;
            if (sessionChanged && state.ActiveSerial != 0 && state.Spawned < state.Expected
                && !HasPendingWave(region, token, revision, state.ActiveSerial)) {
                // An in-memory spawn queue cannot survive a host restart. A
                // partial saved wave is abandoned, with no completed-wave credit.
                CleanupWaveMobs(region, token, revision, state.ActiveSerial);
                state = ArenaWaveProgressionPolicy.Resume(state); WriteWaveProgressZdo(kit, state);
            }
            waveProgressManager = ZDOMan.instance; waveProgressKit = kit; waveProgressToken = token; waveProgressRevision = revision;
            return state;
        }
        private static bool HasPendingWave(PrisonRegion region, string token, int revision, long serial)
        {
            return pendingWave != null && pendingWave.Manager == ZDOMan.instance && SameRegion(pendingWave.Region, region)
                && pendingWave.SentenceToken == token && pendingWave.RunRevision == revision && pendingWave.Serial == serial;
        }
        private static bool IsCurrentWaveMob(ZDO mob, string token, int revision, long serial)
        {
            return mob != null && mob.GetBool(MobKey, false) && mob.GetString(MobSentenceKey, "") == token
                && mob.GetInt(MobRunRevisionKey, 0) == revision && mob.GetLong(MobWaveSerialKey, 0) == serial;
        }
        private static bool IsLiveCurrentWaveMob(ZDO mob, string token, int revision, long serial)
        { return IsCurrentWaveMob(mob, token, revision, serial) && !mob.GetBool(ZDOVars.s_dead, false) && mob.GetFloat(ZDOVars.s_health, 1f) > 0f; }
        private static void CleanupWaveMobs(PrisonRegion region, string token, int revision, long serial)
        {
            foreach (ZDO mob in TaggedRegionObjects(MobKey, region))
                if (IsCurrentWaveMob(mob, token, revision, serial)) DestroyWorldObject(mob);
        }
        public static bool CompleteWaveIfDefeated(PrisonRegion region, string token, int revision)
        {
            ArenaWaveProgress state = GetWaveProgress(region, token, revision);
            if (state.ActiveSerial == 0 || state.Spawned != state.Expected || HasPendingWave(region, token, revision, state.ActiveSerial)) return false;
            foreach (ZDO mob in TaggedRegionObjects(MobKey, region))
                if (IsLiveCurrentWaveMob(mob, token, revision, state.ActiveSerial)) return false;
            ArenaWaveProgress next = ArenaWaveProgressionPolicy.Complete(state, 0, false);
            WriteWaveProgressZdo(waveProgressKit, next); return true;
        }
        private static void RecordWaveSpawned(WaveRequest wave)
        {
            if (wave.Serial == 0) return;
            if (wave.Manager != ZDOMan.instance || !WaveRunMatches(wave.Kit, wave.SentenceToken, wave.RunRevision))
                throw new InvalidOperationException("Arena wave identity changed during spawning.");
            ArenaWaveProgress state = ReadWaveProgressZdo(wave.Kit);
            if (state.ActiveSerial != wave.Serial) throw new InvalidOperationException("Arena wave was cancelled during spawning.");
            WriteWaveProgressZdo(wave.Kit, ArenaWaveProgressionPolicy.SpawnOne(state));
        }
        private static void AbortSpawnWave(WaveRequest wave)
        {
            if (wave.Serial == 0 || wave.Manager != ZDOMan.instance) return;
            if (WaveRunMatches(wave.Kit, wave.SentenceToken, wave.RunRevision)) {
                ArenaWaveProgress state = ReadWaveProgressZdo(wave.Kit);
                if (state.ActiveSerial == wave.Serial) WriteWaveProgressZdo(wave.Kit, ArenaWaveProgressionPolicy.Cancel(state));
            }
            CleanupWaveMobs(wave.Region, wave.SentenceToken, wave.RunRevision, wave.Serial);
        }
        private static void CancelActiveWave(PrisonRegion region)
        {
            ZDO kit = region == null ? waveProgressManager == ZDOMan.instance ? waveProgressKit : null : GetKitZdo(region);
            if (kit == null || kit.GetString(WaveTokenKey, "").Length == 0) return;
            ArenaWaveProgress state = ReadWaveProgressZdo(kit);
            if (state.ActiveSerial != 0) WriteWaveProgressZdo(kit, ArenaWaveProgressionPolicy.Cancel(state));
        }
        private static void ResetWaveProgressRuntime()
        { waveProgressKit = null; waveProgressManager = null; waveProgressToken = ""; waveProgressRevision = 0; }

        public static bool AreAlliedPrisonMobs(Character left, Character right)
        {
            if (left == null || right == null || left is Player || right is Player) return false;
            ZNetView a = left.GetComponent<ZNetView>(), b = right.GetComponent<ZNetView>();
            if (a == null || b == null || !a.IsValid() || !b.IsValid()) return false;
            ZDO first = a.GetZDO(), second = b.GetZDO();
            if (!first.GetBool(MobKey, false) || !second.GetBool(MobKey, false)) return false;
            string sentence = first.GetString(MobSentenceKey, "");
            return sentence.Length == 32 && sentence == second.GetString(MobSentenceKey, "");
        }
    }
}
