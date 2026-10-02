using System;
using System.Globalization;
using System.Text;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimModPack.PinRemoval
{
    // Presentation only. The controller owns gameplay visibility and supplies localized
    // labels plus the current configured shortcut. This view never owns input or focus.
    public sealed class PinSuggestionHud : IDisposable
    {
        private const float DefaultWidth = 330;
        private const float Height = 78;
        private static readonly Vector2 Anchor = new Vector2(1, 0.5f);
        private static readonly Vector2 Offset = new Vector2(-28, -92);
        private GameObject card;
        private Transform parent;
        private Image iconImage;
        private RectTransform accent;
        private Text captionText;
        private Text hintText;
        private string captionValue;
        private string hintValue;
        private float width;
        private bool disposed;

        public bool IsVisible { get { return card != null && card.activeInHierarchy; } }

        public void Show(Sprite icon, string caption, string hotkey, string action)
        {
            if (disposed) return;
            var front = GUIManager.CustomGUIFront;
            if (front == null) { Hide(); return; }
            if (card == null || parent != front.transform || iconImage == null || accent == null
                || captionText == null || hintText == null)
            {
                Hide();
                try { Create(front.transform); }
                catch { Hide(); throw; }
            }
            bool resized = Layout();
            string newCaption = SingleLine(caption);
            string key = SingleLine(hotkey), label = SingleLine(action);
            string newHint = key.Length == 0 ? label : label.Length == 0 ? key : key + "  ·  " + label;
            if (resized || captionValue != newCaption)
            { captionValue = newCaption; captionText.text = Fit(captionText, newCaption); }
            if (resized || hintValue != newHint)
            { hintValue = newHint; hintText.text = Fit(hintText, newHint); }
            if (iconImage.sprite != icon) iconImage.sprite = icon;
            iconImage.enabled = icon != null;
            if (!card.activeSelf) card.SetActive(true);
        }

        private void Create(Transform canvas)
        {
            parent = canvas;
            card = new GameObject("ConfirmMapPinRemoval.PinSuggestion", typeof(RectTransform), typeof(Image), typeof(CanvasGroup));
            card.layer = GUIManager.UILayer;
            card.transform.SetParent(canvas, false);
            var rect = card.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = rect.pivot = Anchor;
            rect.anchoredPosition = Offset;
            var background = card.GetComponent<Image>();
            background.color = new Color(0.055f, 0.045f, 0.03f, 0.86f);
            background.raycastTarget = false;
            var group = card.GetComponent<CanvasGroup>();
            group.interactable = false; group.blocksRaycasts = false;

            var edge = Child("Accent", 3, Height);
            accent = edge.GetComponent<RectTransform>();
            var edgeImage = edge.AddComponent<Image>();
            edgeImage.color = GUIManager.Instance.ValheimOrange; edgeImage.raycastTarget = false;
            var icon = Child("PinIcon", 44, 44);
            iconImage = icon.AddComponent<Image>();
            iconImage.preserveAspect = true; iconImage.raycastTarget = false;
            captionText = Label("Caption", 18, true, 12, 24);
            hintText = Label("Shortcut", 14, false, -14, 22);
            width = 0; captionValue = hintValue = null;
        }

        private GameObject Child(string name, float childWidth, float childHeight)
        {
            var child = new GameObject(name, typeof(RectTransform));
            child.layer = GUIManager.UILayer; child.transform.SetParent(card.transform, false);
            var rect = child.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(childWidth, childHeight);
            return child;
        }

        private Text Label(string name, int size, bool bold, float y, float height)
        {
            var gui = GUIManager.Instance; var center = new Vector2(0.5f, 0.5f);
            var obj = gui.CreateText("", card.transform, center, center, new Vector2(32, y),
                bold ? gui.AveriaSerifBold : gui.AveriaSerif, size,
                bold ? gui.ValheimBeige : gui.ValheimOrange, true, Color.black, 242, height, false);
            obj.name = name;
            var text = obj.GetComponent<Text>();
            text.raycastTarget = false; text.supportRichText = false;
            text.alignment = TextAnchor.MiddleLeft;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.resizeTextForBestFit = false;
            return text;
        }

        private bool Layout()
        {
            var canvas = parent as RectTransform;
            float next = canvas != null && canvas.rect.width > 0
                ? Mathf.Min(DefaultWidth, Mathf.Max(144, canvas.rect.width - 56)) : DefaultWidth;
            if (Mathf.Approximately(width, next)) return false;
            width = next;
            card.GetComponent<RectTransform>().sizeDelta = new Vector2(width, Height);
            accent.anchoredPosition = new Vector2(-width / 2 + 1.5f, 0);
            iconImage.rectTransform.anchoredPosition = new Vector2(-width / 2 + 32, 0);
            captionText.rectTransform.sizeDelta = new Vector2(width - 88, 24);
            hintText.rectTransform.sizeDelta = new Vector2(width - 88, 22);
            return true;
        }

        private static string SingleLine(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            var result = new StringBuilder(128); bool space = false;
            int count = Math.Min(value.Length, 256);
            if (count < value.Length && count > 0 && Char.IsHighSurrogate(value[count - 1])) --count;
            for (int i = 0; i < count; ++i)
            {
                char c = value[i];
                if (Char.IsWhiteSpace(c) || Char.IsControl(c)) { space = result.Length != 0; continue; }
                if (space) { result.Append(' '); space = false; }
                result.Append(c);
            }
            if (count < value.Length) result.Append('…');
            return result.ToString();
        }

        private static string Fit(Text text, string value)
        {
            float available = text.rectTransform.sizeDelta.x;
            var settings = text.GetGenerationSettings(Vector2.zero);
            const string ellipsis = "…";
            // New localized glyphs may not yet be in the dynamic atlas when the
            // card is created. Layout can then report only its known Latin glyphs.
            // Request them first, and bound unavailable glyphs rather than letting
            // an under-reported width expose an unbounded caption.
            if (text.font != null && text.font.dynamic)
                text.font.RequestCharactersInTexture(value + ellipsis, text.fontSize, text.fontStyle);
            if (Measure(text, value, settings) <= available) return value;
            int[] elements = StringInfo.ParseCombiningCharacters(value);
            int low = 0, high = elements.Length;
            while (low < high)
            {
                int middle = (low + high + 1) / 2;
                int length = middle == elements.Length ? value.Length : elements[middle];
                string candidate = value.Substring(0, length) + ellipsis;
                if (Measure(text, candidate, settings) <= available) low = middle;
                else high = middle - 1;
            }
            int end = low == elements.Length ? value.Length : elements[low];
            return value.Substring(0, end) + ellipsis;
        }

        private static float Measure(Text text, string value, TextGenerationSettings settings)
        {
            float generated = text.cachedTextGeneratorForLayout.GetPreferredWidth(value, settings) / text.pixelsPerUnit;
            float advance = 0;
            Font font = text.font;
            float scale = font != null && !font.dynamic && font.fontSize > 0 ? text.fontSize / (float)font.fontSize : 1;
            foreach (char c in value)
            {
                CharacterInfo glyph;
                if (font != null && font.GetCharacterInfo(c, out glyph, text.fontSize, text.fontStyle) && glyph.advance > 0)
                { advance += glyph.advance * scale; continue; }
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category == UnicodeCategory.NonSpacingMark || category == UnicodeCategory.EnclosingMark || category == UnicodeCategory.Format) continue;
                advance += text.fontSize * (Char.IsWhiteSpace(c) ? 0.5f : 1f);
            }
            return Mathf.Max(generated, advance);
        }

        public void Hide()
        {
            if (card != null) { card.SetActive(false); UnityEngine.Object.Destroy(card); }
            card = null; parent = null; iconImage = null; accent = null;
            captionText = hintText = null; captionValue = hintValue = null; width = 0;
        }

        public void Dispose() { Hide(); disposed = true; }
    }
}
