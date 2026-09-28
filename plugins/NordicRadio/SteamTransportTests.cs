using System;
using System.Collections.Generic;
using Steamworks;
using ValheimModPack.NordicRadio;

internal static class SteamTransportTests
{
    private static int checks;
    private static void Check(bool condition,string text) { if(!condition)throw new Exception(text);checks++; }
    private static byte[] Chunk() { return RadioProtocol.Encode(new RadioMessage{Kind=RadioMessageKind.Chunk,Id=new string('a',64),Data=new byte[RadioProtocol.ChunkSize]}); }
    private static void Request(ulong id) { Callback<SteamNetworkingMessagesSessionRequest_t>.Instance.Action(new SteamNetworkingMessagesSessionRequest_t{m_identityRemote=new SteamNetworkingIdentity{Id=id}}); }
    private static void Incoming(ulong id,byte[] frame,int? reportedSize=null) { SteamNetworkingMessages.Enqueue(id,SteamRadioTransport.Channel,frame,reportedSize); }
    public static int Main()
    {
        try { Run();Console.WriteLine("PASS: "+checks+" production Steam transport boundary and lifetime assertions (native API simulated).");return 0; }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    private static void Run()
    {
        byte[] chunk=Chunk(), frame=RadioBulkFrame.Encode(10,2,1,chunk);
        Check(RadioBulkFrame.Decode(frame,10,2,1).Length==chunk.Length,"frame round trip");
        Check(RadioBulkFrame.Decode(frame,11,2,1)==null,"old world rejected");
        Check(RadioBulkFrame.Decode(frame,10,3,1)==null,"forged sender rejected");
        Check(RadioBulkFrame.Decode(frame,10,2,8)==null,"old recipient rejected after reconnect");
        Check(RadioBulkFrame.Decode(new byte[8],10,2,1)==null,"truncated frame rejected");
        byte[] bad=(byte[])frame.Clone();bad[0]^=255;
        Check(RadioBulkFrame.Decode(bad,10,2,1)==null,"other protocol rejected");
        Check(RadioBulkFrame.Decode(new byte[RadioProtocol.MaxPacket+29],10,2,1)==null,"allocation bound");
        var logs=new List<string>(); var transport=new SteamRadioTransport(logs.Add);
        ZNet.instance=new ZNet();
        transport.Poll(delegate{});
        Check(SteamNetworkingMessages.Polls==0 && Callback<SteamNetworkingMessagesSessionRequest_t>.Created==0,"single player does not initialize Steam transport");
        var socket=new ZSteamSocket{SteamId=222};
        var peer=new ZNetPeer{m_uid=2,m_socket=socket}; ZNet.instance.Peers.Add(peer);
        transport.Poll(delegate{});
        Check(Callback<SteamNetworkingMessagesSessionRequest_t>.Created==1,"lazy callback creation for authenticated Steam peer");
        Check(SteamNetworkingMessages.LastSent[29]==SteamRadioTransport.Probe,"both endpoints actively initiate the channel before sending playlist");
        Check(SteamNetworkingMessages.Accepts==1,"pending session accepted even when its request callback was missed");
        Check(!transport.TrySend(2,chunk,96*1024),"queued Steam send is not mistaken for delivered handshake");
        Check(!transport.IsAvailable(2),"native connected without delivery proof is unavailable");
        Request(999);Check(SteamNetworkingMessages.Accepts==1,"unknown Steam session not accepted");
        peer.Ready=false;Request(222);Check(SteamNetworkingMessages.Accepts==1,"unauthenticated game peer not accepted");peer.Ready=true;
        Request(222);Check(SteamNetworkingMessages.Accepts==2,"current game participant accepted");
        Incoming(222,RadioBulkFrame.Encode(10,2,1,new byte[]{2,SteamRadioTransport.ProbeReply}));
        transport.Poll(delegate{throw new Exception("handshake leaked into radio service");});
        Check(transport.IsAvailable(2),"received probe establishes verified transport availability");
        Check(transport.TrySend(2,chunk,96*1024),"audio enqueued over Steam Messages");
        Check(socket.Pending==0 && SteamNetworkingMessages.LastPeer==222,"separate send leaves game queue untouched");
        Check(SteamNetworkingMessages.LastChannel==SteamRadioTransport.Channel && SteamNetworkingMessages.LastFlags==41,"dedicated reliable asynchronous channel");
        Check(RadioBulkFrame.Decode(SteamNetworkingMessages.LastSent,10,1,2).Length==chunk.Length,"outbound frame binds world and both game peers");
        Check(!transport.TrySend(3,chunk,96*1024),"unconnected recipient rejected");
        socket.Pending=4097;Check(!transport.TrySend(2,chunk,96*1024),"game congestion yields bulk upload");
        Check(transport.IsAvailable(2),"game queue pressure does not report channel failure");socket.Pending=0;
        SteamNetworkingMessages.Status=new SteamNetConnectionRealTimeStatus_t{m_cbPendingReliable=40000,m_cbPendingUnreliable=12000,m_cbSentUnackedReliable=40000};
        Check(!transport.TrySend(2,chunk,96*1024),"bulk backlog includes all pending and unacknowledged bytes");
        Check(transport.IsAvailable(2),"full bulk queue remains a healthy transport");
        SteamNetworkingMessages.Status=new SteamNetConnectionRealTimeStatus_t();
        SteamNetworkingMessages.Result=EResult.k_EResultLimitExceeded;
        Check(!transport.TrySend(2,chunk,96*1024),"native queue rejection returned to retrying caller");
        Check(transport.IsAvailable(2),"native send buffer limit is flow control rather than lost delivery proof");
        SteamNetworkingMessages.Result=EResult.k_EResultOK;
        int received=0;
        Incoming(999,frame);Incoming(222,bad);Incoming(222,frame,RadioProtocol.MaxPacket+29);Incoming(222,frame,1);
        transport.Poll((uid,data)=>received++);
        Check(received==0 && SteamNetworkingMessage_t.Messages.Count==0,"invalid and unknown packets released before dispatch");
        Incoming(222,frame);Incoming(222,frame);
        transport.Poll((uid,data)=>{received++;if(received==1)throw new Exception("consumer failure");});
        Check(received==2 && SteamNetworkingMessage_t.Messages.Count==0,"all native messages released after consumer exception");
        Check(logs.FindAll(s=>s.Contains("Send returned") || s.Contains("consumer failure")).Count==1,"send and receive failures logged with throttling");
        for(int i=0;i<10;i++)Incoming(222,frame);
        received=0;transport.Poll((uid,data)=>received++);
        Check(received==8 && SteamNetworkingMessage_t.Messages.Count==2,"per-frame receive work bounded to eight messages");
        transport.Poll((uid,data)=>received++);
        Check(received==10 && SteamNetworkingMessage_t.Messages.Count==0,"bounded poll resumes next frame");
        SteamNetworkingMessages.Enqueue(222,1234,new byte[]{1,2});
        transport.Poll(delegate{});
        Check(SteamNetworkingMessage_t.Messages.Count==1,"other mod channels not drained");
        SteamNetworkingMessage_t.Release(SteamNetworkingMessages.Incoming.Dequeue());
        Incoming(222,frame);ZNet.instance.World=11;received=0;transport.Poll((uid,data)=>received++);
        Check(received==0 && SteamNetworkingMessage_t.Messages.Count==0,"old queued music ignored after world change");
        ZNet.instance.World=10;
        peer.Ready=false;Incoming(222,frame);transport.Poll((uid,data)=>received++);
        Check(received==0,"disconnect between enqueue and receive revokes permission");peer.Ready=true;
        int closed=SteamNetworkingMessages.ClosedChannels.Count;
        ZNet.instance.Peers.Clear();UnityEngine.Time.realtimeSinceStartup=10;transport.Poll(delegate{});
        Check(SteamNetworkingMessages.ClosedChannels.Count>closed,"departed peer's music channel cleaned up");
        ZNet.instance.Peers.Add(peer);transport.TrySend(2,chunk,96*1024);transport.Reset();
        Check(Callback<SteamNetworkingMessagesSessionRequest_t>.Disposed==1,"world reset releases callback");
        Check(SteamNetworkingMessages.SessionCloses==0 && SteamNetworkingMessages.ClosedChannels.TrueForAll(c=>c==SteamRadioTransport.Channel),"reset preserves other mods' sessions and channels");
        ZNet.instance.Host=false;ZNet.instance.Uid=2;
        var server=new ZNetPeer{m_uid=1,m_socket=new ZSteamSocket{SteamId=111}};ZNet.instance.Peers.Add(server);
        transport.Poll(delegate{});Request(222);
        int accepted=SteamNetworkingMessages.Accepts;Request(222);
        Check(SteamNetworkingMessages.Accepts==accepted,"client does not accept another client's music session");
        Request(111);Check(SteamNetworkingMessages.Accepts==accepted+1,"client accepts host only");
        Incoming(111,RadioBulkFrame.Encode(10,1,2,new byte[]{2,SteamRadioTransport.Probe}));
        transport.Poll(delegate{throw new Exception("probe leaked into radio service");});
        Check(SteamNetworkingMessages.LastSent[29]==SteamRadioTransport.ProbeReply,"incoming probe receives an explicit confirmation");
        Check(transport.TrySend(1,chunk,96*1024) && !transport.TrySend(2,chunk,96*1024),"client sends only to current host");
        Callback<SteamNetworkingMessagesSessionFailed_t>.Instance.Action(new SteamNetworkingMessagesSessionFailed_t{m_info=new SteamNetConnectionInfo_t{m_identityRemote=new SteamNetworkingIdentity{Id=111},m_eEndReason=5003,m_szEndDebug="test failure"}});
        Check(!transport.TrySend(1,chunk,96*1024),"failed session immediately revokes readiness");
        Check(!transport.IsAvailable(1),"session failure immediately revokes transport health");
        Incoming(111,RadioBulkFrame.Encode(10,1,2,new byte[]{2,SteamRadioTransport.ProbeReply}));transport.Poll(delegate{});
        Check(transport.TrySend(1,chunk,96*1024),"probe can recover a failed session without restarting game");
        SteamNetworkingMessages.State=ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting;
        Check(!transport.TrySend(1,chunk,96*1024),"connecting native session cannot queue audio behind handshake");
        Check(!transport.IsAvailable(1),"reconnecting native state is unavailable even after old delivery proof");
        SteamNetworkingMessages.State=ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected;
        Check(!transport.IsAvailable(1),"return to connected still requires a fresh delivery confirmation");
        Incoming(111,RadioBulkFrame.Encode(10,1,2,new byte[]{2,SteamRadioTransport.ProbeReply}));transport.Poll(delegate{});
        SteamNetworkingMessages.Status=new SteamNetConnectionRealTimeStatus_t{m_cbPendingReliable=262144};
        UnityEngine.Time.realtimeSinceStartup=21;
        Check(!transport.TrySend(1,chunk,96*1024) && transport.IsAvailable(1),"more than ten seconds of backpressure preserves confirmed connection health");
        SteamNetworkingMessages.Status=new SteamNetConnectionRealTimeStatus_t();
        UnityEngine.Time.realtimeSinceStartup=36;
        Check(!transport.IsAvailable(1),"expired receive proof cannot mask a silently broken channel");
        server.m_socket=new TestSocket();Check(!transport.TrySend(1,chunk,96*1024),"non-Steam connection has no gameplay fallback");
        transport.Dispose();int sent=SteamNetworkingMessages.Sends;
        Check(!transport.TrySend(1,chunk,96*1024) && SteamNetworkingMessages.Sends==sent,"disposed transport cannot send");
        transport.Dispose();Check(Callback<SteamNetworkingMessagesSessionRequest_t>.Disposed==2,"dispose is idempotent");
        Check(Callback<SteamNetworkingMessagesSessionFailed_t>.Disposed==2,"reset and dispose release failure callback too");
        Check(!transport.IsAvailable(1),"disposed transport never reports availability");

        var diagnostics=new List<string>();transport=new SteamRadioTransport(diagnostics.Add);
        server.m_socket=new ZSteamSocket{SteamId=111};
        SteamNetworkingMessages.State=ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_None;
        UnityEngine.Time.realtimeSinceStartup=40;transport.Poll(delegate{});
        Check(diagnostics.Exists(s=>s.Contains("connection state:") && s.Contains("_None")),"initial native None state is observable");
        UnityEngine.Time.realtimeSinceStartup=51;transport.Poll(delegate{});
        int stalled=diagnostics.FindAll(s=>s.Contains("still unconfirmed:")).Count;
        Check(stalled==1 && diagnostics.Exists(s=>s.Contains("queued probes=1")),"stalled delivery reports queued probes instead of silently waiting");
        UnityEngine.Time.realtimeSinceStartup=52;transport.Poll(delegate{});
        Check(diagnostics.FindAll(s=>s.Contains("still unconfirmed:")).Count==stalled,"stalled-channel diagnostics are throttled");
        transport.Dispose();
    }
}
