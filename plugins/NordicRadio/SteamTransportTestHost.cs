// Test-only Steam facade. No native library or running game is used by these tests.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
namespace UnityEngine { public static class Time { public static float realtimeSinceStartup; } }
public class TestSocket { public int Pending; public int GetSendQueueSize() { return Pending; } }
public class ZSteamSocket : TestSocket
{
    public ulong SteamId; public bool Connected=true;
    public bool IsConnected() { return Connected; }
    public Steamworks.CSteamID GetPeerID() { return new Steamworks.CSteamID { m_SteamID=SteamId }; }
}
public class ZNetPeer { public long m_uid; public bool Ready=true; public TestSocket m_socket; public bool IsReady() { return Ready; } }
public class ZNet
{
    public static ZNet instance;
    public long Uid=1, World=10; public bool Host=true, InWorld=true;
    public List<ZNetPeer> Peers=new List<ZNetPeer>();
    public bool IsServer() { return Host; }
    public object GetWorld() { return InWorld?this:null; }
    public long GetWorldUID() { return World; }
    public static long GetUID() { return instance.Uid; }
    public ZNetPeer GetPeer(long uid) { return Peers.Find(p=>p.m_uid==uid); }
    public ZNetPeer GetServerPeer() { return GetPeer(1); }
    public List<ZNetPeer> GetConnectedPeers() { return Peers; }
}
namespace Steamworks
{
    public struct CSteamID { public ulong m_SteamID; }
    public struct SteamNetworkingIdentity
    {
        public ulong Id;
        public ulong GetSteamID64() { return Id; }
        public void SetSteamID64(ulong id) { Id=id; }
    }
    public enum EResult { k_EResultOK, k_EResultLimitExceeded }
    public enum ESteamNetworkingConnectionState { k_ESteamNetworkingConnectionState_None, k_ESteamNetworkingConnectionState_Connecting, k_ESteamNetworkingConnectionState_FindingRoute, k_ESteamNetworkingConnectionState_Connected, k_ESteamNetworkingConnectionState_ClosedByPeer, k_ESteamNetworkingConnectionState_ProblemDetectedLocally }
    public struct SteamNetConnectionInfo_t { public SteamNetworkingIdentity m_identityRemote; public int m_eEndReason; public string m_szEndDebug; }
    public struct SteamNetConnectionRealTimeStatus_t { public int m_cbPendingReliable,m_cbPendingUnreliable,m_cbSentUnackedReliable; }
    public struct SteamNetworkingMessagesSessionRequest_t { public SteamNetworkingIdentity m_identityRemote; }
    public struct SteamNetworkingMessagesSessionFailed_t { public SteamNetConnectionInfo_t m_info; }
    public static class Constants { public const int k_nSteamNetworkingSend_ReliableNoNagle=9,k_nSteamNetworkingSend_AutoRestartBrokenSession=32; }
    public class Callback<T> : IDisposable
    {
        public static Callback<T> Instance; public static int Created,Disposed;
        public Action<T> Action;
        public static Callback<T> Create(Action<T> action) { Created++; return Instance=new Callback<T>{Action=action}; }
        public void Dispose() { Disposed++; if(ReferenceEquals(this,Instance))Instance=null; }
    }
    public struct SteamNetworkingMessage_t
    {
        public IntPtr m_pData; public int m_cbSize,m_nChannel; public SteamNetworkingIdentity m_identityPeer;
        public static readonly Dictionary<IntPtr,SteamNetworkingMessage_t> Messages=new Dictionary<IntPtr,SteamNetworkingMessage_t>();
        public static int Released,Counter;
        public static SteamNetworkingMessage_t FromIntPtr(IntPtr pointer) { return Messages[pointer]; }
        public static void Release(IntPtr pointer) { Marshal.FreeHGlobal(Messages[pointer].m_pData);Messages.Remove(pointer);Released++; }
    }
    public static class SteamNetworkingMessages
    {
        public static int Sends,Polls,Accepts,SessionCloses;
        public static byte[] LastSent; public static ulong LastPeer; public static int LastChannel,LastFlags;
        public static EResult Result=EResult.k_EResultOK;
        public static SteamNetConnectionRealTimeStatus_t Status;
        public static ESteamNetworkingConnectionState State = ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected;
        public static readonly List<int> ClosedChannels=new List<int>();
        public static readonly Queue<IntPtr> Incoming=new Queue<IntPtr>();
        public static EResult SendMessageToUser(ref SteamNetworkingIdentity id,IntPtr data,uint size,int flags,int channel)
        { Sends++;LastSent=new byte[size];Marshal.Copy(data,LastSent,0,(int)size);LastPeer=id.Id;LastChannel=channel;LastFlags=flags;return Result; }
        public static int ReceiveMessagesOnChannel(int channel,IntPtr[] target,int max)
        {
            Polls++; int n=0;
            for(int i=Incoming.Count;i>0;i--) { IntPtr p=Incoming.Dequeue();if(n<max && SteamNetworkingMessage_t.Messages[p].m_nChannel==channel)target[n++]=p;else Incoming.Enqueue(p); }
            return n;
        }
        public static bool AcceptSessionWithUser(ref SteamNetworkingIdentity id) { Accepts++;return true; }
        public static void CloseSessionWithUser(ref SteamNetworkingIdentity id) { SessionCloses++; }
        public static void CloseChannelWithUser(ref SteamNetworkingIdentity id,int channel) { ClosedChannels.Add(channel); }
        public static ESteamNetworkingConnectionState GetSessionConnectionInfo(ref SteamNetworkingIdentity id,out SteamNetConnectionInfo_t info,out SteamNetConnectionRealTimeStatus_t status)
        { info=new SteamNetConnectionInfo_t();status=Status;return State; }
        public static void Enqueue(ulong steamId,int channel,byte[] data,int? reportedSize=null)
        {
            IntPtr memory=Marshal.AllocHGlobal(Math.Max(1,data.Length));Marshal.Copy(data,0,memory,data.Length);
            IntPtr pointer=new IntPtr(++SteamNetworkingMessage_t.Counter);
            SteamNetworkingMessage_t.Messages.Add(pointer,new SteamNetworkingMessage_t{m_pData=memory,m_cbSize=reportedSize??data.Length,
                m_nChannel=channel,m_identityPeer=new SteamNetworkingIdentity{Id=steamId}});
            Incoming.Enqueue(pointer);
        }
    }
}
