// Test-only stand-ins. These files are excluded from the shipped plugin build.
using System;
using System.Collections.Generic;
namespace BepInEx { public class BaseUnityPlugin { } }
namespace UnityEngine
{
    public static class Time { public static float realtimeSinceStartup; }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x=x; this.y=y; this.z=z; }
        public static Vector3 operator -(Vector3 a, Vector3 b) { return new Vector3(a.x-b.x,a.y-b.y,a.z-b.z); }
        public float sqrMagnitude { get { return x*x+y*y+z*z; } }
    }
    public class Transform { public Vector3 position; }
}
public static class TestStableHash { public static int GetStableHashCode(this string value) { unchecked { int h=0; foreach(char c in value) h=h*31+c; return h; } } }
public struct ZDOID : IEquatable<ZDOID>
{
    public long UserID; public uint ID;
    public ZDOID(long user, uint id) { UserID=user; ID=id; }
    public bool IsNone() { return UserID==0 && ID==0; }
    public bool Equals(ZDOID other) { return UserID==other.UserID && ID==other.ID; }
    public override bool Equals(object other) { return other is ZDOID && Equals((ZDOID)other); }
    public override int GetHashCode() { return UserID.GetHashCode() ^ ID.GetHashCode(); }
}
public class ZDO
{
    public int Prefab; public UnityEngine.Vector3 Position;
    public int GetPrefab() { return Prefab; }
    public UnityEngine.Vector3 GetPosition() { return Position; }
}
public class ZDOMan
{
    public static ZDOMan instance;
    public readonly Dictionary<ZDOID,ZDO> Objects=new Dictionary<ZDOID,ZDO>();
    public ZDO GetZDO(ZDOID id) { ZDO zdo; return Objects.TryGetValue(id,out zdo)?zdo:null; }
}
public class Player
{
    public static Player m_localPlayer;
    public readonly UnityEngine.Transform transform=new UnityEngine.Transform();
    public bool IsDead() { return false; }
}
public class ZNetPeer
{
    public long m_uid; public ZDOID m_characterID; public bool Ready=true;
    public bool IsReady() { return Ready; }
}
public class ZNet
{
    public static ZNet instance;
    public long Uid, World=1; public bool Host;
    public double ClockShift;
    public readonly List<ZNetPeer> Peers=new List<ZNetPeer>();
    public bool IsServer() { return Host; }
    public static long GetUID() { return instance.Uid; }
    public long GetWorldUID() { return World; }
    public object GetWorld() { return this; }
    public double GetTimeSeconds() { return 1000 + UnityEngine.Time.realtimeSinceStartup + ClockShift; }
    public ZNetPeer GetPeer(long uid) { return Peers.Find(p=>p.m_uid==uid); }
    public ZNetPeer GetServerPeer() { return GetPeer(1); }
    public List<ZNetPeer> GetConnectedPeers() { return Peers; }
}
public class ZPackage
{
    private readonly byte[] data;
    public ZPackage(byte[] data) { this.data=data; }
    public int Size() { return data.Length; }
    public byte[] GetArray() { return data; }
}
public class ZRoutedRpc
{
    public static ZRoutedRpc instance;
    public readonly Dictionary<string,Action<long,ZPackage>> Methods=new Dictionary<string,Action<long,ZPackage>>();
    public Action<long,string,ZPackage> Transport;
    public void Register<T>(string name,Action<long,T> action) { Methods.Add(name,(sender,package)=>action(sender,(T)(object)package)); }
    public void InvokeRoutedRPC(long peer,string name,params object[] arguments) { Transport(peer,name,(ZPackage)arguments[0]); }
}
