using System;
using System.Collections.Generic;
using System.Reflection;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ValheimModPack.PinRemoval
{
    // Optional native probe source; never included in the release plugin.
    public static class PresetUiNativeChecks
    {
        private static int count;
        public static string Run()
        {
            count = 0; int placed = 0, saved = 0, closed = 0, cancelled = 0, confirmed = 0;
            PinPresetEdit edit = null; Exception failure = null;
            var window = new PinPresetWindow(delegate(string key) { return key == "presets_page" ? "{0} / {1}" : key; },
                delegate(Exception error) { failure = error; });
            var entries = new List<PinPresetEntryView>();
            for (int i = 0; i < 10; ++i) entries.Add(new PinPresetEntryView
            { Id = "id" + i, Name = "Preset " + i, IconType = 0, BuiltIn = true, SearchText = i == 0 ? "iron железо" : "" });
            var icons = new List<PinPresetIconOption>
            { new PinPresetIconOption { Type = 0, Label = "fire" }, new PinPresetIconOption { Type = 6, Label = "portal" } };
            int initialBlocks = InputBlocks();
            try
            {
                window.Show(entries, icons, delegate(string id) { ++placed; }, delegate(string id) { },
                    delegate(PinPresetEdit value) { ++saved; edit = value; }, delegate(string id) { }, delegate { ++closed; }, "Here");
                Check(window.IsVisible && !window.IsEditing, "picker opens");
                Check(Field<InputField>(window, "search").characterLimit == 128, "search has a bounded input");
                Check(EventSystem.current == null || EventSystem.current.currentSelectedGameObject == Field<InputField>(window, "search").gameObject,
                    "search has keyboard focus");
                Check(Rows(window).Length == 8, "first page contains eight rows");
                Check(!Field<Button>(window, "place").interactable, "placement requires selection");
                Button staleFirstRow = Rows(window)[0];
                Field<Button>(window, "next").onClick.Invoke();
                Check(Rows(window).Length == 2, "second page contains remaining rows");
                staleFirstRow.onClick.Invoke();
                Check(!Field<Button>(window, "place").interactable, "stale page row cannot select");
                window.Query = "ЖЕЛЕЗО";
                Check(Rows(window).Length == 1, "search matches case-insensitive aliases");
                Rows(window)[0].onClick.Invoke();
                Check(Field<Button>(window, "place").interactable, "selection enables placement");
                Field<Button>(window, "place").onClick.Invoke();
                Check(placed == 1, "placement invokes one callback");
                Field<Button>(window, "edit").onClick.Invoke();
                Check(window.IsEditing, "edit opens");
                Check(Field<InputField>(window, "name").characterLimit == 96, "editor uses persistence name limit");
                Button oldSave = Find(window, "presets_save");
                Field<GameObject>(window, "iconRows").GetComponentsInChildren<Button>()[1].onClick.Invoke();
                Find(window, "presets_save").onClick.Invoke();
                Check(saved == 1 && edit != null && edit.IconType == 6 && !edit.NameChanged, "icon-only edit preserves localized name binding");
                Field<InputField>(window, "name").text = "  My harbour  ";
                Find(window, "presets_save").onClick.Invoke();
                Check(saved == 2 && edit.Name == "My harbour" && edit.NameChanged, "renamed preset is trimmed and explicit");
                Field<InputField>(window, "name").text = "Unsaved draft";
                window.SetStatus("storage error");
                window.RefreshEntries(entries, false);
                window.RefreshLabels("Selected point");
                Check(window.IsEditing && Field<InputField>(window, "name").text == "Unsaved draft", "language refresh preserves editor draft");
                Check(window.Query == "ЖЕЛЕЗО", "language refresh preserves query");
                Check(Field<Text>(window, "statusText").text == "storage error", "language refresh preserves save failure");
                oldSave.onClick.Invoke();
                Check(saved == 2, "stale editor save callback is ignored");
                Field<InputField>(window, "name").text = "   ";
                Find(window, "presets_save").onClick.Invoke();
                Check(saved == 2 && Field<Text>(window, "statusText").text == "presets_name_required", "empty preset name rejected");
                window.RefreshEntries(entries);
                Check(!window.IsEditing && Rows(window).Length == 1, "successful save returns to filtered picker");
                window.Query = "nothing matches";
                Check(Rows(window).Length == 0 && !Field<Button>(window, "place").interactable, "empty search clears selection");
                window.Query = "";
                Find(window, "presets_add").onClick.Invoke();
                Field<InputField>(window, "name").text = "My preset";
                Find(window, "presets_save").onClick.Invoke();
                Check(saved == 3 && String.IsNullOrEmpty(edit.Id) && edit.NameChanged, "new preset is an explicit custom name");
                Find(window, "presets_cancel").onClick.Invoke();
                Check(!window.IsEditing && window.IsVisible, "editor cancel returns to picker");

                window.ShowRename("Original", delegate(string value) { ++saved; }, delegate { ++cancelled; });
                Check(window.IsVisible && window.IsEditing && Field<InputField>(window, "name").text == "Original", "rename opens with existing name");
                Field<InputField>(window, "name").text = "";
                Find(window, "presets_save").onClick.Invoke();
                Check(saved == 3, "rename rejects empty name");
                Field<InputField>(window, "name").text = "New name";
                Find(window, "presets_save").onClick.Invoke();
                Check(saved == 4 && window.IsVisible, "rename save leaves controller in charge of success");
                Find(window, "presets_cancel").onClick.Invoke();
                Check(cancelled == 1 && !window.IsVisible, "rename cancel closes and releases modal");

                window.ShowConfirm("Delete preset?", "Only this template is removed.", "Confirm", delegate { ++confirmed; window.Hide(); }, delegate { ++cancelled; });
                Check(window.IsVisible && window.IsEditing, "confirmation opens");
                Button confirmButton = Find(window, "Confirm");
                VerifyPlainLabelsAndSounds(window);
                confirmButton.onClick.Invoke(); confirmButton.onClick.Invoke();
                Check(confirmed == 1 && !window.IsVisible, "stale second confirm click is ignored");
                window.ShowConfirm("Question", "Body", "Yes", delegate { ++confirmed; }, delegate { ++cancelled; });
                Find(window, "presets_cancel").onClick.Invoke();
                Check(cancelled == 2 && confirmed == 1 && !window.IsVisible, "confirmation cancel never confirms");
                window.Show(entries, icons, delegate(string id) { }, delegate(string id) { }, delegate(PinPresetEdit value) { },
                    delegate(string id) { }, delegate { ++closed; }, "Here");
                Find(window, "presets_close").onClick.Invoke();
                Check(closed == 1 && !window.IsVisible, "close callback invokes once after hide");
                window.Hide(); window.Hide();
                Check(InputBlocks() == initialBlocks, "input-block count restored after repeated show/hide");
                Check(failure == null, "no caught UI exception");
                return "PASS " + count + " map preset UI checks (actual Jotunn widgets; gameplay input lock requires main scene).";
            }
            finally { window.Hide(); }
        }

        private static void VerifyPlainLabelsAndSounds(PinPresetWindow window)
        {
            var panel = Field<GameObject>(window, "panel"); bool plain = true, oneSound = true;
            foreach (var label in panel.GetComponentsInChildren<Text>(true)) if (label.supportRichText) plain = false;
            foreach (var sound in panel.GetComponentsInChildren<ButtonSfx>(true)) if (sound.m_selectSfxPrefab != null) oneSound = false;
            Check(plain, "all visible labels have rich text disabled");
            Check(oneSound, "button selection sound is disabled");
        }
        private static Button[] Rows(PinPresetWindow window) { return Field<GameObject>(window, "rows").GetComponentsInChildren<Button>(); }
        private static Button Find(PinPresetWindow window, string value)
        {
            foreach (var button in Field<GameObject>(window, "panel").GetComponentsInChildren<Button>())
                foreach (var text in button.GetComponentsInChildren<Text>()) if (text.text == value) return button;
            throw new Exception("Missing UI button " + value);
        }
        private static T Field<T>(object instance, string name)
        {
            return (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance);
        }
        private static int InputBlocks()
        {
            return (int)typeof(GUIManager).GetField("InputBlockRequests", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        }
        private static void Check(bool condition, string label)
        {
            if (!condition) throw new Exception("Map preset UI regression: " + label);
            ++count;
        }
    }
}
