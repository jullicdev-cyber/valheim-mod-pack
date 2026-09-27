using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ValheimModPack.NordicRadio;

internal static class NetworkTests
{
    private static int assertions;
    private static readonly string A=new string('a',64), B=new string('b',64), C=new string('c',64);
    private static void Check(bool good,string name) { if(!good) throw new Exception(name); assertions++; }
    private static void Reject(Action action,string name) { bool failed=false; try { action(); } catch(InvalidDataException) { failed=true; } catch(EndOfStreamException) { failed=true; } Check(failed,name); }
    private static TrackInfo Track(string id) { return new TrackInfo { Id=id,Title="Track",Size=123,Duration=5 }; }
    public static void Main()
    {
        string root=Path.Combine(Path.GetTempPath(),"NordicRadio-tests-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Protocol(); Library(root); Network(root);
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
        Reject(()=>RadioProtocol.Decode(new byte[]{1,255}),"unknown message");
        Reject(()=>RadioProtocol.Decode(new byte[]{2,1}),"protocol version");
        Reject(()=>RadioProtocol.Decode(new byte[]{1,2,255,255,255,127}),"playlist allocation limit");
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
        public ZNet Net; public ZRoutedRpc Rpc; public RadioService Service; public Player Player; public ZDOMan Objects;
    }
    private sealed class Delivery { public long Sender,Target; public string Name; public ZPackage Package; }
    private static readonly List<Node> nodes=new List<Node>();
    private static readonly Queue<Delivery> wire=new Queue<Delivery>();
    private static void Select(Node node) { ZNet.instance=node.Net; ZRoutedRpc.instance=node.Rpc; ZDOMan.instance=node.Objects; Player.m_localPlayer=node.Player; }
    private static void Pump(int count)
    {
        for(int round=0;round<count;round++)
        {
            UnityEngine.Time.realtimeSinceStartup+=0.025f;
            foreach(Node node in nodes) { Select(node); node.Service.Tick(); }
            for(int j=wire.Count;j>0;j--)
            {
                Delivery packet=wire.Dequeue(); Node target=nodes.Find(n=>n.Net.Uid==packet.Target);
                if(target==null) continue; Select(target); Action<long,ZPackage> receiver;
                if(target.Rpc.Methods.TryGetValue(packet.Name,out receiver)) receiver(packet.Sender,packet.Package);
            }
            Thread.Sleep(1);
        }
    }
    private static void Network(string root)
    {
        var objects=new ZDOMan(); var radio=new ZDOID(1,1);
        objects.Objects[radio]=new ZDO { Prefab="vmp_skald_horn".GetStableHashCode() };
        for(int i=0;i<8;i++)
        {
            long uid=i+1; var node=new Node { Net=new ZNet {Uid=uid,Host=i==0},Rpc=new ZRoutedRpc(),Player=new Player(),Objects=objects };
            objects.Objects[new ZDOID(uid,100)]=new ZDO();
            node.Rpc.Transport=(peer,name,package)=>wire.Enqueue(new Delivery{Sender=uid,Target=peer,Name=name,Package=package});
            string data=Path.Combine(root,"node"+uid); Directory.CreateDirectory(Path.Combine(data,"Music"));
            if(i==0) WriteMp3(Path.Combine(data,"Music","Host song.mp3"),1000,true);
            Select(node); node.Service=new RadioService(null,data,Console.WriteLine); nodes.Add(node);
        }
        Node host=nodes[0];
        for(int i=1;i<nodes.Count;i++)
        {
            host.Net.Peers.Add(new ZNetPeer {m_uid=nodes[i].Net.Uid,m_characterID=new ZDOID(nodes[i].Net.Uid,100)});
            nodes[i].Net.Peers.Add(new ZNetPeer{m_uid=1,m_characterID=new ZDOID(1,100)});
        }
        Pump(120);
        Select(host); Check(host.Service.Tracks.Count==1,"host asynchronous scan");
        string id=host.Service.Tracks[0].Id;
        objects.Objects[new ZDOID(7,100)].Position=new UnityEngine.Vector3(115,0,0);
        objects.Objects[new ZDOID(8,100)].Position=new UnityEngine.Vector3(151,0,0);
        foreach(Node node in nodes) { Select(node); node.Service.Watch(radio); }
        Pump(8);
        Select(nodes[6]); Check(nodes[6].Service.GetState(radio)!=null,"listener beyond old 100m radius receives state");
        Select(nodes[7]); Check(nodes[7].Service.GetState(radio)==null,"watch outside 150m rejected");
        Select(nodes[1]); nodes[1].Service.Command(radio,"play","",0); Pump(4);
        Select(host); Check(host.Service.GetState(radio).Playing,"client command accepted nearby");
        Select(nodes[6]); Check(nodes[6].Service.GetState(radio).Playing,"listener at 115m receives live state changes");
        Select(nodes[7]); Check(nodes[7].Service.GetState(radio)==null,"unsubscribed far client receives no state broadcast");
        objects.Objects[new ZDOID(8,100)].Position=new UnityEngine.Vector3(149,0,0);
        Pump(60);
        Select(nodes[7]); nodes[7].Service.Watch(radio); Pump(4);
        Select(nodes[7]); Check(nodes[7].Service.GetState(radio)!=null && nodes[7].Service.GetState(radio).Playing,"approaching listener receives currently playing state within 150m");
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
        }
        for(int i=1;i<8;i++)
        {
            Select(nodes[i]); string path=nodes[i].Service.GetTrackPath(id);
            Check(path!=null && RadioLibrary.HashFile(path)==id,"validated MP3 delivery to client "+i);
        }
        Pump(240); // Cross a maintenance pass without refreshing these still-valid subscriptions.
        Select(host); host.Service.Command(radio,"volume","",0.65f); Pump(4);
        Select(nodes[6]); Check(nodes[6].Service.GetState(radio).Volume==0.65f,"115m listener remains subscribed after maintenance");
        Select(nodes[7]); Check(nodes[7].Service.GetState(radio).Volume==0.65f,"149m listener remains subscribed after maintenance");
        Select(host); float original=host.Service.GetState(radio).Volume;
        Select(nodes[6]); nodes[6].Service.Command(radio,"volume","",0.1f); Pump(4);
        Select(host); Check(host.Service.GetState(radio).Volume==original,"extended listening radius does not permit remote control at 115m");
        objects.Objects[new ZDOID(2,100)].Position=new UnityEngine.Vector3(200,0,0);
        Select(nodes[1]); nodes[1].Service.Command(radio,"volume","",0.1f); Pump(4);
        Select(host); Check(host.Service.GetState(radio).Volume==original,"distant command rejected");
        objects.Objects[new ZDOID(2,100)].Position=new UnityEngine.Vector3();
        objects.Objects[radio].Prefab=123;
        Select(nodes[1]); nodes[1].Service.Command(radio,"volume","",0.2f); Pump(4);
        Select(host); Check(host.Service.GetState(radio).Volume==original,"wrong prefab command rejected");
        objects.Objects[radio].Prefab="vmp_skald_horn".GetStableHashCode();
        double before=host.Service.GetState(radio).Position(host.Net.GetTimeSeconds());
        host.Net.ClockShift=1000; Pump(1); Select(host);
        Check(Math.Abs(host.Service.GetState(radio).Position(host.Net.GetTimeSeconds())-before)<0.1,"sleep time jump preserves song position");
        Select(nodes[1]); nodes[1].Service.ResetSession(); Pump(4);
        Check(nodes[1].Rpc.Methods.Count==1,"reconnect reset does not duplicate RPC registration");
        foreach(Node node in nodes) { Select(node); node.Service.Dispose(); }
        nodes.Clear(); wire.Clear();
    }
}
