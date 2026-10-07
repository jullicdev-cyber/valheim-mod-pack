using UnityEngine;

namespace ValheimModPack.PartyPrison
{
    /// <summary>A host-created cell fixture, never a hammer recipe or crafting station.</summary>
    public sealed class PrisonConsole : MonoBehaviour, Hoverable, Interactable
    {
        public const string PrefabName = "vmp_prison_console", ConsoleKey = "VMP_PP_Console";
        private ZNetView view;

        public ZDO Zdo
        {
            get {
                if (this == null || !isActiveAndEnabled) return null;
                if (!view) view = GetComponent<ZNetView>();
                return view && view.IsValid() ? view.GetZDO() : null;
            }
        }

        public bool ValidNetworkObject
        {
            get {
                ZDO zdo = Zdo;
                return zdo != null && zdo.GetPrefab() == PrefabName.GetStableHashCode()
                    && zdo.GetBool(ArenaBuilder.ProtectedKey, false) && zdo.GetBool(ConsoleKey, false);
            }
        }

        public string GetHoverName() { return Plugin.T("Пульт арены", "Arena controls"); }
        public float GetHoverOffset() { return 0f; }

        public string GetHoverText()
        {
            if (!ValidNetworkObject) return "";
            if (Plugin.Active == null || !Plugin.Active.CanOpenPrisonConsole(this, Player.m_localPlayer))
                return GetHoverName() + "\n" + Plugin.T("Доступен заключённому в камере", "Available to the inmate in the cell");
            string text = GetHoverName() + "\n[<color=yellow><b>$KEY_Use</b></color>] "
                + Plugin.T("Выбрать противников и снаряжение", "Choose enemies and equipment");
            return Localization.instance == null ? text : Localization.instance.Localize(text);
        }

        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            Player player = user as Player;
            if (hold || player == null || player != Player.m_localPlayer || !ValidNetworkObject || Plugin.Active == null) return false;
            return Plugin.Active.TryOpenPrisonConsole(this, player);
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item) { return false; }
    }
}
