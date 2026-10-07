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
            if (Environment.GetEnvironmentVariable("VMP_WORLDCHARACTERS_GRAPHICS") == "1")
                while (GUIManager.CustomGUIFront == null || GUIManager.Instance.AveriaSerif == null) yield return null;
            yield return null;
            try
            {
                Check(Path.GetFullPath(Paths.BepInExRootPath).StartsWith(Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase), "isolated BepInEx");
                Check(Player.m_localPlayer == null, "no live character involved");
                foreach (string expected in File.ReadAllLines(Path.Combine(Root,"expected-plugins.txt")))
                {
                    string[] parts = expected.Split('\t');
                    Check(parts.Length == 2 && Chainloader.PluginInfos.ContainsKey(parts[0])
                        && Chainloader.PluginInfos[parts[0]].Metadata.Version.ToString() == parts[1], "exact copied pack plugin loaded: " + expected);
                }
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
                Check(assembly.GetName().Name == "WorldCharacters", "stable CLR assembly identity for dependent mods");
                foreach (var dependent in AppDomain.CurrentDomain.GetAssemblies())
                    foreach (var reference in dependent.GetReferencedAssemblies().Where(r => r.Name == "WorldCharacters"))
                        Check(ReferenceEquals(Assembly.Load(reference),assembly), "dependent CLR reference resolves to the same plugin: " + dependent.GetName().Name);
                var gameState = assembly.GetType("ValheimModPack.WorldCharacters.GameState", true);
                string fingerprint = (string)gameState.GetMethod("BuildFingerprint").Invoke(null,null);
                Check(fingerprint.Length == 64, "full pack fingerprint computed");
                var source = new Inventory("WC source", null, 8, 10);
                source.GetAllItems().Add(Item("Wood",40,0,0));
                source.GetAllItems().Add(Item("HelmetLeather",1,0,6));
                source.GetAllItems()[1].m_equipped = true;
                source.GetAllItems()[1].m_customData["eaqs_slot"] = "head";
                source.GetAllItems()[1].m_customData["eaqs_player"] = "44";
                source.GetAllItems()[1].m_customData["wc_epic_fixture"] = "enchanted payload \u2603";
                bool hasBackpacks = Chainloader.PluginInfos.ContainsKey("vapok.mods.adventurebackpacks");
                GameObject bagPrefab = null; string backpackKey = null, backpackValue = null;
                if (hasBackpacks)
                {
                    bagPrefab = ObjectDB.instance.m_items.FirstOrDefault(p => p && p.name.StartsWith("Backpack") && p.GetComponent<ItemDrop>());
                    Check(bagPrefab != null, "Adventure Backpacks actual prefab available");
                    var bag = Item(bagPrefab.name,1,1,6);
                    var nested = new Inventory("WC nested",null,4,4); nested.GetAllItems().Add(Item("Iron",12,0,0));
                    object component = Backpack(bag);
                    component.GetType().GetMethod("SetInventory").Invoke(component,new object[]{nested});
                    component.GetType().GetMethod("Serialize").Invoke(component,null);
                    backpackKey = bag.m_customData.Keys.Single(k => k.Contains("AdventureBackpacks.Components.BackpackComponent"));
                    backpackValue = bag.m_customData[backpackKey]; source.GetAllItems().Add(bag);
                }
                var saved = new ZPackage(); source.Save(saved);
                using (var reader = new BinaryReader(new MemoryStream(saved.GetArray())))
                {
                    var parsed = NativeInventory.ReadInventory(reader);
                    Check(parsed[1].Equipped && !parsed[0].Equipped, "actual native equipped flag is projected independently of slot metadata");
                }
                var restored = new Inventory("WC restored",null,8,10); restored.Load(new ZPackage(saved.GetArray()));
                var roundTrip = new ZPackage(); restored.Save(roundTrip);
                File.WriteAllBytes(Path.Combine(Root,"inventory-before.bin"),saved.GetArray());
                File.WriteAllBytes(Path.Combine(Root,"inventory-after.bin"),roundTrip.GetArray());
                using (var before = new BinaryReader(new MemoryStream(saved.GetArray())))
                using (var after = new BinaryReader(new MemoryStream(roundTrip.GetArray())))
                    NativeInventory.RequireSameItems(NativeInventory.ReadInventory(before), NativeInventory.ReadInventory(after));
                Check(restored.GetAllItems().Count == (hasBackpacks ? 3 : 2) && restored.GetAllItems().Any(i => i.m_gridPos.y == 6), "native hidden-row inventory round trip with all pack patches");
                if (hasBackpacks)
                {
                    var restoredBag = restored.GetAllItems().Single(i => i.m_dropPrefab.name == bagPrefab.name);
                    Check(restoredBag.m_customData[backpackKey] == backpackValue, "actual Adventure Backpacks inventory bytes retained");
                    object restoredComponent = Backpack(restoredBag);
                    Inventory restoredContents = (Inventory)restoredComponent.GetType().GetMethod("GetInventory").Invoke(restoredComponent,null);
                    Check(restoredContents.GetAllItems().Any(i => i.m_dropPrefab.name == "Iron" && i.m_stack == 12), "actual BackpackComponent deserializes its iron stack");
                }
                // Exercise PlayerProfile migration through the real native profile and WorldPlayerData types.
                var profile = new PlayerProfile("WorldCharactersProbe", FileHelpers.FileSource.Local); profile.SetName("Миграция");
                AccessTools.Field(typeof(PlayerProfile),"m_playerID").SetValue(profile,44L);
                VerifyProtectedSaveTimer(profile);
                byte[] payload;
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(33); for(int i=0;i<4;++i) writer.Write(25f); writer.Write("GP_Eikthyr"); writer.Write(0f);
                    writer.Write(saved.GetArray()); writer.Write(0); writer.Flush(); payload = stream.ToArray();
                }
                AccessTools.Field(typeof(PlayerProfile),"m_playerData").SetValue(profile,payload);
                var state = (CharacterState)gameState.GetMethod("FromProfile").Invoke(null,new object[]{profile,123L,"host",fingerprint,false,true});
                var encoded = StateCodec.Encode(state); state = StateCodec.Decode(encoded);
                Check(state.WorldData.Length >= 59 && state.Player.SequenceEqual(payload), "native existing-profile migration preserves bytes");
                gameState.GetMethod("Apply").Invoke(null,new object[]{profile,state});
                Check(((byte[])AccessTools.Field(typeof(PlayerProfile),"m_playerData").GetValue(profile)).SequenceEqual(payload), "native profile restore preserves player payload");
                VerifyCurrentWorldFields(profile, gameState, fingerprint);
                string inputResult = WorldCharactersAdministrationInputNativeChecks.Run();
                Check(inputResult.StartsWith("PASS"), "native administration input assertions");
                string uiResult = "Graphical administration checks not requested.";
                if (Environment.GetEnvironmentVariable("VMP_WORLDCHARACTERS_GRAPHICS") == "1")
                {
                    uiResult = AdministrationUiNativeChecks.Run();
                    Check(uiResult.Contains("native UI PASS"), "native administration window assertions executed without skip");
                }
                string result = "PASS: " + checks + " native assertions. Full pack loaded; Harmony hooks, inventory/custom metadata and profile migration verified.\n"
                    + inputResult + "\n" + uiResult
                    + (hasBackpacks ? "" : "\nSKIP: Adventure Backpacks native inventory/component checks; optional plugin is absent.")
                    + "\nMultiplayer sessions are not covered by this isolated menu probe.";
                if (Environment.GetEnvironmentVariable("VMP_WORLDCHARACTERS_GRAPHICS") == "1") StartCoroutine(PreviewAndFinish(result));
                else Finish(result,0);
            }
            catch(Exception e) { Finish("FAIL after " + checks + " native checks: " + e,2); }
        }
        private void VerifyCurrentWorldFields(PlayerProfile profile, Type gameState, string fingerprint)
        {
            const long firstWorld = 123, otherWorld = 987;
            MethodInfo getter = AccessTools.Method(typeof(PlayerProfile), "GetWorldData", new[] { typeof(long) });
            object first = getter.Invoke(profile, new object[] { firstWorld });
            string[] pointNames = { "m_spawnPoint", "m_logoutPoint", "m_deathPoint", "m_homePoint" };
            string[] flagNames = { "m_haveCustomSpawnPoint", "m_haveLogoutPoint", "m_haveDeathPoint" };
            for (int i = 0; i < pointNames.Length; ++i)
                AccessTools.Field(first.GetType(), pointNames[i]).SetValue(first, new Vector3(10 + i, 20 + i, 30 + i));
            for (int i = 0; i < flagNames.Length; ++i) AccessTools.Field(first.GetType(), flagNames[i]).SetValue(first, i != 1);
            byte[] originalMap = { 1, 2, 3, 4 };
            AccessTools.Field(first.GetType(), "m_mapData").SetValue(first, originalMap);
            CharacterState captured = (CharacterState)gameState.GetMethod("FromProfile").Invoke(null, new object[] { profile, firstWorld, "host", fingerprint, false, true });
            // Change the native values after the first read. The cache may hold
            // FieldInfo, never a particular world's values or instance.
            AccessTools.Field(first.GetType(), pointNames[0]).SetValue(first, new Vector3(81, 82, 83));
            AccessTools.Field(first.GetType(), "m_mapData").SetValue(first, new byte[] { 7, 8 });
            CharacterState updated = (CharacterState)gameState.GetMethod("FromProfile").Invoke(null, new object[] { profile, firstWorld, "host", fingerprint, false, true });
            Check(!captured.WorldData.SequenceEqual(updated.WorldData), "cached profile metadata reads changed current positions and map");
            CharacterState positions = (CharacterState)gameState.GetMethod("FromProfile").Invoke(null, new object[] { profile, firstWorld, "host", fingerprint, false, false });
            Check(positions.WorldData.SequenceEqual(StateCodec.WithoutMap(updated.WorldData)), "cached world fields preserve the position-only save envelope");
            // Apply the original snapshot to another world and read it back.
            CharacterState target = captured.Copy(); target.World = otherWorld;
            gameState.GetMethod("Apply").Invoke(null, new object[] { profile, target });
            CharacterState roundTrip = (CharacterState)gameState.GetMethod("FromProfile").Invoke(null, new object[] { profile, otherWorld, "host", fingerprint, false, true });
            Check(roundTrip.WorldData.SequenceEqual(captured.WorldData), "cached world fields retain all flags, positions and map through native restore");
            object other = getter.Invoke(profile, new object[] { otherWorld });
            for (int i = 0; i < pointNames.Length; ++i)
                Check((Vector3)AccessTools.Field(other.GetType(), pointNames[i]).GetValue(other) == new Vector3(10 + i, 20 + i, 30 + i), "native point restored independently: " + pointNames[i]);
            for (int i = 0; i < flagNames.Length; ++i)
                Check((bool)AccessTools.Field(other.GetType(), flagNames[i]).GetValue(other) == (i != 1), "native profile flag preserved: " + flagNames[i]);
            Check(((byte[])AccessTools.Field(first.GetType(), "m_mapData").GetValue(first)).SequenceEqual(new byte[] { 7, 8 }), "restoring another world does not overwrite this world's current map");
            target.WorldData = new byte[0]; gameState.GetMethod("Apply").Invoke(null, new object[] { profile, target });
            Check(flagNames.All(name => !(bool)AccessTools.Field(other.GetType(), name).GetValue(other))
                && AccessTools.Field(other.GetType(), "m_mapData").GetValue(other) == null, "fresh restore clears every current-world flag and map");
        }
        private IEnumerator PreviewAndFinish(string result)
        {
            AdministrationWindow preview = null; Exception failure = null;
            GameObject canvasObject = null, cameraObject = null;
            RenderTexture target = null;
            Camera camera = null;
            try
            {
                preview = AdministrationUiNativeChecks.Preview();
                // A hidden window has no reliable screen backbuffer. Render only
                // the synthetic preview to an owned canvas/texture instead.
                cameraObject = new GameObject("WC screenshot camera",typeof(Camera));
                camera = cameraObject.GetComponent<Camera>(); camera.enabled = false;
                camera.orthographic = true; camera.orthographicSize = 5;
                camera.transform.position = new Vector3(0,0,-10);
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.12f,.14f,.16f,1);
                camera.cullingMask = 1 << GUIManager.UILayer;
                target = new RenderTexture(1280,720,24); target.Create(); camera.targetTexture = target;
                canvasObject = new GameObject("WC screenshot canvas",typeof(RectTransform),typeof(Canvas));
                canvasObject.layer = GUIManager.UILayer;
                Canvas canvas = canvasObject.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = camera; canvas.planeDistance = 5;
                GameObject overlay = (GameObject)AccessTools.Field(typeof(AdministrationWindow),"overlay").GetValue(preview);
                overlay.transform.SetParent(canvasObject.transform,false);
                Canvas.ForceUpdateCanvases();
                AccessTools.Method(typeof(AdministrationWindow),"Scale").Invoke(preview,null);
            }
            catch (Exception error) { failure = error; }
            if (failure != null)
            {
                if (preview != null) preview.Hide(); if (canvasObject != null) UnityEngine.Object.Destroy(canvasObject);
                if (cameraObject != null) UnityEngine.Object.Destroy(cameraObject); if (target != null) { target.Release(); UnityEngine.Object.Destroy(target); }
                Finish("FAIL UI preview: " + failure,2); yield break;
            }
            yield return new WaitForEndOfFrame();
            Texture2D capture = null; RenderTexture previous = RenderTexture.active;
            try
            {
                Canvas.ForceUpdateCanvases(); camera.Render(); RenderTexture.active = target;
                capture = new Texture2D(target.width,target.height,TextureFormat.RGB24,false);
                capture.ReadPixels(new Rect(0,0,target.width,target.height),0,0); capture.Apply();
                Color32[] pixels = capture.GetPixels32(); Color32 first = pixels[0]; int different = 0;
                foreach (Color32 pixel in pixels) if (Math.Abs(pixel.r-first.r)+Math.Abs(pixel.g-first.g)+Math.Abs(pixel.b-first.b)>20) different++;
                Check(different > 10000,"preview contains rendered widgets instead of an empty hidden-window backbuffer");
                File.WriteAllBytes(Path.Combine(Root,"administration-preview.png"),capture.EncodeToPNG());
                Finish(result + "\nPASS: synthetic preview rendered to an owned texture without a visible game window.",0);
            }
            catch (Exception error) { Finish("FAIL UI screenshot: " + error,2); }
            finally
            {
                RenderTexture.active = previous; if (capture != null) UnityEngine.Object.Destroy(capture); preview.Hide();
                UnityEngine.Object.Destroy(canvasObject); camera.targetTexture = null; UnityEngine.Object.Destroy(cameraObject);
                target.Release(); UnityEngine.Object.Destroy(target);
            }
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
        private void VerifyProtectedSaveTimer(PlayerProfile profile)
        {
            // Call the replacement prefix directly: creating Game would run its
            // Awake, while invoking the original would perform a native save.
            // An unloaded protected fixture makes Publish return before I/O.
            object plugin = Chainloader.PluginInfos[Plugin.Id].Instance;
            FieldInfo protectedProfile = AccessTools.Field(typeof(Plugin),"protectedProfile");
            FieldInfo ready = AccessTools.Field(typeof(Plugin),"ready");
            object oldProfile = protectedProfile.GetValue(plugin), oldReady = ready.GetValue(plugin);
            try
            {
                protectedProfile.SetValue(plugin,profile); ready.SetValue(plugin,false);
                Type patch = typeof(Plugin).GetNestedType("SavePatch",BindingFlags.NonPublic);
                MethodInfo prefix = patch.GetMethod("Prefix",BindingFlags.NonPublic | BindingFlags.Static);
                ParameterInfo[] parameters = prefix.GetParameters();
                Check(parameters.Length == 1 && parameters[0].Name == "___m_saveTimer"
                    && parameters[0].ParameterType == typeof(float).MakeByRefType(), "protected save prefix injects native autosave timer by reference");
                object[] arguments = new object[] { 30f };
                Check(!(bool)prefix.Invoke(null,arguments) && (float)arguments[0] == 0f,
                    "protected save resets autosave timer and skips original save without a live character");
            }
            finally { protectedProfile.SetValue(plugin,oldProfile); ready.SetValue(plugin,oldReady); }
        }
        private void Check(bool value,string reason) { if(!value) throw new Exception(reason); ++checks; }
        private void Finish(string result,int code) { if(done) return; done=true; File.WriteAllText(Path.Combine(Root,"result.txt"),result); Logger.LogInfo(result); Application.Quit(code); }
        private void Update() { if(!done && Time.realtimeSinceStartup-start>100) Finish("FAIL native probe timeout",2); }
    }
}
