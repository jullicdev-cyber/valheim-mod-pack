using System;
namespace ValheimModPack.NordicRadio
{
    // Pure DSP. Owned exclusively by one audio thread; no Unity calls or allocations.
    public sealed class AudioGainProcessor
    {
        private float currentGain = 1;
        public void Process(float[] data, int channels, float targetGain, int sampleRate)
        {
            if (data == null || channels <= 0 || data.Length % channels != 0) return;
            if (!PlaybackMath.Finite(targetGain)) targetGain = 1;
            targetGain = Math.Max(0, Math.Min(6, targetGain));
            sampleRate = Math.Max(8000, Math.Min(192000, sampleRate));
            // 20 ms exponential smoothing. Advance once per frame, not channel.
            float smoothing = (float)(1 - Math.Exp(-1.0 / (sampleRate * 0.02)));
            for (int frame = 0; frame < data.Length; frame += channels)
            {
                currentGain += (targetGain - currentGain) * smoothing;
                for (int channel = 0; channel < channels; channel++)
                {
                    float value = data[frame + channel];
                    data[frame + channel] = PlaybackMath.Finite(value) ? Limit((double)value * currentGain) : 0;
                }
            }
        }
        private static float Limit(double value)
        {
            // Keep ordinary samples linear, round only peaks above -0.92 dBFS.
            // This avoids hard digital clipping for already loud user MP3s.
            double magnitude = Math.Abs(value);
            if (magnitude <= 0.9) return (float)value;
            double excess = magnitude - 0.9;
            float limited = (float)(0.9 + 0.1 * (excess / (0.1 + excess)));
            return value < 0 ? -limited : limited;
        }
    }
}
