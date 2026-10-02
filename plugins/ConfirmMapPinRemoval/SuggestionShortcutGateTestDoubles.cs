using System;
using System.Collections.Generic;
using System.Reflection;

namespace UnityEngine
{
    public enum KeyCode { None=0, G=103, F=102, W=119, H=104,P=112,Delete=127,Alpha0=48,Alpha9=57, Space=32, Mouse0=323,
        LeftControl=306,RightControl=305,LeftShift=304,RightShift=303,LeftAlt=308,RightAlt=307,LeftCommand=310,RightCommand=309 }
    public static class Time { public static int frameCount; public static bool inFixedTimeStep; }
    public static class Input
    {
        public static readonly HashSet<KeyCode> Held=new HashSet<KeyCode>(), Down=new HashSet<KeyCode>();
        public static int Reads;
        public static bool GetKey(KeyCode key) { ++Reads; return Held.Contains(key); }
        public static bool GetKeyDown(KeyCode key) { ++Reads; return Down.Contains(key); }
    }
}

namespace BepInEx.Configuration
{
    public struct KeyboardShortcut
    {
        private readonly UnityEngine.KeyCode main;
        private readonly UnityEngine.KeyCode[] modifiers;
        public KeyboardShortcut(UnityEngine.KeyCode key,params UnityEngine.KeyCode[] modifiers) { main=key; this.modifiers=modifiers; }
        public UnityEngine.KeyCode MainKey { get { return main; } }
        public IEnumerable<UnityEngine.KeyCode> Modifiers { get { return modifiers ?? Empty; } }
        private static readonly UnityEngine.KeyCode[] Empty=new UnityEngine.KeyCode[0];
    }
    public sealed class ConfigEntry<T> { public T Value; }
    public sealed class ConfigFile
    { public ConfigEntry<T> Bind<T>(string section,string key,T value,object description) { return new ConfigEntry<T>{Value=value}; } }
    public sealed class ConfigDescription { public ConfigDescription(string text,object range) {} }
    public sealed class AcceptableValueRange<T> { public AcceptableValueRange(T min,T max) {} }
}

namespace HarmonyLib
{
    public static class Priority { public const int First=800; }
    public static class AccessTools
    {
        public static FieldInfo Field(Type type,string name) { return type.GetField(name,BindingFlags.Instance|BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public); }
        public static MethodInfo Method(Type type,string name,Type[] parameters=null)
        {
            const BindingFlags flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public;
            return parameters==null ? type.GetMethod(name,flags) : type.GetMethod(name,flags,null,parameters,null);
        }
    }
    public sealed class HarmonyMethod
    { public MethodInfo method; public int priority; public HarmonyMethod(Type type,string name) { method=AccessTools.Method(type,name); } }
    public sealed class Harmony
    {
        private sealed class Hook { internal MethodBase Target; internal MethodInfo Callback; internal bool Before; }
        private static readonly List<Hook> Hooks=new List<Hook>();
        public Harmony(string id) { }
        public void Patch(MethodBase original,HarmonyMethod prefix=null,HarmonyMethod postfix=null)
        {
            if(prefix!=null) Hooks.Add(new Hook{Target=original,Callback=prefix.method,Before=true});
            if(postfix!=null) Hooks.Add(new Hook{Target=original,Callback=postfix.method,Before=false});
        }
        public void Unpatch(MethodBase original,MethodInfo callback)
        { Hooks.RemoveAll(delegate(Hook hook){return hook.Target==original&&hook.Callback==callback;}); }
        public static bool Before(string name,string button,ref bool result)
        {
            foreach(var hook in Hooks)
            {
                if(!hook.Before||hook.Target.Name!=name) continue;
                object[] args=new object[]{button,result};
                bool proceed=(bool)hook.Callback.Invoke(null,args); result=(bool)args[1];
                if(!proceed) return false;
            }
            return true;
        }
        public static void After(string name)
        { foreach(var hook in Hooks) if(!hook.Before&&hook.Target.Name==name) hook.Callback.Invoke(null,null); }
        public static int Count { get { return Hooks.Count; } }
    }
}

