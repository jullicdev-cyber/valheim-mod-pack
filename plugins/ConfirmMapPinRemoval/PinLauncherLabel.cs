using System;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimModPack.PinRemoval
{
    internal static class PinLauncherLabel
    {
        internal static void Apply(GameObject button, string action, string shortcut, string unbound)
        {
            if (button == null) return;
            Text caption = button.GetComponentInChildren<Text>(true);
            if (caption == null) return;
            string value = action + "\n" + (String.IsNullOrEmpty(shortcut) ? unbound : "[" + shortcut + "]");
            caption.supportRichText = false;
            caption.fontSize = 17;
            caption.resizeTextForBestFit = true;
            caption.resizeTextMinSize = 14;
            caption.resizeTextMaxSize = 17;
            caption.alignment = TextAnchor.MiddleCenter;
            caption.horizontalOverflow = HorizontalWrapMode.Wrap;
            caption.verticalOverflow = VerticalWrapMode.Truncate;
            if (caption.text != value) caption.text = value;
        }
    }
}
