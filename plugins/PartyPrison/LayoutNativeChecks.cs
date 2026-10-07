// Test-only detached fixtures. Never compile this file into the released mod.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
            CheckExpandedLayout(check);
            CheckCellFixtures(check);
            CheckRebuildStorage(check);
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
            var region = new PrisonRegion { Center = new PrisonPoint(0, 6, 0), CellSpawn = new PrisonPoint(-14, 1, 0),
                ArenaSpawn = new PrisonPoint(4, 1, 4), Radius = 26, HalfHeight = 8 };
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
                check(placed.min.y >= -.01f && placed.max.y <= 12.05f, "metal cell walls stay within the taller floor and ceiling");
                Vector3 point = (Vector3)Field(placement, "Offset");
                check(Mathf.Abs(point.x) < 17.99f && Mathf.Abs(point.z) < 17.99f, "metal partitions never replace the external stone shell");
                if (Mathf.Abs(point.z + 10f) < .01f && point.x < -10f && placed.min.y < gate.size.y - .02f)
                    check(placed.max.x <= -15f + .03f || placed.min.x >= -13f - .03f, "release door retains its native two-metre clear opening");
                if (Mathf.Abs(point.x + 10f) < .01f && placed.min.y < gate.size.y - .02f)
                    check(placed.max.z <= -1f + .03f || placed.min.z >= 1f - .03f, "internal gate retains its native two-metre clear opening");
            }
            check(walls.Count >= 192 && walls.Count <= 198, "complete internal partitions fit the bounded native-piece budget");
            // Sample actual prefab collider bounds on three wall planes. This
            // verifies coverage rather than mirroring the loop's piece count.
            for (float y = .125f; y < 12f; y += .5f) {
                for (float z = -9.875f; z < 18f; z += .5f)
                    if (y >= gate.size.y || z <= -1f || z >= 1f)
                        check(Covered(bounds, new Vector3(-10f, y, z)), "internal cell-arena grille is closed outside its native gate");
                for (float x = -17.875f; x < 18f; x += .5f) {
                    if (y >= gate.size.y || x <= -15f || x >= -13f)
                        check(Covered(bounds, new Vector3(x, y, -10f)), "internal corridor divider is closed outside the release doorway");
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

        private static void CheckExpandedLayout(Action<bool, string> check)
        {
            var prefabBounds = new Dictionary<string, Bounds>();
            foreach (string name in new[] { "stone_floor_2x2", "stone_wall_4x2", "stone_wall_2x1", "stone_stair", "iron_wall_2x2", "iron_grate", "piece_bench01",
                ArenaBuilder.CustodyPrefab, "piece_dvergr_lantern", "crystal_wall_1x1", "sign", "bed" })
                prefabBounds[name] = SolidBounds(Prefab(name));
            IList plan = (IList)typeof(ArenaBuilder).GetMethod("CreateLayoutPlan", All).Invoke(null, new object[] { prefabBounds });
            check(plan.Count + 6 <= 1280, "expanded native floor, roof, scenery, storage and two cell fixtures fit the hard piece budget");
            var floors = new List<Bounds>(); var roof = new List<Bounds>(); var shell = new List<Bounds>();
            var obstacles = new List<Bounds>(); var windows = new List<Bounds>(); int stairs = 0;
            foreach (object placement in plan) {
                string name = (string)Field(placement, "Prefab");
                if (name == "sign" || name == "piece_dvergr_lantern" || name == "bed" || name == "piece_bench01" || name == ArenaBuilder.CustodyPrefab) continue;
                Bounds b = WorldBounds(placement, prefabBounds[name]); Vector3 offset = (Vector3)Field(placement, "Offset");
                if (name == "stone_floor_2x2" && b.max.y < .01f) floors.Add(b);
                else if (name == "stone_floor_2x2" && b.min.y > 11.99f) roof.Add(b);
                else if (Mathf.Abs(offset.x) >= 17.99f || Mathf.Abs(offset.z) >= 17.99f) {
                    shell.Add(b); check(name.StartsWith("stone_") || name == "crystal_wall_1x1", "every exterior collider is stone or spectator crystal");
                }
                else if (name.StartsWith("stone_")) obstacles.Add(b);
                if (name == "crystal_wall_1x1") windows.Add(b);
                if (name == "stone_stair") ++stairs;
            }
            check(stairs >= 2 && obstacles.Count > 20, "native stone stairs and solid raised levels add varied battle routes");
            for (float x = -17.75f; x < 18f; x += 1f)
                for (float z = -17.75f; z < 18f; z += 1f) {
                    check(Covered(floors, new Vector3(x, 0f, z)), "expanded room has a continuous solid floor");
                    check(Covered(roof, new Vector3(x, 12f, z)), "expanded room has a continuous solid ceiling");
                }
            for (float y = .125f; y < 12f; y += .5f)
                for (float along = -17.875f; along < 18f; along += .5f) {
                    check(Covered(shell, new Vector3(along, y, -18f)), "south stone shell has no open gaps");
                    check(Covered(shell, new Vector3(along, y, 18f)), "north shell remains solid through stone and crystal");
                    check(Covered(shell, new Vector3(18f, y, along)), "east shell remains solid through stone and crystal");
                    if (y >= 4f || along <= -15f || along >= -13f)
                        check(Covered(shell, new Vector3(-18f, y, along)), "west shell is stone outside the public doorway");
                }
            for (float y = .125f; y < 3f; y += .25f)
                for (float along = 2.125f; along < 10f; along += .5f) {
                    check(Covered(windows, new Vector3(along, y, 18f)), "north spectator glass is one native1m block taller");
                    check(Covered(windows, new Vector3(18f, y, along)), "east spectator glass is one native1m block taller");
                }
            var expanded = new PrisonRegion { Center = new PrisonPoint(0, 6, 0), CellSpawn = new PrisonPoint(-14, 1, 0),
                ArenaSpawn = new PrisonPoint(4, 1, 4), Radius = 26, HalfHeight = 8 };
            foreach (int index in new[] { 0, 1, 2, 3, 4, 5, 6, 7 }) {
                PrisonPoint p = ArenaGeometry.Spawn(expanded, index); Vector3 point = new Vector3((float)p.X, (float)p.Y, (float)p.Z);
                check(ArenaBuilder.IsInsideArena(expanded, point), "native confinement accepts each multi-side enemy spawn");
                foreach (Bounds b in obstacles) {
                    Bounds broad = b; broad.Expand(new Vector3(1.1f, 1.1f, 1.1f));
                    check(!broad.Contains(point), "spawn capsule stays outside all native retaining walls, cover and stairs");
                }
            }
            var old = new PrisonRegion { Center = new PrisonPoint(0, 4, 0), CellSpawn = new PrisonPoint(-8, 1, 0),
                ArenaSpawn = new PrisonPoint(4, 1, 4), Radius = 18, HalfHeight = 8 };
            check(!ArenaBuilder.ContainsRoom(old, new Vector3(15, 1, 15)) && ArenaBuilder.ContainsRoom(expanded, new Vector3(15, 1, 15)),
                "older saved prisons keep their physical containment until an explicit confirmed rebuild");
            check(ArenaBuilder.IsInsideCell(old, new Vector3(-6, 1, 7)) && ArenaBuilder.IsInsideCell(expanded, new Vector3(-14, 1, -6)),
                "legacy and expanded console approaches both lie inside the cell");
        }

        private static ZDO StorageZdo(uint id, string prefab, string marker, int custodyIndex, Vector3 position)
        {
            var zdo = new ZDO { m_uid = new ZDOID(-643591872, id) }; fixtureZdos.Add(zdo);
            typeof(ZDO).GetField("m_prefab", All).SetValue(zdo, prefab.GetStableHashCode());
            typeof(ZDO).GetField("m_position", All).SetValue(zdo, position); zdo.Set(ArenaBuilder.ProtectedKey, true);
            if (marker != null) zdo.Set(marker, true);
            if (custodyIndex >= 0) zdo.Set(ArenaBuilder.CustodyIndexKey, custodyIndex);
            return zdo;
        }

        private static object FixturePlacement(string prefab, Vector3 position, float yaw)
        { return Activator.CreateInstance(typeof(ArenaBuilder).GetNestedType("Placement", All), All, null, new object[] { prefab, position, yaw }, null); }

        private static void CheckCellFixtures(Action<bool, string> check)
        {
            GameObject firePrefab = Prefab(PrisonContent.PrisonCampfirePrefab), bedPrefab = Prefab("bed");
            Bounds console = SolidBounds(Prefab(PrisonConsole.PrefabName)), fire = SolidBounds(firePrefab);
            Bounds bed = SolidBounds(Prefab("bed")), chest = SolidBounds(Prefab(ArenaBuilder.CustodyPrefab));
            foreach (bool expanded in new[] { false, true }) {
                var region = new PrisonRegion { Center = new PrisonPoint(0, expanded ? 6 : 4, 0), CellSpawn = new PrisonPoint(expanded ? -14 : -8, 1, 0),
                    ArenaSpawn = new PrisonPoint(4, 1, 4), Radius = expanded ? 26 : 18, HalfHeight = 8 };
                Vector3 controlPoint = expanded ? new Vector3(-14, -console.min.y, -6) : new Vector3(-6, -console.min.y, 7);
                Vector3 firePoint = expanded ? new Vector3(-14, -fire.min.y, 13) : new Vector3(-7, -fire.min.y, 10);
                Vector3 bedPoint = expanded ? new Vector3(-16, -bed.min.y, 16) : new Vector3(-10, -bed.min.y, 10);
                Vector3 chestPoint = new Vector3(expanded ? -16 : -10, -chest.min.y, 3);
                Bounds controlBox = WorldBounds(FixturePlacement(PrisonConsole.PrefabName, controlPoint, 0), console);
                Bounds fireBox = WorldBounds(FixturePlacement(PrisonContent.PrisonCampfirePrefab, firePoint, 0), fire);
                Bounds bedBox = WorldBounds(FixturePlacement("bed", bedPoint, 90), bed);
                Bounds chestBox = WorldBounds(FixturePlacement(ArenaBuilder.CustodyPrefab, chestPoint, 90), chest);
                check(!controlBox.Intersects(fireBox) && !controlBox.Intersects(bedBox) && !controlBox.Intersects(chestBox),
                    "console native colliders do not overlap the fire, bed or equipment storage in " + (expanded ? "expanded" : "legacy") + " cell");
                check(!fireBox.Intersects(bedBox) && !fireBox.Intersects(chestBox),
                    "permanent campfire native colliders remain clear of bedding and belongings in " + (expanded ? "expanded" : "legacy") + " cell");
                float divider = (float)ArenaGeometry.Divider(region), half = (float)ArenaGeometry.RoomHalfWidth(region);
                foreach (Bounds fixture in new[] { controlBox, fireBox }) {
                    check(fixture.min.x > -half && fixture.max.x < divider && fixture.min.z > divider && fixture.max.z < half,
                        "complete cell fixture stays inside stone shell and internal iron partitions");
                    check(Mathf.Abs(fixture.min.y) < .01f, "cell fixture native collider is aligned with the finished floor");
                }
                check((firePoint - bedPoint).sqrMagnitude < 16f, "campfire is placed close to the bed for resting warmth");
                Bed nativeBed = bedPrefab.GetComponent<Bed>();
                Vector3 lyingPoint = bedPoint + Quaternion.Euler(0, 90, 0) * bedPrefab.transform.InverseTransformPoint(nativeBed.m_spawnPoint.position)
                    + Vector3.up * .5f;
                bool warmed = false;
                foreach (EffectArea area in firePrefab.GetComponentsInChildren<EffectArea>(true)) {
                    if ((area.m_type & EffectArea.Type.Heat) == 0) continue;
                    foreach (Collider collider in area.GetComponents<Collider>()) {
                        string shape = collider is SphereCollider ? "sphere radius=" + ((SphereCollider)collider).radius.ToString("F2")
                            : collider is BoxCollider ? "box size=" + ((BoxCollider)collider).size.ToString("F2") : collider.GetType().Name;
                        Debug.Log("[Party Prison native layout] " + (expanded ? "expanded" : "legacy") + " campfire heat " + shape
                            + "; bed attachment relative to fire=" + (lyingPoint - firePoint).ToString("F2"));
                        check(collider.isTrigger, "native permanent fire heat uses a trigger volume: " + shape);
                        if (InsideNativeHeat(firePrefab, collider, lyingPoint - firePoint)) warmed = true;
                    }
                }
                check(warmed, "native campfire heat collider covers the bed attachment in " + (expanded ? "expanded" : "legacy") + " cell");
                check(ArenaBuilder.IsInsideCell(region, new Vector3(controlPoint.x, 1, controlPoint.z))
                    && ArenaBuilder.IsInsideCell(region, new Vector3(firePoint.x, 1, firePoint.z)), "legacy and expanded fixture approaches stay inside inmate cell permissions");
            }
        }

        private static bool InsideNativeHeat(GameObject prefab, Collider collider, Vector3 fireLocalPoint)
        {
            Vector3 point = collider.transform.InverseTransformPoint(prefab.transform.TransformPoint(fireLocalPoint));
            SphereCollider sphere = collider as SphereCollider;
            if (sphere != null) return (point - sphere.center).sqrMagnitude <= sphere.radius * sphere.radius;
            BoxCollider box = collider as BoxCollider;
            if (box != null) { Vector3 delta = point - box.center; return Mathf.Abs(delta.x) <= box.size.x * .5f && Mathf.Abs(delta.y) <= box.size.y * .5f && Mathf.Abs(delta.z) <= box.size.z * .5f; }
            CapsuleCollider capsule = collider as CapsuleCollider;
            if (capsule != null) {
                Vector3 delta = point - capsule.center; float half = Mathf.Max(0f, capsule.height * .5f - capsule.radius);
                if (capsule.direction == 0) delta.x -= Mathf.Clamp(delta.x, -half, half);
                else if (capsule.direction == 1) delta.y -= Mathf.Clamp(delta.y, -half, half);
                else delta.z -= Mathf.Clamp(delta.z, -half, half);
                return delta.sqrMagnitude <= capsule.radius * capsule.radius;
            }
            return false;
        }

        private static byte[] StoragePayload(bool withWood)
        {
            var inventory = new Inventory("PartyPrison.RebuildStorageFixture", null, 8, 4);
            if (withWood) {
                GameObject prefab = Prefab("Wood"); ItemDrop.ItemData item = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
                item.m_dropPrefab = prefab; item.m_stack = 1; item.m_gridPos = new Vector2i(0, 0);
                inventory.AddItem(item);
            }
            var package = new ZPackage(); inventory.Save(package); return package.GetArray();
        }

        private static byte[] MissingStoragePrefab(byte[] valid)
        {
            byte[] altered = (byte[])valid.Clone();
            using (var stream = new MemoryStream(altered, true)) using (var reader = new BinaryReader(stream)) {
                int version = reader.ReadInt32();
                if (version != 108 && version != 109 || reader.ReadUInt16() != 1) throw new InvalidOperationException("Unexpected native storage fixture format.");
                reader.ReadInt32(); reader.ReadByte(); reader.ReadByte(); reader.ReadByte(); int flags = reader.ReadByte();
                if ((flags & 4) != 0) reader.ReadUInt16(); if ((flags & 8) != 0) reader.ReadUInt16();
                if ((flags & 16) != 0) reader.ReadInt32();
                if ((flags & 32) != 0) { reader.ReadInt64(); reader.ReadString(); }
                if ((flags & 64) == 0) throw new InvalidOperationException("Storage fixture has no prefab field.");
                Array.Copy(BitConverter.GetBytes("vmp_missing_rebuild_fixture_item".GetStableHashCode()), 0, altered, (int)stream.Position, 4);
            }
            return altered;
        }

        private static bool StorageRejected(MethodInfo guard, PrisonRegion region, List<ZDO> objects, int version,
            Func<int, GameObject> prefabs, Func<ZDO, Container> loaded)
        {
            try { guard.Invoke(null, new object[] { region, objects, version, prefabs, loaded }); return false; }
            catch (TargetInvocationException error) {
                if (error.InnerException is InvalidOperationException || error.InnerException is InvalidDataException || error.InnerException is FormatException) return true;
                throw;
            }
        }

        private static void CheckRebuildStorage(Action<bool, string> check)
        {
            var harmony = new Harmony("valheimmodpack.partyprison.nativeprobe.rebuild-storage");
            var records = new List<ZDO>();
            try {
                harmony.Patch(typeof(ZDO).GetMethod("IncreaseDataRevision", All),
                    prefix: new HarmonyMethod(typeof(LayoutNativeChecks).GetMethod("DetachedRevision", All)));
                MethodInfo guard = typeof(ArenaBuilder).GetMethod("RequireEmptyStoredContainers", All, null,
                    new[] { typeof(PrisonRegion), typeof(IList<ZDO>), typeof(int), typeof(Func<int, GameObject>), typeof(Func<ZDO, Container>) }, null);
                check(guard != null, "rebuild guard validates captured native ZDO storage independently of current layout version");
                var region = new PrisonRegion { Center = new PrisonPoint(0, 4, 0), CellSpawn = new PrisonPoint(-8, 1, 0),
                    ArenaSpawn = new PrisonPoint(4, 1, 4), Radius = 18, HalfHeight = 8 };
                for (int index = 0; index < 4; ++index)
                    records.Add(StorageZdo((uint)(100 + index), ArenaBuilder.CustodyPrefab, ArenaBuilder.CustodyKey, index, new Vector3(-6 + index * 4, 0, -10)));
                ZDO kit = StorageZdo(104, ArenaBuilder.CustodyPrefab, ArenaBuilder.KitKey, -1, new Vector3(-10, 0, 3)); records.Add(kit);
                ZDO armory = StorageZdo(105, ArenaBuilder.CustodyPrefab, null, -1, new Vector3(6, 0, 6)); armory.Set(ArenaBuilder.ArmoryKey, true); records.Add(armory);
                ZDO stone = StorageZdo(106, "stone_wall_4x2", null, -1, new Vector3(12, 0, 2)); records.Add(stone);
                byte[] empty = StoragePayload(false), occupied = StoragePayload(true);
                foreach (ZDO zdo in records) if (zdo != stone) zdo.Set(ZDOVars.s_items, empty);
                Func<int, GameObject> prefabs = hash => hash == ArenaBuilder.CustodyPrefab.GetStableHashCode() ? Prefab(ArenaBuilder.CustodyPrefab)
                    : hash == "stone_wall_4x2".GetStableHashCode() ? Prefab("stone_wall_4x2") : null;
                Func<ZDO, Container> loaded = zdo => null;
                check(!StorageRejected(guard, region, records, 4, prefabs, loaded), "empty v4 custody, kit and legacy armory storage can be explicitly rebuilt");
                check(!StorageRejected(guard, region, records, 5, prefabs, loaded), "new-version empty storage obeys the same safe rebuild guard");
                records.Add(records[0]);
                check(!StorageRejected(guard, region, records, 4, prefabs, loaded), "overlapping protected and armory captures deduplicate the same native ZDO ID");
                records.RemoveAt(records.Count - 1);
                kit.Set(ZDOVars.s_items, occupied);
                check(StorageRejected(guard, region, records, 4, prefabs, loaded), "ordinary deposited belongings in the equipment chest block a v4 rebuild");
                check(Convert.ToBase64String(kit.GetByteArray(ZDOVars.s_items, null)) == Convert.ToBase64String(occupied), "rejecting equipment storage preserves its exact saved native payload");
                kit.Set(ZDOVars.s_items, empty); records[2].Set(ZDOVars.s_items, occupied);
                check(StorageRejected(guard, region, records, 4, prefabs, loaded), "custody belongings block rebuilding an older saved layout");
                records[2].Set(ZDOVars.s_items, empty);
                records.Remove(records[3]);
                check(StorageRejected(guard, region, records, 4, prefabs, loaded), "missing one of four permanent property chest records fails closed");
                records.Insert(3, StorageZdo(107, ArenaBuilder.CustodyPrefab, ArenaBuilder.CustodyKey, 3, new Vector3(6, 0, -10)));
                records[3].Set(ZDOVars.s_items, empty); records[3].Set(ArenaBuilder.CustodyIndexKey, 2);
                check(StorageRejected(guard, region, records, 4, prefabs, loaded), "distinct property chests with duplicate permanent indexes fail closed");
                records[3].Set(ArenaBuilder.CustodyIndexKey, 3);
                armory.Set(ZDOVars.s_items, occupied);
                check(StorageRejected(guard, region, records, 4, prefabs, loaded), "legacy marked armory containers also retain visitors' deposited belongings");
                armory.Set(ZDOVars.s_items, empty); kit.Set(ZDOVars.s_items, MissingStoragePrefab(occupied));
                check(StorageRejected(guard, region, records, 4, prefabs, loaded), "missing item prefab cannot masquerade as an empty equipment chest during rebuild");
                kit.Set(ZDOVars.s_items, empty);
                typeof(ZDO).GetField("m_prefab", All).SetValue(kit, "vmp_missing_saved_fixture_chest".GetStableHashCode());
                check(StorageRejected(guard, region, records, 4, prefabs, loaded), "unknown saved protected chest prefab cancels destruction even before loading");
                typeof(ZDO).GetField("m_prefab", All).SetValue(kit, ArenaBuilder.CustodyPrefab.GetStableHashCode());
                ZDO legacy = StorageZdo(108, ArenaBuilder.CustodyPrefab, null, -1, new Vector3(6, 0, 6)); legacy.Set(ArenaBuilder.ArmoryKey, true);
                var legacyOnly = new List<ZDO> { legacy };
                legacy.Set(ZDOVars.s_items, Convert.ToBase64String(occupied));
                check(StorageRejected(guard, region, legacyOnly, 1, prefabs, loaded), "native legacy base64 inventory protects early armory contents");
                check(legacy.GetString(ZDOVars.s_items, "") == Convert.ToBase64String(occupied), "legacy storage rejection preserves the original string bytes");
                legacy.Set(ZDOVars.s_items, "broken_base64!");
                check(StorageRejected(guard, region, legacyOnly, 1, prefabs, loaded), "corrupt legacy inventory never becomes empty during explicit rebuild");
                legacy.Set(ZDOVars.s_items, Convert.ToBase64String(empty));
                check(!StorageRejected(guard, region, legacyOnly, 1, prefabs, loaded), "early layouts without four property chests remain safely rebuildable when native storage is empty");
                typeof(ZDO).GetField("m_position", All).SetValue(legacy, new Vector3(50, 0, 50)); legacy.Set(ZDOVars.s_items, "broken_base64!");
                check(!StorageRejected(guard, region, legacyOnly, 1, prefabs, loaded), "unrelated storage outside the exact old footprint does not block or enter its rebuild");
            }
            finally {
                foreach (ZDO zdo in new List<ZDO>(fixtureZdos)) {
                    typeof(ZDO).GetMethod("Reset", All).Invoke(zdo, null); fixtureZdos.Remove(zdo);
                }
                harmony.UnpatchSelf();
            }
        }
    }
}
