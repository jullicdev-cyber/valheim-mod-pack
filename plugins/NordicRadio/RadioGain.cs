using System;
using System.Threading;
using UnityEngine;
namespace ValheimModPack.NordicRadio
{
    // AudioSource.volume cannot exceed one. Amplify just this horn's PCM before
    // Unity applies its volume, spatial direction, and the game's effects mixer.
    public sealed class RadioGain : MonoBehaviour
    {
        private readonly AudioGainProcessor processor = new AudioGainProcessor();
        private volatile float gain = 1;
        private volatile int sampleRate = 44100;
        private volatile bool silenced;
        private int callbacks;
        public float Gain { set { gain = value; } }
        public bool Silenced { get { return silenced; } set { silenced = value; } }
        public int CallbackCount { get { return Interlocked.CompareExchange(ref callbacks, 0, 0); } }
        private void Awake() { sampleRate = AudioSettings.outputSampleRate; }
        private void OnEnable() { AudioSettings.OnAudioConfigurationChanged += ConfigurationChanged; }
        private void OnDisable() { AudioSettings.OnAudioConfigurationChanged -= ConfigurationChanged; }
        private void ConfigurationChanged(bool deviceChanged) { sampleRate = AudioSettings.outputSampleRate; }
        private void OnAudioFilterRead(float[] data, int channels)
        {
            processor.Process(data, channels, gain, sampleRate);
            if (silenced) Array.Clear(data, 0, data.Length);
            Interlocked.Increment(ref callbacks);
        }
    }
}
