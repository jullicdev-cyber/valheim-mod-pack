using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValheimModPack.PartyPrison
{
    public static partial class SiteClearer
    {
        private const int ForceMaximumRoots = 8192, ForceMaximumColliders = 65536, MaximumRemoteBytes = 1024 * 1024;
        private const string StaticMaskKey = "VMP_PP_StaticClearance_v1", StorageMoveKey = "VMP_PP_StorageMoved";
        private static readonly MethodInfo ClearSupport = typeof(WearNTear).GetMethod("ClearCachedSupport", BindingFlags.Instance | BindingFlags.NonPublic);
        private static PrisonRegion staticRegion;
        private static long staticRegionWorld;
        private static float staticExtent;
        private static bool regionApplied;

        public static Site PlanForce(Vector3 origin, Quaternion rotation, float extent, float low, float high)
        { return PlanForce(origin, rotation, extent, low, high, null); }

        public static Site PlanForce(Vector3 origin, Quaternion rotation, float extent, float low, float high, PrisonRegion replaceableRegion)
        {
            RequireHost();
            if (!Finite(origin) || !Finite(extent) || extent < 12f || extent > 30f ||
                !Finite(low) || !Finite(high) || low < -10000f || high > 10000f || low >= high ||
                !Finite(rotation.x) || !Finite(rotation.y) || !Finite(rotation.z) || !Finite(rotation.w))
                throw new ArgumentException("Неверные границы принудительной расчистки.");
            if (!ZNetScene.instance.IsAreaReady(origin))
                throw new InvalidOperationException("Объекты площадки ещё загружаются. Подождите несколько секунд.");
            return new Site(new ForceSite(origin, extent, low, high, replaceableRegion));
        }

        // State messages arrive regularly. Identical regions do no searches or Unity work.
        public static void ConfigureRegion(PrisonRegion region)
        {
            long currentWorld = ZNet.instance == null ? 0L : ZNet.instance.GetWorldUID();
            if (currentWorld == staticRegionWorld && SameRegion(staticRegion, region)) return;
            staticRegionWorld = currentWorld;
            staticRegion = region == null ? null : region.Copy(); regionApplied = false;
            if (region == null) { staticExtent = 0f; return; }
            double dx = region.Center.X - region.CellSpawn.X, dz = region.Center.Z - region.CellSpawn.Z;
            double yaw = Math.Atan2(-dz, dx);
            staticExtent = (float)TerrainPlan.EnclosingExtent(yaw * 180d / Math.PI, ArenaGeometry.Expanded(region));
            if (ZNetScene.instance != null) { ReapplyCurrentStaticRegion(); regionApplied = true; }
        }

        private static bool SameRegion(PrisonRegion a, PrisonRegion b)
        {
            if (a == null || b == null) return a == b;
            return a.Center.X == b.Center.X && a.Center.Y == b.Center.Y && a.Center.Z == b.Center.Z &&
                a.CellSpawn.X == b.CellSpawn.X && a.CellSpawn.Z == b.CellSpawn.Z &&
                a.ArenaSpawn.X == b.ArenaSpawn.X && a.ArenaSpawn.Z == b.ArenaSpawn.Z;
        }

        internal sealed class ForceSite
        {
            internal readonly Vector3 Origin;
            internal readonly float Extent, Low, High;
            internal readonly PrisonRegion Replaceable;
            internal readonly List<ForceEntry> Entries = new List<ForceEntry>();
            internal readonly ZNet Network;
            internal readonly ZNetScene Scene;
            internal readonly long World;
            private bool applied;
            internal int Count { get { return Entries.Count; } }

            internal ForceSite(Vector3 origin, float extent, float low, float high, PrisonRegion replaceable)
            {
                Origin = origin; Extent = extent; Low = low; High = high; Replaceable = replaceable;
                Network = ZNet.instance; Scene = ZNetScene.instance; World = Network.GetWorldUID();
                Gather();
            }

            private void Gather()
            {
                var selected = new Dictionary<GameObject, ForceEntry>();
                Vector3 center = new Vector3(Origin.x, (Low + High) * .5f, Origin.z);
                Collider[] overlap = Physics.OverlapBox(center, new Vector3(Extent, (High - Low) * .5f, Extent),
                    Quaternion.identity, CollisionMask, QueryTriggerInteraction.Collide);
                if (overlap.Length > ForceMaximumColliders) throw new InvalidOperationException("На площадке более 65536 коллизий; расчистка слишком велика.");
                foreach (Collider collider in overlap) {
                    if (collider == null || collider.GetComponentInParent<Heightmap>() != null || IsViewBlock(collider)) continue;
                    Character actor = collider.GetComponentInParent<Character>();
                    if (actor is Player) {
                        if (!collider.isTrigger) OccupiedPlayer();
                        continue;
                    }
                    ZNetView view = actor == null ? collider.GetComponentInParent<ZNetView>() : actor.GetComponent<ZNetView>();
                    if (collider.isTrigger) {
                        ItemDrop item = collider.GetComponentInParent<ItemDrop>();
                        Pickable pickable = collider.GetComponentInParent<Pickable>();
                        if (item != null && actor == null && ActualItemOverlap(item, center, new Vector3(Extent, (High - Low) * .5f, Extent), Quaternion.identity))
                            AddNetwork(item.GetComponent<ZNetView>(), selected);
                        else if (pickable != null && Inside(pickable.transform.position)) AddNetwork(pickable.GetComponent<ZNetView>(), selected);
                        continue;
                    }
                    if (view != null && view.IsValid() && !StaticParent(view.gameObject)) AddNetwork(view, selected);
                    else AddStatic(collider, view, selected);
                }
                // Spawn sources and pickables need not have any solid collider.
                var near = new List<ZDO>(); var distant = new List<ZDO>();
                ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(Origin), new SimulationDistance(1, 0, false), near, distant);
                foreach (ZDO zdo in near) {
                    if (zdo == null || !Inside(zdo.GetPosition())) continue;
                    ZNetView view = Scene.FindInstance(zdo);
                    if (view == null || !view.IsValid() || view.GetComponent<Heightmap>() != null || view.GetComponent<TerrainComp>() != null || view.GetComponent<TerrainModifier>() != null) continue;
                    if (view.GetComponent<Player>() != null) { OccupiedPlayer(); continue; }
                    if (!StaticParent(view.gameObject)) AddNetwork(view, selected);
                }
                Entries.AddRange(selected.Values);
                Entries.Sort(delegate(ForceEntry a, ForceEntry b) { return String.CompareOrdinal(a.SortKey, b.SortKey); });
                var reserved = new List<Bounds>();
                foreach (ForceEntry entry in Entries) if (entry.Action == ForceAction.Move) entry.Destination = Park(entry, reserved);
                if (Entries.Count > ForceMaximumRoots) throw new InvalidOperationException("На площадке более 8192 объектов; расчистка слишком велика.");
            }

            private bool Inside(Vector3 point)
            { return Mathf.Abs(point.x - Origin.x) <= Extent && Mathf.Abs(point.z - Origin.z) <= Extent && point.y >= Low && point.y <= High; }

            private void AddNetwork(ZNetView view, Dictionary<GameObject, ForceEntry> selected)
            {
                if (view == null || !view.IsValid()) throw new InvalidOperationException("Сетевой объект площадки ещё не готов к перемещению.");
                GameObject root = view.gameObject;
                if (selected.ContainsKey(root) || !root.activeInHierarchy) return;
                if (root.GetComponentInChildren<Player>(true) != null) { OccupiedPlayer(); return; }
                foreach (ForceEntry existing in selected.Values)
                    if (existing.Action == ForceAction.Move && root.transform.IsChildOf(existing.Root.transform)) return;
                ZDO zdo = view.GetZDO();
                bool prison = PrisonMarker(zdo);
                bool replaceable = prison && Replaceable != null && Replaceable.Contains(new PrisonPoint(root.transform.position.x,
                    (float)Replaceable.Center.Y, root.transform.position.z));
                if (prison && !replaceable) throw new InvalidOperationException("На площадке находится другая действующая тюрьма.");
                ForceAction action = replaceable ? ForceAction.Remove : PreserveRoot(root, zdo) ? ForceAction.Move : ForceAction.Remove;
                // A preserved parent carries its complete network child graph; do not process children twice.
                if (action == ForceAction.Move) {
                    var remove = new List<GameObject>();
                    foreach (var pair in selected) if (pair.Key.transform.IsChildOf(root.transform)) remove.Add(pair.Key);
                    foreach (GameObject child in remove) selected.Remove(child);
                }
                selected.Add(root, new ForceEntry(root, view, action, PhysicalBounds(root)));
                if (action == ForceAction.Remove)
                    foreach (ZNetView child in root.GetComponentsInChildren<ZNetView>(true)) if (child != view && child.IsValid()) AddNetwork(child, selected);
            }

            private void AddStatic(Collider collider, ZNetView parent, Dictionary<GameObject, ForceEntry> selected)
            {
                if (parent != null && !parent.IsValid()) parent = null;
                GameObject part = collider.gameObject;
                if (part.GetComponentInChildren<Player>(true) != null) { OccupiedPlayer(); return; }
                // Disable a single solid subtree, never its Location/Proxy root.
                if (part.GetComponent<Location>() != null || part.GetComponent<LocationProxy>() != null || part.GetComponent<DungeonGenerator>() != null) {
                    selected[part] = new ForceEntry(part, parent, ForceAction.StaticCollider, collider.bounds, collider);
                    return;
                }
                if (selected.ContainsKey(part)) return;
                selected.Add(part, new ForceEntry(part, parent, ForceAction.StaticPart, collider.bounds));
            }

            private Vector3 Park(ForceEntry entry, List<Bounds> reserved)
            {
                Bounds bounds = entry.Bounds;
                float spread = Mathf.Max(bounds.extents.x, bounds.extents.z) + 2f;
                for (int ring = 0; ring < 8; ++ring)
                    for (int direction = 0; direction < 32; ++direction) {
                        float angle = direction * Mathf.PI * 2f / 32f;
                        float distance = (Extent + spread + 4f + ring * Mathf.Max(4f, spread)) * 1.414214f;
                        Vector3 point = Origin + new Vector3(Mathf.Sin(angle) * distance, 0f, Mathf.Cos(angle) * distance);
                        float ground;
                        if (!ZoneSystem.instance.IsZoneLoaded(point) || !ZoneSystem.instance.GetGroundHeight(point, out ground) || !Finite(ground)) continue;
                        point.y = Mathf.Max(ground, ZoneSystem.instance.m_waterLevel + .5f) + .25f - (bounds.min.y - entry.Root.transform.position.y);
                        Vector3 delta = point - entry.Root.transform.position;
                        Bounds moved = new Bounds(bounds.center + delta, bounds.size + new Vector3(.4f, .4f, .4f));
                        if (moved.min.x <= Origin.x + Extent && moved.max.x >= Origin.x - Extent &&
                            moved.min.z <= Origin.z + Extent && moved.max.z >= Origin.z - Extent) continue;
                        bool occupied = false;
                        foreach (Bounds prior in reserved) if (prior.Intersects(moved)) { occupied = true; break; }
                        if (occupied) continue;
                        foreach (Collider collider in Physics.OverlapBox(moved.center, moved.extents, Quaternion.identity, CollisionMask, QueryTriggerInteraction.Ignore)) {
                            if (collider == null || collider.GetComponentInParent<Heightmap>() != null || collider.transform.IsChildOf(entry.Root.transform)) continue;
                            occupied = true; break;
                        }
                        if (occupied) continue;
                        reserved.Add(moved); return point;
                    }
                // Dense surroundings must not reject an otherwise valid force plot.
                // An intact storage root may stand above clutter; its support/rain wear
                // is disabled persistently, and no neighboring object is removed.
                for (int direction = 0; direction < 32; ++direction) {
                    float angle = direction * Mathf.PI * 2f / 32f;
                    float distance = (Extent + spread + 5f) * 1.414214f;
                    Vector3 point = Origin + new Vector3(Mathf.Sin(angle) * distance, 0f, Mathf.Cos(angle) * distance);
                    float ground;
                    if (!ZoneSystem.instance.IsZoneLoaded(point) || !ZoneSystem.instance.GetGroundHeight(point, out ground) || !Finite(ground)) continue;
                    float bottom = Mathf.Max(ground, ZoneSystem.instance.m_waterLevel + .5f) + .5f;
                    Vector3 probe = new Vector3(point.x + bounds.center.x - entry.Position.x, bottom + 100f,
                        point.z + bounds.center.z - entry.Position.z);
                    foreach (Collider collider in Physics.OverlapBox(probe, new Vector3(bounds.extents.x + .5f, 100f, bounds.extents.z + .5f),
                        Quaternion.identity, CollisionMask, QueryTriggerInteraction.Ignore)) {
                        if (collider == null || collider.GetComponentInParent<Heightmap>() != null || collider.transform.IsChildOf(entry.Root.transform)) continue;
                        if (Finite(collider.bounds.max.y)) bottom = Mathf.Max(bottom, collider.bounds.max.y + .5f);
                    }
                    point.y = bottom - (bounds.min.y - entry.Position.y);
                    Bounds moved = new Bounds(bounds.center + point - entry.Position, bounds.size + Vector3.one);
                    foreach (Bounds prior in reserved)
                        if (prior.min.x <= moved.max.x && prior.max.x >= moved.min.x && prior.min.z <= moved.max.z && prior.max.z >= moved.min.z) {
                            point.y = Mathf.Max(point.y, prior.max.y + .5f - (bounds.min.y - entry.Position.y));
                            moved = new Bounds(bounds.center + point - entry.Position, bounds.size + Vector3.one);
                        }
                    reserved.Add(moved); return point;
                }
                throw new InvalidOperationException("Место для сохранения вещей пока не загружено. Подождите загрузки соседнего участка.");
            }

            internal Transaction Apply()
            {
                RequireWorld(Network, World, Scene);
                if (applied) throw new InvalidOperationException("Эта расчистка уже выполнена.");
                // Verify exact identities and the bounded current collision set before mutating anything.
                ForceSite current = new ForceSite(Origin, Extent, Low, High, Replaceable);
                if (current.Entries.Count != Entries.Count) throw new InvalidOperationException("Объекты площадки изменились. Повторите постройку.");
                for (int i = 0; i < Entries.Count; ++i) {
                    Entries[i].Check(false);
                    if (!Entries[i].SameIdentity(current.Entries[i])) throw new InvalidOperationException("Объекты площадки изменились. Повторите постройку.");
                }
                applied = true;
                var transaction = new ForceTransaction(this);
                try { transaction.Apply(); return new Transaction(transaction); }
                catch { transaction.Dispose(); throw; }
            }
        }

        internal enum ForceAction { Remove, Move, StaticPart, StaticCollider }

        internal sealed class ForceEntry
        {
            internal readonly GameObject Root;
            internal readonly ZNetView View;
            internal readonly ForceAction Action;
            internal readonly Bounds Bounds;
            internal readonly Collider OnlyCollider;
            private readonly Collider[] localColliders;
            private readonly bool[] enabledColliders;
            private readonly WearNTear[] wearParts;
            private readonly bool[] supportFlags, roofFlags;
            internal readonly List<MoveNode> Nodes = new List<MoveNode>();
            internal readonly Transform Parent;
            internal readonly Vector3 Position, Scale;
            internal readonly Quaternion Rotation;
            internal readonly bool Active;
            internal readonly string Path;
            internal Vector3 Destination;
            internal bool Staged;
            internal string SortKey { get { return (View == null || !View.IsValid() ? "static" : View.GetZDO().m_uid.ToString()) + ":" + Path + ":" + Root.GetInstanceID(); } }

            internal ForceEntry(GameObject root, ZNetView view, ForceAction action, Bounds bounds, Collider collider = null)
            {
                Root = root; View = view; Action = action; Bounds = bounds; OnlyCollider = collider;
                Parent = root.transform.parent; Position = root.transform.position; Rotation = root.transform.rotation;
                Scale = root.transform.lossyScale; Active = root.activeSelf;
                localColliders = action == ForceAction.StaticCollider ? root.GetComponents<Collider>() : new Collider[0];
                enabledColliders = new bool[localColliders.Length];
                for (int i = 0; i < localColliders.Length; ++i) enabledColliders[i] = localColliders[i].enabled;
                wearParts = action == ForceAction.Move ? root.GetComponentsInChildren<WearNTear>(true) : new WearNTear[0];
                supportFlags = new bool[wearParts.Length]; roofFlags = new bool[wearParts.Length];
                for (int i = 0; i < wearParts.Length; ++i) { supportFlags[i] = wearParts[i].m_noSupportWear; roofFlags[i] = wearParts[i].m_noRoofWear; }
                Path = view == null ? String.Empty : StaticPath(view.transform, root.transform);
                if (action == ForceAction.Move)
                    foreach (ZNetView node in root.GetComponentsInChildren<ZNetView>(true)) {
                        // Inactive prefab variants may have a view which has never Awoken;
                        // they have no persistent ID and move with their parent unchanged.
                        if (!node.IsValid()) continue;
                        Nodes.Add(new MoveNode(node));
                    }
                else if (view != null && view.IsValid()) Nodes.Add(new MoveNode(view));
            }

            internal bool SameIdentity(ForceEntry other)
            { return ReferenceEquals(Root, other.Root) && ReferenceEquals(View, other.View) && Action == other.Action && Path == other.Path && ReferenceEquals(OnlyCollider, other.OnlyCollider); }

            internal void Check(bool staged)
            {
                if (Root == null || Root.transform.lossyScale != Scale) Changed();
                if (!staged && (Root.transform.position != Position || Root.transform.rotation != Rotation || Root.activeSelf != Active)) Changed();
                if (staged && !Staged) Changed();
                if (staged && Action == ForceAction.Move && Root.transform.position != Destination) Changed();
                if (staged && (Action == ForceAction.Remove || Action == ForceAction.StaticPart) && Root.activeSelf) Changed();
                if (staged && Action == ForceAction.StaticCollider)
                    foreach (Collider collider in localColliders) if (!collider.isTrigger && !IsViewBlock(collider) && collider.enabled) Changed();
                foreach (MoveNode node in Nodes) node.Check(staged);
            }

            internal void Claim() { foreach (MoveNode node in Nodes) node.Claim(); }
            internal void Stage()
            {
                Staged = true;
                if (Action == ForceAction.Move) {
                    Vector3 delta = Destination - Position;
                    Root.transform.SetParent(null, true);
                    Root.transform.position = Destination;
                    foreach (MoveNode node in Nodes) node.Move(delta);
                    foreach (WearNTear wear in Root.GetComponentsInChildren<WearNTear>(true)) { wear.m_noSupportWear = true; wear.m_noRoofWear = true; ClearWearSupport(wear); }
                }
                else if (Action == ForceAction.StaticCollider) {
                    foreach (Collider collider in localColliders) if (!collider.isTrigger && !IsViewBlock(collider)) collider.enabled = false;
                }
                else Root.SetActive(false);
            }

            internal void Restore()
            {
                if (Root == null) throw new InvalidOperationException("Объект исчез до отмены расчистки.");
                if (Action == ForceAction.Move) {
                    Root.transform.SetParent(Parent, true); Root.transform.SetPositionAndRotation(Position, Rotation);
                    foreach (MoveNode node in Nodes) node.Restore();
                    for (int i = 0; i < wearParts.Length; ++i) if (wearParts[i] != null) {
                        wearParts[i].m_noSupportWear = supportFlags[i]; wearParts[i].m_noRoofWear = roofFlags[i]; ClearWearSupport(wearParts[i]);
                    }
                }
                else {
                    Root.SetActive(Active);
                    for (int i = 0; i < localColliders.Length; ++i) if (localColliders[i] != null) localColliders[i].enabled = enabledColliders[i];
                    foreach (MoveNode node in Nodes) node.RestoreOwner();
                }
                Staged = false;
            }
            private static void Changed() { throw new InvalidOperationException("Объект изменился до завершения расчистки."); }
        }

        internal sealed class MoveNode
        {
            internal readonly ZNetView View;
            internal readonly ZDO Zdo;
            internal readonly ZDOID Id;
            internal readonly Vector3 Position, Velocity, AngularVelocity;
            internal readonly Quaternion Rotation;
            internal readonly long Owner;
            internal readonly uint Revision;
            private uint stagedRevision;
            internal readonly Rigidbody Body;
            private readonly WearNTear wear;
            private readonly bool noSupport, noRoof;
            internal MoveNode(ZNetView view)
            {
                View = view; Zdo = view.GetZDO(); Id = Zdo.m_uid; Owner = Zdo.GetOwner(); Revision = Zdo.DataRevision;
                Position = view.transform.position; Rotation = view.transform.rotation; Body = view.GetComponent<Rigidbody>();
                Velocity = Body == null ? Vector3.zero : Body.linearVelocity; AngularVelocity = Body == null ? Vector3.zero : Body.angularVelocity;
                wear = view.GetComponent<WearNTear>(); noSupport = wear != null && wear.m_noSupportWear; noRoof = wear != null && wear.m_noRoofWear;
            }
            internal void Check(bool staged)
            {
                if (View == null || !View.IsValid() || !ReferenceEquals(View.GetZDO(), Zdo) ||
                    !ReferenceEquals(ZDOMan.instance.GetZDO(Id), Zdo) || Zdo.m_uid != Id ||
                    (!staged && Zdo.DataRevision != Revision) || (staged && Zdo.DataRevision != stagedRevision))
                    throw new InvalidOperationException("Сетевой объект площадки изменился.");
                if (staged && (!View.IsOwner() || !Zdo.IsOwner())) throw new InvalidOperationException("Хост потерял управление объектом расчистки.");
            }
            internal void Claim() { View.ClaimOwnership(); if (!View.IsOwner() || !Zdo.IsOwner()) throw new InvalidOperationException("Не удалось получить управление объектом расчистки."); }
            internal void RecordStagedRevision() { stagedRevision = Zdo.DataRevision; }
            internal void Move(Vector3 delta)
            {
                ApplyPose(View.gameObject, Position + delta, Rotation);
                Zdo.SetPosition(Position + delta); Zdo.SetRotation(Rotation);
                ZSyncTransform sync = View.GetComponent<ZSyncTransform>(); if (sync != null) sync.SyncNow();
            }
            internal void Restore()
            {
                ApplyPose(View.gameObject, Position, Rotation); Zdo.SetPosition(Position); Zdo.SetRotation(Rotation);
                if (Body != null && !Body.isKinematic) { Body.linearVelocity = Velocity; Body.angularVelocity = AngularVelocity; }
                if (wear != null) { wear.m_noSupportWear = noSupport; wear.m_noRoofWear = noRoof; ClearWearSupport(wear); }
                RestoreOwner();
            }
            internal void RestoreOwner() { if (View != null && View.IsValid() && ReferenceEquals(View.GetZDO(), Zdo)) Zdo.SetOwner(Owner); }
        }

        internal sealed class ForceTransaction : IDisposable
        {
            private readonly ForceSite site;
            private readonly List<ForceEntry> claimed = new List<ForceEntry>();
            private bool committed, disposed;
            private byte[] remote = new byte[0];
            private Dictionary<ZDO, byte[]> preparedMasks = new Dictionary<ZDO, byte[]>();
            internal bool Committed { get { return committed; } }
            internal byte[] RemotePayload { get { return (byte[])remote.Clone(); } }
            internal ForceTransaction(ForceSite site) { this.site = site; }
            internal void Apply()
            {
                RequireWorld(site.Network, site.World, site.Scene);
                foreach (ForceEntry entry in site.Entries) { entry.Check(false); claimed.Add(entry); entry.Claim(); }
                foreach (ForceEntry entry in site.Entries) entry.Stage();
                foreach (ForceEntry entry in site.Entries) foreach (MoveNode node in entry.Nodes) node.RecordStagedRevision();
                Physics.SyncTransforms(); ValidateCommit();
            }
            internal void ValidateCommit()
            {
                RequireWorld(site.Network, site.World, site.Scene);
                if (disposed || committed) throw new InvalidOperationException("Расчистка уже завершена.");
                foreach (ForceEntry entry in site.Entries) entry.Check(true);
                // Capacity and persisted-marker decoding are checked before the caller
                // saves its new region. They cannot first fail after durable creation.
                remote = MakePayload(site);
                preparedMasks = PrepareMasks(site.Entries);
            }
            internal void Commit()
            {
                ValidateCommit();
                committed = true;
                try {
                    foreach (var mask in preparedMasks) { mask.Key.Set(StaticMaskKey, mask.Value); ZDOMan.instance.ForceSendZDO(mask.Key.m_uid); }
                    foreach (ForceEntry entry in site.Entries) {
                        if (entry.Action == ForceAction.Move)
                            foreach (MoveNode node in entry.Nodes) { node.Zdo.Set(StorageMoveKey, true); ZDOMan.instance.ForceSendZDO(node.Id); }
                    }
                    foreach (ForceEntry entry in site.Entries) if (entry.Action == ForceAction.Remove) site.Scene.Destroy(entry.Root);
                    FlushDestroyedObjects();
                    foreach (ForceEntry entry in site.Entries)
                        if (entry.Action == ForceAction.Remove && ZDOMan.instance.GetZDO(entry.Nodes[0].Id) != null)
                            throw new InvalidOperationException("Не завершено сетевое удаление объекта: " + entry.Nodes[0].Id);
                }
                catch { FlushDestroyedObjects(); throw; }
                Physics.SyncTransforms();
            }
            public void Dispose()
            {
                if (disposed) return; disposed = true;
                if (committed) return;
                RequireWorld(site.Network, site.World, site.Scene);
                var errors = new List<Exception>();
                for (int i = claimed.Count - 1; i >= 0; --i) try { claimed[i].Restore(); } catch (Exception error) { errors.Add(error); }
                Physics.SyncTransforms();
                if (errors.Count != 0) throw new InvalidOperationException("Не удалось полностью отменить расчистку.", new AggregateException(errors));
            }
        }

        private static void OccupiedPlayer()
        { throw new InvalidOperationException("На площадке находится игрок. Игрок должен выйти за её границы; удалять игроков нельзя."); }

        private static bool PrisonMarker(ZDO zdo)
        { return zdo.GetBool(ArenaBuilder.ProtectedKey, false) || zdo.GetBool(ArenaBuilder.MobKey, false) || zdo.GetBool(ArenaBuilder.CustodyKey, false) || zdo.GetBool(ArenaBuilder.ArmoryKey, false); }

        private static bool StaticParent(GameObject root)
        { return root.GetComponent<LocationProxy>() != null || root.GetComponent<Location>() != null || root.GetComponent<DungeonGenerator>() != null; }

        private static bool PreserveRoot(GameObject root, ZDO zdo)
        {
            if (root.GetComponent<ItemDrop>() != null || root.GetComponentInChildren<Container>(true) != null) return true;
            Character character = root.GetComponent<Character>();
            if (character != null) {
                Tameable tame = root.GetComponent<Tameable>();
                if (character.IsBoss() || character.IsTamed() || zdo.GetBool(ZDOVars.s_tamed, false) || tame != null && tame.m_startsTamed ||
                    character.GetFaction() == Character.Faction.Players || character.GetFaction() == Character.Faction.Dverger ||
                    character.GetFaction() == Character.Faction.PlayerSpawned || character.GetFaction() == Character.Faction.TrainingDummy) return true;
            }
            byte[] items = zdo.GetByteArray(ZDOVars.s_items, null);
            if (items != null && items.Length != 0 || !String.IsNullOrEmpty(zdo.GetString(ZDOVars.s_items, String.Empty))) return true;
            foreach (Component component in root.GetComponentsInChildren<Component>(true))
                if (component != null && (StoredResourceTypes.Contains(component.GetType().Name) || HasInventory(component.GetType(), character != null))) return true;
            return false;
        }

        private static Bounds PhysicalBounds(GameObject root)
        {
            bool found = false; Bounds bounds = new Bounds(root.transform.position, Vector3.zero);
            foreach (Collider collider in root.GetComponentsInChildren<Collider>(true)) {
                if (collider == null || collider.isTrigger || !collider.enabled || !collider.gameObject.activeInHierarchy || IsViewBlock(collider)) continue;
                if (!Finite(collider.bounds.center) || !Finite(collider.bounds.size)) throw new InvalidOperationException("Не удалось прочитать физические границы объекта.");
                if (!found) { bounds = collider.bounds; found = true; } else bounds.Encapsulate(collider.bounds);
            }
            return bounds;
        }

        private static void ApplyPose(GameObject root, Vector3 position, Quaternion rotation)
        {
            root.transform.SetPositionAndRotation(position, rotation);
            Rigidbody body = root.GetComponent<Rigidbody>();
            if (body != null) {
                body.position = position; body.rotation = rotation;
                if (!body.isKinematic) { body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
            }
            foreach (WearNTear wear in root.GetComponentsInChildren<WearNTear>(true)) { wear.m_noSupportWear = true; wear.m_noRoofWear = true; ClearWearSupport(wear); }
        }
        private static void ClearWearSupport(WearNTear wear) { if (ClearSupport != null) ClearSupport.Invoke(wear, null); }

        private static string StaticPath(Transform parent, Transform child)
        {
            var path = new List<string>();
            for (Transform current = child; current != null && current != parent; current = current.parent) {
                Vector3 point = current.localPosition;
                path.Add(current.GetSiblingIndex().ToString(CultureInfo.InvariantCulture) + ":" +
                    Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(current.name)).Replace('+', '-').Replace('/', '_') + ":" +
                    point.x.ToString("R", CultureInfo.InvariantCulture) + "," + point.y.ToString("R", CultureInfo.InvariantCulture) + "," + point.z.ToString("R", CultureInfo.InvariantCulture));
            }
            path.Reverse(); return String.Join("/", path.ToArray());
        }

        private static Transform ResolvePath(Transform parent, string path)
        {
            if (String.IsNullOrEmpty(path)) return parent;
            Transform current = parent;
            foreach (string part in path.Split('/')) {
                string[] fields = part.Split(':'); int index;
                if (fields.Length < 2 || fields.Length > 3 || !Int32.TryParse(fields[0], out index) || index < 0) return null;
                string name = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(fields[1].Replace('-', '+').Replace('_', '/')));
                Vector3 position = Vector3.zero; bool pose = fields.Length == 3;
                if (pose) {
                    string[] components = fields[2].Split(',');
                    if (components.Length != 3 || !Single.TryParse(components[0], NumberStyles.Float, CultureInfo.InvariantCulture, out position.x) ||
                        !Single.TryParse(components[1], NumberStyles.Float, CultureInfo.InvariantCulture, out position.y) ||
                        !Single.TryParse(components[2], NumberStyles.Float, CultureInfo.InvariantCulture, out position.z) || !Finite(position)) return null;
                }
                Transform next = index < current.childCount ? current.GetChild(index) : null;
                if (next == null || next.name != name || pose && (next.localPosition - position).sqrMagnitude > .0004f) {
                    next = null;
                    for (int i = 0; i < current.childCount; ++i) {
                        Transform candidate = current.GetChild(i);
                        if (candidate.name != name || pose && (candidate.localPosition - position).sqrMagnitude > .0004f) continue;
                        if (next != null) return null; // Never guess between equally plausible static subtrees.
                        next = candidate;
                    }
                    if (next == null) return null;
                }
                current = next;
            }
            return current;
        }

        private static List<string> ReadMasks(ZDO zdo)
        {
            byte[] bytes = zdo.GetByteArray(StaticMaskKey, null); var paths = new List<string>();
            if (bytes == null || bytes.Length == 0) return paths;
            if (bytes.Length > MaximumRemoteBytes) throw new InvalidDataException("Static clearance mask too large.");
            var package = new ZPackage(bytes); if (package.ReadInt() != 1) throw new InvalidDataException("Unknown static clearance mask.");
            int count = package.ReadInt(); if (count < 0 || count > ForceMaximumRoots) throw new InvalidDataException("Invalid static clearance mask count.");
            for (int i = 0; i < count; ++i) { string path = package.ReadString(); if (path.Length > 8192) throw new InvalidDataException("Static clearance path too long."); paths.Add(path); }
            if (package.GetPos() != package.Size()) throw new InvalidDataException("Static clearance mask has trailing bytes.");
            return paths;
        }

        private static Dictionary<ZDO, byte[]> PrepareMasks(List<ForceEntry> entries)
        {
            var grouped = new Dictionary<ZDO, List<string>>(); var result = new Dictionary<ZDO, byte[]>();
            foreach (ForceEntry entry in entries) {
                if ((entry.Action != ForceAction.StaticPart && entry.Action != ForceAction.StaticCollider) || entry.View == null || !entry.View.IsValid()) continue;
                ZDO zdo = entry.View.GetZDO(); List<string> paths;
                if (!grouped.TryGetValue(zdo, out paths)) { paths = ReadMasks(zdo); grouped.Add(zdo, paths); }
                string path = (entry.Action == ForceAction.StaticCollider ? "C:" : "P:") + entry.Path;
                if (path.Length > 8192) throw new InvalidDataException("Static clearance path capacity exceeded.");
                if (!paths.Contains(path)) paths.Add(path);
                if (paths.Count > ForceMaximumRoots) throw new InvalidDataException("Static clearance path count exceeded.");
            }
            foreach (var group in grouped) {
                var package = new ZPackage(); package.Write(1); package.Write(group.Value.Count); foreach (string path in group.Value) package.Write(path);
                if (package.Size() > MaximumRemoteBytes) throw new InvalidDataException("Static clearance mask capacity exceeded.");
                result.Add(group.Key, package.GetArray());
            }
            return result;
        }

        internal static void ReapplyStatic(GameObject root)
        {
            if (root == null || ZNet.instance == null) return;
            ZNetView view = root.GetComponent<ZNetView>();
            if (view != null && view.IsValid()) {
                ZDO zdo = view.GetZDO();
                if (zdo.GetBool(StorageMoveKey, false))
                    foreach (WearNTear wear in root.GetComponentsInChildren<WearNTear>(true)) { wear.m_noSupportWear = true; wear.m_noRoofWear = true; }
                ApplyStaticMasks(root, ReadMasks(zdo));
                if (staticRegion != null && !regionApplied && PrisonMarker(zdo)) { regionApplied = true; ReapplyCurrentStaticRegion(); }
            }
        }

        internal static void ReapplyCurrentStaticRegion()
        {
            if (staticRegion == null || staticExtent < 12f || staticExtent > 30f || ZNetScene.instance == null ||
                ZNet.instance == null || staticRegionWorld != ZNet.instance.GetWorldUID()) return;
            Vector3 center = Plugin.Vector(staticRegion.Center);
            // Virtual dungeon rooms share surface X/Z but live thousands of metres
            // above it. A replay concerns only the actual prison's vertical volume.
            float halfHeight = Mathf.Clamp((float)staticRegion.HalfHeight, 8f, 16f);
            foreach (Collider collider in Physics.OverlapBox(center, new Vector3(staticExtent, halfHeight, staticExtent), Quaternion.identity,
                CollisionMask, QueryTriggerInteraction.Ignore)) {
                if (collider == null || collider.GetComponentInParent<Heightmap>() != null || collider.GetComponentInParent<Player>() != null) continue;
                ZNetView view = collider.GetComponentInParent<ZNetView>();
                if (view != null && view.IsValid() && !StaticParent(view.gameObject)) continue;
                if (collider.GetComponent<Location>() != null || collider.GetComponent<LocationProxy>() != null || collider.GetComponent<DungeonGenerator>() != null)
                    collider.enabled = false;
                else collider.gameObject.SetActive(false);
            }
        }

        internal static void ReapplyZone(GameObject root)
        {
            if (root == null || staticRegion == null || ZNet.instance == null || staticRegionWorld != ZNet.instance.GetWorldUID()) return;
            Heightmap map = root.GetComponentInChildren<Heightmap>();
            float half = map == null ? 32f : map.m_width * map.m_scale * .5f;
            if (Mathf.Abs(root.transform.position.x - (float)staticRegion.Center.X) > half + staticExtent ||
                Mathf.Abs(root.transform.position.z - (float)staticRegion.Center.Z) > half + staticExtent) return;
            ReapplyCurrentStaticRegion();
        }
        internal static void ApplyStaticMasks(GameObject root, IEnumerable<string> masks)
        {
            foreach (string value in masks) {
                if (value == null || value.Length < 2) continue;
                Transform part = ResolvePath(root.transform, value.Substring(2));
                if (part == null || part.GetComponentInChildren<Player>(true) != null) continue;
                if (value.StartsWith("C:", StringComparison.Ordinal)) DisableSolidColliders(part);
                else if (value.StartsWith("P:", StringComparison.Ordinal) && part != root.transform) part.gameObject.SetActive(false);
            }
        }

        private static byte[] MakePayload(ForceSite site)
        {
            var moves = new List<MoveNode>(); var statics = new List<ForceEntry>();
            foreach (ForceEntry entry in site.Entries) {
                if (entry.Action == ForceAction.Move) moves.AddRange(entry.Nodes);
                else if (entry.Action == ForceAction.StaticPart || entry.Action == ForceAction.StaticCollider) statics.Add(entry);
            }
            if (moves.Count > ForceMaximumRoots || statics.Count > ForceMaximumRoots) throw new InvalidDataException("Force clearance packet object count exceeded.");
            var package = new ZPackage(); package.Write(1); package.Write(site.World);
            package.Write(site.Origin); package.Write(site.Extent); package.Write(site.Low); package.Write(site.High);
            package.Write(moves.Count);
            foreach (MoveNode node in moves) { package.Write(node.Id); package.Write(node.View.transform.position); package.Write(node.View.transform.rotation); }
            package.Write(statics.Count);
            foreach (ForceEntry entry in statics) {
                package.Write(entry.View == null ? ZDOID.None : entry.View.GetZDO().m_uid);
                package.Write(entry.Path); package.Write(entry.Action == ForceAction.StaticCollider);
            }
            if (package.Size() > MaximumRemoteBytes) throw new InvalidDataException("Force clearance packet capacity exceeded.");
            return package.GetArray();
        }

        // Called only after Plugin authenticates the PrisonWire message as coming from the server.
        public static void ApplyRemote(byte[] bytes)
        {
            CheckThread();
            if (bytes == null || bytes.Length > MaximumRemoteBytes || ZNet.instance == null || ZNetScene.instance == null) throw new InvalidDataException("Invalid clearance packet.");
            var package = new ZPackage(bytes); if (package.ReadInt() != 1 || package.ReadLong() != ZNet.instance.GetWorldUID()) throw new InvalidDataException("Clearance packet belongs to another world.");
            Vector3 origin = package.ReadVector3(); float extent = package.ReadSingle(), low = package.ReadSingle(), high = package.ReadSingle();
            if (!Finite(origin) || !Finite(extent) || extent < 12f || extent > 30f || !Finite(low) || !Finite(high) || low < -10000f || high > 10000f || low >= high) throw new InvalidDataException("Invalid clearance bounds.");
            int count = package.ReadInt(); if (count < 0 || count > ForceMaximumRoots) throw new InvalidDataException("Invalid move count.");
            var ids = new List<ZDOID>(); var positions = new List<Vector3>(); var rotations = new List<Quaternion>();
            for (int i = 0; i < count; ++i) {
                ZDOID id = package.ReadZDOID(); Vector3 position = package.ReadVector3(); Quaternion rotation = package.ReadQuaternion();
                if (!Finite(position) || !Finite(rotation.x) || !Finite(rotation.y) || !Finite(rotation.z) || !Finite(rotation.w)) throw new InvalidDataException("Invalid move position.");
                ids.Add(id); positions.Add(position); rotations.Add(rotation);
            }
            int staticCount = package.ReadInt(); if (staticCount < 0 || staticCount > ForceMaximumRoots) throw new InvalidDataException("Invalid static count.");
            var parents = new List<ZDOID>(); var paths = new List<string>(); var colliderOnly = new List<bool>();
            for (int i = 0; i < staticCount; ++i) {
                parents.Add(package.ReadZDOID()); string path = package.ReadString(); if (path.Length > 8192) throw new InvalidDataException("Invalid static path."); paths.Add(path); colliderOnly.Add(package.ReadBool());
            }
            if (package.GetPos() != package.Size()) throw new InvalidDataException("Trailing clearance bytes.");
            // No object mutation occurs until the entire authenticated packet has parsed.
            var roots = new List<GameObject>(); var movedTransforms = new HashSet<Transform>();
            for (int i = 0; i < count; ++i) {
                GameObject root = ZNetScene.instance.FindInstance(ids[i]);
                if (root != null && root.GetComponentInChildren<Player>(true) != null) root = null;
                roots.Add(root); if (root != null) movedTransforms.Add(root.transform);
            }
            for (int i = 0; i < count; ++i) {
                GameObject root = roots[i]; if (root == null) continue;
                bool movedAncestor = false;
                for (Transform parent = root.transform.parent; parent != null; parent = parent.parent)
                    if (movedTransforms.Contains(parent)) { movedAncestor = true; break; }
                if (!movedAncestor) root.transform.SetParent(null, true);
                ApplyPose(root, positions[i], rotations[i]);
            }
            for (int i = 0; i < staticCount; ++i) {
                GameObject root = ZNetScene.instance.FindInstance(parents[i]); if (root == null) continue;
                Transform part = ResolvePath(root.transform, paths[i]); if (part == null || part.GetComponentInChildren<Player>(true) != null) continue;
                if (colliderOnly[i]) DisableSolidColliders(part);
                else if (part != root.transform) part.gameObject.SetActive(false);
            }
            // Non-network static geometry has no ZDO/path; the authenticated footprint is its bounded scope.
            Vector3 center = new Vector3(origin.x, (low + high) * .5f, origin.z);
            foreach (Collider collider in Physics.OverlapBox(center, new Vector3(extent, (high - low) * .5f, extent), Quaternion.identity, CollisionMask, QueryTriggerInteraction.Ignore)) {
                if (collider == null || collider.GetComponentInParent<Heightmap>() != null || collider.GetComponentInParent<ZNetView>() != null || collider.GetComponentInParent<Player>() != null) continue;
                collider.gameObject.SetActive(false);
            }
            Physics.SyncTransforms();
        }
        private static void DisableSolidColliders(Transform part)
        { foreach (Collider collider in part.GetComponents<Collider>()) if (!collider.isTrigger && !IsViewBlock(collider)) collider.enabled = false; }
    }

    [HarmonyPatch(typeof(ZNetScene), "CreateObject")]
    internal static class ForceClearanceSceneLoad
    { private static void Postfix(GameObject __result) { SiteClearer.ReapplyStatic(__result); } }

    [HarmonyPatch(typeof(LocationProxy), "SpawnLocation")]
    internal static class ForceClearanceLocationLoad
    { private static void Postfix(LocationProxy __instance) { SiteClearer.ReapplyStatic(__instance.gameObject); SiteClearer.ReapplyCurrentStaticRegion(); } }

    [HarmonyPatch(typeof(ZoneSystem), "SpawnZone")]
    internal static class ForceClearanceZoneLoad
    { private static void Postfix(bool __result, GameObject root) { if (__result) SiteClearer.ReapplyZone(root); } }

    [HarmonyPatch]
    internal static class ForceClearanceDungeonLoad
    {
        private static IEnumerable<MethodBase> TargetMethods()
        { return typeof(DungeonGenerator).GetMethods(BindingFlags.Instance | BindingFlags.Public).WhereGenerate(); }
        private static void Postfix(DungeonGenerator __instance)
        {
            ZNetView view = __instance.GetComponentInParent<ZNetView>();
            if (view != null && view.IsValid()) SiteClearer.ReapplyStatic(view.gameObject);
        }
    }

    internal static class ForceClearanceMethodSelection
    {
        internal static IEnumerable<MethodBase> WhereGenerate(this IEnumerable<MethodInfo> methods)
        { foreach (MethodInfo method in methods) if (method.Name == "Generate") yield return method; }
    }
}
