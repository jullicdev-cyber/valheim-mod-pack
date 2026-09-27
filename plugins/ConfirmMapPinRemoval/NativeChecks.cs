// Optional native-engine probe; this file is excluded from the shipped plugin.
using System;
using System.Collections.Generic;
using System.Reflection;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;
namespace ValheimModPack.PinRemoval
{
    public static class NativeChecks
    {
        private static int checks;
        private static void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException("Map history native check: " + message); }
        private static int Blocks()
        { return (int)typeof(GUIManager).GetField("InputBlockRequests", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null); }
        private static GameObject Overlay(object window)
        { return (GameObject)window.GetType().GetField("overlay", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window); }
        private static bool OwnsHistoryInput(PinHistoryWindow window)
        { return (bool)typeof(PinHistoryWindow).GetField("ownsInputBlock", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window); }
        public static string Run()
        {
            checks = 0;
            string shared = CheckSharedMapContract();
            if (GUIManager.CustomGUIFront == null)
            {
                var create = typeof(GUIManager).GetMethod("TryCreateGUI", BindingFlags.NonPublic | BindingFlags.Instance);
                if (create != null) create.Invoke(GUIManager.Instance, null);
            }
            if (GUIManager.CustomGUIFront == null) return shared + " SKIP: map-history native UI requires Jotunn CustomGUIFront; headless scene has none.";
            int baseline = Blocks(), afterForeign = baseline;
            var window = new PinHistoryWindow(); var confirmation = new WoodDialogView();
            try
            {
                GUIManager.BlockInput(true); afterForeign = Blocks();
                Check(afterForeign == baseline || afterForeign == baseline + 1, "Foreign input request has expected lifecycle");
                int clicks = 0; window.Show(window.Hide, value => { }, value => { });
                Check(window.IsVisible, "Actual Jotunn wood window is visible");
                int ownDelta = Blocks() - afterForeign; Check(ownDelta == 0 || ownDelta == 1, "Window owns at most one input request");
                var rows = new List<HistoryRow>();
                for (int i = 0; i < 6; i++) rows.Add(new HistoryRow { Title = "<b>Метка " + i + "</b>",
                    Details = "Автор: Скальд · Создана: 2026-09-27 16:20\nУдалена: 2026-09-27 16:30 · X 120 / Z -500",
                    ActionLabel = "Вернуть", Enabled = true, Activate = () => clicks++ });
                window.Render(rows, true, 0, 2, 7, true);
                var overlay = Overlay(window); var buttons = overlay.GetComponentsInChildren<Button>();
                Check(buttons.Length == 11, "Six recovery rows and five navigation/tab buttons are constructed");
                foreach (var button in buttons)
                {
                    var sound = button.GetComponent<ButtonSfx>();
                    Check(sound == null || sound.m_selectSfxPrefab == null, "Buttons retain only click sound, not selection sound");
                }
                int names = 0;
                foreach (var text in overlay.GetComponentsInChildren<Text>())
                    if (text.text.StartsWith("<b>Метка", StringComparison.Ordinal)) { names++; Check(!text.supportRichText, "Untrusted pin names never interpret markup"); }
                Check(names == 6, "All six rows have native text components");
                foreach (var button in buttons)
                {
                    var label = button.GetComponentInChildren<Text>();
                    if (label != null && label.text == "Вернуть") { button.onClick.Invoke(); break; }
                }
                Check(clicks == 1, "One native recovery button dispatches one action");
                window.Hide(); window.Hide();
                Check(!window.IsVisible && !overlay.activeSelf && Blocks() == afterForeign, "Double hide preserves unrelated input request");
                window.Show(window.Hide, value => { }, value => { });
                Overlay(window).SetActive(false); window.CleanupHidden();
                Check(!OwnsHistoryInput(window) && Overlay(window) == null && Blocks() == afterForeign,
                    "Disabled history panel releases only its own request during cleanup");
                window.Show(window.Hide, value => { }, value => { });
                UnityEngine.Object.DestroyImmediate(Overlay(window));
                Check(!window.IsVisible && OwnsHistoryInput(window), "Destroyed Unity panel leaves a detectable owned request before cleanup");
                window.CleanupHidden();
                Check(!OwnsHistoryInput(window) && ReferenceEquals(Overlay(window), null) && Blocks() == afterForeign,
                    "Destroyed panel cleanup clears managed references and preserves foreign request");
                window.Show(window.Hide, value => { }, value => { });
                UnityEngine.Object.DestroyImmediate(Overlay(window));
                window.Show(window.Hide, value => { }, value => { });
                Check(window.IsVisible && OwnsHistoryInput(window) && Blocks() == afterForeign + ownDelta,
                    "Reopen after destroyed panel replaces rather than adds an owned request");
                window.Hide();
                int staleClicks = 0;
                window.Show(() => staleClicks++, value => { }, value => { });
                var oldButtons = Overlay(window).GetComponentsInChildren<Button>();
                var oldClose = oldButtons[oldButtons.Length - 1];
                Overlay(window).SetActive(false);
                window.Show(window.Hide, value => { }, value => { });
                oldClose.onClick.Invoke();
                Check(staleClicks == 0 && window.IsVisible && Blocks() == afterForeign + ownDelta,
                    "Old disabled-panel callback cannot affect its replacement");
                window.Hide(); window.CleanupHidden();
                Check(!OwnsHistoryInput(window) && Blocks() == afterForeign, "Replacement cleanup is idempotent and retains foreign lease");
                confirmation.Show("Длинная кириллическая метка", () => { }, () => { });
                Check(confirmation.IsVisible, "Existing confirmation window still opens with actual Jotunn assets");
                buttons = Overlay(confirmation).GetComponentsInChildren<Button>();
                Check(buttons.Length == 2, "Confirmation keeps exactly Cancel and Delete");
                foreach (var button in buttons) { var sound = button.GetComponent<ButtonSfx>(); Check(sound == null || sound.m_selectSfxPrefab == null, "Confirmation double-sound fix retained"); }
                confirmation.Hide(); Check(Blocks() == afterForeign, "Confirmation releases only its own input request");
                string presets = PresetUiNativeChecks.Run();
                return shared + " PASS: " + checks + " native map-history/confirmation assertions; "
                    + (ownDelta == 1 ? "live input request increment and release exercised." : "input block is a no-op in menu/headless; live gameplay input still needs checking.")
                    + " " + presets;
            }
            finally { window.Hide(); confirmation.Hide(); GUIManager.BlockInput(false); Check(Blocks() == baseline, "Probe restores starting input state"); }
        }
        private static string CheckSharedMapContract()
        {
            var package = new ZPackage();
            package.Write(3); package.Write(4);
            package.Write(true); package.Write(false); package.Write(true); package.Write(false);
            package.Write(1); package.Write(777L); package.Write("Портал · <имя>");
            package.Write(new Vector3(123.5f, 2.25f, -999.75f)); package.Write(1); package.Write(false); package.Write("Steam_777");
            byte[] compressed = Utils.Compress(package.GetArray());
            Type codec = typeof(PinArchive).Assembly.GetType("ValheimModPack.PinRemoval.SharedPinCodec", true);
            var parsed = (HashSet<string>)codec.GetMethod("ReadVanillaKeys", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { compressed });
            var expected = new PinRecord { Owner = 777, Author = "Steam_777", X = 123.5f, Y = 2.25f, Z = -999.75f, Type = 1 };
            Check(parsed.Count == 1 && parsed.Contains(expected.Key), "Sidecar parser matches actual native ZPackage strings, vectors and gzip");
            foreach (string name in new[] { "Start", "OnWrite", "GetMapData", "RPC_MapData" })
                Check(typeof(MapTable).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) != null, "Actual cartography endpoint " + name);
            return "PASS: shared metadata parser matches native ZPackage/Utils.Compress and MapTable endpoints.";
        }
    }
}
