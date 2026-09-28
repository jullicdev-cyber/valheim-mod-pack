using System;
namespace ValheimModPack.NordicRadio
{
    // Independent of Unity so timeline edge cases are testable without the game.
    public static class PlaybackMath
    {
        public static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        public static double Position(double now, double startedAt, float offset, bool playing)
        {
            if (!Finite(now) || !Finite(startedAt) || !Finite(offset)) return 0;
            return Math.Max(0, offset + (playing ? Math.Max(0, now - startedAt) : 0));
        }
        public static bool NeedsSeek(double actual, double expected, double length)
        {
            return Finite(actual) && Finite(expected) && Finite(length) && length > 0
                && expected < length && Math.Abs(actual - expected) > 0.4;
        }
        public static float SeekPosition(double expected, float length)
        {
            if (!Finite(expected) || !Finite(length) || length <= 0.1f) return 0;
            return (float)Math.Max(0, Math.Min(expected, length - 0.05f));
        }
        public static float Attenuation(double distance, double near, double far)
        {
            if (!Finite(distance) || !Finite(near) || !Finite(far) || near <= 0 || far <= near || distance >= far) return 0;
            if (distance <= near) return 1;
            double t = (distance - near) / (far - near);
            return (float)(near / distance * (1 - t * t));
        }
        public static int SeekSample(double expected, int frequency, int samples)
        {
            if (!Finite(expected) || frequency <= 0 || samples <= 0) return 0;
            return (int)Math.Max(0, Math.Min(samples - 1.0, Math.Floor(expected * frequency)));
        }
    }
}
