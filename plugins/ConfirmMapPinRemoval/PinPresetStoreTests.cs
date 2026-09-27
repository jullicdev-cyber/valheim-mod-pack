using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ValheimModPack.PinRemoval;
internal static class PinPresetStoreTests
{
    private static int checks;
    private static void Check(bool condition, string label) { checks++; if (!condition) throw new Exception(label); }
    private static void Reject(Action operation, string label) { bool rejected = false; try { operation(); } catch { rejected = true; } Check(rejected, label); }
    private static List<PinPreset> Defaults()
    { return new List<PinPreset> { new PinPreset { Id="default.portal", Name="Portal", LocalizationKey="$pin_portal", Icon=2, Builtin=true }, new PinPreset { Id="default.copper", Name="Copper", LocalizationKey="pin.copper", Icon=1, Builtin=true } }; }
    private static string FileFor(string root, long id) { return Path.Combine(root, id.ToString(System.Globalization.CultureInfo.InvariantCulture)+".json"); }
    private static PinPresetStore Corrupted(string root, long id, string json)
    { File.WriteAllText(FileFor(root,id),json, new UTF8Encoding(false)); return new PinPresetStore(root,id,Defaults()); }
    private static void Main()
    {
        string root=Path.Combine(Path.GetTempPath(),"vmp-pin-presets-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var seeds=Defaults(); var store=new PinPresetStore(root,10,seeds);
            Check(!store.ReadOnly && store.Presets.Count==2 && File.Exists(store.FilePath),"new character atomically seeds defaults");
            Check(Path.GetFileName(store.FilePath)=="10.json","decimal character file name");
            seeds[0].Name="Changed outside"; Check(store.Find("default.portal").Name=="Portal","caller seed objects detached");
            IList<PinPreset> snapshot=store.Presets; snapshot[0].Name="Mutated snapshot";Check(store.Find("default.portal").Name=="Portal","snapshot item copies detached");
            Reject(()=>snapshot.Clear(),"snapshot collection read-only");
            PinPreset found=store.Find("default.portal");found.Name="Mutated find";Check(store.Find("default.portal").Name=="Portal","find returns copy");
            PinPreset added=store.Add("  Лес\n\t и\u202e    руины  ",3);Guid id;
            Check(Guid.TryParseExact(added.Id,"N",out id)&&!added.Builtin&&added.LocalizationKey=="","custom stable GUID literal name");
            Check(added.Name=="Лес и руины","whitespace and bidirectional control sanitized");
            added.Name="Mutated result";Check(store.Find(added.Id).Name=="Лес и руины","add result detached");
            Check(File.Exists(store.FilePath+".bak"),"updates create preceding backup");
            Check(store.Update("default.portal",null,4,false)&&store.Find("default.portal").Name=="Portal"&&store.Find("default.portal").LocalizationKey=="$pin_portal","icon-only edit preserves localization");
            Check(store.Update("default.portal","Portal",5)&&store.Find("default.portal").LocalizationKey=="","explicit rename clears localization even same fallback");
            Check(store.Find("default.portal").Builtin&&store.Find("default.portal").Id=="default.portal","edited built-in retains stable identity");
            Check(store.Delete("default.copper"),"built-in deletion allowed");
            var defaultsAfterUpdate=Defaults();defaultsAfterUpdate.Add(new PinPreset {Id="default.new",Name="New seed",LocalizationKey="",Icon=1,Builtin=true});
            var reloaded=new PinPresetStore(root,10,defaultsAfterUpdate);
            Check(reloaded.Find("default.copper")==null&&reloaded.Find("default.new")==null,"restart/update never reseeds deleted or new defaults");
            Check(reloaded.Find("default.portal").Icon==5&&reloaded.Find(added.Id).Name=="Лес и руины","custom and edited defaults survive restart");
            string before=File.ReadAllText(reloaded.FilePath);Check(!reloaded.Update("missing","x",1)&&!reloaded.Delete("missing"),"unknown IDs are no-op");
            Check(File.ReadAllText(reloaded.FilePath)==before,"unknown IDs do not rewrite disk");
            Reject(()=>reloaded.Add(" \n \t",1),"empty custom name rejected");
            Reject(()=>reloaded.Add("Invalid icon",-1),"negative icon rejected");
            Reject(()=>reloaded.Update("default.portal"," ",1),"empty edit rejected");
            Check(File.ReadAllText(reloaded.FilePath)==before&&reloaded.Find("default.portal").Name=="Portal","invalid edit leaves disk and memory unchanged");
            var strict=new PinPresetStore(root,11,Defaults(),icon=>icon==1||icon==2);
            Reject(()=>strict.Add("Not native",3),"caller native icon validator enforced");
            var tooLong=strict.Add(new string('я',200),1);Check(tooLong.Name.Length==96,"name length bounded to 96 UTF-16 units");
            Check(PinPresetStore.SafeName(new string('a',95)+"\ud83d\udd25").Length==95,"emoji not cut into surrogate half");
            Check(PinPresetStore.SafeName("a\ud800b\udc00c")=="abc","isolated surrogate code units removed");
            Check(PinPresetStore.SafeName(new string('a',94)+"\ud83d\udd25").Length==96,"complete emoji fits at boundary");
            var empty=new PinPresetStore(root,12,Defaults());empty.Delete("default.portal");empty.Delete("default.copper");
            Check(new PinPresetStore(root,12,Defaults()).Presets.Count==0,"intentionally empty store survives seed-once reload");
            var independent=new PinPresetStore(root,13,Defaults());Check(independent.Presets.Count==2,"other character independent defaults");
            var negative=new PinPresetStore(root,-14,Defaults());Check(!negative.ReadOnly&&Path.GetFileName(negative.FilePath)=="-14.json","signed nonzero character identities supported");
            Reject(()=>new PinPresetStore(root,0,Defaults()),"unknown character ID cannot get shared file");

            string valid=File.ReadAllText(independent.FilePath);
            var corrupt=Corrupted(root,20,"{broken");byte[] damaged=File.ReadAllBytes(corrupt.FilePath);
            Check(corrupt.ReadOnly&&corrupt.Presets.Count==0&&corrupt.LoadError.Length>0,"malformed JSON preserved read-only without fallback reseed");
            Reject(()=>corrupt.Add("new",1),"read-only add rejected");Reject(()=>corrupt.Delete("default.portal"),"read-only delete rejected");
            Check(Convert.ToBase64String(File.ReadAllBytes(corrupt.FilePath))==Convert.ToBase64String(damaged),"malformed bytes preserved exactly");
            Check(Corrupted(root,21,valid.Replace("\"CharacterId\":13","\"CharacterId\":21").Replace("\"Version\":1","\"Version\":2")).ReadOnly,"future schema preserved read-only");
            Check(Corrupted(root,22,valid).ReadOnly,"other character file rejected");
            Check(Corrupted(root,23,valid.Replace("\"CharacterId\":13","\"CharacterId\":23").Replace("default.copper","default.portal")).ReadOnly,"duplicate IDs rejected");
            Check(Corrupted(root,24,valid.Replace("\"CharacterId\":13","\"CharacterId\":24").Replace("default.copper","default../copper")).ReadOnly,"malformed built-in ID rejected");
            Check(Corrupted(root,25,valid.Replace("\"CharacterId\":13","\"CharacterId\":25")+"junk").ReadOnly,"trailing malformed JSON rejected");
            File.WriteAllBytes(FileFor(root,26),new byte[PinPresetStore.MaximumFileBytes+1]);
            Check(new PinPresetStore(root,26,Defaults()).ReadOnly,"oversized file rejected before allocation");
            var invalidSeeds=Defaults();invalidSeeds[0].Builtin=false;var badSeeds=new PinPresetStore(root,27,invalidSeeds);
            Check(badSeeds.ReadOnly&&!File.Exists(badSeeds.FilePath),"invalid default identity never persisted");

            var capacity=new PinPresetStore(root,30,new PinPreset[0]);
            for(int i=0;i<PinPresetStore.MaximumPresets;i++)capacity.Add("Метка "+i,1);
            Reject(()=>capacity.Add("Excess",1),"129th preset rejected");
            Check(capacity.Presets.Count==128&&new PinPresetStore(root,30,Defaults()).Presets.Count==128,"128-entry bound survives serialization and reload");
            var writer1=new PinPresetStore(root,40,Defaults());var writer2=new PinPresetStore(root,40,Defaults());
            writer1.Add("First window",1);string disk=File.ReadAllText(writer1.FilePath);
            Reject(()=>writer2.Add("Stale window",1),"stale writer cannot overwrite newer edits");
            Check(writer2.ReadOnly&&File.ReadAllText(writer1.FilePath)==disk,"conflict preserves newer disk file");
            var failing=new PinPresetStore(root,50,Defaults());string original=File.ReadAllText(failing.FilePath);
            Directory.CreateDirectory(failing.FilePath+".bak");
            Reject(()=>failing.Add("Unsaved",1),"failed backup/replace aborts mutation");
            Check(failing.Presets.Count==2&&File.ReadAllText(failing.FilePath)==original,"failed replace preserves memory and source file");
            Check(Directory.GetFiles(root,"*.tmp").Length==0,"temporary files cleaned after failed writes");
            System.Console.WriteLine("OK: "+checks+" pin preset persistence, seed-once, Unicode, validation, conflict and rollback assertions.");
        }
        finally
        {
            string path=Path.GetFullPath(root), prefix=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(path).StartsWith("vmp-pin-presets-",StringComparison.Ordinal))Directory.Delete(path,true);
        }
    }
}
