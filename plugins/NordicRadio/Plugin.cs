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
        public const string Version = "1.0.0";
        public static Plugin Instance { get; private set; }
        public RadioService Service { get; private set; }
        public string DataRoot { get; private set; }
        private ConfigEntry<float> personalVolume, nearDistance, farDistance;
        private ConfigEntry<int> wood, bronze, leather, core, uploadRate;
        private readonly Dictionary<RadioPiece, RadioAudio> radios = new Dictionary<RadioPiece, RadioAudio>();
        private RadioWindow window;
        private float nextWatch, nextSelection, nextError;
        private bool ready;
        public float PersonalVolume
        {
            get { return Mathf.Clamp01(personalVolume.Value); }
            set { personalVolume.Value = Single.IsNaN(value) ? 0 : Mathf.Clamp01(value); }
        }
        public float NearDistance { get { return Mathf.Clamp(nearDistance.Value, 0.5f, 10); } }
        public float FarDistance { get { return Mathf.Clamp(farDistance.Value, NearDistance + 1, 80); } }
        private void Awake()
        {
            Instance = this;
            try
            {
                personalVolume = Config.Bind("Audio", "PersonalVolume", 0.8f, new ConfigDescription("Local radio volume; does not change other players.", new AcceptableValueRange<float>(0, 1)));
                nearDistance = Config.Bind("Audio", "NearDistance", 2.5f, new ConfigDescription("Full-volume distance in metres.", new AcceptableValueRange<float>(0.5f, 10)));
                farDistance = Config.Bind("Audio", "FarDistance", 35f, new ConfigDescription("Silence outside this radius in metres.", new AcceptableValueRange<float>(5, 80)));
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
                PrefabManager.OnVanillaPrefabsAvailable += Register;
                ready = true;
                Logger.LogInfo("NordicRadio 1.0.0 ready. Host MP3 folder: " + Path.Combine(DataRoot, "Music"));
            }
            catch (Exception error) { Logger.LogError("NordicRadio initialization failed: " + error); Shutdown(); }
        }
        private void Register()
        {
            PrefabManager.OnVanillaPrefabsAvailable -= Register;
            try { HornModel.Register(wood.Value, bronze.Value, leather.Value, core.Value); }
            catch (Exception error) { Logger.LogError("Skald horn registration failed: " + error); }
        }
        public void Attach(RadioPiece piece)
        {
            if (!ready || piece == null || !piece.IsReady || radios.ContainsKey(piece)) return;
            radios.Add(piece, new RadioAudio(this, piece));
        }
        public void Detach(RadioPiece piece)
        {
            RadioAudio audio;
            if (ReferenceEquals(piece, null) || !radios.TryGetValue(piece, out audio)) return;
            audio.Dispose(); radios.Remove(piece);
        }
        public void OpenRadio(RadioPiece piece)
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
                var dead = new List<RadioPiece>();
                bool watching = Time.unscaledTime >= nextWatch;
                bool selecting = Time.unscaledTime >= nextSelection;
                if (watching) nextWatch = Time.unscaledTime + 2;
                if (selecting)
                {
                    nextSelection = Time.unscaledTime + 0.5f;
                    var candidates = new List<RadioPiece>();
                    foreach (var pair in radios)
                    {
                        pair.Value.Audible = false;
                        if (pair.Key != null && pair.Key.IsReady && Player.m_localPlayer != null
                            && Vector3.Distance(Player.m_localPlayer.transform.position, pair.Key.SoundPosition) <= FarDistance + 2)
                        {
                            var state = Service.GetState(pair.Key.Id);
                            if (state != null && state.Playing) candidates.Add(pair.Key);
                        }
                    }
                    if (Player.m_localPlayer != null)
                    {
                        Vector3 position = Player.m_localPlayer.transform.position;
                        candidates.Sort((a,b) => (a.SoundPosition-position).sqrMagnitude.CompareTo((b.SoundPosition-position).sqrMagnitude));
                        for (int i = 0; i < Math.Min(8, candidates.Count); i++) radios[candidates[i]].Audible = true;
                    }
                }
                foreach (var pair in radios)
                {
                    if (pair.Key == null || !pair.Key.IsReady) { dead.Add(pair.Key); continue; }
                    if (watching && Player.m_localPlayer != null
                        && Vector3.Distance(Player.m_localPlayer.transform.position, pair.Key.transform.position) <= 95)
                        Service.Watch(pair.Key.Id);
                    pair.Value.Tick();
                }
                foreach (var piece in dead) Detach(piece);
            }
            catch (Exception error) { window.Hide(); Report(error); }
        }
        internal void Report(Exception error)
        {
            if (Time.unscaledTime >= nextError) { nextError = Time.unscaledTime + 5; Logger.LogError(error); }
        }
        private void OnDisable()
        {
            if (window != null) window.Hide();
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
            foreach (var audio in radios.Values) audio.Dispose();
            radios.Clear();
            if (Service != null) Service.Dispose();
            Service = null;
        }
        private void OnDestroy() { try { Shutdown(); } finally { Instance = null; } }
    }
}
