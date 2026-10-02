// Optional isolated-menu checks. Excluded from NordicRadio.dll.
using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimModPack.NordicRadio
{
    public static class PersonalAudioNativeChecks
    {
        private static int checks;
        private static bool ranOriginal;
        private static void Observe(bool __runOriginal) { ranOriginal = __runOriginal; }
        private static void Check(bool value, string name)
        { if (!value) throw new InvalidOperationException("NordicRadio personal audio: " + name); checks++; }
        private static object Get(object instance, string name)
        { return instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance); }
        private static void Set(object instance, string name, object value)
        { instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(instance, value); }
        private static object Call(object instance, string name, params object[] args)
        { return instance.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(instance, args); }
        public static string Run()
        {
            if (Player.m_localPlayer != null) return "SKIP: personal audio probe requires isolated menu.";
            if (GUIManager.CustomGUIFront == null || GUIManager.Instance.AveriaSerif == null)
                return "SKIP: personal audio UI requires graphical Jotunn assets.";
            Plugin plugin = Plugin.Instance;
            if (plugin == null) throw new InvalidOperationException("NordicRadio plugin is not initialized");
            checks = 0;
            var volume = (ConfigEntry<float>)Get(plugin, "personalVolume");
            var muted = (ConfigEntry<bool>)Get(plugin, "personalMuted");
            var shortcut = (ConfigEntry<KeyboardShortcut>)Get(plugin, "personalAudioShortcut");
            float oldVolume = volume.Value; bool oldMuted = muted.Value, save = plugin.Config.SaveOnConfigSet;
            KeyboardShortcut oldShortcut = shortcut.Value;
            var window = new PersonalAudioWindow(plugin);
            var actual = (PersonalAudioWindow)Get(plugin, "personalAudioWindow");
            var controls = Get(plugin, "personalAudioControls");
            object observedPlayer = Get(controls, "observedPlayer"), observedNetwork = Get(controls, "observedNetwork");
            var observer = new Harmony(Plugin.Id + ".personal-audio-native-probe");
            int baseline = (int)typeof(GUIManager).GetField("InputBlockRequests", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            GameObject audioObject = null;
            plugin.Config.SaveOnConfigSet = false;
            try
            {
                plugin.PersonalVolume = .63f; plugin.PersonalMuted = false;
                shortcut.Value = new KeyboardShortcut(KeyCode.F8, KeyCode.LeftControl);
                Call(window, "BuildVisuals");
                Check(window.IsVisible, "personal wood panel works without player, radio target or playlist");
                Check(((Text)Get(window, "hint")).text.Contains("Ctrl+F8"), "current configured shortcut is shown");
                var slider = (Slider)Get(window, "slider");
                Check(slider.minValue == 0 && slider.maxValue == 100 && slider.wholeNumbers && slider.fillRect != null && slider.handleRect != null,
                    "real native slider supports 0..100 percent");
                Check(slider.value == 63 && !plugin.PersonalMuted && Math.Abs(plugin.PersonalVolume - .63f) < .001f,
                    "existing preferred volume is reused by personal window");
                plugin.PersonalMuted = true; Call(window, "Refresh");
                Check(plugin.PersonalVolume == 0 && Math.Abs(plugin.PersonalVolumeSetting - .63f) < .001f,
                    "local mute preserves preferred volume and makes effective gain zero");
                Check(slider.value == 63 && ((Button)Get(window, "mute")).GetComponentInChildren<Text>().text.Length > 0,
                    "muted UI retains slider position and exposes unmute action");
                plugin.PersonalMuted = false; Call(window, "Refresh");
                Check(Math.Abs(plugin.PersonalVolume - .63f) < .001f, "unmute restores previous preferred volume");
                Call(window, "ChangeVolume", -.05f); Check(Math.Abs(plugin.PersonalVolumeSetting - .58f) < .001f, "local five percent adjustment");
                plugin.PersonalMuted = true; Call(window, "ChangeVolume", .05f);
                Check(!plugin.PersonalMuted && Math.Abs(plugin.PersonalVolume - .63f) < .001f, "volume change unmutes and adjusts preserved value");
                plugin.PersonalVolume = 5; Check(plugin.PersonalVolumeSetting == 1, "volume clamps to unity");
                plugin.PersonalVolume = -5; Check(plugin.PersonalVolumeSetting == 0, "negative volume clamps to zero");
                plugin.PersonalVolume = Single.NaN; Check(plugin.PersonalVolumeSetting == 0, "nonfinite volume becomes safe zero");
                plugin.PersonalVolume = .4f;
                shortcut.Value = new KeyboardShortcut(KeyCode.F10, KeyCode.RightShift, KeyCode.RightControl); Call(window, "Refresh");
                Check(((Text)Get(window, "hint")).text.Contains("Ctrl+Shift+F10"), "actual rebound modifier sides normalize in caption");
                shortcut.Value = new KeyboardShortcut(KeyCode.None); Call(window, "Refresh");
                string hint = ((Text)Get(window, "hint")).text;
                Check(hint.Contains("Unbound") || hint.Contains("Клавиша не назначена"), "unbound shortcut is explicit");
                Canvas.ForceUpdateCanvases();
                var rect = ((GameObject)Get(window, "panel")).GetComponent<RectTransform>();
                Check(rect.rect.width >= 669 && rect.rect.height >= 451, "native compact panel retains intended dimensions");
                foreach (var caption in ((GameObject)Get(window, "panel")).GetComponentsInChildren<Text>())
                    Check(!caption.supportRichText, "personal UI has only plain text captions");
                Type audioType = typeof(Plugin).Assembly.GetType("ValheimModPack.NordicRadio.RadioAudio", true);
                object audio = Activator.CreateInstance(audioType, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { plugin, null }, null);
                audioObject = new GameObject("NordicRadio.PersonalMute.NativeFixture"); var source = audioObject.AddComponent<AudioSource>(); source.playOnAwake = false;
                audioType.GetField("source", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(audio, source);
                plugin.PersonalMuted = true; Call(audio, "ApplyPersonalMute"); Check(source.mute, "personal mute immediately mutes actual Unity AudioSource");
                plugin.PersonalMuted = false; Call(audio, "ApplyPersonalMute"); Check(!source.mute, "unmute restores actual source mute flag");
                window.Hide(); window.Hide(); Check(!window.IsVisible, "personal window close is idempotent");
                // Use the actual plugin-owned window to verify native hooks, never a world.
                Set(controls, "observedPlayer", Player.m_localPlayer); Set(controls, "observedNetwork", ZNet.instance);
                Call(actual, "BuildVisuals");
                foreach (string name in new[] { "GetButton", "GetButtonDown", "GetButtonUp" })
                {
                    MethodInfo target = AccessTools.Method(typeof(ZInput), name, new[] { typeof(string) });
                    Check(Harmony.GetPatchInfo(target).Owners.Contains(Plugin.Id + ".personal-audio-input"), "native " + name + " is gated");
                    observer.Patch(target, prefix: new HarmonyMethod(typeof(PersonalAudioNativeChecks), "Observe") { priority = Priority.Last });
                }
                ranOriginal = true; Check(!ZInput.GetButton("Jump") && !ranOriginal, "modal suppresses held native gameplay action");
                ranOriginal = true; Check(!ZInput.GetButtonDown("GP") && !ranOriginal, "modal suppresses pressed guardian power");
                ranOriginal = true; Check(!ZInput.GetButtonUp("Jump") && !ranOriginal, "modal suppresses released native gameplay action");
                ranOriginal = false; ZInput.GetButtonDown("JoyButtonB"); Check(ranOriginal, "native controller cancel remains available");
                actual.Hide(); ranOriginal = false; ZInput.GetButtonDown("Jump"); Check(ranOriginal, "closing modal restores ordinary native input");
                Check(Harmony.GetPatchInfo(AccessTools.Method(typeof(GUIManager), "ResetInputBlock")).Owners.Contains(Plugin.Id + ".personal-audio-input"),
                    "global Jotunn input reset is observed by the personal window");
                Check((int)typeof(GUIManager).GetField("InputBlockRequests", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null) == baseline,
                    "menu-only construction and cleanup leave shared input lease count unchanged");
                return "NordicRadio personal audio native PASS; " + checks + " checks (local settings and isolated UI only).";
            }
            finally
            {
                window.Hide(); actual.Hide(); observer.UnpatchSelf();
                if (audioObject != null) UnityEngine.Object.DestroyImmediate(audioObject);
                volume.Value = oldVolume; muted.Value = oldMuted; shortcut.Value = oldShortcut;
                Set(controls, "observedPlayer", observedPlayer); Set(controls, "observedNetwork", observedNetwork);
                plugin.Config.SaveOnConfigSet = save;
            }
        }
    }
}
