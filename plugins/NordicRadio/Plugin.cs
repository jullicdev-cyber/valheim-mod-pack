using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using Jotunn.Managers;
using Jotunn.Utils;
using UnityEngine;
namespace ValheimModPack.NordicRadio
{
    [BepInPlugin(Id, "Nordic Radio - Skald's Horn", Version)]
    [BepInDependency("com.jotunn.jotunn", "2.30.2")]
    [NetworkCompatibility(CompatibilityLevel.EveryoneMustHaveMod, VersionStrictness.Patch)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "valheimmodpack.nordicradio";
        public const string Version = "1.1.0";
        public static Plugin Instance { get; private set; }
        public RadioService Service { get; private set; }
        public string DataRoot { get; private set; }
        private ConfigEntry<float> personalVolume, nearDistance, farDistance, amplification, backgroundMusicVolume;
        private ConfigEntry<int> wood, bronze, leather, core, uploadRate;
        private readonly Dictionary<IRadioTarget, RadioAudio> radios = new Dictionary<IRadioTarget, RadioAudio>();
        private RadioWindow window;
        private MusicDucking musicDucking;
        internal PortableController Portable { get; private set; }
        internal bool RadioWindowVisible { get { return window != null && window.IsVisible; } }
        private float nextWatch, nextSelection, nextError;
        private bool ready;
        public float PersonalVolume
        {
            get { return SafeFloat(personalVolume.Value, 0.8f, 0, 1); }
            set { personalVolume.Value = Single.IsNaN(value) ? 0 : Mathf.Clamp01(value); }
        }
        public float NearDistance { get { return SafeFloat(nearDistance.Value, 2.5f, 0.5f, 20); } }
        public float FarDistance { get { return SafeFloat(farDistance.Value, 150, NearDistance + 1, RadioProtocol.MaxAudioDistance); } }
        public float Amplification { get { return SafeFloat(amplification.Value, 1.5f, 1, 6); } }
        internal Vector3 ListenerPosition
        {
            get
            {
                var listener = AudioMan.instance != null ? AudioMan.instance.GetActiveAudioListener() : null;
                if (listener != null) return listener.transform.position;
                return Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
            }
        }
        private static float SafeFloat(float value, float fallback, float min, float max)
        { return PlaybackMath.Finite(value) ? Mathf.Clamp(value, min, max) : fallback; }
        private void Awake()
        {
            Instance = this;
            try
            {
                personalVolume = Config.Bind("Audio", "PersonalVolume", 0.8f, new ConfigDescription("Local radio volume; does not change other players.", new AcceptableValueRange<float>(0, 1)));
                nearDistance = Config.Bind("Audio", "NearDistance", 2.5f, new ConfigDescription("Full-volume distance in metres; fades progressively beyond this distance.", new AcceptableValueRange<float>(0.5f, 20)));
                farDistance = Config.Bind("Audio", "FarDistance", 150f, new ConfigDescription("Maximum audible radius in metres; sound stops earlier if Valheim unloads the object. World loading distances are unchanged.", new AcceptableValueRange<float>(5, RadioProtocol.MaxAudioDistance)));
                amplification = Config.Bind("Audio", "Amplification", 1.5f, new ConfigDescription("Local signal gain before spatial attenuation and game effects volume; peaks are gently limited.", new AcceptableValueRange<float>(1, 6)));
                backgroundMusicVolume = Config.Bind("Audio", "BackgroundMusicVolume", 0.2f, new ConfigDescription("Fraction of normal Valheim music volume near an audible horn. 1 disables ducking; original music settings are preserved.", new AcceptableValueRange<float>(0, 1)));
                uploadRate = Config.Bind("Network", "UploadKiBPerSecond", 1024, new ConfigDescription("Host total music upload cap shared by all peers. Lower if gameplay lags while downloading.", new AcceptableValueRange<int>(64, 4096)));
                wood = Config.Bind("Recipe", "FineWood", 20, new ConfigDescription("Fine wood required (restart game after changing recipe; keep equal on all peers).", new AcceptableValueRange<int>(1, 100)));
                bronze = Config.Bind("Recipe", "Bronze", 5, new ConfigDescription("Bronze required.", new AcceptableValueRange<int>(1, 100)));
                leather = Config.Bind("Recipe", "LeatherScraps", 4, new ConfigDescription("Leather scraps required.", new AcceptableValueRange<int>(1, 100)));
                core = Config.Bind("Recipe", "SurtlingCore", 1, new ConfigDescription("Surtling cores required.", new AcceptableValueRange<int>(1, 100)));
                DataRoot = Path.Combine(BepInEx.Paths.GameRootPath, "NordicRadio");
                Directory.CreateDirectory(Path.Combine(DataRoot, "Music"));
                Directory.CreateDirectory(Path.Combine(DataRoot, "Cache"));
                Service = new RadioService(this, DataRoot, message => Logger.LogInfo(message));
                window = new RadioWindow(this);
                musicDucking = new MusicDucking();
                Portable = new PortableController(this);
                PrefabManager.OnVanillaPrefabsAvailable += Register;
                ready = true;
                Logger.LogInfo("NordicRadio " + Version + " ready. Host MP3 folder: " + Path.Combine(DataRoot, "Music"));
            }
            catch (Exception error) { Logger.LogError("NordicRadio initialization failed: " + error); Shutdown(); }
        }
        private void Register()
        {
            PrefabManager.OnVanillaPrefabsAvailable -= Register;
            try { HornModel.Register(wood.Value, bronze.Value, leather.Value, core.Value); PortableModel.Register(); }
            catch (Exception error) { Logger.LogError("Skald horn registration failed: " + error); }
        }
        public void Attach(IRadioTarget piece)
        {
            if (!ready || piece == null || !piece.IsReady || radios.ContainsKey(piece)) return;
            radios.Add(piece, new RadioAudio(this, piece));
        }
        public void Detach(IRadioTarget piece)
        {
            RadioAudio audio;
            if (ReferenceEquals(piece, null) || !radios.TryGetValue(piece, out audio)) return;
            audio.Dispose(); radios.Remove(piece);
        }
        public void OpenRadio(IRadioTarget piece)
        {
            if (!ready || piece == null || !piece.IsReady || Player.m_localPlayer == null) return;
            try { Service.Watch(piece.Id); window.Show(piece); }
            catch (Exception error) { window.Hide(); Report(error); }
        }
        private void Update()
        {
            if (!ready) return;
            try
            {
                window.Tick();
                Service.UploadKiBPerSecond = uploadRate.Value;
                Service.Tick();
                Portable.Tick();
                var dead = new List<IRadioTarget>();
                bool watching = Time.unscaledTime >= nextWatch;
                bool selecting = Time.unscaledTime >= nextSelection;
                if (watching) nextWatch = Time.unscaledTime + 2;
                if (selecting)
                {
                    nextSelection = Time.unscaledTime + 0.5f;
                    var candidates = new List<IRadioTarget>();
                    foreach (var pair in radios)
                    {
                        pair.Value.Audible = false;
                        if (pair.Key != null && pair.Key.IsReady && Player.m_localPlayer != null
                            && Vector3.Distance(ListenerPosition, pair.Key.SoundPosition) <= FarDistance + 2)
                        {
                            var state = Service.GetState(pair.Key.Id);
                            if (state != null && state.Playing) candidates.Add(pair.Key);
                        }
                    }
                    if (Player.m_localPlayer != null)
                    {
                        Vector3 position = ListenerPosition;
                        candidates.Sort((a,b) => (a.SoundPosition-position).sqrMagnitude.CompareTo((b.SoundPosition-position).sqrMagnitude));
                        for (int i = 0; i < Math.Min(8, candidates.Count); i++) radios[candidates[i]].Audible = true;
                    }
                }
                float strongestHorn = 0;
                foreach (var pair in radios)
                {
                    if (pair.Key == null || !pair.Key.IsReady) { dead.Add(pair.Key); continue; }
                    if (watching && Player.m_localPlayer != null
                        && Vector3.Distance(Player.m_localPlayer.transform.position, pair.Key.SoundPosition) <= RadioProtocol.WatchDistance - 5)
                        Service.Watch(pair.Key.Id);
                    pair.Value.Tick();
                    strongestHorn = Mathf.Max(strongestHorn, pair.Value.Audibility);
                }
                foreach (var piece in dead) Detach(piece);
                if (Player.m_localPlayer == null || ZNet.instance == null) musicDucking.Reset();
                else
                {
                    // The horn belongs to the effects bus; a muted effects slider
                    // must not suppress music that the listener still wants to hear.
                    float effects = AudioMan.instance != null ? AudioMan.GetSFXVolume() : 1;
                    strongestHorn *= Mathf.Sqrt(SafeFloat(effects, 0, 0, 1));
                    if (AudioListener.volume <= 0) strongestHorn = 0;
                    musicDucking.Update(strongestHorn, SafeFloat(backgroundMusicVolume.Value, 0.2f, 0, 1), Time.unscaledDeltaTime);
                }
            }
            catch (Exception error) { window.Hide(); if (musicDucking != null) musicDucking.Reset(); Report(error); }
        }
        internal void Report(Exception error)
        {
            if (Time.unscaledTime >= nextError) { nextError = Time.unscaledTime + 5; Logger.LogError(error); }
        }
        private void OnDisable()
        {
            if (window != null) window.Hide();
            if (Portable != null) Portable.Reset();
            if (musicDucking != null) musicDucking.Reset();
            foreach (var pair in radios)
            {
                pair.Value.Stop();
                if (pair.Key != null) pair.Key.SetLit(false);
            }
        }
        private void Shutdown()
        {
            ready = false;
            PrefabManager.OnVanillaPrefabsAvailable -= Register;
            if (window != null) window.Hide();
            if (Portable != null) { Portable.Dispose(); Portable = null; }
            if (musicDucking != null) { musicDucking.Dispose(); musicDucking = null; }
            foreach (var audio in radios.Values) audio.Dispose();
            radios.Clear();
            if (Service != null) Service.Dispose();
            Service = null;
        }
        private void OnDestroy() { try { Shutdown(); } finally { Instance = null; } }
    }
}
