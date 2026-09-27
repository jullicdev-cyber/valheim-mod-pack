using System;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
namespace ValheimModPack.NordicRadio
{
    // One local streaming decoder per audible horn; the plugin limits this to eight.
    internal sealed class RadioAudio : IDisposable
    {
        private readonly Plugin plugin;
        private readonly IRadioTarget piece;
        private GameObject emitter;
        private AudioSource source;
        private RadioGain amplifier;
        private AudioClip clip;
        private UnityWebRequest request;
        private string track = "";
        private string failedTrack = "";
        private float nextRetry, nextDrift, requestStarted;
        private bool disposed;
        public bool Audible;
        internal float Audibility
        {
            get { return source != null && source.isPlaying && source.enabled && !source.mute
                ? Mathf.Clamp01(source.volume * plugin.Amplification) : 0; }
        }
        internal RadioAudio(Plugin plugin, IRadioTarget piece) { this.plugin = plugin; this.piece = piece; }
        internal void Tick()
        {
            if (disposed || piece == null || !piece.IsReady) return;
            try { UpdatePlayback(); }
            catch (Exception error)
            {
                failedTrack = track; nextRetry = Time.unscaledTime + 15;
                Stop(); plugin.Report(error);
            }
        }
        private void UpdatePlayback()
        {
            var service = plugin.Service;
            var state = service.GetState(piece.Id);
            bool playing = state != null && state.Playing && !String.IsNullOrEmpty(state.TrackId);
            piece.SetLit(playing);
            bool wanted = playing && Audible && Player.m_localPlayer != null && ZNet.instance != null
                && plugin.PersonalVolume > 0
                && Vector3.Distance(plugin.ListenerPosition, piece.SoundPosition) < plugin.FarDistance;
            if (!wanted)
            {
                if (request != null) CancelRequest();
                if (source != null && source.isPlaying)
                {
                    source.volume = Mathf.MoveTowards(source.volume, 0, Time.unscaledDeltaTime * 2);
                    if (source.volume <= 0.001f) Stop();
                }
                else if (clip != null) Stop();
                return;
            }
            if (failedTrack == state.TrackId && Time.unscaledTime < nextRetry) return;
            service.RequestTrack(state.TrackId);
            if (track != state.TrackId)
            {
                // Finish a short fade before changing clips; use the latest snapshot afterwards.
                if (source != null && source.isPlaying && source.volume > 0.001f)
                { source.volume = Mathf.MoveTowards(source.volume, 0, Time.unscaledDeltaTime * 4); return; }
                Stop(); track = state.TrackId;
            }
            EnsureSource();
            amplifier.Gain = plugin.Amplification;
            source.minDistance = plugin.NearDistance; source.maxDistance = plugin.FarDistance;
            emitter.transform.position = piece.SoundPosition;
            if (clip == null && request == null)
            {
                string path = service.GetTrackPath(track);
                if (path == null) { service.RequestTrack(track); return; }
                request = UnityWebRequestMultimedia.GetAudioClip(new Uri(Path.GetFullPath(path)).AbsoluteUri, AudioType.MPEG);
                ((DownloadHandlerAudioClip)request.downloadHandler).streamAudio = true;
                request.timeout = 30;
                requestStarted = Time.unscaledTime;
                request.SendWebRequest();
            }
            if (request != null)
            {
                if (!request.isDone)
                {
                    if (Time.unscaledTime - requestStarted > 35) throw new TimeoutException("MP3 decoder timed out.");
                    return;
                }
                if (request.result != UnityWebRequest.Result.Success) throw new IOException("Cannot decode MP3: " + request.error);
                clip = DownloadHandlerAudioClip.GetContent(request);
                request.Dispose(); request = null;
                if (clip == null || !PlaybackMath.Finite(clip.length) || clip.length <= 0.1f)
                    throw new IOException("MP3 contains no playable audio.");
                source.clip = clip; source.volume = 0;
                service.SetDuration(track, clip.length);
                failedTrack = "";
            }
            double position = PlaybackMath.Position(ZNet.instance.GetTimeSeconds(), state.StartedAt, state.Offset, state.Playing);
            // At the end, wait for the host's authoritative next-track message.
            if (position >= clip.length) { source.Stop(); return; }
            if (!source.isPlaying)
            {
                source.time = PlaybackMath.SeekPosition(position, clip.length);
                source.Play(); nextDrift = Time.unscaledTime + 2;
            }
            if (Time.unscaledTime >= nextDrift)
            {
                nextDrift = Time.unscaledTime + 2;
                if (PlaybackMath.NeedsSeek(source.time, position, clip.length))
                    source.time = PlaybackMath.SeekPosition(position, clip.length);
            }
            // Explicit distance gain gives a finite, configurable radius. Unity
            // still supplies stereo direction, but does not attenuate it twice.
            float gain = PlaybackMath.Attenuation(Vector3.Distance(plugin.ListenerPosition, piece.SoundPosition), plugin.NearDistance, plugin.FarDistance);
            source.volume = Mathf.MoveTowards(source.volume, Mathf.Clamp01(state.Volume) * plugin.PersonalVolume * gain, Time.unscaledDeltaTime * 2);
        }
        private void EnsureSource()
        {
            if (source != null) return;
            emitter = new GameObject("NordicRadio.Sound");
            emitter.transform.SetParent(plugin.transform, false);
            source = emitter.AddComponent<AudioSource>();
            source.playOnAwake = false; source.loop = false;
            source.spatialBlend = 1; source.dopplerLevel = 0; source.spread = 0;
            source.priority = 128; source.volume = 0;
            amplifier = emitter.AddComponent<RadioGain>();
            amplifier.Gain = plugin.Amplification;
            source.rolloffMode = AudioRolloffMode.Custom;
            source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, AnimationCurve.Linear(0, 1, 1, 1));
            if (AudioMan.instance != null) source.outputAudioMixerGroup = AudioMan.instance.m_ambientMixer;
        }
        private void CancelRequest()
        {
            if (request == null) return;
            try { request.Abort(); } finally { request.Dispose(); request = null; }
        }
        internal void Stop()
        {
            CancelRequest();
            if (source != null) { source.Stop(); source.clip = null; source.volume = 0; }
            if (clip != null) { UnityEngine.Object.Destroy(clip); clip = null; }
            track = "";
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; Stop();
            if (emitter != null) UnityEngine.Object.Destroy(emitter);
            emitter = null; source = null; amplifier = null;
        }
    }
}
