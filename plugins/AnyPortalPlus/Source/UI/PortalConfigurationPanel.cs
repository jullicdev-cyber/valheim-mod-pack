// AnyPortal+ UI rewrite of XPortal 1.2.25, modified 2026-10-06.
// GPL-3.0; original project and license are preserved in ../../LICENSE and README.md.
using Jotunn;
using Jotunn.Configs;
using Jotunn.Managers;
using BepInEx.Configuration;
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using XPortal.Plus;

namespace XPortal.UI
{
    /// <summary>
    /// Selection belongs to an editing session, not to a filtered row index.
    /// Sorting and searching only change a detached view of the registry.
    /// </summary>
    internal sealed class PortalConfigurationPanel : IDisposable
    {
        private static readonly Lazy<PortalConfigurationPanel> lazy = new Lazy<PortalConfigurationPanel>(() => new PortalConfigurationPanel());
        public static PortalConfigurationPanel Instance { get { return lazy.Value; } }
        internal const string GO_MAINPANEL = Mod.Info.Name + "_MainPanel";
        internal const string GO_DESTINATIONDROPDOWN = Mod.Info.Name + "_DestinationDropdown";
        private const int RowCount = 10;
        private const float PanelWidth = 980f, PanelHeight = 860f;
        private static readonly int[] AvailableIcons = { -1, 0, 1, 2, 3, 6 };

        // These lifecycle names are also the InterfaceInputFix API.
        private GameObject mainPanel;
        private InputField portalNameInputField, searchInputField;
        private Dropdown sortDropdown, iconDropdown;
        private Toggle defaultPortalToggle, groupByBiomeToggle;
        private Button directionButton, previousButton, nextButton, applyButton, cancelButton, noneButton;
        private Button markCurrentButton, markSelectedButton, pingButton, cleanupButton;
        private Text heading, nameLabel, iconLabel, searchLabel, sortLabel, defaultLabel, groupLabel;
        private Text selectedLabel, pageLabel, countLabel, statusLabel, emptyLabel, navigationHint;
        private readonly Button[] rows = new Button[RowCount];
        private readonly Image[] rowIcons = new Image[RowCount], rowColours = new Image[RowCount];
        private readonly Text[] rowDetails = new Text[RowCount];
        private readonly List<DisplayRow> displayRows = new List<DisplayRow>();
        private readonly List<PortalEntry> entries = new List<PortalEntry>();
        private readonly Dictionary<string, KnownPortal> portalsById = new Dictionary<string, KnownPortal>(StringComparer.Ordinal);
        private KnownPortal thisPortal;
        private ZDOID selectedTargetId = ZDOID.None;
        private Player sessionPlayer;
        private ZNet sessionNetwork;
        private long sessionWorld;
        private int page, selectedIcon = -1, generation;
        private float nextRegistryRefresh;
        private string snapshotSignature = "", language = "", notice = "";
        private bool rebuilding, descending, inputBlocked, submitting;
        private ButtonConfig uiDropdownScrollUpButton, uiDropdownScrollDownButton;
        public bool DropdownExpanded = false;
        private sealed class DisplayRow { internal PortalEntry Portal; internal string Biome; internal bool Spacer; }
        private PortalConfigurationPanel() { }

