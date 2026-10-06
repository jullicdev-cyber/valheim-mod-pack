// Test-only detached fixtures. Never compile this file into the released mod.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;

namespace ValheimModPack.PartyPrison.NativeVerification
{
    public static class LayoutNativeChecks
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static readonly HashSet<GameObject> fixtureObjects = new HashSet<GameObject>();
        private static readonly HashSet<ZDO> fixtureZdos = new HashSet<ZDO>();

        public static void Run(Action<bool, string> check)
        {
            check(Player.m_localPlayer == null && Game.instance == null, "layout fixture has no live character or world");
            CheckSignPermission(check);
            CheckCellLayout(check);
            CheckMigrationSelection(check);
        }

        private static bool SkipFixtureAwake(Component __instance)
        { return __instance == null || !fixtureObjects.Contains(__instance.gameObject); }

        private static bool DetachedRevision(ZDO __instance)
        {
            if (!fixtureZdos.Contains(__instance)) return true;
            FieldInfo revision = typeof(ZDO).GetField("<DataRevision>k__BackingField", All);
            revision.SetValue(__instance, unchecked((uint)revision.GetValue(__instance) + 1));
            return false;
        }

        private static void CheckSignPermission(Action<bool, string> check)
        {
            var fixture = new Harmony("valheimmodpack.partyprison.nativeprobe.layout");
            GameObject root = null;
            ZDO zdo = null;
            try {
                var awake = new HarmonyMethod(typeof(LayoutNativeChecks).GetMethod("SkipFixtureAwake", All)); awake.priority = Priority.First;
                fixture.Patch(typeof(ZNetView).GetMethod("Awake", All), prefix: awake);
                fixture.Patch(typeof(Sign).GetMethod("Awake", All), prefix: awake);
                fixture.Patch(typeof(ZDO).GetMethod("IncreaseDataRevision", All),
                    prefix: new HarmonyMethod(typeof(LayoutNativeChecks).GetMethod("DetachedRevision", All)));
                root = new GameObject("PartyPrison.LayoutNativeFixture.Sign"); root.SetActive(false); fixtureObjects.Add(root);
                ZNetView view = root.AddComponent<ZNetView>();
                Sign sign = root.AddComponent<Sign>();
                zdo = new ZDO { m_uid = new ZDOID(-643591872, 1) }; fixtureZdos.Add(zdo);
                typeof(ZDO).GetField("m_prefab", All).SetValue(zdo, "sign".GetStableHashCode());
                typeof(ZNetView).GetField("m_zdo", All).SetValue(view, zdo);
                typeof(Sign).GetField("m_nview", All).SetValue(sign, view);
                FieldInfo widgetField = typeof(Sign).GetField("m_textWidget", All);
                check(widgetField != null && typeof(Component).IsAssignableFrom(widgetField.FieldType), "native sign exposes a Unity text component");
                var textObject = new GameObject("Native sign text", typeof(RectTransform), typeof(CanvasRenderer));
                textObject.SetActive(false); textObject.transform.SetParent(root.transform, false);
                Component widget = textObject.AddComponent(widgetField.FieldType); widgetField.SetValue(sign, widget);
                PropertyInfo textProperty = widget.GetType().GetProperty("text", All);
                FieldInfo author = typeof(Sign).GetField("m_author", All);
                FieldInfo lastRevision = typeof(Sign).GetField("m_lastRevision", All);
                FieldInfo isViewable = typeof(Sign).GetField("m_isViewable", All);
                MethodInfo updateText = typeof(Sign).GetMethod("UpdateText", All);
                MethodInfo updatePermission = typeof(Sign).GetMethod("UpdateViewPermission", All);
                check(author != null && lastRevision != null && isViewable != null && updateText != null && updatePermission != null,
                    "installed sign retains its native author, revision, text and permission methods");
                check(view.IsValid() && !view.IsOwner(), "sign uses a detached valid non-owner ZDO without any world registry");
                typeof(Sign).GetField("m_currentText", All).SetValue(sign, ArenaBuilder.CellSignText);
                zdo.Set(ZDOVars.s_text, ArenaBuilder.CellSignText); zdo.Set(ZDOVars.s_author, "host");
                lastRevision.SetValue(sign, UInt32.MaxValue);
                Exception nativeFailure = null;
                try { updateText.Invoke(sign, null); }
                catch (TargetInvocationException error) { nativeFailure = error.InnerException; }
                check(nativeFailure is InvalidOperationException && author.GetValue(sign) == null,
                    "actual native Sign.UpdateText reproduces the null host-author failure before the scoped repair");
                check((string)textProperty.GetValue(widget, null) != ArenaBuilder.CellSignText,
                    "the original permission failure denies native text before dereferencing the empty nullable author");
                Patches patches = Harmony.GetPatchInfo(updatePermission);
                check(patches != null && patches.Owners.Contains(Plugin.Id), "production scoped sign permission prefix is installed");
                zdo.Set(ArenaBuilder.ProtectedKey, true); zdo.Set(ArenaBuilder.SignKey, true);
                lastRevision.SetValue(sign, UInt32.MaxValue);
                updateText.Invoke(sign, null);
                check(author.GetValue(sign) != null && (bool)isViewable.GetValue(sign),
                    "production repair supplies native PlatformUserID.None and allows the old prison sign");
                check((string)textProperty.GetValue(widget, null) == ArenaBuilder.CellSignText,
                    "the repaired native UI component displays the requested Russian inscription");
                updatePermission.Invoke(sign, null);
                check((string)textProperty.GetValue(widget, null) == ArenaBuilder.CellSignText,
                    "the following native permission refresh remains stable instead of repeating an exception");
                zdo.Set(ZDOVars.s_author, ""); lastRevision.SetValue(sign, UInt32.MaxValue);
                updateText.Invoke(sign, null); updateText.Invoke(sign, null);
                check(author.GetValue(sign) != null && (bool)isViewable.GetValue(sign)
                    && (string)textProperty.GetValue(widget, null) == ArenaBuilder.CellSignText,
                    "new empty-author system signs pass both native revision and unchanged-revision paths");
                zdo.Set(ArenaBuilder.SignKey, false); author.SetValue(sign, null);
                MethodInfo repair = typeof(ArenaBuilder).GetMethod("RepairSignViewPermission", All);
                check(repair != null, "the scoped repair entry point exists");
                repair.Invoke(null, new object[] { sign });
                check(author.GetValue(sign) == null, "production repair leaves an ordinary unmarked sign author untouched");
            }
            finally {
                if (root != null) { UnityEngine.Object.DestroyImmediate(root); fixtureObjects.Remove(root); }
                if (zdo != null) { typeof(ZDO).GetMethod("Reset", All).Invoke(zdo, null); fixtureZdos.Remove(zdo); }
                fixture.UnpatchSelf();
            }
        }

