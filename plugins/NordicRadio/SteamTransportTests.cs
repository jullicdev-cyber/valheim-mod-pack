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
        Request(999);Check(SteamNetworkingMessages.Accepts==0,"unknown Steam session not accepted");
        peer.Ready=false;Request(222);Check(SteamNetworkingMessages.Accepts==0,"unauthenticated game peer not accepted");peer.Ready=true;
        Request(222);Check(SteamNetworkingMessages.Accepts==1,"current game participant accepted");
        Check(transport.TrySend(2,chunk,96*1024),"audio enqueued over Steam Messages");
        Check(socket.Pending==0 && SteamNetworkingMessages.LastPeer==222,"separate send leaves game queue untouched");
        Check(SteamNetworkingMessages.LastChannel==SteamRadioTransport.Channel && SteamNetworkingMessages.LastFlags==41,"dedicated reliable asynchronous channel");
        Check(RadioBulkFrame.Decode(SteamNetworkingMessages.LastSent,10,1,2).Length==chunk.Length,"outbound frame binds world and both game peers");
        Check(!transport.TrySend(3,chunk,96*1024),"unconnected recipient rejected");
        socket.Pending=4097;Check(!transport.TrySend(2,chunk,96*1024),"game congestion yields bulk upload");socket.Pending=0;
        SteamNetworkingMessages.Status=new SteamNetConnectionRealTimeStatus_t{m_cbPendingReliable=40000,m_cbPendingUnreliable=12000,m_cbSentUnackedReliable=40000};
        Check(!transport.TrySend(2,chunk,96*1024),"bulk backlog includes all pending and unacknowledged bytes");
        SteamNetworkingMessages.Status=new SteamNetConnectionRealTimeStatus_t();
        SteamNetworkingMessages.Result=EResult.k_EResultLimitExceeded;
        Check(!transport.TrySend(2,chunk,96*1024),"native queue rejection returned to retrying caller");
        SteamNetworkingMessages.Result=EResult.k_EResultOK;
        int received=0;
        Incoming(999,frame);Incoming(222,bad);Incoming(222,frame,RadioProtocol.MaxPacket+29);Incoming(222,frame,1);
        transport.Poll((uid,data)=>received++);
        Check(received==0 && SteamNetworkingMessage_t.Messages.Count==0,"invalid and unknown packets released before dispatch");
        Incoming(222,frame);Incoming(222,frame);
        transport.Poll((uid,data)=>{received++;if(received==1)throw new Exception("consumer failure");});
        Check(received==2 && SteamNetworkingMessage_t.Messages.Count==0,"all native messages released after consumer exception");
        Check(logs.Count==1,"send and receive failures logged with throttling");
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
        Check(SteamNetworkingMessages.Accepts==1,"client does not accept another client's music session");
        Request(111);Check(SteamNetworkingMessages.Accepts==2,"client accepts host only");
        Check(transport.TrySend(1,chunk,96*1024) && !transport.TrySend(2,chunk,96*1024),"client sends only to current host");
        server.m_socket=new TestSocket();Check(!transport.TrySend(1,chunk,96*1024),"non-Steam connection has no gameplay fallback");
        transport.Dispose();int sent=SteamNetworkingMessages.Sends;
        Check(!transport.TrySend(1,chunk,96*1024) && SteamNetworkingMessages.Sends==sent,"disposed transport cannot send");
        transport.Dispose();Check(Callback<SteamNetworkingMessagesSessionRequest_t>.Disposed==2,"dispose is idempotent");
    }
}
