using System;
using ValheimModPack.NordicRadio;
internal static class AudioGainTests
{
    private static int count;
    private static void Check(bool condition, string label) { if (!condition) throw new Exception(label); count++; }
    private static float[] Constant(int count, float value)
    { var data = new float[count]; for (int i = 0; i < data.Length; i++) data[i] = value; return data; }
    private static void Warm(AudioGainProcessor processor, float gain)
    { processor.Process(new float[44100], 1, gain, 44100); }
    public static int Main()
    {
        var processor = new AudioGainProcessor(); Warm(processor, 3);
        float[] quiet = {0.05f, -0.05f, 0, 0.1f}; processor.Process(quiet, 1, 3, 44100);
        Check(Math.Abs(quiet[0] - 0.15) < 0.0001 && Math.Abs(quiet[3] - 0.3) < 0.0001, "quiet PCM amplified threefold");
        Check(quiet[0] == -quiet[1] && quiet[2] == 0, "polarity and silence preserved");
        float[] peaks = {0.31f, 0.4f, 1f, -1f, Single.MaxValue, Single.MinValue}; processor.Process(peaks, 1, 3, 44100);
        Check(peaks[0] < peaks[1] && peaks[1] < peaks[2] && peaks[2] < 1, "soft knee remains monotonic below full scale");
        Check(peaks[2] == -peaks[3] && peaks[4] <= 1 && peaks[5] >= -1, "peak limiter bounded and symmetric");
        float[] malformed = {Single.NaN, Single.PositiveInfinity, Single.NegativeInfinity}; processor.Process(malformed, 1, 3, 44100);
        Check(Array.TrueForAll(malformed, x => x == 0), "nonfinite PCM silenced");
        var unity = new AudioGainProcessor(); float[] dry = {0.25f, -0.25f}; unity.Process(dry, 2, Single.NaN, 0);
        Check(dry[0] == 0.25f && dry[1] == -0.25f, "invalid gain falls back to unity without changing ordinary PCM");
        float[] fullScale = {0, 0.25f, -0.25f, 0.91f, -0.91f, 0.95f, -0.95f, 1, -1, 1.25f, -1.25f, Single.MaxValue, Single.MinValue};
        float[] original = (float[])fullScale.Clone(); unity.Process(fullScale, 1, 1, 44100);
        bool transparent = true; for (int i = 0; i < original.Length; i++) transparent &= fullScale[i] == original[i];
        Check(transparent, "unity gain preserves every finite PCM sample, including full-scale peaks");
        float[] invalidUnity = {Single.NaN, Single.PositiveInfinity, Single.NegativeInfinity}; unity.Process(invalidUnity, 1, 1, 44100);
        Check(Array.TrueForAll(invalidUnity, x => x == 0), "unity bypass still silences nonfinite PCM");
        var returning = new AudioGainProcessor(); Warm(returning, 3);
        float[] beforeBypass = {1}; returning.Process(beforeBypass, 1, 3, 44100);
        float[] toUnity = Constant(44100, 1); returning.Process(toUnity, 1, 1, 44100);
        bool smoothBypass = true; float previous = beforeBypass[0];
        foreach (float sample in toUnity)
        {
            smoothBypass &= sample >= 0 && sample <= 1 && Math.Abs(sample - previous) < 0.0001f;
            previous = sample;
        }
        Check(smoothBypass, "amplified full-scale PCM returns to bypass smoothly without overshoot or a limiter release step");
        Check(toUnity[toUnity.Length - 1] == 1, "unity transition settles into exact bypass");
        var extremeTransition = new AudioGainProcessor(); Warm(extremeTransition, 3);
        float[] extremePair = {Single.MaxValue, Single.MinValue}; extremeTransition.Process(extremePair, 2, 1, 44100);
        Check(PlaybackMath.Finite(extremePair[0]) && PlaybackMath.Finite(extremePair[1]) && extremePair[0] == -extremePair[1],
            "unity crossfade keeps extreme finite PCM finite and stereo symmetric");
        Warm(returning, 3); float[] afterBypass = {0.05f}; returning.Process(afterBypass, 1, 3, 44100);
        Check(Math.Abs(afterBypass[0] - 0.15) < 0.0001, "amplification can be restored after unity bypass");
        var stereo = new AudioGainProcessor(); float[] pair = Constant(2000, 0.1f); stereo.Process(pair, 2, 3, 44100);
        bool equal = true; for (int i = 0; i < pair.Length; i += 2) equal &= pair[i] == pair[i + 1];
        Check(equal, "stereo gain identical per frame");
        Check(pair[0] > 0.1f && pair[0] < 0.101f && pair[1998] > pair[0] && pair[1998] < 0.3f, "gain ramps without a step");
        var continuous = new AudioGainProcessor(); var split = new AudioGainProcessor();
        float[] whole = Constant(4000, 0.1f), first = Constant(2000, 0.1f), second = Constant(2000, 0.1f);
        continuous.Process(whole, 2, 3, 44100); split.Process(first, 2, 3, 44100); split.Process(second, 2, 3, 44100);
        bool same = true; for (int i = 0; i < 2000; i++) same &= whole[i] == first[i] && whole[2000+i] == second[i];
        Check(same, "audio block boundaries do not change gain envelope");
        var mono = new AudioGainProcessor(); float[] monoData = Constant(1000, 0.1f); mono.Process(monoData, 1, 3, 44100);
        Check(monoData[999] == pair[1998], "gain ramp independent of channel count");
        float[] invalidFrame = {0.1f}; processor.Process(invalidFrame, 2, 3, 44100);
        Check(invalidFrame[0] == 0.1f, "malformed frame left untouched");
        float[] fade = Constant(44100, 0.1f); processor.Process(fade, 1, 0, 44100);
        Check(fade[fade.Length - 1] < 0.0001f, "gain can return smoothly to silence");
        Warm(processor, 3); float[] restored = {0.05f}; processor.Process(restored, 1, 3, 44100);
        Check(Math.Abs(restored[0] - 0.15) < 0.0001, "processor recovers after malformed samples and silence");
        Console.WriteLine("OK: " + count + " audio gain checks."); return 0;
    }
}
