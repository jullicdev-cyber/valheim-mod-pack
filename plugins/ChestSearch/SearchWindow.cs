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
        private readonly List<Selectable> controls = new List<Selectable>();
        private GameObject overlay, panel;
        private Player player;
        private ZNet network;
        private InputField input;
        private Text summary, footer;
        private Button previous, next;
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
                var front = GUIManager.CustomGUIFront;
                overlay = new GameObject("ChestSearch.Modal", typeof(RectTransform), typeof(Image));
                overlay.transform.SetParent(front.transform, false); overlay.layer = GUIManager.UILayer;
                var rect = overlay.GetComponent<RectTransform>();
                rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
                var dim = overlay.GetComponent<Image>(); dim.color = new Color(0, 0, 0, 0.58f); dim.raycastTarget = true;
                var gui = GUIManager.Instance;
                var center = new Vector2(0.5f, 0.5f);
                panel = gui.CreateWoodpanel(overlay.transform, center, center, Vector2.zero, 760, 660, false);
                panel.name = "ChestSearch.WoodPanel";
                var group = panel.AddComponent<CanvasGroup>(); group.interactable = true; group.blocksRaycasts = true;
                Label(plugin.T("Поиск по сундукам", "Search nearby chests"), 0, 285, 620, 46, 30, true);
                ButtonAt("X", 326, 285, 44, 40, Hide);
                input = gui.CreateInputField(panel.transform, center, center, new Vector2(-66, 225), InputField.ContentType.Standard,
                    plugin.T("Название предмета: железо, Iron, FineWood…", "Item name: Iron, FineWood…"), 18, 552, 42).GetComponent<InputField>();
                input.characterLimit = 64; input.lineType = InputField.LineType.SingleLine;
                input.textComponent.supportRichText = false;
                Text placeholder = input.placeholder as Text; if (placeholder != null) placeholder.supportRichText = false;
                input.text = plugin.LastQuery ?? "";
                int currentGeneration = generation;
                input.onValueChanged.AddListener(value =>
                {
                    if (currentGeneration != generation || !IsVisible) return;
                    plugin.LastQuery = SearchText.Safe(value, 64); page = 0; searchAt = Time.unscaledTime + 0.25f;
                });
                controls.Add(input);
                ButtonAt(plugin.T("Обновить", "Refresh"), 279, 225, 132, 42, () => { searchAt = 0; });
                summary = Label("", 0, 171, 680, 54, 18, false);
                for (int i = 0; i < PageSize; i++)
                {
                    int slot = i;
                    rows[i] = ButtonAt("", 0, 108 - i * 58, 684, 52, () => Select(slot));
                    var text = rows[i].GetComponentInChildren<Text>();
                    text.alignment = TextAnchor.MiddleLeft; text.fontSize = 17;
                    text.resizeTextMinSize = 14; text.resizeTextMaxSize = 17;
                }
                previous = ButtonAt("<", -295, -245, 88, 38, () => { if (page > 0) page--; Repaint(); });
                next = ButtonAt(">", 295, -245, 88, 38, () => { page++; Repaint(); });
                footer = Label("", 0, -245, 475, 36, 17, false);
                Label(plugin.T("Выберите сундук, чтобы отметить его на " + plugin.MarkerSeconds.ToString("0") + " с.  Esc — закрыть.",
                    "Select a chest to mark it for " + plugin.MarkerSeconds.ToString("0") + " s.  Esc closes."), 0, -292, 685, 36, 16, false);
                page = 0; report = new SearchReport(); searchAt = Time.unscaledTime + 0.05f;
                Repaint();
                inputBlock.Acquire();
                overlay.transform.SetAsLastSibling();
                if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(input.gameObject);
                input.ActivateInputField();
            }
            catch (Exception error) { Hide(); plugin.Report(error); }
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
                    report = plugin.Search.Search(player, plugin.LastQuery, plugin.Radius);
                    Repaint();
                }
                if (Time.unscaledTime >= nextScale)
                {
                    nextScale = Time.unscaledTime + 0.5f;
                    var rect = overlay.GetComponent<RectTransform>();
                    float scale = Mathf.Min(1, Mathf.Min(rect.rect.width / 780, rect.rect.height / 690));
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
            if (index < 0 || index >= report.Results.Count) return;
            if (!plugin.Locate(report.Results[index]))
            {
                summary.text = plugin.T("Сундук изменился или недоступен. Обновляю результаты…", "The chest changed or became unavailable. Refreshing…");
                searchAt = Time.unscaledTime + 0.5f;
            }
        }
        private void Repaint()
        {
            int pages = Math.Max(1, (report.Results.Count + PageSize - 1) / PageSize);
            page = Math.Max(0, Math.Min(page, pages - 1));
            summary.text = SearchText.Terms(plugin.LastQuery).Length == 0 ? plugin.T("Введите название предмета. Поиск по русскому, английскому названию и имени prefab.", "Enter an item name. Search supports Russian, English and prefab names.")
                : plugin.T("Найдено: " + report.Quantity + " шт. в " + report.Results.Count + " сундуках. Проверено: " + report.Checked + ". Радиус: " + plugin.Radius.ToString("0") + " м.",
                    "Found: " + report.Quantity + " items in " + report.Results.Count + " chests. Checked: " + report.Checked + ". Radius: " + plugin.Radius.ToString("0") + " m.")
                    + (report.Pending > 0 ? plugin.T("\nЗанятые или ожидающие синхронизации: " + report.Pending + ".", "\nBusy or awaiting synchronization: " + report.Pending + ".") : "")
                    + (report.Limited ? plugin.T(" Результаты ограничены.", " Results limited.") : "");
            for (int i = 0; i < PageSize; i++)
            {
                int index = page * PageSize + i;
                bool available = index < report.Results.Count;
                rows[i].interactable = available;
                string value = "";
                if (available)
                {
                    var result = report.Results[index];
                    value = "  " + SearchText.Safe(result.ChestName, 38) + " — " + result.Snapshot.Distance.ToString("0.0", CultureInfo.InvariantCulture)
                        + plugin.T(" м", " m") + " — " + result.Quantity + plugin.T(" шт.", " items")
                        + "\n  " + SearchText.Safe(result.ItemSummary, 100);
                }
                rows[i].GetComponentInChildren<Text>().text = value;
            }
            previous.interactable = page > 0; next.interactable = page + 1 < pages;
            footer.text = (page + 1) + " / " + pages + plugin.T("  •  Только доступные загруженные сундуки", "  •  Accessible loaded chests only");
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
                    if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null
                        && EventSystem.current.currentSelectedGameObject.transform.IsChildOf(overlay.transform)) EventSystem.current.SetSelectedGameObject(null);
                    overlay.SetActive(false); UnityEngine.Object.Destroy(overlay);
                }
            }
            catch (Exception error) { plugin.Report(error); }
            finally
            {
                overlay = null; panel = null; input = null; player = null; network = null; controls.Clear();
                report = new SearchReport(); Array.Clear(rows, 0, rows.Length);
                try { inputBlock.Release(); } catch (Exception error) { plugin.Report(error); }
            }
        }
    }
}
