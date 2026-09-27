using System;
using HarmonyLib;
using UnityEngine;

namespace ValheimModPack.NordicRadio
{
    // Changes only the live vanilla music source. The user's preferences, music
    // mixer and MusicMan's fade/proximity state remain owned by the game.
    internal sealed class MusicDucking : IDisposable
    {
        private static MusicDucking active;
        private readonly Harmony harmony;
        private AudioSource source;
        private float baseline, applied;
        private bool ownsVolume, disposed;
        internal float CurrentFactor { get; private set; }

        internal MusicDucking()
        {
            if (active != null) throw new InvalidOperationException("Music ducking is already installed.");
            var method = AccessTools.Method(typeof(MusicMan), "UpdateMusic", new[] { typeof(float) });
            var field = AccessTools.Field(typeof(MusicMan), "m_musicSource");
            if (method == null || field == null || field.FieldType != typeof(AudioSource))
                throw new MissingMemberException("Valheim MusicMan audio API has changed.");
            CurrentFactor = 1;
            harmony = new Harmony("valheimmodpack.nordicradio.music-duck");
            active = this;
            try
            {
                harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(MusicDucking), "BeforeMusicUpdate") { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(MusicDucking), "AfterMusicUpdate") { priority = Priority.Last });
            }
            catch
            {
                active = null;
                harmony.UnpatchSelf();
                throw;
            }
        }

        // audibility is the strongest actually playing horn's effective linear
        // gain (including distance, fades, volume, amplification and SFX bus).
        // At gain >= 0.2 it is clearly present; near the radius edge the duck
        // smoothly disappears. Pass zero while downloading or muted.
        internal void Update(float audibility, float nearbyMusicVolume, float deltaTime)
        {
            if (disposed) return;
            audibility = Unit(audibility / 0.2f, 0);
            nearbyMusicVolume = Unit(nearbyMusicVolume, 0.2f);
            float presence = audibility * audibility * (3 - 2 * audibility);
            float target = 1 - (1 - nearbyMusicVolume) * presence;
            float delta = Finite(deltaTime) ? Math.Max(0, Math.Min(0.25f, deltaTime)) : 0;
            // 0.4 seconds to duck from 100% to 20%; 1.6 seconds to restore it.
            float step = delta * (target < CurrentFactor ? 2 : 0.5f);
            CurrentFactor = CurrentFactor < target
                ? Math.Min(target, CurrentFactor + step) : Math.Max(target, CurrentFactor - step);
            if (source == null) { ownsVolume = false; return; }
            RestoreOwnedVolume();
            ApplyFreshVolume(source);
        }

        // Immediate restoration for world exit, disable, initialization failure.
        internal void Reset()
        {
            CurrentFactor = 1;
            RestoreOwnedVolume();
            source = null;
        }

        private static void BeforeMusicUpdate(AudioSource ___m_musicSource)
        {
            if (active == null) return;
            active.RestoreOwnedVolume();
            active.source = ___m_musicSource;
        }

        private static void AfterMusicUpdate(AudioSource ___m_musicSource)
        {
            if (active != null) active.ApplyFreshVolume(___m_musicSource);
        }

        private void RestoreOwnedVolume()
        {
            // An external writer may have changed the source in the meantime.
            // In that case its new value becomes the next baseline; never undo it.
            if (ownsVolume && source != null && Math.Abs(source.volume - applied) <= 0.000001f)
                source.volume = baseline;
            ownsVolume = false;
        }

        private void ApplyFreshVolume(AudioSource musicSource)
        {
            if (source != musicSource) RestoreOwnedVolume();
            source = musicSource;
            if (source == null) return;
            baseline = Unit(source.volume, 0);
            if (CurrentFactor >= 1) { ownsVolume = false; return; }
            applied = baseline * CurrentFactor;
            source.volume = applied;
            ownsVolume = true;
        }

        private static bool Finite(float value) { return !Single.IsNaN(value) && !Single.IsInfinity(value); }
        private static float Unit(float value, float fallback) { return Finite(value) ? Math.Max(0, Math.Min(1, value)) : fallback; }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { Reset(); }
            finally
            {
                if (ReferenceEquals(active, this)) active = null;
                harmony.UnpatchSelf();
            }
        }
    }
}
