// Test-only BepInEx plugin. Excluded from the distributed NordicRadio DLL.
using System;
using System.Collections;
using System.IO;
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
                string mp3 = Environment.GetEnvironmentVariable("NORDICRADIO_SMOKE_MP3");
                if (!String.IsNullOrEmpty(mp3)) StartCoroutine(Decode(report, mp3));
                else Finish(report + "MP3 decode not requested.\n", 0);
            }
            catch (Exception error) { Finish("FAIL " + error, 2); }
        }
        private IEnumerator Decode(string report, string path)
        {
            UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.MPEG);
            ((DownloadHandlerAudioClip)request.downloadHandler).streamAudio = true;
            using (request)
            {
                yield return request.SendWebRequest();
                if (request.isNetworkError || request.isHttpError)
                { Finish(report + "FAIL MP3 decoder: " + request.error, 4); yield break; }
                AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                if (!clip || clip.length <= 0 || clip.samples <= 0 || clip.channels <= 0)
                { Finish(report + "FAIL MP3 decoder returned an empty clip", 4); yield break; }
                report += "PASS MP3 decoder: " + clip.length + " seconds, " + clip.samples + " samples, "
                    + clip.channels + " channels. No audio was played.\n";
                UnityEngine.Object.Destroy(clip);
            }
            Finish(report, 0);
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
}
