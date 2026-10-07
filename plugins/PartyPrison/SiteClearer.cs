using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace ValheimModPack.PartyPrison
{
    /// <summary>Bounded manual construction cleanup. Planning is read-only; removal follows successful building.</summary>
    public static partial class SiteClearer
    {
        public const int MaximumObjects = 512;
        private const int MaximumColliders = 8192;
        private static readonly int ViewBlockMask = LayerMask.GetMask("viewblock");
        public static int CollisionMask { get { return ~ViewBlockMask; } }
        private static bool IsViewBlock(Collider collider)
        { return (ViewBlockMask & (1 << collider.gameObject.layer)) != 0; }
        private static int unityThread;
        private static readonly MethodInfo FlushDestroyed = typeof(ZDOMan).GetMethod("SendDestroyed", BindingFlags.Instance | BindingFlags.NonPublic,
            null, Type.EmptyTypes, null);
        private static readonly Dictionary<Type, bool> InventoryTypes = new Dictionary<Type, bool>();
        private static readonly Dictionary<Type, bool> WildInventoryTypes = new Dictionary<Type, bool>();
        private static readonly HashSet<string> StoredResourceTypes = new HashSet<string>(StringComparer.Ordinal) {
            "Container", "ItemStand", "ArmorStand", "Fermenter", "Smelter", "CookingStation", "Beehive",
            "SapCollector", "Turret", "ShieldGenerator", "Incinerator"
        };
        private static readonly HashSet<string> ProtectedWorldTypes = new HashSet<string>(StringComparer.Ordinal) {
            "BossStone", "OfferingBowl", "RuneStone", "Vegvisir", "DungeonGenerator", "LocationProxy",
            "SpawnArea", "CreatureSpawner", "Trader", "Raven", "Teleport", "DungeonEntrance", "Room"
        };

        public static Site Plan(Vector3 origin, Quaternion rotation, float extent, float low, float high,
            Vector3 altar, bool clearPlayerStructures)
        {
            RequireHost();
            if (!Finite(origin) || !Finite(altar) || !Finite(extent) || extent < 12f || extent > 30f ||
                !Finite(low) || !Finite(high) || high <= low || high - low > 40f ||
                !Finite(rotation.x) || !Finite(rotation.y) || !Finite(rotation.z) || !Finite(rotation.w))
                throw new ArgumentException("Неверные границы очистки площадки под тюрьму.");
            Quaternion yaw = Quaternion.Euler(0f, rotation.eulerAngles.y, 0f);
            Vector3 boxCenter = new Vector3(origin.x, (low + high) * 0.5f, origin.z);
            Vector3 boxHalf = new Vector3(extent, (high - low) * 0.5f, extent);
            Bounds footprint = RotatedBounds(boxCenter, boxHalf, yaw);
            RequireOutsideAltar(footprint, altar);
            // Include pickup triggers so loose belongings cannot be buried by the new floor.
            // Other trigger volumes (for example AI detection ranges) are not solid obstacles.
            Collider[] overlap = Physics.OverlapBox(boxCenter, boxHalf, yaw, CollisionMask, QueryTriggerInteraction.Collide);
            if (overlap.Length > MaximumColliders)
                throw new InvalidOperationException("На площадке слишком много объектов для безопасной очистки.");
            Dictionary<GameObject, Entry> selected = new Dictionary<GameObject, Entry>();
            foreach (Collider collider in overlap) {
                if (collider == null || collider.GetComponentInParent<Heightmap>() != null) continue;
                Character actor = collider.GetComponentInParent<Character>();
                ItemDrop loose = collider.GetComponentInParent<ItemDrop>();
                if (loose != null && actor == null && loose.GetComponentInParent<Piece>() == null && loose.GetComponentInParent<Container>() == null) {
                    if (!collider.isTrigger || ActualItemOverlap(loose, boxCenter, boxHalf, yaw))
                        throw new InvalidOperationException("На площадке лежат вещи. Соберите их или выберите другое место для тюрьмы.");
                    continue;
                }
                if (collider.isTrigger) continue;
                if (collider.GetComponentInParent<Player>() != null)
                    throw new InvalidOperationException("На площадке находится игрок. Выберите другое место или освободите площадку.");
                ZNetView view = actor == null ? collider.GetComponentInParent<ZNetView>() : actor.GetComponent<ZNetView>();
                if (view == null || !view.IsValid())
                    throw new InvalidOperationException("На площадке есть объект, который нельзя безопасно удалить из мира: " + collider.gameObject.name);
                GameObject root = view.gameObject;
                if (selected.ContainsKey(root)) continue;
                Classify(root, view, clearPlayerStructures);
                Bounds bounds = SolidBounds(root);
                RequireOutsideAltar(bounds, altar);
                RequireBoundedRoot(root, bounds, origin, yaw, extent);
                selected.Add(root, new Entry(root, view, bounds));
                if (selected.Count > MaximumObjects)
                    throw new InvalidOperationException("Очистка площадки затрагивает более 512 объектов. Выберите менее застроенное место.");
            }
            List<Entry> entries = new List<Entry>(selected.Values);
            entries.Sort(delegate(Entry a, Entry b) { return String.CompareOrdinal(a.Id.ToString(), b.Id.ToString()); });
            return new Site(origin, yaw, extent, low, high, altar, clearPlayerStructures, entries,
                ZNet.instance, ZNet.instance.GetWorldUID(), ZNetScene.instance);
        }

        public sealed class Site
        {
            private readonly ForceSite force;
            private readonly Vector3 origin, altar;
            private readonly Quaternion rotation;
            private readonly float extent, low, high;
            private readonly bool clearPlayerStructures;
            private readonly List<Entry> entries;
            private readonly ZNet network;
            private readonly long world;
            private readonly ZNetScene scene;
            private bool applied;
            public int Count { get { return force == null ? entries.Count : force.Count; } }
            public int Cost { get { return Count; } }
            internal Site(ForceSite force) { this.force = force; }

            internal Site(Vector3 origin, Quaternion rotation, float extent, float low, float high, Vector3 altar,
                bool clearPlayerStructures, List<Entry> entries, ZNet network, long world, ZNetScene scene)
            {
                this.origin = origin; this.rotation = rotation; this.extent = extent; this.low = low; this.high = high;
                this.altar = altar; this.clearPlayerStructures = clearPlayerStructures; this.entries = entries;
                this.network = network; this.world = world; this.scene = scene;
            }

            public Transaction Apply()
            {
                if (force != null) return force.Apply();
                RequireWorld(network, world, scene);
                if (applied) throw new InvalidOperationException("Эта очистка площадки уже была выполнена.");
                // Re-enumerate just this footprint: new obstacles cannot slip between inspection and staging.
                Site current = Plan(origin, rotation, extent, low, high, altar, clearPlayerStructures);
                if (current.entries.Count != entries.Count) Changed();
                HashSet<GameObject> expected = new HashSet<GameObject>();
                foreach (Entry entry in entries) { entry.Check(false); expected.Add(entry.Root); }
                foreach (Entry entry in current.entries) if (!expected.Contains(entry.Root)) Changed();
                applied = true;
                Transaction transaction = new Transaction(entries, clearPlayerStructures, network, world, scene);
                try { transaction.Apply(); return transaction; }
                catch (Exception error) {
                    try { transaction.Dispose(); }
                    catch (Exception rollback) {
                        throw new InvalidOperationException("Не удалось временно освободить площадку и полностью вернуть её объекты. " +
                            rollback.Message, new AggregateException(error, rollback));
                    }
                    throw new InvalidOperationException("Не удалось освободить площадку. Её объекты возвращены. " + error.Message, error);
                }
            }
            private static void Changed() { throw new InvalidOperationException("Объекты на площадке изменились. Заново выберите место для тюрьмы."); }
        }

        public sealed class Transaction : IDisposable
        {
            private readonly ForceTransaction force;
            private readonly List<Entry> entries;
            private readonly bool clearPlayerStructures;
            private readonly ZNet network;
            private readonly long world;
            private readonly ZNetScene scene;
            private readonly List<Entry> claimed = new List<Entry>();
            private bool committed, disposed;
            public bool Committed { get { return force == null ? committed : force.Committed; } }
            public byte[] RemotePayload { get { return force == null ? new byte[0] : force.RemotePayload; } }
            internal Transaction(ForceTransaction force) { this.force = force; }

            internal Transaction(List<Entry> entries, bool clearPlayerStructures, ZNet network, long world, ZNetScene scene)
            { this.entries = entries; this.clearPlayerStructures = clearPlayerStructures; this.network = network; this.world = world; this.scene = scene; }

            internal void Apply()
            {
                if (force != null) { force.Apply(); return; }
                RequireWorld(network, world, scene);
                // Claim all objects before disabling the first one, preserving every original owner for rollback.
                foreach (Entry entry in entries) {
                    entry.Check(false); Classify(entry.Root, entry.View, clearPlayerStructures);
                    claimed.Add(entry); entry.Claim();
                }
                foreach (Entry entry in entries) { entry.Staged = true; entry.Root.SetActive(false); }
                Physics.SyncTransforms();
                ValidateCommit();
            }

            /// <summary>Call before saving the new prison region, while all work is still reversible.</summary>
            public void ValidateCommit()
            {
                if (force != null) { force.ValidateCommit(); return; }
                RequireWorld(network, world, scene);
                if (disposed || committed) throw new InvalidOperationException("Очистка площадки уже завершена.");
                foreach (Entry entry in entries) {
                    entry.Check(true); Classify(entry.Root, entry.View, clearPlayerStructures);
                    if (!entry.View.IsOwner() || !entry.Zdo.IsOwner())
                        throw new InvalidOperationException("Хост потерял управление объектом площадки до завершения строительства.");
                }
            }

            public void Commit()
            {
                if (force != null) { force.Commit(); return; }
                ValidateCommit();
                // Native destruction is irreversible. Never roll the prison back after the first queued deletion.
                committed = true;
                int removed = 0;
                try {
                    foreach (Entry entry in entries) { scene.Destroy(entry.Root); ++removed; }
                    FlushDestroyedObjects();
                    // Destroy queues a network deletion. Verify the captured IDs disappeared before the world is saved.
                    foreach (Entry entry in entries)
                        if (ZDOMan.instance.GetZDO(entry.Id) != null)
                            throw new InvalidOperationException("Не удалось завершить сетевое удаление объекта площадки: " + entry.Id);
                    Physics.SyncTransforms();
                }
                catch (Exception error) {
                    List<Exception> errors = new List<Exception>(); errors.Add(error);
                    // A failure partway through Destroy must still publish deletions already queued by native code.
                    try { FlushDestroyedObjects(); } catch (Exception flush) { errors.Add(flush); }
                    removed = 0;
                    int restored = 0;
                    foreach (Entry entry in entries) {
                        try {
                            if (ZDOMan.instance.GetZDO(entry.Id) == null) { ++removed; continue; }
                            if (entry.Root == null || entry.View == null || !entry.View.IsValid() ||
                                !ReferenceEquals(entry.View.GetZDO(), entry.Zdo)) continue;
                            entry.Restore(); ++restored;
                        }
                        catch (Exception restore) { errors.Add(restore); }
                    }
                    try { Physics.SyncTransforms(); } catch (Exception sync) { errors.Add(sync); }
                    throw new InvalidOperationException("Тюрьма создана, но очистка площадки завершилась частично: удалено " +
                        removed + " из " + entries.Count + ", возвращено оставшихся объектов " + restored + ". " +
                        "Проверьте площадку перед использованием тюрьмы. " + error.Message, new AggregateException(errors));
                }
            }

            public void Dispose()
            {
                if (force != null) { force.Dispose(); return; }
                if (disposed) return;
                CheckThread();
                if (committed) { disposed = true; return; }
                RequireWorld(network, world, scene);
                List<Exception> errors = new List<Exception>();
                foreach (Entry entry in claimed) {
                    try { entry.Restore(); }
                    catch (Exception error) { errors.Add(error); }
                }
                try { Physics.SyncTransforms(); } catch (Exception error) { errors.Add(error); }
                disposed = true;
                if (errors.Count != 0)
                    throw new InvalidOperationException("Не удалось полностью вернуть объекты площадки: " + errors[0].Message,
                        new AggregateException(errors));
            }
        }

        internal sealed class Entry
        {
            internal readonly GameObject Root;
            internal readonly ZNetView View;
            internal readonly ZDO Zdo;
            internal readonly ZDOID Id;
            internal readonly Bounds Bounds;
            internal bool Staged;
            private readonly uint revision;
            private readonly long owner;
            private readonly bool active;
            private readonly Vector3 position, scale;
            private readonly Quaternion rotation;
            private readonly Collider[] colliders;
            internal Entry(GameObject root, ZNetView view, Bounds bounds)
            {
                Root = root; View = view; Zdo = view.GetZDO(); Id = Zdo.m_uid; Bounds = bounds;
                revision = Zdo.DataRevision; owner = Zdo.GetOwner(); active = root.activeSelf;
                position = root.transform.position; rotation = root.transform.rotation; scale = root.transform.lossyScale;
                colliders = root.GetComponentsInChildren<Collider>(true);
            }
            internal void Check(bool staged)
            {
                if (Root == null || View == null || !View.IsValid() || !ReferenceEquals(View.GetZDO(), Zdo) ||
                    Zdo.m_uid != Id || !ReferenceEquals(ZDOMan.instance.GetZDO(Id), Zdo) ||
                    Root.transform.position != position || Root.transform.rotation != rotation || Root.transform.lossyScale != scale ||
                    (!staged && (Root.activeSelf != active || !Root.activeInHierarchy || Zdo.DataRevision != revision)) ||
                    (staged && (!Staged || Root.activeSelf))) Changed();
                Collider[] current = Root.GetComponentsInChildren<Collider>(true);
                if (current.Length != colliders.Length) Changed();
                for (int i = 0; i < current.Length; ++i) if (!ReferenceEquals(current[i], colliders[i])) Changed();
                if (!staged) {
                    Bounds currentBounds = SolidBounds(Root);
                    if ((currentBounds.center - Bounds.center).sqrMagnitude > 0.000004f ||
                        (currentBounds.size - Bounds.size).sqrMagnitude > 0.000004f) Changed();
                }
            }
            internal void Claim()
            {
                View.ClaimOwnership();
                if (!View.IsOwner() || !Zdo.IsOwner())
                    throw new InvalidOperationException("Не удалось получить управление объектом площадки.");
            }
            internal void Restore()
            {
                if (Root == null || View == null || !View.IsValid() || !ReferenceEquals(View.GetZDO(), Zdo) ||
                    !ReferenceEquals(ZDOMan.instance.GetZDO(Id), Zdo))
                    throw new InvalidOperationException("Объект площадки исчез до отмены очистки: " + Id);
                Zdo.SetOwner(owner);
                Root.SetActive(active); Staged = false;
                if (Root.activeSelf != active || Zdo.GetOwner() != owner)
                    throw new InvalidOperationException("Не удалось вернуть состояние объекта площадки: " + Root.name);
            }
            private static void Changed() { throw new InvalidOperationException("Объект площадки изменился до завершения очистки."); }
        }

        private static void Classify(GameObject root, ZNetView view, bool clearPlayerStructures)
        {
            if (root == null || view == null || !view.IsValid() || ZNetScene.instance.GetPrefab(view.GetZDO().GetPrefab()) == null)
                throw new InvalidOperationException("На площадке есть неподдерживаемый сетевой объект.");
            ZDO zdo = view.GetZDO();
            if (zdo.GetBool(ArenaBuilder.ProtectedKey, false) || zdo.GetBool(ArenaBuilder.MobKey, false) ||
                zdo.GetBool(ArenaBuilder.ArmoryKey, false) || zdo.GetBool(ArenaBuilder.CustodyKey, false) ||
                zdo.GetBool(Plugin.InmateKey, false) || zdo.GetBool(ArenaBuilder.ExitKey, false) || zdo.GetBool(ArenaBuilder.InnerGateKey, false))
                throw new InvalidOperationException("Нельзя удалять объекты существующей тюрьмы.");
            byte[] savedItems = zdo.GetByteArray(ZDOVars.s_items, null);
            if (savedItems != null && savedItems.Length != 0 || !String.IsNullOrEmpty(zdo.GetString(ZDOVars.s_items, String.Empty)))
                throw new InvalidOperationException("На площадке есть объект с сохранёнными вещами. Перенесите его вручную.");
            if (root.GetComponentInParent<Player>() != null || root.GetComponentsInChildren<Player>(true).Length != 0)
                throw new InvalidOperationException("Нельзя удалять игроков при очистке площадки.");
            if (root.GetComponentInParent<Container>() != null || root.GetComponentsInChildren<Container>(true).Length != 0)
                throw new InvalidOperationException("На площадке есть сундук или другое хранилище. Перенесите его вручную.");
            foreach (ZNetView child in root.GetComponentsInChildren<ZNetView>(true))
                if (child != view) throw new InvalidOperationException("Нельзя удалять целиком объект с вложенными сетевыми объектами: " + root.name);
            foreach (Transform ancestor in Ancestors(root.transform)) {
                foreach (Component component in ancestor.GetComponents<Component>()) {
                    if (component == null) continue;
                    if (StoredResourceTypes.Contains(component.GetType().Name) || ProtectedWorldTypes.Contains(component.GetType().Name))
                        throw new InvalidOperationException("На площадке есть защищённый объект или хранилище: " + ancestor.name);
                    Location location = component as Location;
                    if (location != null && (ancestor == root.transform || location.m_hasInterior || location.m_noBuild))
                        throw new InvalidOperationException("Нельзя очищать целиком игровую локацию или вход в подземелье.");
                }
            }
            Character character = root.GetComponent<Character>();
            foreach (Component component in root.GetComponentsInChildren<Component>(true)) {
                if (component == null) throw new InvalidOperationException("На площадке есть объект с неизвестным компонентом.");
                Type type = component.GetType();
                if (StoredResourceTypes.Contains(type.Name) || ProtectedWorldTypes.Contains(type.Name) || component is Location)
                    throw new InvalidOperationException("На площадке есть защищённый объект или хранилище: " + root.name);
                Character child = component as Character;
                if (child != null && child != character) throw new InvalidOperationException("Нельзя удалять объект с вложенным существом.");
                if (HasInventory(type, character != null))
                    throw new InvalidOperationException("На площадке есть объект с инвентарём: " + root.name);
            }
            if (character != null) {
                Tameable tameable = root.GetComponent<Tameable>();
                Character.Faction faction = character.GetFaction();
                if (character is Player || character.IsBoss() || faction == Character.Faction.Boss ||
                    character.IsTamed() || zdo.GetBool(ZDOVars.s_tamed, false) || tameable != null && tameable.m_startsTamed ||
                    faction == Character.Faction.Players || faction == Character.Faction.Dverger ||
                    faction == Character.Faction.PlayerSpawned || faction == Character.Faction.TrainingDummy ||
                    character.GetZDOID() != zdo.m_uid || character.GetBaseAI() == null ||
                    faction != Character.Faction.AnimalsVeg && !BaseAI.IsEnemy(character, Player.m_localPlayer))
                    throw new InvalidOperationException("На площадке находится питомец, босс или мирный персонаж. Перенесите тюрьму.");
                return;
            }
            Piece piece = root.GetComponent<Piece>();
            if (piece != null) {
                if (piece.GetCreator() != 0 && !clearPlayerStructures)
                    throw new InvalidOperationException("На площадке есть постройка игрока. Перенесите тюрьму.");
                return;
            }
            if (root.GetComponent<TreeBase>() != null || root.GetComponent<TreeLog>() != null ||
                root.GetComponent<MineRock>() != null || root.GetComponent<MineRock5>() != null) return;
            Destructible destructible = root.GetComponent<Destructible>();
            GameObject prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
            if (destructible != null && (destructible.m_destructibleType == DestructibleType.Tree ||
                prefab.name.StartsWith("rock", StringComparison.OrdinalIgnoreCase) ||
                prefab.name.StartsWith("stub", StringComparison.OrdinalIgnoreCase) ||
                prefab.name.StartsWith("stump", StringComparison.OrdinalIgnoreCase))) return;
            throw new InvalidOperationException("На площадке есть объект, который нельзя безопасно очистить автоматически: " + root.name);
        }

        private static bool HasInventory(Type type, bool wildCharacter)
        {
            Dictionary<Type, bool> cache = wildCharacter ? WildInventoryTypes : InventoryTypes;
            bool cached;
            if (cache.TryGetValue(type, out cached)) return cached;
            bool result = InspectInventory(type, wildCharacter);
            if (cache.Count >= 1024) cache.Clear();
            cache.Add(type, result); return result;
        }

        private static bool InspectInventory(Type type, bool wildCharacter)
        {
            for (Type current = type; current != null && current != typeof(Component) && current != typeof(UnityEngine.Object); current = current.BaseType) {
                foreach (FieldInfo field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) {
                    // Native wild humanoids carry their combat weapon in this inventory; they are explicitly clearable.
                    if (wildCharacter && current == typeof(Humanoid) && field.Name == "m_inventory") continue;
                    if (typeof(Inventory).IsAssignableFrom(field.FieldType) || typeof(Container).IsAssignableFrom(field.FieldType)) return true;
                }
                foreach (PropertyInfo property in current.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (typeof(Inventory).IsAssignableFrom(property.PropertyType) || typeof(Container).IsAssignableFrom(property.PropertyType)) return true;
            }
            return false;
        }

        private static IEnumerable<Transform> Ancestors(Transform transform)
        { for (Transform current = transform; current != null; current = current.parent) yield return current; }

        private static Bounds SolidBounds(GameObject root)
        {
            bool found = false; Bounds bounds = new Bounds();
            foreach (Collider collider in root.GetComponentsInChildren<Collider>(true)) {
                if (collider == null || collider.isTrigger || IsViewBlock(collider) || !collider.enabled || !collider.gameObject.activeInHierarchy) continue;
                Bounds value = collider.bounds;
                if (!Finite(value.center) || !Finite(value.size)) throw new InvalidOperationException("Не удалось проверить границы объекта площадки.");
                if (value.size.sqrMagnitude == 0f) continue;
                if (!found) { bounds = value; found = true; } else bounds.Encapsulate(value);
            }
            if (!found) throw new InvalidOperationException("Не удалось проверить коллизии объекта площадки: " + root.name);
            return bounds;
        }

        private static void RequireBoundedRoot(GameObject root, Bounds bounds, Vector3 origin, Quaternion rotation, float extent)
        {
            if (bounds.size.y > ClearanceFootprint.MaximumHeight)
                throw new InvalidOperationException("Слишком высокий объект для расчистки: " + DescribeObject(root, bounds) + ". Предел высоты — 100 м.");
            if (!ClearanceFootprint.ContainsRoot(bounds.center.x, bounds.center.z, bounds.size.x, bounds.size.z,
                origin.x, origin.z, rotation.eulerAngles.y, extent))
                throw new InvalidOperationException("Объект выходит за границы расчистки: " + DescribeObject(root, bounds) + ". Допустимый запас по краям площадки — 4 м.");
        }

        private static string DescribeObject(GameObject root, Bounds bounds)
        {
            string name = root.name.Replace("(Clone)", "");
            ZNetView view = root.GetComponent<ZNetView>();
            GameObject prefab = view == null || !view.IsValid() ? null : ZNetScene.instance.GetPrefab(view.GetZDO().GetPrefab());
            if (prefab != null) name = prefab.name;
            string kind = root.GetComponent<TreeBase>() != null || root.GetComponent<TreeLog>() != null ? "дерево "
                : root.GetComponent<MineRock>() != null || root.GetComponent<MineRock5>() != null ? "камень "
                : root.GetComponent<Piece>() != null ? "постройка " : "";
            return String.Format(CultureInfo.InvariantCulture, "{0}{1} ({2:0.#} × {3:0.#} × {4:0.#} м; X={5:0}, Z={6:0})",
                kind, name, bounds.size.x, bounds.size.y, bounds.size.z, root.transform.position.x, root.transform.position.z);
        }

        private static void RequireOutsideAltar(Bounds bounds, Vector3 altar)
        {
            double dx = Math.Max(0d, Math.Abs(bounds.center.x - altar.x) - bounds.extents.x);
            double dz = Math.Max(0d, Math.Abs(bounds.center.z - altar.z) - bounds.extents.z);
            if (dx * dx + dz * dz <= TerrainPlan.AltarProtectionRadius * TerrainPlan.AltarProtectionRadius)
                throw new InvalidOperationException("Нельзя очищать жертвенные камни или объекты в их охраняемой области.");
        }

        private static bool ActualItemOverlap(ItemDrop item, Vector3 center, Vector3 half, Quaternion rotation)
        {
            Quaternion inverse = Quaternion.Inverse(rotation);
            Vector3 point = inverse * (item.transform.position - center);
            if (Mathf.Abs(point.x) <= half.x && Mathf.Abs(point.y) <= half.y && Mathf.Abs(point.z) <= half.z) return true;
            foreach (Renderer renderer in item.GetComponentsInChildren<Renderer>(true)) {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy ||
                    !(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer)) continue;
                Bounds bounds = renderer.bounds;
                if (!Finite(bounds.center) || !Finite(bounds.size))
                    throw new InvalidOperationException("Не удалось проверить положение вещей на площадке.");
                bool found = false; Bounds local = new Bounds();
                for (int x = -1; x <= 1; x += 2)
                    for (int y = -1; y <= 1; y += 2)
                        for (int z = -1; z <= 1; z += 2) {
                            Vector3 corner = inverse * (bounds.center + Vector3.Scale(bounds.extents, new Vector3(x, y, z)) - center);
                            if (!found) { local = new Bounds(corner, Vector3.zero); found = true; } else local.Encapsulate(corner);
                        }
                if (local.Intersects(new Bounds(Vector3.zero, half * 2f))) return true;
            }
            return false;
        }

        private static Bounds RotatedBounds(Vector3 center, Vector3 half, Quaternion rotation)
        {
            bool found = false; Bounds bounds = new Bounds();
            for (int x = -1; x <= 1; x += 2)
                for (int z = -1; z <= 1; z += 2) {
                    Vector3 point = center + rotation * new Vector3(half.x * x, 0f, half.z * z);
                    if (!found) { bounds = new Bounds(point, new Vector3(0f, half.y * 2f, 0f)); found = true; }
                    else bounds.Encapsulate(point);
                }
            return bounds;
        }
        private static bool Finite(float value) { return !Single.IsNaN(value) && !Single.IsInfinity(value); }
        private static bool Finite(Vector3 value) { return Finite(value.x) && Finite(value.y) && Finite(value.z); }
        private static void CheckThread()
        {
            int current = Thread.CurrentThread.ManagedThreadId;
            if (unityThread == 0) unityThread = current;
            if (unityThread != current) throw new InvalidOperationException("Очистка площадки доступна только из игрового потока Unity.");
        }
        private static void RequireHost()
        {
            CheckThread();
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZNetScene.instance == null || ZDOMan.instance == null ||
                ZoneSystem.instance == null || Player.m_localPlayer == null)
                throw new InvalidOperationException("Очищать площадку под тюрьму может только хост в игровом мире.");
            if (FlushDestroyed == null || ZRoutedRpc.instance == null)
                throw new InvalidOperationException("В этой версии Valheim недоступно безопасное сетевое удаление объектов.");
        }
        private static void FlushDestroyedObjects()
        { FlushDestroyed.Invoke(ZDOMan.instance, null); }
        internal static void FlushPendingDestruction() { RequireHost(); FlushDestroyedObjects(); }
        private static void RequireWorld(ZNet network, long world, ZNetScene scene)
        {
            RequireHost();
            if (!ReferenceEquals(network, ZNet.instance) || world != ZNet.instance.GetWorldUID() || !ReferenceEquals(scene, ZNetScene.instance))
                throw new InvalidOperationException("Мир сменился до завершения очистки площадки.");
        }
    }
}
