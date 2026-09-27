using System;
using ValheimModPack.NordicRadio;
internal static class PlaybackTests
{
    private static int count;
    private static void Check(bool condition, string label) { if (!condition) throw new Exception(label); count++; }
    public static int Main()
    {
        Check(PlaybackMath.Position(130, 100, 0, true) == 30, "late join starts at current host position");
        Check(PlaybackMath.Position(130, 100, 8, true) == 38, "resume includes paused position");
        Check(PlaybackMath.Position(130, 100, 8, false) == 8, "paused position does not advance");
        Check(PlaybackMath.Position(99, 100, 8, true) == 8, "future timestamp does not seek negative");
        Check(PlaybackMath.Position(Double.NaN, 0, 0, true) == 0, "invalid network time");
        Check(PlaybackMath.Position(20, Double.PositiveInfinity, 0, true) == 0, "invalid start time");
        Check(PlaybackMath.Position(20, 10, Single.NaN, true) == 0, "invalid offset");
        Check(!PlaybackMath.NeedsSeek(30.1, 30.2, 100), "small jitter must not stutter playback");
        Check(PlaybackMath.NeedsSeek(20, 30, 100), "significant drift corrects");
        Check(!PlaybackMath.NeedsSeek(99, 102, 100), "finished clip waits for authoritative next track");
        Check(!PlaybackMath.NeedsSeek(0, Double.NaN, 100), "invalid seek rejected");
        Check(PlaybackMath.SeekPosition(-4, 100) == 0, "negative seek clamped");
        Check(PlaybackMath.SeekPosition(200, 100) < 100, "seek remains inside decoder range");
        Check(PlaybackMath.SeekPosition(20, 100) == 20, "valid seek preserved");
        Check(PlaybackMath.SeekPosition(20, 0) == 0, "empty clip handled");
        Check(PlaybackMath.Attenuation(0, 2.5, 35) == 1, "near source has full volume");
        Check(PlaybackMath.Attenuation(2.5, 2.5, 35) == 1, "full-volume boundary");
        Check(PlaybackMath.Attenuation(35, 2.5, 35) == 0, "finite audible radius");
        Check(PlaybackMath.Attenuation(100, 2.5, 35) == 0, "distant source is silent");
        Check(PlaybackMath.Attenuation(5, 2.5, 35) > PlaybackMath.Attenuation(15, 2.5, 35), "distance reduces volume");
        Check(PlaybackMath.Attenuation(Double.NaN, 2.5, 35) == 0, "invalid distance silenced");
        Check(PlaybackMath.Attenuation(3, 5, 5) == 0, "invalid radius silenced");
        Check(PlaybackMath.Attenuation(2.5, 2.5, 100) == 1 && PlaybackMath.Attenuation(5, 2.5, 100) < 0.5f, "original nearby distance effect preserved");
        Check(PlaybackMath.Attenuation(35, 2.5, 100) > 0 && PlaybackMath.Attenuation(95, 2.5, 100) > 0, "new radius audible beyond old boundary");
        Check(PlaybackMath.Attenuation(100, 2.5, 100) == 0 && PlaybackMath.Attenuation(120, 2.5, 100) == 0, "silence at and beyond 100 metres");
        Check(PlaybackMath.Attenuation(99.999, 2.5, 100) < 0.00001 && PlaybackMath.Attenuation(2.501, 2.5, 100) > 0.999, "continuous near and far transitions");
        bool decreases = true;
        for (int metre = 3; metre <= 100; metre++) decreases &= PlaybackMath.Attenuation(metre, 2.5, 100) < PlaybackMath.Attenuation(metre - 1, 2.5, 100);
        Check(decreases, "extended range still strictly fades with distance");
        Console.WriteLine("OK: " + count + " playback timeline checks.");
        return 0;
    }
}
