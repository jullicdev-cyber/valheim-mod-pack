using System;
using Steamworks;
using UnityEngine;
using ValheimModPack.NordicRadio;
internal static class SteamSocketTests
{
    private static int checks;
    private static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
    private static ZNetPeer Peer(long uid,ulong steam){return new ZNetPeer{m_uid=uid,m_socket=new ZSteamSocket{SteamId=steam}};}
    private static byte[] Frame(long world,long sender,long receiver,byte kind){return RadioBulkFrame.Encode(world,sender,receiver,new byte[]{2,kind});}
    private sealed class Fixture : IDisposable
    {
        internal SteamSocketRadioTransport Transport; internal ZNetPeer Remote;
        internal Fixture(bool host=true)
        {
            SteamNetworkingSockets.Reset();Time.realtimeSinceStartup=100;
            ZNet.instance=new ZNet{Uid=host?1:2,Host=host};Remote=Peer(host?2:1,host?22UL:11UL);ZNet.instance.Peers.Add(Remote);
            Transport=new SteamSocketRadioTransport(delegate{});Transport.Poll(delegate{});
        }
        internal HSteamNetConnection Connect()
        {
            var conn=ZNet.instance.Host?SteamNetworkingSockets.NewConnection(((ZSteamSocket)Remote.m_socket).SteamId,SteamNetworkingSockets.LastListener):SteamNetworkingSockets.LastConnection;
            SteamNetworkingSockets.Event(conn,ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting);
            SteamNetworkingSockets.Event(conn,ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected);return conn;
        }
        internal void Ready(HSteamNetConnection conn)
        {
            SteamNetworkingSockets.Enqueue(conn,((ZSteamSocket)Remote.m_socket).SteamId,Frame(10,Remote.m_uid,ZNet.GetUID(),SteamSocketRadioTransport.Probe));Transport.Poll(delegate{});
        }
        public void Dispose(){Transport.Dispose();SteamNetworkingSockets.Reset();}
    }
    private static void HostAndFlow()
    {
        using(var f=new Fixture())
        {
            Check(SteamNetworkingSockets.Listens==1&&SteamNetworkingSockets.LastPort!=0,"separate listener, never port zero");
            Check(HarmonyLib.Harmony.Patches==1,"game callback guard installed once");
            var alien=SteamNetworkingSockets.NewConnection(99,SteamNetworkingSockets.LastListener);
            var callback=new SteamNetConnectionStatusChangedCallback_t{m_hConn=alien,m_info=SteamNetworkingSockets.Connections[alien.m_HSteamNetConnection]};
            Check(!RadioSocketCallbackGuard.AllowGameCallback(callback),"radio incoming is protected before own callback");
            SteamNetworkingSockets.Event(alien,ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting);
            Check(SteamNetworkingSockets.Closed.Contains(alien.m_HSteamNetConnection)&&SteamNetworkingSockets.Accepts==0,"unknown Steam identity rejected");
            Check(!RadioSocketCallbackGuard.AllowGameCallback(callback),"rejected callback cannot be stolen by game afterwards");
            callback.m_hConn=new HSteamNetConnection{m_HSteamNetConnection=99};callback.m_info.m_hListenSocket=new HSteamListenSocket{m_HSteamListenSocket=98};
            Check(RadioSocketCallbackGuard.AllowGameCallback(callback),"game and other mod listener callbacks untouched");
            var conn=f.Connect();Check(SteamNetworkingSockets.Accepts==1,"authenticated incoming accepted");
            Check(!f.Transport.IsAvailable(2),"connected socket is not delivery proof");
            SteamNetworkingSockets.Enqueue(conn,22,Frame(11,2,1,SteamSocketRadioTransport.Probe));f.Transport.Poll(delegate{});
            Check(!f.Transport.IsAvailable(2),"cross-world probe rejected");
            SteamNetworkingSockets.Enqueue(conn,22,Frame(10,3,1,SteamSocketRadioTransport.Probe));f.Transport.Poll(delegate{});
            Check(!f.Transport.IsAvailable(2),"wrong game identity rejected");
            SteamNetworkingSockets.Enqueue(conn,33,Frame(10,2,1,SteamSocketRadioTransport.Probe));f.Transport.Poll(delegate{});
            Check(!f.Transport.IsAvailable(2),"wrong Steam identity rejected");
            f.Ready(conn);Check(f.Transport.IsAvailable(2),"matching world/session delivery enables socket");
            var reply=RadioBulkFrame.Decode(SteamNetworkingSockets.LastSent,10,1,2);Check(reply!=null&&reply[1]==SteamSocketRadioTransport.ProbeReply,"probe ACK bound to same world and peers");
            var packet=new byte[24000];packet[0]=2;packet[1]=(byte)RadioMessageKind.Chunk;
            Check(f.Transport.TrySend(2,packet,32768),"audio bulk uses explicit socket");
            Check(f.Remote.m_socket.Pending==0,"audio leaves gameplay queue empty");
            SteamNetworkingSockets.Status.m_cbSentUnackedReliable=9000;
            Check(!f.Transport.TrySend(2,packet,32768),"unacknowledged bytes included in bound");
            Check(f.Transport.IsAvailable(2),"queue pressure does not masquerade as disconnect");
            SteamNetworkingSockets.Status.m_cbSentUnackedReliable=0;f.Remote.m_socket.Pending=4097;
            Check(!f.Transport.TrySend(2,packet,32768)&&f.Transport.IsAvailable(2),"game pressure yields audio while retaining fast route");
            f.Remote.m_socket.Pending=0;packet=new byte[40000];packet[0]=2;packet[1]=(byte)RadioMessageKind.Library;
            Check(f.Transport.TrySend(2,packet,1000),"large playlist permitted only on empty independent queue");
            SteamNetworkingSockets.Status.m_cbPendingReliable=1;
            Check(!f.Transport.TrySend(2,packet,1000),"large playlist cannot bypass queued bytes");
            SteamNetworkingSockets.Status.m_cbPendingReliable=0;
            var duplicate=SteamNetworkingSockets.NewConnection(22,SteamNetworkingSockets.LastListener);SteamNetworkingSockets.Event(duplicate,ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting);
            Check(SteamNetworkingSockets.Closed.Contains(duplicate.m_HSteamNetConnection)&&f.Transport.IsAvailable(2),"duplicate cannot evict existing healthy connection");
            int released=SteamNetworkingMessage_t.Released,seen=0;
            for(int i=0;i<10;i++)SteamNetworkingSockets.Enqueue(conn,22,Frame(10,2,1,(byte)RadioMessageKind.Library));
            f.Transport.Poll(delegate{seen++;throw new Exception("consumer fixture");});
            Check(seen==8&&SteamNetworkingMessage_t.Released-released==8,"eight messages max and all pointers released despite receiver exception");
            f.Transport.Poll(delegate{seen++;});Check(seen==10,"receive remainder next frame");
            SteamNetworkingSockets.Enqueue(conn,22,new byte[1],RadioProtocol.MaxPacket+RadioBulkFrame.HeaderSize+1);f.Transport.Poll(delegate{throw new Exception("oversize delivered");});
            Check(SteamNetworkingMessage_t.Messages.Count==0,"oversized frame released without copy");
            f.Remote.Ready=false;Time.realtimeSinceStartup+=1;f.Transport.Poll(delegate{});
            Check(!f.Transport.IsAvailable(2)&&SteamNetworkingSockets.Closed.Contains(conn.m_HSteamNetConnection),"game disconnect revokes independent connection");
            Check(!SteamNetworkingSockets.Closed.Contains(99),"only radio connections are closed");
        }
    }
    private static void ClientAndReset()
    {
        using(var f=new Fixture(false))
        {
            Check(SteamNetworkingSockets.Listens==0&&SteamNetworkingSockets.Connects==1&&SteamNetworkingSockets.LastConnectId==11,"only client initiates, only to authenticated host");
            var conn=f.Connect();f.Ready(conn);Check(f.Transport.IsAvailable(1),"client delivery confirmed");
            ZNet.instance.Peers.Add(Peer(3,33));Check(!f.Transport.IsAvailable(3),"client cannot address another guest");
            Time.realtimeSinceStartup+=26;Check(!f.Transport.IsAvailable(1),"stale delivery cannot stay healthy");
            Time.realtimeSinceStartup+=5;f.Transport.Poll(delegate{});Check(SteamNetworkingSockets.Closed.Contains(conn.m_HSteamNetConnection),"silent link bounded by timeout");
            Check(SteamNetworkingSockets.Connects==1,"timeout does not cause immediate reconnect storm");
            Time.realtimeSinceStartup+=2;f.Transport.Poll(delegate{});Check(SteamNetworkingSockets.Connects==2,"client reconnect after bounded two-second backoff");
            Check(f.Transport.IsConnecting(1),"pending authenticated connection grants bounded handshake grace");
            int disposed=Callback<SteamNetConnectionStatusChangedCallback_t>.Disposed;
            var pending=SteamNetworkingSockets.LastConnection;ZNet.instance.World++;f.Transport.Poll(delegate{});
            Check(Callback<SteamNetConnectionStatusChangedCallback_t>.Disposed==disposed+1,"world change disposes old callbacks");
            Check(SteamNetworkingSockets.Closed.Contains(pending.m_HSteamNetConnection)&&SteamNetworkingSockets.DestroyedGroups.Count>0,"world change closes own old sockets and group");
            Check(!f.Transport.IsAvailable(1),"new world cannot reuse old delivery proof");
            f.Transport.Dispose();int connects=SteamNetworkingSockets.Connects;f.Transport.Poll(delegate{});
            Check(SteamNetworkingSockets.Connects==connects&&Callback<SteamNetConnectionStatusChangedCallback_t>.Instance==null,"disposed transport stays stopped");
        }
    }
    private static void BoundsAndFailure()
    {
        using(var f=new Fixture())
        {
            var first=f.Connect();f.Ready(first);
            for(int i=3;i<=66;i++)
            {
                ZNet.instance.Peers.Add(Peer(i,(ulong)(i*11)));var c=SteamNetworkingSockets.NewConnection((ulong)(i*11),SteamNetworkingSockets.LastListener);
                SteamNetworkingSockets.Event(c,ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting);
            }
            Check(SteamNetworkingSockets.Accepts==64,"64 authenticated music peers maximum");
            var listener=SteamNetworkingSockets.LastListener;f.Transport.Reset();
            Check(SteamNetworkingSockets.ClosedListeners.Contains(listener.m_HSteamListenSocket),"reset closes only owned listener");
            var late=new SteamNetConnectionStatusChangedCallback_t{m_hConn=first,m_info=new SteamNetConnectionInfo_t{m_hListenSocket=listener}};
            Check(!RadioSocketCallbackGuard.AllowGameCallback(late),"late callback protected after reset");
            Time.realtimeSinceStartup+=31;Check(RadioSocketCallbackGuard.AllowGameCallback(late),"retired handle protection expires");
        }
        using(var f=new Fixture())
        {
            SteamNetworkingSockets.FailPollGroup=true;var c=SteamNetworkingSockets.NewConnection(22,SteamNetworkingSockets.LastListener);
            SteamNetworkingSockets.Event(c,ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting);
            Check(SteamNetworkingSockets.Closed.Contains(c.m_HSteamNetConnection)&&SteamNetworkingSockets.Accepts==0,"poll-group failure cleans up without acceptance");
        }
    }
    public static int Main()
    {
        try{HostAndFlow();ClientAndReset();BoundsAndFailure();Console.WriteLine("PASS explicit Steam socket transport: "+checks+" assertions (mock API, not two-PC networking).");return 0;}
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
