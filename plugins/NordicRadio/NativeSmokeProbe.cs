// Test-only BepInEx plugin. Excluded from the distributed NordicRadio DLL.
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using BepInEx;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.Networking;
using ValheimModPack.NordicRadio;

namespace ValheimModPack.NordicRadioSmoke
{
    [BepInPlugin("valheimmodpack.nordicradio.smokeprobe", "Nordic Radio native smoke probe", "1.0.0")]
    [BepInDependency("valheimmodpack.nordicradio", "1.0.0")]
    public sealed class NativeSmokeProbe : BaseUnityPlugin
    {
        private float started;
        private bool finished;
        private static string Root { get { return Environment.GetEnvironmentVariable("NORDICRADIO_SMOKE_ROOT"); } }
        private void Awake()
        {
            if (String.IsNullOrEmpty(Root)) { enabled = false; return; }
            started = Time.realtimeSinceStartup;
            Utils.SetSaveDataPath(Path.Combine(Root, "Saves"));
            PrefabManager.OnVanillaPrefabsAvailable += Check;
            Logger.LogInfo("SMOKE isolated BepInEx root=" + Paths.BepInExRootPath + " saves=" + Path.Combine(Root, "Saves"));
        }
        private void Check()
        {
            PrefabManager.OnVanillaPrefabsAvailable -= Check;
            try
            {
                GameObject prefab = PrefabManager.Instance.GetPrefab(HornModel.PrefabName);
                if (!prefab) throw new Exception("Registered prefab missing");
                MeshFilter[] meshes = prefab.GetComponentsInChildren<MeshFilter>(true);
                if (meshes.Length != 7) throw new Exception("Mesh count " + meshes.Length);
                int vertices = 0;
                foreach (MeshFilter mesh in meshes)
                {
                    if (!mesh.sharedMesh || mesh.sharedMesh.vertexCount == 0) throw new Exception("Missing native mesh");
                    vertices += mesh.sharedMesh.vertexCount;
                }
                Piece piece = prefab.GetComponent<Piece>();
                if (!piece.m_icon || piece.m_icon.texture.width != 128) throw new Exception("Missing native icon");
                WearNTear wear = prefab.GetComponent<WearNTear>();
                if (!wear || wear.m_new != wear.m_worn || wear.m_new != wear.m_broken) throw new Exception("Damage visuals");
                if (prefab.GetComponents<Collider>().Length != 2) throw new Exception("Colliders");
                if (prefab.GetComponent<RadioPiece>() == null) throw new Exception("Interaction missing");
                string report = "PASS native Unity model: " + meshes.Length + " meshes, " + vertices
                    + " vertices, 128px icon, colliders, health visuals, prefab registration.\n";
                foreach (MeshRenderer renderer in prefab.GetComponentsInChildren<MeshRenderer>(true))
                {
                    string shader = renderer.sharedMaterial.shader.name;
                    if (shader != "Custom/Piece") throw new Exception("Unexpected furniture shader " + shader);
                    report += renderer.name + " shader=" + shader + "\n";
                }
                report += CheckMusicDucking();
                StartCoroutine(CheckPortableThenDecode(report));
            }
            catch (Exception error) { Finish("FAIL " + error, 2); }
        }
        private IEnumerator CheckPortableThenDecode(string report)
        {
            float deadline = Time.realtimeSinceStartup + 20;
            while ((ObjectDB.instance == null || ObjectDB.instance.GetItemPrefab(PortableModel.PrefabName) == null)
                && Time.realtimeSinceStartup < deadline) yield return null;
            try
            {
                GameObject prefab = ObjectDB.instance == null ? null : ObjectDB.instance.GetItemPrefab(PortableModel.PrefabName);
                if (!prefab) throw new Exception("Portable idol not registered in native ObjectDB");
                ItemDrop drop = prefab.GetComponent<ItemDrop>();
                var shared = drop.m_itemData.m_shared;
                if (shared.m_itemType != ItemDrop.ItemData.ItemType.Tool || shared.m_buildPieces != null || shared.m_useDurability || shared.m_maxStackSize != 1)
                    throw new Exception("Portable item type/build/durability/stack configuration");
                if (shared.m_icons == null || shared.m_icons.Length == 0 || shared.m_icons[0].texture.width != 128)
                    throw new Exception("Portable item icon");
                Transform attach = prefab.transform.Find("attach");
                if (!attach || attach.GetComponentsInChildren<MeshFilter>(true).Length != 5 || attach.GetComponentsInChildren<Collider>(true).Length != 0)
                    throw new Exception("Portable equipped visual/physics separation");
                if (prefab.GetComponents<Collider>().Length != 1) throw new Exception("Portable dropped collider");
                var hammer = PrefabManager.Instance.GetPrefab("Hammer").GetComponent<ItemDrop>().m_itemData.m_shared;
                if (hammer.m_buildPieces == null || ReferenceEquals(hammer, shared)) throw new Exception("Original hammer was modified");
                // Fejd registers items but defers recipes until world startup.
                // Exercise that exact Jotunn registration path in this isolated DB.
                MethodInfo registerRecipes = typeof(ItemManager).GetMethod("RegisterCustomRecipes", BindingFlags.Instance | BindingFlags.NonPublic);
                if (registerRecipes == null) throw new Exception("Jotunn recipe registration API changed");
                registerRecipes.Invoke(ItemManager.Instance, new object[] { ObjectDB.instance });
                Recipe recipe = ItemManager.Instance.GetItem(PortableModel.PrefabName).Recipe.Recipe;
                if (recipe.m_minStationLevel != 1 || !recipe.m_craftingStation || recipe.m_craftingStation.name != "forge")
                    throw new Exception("Portable forge recipe station: " + (recipe.m_craftingStation ? recipe.m_craftingStation.name : "null") + " level=" + recipe.m_minStationLevel);
                var expected = new System.Collections.Generic.Dictionary<string,int> {
                    {"Wood",10}, {"FineWood",5}, {"Bronze",2}, {"SurtlingCore",1} };
                if (recipe.m_resources.Length != expected.Count) throw new Exception("Portable recipe resource count");
                foreach (var requirement in recipe.m_resources)
                {
                    int amount;
                    if (!requirement.m_resItem || !expected.TryGetValue(requirement.m_resItem.name, out amount) || requirement.m_amount != amount)
                        throw new Exception("Portable recipe material/amount");
                }
                report += "PASS native portable idol: ObjectDB item, forge I recipe Wood10/FineWood5/Bronze2/SurtlingCore1, 128px icon, five equipped meshes, root collider, original Hammer unchanged.\n";
            }
            catch (Exception error) { Finish(report + "FAIL portable idol: " + error, 6); yield break; }
            string mp3 = Environment.GetEnvironmentVariable("NORDICRADIO_SMOKE_MP3");
            if (!String.IsNullOrEmpty(mp3)) yield return StartCoroutine(Decode(report, mp3));
            else Finish(report + "MP3 decode not requested.\n", 0);
        }
        private static string CheckMusicDucking()
        {
            MusicMan manager = MusicMan.instance;
            if (!manager) throw new Exception("Native MusicMan instance unavailable for ducking test");
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            FieldInfo controllerField = typeof(Plugin).GetField("musicDucking", flags);
            object controller = controllerField == null ? null : controllerField.GetValue(Plugin.Instance);
            if (controller == null) throw new Exception("Music ducking controller is not installed");
            MethodInfo update = controller.GetType().GetMethod("Update", flags);
            MethodInfo reset = controller.GetType().GetMethod("Reset", flags);
            MethodInfo nativeUpdate = typeof(MusicMan).GetMethod("UpdateMusic", flags);
            string[] fieldNames = { "m_musicSource", "m_queuedMusic", "m_currentMusic", "m_stopMusic", "m_resetMusicTimer" };
            var fields = new FieldInfo[fieldNames.Length];
            var saved = new object[fieldNames.Length];
            for (int i = 0; i < fields.Length; i++)
            {
                fields[i] = typeof(MusicMan).GetField(fieldNames[i], flags);
                if (fields[i] == null) throw new Exception("MusicMan test contract changed: " + fieldNames[i]);
                saved[i] = fields[i].GetValue(manager);
            }
            bool originalDebug = Terminal.m_showTests;
            var holder = new GameObject("NordicRadio.SilentMusicSmokeTest");
            var dummy = holder.AddComponent<AudioSource>();
            dummy.playOnAwake = false;
            dummy.volume = 0.6f;
            try
            {
                reset.Invoke(controller, null);
                fields[0].SetValue(manager, dummy);
                fields[1].SetValue(manager, null);
                fields[2].SetValue(manager, null);
                fields[3].SetValue(manager, false);
                fields[4].SetValue(manager, 0f);
                Terminal.m_showTests = false;
                // No queued/current clip and a stopped dummy source exercise
                // the native branch without a volume write, the compounding risk.
                nativeUpdate.Invoke(manager, new object[] { 0.016f });
                Close(dummy.volume, 0.6f, "initial baseline");
                for (int i = 0; i < 5; i++) update.Invoke(controller, new object[] { 1f, 0.2f, 0.1f });
                Close(dummy.volume, 0.12f, "twenty percent duck");
                for (int i = 0; i < 100; i++)
                {
                    nativeUpdate.Invoke(manager, new object[] { 0.016f });
                    update.Invoke(controller, new object[] { 1f, 0.2f, 0.016f });
                }
                Close(dummy.volume, 0.12f, "no repeated multiplication");
                dummy.volume = 0.35f;
                nativeUpdate.Invoke(manager, new object[] { 0.016f });
                Close(dummy.volume, 0.07f, "fresh external volume");
                reset.Invoke(controller, null);
                Close(dummy.volume, 0.35f, "reset restoration");
                if (dummy.isPlaying || dummy.clip != null) throw new Exception("Ducking test unexpectedly played audio");
                return "PASS native MusicMan Harmony dispatch: duck to 20%, 100 updates without compounding, changed baseline, restoration. No audio played or preferences changed.\n";
            }
            finally
            {
                reset.Invoke(controller, null);
                for (int i = 0; i < fields.Length; i++) fields[i].SetValue(manager, saved[i]);
                Terminal.m_showTests = originalDebug;
                UnityEngine.Object.Destroy(holder);
            }
        }
        private static void Close(float actual, float expected, string name)
        {
            if (Single.IsNaN(actual) || Math.Abs(actual - expected) > 0.0001f)
                throw new Exception("Native music ducking " + name + ": expected " + expected + ", got " + actual);
        }
        private IEnumerator Decode(string report, string path)
        {
            AudioClip clip;
            UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.MPEG);
            ((DownloadHandlerAudioClip)request.downloadHandler).streamAudio = true;
            using (request)
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                { Finish(report + "FAIL MP3 decoder: " + request.error, 4); yield break; }
                clip = DownloadHandlerAudioClip.GetContent(request);
                if (!clip || clip.length <= 0 || clip.samples <= 0 || clip.channels <= 0)
                { Finish(report + "FAIL MP3 decoder returned an empty clip", 4); yield break; }
                report += "PASS MP3 decoder: " + clip.length + " seconds, " + clip.samples + " samples, "
                    + clip.channels + " channels.\n";
            }
            // Exercise the real streaming source -> RadioGain -> next filter
            // chain. The last test-only filter zeros every sample before output.
            var holder = new GameObject("NordicRadio.SilentDspSmokeTest");
            var source = holder.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0;
            source.volume = 1;
            source.clip = clip;
            var gain = holder.AddComponent<RadioGain>();
            gain.Gain = 1f;
            var probe = holder.AddComponent<SilentDspProbe>();
            source.Play();
            float deadline = Time.realtimeSinceStartup + 2;
            float earliest = Time.realtimeSinceStartup + 0.3f;
            while (Time.realtimeSinceStartup < deadline && (Time.realtimeSinceStartup < earliest || probe.Callbacks < 8 || probe.Peak <= 0)) yield return null;
            source.Stop();
            string failure = null;
            try
            {
                if (probe.Callbacks < 1 || probe.Peak <= 0 || probe.Invalid)
                    throw new Exception("No finite nonzero PCM reached the last filter (callbacks=" + probe.Callbacks + ", peak=" + probe.Peak + "). The audio device may be unavailable.");
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                object processor = typeof(RadioGain).GetField("processor", flags).GetValue(gain);
                float currentGain = (float)processor.GetType().GetField("currentGain", flags).GetValue(processor);
                if (currentGain != 1f) throw new Exception("Native RadioGain is not at unity: " + currentGain);
                report += "PASS native streaming MP3 DSP: " + probe.Callbacks + " callbacks, finite nonzero PCM, gain=" + currentGain + ". Test output was zeroed before speakers.\n";
            }
            catch (Exception error) { failure = "FAIL native streaming DSP: " + error; }
            UnityEngine.Object.Destroy(holder);
            UnityEngine.Object.Destroy(clip);
            Finish(report + (failure ?? ""), failure == null ? 0 : 5);
        }
        private void Update()
        {
            if (!finished && Time.realtimeSinceStartup - started > 90f)
                Finish("FAIL timeout waiting for native registration/MP3 decoder", 3);
        }
        private void Finish(string message, int code)
        {
            if (finished) return;
            finished = true;
            Logger.LogInfo(message);
            File.WriteAllText(Path.Combine(Root, "result.txt"), message);
            Application.Quit(code);
        }
    }

    public sealed class SilentDspProbe : MonoBehaviour
    {
        internal volatile int Callbacks;
        internal volatile bool Invalid;
        internal volatile float Peak;
        private void OnAudioFilterRead(float[] data, int channels)
        {
            float peak = 0;
            for (int i = 0; i < data.Length; i++)
            {
                float value = data[i];
                if (Single.IsNaN(value) || Single.IsInfinity(value)) Invalid = true;
                else peak = Math.Max(peak, Math.Abs(value));
                data[i] = 0;
            }
            Peak = Math.Max(Peak, peak);
            Callbacks++;
        }
    }
}
