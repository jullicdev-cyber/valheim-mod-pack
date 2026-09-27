// Test-only plugin; never included in the distributed DLL.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;
using ValheimModPack.WorldCharacters;

namespace ValheimModPack.WorldCharactersProbe
{
    [BepInPlugin("valheimmodpack.worldcharacters.probe", "World Characters native tests", "1.0.0")]
    [BepInDependency(Plugin.Id, Plugin.Version)]
    [BepInDependency("com.jotunn.jotunn")]
    public sealed class Probe : BaseUnityPlugin
    {
        private int checks;
        private float start;
        private bool done;
        private static string Root { get { return Environment.GetEnvironmentVariable("VMP_WORLDCHARACTERS_PROBE"); } }
        private void Awake()
        {
            if (String.IsNullOrEmpty(Root)) { enabled = false; return; }
            start = Time.realtimeSinceStartup;
            Utils.SetSaveDataPath(Path.Combine(Root,"Saves"));
            PrefabManager.OnVanillaPrefabsAvailable += Available;
        }
        private void Available() { PrefabManager.OnVanillaPrefabsAvailable -= Available; StartCoroutine(Run()); }
        private IEnumerator Run()
        {
            while (!ObjectDB.instance || !ObjectDB.instance.GetItemPrefab("Wood")) yield return null;
            yield return null;
            try
            {
                Check(Path.GetFullPath(Paths.BepInExRootPath).StartsWith(Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase), "isolated BepInEx");
                Check(Player.m_localPlayer == null, "no live character involved");
                foreach (var entry in new[] { Tuple.Create(typeof(ZNet),"OnNewConnection"), Tuple.Create(typeof(ZNet),"RPC_PeerInfo"),
                    Tuple.Create(typeof(ZNet),"SendPeerInfo"), Tuple.Create(typeof(Game),"RequestRespawn"), Tuple.Create(typeof(PlayerProfile),"LoadPlayerData"),
                    Tuple.Create(typeof(PlayerProfile),"SavePlayerToDisk"), Tuple.Create(typeof(Player),"OnSpawned"), Tuple.Create(typeof(Player),"Load"),
                    Tuple.Create(typeof(Game),"SavePlayerProfile"), Tuple.Create(typeof(Game),"Logout"), Tuple.Create(typeof(Player),"TakeInput"),
                    Tuple.Create(typeof(ZNet),"SaveWorldThread"), Tuple.Create(typeof(SaveSystem),"EndSave") })
                {
                    var patches = Harmony.GetPatchInfo(AccessTools.Method(entry.Item1,entry.Item2));
                    Check(patches != null && patches.Owners.Contains(Plugin.Id), "native Harmony hook " + entry.Item1.Name + "." + entry.Item2);
                }
                var assembly = typeof(Plugin).Assembly;
                var gameState = assembly.GetType("ValheimModPack.WorldCharacters.GameState", true);
                string fingerprint = (string)gameState.GetMethod("BuildFingerprint").Invoke(null,null);
                Check(fingerprint.Length == 64, "full pack fingerprint computed");
                var source = new Inventory("WC source", null, 8, 10);
                source.GetAllItems().Add(Item("Wood",40,0,0));
                source.GetAllItems().Add(Item("HelmetLeather",1,0,6));
                source.GetAllItems()[1].m_customData["eaqs_slot"] = "head";
                source.GetAllItems()[1].m_customData["eaqs_player"] = "44";
                source.GetAllItems()[1].m_customData["wc_epic_fixture"] = "enchanted payload \u2603";
                var bagPrefab = ObjectDB.instance.m_items.FirstOrDefault(p => p && p.name.StartsWith("Backpack") && p.GetComponent<ItemDrop>());
                Check(bagPrefab != null, "Adventure Backpacks actual prefab available");
                var bag = Item(bagPrefab.name,1,1,6);
                var nested = new Inventory("WC nested",null,4,4); nested.GetAllItems().Add(Item("Iron",12,0,0));
                object component = Backpack(bag);
                component.GetType().GetMethod("SetInventory").Invoke(component,new object[]{nested});
                component.GetType().GetMethod("Serialize").Invoke(component,null);
                string backpackKey = bag.m_customData.Keys.Single(k => k.Contains("AdventureBackpacks.Components.BackpackComponent"));
                string backpackValue = bag.m_customData[backpackKey]; source.GetAllItems().Add(bag);
                var saved = new ZPackage(); source.Save(saved);
                var restored = new Inventory("WC restored",null,8,10); restored.Load(new ZPackage(saved.GetArray()));
                var roundTrip = new ZPackage(); restored.Save(roundTrip);
                File.WriteAllBytes(Path.Combine(Root,"inventory-before.bin"),saved.GetArray());
                File.WriteAllBytes(Path.Combine(Root,"inventory-after.bin"),roundTrip.GetArray());
                using (var before = new BinaryReader(new MemoryStream(saved.GetArray())))
                using (var after = new BinaryReader(new MemoryStream(roundTrip.GetArray())))
                    NativeInventory.RequireSameItems(NativeInventory.ReadInventory(before), NativeInventory.ReadInventory(after));
                Check(restored.GetAllItems().Count == 3 && restored.GetAllItems().Any(i => i.m_gridPos.y == 6), "native hidden-row inventory round trip with all pack patches");
                var restoredBag = restored.GetAllItems().Single(i => i.m_dropPrefab.name == bagPrefab.name);
                Check(restoredBag.m_customData[backpackKey] == backpackValue, "actual Adventure Backpacks inventory bytes retained");
                object restoredComponent = Backpack(restoredBag);
                Inventory restoredContents = (Inventory)restoredComponent.GetType().GetMethod("GetInventory").Invoke(restoredComponent,null);
                Check(restoredContents.GetAllItems().Any(i => i.m_dropPrefab.name == "Iron" && i.m_stack == 12), "actual BackpackComponent deserializes its iron stack");
                // Exercise PlayerProfile migration through the real native profile and WorldPlayerData types.
                var profile = new PlayerProfile("WorldCharactersProbe", FileHelpers.FileSource.Local); profile.SetName("Миграция");
                AccessTools.Field(typeof(PlayerProfile),"m_playerID").SetValue(profile,44L);
                byte[] payload;
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(33); for(int i=0;i<4;++i) writer.Write(25f); writer.Write("GP_Eikthyr"); writer.Write(0f);
                    writer.Write(saved.GetArray()); writer.Write(0); writer.Flush(); payload = stream.ToArray();
                }
                AccessTools.Field(typeof(PlayerProfile),"m_playerData").SetValue(profile,payload);
                var state = (CharacterState)gameState.GetMethod("FromProfile").Invoke(null,new object[]{profile,123L,"host",fingerprint,false});
                var encoded = StateCodec.Encode(state); state = StateCodec.Decode(encoded);
                Check(state.WorldData.Length >= 59 && state.Player.SequenceEqual(payload), "native existing-profile migration preserves bytes");
                gameState.GetMethod("Apply").Invoke(null,new object[]{profile,state});
                Check(((byte[])AccessTools.Field(typeof(PlayerProfile),"m_playerData").GetValue(profile)).SequenceEqual(payload), "native profile restore preserves player payload");
                Finish("PASS: " + checks + " native assertions. Full pack loaded; Harmony hooks, inventory/custom metadata and profile migration verified. Multiplayer sessions are not covered by this probe.",0);
            }
            catch(Exception e) { Finish("FAIL after " + checks + " native checks: " + e,2); }
        }
        private static ItemDrop.ItemData Item(string prefab,int count,int x,int y)
        {
            GameObject go=ObjectDB.instance.GetItemPrefab(prefab); var item=go.GetComponent<ItemDrop>().m_itemData.Clone();
            item.m_dropPrefab=go; item.m_stack=count; item.m_gridPos=new Vector2i(x,y); return item;
        }
        private static object Backpack(ItemDrop.ItemData item)
        {
            Assembly a = Chainloader.PluginInfos["vapok.mods.adventurebackpacks"].Instance.GetType().Assembly;
            var extensions = a.GetType("Vapok.Common.Managers.ItemExtensions",true);
            object info = extensions.GetMethod("Data",new[]{typeof(ItemDrop.ItemData)}).Invoke(null,new object[]{item});
            return info.GetType().GetMethod("GetOrCreate").MakeGenericMethod(a.GetType("AdventureBackpacks.Components.BackpackComponent",true)).Invoke(info,new object[]{""});
        }
        private void Check(bool value,string reason) { if(!value) throw new Exception(reason); ++checks; }
        private void Finish(string result,int code) { if(done) return; done=true; File.WriteAllText(Path.Combine(Root,"result.txt"),result); Logger.LogInfo(result); Application.Quit(code); }
        private void Update() { if(!done && Time.realtimeSinceStartup-start>100) Finish("FAIL native probe timeout",2); }
    }
}
