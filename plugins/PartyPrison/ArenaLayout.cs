using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ValheimModPack.PartyPrison
{
    public static partial class ArenaBuilder
    {
        public const string ArenaStairKey = "VMP_PP_ArenaStair180";
        public const string ArenaStairSupportKey = "VMP_PP_ArenaStairSupport";
        private static readonly FieldInfo SignAuthorField = typeof(Sign).GetField("m_author", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>Only the system sign needs the native host-sentinel workaround.</summary>
        public static void RepairSignViewPermission(Sign sign)
        {
            if (sign == null || !HasMarker(sign.gameObject, SignKey)) return;
            ZNetView view = sign.GetComponent<ZNetView>();
            if (view == null || !view.IsValid()) return;
            ZDO zdo = view.GetZDO();
            if (ZNet.instance != null && ZNet.instance.IsServer() && view.IsOwner()
                && zdo.GetString(ZDOVars.s_author, "") == "host") {
                zdo.Set(ZDOVars.s_author, "");
                zdo.Set(ZDOVars.s_authorDisplayName, "");
            }
            // Sign.UpdateViewPermission forgets to return after !HasValue. An
            // explicitly empty native identity takes the ordinary public path.
            if (SignAuthorField == null) throw new MissingFieldException("Sign", "m_author");
            if (SignAuthorField.GetValue(sign) == null)
                SignAuthorField.SetValue(sign, (Splatform.PlatformUserID?)Splatform.PlatformUserID.None);
        }

        public static bool IsPrisonBed(GameObject gameObject) { return HasMarker(gameObject, BedKey); }

        /// <summary>Rest on the native bed without changing the character's home spawn.</summary>
        public static bool RestOnBed(Bed bed, Player player)
        {
            if (bed == null || player == null || !IsPrisonBed(bed.gameObject) || bed.m_spawnPoint == null) return false;
            // The isBed argument deliberately remains false: a visual rest must
            // not set s_inBed or trigger the server's all-players-asleep clock.
            player.AttachStart(bed.m_spawnPoint, bed.gameObject, true, false, false, "attach_bed", new Vector3(0f, .5f, 0f), null);
            return true;
        }

        private static void AppendCellGrilles(List<Placement> plan, Bounds grille, Bounds gate)
        {
            for (int tier = 0; tier < 6; ++tier) {
                float y = tier * 2f - grille.min.y;
                // Both metal planes are internal. The outer north and west cell
                // walls are stone like the rest of the enclosure.
                float[] positions = tier < 2 ? new float[] { -17f, -16f, -12f, -11f } : new float[] { -17f, -15f, -13f, -11f };
                foreach (float x in positions)
                    plan.Add(new Placement("iron_wall_2x2", new Vector3(x, y, -10f), 0f, CellWallKey, -1));
                for (int segment = 0; segment < 14; ++segment)
                    plan.Add(new Placement("iron_wall_2x2", new Vector3(-9f + segment * 2f, y, -10f), 0f, CellWallKey, -1));
                float[] zPositions = tier < 2
                    ? new float[] { -9f, -7f, -5f, -3f, -2f, 2f, 3f, 5f, 7f, 9f, 11f, 13f, 15f, 17f }
                    : new float[] { -9f, -7f, -5f, -3f, -1f, 1f, 3f, 5f, 7f, 9f, 11f, 13f, 15f, 17f };
                foreach (float z in zPositions)
                    plan.Add(new Placement("iron_wall_2x2", new Vector3(-10f, y, z), 90f, CellWallKey, -1));
            }
            // A native gate can be shorter than its four-metre opening. Fill
            // exactly the space above the collider rather than closing the leaf.
            for (float height = gate.size.y; height < 4f; height += grille.size.y) {
                float y = height - grille.min.y;
                plan.Add(new Placement("iron_wall_2x2", new Vector3(-14f, y, -10f), 0f, CellWallKey, -1));
                plan.Add(new Placement("iron_wall_2x2", new Vector3(-10f, y, 0f), 90f, CellWallKey, -1));
            }
        }

        private static List<Placement> CreateLayoutPlan(Dictionary<string, Bounds> bounds)
        {
            var plan = new List<Placement>();
            Bounds floor = bounds["stone_floor_2x2"], wall = bounds["stone_wall_4x2"], small = bounds["stone_wall_2x1"];
            // The continuous floor/roof remains solid; six stone tiers enclose a
            // twelve-metre room instead of the former eight-metre room.
            for (int x = 0; x < 18; ++x)
                for (int z = 0; z < 18; ++z) {
                    float px = -17f + x * 2f, pz = -17f + z * 2f;
                    plan.Add(new Placement("stone_floor_2x2", new Vector3(px, -floor.max.y, pz), 0f));
                    plan.Add(new Placement("stone_floor_2x2", new Vector3(px, RoomHeight - floor.min.y, pz), 0f));
                }
            for (int tier = 0; tier < 6; ++tier)
                for (int segment = 0; segment < 9; ++segment) {
                    float along = -16f + segment * 4f, y = tier * 2f - wall.min.y;
                    plan.Add(new Placement("stone_wall_4x2", new Vector3(along, y, -RoomHalfWidth), 0f));
                    if (!IsWindowSegment(tier, segment)) {
                        plan.Add(new Placement("stone_wall_4x2", new Vector3(along, y, RoomHalfWidth), 0f));
                        plan.Add(new Placement("stone_wall_4x2", new Vector3(RoomHalfWidth, y, along), 90f));
                    }
                    if (segment >= 2 || tier >= 2)
                        plan.Add(new Placement("stone_wall_4x2", new Vector3(-RoomHalfWidth, y, along), 90f));
                }
            // Fill the west doorway shoulders while retaining its native 2m
            // opening. Stone strips above the taller windows close the 3–4m gap.
            for (int tier = 0; tier < 4; ++tier)
                foreach (float z in new float[] { -17f, -16f, -12f, -11f })
                    plan.Add(new Placement("stone_wall_2x1", new Vector3(-RoomHalfWidth, tier - small.min.y, z), 90f));
            foreach (float along in new float[] { 3f, 5f, 7f, 9f }) {
                plan.Add(new Placement("stone_wall_2x1", new Vector3(along, 3f - small.min.y, RoomHalfWidth), 0f));
                plan.Add(new Placement("stone_wall_2x1", new Vector3(RoomHalfWidth, 3f - small.min.y, along), 90f));
            }
            Bounds gate = bounds["iron_grate"];
            plan.Add(new Placement("iron_grate", new Vector3(-14f, -gate.min.y, -10f), 0f, ExitKey, -1));
            plan.Add(new Placement("iron_grate", new Vector3(-10f, -gate.min.y, 0f), 90f, InnerGateKey, -1));
            plan.Add(new Placement("piece_bench01", new Vector3(-16f, -bounds["piece_bench01"].min.y, 7f), 90f));
            AppendWindows(plan, bounds["crystal_wall_1x1"]);
            AppendCellFurniture(plan, bounds[CustodyPrefab], bounds["bed"]);
            AppendCellGrilles(plan, bounds["iron_wall_2x2"], gate);
            AppendLamps(plan);
            AppendArenaTerrain(plan, bounds);
            return plan;
        }

        private static void AppendArenaTerrain(List<Placement> plan, Dictionary<string, Bounds> bounds)
        {
            Bounds wall = bounds["stone_wall_4x2"];
            // Two solid raised islands provide 2m and 4m fighting levels. Their
            // retaining bodies have no hidden hollow chambers to trap mobs.
            AppendPlatform(plan, bounds, -4f, 8f, 2f);
            AppendPlatform(plan, bounds, 8f, -2f, 4f);
            plan.AddRange(CreateArenaStairPlan(bounds));
            // Low independent walls break lines of sight without sealing paths,
            // blocking the inner gate, or occupying any scheduled spawn anchor.
            plan.Add(new Placement("stone_wall_4x2", new Vector3(1f, -wall.min.y, 0f), 0f));
            plan.Add(new Placement("stone_wall_4x2", new Vector3(8f, -wall.min.y, 9f), 90f));
        }

        private static void AppendPlatform(List<Placement> plan, Dictionary<string, Bounds> bounds, float x, float z, float top)
        {
            Bounds floor = bounds["stone_floor_2x2"], wall = bounds["stone_wall_4x2"];
            for (int tileX = 0; tileX < 3; ++tileX)
                for (int tileZ = 0; tileZ < 3; ++tileZ)
                    plan.Add(new Placement("stone_floor_2x2", new Vector3(x + 1f + tileX * 2f, top - floor.max.y, z + 1f + tileZ * 2f), 0f));
            for (float height = 0f; height < top; height += 2f)
                for (int row = 0; row < 6; ++row) {
                    // Wall thickness is native1m; tightly spaced rows fill all
                    // of the island instead of forming an inaccessible chamber.
                    plan.Add(new Placement("stone_wall_4x2", new Vector3(x + 2f, height - wall.min.y, z + .5f + row), 0f));
                    plan.Add(new Placement("stone_wall_4x2", new Vector3(x + 4f, height - wall.min.y, z + .5f + row), 0f));
                }
        }

        private static List<Placement> CreateArenaStairPlan(Dictionary<string, Bounds> bounds)
        {
            var plan = new List<Placement>();
            AppendStairFlight(plan, bounds["stone_stair"], bounds["stone_floor_2x2"], new Vector3(-1f, 0f, 8f), 0f, 2f);
            AppendStairFlight(plan, bounds["stone_stair"], bounds["stone_floor_2x2"], new Vector3(8f, 0f, 1f), 90f, 4f);
            return plan;
        }

        private static void AppendStairFlight(List<Placement> plan, Bounds stair, Bounds stone, Vector3 topEdge, float approachYaw, float top)
        {
            float rise = stair.size.y, run = stair.size.z;
            if (rise < .25f || rise > 4f || run < .5f || run > 4f)
                throw new InvalidOperationException("Изменились размеры каменной лестницы Valheim.");
            if (stone.size.y < .25f || stone.size.y > 2f || stone.size.x + .1f < stair.size.x || stone.size.z + .1f < run)
                throw new InvalidOperationException("Изменились размеры каменного основания лестницы Valheim.");
            int steps = Mathf.CeilToInt(top / rise);
            Quaternion approach = Quaternion.Euler(0f, approachYaw, 0f);
            for (int step = 0; step < steps; ++step) {
                float baseHeight = top - (steps - step) * rise;
                Vector3 offset = topEdge + approach * new Vector3(0f, 0f, -(steps - step - .5f) * run);
                offset.y = baseHeight - stair.min.y;
                // Native stone_stair rises toward its local -Z. Keep the saved
                // footprints, but reverse only its orientation toward the island.
                plan.Add(new Placement("stone_stair", offset, approachYaw + 180f, ArenaStairKey, -1));
                for (float bottom = 0f; bottom + stone.size.y <= baseHeight + .01f; bottom += stone.size.y) {
                    Vector3 support = offset; support.y = bottom - stone.min.y;
                    plan.Add(new Placement("stone_floor_2x2", support, approachYaw, ArenaStairSupportKey, -1));
                }
            }
        }

        private sealed class ArenaStairRetrofitPlan
        {
            internal readonly List<Placement> Create = new List<Placement>();
            internal readonly List<ZDO> Remove = new List<ZDO>();
            internal readonly List<ZDO> Keep = new List<ZDO>();
        }

        /// <summary>A detached bounded plan; only expected protected stair slots can be replaced.</summary>
        private static ArenaStairRetrofitPlan PlanArenaStairRetrofit(List<ZDO> pieces, List<Placement> desired,
            Vector3 basePoint, Quaternion rotation)
        {
            var plan = new ArenaStairRetrofitPlan();
            var remove = new HashSet<ZDOID>();
            foreach (Placement placement in desired) {
                Vector3 position = basePoint + rotation * placement.Offset;
                Quaternion facing = rotation * Quaternion.Euler(0f, placement.Yaw, 0f);
                ZDO correct = null;
                var matches = new List<ZDO>();
                foreach (ZDO old in pieces) {
                    if (old == null || !old.GetBool(ProtectedKey, false) || old.GetPrefab() != placement.Prefab.GetStableHashCode()
                        || (old.GetPosition() - position).sqrMagnitude > .0016f) continue;
                    // A support is ours only when marked. Ordinary floors,
                    // property containers and surrounding scenery are excluded.
                    if (placement.Marker == ArenaStairSupportKey && !old.GetBool(ArenaStairSupportKey, false)) continue;
                    if (placement.Marker == ArenaStairKey && !old.GetBool(ArenaStairKey, false)
                        && Quaternion.Angle(old.GetRotation(), facing) >= .1f
                        && Quaternion.Angle(old.GetRotation(), facing * Quaternion.Euler(0f, 180f, 0f)) >= .1f)
                        throw new InvalidOperationException("Неожиданное направление сохранённой лестницы. Предметы и постройка не изменены.");
                    matches.Add(old);
                    if (correct == null && old.GetBool(placement.Marker, false) && Quaternion.Angle(old.GetRotation(), facing) < .1f)
                        correct = old;
                }
                if (correct == null) plan.Create.Add(placement);
                else plan.Keep.Add(correct);
                foreach (ZDO old in matches)
                    if (old != correct && remove.Add(old.m_uid)) plan.Remove.Add(old);
            }
            return plan;
        }

        /// <summary>Replace the two saved expanded stair flights once, preserving all storage and terrain.</summary>
        public static bool EnsureArenaStairs(PrisonRegion region)
        {
            RequireHost();
            if (region == null || !ArenaGeometry.Expanded(region)) return true;
            if (ZRoutedRpc.instance == null || !ZNetScene.instance.IsAreaReady(Vector(region.Center))) return false;
            GameObject stair = RequirePrefab("stone_stair"), stone = RequirePrefab("stone_floor_2x2");
            if (stair.GetComponent<Piece>() == null || stone.GetComponent<Piece>() == null)
                throw new InvalidOperationException("Не найдены каменная лестница и её основание.");
            var bounds = new Dictionary<string, Bounds> { { "stone_stair", SolidBounds(stair) }, { "stone_floor_2x2", SolidBounds(stone) } };
            List<Placement> desired = CreateArenaStairPlan(bounds);
            Vector3 right = CellRight(region), forward = Vector3.Cross(right, Vector3.up);
            Vector3 basePoint = Vector(region.Center); basePoint.y = (float)region.CellSpawn.Y - 1f;
            Quaternion rotation = Quaternion.LookRotation(forward);
            var pieces = new List<ZDO>();
            foreach (ZDO piece in TaggedRegionObjects(ProtectedKey, region))
                if (InsideStructure(region, piece.GetPosition())) pieces.Add(piece);
            if (pieces.Count == 0) return false;
            ArenaStairRetrofitPlan retrofit = PlanArenaStairRetrofit(pieces, desired, basePoint, rotation);
            if (pieces.Count + retrofit.Create.Count > MaximumPieces)
                throw new InvalidOperationException("Превышен лимит деталей при обновлении лестниц тюрьмы.");
            foreach (Placement placement in desired)
                if (!ZNetScene.instance.IsAreaReady(basePoint + rotation * placement.Offset)) return false;
            foreach (ZDO piece in retrofit.Remove) {
                ZNetView view = ZNetScene.instance.FindInstance(piece);
                if (view == null || !view.IsValid()) return false;
            }
            foreach (ZDO piece in retrofit.Keep) {
                ZNetView view = ZNetScene.instance.FindInstance(piece);
                if (view == null || !view.IsValid()) return false;
            }
            if (retrofit.Create.Count == 0 && retrofit.Remove.Count == 0) return true;
            var created = new List<GameObject>();
            try {
                // The correct new network objects reach already loaded clients
                // with their rotation; static old pieces need no polling patch.
                foreach (Placement placement in retrofit.Create)
                    created.Add(CreatePiece(placement.Prefab == "stone_stair" ? stair : stone, placement, basePoint, rotation));
            }
            catch {
                for (int i = created.Count - 1; i >= 0; --i) DestroyCreated(created[i]);
                SiteClearer.FlushPendingDestruction();
                throw;
            }
            try {
                foreach (ZDO piece in retrofit.Remove) DestroyWorldObject(piece);
            }
            finally {
                // Native DestroyZDO queues deletion. Flush its table/network
                // update before the ordinary autosave; a retry remains idempotent.
                SiteClearer.FlushPendingDestruction();
                InvalidateLayoutCache();
            }
            return true;
        }

        public static bool IsCurrentFootprint(PrisonRegion region) { return ArenaGeometry.Expanded(region); }

        /// <summary>Cell fixtures are retrofitted without altering saved terrain, geometry or storage.</summary>
        public static bool EnsureConsole(PrisonRegion region)
        {
            RequireHost();
            if (region == null || !ZNetScene.instance.IsAreaReady(Vector(region.CellSpawn))) return false;
            bool hasConsole = false, hasFire = false;
            foreach (ZDO old in TaggedRegionObjects(ProtectedKey, region)) {
                if (!InsideStructure(region, old.GetPosition())) continue;
                if (old.GetPrefab() == PrisonConsole.PrefabName.GetStableHashCode() && old.GetBool(PrisonConsole.ConsoleKey, false)) hasConsole = true;
                if (old.GetPrefab() == PrisonContent.PrisonCampfirePrefab.GetStableHashCode() && old.GetBool(PrisonContent.PrisonCampfireMarker, false)) hasFire = true;
            }
            if (hasConsole && hasFire) return true;
            Vector3 right = CellRight(region), forward = Vector3.Cross(right, Vector3.up);
            Vector3 basePoint = Vector(region.Center); basePoint.y = (float)region.CellSpawn.Y - 1f;
            Quaternion rotation = Quaternion.LookRotation(forward);
            var created = new List<GameObject>(2);
            try {
                if (!hasConsole) {
                    GameObject prefab = RequirePrefab(PrisonConsole.PrefabName); Bounds bounds = SolidBounds(prefab);
                    Vector3 local = ArenaGeometry.Expanded(region) ? new Vector3(-14f, -bounds.min.y, -6f) : new Vector3(-6f, -bounds.min.y, 7f);
                    created.Add(CreatePiece(prefab, new Placement(PrisonConsole.PrefabName, local, 0f, PrisonConsole.ConsoleKey, -1), basePoint, rotation));
                }
                if (!hasFire) {
                    GameObject prefab = RequirePrefab(PrisonContent.PrisonCampfirePrefab); Bounds bounds = SolidBounds(prefab);
                    Vector3 local = ArenaGeometry.Expanded(region) ? new Vector3(-14f, -bounds.min.y, 13f) : new Vector3(-7f, -bounds.min.y, 10f);
                    created.Add(CreatePiece(prefab, new Placement(PrisonContent.PrisonCampfirePrefab, local, 0f, PrisonContent.PrisonCampfireMarker, -1), basePoint, rotation));
                }
            }
            catch {
                for (int i = created.Count - 1; i >= 0; --i) DestroyCreated(created[i]);
                throw;
            }
            InvalidateLayoutCache(); return true;
        }

        /// <summary>Geometry is never silently enlarged under an occupied saved prison.</summary>
        private static bool UpgradeLoadedLayout(PrisonRegion region)
        {
            return EnsureConsole(region);
        }
    }
}
