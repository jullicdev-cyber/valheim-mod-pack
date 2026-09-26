// Lifecycle regression harness: compiles the real Plugin.cs with small host doubles.
// It tests delayed config binding, not Unity gameplay or Harmony patch execution.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Bootstrap;

namespace UnityEngine { public enum KeyCode { None, O } }
public struct Vector2i { public int x, y; }
public class Inventory { public int GetWidth() { return 8; } public int GetHeight() { return 8; } }
public class Player { public static Player m_localPlayer; public Inventory GetInventory() { return null; } }
namespace EquipmentAndQuickSlots { public static class API { public static int GetVisibleRows() { return 5; } public static int GetFullHeight() { return 8; } } }
namespace BepInEx
{
    public class BepInPlugin : Attribute { public BepInPlugin(string a,string b,string c) {} }
    [AttributeUsage(AttributeTargets.Class,AllowMultiple=true)]
    public class BepInDependency : Attribute { public BepInDependency(string a,string b) {} }
    public class Log { public int Errors; public void LogInfo(object o) {} public void LogError(object o) { Errors++; } }
    public class BaseUnityPlugin { public ConfigFile Config = new ConfigFile(); public Log Logger = new Log(); }
}
namespace BepInEx.Configuration
{
    public class ConfigDefinition
    {
        private readonly string id;
        public ConfigDefinition(string section,string key) { id=section+"."+key; }
        public override bool Equals(object o) { return o is ConfigDefinition && ((ConfigDefinition)o).id==id; }
        public override int GetHashCode() { return id.GetHashCode(); }
    }
    public class ConfigEntryBase { public object BoxedValue; public Type SettingType; }
    public class ConfigFile : Dictionary<ConfigDefinition,ConfigEntryBase> { public bool SaveOnConfigSet=true; }
    public struct KeyboardShortcut { public UnityEngine.KeyCode MainKey; public KeyboardShortcut(UnityEngine.KeyCode key) { MainKey=key; } }
}
namespace BepInEx.Bootstrap
{
    public class Metadata { public System.Version Version; }
    public class Info { public BepInEx.BaseUnityPlugin Instance=new BepInEx.BaseUnityPlugin(); public Metadata Metadata=new Metadata(); }
    public static class Chainloader { public static Dictionary<string,Info> PluginInfos=new Dictionary<string,Info>(); }
}
namespace HarmonyLib
{
    public class HarmonyMethod { public HarmonyMethod(Type t,string s) {} }
    public class Harmony
    {
        public static int Patches;
        public Harmony(string s) {}
        public void Patch(MethodInfo t,HarmonyMethod prefix=null,HarmonyMethod postfix=null) { Patches++; }
        public void UnpatchSelf() { Patches=0; }
    }
    public static class AccessTools
    {
        public static MethodInfo Method(string s,Type[] types=null) { return typeof(StartupTests).GetMethod(s.Contains("InternalIs") ? "Filter" : "Sort"); }
    }
}
public static class StartupTests
{
    public enum Buttons { Both, OnlyContainerButton }
    public static bool Filter(int a,int b,Vector2i c,bool d) { return false; }
    public static void Sort(Inventory inv) {}
    private static void Check(bool ok,string reason) { if (!ok) throw new Exception(reason); }
    public static void Main()
    {
        var qs=new Info(); qs.Metadata.Version=new System.Version("1.4.15");
        var ea=new Info(); ea.Metadata.Version=new System.Version("3.1.3");
        Chainloader.PluginInfos.Add("goldenrevolver.quick_stack_store",qs);
        Chainloader.PluginInfos.Add("randyknapp.mods.equipmentandquickslots",ea);
        var plugin=new ValheimModPack.Plugin();
        var start=(IEnumerator)typeof(ValheimModPack.Plugin).GetMethod("Start",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(plugin,null);
        Check(start.MoveNext() && start.MoveNext(),"Must wait while Quick Stack Start has not bound config");
        Check(HarmonyLib.Harmony.Patches==0 && plugin.Logger.Errors==0,"Waiting must not fail initialization");
        var display=new ConfigEntryBase { SettingType=typeof(Buttons),BoxedValue=Buttons.OnlyContainerButton };
        qs.Instance.Config.Add(new ConfigDefinition("4 - Sorting","DisplaySortButtons"),display);
        Check(start.MoveNext(),"Must wait until both entries exist");
        var key=new ConfigEntryBase { SettingType=typeof(KeyboardShortcut),BoxedValue=new KeyboardShortcut(UnityEngine.KeyCode.None) };
        qs.Instance.Config.Add(new ConfigDefinition("4 - Sorting","SortKeybind"),key);
        Check(!start.MoveNext(),"Initialization should complete after config is bound");
        Check(HarmonyLib.Harmony.Patches==2 && plugin.Logger.Errors==0,"Both protection hooks must install before enabling controls");
        Check((Buttons)display.BoxedValue==Buttons.Both && ((KeyboardShortcut)key.BoxedValue).MainKey==UnityEngine.KeyCode.O,"O and buttons must enable");
        Check(qs.Instance.Config.SaveOnConfigSet,"SaveOnConfigSet must be restored");
        typeof(ValheimModPack.Plugin).GetMethod("OnDestroy",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(plugin,null);
        Check((Buttons)display.BoxedValue==Buttons.OnlyContainerButton && HarmonyLib.Harmony.Patches==0,"Shutdown must restore guard");
        Console.WriteLine("OK: real bridge source waits for delayed/partial config, installs both hooks, enables O, and restores guard on shutdown.");
    }
}
