using System;
using System.Collections.Generic;
using System.Globalization;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ValheimModPack.InventoryAdmin
{
    public sealed class AdminWindow
    {
        private const int PlayerPageSize = 6, ItemPageSize = 6;
        private readonly AdminUiBindings bindings;
        private readonly AdminInputLease inputLease = new AdminInputLease(GUIManager.BlockInput);
        private readonly List<AdminPlayerView> players = new List<AdminPlayerView>();
        private readonly Button[] playerRows = new Button[PlayerPageSize], itemRows = new Button[ItemPageSize];
        private readonly Image[] icons = new Image[ItemPageSize];
        private readonly List<Selectable> controls = new List<Selectable>();
        private GameObject overlay, panel, confirmation, dragGhost, groupRadiusModal, groupRadiusPanel;
        private Player localPlayer;
        private ZNet network;
        private AdminInventoryView snapshot;
        private InputField quantity;
        private Toggle showPlayersOnMap;
        private Text mapTrackingLabel;
        private Text heading, subtitle, playerHeading, itemsHeading, playerPages, itemPages, selection, details, status, receiverText, roleHint, quantityHeading;
        private Button playerPrev, playerNext, itemPrev, itemNext, take, delete, refresh, grant, revoke, findSelectedOnMap;
        private Button openGroupRadius;
        private readonly List<Selectable> groupRadiusControls = new List<Selectable>();
        private readonly Button[] groupRadiusPlayers = new Button[PlayerPageSize];
        private Button groupRadiusPrev, groupRadiusNext, groupRadiusApply, groupRadiusLeader, groupRadiusExemptionApply, groupRadiusReload, groupRadiusBack;
        private Toggle groupRadiusEnabled, groupRadiusExempt;
        private InputField groupRadiusValue;
        private Text groupRadiusHeading, groupRadiusSubtitle, groupRadiusPlayersHeading, groupRadiusPages, groupRadiusEnabledLabel,
            groupRadiusValueLabel, groupRadiusCurrent, groupRadiusSelected, groupRadiusExemptLabel, groupRadiusHint, groupRadiusStatus;
        private long groupRadiusRevision = -1;
        private bool groupRadiusSettingsDirty, groupRadiusExemptionDirty, groupRadiusPainting;
        private bool GroupRadiusHasDrafts { get { return groupRadiusSettingsDirty || groupRadiusExemptionDirty; } }
        private long selectedPeer;
        private string selectedItem, notice = "", dragSnapshot, dragItem;
        private int playerPage, itemPage, generation, dragQuantity;
        private bool busy;
        private float nextRefresh;
        public bool IsVisible { get { return overlay != null && overlay.activeInHierarchy; } }

        public AdminWindow(AdminUiBindings bindings)
        {
            if (bindings == null) throw new ArgumentNullException("bindings");
            this.bindings = bindings;
        }
        private string T(string ru, string en)
        { return bindings.Translate == null ? en : bindings.Translate(ru, en); }
        private void Report(Exception error) { if (bindings.Error != null) bindings.Error(error); }
        private bool Allowed() { return bindings.CanUse != null && bindings.CanUse(); }
        private bool Host() { return bindings.IsHost != null && bindings.IsHost(); }
        private long Self() { return bindings.LocalPeerId == null ? 0 : bindings.LocalPeerId(); }
        private static string Safe(string value, int max)
        {
            if (String.IsNullOrEmpty(value)) return "";
            var chars = new List<char>(Math.Min(value.Length, max));
            foreach (char c in value)
            {
                if (chars.Count == max) break;
                if (!Char.IsControl(c)) chars.Add(c);
            }
            return new string(chars.ToArray());
        }
        public void Show()
        {
            Hide();
            localPlayer = Player.m_localPlayer; network = ZNet.instance;
            if (!ValidContext() || GUIManager.CustomGUIFront == null) return;
            try
            {
                BuildVisuals(); inputLease.Acquire(); overlay.transform.SetAsLastSibling();
                if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(refresh.gameObject);
                if (bindings.RequestPlayers != null) bindings.RequestPlayers();
            }
            catch (Exception error) { Report(error); Hide(); }
        }
        public void ShowGroupRadius()
        {
            if (!IsVisible) Show();
            if (!IsVisible) return;
            try { OpenGroupRadius(); }
            catch (Exception error) { Report(error); SetStatus(T("Не удалось открыть настройки радиуса группы.", "Could not open group radius settings.")); }
        }
        // Called separately by the isolated menu probe, without a real player or an input lease.
        private void BuildVisuals()
        {
            Vector2 center = new Vector2(.5f, .5f);
            overlay = new GameObject("InventoryAdmin.Modal", typeof(RectTransform), typeof(Image));
            overlay.layer = GUIManager.UILayer; overlay.transform.SetParent(GUIManager.CustomGUIFront.transform, false);
            var overlayRect = overlay.GetComponent<RectTransform>();
            overlayRect.anchorMin = Vector2.zero; overlayRect.anchorMax = Vector2.one; overlayRect.offsetMin = overlayRect.offsetMax = Vector2.zero;
            overlay.GetComponent<Image>().color = new Color(0, 0, 0, .6f);
            panel = GUIManager.Instance.CreateWoodpanel(overlay.transform, center, center, Vector2.zero, 1130, 860, false);
            panel.name = "InventoryAdmin.WoodPanel";
            var group = panel.AddComponent<CanvasGroup>(); group.interactable = true; group.blocksRaycasts = true;
            heading = Label("", 0, 331, 975, 46, 29, true);
            ButtonAt("X", 512, 336, 42, 36, Hide);
            subtitle = Label("", 0, 284, 1040, 38, 16, false);
            playerHeading = Label("", -394, 237, 228, 32, 22, true);
            itemsHeading = Label("", -25, 237, 477, 34, 22, true);
            for (int i = 0; i < PlayerPageSize; i++)
            {
                int slot = i;
                playerRows[i] = ButtonAt("", -394, 187 - i * 56, 228, 48, () => SelectPlayer(slot));
            }
            playerPrev = ButtonAt("<", -480, -166, 42, 32, () => { if (playerPage > 0) playerPage--; Repaint(); });
            playerPages = Label("", -394, -166, 120, 32, 16, false);
            playerNext = ButtonAt(">", -307, -166, 42, 32, () => { playerPage++; Repaint(); });
            for (int i = 0; i < ItemPageSize; i++)
            {
                int slot = i;
                itemRows[i] = ButtonAt("", -25, 187 - i * 56, 477, 48, () => SelectItem(slot));
                Text text = itemRows[i].GetComponentInChildren<Text>(); text.alignment = TextAnchor.MiddleLeft;
                text.resizeTextMinSize = 13; text.resizeTextMaxSize = 16;
                text.rectTransform.anchorMin = Vector2.zero; text.rectTransform.anchorMax = Vector2.one;
                text.rectTransform.offsetMin = new Vector2(56, 1); text.rectTransform.offsetMax = new Vector2(-8, -1);
                var iconObject = new GameObject("InventoryAdmin.ItemIcon", typeof(RectTransform), typeof(Image));
                iconObject.layer = GUIManager.UILayer; iconObject.transform.SetParent(itemRows[i].transform, false);
                var iconRect = iconObject.GetComponent<RectTransform>(); iconRect.anchorMin = iconRect.anchorMax = new Vector2(0, .5f);
                iconRect.sizeDelta = new Vector2(40, 40); iconRect.anchoredPosition = new Vector2(27, 0);
                icons[i] = iconObject.GetComponent<Image>(); icons[i].preserveAspect = true; icons[i].raycastTarget = false;
                var drag = itemRows[i].gameObject.AddComponent<AdminItemDrag>(); drag.Configure(this, i, generation);
            }
            itemPrev = ButtonAt("<", -218, -166, 42, 32, () => { if (itemPage > 0) itemPage--; Repaint(); });
            itemPages = Label("", -25, -166, 312, 32, 16, false);
            itemNext = ButtonAt(">", 169, -166, 42, 32, () => { itemPage++; Repaint(); });
            take = ButtonAt("", 388, 165, 250, 136, TakeSelected);
            take.gameObject.name = "InventoryAdmin.TakeDestination";
            take.GetComponentInChildren<Text>().rectTransform.offsetMin = new Vector2(10, 6);
            take.GetComponentInChildren<Text>().rectTransform.offsetMax = new Vector2(-10, -6);
            var drop = take.gameObject.AddComponent<AdminItemDrop>(); drop.Configure(this, generation);
            receiverText = Label("", 388, 48, 250, 80, 16, false);
            quantityHeading = Label("", 315, -22, 113, 30, 17, false);
            quantity = GUIManager.Instance.CreateInputField(panel.transform, center, center, new Vector2(453, -22),
                InputField.ContentType.IntegerNumber, "1", 20, 114, 38).GetComponent<InputField>();
            quantity.characterLimit = 6; quantity.textComponent.supportRichText = false;
            var placeholder = quantity.placeholder as Text; if (placeholder != null) placeholder.supportRichText = false;
            quantity.text = "1"; controls.Add(quantity);
            int created = generation;
            quantity.onValueChanged.AddListener(value => { if (created == generation && IsVisible) UpdateActions(); });
            delete = ButtonAt("", 388, -83, 250, 44, ConfirmDelete);
            roleHint = Label("", 388, -148, 250, 48, 15, false);
            grant = ButtonAt("", -394, -224, 228, 42, () => ChangeRole(true));
            revoke = ButtonAt("", -394, -275, 228, 42, () => ChangeRole(false));
            selection = Label("", -25, -218, 477, 35, 17, false);
            details = Label("", -25, -268, 477, 62, 15, false);
            refresh = ButtonAt("", 388, -218, 250, 42, Refresh);
            openGroupRadius = ButtonAt("", 388, -275, 250, 42, OpenGroupRadius);
            openGroupRadius.gameObject.name = "InventoryAdmin.OpenGroupRadius";
            showPlayersOnMap = GUIManager.Instance.CreateToggle(panel.transform, 28, 28).GetComponent<Toggle>();
            showPlayersOnMap.gameObject.name = "InventoryAdmin.ShowPlayersOnMap";
            showPlayersOnMap.gameObject.layer = GUIManager.UILayer;
            var mapToggleRect = showPlayersOnMap.GetComponent<RectTransform>();
            mapToggleRect.anchorMin = mapToggleRect.anchorMax = center;
            mapToggleRect.pivot = center; mapToggleRect.sizeDelta = new Vector2(720, 44);
            mapToggleRect.anchoredPosition = new Vector2(-145, -332); mapToggleRect.localScale = Vector3.one;
            var mapToggleBackground = showPlayersOnMap.transform.Find("Background").GetComponent<RectTransform>();
            mapToggleBackground.anchorMin = mapToggleBackground.anchorMax = new Vector2(0, .5f);
            mapToggleBackground.pivot = center; mapToggleBackground.anchoredPosition = new Vector2(16, 0);
            mapToggleBackground.sizeDelta = new Vector2(28, 28);
            mapTrackingLabel = showPlayersOnMap.GetComponentInChildren<Text>();
            mapTrackingLabel.font = GUIManager.Instance.AveriaSerif;
            mapTrackingLabel.fontSize = 18; mapTrackingLabel.color = GUIManager.Instance.ValheimBeige;
            mapTrackingLabel.supportRichText = false; mapTrackingLabel.alignment = TextAnchor.MiddleLeft;
            mapTrackingLabel.horizontalOverflow = HorizontalWrapMode.Wrap; mapTrackingLabel.verticalOverflow = VerticalWrapMode.Truncate;
            mapTrackingLabel.resizeTextForBestFit = true; mapTrackingLabel.resizeTextMinSize = 13; mapTrackingLabel.resizeTextMaxSize = 18;
            mapTrackingLabel.rectTransform.anchorMin = Vector2.zero; mapTrackingLabel.rectTransform.anchorMax = Vector2.one;
            mapTrackingLabel.rectTransform.offsetMin = new Vector2(40, 1); mapTrackingLabel.rectTransform.offsetMax = new Vector2(-4, -1);
            showPlayersOnMap.onValueChanged.AddListener(value => Invoke(created, () => SetMapTracking(value), false));
            controls.Add(showPlayersOnMap);
            findSelectedOnMap = ButtonAt("", 388, -332, 250, 44, FindSelectedOnMap);
            findSelectedOnMap.gameObject.name = "InventoryAdmin.FindSelectedOnMap";
            status = Label("", 0, -390, 1030, 57, 16, false);
            Repaint(); Scale();
        }
        private bool ValidContext()
        {
            return Allowed() && localPlayer != null && ReferenceEquals(localPlayer, Player.m_localPlayer)
                && network != null && ReferenceEquals(network, ZNet.instance)
                && !localPlayer.IsDead() && !localPlayer.IsTeleporting() && !localPlayer.IsSleeping() && !localPlayer.InCutscene()
                && !ZInput.s_IsRebindActive && !UnifiedPopup.IsVisible() && !Menu.IsVisible() && !InventoryGui.IsVisible()
                && !StoreGui.IsVisible() && !Hud.IsPieceSelectionVisible() && !PlayerCustomizaton.IsBarberGuiVisible()
                && !global::Console.IsVisible() && (Chat.instance == null || !Chat.instance.HasFocus())
                && (TextInput.instance == null || TextInput.instance.m_panel == null || !TextInput.instance.m_panel.activeInHierarchy)
                && (Minimap.instance == null || Minimap.instance.m_mode != Minimap.MapMode.Large);
        }
        public void Tick()
        {
            if (overlay == null && !inputLease.Held) return;
            try
            {
                if (!IsVisible || !ValidContext()) { Hide(); return; }
                if (Input.GetKeyDown(KeyCode.Escape) || ZInput.GetButtonDown("JoyButtonB"))
                {
                    if (ZInput.GetButtonDown("JoyButtonB")) ZInput.ResetButtonStatus("JoyButtonB");
                    if (confirmation != null) CancelConfirmation(); else if (groupRadiusModal != null) BackFromGroupRadius(); else Hide();
                    return;
                }
                if (Time.unscaledTime >= nextRefresh) { nextRefresh = Time.unscaledTime + .25f; Repaint(); Scale(); }
            }
            catch (Exception error) { Report(error); Hide(); }
        }
        public void SetPlayers(IList<AdminPlayerView> online)
        {
            players.Clear();
            if (online != null) foreach (var player in online) if (player != null) players.Add(player);
            if (selectedPeer != 0 && SelectedPlayer() == null)
            {
                selectedPeer = 0; snapshot = null; selectedItem = null; CancelDrag(); CancelConfirmation();
                notice = T("Игрок отключился. Выберите другого игрока.", "The player disconnected. Select another player.");
            }
            Repaint();
        }
        public void SetSnapshot(AdminInventoryView view)
        {
            if (view == null || view.PeerId != selectedPeer || !IsVisible) return;
            string previousToken = snapshot == null ? null : snapshot.SnapshotToken;
            snapshot = view;
            if (!String.Equals(previousToken, view.SnapshotToken, StringComparison.Ordinal))
            { selectedItem = null; itemPage = 0; CancelDrag(); CancelConfirmation(); }
            if (SelectedItem() == null) selectedItem = null;
            Repaint();
        }
        public void SetStatus(string message) { notice = Safe(message, 350); Repaint(); }
        public void SetBusy(bool value)
        {
            busy = value; if (busy) { CancelDrag(); CancelConfirmation(); }
            Repaint();
        }
        public void HandleInputReset()
        {
            // Jotunn already discarded every request. Releasing a remembered
            // increment now could subtract a newly opened window's request.
            inputLease.ForgetAfterGlobalReset(); Hide();
        }
        private AdminPlayerView SelectedPlayer()
        { foreach (var player in players) if (player.PeerId == selectedPeer) return player; return null; }
        private AdminItemView SelectedItem()
        {
            if (snapshot == null || snapshot.PeerId != selectedPeer) return null;
            foreach (var item in snapshot.Items) if (item != null && String.Equals(item.ItemToken, selectedItem, StringComparison.Ordinal)) return item;
            return null;
        }
        private int Quantity(AdminItemView item)
        {
            int value;
            return item != null && quantity != null && Int32.TryParse(quantity.text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
                && value >= 1 && value <= item.Count ? value : 0;
        }
        private void SelectPlayer(int slot)
        {
            int index = playerPage * PlayerPageSize + slot;
            if (busy || index < 0 || index >= players.Count) return;
            selectedPeer = players[index].PeerId; snapshot = null; selectedItem = null; itemPage = 0;
            CancelDrag(); CancelConfirmation(); notice = T("Запрашиваю инвентарь…", "Requesting inventory…");
            Repaint(); if (bindings.RequestInventory != null) bindings.RequestInventory(selectedPeer);
        }
        private void SelectItem(int slot)
        {
            int index = itemPage * ItemPageSize + slot;
            if (busy || snapshot == null || index < 0 || index >= snapshot.Items.Count) return;
            selectedItem = snapshot.Items[index].ItemToken;
            quantity.text = Math.Max(1, snapshot.Items[index].Count).ToString(CultureInfo.InvariantCulture);
            CancelConfirmation(); Repaint();
        }
        private void Refresh()
        {
            if (busy) return;
            selectedItem = null; snapshot = null; CancelDrag(); CancelConfirmation();
            notice = T("Обновляю список игроков и инвентарь…", "Refreshing players and inventory…");
            Repaint(); if (bindings.RequestPlayers != null) bindings.RequestPlayers();
            if (selectedPeer != 0 && bindings.RequestInventory != null) bindings.RequestInventory(selectedPeer);
        }
        private void ChangeRole(bool value)
        {
            var player = SelectedPlayer();
            if (!Host() || busy || player == null || player.PeerId == Self() || player.IsAdmin == value) return;
            if (bindings.SetAdmin != null) bindings.SetAdmin(player.PeerId, value);
        }
        private void SetMapTracking(bool value)
        {
            if (busy || !Allowed() || bindings.SetTrackingPlayers == null) return;
            bindings.SetTrackingPlayers(value); Repaint();
        }
        private void FindSelectedOnMap()
        {
            var player = SelectedPlayer();
            if (busy || !Allowed() || player == null || bindings.FindPlayerOnMap == null
                || bindings.CanFindPlayerOnMap == null || !bindings.CanFindPlayerOnMap(player.PeerId)) return;
            long peerId = player.PeerId;
            // Release our input lease before Valheim opens its large map.
            Hide(); bindings.FindPlayerOnMap(peerId);
        }
        private void OpenGroupRadius()
        {
            if (groupRadiusModal != null || busy || !Allowed() || bindings.GetGroupRadius == null) return;
            CancelDrag(); CancelConfirmation();
            if (quantity != null) quantity.DeactivateInputField();
            var center = new Vector2(.5f, .5f);
            groupRadiusModal = new GameObject("InventoryAdmin.GroupRadiusModal", typeof(RectTransform), typeof(Image));
            groupRadiusModal.layer = GUIManager.UILayer; groupRadiusModal.transform.SetParent(overlay.transform, false);
            var rect = groupRadiusModal.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero;
            groupRadiusModal.GetComponent<Image>().color = new Color(0, 0, 0, .1f);
            groupRadiusPanel = GUIManager.Instance.CreateWoodpanel(groupRadiusModal.transform, center, center, Vector2.zero, 960, 750, false);
            groupRadiusPanel.name = "InventoryAdmin.GroupRadiusWoodPanel";
            groupRadiusHeading = GroupRadiusLabel("", 0, 302, 790, 42, 28, true);
            GroupRadiusButton("X", 424, 308, 42, 36, BackFromGroupRadius);
            groupRadiusSubtitle = GroupRadiusLabel("", 0, 258, 860, 40, 16, false);
            groupRadiusPlayersHeading = GroupRadiusLabel("", -306, 203, 246, 34, 22, true);
            for (int i = 0; i < PlayerPageSize; i++)
            {
                int slot = i;
                groupRadiusPlayers[i] = GroupRadiusButton("", -306, 153 - i * 54, 246, 46, () => SelectGroupRadiusPlayer(slot));
            }
            groupRadiusPrev = GroupRadiusButton("<", -405, -178, 42, 32, () => { if (playerPage > 0) playerPage--; RepaintGroupRadius(); });
            groupRadiusPages = GroupRadiusLabel("", -306, -178, 130, 32, 16, false);
            groupRadiusNext = GroupRadiusButton(">", -207, -178, 42, 32, () => { playerPage++; RepaintGroupRadius(); });
            groupRadiusEnabled = GroupRadiusToggle("InventoryAdmin.GroupRadiusEnabled", -127, 192, 550, out groupRadiusEnabledLabel);
            groupRadiusValueLabel = GroupRadiusLabel("", 4, 134, 286, 40, 18, false);
            groupRadiusValue = GUIManager.Instance.CreateInputField(groupRadiusPanel.transform, center, center, new Vector2(225, 134),
                InputField.ContentType.Standard, "500", 20, 154, 40).GetComponent<InputField>();
            groupRadiusValue.gameObject.name = "InventoryAdmin.GroupRadiusValue";
            groupRadiusValue.characterLimit = 16; groupRadiusValue.textComponent.supportRichText = false;
            var placeholder = groupRadiusValue.placeholder as Text; if (placeholder != null) placeholder.supportRichText = false;
            groupRadiusControls.Add(groupRadiusValue);
            int created = generation; GameObject modal = groupRadiusModal;
            groupRadiusEnabled.onValueChanged.AddListener(value => GroupRadiusDraftChanged(created, modal, true));
            groupRadiusExempt = GroupRadiusToggle("InventoryAdmin.GroupRadiusExempt", -127, -90, 550, out groupRadiusExemptLabel);
            groupRadiusExempt.onValueChanged.AddListener(value => GroupRadiusDraftChanged(created, modal, false));
            groupRadiusValue.onValueChanged.AddListener(value => GroupRadiusDraftChanged(created, modal, true));
            groupRadiusApply = GroupRadiusButton("", 159, 72, 510, 42, ApplyGroupRadiusSettings);
            groupRadiusCurrent = GroupRadiusLabel("", 159, 11, 550, 64, 17, false);
            groupRadiusSelected = GroupRadiusLabel("", 159, -48, 550, 38, 18, false);
            groupRadiusLeader = GroupRadiusButton("", 159, -149, 510, 42, ApplyGroupRadiusLeader);
            groupRadiusExemptionApply = GroupRadiusButton("", 159, -203, 510, 42, ApplyGroupRadiusExemption);
            groupRadiusHint = GroupRadiusLabel("", 0, -258, 858, 54, 15, false);
            groupRadiusReload = GroupRadiusButton("", -212, -310, 402, 42, ReloadGroupRadius);
            groupRadiusBack = GroupRadiusButton("", 212, -310, 402, 42, BackFromGroupRadius);
            groupRadiusStatus = GroupRadiusLabel("", 0, -353, 858, 40, 15, false);
            groupRadiusRevision = -1; groupRadiusSettingsDirty = groupRadiusExemptionDirty = false; notice = "";
            panel.SetActive(false); groupRadiusModal.transform.SetAsLastSibling();
            RepaintGroupRadius(); Scale();
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(groupRadiusBack.gameObject);
        }
        private void GroupRadiusDraftChanged(int created, GameObject modal, bool settings)
        {
            if (groupRadiusPainting || created != generation || !ReferenceEquals(modal, groupRadiusModal) || !IsVisible) return;
            if (settings) groupRadiusSettingsDirty = true; else groupRadiusExemptionDirty = true;
            RepaintGroupRadius();
        }
        private static bool ValidGroupRadiusView(GroupRadiusAdminView view)
        {
            return view != null && view.ExemptPeers != null && view.Revision >= 0 && !Single.IsNaN(view.Radius) && !Single.IsInfinity(view.Radius)
                && view.Radius >= 50 && view.Radius <= 10000;
        }
        private GroupRadiusAdminView GroupRadiusSnapshot()
        { return bindings.GetGroupRadius == null ? null : bindings.GetGroupRadius(); }
        private void LoadGroupRadiusDraft(GroupRadiusAdminView view)
        {
            if (!ValidGroupRadiusView(view)) return;
            groupRadiusPainting = true;
            try
            {
                groupRadiusRevision = view.Revision;
                groupRadiusEnabled.SetIsOnWithoutNotify(view.Enabled);
                groupRadiusValue.text = view.Radius.ToString("0.###", CultureInfo.InvariantCulture);
                groupRadiusExempt.SetIsOnWithoutNotify(selectedPeer != 0 && view.ExemptPeers.Contains(selectedPeer));
                groupRadiusSettingsDirty = groupRadiusExemptionDirty = false;
            }
            finally { groupRadiusPainting = false; }
        }
        private void ReloadGroupRadius()
        {
            if (busy || !Allowed()) return;
            var view = GroupRadiusSnapshot();
            if (!ValidGroupRadiusView(view))
            { notice = T("Настройки ещё не получены. Дождитесь ответа сервера.", "Settings have not arrived yet. Wait for the server response."); }
            else { LoadGroupRadiusDraft(view); notice = T("Загружены текущие настройки сервера.", "Current server settings loaded."); }
            if (bindings.RequestPlayers != null) bindings.RequestPlayers();
            RepaintGroupRadius();
        }
        private void SelectGroupRadiusPlayer(int slot)
        {
            int index = playerPage * PlayerPageSize + slot;
            if (busy || !Allowed() || index < 0 || index >= players.Count) return;
            long peer = players[index].PeerId;
            if (peer != selectedPeer)
            {
                selectedPeer = peer; snapshot = null; selectedItem = null; itemPage = 0;
                var view = GroupRadiusSnapshot();
                groupRadiusExempt.SetIsOnWithoutNotify(ValidGroupRadiusView(view) && view.ExemptPeers.Contains(peer));
                groupRadiusExemptionDirty = false;
            }
            RepaintGroupRadius();
        }
        private bool CanSubmitGroupRadius(out GroupRadiusAdminView view)
        {
            view = GroupRadiusSnapshot();
            if (groupRadiusModal == null || busy || !Allowed()) return false;
            if (!ValidGroupRadiusView(view))
            { notice = T("Дождитесь актуальных настроек сервера.", "Wait for current server settings."); RepaintGroupRadius(); return false; }
            if (view.ReadOnly)
            { notice = String.IsNullOrEmpty(view.Notice) ? T("Настройки доступны только для чтения.", "Settings are read-only.") : Safe(view.Notice, 350); RepaintGroupRadius(); return false; }
            if (view.Revision != groupRadiusRevision)
            { notice = T("Настройки изменились. Нажмите «Обновить», затем повторите действие.", "Settings changed. Press Refresh, then try the action again."); RepaintGroupRadius(); return false; }
            return true;
        }
        private void ApplyGroupRadiusSettings()
        {
            GroupRadiusAdminView view;
            if (bindings.UpdateGroupRadius == null || !CanSubmitGroupRadius(out view)) return;
            float radius;
            if (!Single.TryParse(groupRadiusValue.text, NumberStyles.Float, CultureInfo.InvariantCulture, out radius)
                || Single.IsNaN(radius) || Single.IsInfinity(radius) || radius < 50 || radius > 10000)
            {
                notice = T("Введите радиус от 50 до 10000 м. Десятичный разделитель — точка.", "Enter a radius from 50 to 10000 m. Use a dot for decimals.");
                RepaintGroupRadius(); return;
            }
            bindings.UpdateGroupRadius(groupRadiusRevision, groupRadiusEnabled.isOn, radius);
            groupRadiusSettingsDirty = false; RepaintGroupRadius();
        }
        private void ApplyGroupRadiusLeader()
        {
            GroupRadiusAdminView view;
            if (bindings.SetGroupRadiusLeader == null || !CanSubmitGroupRadius(out view)) return;
            var player = SelectedPlayer(); if (player == null) return;
            bindings.SetGroupRadiusLeader(groupRadiusRevision, player.PeerId);
            RepaintGroupRadius();
        }
        private void ApplyGroupRadiusExemption()
        {
            GroupRadiusAdminView view;
            if (bindings.SetGroupRadiusExemption == null || !CanSubmitGroupRadius(out view)) return;
            var player = SelectedPlayer(); if (player == null) return;
            bindings.SetGroupRadiusExemption(groupRadiusRevision, player.PeerId, groupRadiusExempt.isOn);
            groupRadiusExemptionDirty = false; RepaintGroupRadius();
        }
        private void RepaintGroupRadius()
        {
            if (groupRadiusModal == null || groupRadiusHeading == null) return;
            var view = GroupRadiusSnapshot();
            bool valid = ValidGroupRadiusView(view);
            if (valid && !GroupRadiusHasDrafts && view.Revision != groupRadiusRevision) LoadGroupRadiusDraft(view);
            bool stale = valid && view.Revision != groupRadiusRevision;
            bool can = !busy && Allowed() && valid && !stale && !view.ReadOnly;
            string shortcut = bindings.GroupRadiusShortcutLabel == null ? "" : bindings.GroupRadiusShortcutLabel();
            groupRadiusHeading.text = T("Радиус группы", "Group radius");
            groupRadiusSubtitle.text = T("Настройки действуют для всего сервера. Открыть: ", "Settings apply to the whole server. Open: ")
                + (String.IsNullOrEmpty(shortcut) ? T("Клавиша не назначена", "Unbound") : shortcut);
            groupRadiusPlayersHeading.text = T("Игроки онлайн", "Online players");
            int pages = Math.Max(1, (players.Count + PlayerPageSize - 1) / PlayerPageSize);
            playerPage = Math.Max(0, Math.Min(playerPage, pages - 1)); groupRadiusPages.text = (playerPage + 1) + " / " + pages;
            for (int i = 0; i < PlayerPageSize; i++)
            {
                int index = playerPage * PlayerPageSize + i;
                var player = index < players.Count ? players[index] : null;
                groupRadiusPlayers[i].interactable = !busy && Allowed() && player != null;
                groupRadiusPlayers[i].GetComponentInChildren<Text>().text = player == null ? "" : (player.PeerId == selectedPeer ? "› " : "")
                    + Safe(player.Name, 48) + (player.PeerId == Self() ? T(" (вы)", " (you)") : "")
                    + (valid && player.PeerId == view.LeaderPeerId ? T("\nЦентральный игрок", "\nCentral player") : "");
            }
            groupRadiusPrev.interactable = !busy && Allowed() && playerPage > 0;
            groupRadiusNext.interactable = !busy && Allowed() && playerPage + 1 < pages;
            groupRadiusEnabledLabel.text = T("Ограничивать удаление от центрального игрока", "Limit distance from the central player");
            groupRadiusValueLabel.text = T("Радиус, м (50–10000)", "Radius, m (50–10000)");
            groupRadiusEnabled.interactable = groupRadiusValue.interactable = can;
            groupRadiusApply.GetComponentInChildren<Text>().text = T("Применить режим и радиус", "Apply mode and radius");
            groupRadiusApply.interactable = can && bindings.UpdateGroupRadius != null;
            bool leaderOnline = false;
            if (valid) foreach (var player in players) if (player.PeerId == view.LeaderPeerId) { leaderOnline = true; break; }
            groupRadiusCurrent.text = !valid ? T("Ожидаю настройки сервера…", "Waiting for server settings…")
                : T("Сейчас: ", "Current: ") + (view.Enabled ? T("включено", "enabled") : T("выключено", "disabled")) + " • "
                    + view.Radius.ToString("0.###", CultureInfo.InvariantCulture) + T(" м\nЦентральный игрок: ", " m\nCentral player: ")
                    + (view.LeaderPeerId == 0 && String.IsNullOrEmpty(view.LeaderName) ? T("не выбран", "not selected") : Safe(view.LeaderName, 48)
                        + (leaderOnline ? "" : T(" (не в сети)", " (offline)")));
            var selected = SelectedPlayer();
            groupRadiusSelected.text = selected == null ? T("Выберите игрока слева.", "Select a player on the left.")
                : T("Выбран: ", "Selected: ") + Safe(selected.Name, 48);
            groupRadiusExemptLabel.text = T("Личное исключение из ограничения", "Personal exemption from the limit");
            groupRadiusExempt.interactable = can && selected != null;
            groupRadiusLeader.GetComponentInChildren<Text>().text = T("Сделать выбранного игрока центральным", "Make selected player the center");
            groupRadiusLeader.interactable = can && selected != null && bindings.SetGroupRadiusLeader != null && selected.PeerId != view.LeaderPeerId;
            groupRadiusExemptionApply.GetComponentInChildren<Text>().text = T("Применить исключение выбранного игрока", "Apply selected player's exemption");
            groupRadiusExemptionApply.interactable = can && selected != null && bindings.SetGroupRadiusExemption != null
                && groupRadiusExempt.isOn != view.ExemptPeers.Contains(selected.PeerId);
            groupRadiusHint.text = T("Центральный игрок автоматически исключён. Если он недоступен, ограничение приостанавливается. Изменения сохраняются после применения.",
                "The central player is automatically exempt. The limit pauses when that player is unavailable. Changes are saved after Apply.");
            groupRadiusReload.GetComponentInChildren<Text>().text = T("Обновить настройки и игроков", "Refresh settings and players");
            groupRadiusReload.interactable = !busy && Allowed();
            groupRadiusBack.GetComponentInChildren<Text>().text = T("Назад к инвентарям", "Back to inventories");
            groupRadiusStatus.text = busy ? T("Ожидаю результат сервера…", "Waiting for the server result…")
                : !Allowed() ? T("Доступ администратора отозван.", "Administrator access was revoked.")
                : valid && view.ReadOnly ? (String.IsNullOrEmpty(view.Notice) ? T("Настройки доступны только для чтения.", "Settings are read-only.") : Safe(view.Notice, 350))
                : stale ? T("Настройки изменились. Обновите их перед применением.", "Settings changed. Refresh them before applying.") : notice;
            SetNavigation(groupRadiusControls);
        }
        private Text GroupRadiusLabel(string value, float x, float y, float width, float height, int size, bool headingStyle)
        {
            var original = panel; panel = groupRadiusPanel;
            try { return Label(value, x, y, width, height, size, headingStyle); }
            finally { panel = original; }
        }
        private Button GroupRadiusButton(string caption, float x, float y, float width, float height, Action action)
        {
            var center = new Vector2(.5f, .5f); int created = generation; GameObject modal = groupRadiusModal;
            var button = GUIManager.Instance.CreateButton(caption, groupRadiusPanel.transform, center, center, new Vector2(x, y), width, height).GetComponent<Button>();
            Style(button); groupRadiusControls.Add(button);
            button.onClick.AddListener(() => InvokeGroupRadius(created, modal, action)); return button;
        }
        private Toggle GroupRadiusToggle(string name, float x, float y, float width, out Text label)
        {
            var center = new Vector2(.5f, .5f);
            var toggle = GUIManager.Instance.CreateToggle(groupRadiusPanel.transform, 28, 28).GetComponent<Toggle>();
            toggle.gameObject.name = name; toggle.gameObject.layer = GUIManager.UILayer;
            var rect = toggle.GetComponent<RectTransform>(); rect.anchorMin = rect.anchorMax = center; rect.pivot = new Vector2(0, .5f);
            rect.sizeDelta = new Vector2(width, 42); rect.anchoredPosition = new Vector2(x, y); rect.localScale = Vector3.one;
            var background = toggle.transform.Find("Background").GetComponent<RectTransform>();
            background.anchorMin = background.anchorMax = new Vector2(0, .5f); background.pivot = center;
            background.anchoredPosition = new Vector2(16, 0); background.sizeDelta = new Vector2(28, 28);
            label = toggle.GetComponentInChildren<Text>(); label.font = GUIManager.Instance.AveriaSerif;
            label.fontSize = 18; label.color = GUIManager.Instance.ValheimBeige; label.supportRichText = false;
            label.alignment = TextAnchor.MiddleLeft; label.horizontalOverflow = HorizontalWrapMode.Wrap; label.verticalOverflow = VerticalWrapMode.Truncate;
            label.resizeTextForBestFit = true; label.resizeTextMinSize = 13; label.resizeTextMaxSize = 18;
            label.rectTransform.anchorMin = Vector2.zero; label.rectTransform.anchorMax = Vector2.one;
            label.rectTransform.offsetMin = new Vector2(40, 1); label.rectTransform.offsetMax = new Vector2(-4, -1);
            groupRadiusControls.Add(toggle); return toggle;
        }
        private void InvokeGroupRadius(int created, GameObject modal, Action action)
        {
            if (created != generation || !IsVisible || !ReferenceEquals(modal, groupRadiusModal) || modal == null || !modal.activeInHierarchy) return;
            try { if (!ValidContext()) { Hide(); return; } action(); }
            catch (Exception error)
            {
                Report(error); notice = T("Не удалось применить настройки радиуса группы. ", "Could not apply group radius settings. ") + Safe(error.Message, 160);
                RepaintGroupRadius();
            }
        }
        private static void SetNavigation(IList<Selectable> source)
        {
            var active = new List<Selectable>();
            foreach (var control in source) if (control != null && control.gameObject.activeInHierarchy && control.IsInteractable()) active.Add(control);
            for (int i = 0; i < active.Count; i++) active[i].navigation = new Navigation
            {
                mode = Navigation.Mode.Explicit, selectOnUp = active[(i + active.Count - 1) % active.Count], selectOnLeft = active[(i + active.Count - 1) % active.Count],
                selectOnDown = active[(i + 1) % active.Count], selectOnRight = active[(i + 1) % active.Count]
            };
        }
        private void BackFromGroupRadius()
        {
            if (groupRadiusModal == null) return;
            CloseGroupRadiusVisuals(); panel.SetActive(true); notice = ""; Repaint();
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(openGroupRadius.gameObject);
            if (!busy && Allowed() && selectedPeer != 0 && snapshot == null && bindings.RequestInventory != null) bindings.RequestInventory(selectedPeer);
        }
        private void CloseGroupRadiusVisuals()
        {
            if (groupRadiusValue != null) groupRadiusValue.DeactivateInputField();
            if (groupRadiusModal != null)
            {
                if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null
                    && EventSystem.current.currentSelectedGameObject.transform.IsChildOf(groupRadiusModal.transform)) EventSystem.current.SetSelectedGameObject(null);
                groupRadiusModal.SetActive(false); UnityEngine.Object.Destroy(groupRadiusModal);
            }
            groupRadiusModal = groupRadiusPanel = null; groupRadiusEnabled = groupRadiusExempt = null; groupRadiusValue = null;
            groupRadiusHeading = groupRadiusSubtitle = groupRadiusPlayersHeading = groupRadiusPages = groupRadiusEnabledLabel = groupRadiusValueLabel
                = groupRadiusCurrent = groupRadiusSelected = groupRadiusExemptLabel = groupRadiusHint = groupRadiusStatus = null;
            groupRadiusPrev = groupRadiusNext = groupRadiusApply = groupRadiusLeader = groupRadiusExemptionApply = groupRadiusReload = groupRadiusBack = null;
            groupRadiusControls.Clear(); Array.Clear(groupRadiusPlayers, 0, groupRadiusPlayers.Length);
            groupRadiusRevision = -1; groupRadiusSettingsDirty = groupRadiusExemptionDirty = groupRadiusPainting = false;
        }
        private void TakeSelected()
        {
            var item = SelectedItem(); int count = Quantity(item);
            if (busy || selectedPeer == Self() || item == null || count == 0 || bindings.Take == null) return;
            bindings.Take(snapshot.SnapshotToken, item.ItemToken, count);
        }
        private void ConfirmDelete()
        {
            var item = SelectedItem(); int count = Quantity(item);
            if (busy || item == null || count == 0 || bindings.Delete == null || confirmation != null) return;
            string snapshotToken = snapshot.SnapshotToken, itemToken = item.ItemToken;
            long peer = selectedPeer;
            var center = new Vector2(.5f, .5f);
            confirmation = new GameObject("InventoryAdmin.DeleteConfirmation", typeof(RectTransform), typeof(Image));
            confirmation.layer = GUIManager.UILayer; confirmation.transform.SetParent(overlay.transform, false);
            var rect = confirmation.GetComponent<RectTransform>(); rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero; confirmation.GetComponent<Image>().color = new Color(0, 0, 0, .65f);
            var wood = GUIManager.Instance.CreateWoodpanel(confirmation.transform, center, center, Vector2.zero, 650, 282, false);
            Text text = GUIManager.Instance.CreateText(T("Удалить предмет?", "Delete item?") + "\n\n" + Safe(item.Name, 80) + " × " + count
                + "\n" + T("У игрока: ", "Player: ") + Safe(snapshot.Name, 55) + "\n" + T("Предмет будет удалён безвозвратно.", "The item will be permanently removed."),
                wood.transform, center, center, new Vector2(0, 28), GUIManager.Instance.AveriaSerif, 20,
                GUIManager.Instance.ValheimBeige, true, Color.black, 590, 164, false).GetComponent<Text>();
            text.supportRichText = false; text.alignment = TextAnchor.MiddleCenter; text.raycastTarget = false;
            text.resizeTextForBestFit = true; text.resizeTextMinSize = 15; text.resizeTextMaxSize = 20;
            ConfirmButton(wood, T("Отмена", "Cancel"), -150, CancelConfirmation);
            ConfirmButton(wood, T("Удалить", "Delete"), 150, () =>
            {
                var current = SelectedItem();
                if (busy || snapshot == null || selectedPeer != peer || current == null || current.ItemToken != itemToken
                    || snapshot.SnapshotToken != snapshotToken || current.Count < count) { CancelConfirmation(); return; }
                CancelConfirmation(); bindings.Delete(snapshotToken, itemToken, count);
            });
            confirmation.transform.SetAsLastSibling();
        }
        private void ConfirmButton(GameObject wood, string caption, float x, Action action)
        {
            int created = generation; GameObject dialog = confirmation;
            var button = GUIManager.Instance.CreateButton(caption, wood.transform, new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(x, -90), 246, 42).GetComponent<Button>();
            Style(button); button.onClick.AddListener(() =>
            {
                if (!ReferenceEquals(confirmation, dialog) || dialog == null || !dialog.activeInHierarchy) return;
                Invoke(created, action, true);
            });
        }
        private void CancelConfirmation()
        {
            if (confirmation == null) return;
            if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null
                && EventSystem.current.currentSelectedGameObject.transform.IsChildOf(confirmation.transform)) EventSystem.current.SetSelectedGameObject(null);
            confirmation.SetActive(false); UnityEngine.Object.Destroy(confirmation); confirmation = null;
        }
        internal void BeginDrag(int slot, int created, PointerEventData eventData)
        {
            if (created != generation || !IsVisible || !ValidContext() || busy || groupRadiusModal != null || confirmation != null || selectedPeer == Self()) return;
            int index = itemPage * ItemPageSize + slot;
            if (snapshot == null || index < 0 || index >= snapshot.Items.Count || snapshot.Items[index] == null) return;
            if (selectedItem != snapshot.Items[index].ItemToken) SelectItem(slot);
            var item = SelectedItem(); int count = Quantity(item);
            if (item == null || count == 0 || bindings.Take == null) return;
            CancelDrag(); dragSnapshot = snapshot.SnapshotToken; dragItem = item.ItemToken; dragQuantity = count;
            dragGhost = new GameObject("InventoryAdmin.DraggedItem", typeof(RectTransform), typeof(Image));
            dragGhost.layer = GUIManager.UILayer; dragGhost.transform.SetParent(overlay.transform, false);
            var rect = dragGhost.GetComponent<RectTransform>(); rect.sizeDelta = new Vector2(48, 48);
            var image = dragGhost.GetComponent<Image>(); image.sprite = item.Icon; image.preserveAspect = true; image.raycastTarget = false;
            image.color = new Color(1, 1, 1, .85f); MoveDrag(created, eventData);
        }
        internal void MoveDrag(int created, PointerEventData eventData)
        {
            if (created != generation || dragGhost == null || eventData == null) return;
            Vector2 local;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(overlay.GetComponent<RectTransform>(), eventData.position, eventData.pressEventCamera, out local))
                dragGhost.GetComponent<RectTransform>().anchoredPosition = local;
        }
        internal void Drop(int created)
        {
            if (created != generation || !IsVisible || !ValidContext() || busy || groupRadiusModal != null || confirmation != null || dragGhost == null) return;
            var item = SelectedItem();
            if (snapshot == null || item == null || snapshot.SnapshotToken != dragSnapshot || item.ItemToken != dragItem
                || dragQuantity < 1 || dragQuantity > item.Count || selectedPeer == Self() || bindings.Take == null) { CancelDrag(); return; }
            string version = dragSnapshot, token = dragItem; int count = dragQuantity; CancelDrag();
            try { bindings.Take(version, token, count); } catch (Exception error) { Report(error); Hide(); }
        }
        internal void EndDrag(int created) { if (created == generation) CancelDrag(); }
        private void CancelDrag()
        {
            if (dragGhost != null) { dragGhost.SetActive(false); UnityEngine.Object.Destroy(dragGhost); }
            dragGhost = null; dragSnapshot = dragItem = null; dragQuantity = 0;
        }
        private void UpdateActions()
        {
            if (take == null) return;
            var item = SelectedItem(); bool can = !busy && Allowed() && item != null && Quantity(item) > 0;
            take.interactable = can && selectedPeer != Self() && bindings.Take != null;
            delete.interactable = can && bindings.Delete != null;
            quantity.interactable = !busy && item != null;
        }
        private void Repaint()
        {
            if (heading == null) return;
            if (groupRadiusModal != null) { RepaintGroupRadius(); return; }
            string shortcut = bindings.ShortcutLabel == null ? "" : bindings.ShortcutLabel();
            heading.text = T("Инвентари игроков", "Player inventories");
            subtitle.text = T("Открыть / закрыть: ", "Open / close: ") + (String.IsNullOrEmpty(shortcut) ? T("Клавиша не назначена", "Unbound") : shortcut)
                + T("  •  Только игроки онлайн. Рюкзаки показываются отдельно, если доступны.", "  •  Online players only. Backpack contents appear separately when supported.");
            playerHeading.text = T("Игроки онлайн", "Online players");
            var selected = SelectedPlayer();
            itemsHeading.text = selected == null ? T("Выберите игрока", "Select a player") : T("Инвентарь: ", "Inventory: ") + Safe(selected.Name, 38);
            int playerCount = Math.Max(1, (players.Count + PlayerPageSize - 1) / PlayerPageSize);
            playerPage = Math.Max(0, Math.Min(playerPage, playerCount - 1));
            playerPages.text = (playerPage + 1) + " / " + playerCount;
            for (int i = 0; i < PlayerPageSize; i++)
            {
                int index = playerPage * PlayerPageSize + i; bool available = index < players.Count;
                playerRows[i].interactable = !busy && available;
                var p = available ? players[index] : null;
                playerRows[i].GetComponentInChildren<Text>().text = p == null ? "" : (p.PeerId == selectedPeer ? "› " : "") + Safe(p.Name, 48)
                    + (p.PeerId == Self() ? T(" (вы)", " (you)") : "") + (Host() && p.IsAdmin ? T("\nАдминистратор", "\nAdministrator") : "");
            }
            playerPrev.interactable = !busy && playerPage > 0; playerNext.interactable = !busy && playerPage + 1 < playerCount;
            int itemCount = snapshot == null ? 0 : snapshot.Items.Count;
            int pages = Math.Max(1, (itemCount + ItemPageSize - 1) / ItemPageSize);
            itemPage = Math.Max(0, Math.Min(itemPage, pages - 1)); itemPages.text = (itemPage + 1) + " / " + pages + T("  •  Предметов: ", "  •  Entries: ") + itemCount;
            for (int i = 0; i < ItemPageSize; i++)
            {
                int index = itemPage * ItemPageSize + i; bool available = snapshot != null && index < snapshot.Items.Count;
                var item = available ? snapshot.Items[index] : null;
                itemRows[i].interactable = !busy && item != null;
                icons[i].sprite = item == null ? null : item.Icon; icons[i].enabled = item != null && item.Icon != null;
                itemRows[i].GetComponentInChildren<Text>().text = item == null ? "" : (item.ItemToken == selectedItem ? "› " : "")
                    + Safe(item.Name, 66) + " × " + item.Count + "\n" + Group(item.Group) + " [" + (item.SlotX + 1) + "," + (item.SlotY + 1) + "]"
                    + T(" • качество ", " • quality ") + item.Quality + (item.Equipped ? T(" • надето", " • equipped") : "");
            }
            itemPrev.interactable = !busy && itemPage > 0; itemNext.interactable = !busy && itemPage + 1 < pages;
            take.GetComponentInChildren<Text>().text = T("В мой инвентарь\n\nВзять / перетащить сюда", "Into my inventory\n\nTake / drag here");
            receiverText.text = T("Выберите предмет и количество. Можно нажать кнопку или перетащить предмет на неё.", "Select an item and quantity. Use the button or drag the item onto it.");
            quantityHeading.text = T("Количество", "Quantity");
            delete.GetComponentInChildren<Text>().text = T("Удалить предмет…", "Delete item…");
            refresh.GetComponentInChildren<Text>().text = T("Обновить", "Refresh"); refresh.interactable = !busy;
            openGroupRadius.GetComponentInChildren<Text>().text = T("Радиус группы", "Group radius");
            openGroupRadius.interactable = !busy && Allowed() && bindings.GetGroupRadius != null;
            grant.GetComponentInChildren<Text>().text = T("Назначить администратором", "Grant administrator");
            revoke.GetComponentInChildren<Text>().text = T("Снять администраторство", "Revoke administrator");
            grant.gameObject.SetActive(Host()); revoke.gameObject.SetActive(Host());
            grant.interactable = !busy && selected != null && selected.PeerId != Self() && !selected.IsAdmin;
            revoke.interactable = !busy && selected != null && selected.PeerId != Self() && selected.IsAdmin;
            roleHint.text = Host() ? T("Назначать администраторов может только хост.", "Only the host can assign administrators.")
                : T("Доступ администратора. Назначение доступно только хосту.", "Administrator access. Only the host can assign roles.");
            bool tracking = bindings.IsTrackingPlayers != null && bindings.IsTrackingPlayers();
            showPlayersOnMap.SetIsOnWithoutNotify(tracking);
            showPlayersOnMap.interactable = !busy && Allowed() && bindings.SetTrackingPlayers != null;
            string mapShortcut = bindings.TrackingShortcutLabel == null ? "" : bindings.TrackingShortcutLabel();
            mapTrackingLabel.text = T("Показывать игроков на карте, включая скрытых", "Show players on the map, including hidden players")
                + (String.IsNullOrEmpty(mapShortcut) ? "" : "  (" + mapShortcut + ")");
            findSelectedOnMap.GetComponentInChildren<Text>().text = T("Найти игрока на карте", "Find player on map");
            findSelectedOnMap.interactable = !busy && Allowed() && selected != null && bindings.FindPlayerOnMap != null
                && bindings.CanFindPlayerOnMap != null && bindings.CanFindPlayerOnMap(selected.PeerId);
            var current = SelectedItem();
            selection.text = current == null ? T("Выберите предмет в инвентаре игрока.", "Select an item in the player's inventory.") : Safe(current.Name, 70);
            details.text = current == null ? "" : Safe(current.Prefab, 65) + " • " + Group(current.Group) + " • " + T("Количество: ", "Quantity: ") + current.Count
                + (current.MaxDurability > 0 ? T(" • прочность: ", " • durability: ") + current.Durability.ToString("0.0", CultureInfo.InvariantCulture) + "/" + current.MaxDurability.ToString("0.0", CultureInfo.InvariantCulture) : "")
                + (current.Equipped ? T("\nНадетый предмет будет снят перед удалением или переносом.", "\nAn equipped item will be unequipped before removal or transfer.") : "");
            status.text = busy ? T("Операция выполняется. Не закрывайте игру и дождитесь результата.", "Operation in progress. Keep the game open and wait for the result.") : notice;
            UpdateActions();
            var active = new List<Selectable>();
            foreach (var control in controls) if (control != null && control.gameObject.activeInHierarchy && control.IsInteractable()) active.Add(control);
            for (int i = 0; i < active.Count; i++)
                active[i].navigation = new Navigation { mode = Navigation.Mode.Explicit, selectOnUp = active[(i + active.Count - 1) % active.Count],
                    selectOnLeft = active[(i + active.Count - 1) % active.Count], selectOnDown = active[(i + 1) % active.Count], selectOnRight = active[(i + 1) % active.Count] };
        }
        private string Group(string name)
        {
            if ((name ?? "").StartsWith("backpack:", StringComparison.OrdinalIgnoreCase))
                return T("Рюкзак: ", "Backpack: ") + Safe(name.Substring(9), 28);
            switch ((name ?? "").ToLowerInvariant())
            {
                case "main": case "inventory": return T("Основной инвентарь", "Main inventory");
                case "equipment": return T("Экипировка", "Equipment");
                case "quick": case "quickslots": return T("Быстрые слоты", "Quick slots");
                case "backpack": return T("Рюкзак", "Backpack");
                default: return Safe(name, 36);
            }
        }
        private Text Label(string value, float x, float y, float width, float height, int size, bool headingStyle)
        {
            var gui = GUIManager.Instance; var center = new Vector2(.5f, .5f);
            Text text = gui.CreateText(value, panel.transform, center, center, new Vector2(x, y), headingStyle ? gui.AveriaSerifBold : gui.AveriaSerif,
                size, headingStyle ? gui.ValheimOrange : gui.ValheimBeige, true, Color.black, width, height, false).GetComponent<Text>();
            text.supportRichText = false; text.raycastTarget = false; text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Truncate;
            text.resizeTextForBestFit = true; text.resizeTextMinSize = 13; text.resizeTextMaxSize = size; return text;
        }
        private Button ButtonAt(string caption, float x, float y, float width, float height, Action action)
        {
            var center = new Vector2(.5f, .5f); int created = generation;
            var button = GUIManager.Instance.CreateButton(caption, panel.transform, center, center, new Vector2(x, y), width, height).GetComponent<Button>();
            Style(button); button.onClick.AddListener(() => Invoke(created, action, false)); controls.Add(button); return button;
        }
        private static void Style(Button button)
        {
            Text text = button.GetComponentInChildren<Text>(); text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Truncate;
            text.resizeTextForBestFit = true; text.resizeTextMinSize = 13; text.resizeTextMaxSize = 18;
            var sound = button.GetComponent<ButtonSfx>(); if (sound != null) sound.m_selectSfxPrefab = null;
        }
        private void Invoke(int created, Action action, bool isConfirmation)
        {
            if (created != generation || !IsVisible || groupRadiusModal != null || (confirmation != null && !isConfirmation)) return;
            try { if (!ValidContext()) { Hide(); return; } action(); }
            catch (Exception error) { Report(error); Hide(); }
        }
        private void Scale()
        {
            if (overlay == null || panel == null) return;
            var rect = overlay.GetComponent<RectTransform>();
            float scale = Mathf.Min(1, Mathf.Min(rect.rect.width / 1160, rect.rect.height / 890));
            if (scale > .01f) panel.transform.localScale = Vector3.one * scale;
            if (groupRadiusPanel != null)
            {
                float groupScale = Mathf.Min(1, Mathf.Min(rect.rect.width / 990, rect.rect.height / 780));
                if (groupScale > .01f) groupRadiusPanel.transform.localScale = Vector3.one * groupScale;
            }
        }
        public void Hide()
        {
            bool wasVisible = IsVisible; generation++;
            try
            {
                CancelDrag(); CancelConfirmation(); CloseGroupRadiusVisuals();
                if (quantity != null) quantity.DeactivateInputField();
                if (overlay != null)
                {
                    if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null
                        && EventSystem.current.currentSelectedGameObject.transform.IsChildOf(overlay.transform)) EventSystem.current.SetSelectedGameObject(null);
                    overlay.SetActive(false); UnityEngine.Object.Destroy(overlay);
                }
            }
            catch (Exception error) { Report(error); }
            finally
            {
                overlay = panel = null; localPlayer = null; network = null; snapshot = null; quantity = null; showPlayersOnMap = null;
                selectedPeer = 0; selectedItem = null;
                heading = subtitle = playerHeading = itemsHeading = playerPages = itemPages = selection = details = status = receiverText = roleHint = quantityHeading = null;
                mapTrackingLabel = null;
                take = delete = refresh = grant = revoke = playerPrev = playerNext = itemPrev = itemNext = findSelectedOnMap = openGroupRadius = null;
                players.Clear(); controls.Clear(); Array.Clear(playerRows, 0, playerRows.Length); Array.Clear(itemRows, 0, itemRows.Length); Array.Clear(icons, 0, icons.Length);
                notice = ""; busy = false; playerPage = itemPage = 0;
                try { inputLease.Release(); } catch (Exception error) { Report(error); }
                if (wasVisible && bindings.OnClosed != null) { try { bindings.OnClosed(); } catch (Exception error) { Report(error); } }
            }
        }
    }
    public sealed class AdminItemDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        private AdminWindow window; private int slot, generation;
        public void Configure(AdminWindow window, int slot, int generation) { this.window = window; this.slot = slot; this.generation = generation; }
        public void OnBeginDrag(PointerEventData data) { if (window != null) window.BeginDrag(slot, generation, data); }
        public void OnDrag(PointerEventData data) { if (window != null) window.MoveDrag(generation, data); }
        public void OnEndDrag(PointerEventData data) { if (window != null) window.EndDrag(generation); }
    }
    public sealed class AdminItemDrop : MonoBehaviour, IDropHandler
    {
        private AdminWindow window; private int generation;
        public void Configure(AdminWindow window, int generation) { this.window = window; this.generation = generation; }
        public void OnDrop(PointerEventData data) { if (window != null) window.Drop(generation); }
    }
}
