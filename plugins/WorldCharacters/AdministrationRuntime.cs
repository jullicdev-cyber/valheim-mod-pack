using System;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;

namespace ValheimModPack.WorldCharacters
{
    public sealed partial class Plugin
    {
        private AdministrationSession administration;
        private AdministrationWindow administrationWindow;
        private ConfigEntry<KeyboardShortcut> administrationShortcut;
        private ZNet administrationNetwork;
        private long administrationWorld;
        private float administrationErrorAt;
        private string administrationError = "";
        private static readonly FieldInfo AdministrationInputRequests = AccessTools.Field(typeof(GUIManager), "InputBlockRequests");

        private static string AdminT(string ru, string en)
        { return Localization.instance != null && Localization.instance.GetSelectedLanguage() == "Russian" ? ru : en; }

        private void InitializeAdministration()
        {
            administrationShortcut = Config.Bind("Controls", "OpenAdministration", new KeyboardShortcut(KeyCode.F4, KeyCode.LeftControl),
                "Open the local host's World Characters panel. Rebind in Bindrune. The opening chord consumes gameplay input.");
            administration = new AdministrationSession(store);
            administrationWindow = new AdministrationWindow(new AdministrationUiBindings {
                CanUse = CanAdministerCharacters, WorldId = AdministrationWorldId,
                ShortcutLabel = AdministrationShortcutLabel, Translate = AdminT,
                Snapshot = administration.GetSnapshot,
                ItemName = AdministrationItemName, ItemIcon = AdministrationItemIcon,
                Refresh = () => { administrationError = ""; administration.Refresh(); },
                Inspect = id => { administrationError = ""; administration.Inspect(id); },
                Decide = (id, fingerprint, decision) => { administrationError = ""; administration.Decide(id, fingerprint, decision); },
                StatusText = AdministrationStatus, Error = ReportAdministration,
                OnClosed = () => { }
            });
            InitializeAdminInput(administrationShortcut);
        }

        private long AdministrationWorldId()
        { return ZNet.instance == null ? 0 : ZNet.instance.GetWorldUID(); }

        private bool CanAdministerCharacters()
        {
            return isActiveAndEnabled && store != null && ZNet.instance != null && ZNet.instance.IsServer()
                && Game.instance != null && !Game.instance.IsShuttingDown()
                && AdministrativeReady && Hosting && AdministrationWorldId() != 0;
        }

        private bool CanOpenAdminWindow()
        {
            Player player = Player.m_localPlayer;
            if (!CanAdministerCharacters() || player == null || player.IsTeleporting() || player.IsSleeping() || player.InCutscene()
                || GUIManager.CustomGUIFront == null || GUIManager.Instance.AveriaSerif == null
                || ZInput.s_IsRebindActive || InventoryGui.IsVisible() || Menu.IsVisible() || UnifiedPopup.IsVisible()
                || StoreGui.IsVisible() || TextInput.IsVisible() || global::Console.IsVisible()
                || Hud.IsPieceSelectionVisible() || PlayerCustomizaton.IsBarberGuiVisible()
                || Chat.instance != null && Chat.instance.HasFocus()
                || Minimap.instance != null && Minimap.instance.m_mode == Minimap.MapMode.Large) return false;
            return AdministrationInputRequests != null && (int)AdministrationInputRequests.GetValue(null) == 0;
        }

        private void UpdateAdministration()
        {
            if (administration == null || administrationWindow == null) return;
            try
            {
                ZNet current = ZNet.instance; long worldId = AdministrationWorldId();
                if (!ReferenceEquals(current, administrationNetwork) || worldId != administrationWorld)
                {
                    administration.SetContext(0, false); administrationWindow.Hide(); ResetAdminInput();
                    administrationNetwork = current; administrationWorld = worldId; administrationError = "";
                }
                administration.SetContext(worldId, CanAdministerCharacters());
                administrationWindow.Tick(); PollAdminShortcut(); ProcessAdminPendingOpen();
            }
            catch (Exception error) { administrationWindow.Hide(); ResetAdminInput(); ReportAdministration(error); }
        }

