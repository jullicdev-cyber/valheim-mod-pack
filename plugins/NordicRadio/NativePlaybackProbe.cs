// Test-only transport regression using a real streaming MP3 and its decoded PCM.
using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using ValheimModPack.NordicRadio;
namespace ValheimModPack.NordicRadioSmoke
{
    internal static class NativePlaybackProbe
    {
        internal static IEnumerator Run(AudioClip clip, Action<string, bool> complete)
        {
            var holder = new GameObject("NordicRadio.SilentSeekTest");
            var source = holder.AddComponent<AudioSource>();
            source.playOnAwake = false; source.spatialBlend = 0; source.clip = clip;
            var gain = holder.AddComponent<RadioGain>(); gain.Gain = 1;
            var probe = holder.AddComponent<SeekDspProbe>(); probe.SampleRate = AudioSettings.outputSampleRate;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Type type = typeof(Plugin).Assembly.GetType("ValheimModPack.NordicRadio.RadioPlayback", true);
            object transport = Activator.CreateInstance(type, flags, null, new object[] { source, gain }, null);
            MethodInfo tick = type.GetMethod("Tick", flags), pause = type.GetMethod("Pause", flags), stop = type.GetMethod("Stop", flags);
            string failure = null, report = "";
            double offset = 8;
            // The fixture has 220 Hz at 0-6 s, 880 Hz at 6-12 s and 1760 Hz at 12-18 s.
            // Wrong early PCM is detected even when Unity reports the requested cursor.
            for (int phase = 0; phase < 4 && failure == null; phase++)
            {
                if (phase == 3) offset = 14; // large drift while already playing
                probe.ExpectedFrequency = phase == 3 ? 1760 : 880;
                probe.Arm = false; probe.BadTone = false; probe.NonzeroBuffers = 0;
                float start = Time.unscaledTime, readyAt = -1;
                while (Time.unscaledTime - start < 7 && (readyAt < 0 || Time.unscaledTime - readyAt < .6f))
                {
                    try
                    {
                        bool ready = (bool)tick.Invoke(transport, new object[] { offset + Time.unscaledTime - start });
                        // Before the gate opens the post-gain PCM must be zero; after it opens it must be the target tone.
                        probe.Arm = true;
                        if (ready) { source.volume = 1; if (readyAt < 0) readyAt = Time.unscaledTime; }
                        if (probe.BadTone) throw new Exception("Wrong track segment reached PCM after seek/resume, phase=" + phase + ", measuredHz=" + probe.LastFrequency);
                    }
                    catch (Exception error) { failure = error.ToString(); break; }
                    yield return null;
                }
                if (failure == null && (readyAt < 0 || probe.NonzeroBuffers < 4))
                    failure = "No audible target PCM, phase=" + phase + ", buffers=" + probe.NonzeroBuffers;
                report += "phase=" + phase + " cursor=" + source.time + " target=" + (offset + Time.unscaledTime - start)
                    + " buffers=" + probe.NonzeroBuffers + " measuredHz=" + probe.LastFrequency + "\n";
                if (failure != null) break;
                offset += Time.unscaledTime - start;
                if (phase < 2)
                {
                    pause.Invoke(transport, null);
                    int sample = source.timeSamples;
                    yield return new WaitForSecondsRealtime(phase == 0 ? .7f : .03f);
                    if (source.isPlaying || source.clip != clip || Math.Abs(source.timeSamples - sample) > clip.frequency / 10)
                        failure = "Pause failed to retain clip or freeze decoder";
                }
                else if (phase == 2)
                {
                    // Let the production two-second drift interval elapse before changing the target.
                    probe.Arm = false;
                    yield return new WaitForSecondsRealtime(2.1f);
                }
            }
            probe.Arm = false; stop.Invoke(transport, null);
            UnityEngine.Object.Destroy(holder);
            complete((failure == null ? "PASS native MP3 transport: cold seek, pause/resume, rapid resume, drift correction. No wrong segment reached PCM.\n" : "FAIL native MP3 transport: " + failure + "\n") + report, failure == null);
        }
    }
    public sealed class SeekDspProbe : MonoBehaviour
    {
        internal int SampleRate;
        internal volatile int ExpectedFrequency, NonzeroBuffers;
        internal volatile bool Arm, BadTone;
        internal volatile float LastFrequency;
        private void OnAudioFilterRead(float[] data, int channels)
        {
            int crossings = 0; float peak = 0, previous = 0;
            for (int i = 0; i < data.Length; i += channels)
            {
                float value = data[i]; peak = Math.Max(peak, Math.Abs(value));
                if (value > 0 && previous <= 0) crossings++;
                previous = value;
            }
            if (Arm && peak > .02f)
            {
                float frequency = (float)crossings * SampleRate / (data.Length / channels);
                LastFrequency = frequency; NonzeroBuffers++;
                if (Math.Abs(frequency - ExpectedFrequency) > ExpectedFrequency * .25f) BadTone = true;
            }
            // Native test never sends these tones to the speakers.
            Array.Clear(data, 0, data.Length);
        }
    }
}
