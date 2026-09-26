using System;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
namespace ValheimModPack.PinRemoval
{
    public sealed class WoodDialogView : IDialogView
    {
        private GameObject overlay;
        private bool ownsInputBlock;
        public bool IsVisible { get { return overlay != null && overlay.activeInHierarchy; } }
        public void Show(string name, Action confirm, Action cancel)
        {
            if (overlay != null) throw new InvalidOperationException("Confirmation window already exists");
            var front = GUIManager.CustomGUIFront;
            if (front == null) throw new InvalidOperationException("Jotunn front canvas is not ready");
            bool ru = Localization.instance.GetSelectedLanguage() == "Russian";
            try
            {
                overlay = new GameObject("ConfirmMapPinRemoval.Modal", typeof(RectTransform), typeof(Image));
                overlay.transform.SetParent(front.transform, false);
                overlay.layer = GUIManager.UILayer;
                var rect = overlay.GetComponent<RectTransform>();
                rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
                var dim = overlay.GetComponent<Image>();
                dim.color = new Color(0, 0, 0, 0.55f); dim.raycastTarget = true;
                var gui = GUIManager.Instance;
                var center = new Vector2(0.5f, 0.5f);
                var panel = gui.CreateWoodpanel(overlay.transform, center, center, Vector2.zero, 540f, 280f, false);
                panel.name = "ConfirmMapPinRemoval.WoodPanel";
                var group = panel.AddComponent<CanvasGroup>();
                group.interactable = true; group.blocksRaycasts = true;
                Text title = gui.CreateText(ru ? "Удаление метки" : "Remove map pin", panel.transform,
                    center, center, new Vector2(0, 88), gui.AveriaSerifBold, 30, gui.ValheimOrange,
                    true, Color.black, 480, 48, false).GetComponent<Text>();
                title.alignment = TextAnchor.MiddleCenter; title.supportRichText = false;
                string safeName = PinLabel.Format(name, ru);
                Text body = gui.CreateText(ru ? "Удалить метку «" + safeName + "»?" : "Remove pin “" + safeName + "”?",
                    panel.transform, center, center, new Vector2(0, 16), gui.AveriaSerif, 22,
                    gui.ValheimBeige, true, Color.black, 470, 104, false).GetComponent<Text>();
                body.alignment = TextAnchor.MiddleCenter; body.supportRichText = false;
                body.horizontalOverflow = HorizontalWrapMode.Wrap;
                body.verticalOverflow = VerticalWrapMode.Truncate;
                body.resizeTextForBestFit = true; body.resizeTextMinSize = 16; body.resizeTextMaxSize = 22;
                var cancelButton = gui.CreateButton(ru ? "Отмена" : "Cancel", panel.transform, center, center,
                    new Vector2(-120, -88), 200, 48).GetComponent<Button>();
                var deleteButton = gui.CreateButton(ru ? "Удалить" : "Delete", panel.transform, center, center,
                    new Vector2(120, -88), 200, 48).GetComponent<Button>();
                cancelButton.onClick.AddListener(() => cancel());
                deleteButton.onClick.AddListener(() => confirm());
                var cancelNavigation = new Navigation { mode = Navigation.Mode.Explicit, selectOnRight = deleteButton };
                var deleteNavigation = new Navigation { mode = Navigation.Mode.Explicit, selectOnLeft = cancelButton };
                cancelButton.navigation = cancelNavigation; deleteButton.navigation = deleteNavigation;
                // BlockInput increments its counter before updating the game camera, which can throw.
                ownsInputBlock = true; GUIManager.BlockInput(true);
                overlay.transform.SetAsLastSibling();
                if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(cancelButton.gameObject);
            }
            catch { Hide(); throw; }
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
                    overlay.SetActive(false);
                    UnityEngine.Object.Destroy(overlay);
                    overlay = null;
                }
            }
            finally
            {
                if (ownsInputBlock) { ownsInputBlock = false; GUIManager.BlockInput(false); }
            }
        }
    }
}
