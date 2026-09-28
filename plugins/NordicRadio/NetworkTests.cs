using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ValheimModPack.NordicRadio;

internal static partial class NetworkTests
{
    private static int assertions;
    private static readonly string A=new string('a',64), B=new string('b',64), C=new string('c',64);
    private static void Check(bool good,string name) { if(!good) throw new Exception(name); assertions++; }
    private static void Reject(Action action,string name) { bool failed=false; try { action(); } catch(InvalidDataException) { failed=true; } catch(EndOfStreamException) { failed=true; } Check(failed,name); }
    private static TrackInfo Track(string id) { return new TrackInfo { Id=id,Title="Track",Size=123,Duration=5 }; }
    public static int Main()
    {
        try { Run(); return 0; }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"NordicRadio-tests-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        ThreadPool.SetMinThreads(16,16);
        Protocol(); Library(root); Network(root); Transfers(root);
        Console.WriteLine("PASS: "+assertions+" NordicRadio protocol, library, state, rate and simulated multiplayer assertions.");
        Console.WriteLine("Fixtures retained: "+root);
    }
    private static void Protocol()
    {
        Check(RadioProtocol.ValidId(A),"valid hash");
        Check(!RadioProtocol.ValidId("../"+A) && !RadioProtocol.ValidId(A.ToUpperInvariant()),"reject paths and uppercase");
        var library=new RadioMessage { Kind=RadioMessageKind.Library,Tracks=new List<TrackInfo>{Track(A),Track(B)} };
        var encoded=RadioProtocol.Encode(library);
        Check(RadioProtocol.Decode(encoded).Tracks.Count==2,"playlist roundtrip");
        byte[] truncated=new byte[encoded.Length-1]; Array.Copy(encoded,truncated,truncated.Length);
        Reject(()=>RadioProtocol.Decode(truncated),"truncated packet");
        Reject(()=>RadioProtocol.Decode(new byte[RadioProtocol.MaxPacket+1]),"packet limit");
        Reject(()=>RadioProtocol.Decode(new byte[]{2,255}),"unknown message");
        Reject(()=>RadioProtocol.Decode(new byte[]{1,1}),"protocol version");
        Reject(()=>RadioProtocol.Decode(new byte[]{2,2,255,255,255,127}),"playlist allocation limit");
        string token = new string('a',32);
        var presence = new RadioMessage { Kind=RadioMessageKind.PortablePresence,Owner=1,Object=100,Id=token };
        Check(RadioProtocol.Decode(RadioProtocol.Encode(presence)).Id==token,"portable presence token roundtrip");
        presence.Id=""; Check(RadioProtocol.Decode(RadioProtocol.Encode(presence)).Id=="","portable release roundtrip");
        presence.Id=token.ToUpperInvariant(); Reject(()=>RadioProtocol.Decode(RadioProtocol.Encode(presence)),"portable uppercase token rejected");
        presence.Id=new string('a',33); Reject(()=>RadioProtocol.Encode(presence),"portable token allocation bounded");
        presence.Id="../invalid"; Reject(()=>RadioProtocol.Decode(RadioProtocol.Encode(presence)),"portable malformed token rejected");
        library.Tracks.Add(Track(A)); Reject(()=>RadioProtocol.Decode(RadioProtocol.Encode(library)),"duplicate content ids");
        Reject(()=>RadioProtocol.Decode(RadioProtocol.Encode(new RadioMessage{Kind=RadioMessageKind.ChunkRequest,Id=A,Offset=1})),"unaligned offsets");
        Reject(()=>RadioProtocol.Decode(RadioProtocol.Encode(new RadioMessage{Kind=RadioMessageKind.Command,Owner=1,Object=1,Command="volume",Value=Single.NaN})),"nonfinite input");
        Reject(()=>RadioProtocol.Decode(RadioProtocol.Encode(new RadioMessage{Kind=RadioMessageKind.State,Owner=1,Object=1,State=new RadioSnapshot{Playing=true}})),"playing empty state");
        var state=new RadioSnapshot(); var songs=new List<TrackInfo>{Track(A),Track(B),Track(C)}; var random=new Random(1);
        Check(RadioProtocol.Apply(state,songs,"previous","",0,10,random) && state.TrackId==C,"previous on empty selects last");
        Check(RadioProtocol.Apply(state,songs,"next","",0,12,random) && state.TrackId==A,"next wraps");
        Check(RadioProtocol.Apply(state,songs,"pause","",0,14,random) && !state.Playing && state.Offset==2,"pause preserves position");
        Check(RadioProtocol.Apply(state,songs,"play","",0,20,random) && Math.Abs(state.Position(21)-3)<0.001,"resume preserves offset");
        int revision=state.Revision;
        Check(!RadioProtocol.Apply(state,songs,"select",new string('d',64),0,22,random) && state.Revision==revision,"unknown selection rejected");
        Check(!RadioProtocol.Apply(state,songs,"volume","",2,22,random),"invalid volume rejected");
        Check(RadioProtocol.Apply(state,songs,"select",B,0,22,random) && state.TrackId==B && state.Offset==0,"select restarts");
        RadioProtocol.Apply(state,songs,"shuffle","",1,22,random);
        RadioProtocol.Apply(state,songs,"next","",0,22,random);
        Check(state.TrackId!=B,"shuffle avoids same song");
        var bucket=new RadioTokenBucket(100,100);
        Check(bucket.Take(100,0) && !bucket.Take(1,0),"upload burst bound");
        Check(bucket.Take(50,0.5) && !bucket.Take(1,0.5),"upload rate bound");
        Check(!bucket.Take(1,0.4),"backwards clock adds no tokens");
        Check(!bucket.Take(Double.NaN,1),"nonfinite bucket cost rejected");
    }
    private static void WriteMp3(string path,int frames,bool variable)
    {
        using(var stream=File.Create(path)) for(int i=0;i<frames;i++)
        {
            int bitrate=variable && i%2==1?160000:128000;
            int size=144*bitrate/44100;
            byte[] frame=new byte[size]; frame[0]=255;frame[1]=251;frame[2]=(byte)(bitrate==128000?144:160);
            stream.Write(frame,0,frame.Length);
        }
    }
    private static void Library(string root)
    {
        var library=new RadioLibrary(Path.Combine(root,"файлы музыки"));
        // '<' is invalid on Windows: cover title sanitization independently.
        string path=Path.Combine(library.MusicDirectory,"песня.mp3");
        WriteMp3(path,100,true);
        Check(Math.Abs(RadioLibrary.Mp3Duration(path)-100*1152.0/44100)<0.000001,"exact VBR frame duration");
        Check(RadioLibrary.SafeTitle("<b>Rune</b>\n").IndexOf('<')<0,"UI title markup stripped");
        File.Copy(path,Path.Combine(library.MusicDirectory,"duplicate.mp3"));
        File.WriteAllText(Path.Combine(library.MusicDirectory,"invalid.mp3"),"This is not MPEG audio.");
        List<LibraryEntry> entries=library.Scan(delegate{});
        Check(entries.Count==1,"invalid MP3 and duplicate skipped");
        TrackInfo track=entries[0].Track;
        Reject(()=>library.CachePath("../outside"),"cache path traversal rejected");
        string cache=library.CachePath(track.Id);
        File.WriteAllText(cache,"wrong");
        Check(library.ValidateCache(track)==null,"corrupt cache rejected");
        File.Copy(path,cache,true);
        Check(library.ValidateCache(track)==cache && library.GetVerified(track.Id)==cache,"verified cache available");
        File.AppendAllText(cache,"changed");
        Check(library.GetVerified(track.Id)==null,"changed verified cache invalidated");
        string temp=library.BeginDownload(track,new HashSet<string>());
        File.Copy(path,temp); library.CommitDownload(track.Id,temp);
        Check(!File.Exists(temp) && RadioLibrary.HashFile(cache)==track.Id,"atomic cache completion");
        Check(library.Scan(delegate{},()=>true).Count==0,"cancelled library scan");
    }
    private sealed class Node
    {
        public ZNet Net; public ZRoutedRpc Rpc; public RadioService Service; public Player Player; public ZDOMan Objects; public TestBulk Bulk;
    }
    private sealed class TestBulk : IRadioBulkTransport
    {
        private readonly Node node;
        public bool Available=true;
        public readonly HashSet<long> BlockedPeers=new HashSet<long>();
        public readonly Dictionary<long,int> Attempts=new Dictionary<long,int>();
        public readonly Queue<Delivery> Inbox=new Queue<Delivery>();
        private readonly Dictionary<long,TestSocket> queues=new Dictionary<long,TestSocket>();
        public TestBulk(Node node) { this.node=node; }
        public TestSocket Queue(long uid) { TestSocket socket; if(!queues.TryGetValue(uid,out socket)) queues.Add(uid,socket=new TestSocket()); return socket; }
        public bool TrySend(long peer,byte[] data,int limit)
        {
            int count; Attempts.TryGetValue(peer,out count);Attempts[peer]=count+1;
            if(!Available || BlockedPeers.Contains(peer)) return false;
            var kind=(RadioMessageKind)data[1];
            if(kind==RadioMessageKind.Chunk && node.Net.GetPeer(peer).m_socket.Pending>4096) return false;
            int size=data.Length+RadioBulkFrame.HeaderSize;
            if(Queue(peer).Pending+size>(kind==RadioMessageKind.Library?Math.Max(limit,size):limit)) return false;
            QueuePacket(node.Net.Uid,peer,"bulk",new ZPackage(data)); return true;
        }
        public void Poll(Action<long,byte[]> receive) { for(int i=0;i<8 && Inbox.Count>0;i++){Delivery p=Inbox.Dequeue();receive(p.Sender,p.Package.GetArray());} }
        public void Reset() { Inbox.Clear(); queues.Clear(); }
        public void Dispose() { Reset(); }
    }
    private sealed class Delivery { public long Sender,Target; public string Name; public ZPackage Package; public double Due; public TestSocket Socket; }
    private static readonly List<Node> nodes=new List<Node>();
    private static readonly Queue<Delivery> wire=new Queue<Delivery>();
    private static int worldBlockedTicks;
    private static void Select(Node node) { ZNet.instance=node.Net; ZRoutedRpc.instance=node.Rpc; ZDOMan.instance=node.Objects; Player.m_localPlayer=node.Player; }
    private static void Pump(int count)
    {
        for(int round=0;round<count;round++)
        {
            UnityEngine.Time.realtimeSinceStartup+=0.025f;
            foreach(Node node in nodes) { Select(node); node.Service.Tick(); }
            // Valheim ZDOMan.SendZDOs needs at least 2048 of its 10240-byte budget.
            foreach(Node node in nodes) if(node.Net.Host)
                foreach(ZNetPeer peer in node.Net.Peers) if(peer.m_socket.Pending>8192) { worldBlockedTicks++; break; }
            for(int j=wire.Count;j>0;j--)
            {
                Delivery packet=wire.Dequeue(); Node target=nodes.Find(n=>n.Net.Uid==packet.Target);
                if(packet.Due>UnityEngine.Time.realtimeSinceStartup) { wire.Enqueue(packet); continue; }
                if(packet.Socket!=null) acknowledgements.Enqueue(new Delivery {Name=packet.Name,Socket=packet.Socket,Package=packet.Package,Due=UnityEngine.Time.realtimeSinceStartup+transferDelay});
                if(target==null) continue; Select(target); Action<long,ZPackage> receiver;
                if(packet.Name=="bulk") { target.Bulk.Inbox.Enqueue(packet); continue; }
                if(target.Rpc.Methods.TryGetValue(packet.Name,out receiver)) receiver(packet.Sender,packet.Package);
            }
            for(int j=acknowledgements.Count;j>0;j--)
            {
                Delivery ack=acknowledgements.Dequeue();
                if(ack.Due>UnityEngine.Time.realtimeSinceStartup) acknowledgements.Enqueue(ack);
                else ack.Socket.Pending-=ack.Package.Size()+(ack.Name=="bulk"?RadioBulkFrame.HeaderSize:0);
            }
            Thread.Sleep(1);
        }
    }
    private static void Network(string root)
    {
        int blockedBefore=worldBlockedTicks;
        var objects=new ZDOMan(); var radio=new ZDOID(1,1);
        objects.Objects[radio]=new ZDO { Prefab="vmp_skald_horn".GetStableHashCode() };
        for(int i=0;i<8;i++)
        {
            long uid=i+1; var node=new Node { Net=new ZNet {Uid=uid,Host=i==0},Rpc=new ZRoutedRpc(),Player=new Player(),Objects=objects };
            var characterId = new ZDOID(uid,100);
            objects.Objects[characterId]=node.Player.Character=new ZDO { m_uid=characterId };
            node.Rpc.Transport=(peer,name,package)=>QueuePacket(uid,peer,name,package);
            string data=Path.Combine(root,"node"+uid); Directory.CreateDirectory(Path.Combine(data,"Music"));
            if(i==0) WriteMp3(Path.Combine(data,"Music","Host song.mp3"),1000,true);
            Select(node); node.Bulk=new TestBulk(node); node.Service=new RadioService(null,data,Console.WriteLine,node.Bulk); nodes.Add(node);
        }
        Node host=nodes[0];
        musicRecipients.Clear();
        for(int i=1;i<nodes.Count;i++)
        {
            host.Net.Peers.Add(new ZNetPeer {m_uid=nodes[i].Net.Uid,m_characterID=new ZDOID(nodes[i].Net.Uid,100)});
            nodes[i].Net.Peers.Add(new ZNetPeer{m_uid=1,m_characterID=new ZDOID(1,100)});
        }
        host.Bulk.BlockedPeers.Add(2);
        Pump(120);
        Check(nodes[1].Service.Tracks.Count==0 && nodes[2].Service.Tracks.Count==1,"unavailable music peer does not block others' playlists");
        Check(host.Bulk.Attempts[2]<10,"failed large playlists are retried with cooldown rather than every frame");
        host.Bulk.BlockedPeers.Clear();Pump(60);
        Check(nodes[1].Service.Tracks.Count==1,"pending playlist recovers when music channel becomes available");
        Select(host); Check(host.Service.Tracks.Count==1,"host asynchronous scan");
        string id=host.Service.Tracks[0].Id;
        objects.Objects[new ZDOID(7,100)].Position=new UnityEngine.Vector3(145,0,0);
        objects.Objects[new ZDOID(8,100)].Position=new UnityEngine.Vector3(201,0,0);
        foreach(Node node in nodes) { Select(node); node.Service.Watch(radio); }
        Pump(8);
        Select(nodes[6]); Check(nodes[6].Service.GetState(radio)!=null,"listener beyond old 100m radius receives state");
        Select(nodes[7]); Check(nodes[7].Service.GetState(radio)==null,"watch outside 200m rejected");
        Select(nodes[1]); nodes[1].Service.Command(radio,"play","",0); Pump(4);
        Select(host); Check(host.Service.GetState(radio).Playing,"client command accepted nearby");
        Select(nodes[6]); Check(nodes[6].Service.GetState(radio).Playing,"listener at 145m receives live state changes");
        Select(nodes[7]); Check(nodes[7].Service.GetState(radio)==null,"unsubscribed far client receives no state broadcast");
        objects.Objects[new ZDOID(8,100)].Position=new UnityEngine.Vector3(199,0,0);
        Pump(60);
        Select(nodes[7]); nodes[7].Service.Watch(radio); Pump(4);
        Select(nodes[7]); Check(nodes[7].Service.GetState(radio)!=null && nodes[7].Service.GetState(radio).Playing,"approaching listener receives currently playing state within 200m");
        Select(nodes[1]);
        RadioSnapshot genuine=nodes[1].Service.GetState(radio);
        var forged=new RadioMessage { Kind=RadioMessageKind.State,Owner=1,Object=1,State=new RadioSnapshot{Volume=0.1f,Revision=genuine.Revision+1} };
        nodes[1].Rpc.Methods[RadioProtocol.RpcName](3,new ZPackage(RadioProtocol.Encode(forged)));
        Check(nodes[1].Service.GetState(radio).Volume==genuine.Volume,"non-host state injection rejected");
        forged.State.Revision=0;
        nodes[1].Rpc.Methods[RadioProtocol.RpcName](1,new ZPackage(RadioProtocol.Encode(forged)));
        Check(nodes[1].Service.GetState(radio).Playing,"stale state revision ignored");
        for(int i=1;i<8;i++) { Select(nodes[i]); Check(nodes[i].Service.Tracks.Count==1,"client receives host playlist "+i); }
        for(int round=0;round<400;round++)
        {
            foreach(Node node in nodes) { Select(node); node.Service.Watch(radio); node.Service.RequestTrack(id); }
            Pump(1);
            if(round==100) Check(musicRecipients.Count==7,"all seven listeners get upload turns while transfers are active");
        }
        for(int i=1;i<8;i++)
        {
            Select(nodes[i]); string path=nodes[i].Service.GetTrackPath(id);
            Check(path!=null && RadioLibrary.HashFile(path)==id,"validated MP3 delivery to client "+i);
        }
        Check(worldBlockedTicks==blockedBefore,"seven simultaneous listeners do not block world updates");
        Select(nodes[1]);
        var unsolicited=new RadioMessage {Kind=RadioMessageKind.Library,Tracks=new List<TrackInfo>()};
        nodes[1].Rpc.Methods[RadioProtocol.RpcName](1,new ZPackage(RadioProtocol.Encode(unsolicited)));
        Check(nodes[1].Service.Tracks.Count==1,"bulk playlist cannot fall back to gameplay RPC");
        nodes[1].Bulk.Inbox.Enqueue(new Delivery{Sender=3,Package=new ZPackage(RadioProtocol.Encode(unsolicited))});
        Pump(1);Select(nodes[1]);
        Check(nodes[1].Service.Tracks.Count==1,"another client's bulk playlist injection rejected");
        // Simulate metadata lost across a bulk connection restart while control
        // state still arrives. The unknown song must trigger a fresh host playlist.
        nodes[1].Bulk.Inbox.Enqueue(new Delivery{Sender=1,Package=new ZPackage(RadioProtocol.Encode(unsolicited))});
        Pump(1);Select(nodes[1]);Check(nodes[1].Service.Tracks.Count==0,"metadata recovery fixture applied");
        for(int i=0;i<200 && nodes[1].Service.Tracks.Count==0;i++)Pump(1);
        Check(nodes[1].Service.Tracks.Count==1,"unknown playing song resynchronizes lost metadata over bulk transport");
        Pump(240); // Cross a maintenance pass without refreshing these still-valid subscriptions.
        Select(host); host.Service.Command(radio,"volume","",0.65f); Pump(4);
        Select(nodes[6]); Check(nodes[6].Service.GetState(radio).Volume==0.65f,"145m listener remains subscribed after maintenance");
        Select(nodes[7]); Check(nodes[7].Service.GetState(radio).Volume==0.65f,"199m listener remains subscribed after maintenance");
        Select(host); float original=host.Service.GetState(radio).Volume;
        Select(nodes[6]); nodes[6].Service.Command(radio,"volume","",0.1f); Pump(4);
        Select(host); Check(host.Service.GetState(radio).Volume==original,"extended listening radius does not permit remote control at 145m");
        objects.Objects[new ZDOID(2,100)].Position=new UnityEngine.Vector3(200,0,0);
        Select(nodes[1]); nodes[1].Service.Command(radio,"volume","",0.1f); Pump(4);
        Select(host); Check(host.Service.GetState(radio).Volume==original,"distant command rejected");
        objects.Objects[new ZDOID(2,100)].Position=new UnityEngine.Vector3();
        objects.Objects[radio].Prefab=123;
        Select(nodes[1]); nodes[1].Service.Command(radio,"volume","",0.2f); Pump(4);
        Select(host); Check(host.Service.GetState(radio).Volume==original,"wrong prefab command rejected");
        objects.Objects[radio].Prefab="vmp_skald_horn".GetStableHashCode();
        PortableNetwork(root,host,objects,radio,id);
        double before=host.Service.GetState(radio).Position(host.Net.GetTimeSeconds());
        host.Net.ClockShift=1000; Pump(1); Select(host);
        Check(Math.Abs(host.Service.GetState(radio).Position(host.Net.GetTimeSeconds())-before)<0.1,"sleep time jump preserves song position");
        Select(nodes[1]); nodes[1].Service.ResetSession(); Pump(4);
        Check(nodes[1].Rpc.Methods.Count==1,"reconnect reset does not duplicate RPC registration");
        foreach(Node node in nodes) { Select(node); node.Service.Dispose(); }
        nodes.Clear(); wire.Clear();
    }

