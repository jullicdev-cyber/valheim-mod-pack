// Standalone managed regression fixtures; never packaged in the mod DLL.
using System;
using System.Collections.Generic;
namespace UnityEngine
{
    public class Object { public static void Destroy(Object value) { } }
    public struct Vector2 { public Vector2(float x, float y) { } }
    public class Transform { }
    public class GameObject : Object
    {
        private readonly Dictionary<Type, object> components = new Dictionary<Type, object>();
        public string name; public bool activeInHierarchy = true; public Transform transform = new Transform();
        public void SetActive(bool value) { activeInHierarchy = value; }
        public T GetComponent<T>() where T : class, new()
        { object value; if (!components.TryGetValue(typeof(T), out value)) components[typeof(T)] = value = new T(); return (T)value; }
        public T GetComponentInChildren<T>(bool inactive) where T : class, new() { return GetComponent<T>(); }
    }
    public enum KeyCode { Escape }
    public static class Input { public static bool Escape; public static bool GetKeyDown(KeyCode key) { return Escape; } }
}
namespace UnityEngine.UI
{
    public class Text { public bool supportRichText, resizeTextForBestFit; public string text; public int resizeTextMinSize, resizeTextMaxSize; }
    public class Button
    {
        public sealed class Click { public Action Action; public void AddListener(Action action) { Action += action; } }
        public Click onClick = new Click();
    }
}
namespace Jotunn.Managers
{
    public class GUIManager
    {
        public static UnityEngine.GameObject CustomGUIFront = new UnityEngine.GameObject();
        public static GUIManager Instance = new GUIManager();
        public UnityEngine.GameObject CreateButton(string name, UnityEngine.Transform parent, UnityEngine.Vector2 a,
            UnityEngine.Vector2 b, UnityEngine.Vector2 position, float width, float height) { return new UnityEngine.GameObject(); }
    }
}
public class ButtonSfx { public object m_selectSfxPrefab = new object(); }
public class Minimap
{
    public enum MapMode { Small, Large } public enum PinType { Icon0, Icon1, Icon2, Icon3, Death, Bed, Icon4 }
    public sealed class PinData { public bool m_save; public PinType m_type; public string m_name; public long m_ownerID; }
    private readonly List<PinData> m_pins = new List<PinData>();
    public List<PinData> Pins { get { return m_pins; } }
    public static Minimap instance; public MapMode m_mode = MapMode.Large;
}
public class Player
{
    public static Player m_localPlayer; public long Id = 11; public bool Safe = true; public int Messages;
    public long GetPlayerID() { return Id; }
    public void Message(MessageHud.MessageType type, string message, int amount, object icon) { Messages++; }
}
public class ZNet { public static ZNet instance; public long World = 22; public long GetWorldUID() { return World; } }
public class MessageHud { public enum MessageType { Center } }
public class TextInput
{
    public static TextInput instance; public UnityEngine.GameObject m_panel; public static bool Visible;
    public static bool IsVisible() { return Visible; }
}
public class Chat { public static Chat instance; public bool Focus; public bool HasFocus() { return Focus; } }
public static class Menu { public static bool Visible; public static bool IsVisible() { return Visible; } }
public static class Console { public static bool Visible; public static bool IsVisible() { return Visible; } }
public static class UnifiedPopup { public static bool Visible; public static bool IsVisible() { return Visible; } }
public static class InventoryGui { public static bool Visible; public static bool IsVisible() { return Visible; } }
public static class ZInput { public static bool Cancel; public static bool GetButtonDown(string name) { return Cancel; } }
public class Localization
{ public static Localization instance = new Localization(); public string Language = "English"; public string GetSelectedLanguage() { return Language; } }
namespace ValheimModPack.PinRemoval
{
    internal static class PinLauncherLabel
    { internal static void Apply(UnityEngine.GameObject button, string action, string shortcut, string unbound) { } }
    public class PinHistoryController
    {
        public int Calls; public bool Fail; public List<Minimap.PinData> Deleted;
        internal static bool SafePlayer(Player player) { return player != null && player.Safe; }
        public void RemoveMany(Minimap map, IList<Minimap.PinData> pins)
        {
            Calls++; if (Fail) throw new System.IO.IOException("history write failed");
            Deleted = new List<Minimap.PinData>(pins);
            foreach (var pin in pins) map.Pins.Remove(pin);
        }
    }
    public sealed class PinActionMenuView : IDisposable
    {
        public static PinActionMenuView Last; public static bool FailShow, FailHide;
        public bool IsVisible { get; private set; } public Action Rename, Delete, Cancel;
        public PinActionMenuView(Func<string, string> text) { Last = this; }
        public void Show(string name, Action rename, Action delete, Action cancel)
        { IsVisible = true; Rename = rename; Delete = delete; Cancel = cancel; if (FailShow) throw new InvalidOperationException("view unavailable"); }
        public void Hide() { IsVisible = false; if (FailHide) throw new InvalidOperationException("input release failed"); }
        public void Dispose() { Hide(); }
    }
    public sealed class WoodDialogView : IDialogView
    {
        public static WoodDialogView Last; public bool IsVisible { get; private set; } public Action Confirm, Cancel; public string Count;
        public WoodDialogView(Func<string> title, Func<string, string> body, Func<string> label) { Last = this; }
        public void Show(string count, Action confirm, Action cancel)
        { Count = count; Confirm = confirm; Cancel = cancel; IsVisible = true; }
        public void Hide() { IsVisible = false; }
    }
}
