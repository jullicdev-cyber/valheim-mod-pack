// Host-only doubles for controller routing tests. Actual game API/UI are compiled and probed separately.
using System;
using System.Collections.Generic;
using System.Reflection;

namespace UnityEngine
{
    public class Object { public static void Destroy(Object value) { var go = value as GameObject; if (go != null) go.activeInHierarchy = false; } }
    public class Component : Object
    {
        public GameObject gameObject;
        public Transform transform { get { return gameObject.transform; } }
    }
    public class Behaviour : Component
    {
        public bool enabled = true;
        public bool isActiveAndEnabled { get { return enabled && gameObject != null && gameObject.activeInHierarchy; } }
    }
    public class GameObject : Object
    {
        public string name; public bool activeInHierarchy = true; public Transform transform;
        private readonly List<Component> components = new List<Component>();
        public GameObject() { transform = new Transform { gameObject = this }; }
        public T AddComponent<T>() where T : Component, new() { var value = new T { gameObject = this }; components.Add(value); return value; }
        public T GetComponent<T>() where T : class { foreach (var value in components) if (value is T) return value as T; return null; }
        public T GetComponentInParent<T>() where T : class { return GetComponent<T>(); }
        public T GetComponentInChildren<T>() where T : class { return GetComponent<T>(); }
        public T[] GetComponentsInParent<T>() where T : class
        { var result = new List<T>(); foreach (var value in components) if (value is T) result.Add(value as T); return result.ToArray(); }
        public void SetActive(bool value) { activeInHierarchy = value; }
    }
    public class Transform { public GameObject gameObject; public Vector3 position; }
    public class Sprite : Object { }
    public struct Vector2 { public float x, y; public Vector2(float x, float y) { this.x = x; this.y = y; } }
    public struct Vector3
    {
        public float x, y, z; public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public float sqrMagnitude { get { return x*x + y*y + z*z; } }
        public static Vector3 operator -(Vector3 a, Vector3 b) { return new Vector3(a.x-b.x, a.y-b.y, a.z-b.z); }
    }
    public enum KeyCode { P, LeftControl, RightControl, LeftAlt, RightAlt, LeftShift, RightShift, Escape }
    public static class Time { public static float unscaledTime; }
    public static class Input
    {
        public static readonly HashSet<KeyCode> Keys = new HashSet<KeyCode>();
        public static bool GetKey(KeyCode key) { return Keys.Contains(key); }
        public static bool GetKeyDown(KeyCode key) { return Keys.Contains(key); }
    }
}
namespace UnityEngine.UI
{
    public class InputField : UnityEngine.Behaviour { public bool isFocused; }
    public class Text : UnityEngine.Component { public string text; public bool richText; }
    public class Button : UnityEngine.Component { public readonly ClickEvent onClick = new ClickEvent(); }
    public class ClickEvent { private Action action; public void AddListener(Action value) { action += value; } public void Invoke() { if (action != null) action(); } }
}
namespace UnityEngine.EventSystems
{
    public class EventSystem { public static EventSystem current = new EventSystem(); public UnityEngine.GameObject currentSelectedGameObject; }
}
namespace HarmonyLib
{
    public static class Priority { public const int First = 800; }
    public class HarmonyMethod { public int priority; public HarmonyMethod(Type type, string name) { } }
    public class Harmony { public int Patches; public void Patch(MethodInfo method, HarmonyMethod prefix = null, HarmonyMethod postfix = null) { if (method == null) throw new Exception("Missing patch target"); ++Patches; } }
    public static class AccessTools
    {
        public static MethodInfo Method(Type type, string name, Type[] parameters)
        { return type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance, null, parameters, null); }
        public static FieldInfo Field(Type type, string name)
        { return type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance); }
    }
}
namespace Jotunn.Managers
{
    public sealed class GUIManager
    {
        public static readonly GUIManager Instance = new GUIManager();
        public static UnityEngine.GameObject CustomGUIFront = new UnityEngine.GameObject();
        public static int ForeignBlocks, OwnBlocks;
        public UnityEngine.GameObject CreateButton(string name, UnityEngine.Transform parent, UnityEngine.Vector2 a,
            UnityEngine.Vector2 b, UnityEngine.Vector2 pos, float width, float height)
        { var result = new UnityEngine.GameObject(); result.AddComponent<UnityEngine.UI.Button>(); result.AddComponent<ButtonSfx>(); return result; }
    }
}
namespace BepInEx { public static class Paths { public static string GameRootPath; } }
namespace Splatform { public struct PlatformUserID { public static readonly PlatformUserID None = new PlatformUserID(); } }
public sealed class ButtonSfx : UnityEngine.Component { public UnityEngine.GameObject m_selectSfxPrefab; }
public class TMP_InputField : UnityEngine.Behaviour { public bool isFocused { get; set; } }
public sealed class GuiInputField : TMP_InputField { }
public static class MessageHud { public enum MessageType { Center } }
public sealed class Player
{
    public static Player m_localPlayer; public long Id = 777; public bool Safe = true;
    public UnityEngine.Transform transform = new UnityEngine.GameObject().transform;
    public readonly List<string> Messages = new List<string>();
    public long GetPlayerID() { return Id; }
    public bool InPlaceMode() { return false; }
    public void Message(MessageHud.MessageType type, string value, int amount, object icon) { Messages.Add(value); }
}
public sealed class ZNet { public static ZNet instance; public long World = 10; public long GetWorldUID() { return World; } }
public static class ZInput { public static UnityEngine.Vector3 pointerPosition; }
public static class UnifiedPopup { public static bool Visible; public static bool IsVisible() { return Visible; } }
public static class Menu { public static bool Visible; public static bool IsVisible() { return Visible; } }
public static class Console { public static bool Visible; public static bool IsVisible() { return Visible; } }
public static class InventoryGui { public static bool Visible; public static bool IsVisible() { return Visible; } }
public sealed class Chat { public static Chat instance; public bool Focus; public bool HasFocus() { return Focus; } }
public sealed class TextInput
{
    public static TextInput instance; public UnityEngine.GameObject m_panel;
    // Jotunn patches the actual TextInput.IsVisible to include its active input block.
    public static bool IsVisible() { return (instance != null && instance.m_panel != null && instance.m_panel.activeInHierarchy)
        || Jotunn.Managers.GUIManager.ForeignBlocks + Jotunn.Managers.GUIManager.OwnBlocks > 0; }
}
public sealed class Localization
{
    public static Localization instance = new Localization(); public string Language = "English";
    public string GetSelectedLanguage() { return Language; }
    public string Localize(string token) { return token; }
}
public sealed class Minimap
{
    public static Minimap instance; public static bool TextFocused;
    public enum MapMode { Small, Large } public enum PinType { Icon0=0, Icon1=1, Icon2=2, Icon3=3, Icon4=6 }
    public MapMode m_mode; public bool m_pinUpdateRequired; public PinData Closest;
    public readonly List<PinData> m_pins = new List<PinData>();
    public int MarkerDestroys;
    public static bool InTextInput() { return TextFocused; }
    public void SetMapMode(MapMode mode) { m_mode = mode; }
    private UnityEngine.Sprite GetSprite(PinType type) { return new UnityEngine.Sprite(); }
    private UnityEngine.Vector3 ScreenToWorldPoint(UnityEngine.Vector3 point) { return point; }
    private PinData GetClosestPinToCursor() { return Closest; }
    private void DestroyPinMarker(PinData pin) { ++MarkerDestroys; }
    private void OnMapLeftClick() { }
    private void OnMapDblClick() { }
    public PinData AddPin(UnityEngine.Vector3 pos, PinType type, string name, bool save, bool check, long owner, Splatform.PlatformUserID author)
    { var pin = new PinData { m_pos = pos, m_type = type, m_name = name, m_save = save }; pin.m_NamePinData = new PinNameData(pin); m_pins.Add(pin); return pin; }
    public void RemovePin(PinData pin) { m_pins.Remove(pin); }
    public sealed class PinData
    {
        public string m_name; public bool m_save; public PinType m_type; public UnityEngine.Vector3 m_pos;
        public PinNameData m_NamePinData;
    }
    public sealed class PinNameData
    {
        public PinData ParentPin; public UnityEngine.UI.Text PinNameText = new UnityEngine.UI.Text();
        public PinNameData(PinData pin) { ParentPin = pin; PinNameText.text = pin.m_name; }
        public void SetTextAndGameObject(UnityEngine.GameObject value) { }
    }
}
namespace ValheimModPack.PinRemoval
{
    internal static class PinLauncherLabel
    { internal static void Apply(UnityEngine.GameObject button, string action, string shortcut, string unbound) { } }
    public sealed class PinRecord { public string PresetKey, Name, BoundName; }
    public sealed class PinHistoryController
    {
        private static readonly Dictionary<Minimap.PinData, string> keys = new Dictionary<Minimap.PinData, string>();
        private static readonly Dictionary<Minimap.PinData, string> names = new Dictionary<Minimap.PinData, string>();
        public static bool FailRemember; public int Closes;
        public void Close() { ++Closes; }
        public static bool SafePlayer(Player player) { return player != null && player.Safe; }
        public static string PresetFor(Minimap.PinData pin)
        { return keys.ContainsKey(pin) && names[pin] == pin.m_name ? keys[pin] : ""; }
        public static void RememberQuickPin(Minimap.PinData pin, string key)
        { if (FailRemember) throw new System.IO.IOException("History unavailable"); keys[pin] = key; names[pin] = pin.m_name; }
        public static void RememberRename(Minimap.PinData pin, string value)
        { if (FailRemember) throw new System.IO.IOException("History unavailable"); keys.Remove(pin); names.Remove(pin); }
    }
    public sealed class PinPresetEntryView { public string Id, Name, SearchText; public int IconType; public UnityEngine.Sprite Icon; public bool BuiltIn; }
    public sealed class PinPresetIconOption { public int Type; public UnityEngine.Sprite Icon; public string Label; }
    public sealed class PinPresetEdit { public string Id, Name; public int IconType; public bool NameChanged; }
    public sealed class PinPresetWindow
    {
        public bool IsVisible, IsEditing; public string Status, InitialRename, PlaceLabel;
        public IList<PinPresetEntryView> Entries;
        public Action<string> Place, Choose, Delete, RenameSave; public Action<PinPresetEdit> Save;
        public Action Closed, Confirm, Cancel;
        public PinPresetWindow(Func<string,string> localize, Action<Exception> report) { }
        public void Show(IList<PinPresetEntryView> entries, IList<PinPresetIconOption> icons, Action<string> place,
            Action<string> choose, Action<PinPresetEdit> save, Action<string> delete, Action close, string label)
        { Hide(); Entries=entries; Place=place; Choose=choose; Save=save; Delete=delete; Closed=close; PlaceLabel=label; Open(false); }
        public void ShowRename(string name, Action<string> save, Action cancel) { Hide(); InitialRename=name; RenameSave=save; Cancel=cancel; Open(true); }
        public void ShowConfirm(string title, string body, string label, Action confirm, Action cancel)
        { Hide(); Confirm=confirm; Cancel=cancel; Open(true); }
        private void Open(bool editing) { IsVisible=true; IsEditing=editing; ++Jotunn.Managers.GUIManager.OwnBlocks; }
        public void Hide() { if (IsVisible) --Jotunn.Managers.GUIManager.OwnBlocks; IsVisible=false; IsEditing=false; }
        public void RefreshEntries(IList<PinPresetEntryView> entries, bool returnToPicker=true) { Entries=entries; if(returnToPicker) IsEditing=false; }
        public void RefreshLabels(string label) { PlaceLabel=label; }
        public void SetStatus(string message) { Status=message; }
        public void Tick() { }
    }
}
