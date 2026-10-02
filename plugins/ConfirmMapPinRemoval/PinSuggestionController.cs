using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimModPack.PinRemoval
{
    // Only loaded colliders are inspected; this never reads world-generation
    // locations, requests remote ZDOs, or searches the whole scene.
    internal sealed class PinSuggestionController : IDisposable
    {
        private static readonly System.Reflection.MethodInfo BedOwner = AccessTools.Method(typeof(Bed), "GetOwner", Type.EmptyTypes);
        private static readonly System.Reflection.MethodInfo LocationIcon = AccessTools.Method(typeof(Minimap), "GetLocationIcon", new[] { typeof(string) });
        private sealed class Target
        {
            internal GameObject Object;
            internal Collider Collider;
            internal NearbyPinObservation Observation;
        }
        private sealed class Dismissed
        {
            internal ExistingSuggestedPin Pin;
            internal float Until;
        }
        private readonly QuickPinController quick;
        private readonly MapControls controls;
        private readonly Action<Exception> report;
        private readonly PinPresetLocalization text = new PinPresetLocalization();
        private readonly PinSuggestionHud hud = new PinSuggestionHud();
        private readonly SuggestionShortcutGate gate;
        private readonly KeyboardShortcut[] bindings = new KeyboardShortcut[3];
        private readonly Dictionary<string, Target> scanning = new Dictionary<string, Target>();
        private Dictionary<string, Target> targets = new Dictionary<string, Target>();
        private readonly List<Dismissed> dismissed = new List<Dismissed>();
        private readonly RaycastHit[] rays = new RaycastHit[32];
        private Collider[] colliders = new Collider[512];
        private List<PinSuggestion> suggestions = new List<PinSuggestion>();
        private Player player;
        private Minimap map;
        private ZNet network;
        private long world, character;
        private int cursor, count, selected, queued;
        private bool allowed, disposed, scanningNow;
        private float nextScan, nextRefresh;
        private string language, selectedKey, queuedKey;
        private Sprite haldorIcon, hildirIcon;
        private float detectionRadius = 20, resourceRadius = 40, portalRadius = 8;
        public bool IsVisible { get { return hud.IsVisible; } }

        internal PinSuggestionController(QuickPinController quick, PinHistoryController history,
            MapControls controls, Action<Exception> report)
        {
            this.quick = quick; this.controls = controls; this.report = report;
            gate = new SuggestionShortcutGate(new Harmony(Plugin.Id + ".suggestions"), CanCapture,
                () => bindings,
                action => { if (queued == 0) { queued = action; queuedKey = selectedKey; } }, report);
        }
        private bool Live()
        {
            return player != null && ReferenceEquals(player, Player.m_localPlayer) && ReferenceEquals(map, Minimap.instance)
                && network != null && ReferenceEquals(network, ZNet.instance) && world == network.GetWorldUID()
                && character == player.GetPlayerID() && PinHistoryController.SafePlayer(player);
        }
        private bool CanCapture()
        { return !disposed && allowed && controls.SuggestionEnabled.Value && hud.IsVisible && Live() && quick.CanSuggest; }
        public void Tick(bool canShow)
        {
            if (disposed) return;
            allowed = canShow;
            bindings[0] = controls.Suggestion.Value; bindings[1] = controls.NextSuggestion.Value; bindings[2] = controls.DismissSuggestion.Value;
            detectionRadius = Radius(controls.DetectionRadius.Value, 20, 5, 40);
            resourceRadius = Radius(controls.ResourceRadius.Value, 40, 5, 100);
            portalRadius = Radius(controls.PortalRadius.Value, 8, 1, 20);
            Player current = Player.m_localPlayer; ZNet net = ZNet.instance; Minimap currentMap = Minimap.instance;
            if (current == null || net == null || currentMap == null) { Close(); ForgetSession(); return; }
            long nextWorld = net.GetWorldUID(), nextCharacter = current.GetPlayerID();
            if (!ReferenceEquals(player, current) || !ReferenceEquals(map, currentMap) || !ReferenceEquals(network, net)
                || world != nextWorld || character != nextCharacter)
            {
                Close(); ForgetSession(); player = current; map = currentMap; network = net; world = nextWorld; character = nextCharacter;
                if (LocationIcon != null)
                {
                    haldorIcon = LocationIcon.Invoke(map, new object[] { "Vendor_BlackForest" }) as Sprite;
                    hildirIcon = LocationIcon.Invoke(map, new object[] { "Hildir_camp" }) as Sprite;
                }
            }
            if (!allowed || !controls.SuggestionEnabled.Value || controls.Suggestion.Value.MainKey == KeyCode.None
                || !Live() || !quick.CanSuggest) { Close(); return; }
            try
            {
                float now = Time.unscaledTime;
                if (!scanningNow && now >= nextScan) BeginScan(now);
                if (scanningNow) ScanSlice();
                if (now >= nextRefresh || language != text.Language)
                { Refresh(); nextRefresh = now + .5f; language = text.Language; }
                Render();
                gate.Tick();
                int action = queued; string actionKey = queuedKey; queued = 0; queuedKey = null;
                if (action != 0 && actionKey == selectedKey && CanCapture()) Act(action);
            }
            catch (Exception error) { Close(); nextScan = Time.unscaledTime + 5; report(error); }
        }
        private SuggestionOptions Options()
        {
            return new SuggestionOptions { NearbyRadius = detectionRadius,
                ResourceClusterRadius = resourceRadius, PortalClusterRadius = portalRadius };
        }
        private static float Radius(float value, float fallback, float minimum, float maximum)
        { return Single.IsNaN(value) || Single.IsInfinity(value) ? fallback : Mathf.Clamp(value, minimum, maximum); }
        private void BeginScan(float now)
        {
            cursor = 0; scanning.Clear();
            // Buffers grow only when a crowded base needs them, and stay bounded.
            do
            {
                count = Physics.OverlapSphereNonAlloc(player.transform.position, detectionRadius, colliders,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide);
                if (count < colliders.Length || colliders.Length >= 4096) break;
                colliders = new Collider[colliders.Length * 2];
            } while (true);
            scanningNow = true; nextScan = now + 1.5f;
        }
        private void ScanSlice()
        {
            // Spread discovery through frames instead of periodically stalling
            // the client on every collider in a large multiplayer base.
            int end = Math.Min(count, cursor + 32);
            for (; cursor < end; ++cursor)
            {
                Collider collider = colliders[cursor]; colliders[cursor] = null;
                if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy) continue;
                Target target = FindTarget(collider);
                if (target != null && !scanning.ContainsKey(target.Observation.ObjectKey) && Valid(target))
                    scanning.Add(target.Observation.ObjectKey, target);
            }
            if (cursor < count) return;
            targets = new Dictionary<string, Target>(scanning);
            scanning.Clear(); scanningNow = false; Refresh();
        }
        private static Target FindTarget(Collider collider)
        {
            if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy) return null;
            Transform node = collider.transform;
            for (int depth = 0; node != null && depth < 12; ++depth, node = node.parent)
            {
                string name = Utils.GetPrefabName(node.gameObject);
                string[] ids = SuggestionPolicy.MatchPrefab(name);
                if (ids.Length == 0) ids = SuggestionPolicy.MatchLocation(name);
                if (ids.Length == 0) continue;
                return new Target { Object = node.gameObject, Collider = collider,
                    Observation = new NearbyPinObservation {
                        ObjectKey = node.gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture), PresetIds = ids } };
            }
            return null;
        }
        private bool Valid(Target target)
        {
            if (target.Object == null || !target.Object.activeInHierarchy || target.Collider == null
                || !target.Collider.enabled || !target.Collider.gameObject.activeInHierarchy) return false;
            Vector3 position = target.Object.transform.position;
            if ((position - player.transform.position).sqrMagnitude > detectionRadius * detectionRadius) return false;
            string id = target.Observation.PresetIds[0];
            if (id == "default.base")
            {
                var bed = target.Object.GetComponent<Bed>(); var piece = target.Object.GetComponent<Piece>();
                if (bed == null || piece == null || piece.GetCreator() == 0 || BedOwner == null
                    || (long)BedOwner.Invoke(bed, null) == 0) return false;
            }
            if (id == "default.boars")
            {
                var animal = target.Object.GetComponent<Character>();
                if (animal == null || animal.IsDead() || animal.IsTamed()) return false;
            }
            if (!Visible(player.transform.position + Vector3.up * 1.6f, player.transform, target.Object, target.Collider, rays)) return false;
            target.Observation.X = position.x; target.Observation.Y = position.y; target.Observation.Z = position.z;
            return true;
        }
        private static bool Visible(Vector3 eye, Transform ignore, GameObject target, Collider collider, RaycastHit[] buffer)
        {
            if (target == null || !target.activeInHierarchy || collider == null || !collider.enabled
                || !collider.gameObject.activeInHierarchy || buffer == null || buffer.Length == 0) return false;
            Vector3 point = collider.bounds.center;
            Vector3 direction = point - eye; float distance = direction.magnitude;
            if (distance > .05f)
            {
                int hits = Physics.RaycastNonAlloc(eye, direction / distance, buffer, distance,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                // A saturated buffer cannot prove line of sight safely.
                if (hits == buffer.Length) return false;
                Collider first = null; float nearest = Single.MaxValue;
                for (int i = 0; i < hits; ++i)
                {
                    Collider hit = buffer[i].collider;
                    if (hit == null || (ignore != null && hit.transform.IsChildOf(ignore))) continue;
                    if (buffer[i].distance < nearest) { nearest = buffer[i].distance; first = hit; }
                }
                if (first != null && !first.transform.IsChildOf(target.transform)) return false;
            }
            return true;
        }
        private List<ExistingSuggestedPin> CurrentPins()
        {
            var pins = new List<ExistingSuggestedPin>();
            Vector3 position = player.transform.position;
            float radius = detectionRadius + Mathf.Max(60, resourceRadius);
            foreach (Minimap.PinData pin in quick.SuggestionPins)
            {
                string builtin = "";
                if (!pin.m_save)
                {
                    // Native bed and already discovered trader icons also count
                    // as existing markers, despite not being manual saved pins.
                    if (pin.m_type == Minimap.PinType.Bed) builtin = "default.base";
                    else if (pin.m_type == Minimap.PinType.None && pin.m_icon != null)
                    {
                        if (haldorIcon != null && pin.m_icon == haldorIcon) builtin = "default.haldor";
                        else if (hildirIcon != null && pin.m_icon == hildirIcon) builtin = "default.hildir";
                    }
                    if (builtin.Length == 0) continue;
                }
                float dx = pin.m_pos.x - position.x, dz = pin.m_pos.z - position.z;
                if (dx * dx + dz * dz > radius * radius) continue;
                pins.Add(new ExistingSuggestedPin { BuiltinPresetId = pin.m_save ? PinHistoryController.PresetForKnownCurrentPin(pin) : builtin,
                    RawName = pin.m_name, DisplayName = pin.m_name, Icon = (int)pin.m_type,
                    X = pin.m_pos.x, Y = pin.m_pos.y, Z = pin.m_pos.z });
            }
            for (int i = dismissed.Count - 1; i >= 0; --i)
            {
                if (Time.unscaledTime >= dismissed[i].Until) dismissed.RemoveAt(i);
                else pins.Add(dismissed[i].Pin);
            }
            return pins;
        }
        private void Refresh()
        {
            var observations = new List<NearbyPinObservation>();
            foreach (Target target in targets.Values)
                if (target.Object != null && target.Object.activeInHierarchy)
                {
                    Vector3 pos = target.Object.transform.position;
                    target.Observation.X = pos.x; target.Observation.Y = pos.y; target.Observation.Z = pos.z;
                    observations.Add(target.Observation);
                }
            IList<PinPreset> presets = quick.SuggestionPresets;
            var labels = new Dictionary<string, string>();
            foreach (PinPreset preset in presets) labels[preset.Id] = quick.PresetName(preset);
            Vector3 playerPosition = player.transform.position;
            suggestions = SuggestionPolicy.Select(presets, labels, observations, CurrentPins(),
                playerPosition.x, playerPosition.y, playerPosition.z, Options());
            selected = 0;
            for (int i = 0; i < suggestions.Count; ++i)
                if (Identity(suggestions[i]) == selectedKey) { selected = i; break; }
            selectedKey = suggestions.Count == 0 ? null : Identity(suggestions[selected]);
        }
        private static string Identity(PinSuggestion suggestion)
        { return suggestion.Preset.Id + ":" + suggestion.ObjectKey; }
        private void Render()
        {
            if (suggestions.Count == 0) { hud.Hide(); gate.Reset(); return; }
            PinSuggestion suggestion = suggestions[selected];
            Target target;
            if (!targets.TryGetValue(suggestion.ObjectKey, out target) || !Valid(target))
            { targets.Remove(suggestion.ObjectKey); Refresh(); hud.Hide(); gate.Reset(); return; }
            string caption = quick.PresetName(suggestion.Preset);
            if (suggestions.Count > 1) caption += "  (" + (selected + 1) + "/" + suggestions.Count + ")";
            hud.Show(quick.PresetSprite(suggestion.Preset), caption, MapControls.Label(controls.Suggestion.Value), text.Get("suggestion_place"));
        }
        private void Act(int action)
        {
            if (suggestions.Count == 0) return;
            if (action == 2)
            { selected = (selected + 1) % suggestions.Count; selectedKey = Identity(suggestions[selected]); Render(); return; }
            PinSuggestion chosen = suggestions[selected]; Target target;
            if (!targets.TryGetValue(chosen.ObjectKey, out target) || !Valid(target)) { Refresh(); Render(); return; }
            // Re-evaluate duplicates and current preset immediately before use:
            // another pin or a personal preset edit may have changed since scan.
            string identity = Identity(chosen); Refresh();
            PinSuggestion current = suggestions.Find(item => Identity(item) == identity);
            if (current == null) { Render(); return; }
            if (action == 3)
            {
                if (dismissed.Count >= 128) dismissed.RemoveAt(0);
                dismissed.Add(new Dismissed { Until = Time.unscaledTime + 120,
                    Pin = new ExistingSuggestedPin { BuiltinPresetId = current.Preset.Id, Icon = current.Preset.Icon,
                        RawName = current.Name, X = current.X, Y = current.Y, Z = current.Z } });
            }
            else if (action == 1)
                quick.PlaceSuggested(current.Preset.Id, new Vector3((float)current.X, (float)current.Y, (float)current.Z));
            Refresh(); Render();
        }
        public void Close()
        {
            hud.Hide(); gate.Reset(); queued = 0; queuedKey = null;
            scanningNow = false; cursor = count = 0; Array.Clear(colliders, 0, colliders.Length);
            targets.Clear(); scanning.Clear(); suggestions.Clear(); selectedKey = null; nextScan = 0;
        }
        private void ForgetSession()
        { player = null; map = null; network = null; world = character = 0; dismissed.Clear(); language = null; haldorIcon = hildirIcon = null; }
        public void Dispose()
        { if (disposed) return; Close(); gate.Dispose(); hud.Dispose(); ForgetSession(); disposed = true; }
    }
}
