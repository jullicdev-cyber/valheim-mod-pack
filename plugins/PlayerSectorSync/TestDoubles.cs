// Managed model of the native ordering and receiver rules, not a Unity/Steam run.
using System;
using System.Collections.Generic;

namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)] public sealed class HarmonyPatch : Attribute { public HarmonyPatch(Type type, string name) { } }
    [AttributeUsage(AttributeTargets.Method)] public sealed class HarmonyPrefix : Attribute { }
    [AttributeUsage(AttributeTargets.Method)] public sealed class HarmonyPostfix : Attribute { }
    [AttributeUsage(AttributeTargets.Method)] public sealed class HarmonyPriority : Attribute { public HarmonyPriority(int value) { } }
    public static class Priority { public const int Last = 0; }
}

namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x=x; this.y=y; this.z=z; }
    }
}

public struct ZDOID : IEquatable<ZDOID>
{
    private readonly long id;
    public ZDOID(long id) { this.id=id; }
    public bool IsNone() { return id == 0; }
    public bool Equals(ZDOID other) { return id == other.id; }
    public override bool Equals(object value) { return value is ZDOID && Equals((ZDOID)value); }
    public override int GetHashCode() { return id.GetHashCode(); }
    public static bool operator ==(ZDOID left, ZDOID right) { return left.Equals(right); }
    public static bool operator !=(ZDOID left, ZDOID right) { return !left.Equals(right); }
}

public sealed class ZoneSystem
{
    public struct SectorIndex : IEquatable<SectorIndex>
    {
        public int X, Z;
        public bool Outside;
        public SectorIndex(int x, int z, bool outside) { X=x; Z=z; Outside=outside; }
        public bool Equals(SectorIndex other) { return X==other.X && Z==other.Z && Outside==other.Outside; }
        public override bool Equals(object value) { return value is SectorIndex && Equals((SectorIndex)value); }
        public override int GetHashCode() { return X ^ (Z << 16) ^ (Outside ? -1 : 0); }
        public static bool operator ==(SectorIndex a, SectorIndex b) { return a.Equals(b); }
        public static bool operator !=(SectorIndex a, SectorIndex b) { return !a.Equals(b); }
    }
    public static SectorIndex SectorZero = new SectorIndex(0,0,true);
    public static SectorIndex GetSectorIndex(UnityEngine.Vector3 position)
    {
        return new SectorIndex((int)Math.Floor((position.x+32)/64), (int)Math.Floor((position.z+32)/64), false);
    }
}

public sealed class ZNetPeer
{
    public long m_uid;
    public ZDOID m_characterID;
    public bool Ready=true;
    public UnityEngine.Vector3 RefPos;
    public readonly HashSet<ZDOID> Known=new HashSet<ZDOID>();
    public readonly HashSet<ZDOID> Invalid=new HashSet<ZDOID>();
    public bool IsReady() { return Ready; }
}

public sealed class ZNet
{
    public static ZNet instance;
    public bool Server=true, ThrowOnPeers;
    public ZDOID LocalPlayerCharacterID { get; set; }
    public List<ZNetPeer> Peers=new List<ZNetPeer>();
    public bool IsServer() { return Server; }
    public List<ZNetPeer> GetPeers() { if(ThrowOnPeers) throw new InvalidOperationException("fixture"); return Peers; }
}

public sealed class ZDO
{
    public ZDOID m_uid;
    public long Owner;
    public bool Persistent;
    public UnityEngine.Vector3 Position;
    public ZoneSystem.SectorIndex IndexedSector;
    public bool ThrowOnPosition;
    public int Revision;
    public readonly List<string> Inventory=new List<string>();
    public ZDO(long id, long owner, UnityEngine.Vector3 position)
    {
        m_uid=new ZDOID(id); Owner=owner; Position=position; IndexedSector=GetSectorIndex();
        Inventory.Add("SilverArmor");
    }
    public UnityEngine.Vector3 GetPosition() { if(ThrowOnPosition) throw new InvalidOperationException("fixture"); return Position; }
    public ZoneSystem.SectorIndex GetSectorIndex() { return ZoneSystem.GetSectorIndex(Position); }
    public void InternalSetPosition(UnityEngine.Vector3 target)
    {
        if(Position.x==target.x && Position.y==target.y && Position.z==target.z) return;
        var next=ZoneSystem.GetSectorIndex(target);
        if(IndexedSector!=next)
        {
            IndexedSector=next;
            if(ZNet.instance.Server) ZDOMan.instance.ZDOSectorInvalidated(this);
        }
        Position=target;
        ++Revision;
    }
}

public sealed class ZDOMan
{
    public static ZDOMan instance;
    public int Notifications;
    public bool ThrowOnNotify;
    public void ZDOSectorInvalidated(ZDO zdo)
    {
        ++Notifications;
        if(ThrowOnNotify) throw new InvalidOperationException("fixture");
        foreach(var peer in ZNet.instance.Peers)
        {
            if(peer==null || peer.m_uid==zdo.Owner || !peer.Known.Contains(zdo.m_uid)) continue;
            if(Near(zdo.GetPosition(), peer.RefPos)) continue;
            peer.Invalid.Add(zdo.m_uid);
            peer.Known.Remove(zdo.m_uid);
        }
    }
    public static bool Near(UnityEngine.Vector3 a, UnityEngine.Vector3 b)
    {
        var left=ZoneSystem.GetSectorIndex(a); var right=ZoneSystem.GetSectorIndex(b);
        return Math.Abs(left.X-right.X)<=2 && Math.Abs(left.Z-right.Z)<=2;
    }
}

public sealed class Receiver
{
    public ZNetPeer Peer;
    public bool Visual=true, WorldZdoAlive=true;
    public int Animations, DestroyedWorldZdos;
    public UnityEngine.Vector3 DisplayedPosition;
    public Receiver(ZNetPeer peer, ZDO zdo) { Peer=peer; DisplayedPosition=zdo.Position; }
    public void Trigger() { if(Visual) ++Animations; }
    public void SendNativeData(ZDO zdo, int queuedBytes)
    {
        if(queuedBytes>10240 || 10240-queuedBytes<2048) return;
        bool invalid=Peer.Invalid.Remove(zdo.m_uid);
        bool included=ZDOMan.Near(zdo.Position, Peer.RefPos);
        // The native packet processes invalidations before any full ZDO records.
        if(included)
        {
            DisplayedPosition=zdo.Position; Peer.Known.Add(zdo.m_uid); Visual=true;
        }
        else if(invalid)
        {
            Visual=false;
            // Native RemoveObjects only destroys a nonpersistent ZDO if this receiver owns it.
            if(!zdo.Persistent && zdo.Owner==Peer.m_uid) { WorldZdoAlive=false; ++DestroyedWorldZdos; }
        }
    }
}
