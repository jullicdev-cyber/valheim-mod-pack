using System;
using System.Globalization;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimModPack.ChestSearch
{
    public sealed class ChestMarker : IDisposable
    {
        private readonly Plugin plugin;
        private Container target;
        private Player player;
        private ZNet network;
        private ZDOID id;
        private GameObject marker;
        private GameObject labelObject;
        private Text label;
        private Material material;
        private float until;
        private float nextCheck;
        private string chestName;
        private string itemKey;

        public ChestMarker(Plugin plugin) { this.plugin = plugin; }
        public bool Show(SearchResult result)
        {
            return Show(result, null);
        }
        public bool Show(SearchResult result, string selectedItemKey)
        {
            Clear();
            if (result == null || result.Snapshot == null) return false;
            ChestSnapshot fresh; ReadFailure failure;
            if (!plugin.Reader.TryRead(result.Snapshot.Container, Player.m_localPlayer, plugin.Radius, out fresh, out failure)
                || fresh.Id != result.Snapshot.Id
                || (String.IsNullOrEmpty(selectedItemKey) ? fresh.Revision != result.Snapshot.Revision : !SearchService.ContainsItem(fresh, selectedItemKey))) return false;
            target = fresh.Container; id = fresh.Id; player = Player.m_localPlayer; network = ZNet.instance;
            itemKey = selectedItemKey;
            chestName = SearchText.Safe(result.ChestName, 32);
            until = Time.unscaledTime + plugin.MarkerSeconds;
            try
            {
                marker = new GameObject("ChestSearch.LocalMarker");
                marker.transform.position = target.transform.position;
                var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
                if (shader != null)
                {
                    material = new Material(shader);
                    material.name = "ChestSearch.MarkerMaterial";
                    material.renderQueue = 3100;
                    var ring = CreateLine("Ring", 33, 0.055f);
                    for (int i = 0; i < 33; i++)
                    {
                        float angle = i * Mathf.PI * 2 / 32;
                        ring.SetPosition(i, new Vector3(Mathf.Cos(angle) * 0.7f, 0.08f, Mathf.Sin(angle) * 0.7f));
                    }
                    var arrow = CreateLine("Arrow", 5, 0.075f);
                    arrow.SetPosition(0, new Vector3(0, 2.8f, 0));
                    arrow.SetPosition(1, new Vector3(0, 1.15f, 0));
                    arrow.SetPosition(2, new Vector3(-0.3f, 1.6f, 0));
                    arrow.SetPosition(3, new Vector3(0, 1.15f, 0));
                    arrow.SetPosition(4, new Vector3(0.3f, 1.6f, 0));
                }
                var front = GUIManager.CustomGUIFront;
                if (front != null)
                {
                    var gui = GUIManager.Instance;
                    var center = new Vector2(0.5f, 0.5f);
                    labelObject = gui.CreateText("", front.transform, center, center, Vector2.zero, gui.AveriaSerifBold,
                        20, gui.ValheimOrange, true, Color.black, 320, 54, false);
                    labelObject.name = "ChestSearch.LocalMarkerLabel";
                    label = labelObject.GetComponent<Text>();
                    label.supportRichText = false; label.raycastTarget = false;
                    label.alignment = TextAnchor.MiddleCenter;
                    label.resizeTextForBestFit = true; label.resizeTextMinSize = 14; label.resizeTextMaxSize = 20;
                }
                Tick();
                return true;
            }
            catch { Clear(); throw; }
        }
        private LineRenderer CreateLine(string name, int count, float width)
        {
            var obj = new GameObject("ChestSearch." + name);
            obj.transform.SetParent(marker.transform, false);
            var line = obj.AddComponent<LineRenderer>();
            line.sharedMaterial = material;
            line.useWorldSpace = false; line.positionCount = count;
            line.startWidth = width; line.endWidth = width;
            line.startColor = line.endColor = new Color(1, 0.68f, 0.2f, 0.95f);
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            return line;
        }
        public void Tick()
        {
            if (target == null)
            {
                if (marker != null || labelObject != null || material != null || !ReferenceEquals(player, null) || !ReferenceEquals(network, null)) Clear();
                return;
            }
            if (player == null || !ReferenceEquals(player, Player.m_localPlayer) || network == null
                || !ReferenceEquals(network, ZNet.instance) || player.IsDead() || player.IsTeleporting()
                || Time.unscaledTime >= until || Vector3.Distance(player.transform.position, target.transform.position) > plugin.Radius)
            { Clear(); return; }
            if (Time.unscaledTime >= nextCheck)
            {
                nextCheck = Time.unscaledTime + 0.25f;
                ChestSnapshot fresh; ReadFailure failure;
                if (!plugin.Reader.TryRead(target, player, plugin.Radius, out fresh, out failure) || fresh.Id != id
                    || (!String.IsNullOrEmpty(itemKey) && !SearchService.ContainsItem(fresh, itemKey)))
                { Clear(); return; }
            }
            if (marker != null) marker.transform.position = target.transform.position;
            if (labelObject == null) return;
            Camera camera = Utils.GetMainCamera();
            if (camera == null) { labelObject.SetActive(false); return; }
            Vector3 screen = camera.WorldToScreenPoint(target.transform.position + Vector3.up * 2.9f);
            bool onScreen = screen.z > 0 && screen.x >= 0 && screen.x <= Screen.width && screen.y >= 0 && screen.y <= Screen.height;
            labelObject.SetActive(onScreen);
            if (!onScreen) return;
            RectTransform parent = labelObject.transform.parent as RectTransform;
            Canvas canvas = labelObject.GetComponentInParent<Canvas>();
            Vector2 local;
            if (parent != null && RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, screen,
                canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera, out local))
                labelObject.GetComponent<RectTransform>().anchoredPosition = local;
            label.text = chestName + "\n" + Vector3.Distance(player.transform.position, target.transform.position).ToString("0.0", CultureInfo.InvariantCulture) + " " + (plugin.Russian ? "м" : "m");
        }
        public void Clear()
        {
            if (marker != null) UnityEngine.Object.Destroy(marker);
            if (labelObject != null) UnityEngine.Object.Destroy(labelObject);
            if (material != null) UnityEngine.Object.Destroy(material);
            marker = null; labelObject = null; material = null; label = null;
            target = null; player = null; network = null; itemKey = null; nextCheck = 0;
        }
        public void Dispose() { Clear(); }
    }
}
