using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ValheimModPack.PartyPrison
{
    /// <summary>Host-built native pieces; manual construction can prepare a bounded, empty terrain site.</summary>
    public static class ArenaBuilder
    {
        public const string ProtectedKey = "VMP_PP_Protected";
        public const string MobKey = "VMP_PP_Mob";
        public const string ArmoryKey = "VMP_PP_Armory";
        public const string LoanKey = "VMP_PP_Loan";
        public const string ArmoryPrefabKey = "VMP_PP_ArmoryPrefab";
        public const string ExitKey = "VMP_PP_Exit";
        public const string InnerGateKey = "VMP_PP_InnerGate";
        public const string CustodyKey = "VMP_PP_Custody";
        public const string CustodyIndexKey = "VMP_PP_CustodyIndex";
        public const string CustodyPrefab = "piece_chest";
        public const string LayoutVersionKey = "VMP_PP_LayoutVersion";
        public const int CurrentLayoutVersion = 2;
        private const int MaximumPieces = 640;
        private const int MaximumMobs = 8;
        private const float RoomHalfWidth = 12f;
        private const float RoomHeight = 8f;
        private static readonly FieldInfo WorldObjectsField = typeof(ZDOMan).GetField("m_objectsByID", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly string[] MobNames = {
            "Neck", "Greydwarf", "Greydwarf_Elite", "Greydwarf_Shaman",
            "Skeleton", "Draugr", "Draugr_Elite", "Goblin"
        };
        private static readonly string[] ArmoryNames = {
            "ArmorLeatherChest", "ArmorLeatherLegs", "HelmetLeather", "SwordBronze",
            "MaceBronze", "AxeBronze", "SpearBronze", "BowFineWood", "ShieldWood", "ArrowWood"
        };

        public static string AllowedMobs { get { return String.Join(", ", MobNames); } }

        /// <summary>Choose a clear, loaded site beside the world's boss-trophy altar.</summary>
        public static PrisonRegion BuildNearAltars()
        { return BuildNearAltars(false, null); }

        /// <summary>Keep terrain preparation and the durable region write in one rollback scope.</summary>
        public static PrisonRegion BuildNearAltars(bool levelGround, Action<PrisonRegion> saveRegion)
        {
            RequireHost();
            if (ZoneSystem.instance == null || Player.m_localPlayer == null)
                throw new InvalidOperationException("Сначала войдите в игровой мир за хоста.");
            ZoneSystem.LocationInstance temple;
            if (!ZoneSystem.instance.FindClosestLocation("StartTemple", Player.m_localPlayer.transform.position, out temple))
                throw new InvalidOperationException("В этом мире не найдены жертвенные камни с головами боссов.");
            Vector3 altar = temple.m_position;
            Vector3 distance = Player.m_localPlayer.transform.position - altar; distance.y = 0f;
            if (distance.sqrMagnitude > 140f * 140f)
                throw new InvalidOperationException("Для первой генерации подойдите к жертвенным камням с головами боссов. Тюрьма появится рядом с ними.");
            Vector3 best = Vector3.zero;
            Quaternion bestRotation = Quaternion.identity;
            TerrainLeveler.Site bestTerrain = null;
            float bestScore = Single.MaxValue;
            string failure = "";
            // Search only loaded terrain, so native buildings, rocks, trees and the
            // altar itself are all included in collision validation. Never clear them.
            for (int ring = 0; ring < 5; ++ring)
                for (int direction = 0; direction < 24; ++direction) {
                    float angle = direction * Mathf.PI * 2f / 24f;
                    Vector3 candidate = altar + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * (40f + ring * 12f);
                    float height;
                    if (!ZoneSystem.instance.GetGroundHeight(candidate, out height)) continue;
                    candidate.y = height;
                    Quaternion rotation = levelGround ? Quaternion.identity
                        : Quaternion.LookRotation(new Vector3(-Mathf.Sin(angle), 0f, Mathf.Cos(angle)));
                    try {
                        TerrainLeveler.Site terrain = levelGround ? TerrainLeveler.Plan(candidate, altar) : null;
                        float floor;
                        if (terrain == null) floor = PreflightSite(candidate, rotation);
                        else {
                            // Include both the existing terrain and the whole future room.
                            // Do not bury objects when raising the floor or uncover them when lowering it.
                            float low = terrain.TargetHeight - (float)TerrainPlan.MaximumGroundChange - 1f;
                            float high = terrain.TargetHeight + (float)TerrainPlan.MaximumGroundChange + RoomHeight + 1f;
                            RequireClearSite(candidate, rotation, (float)(TerrainPlan.HalfWidth + TerrainPlan.FootprintPadding), low, high, false);
                            floor = terrain.FloorHeight;
                        }
                        float score = ring * 20f + Mathf.Abs(floor - height);
                        if (score < bestScore) {
                            bestScore = score; best = candidate; bestRotation = rotation; bestTerrain = terrain;
                        }
                    }
                    catch (InvalidOperationException error) { failure = error.Message; }
                }
            if (bestScore == Single.MaxValue)
                throw new InvalidOperationException("Рядом с алтарями нет подходящего свободного сухого места под тюрьму. Подойдите к камням и освободите площадку в пределах 40–88 м. " + failure);
            using (TerrainLeveler.Transaction terrain = bestTerrain == null ? null : bestTerrain.Apply()) {
                if (bestTerrain != null) best.y = bestTerrain.TargetHeight;
                PrisonRegion created = Build(best, bestRotation);
                try { if (saveRegion != null) saveRegion(created); }
                catch { TryRollback(created); throw; }
                if (terrain != null) terrain.Commit();
                return created;
            }
        }

        public static PrisonRegion Build(Vector3 origin, Quaternion facing)
        {
            RequireHost();
            if (!Finite(origin)) throw new ArgumentException("Неверные координаты тюрьмы.");
            if (ZoneSystem.instance == null) throw new InvalidOperationException("Мир ещё не загружен.");
            // Pitch and roll are never applied to building pieces.
            Quaternion rotation = Quaternion.Euler(0f, facing.eulerAngles.y, 0f);
            Dictionary<string, GameObject> prefabs = new Dictionary<string, GameObject>();
            string[] pieceNames = { "stone_floor_2x2", "stone_wall_4x2", "stone_wall_2x1", "iron_wall_2x2", "iron_grate", "piece_bench01", CustodyPrefab, "piece_dvergr_lantern" };
            foreach (string name in pieceNames) {
                GameObject prefab = RequirePrefab(name);
                if (prefab.GetComponent<Piece>() == null || prefab.GetComponent<WearNTear>() == null)
                    throw new InvalidOperationException("Неподдерживаемая строительная деталь: " + name);
                prefabs.Add(name, prefab);
            }
            foreach (string name in ArmoryNames) RequireItemPrefab(name);
            if (prefabs["iron_grate"].GetComponent<Door>() == null || prefabs[CustodyPrefab].GetComponent<Container>() == null)
                throw new InvalidOperationException("В этой версии игры не поддерживаются тюремная решётка или железный сундук.");
            Dictionary<string, Bounds> bounds = new Dictionary<string, Bounds>();
            foreach (string name in pieceNames) bounds.Add(name, SolidBounds(prefabs[name]));
            float floor = PreflightSite(origin, rotation);
            Vector3 basePoint = new Vector3(origin.x, floor, origin.z);
            PrisonRegion region = new PrisonRegion {
                Center = Point(basePoint + Vector3.up * 4f), Radius = 18d, HalfHeight = 8d,
                CellSpawn = Point(basePoint + rotation * new Vector3(-8f, 1f, 0f)),
                ArenaSpawn = Point(basePoint + rotation * new Vector3(4f, 1f, 4f))
            };
            List<Placement> plan = new List<Placement>();
            // Floors and ceilings are a continuous grid with native collider edges.
            for (int x = 0; x < 12; ++x)
                for (int z = 0; z < 12; ++z) {
                    float px = -11f + x * 2f, pz = -11f + z * 2f;
                    plan.Add(new Placement("stone_floor_2x2", new Vector3(px, -bounds["stone_floor_2x2"].max.y, pz), 0f));
                    plan.Add(new Placement("stone_floor_2x2", new Vector3(px, RoomHeight - bounds["stone_floor_2x2"].min.y, pz), 0f));
                }
            for (int tier = 0; tier < 4; ++tier)
                for (int segment = 0; segment < 6; ++segment) {
                    float along = -10f + segment * 4f;
                    float y = tier * 2f - bounds["stone_wall_4x2"].min.y;
                    plan.Add(new Placement("stone_wall_4x2", new Vector3(along, y, -RoomHalfWidth), 0f));
                    plan.Add(new Placement("stone_wall_4x2", new Vector3(along, y, RoomHalfWidth), 0f));
                    // The public custody lobby has a permanent two-metre doorway.
                    if (tier >= 2 || (segment != 0 && segment != 1))
                        plan.Add(new Placement("stone_wall_4x2", new Vector3(-RoomHalfWidth, y, along), 90f));
                    plan.Add(new Placement("stone_wall_4x2", new Vector3(RoomHalfWidth, y, along), 90f));
                }
            for (int tier = 0; tier < 4; ++tier)
                foreach (float z in new float[] { -11f, -10f, -6f, -5f })
                    plan.Add(new Placement("stone_wall_2x1", new Vector3(-RoomHalfWidth, tier - bounds["stone_wall_2x1"].min.y, z), 90f));
            // A separate lobby holds exactly four personal-property chests. Its
            // northern wall has the release gate at x=-8; the arena has no lobby exit.
            for (int tier = 0; tier < 4; ++tier) {
                float y = tier * 2f - bounds["stone_wall_4x2"].min.y;
                foreach (float x in new float[] { -2f, 2f, 6f, 10f })
                    plan.Add(new Placement("stone_wall_4x2", new Vector3(x, y, -4f), 0f));
                if (tier >= 2) {
                    plan.Add(new Placement("stone_wall_4x2", new Vector3(-10f, y, -4f), 0f));
                    plan.Add(new Placement("stone_wall_4x2", new Vector3(-6f, y, -4f), 0f));
                }
                else for (int half = 0; half < 2; ++half)
                    foreach (float x in new float[] { -11f, -10f, -6f, -5f })
                        plan.Add(new Placement("stone_wall_2x1", new Vector3(x, tier * 2f + half - bounds["stone_wall_2x1"].min.y, -4f), 0f));
            }
            // An internal grille lets the prisoner physically walk from the bench
            // cell to the adjoining arena. A second grille opens only on release.
            for (int tier = 0; tier < 4; ++tier)
                foreach (float z in new float[] { -3f, -2f, 2f, 4f, 6f, 8f, 10f, 11f })
                    plan.Add(new Placement("iron_wall_2x2", new Vector3(-4f, tier * 2f - bounds["iron_wall_2x2"].min.y, z), 90f));
            for (int tier = 2; tier < 4; ++tier)
                plan.Add(new Placement("iron_wall_2x2", new Vector3(-4f, tier * 2f - bounds["iron_wall_2x2"].min.y, 0f), 90f));
            plan.Add(new Placement("iron_grate", new Vector3(-8f, -bounds["iron_grate"].min.y, -4f), 0f, ExitKey, -1));
            plan.Add(new Placement("iron_grate", new Vector3(-4f, -bounds["iron_grate"].min.y, 0f), 90f, InnerGateKey, -1));
            // Close any space above the native two-metre gate leaf up to the
            // four-metre headers; do not rely on its animation collider height.
            float gateHeight = bounds["iron_grate"].size.y;
            for (float height = gateHeight; height < 4f; height += 1f) {
                plan.Add(new Placement("stone_wall_2x1", new Vector3(-8f, height - bounds["stone_wall_2x1"].min.y, -4f), 0f));
                plan.Add(new Placement("stone_wall_2x1", new Vector3(-4f, height - bounds["stone_wall_2x1"].min.y, 0f), 90f));
            }
            plan.Add(new Placement("piece_bench01", new Vector3(-10f, -bounds["piece_bench01"].min.y, 7f), 90f));
            for (int chest = 0; chest < 4; ++chest)
                plan.Add(new Placement(CustodyPrefab, new Vector3(-6f + chest * 4f, -bounds[CustodyPrefab].min.y, -10f), 0f, CustodyKey, chest));
            // Native Dvergr lamps have no fire, smoke or consumable fuel.
            plan.Add(new Placement("piece_dvergr_lantern", new Vector3(-11.4f, 3f, 0f), 90f));
            plan.Add(new Placement("piece_dvergr_lantern", new Vector3(-4.6f, 3f, 0f), 90f));
            plan.Add(new Placement("piece_dvergr_lantern", new Vector3(3f, 3f, -11.4f), 0f));
            plan.Add(new Placement("piece_dvergr_lantern", new Vector3(3f, 3f, 11.4f), 180f));
            plan.Add(new Placement("piece_dvergr_lantern", new Vector3(11.4f, 3f, -6f), 270f));
            plan.Add(new Placement("piece_dvergr_lantern", new Vector3(11.4f, 3f, 6f), 270f));
            if (plan.Count > MaximumPieces) throw new InvalidOperationException("Превышен лимит деталей тюрьмы.");
            List<GameObject> created = new List<GameObject>();
            try {
                foreach (Placement placement in plan) {
                    GameObject piece = UnityEngine.Object.Instantiate(prefabs[placement.Prefab],
                        basePoint + rotation * placement.Offset, rotation * Quaternion.Euler(0f, placement.Yaw, 0f));
                    created.Add(piece);
                    ZDO zdo = MarkPersistent(piece, ProtectedKey);
                    zdo.Set(LayoutVersionKey, CurrentLayoutVersion);
                    if (placement.Marker != null) zdo.Set(placement.Marker, true);
                    if (placement.CustodyIndex >= 0) zdo.Set(CustodyIndexKey, placement.CustodyIndex);
                    if (placement.Marker == ExitKey) zdo.Set(ZDOVars.s_state, 1);
                    Piece nativePiece = piece.GetComponent<Piece>();
                    if (nativePiece != null) nativePiece.m_canBeRemoved = false;
                    WearNTear wear = piece.GetComponent<WearNTear>();
                    if (wear != null) { wear.m_noRoofWear = true; wear.m_noSupportWear = true; }
                }
                // Each item is recorded in the same rollback list as the enclosure.
                SpawnMissingArmory(region, created);
                return region;
            }
            catch {
                for (int i = created.Count - 1; i >= 0; --i) DestroyCreated(created[i]);
                throw;
            }
        }

        public static bool IsProtected(GameObject gameObject) { return HasMarker(gameObject, ProtectedKey); }
        public static bool IsMob(GameObject gameObject) { return HasMarker(gameObject, MobKey); }
        public static bool IsExitGate(GameObject gameObject) { return HasMarker(gameObject, ExitKey); }
        public static bool IsInnerGate(GameObject gameObject) { return HasMarker(gameObject, InnerGateKey); }
        public static bool IsCustody(GameObject gameObject) { return HasMarker(gameObject, CustodyKey); }

        public static int LayoutVersion(PrisonRegion region)
        {
            int version = Int32.MaxValue;
            foreach (ZDO zdo in TaggedWorldObjects(ProtectedKey))
                if (InsideStructure(region, zdo.GetPosition())) version = Math.Min(version, zdo.GetInt(LayoutVersionKey, 0));
            return version == Int32.MaxValue ? 0 : version;
        }

        /// <summary>Only an idle layout replacement; never destroys unmarked native world objects.</summary>
        public static int RemoveStructure(PrisonRegion region)
        {
            RequireHost();
            if (region == null) return 0;
            List<ZDO> objects = TaggedWorldObjects(ProtectedKey);
            objects.AddRange(TaggedWorldObjects(ArmoryKey));
            objects.AddRange(TaggedWorldObjects(MobKey));
            HashSet<ZDOID> removed = new HashSet<ZDOID>();
            foreach (ZDO zdo in objects) {
                if (!InsideStructure(region, zdo.GetPosition()) || !removed.Add(zdo.m_uid)) continue;
                zdo.SetOwner(ZNet.GetUID());
                ZDOMan.instance.DestroyZDO(zdo);
            }
            return removed.Count;
        }

        public static List<ZDO> GetCustodyZdos(PrisonRegion region)
        {
            List<ZDO> result = new List<ZDO>();
            foreach (ZDO zdo in TaggedWorldObjects(CustodyKey))
                if (InsideStructure(region, zdo.GetPosition())) result.Add(zdo);
            result.Sort(delegate(ZDO left, ZDO right) { return left.GetInt(CustodyIndexKey, -1).CompareTo(right.GetInt(CustodyIndexKey, -1)); });
            return result;
        }

        public static void ChestDimensions(out int width, out int height)
        {
            Container chest = RequirePrefab(CustodyPrefab).GetComponent<Container>();
            if (chest == null || chest.m_width < 1 || chest.m_height < 1)
                throw new InvalidOperationException("Не удалось определить вместимость железного сундука.");
            width = chest.m_width; height = chest.m_height;
        }

        /// <summary>Server state persists and native Door.UpdateState animates on every peer.</summary>
        public static void SetExitLocked(PrisonRegion region, bool locked)
        {
            RequireHost();
            foreach (ZDO zdo in TaggedWorldObjects(ExitKey)) {
                if (!InsideStructure(region, zdo.GetPosition())) continue;
                zdo.SetOwner(ZNet.GetUID());
                zdo.Set(ZDOVars.s_state, locked ? 0 : 1);
            }
        }
        public static bool ContainsRoom(PrisonRegion region, Vector3 position)
        {
            if (region == null || !Finite(position) || !region.Contains(Point(position))) return false;
            Vector3 right = CellRight(region), forward = Vector3.Cross(right, Vector3.up);
            Vector3 delta = position - Vector(region.Center);
            float x = Vector3.Dot(delta, right), z = Vector3.Dot(delta, forward);
            float floor = (float)region.CellSpawn.Y - 1f;
            return Mathf.Abs(x) <= 11.7f && Mathf.Abs(z) <= 11.7f
                && position.y >= floor - 0.25f && position.y <= floor + 7.5f;
        }

        /// <summary>The public property lobby is outside the sentence boundary.</summary>
        public static bool ContainsConfinement(PrisonRegion region, Vector3 position)
        {
            if (!ContainsRoom(region, position)) return false;
            Vector3 right = CellRight(region), forward = Vector3.Cross(right, Vector3.up);
            return Vector3.Dot(position - Vector(region.Center), forward) >= -3.7f;
        }

        public static bool IsInsideArena(PrisonRegion region, Vector3 position) { return InsideArena(region, position); }

        /// <summary>The whole bench cell is safe, including its northern seating area.</summary>
        public static bool IsInsideCell(PrisonRegion region, Vector3 position)
        {
            if (!ContainsConfinement(region, position)) return false;
            return Vector3.Dot(position - Vector(region.Center), CellRight(region)) <= -4.3f;
        }
        public static bool IsArmory(GameObject gameObject)
        {
            if (HasMarker(gameObject, ArmoryKey)) return true;
            // Native spear projectiles can recreate the item with its ItemData but
            // without our world-object ZDO tag after hitting a target.
            ItemDrop drop = gameObject == null ? null : gameObject.GetComponent<ItemDrop>();
            string loan;
            return drop != null && drop.m_itemData != null && drop.m_itemData.m_customData != null
                && drop.m_itemData.m_customData.TryGetValue(LoanKey, out loan) && loan == "1";
        }

        /// <summary>Call only while the host is near the prison; unloaded ZDOs must not be duplicated.</summary>
        public static int EnsureArmory(PrisonRegion region)
        {
            RequireHost();
            if (region == null || Player.m_localPlayer == null) return 0;
            Vector3 center = Vector(region.Center);
            if ((Player.m_localPlayer.transform.position - center).sqrMagnitude > 60f * 60f) return 0;
            float ground;
            if (ZoneSystem.instance == null || !ZoneSystem.instance.GetGroundHeight(center, out ground)) return 0;
            List<GameObject> created = new List<GameObject>();
            try { SpawnMissingArmory(region, created); return created.Count; }
            catch {
                foreach (GameObject item in created) DestroyCreated(item);
                throw;
            }
        }

        public static int SpawnWave(PrisonRegion region, string prefabName, int count, int level)
        {
            RequireHost();
            if (region == null) throw new InvalidOperationException("Сначала создайте тюрьму.");
            string canonical = null;
            foreach (string allowed in MobNames)
                if (String.Equals(allowed, prefabName, StringComparison.OrdinalIgnoreCase)) { canonical = allowed; break; }
            if (canonical == null) throw new ArgumentException("Допустимые мобы: " + AllowedMobs);
            if (count < 1 || count > MaximumMobs || level < 1 || level > 3)
                throw new ArgumentException("За волну: 1–8 мобов; уровень: 1–3.");
            int existing = LiveMobCount(null);
            if (existing + count > MaximumMobs)
                throw new InvalidOperationException("В тюрьме уже есть мобы; общий лимит — 8. Сначала завершите или очистите волну.");
            GameObject prefab = RequirePrefab(canonical);
            if (prefab.GetComponent<Character>() == null || prefab.GetComponent<MonsterAI>() == null)
                throw new InvalidOperationException("Неподдерживаемый моб: " + canonical);
            Vector3 spawn = Vector(region.ArenaSpawn);
            // The persisted room supplies its exact floor. The host need not load its
            // terrain: native ZDO ownership/instantiation transfers to the nearby inmate.
            Vector3 right = CellRight(region), forward = Vector3.Cross(right, Vector3.up);
            List<GameObject> created = new List<GameObject>();
            try {
                for (int index = 0; index < count; ++index) {
                    float angle = index * Mathf.PI * 2f / count;
                    Vector3 position = spawn + (right * Mathf.Cos(angle) + forward * Mathf.Sin(angle)) * 4f;
                    GameObject mob = UnityEngine.Object.Instantiate(prefab, position, Quaternion.LookRotation(-right));
                    created.Add(mob);
                    MarkPersistent(mob, MobKey);
                    Character character = mob.GetComponent<Character>();
                    character.SetLevel(level);
                    // Native mob drops remain enabled. The prisoner keeps all
                    // collected materials when the sentence ends.
                }
                return created.Count;
            }
            catch {
                foreach (GameObject mob in created) DestroyCreated(mob);
                throw;
            }
        }

        public static int LiveMobCount(PrisonRegion region)
        {
            int count = 0;
            foreach (ZDO zdo in TaggedWorldObjects(MobKey))
                if (!zdo.GetBool(ZDOVars.s_dead, false) && zdo.GetFloat(ZDOVars.s_health, 1f) > 0f
                    && (region == null || ContainsRoom(region, zdo.GetPosition()))) ++count;
            return count;
        }

        public static int CleanupMobs() { return CleanupMobs(null, true); }
        public static int CleanupMobs(PrisonRegion region) { return CleanupMobs(region, true); }

        public static int CleanupMobs(PrisonRegion region, bool all)
        {
            RequireHost();
            int removed = 0;
            foreach (ZDO zdo in TaggedWorldObjects(MobKey)) {
                if (!all && (region == null || InsideArena(region, zdo.GetPosition()))) continue;
                // DestroyZDO requires ownership. This host-only path also removes
                // unloaded mobs and announces destruction to their remote owners.
                zdo.SetOwner(ZNet.GetUID());
                ZDOMan.instance.DestroyZDO(zdo); ++removed;
            }
            return removed;
        }

        /// <summary>All peers confine the marked mobs they own, including ones whose ownership moved off the host.</summary>
        public static void EnforceOwnedMobs(PrisonRegion region)
        {
            if (region == null || ZNetScene.instance == null) return;
            foreach (Character character in Character.GetAllCharacters()) {
                if (character == null || character.IsDead() || !IsMob(character.gameObject)) continue;
                ZNetView view = character.GetComponent<ZNetView>();
                if (view == null || !view.IsOwner() || InsideArena(region, character.transform.position)) continue;
                Vector3 position = Vector(region.ArenaSpawn);
                character.transform.position = position;
                Rigidbody body = character.GetComponent<Rigidbody>();
                if (body != null) { body.position = position; body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
            }
        }

        /// <summary>Only for an unsuccessful initial build transaction, before publishing the region.</summary>
        public static int Rollback(PrisonRegion region)
        {
            RequireHost();
            if (region == null) return 0;
            int removed = 0;
            foreach (ZNetView view in UnityEngine.Object.FindObjectsByType<ZNetView>(FindObjectsSortMode.None)) {
                if (view == null || !view.IsValid() || !InsideStructure(region, view.transform.position)) continue;
                ZDO zdo = view.GetZDO();
                if (!zdo.GetBool(ProtectedKey, false) && !zdo.GetBool(ArmoryKey, false) && !zdo.GetBool(MobKey, false)) continue;
                DestroyCreated(view.gameObject); ++removed;
            }
            return removed;
        }

        public static int TryRollback(PrisonRegion region) { return Rollback(region); }

        private static void SpawnMissingArmory(PrisonRegion region, List<GameObject> created)
        {
            HashSet<string> present = new HashSet<string>(StringComparer.Ordinal);
            foreach (ZDO zdo in TaggedWorldObjects(ArmoryKey))
                if (InsideStructure(region, zdo.GetPosition())) present.Add(zdo.GetString(ArmoryPrefabKey, ""));
            foreach (ItemDrop item in UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None)) {
                if (item == null || !IsArmory(item.gameObject)) continue;
                ZNetView view = item.GetComponent<ZNetView>();
                string name = view == null || !view.IsValid() ? "" : view.GetZDO().GetString(ArmoryPrefabKey, "");
                if (name.Length == 0 && item.m_itemData != null && item.m_itemData.m_customData != null) {
                    string itemName;
                    if (item.m_itemData.m_customData.TryGetValue(ArmoryPrefabKey, out itemName)) name = itemName;
                }
                present.Add(name);
            }
            // A held loan weapon also counts as stock. This avoids filling a prisoner's
            // inventory with duplicate weapons when the host replenishes the display.
            foreach (Player player in Player.GetAllPlayers()) {
                if (player == null || player.GetInventory() == null) continue;
                foreach (ItemDrop.ItemData item in player.GetInventory().GetAllItems()) {
                    string loan, prefab;
                    if (item.m_customData != null && item.m_customData.TryGetValue(LoanKey, out loan) && loan == "1"
                        && item.m_customData.TryGetValue(ArmoryPrefabKey, out prefab)) present.Add(prefab);
                }
            }
            Vector3 cell = Vector(region.CellSpawn), right = CellRight(region), forward = Vector3.Cross(right, Vector3.up);
            for (int index = 0; index < ArmoryNames.Length; ++index) {
                string name = ArmoryNames[index];
                if (present.Contains(name)) continue;
                GameObject prefab = RequireItemPrefab(name);
                float along = -2f + (index % 5) * 1.4f;
                float across = index < 5 ? -2f : -0.7f;
                Vector3 position = cell + right * across + forward * along + Vector3.up * 0.1f;
                GameObject gameObject = UnityEngine.Object.Instantiate(prefab, position, Quaternion.identity);
                created.Add(gameObject);
                ZDO zdo = MarkPersistent(gameObject, ArmoryKey);
                zdo.Set(ArmoryPrefabKey, name);
                ItemDrop drop = gameObject.GetComponent<ItemDrop>();
                if (drop == null || drop.m_itemData == null) throw new InvalidOperationException("Не создан предмет арсенала: " + name);
                if (drop.m_itemData.m_customData == null) drop.m_itemData.m_customData = new Dictionary<string, string>();
                drop.m_itemData.m_customData[LoanKey] = "1";
                drop.m_itemData.m_customData[ArmoryPrefabKey] = name;
                if (name == "ArrowWood") drop.m_itemData.m_stack = 100;
                ItemDrop.SaveToZDO(drop.m_itemData, zdo);
            }
        }

        private static float PreflightSite(Vector3 origin, Quaternion rotation)
        {
            float low = Single.MaxValue, high = Single.MinValue;
            for (int x = -12; x <= 12; x += 4)
                for (int z = -12; z <= 12; z += 4) {
                    Vector3 point = origin + rotation * new Vector3(x, 0f, z);
                    float height;
                    if (!ZoneSystem.instance.IsZoneLoaded(point) || !ZoneSystem.instance.GetGroundHeight(point, out height))
                        throw new InvalidOperationException("Площадка не загружена. Выберите открытое место рядом с хостом.");
                    if (height <= ZoneSystem.instance.m_waterLevel + 0.5f)
                        throw new InvalidOperationException("Тюрьме нужна сухая площадка вдали от воды.");
                    low = Mathf.Min(low, height); high = Mathf.Max(high, height);
                }
            if (high - low > 2f || Mathf.Abs(origin.y - high) > 4f)
                throw new InvalidOperationException("Для фоновой генерации нужна ровная площадка 27 × 27 м. Команда /prison build сама подготовит землю.");
            RequireClearSite(origin, rotation, 13.5f, high - 1f, high + RoomHeight + 1f);
            return high + 0.15f;
        }

        private static void RequireClearSite(Vector3 origin, Quaternion rotation, float halfWidth, float low, float high, bool allowHost = true)
        {
            Vector3 center = new Vector3(origin.x, (low + high) * 0.5f, origin.z);
            foreach (Collider collider in Physics.OverlapBox(center, new Vector3(halfWidth, (high - low) * 0.5f, halfWidth), rotation, ~0, QueryTriggerInteraction.Ignore)) {
                if (collider == null || collider.GetComponentInParent<Heightmap>() != null) continue;
                Player nearby = collider.GetComponentInParent<Player>();
                if (allowHost && nearby != null && nearby == Player.m_localPlayer) continue;
                throw new InvalidOperationException("Площадка занята строением, камнем, деревом или существом. Очистите место или выберите другое.");
            }
        }

        private static Bounds SolidBounds(GameObject prefab)
        {
            bool found = false;
            Bounds bounds = new Bounds();
            foreach (Collider collider in prefab.GetComponentsInChildren<Collider>(true)) {
                if (collider.isTrigger) continue;
                BoxCollider box = collider as BoxCollider;
                MeshCollider mesh = collider as MeshCollider;
                Bounds local;
                if (box != null) local = new Bounds(box.center, box.size);
                else if (mesh != null && mesh.sharedMesh != null) local = mesh.sharedMesh.bounds;
                else continue;
                for (int x = -1; x <= 1; x += 2)
                    for (int y = -1; y <= 1; y += 2)
                        for (int z = -1; z <= 1; z += 2) {
                            Vector3 corner = local.center + Vector3.Scale(local.extents, new Vector3(x, y, z));
                            Vector3 point = prefab.transform.InverseTransformPoint(collider.transform.TransformPoint(corner));
                            if (!found) { bounds = new Bounds(point, Vector3.zero); found = true; }
                            else bounds.Encapsulate(point);
                        }
            }
            if (!found) throw new InvalidOperationException("Не найдены коллайдеры детали: " + prefab.name);
            return bounds;
        }

        private static bool HasMarker(GameObject gameObject, string key)
        {
            if (gameObject == null) return false;
            ZNetView view = gameObject.GetComponent<ZNetView>();
            if (view == null) view = gameObject.GetComponentInParent<ZNetView>();
            return view != null && view.IsValid() && view.GetZDO().GetBool(key, false);
        }

        private static List<ZDO> TaggedWorldObjects(string key)
        {
            List<ZDO> result = new List<ZDO>();
            if (ZDOMan.instance == null) return result;
            if (WorldObjectsField == null) throw new MissingFieldException("ZDOMan", "m_objectsByID");
            Dictionary<ZDOID, ZDO> objects = WorldObjectsField.GetValue(ZDOMan.instance) as Dictionary<ZDOID, ZDO>;
            if (objects == null) throw new InvalidOperationException("Изменился реестр сетевых объектов Valheim.");
            // This method runs on Unity's main thread. Copy matching references
            // before the caller performs ownership changes or queues destruction.
            int hash = key.GetStableHashCode();
            foreach (ZDO zdo in objects.Values)
                if (zdo != null && zdo.GetBool(hash, false)) result.Add(zdo);
            return result;
        }

        private static ZDO MarkPersistent(GameObject gameObject, string key)
        {
            ZNetView view = gameObject.GetComponent<ZNetView>();
            if (view == null || !view.IsValid()) throw new InvalidOperationException("Не создан сетевой объект: " + gameObject.name);
            ZDO zdo = view.GetZDO();
            zdo.Set(key, true); zdo.Persistent = true; view.m_persistent = true;
            return zdo;
        }

        private static void DestroyCreated(GameObject gameObject)
        {
            if (gameObject == null) return;
            ZNetView view = gameObject.GetComponent<ZNetView>();
            if (view != null && view.IsValid()) { view.ClaimOwnership(); ZNetScene.instance.Destroy(gameObject); }
            else UnityEngine.Object.Destroy(gameObject);
        }

        private static GameObject RequirePrefab(string name)
        {
            GameObject prefab = ZNetScene.instance.GetPrefab(name);
            if (prefab == null || prefab.GetComponent<ZNetView>() == null)
                throw new InvalidOperationException("В этой версии игры отсутствует сетевой объект: " + name);
            return prefab;
        }

        private static GameObject RequireItemPrefab(string name)
        {
            GameObject prefab = RequirePrefab(name);
            if (prefab.GetComponent<ItemDrop>() == null) throw new InvalidOperationException("Неподдерживаемое оружие: " + name);
            return prefab;
        }

        private static void RequireHost()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZNetScene.instance == null)
                throw new InvalidOperationException("Создавать тюрьму и управлять мобами может только хост.");
        }

        private static Vector3 CellRight(PrisonRegion region)
        {
            Vector3 delta = Vector(region.Center) - Vector(region.CellSpawn); delta.y = 0f;
            return delta.sqrMagnitude < 0.1f ? Vector3.right : delta.normalized;
        }
        private static bool InsideArena(PrisonRegion region, Vector3 position)
        {
            if (!ContainsRoom(region, position)) return false;
            Vector3 right = CellRight(region), forward = Vector3.Cross(right, Vector3.up);
            Vector3 delta = position - Vector(region.Center);
            float x = Vector3.Dot(delta, right), z = Vector3.Dot(delta, forward);
            return x >= -3.7f && z >= -3.7f;
        }
        private static bool InsideStructure(PrisonRegion region, Vector3 position)
        {
            if (region == null || !Finite(position)) return false;
            Vector3 right = CellRight(region), forward = Vector3.Cross(right, Vector3.up);
            Vector3 delta = position - Vector(region.Center);
            float floor = (float)region.CellSpawn.Y - 1f;
            // Exact footprint avoids deleting a neighbouring new layout whose
            // circumscribed region happens to intersect the old region's circle.
            return Mathf.Abs(Vector3.Dot(delta, right)) <= 12.5f && Mathf.Abs(Vector3.Dot(delta, forward)) <= 12.5f
                && position.y >= floor - 1f && position.y <= floor + 10f;
        }
        private static Vector3 Vector(PrisonPoint point) { return new Vector3((float)point.X, (float)point.Y, (float)point.Z); }
        private static PrisonPoint Point(Vector3 point) { return new PrisonPoint(point.x, point.y, point.z); }
        private static bool Finite(Vector3 point)
        {
            return !Single.IsNaN(point.x) && !Single.IsInfinity(point.x) && !Single.IsNaN(point.y)
                && !Single.IsInfinity(point.y) && !Single.IsNaN(point.z) && !Single.IsInfinity(point.z);
        }
        private sealed class Placement
        {
            public readonly string Prefab; public readonly Vector3 Offset; public readonly float Yaw;
            public readonly string Marker; public readonly int CustodyIndex;
            public Placement(string prefab, Vector3 offset, float yaw) : this(prefab, offset, yaw, null, -1) { }
            public Placement(string prefab, Vector3 offset, float yaw, string marker, int custodyIndex)
            { Prefab = prefab; Offset = offset; Yaw = yaw; Marker = marker; CustodyIndex = custodyIndex; }
        }
    }
}
