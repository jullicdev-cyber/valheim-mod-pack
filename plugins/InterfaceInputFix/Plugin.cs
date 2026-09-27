using System;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Managers;
using Jotunn.Utils;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ValheimModPack.InterfaceInputFix
{
    [BepInPlugin(Id, "Interface Input Fix", Version)]
    [BepInDependency("com.jotunn.jotunn", "2.30.2")]
    [BepInDependency("yay.spikehimself.xportal", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("vapok.mods.adventurebackpacks", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("isimp.Bindrune", BepInDependency.DependencyFlags.SoftDependency)]
    [NetworkCompatibility(CompatibilityLevel.NotEnforced, VersionStrictness.None)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "valheimmodpack.interfaceinputfix", Version = "1.1.0";
        private static Plugin active;
        private Harmony harmony;
        private InputLease portalLease;
        private object portal;
        private FieldInfo portalPanel, backpackOpen;
        private MethodInfo initialise, activate, setActive, hideBackpack;
        private readonly ReentryGuard backpackGuard = new ReentryGuard();
        private ConfigEntry<bool> diagnostics;
        private float nextDiagnostic;
        private int focusFrame = -1;
        public bool PortalPatched { get; private set; }
        public bool BackpackPatched { get; private set; }

        private void Awake()
        {
            active = this; harmony = new Harmony(Id);
            portalLease = new InputLease(GUIManager.BlockInput);
            diagnostics = Config.Bind("Diagnostics", "LogBlockedShortcuts", false,
                "Log input gates on Ctrl+F/I presses. Read-only; does not reset input locks or log typed text.");
            try { PatchPortal(); }
            catch (Exception e) { harmony.UnpatchSelf(); PortalPatched = false; Logger.LogError("XPortal compatibility disabled: " + e); }
            try { PatchBackpack(); } catch (Exception e) { Logger.LogError("Backpack compatibility disabled: " + e); }
            try { BindruneCompat.Install(e => Logger.LogError(e)); } catch (Exception e) { Logger.LogError("Bindrune compatibility disabled: " + e); }
            Logger.LogInfo("Interface fixes ready: XPortal=" + PortalPatched + ", AdventureBackpacks=" + BackpackPatched);
        }
        private Type SupportedType(string guid, string version, string name)
        {
            PluginInfo info;
            if (!Chainloader.PluginInfos.TryGetValue(guid, out info)) return null;
            if (info.Metadata.Version.ToString() != version)
            {
                Logger.LogWarning(guid + " " + info.Metadata.Version + " is not the tested " + version + "; its compatibility patch is skipped.");
                return null;
            }
            var type = info.Instance.GetType().Assembly.GetType(name, true); return type;
        }
        private static MethodInfo Method(Type type, string name, params Type[] args)
        {
            var method = AccessTools.Method(type, name, args);
            if (method == null) throw new MissingMethodException(type.FullName, name);
            return method;
        }
        private static FieldInfo Field(Type type, string name, Type expected)
        {
            var field = AccessTools.Field(type, name);
            if (field == null || field.FieldType != expected) throw new MissingFieldException(type.FullName, name);
            return field;
        }
        private void PatchPortal()
        {
            Type type = SupportedType("yay.spikehimself.xportal", "1.2.25", "XPortal.UI.PortalConfigurationPanel");
            if (type == null) return;
            portalPanel = Field(type, "mainPanel", typeof(GameObject));
            initialise = Method(type, "InitialiseUI");
            activate = Method(type, "ActivateInputField", typeof(bool), typeof(object));
            setActive = Method(type, "SetActive", typeof(bool));
            MethodInfo hide = Method(type, "Hide", typeof(bool), typeof(object));
            MethodInfo dispose = Method(type, "Dispose");
            MethodInfo reset = Method(typeof(GUIManager), "ResetInputBlock");
            harmony.Patch(setActive, prefix: new HarmonyMethod(typeof(Plugin), "SetActivePrefix"));
            harmony.Patch(hide, prefix: new HarmonyMethod(typeof(Plugin), "HidePrefix"));
            harmony.Patch(dispose, prefix: new HarmonyMethod(typeof(Plugin), "DisposePrefix"));
            // Observe the actual GUI reset, never cause one or change another mod's request.
            harmony.Patch(reset, postfix: new HarmonyMethod(typeof(Plugin), "GuiResetPostfix"));
            PortalPatched = true;
        }
        private void PatchBackpack()
        {
            Type type = SupportedType("vapok.mods.adventurebackpacks", "2.1.13", "AdventureBackpacks.Patches.InventoryGuiPatches");
            if (type == null) return;
            backpackOpen = Field(type, "BackpackIsOpen", typeof(bool));
            hideBackpack = Method(type, "HideBackpack", typeof(InventoryGui));
            harmony.Patch(Method(typeof(InventoryGui), "Hide"), prefix: new HarmonyMethod(typeof(Plugin), "InventoryHidePrefix"));
            BackpackPatched = true;
        }
        private static bool SetActivePrefix(object __instance, bool __0)
        {
            if (active == null) return true;
            active.SetPortal(__instance, __0); return false;
        }
        private void SetPortal(object instance, bool show)
        {
            try
            {
                if (!ReferenceEquals(portal, instance))
                {
                    if (portal != null) ClosePortal();
                    portal = instance;
                }
                if (!show) { ClosePortal(); return; }
                var panel = GetPanel();
                if (panel == null) { initialise.Invoke(portal, null); panel = GetPanel(); }
                if (panel == null) throw new InvalidOperationException("XPortal canvas is not ready");
                panel.SetActive(true);
                portalLease.Show();
                // Preserve the two-frame delay, but cancel it if the window closes.
                focusFrame = Time.frameCount + 2;
            }
            catch (Exception e)
            {
                try { ClosePortal(); } catch (Exception close) { Logger.LogError(close); }
                Logger.LogError("XPortal window could not change state: " + e);
            }
        }
        private GameObject GetPanel() { return portal == null ? null : (GameObject)portalPanel.GetValue(portal); }
        private void ClosePortal()
        {
            focusFrame = -1;
            try
            {
                GameObject panel = GetPanel();
                if (panel != null)
                {
                    var events = EventSystem.current;
                    if (events != null && events.currentSelectedGameObject != null
                        && events.currentSelectedGameObject.transform.IsChildOf(panel.transform)) events.SetSelectedGameObject(null);
                    panel.SetActive(false);
                }
            }
            finally { portalLease.Close(); }
        }
        private static bool HidePrefix(object __instance, bool __0)
        {
            if (active == null) return true;
            if (!ReferenceEquals(active.portal, __instance)) return false;
            if (__0) active.portalLease.ScheduleClose(Time.frameCount + 2);
            else active.SetPortal(__instance, false);
            return false;
        }
        private static void DisposePrefix(object __instance)
        {
            if (active == null || !ReferenceEquals(active.portal, __instance)) return;
            try { active.ClosePortal(); } catch (Exception e) { active.Logger.LogError(e); }
            finally { active.portal = null; }
        }
        private static void GuiResetPostfix()
        {
            if (active != null && active.portalLease != null) { active.portalLease.Forget(); active.focusFrame = -1; }
        }
        private static void InventoryHidePrefix(InventoryGui __instance)
        {
            if (active == null || !active.BackpackPatched || __instance == null) return;
            active.backpackGuard.Run(() =>
            {
                try
                {
                    if ((bool)active.backpackOpen.GetValue(null))
                        active.hideBackpack.Invoke(null, new object[] { __instance });
                }
                catch (Exception e) { active.Logger.LogError("Backpack close cleanup failed: " + e); }
            });
        }
        private void LateUpdate()
        {
            try
            {
                BindruneCompat.Tick();
                if (PortalPatched && portalLease.Held)
                {
                    var panel = GetPanel();
                    if (panel == null || !panel.activeInHierarchy || Player.m_localPlayer == null
                        || Player.m_localPlayer.IsDead() || portalLease.CloseDue(Time.frameCount)) ClosePortal();
                    else if (focusFrame >= 0 && Time.frameCount >= focusFrame)
                    {
                        focusFrame = -1; activate.Invoke(portal, new object[] { false, null });
                    }
                }
                if (diagnostics.Value && Time.unscaledTime >= nextDiagnostic && (Input.GetKeyDown(KeyCode.I)
                    || (Input.GetKeyDown(KeyCode.F) && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))))
                {
                    nextDiagnostic = Time.unscaledTime + 1f; Logger.LogInfo(InputDiagnostics.Read());
                }
            }
            catch (Exception e)
            {
                try { ClosePortal(); } catch { }
                Logger.LogError("Interface lifecycle: " + e);
            }
        }
        private void OnDestroy()
        {
            try { BindruneCompat.Dispose(); if (portalLease != null) ClosePortal(); }
            finally { if (harmony != null) harmony.UnpatchSelf(); if (ReferenceEquals(active, this)) active = null; }
        }
    }
}
