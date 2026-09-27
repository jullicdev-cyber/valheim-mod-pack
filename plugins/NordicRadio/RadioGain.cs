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
        public float Gain { set { gain = value; } }
        private void Awake() { sampleRate = AudioSettings.outputSampleRate; }
        private void OnEnable() { AudioSettings.OnAudioConfigurationChanged += ConfigurationChanged; }
        private void OnDisable() { AudioSettings.OnAudioConfigurationChanged -= ConfigurationChanged; }
        private void ConfigurationChanged(bool deviceChanged) { sampleRate = AudioSettings.outputSampleRate; }
        private void OnAudioFilterRead(float[] data, int channels)
        {
            processor.Process(data, channels, gain, sampleRate);
        }
    }
}
