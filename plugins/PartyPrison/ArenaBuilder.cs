using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ValheimModPack.PartyPrison
{
    /// <summary>Host-built native pieces; manual construction prepares one bounded site.</summary>
    public static partial class ArenaBuilder
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
        public const string KitKey = "VMP_PP_Kit";
        public const string KitTokenKey = "VMP_PP_KitToken";
        public const string KitStockKey = "VMP_PP_KitStock";
        public const string SignKey = "VMP_PP_Sign";
        public const string LampKey = "VMP_PP_Lamp";
        public const string WindowKey = "VMP_PP_Window";
        public const string BedKey = "VMP_PP_Bed";
        public const string CellWallKey = "VMP_PP_CellWall";
        public const string CellSignText = "преступление против вальхейма";
        public const string CustodyPrefab = "piece_chest";
        public const string LayoutVersionKey = "VMP_PP_LayoutVersion";
        public const int CurrentLayoutVersion = 5;
        private const int MaximumPieces = 1280;
        private const int MaximumMobs = 8;
        private const float RoomHalfWidth = 18f;
        private const float RoomHeight = 12f;
        private static readonly FieldInfo WorldObjectsField = typeof(ZDOMan).GetField("m_objectsByID", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo ContainerLoad = typeof(Container).GetMethod("Load", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        private static ZDOMan cachedLayoutManager;
        private static PrisonRegion cachedLayoutRegion;
        private static int cachedLayoutVersion;
        private static float nextLayoutCheck, nextMobEnforcement;
        private static WaveRequest pendingWave;
        private static readonly string[] MobNames = {
            "Neck", "Greydwarf", "Greydwarf_Elite", "Greydwarf_Shaman",
            "Skeleton", "Draugr", "Draugr_Elite", "Goblin", "Greyling", "Hatchling", "Wolf"
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
        { return BuildNearAltars(levelGround, saveRegion, null); }

        public static PrisonRegion BuildNearAltars(bool levelGround, Action<PrisonRegion> saveRegion, Action<string> reportClearFailure)
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
            SiteClearer.Site bestClear = null;
            float bestScore = Single.MaxValue;
            string failure = "";
            string clearanceFailure = null;
            // Candidate searches are read-only. Only the selected manual site is
            // staged for clearing; background generation still needs an empty site.
            for (int ring = 0; ring < 5; ++ring)
                for (int direction = 0; direction < 24; ++direction) {
                    float angle = direction * Mathf.PI * 2f / 24f;
                    Vector3 candidate = altar + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * (40f + ring * 12f);
                    float height;
                    if (!ZoneSystem.instance.GetGroundHeight(candidate, out height)) continue;
                    candidate.y = height;
                    Quaternion rotation = levelGround ? Quaternion.identity
                        : Quaternion.LookRotation(new Vector3(-Mathf.Sin(angle), 0f, Mathf.Cos(angle)));
                    bool inspectedTerrain = false;
                    try {
                        TerrainLeveler.Site terrain = levelGround ? TerrainLeveler.Plan(candidate, altar) : null;
                        inspectedTerrain = terrain != null;
                        SiteClearer.Site clear = null;
                        float floor;
                        if (terrain == null) floor = PreflightSite(candidate, rotation);
                        else {
                            // Include both the existing terrain and the whole future room.
                            // Do not bury objects when raising the floor or uncover them when lowering it.
                            float low = terrain.TargetHeight - (float)TerrainPlan.MaximumGroundChange - 1f;
                            float high = terrain.TargetHeight + (float)TerrainPlan.MaximumGroundChange + RoomHeight + 1f;
                            clear = SiteClearer.Plan(candidate, rotation, (float)(TerrainPlan.HalfWidth + TerrainPlan.FootprintPadding), low, high, altar, true);
                            floor = terrain.FloorHeight;
                        }
                        float score = ring * 20f + Mathf.Abs(floor - height) + (clear == null ? 0f : clear.Cost * 0.5f);
                        if (score < bestScore) {
                            bestScore = score; best = candidate; bestRotation = rotation; bestTerrain = terrain; bestClear = clear;
                        }
                    }
                    catch (InvalidOperationException error) {
                        failure = error.Message;
                        if (inspectedTerrain && clearanceFailure == null) clearanceFailure = error.Message;
                    }
                }
            if (bestScore == Single.MaxValue)
                throw new InvalidOperationException(levelGround
                    ? "Подходящая площадка у алтарей не найдена. " + (clearanceFailure ?? failure)
                    : "Рядом с алтарями нет свободного ровного места для фоновой постройки. Команда /prison build сама расчистит и выровняет площадку. " + failure);
            using (SiteClearer.Transaction clear = bestClear == null ? null : bestClear.Apply())
            using (TerrainLeveler.Transaction terrain = bestTerrain == null ? null : bestTerrain.Apply()) {
                if (bestTerrain != null) best.y = bestTerrain.TargetHeight;
                PrisonRegion created = Build(best, bestRotation);
                try {
                    if (clear != null) clear.ValidateCommit();
                    if (saveRegion != null) saveRegion(created);
                }
                catch { TryRollback(created); throw; }
                if (terrain != null) terrain.Commit();
                if (clear != null) {
                    try { clear.Commit(); }
                    catch (Exception error) {
                        // The prison and its durable record are already committed.
                        // A partially completed native deletion cannot be rolled back.
                        string message = "Тюрьма построена, но расчистка завершилась с ошибкой: " + error.Message;
                        if (reportClearFailure != null) reportClearFailure(message);
                        else Debug.LogWarning("[Party Prison] " + message);
                    }
                }
                return created;
            }
        }

        /// <summary>The caller's frozen forward site is prepared without environmental vetoes.</summary>
        public static PrisonRegion BuildAnywhere(Vector3 origin, Quaternion facing, PrisonRegion oldRegion,
            Action<PrisonRegion> saveRegion, Action<string> reportClearFailure, Action<byte[]> broadcast)
        {
            RequireHost();
            if (ZoneSystem.instance == null) throw new InvalidOperationException("Мир ещё не загружен.");
            float target = Mathf.Max(origin.y, ZoneSystem.instance.m_waterLevel + .75f);
            TerrainLeveler.Site site = TerrainLeveler.PlanAnywhere(origin, target, facing);
            float low = Mathf.Min(site.LowestGroundHeight, target) - 3f;
            float high = Mathf.Max(site.HighestGroundHeight, target + RoomHeight) + 3f;
            SiteClearer.Site clearing = SiteClearer.PlanForce(origin, Quaternion.identity, site.WorldExtent, low, high, oldRegion);
            using (SiteClearer.Transaction clear = clearing.Apply())
            using (TerrainLeveler.Transaction terrain = site.Apply()) {
                origin.y = site.TargetHeight;
                PrisonRegion created = BuildPrepared(origin, facing);
                try {
                    clear.ValidateCommit();
                    if (saveRegion != null) saveRegion(created);
                }
                catch { TryRollback(created); throw; }
                terrain.Commit();
                try {
                    clear.Commit();
                }
                catch (Exception error) {
                    string message = "Тюрьма построена; ошибка завершения расчистки: " + error.Message;
                    if (reportClearFailure != null) reportClearFailure(message); else Debug.LogWarning("[Party Prison] " + message);
                }
                finally {
                    if (clear.Committed && broadcast != null) {
                        try { broadcast(clear.RemotePayload); }
                        catch (Exception error) {
                            string message = "Тюрьма построена; повторно подключите клиентов для обновления расчистки: " + error.Message;
                            if (reportClearFailure != null) reportClearFailure(message); else Debug.LogWarning("[Party Prison] " + message);
                        }
                    }
                }
                return created;
            }
        }

        public static PrisonRegion Build(Vector3 origin, Quaternion facing)
        { return BuildStructure(origin, facing, false); }

        private static PrisonRegion BuildPrepared(Vector3 origin, Quaternion facing)
        { return BuildStructure(origin, facing, true); }

        private static PrisonRegion BuildStructure(Vector3 origin, Quaternion facing, bool prepared)
        {
            RequireHost();
            if (!Finite(origin)) throw new ArgumentException("Неверные координаты тюрьмы.");
            if (ZoneSystem.instance == null) throw new InvalidOperationException("Мир ещё не загружен.");
            // Pitch and roll are never applied to building pieces.
            Quaternion rotation = Quaternion.Euler(0f, facing.eulerAngles.y, 0f);
            Dictionary<string, GameObject> prefabs = new Dictionary<string, GameObject>();
            string[] pieceNames = { "stone_floor_2x2", "stone_wall_4x2", "stone_wall_2x1", "stone_stair", "iron_wall_2x2", "iron_grate", "piece_bench01", CustodyPrefab, "piece_dvergr_lantern", "crystal_wall_1x1", "sign", "bed", PrisonConsole.PrefabName, PrisonContent.PrisonCampfirePrefab };
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
            float floor = prepared ? origin.y + .15f : PreflightSite(origin, rotation);
            Vector3 basePoint = new Vector3(origin.x, floor, origin.z);
            PrisonRegion region = new PrisonRegion {
                Center = Point(basePoint + Vector3.up * 6f), Radius = ArenaGeometry.ExpandedRadius, HalfHeight = 8d,
                CellSpawn = Point(basePoint + rotation * new Vector3(-14f, 1f, 0f)),
                ArenaSpawn = Point(basePoint + rotation * new Vector3(4f, 1f, 4f))
            };
            List<Placement> plan = CreateLayoutPlan(bounds);
            for (int chest = 0; chest < 4; ++chest)
                plan.Add(new Placement(CustodyPrefab, new Vector3(-12f + chest * 4f, -bounds[CustodyPrefab].min.y, -16f), 0f, CustodyKey, chest));
            plan.Add(new Placement(PrisonConsole.PrefabName, new Vector3(-14f, -bounds[PrisonConsole.PrefabName].min.y, -6f), 0f, PrisonConsole.ConsoleKey, -1));
            plan.Add(new Placement(PrisonContent.PrisonCampfirePrefab, new Vector3(-14f, -bounds[PrisonContent.PrisonCampfirePrefab].min.y, 13f), 0f, PrisonContent.PrisonCampfireMarker, -1));
            if (plan.Count > MaximumPieces) throw new InvalidOperationException("Превышен лимит деталей тюрьмы.");
            List<GameObject> created = new List<GameObject>();
            try {
                foreach (Placement placement in plan) {
                    GameObject piece = CreatePiece(prefabs[placement.Prefab], placement, basePoint, rotation);
                    created.Add(piece);
                }
                // The cell chest remains empty until a confirmed admission stocks it.
                InvalidateLayoutCache();
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
        public static bool IsCustody(GameObject gameObject)
        { return HasMarker(gameObject, CustodyKey) && !HasMarker(gameObject, CustodyInventory.PublicKey); }

        private static bool IsWindowSegment(int tier, int segment)
        { return tier < 2 && (segment == 5 || segment == 6); }

        private static void AppendWindows(List<Placement> plan, Bounds glass)
        {
            // Two eight-metre viewing walls at standing eye height. Glass is a
            // native solid building piece, so spectators can see but cannot enter.
            for (int along = 0; along < 8; ++along)
                for (int tier = 0; tier < 3; ++tier) {
                    float y = tier - glass.min.y, offset = 2.5f + along;
                    plan.Add(new Placement("crystal_wall_1x1", new Vector3(offset, y, RoomHalfWidth), 0f, WindowKey, -1));
                    plan.Add(new Placement("crystal_wall_1x1", new Vector3(RoomHalfWidth, y, offset), 90f, WindowKey, -1));
                }
        }

        private static void AppendCellFurniture(List<Placement> plan, Bounds chest, Bounds bed)
        {
            plan.Add(new Placement(CustodyPrefab, new Vector3(-16f, -chest.min.y, 3f), 90f, KitKey, -1));
            plan.Add(new Placement("sign", new Vector3(-14f, 2.3f, 17.6f), 180f, SignKey, -1));
            plan.Add(new Placement("bed", new Vector3(-16f, -bed.min.y, 16f), 90f, BedKey, -1));
        }

        private static void AppendLamps(List<Placement> plan)
        {
            // This native bracket extends along local -X. Orient it into the room.
            plan.Add(new Placement("piece_dvergr_lantern", new Vector3(-17.4f, 3f, 0f), 180f, LampKey, -1));
            plan.Add(new Placement("piece_dvergr_lantern", new Vector3(-10.6f, 3f, 0f), 0f, LampKey, -1));
            plan.Add(new Placement("piece_dvergr_lantern", new Vector3(3f, 3f, -17.4f), 90f, LampKey, -1));
            plan.Add(new Placement("piece_dvergr_lantern", new Vector3(3f, 4f, 17.4f), 270f, LampKey, -1));
            plan.Add(new Placement("piece_dvergr_lantern", new Vector3(17.4f, 3f, -6f), 0f, LampKey, -1));
            plan.Add(new Placement("piece_dvergr_lantern", new Vector3(17.4f, 3f, 6f), 0f, LampKey, -1));
        }

        private static GameObject CreatePiece(GameObject prefab, Placement placement, Vector3 basePoint, Quaternion rotation)
        {
            GameObject piece = UnityEngine.Object.Instantiate(prefab,
                basePoint + rotation * placement.Offset, rotation * Quaternion.Euler(0f, placement.Yaw, 0f));
            try {
                ZDO zdo = MarkPersistent(piece, ProtectedKey);
                zdo.Set(LayoutVersionKey, CurrentLayoutVersion);
                if (placement.Marker != null) zdo.Set(placement.Marker, true);
                if (placement.Marker == CustodyKey || placement.Marker == KitKey) zdo.Set(CustodyInventory.PublicKey, true);
                if (placement.CustodyIndex >= 0) zdo.Set(CustodyIndexKey, placement.CustodyIndex);
                if (placement.Marker == ExitKey) zdo.Set(ZDOVars.s_state, 1);
                if (placement.Marker == SignKey) {
                    if (piece.GetComponent<Sign>() == null) throw new InvalidOperationException("Не создана табличка в камере.");
                    zdo.Set(ZDOVars.s_text, CellSignText);
                    // Empty means a public system sign. The native "host" sentinel
                    // leaves Sign.m_author null and its permission code dereferences it.
                    zdo.Set(ZDOVars.s_author, "");
                    zdo.Set(ZDOVars.s_authorDisplayName, "");
                }
                Piece nativePiece = piece.GetComponent<Piece>();
                if (nativePiece != null) nativePiece.m_canBeRemoved = false;
                WearNTear wear = piece.GetComponent<WearNTear>();
                if (wear != null) { wear.m_noRoofWear = true; wear.m_noSupportWear = true; }
                return piece;
            }
            catch { DestroyCreated(piece); throw; }
        }

        /// <summary>Upgrade the loaded enclosure without replacing any property chest.</summary>
        public static bool UpgradeLayout(PrisonRegion region)
        { return UpgradeLoadedLayout(region); }

        private static void InvalidateLayoutCache() { cachedLayoutManager = null; nextLayoutCheck = 0f; InvalidateStoredGearCache(); }

        public static int LayoutVersion(PrisonRegion region)
        {
            float now = Time.realtimeSinceStartup;
            if (cachedLayoutManager == ZDOMan.instance && SameRegion(cachedLayoutRegion, region) && now < nextLayoutCheck)
                return cachedLayoutVersion;
            int version = Int32.MaxValue;
            foreach (ZDO zdo in TaggedRegionObjects(ProtectedKey, region))
                if (InsideStructure(region, zdo.GetPosition())) version = Math.Min(version, zdo.GetInt(LayoutVersionKey, 0));
            cachedLayoutVersion = version == Int32.MaxValue ? 0 : version;
            cachedLayoutManager = ZDOMan.instance; cachedLayoutRegion = region; nextLayoutCheck = now + .5f;
            return cachedLayoutVersion;
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

        public static List<ZDOID> CaptureStructure(PrisonRegion region)
        {
            var captured = new List<ZDOID>(); if (region == null) return captured;
            var seen = new HashSet<ZDOID>();
            foreach (string key in new[] { ProtectedKey, ArmoryKey, MobKey })
                foreach (ZDO zdo in TaggedWorldObjects(key))
                    if (InsideStructure(region, zdo.GetPosition()) && seen.Add(zdo.m_uid)) captured.Add(zdo.m_uid);
            return captured;
        }

        public static void RemoveCapturedStructure(List<ZDOID> captured)
        {
            RequireHost();
            foreach (ZDOID id in captured) {
                ZDO zdo = ZDOMan.instance.GetZDO(id); if (zdo == null) continue;
                ZNetView view = ZNetScene.instance.FindInstance(zdo);
                if (view != null && view.IsValid()) { view.ClaimOwnership(); ZNetScene.instance.Destroy(view.gameObject); }
                else { zdo.SetOwner(ZNet.GetUID()); ZDOMan.instance.DestroyZDO(zdo); }
            }
        }

        /// <summary>Explicit rebuilds never dispose of any old property, kit or legacy armory inventory.</summary>
        public static void RequireEmptyStoredContainers(PrisonRegion region)
        {
            RequireHost();
            if (region == null) return;
            var objects = TaggedWorldObjects(ProtectedKey);
            objects.AddRange(TaggedWorldObjects(ArmoryKey));
            RequireEmptyStoredContainers(region, objects, LayoutVersion(region), ResolveStoredPrefab, LoadedStoredContainer);
        }

        private static GameObject ResolveStoredPrefab(int hash) { return ZNetScene.instance.GetPrefab(hash); }
        private static Container LoadedStoredContainer(ZDO zdo)
        {
            ZNetView view = ZNetScene.instance.FindInstance(zdo);
            return view == null || !view.IsValid() ? null : view.GetComponentInChildren<Container>(true);
        }

        private static void RequireEmptyStoredContainers(PrisonRegion region, IList<ZDO> objects, int layoutVersion,
            Func<int, GameObject> prefabResolver, Func<ZDO, Container> loadedResolver)
        {
            var seen = new HashSet<ZDOID>(); var custodyIndices = new bool[4]; int custodyCount = 0;
            foreach (ZDO zdo in objects) {
                if (zdo == null || !InsideStructure(region, zdo.GetPosition()) || !seen.Add(zdo.m_uid)
                    || !zdo.GetBool(ProtectedKey, false) && !zdo.GetBool(ArmoryKey, false)) continue;
                bool custody = zdo.GetBool(CustodyKey, false), kit = zdo.GetBool(KitKey, false);
                if (custody) {
                    int index = zdo.GetInt(CustodyIndexKey, -1);
                    if (index < 0 || index >= 4 || custodyIndices[index])
                        throw new InvalidOperationException("Повреждены постоянные номера сундуков тюрьмы; перестройка отменена.");
                    custodyIndices[index] = true; ++custodyCount;
                }
                GameObject prefab = prefabResolver(zdo.GetPrefab());
                if (prefab == null)
                    throw new InvalidOperationException("Неизвестная сохранённая деталь тюрьмы; перестройка отменена для сохранения её содержимого.");
                byte[] payload = zdo.GetByteArray(ZDOVars.s_items, null);
                string legacy = payload == null ? zdo.GetString(ZDOVars.s_items, "") : "";
                bool container = prefab.GetComponentInChildren<Container>(true) != null;
                if ((custody || kit) && !container)
                    throw new InvalidOperationException("Сохранённый сундук тюрьмы больше не поддерживается; перестройка отменена.");
                if (!container && payload == null && String.IsNullOrEmpty(legacy)) continue;
                Container loaded = loadedResolver(zdo);
                if (loaded != null && (loaded.IsInUse() || loaded.GetInventory() != null && loaded.GetInventory().GetAllItems().Count != 0))
                    throw new InvalidOperationException("Закройте и освободите все сундуки тюрьмы, включая сундук снаряжения, перед перестройкой.");
                if (payload == null && !String.IsNullOrEmpty(legacy)) {
                    try { payload = Convert.FromBase64String(legacy); }
                    catch (FormatException error) { throw new InvalidOperationException("Повреждено содержимое старого сундука; перестройка отменена.", error); }
                }
                // Count checks a strict native round trip, including custom-data
                // and missing prefabs. A failed decode never becomes an empty chest.
                if (payload != null && CustodyInventory.Count(payload) != 0)
                    throw new InvalidOperationException("Заберите все вещи из сундуков тюрьмы, включая сундук снаряжения и старую оружейную, перед перестройкой.");
            }
            if (layoutVersion >= 2 && custodyCount != 4)
                throw new InvalidOperationException("Ожидаются четыре сохранённых сундука тюрьмы; перестройка отменена до восстановления их записей.");
        }

        public static List<ZDO> GetCustodyZdos(PrisonRegion region)
        {
            List<ZDO> result = new List<ZDO>();
            foreach (ZDO zdo in TaggedRegionObjects(CustodyKey, region))
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
            foreach (ZDO zdo in TaggedRegionObjects(ExitKey, region)) {
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
            float halfWidth = (float)ArenaGeometry.RoomHalfWidth(region) - .3f;
            return Mathf.Abs(x) <= halfWidth && Mathf.Abs(z) <= halfWidth
                && position.y >= floor - 0.25f && position.y <= floor + (float)ArenaGeometry.RoomHeight(region) - .5f;
        }

        /// <summary>The public property lobby is outside the sentence boundary.</summary>
        public static bool ContainsConfinement(PrisonRegion region, Vector3 position)
        {
            if (!ContainsRoom(region, position)) return false;
            Vector3 right = CellRight(region), forward = Vector3.Cross(right, Vector3.up);
            return Vector3.Dot(position - Vector(region.Center), forward) >= (float)ArenaGeometry.Divider(region) + .3f;
        }

        public static bool IsInsideArena(PrisonRegion region, Vector3 position) { return InsideArena(region, position); }

        /// <summary>The whole bench cell is safe, including its northern seating area.</summary>
        public static bool IsInsideCell(PrisonRegion region, Vector3 position)
        {
            if (!ContainsConfinement(region, position)) return false;
            return Vector3.Dot(position - Vector(region.Center), CellRight(region)) <= (float)ArenaGeometry.Divider(region) - .3f;
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

        /// <summary>Compatibility entry point: stock is now created only for a sentence.</summary>
        public static int EnsureArmory(PrisonRegion region)
        { RequireHost(); return 0; }

        public static ZDO GetKitZdo(PrisonRegion region)
        {
            if (region == null) return null;
            foreach (ZDO zdo in StoredGearChests(region))
                if (zdo.GetBool(KitKey, false) && InsideStructure(region, zdo.GetPosition()) && ZDOMan.instance.GetZDO(zdo.m_uid) == zdo) return zdo;
            return null;
        }

        public static Container GetKitContainer(PrisonRegion region)
        {
            ZDO zdo = GetKitZdo(region);
            ZNetView view = zdo == null || ZNetScene.instance == null ? null : ZNetScene.instance.FindInstance(zdo);
            return view == null || !view.IsValid() ? null : view.GetComponent<Container>();
        }

        /// <summary>Each admission stocks ordinary items once, preserving visitors' deposits.</summary>
        public static int EnsureSentenceKit(PrisonRegion region, string token)
        {
            RequireHost();
            if (String.IsNullOrEmpty(token)) throw new ArgumentException("Не указан срок для выдачи снаряжения.");
            ZDO zdo = GetKitZdo(region);
            if (zdo == null) throw new InvalidOperationException("Сундук снаряжения ещё не загружен; подойдите к тюрьме.");
            if (zdo.GetString(KitTokenKey, "") == token && zdo.GetInt(KitRevisionKey, 0) > 0) return 0;
            ConfigureSentenceKit(region, token, 0, 0);
            return CombatCatalog.Get(0, 0).GearPrefabs.Length;
        }

        /// <summary>Empty only the generated stock; ordinary deposits stay in the public chest.</summary>
        public static bool ClearSentenceKit(PrisonRegion region)
        {
            RequireHost();
            ZDO zdo = GetKitZdo(region);
            if (zdo == null || zdo.GetString(KitTokenKey, "").Length == 0) return false;
            Container chest = GetKitContainer(region);
            if (chest == null || chest.IsInUse()) return false;
            var storage = new PrisonKitStorage(zdo, chest.GetInventory());
            chest.GetComponent<ZNetView>().ClaimOwnership();
            if (ContainerLoad == null) throw new MissingMethodException("Container", "Load");
            ContainerLoad.Invoke(chest, null);
            Inventory inventory = storage.WorkingCopy(chest.GetInventory());
            var remove = new List<ItemDrop.ItemData>();
            foreach (ItemDrop.ItemData item in inventory.GetAllItems())
                if (IsGeneratedGear(item)) remove.Add(item);
            foreach (ItemDrop.ItemData item in remove) inventory.RemoveItem(item);
            if (remove.Count > 0) storage.Publish(chest, inventory);
            zdo.Set(KitTokenKey, ""); zdo.Set(KitRevisionKey, 0); ZDOMan.instance.ForceSendZDO(zdo.m_uid);
            return true;
        }

        public static int SpawnWave(PrisonRegion region, string prefabName, int count, int level)
        {
            RequireHost();
            if (region == null) throw new InvalidOperationException("Сначала создайте тюрьму.");
            if (!CanSpawnWave(region)) throw new InvalidOperationException("Арена готовится к следующему бою; подождите несколько секунд.");
            string canonical = null;
            foreach (string allowed in MobNames)
                if (String.Equals(allowed, prefabName, StringComparison.OrdinalIgnoreCase)) { canonical = allowed; break; }
            if (canonical == null) throw new ArgumentException("Допустимые мобы: " + AllowedMobs);
            if (count < 1 || count > MaximumMobs || level < 1 || level > 3)
                throw new ArgumentException("За волну: 1–8 мобов; уровень: 1–3.");
            int existing = LiveMobCount(region);
            if (existing + count > MaximumMobs)
                throw new InvalidOperationException("В тюрьме уже есть мобы; общий лимит — 8. Сначала завершите или очистите волну.");
            GameObject prefab = RequirePrefab(canonical);
            if (prefab.GetComponent<Character>() == null || prefab.GetComponent<MonsterAI>() == null)
                throw new InvalidOperationException("Неподдерживаемый моб: " + canonical);
            if (pendingWave != null) throw new InvalidOperationException("Дождитесь завершения появления текущей волны.");
            pendingWave = new WaveRequest { Region = region, Prefab = prefab, Count = count, Level = level,
                Manager = ZDOMan.instance, NextSpawn = Time.realtimeSinceStartup + .1f };
            return count;
        }

        public static int PendingMobCount(PrisonRegion region)
        { return pendingWave != null && SameRegion(pendingWave.Region, region) ? pendingWave.Count - pendingWave.Spawned : 0; }

        /// <summary>One native AI/network instantiation per interval, never a synchronous batch.</summary>
        public static void TickWaveSpawning(PrisonRegion region)
        {
            if (pendingWave == null) return;
            RequireHost();
            WaveRequest wave = pendingWave;
            if (wave.Manager != ZDOMan.instance || !SameRegion(wave.Region, region)) { pendingWave = null; return; }
            float now = Time.realtimeSinceStartup;
            if (now < wave.NextSpawn) return;
            Vector3 position;
            if (!TryWavePosition(region, wave.Spawned, wave.Prefab.name == "Hatchling", out position)) {
                // Occupied entrances never become a telefrag or an enemy embedded
                // in stone. A completely obstructed arena cancels after ten waits.
                if (++wave.BlockedAttempts >= 10) pendingWave = null;
                else wave.NextSpawn = now + 1f;
                return;
            }
            // Drakes start below the solid ceiling, with enough height for their
            // native flying AI to track the prisoner through the viewing grilles.
            if (wave.Prefab.name == "Hatchling") position.y += 2f;
            GameObject mob = null;
            try {
                Vector3 look = Vector(region.ArenaSpawn) - position; look.y = 0f;
                mob = UnityEngine.Object.Instantiate(wave.Prefab, position, look.sqrMagnitude > .01f ? Quaternion.LookRotation(look) : Quaternion.identity);
                MarkPersistent(mob, MobKey); mob.GetComponent<Character>().SetLevel(wave.Level);
                // Native mob drops stay enabled; cell access follows the native door.
                ++wave.Spawned; wave.BlockedAttempts = 0; wave.NextSpawn = now + .6f;
                if (wave.Spawned >= wave.Count) pendingWave = null;
            }
            catch { if (mob != null) DestroyCreated(mob); pendingWave = null; throw; }
        }

        public static void CancelPendingWave() { pendingWave = null; }

        private static readonly int SpawnCollisionMask = LayerMask.GetMask("Default", "static_solid", "piece", "character", "character_net", "terrain");
        private static bool TryWavePosition(PrisonRegion region, int index, bool flying, out Vector3 position)
        {
            Vector3 right = CellRight(region), forward = Vector3.Cross(right, Vector3.up);
            for (int candidate = 0; candidate < 8; ++candidate) {
                PrisonPoint anchor = ArenaGeometry.Spawn(region, (index + candidate) % 8);
                position = Vector(region.Center) + right * (float)anchor.X + forward * (float)anchor.Z;
                position.y = (float)region.CellSpawn.Y;
                Vector3 bottom = position + Vector3.up * (flying ? 2f : 0f);
                if (!Physics.CheckCapsule(bottom, bottom + Vector3.up * 1.2f, .55f, SpawnCollisionMask, QueryTriggerInteraction.Ignore)) return true;
            }
            position = Vector3.zero; return false;
        }

        public static void ResetRuntime()
        { pendingWave = null; ResetCombatRuntime(); nextMobEnforcement = 0f; cachedLayoutRegion = null; InvalidateLayoutCache(); }

        public static int LiveMobCount(PrisonRegion region)
        {
            if (region == null) return 0;
            int count = PendingMobCount(region);
            foreach (ZDO zdo in TaggedRegionObjects(MobKey, region))
                if (!zdo.GetBool(ZDOVars.s_dead, false) && zdo.GetFloat(ZDOVars.s_health, 1f) > 0f
                    && (region == null || ContainsRoom(region, zdo.GetPosition()))) ++count;
            return count;
        }

        public static int CleanupMobs() { return CleanupMobs(null, true); }
        public static int CleanupMobs(PrisonRegion region) { return CleanupMobs(region, true); }

        public static int CleanupMobs(PrisonRegion region, bool all)
        {
            RequireHost();
            if (all) CancelPendingWave();
            int removed = 0;
            foreach (ZDO zdo in region == null ? TaggedWorldObjects(MobKey) : TaggedRegionObjects(MobKey, region)) {
                if (!all && (region == null || ContainsRoom(region, zdo.GetPosition()))) continue;
                // DestroyZDO requires ownership. This host-only path also removes
                // unloaded mobs and announces destruction to their remote owners.
                DestroyWorldObject(zdo); ++removed;
            }
            return removed;
        }

        /// <summary>All peers confine the marked mobs they own, including ones whose ownership moved off the host.</summary>
        public static void EnforceOwnedMobs(PrisonRegion region)
        {
            if (region == null || ZNetScene.instance == null) return;
            float now = Time.realtimeSinceStartup;
            if (now < nextMobEnforcement) return;
            nextMobEnforcement = now + .5f;
            foreach (Character character in Character.GetAllCharacters()) {
                if (character == null || character.IsDead() || !IsMob(character.gameObject)) continue;
                ZNetView view = character.GetComponent<ZNetView>();
                if (view == null || !view.IsOwner() || ContainsRoom(region, character.transform.position)) continue;
                Vector3 position = Vector(region.ArenaSpawn);
                if (view.GetZDO().GetPrefab() == "Hatchling".GetStableHashCode()) {
                    Vector3 current = character.transform.position, delta = current - Vector(region.Center);
                    float floor = (float)region.CellSpawn.Y - 1f;
                    float halfWidth = (float)ArenaGeometry.RoomHalfWidth(region) - .3f;
                    float height = (float)ArenaGeometry.RoomHeight(region);
                    if (Mathf.Abs(Vector3.Dot(delta, CellRight(region))) <= halfWidth
                        && Mathf.Abs(Vector3.Dot(delta, Vector3.Cross(CellRight(region), Vector3.up))) <= halfWidth && current.y > floor + height - .5f)
                        // A flying enemy that pushes into the ceiling retains its
                        // horizontal position, including inside the open cell.
                        position = new Vector3(current.x, floor + height - 2.5f, current.z);
                }
                character.transform.position = position;
                Rigidbody body = character.GetComponent<Rigidbody>();
                if (body != null) { body.position = position; body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
                view.GetZDO().SetPosition(position);
                ZSyncTransform sync = character.GetComponent<ZSyncTransform>(); if (sync != null) sync.SyncNow();
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

        private static float PreflightSite(Vector3 origin, Quaternion rotation)
        {
            float low = Single.MaxValue, high = Single.MinValue;
            for (int x = -18; x <= 18; x += 4)
                for (int z = -18; z <= 18; z += 4) {
                    Vector3 point = origin + rotation * new Vector3(x, 0f, z);
                    float height;
                    if (!ZoneSystem.instance.IsZoneLoaded(point) || !ZoneSystem.instance.GetGroundHeight(point, out height))
                        throw new InvalidOperationException("Площадка не загружена. Выберите открытое место рядом с хостом.");
                    if (height <= ZoneSystem.instance.m_waterLevel + 0.5f)
                        throw new InvalidOperationException("Тюрьме нужна сухая площадка вдали от воды.");
                    low = Mathf.Min(low, height); high = Mathf.Max(high, height);
                }
            if (high - low > 2f || Mathf.Abs(origin.y - high) > 4f)
                throw new InvalidOperationException("Для фоновой генерации нужна ровная площадка 41 × 41 м. Команда /prison build сама подготовит землю.");
            RequireClearSite(origin, rotation, 20.5f, high - 1f, high + RoomHeight + 1f);
            return high + 0.15f;
        }

        private static void RequireClearSite(Vector3 origin, Quaternion rotation, float halfWidth, float low, float high, bool allowHost = true)
        {
            Vector3 center = new Vector3(origin.x, (low + high) * 0.5f, origin.z);
            foreach (Collider collider in Physics.OverlapBox(center, new Vector3(halfWidth, (high - low) * 0.5f, halfWidth), rotation, SiteClearer.CollisionMask, QueryTriggerInteraction.Ignore)) {
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

        private static List<ZDO> TaggedRegionObjects(string key, PrisonRegion region)
        {
            var result = new List<ZDO>();
            if (region == null || ZDOMan.instance == null || ZoneSystem.instance == null) return result;
            var objects = new List<ZDO>();
            // A room is 24 metres wide. These nine native 64-metre sectors include
            // its boundary and escaped mobs without scanning the complete world.
            ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(Vector(region.Center)), new SimulationDistance(1, 0, true), objects, null);
            int hash = key.GetStableHashCode();
            foreach (ZDO zdo in objects) if (zdo != null && zdo.GetBool(hash, false)) result.Add(zdo);
            return result;
        }

        private static bool SameRegion(PrisonRegion left, PrisonRegion right)
        {
            return left != null && right != null && left.Center.X == right.Center.X && left.Center.Y == right.Center.Y
                && left.Center.Z == right.Center.Z && left.CellSpawn.X == right.CellSpawn.X && left.CellSpawn.Z == right.CellSpawn.Z;
        }

        private static void DestroyWorldObject(ZDO zdo)
        {
            ZNetView view = ZNetScene.instance.FindInstance(zdo);
            if (view != null && view.IsValid()) DestroyCreated(view.gameObject);
            else { zdo.SetOwner(ZNet.GetUID()); ZDOMan.instance.DestroyZDO(zdo); }
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
            float minimum = (float)ArenaGeometry.Divider(region) + .3f;
            return x >= minimum && z >= minimum;
        }
        private static bool InsideStructure(PrisonRegion region, Vector3 position)
        {
            if (region == null || !Finite(position)) return false;
            Vector3 right = CellRight(region), forward = Vector3.Cross(right, Vector3.up);
            Vector3 delta = position - Vector(region.Center);
            float floor = (float)region.CellSpawn.Y - 1f;
            // Exact footprint avoids deleting a neighbouring new layout whose
            // circumscribed region happens to intersect the old region's circle.
            float halfWidth = (float)ArenaGeometry.RoomHalfWidth(region) + .5f;
            return Mathf.Abs(Vector3.Dot(delta, right)) <= halfWidth && Mathf.Abs(Vector3.Dot(delta, forward)) <= halfWidth
                && position.y >= floor - 1f && position.y <= floor + (float)ArenaGeometry.RoomHeight(region) + 2f;
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
        private sealed class WaveRequest
        {
            public PrisonRegion Region;
            public GameObject Prefab;
            public ZDOMan Manager;
            public int Count, Spawned, Level, BlockedAttempts;
            public float NextSpawn;
        }
    }
}
