// Test-only native facade. These tests never initialize Steam or launch Valheim.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
namespace UnityEngine { public static class Time { public static float realtimeSinceStartup; } }
namespace HarmonyLib
{
    public static class Priority { public const int First=800; }
    public sealed class HarmonyMethod { public int priority; public HarmonyMethod(Type t,string n){} }
    public sealed class Harmony { public static int Patches; public Harmony(string id){} public void Patch(MethodInfo m,HarmonyMethod prefix){Patches++;} public void UnpatchSelf(){} }
    public static class AccessTools { public static MethodInfo Method(Type t,string n,Type[] a){return t.GetMethod(n,BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public,null,a,null);} }
}
public class TestSocket { public int Pending; public int GetSendQueueSize(){return Pending;} }
public class ZSteamSocket : TestSocket
{
    public ulong SteamId; public bool Connected=true;
    public bool IsConnected(){return Connected;}
    public Steamworks.CSteamID GetPeerID(){return new Steamworks.CSteamID{m_SteamID=SteamId};}
    private static void OnStatusChanged(Steamworks.SteamNetConnectionStatusChangedCallback_t data){}
}
public class ZNetPeer { public long m_uid; public bool Ready=true; public TestSocket m_socket; public bool IsReady(){return Ready;} }
public class ZNet
{
    public static ZNet instance; public long Uid=1,World=10; public bool Host=true,InWorld=true;
    public List<ZNetPeer> Peers=new List<ZNetPeer>();
    public bool IsServer(){return Host;} public object GetWorld(){return InWorld?this:null;} public long GetWorldUID(){return World;}
    public static long GetUID(){return instance.Uid;}
    public ZNetPeer GetPeer(long uid){return Peers.Find(p=>p.m_uid==uid);}
    public ZNetPeer GetServerPeer(){return GetPeer(1);}
    public List<ZNetPeer> GetConnectedPeers(){return new List<ZNetPeer>(Peers);}
}
namespace Steamworks
{
    public struct CSteamID { public ulong m_SteamID; }
    public struct SteamNetworkingIdentity { public ulong Id; public ulong GetSteamID64(){return Id;} public void SetSteamID64(ulong id){Id=id;} }
    public struct HSteamNetConnection
    {
        public uint m_HSteamNetConnection; public static readonly HSteamNetConnection Invalid=new HSteamNetConnection();
        public static bool operator ==(HSteamNetConnection a,HSteamNetConnection b){return a.m_HSteamNetConnection==b.m_HSteamNetConnection;}
        public static bool operator !=(HSteamNetConnection a,HSteamNetConnection b){return !(a==b);}
        public override bool Equals(object o){return o is HSteamNetConnection&&this==(HSteamNetConnection)o;} public override int GetHashCode(){return (int)m_HSteamNetConnection;}
    }
    public struct HSteamListenSocket
    {
        public uint m_HSteamListenSocket; public static readonly HSteamListenSocket Invalid=new HSteamListenSocket();
        public static bool operator ==(HSteamListenSocket a,HSteamListenSocket b){return a.m_HSteamListenSocket==b.m_HSteamListenSocket;}
        public static bool operator !=(HSteamListenSocket a,HSteamListenSocket b){return !(a==b);}
        public override bool Equals(object o){return o is HSteamListenSocket&&this==(HSteamListenSocket)o;} public override int GetHashCode(){return (int)m_HSteamListenSocket;}
    }
    public struct HSteamNetPollGroup
    {
        public uint Id; public static readonly HSteamNetPollGroup Invalid=new HSteamNetPollGroup();
        public static bool operator ==(HSteamNetPollGroup a,HSteamNetPollGroup b){return a.Id==b.Id;} public static bool operator !=(HSteamNetPollGroup a,HSteamNetPollGroup b){return !(a==b);}
        public override bool Equals(object o){return o is HSteamNetPollGroup&&this==(HSteamNetPollGroup)o;} public override int GetHashCode(){return (int)Id;}
    }
    public enum EResult { k_EResultOK,k_EResultFail,k_EResultLimitExceeded }
    public enum ESteamNetworkingConnectionState { k_ESteamNetworkingConnectionState_None,k_ESteamNetworkingConnectionState_Connecting,k_ESteamNetworkingConnectionState_FindingRoute,k_ESteamNetworkingConnectionState_Connected,k_ESteamNetworkingConnectionState_ClosedByPeer,k_ESteamNetworkingConnectionState_ProblemDetectedLocally }
    public struct SteamNetConnectionInfo_t { public SteamNetworkingIdentity m_identityRemote; public HSteamListenSocket m_hListenSocket; public ESteamNetworkingConnectionState m_eState; public int m_eEndReason; public string m_szEndDebug; }
    public struct SteamNetConnectionStatusChangedCallback_t { public HSteamNetConnection m_hConn; public SteamNetConnectionInfo_t m_info; public ESteamNetworkingConnectionState m_eOldState; }
    public struct SteamNetConnectionRealTimeStatus_t { public int m_cbPendingReliable,m_cbPendingUnreliable,m_cbSentUnackedReliable; }
    public struct SteamNetConnectionRealTimeLaneStatus_t {}
    public struct SteamNetworkingConfigValue_t {}
    public static class Constants { public const int k_nSteamNetworkingSend_ReliableNoNagle=9; }
    public class Callback<T> : IDisposable
    {
        public static Callback<T> Instance; public static int Created,Disposed; public Action<T> Action;
        public static Callback<T> Create(Action<T> action){Created++;return Instance=new Callback<T>{Action=action};}
        public void Dispose(){Disposed++;if(ReferenceEquals(this,Instance))Instance=null;}
    }
    public struct SteamNetworkingMessage_t
    {
        public IntPtr m_pData; public int m_cbSize; public HSteamNetConnection m_conn; public SteamNetworkingIdentity m_identityPeer;
        public static readonly Dictionary<IntPtr,SteamNetworkingMessage_t> Messages=new Dictionary<IntPtr,SteamNetworkingMessage_t>(); public static int Released,Counter;
        public static SteamNetworkingMessage_t FromIntPtr(IntPtr p){return Messages[p];}
        public static void Release(IntPtr p){Marshal.FreeHGlobal(Messages[p].m_pData);Messages.Remove(p);Released++;}
    }
    public static class SteamNetworkingSockets
    {
        public static uint Counter=1000; public static int Listens,Connects,Accepts,Sends,Polls,LastPort; public static ulong LastConnectId;
        public static HSteamListenSocket LastListener; public static HSteamNetConnection LastConnection; public static HSteamNetPollGroup LastGroup;
        public static bool FailPollGroup; public static EResult SendResult=EResult.k_EResultOK;
        public static byte[] LastSent; public static SteamNetConnectionRealTimeStatus_t Status;
        public static readonly Dictionary<uint,SteamNetConnectionInfo_t> Connections=new Dictionary<uint,SteamNetConnectionInfo_t>();
        public static readonly Dictionary<uint,uint> Groups=new Dictionary<uint,uint>(); public static readonly List<uint> Closed=new List<uint>(),ClosedListeners=new List<uint>(),DestroyedGroups=new List<uint>();
        public static readonly Queue<IntPtr> Incoming=new Queue<IntPtr>();
        public static HSteamNetPollGroup CreatePollGroup(){return LastGroup=new HSteamNetPollGroup{Id=++Counter};}
        public static bool DestroyPollGroup(HSteamNetPollGroup group){DestroyedGroups.Add(group.Id);return true;}
        public static HSteamListenSocket CreateListenSocketP2P(int port,int n,SteamNetworkingConfigValue_t[] options){if(port<0||port>65535)return HSteamListenSocket.Invalid;LastPort=port;Listens++;return LastListener=new HSteamListenSocket{m_HSteamListenSocket=++Counter};}
        public static HSteamNetConnection ConnectP2P(ref SteamNetworkingIdentity identity,int port,int n,SteamNetworkingConfigValue_t[] options){if(port<0||port>65535)return HSteamNetConnection.Invalid;Connects++;LastPort=port;LastConnectId=identity.Id;return LastConnection=NewConnection(identity.Id,HSteamListenSocket.Invalid);}
        public static HSteamNetConnection NewConnection(ulong identity,HSteamListenSocket listen)
        {
            var conn=new HSteamNetConnection{m_HSteamNetConnection=++Counter}; Connections[conn.m_HSteamNetConnection]=new SteamNetConnectionInfo_t{m_identityRemote=new SteamNetworkingIdentity{Id=identity},m_hListenSocket=listen,m_eState=ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting};return conn;
        }
        public static void Event(HSteamNetConnection conn,ESteamNetworkingConnectionState state)
        {
            var info=Connections[conn.m_HSteamNetConnection]; var old=info.m_eState;info.m_eState=state;Connections[conn.m_HSteamNetConnection]=info;
            if(Callback<SteamNetConnectionStatusChangedCallback_t>.Instance!=null)Callback<SteamNetConnectionStatusChangedCallback_t>.Instance.Action(new SteamNetConnectionStatusChangedCallback_t{m_hConn=conn,m_info=info,m_eOldState=old});
        }
        public static EResult AcceptConnection(HSteamNetConnection conn){Accepts++;return EResult.k_EResultOK;}
        public static bool CloseConnection(HSteamNetConnection conn,int reason,string debug,bool linger){Closed.Add(conn.m_HSteamNetConnection);Connections.Remove(conn.m_HSteamNetConnection);return true;}
        public static bool CloseListenSocket(HSteamListenSocket listener){ClosedListeners.Add(listener.m_HSteamListenSocket);return true;}
        public static bool SetConnectionPollGroup(HSteamNetConnection conn,HSteamNetPollGroup group){Groups[conn.m_HSteamNetConnection]=group.Id;return !FailPollGroup;}
        public static bool GetConnectionInfo(HSteamNetConnection conn,out SteamNetConnectionInfo_t info){return Connections.TryGetValue(conn.m_HSteamNetConnection,out info);}
        public static EResult GetConnectionRealTimeStatus(HSteamNetConnection conn,ref SteamNetConnectionRealTimeStatus_t status,int count,ref SteamNetConnectionRealTimeLaneStatus_t lanes){status=Status;return EResult.k_EResultOK;}
        public static EResult SendMessageToConnection(HSteamNetConnection conn,IntPtr data,uint size,int flags,out long number){number=++Sends;LastSent=new byte[size];Marshal.Copy(data,LastSent,0,(int)size);return SendResult;}
        public static int ReceiveMessagesOnPollGroup(HSteamNetPollGroup group,IntPtr[] pointers,int max)
        {
            Polls++; int count=0;for(int i=Incoming.Count;i>0;i--){IntPtr p=Incoming.Dequeue();uint g;var msg=SteamNetworkingMessage_t.Messages[p];if(count<max&&Groups.TryGetValue(msg.m_conn.m_HSteamNetConnection,out g)&&g==group.Id)pointers[count++]=p;else Incoming.Enqueue(p);}return count;
        }
        public static void Enqueue(HSteamNetConnection conn,ulong id,byte[] bytes,int? length=null)
        {
            IntPtr data=Marshal.AllocHGlobal(Math.Max(1,bytes.Length));Marshal.Copy(bytes,0,data,bytes.Length);IntPtr p=new IntPtr(++SteamNetworkingMessage_t.Counter);
            SteamNetworkingMessage_t.Messages[p]=new SteamNetworkingMessage_t{m_conn=conn,m_identityPeer=new SteamNetworkingIdentity{Id=id},m_pData=data,m_cbSize=length??bytes.Length};Incoming.Enqueue(p);
        }
        public static void Reset()
        {
            while(Incoming.Count>0)SteamNetworkingMessage_t.Release(Incoming.Dequeue());Connections.Clear();Groups.Clear();Closed.Clear();ClosedListeners.Clear();DestroyedGroups.Clear();
            Status=new SteamNetConnectionRealTimeStatus_t();FailPollGroup=false;SendResult=EResult.k_EResultOK;Listens=Connects=Accepts=Sends=Polls=0;LastSent=null;
        }
    }
}