        private static GameObject Prefab(string name)
        {
            GameObject value = PrefabManager.Instance.GetPrefab(name);
            if (value == null) throw new InvalidOperationException("Native layout prefab missing: " + name);
            return value;
        }

        private static Bounds SolidBounds(GameObject prefab)
        { return (Bounds)typeof(ArenaBuilder).GetMethod("SolidBounds", All).Invoke(null, new object[] { prefab }); }
        private static IList Plan()
        { return (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(typeof(ArenaBuilder).GetNestedType("Placement", All))); }
        private static object Field(object instance, string name)
        { return instance.GetType().GetField(name, All).GetValue(instance); }

        private static Bounds WorldBounds(object placement, Bounds prefabBounds)
        {
            Vector3 position = (Vector3)Field(placement, "Offset");
            Quaternion rotation = Quaternion.Euler(0f, (float)Field(placement, "Yaw"), 0f);
            var result = new Bounds(position + rotation * prefabBounds.center, Vector3.zero);
            foreach (float x in new[] { prefabBounds.min.x, prefabBounds.max.x })
                foreach (float y in new[] { prefabBounds.min.y, prefabBounds.max.y })
                    foreach (float z in new[] { prefabBounds.min.z, prefabBounds.max.z })
                        result.Encapsulate(position + rotation * new Vector3(x, y, z));
            return result;
        }

