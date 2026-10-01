using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ValheimModPack.PortalFinder;

namespace UnityEngine
{
    public struct Vector3 { public float x, y, z; public Vector3(float x, float y, float z) { this.x=x; this.y=y; this.z=z; } }
}
namespace HarmonyLib
{
    public sealed class HarmonyMethod { public HarmonyMethod(MethodInfo method) { } }
    public sealed class Harmony
    {
        public int Patches, Unpatches, FailAt;
        public void Patch(MethodBase method, HarmonyMethod postfix)
        { Patches++; if(Patches==FailAt) throw new InvalidOperationException("fake patch failure"); }
        public void Unpatch(MethodBase method, MethodInfo postfix) { Unpatches++; }
    }
    public static class AccessTools
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        public static bool XPortalMissing;
        public static Type TypeByName(string name) { return XPortalMissing ? null : typeof(AccessTools).Assembly.GetType(name); }
        public static PropertyInfo Property(Type type, string name) { return type.GetProperty(name, All); }
        public static MethodInfo Method(Type type, string name, Type[] parameters = null)
        { return parameters == null ? type.GetMethod(name, All) : type.GetMethod(name, All, null, parameters, null); }
    }
}
public struct ZDOID
{
    public static readonly ZDOID None;
    public int Value;
    public ZDOID(int value) { Value=value; }
    public static bool operator ==(ZDOID a, ZDOID b) { return a.Value==b.Value; }
    public static bool operator !=(ZDOID a, ZDOID b) { return a.Value!=b.Value; }
    public override bool Equals(object other) { return other is ZDOID && this==(ZDOID)other; }
    public override int GetHashCode() { return Value; }
    public override string ToString() { return "id:"+Value; }
}
public sealed class ZPackage { }
public sealed class ZDO
{
    public ZDOID m_uid;
    public bool Valid=true;
    public string Name="";
    public UnityEngine.Vector3 Position;
    public bool IsValid() { return Valid; }
    public string GetString(string key, string fallback) { return Name; }
    public UnityEngine.Vector3 GetPosition() { return Position; }
}
public sealed class ZDOMan
{
    public static ZDOMan instance=new ZDOMan();
    public List<ZDO> Portals=new List<ZDO>();
    public int Reads;
    public List<ZDO> GetPortalList() { Reads++; return Portals; }
}
public sealed class ZNet
{
    public static ZNet instance;
    public bool Server;
    public long World=1;
    public bool IsServer() { return Server; }
    public long GetWorldUID() { return World; }
}
namespace XPortal
{
    internal sealed class KnownPortalsManager
    {
        public static KnownPortalsManager Instance { get; private set; }
        static KnownPortalsManager() { Instance=new KnownPortalsManager(); }
        public List<KnownPortal> Portals=new List<KnownPortal>();
        public bool Throw;
        public List<KnownPortal> GetList()
        { if(Throw) throw new InvalidOperationException("fake XPortal error"); return new List<KnownPortal>(Portals); }
        public void UpdateFromResyncPackage(ZPackage package) { }
        public void Reset() { }
    }
    public sealed class KnownPortal
    {
        public ZDOID Id { get; set; }
        public string Name { get; set; }
        public UnityEngine.Vector3 Location { get; set; }
    }
}
