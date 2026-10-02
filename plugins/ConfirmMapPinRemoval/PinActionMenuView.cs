using System;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ValheimModPack.PinRemoval
{
    public sealed class PinActionMenuView : IDisposable
    {
        private static readonly Vector2 Center = new Vector2(0.5f, 0.5f);
        private readonly Func<string, string> localize;
        private GameObject overlay;
        private bool ownsInputBlock;
        private bool disposed;
        private int generation;

        public PinActionMenuView(Func<string, string> localize) { this.localize = localize; }
        public bool IsVisible { get { return overlay != null && overlay.activeInHierarchy; } }

        public void Show(string caption, Action rename, Action delete, Action cancel)
        {
            if (disposed) return;
            Hide();
            var front = GUIManager.CustomGUIFront;
            if (front == null) throw new InvalidOperationException("Jotunn front canvas is not ready");
            try
            {
                overlay = new GameObject("ConfirmMapPinRemoval.PinActions", typeof(RectTransform), typeof(Image));
                overlay.layer = GUIManager.UILayer; overlay.transform.SetParent(front.transform, false);
                var rect = overlay.GetComponent<RectTransform>();
                rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
                rect.offsetMin = rect.offsetMax = Vector2.zero;
                var dim = overlay.GetComponent<Image>();
                dim.color = new Color(0, 0, 0, 0.45f); dim.raycastTarget = true;
                var gui = GUIManager.Instance;
                var panel = gui.CreateWoodpanel(overlay.transform, Center, Center, Vector2.zero, 570, 260, false);
                panel.name = "ConfirmMapPinRemoval.PinActions.WoodPanel";
                var group = panel.AddComponent<CanvasGroup>();
                group.interactable = true; group.blocksRaycasts = true;
                Label(panel.transform, Text("action_title"), 72, 44, 28, true);
                bool russian = Localization.instance != null && Localization.instance.GetSelectedLanguage() == "Russian";
                Label(panel.transform, PinLabel.Format(caption, russian), 10, 58, 22, false);
                var renameButton = Button(panel.transform, Text("action_rename"), -177, rename);
                var deleteButton = Button(panel.transform, Text("action_delete"), 0, delete);
                var cancelButton = Button(panel.transform, Text("action_cancel"), 177, cancel);
                renameButton.navigation = new Navigation { mode = Navigation.Mode.Explicit, selectOnRight = deleteButton };
                deleteButton.navigation = new Navigation { mode = Navigation.Mode.Explicit, selectOnLeft = renameButton, selectOnRight = cancelButton };
                cancelButton.navigation = new Navigation { mode = Navigation.Mode.Explicit, selectOnLeft = deleteButton };
                // Jotunn increments before refreshing the camera, so cleanup must own the
                // request even if that refresh throws during construction.
                ownsInputBlock = true; GUIManager.BlockInput(true);
                overlay.transform.SetAsLastSibling();
                if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(cancelButton.gameObject);
            }
            catch { Hide(); throw; }
        }

        private string Text(string key)
        {
            string value = localize == null ? null : localize(key);
            if (!String.IsNullOrEmpty(value) && value != key) return value;
            bool russian = Localization.instance != null && Localization.instance.GetSelectedLanguage() == "Russian";
            switch (key)
            {
                case "action_title": return russian ? "Метка на карте" : "Map pin";
                case "action_rename": return russian ? "Переименовать" : "Rename";
                case "action_delete": return russian ? "Удалить" : "Delete";
                default: return russian ? "Отмена" : "Cancel";
            }
        }

        private static void Label(Transform parent, string value, float y, float height, int size, bool title)
        {
            var gui = GUIManager.Instance;
            var text = gui.CreateText(value, parent, Center, Center, new Vector2(0, y),
                title ? gui.AveriaSerifBold : gui.AveriaSerif, size,
                title ? gui.ValheimOrange : gui.ValheimBeige, true, Color.black, 510, height, false).GetComponent<Text>();
            text.supportRichText = false; text.raycastTarget = false; text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Truncate;
            text.resizeTextForBestFit = true; text.resizeTextMinSize = title ? 22 : 16; text.resizeTextMaxSize = size;
        }

        private Button Button(Transform parent, string value, float x, Action action)
        {
            var button = GUIManager.Instance.CreateButton(value, parent, Center, Center, new Vector2(x, -76), 160, 48).GetComponent<Button>();
            var sound = button.GetComponent<ButtonSfx>();
            if (sound != null) sound.m_selectSfxPrefab = null;
            foreach (var text in button.GetComponentsInChildren<Text>(true))
            {
                text.supportRichText = false; text.raycastTarget = false;
                text.resizeTextForBestFit = true; text.resizeTextMinSize = 14; text.resizeTextMaxSize = 20;
            }
            int created = generation;
            button.interactable = action != null;
            button.onClick.AddListener(delegate
            {
                if (disposed || generation != created || !IsVisible || action == null) return;
                // Closing first invalidates every old callback and releases this lease
                // before Rename or Delete opens its own dialog.
                Hide(); action();
            });
            return button;
        }

        public void CleanupHidden()
        {
            if (!IsVisible && (ownsInputBlock || !ReferenceEquals(overlay, null))) Hide();
        }

        public void Hide()
        {
            ++generation;
            try
            {
                if (overlay != null)
                {
                    try
                    {
                        var events = EventSystem.current;
                        if (events != null && events.currentSelectedGameObject != null
                            && events.currentSelectedGameObject.transform.IsChildOf(overlay.transform))
                            events.SetSelectedGameObject(null);
                    }
                    finally { overlay.SetActive(false); UnityEngine.Object.Destroy(overlay); }
                }
            }
            finally
            {
                overlay = null;
                if (ownsInputBlock) { ownsInputBlock = false; GUIManager.BlockInput(false); }
            }
        }

        public void Dispose() { disposed = true; Hide(); }
    }
}
