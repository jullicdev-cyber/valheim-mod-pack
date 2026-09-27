using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimModPack.NordicRadio
{
    // Music is tied to one selected item in the main inventory, not to an
    // equipment slot. Holstering for a ship's rudder therefore keeps it playing.
    internal sealed class PortableController : IDisposable
    {
        internal const string Marker = "vmp_nordicradio_idol";
        internal const string ItemToken = "vmp_nordicradio_item";
        private readonly Plugin plugin;
        private readonly Harmony harmony;
        private readonly Dictionary<Player, PortableRadioTarget> targets = new Dictionary<Player, PortableRadioTarget>();
        private Player local;
        private ZDOID activeId;
        private string token = "";
        private bool pendingOpen;
        private int openAfterFrame;
        private float openDeadline, nextDiscover;
        private bool disposed;

        internal PortableController(Plugin plugin)
        {
            this.plugin = plugin;
            harmony = new Harmony(Plugin.Id + ".portable");
            try
            {
                harmony.Patch(AccessTools.Method(typeof(Humanoid), "UseItem", new[] { typeof(Inventory), typeof(ItemDrop.ItemData), typeof(bool) }),
                    prefix: new HarmonyMethod(typeof(PortableController), "BeforeUseItem"));
                harmony.Patch(AccessTools.Method(typeof(Humanoid), "StartAttack", new[] { typeof(Character), typeof(bool) }),
                    prefix: new HarmonyMethod(typeof(PortableController), "BeforeAttack"));
            }
            catch { harmony.UnpatchSelf(); throw; }
        }
        internal static bool IsIdol(ItemDrop.ItemData item)
        {
            return item != null && item.m_dropPrefab != null && item.m_dropPrefab.name == PortableModel.PrefabName;
        }
        internal static ZDO PlayerState(Player player)
        {
            if (player == null) return null;
            ZNetView view = player.GetComponent<ZNetView>();
            return view != null && view.IsValid() ? view.GetZDO() : null;
        }
        internal bool HasItem(string expected)
        {
            if (disposed || local == null || local != Player.m_localPlayer || local.IsDead()
                || String.IsNullOrEmpty(expected) || expected != token) return false;
            foreach (var item in local.GetInventory().GetAllItems())
            {
                string saved;
                if (IsIdol(item) && item.m_customData != null && item.m_customData.TryGetValue(ItemToken, out saved) && saved == expected) return true;
            }
            return false;
        }
        private static bool BeforeUseItem(Humanoid __instance, Inventory __0, ItemDrop.ItemData __1)
        {
            if (__instance != Player.m_localPlayer || !IsIdol(__1) || Plugin.Instance == null || Plugin.Instance.Portable == null) return true;
            try { Plugin.Instance.Portable.Activate(__1); }
            catch (Exception error) { Plugin.Instance.Report(error); }
            return false;
        }
        private static bool BeforeAttack(Humanoid __instance, ItemDrop.ItemData ___m_rightItem, ref bool __result)
        {
            if (__instance != Player.m_localPlayer || !IsIdol(___m_rightItem) || Plugin.Instance == null || Plugin.Instance.Portable == null) return true;
            try { Plugin.Instance.Portable.Activate(___m_rightItem); }
            catch (Exception error) { Plugin.Instance.Report(error); }
            __result = false; return false;
        }
        private void Activate(ItemDrop.ItemData item)
        {
            Player player = Player.m_localPlayer;
            if (disposed || player == null || player.IsDead() || player.IsTeleporting() || player.InCutscene()
                || player.IsSleeping() || !player.GetInventory().ContainsItem(item)) return;
            ZDO state = PlayerState(player);
            if (state == null || !state.IsOwner()) return;
            if (plugin.RadioWindowVisible || (pendingOpen && player.IsItemEquiped(item))) return;
            if (!player.IsItemEquiped(item) && !player.EquipItem(item, true)) return;
            if (local != player) { Reset(); local = player; }
            if (item.m_customData == null) item.m_customData = new Dictionary<string, string>();
            string selected;
            if (!item.m_customData.TryGetValue(ItemToken, out selected) || !ValidToken(selected))
            { selected = Guid.NewGuid().ToString("N"); item.m_customData[ItemToken] = selected; }
            if (token != selected) ClearLocal();
            token = selected; activeId = state.m_uid;
            state.Set(Marker, token);
            plugin.Service.SetPortable(activeId, token);
            if (InventoryGui.instance != null && InventoryGui.IsVisible()) InventoryGui.instance.Hide();
            // Defer until the inventory's closing animation/input state releases.
            pendingOpen = true; openAfterFrame = Time.frameCount + 1; openDeadline = Time.unscaledTime + 3;
            nextDiscover = 0;
        }
        private static bool ValidToken(string value)
        {
            if (value == null || value.Length != 32) return false;
            foreach (char c in value) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }
        internal void Tick()
        {
            if (disposed) return;
            Player player = Player.m_localPlayer;
            if (local != player)
            {
                Reset(); local = player;
                ZDO joined = PlayerState(local);
                if (joined != null && joined.IsOwner()) joined.Set(Marker, "");
            }
            // Remote Player objects can outlive the local player during scene
            // teardown; do not rediscover them after Reset has detached sound.
            if (player == null) return;
            ZDO localState = PlayerState(local);
            if (!String.IsNullOrEmpty(token))
            {
                if (!HasItem(token) || localState == null || localState.m_uid != activeId) ClearLocal();
                else
                {
                    if (localState.GetString(Marker, "") != token) localState.Set(Marker, token);
                    plugin.Service.SetPortable(activeId, token);
                }
            }
            if (Time.unscaledTime >= nextDiscover)
            {
                nextDiscover = Time.unscaledTime + 0.25f;
                var dead = new List<Player>();
                foreach (var pair in targets) if (!pair.Value.IsReady) dead.Add(pair.Key);
                foreach (Player key in dead) { plugin.Detach(targets[key]); targets.Remove(key); }
                foreach (Player other in Player.GetAllPlayers())
                {
                    if (other == null || other.IsDead() || targets.ContainsKey(other)) continue;
                    ZDO state = PlayerState(other);
                    string marker = state == null ? "" : state.GetString(Marker, "");
                    if (!ValidToken(marker)) continue;
                    var target = new PortableRadioTarget(this, other, marker);
                    if (!target.IsReady) continue;
                    targets.Add(other, target); plugin.Attach(target);
                    plugin.Service.Watch(target.Id);
                }
            }
            if (pendingOpen)
            {
                if (!HasItem(token) || Time.unscaledTime > openDeadline || Input.GetKeyDown(KeyCode.Escape)
                    || ZInput.GetButtonDown("JoyButtonB")) pendingOpen = false;
                // No radio lease is held yet: Jotunn's patched TextInput guard
                // correctly includes another mod's modal here.
                else if (Time.frameCount >= openAfterFrame && !InventoryGui.IsVisible() && !Menu.IsVisible() && !UnifiedPopup.IsVisible()
                    && !TextInput.IsVisible() && !global::Console.IsVisible() && (Chat.instance == null || !Chat.instance.HasFocus()))
                {
                    PortableRadioTarget target;
                    if (targets.TryGetValue(local, out target) && target.IsReady)
                    { pendingOpen = false; plugin.OpenRadio(target); }
                }
            }
        }
        private void ClearLocal()
        {
            if (!activeId.IsNone() && plugin.Service != null) plugin.Service.SetPortable(activeId, "");
            ZDO state = PlayerState(local);
            if (state != null && state.IsOwner()) state.Set(Marker, "");
            activeId = default(ZDOID); token = ""; pendingOpen = false;
        }
        internal void Reset()
        {
            ClearLocal();
            foreach (var target in targets.Values) plugin.Detach(target);
            targets.Clear(); local = null;
        }
        public void Dispose()
        {
            if (disposed) return;
            try { Reset(); } finally { disposed = true; harmony.UnpatchSelf(); }
        }
    }

    internal sealed class PortableRadioTarget : IRadioTarget
    {
        private readonly PortableController controller;
        private readonly Player owner;
        private readonly string token;
        internal PortableRadioTarget(PortableController controller, Player owner, string token)
        { this.controller = controller; this.owner = owner; this.token = token; }
        public ZDOID Id { get { ZDO state = PortableController.PlayerState(owner); return state == null ? default(ZDOID) : state.m_uid; } }
        public bool IsReady
        {
            get
            {
                if (owner == null || owner.IsDead() || !owner.isActiveAndEnabled) return false;
                ZDO state = PortableController.PlayerState(owner);
                return state != null && state.GetString(PortableController.Marker, "") == token
                    && (owner != Player.m_localPlayer || controller.HasItem(token));
            }
        }
        public Vector3 SoundPosition { get { return owner != null ? owner.transform.position + Vector3.up * 1.1f : Vector3.zero; } }
        public bool HasAccess(Player player) { return player != null && player == owner && player == Player.m_localPlayer && IsReady; }
        public string GetHoverName() { return Localization.instance != null ? Localization.instance.Localize("$vmp_skald_idol") : "Skald's Idol"; }
        public void SetLit(bool on) { }
    }
}
