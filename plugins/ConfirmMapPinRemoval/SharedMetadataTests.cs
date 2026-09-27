using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using HarmonyLib;
using ValheimModPack.PinRemoval;
public static class SharedMetadataTests
{
    private static int checks;
    private static void Check(bool value,string label) { checks++; if(!value) throw new Exception(label); }
    private static void Reject(Action action,string label) { bool failed=false;try{action();}catch{failed=true;}Check(failed,label); }
    private static object Hook(string name,params object[] args) { return typeof(SharedPinMetadata).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,args); }
    private static PinRecord Record(long owner,string author,float x,long ticks) { return new PinRecord { Owner=owner,Author=author,X=x,Y=2,Z=3,Type=1,CreatedUtc=ticks,CreatorName="Скальд" }; }
    private static byte[] Compress(byte[] bytes)
    {
        using(var output=new MemoryStream())
        { using(var gzip=new GZipStream(output,CompressionMode.Compress,true))gzip.Write(bytes,0,bytes.Length);return output.ToArray(); }
    }
    private static byte[] Map(params PinRecord[] pins)
    {
        using(var memory=new MemoryStream())using(var writer=new BinaryWriter(memory,Encoding.UTF8))
        {
            writer.Write(3);writer.Write(4);writer.Write(new byte[]{1,0,1,0});writer.Write(pins.Length);
            foreach(var pin in pins){writer.Write(pin.Owner);writer.Write(pin.Name);writer.Write(pin.X);writer.Write(pin.Y);writer.Write(pin.Z);writer.Write(pin.Type);writer.Write(pin.Checked);writer.Write(pin.Author);}
            writer.Flush();return Compress(memory.ToArray());
        }
    }
    private static byte[] Metadata(byte[] map,IEnumerable<PinRecord> pins,long world=100,bool legacy=false)
    { return SharedPinCodec.Encode(new SharedPinCodec.Payload{World=world,MapHash=SharedPinCodec.Hash(map),Records=new List<PinRecord>(pins)},legacy); }
    private static void NativeWrite(MapTable table,byte[] data,long sender)
    {
        object[] before={table,null};Hook("BeforeMapReceived",before);
        table.View.Data.Set("data",data);Hook("MapReceived",table,sender,new ZPackage(data),before[1]);
    }
    private static MapTable Table(byte[] data,long sender=5)
    {
        var table=new MapTable();Hook("Register",table);NativeWrite(table,data,sender);return table;
    }
    private static void Send(MapTable table,long sender,byte[] metadata,bool legacy=false) { table.View.Rpcs[legacy?"VMP_PinMetadata_1":"VMP_PinMetadata_2"](sender,new ZPackage(metadata)); }
    private static SharedPinCodec.Payload Stored(MapTable table) {return SharedPinCodec.Decode(table.View.Data.GetByteArray("vmp_pin_metadata_2",null));}
    public static void Main()
    {
        long time=DateTime.UtcNow.Ticks;var known=Record(777,"Steam_777",4,time);known.Name="Общая";
        byte[] native=Map(known),metadata=Metadata(native,new[]{known});
        var roundtrip=SharedPinCodec.Decode(metadata);Check(roundtrip.Records.Count==1&&roundtrip.Records[0].CreatedUtc==time,"sidecar UTC roundtrip");
        Check(roundtrip.Version==2,"new sidecar writes format2");
        var bound=known.Copy();bound.PresetKey="default.copper";bound.BoundName=bound.Name;
        var boundRoundtrip=SharedPinCodec.Decode(Metadata(native,new[]{bound}));
        Check(boundRoundtrip.Records[0].PresetKey=="default.copper"&&boundRoundtrip.Records[0].BoundName==bound.Name,"format2 preserves built-in binding and exact native baseline");
        var oldRoundtrip=SharedPinCodec.Decode(Metadata(native,new[]{bound},100,true));
        Check(oldRoundtrip.Version==1&&oldRoundtrip.Records[0].PresetKey==""&&oldRoundtrip.Records[0].CreatedUtc==time,"legacy writer preserves dates and omits labels");
        var customBinding=bound.Copy();customBinding.PresetKey=Guid.NewGuid().ToString("N");
        Check(SharedPinCodec.Decode(Metadata(native,new[]{customBinding})).Records[0].PresetKey=="","private custom preset GUID never sent to another character");
        var literal=bound.Copy();literal.PresetKey="";
        Check(SharedPinCodec.Decode(Metadata(native,new[]{literal})).Records[0].BoundName=="","explicit local literal-name marker never exported");
        var tooLongBound=bound.Copy();tooLongBound.BoundName=new string('x',257);Reject(()=>Metadata(native,new[]{tooLongBound}),"oversized binding baseline rejected");
        Check(roundtrip.Records[0].Key==known.Key&&roundtrip.Records[0].CreatorName=="Скальд","sidecar exact identity and creator");
        Check(SharedPinCodec.ReadVanillaKeys(native).Contains(known.Key),"native shared-map v3 bounded parser");
        Reject(()=>SharedPinCodec.Decode(new byte[SharedPinCodec.MaximumBytes+1]),"oversized sidecar rejected");
        var truncated=new byte[metadata.Length-1];Array.Copy(metadata,truncated,truncated.Length);Reject(()=>SharedPinCodec.Decode(truncated),"truncated sidecar rejected");
        byte[] trailing=new byte[metadata.Length+1];Array.Copy(metadata,trailing,metadata.Length);Reject(()=>SharedPinCodec.Decode(trailing),"trailing sidecar rejected");
        byte[] badVersion=(byte[])metadata.Clone();badVersion[4]=3;Reject(()=>SharedPinCodec.Decode(badVersion),"unknown sidecar version rejected");
        Reject(()=>Metadata(native,new[]{known,known}),"duplicate identity rejected");
        var unknown=known.Copy();unknown.CreatedUtc=0;Reject(()=>Metadata(native,new[]{unknown}),"unknown dates cannot be advertised");
        var invalid=known.Copy();invalid.X=Single.NaN;Reject(()=>Metadata(native,new[]{invalid}),"nonfinite position rejected");
        invalid=known.Copy();invalid.Author="";Reject(()=>Metadata(native,new[]{invalid}),"missing author rejected");
        var missing=known.Copy();missing.X=100;var replacement=known.Copy();replacement.CreatedUtc=time+10;
        var merged=SharedPinCodec.Merge(new[]{known},new[]{replacement,missing},SharedPinCodec.ReadVanillaKeys(native));
        Check(merged.Count==1&&merged[0].CreatedUtc==time+10,"newer recreated pin origin wins and absent native pins filtered");
        Check(SharedPinCodec.Merge(new[]{replacement},new[]{known},SharedPinCodec.ReadVanillaKeys(native))[0].CreatedUtc==time+10,"stale relay cannot roll back recreated pin date");
        var many=new List<PinRecord>();var present=new HashSet<string>();for(int i=0;i<600;i++){var record=Record(777,"Steam_777",i,time+i);many.Add(record);present.Add(record.Key);}
        Check(SharedPinCodec.Merge(new PinRecord[0],many,present).Count==512,"merge bounded to newest 512 records");
        Reject(()=>SharedPinCodec.ReadVanillaKeys(Compress(new byte[]{2,0,0,0})),"unknown vanilla format rejected without changing map");
        Reject(()=>SharedPinCodec.ReadVanillaKeys(Compress(new byte[21*1024*1024])),"decompression size limit");

        string folder=Path.Combine(Path.GetTempPath(),"vmp-pin-shared-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var errors=new List<Exception>();BepInEx.Paths.GameRootPath=folder;
        var player=new Player();Player.m_localPlayer=player;ZNet.instance=new ZNet();Minimap.instance=new Minimap();
        PinHistoryController controller=null;
        try
        {
            controller=new PinHistoryController(new Harmony(),AccessTools.Field(typeof(Minimap),"m_pins"),errors.Add);
            controller.OpenShortcut = () => UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.H) && UnityEngine.Input.GetKey(UnityEngine.KeyCode.LeftControl);
            controller.Tick(true);
            var table=Table(native);Send(table,5,metadata);Check(Stored(table).Records.Count==1,"native write grants one matching extension");
            var labels=Table(native);Send(labels,5,Metadata(native,new[]{bound}));
            Check(Stored(labels).Records[0].PresetKey=="default.copper","owner accepts exact native-name binding");
            var legacyStored=SharedPinCodec.Decode(labels.View.Data.GetByteArray("vmp_pin_metadata_1",null));
            Check(legacyStored.Version==1&&legacyStored.Records[0].PresetKey==""&&legacyStored.Records[0].CreatedUtc==time,"new owner writes readable legacy dates alongside labels");
            NativeWrite(labels,native,6);Send(labels,6,Metadata(native,new[]{known},100,true),true);
            Check(Stored(labels).Records[0].PresetKey=="default.copper","legacy RPC1 cannot erase known equal-date binding");
            var wrongName=bound.Copy();wrongName.BoundName="Different visible label";
            var invalidLabel=Table(native);Send(invalidLabel,5,Metadata(native,new[]{wrongName}));
            Check(Stored(invalidLabel).Records[0].PresetKey=="","owner rejects binding that does not match actual vanilla name");
            var duplicateName=known.Copy();duplicateName.Name="Other label";byte[] ambiguousMap=Map(known,duplicateName);
            var ambiguous=Table(ambiguousMap);Send(ambiguous,5,Metadata(ambiguousMap,new[]{bound}));
            Check(Stored(ambiguous).Records[0].PresetKey=="","ambiguous identical native pin identities never gain a binding");
            var renamedNative=known.Copy();renamedNative.Name="Manually renamed";byte[] renamedMap=Map(renamedNative);
            labels.View.Data.Set("data",renamedMap);labels.View.Data.Set("vmp_pin_metadata_1",Metadata(renamedMap,new[]{known},100,true));
            var selected=(SharedPinCodec.Payload)Hook("Read",labels.View);
            Check(selected.Version==1&&selected.Records[0].PresetKey=="","stale v2 hash after old owner update falls back to current legacy");
            NativeWrite(labels,renamedMap,8);
            Check(Stored(labels).Records[0].PresetKey=="","new owner does not resurrect stale labels from old hash");
            var sameHash=Table(native);Send(sameHash,5,Metadata(native,new[]{bound}));
            var changedOrigin=known.Copy();changedOrigin.CreatedUtc=time+100;
            sameHash.View.Data.Set("vmp_pin_metadata_1",Metadata(native,new[]{changedOrigin},100,true));
            selected=(SharedPinCodec.Payload)Hook("Read",sameHash.View);
            Check(selected.Version==1&&selected.Records[0].CreatedUtc==time+100&&selected.Records[0].PresetKey=="","same-hash legacy origin update still invalidates stale v2 binding");
            Send(table,5,Metadata(native,new[]{replacement}));Check(Stored(table).Records[0].CreatedUtc==time,"second extension cannot replace origin");
            var spoof=Table(native);Send(spoof,99,metadata);Check(Stored(spoof).Records.Count==0,"other sender cannot attach metadata");
            var changed=Table(native);Send(changed,5,Metadata(Map(missing),new[]{known}));Check(Stored(changed).Records.Count==0,"mismatched native content hash rejected");
            var world=Table(native);Send(world,5,Metadata(native,new[]{known},999));Check(Stored(world).Records.Count==0,"cross-world metadata rejected");
            var late=Table(native);UnityEngine.Time.realtimeSinceStartup=16;Send(late,5,metadata);Check(Stored(late).Records.Count==0,"expired write receipt rejected");UnityEngine.Time.realtimeSinceStartup=0;
            var remote=Table(native);remote.View.Owner=false;Send(remote,5,metadata);Check(Stored(remote).Records.Count==0,"nonowner never writes ZDO sidecar");
            var omitted=Table(native);Send(omitted,5,Metadata(native,new[]{known,missing}));Check(Stored(omitted).Records.Count==1,"metadata cannot introduce native-absent pin");
            var unrelated=new MapTable();Hook("Register",unrelated);unrelated.View.Data.Set("data",native);Send(unrelated,5,metadata);Check(unrelated.View.Data.GetByteArray("vmp_pin_metadata_1",null)==null,"no prior native write means no authority");
            var nextNative=Map(known,missing);NativeWrite(table,nextNative,6);Check(Stored(table).Records.Count==1,"subsequent writer preserves previously shared provenance");
            byte[] emptyNative=Map();NativeWrite(table,emptyNative,6);Check(Stored(table).Records.Count==0,"deleted native pin prunes sidecar entry");

            var live=Minimap.instance.AddPin(new UnityEngine.Vector3(4,2,3),Minimap.PinType.Icon1,"Общая",true,false,777,new Splatform.PlatformUserID("Steam_777"));
            var own=Minimap.instance.AddPin(new UnityEngine.Vector3(8,2,3),Minimap.PinType.Icon1,"Своя",true,false,0,Splatform.PlatformUserID.None);
            typeof(PinHistoryController).GetMethod("AfterCreated",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{Minimap.instance,own});
            var exported=PinHistoryController.ExportSharedMetadata();Check(exported.Count==1&&exported[0].Owner==200&&exported[0].Author=="Steam_200","local pin canonicalized exactly like vanilla export");
            long originalOwn=exported[0].CreatedUtc;
            var readTable=Table(native);Send(readTable,5,metadata);Hook("AfterRead",readTable,player,null);
            exported=PinHistoryController.ExportSharedMetadata();Check(exported.Count==2&&exported.Find(p=>p.Owner==777).CreatedUtc==time,"actual imported pin receives original creation time");
            var localArchive=(PinArchive)typeof(PinHistoryController).GetField("archive",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(controller);
            var recordOwn=exported.Find(p=>p.Owner==200);recordOwn.CreatedUtc=time-1000;PinHistoryController.ImportSharedMetadata(new[]{recordOwn});
            Check(PinHistoryController.ExportSharedMetadata().Find(p=>p.Owner==200).CreatedUtc==originalOwn,"local creation is never overwritten by table date");
            var forged=known.Copy();forged.X=99;PinHistoryController.ImportSharedMetadata(new[]{forged});
            var absent=forged.Copy();localArchive.Enrich(absent);Check(Minimap.instance.Added==2,"import only enriches existing pins, never creates map entries");
            var blocked=known.Copy();blocked.CreatedUtc=time+100;var privacy=Table(native);Send(privacy,5,Metadata(native,new[]{blocked}));PrivateArea.Allowed=false;
            Hook("AfterRead",privacy,player,null);PrivateArea.Allowed=true;Check(PinHistoryController.ExportSharedMetadata().Find(p=>p.Owner==777).CreatedUtc==time,"read honors ward permissions and retains original date");
            var beforeAdopt=typeof(PinHistoryController).GetMethod("BeforeAdoption",BindingFlags.NonPublic|BindingFlags.Static);
            object[] adoptionArgs={Minimap.instance,null};beforeAdopt.Invoke(null,adoptionArgs);live.m_ownerID=0;
            typeof(PinHistoryController).GetMethod("AfterAdoption",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new[]{(object)Minimap.instance,adoptionArgs[1]});
            var adopted=PinHistoryController.ExportSharedMetadata().Find(p=>p.Author=="Steam_777");
            Check(adopted!=null&&adopted.Owner==200&&adopted.CreatedUtc==time&&adopted.CreatorName=="Скальд","native shared-pin adoption preserves origin under new owner key");
            Check(errors.Count==0,"valid and rejected network paths complete without exceptions");

            string archiveFile=Path.Combine(folder,"batch.bin");var archive=new PinArchive(archiveFile,100,200);
            archive.ImportCreationMetadata(new[]{known});var knownRead=known.Copy();knownRead.CreatedUtc=0;archive.Enrich(knownRead);Check(knownRead.CreatedUtc==time,"batch metadata persisted");
            archive=new PinArchive(archiveFile,100,200);knownRead.CreatedUtc=0;archive.Enrich(knownRead);Check(knownRead.CreatedUtc==time,"shared date survives reload");
            archive.ImportCreationMetadata(new[]{replacement});knownRead.CreatedUtc=0;archive.Enrich(knownRead);Check(knownRead.CreatedUtc==time+10,"shared recreated pin updates to newer known date");
            archive.ImportCreationMetadata(new[]{known});knownRead.CreatedUtc=0;archive.Enrich(knownRead);Check(knownRead.CreatedUtc==time+10,"archive ignores stale relayed date");
            var invalidBatch=missing.Copy();invalidBatch.Type=-1;Reject(()=>archive.ImportCreationMetadata(new[]{missing,invalidBatch}),"invalid batch rejected atomically");
            var rollback=missing.Copy();rollback.CreatedUtc=0;archive.Enrich(rollback);Check(rollback.CreatedUtc==0,"failed batch leaves no in-memory partial metadata");
            var bindingArchive=new PinArchive(Path.Combine(folder,"binding-backfill.bin"),100,200);bindingArchive.ImportCreationMetadata(new[]{known});
            bindingArchive.ImportCreationMetadata(new[]{bound});Check(bindingArchive.PresetFor(known)=="default.copper","equal-date matching shared pin receives missing binding");
            bindingArchive.BindPreset(known,"");bindingArchive.ImportCreationMetadata(new[]{bound});
            Check(bindingArchive.PresetFor(known)=="","explicit rename to the same text blocks equal-date backfill");
            var manuallyRenamed=known.Copy();manuallyRenamed.Name="Custom literal";bindingArchive.BindPreset(manuallyRenamed,"");
            bindingArchive.ImportCreationMetadata(new[]{bound});Check(bindingArchive.PresetFor(manuallyRenamed)=="","different manual name is never rebound by incoming metadata");
            var literalSnapshot=bindingArchive.RecordBeforeDelete(manuallyRenamed,time+10);
            Check(literalSnapshot.PresetKey==""&&literalSnapshot.BoundName==manuallyRenamed.Name,"deletion and restoration snapshot preserves explicit literal marker");
            var sameLiteralLater=bound.Copy();sameLiteralLater.Name=manuallyRenamed.Name;sameLiteralLater.BoundName=manuallyRenamed.Name;sameLiteralLater.CreatedUtc=time+1000;
            bindingArchive.ImportCreationMetadata(new[]{sameLiteralLater});Check(bindingArchive.PresetFor(manuallyRenamed)=="","newer provenance cannot overwrite explicit local literal choice");
            System.Console.WriteLine("OK: "+checks+" shared metadata codec, native-map contract, owner RPC and archive/controller assertions.");
        }
        finally
        {
            if(controller!=null)controller.Dispose();
            string resolved=Path.GetFullPath(folder), parent=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(resolved.StartsWith(parent,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(resolved).StartsWith("vmp-pin-shared-",StringComparison.Ordinal))Directory.Delete(resolved,true);
        }
    }
}
