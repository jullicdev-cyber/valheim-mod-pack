using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ValheimModPack.NordicRadio;

internal static partial class NetworkTests
{
    private static double transferDelay;
    private static int chunkPackets, requestPackets, maximumQueued, gameplayMusicPackets;
    private static bool disorder;
    private static readonly HashSet<long> musicRecipients=new HashSet<long>();
    private static readonly Queue<Delivery> acknowledgements=new Queue<Delivery>();
    private static void QueuePacket(long sender,long target,string name,ZPackage package)
    {
        RadioMessage message=RadioProtocol.Decode(package.GetArray());
        double delay=transferDelay;
        if(message.Kind==RadioMessageKind.ChunkRequest) requestPackets++;
        Node origin=nodes.Find(n=>n.Net.Uid==sender);
        TestSocket socket=name=="bulk"?origin.Bulk.Queue(target):origin.Net.GetPeer(target).m_socket;
        socket.Pending+=package.Size()+(name=="bulk"?RadioBulkFrame.HeaderSize:0);
        if(name=="bulk") maximumQueued=Math.Max(maximumQueued,socket.Pending);
        if(message.Kind==RadioMessageKind.Chunk)
        {
            if(name!="bulk")gameplayMusicPackets++;
            chunkPackets++;
            musicRecipients.Add(target);
            if(disorder && message.Offset==0) delay+=0.15;
            if(disorder && message.Offset==RadioProtocol.ChunkSize)
                wire.Enqueue(new Delivery{Sender=sender,Target=target,Name=name,Package=package,Due=UnityEngine.Time.realtimeSinceStartup+delay+0.1});
        }
        wire.Enqueue(new Delivery{Sender=sender,Target=target,Name=name,Package=package,Socket=socket,Due=UnityEngine.Time.realtimeSinceStartup+delay});
    }
    private static void Transfers(string root)
    {
        double serial=TransferRun(Path.Combine(root,"serial"),1,false,false,false);
        double pipelined=TransferRun(Path.Combine(root,"pipeline"),4,false,true,false);
        double wide=TransferRun(Path.Combine(root,"wide"),8,false,false,false);
        Check(pipelined < serial*0.65,"windowed transfer improves high-latency download time");
        Check(wide < pipelined*0.8,"eight-block window reduces round-trip waiting further");
        Console.WriteLine("Simulated 300 ms RTT, 3.6 MiB: window1="+serial.ToString("F2")+"s; window4="+pipelined.ToString("F2")+"s; window8="+wide.ToString("F2")+"s (not a real Steam bandwidth measurement).");
        TransferRun(Path.Combine(root,"local"),4,true,false,false);
        TransferRun(Path.Combine(root,"congestion"),4,false,false,true);
        TransferRun(Path.Combine(root,"blocked-channel"),8,false,false,false,true);
        WriterFailures(Path.Combine(root,"writer"));
    }
    private static double TransferRun(string root,int window,bool local,bool reordered,bool congestion,bool blockedChannel=false)
    {
        nodes.Clear(); wire.Clear(); acknowledgements.Clear(); transferDelay=0.15;
        chunkPackets=requestPackets=maximumQueued=gameplayMusicPackets=0; disorder=reordered;
        var objects=new ZDOMan(); var radio=new ZDOID(1,1);
        objects.Objects[radio]=new ZDO{Prefab="vmp_skald_horn".GetStableHashCode()};
        for(int i=0;i<2;i++)
        {
            long uid=i+1; var node=new Node{Net=new ZNet{Uid=uid,Host=i==0},Rpc=new ZRoutedRpc(),Player=new Player(),Objects=objects};
            var cid=new ZDOID(uid,100); objects.Objects[cid]=node.Player.Character=new ZDO{m_uid=cid};
            node.Rpc.Transport=(peer,name,package)=>QueuePacket(uid,peer,name,package);
            string data=Path.Combine(root,"node"+uid); Directory.CreateDirectory(Path.Combine(data,"Music"));
            if(i==0) WriteMp3(Path.Combine(data,"Music","song.mp3"),8000,true);
            else if(local) File.Copy(Path.Combine(root,"node1","Music","song.mp3"),Path.Combine(data,"Music","another name.mp3"));
            Select(node); node.Bulk=new TestBulk(node); node.Service=new RadioService(null,data,Console.WriteLine,node.Bulk); node.Service.DownloadWindow=window; nodes.Add(node);
        }
        Node host=nodes[0],client=nodes[1];
        host.Net.Peers.Add(new ZNetPeer{m_uid=2,m_characterID=new ZDOID(2,100)});
        client.Net.Peers.Add(new ZNetPeer{m_uid=1,m_characterID=new ZDOID(1,100)});
        Pump(160); Select(host); Check(host.Service.Tracks.Count==1,"transfer fixture scanned");
        string id=host.Service.Tracks[0].Id;
        Select(client); client.Service.Watch(radio); Pump(24);
        if(congestion) host.Net.Peers[0].m_socket.Pending=256*1024;
        if(blockedChannel) client.Bulk.Available=false;
        double start=UnityEngine.Time.realtimeSinceStartup;
        int blockedBefore=worldBlockedTicks;
        for(int tick=0;tick<5000;tick++)
        {
            if(congestion && tick==0) client.Service.DownloadEnabled=false;
            if(congestion && tick==40)
            { Check(requestPackets==0,"muted listener does not request audio downloads");client.Service.DownloadEnabled=true; }
            Select(client); client.Service.Watch(radio); client.Service.RequestTrack(id);
            if(congestion && tick==120)
            {
                Check(chunkPackets==0,"no music added to congested gameplay socket");
                Select(host); host.Service.Command(radio,"play","",0); Pump(20);
                Select(client); Check(client.Service.GetState(radio).Playing,"control messages continue while file transfer pauses");
                host.Net.Peers[0].m_socket.Pending=0;
            }
            if(blockedChannel && tick==80)
            {
                Check(requestPackets==0,"rejected bulk requests do not silently become outstanding requests");
                client.Bulk.Available=true;host.Bulk.Available=false;
            }
            if(blockedChannel && tick==160)
            {
                Check(requestPackets>0 && chunkPackets==0,"blocked upload retains ready data for retry");
                host.Bulk.Available=true;
            }
            Pump(1); Select(client);
            if(client.Service.GetTrackPath(id)!=null) break;
        }
        double elapsed=UnityEngine.Time.realtimeSinceStartup-start;
        Select(client); string path=client.Service.GetTrackPath(id);
        Check(path!=null && RadioLibrary.HashFile(path)==id,"completed exact verified bytes with window "+window);
        if(!congestion) Check(worldBlockedTicks==blockedBefore,"music never exhausts the native world-update queue budget");
        Check(gameplayMusicPackets==0,"MP3 bytes only use the separate music connection");
        if(local)
        {
            Check(chunkPackets==0 && requestPackets==0,"pre-shared MP3 requires no music transfer");
            Check(Path.GetFileName(path)=="another name.mp3","local music matched by bytes, not title");
            Check(Directory.GetFiles(Path.Combine(root,"node2","Cache"),"*.mp3").Length==0,"local MP3 not duplicated in cache");
        }
        else Check(maximumQueued <= host.Service.MaxQueuedKiB*1024,"separate music queue limit including unacknowledged bytes respected");
        int previous=chunkPackets;
        for(int tick=0;tick<100;tick++){Select(client);client.Service.Watch(radio);client.Service.RequestTrack(id);Pump(1);}
        Check(chunkPackets==previous,"completed cached song is not downloaded again");
        foreach(Node node in nodes){Select(node);node.Service.Dispose();}
        Pump(4); nodes.Clear();wire.Clear();acknowledgements.Clear();transferDelay=0;disorder=false;
        return elapsed;
    }
    private static void WriterFailures(string root)
    {
        var library=new RadioLibrary(root); string mp3=Path.Combine(library.MusicDirectory,"local.mp3");WriteMp3(mp3,100,true);
        TrackInfo track=library.Scan(delegate{})[0].Track;
        byte[] data=File.ReadAllBytes(mp3);
        var blocks=new List<byte[]>();
        for(int offset=0;offset<data.Length;offset+=RadioProtocol.ChunkSize){int n=Math.Min(RadioProtocol.ChunkSize,data.Length-offset);var b=new byte[n];Buffer.BlockCopy(data,offset,b,0,n);blocks.Add(b);}
        using(var done=new ManualResetEvent(false))
        {
            bool finished=false;Exception error=null;int caller=Thread.CurrentThread.ManagedThreadId,worker=caller;
            var writer=new RadioDownloadWriter(library,track,new HashSet<string>());
            writer.Write(0,blocks.ToArray(),(offset,complete,failure)=>{worker=Thread.CurrentThread.ManagedThreadId;finished=complete;error=failure;done.Set();});
            Check(done.WaitOne(10000) && finished && error==null,"background writer verifies and commits file");
            Check(worker!=caller,"disk and checksum completion runs off the calling game thread");
            writer.Dispose();
            Check(library.ValidateCache(track)!=null,"writer produces verified cache");
        }
        var wrong=new TrackInfo{Id=A,Size=track.Size,Title="wrong"};
        using(var done=new ManualResetEvent(false))
        {
            Exception error=null;var writer=new RadioDownloadWriter(library,wrong,new HashSet<string>());
            writer.Write(0,blocks.ToArray(),(offset,complete,failure)=>{error=failure;done.Set();});
            Check(done.WaitOne(10000) && error is InvalidDataException,"corrupt download rejected before publication");writer.Dispose();
            Check(!File.Exists(library.CachePath(A)),"bad checksum never exposed to decoder");
        }
        var cancelledWriter=new RadioDownloadWriter(library,track,new HashSet<string>());cancelledWriter.Dispose();
        bool rejected=false;try{cancelledWriter.Write(0,blocks.ToArray(),delegate{});}catch(InvalidOperationException){rejected=true;}
        Check(rejected,"cancelled writer cannot accept more data");
        using(var done=new ManualResetEvent(false))
        {
            var writer=new RadioDownloadWriter(library,track,new HashSet<string>());
            writer.Write(0,new[]{blocks[0]},(offset,complete,failure)=>done.Set());
            Check(done.WaitOne(10000),"partial write completed before cancellation");writer.Dispose();
            for(int i=0;i<100 && Directory.GetFiles(library.CacheDirectory,"*.part").Length>0;i++)Thread.Sleep(10);
            Check(Directory.GetFiles(library.CacheDirectory,"*.part").Length==0,"cancel removes partial file asynchronously");
        }
        Check(library.FindLocalTrack(track,()=>true)==null,"local lookup honours cancellation");
        var mismatch=new TrackInfo{Id=B,Size=track.Size,Title="local"};
        Check(library.FindLocalTrack(mismatch,()=>false)==null,"same-size local song with different bytes is not accepted");
    }
}