    private static void PortableNetwork(string root, Node host, ZDOMan objects, ZDOID radio, string song)
    {
        Node owner=nodes[1], listener=nodes[2], attacker=nodes[3];
        ZDOID portable=new ZDOID(2,100), hostPortable=new ZDOID(1,100);
        string tokenA=new string('a',32), tokenB=new string('b',32), tokenC=new string('c',32);
        foreach(Node node in nodes) objects.Objects[node.Player.Character.m_uid].Position=new UnityEngine.Vector3();
        Select(attacker); attacker.Service.SetPortable(portable,tokenA); attacker.Service.Watch(portable); Pump(4);
        Select(host); Check(host.Service.GetState(portable)==null,"another player cannot claim a portable source");
        float fixedVolume=host.Service.GetState(radio).Volume;
        Select(owner); owner.Service.SetPortable(radio,tokenA); Pump(4);
        Select(attacker); attacker.Service.Command(radio,"volume","",0.42f); Pump(4);
        Select(host); Check(host.Service.GetState(radio).Volume==0.42f,"forged portable identity cannot restrict stationary radio ownership");
        host.Service.Command(radio,"volume","",fixedVolume);

        Select(owner); owner.Service.SetPortable(portable,tokenA); owner.Service.Watch(portable); Pump(4);
        Select(host); Check(host.Service.GetState(portable)!=null,"owner establishes portable source on its character identity");
        Select(listener); listener.Service.Watch(portable); Pump(4);
        Select(owner); owner.Service.Command(portable,"play","",0); Pump(4);
        Select(listener); Check(listener.Service.GetState(portable).Playing,"nearby listener hears portable state");
        objects.Objects[portable].Position=new UnityEngine.Vector3(250,0,0);
        Select(listener); float nearVolume=listener.Service.GetState(portable).Volume;
        Select(owner); owner.Service.Command(portable,"volume","",0.25f); Pump(4);
        Select(host); Check(host.Service.GetState(portable).Volume==0.25f,"owner controls moving source using authoritative character position");
        Select(listener); Check(listener.Service.GetState(portable).Volume==nearVolume,"distant listener receives no moving-source state outside 200m");
        objects.Objects[portable].Position=new UnityEngine.Vector3(145,0,0);
        Select(owner); owner.Service.Command(portable,"volume","",0.7f); Pump(4);
        Select(listener); Check(listener.Service.GetState(portable).Volume==0.7f,"moving source approaching within range resumes shared updates");
        objects.Objects[portable].Position=new UnityEngine.Vector3();
        Select(attacker); attacker.Service.Command(portable,"pause","",0); Pump(4);
        Select(host); Check(host.Service.GetState(portable).Playing,"nearby other player cannot control owner portable");
        Select(attacker); attacker.Service.SetPortable(portable,""); Pump(4);
        Select(host); Check(host.Service.GetState(portable).Playing,"forged portable release rejected");

        // A fresh song reaches a listener with only a portable subscription.
        WriteMp3(Path.Combine(root,"node1","Music","Portable song.mp3"),700,false);
        Select(host); host.Service.RefreshLibrary(); Pump(80);
        Select(host); string extra=null;
        foreach(TrackInfo track in host.Service.Tracks) if(track.Id!=song) extra=track.Id;
        Check(extra!=null,"additional host track scanned for portable delivery");
        Select(listener); listener.Service.ResetSession(); Pump(4);
        Select(listener); listener.Service.Watch(portable);
        // A reconnect can fall inside the host's two-second Hello cooldown;
        // allow the next five-second greeting and asynchronous cache verification.
        for(int round=0;round<600;round++)
        {
            Select(owner); owner.Service.SetPortable(portable,tokenA);
            Select(listener); listener.Service.Watch(portable); listener.Service.RequestTrack(extra);
            Pump(1);
            Select(listener); if(listener.Service.GetTrackPath(extra)!=null) break;
        }
        Select(listener); string path=listener.Service.GetTrackPath(extra);
        Check(path!=null && RadioLibrary.HashFile(path)==extra,"host MP3 transferred using portable-only subscription");
        Select(host); Check(host.Service.GetState(portable).Playing,"owner heartbeat renews portable past original expiry");
        Select(owner); owner.Service.Command(portable,"volume","",0.4f); Pump(4);
        Select(listener); Check(listener.Service.GetState(portable).Volume==0.4f,"portable settings use same shared state menu commands");

        Select(host); int revision=host.Service.GetState(portable).Revision;
        Select(owner); owner.Service.SetPortable(portable,""); Pump(4);
        Select(host); Check(!host.Service.GetState(portable).Playing && host.Service.GetState(portable).Revision>revision,"release stops playback and advances revision");
        Select(listener); Check(!listener.Service.GetState(portable).Playing,"release stop reaches existing listeners after lease removal");
        Select(owner); owner.Service.Command(portable,"play","",0); Pump(4);
        Select(host); Check(!host.Service.GetState(portable).Playing,"released portable cannot restart without ownership heartbeat");
        Select(owner); owner.Service.SetPortable(portable,tokenB); Pump(4);
        Select(host); Check(!host.Service.GetState(portable).Playing,"new inventory item does not automatically restart old music");
        Select(owner); owner.Service.Command(portable,"play","",0); Pump(4);
        Select(owner); owner.Service.SetPortable(portable,tokenC); Pump(4);
        Select(host); Check(!host.Service.GetState(portable).Playing,"changing exact item token stops old playback");
        Select(owner); owner.Service.Command(portable,"play","",0); Pump(4);
        Select(listener); listener.Service.Watch(portable); Pump(4);
        Pump(245);
        Select(host); Check(!host.Service.GetState(portable).Playing,"portable expires without owner heartbeat within six seconds");
        Select(listener); Check(!listener.Service.GetState(portable).Playing,"expired source stop is delivered to listeners");

        Select(owner); owner.Service.SetPortable(portable,tokenA); owner.Service.Command(portable,"play","",0); Pump(4);
        ZNetPeer peer=host.Net.GetPeer(2); host.Net.Peers.Remove(peer); Pump(4);
        Select(host); Check(!host.Service.GetState(portable).Playing,"disconnect immediately revokes portable lease");
        host.Net.Peers.Add(peer);
        Select(owner); owner.Service.SetPortable(portable,tokenB); owner.Service.Command(portable,"play","",0); Pump(4);
        ZDO character=objects.Objects[portable]; objects.Objects.Remove(portable); Pump(4);
        Select(host); RadioSnapshot vanished=host.Service.GetState(portable);
        Check(vanished==null || !vanished.Playing,"missing character ZDO revokes playback");
        objects.Objects[portable]=character;

        Select(host); host.Service.SetPortable(hostPortable,tokenA); host.Service.Watch(hostPortable); host.Service.Command(hostPortable,"play","",0);
        Check(host.Service.GetState(hostPortable).Playing,"listen host can operate its own portable source");
        host.Player.Dead=true; Pump(4);
        Select(host); Check(!host.Service.GetState(hostPortable).Playing,"host death immediately revokes its portable source");
        host.Player.Dead=false;
        host.Service.Command(radio,"play","",0); Pump(4);
        Select(host); Check(host.Service.GetState(radio).Playing,"stationary horn remains usable after portable lifecycle changes");
    }
}
