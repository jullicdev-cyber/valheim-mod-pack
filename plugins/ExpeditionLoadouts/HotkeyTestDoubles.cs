// Test-only boundaries. Plugin.cs and ShortcutCapture.cs are compiled unchanged.
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
namespace UnityEngine
{
    public enum KeyCode { None, F, L, G, K, Escape, Space, LeftControl, RightControl, LeftShift, RightShift, LeftAlt, RightAlt, LeftCommand, RightCommand, LeftWindows, RightWindows, AltGr }
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
namespace TMPro { public class TMP_InputField : Behaviour { public bool isFocused { get; set; } } }
namespace UnityEngine.EventSystems { public class EventSystem { public static EventSystem current; public GameObject currentSelectedGameObject; } }
namespace BepInEx
{
    public class BepInPlugin : Attribute { public BepInPlugin(string id, string name, string version) { } }
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class BepInDependency : Attribute
    {
        public enum DependencyFlags { SoftDependency }
        public BepInDependency(string id, string version) { }
        public BepInDependency(string id, DependencyFlags flags) { }
    }
    public static class Paths { public static string BepInExRootPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "expedition-fixture", "BepInEx"); }
    public class BaseUnityPlugin : Behaviour { public readonly Configuration.ConfigFile Config = new Configuration.ConfigFile(); public readonly TestLogger Logger = new TestLogger(); }
    public class TestLogger { public void LogInfo(object value) { } public void LogWarning(object value) { } public void LogError(object value) { throw new Exception("Unexpected plugin error: " + value); } }
}
namespace BepInEx.Configuration
{
    public class ConfigEntry<T>
    {
        private T value;
        public event EventHandler SettingChanged;
        public T Value { get { return value; } set { this.value = value; if (SettingChanged != null) SettingChanged(this, EventArgs.Empty); } }
    }
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
        public bool IsPressed() { if (!Input.GetKey(MainKey)) return false; foreach (var key in Modifiers) if (!Input.GetKey(key)) return false; return true; }
    }
}
namespace Jotunn.Utils
{
    public enum CompatibilityLevel { NotEnforced } public enum VersionStrictness { None }
    public class NetworkCompatibility : Attribute { public NetworkCompatibility(CompatibilityLevel level, VersionStrictness strictness) { } }
}
namespace Jotunn.Managers
{
    public static class GUIManager
    {
        public static GameObject CustomGUIFront = new GameObject();
        public static int InputBlocks;
        public static void BlockInput(bool block) { InputBlocks += block ? 1 : -1; }
    }
}
namespace HarmonyLib
{
    public static class Priority { public const int First = 800; }
    public class HarmonyMethod { public MethodInfo method; public int priority; public HarmonyMethod(Type type, string name) { method = AccessTools.Method(type, name); } }
    public static class AccessTools
    {
        public static FieldInfo Field(Type type, string name) { return type.GetField(name, (BindingFlags)60); }
        public static MethodInfo Method(Type type, string name, Type[] args = null) { return args == null ? type.GetMethod(name, (BindingFlags)60) : type.GetMethod(name, (BindingFlags)60, null, args, null); }
    }
    public class Harmony
    {
        public static readonly Dictionary<MethodInfo, HarmonyMethod> Prefixes = new Dictionary<MethodInfo, HarmonyMethod>();
        public static readonly Dictionary<MethodInfo, HarmonyMethod> Postfixes = new Dictionary<MethodInfo, HarmonyMethod>();
        public Harmony(string owner) { }
        public void Patch(MethodInfo original, HarmonyMethod prefix = null, HarmonyMethod postfix = null) { if (original == null) throw new Exception("Missing target"); if (prefix != null) Prefixes.Add(original, prefix); if (postfix != null) Postfixes.Add(original, postfix); }
        public void UnpatchSelf() { Prefixes.Clear(); Postfixes.Clear(); }
        public static void After(Type type, string name, Type argumentType) { HarmonyMethod patch; if (Postfixes.TryGetValue(AccessTools.Method(type, name, new[] { argumentType }), out patch)) patch.method.Invoke(null, null); }
        public static bool Query(Type type, string name, object argument, bool original = true)
        {
            HarmonyMethod patch;
            if (!Prefixes.TryGetValue(AccessTools.Method(type, name), out patch)) return original;
            object[] args = { argument, true };
            bool run = (bool)patch.method.Invoke(null, args);
            return run ? original : (bool)args[1];
        }
    }
}
public class Player
{
    public static Player m_localPlayer; public long Id = 123; public long GetPlayerID() { return Id; } public bool Dead, Teleporting, Sleeping, Cutscene; public int GuardianStarts;
    public bool IsDead() { return Dead; } public bool IsTeleporting() { return Teleporting; } public bool IsSleeping() { return Sleeping; } public bool InCutscene() { return Cutscene; }
    // The real Player.Update has a separate TakeInput gate before the GP branch;
    // PlayerController.TakeInput belongs to FixedUpdate and does not protect it.
    public bool TakeInput() { return HarmonyLib.Harmony.Query(typeof(Player), "TakeInput", this, !InventoryGui.IsVisible()); }
    public bool StartGuardianPower() { if (!HarmonyLib.Harmony.Query(typeof(Player), "StartGuardianPower", this)) return false; GuardianStarts++; return true; }
    public void NativeUpdate() { if (TakeInput() && ZInput.GetButtonDown("GP")) StartGuardianPower(); }
}
public class PlayerController { public bool TakeInput(bool camera = false) { return HarmonyLib.Harmony.Query(typeof(PlayerController), "TakeInput", Player.m_localPlayer); } }
public class ZNet { public static ZNet instance; }
public class ZInput
{
    public static bool s_IsRebindActive;
    public static bool CachedButtons;
    private static readonly ZInput m_instance = new ZInput();
    private readonly Dictionary<string, ButtonDef> m_buttons = new Dictionary<string, ButtonDef>();
    public class ButtonDef
    {
        // Names and phase separation match assembly_utils.dll 1.0.16. The raw
        // GetKeyDown edge is deliberately independent of these cached states.
        private bool m_heldDynamic, m_heldFixed, m_pressedDynamic, m_pressedFixed;
        private bool m_wasPressedDynamic, m_wasPressedFixed, m_releasedDynamic, m_releasedFixed;
        public void Press() { m_wasPressedDynamic = m_wasPressedFixed = false; m_heldDynamic = m_heldFixed = true; }
        public void Release() { m_heldDynamic = m_heldFixed = false; }
        public void Tick()
        {
            m_pressedDynamic = !m_wasPressedDynamic && m_heldDynamic;
            m_releasedDynamic = m_wasPressedDynamic && !m_heldDynamic;
            m_wasPressedDynamic = m_heldDynamic;
            m_pressedFixed = !m_wasPressedFixed && m_heldFixed;
            m_releasedFixed = m_wasPressedFixed && !m_heldFixed;
            m_wasPressedFixed = m_heldFixed;
        }
        public bool Pressed { get { return m_pressedDynamic; } }
        public bool Held { get { return m_heldDynamic || (Pressed && !m_releasedDynamic); } }
        public bool Released { get { return m_releasedDynamic; } }
        public bool FixedPressed { get { return m_pressedFixed; } }
        public bool FixedReleased { get { return m_releasedFixed; } }
    }
    public static ButtonDef Button(string name) { ButtonDef button; if (!m_instance.m_buttons.TryGetValue(name, out button)) m_instance.m_buttons[name] = button = new ButtonDef(); return button; }
    public static void ResetFixture() { CachedButtons = false; m_instance.m_buttons.Clear(); }
    public static void Update(float dt) { foreach (var button in m_instance.m_buttons.Values) button.Tick(); HarmonyLib.Harmony.After(typeof(ZInput), "Update", typeof(float)); }
    public static void FixedUpdate(float dt) { foreach (var button in m_instance.m_buttons.Values) button.Tick(); HarmonyLib.Harmony.After(typeof(ZInput), "FixedUpdate", typeof(float)); }
    public static bool GetKey(KeyCode key, bool ignored = false) { return Input.GetKey(key); }
    public static bool GetKeyDown(KeyCode key, bool ignored = false) { return Input.GetKeyDown(key); }
    public static bool GetButton(string name) { return HarmonyLib.Harmony.Query(typeof(ZInput), "GetButton", name, !CachedButtons || Button(name).Held); }
    public static bool GetButtonDown(string name) { return HarmonyLib.Harmony.Query(typeof(ZInput), "GetButtonDown", name, !CachedButtons || Button(name).Pressed); }
    public static bool GetButtonUp(string name) { return HarmonyLib.Harmony.Query(typeof(ZInput), "GetButtonUp", name, !CachedButtons || Button(name).Released); }
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
namespace ValheimModPack.ExpeditionLoadouts
{
    public class PresetStore
    {
        public bool ReadOnly; public string LoadError; public long PlayerId;
        public PresetStore(string directory, long playerId) { PlayerId = playerId; }
    }
    public class ChestService
    {
        public int Ticks, Cancels, Disposes;
        public ChestService(BepInEx.TestLogger logger) { }
        public void Tick() { Ticks++; }
        public void Cancel() { Cancels++; }
        public void Dispose() { Disposes++; }
    }
    public class LoadoutWindow
    {
        private readonly Plugin plugin;
        public bool IsVisible; public int Opens;
        public LoadoutWindow(Plugin plugin) { this.plugin = plugin; }
        public void Tick()
        {
            if (IsVisible && (!Plugin.ValidPlayer(Player.m_localPlayer) || Input.GetKeyDown(KeyCode.Escape)
                || Menu.IsVisible() || StoreGui.IsVisible() || Hud.IsPieceSelectionVisible()
                || PlayerCustomizaton.IsBarberGuiVisible() || ZInput.s_IsRebindActive)) Hide();
        }
        public void Show()
        {
            if (InventoryGui.IsVisible()) throw new Exception("Inventory still visible");
            if (plugin.Store == null) throw new Exception("Preset store not initialized");
            Hide(); IsVisible = true; Opens++; Jotunn.Managers.GUIManager.BlockInput(true);
        }
        public void Hide()
        {
            if (IsVisible) Jotunn.Managers.GUIManager.BlockInput(false);
            IsVisible = false;
            if (plugin.Service != null) plugin.Service.Cancel();
        }
    }
}