        private static void CheckCellLayout(Action<bool, string> check)
        {
            Bounds bedBounds = SolidBounds(Prefab("bed")), chestBounds = SolidBounds(Prefab(ArenaBuilder.CustodyPrefab));
            Bed nativeBed = Prefab("bed").GetComponent<Bed>();
            check(nativeBed != null && nativeBed.m_spawnPoint != null, "the actual bed prefab supplies a native lying attachment");
            IList furniture = Plan();
            typeof(ArenaBuilder).GetMethod("AppendCellFurniture", All).Invoke(null, new object[] { furniture, chestBounds, bedBounds });
            check(furniture.Count == 3, "cell furniture contains one chest, one sign and one bed");
            int beds = 0, chests = 0;
            var region = new PrisonRegion { Center = new PrisonPoint(0, 4, 0), CellSpawn = new PrisonPoint(-8, 1, 0),
                ArenaSpawn = new PrisonPoint(4, 1, 4), Radius = 18, HalfHeight = 8 };
            foreach (object placement in furniture) {
                string prefab = (string)Field(placement, "Prefab"); Vector3 offset = (Vector3)Field(placement, "Offset");
                check(ArenaBuilder.IsInsideCell(region, offset + Vector3.up), "furniture remains inside the cell: " + prefab);
                if (prefab == "bed") {
                    ++beds;
                    check((string)Field(placement, "Marker") == "VMP_PP_Bed" && Mathf.Abs(WorldBounds(placement, bedBounds).min.y) < .01f,
                        "marked native bed rests exactly on the finished floor");
                }
                if (prefab == ArenaBuilder.CustodyPrefab) {
                    ++chests; check((string)Field(placement, "Marker") == ArenaBuilder.KitKey, "cell equipment still uses the existing kit chest identity");
                }
            }
            check(beds == 1 && chests == 1, "layout adds one bed without multiplying the kit chest");
            Bounds grille = SolidBounds(Prefab("iron_wall_2x2")), gate = SolidBounds(Prefab("iron_grate"));
            IList walls = Plan();
            typeof(ArenaBuilder).GetMethod("AppendCellGrilles", All).Invoke(null, new object[] { walls, grille, gate });
            var bounds = new List<Bounds>();
            foreach (object placement in walls) {
                Bounds placed = WorldBounds(placement, grille); bounds.Add(placed);
                check((string)Field(placement, "Prefab") == "iron_wall_2x2" && (string)Field(placement, "Marker") == "VMP_PP_CellWall",
                    "cell wall replacement is a marked native metal grille");
                check(placed.min.y >= -.01f && placed.max.y <= 8.05f, "metal cell walls stay within the floor and ceiling");
                Vector3 point = (Vector3)Field(placement, "Offset");
                if (Mathf.Abs(point.z + 4f) < .01f && placed.min.y < gate.size.y - .02f)
                    check(placed.max.x <= -9f + .03f || placed.min.x >= -7f - .03f, "release door retains its native two-metre clear opening");
                if (Mathf.Abs(point.x + 4f) < .01f)
                    check(placed.min.y >= gate.size.y - .02f, "internal gate header starts above the native gate leaf");
            }
            check(walls.Count >= 64 && walls.Count <= 68, "complete cell enclosure fits the bounded native-piece budget");
            // Sample actual prefab collider bounds on three wall planes. This
            // verifies coverage rather than mirroring the loop's piece count.
            for (float y = .125f; y < 8f; y += .5f) {
                for (float z = -3.875f; z < 12f; z += .5f)
                    check(Covered(bounds, new Vector3(-12f, y, z)), "west metal wall continuously covers its full cell height");
                for (float x = -11.875f; x < -4f; x += .5f) {
                    check(Covered(bounds, new Vector3(x, y, 12f)), "north metal wall continuously covers its full cell width");
                    if (y >= gate.size.y || x <= -9f || x >= -7f)
                        check(Covered(bounds, new Vector3(x, y, -4f)), "foyer divider is closed outside the native release doorway");
                }
            }
        }

        private static bool Covered(List<Bounds> walls, Vector3 point)
        {
            foreach (Bounds bounds in walls) {
                Bounds tolerant = bounds; tolerant.Expand(.03f);
                if (tolerant.Contains(point)) return true;
            }
            return false;
        }

        private static void CheckMigrationSelection(Action<bool, string> check)
        {
            MethodInfo select = typeof(ArenaBuilder).GetMethod("IsOldCellStoneWall", All);
            check(select != null, "upgrade uses a scoped old-cell-wall selection predicate");
            int stone = "stone_wall_4x2".GetStableHashCode(), small = "stone_wall_2x1".GetStableHashCode();
            foreach (float z in new[] { -2f, 2f, 6f, 10f })
                check((bool)select.Invoke(null, new object[] { stone, -12f, z }), "old west cell wall is selected for metal replacement");
            foreach (float x in new[] { -10f, -6f }) {
                check((bool)select.Invoke(null, new object[] { stone, x, 12f }), "old north cell wall is selected for metal replacement");
                check((bool)select.Invoke(null, new object[] { stone, x, -4f }), "old stone cell divider is selected for replacement");
            }
            check((bool)select.Invoke(null, new object[] { small, -8f, -4f })
                && (bool)select.Invoke(null, new object[] { small, -4f, 0f }), "both old stone gate headers become metal");
            foreach (Vector2 position in new[] { new Vector2(-12, -10), new Vector2(-10, -12), new Vector2(2, -4), new Vector2(12, 6), new Vector2(10, 12) })
                check(!(bool)select.Invoke(null, new object[] { stone, position.x, position.y }), "upgrade preserves public foyer and unrelated arena stone walls");
            int chest = ArenaBuilder.CustodyPrefab.GetStableHashCode();
            foreach (Vector2 position in new[] { new Vector2(-6, -10), new Vector2(-2, -10), new Vector2(2, -10), new Vector2(6, -10), new Vector2(-10, 3) })
                check(!(bool)select.Invoke(null, new object[] { chest, position.x, position.y }), "wall migration never selects any existing property or kit chest for destruction");
        }
    }
}
