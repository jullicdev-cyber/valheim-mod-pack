// Test-only boundaries. Plugin.cs and ShortcutCapture.cs are compiled unchanged.
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
namespace UnityEngine
{
    public enum KeyCode { None, F, G, K, Escape, Space, LeftControl, RightControl, LeftShift, RightShift, LeftAlt, RightAlt, LeftCommand, RightCommand, LeftWindows, RightWindows, AltGr }
    public class Component { public GameObject gameObject = new GameObject(false); }
    public class Behaviour : Component { public bool enabled = true; public bool isActiveAndEnabled = true; }
    public class GameObject
    {
        public bool activeInHierarchy = true; public readonly List<Component> Components = new List<Component>();
        public GameObject() { } public GameObject(bool ignored) { }
        public T GetComponentInParent<T>() where T : class { foreach (var c in Components) if (c is T) return c as T; return null; }
        public T[] GetComponentsInParent<T>() where T : class { var values = new List<T>(); foreach (var c in Components) if (c is T) values.Add(c as T); return values.ToArray(); }
    }
    public static class Time { public static int frameCount; public static float unscaledTime; }
    public static class Input
    {
        public static readonly HashSet<KeyCode> Down = new HashSet<KeyCode>(), Held = new HashSet<KeyCode>();
        public static bool GetKeyDown(KeyCode key) { return Down.Contains(key); }
        public static bool GetKey(KeyCode key) { return Held.Contains(key); }
    }
    public static class Mathf { public static float Clamp(float value, float low, float high) { return Math.Max(low, Math.Min(high, value)); } }
}
namespace UnityEngine.UI { public class InputField : Behaviour { public bool isFocused; } }
namespace UnityEngine.EventSystems { public class EventSystem { public static EventSystem current; public GameObject currentSelectedGameObject; } }
namespace BepInEx
{
    public class BepInPlugin : Attribute { public BepInPlugin(string id, string name, string version) { } }
    public class BepInDependency : Attribute { public BepInDependency(string id, string version) { } }
    public class BaseUnityPlugin : Behaviour { public readonly Configuration.ConfigFile Config = new Configuration.ConfigFile(); public readonly TestLogger Logger = new TestLogger(); }
    public class TestLogger { public void LogInfo(object value) { } public void LogWarning(object value) { } public void LogError(object value) { throw new Exception("Unexpected plugin error: " + value); } }
}
namespace BepInEx.Configuration
{
    public class ConfigEntry<T> { public T Value; }
    public class ConfigDescription { public ConfigDescription(string text, object range) { } }
    public class AcceptableValueRange<T> { public AcceptableValueRange(T min, T max) { } }
    public class ConfigFile
    {
        public ConfigEntry<T> Bind<T>(string section, string key, T value, object description) { return new ConfigEntry<T> { Value = value }; }
    }
    public struct KeyboardShortcut
    {
        public KeyCode MainKey { get; private set; } public IEnumerable<KeyCode> Modifiers { get; private set; }
        public KeyboardShortcut(KeyCode key, params KeyCode[] modifiers) : this() { MainKey = key; Modifiers = modifiers; }
        public bool IsDown() { if (!Input.GetKeyDown(MainKey)) return false; foreach (var key in Modifiers) if (!Input.GetKey(key)) return false; return true; }
    }
}
namespace Jotunn.Utils
{
    public enum CompatibilityLevel { NotEnforced } public enum VersionStrictness { None }
    public class NetworkCompatibility : Attribute { public NetworkCompatibility(CompatibilityLevel level, VersionStrictness strictness) { } }
}
namespace Jotunn.Managers { public static class GUIManager { public static GameObject CustomGUIFront = new GameObject(); } }
namespace HarmonyLib
{
    public static class Priority { public const int First = 800; }
    public class HarmonyMethod { public MethodInfo method; public int priority; public HarmonyMethod(Type type, string name) { method = AccessTools.Method(type, name); } }
    public static class AccessTools
    {
        public static MethodInfo Method(Type type, string name, Type[] args = null) { return args == null ? type.GetMethod(name, (BindingFlags)60) : type.GetMethod(name, (BindingFlags)60, null, args, null); }
    }
    public class Harmony
    {
        public static readonly Dictionary<MethodInfo, HarmonyMethod> Prefixes = new Dictionary<MethodInfo, HarmonyMethod>();
        public Harmony(string owner) { }
        public void Patch(MethodInfo original, HarmonyMethod prefix = null) { if (original == null) throw new Exception("Missing target"); Prefixes.Add(original, prefix); }
        public void UnpatchSelf() { Prefixes.Clear(); }
        public static bool Query(Type type, string name, object argument)
        {
            HarmonyMethod patch;
            if (!Prefixes.TryGetValue(AccessTools.Method(type, name), out patch)) return true;
            object[] args = { argument, true };
            bool run = (bool)patch.method.Invoke(null, args);
            return run || (bool)args[1];
        }
    }
}
public class Player { public static Player m_localPlayer; public bool Dead, Teleporting, Sleeping, Cutscene; public bool IsDead() { return Dead; } public bool IsTeleporting() { return Teleporting; } public bool IsSleeping() { return Sleeping; } public bool InCutscene() { return Cutscene; } }
public class PlayerController { public bool TakeInput(bool camera = false) { return HarmonyLib.Harmony.Query(typeof(PlayerController), "TakeInput", Player.m_localPlayer); } }
public class ZNet { public static ZNet instance; }
public class ZInput
{
    public static bool s_IsRebindActive;
    public static bool GetKey(KeyCode key, bool ignored = false) { return Input.GetKey(key); }
    public static bool GetKeyDown(KeyCode key, bool ignored = false) { return Input.GetKeyDown(key); }
    public static bool GetButton(string name) { return HarmonyLib.Harmony.Query(typeof(ZInput), "GetButton", name); }
    public static bool GetButtonDown(string name) { return HarmonyLib.Harmony.Query(typeof(ZInput), "GetButtonDown", name); }
    public static bool GetButtonUp(string name) { return HarmonyLib.Harmony.Query(typeof(ZInput), "GetButtonUp", name); }
}
public class InventoryGui { public static InventoryGui instance = new InventoryGui(); public static bool Visible; public static int HiddenUntil; public static bool IsVisible() { return Visible || Time.frameCount < HiddenUntil; } public void Hide() { Visible = false; HiddenUntil = Time.frameCount + 2; } }
public static class UnifiedPopup { public static bool Visible; public static bool IsVisible() { return Visible; } }
public static class Menu { public static bool Visible; public static bool IsVisible() { return Visible; } }
public static class TextInput { public static bool Visible; public static bool IsVisible() { return Visible; } }
public static class StoreGui { public static bool Visible; public static bool IsVisible() { return Visible; } }
public static class Hud { public static bool Visible; public static bool IsPieceSelectionVisible() { return Visible; } }
public static class PlayerCustomizaton { public static bool Visible; public static bool IsBarberGuiVisible() { return Visible; } }
public static class Console { public static bool Visible; public static bool IsVisible() { return Visible; } }
public class Chat { public static Chat instance; public bool Focus; public bool HasFocus() { return Focus; } }
public class Minimap { public enum MapMode { Small, Large } public static Minimap instance; public MapMode m_mode; }
public class Localization { public static Localization instance; public static event Action OnLanguageChange; public string GetSelectedLanguage() { return "English"; } }
namespace ValheimModPack.ChestSearch
{
    public enum ItemSort { Name }
    public class NativeChestReader { }
    public class SearchService { public SearchService(NativeChestReader reader, NameIndex names) { } }
    public class NameIndex { public NameIndex(Action<string> warning) { } public void Clear() { } }
    public class SearchResult { }
    public class ItemSearchResult { public string Key; public List<SearchResult> Chests = new List<SearchResult>(); }
    public class ChestMarker { public ChestMarker(Plugin plugin) { } public void Tick() { } public bool Show(SearchResult item, string key = null) { return true; } public void Clear() { } }
    public class SearchWindow
    {
        public bool IsVisible; public int Opens; public SearchWindow(Plugin plugin) { }
        public void Tick() { }
        public void Show() { if (InventoryGui.IsVisible()) throw new Exception("Inventory still visible"); IsVisible = true; Opens++; }
        public void Hide() { IsVisible = false; }
    }
}