public sealed class ZInput
{
    private static ZInput m_instance;
    private readonly Dictionary<string,ButtonDef> m_buttons=new Dictionary<string,ButtonDef>();
    public static bool s_IsRebindActive;
    public static void NewInput()
    {
        m_instance=new ZInput();
        m_instance.Add("GP",UnityEngine.KeyCode.G);
        m_instance.Add("Forward",UnityEngine.KeyCode.W);
        m_instance.Add("Jump",UnityEngine.KeyCode.Space);
        m_instance.Add("Crouch",UnityEngine.KeyCode.LeftControl);
        m_instance.Add("RightCrouch",UnityEngine.KeyCode.RightControl);
        m_instance.Add("Use",UnityEngine.KeyCode.F);
        m_instance.m_buttons.Add("JoyButtonB",new ButtonDef{Path="<Gamepad>/buttonEast",Gamepad=true});
        m_instance.m_buttons.Add("NullVirtual",new ButtonDef{ButtonAction=null});
        m_instance.m_buttons.Add("HotbarUse",new ButtonDef{PathError=new IndexOutOfRangeException("no native binding zero"),Gamepad=true,GamepadHeld=true});
    }
    private void Add(string name,UnityEngine.KeyCode key) { m_buttons.Add(name,new ButtonDef{PhysicalKey=key,Path=KeyCodeToPath(key,false)}); }
    public static ButtonDef Button(string name) { return m_instance.m_buttons[name]; }
    public static void Stage()
    {
        foreach(var button in m_instance.m_buttons.Values)
        {
            bool held=button.Gamepad ? button.GamepadHeld : UnityEngine.Input.Held.Contains(button.PhysicalKey);
            button.Physical(held); button.Tick(false); button.Tick(true);
        }
    }
    public static bool GetKey(UnityEngine.KeyCode key,bool log) { return UnityEngine.Input.GetKey(key); }
    public static bool GetKeyDown(UnityEngine.KeyCode key,bool log) { return UnityEngine.Input.GetKeyDown(key); }
    public static bool IsKeyCodeValid(UnityEngine.KeyCode key) { return key!=UnityEngine.KeyCode.None; }
    public static bool GetButton(string name)
    { bool result=false; return HarmonyLib.Harmony.Before("GetButton",name,ref result) ? Button(name).Held : result; }
    public static bool GetButtonDown(string name)
    { bool result=false; return HarmonyLib.Harmony.Before("GetButtonDown",name,ref result) ? Button(name).Pressed : result; }
    public static bool GetButtonUp(string name)
    { bool result=false; return HarmonyLib.Harmony.Before("GetButtonUp",name,ref result) ? Button(name).Released : result; }
    public static void Update(float dt) { Stage(); HarmonyLib.Harmony.After("Update"); }
    public static void FixedUpdate(float dt) { Stage(); HarmonyLib.Harmony.After("FixedUpdate"); }
    private static string KeyCodeToPath(UnityEngine.KeyCode key,bool log)
    {
        switch(key)
        {
            case UnityEngine.KeyCode.LeftControl:return "<Keyboard>/leftCtrl";
            case UnityEngine.KeyCode.RightControl:return "<Keyboard>/rightCtrl";
            case UnityEngine.KeyCode.LeftShift:return "<Keyboard>/leftShift";
            case UnityEngine.KeyCode.RightShift:return "<Keyboard>/rightShift";
            case UnityEngine.KeyCode.LeftAlt:return "<Keyboard>/leftAlt";
            case UnityEngine.KeyCode.RightAlt:return "<Keyboard>/rightAlt";
            case UnityEngine.KeyCode.LeftCommand:return "<Keyboard>/leftMeta";
            case UnityEngine.KeyCode.RightCommand:return "<Keyboard>/rightMeta";
            case UnityEngine.KeyCode.Mouse0:return "<Mouse>/leftButton";
            default:return "<Keyboard>/"+key.ToString().ToLowerInvariant();
        }
    }
    public sealed class ButtonDef
    {
        // Exactly the native fields: no legacy m_key exists in this fixture.
        private bool m_heldDynamic,m_heldFixed,m_pressedDynamic,m_pressedFixed;
        private bool m_wasPressedDynamic,m_wasPressedFixed,m_releasedDynamic,m_releasedFixed;
        public object ButtonAction { get; set; }
        public string Path; public Exception PathError; public UnityEngine.KeyCode PhysicalKey; public bool Gamepad,GamepadHeld;
        public ButtonDef() { ButtonAction=new object(); }
        public string GetActionPath(bool effective) { if(PathError!=null) throw PathError; return Path; }
        public void Rebind(UnityEngine.KeyCode key) { PhysicalKey=key; Path=KeyCodeToPath(key,false); }
        public void Physical(bool held)
        {
            if(held&&!m_heldDynamic) { m_wasPressedDynamic=m_wasPressedFixed=false; }
            m_heldDynamic=m_heldFixed=held;
        }
        public void Tick(bool fixedTime)
        {
            if(fixedTime)
            { m_pressedFixed=!m_wasPressedFixed&&m_heldFixed; m_releasedFixed=m_wasPressedFixed&&!m_heldFixed; m_wasPressedFixed=m_heldFixed; }
            else
            { m_pressedDynamic=!m_wasPressedDynamic&&m_heldDynamic; m_releasedDynamic=m_wasPressedDynamic&&!m_heldDynamic; m_wasPressedDynamic=m_heldDynamic; }
        }
        public bool Held { get { return UnityEngine.Time.inFixedTimeStep ? m_heldFixed : m_heldDynamic; } }
        public bool Pressed { get { return UnityEngine.Time.inFixedTimeStep ? m_pressedFixed : m_pressedDynamic; } }
        public bool Released { get { return UnityEngine.Time.inFixedTimeStep ? m_releasedFixed : m_releasedDynamic; } }
        public bool AnyEdge { get { return m_pressedDynamic||m_pressedFixed||m_releasedDynamic||m_releasedFixed; } }
    }
}