        internal void AddInputs()
        {
            if (uiDropdownScrollUpButton != null) return;
            uiDropdownScrollUpButton = AddInput("XPortal_DropdownScrollUp", "Portal list: previous", InputManager.GamepadButton.DPadUp, XPortalConfig.Instance.Local.PreviousPortalShortcut);
            uiDropdownScrollDownButton = AddInput("XPortal_DropdownScrollDown", "Portal list: next", InputManager.GamepadButton.DPadDown, XPortalConfig.Instance.Local.NextPortalShortcut);
        }
        private static ButtonConfig AddInput(string name, string hint, InputManager.GamepadButton gamepad, ConfigEntry<KeyboardShortcut> shortcut)
        {
            var result = new ButtonConfig { Name = name, HintToken = hint, ActiveInGUI = true, ActiveInCustomGUI = true,
                ShortcutConfig = shortcut, GamepadButton = gamepad, RepeatDelay = .2f, BlockOtherInputs = true };
            InputManager.Instance.AddButton(Mod.Info.GUID, result); return result;
        }
        public bool IsActive() { return mainPanel != null && mainPanel.activeInHierarchy; }
        internal void OnInputBlockReset()
        {
            // Jotunn already discarded all requests. A later close must not
            // subtract a different window's newly acquired input request.
            inputBlocked = false;
        }
        public void SetActive(bool active)
        {
            if (active && mainPanel == null) InitialiseUI();
            if (mainPanel == null) return;
            // InterfaceInputFix normally owns the lease. Keep the standalone
            // fallback idempotent as well, including repeated close calls.
            if (active && !inputBlocked) { GUIManager.BlockInput(true); inputBlocked = true; }
            if (!active && inputBlocked) { GUIManager.BlockInput(false); inputBlocked = false; }
            if (active) generation++; // A new Show cancels an older delayed close/focus.
            if (!active) { CloseDropdowns(); ClearSelectedControl(); generation++; }
            mainPanel.SetActive(active);
            if (active) { mainPanel.transform.SetAsLastSibling(); ActivateInputField(); }
        }
        private void ActivateInputField(bool delayed = true, object state = null)
        {
            if (delayed) { QueuedAction.Queue(ActivateInputField, state: generation); return; }
            if (state is int && (int)state != generation) return;
            if (IsActive() && portalNameInputField != null) portalNameInputField.ActivateInputField();
        }
        public void Show() { SetActive(true); }
        public void Hide(bool delayed = true, object state = null)
        {
            if (delayed) { QueuedAction.Queue(Hide, state: generation); return; }
            if (state is int && (int)state != generation) return;
            SetActive(false);
        }
        public void ConfigurePortal(KnownPortal portal)
        {
            if (portal == null || Environment.IsHeadless) return;
            InitialiseUI(); if (mainPanel == null) return;
            CloseDropdowns(); generation++; submitting = false;
            thisPortal = portal; selectedTargetId = portal.Target;
            sessionPlayer = Player.m_localPlayer; sessionNetwork = ZNet.instance;
            sessionWorld = sessionNetwork == null ? 0 : sessionNetwork.GetWorldUID();
            rebuilding = true;
            try {
                portalNameInputField.text = portal.Name ?? "";
                defaultPortalToggle.isOn = portal.IsDefaultPortal;
                selectedIcon = Array.IndexOf(AvailableIcons, portal.Icon) < 0 ? -1 : portal.Icon;
                iconDropdown.value = Math.Max(0, Array.IndexOf(AvailableIcons, selectedIcon));
                searchInputField.text = "";
            }
            finally { rebuilding = false; }
            notice = ""; page = 0; snapshotSignature = "";
            UpdateLocalization(); RefreshRegistry(true); Scale(); Show();
        }
        private bool ValidSession()
        {
            return thisPortal != null && sessionPlayer != null && ReferenceEquals(sessionPlayer, Player.m_localPlayer)
                && sessionNetwork != null && ReferenceEquals(sessionNetwork, ZNet.instance)
                && sessionWorld != 0 && sessionNetwork.GetWorldUID() == sessionWorld
                && ZInput.instance != null
                && !sessionPlayer.IsDead() && !sessionPlayer.IsTeleporting() && !sessionPlayer.InCutscene()
                && Game.instance != null && !Game.instance.m_shuttingDown
                && KnownPortalsManager.Instance.ContainsId(thisPortal.Id);
        }
        public void HandleInput()
        {
            if (!IsActive()) return;
            if (!ValidSession()) { Hide(false); return; }
            Scale();
            if (Time.unscaledTime >= nextRegistryRefresh) {
                nextRegistryRefresh = Time.unscaledTime + 2f; UpdateLocalization(); RefreshRegistry(false); UpdateNavigationHint();
            }
            bool popup = UnifiedPopup.IsVisible();
            CanvasGroup group = mainPanel.GetComponent<CanvasGroup>(); if (group != null) group.interactable = !popup;
            if (popup) return;
            if (Input.GetKeyDown(KeyCode.Escape) || ZInput.GetButtonDown("JoyButtonB")) {
                if (ZInput.GetButtonDown("JoyButtonB")) ZInput.ResetButtonStatus("JoyButtonB");
                if (AnyDropdownExpanded()) CloseDropdowns(); else Hide(); return;
            }
            // Editing owns its caret keys. It must not select another portal.
            if (portalNameInputField.isFocused || searchInputField.isFocused || AnyDropdownExpanded()) return;
            if (uiDropdownScrollUpButton != null && ZInput.GetButtonUp(uiDropdownScrollUpButton.Name)) SelectAdjacent(-1);
            else if (uiDropdownScrollDownButton != null && ZInput.GetButtonUp(uiDropdownScrollDownButton.Name)) SelectAdjacent(1);
        }
        private void RefreshRegistry(bool force)
        {
            List<KnownPortal> portals = KnownPortalsManager.Instance.GetList();
            portals.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id.ToString(), b.Id.ToString()));
            var signature = new System.Text.StringBuilder();
            foreach (KnownPortal portal in portals) {
                signature.Append(portal.Id).Append('\0').Append(portal.Name).Append('\0').Append(portal.Biome).Append(':')
                    .Append(portal.Icon).Append(':').Append(portal.CreatedUtcTicks).Append(':')
                    .Append(portal.Location.x.ToString("R", CultureInfo.InvariantCulture)).Append(':')
                    .Append(portal.Location.y.ToString("R", CultureInfo.InvariantCulture)).Append(':')
                    .Append(portal.Location.z.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            }
            string current = signature.ToString(); if (!force && current == snapshotSignature) return;
            snapshotSignature = current; portalsById.Clear(); entries.Clear();
            foreach (KnownPortal portal in portals) {
                string id = portal.Id.ToString(); portalsById[id] = portal;
                entries.Add(new PortalEntry { Id = id, Name = portal.Name ?? "", Biome = ((Heightmap.Biome)portal.Biome).ToString(),
                    X = portal.Location.x, Y = portal.Location.y, Z = portal.Location.z, CreatedUtcTicks = portal.CreatedUtcTicks, Icon = portal.Icon });
            }
            RebuildList(false);
        }
        private void RebuildList(bool resetPage)
        {
            if (thisPortal == null || rebuilding) return;
            if (resetPage) page = 0;
            KnownPortal origin; if (!portalsById.TryGetValue(thisPortal.Id.ToString(), out origin)) origin = thisPortal;
            var matches = PortalListModel.Query(entries, thisPortal.Id.ToString(), origin.Location.x, origin.Location.y, origin.Location.z,
                searchInputField.text, (PortalSort)sortDropdown.value, descending, groupByBiomeToggle.isOn);
            displayRows.Clear(); string previousBiome = null;
            foreach (PortalEntry portal in matches) {
                if (groupByBiomeToggle.isOn && (portal.Biome != previousBiome || displayRows.Count % RowCount == 0)) {
                    // Do not leave a biome heading alone at the page bottom.
                    // Repeat its heading when that group continues on a new page.
                    if (displayRows.Count % RowCount == RowCount - 1) displayRows.Add(new DisplayRow { Spacer = true });
                    displayRows.Add(new DisplayRow { Biome = portal.Biome }); previousBiome = portal.Biome;
                }
                displayRows.Add(new DisplayRow { Portal = portal });
            }
            page = Math.Max(0, Math.Min(page, PageCount() - 1));
            countLabel.text = PlusText.Get("Найдено порталов: ", "Portals found: ") + matches.Count.ToString(CultureInfo.InvariantCulture);
            emptyLabel.gameObject.SetActive(matches.Count == 0);
            emptyLabel.text = String.IsNullOrWhiteSpace(searchInputField.text) ? PlusText.Get("Других порталов пока нет.", "There are no other portals yet.")
                : PlusText.Get("По этому запросу порталы не найдены.", "No portals match this search.");
            PaintRows();
        }
        private int PageCount() { return Math.Max(1, (displayRows.Count + RowCount - 1) / RowCount); }
        private void PaintRows()
        {
            for (int i = 0; i < RowCount; i++) {
                int index = page * RowCount + i; bool exists = index < displayRows.Count && !displayRows[index].Spacer;
                rows[i].gameObject.SetActive(exists); if (!exists) continue;
                DisplayRow row = displayRows[index]; Text title = rows[i].GetComponentInChildren<Text>(); bool header = row.Portal == null;
                rows[i].interactable = !header; rowDetails[i].gameObject.SetActive(!header);
                rowColours[i].gameObject.SetActive(false); rowIcons[i].gameObject.SetActive(false);
                title.color = header ? GUIManager.Instance.ValheimOrange : GUIManager.Instance.ValheimBeige;
                title.fontSize = header ? 17 : 18; title.rectTransform.offsetMin = new Vector2(15, 0);
                title.rectTransform.offsetMax = new Vector2(header ? -15 : -230, 0);
                if (header) { title.text = PlusText.Biome(row.Biome); continue; }
                KnownPortal portal; if (!portalsById.TryGetValue(row.Portal.Id, out portal)) { rows[i].interactable = false; continue; }
                title.text = (portal.Id == selectedTargetId ? "\u2713 " : "") + SafeName(portal);
                Sprite sprite = portal.Icon == -1 ? null : PlusMapMarkers.GetIconSprite(portal.Icon);
                if (sprite != null) { rowIcons[i].sprite = sprite; rowIcons[i].gameObject.SetActive(true); title.rectTransform.offsetMin = new Vector2(54, 0); }
                Color colour;
                if (XPortalConfig.Instance.Local.DisplayPortalColour && ColorUtility.TryParseHtmlString(portal.Colour, out colour)) {
                    rowColours[i].color = colour; rowColours[i].gameObject.SetActive(true);
                }
                string details = DateText(portal.CreatedUtcTicks);
                if (!XPortalConfig.Instance.Server.HidePortalDistance) {
                    details = DistanceText(PortalListModel.Distance(row.Portal, thisPortal.Location.x, thisPortal.Location.y, thisPortal.Location.z)) + "  |  " + details;
                }
                rowDetails[i].text = details;
                var colours = rows[i].colors; colours.normalColor = portal.Id == selectedTargetId ? new Color(.8f, .65f, .35f, 1f) : Color.white;
                rows[i].colors = colours;
            }
            previousButton.interactable = page > 0; nextButton.interactable = page + 1 < PageCount();
            pageLabel.text = (page + 1).ToString(CultureInfo.InvariantCulture) + " / " + PageCount().ToString(CultureInfo.InvariantCulture);
            UpdateSelection();
        }
        private void UpdateSelection()
        {
            KnownPortal selected; bool none = selectedTargetId == ZDOID.None || selectedTargetId.IsNone();
            bool exists = !none && portalsById.ContainsKey(selectedTargetId.ToString());
            if (none) selectedLabel.text = PlusText.Get("Выход: не выбран", "Destination: none");
            else if (portalsById.TryGetValue(selectedTargetId.ToString(), out selected)) selectedLabel.text = PlusText.Get("Выход: ", "Destination: ") + SafeName(selected);
            else selectedLabel.text = PlusText.Get("Выбранный портал удалён. Выберите другой выход или «Без выхода».", "The selected portal was removed. Choose another destination or None.");
            bool map = Minimap.instance != null && ZoneSystem.instance != null && !ZoneSystem.instance.GetGlobalKey("nomap");
            applyButton.interactable = !submitting && (none || exists); markSelectedButton.interactable = map && exists;
            markCurrentButton.interactable = map; cleanupButton.interactable = map;
            pingButton.interactable = map && exists && !XPortalConfig.Instance.Server.PingMapDisabled; statusLabel.text = notice;
        }
        private void SelectRow(int slot)
        {
            if (!CanAct()) return;
            int index = page * RowCount + slot; if (index >= displayRows.Count || displayRows[index].Portal == null) return;
            KnownPortal selected; if (!portalsById.TryGetValue(displayRows[index].Portal.Id, out selected)) return;
            selectedTargetId = selected.Id; notice = ""; PaintRows();
        }
        private void SelectAdjacent(int direction)
        {
            if (displayRows.Count == 0) return;
            int index = -1;
            for (int i = 0; i < displayRows.Count; i++) if (displayRows[i].Portal != null && displayRows[i].Portal.Id == selectedTargetId.ToString()) { index = i; break; }
            if (index < 0) index = direction < 0 ? displayRows.Count : -1;
            for (index += direction; index >= 0 && index < displayRows.Count; index += direction) {
                if (displayRows[index].Portal == null) continue;
                KnownPortal selected; if (!portalsById.TryGetValue(displayRows[index].Portal.Id, out selected)) continue;
                selectedTargetId = selected.Id; page = index / RowCount; PaintRows(); return;
            }
        }
        private bool CanAct()
        {
            if (!IsActive() || UnifiedPopup.IsVisible()) return false;
            if (!ValidSession()) { Hide(false); return false; } return true;
        }
        private void OnOkayButtonClicked()
        {
            if (!CanAct() || submitting || !applyButton.interactable) return;
            submitting = true; applyButton.interactable = false;
            try { XPortal.PortalInfoSubmitted(thisPortal, portalNameInputField.text, selectedTargetId, defaultPortalToggle.isOn, selectedIcon); Hide(); }
            catch (Exception error) { submitting = false; notice = error.Message; UpdateSelection(); Log.Error(error); }
        }
        private void OnPingMapButtonClicked()
        {
            if (!CanAct() || !pingButton.interactable) return;
            XPortal.PingMapButtonClicked(selectedTargetId); Hide();
        }
        private void MarkPortal(bool current)
        {
            if (!CanAct()) return;
            ZDOID id = current ? thisPortal.Id : selectedTargetId;
            if (!KnownPortalsManager.Instance.ContainsId(id)) { notice = PlusText.Get("Портал больше не существует.", "This portal no longer exists."); UpdateSelection(); return; }
            bool added = PlusMapMarkers.AddOrUpdate(KnownPortalsManager.Instance.GetKnownPortalById(id));
            notice = added ? PlusText.Get("Метка портала добавлена или обновлена на вашей карте.", "The portal marker was added or updated on your map.")
                : PlusText.Get("Не удалось добавить метку. Карта пока недоступна.", "The marker could not be added. The map is not available yet.");
            UpdateSelection();
        }
        private void UpdateLocalization()
        {
            string current = PlusText.Language; if (current == language && !String.IsNullOrEmpty(language)) return;
            language = current; rebuilding = true;
            try {
                heading.text = "AnyPortal+"; nameLabel.text = PlusText.Get("Имя портала", "Portal name"); iconLabel.text = PlusText.Get("Значок", "Icon");
                searchLabel.text = PlusText.Get("Поиск", "Search"); sortLabel.text = PlusText.Get("Сортировка", "Sort by");
                defaultLabel.text = PlusText.Get("Использовать как выход по умолчанию", "Use as the default destination");
                groupLabel.text = PlusText.Get("Группировать по биомам", "Group by biome");
                Text hint = searchInputField.placeholder as Text; if (hint != null) hint.text = PlusText.Get("Название портала…", "Portal name…");
                SetButtonText(directionButton, descending ? PlusText.Get("Убывание ↓", "Descending ↓") : PlusText.Get("Возрастание ↑", "Ascending ↑"));
                SetButtonText(markCurrentButton, PlusText.Get("Метка этого портала", "Mark this portal"));
                SetButtonText(markSelectedButton, PlusText.Get("Метка выбранного выхода", "Mark selected destination"));
                SetButtonText(pingButton, PlusText.Get("Показать / пинг на карте", "Show / ping on map"));
                SetButtonText(cleanupButton, PlusText.Get("Очистить устаревшие метки", "Clean up stale markers"));
                SetButtonText(applyButton, PlusText.Get("Применить", "Apply")); SetButtonText(cancelButton, PlusText.Get("Отмена", "Cancel"));
                SetButtonText(noneButton, PlusText.Get("Без выхода", "No destination"));
                int sort = sortDropdown.value; sortDropdown.ClearOptions();
                sortDropdown.AddOptions(new List<string> { PlusText.Get("По имени", "Name"), PlusText.Get("По удалённости", "Distance"), PlusText.Get("По дате создания", "Creation date") });
                sortDropdown.value = sort; sortDropdown.RefreshShownValue();
                int icon = Math.Max(0, Array.IndexOf(AvailableIcons, selectedIcon)); iconDropdown.ClearOptions();
                var names = new[] { PlusText.Get("Без значка", "No icon"), PlusText.Get("Костёр", "Campfire"), PlusText.Get("Дом", "House"),
                    PlusText.Get("Молот", "Hammer"), PlusText.Get("Точка", "Marker"), PlusText.Get("Портал", "Portal") };
                for (int i = 0; i < AvailableIcons.Length; i++) iconDropdown.options.Add(new Dropdown.OptionData(names[i], AvailableIcons[i] < 0 ? null : PlusMapMarkers.GetIconSprite(AvailableIcons[i])));
                iconDropdown.value = icon; iconDropdown.RefreshShownValue();
            }
            finally { rebuilding = false; }
            if (thisPortal != null) RebuildList(false);
        }
        private static string SafeName(KnownPortal portal)
        {
            var clean = new System.Text.StringBuilder(); foreach (char c in portal.Name ?? "") if (!Char.IsControl(c)) clean.Append(c);
            return clean.Length == 0 ? PlusText.Get("Без названия", "Unnamed") : clean.ToString();
        }
        private static string DistanceText(double distance)
        { return distance < 1000d ? Math.Round(distance).ToString("0", CultureInfo.CurrentCulture) + " m" : (distance / 1000d).ToString("0.0", CultureInfo.CurrentCulture) + " km"; }
        private static string DateText(long ticks)
        {
            if (ticks <= 0 || ticks > DateTime.MaxValue.Ticks) return PlusText.Get("Дата неизвестна", "Date unknown");
            return new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().ToString(PlusText.Language == "Russian" ? "dd.MM.yyyy HH:mm" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        private void InitialiseUI()
        {
            if (Environment.IsHeadless || mainPanel != null) return;
            GameObject hook = GUIManager.CustomGUIFront; if (hook == null) { Log.Error("AnyPortal+ GUI canvas is not ready"); return; }
            Vector2 center = new Vector2(.5f, .5f);
            mainPanel = GUIManager.Instance.CreateWoodpanel(hook.transform, center, center, Vector2.zero, PanelWidth, PanelHeight, false);
            mainPanel.name = GO_MAINPANEL; mainPanel.AddComponent<CanvasGroup>(); mainPanel.AddComponent<UIGroupHandler>();
            heading = Label("", 0, 389, 890, 50, 30, true, TextAnchor.MiddleCenter);
            nameLabel = Label("", -395, 326, 140, 34, 18, true);
            portalNameInputField = InputAt("AnyPortalPlus.Name", -84, 326, 470, ""); portalNameInputField.characterLimit = 256;
            iconLabel = Label("", 202, 326, 82, 34, 18, true); iconDropdown = DropdownAt("AnyPortalPlus.Icon", 357, 326, 186); AddIconImages(iconDropdown);
            iconDropdown.onValueChanged.AddListener(value => { if (!rebuilding && value >= 0 && value < AvailableIcons.Length) selectedIcon = AvailableIcons[value]; });
            defaultPortalToggle = ToggleAt(-449, 273); defaultLabel = Label("", -237, 273, 387, 36, 16, false);
            markCurrentButton = ButtonAt("", 238, 273, 416, 37, () => MarkPortal(true));
            searchLabel = Label("", -411, 229, 95, 25, 16, true); sortLabel = Label("", 4, 229, 224, 25, 16, true);
            searchInputField = InputAt("AnyPortalPlus.Search", -287, 196, 322, ""); searchInputField.characterLimit = 256;
            searchInputField.onValueChanged.AddListener(value => { if (!rebuilding) RebuildList(true); });
            sortDropdown = DropdownAt("AnyPortalPlus.Sort", 3, 196, 232); sortDropdown.onValueChanged.AddListener(value => { if (!rebuilding) RebuildList(true); });
            directionButton = ButtonAt("", 287, 196, 319, 37, () => {
                descending = !descending; SetButtonText(directionButton, descending ? PlusText.Get("Убывание ↓", "Descending ↓") : PlusText.Get("Возрастание ↑", "Ascending ↑")); RebuildList(true);
            });
            groupByBiomeToggle = ToggleAt(-449, 150); groupLabel = Label("", -254, 150, 354, 31, 17, false);
            groupByBiomeToggle.onValueChanged.AddListener(value => { if (!rebuilding) RebuildList(true); });
            countLabel = Label("", 244, 150, 407, 31, 16, false, TextAnchor.MiddleRight);
            selectedLabel = Label("", -63, 108, 773, 38, 17, true);
            noneButton = ButtonAt("", 375, 108, 141, 36, () => { if (CanAct()) { selectedTargetId = ZDOID.None; PaintRows(); } }); noneButton.gameObject.name = "AnyPortalPlus.None";
            for (int i = 0; i < RowCount; i++) {
                int slot = i; rows[i] = ButtonAt("", 0, 62 - i * 36, 898, 33, () => SelectRow(slot)); rows[i].gameObject.name = "AnyPortalPlus.Destination." + i;
                Text title = rows[i].GetComponentInChildren<Text>(); title.alignment = TextAnchor.MiddleLeft;
                title.resizeTextForBestFit = true; title.resizeTextMinSize = 13; title.resizeTextMaxSize = 18;
                title.rectTransform.anchorMin = Vector2.zero; title.rectTransform.anchorMax = Vector2.one;
                rowIcons[i] = ImageAt(rows[i].transform, "PortalIcon", new Vector2(0, .5f), new Vector2(31, 0), new Vector2(28, 28));
                rowColours[i] = ImageAt(rows[i].transform, "PortalColour", new Vector2(0, .5f), new Vector2(5, 0), new Vector2(4, 29));
                rowDetails[i] = Label("", 334, 0, 211, 30, 13, false, TextAnchor.MiddleRight, rows[i].transform);
            }
            emptyLabel = Label("", 0, -102, 872, 62, 20, false, TextAnchor.MiddleCenter); emptyLabel.gameObject.SetActive(false);
            previousButton = ButtonAt("<", -126, -288, 46, 33, () => { if (page > 0) { page--; PaintRows(); } });
            pageLabel = Label("", 0, -288, 180, 32, 16, false, TextAnchor.MiddleCenter);
            nextButton = ButtonAt(">", 126, -288, 46, 33, () => { if (page + 1 < PageCount()) { page++; PaintRows(); } });
            navigationHint = Label("", -305, -288, 267, 33, 12, false);
            markSelectedButton = ButtonAt("", -306, -337, 282, 36, () => MarkPortal(false)); pingButton = ButtonAt("", 0, -337, 282, 36, OnPingMapButtonClicked);
            cleanupButton = ButtonAt("", 306, -337, 282, 36, () => {
                if (!CanAct()) return;
                // Release the portal canvas before opening the native modal.
                Hide(false, null); PlusMapMarkers.CleanupWithConfirmation();
            });
            statusLabel = Label("", -152, -389, 591, 52, 14, false);
            cancelButton = ButtonAt("", 223, -390, 142, 44, () => Hide()); cancelButton.gameObject.name = "AnyPortalPlus.Cancel";
            applyButton = ButtonAt("", 375, -390, 142, 44, OnOkayButtonClicked); applyButton.gameObject.name = "AnyPortalPlus.Apply";
            language = ""; UpdateLocalization(); UpdateNavigationHint(); mainPanel.SetActive(false); Scale();
        }
        private void UpdateNavigationHint()
        {
            if (navigationHint == null) return;
            navigationHint.text = PlusText.Get("Выбрать выход: ", "Select destination: ")
                + XPortalConfig.Instance.Local.PreviousPortalShortcut.Value + " / " + XPortalConfig.Instance.Local.NextPortalShortcut.Value;
        }
        private Text Label(string text, float x, float y, float width, float height, int size, bool accent,
            TextAnchor alignment = TextAnchor.MiddleLeft, Transform parent = null)
        {
            Vector2 center = new Vector2(.5f, .5f);
            var created = GUIManager.Instance.CreateText(text, parent ?? mainPanel.transform, center, center, new Vector2(x, y),
                accent ? GUIManager.Instance.AveriaSerifBold : GUIManager.Instance.AveriaSerif, size,
                accent ? GUIManager.Instance.ValheimOrange : GUIManager.Instance.ValheimBeige, true, Color.black, width, height, false);
            Text result = created.GetComponent<Text>(); result.supportRichText = false; result.alignment = alignment;
            result.horizontalOverflow = HorizontalWrapMode.Wrap; result.verticalOverflow = VerticalWrapMode.Truncate; result.raycastTarget = false; return result;
        }
        private Button ButtonAt(string text, float x, float y, float width, float height, Action action)
        {
            Vector2 center = new Vector2(.5f, .5f);
            Button button = GUIManager.Instance.CreateButton(text, mainPanel.transform, center, center, new Vector2(x, y), width, height).GetComponent<Button>();
            Text label = button.GetComponentInChildren<Text>(); label.supportRichText = false; label.fontSize = 17;
            label.resizeTextForBestFit = true; label.resizeTextMinSize = 12; label.resizeTextMaxSize = 17;
            button.onClick.AddListener(() => { try { if (action != null) action(); }
                catch (Exception error) { notice = PlusText.Get("Ошибка: ", "Error: ") + error.Message; if (statusLabel != null) statusLabel.text = notice; Log.Error(error); } });
            return button;
        }
        private InputField InputAt(string name, float x, float y, float width, string placeholder)
        {
            Vector2 center = new Vector2(.5f, .5f);
            InputField input = GUIManager.Instance.CreateInputField(mainPanel.transform, center, center, new Vector2(x, y),
                InputField.ContentType.Standard, placeholder, 18, width, 37).GetComponent<InputField>();
            input.gameObject.name = name; input.textComponent.supportRichText = false;
            var hint = input.placeholder as Text; if (hint != null) hint.supportRichText = false;
            input.shouldActivateOnSelect = true; return input;
        }
        private Dropdown DropdownAt(string name, float x, float y, float width)
        {
            Vector2 center = new Vector2(.5f, .5f);
            Dropdown dropdown = GUIManager.Instance.CreateDropDown(mainPanel.transform, center, center, new Vector2(x, y), 17, width, 37).GetComponent<Dropdown>();
            dropdown.gameObject.name = name; dropdown.captionText.supportRichText = false; dropdown.itemText.supportRichText = false;
            dropdown.template.sizeDelta = new Vector2(dropdown.template.sizeDelta.x, 260); return dropdown;
        }
        private Toggle ToggleAt(float x, float y)
        {
            Toggle toggle = GUIManager.Instance.CreateToggle(mainPanel.transform, 28, 28).GetComponent<Toggle>();
            RectTransform rect = toggle.GetComponent<RectTransform>(); rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
            rect.pivot = new Vector2(.5f, .5f); rect.anchoredPosition = new Vector2(x, y); rect.localScale = Vector3.one;
            Text label = toggle.GetComponentInChildren<Text>(); if (label != null) { label.text = ""; label.raycastTarget = false; } return toggle;
        }
        private static Image ImageAt(Transform parent, string name, Vector2 anchor, Vector2 position, Vector2 size)
        {
            var created = new GameObject("AnyPortalPlus." + name, typeof(RectTransform), typeof(Image)); created.layer = GUIManager.UILayer; created.transform.SetParent(parent, false);
            RectTransform rect = created.GetComponent<RectTransform>(); rect.anchorMin = rect.anchorMax = anchor; rect.sizeDelta = size; rect.anchoredPosition = position;
            Image image = created.GetComponent<Image>(); image.preserveAspect = true; image.raycastTarget = false; return image;
        }
        private static void AddIconImages(Dropdown dropdown)
        {
            Transform templateItem = dropdown.template.Find("Viewport/Content/Item");
            if (templateItem != null) {
                dropdown.itemImage = ImageAt(templateItem, "IconOption", new Vector2(0, .5f), new Vector2(36, 0), new Vector2(24, 24));
                dropdown.itemText.rectTransform.offsetMin = new Vector2(54, dropdown.itemText.rectTransform.offsetMin.y);
            }
            dropdown.captionImage = ImageAt(dropdown.transform, "SelectedIcon", new Vector2(0, .5f), new Vector2(20, 0), new Vector2(24, 24));
            dropdown.captionText.rectTransform.offsetMin = new Vector2(38, dropdown.captionText.rectTransform.offsetMin.y);
        }
        private static void SetButtonText(Button button, string text) { if (button != null) button.GetComponentInChildren<Text>().text = text; }
        private bool AnyDropdownExpanded()
        { return (sortDropdown != null && sortDropdown.transform.Find("Dropdown List") != null) || (iconDropdown != null && iconDropdown.transform.Find("Dropdown List") != null); }
        private void CloseDropdowns()
        { if (sortDropdown != null) sortDropdown.Hide(); if (iconDropdown != null) iconDropdown.Hide(); DropdownExpanded = false; }
        private void ClearSelectedControl()
        {
            if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null && mainPanel != null
                && EventSystem.current.currentSelectedGameObject.transform.IsChildOf(mainPanel.transform)) EventSystem.current.SetSelectedGameObject(null);
        }
        private void Scale()
        {
            if (mainPanel == null) return;
            RectTransform parent = mainPanel.transform.parent as RectTransform; if (parent == null || parent.rect.width <= 0 || parent.rect.height <= 0) return;
            float scale = Mathf.Min(1f, Mathf.Min((parent.rect.width - 24f) / PanelWidth, (parent.rect.height - 24f) / PanelHeight));
            mainPanel.transform.localScale = Vector3.one * Mathf.Max(.1f, scale);
        }
        public void Dispose()
        {
            Hide(false); thisPortal = null; sessionPlayer = null; sessionNetwork = null; entries.Clear(); displayRows.Clear(); portalsById.Clear();
            if (mainPanel != null) GameObject.Destroy(mainPanel); mainPanel = null;
        }
    }
}
