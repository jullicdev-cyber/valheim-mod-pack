using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ValheimModPack.NordicRadio;

internal static class RecoveryTransportTests
{
    private static int checks, sentPackets, maxQueue, maxPacket, delivered;
    private static float now;
    private static bool dropData, dropAck;
    private static readonly List<Node> nodes = new List<Node>();
    private static readonly List<Wire> wire = new List<Wire>();
    private static readonly List<string> logs = new List<string>();
    private sealed class DeadSteam : IRadioBulkTransport
    {
        internal bool Available;
        internal byte[] Incoming;
        internal int Sends;
        public void Poll(Action<long,byte[]> receive) { if(Incoming!=null){var data=Incoming;Incoming=null;receive(2,data);} }
        public bool TrySend(long peer,byte[] data,int limit) { Sends++;return Available; }
        public void Reset() { }
        public void Dispose() { }
    }
    private sealed class Node
    {
        internal ZNet Net;
        internal ZRoutedRpc Rpc;
        internal IRadioBulkTransport Transport;
        internal RadioService Service;
        internal Player Player;
        internal ZDOMan Objects;
        internal List<byte[]> Received = new List<byte[]>();
    }
    private sealed class Wire
    {
        internal Node Source, Target;
        internal long Sender;
        internal string Method;
        internal ZPackage Package;
        internal TestSocket Socket;
        internal int Bytes;
        internal float Due, Free;
        internal bool Arrived;
    }
    private static void Check(bool ok,string message) { if(!ok)throw new Exception(message);checks++; }
    private static void Select(Node node)
    {
        ZNet.instance=node.Net;ZRoutedRpc.instance=node.Rpc;Player.m_localPlayer=node.Player;
        ZDOMan.instance=node.Objects;UnityEngine.Time.realtimeSinceStartup=now;
    }
    private static Node Add(int uid,bool recovery)
    {
        var node=new Node{Net=new ZNet{Uid=uid,Host=uid==1,World=17},Rpc=new ZRoutedRpc(),Objects=new ZDOMan(),
            Player=new Player{Character=new ZDO{m_uid=new ZDOID(uid,100)},transform={position=new UnityEngine.Vector3(1,0,0)}}};
        var routed=new RoutedRadioTransport();
        node.Transport=recovery?(IRadioBulkTransport)new RecoveringRadioTransport(new DeadSteam(),routed,logs.Add):routed;
        foreach(Node other in nodes)
        {
            if(uid==1 || other.Net.Uid==1)
            {
                node.Net.Peers.Add(new ZNetPeer{m_uid=other.Net.Uid,m_characterID=other.Player.Character.m_uid});
                other.Net.Peers.Add(new ZNetPeer{m_uid=uid,m_characterID=node.Player.Character.m_uid});
            }
        }
        node.Rpc.Transport=(target,method,package)=>
        {
            Node destination=nodes.Find(n=>n.Net.Uid==target);var socket=node.Net.GetPeer(target).m_socket;
            int size=package.Size()+128;
            if(method==RoutedRadioTransport.RpcName)
            {
                if(package.Size()>41+RoutedRadioTransport.FragmentSize)throw new Exception("Oversized recovery RPC");
                if(socket.Pending+size>RoutedRadioTransport.QueueCeiling)throw new Exception("Recovery crossed queue ceiling");
                maxPacket=Math.Max(maxPacket,package.Size());
            }
            socket.Pending+=size;maxQueue=Math.Max(maxQueue,socket.Pending);sentPackets++;
            var packet=new Wire{Source=node,Target=destination,Sender=node.Net.Uid,Method=method,Package=package,
                Socket=socket,Bytes=size,Due=now+0.05f,Free=now+0.1f};
            byte[] bytes=package.GetArray();
            if(method==RoutedRadioTransport.RpcName && ((dropData&&bytes[0]==1)||(dropAck&&bytes[0]==2)))
            { if(bytes[0]==1)dropData=false;else dropAck=false;packet.Arrived=true; }
            wire.Add(packet);
        };
        nodes.Add(node);Select(node);node.Transport.Poll((sender,data)=>node.Received.Add(data));return node;
    }
    private static void Step(bool services=false)
    {
        now+=0.02f;
        foreach(Wire packet in new List<Wire>(wire))
        {
            if(!packet.Arrived && now>=packet.Due)
            {
                Select(packet.Target);Action<long,ZPackage> callback;
                if(packet.Target.Rpc.Methods.TryGetValue(packet.Method,out callback))callback(packet.Sender,packet.Package);
                packet.Arrived=true;delivered++;
            }
            if(now>=packet.Free){packet.Socket.Pending-=packet.Bytes;wire.Remove(packet);}
        }
        foreach(Node node in nodes)
        {
            Select(node);
            if(services){node.Service.Tick();node.Service.Watch(new ZDOID(1,50));}
            else node.Transport.Poll((sender,data)=>node.Received.Add(data));
            foreach(var peer in node.Net.Peers)
                if(peer.m_socket.Pending>8192)throw new Exception("Recovery starved the native ZDO queue budget");
        }
        if(services)Thread.Sleep(1);
    }
    private static void Until(Func<bool> predicate,int ticks,string message,bool services=false)
    {
        for(int i=0;i<ticks && !predicate();i++)Step(services);
        Check(predicate(),message);
    }
    private static void Clear()
    {
        foreach(var node in nodes){Select(node);if(node.Service!=null)node.Service.Dispose();else node.Transport.Dispose();}
        nodes.Clear();wire.Clear();logs.Clear();now=0;dropData=dropAck=false;
    }
    public static int Main()
    {
        try { Basic();Multiplayer();Console.WriteLine("PASS: "+checks+" recovery transport and 8-player radio assertions; max recovery packet="+maxPacket+" bytes, observed game queue="+maxQueue+" bytes. Native Steam unavailable in this simulation.");return 0; }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally{Clear();}
    }
    private static void Basic()
    {
        Node host=Add(1,false),client=Add(2,false);
        byte[] payload=new byte[24580];new Random(21).NextBytes(payload);payload[0]=2;payload[1]=7;
        Select(host);Check(!host.Transport.TrySend(99,payload,262144),"unknown recipient rejected");
        Check(host.Transport.TrySend(2,payload,262144),"bulk accepted into bounded private memory");
        Check(host.Transport.TrySend(2,payload,262144),"duplicate pending transfer coalesced");
        host.Net.GetPeer(2).m_socket.Pending=6144;
        int sent=sentPackets;for(int i=0;i<30;i++)Step();
        Check(sentPackets==sent,"busy game connection delays all radio fragments");
        host.Net.GetPeer(2).m_socket.Pending=2048;client.Net.GetPeer(1).m_socket.Pending=2048;
        dropData=true;dropAck=true;
        Until(()=>client.Received.Count>0,2500,"data and ACK loss recover without a game restart");
        Check(client.Received[0].Length==payload.Length,"complete payload size");
        bool equal=true;for(int i=0;i<payload.Length;i++)equal&=client.Received[0][i]==payload[i];Check(equal,"all fragment bytes reconstructed exactly");
        for(int i=0;i<100;i++)Step();Check(client.Received.Count==1,"retries and duplicate sends never redeliver completed packet");
        Select(host);Check(host.Transport.TrySend(2,new byte[]{2,8},1024),"small following packet accepted");
        Until(()=>client.Received.Count==2,100,"following transfer not blocked by previous retry");
        Select(host);host.Net.World=18;Check(!host.Transport.TrySend(2,payload,262144),"old session cannot send after world switch");
        host.Transport.Poll(delegate{});Check(host.Transport.TrySend(2,payload,262144),"new world starts fresh transfer");
        for(int i=0;i<100;i++)Step();Check(client.Received.Count==2,"other-world fragments rejected before delivery");
        Clear();host=Add(1,true);client=Add(2,true);
        Select(host);Check(!host.Transport.TrySend(2,payload,262144),"separate Steam channel gets an initial recovery interval");
        for(int i=0;i<510;i++)Step();Select(host);Check(host.Transport.TrySend(2,payload,262144),"failed Steam connection activates bounded recovery");
        Until(()=>client.Received.Count==1,400,"music payload delivered despite total Steam Messages outage");
        Select(client);Check(client.Transport.TrySend(1,new byte[]{2,6},262144),"client immediately replies through confirmed recovery path");
        Until(()=>host.Received.Count==1,100,"recovery is bidirectional");
        Check(logs.Count>=2,"recovery path logged on both peers");
        Clear();
        host=Add(1,false);client=Add(2,false);Select(host);
        for(int i=0;i<8;i++)Check(host.Transport.TrySend(2,new byte[]{2,8,(byte)i},262144),"bounded private queue accepts entry "+i);
        Check(!host.Transport.TrySend(2,new byte[]{2,8,9},262144),"private queue count bound");
        host.Transport.Reset();host.Transport.Poll(delegate{});
        byte[] large=new byte[RadioProtocol.MaxPacket];large[0]=2;large[1]=2;
        Check(host.Transport.TrySend(2,large,262144),"maximum playlist held privately");large[2]=1;
        Check(!host.Transport.TrySend(2,large,262144),"private queue byte bound includes frame overhead");
        Check(!host.Transport.TrySend(2,new byte[RadioProtocol.MaxPacket+1],262144),"oversize input rejected");
        host.Transport.Dispose();Check(!host.Transport.TrySend(2,payload,262144),"disposed recovery cannot send");
        Clear();host=Add(1,false);client=Add(2,false);Select(host);
        var steam=new DeadSteam();var memory=new DeadSteam{Available=true};
        var hybrid=new RecoveringRadioTransport(steam,memory,logs.Add);
        Check(!hybrid.TrySend(2,payload,262144),"hybrid waits for Steam first");
        now=11;Select(host);Check(hybrid.TrySend(2,payload,262144),"hybrid selects recovery after timeout");
        steam.Available=true;int attempts=steam.Sends;
        Check(hybrid.TrySend(2,payload,262144) && steam.Sends==attempts,"recovery route remains ordered when Steam recovers");
        steam.Incoming=new byte[]{2,2};int received=0;hybrid.Poll((sender,data)=>received++);
        Check(received==0,"late playlist from old route cannot overwrite recovery playlist");
        hybrid.Reset();Check(hybrid.TrySend(2,payload,262144) && steam.Sends==attempts+1,"new session can select fast Steam route again");
        hybrid.Dispose();Clear();
    }
    private static void Multiplayer()
    {
        string root=Path.Combine(Path.GetTempPath(),"NordicRadio-recovery-"+Guid.NewGuid().ToString("N"));
        for(int i=1;i<=8;i++)Add(i,true);
        var horn=new ZDO{m_uid=new ZDOID(1,50),Prefab="vmp_skald_horn".GetStableHashCode(),Position=new UnityEngine.Vector3(0,0,0)};
        foreach(Node node in nodes)
        {
            node.Objects.Objects.Add(horn.m_uid,horn);
            foreach(Node other in nodes)node.Objects.Objects.Add(other.Player.Character.m_uid,other.Player.Character);
            string dataRoot=Path.Combine(root,"peer-"+node.Net.Uid);
            Directory.CreateDirectory(Path.Combine(dataRoot,"Music"));
            if(node.Net.Host)
            {
                using(var stream=File.Create(Path.Combine(dataRoot,"Music","recovery-test.mp3")))
                for(int i=0;i<200;i++){var frame=new byte[417];frame[0]=255;frame[1]=251;frame[2]=144;stream.Write(frame,0,frame.Length);}
            }
            Select(node);node.Service=new RadioService(null,dataRoot,logs.Add,node.Transport);
        }
        Until(()=>nodes.TrueForAll(n=>n.Service.Tracks.Count==1),3000,"all 7 listeners receive host playlist with Steam transport unavailable",true);
        string id=nodes[0].Service.Tracks[0].Id;
        foreach(Node node in nodes){Select(node);node.Service.Watch(horn.m_uid);}
        for(int i=0;i<30;i++)Step(true);
        // Exercise a friend's command, not just host playback.
        Select(nodes[1]);nodes[1].Service.Command(horn.m_uid,"select",id,0);
        Until(()=>nodes.TrueForAll(n=>n.Service.GetState(horn.m_uid)!=null && n.Service.GetState(horn.m_uid).TrackId==id),500,"friend can control the radio and all peers receive selected track",true);
        foreach(Node node in nodes){Select(node);node.Service.RequestTrack(id);}
        Until(()=>AllReady(id),7000,"all listeners finish MP3 download and SHA validation without freezing world replication",true);
        Check(AllReady(id),"verified audio files are ready on every client");
        Check(maxPacket<=809,"playlist and audio never become a large gameplay packet");
        Check(maxQueue<=8192,"native ZDO 2048-byte reserve maintained throughout multiplayer transfer");
        Console.WriteLine("Recovery simulation fixtures: "+root);
    }
    private static bool AllReady(string id)
    {
        foreach(Node node in nodes){Select(node);string path=node.Service.GetTrackPath(id);if(path==null || !File.Exists(path))return false;}
        return true;
    }
}
