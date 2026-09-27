using UnityEngine;

namespace ValheimModPack.NordicRadio
{
    // Only placed, networked instances attach. The hammer ghost and Jotunn's
    // inactive prefab must never request tracks or create a second audio source.
    public sealed class RadioPiece : MonoBehaviour, Hoverable, Interactable, IRadioTarget
    {
        private ZNetView view;
        private bool attached;
        private bool lit;
        private Transform glow;
        private Light coreLight;
        private float nextAttachAttempt;

        public ZDOID Id { get { return IsReady ? view.GetZDO().m_uid : default(ZDOID); } }
        public bool IsReady
        {
            get
            {
                if (this == null) return false;
                if (!view) view = GetComponent<ZNetView>();
                return isActiveAndEnabled && view && view.IsValid();
            }
        }
        public Vector3 SoundPosition { get { return transform.TransformPoint(new Vector3(0.51f, 1.07f, 0f)); } }

        private void Start() { TryAttach(); }
        private void OnEnable() { TryAttach(); }
        private void Update()
        {
            if (!attached && Time.unscaledTime >= nextAttachAttempt)
            {
                nextAttachAttempt = Time.unscaledTime + 1f;
                TryAttach();
            }
        }
        private void TryAttach()
        {
            if (attached || !IsReady || Plugin.Instance == null) return;
            attached = true;
            Plugin.Instance.Attach(this);
        }
        private void OnDisable() { Detach(); }
        private void OnDestroy() { Detach(); }
        private void Detach()
        {
            if (!attached) return;
            attached = false;
            if (Plugin.Instance != null) Plugin.Instance.Detach(this);
        }

        public bool HasAccess(Player player)
        {
            return player && IsReady && !player.IsDead()
                && (player.transform.position - transform.position).sqrMagnitude <= 25f
                && PrivateArea.CheckAccess(transform.position, 0f, false, false);
        }

        public string GetHoverName() { return Localize("$vmp_skald_horn"); }
        public string GetHoverText()
        {
            if (!IsReady) return string.Empty;
            return Localize("$vmp_skald_horn\n[<color=yellow><b>$KEY_Use</b></color>] $vmp_radio_open");
        }
        public float GetHoverOffset() { return 0f; }
        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            Player player = user as Player;
            if (hold || player == null || player != Player.m_localPlayer || !HasAccess(player)) return false;
            if (Plugin.Instance == null) return false;
            Plugin.Instance.OpenRadio(this);
            return true;
        }
        public bool UseItem(Humanoid user, ItemDrop.ItemData item) { return false; }

        public void SetLit(bool on)
        {
            if (this == null) return;
            // Toggle the dedicated overlay, never change shared material instances.
            // Every placed horn shares immutable model assets with the prefab.
            if (!glow) glow = transform.Find("SkaldModel/PlayingGlow");
            if (!coreLight)
            {
                Transform child = transform.Find("SkaldModel/CoreLight");
                if (child) coreLight = child.GetComponent<Light>();
            }
            if (glow && (lit != on || glow.gameObject.activeSelf != on)) glow.gameObject.SetActive(on);
            if (coreLight) coreLight.enabled = on;
            lit = on;
        }
        private static string Localize(string text)
        {
            return Localization.instance != null ? Localization.instance.Localize(text) : text;
        }
    }
}
