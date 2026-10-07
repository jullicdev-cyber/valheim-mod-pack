// Test-only: requires the isolated full-pack probe; never shipped or run in a live world.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace ValheimModPack.InterfaceInputFix
{
    public static class ValheimPlusEditingNativeChecks
    {
        private static int checks;
        private static int workEntered, runEntered;
        private static ZDO fixtureZdo;
        private static readonly BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        private static readonly BindingFlags Members = All | BindingFlags.Instance;
        private static bool IgnoreNotify() { return false; }
        private static bool SkipFixtureViewAwake(ZNetView __instance) { return !__instance.gameObject.name.StartsWith("VMP.IsolatedAEM.", StringComparison.Ordinal); }
        private static bool SkipFixtureRevision(ZDO __instance) { return !ReferenceEquals(__instance, fixtureZdo); }
        private static bool ObserveWork(bool __runOriginal) { if (__runOriginal) workEntered++; return false; }
        private static bool ObserveRun(bool __runOriginal) { if (__runOriginal) runEntered++; return false; }
        public static string Run()
        {
            checks = 0;
            string isolated = Environment.GetEnvironmentVariable("VMP_QOL_SMOKE_ROOT");
            Check(!String.IsNullOrEmpty(isolated)
                && Path.GetFullPath(Paths.BepInExRootPath).StartsWith(Path.GetFullPath(isolated) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "isolated probe root required");
            Check(Player.m_localPlayer == null, "no live player or inventory");
            PluginInfo vendor;
            if (!Chainloader.PluginInfos.TryGetValue("org.bepinex.plugins.valheim_plus", out vendor))
                return "SKIP: Valheim Plus editing native checks; optional vendor is absent.";
            Check(vendor.Metadata.Version == new System.Version("0.10.2.0"), "exact vendor version");
            Type compat = typeof(Plugin).Assembly.GetType("ValheimModPack.InterfaceInputFix.ValheimPlusEditingCompat", true);
            Check((bool)compat.GetProperty("Ready", All).GetValue(null, null), "adapter initialized");
            Type aem = vendor.Instance.GetType().Assembly.GetType("ValheimPlus.AEM", true);
            MethodInfo ray = AccessTools.Method(aem, "ExecuteRayCast"), reset = AccessTools.Method(aem, "resetObjectTransform"),
                exists = AccessTools.Method(aem, "hitPieceStillExists"), clear = AccessTools.Method(aem, "resetObjectInfo"),
                validate = AccessTools.Method(aem, "isValidRayCastTarget"), start = AccessTools.Method(aem, "startMode"),
                run = AccessTools.Method(aem, "run"), work = AccessTools.Method(aem, "listenToHotKeysAndDoWork");
            string owner = Plugin.Id + ".valheimplus.editing";
            Check(HasOwner(ray, owner) && HasOwner(reset, owner) && HasOwner(exists, owner) && HasOwner(validate, owner)
                && HasOwner(start, owner) && HasOwner(run, owner) && HasOwner(work, owner), "all seven actual vendor methods patched");
            var saved = new Dictionary<FieldInfo, object>();
            foreach (FieldInfo field in aem.GetFields(All)) if (!field.IsLiteral && !field.IsInitOnly) saved.Add(field, field.GetValue(null));
            FieldInfo cameraInstance = AccessTools.Field(typeof(GameCamera), "m_instance");
            Check(cameraInstance != null && cameraInstance.FieldType == typeof(GameCamera), "native camera singleton shape");
            object previousCamera = cameraInstance.GetValue(null);
            var cameraObject = new GameObject("VMP.IsolatedAEM.Camera"); cameraObject.SetActive(false);
            var playerObject = new GameObject("VMP.IsolatedAEM.Player"); playerObject.SetActive(false);
            var fixture = new Harmony(Plugin.Id + ".valheimplus.editing.nativeprobe");
            GameObject target = null;
            try
            {
                // A main-menu probe has no gameplay MessageHud; only mute the fixture notification.
                fixture.Patch(AccessTools.Method(aem, "notifyUser"), prefix: new HarmonyMethod(typeof(ValheimPlusEditingNativeChecks), "IgnoreNotify"));
                var camera = cameraObject.AddComponent<GameCamera>();
                camera.transform.position = new Vector3(0, 20000, 0); camera.transform.rotation = Quaternion.identity;
                cameraInstance.SetValue(null, camera);
                var player = playerObject.AddComponent<Player>(); player.transform.position = camera.transform.position;
                AccessTools.Field(typeof(Character), "m_eye").SetValue(player, player.transform);
                AccessTools.Field(typeof(Player), "m_placeRayMask").SetValue(player, 1);
                AccessTools.Field(typeof(Player), "m_maxPlaceDistance").SetValue(player, 50f);
                target = GameObject.CreatePrimitive(PrimitiveType.Cube);
                target.name = "VMP.IsolatedAEM.TerrainHit"; target.transform.position = new Vector3(0, 20000, 4);
                target.layer = 0; Physics.SyncTransforms();
                // Prove the original vendor defect against an ordinary collider without Piece.
                compat.GetMethod("Dispose", All).Invoke(null, null);
                bool rayFailed = Throws<NullReferenceException>(() => ray.Invoke(null, new object[] { player }));
                bool resetFailed = Throws<NullReferenceException>(() => reset.Invoke(null, null));
                AccessTools.Field(aem, "isActive").SetValue(null, true);
                Check(rayFailed && resetFailed && (bool)exists.Invoke(null, null), "vendor baseline reproduces all three missing-target defects");
                compat.GetMethod("Install", All).Invoke(null, new object[] { null });
                Check(!(bool)ray.Invoke(null, new object[] { player }), "real terrain ray returns false without exception");
                Check((Piece)AccessTools.Field(aem, "HitPiece").GetValue(null) == null
                    && (GameObject)AccessTools.Field(aem, "HitObject").GetValue(null) == null, "native reset clears terrain target");
                reset.Invoke(null, null);
                Check(!(bool)exists.Invoke(null, null) && !(bool)AccessTools.Field(aem, "isInExistence").GetValue(null), "missing target resets existence result");
                var piece = target.AddComponent<Piece>(); piece.m_allowedInDungeons = true; piece.m_onlyInTeleportArea = false;
                bool result = (bool)ray.Invoke(null, new object[] { player });
                Check(ReferenceEquals(AccessTools.Field(aem, "HitPiece").GetValue(null), piece)
                    && (Vector3)AccessTools.Field(aem, "InitialPosition").GetValue(null) == target.transform.position,
                    "real valid ray preserves native target and position snapshot");
                Check(result == (bool)validate.Invoke(null, null), "native target/access validator result preserved");
                piece.m_allowedInDungeons = false;
                Check(!(bool)ray.Invoke(null, new object[] { player }), "native dungeon placement denial preserved");
                piece.m_allowedInDungeons = true;
                ray.Invoke(null, new object[] { player });
                Vector3 initial = (Vector3)AccessTools.Field(aem, "InitialPosition").GetValue(null);
                target.transform.position += Vector3.right;
                reset.Invoke(null, null);
                Check(target.transform.position == initial, "existing target uses original transform reset");
                start.Invoke(null, null);
                Check((bool)exists.Invoke(null, null), "existing Piece uses vendor existence path");
                PrisonChecks(aem, ray, start, run, work, player, target, piece, fixture);
                ray.Invoke(null, new object[] { player });
                UnityEngine.Object.DestroyImmediate(piece);
                Check(!ReferenceEquals(AccessTools.Field(aem, "HitPiece").GetValue(null), null)
                    && !(bool)exists.Invoke(null, null), "real Unity-destroyed Piece is rejected with retained managed reference");
                reset.Invoke(null, null);
                Check(target.transform.position == initial, "destroyed target reset is harmless");
                Check(!(bool)ray.Invoke(null, new object[] { player }), "removed Piece collider is rejected by real ray guard");
                return "Valheim Plus editing native PASS: " + checks + " assertions; actual raycast, original defect baseline, access denial, transform reset, Unity-destroyed Piece; isolated objects only.";
            }
            finally
            {
                try { if (!(bool)compat.GetProperty("Ready", All).GetValue(null, null)) compat.GetMethod("Install", All).Invoke(null, new object[] { null }); }
                finally
                {
                    fixture.UnpatchSelf();
                    clear.Invoke(null, null);
                    foreach (var entry in saved) entry.Key.SetValue(null, entry.Value);
                    cameraInstance.SetValue(null, previousCamera);
                    if (target != null && target.GetComponent<ZNetView>() != null) AccessTools.Field(typeof(ZNetView), "m_zdo").SetValue(target.GetComponent<ZNetView>(), null);
                    if (target != null) UnityEngine.Object.DestroyImmediate(target);
                    if (fixtureZdo != null) AccessTools.Method(typeof(ZDO), "Reset").Invoke(fixtureZdo, null);
                    fixtureZdo = null;
                    UnityEngine.Object.DestroyImmediate(playerObject); UnityEngine.Object.DestroyImmediate(cameraObject);
                }
            }
        }
        private static void PrisonChecks(Type aem, MethodInfo ray, MethodInfo start, MethodInfo run, MethodInfo work,
            Player player, GameObject target, Piece piece, Harmony fixture)
        {
            PluginInfo prison;
            if (!Chainloader.PluginInfos.TryGetValue("valheimmodpack.partyprison", out prison)) return;
            Type access = typeof(Plugin).Assembly.GetType("ValheimModPack.InterfaceInputFix.PrisonEditingAccess", true);
            Check((bool)access.GetProperty("Ready", All).GetValue(null, null), "reviewed native prison delegates initialized");
            Type plugin = prison.Instance.GetType();
            FieldInfo active = AccessTools.Field(plugin, "Active"), sentence = AccessTools.Field(plugin, "localSentence"), region = AccessTools.Field(plugin, "region");
            object previousActive = active.GetValue(null), previousSentence = sentence.GetValue(prison.Instance), previousRegion = region.GetValue(prison.Instance);
            fixture.Patch(AccessTools.Method(typeof(ZNetView), "Awake"), prefix: new HarmonyMethod(typeof(ValheimPlusEditingNativeChecks), "SkipFixtureViewAwake"));
            fixture.Patch(AccessTools.Method(typeof(ZDO), "IncreaseDataRevision"), prefix: new HarmonyMethod(typeof(ValheimPlusEditingNativeChecks), "SkipFixtureRevision"));
            fixture.Patch(work, prefix: new HarmonyMethod(typeof(ValheimPlusEditingNativeChecks), "ObserveWork") { priority = Priority.Last });
            fixture.Patch(run, prefix: new HarmonyMethod(typeof(ValheimPlusEditingNativeChecks), "ObserveRun") { priority = Priority.Last });
            try
            {
                active.SetValue(null, prison.Instance); sentence.SetValue(prison.Instance, null);
                region.SetValue(prison.Instance, Activator.CreateInstance(region.FieldType));
                ZNetView view = target.AddComponent<ZNetView>();
                fixtureZdo = new ZDO { m_uid = new ZDOID(-643591881, 1) };
                AccessTools.Field(typeof(ZNetView), "m_zdo").SetValue(view, fixtureZdo);
                fixtureZdo.Set("VMP_PP_Protected", true);
                Check(!(bool)ray.Invoke(null, new object[] { player }), "real marker blocks normal-player prison selection");
                AccessTools.Field(aem, "HitPiece").SetValue(null, piece); start.Invoke(null, null);
                Check(!(bool)AccessTools.Field(aem, "isActive").GetValue(null), "real protected target cannot enter AEM");
                AccessTools.Field(aem, "HitPiece").SetValue(null, piece); AccessTools.Field(aem, "isActive").SetValue(null, true);
                workEntered = 0; Vector3 before = target.transform.position; work.Invoke(null, null);
                Check(workEntered == 0 && target.transform.position == before && !(bool)AccessTools.Field(aem, "isActive").GetValue(null),
                    "active prison target stops before native confirm/transform body");
                fixtureZdo.Set("VMP_PP_Protected", false);
                Check((bool)ray.Invoke(null, new object[] { player }), "unmarked native piece remains editable");
                start.Invoke(null, null); workEntered = 0; work.Invoke(null, null);
                Check(workEntered == 1, "normal native active work accepted before test-only body suppression");
                sentence.SetValue(prison.Instance, Activator.CreateInstance(sentence.FieldType));
                Check((bool)access.GetProperty("Restricted", All).GetValue(null, null), "cached native delegate observes actual confined getter");
                runEntered = 0; workEntered = 0; run.Invoke(null, null);
                Check(runEntered == 0 && workEntered == 0 && target.transform.position == before
                    && !(bool)AccessTools.Field(aem, "isActive").GetValue(null), "sentence received during AEM stops before native run/confirm work");
                Check(!(bool)ray.Invoke(null, new object[] { player }), "real confined state blocks ordinary-piece entry");
                AccessTools.Field(aem, "HitPiece").SetValue(null, piece); AccessTools.Field(aem, "isActive").SetValue(null, true);
                work.Invoke(null, null);
                Check(workEntered == 0, "direct native confirm work cannot bypass confinement");
            }
            finally
            {
                sentence.SetValue(prison.Instance, previousSentence); region.SetValue(prison.Instance, previousRegion); active.SetValue(null, previousActive);
                if (fixtureZdo != null) fixtureZdo.Set("VMP_PP_Protected", false);
            }
        }
        private static bool HasOwner(MethodInfo method, string owner)
        { var info = Harmony.GetPatchInfo(method); return info != null && info.Owners.Contains(owner); }
        private static bool Throws<T>(Action action) where T : Exception
        { try { action(); return false; } catch (TargetInvocationException e) { return e.InnerException is T; } }
        private static void Check(bool value, string label) { checks++; if (!value) throw new InvalidOperationException(label); }
    }
}
