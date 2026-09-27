using System;
using System.Collections.Generic;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
namespace ValheimModPack.PinRemoval
{
    public sealed class PinHistoryWindow
    {
        private GameObject overlay, content;
        private bool ownsInputBlock;
        private Button previous, next, deleted, current;
        private Text footer;
        public bool IsVisible { get { return overlay != null && overlay.activeInHierarchy; } }
        private static readonly Vector2 Center = new Vector2(.5f, .5f);
        public void Show(Action close, Action<bool> tab, Action<int> changePage)
        {
            if (overlay != null) throw new InvalidOperationException("Map history already open");
            var front = GUIManager.CustomGUIFront;
            if (front == null) throw new InvalidOperationException("Jotunn canvas is not ready");
            bool ru = Localization.instance.GetSelectedLanguage() == "Russian";
            try
            {
                overlay = new GameObject("ConfirmMapPinRemoval.History", typeof(RectTransform), typeof(Image));
                overlay.transform.SetParent(front.transform, false); overlay.layer = GUIManager.UILayer;
                var rect = overlay.GetComponent<RectTransform>(); rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
                var dim = overlay.GetComponent<Image>(); dim.color = new Color(0, 0, 0, .55f); dim.raycastTarget = true;
                var panel = GUIManager.Instance.CreateWoodpanel(overlay.transform, Center, Center, Vector2.zero, 840, 690, false);
                panel.name = "ConfirmMapPinRemoval.HistoryWoodPanel";
                Text(panel.transform, ru ? "Метки карты" : "Map pins", 0, 295, 740, 46, 30, true);
                deleted = Button(panel.transform, ru ? "Удалённые" : "Deleted", -180, 242, 320, 42, () => tab(true));
                current = Button(panel.transform, ru ? "На карте · сведения" : "On map · details", 180, 242, 320, 42, () => tab(false));
                content = new GameObject("Rows", typeof(RectTransform)); content.layer = GUIManager.UILayer;
                content.transform.SetParent(panel.transform, false);
                var body = content.GetComponent<RectTransform>(); body.anchorMin = Center; body.anchorMax = Center;
                body.sizeDelta = new Vector2(780, 450); body.anchoredPosition = new Vector2(0, -10);
                previous = Button(panel.transform, "‹", -320, -264, 70, 38, () => changePage(-1));
                next = Button(panel.transform, "›", 320, -264, 70, 38, () => changePage(1));
                footer = Text(panel.transform, "", 0, -264, 530, 36, 18, false); footer.alignment = TextAnchor.MiddleCenter;
                var closeButton = Button(panel.transform, ru ? "Закрыть" : "Close", 0, -310, 210, 40, close);
                ownsInputBlock = true; GUIManager.BlockInput(true); overlay.transform.SetAsLastSibling();
                if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(closeButton.gameObject);
            }
            catch { Hide(); throw; }
        }
        public void Render(IList<HistoryRow> rows, bool deletedTab, int page, int pages, int count, bool ru)
        {
            if (!IsVisible) return;
            foreach (Transform child in content.transform) { child.gameObject.SetActive(false); UnityEngine.Object.Destroy(child.gameObject); }
            deleted.interactable = !deletedTab; current.interactable = deletedTab;
            previous.interactable = page > 0; next.interactable = page + 1 < pages;
            footer.text = (page + 1) + " / " + pages + " · " + count + (ru ? " меток" : " pins");
            if (rows.Count == 0)
            {
                var empty = Text(content.transform, deletedTab
                    ? (ru ? "Здесь появятся метки, удалённые после установки этого обновления.\nСохраняются последние 500 удалений для этого мира и персонажа."
                        : "Pins deleted after installing this update appear here.\nThe last 500 deletions are saved for this world and character.")
                    : (ru ? "Сохранённых меток нет." : "No saved pins."), 0, 60, 710, 180, 22, false);
                empty.alignment = TextAnchor.MiddleCenter; return;
            }
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i]; float y = 197 - i * 76;
                var name = Text(content.transform, row.Title, deletedTab ? -83 : 0, y, deletedTab ? 560 : 750, 25, 22, true);
                name.alignment = TextAnchor.MiddleLeft;
                var details = Text(content.transform, row.Details, deletedTab ? -83 : 0, y - 30, deletedTab ? 560 : 750, 46, 16, false);
                details.alignment = TextAnchor.UpperLeft;
                if (deletedTab) Button(content.transform, row.ActionLabel, 298, y - 12, 145, 40, row.Activate).interactable = row.Enabled;
            }
        }
        private static Text Text(Transform parent, string value, float x, float y, float width, float height, int size, bool title)
        {
            var gui = GUIManager.Instance;
            var text = gui.CreateText(value, parent, Center, Center, new Vector2(x, y), title ? gui.AveriaSerifBold : gui.AveriaSerif,
                size, title ? gui.ValheimOrange : gui.ValheimBeige, true, Color.black, width, height, false).GetComponent<Text>();
            text.supportRichText = false; text.raycastTarget = false; text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate; text.alignment = TextAnchor.MiddleCenter; return text;
        }
        private static Button Button(Transform parent, string value, float x, float y, float width, float height, Action click)
        {
            var button = GUIManager.Instance.CreateButton(value, parent, Center, Center, new Vector2(x, y), width, height).GetComponent<Button>();
            var sound = button.GetComponent<ButtonSfx>(); if (sound != null) sound.m_selectSfxPrefab = null;
            button.onClick.AddListener(() => { if (click != null) click(); }); return button;
        }
        public void Hide()
        {
            try
            {
                if (overlay != null)
                {
                    if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null
                        && EventSystem.current.currentSelectedGameObject.transform.IsChildOf(overlay.transform))
                        EventSystem.current.SetSelectedGameObject(null);
                    overlay.SetActive(false); UnityEngine.Object.Destroy(overlay); overlay = null; content = null;
                }
            }
            finally { if (ownsInputBlock) { ownsInputBlock = false; GUIManager.BlockInput(false); } }
        }
    }
}
