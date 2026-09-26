// Minimal host doubles for the real Plugin.cs. Never included in the shipped DLL.
using System;
using System.Collections.Generic;
using System.Reflection;
namespace BepInEx
{
    [AttributeUsage(AttributeTargets.Class)] public class BepInPlugin : Attribute
    { public BepInPlugin(string id, string name, string version) { } }
    [AttributeUsage(AttributeTargets.Class)] public class BepInDependency : Attribute
    { public BepInDependency(string id, string version) { } }
    public class BaseUnityPlugin
    {
        public bool isActiveAndEnabled = true;
        public readonly Log Logger = new Log();
    }
    public class Log
    {
        public readonly List<object> Errors = new List<object>();
        public void LogInfo(object value) { }
        public void LogError(object value) { Errors.Add(value); }
    }
}
namespace HarmonyLib
{
    public class HarmonyMethod { public HarmonyMethod(Type type, string name) { } }
    public class Harmony
    {
        public static readonly List<string> Patched = new List<string>();
        public Harmony(string id) { Patched.Clear(); }
        public void Patch(MethodInfo target, HarmonyMethod prefix) { Patched.Add(target.Name); }
        public void UnpatchSelf() { Patched.Clear(); }
    }
    public static class AccessTools
    {
        public static string MissingMethod;
        public static MethodInfo Method(Type type, string name, Type[] args)
        { return name == MissingMethod ? null : type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static, null, args, null); }
        public static FieldInfo Field(Type type, string name)
        { return type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static); }
    }
}
namespace UnityEngine
{
    public enum KeyCode { Escape }
    public static class Input
    {
        public static bool Escape;
        public static bool GetKeyDown(KeyCode key) { return Escape; }
    }
}
public static class UnifiedPopup
{
    public static bool Visible;
    public static bool IsVisible() { return Visible; }
}
public static class ZInput
{
    public static bool Cancel;
    public static bool GetButtonDown(string name) { return Cancel; }
}
public class MessageHud { public enum MessageType { Center } }
public class Player
{
    public static Player m_localPlayer;
    public bool Dead;
    public int Warnings;
    public bool IsDead() { return Dead; }
    public void Message(MessageHud.MessageType kind, string text, int amount, object icon) { Warnings++; }
    private void OnDestroy() { }
}
public class Minimap
{
    public enum MapMode { None, Small, Large }
    public class PinData { public bool m_save = true; public string m_name = "База"; }
    public static Minimap instance;
    public MapMode m_mode = MapMode.Large;
    private readonly List<PinData> m_pins = new List<PinData>();
    public PinData Cursor;
    public int Deleted, NameInputClosed;
    public bool FailSelection;
    public void Add(PinData pin) { m_pins.Add(pin); }
    public bool Contains(PinData pin) { return m_pins.Contains(pin); }
    private void RemovePinUnderPointer() { throw new Exception("Vanilla deletion must be suppressed"); }
    private PinData GetClosestPinToCursor()
    { if (FailSelection) throw new Exception("selection failed"); return Cursor; }
    private void HidePinTextInput(bool delay) { NameInputClosed++; }
    public void RemovePin(PinData pin) { if (m_pins.Remove(pin)) Deleted++; }
}
namespace ValheimModPack.PinRemoval
{
    public class WoodDialogView : IDialogView
    {
        public static WoodDialogView Last;
        public Action Yes, No;
        public bool IsVisible { get; set; }
        public int Hides;
        public WoodDialogView() { Last = this; }
        public void Show(string name, Action confirm, Action cancel)
        { IsVisible = true; Yes = confirm; No = cancel; }
        public void Hide() { IsVisible = false; Hides++; }
    }
}
