// Optional native-engine probe. Excluded from the release plugin.
using System;
using System.Reflection;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ValheimModPack.PinRemoval
{
    public static class PinPresentationNativeChecks
    {
        private static int checks;

        public static string Run()
        {
            if (GUIManager.CustomGUIFront == null) return "SKIP: pin presentation checks require an existing Jotunn canvas.";
            checks = 0;
            int baseline = Blocks();
            GameObject selected = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
            var hud = new PinSuggestionHud();
            var menu = new PinActionMenuView(delegate(string key)
            {
                switch (key)
                {
                    case "action_title": return "Map pin";
                    case "action_rename": return "Rename";
                    case "action_delete": return "Delete";
                    default: return "Cancel";
                }
            });
            Texture2D texture = null; Sprite sprite = null;
            try
            {
                texture = new Texture2D(8, 8);
                sprite = Sprite.Create(texture, new Rect(0, 0, 8, 8), new Vector2(0.5f, 0.5f));
                hud.Show(sprite, new String('Ж', 220) + "\nsecond line", "Ctrl+G", "Place pin");
                GameObject card = Field<GameObject>(hud, "card");
                Check(hud.IsVisible && card.transform.parent == GUIManager.CustomGUIFront.transform, "HUD uses the existing Jotunn canvas");
                Check(card.GetComponent<RectTransform>().sizeDelta.y == 78, "HUD has compact fixed height");
                Check(card.GetComponentsInChildren<Canvas>(true).Length == 0, "HUD creates no extra canvas");
                Check(card.GetComponentsInChildren<Selectable>(true).Length == 0, "HUD has no focusable controls");
                Check(card.GetComponentsInChildren<ButtonSfx>(true).Length == 0, "HUD has no sound-producing buttons");
                var group = card.GetComponent<CanvasGroup>();
                Check(!group.blocksRaycasts && !group.interactable, "HUD group never intercepts input");
                foreach (var graphic in card.GetComponentsInChildren<Graphic>(true))
                    Check(!graphic.raycastTarget, "Every HUD graphic ignores raycasts");
                Text caption = Field<Text>(hud, "captionText"), hint = Field<Text>(hud, "hintText");
                Check(!caption.supportRichText && !hint.supportRichText, "HUD labels never interpret markup");
                Check(caption.text.EndsWith("…") && caption.text.IndexOf('\n') < 0,
                    "Long captions fit on one line with ellipsis (length=" + caption.text.Length + ", width=" + caption.preferredWidth + ", font=" + (caption.font == null ? "null" : caption.font.name) + ")");
                Check(caption.preferredWidth <= caption.rectTransform.sizeDelta.x + 0.5f, "Rendered native caption width stays inside its label rectangle");
                Check(Field<Image>(hud, "iconImage").sprite == sprite, "HUD displays the supplied pin sprite");
                Check(Blocks() == baseline, "Showing HUD acquires no input lease");
                Check(EventSystem.current == null || EventSystem.current.currentSelectedGameObject == selected, "Showing HUD preserves existing focus");
                hud.Show(sprite, "Fresh caption", "Shift+K", "Place pin");
                Check(Field<GameObject>(hud, "card") == card, "Refreshing HUD reuses its widgets");
                Check(caption.text == "Fresh caption" && hint.text.Contains("Shift+K") && !hint.text.Contains("Ctrl+G"), "Refresh displays current caption and configured shortcut");
                UnityEngine.Object.DestroyImmediate(card);
                hud.Show(null, "Rebuilt", "Alt+H", "Place pin");
                Check(hud.IsVisible && !Field<Image>(hud, "iconImage").enabled, "Destroyed HUD references rebuild safely and absent icon is hidden");
                hud.Hide(); hud.Hide();
                Check(!hud.IsVisible && ReferenceEquals(Field<GameObject>(hud, "card"), null) && Blocks() == baseline, "Repeated HUD hide clears references without changing input leases");
                hud.Dispose(); hud.Show(sprite, "Disposed", "F9", "Place pin");
                Check(!hud.IsVisible, "Disposed HUD cannot reopen");

                int renamed = 0, deleted = 0, cancelled = 0;
                bool releasedBeforeAction = false;
                menu.Show("<b>Harbour</b>", delegate
                { ++renamed; releasedBeforeAction = !menu.IsVisible && Blocks() == baseline; },
                    delegate { ++deleted; }, delegate { ++cancelled; });
                GameObject overlay = Field<GameObject>(menu, "overlay");
                int ownDelta = Blocks() - baseline;
                Check(menu.IsVisible && (ownDelta == 0 || ownDelta == 1), "Action menu opens with at most one counted input request");
                Check(overlay.GetComponentsInChildren<Button>().Length == 3, "Action menu contains exactly Rename, Delete and Cancel");
                foreach (var button in overlay.GetComponentsInChildren<Button>())
                {
                    var sound = button.GetComponent<ButtonSfx>();
                    Check(sound == null || sound.m_selectSfxPrefab == null, "Action buttons suppress selection sounds");
                    foreach (var text in button.GetComponentsInChildren<Text>(true))
                        Check(!text.supportRichText, "Action button captions never interpret markup");
                }
                Button oldRename = Find(overlay, "Rename");
                oldRename.onClick.Invoke(); oldRename.onClick.Invoke();
                Check(renamed == 1 && deleted == 0 && cancelled == 0 && releasedBeforeAction, "Rename dispatches once after closing and releasing its input lease");
                menu.Show("New pin", delegate { ++renamed; }, delegate { ++deleted; }, delegate { ++cancelled; });
                oldRename.onClick.Invoke();
                Check(renamed == 1 && menu.IsVisible, "A stale menu callback cannot affect its replacement");
                Find(Field<GameObject>(menu, "overlay"), "Delete").onClick.Invoke();
                Check(deleted == 1 && !menu.IsVisible && Blocks() == baseline, "Delete dispatches after the menu closes");
                menu.Show("New pin", delegate { ++renamed; }, delegate { ++deleted; }, delegate { ++cancelled; });
                Find(Field<GameObject>(menu, "overlay"), "Cancel").onClick.Invoke();
                Check(cancelled == 1 && !menu.IsVisible && Blocks() == baseline, "Cancel only cancels and releases its request");
                menu.Show("New pin", delegate { }, delegate { }, delegate { });
                UnityEngine.Object.DestroyImmediate(Field<GameObject>(menu, "overlay"));
                menu.CleanupHidden();
                Check(!menu.IsVisible && !Field<bool>(menu, "ownsInputBlock") && Blocks() == baseline, "Destroyed action menu releases its owned request during cleanup");
                menu.Show("New pin", delegate { }, delegate { }, delegate { });
                UnityEngine.Object.DestroyImmediate(Field<GameObject>(menu, "overlay"));
                menu.Show("Replacement", delegate { }, delegate { }, delegate { });
                Check(menu.IsVisible && Blocks() == baseline + ownDelta, "Reopening after destruction replaces the owned request");
                menu.Hide(); menu.Hide(); menu.Dispose();
                menu.Show("Disposed", delegate { }, delegate { }, delegate { });
                Check(!menu.IsVisible && Blocks() == baseline, "Repeated hide and dispose remain idempotent");
                return "PASS: " + checks + " native pin suggestion/action menu assertions; "
                    + (ownDelta == 1 ? "live input lease exercised." : "menu/headless input block is a no-op; gameplay lease requires a main scene.");
            }
            finally
            {
                hud.Dispose(); menu.Dispose();
                if (sprite != null) UnityEngine.Object.DestroyImmediate(sprite);
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
                Check(Blocks() == baseline, "Probe leaves input-block count unchanged");
            }
        }

        private static int Blocks()
        { return (int)typeof(GUIManager).GetField("InputBlockRequests", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null); }

        private static T Field<T>(object value, string name)
        { return (T)value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(value); }

        private static Button Find(GameObject parent, string label)
        {
            foreach (var button in parent.GetComponentsInChildren<Button>())
                foreach (var text in button.GetComponentsInChildren<Text>()) if (text.text == label) return button;
            throw new InvalidOperationException("Missing pin action button: " + label);
        }

        private static void Check(bool condition, string label)
        { ++checks; if (!condition) throw new InvalidOperationException("Pin presentation native check: " + label); }
    }
}
