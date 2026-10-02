using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Bootstrap;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ValheimModPack.PortalFinder
{
    [BepInPlugin(Id, "Portal Finder", Version)]
    [BepInDependency("com.jotunn.jotunn", "2.30.2")]
    [BepInDependency("yay.spikehimself.xportal", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("valheimmodpack.confirmpinremoval", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "valheimmodpack.portalfinder", Version = "1.0.1";
        private static Plugin active;
        private Harmony harmony;
        private PortalRegistry registry;
        private MethodInfo worldPoint;
        private FieldInfo quickTool;
        private PropertyInfo quickBusy;
        private ConfigEntry<KeyboardShortcut> nearMe, nearPoint;
        private GameObject ownButton, pointButton, captionObject;
        private Text caption;
        private Minimap map;
        private Player owner;
        private ZNet network;
        private long world;
        private Minimap.PinData marker;
        private PortalMatch result;
        private bool armed, fromPoint, ready;
        private int status;
        private float ignoreDoubleClickUntil, nextError;
        private string language, ownShortcutLabel, pointShortcutLabel;
        private static readonly Vector2 TopRight = new Vector2(1, 1);
        private bool Russian { get { return Localization.instance != null && Localization.instance.GetSelectedLanguage() == "Russian"; } }
        private string TextFor(string ru, string en) { return Russian ? ru : en; }

        private void Awake()
        {
            active = this;
            try
            {
                nearMe = Config.Bind("Controls", "FindNearestToPlayer", new KeyboardShortcut(KeyCode.J, KeyCode.LeftControl),
                    "Find the nearest portal while the large map is open. Default Ctrl+J; rebind with Bindrune, or None to use the map button only.");
                nearPoint = Config.Bind("Controls", "SelectMapPoint", new KeyboardShortcut(KeyCode.J, KeyCode.LeftControl, KeyCode.LeftShift),
                    "Arm/cancel selection of a map point for a portal search. Default Ctrl+Shift+J; rebind with Bindrune, or None to use the map button only.");
                worldPoint = AccessTools.Method(typeof(Minimap), "ScreenToWorldPoint", new[] { typeof(Vector3) });
                if (worldPoint == null || worldPoint.ReturnType != typeof(Vector3)) throw new MissingMethodException("Minimap.ScreenToWorldPoint contract changed.");
                Type pinPlugin = AccessTools.TypeByName("ValheimModPack.PinRemoval.Plugin");
                Type pinController = AccessTools.TypeByName("ValheimModPack.PinRemoval.QuickPinController");
                if (pinPlugin != null && pinController != null)
                { quickTool = AccessTools.Field(pinPlugin, "quick"); quickBusy = AccessTools.Property(pinController, "IsBusy"); }
                registry = new PortalRegistry(Report);
                harmony = new Harmony(Id);
                registry.Patch(harmony);
                harmony.PatchAll(typeof(Plugin).Assembly);
                ready = true;
                Logger.LogInfo("Portal Finder ready. Open the large map to search at your position or select a point.");
            }
            catch (Exception error) { Report(error); }
        }

        private bool Context()
        {
            Player nextOwner = Player.m_localPlayer;
            Minimap nextMap = Minimap.instance;
            ZNet nextNetwork = ZNet.instance;
            if (!ready || !nextOwner || !nextMap || !nextNetwork) { ResetView(); return false; }
            long nextWorld = nextNetwork.GetWorldUID();
            if (nextWorld == 0) { ResetView(); return false; }
            if (!ReferenceEquals(owner, nextOwner) || !ReferenceEquals(map, nextMap)
                || !ReferenceEquals(network, nextNetwork) || world != nextWorld)
            {
                ResetView(); owner = nextOwner; map = nextMap; network = nextNetwork; world = nextWorld;
            }
            return true;
        }
        private bool CanUse()
        {
            return ready && owner && ReferenceEquals(owner, Player.m_localPlayer) && map
                && ReferenceEquals(map, Minimap.instance) && network && ReferenceEquals(network, ZNet.instance)
                && world == network.GetWorldUID() && map.m_mode == Minimap.MapMode.Large
                && !owner.IsDead() && !owner.IsTeleporting() && !owner.IsSleeping() && !owner.InCutscene()
                && !UnifiedPopup.IsVisible() && !Menu.IsVisible() && !global::Console.IsVisible()
                && !InventoryGui.IsVisible() && !TextInput.IsVisible() && !Minimap.InTextInput()
                && !ZInput.s_IsRebindActive && (Chat.instance == null || !Chat.instance.HasFocus())
                && !FocusedField() && !OtherMapToolBusy();
        }
        private bool OtherMapToolBusy()
        {
            if (quickTool == null || quickBusy == null) return false;
            PluginInfo info;
            if (!Chainloader.PluginInfos.TryGetValue("valheimmodpack.confirmpinremoval", out info) || info.Instance == null) return false;
            object controller = quickTool.GetValue(info.Instance);
            return controller != null && (bool)quickBusy.GetValue(controller, null);
        }
        private static bool FocusedField()
        {
            if (EventSystem.current == null || EventSystem.current.currentSelectedGameObject == null) return false;
            GameObject selected = EventSystem.current.currentSelectedGameObject;
            if (!selected.activeInHierarchy) return false;
            InputField input = selected.GetComponentInParent<InputField>();
            if (input != null && input.isActiveAndEnabled && input.isFocused) return true;
            foreach (Component component in selected.GetComponentsInParent<Component>())
            {
                var behaviour = component as Behaviour;
                if (behaviour == null || !behaviour.isActiveAndEnabled || !component.gameObject.activeInHierarchy) continue;
                Type type = component.GetType();
                while (type != null && type.Name != "TMP_InputField") type = type.BaseType;
                if (type == null) continue;
                PropertyInfo focus = component.GetType().GetProperty("isFocused");
                if (focus != null && (bool)focus.GetValue(component, null)) return true;
            }
            return false;
        }
        private void Update()
        {
            try
            {
                if (!Context()) return;
                bool allowed = CanUse();
                if (map.m_mode != Minimap.MapMode.Large)
                { armed = false; ClearMarker(); result = null; status = 0; RefreshText(); }
                if (!allowed) { armed = false; if (status == 1) status = 0; RefreshText(); SetVisible(false); return; }
                EnsureButtons(); SetVisible(true);
                if (Shortcut.Pressed(nearMe.Value)) Find(owner.transform.position, false);
                else if (Shortcut.Pressed(nearPoint.Value)) TogglePoint();
                if (armed && Input.GetKeyDown(KeyCode.Escape)) { armed = false; status = 0; RefreshText(); }
                string current = Localization.instance == null ? "English" : Localization.instance.GetSelectedLanguage();
                if (current != language || ownShortcutLabel != Shortcut.Label(nearMe.Value) || pointShortcutLabel != Shortcut.Label(nearPoint.Value))
                { language = current; RefreshText(); }
            }
            catch (Exception error) { armed = false; SetVisible(false); Report(error); }
        }
        private void EnsureButtons()
        {
            if (ownButton || GUIManager.CustomGUIFront == null) return;
            Transform parent = GUIManager.CustomGUIFront.transform;
            ownButton = GUIManager.Instance.CreateButton("", parent, TopRight, TopRight, new Vector2(-220, -185), 420, 50);
            ownButton.name = "PortalFinder.NearestToPlayer";
            pointButton = GUIManager.Instance.CreateButton("", parent, TopRight, TopRight, new Vector2(-220, -245), 420, 50);
            pointButton.name = "PortalFinder.NearestToPoint";
            BindButton(ownButton, () => { if (CanUse()) Find(owner.transform.position, false); });
            BindButton(pointButton, () => { if (CanUse()) TogglePoint(); });
            captionObject = GUIManager.Instance.CreateText("", parent, TopRight, TopRight, new Vector2(-220, -320),
                GUIManager.Instance.AveriaSerif, 17, GUIManager.Instance.ValheimBeige, true, Color.black, 420, 96, false);
            captionObject.name = "PortalFinder.Result";
            caption = captionObject.GetComponent<Text>(); caption.supportRichText = false; caption.raycastTarget = false;
            caption.alignment = TextAnchor.UpperCenter; caption.horizontalOverflow = HorizontalWrapMode.Wrap;
            caption.verticalOverflow = VerticalWrapMode.Truncate;
            RefreshText();
        }
        private static void BindButton(GameObject button, Action click)
        {
            var sound = button.GetComponent<ButtonSfx>(); if (sound != null) sound.m_selectSfxPrefab = null;
            foreach (Text label in button.GetComponentsInChildren<Text>(true))
            {
                label.supportRichText = false; label.raycastTarget = false;
                label.alignment = TextAnchor.MiddleCenter;
                label.horizontalOverflow = HorizontalWrapMode.Wrap;
                label.verticalOverflow = VerticalWrapMode.Truncate;
                label.fontSize = 17; label.resizeTextForBestFit = true;
                label.resizeTextMinSize = 14; label.resizeTextMaxSize = 17;
            }
            button.GetComponent<Button>().onClick.AddListener(() => click());
        }
        private static void SetButtonLabel(GameObject button, string value)
        { if (button) { Text label = button.GetComponentInChildren<Text>(true); if (label != null) label.text = value; } }
        private void SetVisible(bool value)
        {
            if (ownButton) ownButton.SetActive(value);
            if (pointButton) pointButton.SetActive(value);
            if (captionObject) captionObject.SetActive(value);
        }
        private void TogglePoint()
        {
            armed = !armed; status = armed ? 1 : 0;
            RefreshText();
        }
        private void Find(Vector3 position, bool selectedPoint)
        {
            armed = false; fromPoint = selectedPoint; result = null; ClearMarker();
            List<PortalRecord> portals; string reason;
            if (!registry.TryRead(out portals, out reason)) { status = reason == "sync" ? 2 : 3; RefreshText(); return; }
            result = PortalSearch.Find(portals, position.x, position.z);
            if (result == null) { status = 4; RefreshText(); return; }
            var portal = result.Portal;
            var location = new Vector3((float)portal.X, (float)portal.Y, (float)portal.Z);
            marker = map.AddPin(location, (Minimap.PinType)6, PortalName(portal.Name), false, false);
            map.ShowPointOnMap(location);
            status = 5; RefreshText();
        }
        private string PortalName(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return TextFor("Портал без названия", "Unnamed portal");
            // Map captions use native rich text; neutralize markup without changing portal tags.
            string name = value.Replace('<', '‹').Replace('>', '›').Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ');
            return name.Length > 80 ? name.Substring(0, 80) + "…" : name;
        }
        private void RefreshText()
        {
            ownShortcutLabel = nearMe == null ? "" : Shortcut.Label(nearMe.Value);
            pointShortcutLabel = nearPoint == null ? "" : Shortcut.Label(nearPoint.Value);
            SetButtonLabel(ownButton, WithShortcut(TextFor("Ближайший портал ко мне", "Nearest portal to me"), ownShortcutLabel));
            SetButtonLabel(pointButton, WithShortcut(armed ? TextFor("Отменить выбор точки портала", "Cancel portal point selection")
                : TextFor("Ближайший портал к точке", "Nearest portal to a point"), pointShortcutLabel));
            if (!caption) return;
            string value = "";
            if (status == 1) value = TextFor("Выбери ЛКМ точку для поиска ближайшего портала.", "Left-click a point to find its nearest portal.");
            if (status == 2) value = TextFor("Порталы ещё синхронизируются. Повтори поиск.", "Portals are still syncing. Try again shortly.");
            if (status == 3) value = TextFor("Полный список порталов недоступен. Проверь XPortal.", "Complete portal list unavailable. Check XPortal.");
            if (status == 4) value = TextFor("В этом мире порталов не найдено.", "No portals found in this world.");
            if (status == 5 && result != null)
                value = TextFor("Портал: ", "Portal: ") + PortalName(result.Portal.Name) + "\n" + Math.Round(result.Distance).ToString("0")
                    + TextFor(" м ", " m ") + (fromPoint ? TextFor("от выбранной точки", "from the selected point") : TextFor("от тебя", "from you"));
            caption.text = value;
            if (marker != null && result != null)
            {
                string title = PortalName(result.Portal.Name);
                if (marker.m_name != title && map)
                {
                    Vector3 position = marker.m_pos;
                    map.RemovePin(marker);
                    marker = map.AddPin(position, (Minimap.PinType)6, title, false, false);
                }
            }
        }
        private string WithShortcut(string action, string shortcut)
        { return action + "\n" + (String.IsNullOrEmpty(shortcut) ? TextFor("Клавиша не назначена", "Unbound") : "[" + shortcut + "]"); }
        private bool ConsumePoint(Minimap clicked)
        {
            if (!armed || !ReferenceEquals(clicked, map) || !CanUse()) return false;
            // Suppress native double-click creation following this single consumed click.
            ignoreDoubleClickUntil = Time.unscaledTime + 0.5f;
            armed = false;
            try { Find((Vector3)worldPoint.Invoke(map, new object[] { ZInput.pointerPosition }), true); }
            catch (Exception error) { ClearMarker(); result = null; status = 3; RefreshText(); Report(error); }
            return true;
        }
        private void ClearMarker()
        { if (marker != null && map) map.RemovePin(marker); marker = null; }
        private void ResetView()
        {
            ClearMarker(); armed = false; result = null; status = 0; ignoreDoubleClickUntil = 0;
            RefreshText();
            SetVisible(false); owner = null; map = null; network = null; world = 0;
        }
        private void Report(Exception error)
        { if (Time.unscaledTime >= nextError) { nextError = Time.unscaledTime + 5; Logger.LogError(error); } }
        private void OnDisable() { ResetView(); }
        private void OnDestroy()
        {
            ResetView();
            if (ownButton) Destroy(ownButton); if (pointButton) Destroy(pointButton); if (captionObject) Destroy(captionObject);
            if (harmony != null) harmony.UnpatchSelf(); if (registry != null) registry.Dispose();
            if (ReferenceEquals(active, this)) active = null;
        }
        [HarmonyPatch(typeof(Minimap), "OnMapLeftClick")]
        private static class PointClickPatch
        {
            [HarmonyPriority(Priority.First), HarmonyBefore("valheimmodpack.confirmpinremoval")]
            private static bool Prefix(Minimap __instance)
            { return active == null || !active.isActiveAndEnabled || !active.ConsumePoint(__instance); }
        }
        [HarmonyPatch(typeof(Minimap), "OnMapDblClick")]
        private static class DoubleClickPatch
        {
            [HarmonyPriority(Priority.First), HarmonyBefore("valheimmodpack.confirmpinremoval")]
            private static bool Prefix(Minimap __instance)
            { return active == null || !active.isActiveAndEnabled || !ReferenceEquals(active.map, __instance) || Time.unscaledTime >= active.ignoreDoubleClickUntil; }
        }
        [HarmonyPatch(typeof(ZInput), "GetButtonDown")]
        private static class DownPatch { private static bool Prefix(ref bool __result) { return AllowNative(ref __result); } }
        [HarmonyPatch(typeof(ZInput), "GetButton")]
        private static class HeldPatch { private static bool Prefix(ref bool __result) { return AllowNative(ref __result); } }
        private static bool AllowNative(ref bool result)
        {
            if (active == null || !active.isActiveAndEnabled || !active.CanUse()) return true;
            if (!Shortcut.Held(active.nearMe.Value) && !Shortcut.Held(active.nearPoint.Value)) return true;
            result = false; return false;
        }
    }
}
