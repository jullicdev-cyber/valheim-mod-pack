// Boundary doubles for production PinHistoryController. Never shipped.
using System;
using System.Collections.Generic;
using System.Reflection;
namespace BepInEx { public static class Paths { public static string GameRootPath; } }
namespace ValheimModPack.PinRemoval
{
    public static class QuickPinController
    {
        public static string DisplayRecord(PinRecord pin) { return pin.Name; }
        public static void RenameFromHistory(Minimap.PinData pin) { }
        public static void RefreshKnownCaptions() { }
    }
}
namespace HarmonyLib
{
    public sealed class HarmonyMethod { public HarmonyMethod(Type type, string name) { } }
    public sealed class Harmony { public void Patch(MethodInfo method, HarmonyMethod prefix = null, HarmonyMethod postfix = null) { } }
    public static class AccessTools
    {
        public static MethodInfo Method(Type type, string name, Type[] args)
        { return type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static, null, args, null); }
        public static FieldInfo Field(Type type, string name)
        { return type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static); }
    }
}
namespace UnityEngine
{
    public enum KeyCode { H, LeftControl, RightControl, Escape }
    public static class Input
    {
        public static readonly HashSet<KeyCode> Down = new HashSet<KeyCode>(), Held = new HashSet<KeyCode>();
        public static bool GetKeyDown(KeyCode key) { return Down.Contains(key); }
        public static bool GetKey(KeyCode key) { return Held.Contains(key); }
    }
    public struct Vector2 { public float x, y; public Vector2(float x, float y) { this.x = x; this.y = y; } }
    public struct Vector3 { public float x, y, z; public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public float sqrMagnitude { get { return x*x+y*y+z*z; } }
        public static Vector3 operator -(Vector3 a, Vector3 b) { return new Vector3(a.x-b.x,a.y-b.y,a.z-b.z); } }
    public class Transform { public Vector3 position; }
    public static class Time { public static float realtimeSinceStartup; }
    public class Object { public static void Destroy(object value) { } }
    public class GameObject
    {
        public string name;
        public bool Active;
        public Transform transform = new Transform();
        private readonly UnityEngine.UI.Button button = new UnityEngine.UI.Button();
        private readonly ButtonSfx sound = new ButtonSfx();
        public T GetComponent<T>() where T : class { return typeof(T) == typeof(ButtonSfx) ? sound as T : button as T; }
        public void SetActive(bool value) { Active = value; }
    }
}
namespace UnityEngine.UI
{
    public sealed class Button
    { public readonly ClickEvent onClick = new ClickEvent(); public sealed class ClickEvent { public Action Callback; public void AddListener(Action value) { Callback += value; } } }
}
namespace Jotunn.Managers
{
    public sealed class GUIManager
    {
        public static UnityEngine.GameObject CustomGUIFront = new UnityEngine.GameObject();
        public static GUIManager Instance = new GUIManager();
        public UnityEngine.GameObject CreateButton(string title, UnityEngine.Transform parent, UnityEngine.Vector2 a, UnityEngine.Vector2 b, UnityEngine.Vector2 position, float width, float height)
        { return new UnityEngine.GameObject(); }
    }
}
public sealed class ButtonSfx { public object m_selectSfxPrefab; }
namespace Splatform
{
    public static class PlatformManager { public static Platform DistributionPlatform = new Platform(); }
    public sealed class Platform { public Local LocalUser = new Local(); }
    public sealed class Local { public PlatformUserID PlatformUserID = new PlatformUserID("Steam_200"); }
    public struct PlatformUserID
    {
        private string value;
        public bool IsValid { get { return !String.IsNullOrEmpty(value); } }
        public PlatformUserID(string value) { this.value = value; }
        public static PlatformUserID None { get { return new PlatformUserID(""); } }
        public static bool TryParse(string value, out PlatformUserID result) { result = new PlatformUserID(value); return value.StartsWith("Steam_"); }
        public override string ToString() { return value; }
    }
}
public sealed class Localization
{ public static Localization instance = new Localization(); public string GetSelectedLanguage() { return "Russian"; } }
public static class UnifiedPopup { public static bool Visible; public static bool IsVisible() { return Visible; } }
public static class Menu { public static bool Visible; public static bool IsVisible() { return Visible; } }
public static class Console { public static bool Visible; public static bool IsVisible() { return Visible; } }
public sealed class Chat { public static Chat instance; public bool Focus; public bool HasFocus() { return Focus; } }
public static class ZInput { public static bool Cancel; public static bool GetButtonDown(string name) { return Cancel; } }
public sealed class MessageHud { public enum MessageType { Center } }
public class Humanoid { public readonly UnityEngine.Transform transform = new UnityEngine.Transform(); }
public sealed class Player : Humanoid
{
    public static Player m_localPlayer;
    public long Id = 200;
    public string Name = "Скальд";
    public bool Dead, Teleporting, Sleeping, Cutscene;
    public int Warnings;
    public bool IsDead() { return Dead; }
    public bool IsTeleporting() { return Teleporting; }
    public bool IsSleeping() { return Sleeping; }
    public bool InCutscene() { return Cutscene; }
    public long GetPlayerID() { return Id; }
    public string GetPlayerName() { return Name; }
    public void Message(MessageHud.MessageType type, string text, int amount, object icon) { Warnings++; }
}
public sealed class ItemDrop { public sealed class ItemData { } }
public sealed class Switch { }
public static class PrivateArea { public static bool Allowed = true; public static bool CheckAccess(UnityEngine.Vector3 p, float r, bool flash, bool check) { return Allowed; } }
public sealed class ZPackage { private readonly byte[] bytes; public ZPackage(byte[] value) { bytes = value; } public byte[] GetArray() { return bytes; } public int Size() { return bytes.Length; } }
public sealed class ZDO
{
    public readonly Dictionary<string,byte[]> Data = new Dictionary<string,byte[]>();
    public byte[] GetByteArray(string key,byte[] fallback) { byte[] value; return Data.TryGetValue(key,out value)?value:fallback; }
    public void Set(string key,byte[] value) { Data[key]=value; }
}
public sealed class ZNetView
{
    public bool Valid=true,Owner=true; public readonly ZDO Data=new ZDO();
    public readonly Dictionary<string,Action<long,ZPackage>> Rpcs = new Dictionary<string,Action<long,ZPackage>>();
    public bool IsValid() { return Valid; } public bool IsOwner() { return Owner; } public ZDO GetZDO() { return Data; }
    public void Register<T>(string name,Action<long,T> callback) { Rpcs[name]=(sender,p)=>callback(sender,(T)(object)p); }
    public void InvokeRPC(string name,object[] args) { Rpcs[name](1,(ZPackage)args[0]); }
}
public sealed class MapTable
{
    private readonly ZNetView m_nview=new ZNetView(); public ZNetView View { get { return m_nview; } }
    public readonly UnityEngine.Transform transform=new UnityEngine.Transform();
    private void Start() { }
    private bool OnWrite(Switch s,Humanoid h,ItemDrop.ItemData i) { return true; }
    private bool OnRead(Switch s,Humanoid h,ItemDrop.ItemData i,bool message) { return false; }
    private ZPackage GetMapData(byte[] data) { return new ZPackage(data); }
    private void RPC_MapData(long sender,ZPackage package) { }
}
public sealed class ZNet
{
    public static ZNet instance;
    public long World = 100;
    public long GetWorldUID() { return World; }
    public sealed class UserInfo { public Splatform.PlatformUserID m_id; }
    public sealed class PlayerInfo { public string m_name; public UserInfo m_userInfo = new UserInfo(); }
    public List<PlayerInfo> Players = new List<PlayerInfo>();
    public List<PlayerInfo> GetPlayerList() { return Players; }
}
public sealed class Minimap
{
    public enum MapMode { None, Small, Large }
    public enum PinType { Icon0, Icon1, Icon2 }
    public sealed class PinData
    {
        public string m_name = "";
        public bool m_save = true, m_checked, m_doubleSize, m_animate;
        public PinType m_type;
        public UnityEngine.Vector3 m_pos;
        public long m_ownerID;
        public Splatform.PlatformUserID m_author;
        public float m_worldSize;
    }
    public static Minimap instance;
    public static bool Naming;
    public MapMode m_mode = MapMode.Large;
    private readonly List<PinData> m_pins = new List<PinData>();
    public PinData m_namePin;
    public bool FailRemove;
    public PinData FailSpecificRemove;
    public Action<PinData> BeforeRemove;
    public int Added, Removed;
    private void ShowPinNameInput(UnityEngine.Vector3 value) { }
    private void OnMapLeftClick() { }
    public static bool InTextInput() { return Naming; }
    public bool Contains(PinData value) { return m_pins.Contains(value); }
    public PinData AddPin(UnityEngine.Vector3 pos, PinType type, string name, bool save, bool isChecked, long ownerID, Splatform.PlatformUserID author)
    {
        var pin = new PinData { m_pos = pos, m_type = type, m_name = name, m_save = save, m_checked = isChecked, m_ownerID = ownerID, m_author = author };
        m_pins.Add(pin); Added++; return pin;
    }
    public void RemovePin(PinData pin)
    {
        if (BeforeRemove != null) BeforeRemove(pin);
        if (FailRemove || Object.ReferenceEquals(pin, FailSpecificRemove)) throw new Exception("Game removal failed");
        if (m_pins.Remove(pin)) Removed++;
    }
}
namespace ValheimModPack.PinRemoval
{
    public sealed class PinHistoryWindow
    {
        public static PinHistoryWindow Last;
        public bool IsVisible, OwnsInputBlock;
        public IList<HistoryRow> Rows;
        public bool Deleted;
        public Action<bool> Tab;
        public Action<int> Page;
        public Action Cancel;
        public PinHistoryWindow() { Last = this; }
        public void Show(Action cancel, Action<bool> tab, Action<int> page) { Cancel = cancel; Tab = tab; Page = page; IsVisible = true; OwnsInputBlock = true; }
        public void Render(IList<HistoryRow> rows, bool deleted, int page, int pages, int count, bool ru) { Rows = rows; Deleted = deleted; }
        public void CleanupHidden() { if (!IsVisible && OwnsInputBlock) Hide(); }
        public void Hide() { IsVisible = false; OwnsInputBlock = false; }
    }
}
