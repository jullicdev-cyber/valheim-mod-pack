// Test-only boundaries: the production Plugin, Registry, Search and Shortcut sources run unchanged.
using System;
using System.Collections.Generic;
using System.Reflection;
namespace UnityEngine
{
    public class Object
    {
        public bool Destroyed;
        public static implicit operator bool(Object value) { return !ReferenceEquals(value,null) && !value.Destroyed; }
        public static void Destroy(Object value) { if(value) value.Destroyed=true; }
    }
    public class Component : Object
    {
        public GameObject gameObject;
        public Transform transform { get { return gameObject==null ? null : gameObject.transform; } }
    }
    public class Behaviour : Component
    {
        public bool enabled=true;
        public bool isActiveAndEnabled { get { return enabled && (gameObject==null || gameObject.activeInHierarchy); } }
    }
    public class Transform : Component { public Vector3 position; }
    public class GameObject : Object
    {
        public string name; public bool activeSelf=true;
        public Vector2 TestPosition; public float TestWidth, TestHeight;
        public bool activeInHierarchy { get { return !Destroyed && activeSelf; } }
        public readonly Transform transform;
        public readonly List<Component> Components=new List<Component>();
        public GameObject() { transform=new Transform { gameObject=this }; }
        public void SetActive(bool value) { activeSelf=value; }
        public T AddComponent<T>() where T : Component,new() { var value=new T { gameObject=this }; Components.Add(value); return value; }
        public T GetComponent<T>() where T : class { foreach(var value in Components) if(value is T) return value as T; return null; }
        public T GetComponentInParent<T>() where T : class { return GetComponent<T>(); }
        public T GetComponentInChildren<T>(bool inactive=false) where T : class { return GetComponent<T>(); }
        public T[] GetComponentsInParent<T>() where T : class { return GetComponentsInChildren<T>(); }
        public T[] GetComponentsInChildren<T>(bool inactive=false) where T : class
        { var result=new List<T>(); foreach(var value in Components) if(value is T) result.Add(value as T); return result.ToArray(); }
    }
    public struct Vector2 { public float x,y; public Vector2(float x,float y) { this.x=x; this.y=y; } }
    public struct Vector3 { public float x,y,z; public Vector3(float x,float y,float z) { this.x=x; this.y=y; this.z=z; } }
    public struct Color { public static readonly Color black; }
    public class Font : Object { }
    public enum TextAnchor { UpperCenter, MiddleCenter }
    public enum KeyCode { None=0, Escape=27, F=102, G=103, J=106, Alpha0=48, Alpha1=49, Alpha2=50, Alpha3=51,
        Alpha4=52, Alpha5=53, Alpha6=54, Alpha7=55, Alpha8=56, Alpha9=57, KeypadEnter=271,
        LeftControl=306, RightControl=305, LeftShift=304, RightShift=303, LeftAlt=308, RightAlt=307, LeftCommand=310, RightCommand=309 }
    public static class Time { public static float unscaledTime=100; }
    public static class Input
    {
        public static readonly HashSet<KeyCode> Down=new HashSet<KeyCode>(),Held=new HashSet<KeyCode>();
        public static bool GetKeyDown(KeyCode key) { return Down.Contains(key); }
        public static bool GetKey(KeyCode key) { return Held.Contains(key); }
    }
}
namespace UnityEngine.UI
{
    public enum HorizontalWrapMode { Wrap }
    public enum VerticalWrapMode { Truncate }
    public sealed class Text : UnityEngine.Behaviour
    { public string text=""; public bool supportRichText=true,raycastTarget=true,resizeTextForBestFit; public int fontSize,resizeTextMinSize,resizeTextMaxSize;
        public UnityEngine.TextAnchor alignment; public HorizontalWrapMode horizontalOverflow; public VerticalWrapMode verticalOverflow; }
    public sealed class InputField : UnityEngine.Behaviour { public bool isFocused; }
    public sealed class Button : UnityEngine.Behaviour { public readonly ClickEvent onClick=new ClickEvent(); }
    public sealed class ClickEvent { private Action callback; public void AddListener(Action value) { callback+=value; } public void Invoke() { if(callback!=null) callback(); } }
}
namespace UnityEngine.EventSystems { public sealed class EventSystem { public static EventSystem current=new EventSystem(); public UnityEngine.GameObject currentSelectedGameObject; } }
namespace BepInEx
{
    public sealed class BepInPlugin : Attribute { public BepInPlugin(string id,string name,string version) { } }
    [AttributeUsage(AttributeTargets.Class,AllowMultiple=true)]
    public sealed class BepInDependency : Attribute
    { public enum DependencyFlags { SoftDependency=2 } public BepInDependency(string id,string version) { } public BepInDependency(string id,DependencyFlags flags) { } }
    public class BaseUnityPlugin : UnityEngine.Behaviour { public readonly Configuration.ConfigFile Config=new Configuration.ConfigFile(); public readonly TestLogger Logger=new TestLogger(); }
    public sealed class PluginInfo { public BaseUnityPlugin Instance { get; set; } }
    public sealed class TestLogger { public readonly List<object> Errors=new List<object>(); public void LogInfo(object value) { } public void LogError(object value) { Errors.Add(value); } }
}
namespace BepInEx.Bootstrap
{ public static class Chainloader { public static readonly Dictionary<string,BepInEx.PluginInfo> PluginInfos=new Dictionary<string,BepInEx.PluginInfo>(); } }
namespace BepInEx.Configuration
{
    public sealed class ConfigEntry<T> { public T Value; }
    public sealed class ConfigFile { public ConfigEntry<T> Bind<T>(string section,string key,T value,string help) { return new ConfigEntry<T> { Value=value }; } }
    public struct KeyboardShortcut
    {
        public UnityEngine.KeyCode MainKey { get; private set; } public IEnumerable<UnityEngine.KeyCode> Modifiers { get; private set; }
        public KeyboardShortcut(UnityEngine.KeyCode key,params UnityEngine.KeyCode[] modifiers) : this()
        {
            MainKey=key;
            var stored=(UnityEngine.KeyCode[])modifiers.Clone();
            Array.Sort(stored); // BepInEx SanitizeKeys stores numeric enum order.
            Modifiers=stored;
        }
    }
}
namespace HarmonyLib
{
    public static class Priority { public const int First=800; }
    public sealed class HarmonyPatch : Attribute { public HarmonyPatch(Type type,string method) { } }
    public sealed class HarmonyPriority : Attribute { public HarmonyPriority(int priority) { } }
    public sealed class HarmonyBefore : Attribute { public HarmonyBefore(params string[] owners) { } }
    public sealed class HarmonyMethod { public HarmonyMethod(MethodInfo method) { } }
    public sealed class Harmony
    { public int Unpatches; public Harmony(string id) { } public void Patch(MethodBase method,HarmonyMethod postfix) { } public void Unpatch(MethodBase method,MethodInfo postfix) { } public void PatchAll(Assembly assembly) { } public void UnpatchSelf() { Unpatches++; } }
    public static class AccessTools
    {
        private const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        public static Type TypeByName(string name) { return typeof(AccessTools).Assembly.GetType(name); }
        public static FieldInfo Field(Type type,string name) { return type.GetField(name,All); }
        public static PropertyInfo Property(Type type,string name) { return type.GetProperty(name,All); }
        public static MethodInfo Method(Type type,string name,Type[] parameters=null)
        { return parameters==null ? type.GetMethod(name,All) : type.GetMethod(name,All,null,parameters,null); }
    }
}
namespace Jotunn.Managers
{
    public sealed class GUIManager
    {
        public static readonly GUIManager Instance=new GUIManager(); public static UnityEngine.GameObject CustomGUIFront=new UnityEngine.GameObject();
        public readonly UnityEngine.Font AveriaSerif=new UnityEngine.Font(); public readonly UnityEngine.Color ValheimBeige;
        public UnityEngine.GameObject CreateButton(string text,UnityEngine.Transform parent,UnityEngine.Vector2 a,UnityEngine.Vector2 b,UnityEngine.Vector2 pos,float w,float h)
        { var result=new UnityEngine.GameObject { TestPosition=pos,TestWidth=w,TestHeight=h }; result.AddComponent<UnityEngine.UI.Button>(); result.AddComponent<ButtonSfx>(); result.AddComponent<UnityEngine.UI.Text>().text=text; return result; }
        public UnityEngine.GameObject CreateText(string text,UnityEngine.Transform parent,UnityEngine.Vector2 a,UnityEngine.Vector2 b,UnityEngine.Vector2 pos,UnityEngine.Font font,int size,UnityEngine.Color color,bool outline,UnityEngine.Color shadow,float w,float h,bool rich)
        { var result=new UnityEngine.GameObject { TestPosition=pos,TestWidth=w,TestHeight=h }; result.AddComponent<UnityEngine.UI.Text>().text=text; return result; }
    }
}
namespace Splatform { public struct PlatformUserID { } }
namespace ValheimModPack.PinRemoval
{
    public sealed class QuickPinController { public bool IsBusy { get; set; } }
    public sealed class Plugin : BepInEx.BaseUnityPlugin { public readonly QuickPinController quick=new QuickPinController(); }
}
public sealed class ButtonSfx : UnityEngine.Component { public UnityEngine.GameObject m_selectSfxPrefab; }
public sealed class Player : UnityEngine.Object
{
    public static Player m_localPlayer; public readonly UnityEngine.Transform transform=new UnityEngine.GameObject().transform;
    public bool Dead,Teleporting,Sleeping,Cutscene;
    public bool IsDead() { return Dead; } public bool IsTeleporting() { return Teleporting; } public bool IsSleeping() { return Sleeping; } public bool InCutscene() { return Cutscene; }
}
public sealed class ZNet : UnityEngine.Object { public static ZNet instance; public long World=1; public bool IsServer() { return true; } public long GetWorldUID() { return World; } }
public sealed class ZPackage { }
public struct ZDOID
{
    public static readonly ZDOID None; public int Value; public ZDOID(int value) { Value=value; }
    public static bool operator ==(ZDOID a,ZDOID b) { return a.Value==b.Value; } public static bool operator !=(ZDOID a,ZDOID b) { return a.Value!=b.Value; }
    public override bool Equals(object other) { return other is ZDOID && this==(ZDOID)other; } public override int GetHashCode() { return Value; } public override string ToString() { return "id:"+Value; }
}
public sealed class ZDO
{ public ZDOID m_uid; public string Name; public UnityEngine.Vector3 Position; public bool IsValid() { return m_uid!=ZDOID.None; } public string GetString(string key,string fallback) { return Name; } public UnityEngine.Vector3 GetPosition() { return Position; } }
public sealed class ZDOMan { public static ZDOMan instance=new ZDOMan(); public readonly List<ZDO> Portals=new List<ZDO>(); public int Reads; public List<ZDO> GetPortalList() { Reads++; return Portals; } }
public static class ZInput { public static bool s_IsRebindActive; public static UnityEngine.Vector3 pointerPosition; }
public static class UnifiedPopup { public static bool Visible; public static bool IsVisible() { return Visible; } }
public static class Menu { public static bool Visible; public static bool IsVisible() { return Visible; } }
public static class Console { public static bool Visible; public static bool IsVisible() { return Visible; } }
public static class InventoryGui { public static bool Visible; public static bool IsVisible() { return Visible; } }
public static class TextInput { public static bool Visible; public static bool IsVisible() { return Visible; } }
public sealed class Chat { public static Chat instance; public bool Focus; public bool HasFocus() { return Focus; } }
public sealed class Localization { public static Localization instance=new Localization(); public string Language="English"; public string GetSelectedLanguage() { return Language; } }
public sealed class Minimap : UnityEngine.Object
{
    public static Minimap instance; public static bool TextFocused; public enum MapMode { Small,Large } public enum PinType { Icon4=6 }
    public MapMode m_mode=MapMode.Large; public readonly List<PinData> Pins=new List<PinData>(); public int Adds,Removes,Centers,NativeClicks,NativeDoubleClicks;
    public UnityEngine.Vector3 Offset,ShownPoint;
    public static bool InTextInput() { return TextFocused; }
    private UnityEngine.Vector3 ScreenToWorldPoint(UnityEngine.Vector3 pointer) { return new UnityEngine.Vector3(pointer.x+Offset.x,0,pointer.y+Offset.z); }
    public void ShowPointOnMap(UnityEngine.Vector3 point) { ShownPoint=point; Centers++; }
    public PinData AddPin(UnityEngine.Vector3 position,PinType type,string name,bool save,bool check,long owner=0,Splatform.PlatformUserID author=default(Splatform.PlatformUserID))
    { var pin=new PinData { m_pos=position,m_name=name,m_save=save }; Pins.Add(pin); Adds++; return pin; }
    public void RemovePin(PinData pin) { if(Pins.Remove(pin)) Removes++; }
    public sealed class PinData { public UnityEngine.Vector3 m_pos; public string m_name; public bool m_save; }
}
