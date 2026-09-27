using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
namespace ValheimModPack.PinRemoval
{
    // Presets are personal; placed pins remain ordinary Valheim pins with readable saved names.
    public sealed class QuickPinController : IDisposable
    {
        private static QuickPinController active;
        private static readonly HashSet<string> knownPresets = KnownPresets();
        private readonly Action<Exception> report;
        private readonly PinHistoryController history;
        private readonly PinPresetLocalization text = new PinPresetLocalization();
        private readonly PinPresetWindow window;
        private readonly FieldInfo pins;
        private readonly MethodInfo sprite, worldPoint, closest;
        private readonly MethodInfo destroyMarker;
        private readonly FieldInfo pinUpdate;
        private Player owner;
        private Minimap map;
        private long world, character;
        private int generation;
        private PinPresetStore store;
        private Vector3 target;
        private bool targetIsMap, allowed, unavailable;
        private string armed, language;
        private float ignoreClickUntil;
        private GameObject launcher;
        public bool IsOpen { get { return window.IsVisible; } }
        public bool IsBusy { get { return IsOpen || armed != null; } }

        public QuickPinController(Harmony harmony, PinHistoryController history, FieldInfo pins, Action<Exception> report)
        {
            this.history = history; this.pins = pins; this.report = report;
            window = new PinPresetWindow(text.Get, report);
            sprite = AccessTools.Method(typeof(Minimap), "GetSprite", new[] { typeof(Minimap.PinType) });
            worldPoint = AccessTools.Method(typeof(Minimap), "ScreenToWorldPoint", new[] { typeof(Vector3) });
            closest = AccessTools.Method(typeof(Minimap), "GetClosestPinToCursor", Type.EmptyTypes);
            destroyMarker = AccessTools.Method(typeof(Minimap), "DestroyPinMarker", new[] { typeof(Minimap.PinData) });
            pinUpdate = AccessTools.Field(typeof(Minimap), "m_pinUpdateRequired");
            if (sprite == null || worldPoint == null || closest == null || destroyMarker == null || pinUpdate == null) throw new MissingMemberException("Quick pin map API unavailable");
            harmony.Patch(AccessTools.Method(typeof(Minimap), "OnMapLeftClick", Type.EmptyTypes),
                prefix: new HarmonyMethod(typeof(QuickPinController), "BeforeClick") { priority = Priority.First });
            harmony.Patch(AccessTools.Method(typeof(Minimap), "OnMapDblClick", Type.EmptyTypes),
                prefix: new HarmonyMethod(typeof(QuickPinController), "BeforeDoubleClick"));
            harmony.Patch(AccessTools.Method(typeof(Minimap.PinNameData), "SetTextAndGameObject", new[] { typeof(GameObject) }),
                postfix: new HarmonyMethod(typeof(QuickPinController), "AfterCaptionCreated"));
            active = this;
        }
        private bool Context()
        {
            Player player = Player.m_localPlayer; Minimap nextMap = Minimap.instance;
            if (player == null || nextMap == null || ZNet.instance == null) { Reset(); return false; }
            long nextWorld = ZNet.instance.GetWorldUID(), nextCharacter = player.GetPlayerID();
            if (nextWorld == 0 || nextCharacter == 0) { Reset(); return false; }
            if (!ReferenceEquals(player, owner) || !ReferenceEquals(nextMap, map) || nextWorld != world || nextCharacter != character)
            {
                Reset(); owner = player; map = nextMap; world = nextWorld; character = nextCharacter;
                try
                {
                    store = new PinPresetStore(Path.Combine(BepInEx.Paths.GameRootPath, "ValheimModpack", "MapPinPresets"), character, PinPresetCatalog.Defaults(), ManualIcon);
                    if (store.ReadOnly) report(new InvalidDataException("Pin presets preserved after load error: " + store.LoadError));
                }
                catch (Exception error) { unavailable = true; report(error); }
            }
            return !unavailable && store != null;
        }
        private bool Live()
        {
            return owner != null && ReferenceEquals(owner, Player.m_localPlayer) && ReferenceEquals(map, Minimap.instance)
                && ZNet.instance != null && world == ZNet.instance.GetWorldUID() && character == owner.GetPlayerID()
                && PinHistoryController.SafePlayer(owner);
        }
        private static bool ManualIcon(int type) { return type == 0 || type == 1 || type == 2 || type == 3 || type == 6; }
        private static HashSet<string> KnownPresets()
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (PinPreset preset in PinPresetCatalog.Defaults()) result.Add(preset.Id);
            return result;
        }
        private static bool NativeModal()
        {
            return UnifiedPopup.IsVisible() || Menu.IsVisible() || global::Console.IsVisible()
                || (Chat.instance != null && Chat.instance.HasFocus())
                || (TextInput.instance != null && TextInput.instance.m_panel != null && TextInput.instance.m_panel.activeInHierarchy)
                || Minimap.InTextInput();
        }
        private bool CanStart()
        {
            if (!allowed || !Live() || NativeModal() || InventoryGui.IsVisible() || TextInput.IsVisible()) return false;
            if (EventSystem.current == null || EventSystem.current.currentSelectedGameObject == null) return true;
            GameObject selected = EventSystem.current.currentSelectedGameObject;
            if (!selected.activeInHierarchy) return true;
            InputField field = selected.GetComponentInParent<InputField>();
            if (field != null && field.isActiveAndEnabled && field.gameObject.activeInHierarchy && field.isFocused) return false;
            foreach (Component component in selected.GetComponentsInParent<Component>())
            {
                var behaviour = component as Behaviour;
                if (behaviour == null || !behaviour.isActiveAndEnabled || !component.gameObject.activeInHierarchy) continue;
                Type inputType = component.GetType();
                while (inputType != null && inputType.Name != "TMP_InputField") inputType = inputType.BaseType;
                if (inputType == null) continue;
                var focused = component.GetType().GetProperty("isFocused");
                if (focused != null && (bool)focused.GetValue(component, null)) return false;
            }
            return true;
        }
        public void Tick(bool canOpen)
        {
            allowed = canOpen;
            if (!Context()) { SetLauncher(false); return; }
            if (!Live() || !allowed || NativeModal() || InventoryGui.IsVisible() || (armed != null && TextInput.IsVisible())
                || (window.IsVisible && targetIsMap && map.m_mode != Minimap.MapMode.Large)) { Close(); SetLauncher(false); return; }
            if (language != text.Language)
            {
                language = text.Language; RefreshCaptions();
                if (window.IsVisible)
                {
                    window.RefreshEntries(Entries(), false);
                    window.RefreshLabels(PlaceLabel);
                }
                if (launcher != null) { UnityEngine.Object.Destroy(launcher); launcher = null; }
            }
            window.Tick();
            if (armed != null && (map.m_mode != Minimap.MapMode.Large || Input.GetKeyDown(KeyCode.Escape))) armed = null;
            SetLauncher(map.m_mode == Minimap.MapMode.Large && !IsBusy && CanStart());
            if (!IsBusy && CanStart() && Input.GetKeyDown(KeyCode.P)
                && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                && !Input.GetKey(KeyCode.LeftAlt) && !Input.GetKey(KeyCode.RightAlt)) OpenAt(owner.transform.position, false);
        }
        private string PlaceLabel { get { return text.Get(targetIsMap ? "place_point" : "place_here"); } }
        private List<Minimap.PinData> CurrentPins { get { return (List<Minimap.PinData>)pins.GetValue(map); } }
        private void SetLauncher(bool visible)
        {
            if (!visible) { if (launcher != null) launcher.SetActive(false); return; }
            if (launcher == null && GUIManager.CustomGUIFront != null)
            {
                launcher = GUIManager.Instance.CreateButton(text.Get("quick_pins") + " · Ctrl+P", GUIManager.CustomGUIFront.transform,
                    new Vector2(1, 1), new Vector2(1, 1), new Vector2(-160, -115), 270, 44);
                launcher.name = "ConfirmMapPinRemoval.QuickPinsLauncher";
                var sound = launcher.GetComponent<ButtonSfx>(); if (sound != null) sound.m_selectSfxPrefab = null;
                launcher.GetComponent<Button>().onClick.AddListener(() => { if (CanStart()) OpenAt(owner.transform.position, false); });
            }
            if (launcher != null) launcher.SetActive(true);
        }
        private List<PinPresetIconOption> Icons()
        {
            var result = new List<PinPresetIconOption>();
            foreach (int type in new[] { 0, 1, 2, 3, 6 })
            {
                Sprite icon = sprite.Invoke(map, new object[] { (Minimap.PinType)type }) as Sprite;
                string label = type == 0 ? "presets_icon_fire" : type == 1 ? "presets_icon_house" : type == 2 ? "presets_icon_hammer" : type == 3 ? "presets_icon_pin" : "presets_icon_portal";
                if (icon != null) result.Add(new PinPresetIconOption { Type = type, Icon = icon, Label = text.Get(label) });
            }
            return result;
        }
        private List<PinPresetEntryView> Entries()
        {
            var result = new List<PinPresetEntryView>();
            foreach (PinPreset preset in store.Presets)
                result.Add(new PinPresetEntryView { Id = preset.Id, Name = text.Name(preset), IconType = preset.Icon,
                    Icon = sprite.Invoke(map, new object[] { (Minimap.PinType)preset.Icon }) as Sprite, BuiltIn = preset.Builtin,
                    SearchText = text.SearchAliases(preset) });
            return result;
        }
        private void OpenAt(Vector3 position, bool fromMap)
        {
            if (!Context() || !CanStart()) return;
            history.Close(); target = position; targetIsMap = fromMap; armed = null;
            ShowPicker();
        }
        private void ShowPicker()
        {
            if (!Live()) { Close(); return; }
            int request = ++generation;
            window.Show(Entries(), Icons(),
                id => { if (Current(request)) Place(id, target); },
                id => { if (Current(request)) ChooseOnMap(id); },
                edit => { if (Current(request)) Save(edit); },
                id => { if (Current(request)) Delete(id); }, Close, PlaceLabel);
            if (store.ReadOnly) window.SetStatus(text.Get("read_only"));
            SetLauncher(false);
        }
        private bool Current(int request) { return request == generation && Live() && window.IsVisible && !NativeModal(); }
        private bool Writable()
        {
            if (store != null && !store.ReadOnly) return true;
            window.SetStatus(text.Get("read_only")); return false;
        }
        private void Save(PinPresetEdit edit)
        {
            try
            {
                if (!Writable()) return;
                if (!Icons().Exists(icon => icon.Type == edit.IconType)) throw new InvalidDataException("Unsupported manual map icon");
                if (String.IsNullOrEmpty(edit.Id)) store.Add(edit.Name, edit.IconType);
                else if (!store.Update(edit.Id, edit.Name, edit.IconType, edit.NameChanged)) throw new InvalidOperationException("Preset no longer exists");
                window.RefreshEntries(Entries());
            }
            catch (Exception error) { report(error); window.SetStatus(text.Get("save_failed")); }
        }
        private void Delete(string id)
        {
            if (!Writable()) return;
            PinPreset preset = store.Find(id); if (preset == null) return;
            int request = ++generation;
            window.ShowConfirm(text.Get("delete_preset"), String.Format(text.Get("delete_preset_body"), text.Name(preset)), text.Get("delete"),
                () =>
                {
                    if (!Current(request)) return;
                    try { store.Delete(id); ShowPicker(); }
                    catch (Exception error) { report(error); window.SetStatus(text.Get("save_failed")); }
                }, () => { if (Live()) ShowPicker(); else Close(); });
        }
        private void ChooseOnMap(string id)
        {
            if (store.Find(id) == null) return;
            window.Hide(); ++generation; armed = id;
            map.SetMapMode(Minimap.MapMode.Large); ignoreClickUntil = Time.unscaledTime + .2f;
            owner.Message(MessageHud.MessageType.Center, text.Get("choose_point_hint"), 0, null);
        }
        private void Place(string id, Vector3 position)
        {
            if (!Live()) { Close(); return; }
            PinPreset preset = store.Find(id); if (preset == null) return;
            if (!Icons().Exists(icon => icon.Type == preset.Icon)) throw new InvalidDataException("Unsupported preset icon");
            string name = text.Name(preset);
            string binding = String.IsNullOrEmpty(preset.LocalizationKey) ? "" : preset.Id;
            // Ignore repeated clicks without coalescing genuinely distinct locations or icons.
            foreach (Minimap.PinData existing in CurrentPins)
                if (existing.m_save && existing.m_type == (Minimap.PinType)preset.Icon
                    && (existing.m_name == name || (binding.Length != 0 && PinHistoryController.PresetFor(existing) == binding))
                    && (existing.m_pos - position).sqrMagnitude < .01f) { Close(); return; }
            Minimap.PinData pin = map.AddPin(position, (Minimap.PinType)preset.Icon, name, true, false, 0, Splatform.PlatformUserID.None);
            if (pin == null) throw new InvalidOperationException("Game refused to add a map pin");
            try { PinHistoryController.RememberQuickPin(pin, binding); }
            catch { map.RemovePin(pin); throw; }
            Close(); ignoreClickUntil = Time.unscaledTime + .4f;
            owner.Message(MessageHud.MessageType.Center, text.Get("pin_created") + ": " + name, 0, null);
        }
        public void Rename(Minimap.PinData pin)
        {
            if (!Context() || !Live() || pin == null || !pin.m_save || !CurrentPins.Contains(pin)) return;
            history.Close();
            if (NativeModal() || InventoryGui.IsVisible() || TextInput.IsVisible()) return;
            int request = ++generation;
            targetIsMap = true;
            window.ShowRename(DisplayName(pin), name =>
            {
                if (!Current(request) || !CurrentPins.Contains(pin) || map.m_mode != Minimap.MapMode.Large) return;
                try
                {
                    name = PinPresetStore.CleanName(name);
                    if (String.IsNullOrWhiteSpace(name)) throw new InvalidDataException("A pin name is required");
                    PinHistoryController.RememberRename(pin, name);
                    pin.m_name = name;
                    destroyMarker.Invoke(map, new object[] { pin });
                    pin.m_NamePinData = new Minimap.PinNameData(pin);
                    pinUpdate.SetValue(map, true);
                    Close(); ignoreClickUntil = Time.unscaledTime + .4f;
                }
                catch (Exception error) { report(error); window.SetStatus(text.Get("save_failed")); }
            }, Close);
        }
        public string DisplayName(Minimap.PinData pin)
        {
            string key = PinHistoryController.PresetFor(pin);
            return knownPresets.Contains(key) ? text.Get(key) : pin.m_name;
        }
        internal static string DisplayRecord(PinRecord pin)
        {
            return active != null && knownPresets.Contains(pin.PresetKey) && pin.Name == pin.BoundName
                ? active.text.Get(pin.PresetKey) : pin.Name;
        }
        internal static void RenameFromHistory(Minimap.PinData pin) { if (active != null) active.Rename(pin); }
        internal static void RefreshKnownCaptions() { if (active != null && active.Live()) active.RefreshCaptions(); }
        private static bool BeforeClick(Minimap __instance)
        {
            QuickPinController self = active;
            if (self == null) return true;
            try
            {
                if (self.IsOpen || Time.unscaledTime < self.ignoreClickUntil) return false;
                if (!self.Context() || !ReferenceEquals(self.map, __instance) || !self.CanStart() || __instance.m_mode != Minimap.MapMode.Large) return true;
                if (self.armed != null)
                {
                    string selected = self.armed; self.armed = null;
                    self.Place(selected, (Vector3)self.worldPoint.Invoke(__instance, new object[] { ZInput.pointerPosition })); return false;
                }
                if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))
                {
                    var pin = self.closest.Invoke(__instance, null) as Minimap.PinData;
                    if (pin != null && pin.m_save) { self.Rename(pin); return false; }
                }
                if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                {
                    self.OpenAt((Vector3)self.worldPoint.Invoke(__instance, new object[] { ZInput.pointerPosition }), true); return false;
                }
            }
            catch (Exception error) { self.Close(); self.report(error); return false; }
            return true;
        }
        private static bool BeforeDoubleClick()
        { return active == null || (!active.IsBusy && Time.unscaledTime >= active.ignoreClickUntil); }
        private static void AfterCaptionCreated(Minimap.PinNameData __instance)
        { if (active != null) active.Caption(__instance.ParentPin); }
        private void Caption(Minimap.PinData pin)
        {
            try
            {
                if (pin == null || !pin.m_save || pin.m_NamePinData == null || pin.m_NamePinData.PinNameText == null) return;
                string key = PinHistoryController.PresetFor(pin); if (!knownPresets.Contains(key)) return;
                pin.m_NamePinData.PinNameText.text = text.Get(key);
            }
            catch (Exception error) { report(error); }
        }
        private void RefreshCaptions() { if (map != null) foreach (var pin in CurrentPins) Caption(pin); }
        public void Close() { ++generation; window.Hide(); armed = null; SetLauncher(false); }
        private void Reset() { Close(); owner = null; map = null; world = character = 0; store = null; unavailable = false; language = null; }
        public void Dispose() { Reset(); if (launcher != null) UnityEngine.Object.Destroy(launcher); if (ReferenceEquals(active, this)) active = null; }
    }
}
