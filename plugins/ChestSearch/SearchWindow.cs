using System;
using System.Collections.Generic;
using System.Globalization;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ValheimModPack.ChestSearch
{
    public sealed class SearchWindow
    {
        private const int PageSize = 6;
        private readonly Plugin plugin;
        private readonly Button[] rows = new Button[PageSize];
        private readonly Image[] icons = new Image[PageSize];
        private readonly List<Selectable> controls = new List<Selectable>();
        private GameObject overlay, panel;
        private Player player;
        private ZNet network;
        private InputField input;
        private Text summary, footer, selection;
        private Button previous, next, highlight, sort, direction;
        private Toggle showAll;
        private string selectedKey;
        private SearchReport report = new SearchReport();
        private int page, generation;
        private float searchAt, nextScale;
        private readonly InputBlockLease inputBlock = new InputBlockLease(GUIManager.BlockInput);
        public bool IsVisible { get { return overlay != null && overlay.activeInHierarchy; } }
        public SearchWindow(Plugin plugin) { this.plugin = plugin; }

        public void Show()
        {
            Hide();
            player = Player.m_localPlayer; network = ZNet.instance;
            if (!ValidContext() || GUIManager.CustomGUIFront == null) return;
            try
            {
                BuildVisuals();
                inputBlock.Acquire();
                overlay.transform.SetAsLastSibling();
                if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(input.gameObject);
                input.ActivateInputField();
            }
            catch (Exception error) { Hide(); plugin.Report(error); }
        }
        // Separate construction permits a native UI smoke test without a world
        // or a player and without taking a gameplay input lock.
        private void BuildVisuals()
        {
                var front = GUIManager.CustomGUIFront;
                overlay = new GameObject("ChestSearch.Modal", typeof(RectTransform), typeof(Image));
                overlay.transform.SetParent(front.transform, false); overlay.layer = GUIManager.UILayer;
                var rect = overlay.GetComponent<RectTransform>();
                rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
                var dim = overlay.GetComponent<Image>(); dim.color = new Color(0, 0, 0, 0.58f); dim.raycastTarget = true;
                var gui = GUIManager.Instance;
                var center = new Vector2(0.5f, 0.5f);
                panel = gui.CreateWoodpanel(overlay.transform, center, center, Vector2.zero, 860, 800, false);
                panel.name = "ChestSearch.WoodPanel";
                var group = panel.AddComponent<CanvasGroup>(); group.interactable = true; group.blocksRaycasts = true;
                Label(plugin.T("Поиск по сундукам", "Search nearby chests"), 0, 350, 680, 46, 30, true);
                ButtonAt("X", 382, 350, 44, 40, Hide);
                input = gui.CreateInputField(panel.transform, center, center, new Vector2(-73, 295), InputField.ContentType.Standard,
                    plugin.T("Название предмета: железо, Iron, FineWood…", "Item name: Iron, FineWood…"), 18, 630, 42).GetComponent<InputField>();
                input.characterLimit = 64; input.lineType = InputField.LineType.SingleLine;
                input.textComponent.supportRichText = false;
                Text placeholder = input.placeholder as Text; if (placeholder != null) placeholder.supportRichText = false;
                input.text = plugin.LastQuery ?? "";
                int currentGeneration = generation;
                input.onValueChanged.AddListener(value =>
                {
                    if (currentGeneration != generation || !IsVisible) return;
                    plugin.LastQuery = SearchText.Safe(value, 64); page = 0; selectedKey = null;
                    report = new SearchReport(); searchAt = Time.unscaledTime + 0.25f; Repaint();
                });
                controls.Add(input);
                ButtonAt(plugin.T("Обновить", "Refresh"), 326, 295, 132, 42, () => { searchAt = 0; });
                BuildCheckbox(currentGeneration);
                sort = ButtonAt("", -125, 199, 520, 38, () =>
                { plugin.SortOrder = (ItemSort)(((int)plugin.SortOrder + 1) % 4); page = 0; Repaint(); });
                direction = ButtonAt("", 265, 199, 252, 38, () =>
                { plugin.SortDescending = !plugin.SortDescending; page = 0; Repaint(); });
                summary = Label("", 0, 145, 785, 54, 18, false);
                for (int i = 0; i < PageSize; i++)
                {
                    int slot = i;
                    rows[i] = ButtonAt("", 0, 86 - i * 58, 788, 52, () => Select(slot));
                    var text = rows[i].GetComponentInChildren<Text>();
                    text.alignment = TextAnchor.MiddleLeft; text.fontSize = 17;
                    text.resizeTextMinSize = 14; text.resizeTextMaxSize = 17;
                    var textRect = text.rectTransform;
                    textRect.anchorMin = Vector2.zero; textRect.anchorMax = Vector2.one;
                    textRect.offsetMin = new Vector2(65, 2); textRect.offsetMax = new Vector2(-12, -2);
                    var iconObject = new GameObject("ItemIcon", typeof(RectTransform), typeof(Image));
                    iconObject.layer = GUIManager.UILayer; iconObject.transform.SetParent(rows[i].transform, false);
                    var iconRect = iconObject.GetComponent<RectTransform>();
                    iconRect.anchorMin = iconRect.anchorMax = new Vector2(0, .5f);
                    iconRect.sizeDelta = new Vector2(42, 42); iconRect.anchoredPosition = new Vector2(32, 0);
                    icons[i] = iconObject.GetComponent<Image>(); icons[i].preserveAspect = true; icons[i].raycastTarget = false;
                }
                previous = ButtonAt("<", -342, -260, 88, 38, () => { if (page > 0) page--; Repaint(); });
                next = ButtonAt(">", 342, -260, 88, 38, () => { page++; Repaint(); });
                footer = Label("", 0, -260, 590, 36, 16, false);
                selection = Label("", 0, -303, 786, 42, 17, false);
                highlight = ButtonAt("", -83, -355, 614, 42, HighlightSelected);
                ButtonAt(plugin.T("Снять метки", "Clear marks"), 310, -355, 158, 42, plugin.ClearMarkers);
                page = 0; report = new SearchReport(); searchAt = Time.unscaledTime + 0.05f;
                Repaint();
        }
        public void Tick()
        {
            if (overlay == null && !inputBlock.Held) return;
            try
            {
                if (!IsVisible || !ValidContext() || Input.GetKeyDown(KeyCode.Escape) || ZInput.GetButtonDown("JoyButtonB"))
                {
                    if (ZInput.GetButtonDown("JoyButtonB")) ZInput.ResetButtonStatus("JoyButtonB");
                    Hide(); return;
                }
                if (Time.unscaledTime >= searchAt)
                {
                    searchAt = Time.unscaledTime + 2;
                    plugin.LastQuery = SearchText.Safe(input.text, 64);
                    report = plugin.Search.Search(player, plugin.LastQuery, plugin.Radius, plugin.ShowWithoutInput);
                    Repaint();
                }
                if (Time.unscaledTime >= nextScale)
                {
                    nextScale = Time.unscaledTime + 0.5f;
                    var rect = overlay.GetComponent<RectTransform>();
                    float scale = Mathf.Min(1, Mathf.Min(rect.rect.width / 890, rect.rect.height / 830));
                    if (scale > 0.01f) panel.transform.localScale = Vector3.one * scale;
                }
            }
            catch (Exception error) { Hide(); plugin.Report(error); }
        }
        private bool ValidContext()
        {
            return player != null && ReferenceEquals(player, Player.m_localPlayer) && network != null && ReferenceEquals(network, ZNet.instance)
                && !player.IsDead() && !player.IsTeleporting() && !player.IsSleeping() && !player.InCutscene()
                && !UnifiedPopup.IsVisible() && !Menu.IsVisible() && !InventoryGui.IsVisible()
                // Jotunn makes TextInput.IsVisible() true for our own input block.
                // Inspect the real native input panel instead of closing ourselves.
                && (TextInput.instance == null || TextInput.instance.m_panel == null || !TextInput.instance.m_panel.activeInHierarchy)
                && !global::Console.IsVisible() && (Chat.instance == null || !Chat.instance.HasFocus())
                && (Minimap.instance == null || Minimap.instance.m_mode != Minimap.MapMode.Large);
        }
        private void Select(int slot)
        {
            int index = page * PageSize + slot;
            if (index < 0 || index >= report.Items.Count) return;
            selectedKey = report.Items[index].Key; Repaint();
        }
        private ItemSearchResult Selected()
        {
            foreach (var item in report.Items) if (String.Equals(item.Key, selectedKey, StringComparison.Ordinal)) return item;
            selectedKey = null; return null;
        }
        private void HighlightSelected()
        {
            var item = Selected(); if (item == null) return;
            if (plugin.LocateAll(item) == 0)
            {
                summary.text = plugin.T("Предмет больше не доступен в этих сундуках. Обновляю…", "The item is no longer accessible in these chests. Refreshing…");
                searchAt = Time.unscaledTime + 0.5f;
            }
        }
        private void Repaint()
        {
            if (summary == null) return;
            SearchService.Sort(report.Items, plugin.SortOrder, plugin.SortDescending);
            int pages = Math.Max(1, (report.Items.Count + PageSize - 1) / PageSize);
            page = Math.Max(0, Math.Min(page, pages - 1));
            string[] ru = { "название", "количество", "расстояние до ближайшего", "число сундуков" };
            string[] en = { "name", "quantity", "nearest distance", "chest count" };
            sort.GetComponentInChildren<Text>().text = plugin.T("Сортировка: " + ru[(int)plugin.SortOrder], "Sort: " + en[(int)plugin.SortOrder]);
            direction.GetComponentInChildren<Text>().text = plugin.SortDescending ? plugin.T("DESC — по убыванию", "DESC — descending") : plugin.T("ASC — по возрастанию", "ASC — ascending");
            summary.text = SearchText.Terms(plugin.LastQuery).Length == 0 && !plugin.ShowWithoutInput ? plugin.T("Введите название или включите «Показывать без ввода». Поиск понимает русские и английские названия.", "Enter a name or enable Show without input. Russian and English names are searchable.")
                : plugin.T("Предметов: " + report.Items.Count + " видов, " + report.Quantity + " шт. Сундуков: " + report.Results.Count + ". Радиус: " + plugin.Radius.ToString("0") + " м.",
                    "Items: " + report.Items.Count + " types, " + report.Quantity + " total. Chests: " + report.Results.Count + ". Radius: " + plugin.Radius.ToString("0") + " m.")
                    + (report.Pending > 0 ? plugin.T("\nЗанятые или ожидающие синхронизации: " + report.Pending + ".", "\nBusy or awaiting synchronization: " + report.Pending + ".") : "")
                    + (report.Limited ? plugin.T(" Результаты ограничены.", " Results limited.") : "");
            for (int i = 0; i < PageSize; i++)
            {
                int index = page * PageSize + i;
                bool available = index < report.Items.Count;
                rows[i].interactable = available;
                icons[i].enabled = false; icons[i].sprite = null;
                string value = "";
                if (available)
                {
                    var result = report.Items[index];
                    value = (result.Key == selectedKey ? "› " : "") + SearchText.Safe(result.Name, 70) + " — " + result.Quantity + plugin.T(" шт.", " items")
                        + "\n" + plugin.T("Сундуков: ", "Chests: ") + result.ChestCount + plugin.T(" • ближайший: ", " • nearest: ")
                        + result.NearestDistance.ToString("0.0", CultureInfo.InvariantCulture) + plugin.T(" м", " m");
                    icons[i].sprite = result.Icon; icons[i].enabled = result.Icon != null;
                }
                rows[i].GetComponentInChildren<Text>().text = value;
            }
            previous.interactable = page > 0; next.interactable = page + 1 < pages;
            footer.text = (page + 1) + " / " + pages + plugin.T("  •  Только доступные загруженные сундуки", "  •  Accessible loaded chests only");
            var selected = Selected();
            highlight.interactable = selected != null;
            highlight.GetComponentInChildren<Text>().text = plugin.T("Подсветить все сундуки с предметом", "Highlight all chests containing the item") + (selected == null ? "" : " (" + selected.ChestCount + ")");
            selection.text = selected == null ? plugin.T("Выберите предмет в списке. Подсветка включается только кнопкой ниже.", "Select an item. Only the button below starts highlighting.")
                : SearchText.Safe(selected.Name, 64) + plugin.T(" • подсветка на ", " • mark for ") + plugin.MarkerSeconds.ToString("0") + plugin.T(" с", " s");
            var active = new List<Selectable>();
            foreach (var control in controls) if (control != null && control.IsInteractable()) active.Add(control);
            for (int i = 0; i < active.Count; i++)
            {
                var prev = active[(i + active.Count - 1) % active.Count]; var following = active[(i + 1) % active.Count];
                active[i].navigation = new Navigation { mode = Navigation.Mode.Explicit, selectOnUp = prev, selectOnLeft = prev, selectOnDown = following, selectOnRight = following };
            }
        }
        private Text Label(string value, float x, float y, float width, float height, int size, bool heading)
        {
            var gui = GUIManager.Instance; var center = new Vector2(0.5f, 0.5f);
            Text text = gui.CreateText(value, panel.transform, center, center, new Vector2(x, y), heading ? gui.AveriaSerifBold : gui.AveriaSerif,
                size, heading ? gui.ValheimOrange : gui.ValheimBeige, true, Color.black, width, height, false).GetComponent<Text>();
            text.supportRichText = false; text.raycastTarget = false; text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Truncate;
            text.resizeTextForBestFit = true; text.resizeTextMinSize = 14; text.resizeTextMaxSize = size;
            return text;
        }
        private void BuildCheckbox(int created)
        {
            var holder = new GameObject("ShowWithoutInput", typeof(RectTransform), typeof(Toggle));
            holder.layer = GUIManager.UILayer; holder.transform.SetParent(panel.transform, false);
            var rect = holder.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
            rect.anchoredPosition = new Vector2(0, 246); rect.sizeDelta = new Vector2(780, 38);
            var square = new GameObject("Box", typeof(RectTransform), typeof(Image));
            square.layer = GUIManager.UILayer; square.transform.SetParent(holder.transform, false);
            var box = square.GetComponent<RectTransform>();
            box.anchorMin = box.anchorMax = new Vector2(0, .5f); box.anchoredPosition = new Vector2(17, 0); box.sizeDelta = new Vector2(30, 30);
            var background = square.GetComponent<Image>(); background.color = new Color(.45f, .36f, .25f, 1);
            var checkObject = new GameObject("Check", typeof(RectTransform), typeof(Image));
            checkObject.layer = GUIManager.UILayer; checkObject.transform.SetParent(square.transform, false);
            var check = checkObject.GetComponent<RectTransform>();
            check.anchorMin = Vector2.zero; check.anchorMax = Vector2.one; check.offsetMin = new Vector2(6, 6); check.offsetMax = new Vector2(-6, -6);
            var graphic = checkObject.GetComponent<Image>(); graphic.color = GUIManager.Instance.ValheimOrange; graphic.raycastTarget = false;
            showAll = holder.GetComponent<Toggle>(); showAll.targetGraphic = background; showAll.graphic = graphic;
            showAll.isOn = plugin.ShowWithoutInput;
            var label = Label(plugin.T("Показывать без ввода", "Show without input"), 29, 246, 710, 38, 19, false);
            label.alignment = TextAnchor.MiddleLeft; label.raycastTarget = true;
            label.transform.SetParent(holder.transform, false);
            label.rectTransform.anchorMin = Vector2.zero; label.rectTransform.anchorMax = Vector2.one;
            label.rectTransform.offsetMin = new Vector2(47, 0); label.rectTransform.offsetMax = new Vector2(-2, 0);
            showAll.onValueChanged.AddListener(value =>
            {
                if (generation != created || !IsVisible) return;
                try
                {
                    if (!ValidContext()) { Hide(); return; }
                    plugin.ShowWithoutInput = value; page = 0; selectedKey = null;
                    report = new SearchReport(); searchAt = 0; Repaint();
                }
                catch (Exception error) { Hide(); plugin.Report(error); }
            });
            controls.Add(showAll);
        }
        private Button ButtonAt(string title, float x, float y, float width, float height, Action action)
        {
            int created = generation; var center = new Vector2(0.5f, 0.5f);
            var button = GUIManager.Instance.CreateButton(title, panel.transform, center, center, new Vector2(x, y), width, height).GetComponent<Button>();
            Text text = button.GetComponentInChildren<Text>(); text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Truncate;
            text.resizeTextForBestFit = true; text.resizeTextMinSize = 14; text.resizeTextMaxSize = 20;
            var sound = button.GetComponent<ButtonSfx>(); if (sound != null) sound.m_selectSfxPrefab = null;
            button.onClick.AddListener(() =>
            {
                if (generation != created || !IsVisible) return;
                try { if (!ValidContext()) { Hide(); return; } action(); }
                catch (Exception error) { Hide(); plugin.Report(error); }
            });
            controls.Add(button); return button;
        }
        public void Hide()
        {
            generation++;
            try
            {
                if (overlay != null)
                {
                    if (input != null) input.DeactivateInputField();
                    if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null
                        && EventSystem.current.currentSelectedGameObject.transform.IsChildOf(overlay.transform)) EventSystem.current.SetSelectedGameObject(null);
                    overlay.SetActive(false); UnityEngine.Object.Destroy(overlay);
                }
            }
            catch (Exception error) { plugin.Report(error); }
            finally
            {
                overlay = null; panel = null; input = null; player = null; network = null; controls.Clear();
                report = new SearchReport(); selectedKey = null; summary = null; footer = null; selection = null;
                showAll = null; Array.Clear(rows, 0, rows.Length); Array.Clear(icons, 0, icons.Length);
                try { inputBlock.Release(); } catch (Exception error) { plugin.Report(error); }
            }
        }
    }
}
