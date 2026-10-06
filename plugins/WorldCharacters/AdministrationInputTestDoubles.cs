// Standalone boundaries: the shipped input files are compiled unchanged.
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UnityEngine
{
    public enum KeyCode { None, F, F6, Escape, Space, LeftControl, RightControl, LeftShift, RightShift, LeftAlt, RightAlt, AltGr, LeftCommand, RightCommand, LeftWindows, RightWindows }
    public static class Time { public static int frameCount; public static float unscaledTime; }
    public static class Input
    {
        public static readonly HashSet<KeyCode> Held = new HashSet<KeyCode>(), Down = new HashSet<KeyCode>();
        public static bool GetKeyDown(KeyCode key) { return Down.Contains(key); }
    }
}
namespace BepInEx.Configuration
{
    public struct KeyboardShortcut
    {
        public KeyCode MainKey { get; private set; }
        public IEnumerable<KeyCode> Modifiers { get; private set; }
        public KeyboardShortcut(KeyCode key, params KeyCode[] modifiers) : this() { MainKey = key; Modifiers = modifiers; }
    }
    public sealed class ConfigEntry<T>
    {
        private T value;
        public event EventHandler SettingChanged;
        public T Value { get { return value; } set { this.value = value; if (SettingChanged != null) SettingChanged(this, EventArgs.Empty); } }
    }
}
namespace HarmonyLib
{
    public class HarmonyPatch : Attribute { public HarmonyPatch(Type type, string name, Type[] parameters) { } }
    public class HarmonyPriority : Attribute { public HarmonyPriority(int priority) { } }
    public static class Priority { public const int First = 800; }
    public static class AccessTools
    { public static FieldInfo Field(Type type, string name) { return type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance); } }
}
public sealed class Player
{
    public static Player m_localPlayer;
    public int GuardianStarts;
    public bool StartGuardianPower()
    {
        object[] args = { this, true };
        if (!FixtureCall.Prefix("AdminGuardianPowerInputPatch", args)) return (bool)args[1];
        GuardianStarts++; return true;
    }
}
public sealed class PlayerController
{
    public bool TakeInput()
    {
        object[] args = { Player.m_localPlayer, true };
        return FixtureCall.Prefix("AdminControllerInputPatch", args) ? true : (bool)args[1];
    }
}
public sealed class ZNet { public static ZNet instance; }
public sealed class ZInput
{
    public static bool s_IsRebindActive;
    private static ZInput m_instance = new ZInput();
    private readonly Dictionary<string, ButtonDef> m_buttons = new Dictionary<string, ButtonDef>();
    public sealed class ButtonDef
    {
        public bool m_heldDynamic, m_heldFixed, m_wasPressedDynamic, m_wasPressedFixed;
        public bool m_pressedDynamic, m_pressedFixed, m_releasedDynamic, m_releasedFixed;
        public void Press() { m_heldDynamic = m_heldFixed = m_pressedDynamic = m_pressedFixed = true; }
        public void Release() { m_heldDynamic = m_heldFixed = false; m_releasedDynamic = m_releasedFixed = true; }
    }
    public static ButtonDef Button(string name)
    { ButtonDef value; if (!m_instance.m_buttons.TryGetValue(name, out value)) m_instance.m_buttons[name] = value = new ButtonDef(); return value; }
    public static void Reset() { m_instance = new ZInput(); s_IsRebindActive = false; }
    public static bool GetKey(KeyCode key, bool ignored = false) { return Input.Held.Contains(key); }
    public static bool GetKeyDown(KeyCode key, bool ignored = false) { return Input.Down.Contains(key); }
    private static bool GetButtonState(string name, string patch, Func<ButtonDef, bool> read)
    {
        object[] args = { name, false };
        return FixtureCall.Prefix(patch, args) ? read(Button(name)) : (bool)args[1];
    }
    public static bool GetButton(string name) { return GetButtonState(name, "AdminHeldInputPatch", b => b.m_heldDynamic); }
    public static bool GetButtonDown(string name) { return GetButtonState(name, "AdminDownInputPatch", b => b.m_pressedDynamic); }
    public static bool GetButtonUp(string name) { return GetButtonState(name, "AdminUpInputPatch", b => b.m_releasedDynamic); }
    public static void Update(float ignored) { FixtureCall.Postfix("AdminDynamicInputPatch"); }
    public static void FixedUpdate(float ignored) { FixtureCall.Postfix("AdminFixedInputPatch"); }
}
internal static class FixtureCall
{
    private static Type Patch(string name) { return typeof(ValheimModPack.WorldCharacters.Plugin).GetNestedType(name, BindingFlags.NonPublic); }
    public static bool Prefix(string patch, object[] args) { return (bool)Patch(patch).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, args); }
    public static void Postfix(string patch) { Patch(patch).GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null); }
}
namespace ValheimModPack.WorldCharacters
{
    public sealed class InputWindowFixture
    {
        public bool IsVisible;
        public int Opens;
        public void Hide() { IsVisible = false; }
        public void Show() { IsVisible = true; Opens++; }
    }
    public sealed partial class Plugin
    {
        internal static Plugin Instance;
        internal readonly InputWindowFixture administrationWindow = new InputWindowFixture();
        internal bool isActiveAndEnabled = true, Allowed = true, OtherModal;
        internal readonly BepInEx.Configuration.ConfigEntry<BepInEx.Configuration.KeyboardShortcut> Binding
            = new BepInEx.Configuration.ConfigEntry<BepInEx.Configuration.KeyboardShortcut>();
        private bool CanOpenAdminWindow() { return Allowed && !OtherModal; }
        internal Plugin()
        {
            Instance = this; Binding.Value = new BepInEx.Configuration.KeyboardShortcut(KeyCode.F6, KeyCode.LeftControl);
            InitializeAdminInput(Binding);
        }
        internal void Tick() { PollAdminShortcut(); ProcessAdminPendingOpen(); }
        internal bool Blocks() { return AdminBlocksGameplay(); }
        internal void End() { DisposeAdminInput(); administrationWindow.Hide(); Instance = null; }
        internal void SessionReset() { administrationWindow.Hide(); ResetAdminInput(); }
    }
}
