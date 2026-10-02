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
        public bool isActiveAndEnabled = true; public object Config;
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
public static class Menu { public static bool IsVisible() { return false; } }
public static class Console { public static bool Visible; public static bool IsVisible() { return Visible; } }
public static class InventoryGui { public static bool Visible; public static bool IsVisible() { return Visible; } }
public class Chat { public static Chat instance; public bool Focus; public bool HasFocus() { return Focus; } }
public class TextInput
{
    public class Panel { public bool activeInHierarchy; }
    public static TextInput instance; public Panel m_panel; public static bool ForeignVisible;
    public static bool IsVisible() { return ForeignVisible || (instance != null && instance.m_panel != null && instance.m_panel.activeInHierarchy); }
}
public class ZNet { public static ZNet instance = new ZNet(); public long World = 100; public long GetWorldUID() { return World; } }
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
    public long Id = 200;
    public long GetPlayerID() { return Id; }
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
    internal sealed class MapControls
    {
        internal sealed class Entry { internal object Value; }
        internal Entry History = new Entry(), Quick = new Entry(), Place = new Entry(), Rename = new Entry(), ClearDeathPins = new Entry();
        internal MapControls(object config) { }
        internal static bool Down(object key) { return false; }
        internal static bool Held(object key) { return false; }
        internal static string Label(object key) { return "Ctrl+Shift+Delete"; }
    }
    public sealed class QuickPinController : IDisposable
    {
        public static QuickPinController Last;
        public bool IsBusy, LastAllowed; public int Renamed; public Func<bool> OpenShortcut, PlaceModifier, RenameModifier; public Func<string> ShortcutLabel;
        public QuickPinController(HarmonyLib.Harmony harmony, PinHistoryController history, System.Reflection.FieldInfo pins, Action<Exception> report) { Last = this; }
        public void Tick(bool allowed) { LastAllowed = allowed; if (!allowed) Close(); }
        public void Close() { IsBusy = false; }
        public void Dispose() { Close(); }
        public string DisplayName(Minimap.PinData pin) { return pin.m_name; }
        public void Rename(Minimap.PinData pin) { Renamed++; IsBusy = true; }
    }
    public sealed class PinHistoryController : IDisposable
    {
        public static PinHistoryController Last;
        public bool IsOpen, LastAllowed; public Func<bool> OpenShortcut;
        public PinHistoryController(HarmonyLib.Harmony harmony, System.Reflection.FieldInfo pins, Action<Exception> report) { Last = this; }
        public void Tick(bool canOpen) { LastAllowed = canOpen; if (!canOpen) Close(); }
        public void Close() { IsOpen = false; }
        public void Dispose() { Close(); }
        public void Remove(Minimap map, Minimap.PinData pin) { map.RemovePin(pin); }
        internal static bool SafePlayer(Player player) { return player != null && !player.IsDead(); }
    }
    public sealed class PinActionController : IDisposable
    {
        public static PinActionController Last;
        private readonly Action<Minimap.PinData> rename, delete;
        private Minimap map; private Player player; private ZNet network; private long world, character;
        private Minimap.PinData target;
        public bool IsOpen { get { return target != null; } }
        public PinActionController(FieldInfo pins, Func<Minimap.PinData, string> displayName,
            Action<Minimap.PinData> rename, Action<Minimap.PinData> requestDelete, Action<Exception> report)
        { this.rename = rename; delete = requestDelete; Last = this; }
        public void Show(Minimap map, Minimap.PinData pin)
        {
            this.map = map; player = Player.m_localPlayer; network = ZNet.instance;
            world = network.GetWorldUID(); character = player.GetPlayerID(); target = pin;
        }
        private bool Valid()
        {
            return IsOpen && ReferenceEquals(map, Minimap.instance) && map.m_mode == Minimap.MapMode.Large
                && ReferenceEquals(player, Player.m_localPlayer) && PinHistoryController.SafePlayer(player)
                && ReferenceEquals(network, ZNet.instance) && network.GetWorldUID() == world && player.GetPlayerID() == character
                && target.m_save && map.Contains(target) && !UnifiedPopup.IsVisible() && !Console.IsVisible()
                && !InventoryGui.IsVisible() && (Chat.instance == null || !Chat.instance.HasFocus())
                && (TextInput.instance == null || TextInput.instance.m_panel == null || !TextInput.instance.m_panel.activeInHierarchy);
        }
        public void ChooseRename() { var pin = target; bool valid = Valid(); Close(); if (valid) rename(pin); }
        public void ChooseDelete() { var pin = target; bool valid = Valid(); Close(); if (valid) delete(pin); }
        public void Tick(bool allowed) { if (!allowed || !Valid() || UnityEngine.Input.Escape || ZInput.Cancel) Close(); }
        public void Close() { target = null; }
        public void Dispose() { Close(); }
    }
    public sealed class DeathPinController : IDisposable
    {
        public static DeathPinController Last;
        public bool IsOpen, LastAllowed; public Func<bool> OpenShortcut; public Func<string> ShortcutLabel;
        public DeathPinController(FieldInfo pins, PinHistoryController history, Action<Exception> report) { Last = this; }
        public void Tick(bool allowed) { LastAllowed = allowed; if (!allowed) Close(); }
        public void Open() { if (LastAllowed) IsOpen = true; }
        public void Close() { IsOpen = false; }
        public void Dispose() { Close(); }
    }
    internal sealed class PinSuggestionController : IDisposable
    {
        internal static PinSuggestionController Last;
        public bool IsVisible, LastAllowed;
        public PinSuggestionController(QuickPinController quick, PinHistoryController history, MapControls controls, Action<Exception> report) { Last = this; }
        public void Tick(bool allowed) { LastAllowed = allowed; if (!allowed) Close(); }
        public void Close() { IsVisible = false; }
        public void Dispose() { Close(); }
    }
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
