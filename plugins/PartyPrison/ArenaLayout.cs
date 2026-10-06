using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ValheimModPack.PartyPrison
{
    public static partial class ArenaBuilder
    {
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
            for (int tier = 0; tier < 4; ++tier) {
                float y = tier * 2f - grille.min.y;
                // The cell occupies x=[-12,-4], z=[-4,12]. Its exterior west
                // and north walls now permit an unobstructed view through bars.
                for (int segment = 0; segment < 8; ++segment)
                    plan.Add(new Placement("iron_wall_2x2", new Vector3(-RoomHalfWidth, y, -3f + segment * 2f), 90f, CellWallKey, -1));
                for (int segment = 0; segment < 4; ++segment)
                    plan.Add(new Placement("iron_wall_2x2", new Vector3(-11f + segment * 2f, y, RoomHalfWidth), 0f, CellWallKey, -1));
                // The public foyer divider retains a two-metre release doorway.
                float[] positions = tier < 2 ? new float[] { -11f, -10f, -6f, -5f } : new float[] { -11f, -9f, -7f, -5f };
                foreach (float x in positions)
                    plan.Add(new Placement("iron_wall_2x2", new Vector3(x, y, -4f), 0f, CellWallKey, -1));
            }
            // A native gate can be shorter than its four-metre opening. Fill
            // exactly the space above the collider rather than closing the leaf.
            for (float height = gate.size.y; height < 4f; height += grille.size.y) {
                float y = height - grille.min.y;
                plan.Add(new Placement("iron_wall_2x2", new Vector3(-8f, y, -4f), 0f, CellWallKey, -1));
                plan.Add(new Placement("iron_wall_2x2", new Vector3(-4f, y, 0f), 90f, CellWallKey, -1));
            }
        }

        /// <summary>Upgrade either v2 or v3 once, without replacing native inventories.</summary>
        private static bool UpgradeLoadedLayout(PrisonRegion region)
        {
            RequireHost();
            if (region == null || ZoneSystem.instance == null || !ZNetScene.instance.IsAreaReady(Vector(region.Center))) return false;
            List<ZDO> protectedPieces = TaggedRegionObjects(ProtectedKey, region);
            int version = Int32.MaxValue;
            foreach (ZDO piece in protectedPieces)
                if (InsideStructure(region, piece.GetPosition())) version = Math.Min(version, piece.GetInt(LayoutVersionKey, 0));
            if (version < 2 || version >= CurrentLayoutVersion) return false;
            // Everything must be loaded before creating replacements or deleting
            // old walls. This also guarantees the contents of all chests survive.
            foreach (ZDO piece in protectedPieces) {
                if (!InsideStructure(region, piece.GetPosition())) continue;
                ZNetView view = ZNetScene.instance.FindInstance(piece);
                if (view == null || !view.IsValid()) return false;
            }
            var prefabs = new Dictionary<string, GameObject>();
            foreach (string name in new[] { "crystal_wall_1x1", "sign", CustodyPrefab, "piece_dvergr_lantern", "bed", "iron_wall_2x2", "iron_grate" })
                prefabs.Add(name, RequirePrefab(name));
            if (prefabs["sign"].GetComponent<Sign>() == null || prefabs[CustodyPrefab].GetComponent<Container>() == null
                || prefabs["bed"].GetComponent<Bed>() == null)
                throw new InvalidOperationException("Не найдены табличка, сундук или кровать для обновления тюрьмы.");
            Vector3 basePoint = Vector(region.Center) - Vector3.up * 4f;
            Vector3 right = CellRight(region), forward = Vector3.Cross(right, Vector3.up);
            Quaternion rotation = Quaternion.LookRotation(forward);
            var additions = new List<Placement>();
            AppendWindows(additions, SolidBounds(prefabs["crystal_wall_1x1"]));
            AppendCellFurniture(additions, SolidBounds(prefabs[CustodyPrefab]), SolidBounds(prefabs["bed"]));
            AppendLamps(additions);
            AppendCellGrilles(additions, SolidBounds(prefabs["iron_wall_2x2"]), SolidBounds(prefabs["iron_grate"]));
            TerrainLeveler.ClearGrass(region);
            var newlyCreated = new List<GameObject>();
            try {
                // Prefab+marker+position makes retries idempotent after partial
                // creation; custody and kit chests are never replaced or emptied.
                foreach (Placement placement in additions) {
                    Vector3 position = basePoint + rotation * placement.Offset;
                    bool exists = false;
                    foreach (ZDO old in protectedPieces)
                        if (old.GetPrefab() == placement.Prefab.GetStableHashCode() && old.GetBool(placement.Marker, false)
                            && (old.GetPosition() - position).sqrMagnitude < .01f) { exists = true; break; }
                    if (!exists) newlyCreated.Add(CreatePiece(prefabs[placement.Prefab], placement, basePoint, rotation));
                }
            }
            catch {
                for (int i = newlyCreated.Count - 1; i >= 0; --i) DestroyCreated(newlyCreated[i]);
                throw;
            }
            int wallPrefab = "stone_wall_4x2".GetStableHashCode(), lampPrefab = "piece_dvergr_lantern".GetStableHashCode();
            float bottomWallY = -SolidBounds(RequirePrefab("stone_wall_4x2")).min.y;
            foreach (ZDO piece in protectedPieces) {
                if (!InsideStructure(region, piece.GetPosition())) continue;
                Vector3 delta = piece.GetPosition() - basePoint;
                float x = Vector3.Dot(delta, right), z = Vector3.Dot(delta, forward);
                bool lowerViewingWall = piece.GetPrefab() == wallPrefab && Mathf.Abs(delta.y - bottomWallY) < .1f
                    && ((Mathf.Abs(z - RoomHalfWidth) < .1f && (Mathf.Abs(x - 2f) < .1f || Mathf.Abs(x - 6f) < .1f))
                        || (Mathf.Abs(x - RoomHalfWidth) < .1f && (Mathf.Abs(z - 2f) < .1f || Mathf.Abs(z - 6f) < .1f)));
                if (lowerViewingWall || IsOldCellStoneWall(piece.GetPrefab(), x, z)
                    || piece.GetPrefab() == lampPrefab && !piece.GetBool(LampKey, false)) {
                    DestroyWorldObject(piece); continue;
                }
                if (piece.GetBool(SignKey, false)) {
                    piece.Set(ZDOVars.s_text, CellSignText);
                    piece.Set(ZDOVars.s_author, "");
                    piece.Set(ZDOVars.s_authorDisplayName, "");
                }
                // Never take ownership of a public chest just to update layout
                // metadata: a player may already have its ordinary UI open.
                piece.Set(LayoutVersionKey, CurrentLayoutVersion);
            }
            foreach (ZDO item in TaggedRegionObjects(ArmoryKey, region))
                if (InsideStructure(region, item.GetPosition())) DestroyWorldObject(item);
            InvalidateLayoutCache();
            return true;
        }

        private static bool IsOldCellStoneWall(int prefab, float x, float z)
        {
            if (prefab != "stone_wall_4x2".GetStableHashCode() && prefab != "stone_wall_2x1".GetStableHashCode()) return false;
            bool cellWest = Mathf.Abs(x + RoomHalfWidth) < .1f && z >= -4.1f && z <= 12.1f;
            bool cellNorth = Mathf.Abs(z - RoomHalfWidth) < .1f && x >= -12.1f && x <= -4.1f;
            bool cellSouth = Mathf.Abs(z + 4f) < .1f && x >= -12.1f && x <= -4.1f;
            bool innerHeader = Mathf.Abs(x + 4f) < .1f && Mathf.Abs(z) < .1f;
            return cellWest || cellNorth || cellSouth || innerHeader;
        }
    }
}
