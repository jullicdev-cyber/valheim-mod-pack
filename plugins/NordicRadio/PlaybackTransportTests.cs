// Behavioral decoder double: Play resets its cursor and seeking can be delayed or ignored.
using System;
using System.IO;
using ValheimModPack.NordicRadio;
using UnityEngine;

namespace UnityEngine
{
    public static class Time { public static float unscaledTime; }
    public static class AudioSettings
    {
        public static int outputSampleRate = 48000;
        public static void GetDSPBufferSize(out int size, out int count) { size = 1024; count = 4; }
    }
    public sealed class AudioClip { public int frequency = 48000, samples = 4800000; public float length = 100; }
    public sealed class AudioSource
    {
        public AudioClip clip = new AudioClip();
        public bool isPlaying, IgnoreSeek, UnpauseUnavailable;
        public float volume = 1;
        public int Plays, Resumes, Pauses, Cursor, Pending = -1;
        public bool DelaySeek;
        public int timeSamples
        {
            get { return Cursor; }
            set { if (!isPlaying || IgnoreSeek) return; if (DelaySeek) Pending = value; else Cursor = value; }
        }
        public void Play() { Plays++; isPlaying = true; Cursor = 0; }
        public void Pause() { Pauses++; isPlaying = false; }
        public void UnPause() { Resumes++; if (!UnpauseUnavailable) isPlaying = true; }
        public void Stop() { isPlaying = false; Cursor = 0; }
    }
}
namespace ValheimModPack.NordicRadio
{
    public sealed class RadioGain { public bool Silenced; public int CallbackCount; }
}
internal static class PlaybackTransportTests
{
    private static int checks;
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
    private static void Frame(AudioSource source, RadioGain gain, float seconds)
    {
        Time.unscaledTime += seconds;
        if (source.isPlaying) { source.Cursor += (int)(seconds * source.clip.frequency); gain.CallbackCount++; }
    }
    private static bool Settle(RadioPlayback playback, AudioSource source, RadioGain gain, double offset)
    {
        float start = Time.unscaledTime;
        bool ready = playback.Tick(offset);
        for (int i = 0; i < 20 && !ready; i++)
        { Frame(source, gain, .02f); ready = playback.Tick(offset + Time.unscaledTime - start); }
        return ready;
    }
    public static int Main()
    {
        var source = new AudioSource(); var gain = new RadioGain(); var playback = new RadioPlayback(source, gain);
        AudioClip clip = source.clip;
        Check(gain.Silenced && source.volume == 0, "new transport begins silent");
        Check(!playback.Tick(25), "cold start stays gated");
        Check(source.timeSamples == 1200000 && source.Plays == 1, "seek follows decoder Play reset");
        Frame(source, gain, .02f);
        Check(!playback.Tick(25.02) && gain.Silenced, "one aligned frame is insufficient");
        Check(Settle(playback, source, gain, 25.02), "buffered start eventually becomes audible");
        Check(!gain.Silenced, "PCM gate opens at stable position");
        int before = source.Cursor;
        playback.Pause(); playback.Pause();
        Check(source.Pauses == 1 && !source.isPlaying && gain.Silenced, "repeated pause is idempotent and silent");
        Frame(source, gain, 4);
        Check(source.Cursor == before && ReferenceEquals(clip, source.clip), "pause preserves clip and playback position");
        Check(Settle(playback, source, gain, (double)before / clip.frequency), "resume settles");
        Check(source.Plays == 1 && source.Resumes == 1, "resume reuses paused decoder without restart");
        playback.Pause();
        Check(!playback.Tick(35) && gain.Silenced, "rapid resume stays silent during corrective seek");
        playback.Pause();
        Check(Settle(playback, source, gain, 35), "pause during a pending seek can resume");
        source.Cursor = 0; Frame(source, gain, 3);
        Check(!playback.Tick(40) && gain.Silenced, "large drift is corrected behind PCM gate");
        Check(Settle(playback, source, gain, 40), "drift settles without reopening decoder");
        playback.Stop(); source.DelaySeek = true;
        Check(!playback.Tick(50) && gain.Silenced, "asynchronous seek is gated");
        Frame(source, gain, .1f);
        Check(!playback.Tick(50.1) && gain.Silenced, "incorrect cursor cannot unmute despite callbacks");
        source.Cursor = source.Pending; source.DelaySeek = false;
        Check(Settle(playback, source, gain, 50), "delayed seek can complete");
        playback.Stop(); source.IgnoreSeek = true;
        playback.Tick(60);
        bool timedOut = false;
        try { for (int i = 0; i < 60; i++) { Frame(source, gain, .1f); playback.Tick(60 + i * .1); } }
        catch (IOException) { timedOut = true; }
        Check(timedOut && gain.Silenced && !source.isPlaying, "broken decoder times out without exposing wrong audio");
        source.IgnoreSeek = false;
        Check(!playback.Tick(100) && !source.isPlaying, "end of track does not restart");
        Check(!playback.Tick(Double.NaN) && gain.Silenced, "invalid timeline stays silent");
        Check(!playback.Tick(-1), "negative timeline stays silent");
        playback.Pause(); source.UnpauseUnavailable = true;
        Check(Settle(playback, source, gain, 5), "resume can restart a decoder already stopped before pause arrived");
        playback.Stop(); playback.Tick(10);
        Time.unscaledTime += .2f; source.Cursor = 489600;
        Check(!playback.Tick(10.2) && gain.Silenced, "aligned cursor without DSP callbacks stays gated");
        Time.unscaledTime += .2f; source.Cursor = 499200;
        Check(!playback.Tick(10.4) && gain.Silenced, "stable main-thread cursor alone cannot release PCM gate");
        Check(PlaybackMath.SeekSample(1.5, 48000, 4800000) == 72000, "fractional position uses sample index");
        Check(PlaybackMath.SeekSample(100, 48000, 4800000) == 4799999, "seek cannot exceed last sample");
        Check(PlaybackMath.SeekSample(Double.PositiveInfinity, 48000, 4800000) == 0, "infinite position rejected");
        Console.WriteLine("OK: " + checks + " playback transport checks."); return 0;
    }
}
