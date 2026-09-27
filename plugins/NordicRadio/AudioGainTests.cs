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
