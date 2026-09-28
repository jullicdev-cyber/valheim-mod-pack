using System;
using System.IO;
using UnityEngine;

namespace ValheimModPack.NordicRadio
{
    // Owns only the decoder transport. A seek is never exposed to the listener until it has settled.
    internal sealed class RadioPlayback
    {
        private readonly AudioSource source;
        private readonly RadioGain gain;
        private readonly float bufferSeconds;
        private bool paused, seeking;
        private bool haveTimeline;
        private double lastExpected;
        private float lastTick;
        private float startedSeek, lastSeek, nextDrift;
        private int callbacksAtSeek, alignedFrames;

        internal RadioPlayback(AudioSource source, RadioGain gain)
        {
            this.source = source; this.gain = gain;
            int size, count; AudioSettings.GetDSPBufferSize(out size, out count);
            bufferSeconds = Math.Max(0.05f, Math.Min(0.5f, (float)size * count / Math.Max(8000, AudioSettings.outputSampleRate) + 0.02f));
            Silence();
        }
        internal void Pause()
        {
            Silence();
            if (!paused) source.Pause();
            paused = true; seeking = false; haveTimeline = false;
        }
        internal void Stop()
        {
            Silence(); source.Stop(); paused = seeking = haveTimeline = false; alignedFrames = 0;
        }
        private void Silence() { gain.Silenced = true; source.volume = 0; }
        private double Position { get { return source.clip == null ? 0 : (double)source.timeSamples / Math.Max(1, source.clip.frequency); } }
        private void Seek(double expected)
        {
            // Play/UnPause MUST precede seeking for the streaming MP3 decoder; its startup can reset a prior seek.
            source.timeSamples = PlaybackMath.SeekSample(expected, source.clip.frequency, source.clip.samples);
            lastSeek = Time.unscaledTime; callbacksAtSeek = gain.CallbackCount; alignedFrames = 0;
        }
        private void Begin(double expected)
        {
            Silence();
            if (paused) source.UnPause();
            // UnPause cannot restart a source that already reached its end before the pause arrived.
            if (!source.isPlaying) source.Play();
            paused = false; seeking = true; startedSeek = Time.unscaledTime;
            Seek(expected);
        }
        internal bool Tick(double expected)
        {
            if (source.clip == null || !PlaybackMath.Finite(expected) || expected < 0 || expected >= source.clip.length)
            { Stop(); return false; }
            float now = Time.unscaledTime;
            // A host restart of the SAME track does not replace the clip. React to a timeline
            // discontinuity immediately instead of playing the old segment for up to two seconds.
            bool jumped = haveTimeline && Math.Abs((expected - lastExpected) - (now - lastTick)) > 0.4;
            haveTimeline = true; lastExpected = expected; lastTick = now;
            if (!seeking && (paused || !source.isPlaying || jumped)) Begin(expected);
            else if (!seeking && now >= nextDrift)
            {
                nextDrift = now + 2;
                if (PlaybackMath.NeedsSeek(Position, expected, source.clip.length)) Begin(expected);
            }
            if (!seeking) return true;
            if (now - startedSeek > 5)
            { Stop(); throw new IOException("MP3 decoder did not reach the requested playback position"); }
            bool aligned = source.isPlaying && Math.Abs(Position - expected) <= 0.15;
            alignedFrames = aligned ? alignedFrames + 1 : 0;
            // Account for queued DSP buffers as well as the main-thread cursor. PCM remains gated throughout.
            if (alignedFrames >= 2 && now - lastSeek >= bufferSeconds && gain.CallbackCount - callbacksAtSeek >= 2)
            {
                seeking = false; nextDrift = now + 2; gain.Silenced = false; return true;
            }
            if (!aligned && now - lastSeek >= Math.Max(0.2f, bufferSeconds)) Seek(expected);
            return false;
        }
    }
}