        private string AdministrationShortcutLabel()
        {
            KeyboardShortcut key = administrationShortcut.Value;
            if (key.MainKey == KeyCode.None) return AdminT("не назначено", "unbound");
            return key.ToString().Replace("LeftControl", "Ctrl").Replace("RightControl", "Ctrl")
                .Replace("LeftShift", "Shift").Replace("RightShift", "Shift")
                .Replace("LeftAlt", "Alt").Replace("RightAlt", "Alt");
        }

        private static string AdministrationItemName(int hash)
        {
            GameObject prefab = ObjectDB.instance == null ? null : ObjectDB.instance.GetItemPrefab(hash);
            ItemDrop item = prefab == null ? null : prefab.GetComponent<ItemDrop>();
            if (item == null || item.m_itemData == null || item.m_itemData.m_shared == null) return GameState.ItemName(hash);
            string name = item.m_itemData.m_shared.m_name;
            return Localization.instance == null ? name : Localization.instance.Localize(name);
        }

        private static Sprite AdministrationItemIcon(int hash)
        {
            GameObject prefab = ObjectDB.instance == null ? null : ObjectDB.instance.GetItemPrefab(hash);
            ItemDrop item = prefab == null ? null : prefab.GetComponent<ItemDrop>();
            if (item == null || item.m_itemData == null || item.m_itemData.m_shared == null
                || item.m_itemData.m_shared.m_icons == null || item.m_itemData.m_shared.m_icons.Length == 0) return null;
            return item.m_itemData.m_shared.m_icons[0];
        }

        private string AdministrationStatus()
        {
            return AdminT("Защита персонажа: ", "Character protection: ") + (Managed ? AdminT("включена", "enabled") : AdminT("выключена", "disabled"))
                + "\n" + AdminT("Персонаж загружен: ", "Character loaded: ") + (ready ? AdminT("да", "yes") : AdminT("нет", "no"))
                + "\n" + AdminT("Ошибка сохранения: ", "Save failure: ") + (failed ? AdminT("да", "yes") : AdminT("нет", "no"))
                + "\n" + AdminT("Снимков до записи: ", "Snapshots awaiting commit: ") + Math.Max(0, localHost == null ? produced - acknowledged : localHost.AcceptedSequence - localHost.LastSequence).ToString(CultureInfo.InvariantCulture)
                + "\n" + AdminT("Подготовка снимка: ", "Snapshot capture: ") + lastCaptureMs.ToString("F2", CultureInfo.InvariantCulture) + " ms"
                + "\n" + AdminT("Фоновая запись: ", "Background write: ") + lastWriteMs.ToString("F2", CultureInfo.InvariantCulture) + " ms"
                + "\n" + AdminT("Очередь записи: ", "Write queue: ") + (writer == null ? "0" : writer.PendingCount.ToString(CultureInfo.InvariantCulture))
                + " / " + (writer == null ? "0" : writer.PendingBytes.ToString(CultureInfo.InvariantCulture)) + " bytes"
                + "\n" + AdminT("Последний сетевой пакет: ", "Last network packet: ") + lastPacketBytes.ToString(CultureInfo.InvariantCulture) + " bytes"
                + (administrationError.Length == 0 ? "" : "\n" + administrationError);
        }

        private void ReportAdministration(Exception error)
        {
            administrationError = AdminT("Ошибка интерфейса: ", "Interface error: ") + error.Message;
            if (Time.realtimeSinceStartup >= administrationErrorAt)
            { administrationErrorAt = Time.realtimeSinceStartup + 10; Logger.LogWarning("World Characters interface: " + error); }
        }

        private void ResetAdministration()
        {
            if (administration != null) administration.SetContext(0, false);
            if (administrationWindow != null) administrationWindow.Hide();
            ResetAdminInput(); administrationNetwork = null; administrationWorld = 0; administrationError = "";
        }

        private void DisposeAdministration()
        {
            if (administrationWindow != null) administrationWindow.Hide(); DisposeAdminInput();
            if (administration != null) { administration.Dispose(); administration = null; }
            administrationWindow = null;
        }

        [HarmonyPatch(typeof(GUIManager), "ResetInputBlock")]
        private static class AdministrationInputResetPatch
        { private static void Postfix() { if (Instance != null && Instance.administrationWindow != null) Instance.administrationWindow.HandleInputReset(); } }
    }
}
