// Test-only native inventories and detached chest records; no player/world.
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;

namespace ValheimModPack.PartyPrison.NativeVerification
{
    public static class CustodyInventoryNativeChecks
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static readonly HashSet<ZDO> records = new HashSet<ZDO>();
        private static readonly HashSet<GameObject> objects = new HashSet<GameObject>();
        private static readonly Dictionary<ZDO, NativeChest> nativeChests = new Dictionary<ZDO, NativeChest>();
        private static int nativeLoads, nativeSaves, corruptCacheMode;
        private static Container corruptCacheTarget;
        private static int identity;
        private const string Account = "Steam_76561198000000031";
        private const string Token = "f1873fdeed404e0f878d37bff8c297d6";

        private static bool DetachedRevision(ZDO __instance)
        {
            if (!records.Contains(__instance)) return true;
            FieldInfo revision = typeof(ZDO).GetField("<DataRevision>k__BackingField", All);
            revision.SetValue(__instance, unchecked((uint)revision.GetValue(__instance) + 1)); return false;
        }
        private static bool SkipAwake(Component __instance)
        { return __instance == null || !objects.Contains(__instance.gameObject); }
        private static bool NativeOwner(Container __instance, ref bool __result)
        {
            if (!nativeChests.Values.Any(chest => chest.Container == __instance)) return true;
            __result = true; return false;
        }
        private static void CountSave(Container __instance)
        { if (nativeChests.Values.Any(chest => chest.Container == __instance)) ++nativeSaves; }
        private static void LoadedCache(Container __instance)
        {
            if (!nativeChests.Values.Any(chest => chest.Container == __instance)) return;
            ++nativeLoads;
            if (__instance != corruptCacheTarget || corruptCacheMode == 0) return;
            int mode = corruptCacheMode; corruptCacheMode = 0;
            Inventory inventory = (Inventory)typeof(Container).GetField("m_inventory", All).GetValue(__instance);
            if (mode == 1) ++RawItems(inventory)[0].m_quality;
            else RawItems(inventory).Clear();
        }
        public static void Run(Action<bool, string> check)
        {
            check(Player.m_localPlayer == null && Game.instance == null, "custody append fixture never opens a world or live character");
            var harmony = new Harmony("valheimmodpack.partyprison.nativeprobe.custody-append");
            try {
                harmony.Patch(typeof(ZDO).GetMethod("IncreaseDataRevision", All),
                    prefix: new HarmonyMethod(typeof(CustodyInventoryNativeChecks).GetMethod("DetachedRevision", All)));
                var awake = new HarmonyMethod(typeof(CustodyInventoryNativeChecks).GetMethod("SkipAwake", All)) { priority = Priority.First };
                harmony.Patch(typeof(ZNetView).GetMethod("Awake", All), prefix: awake);
                harmony.Patch(typeof(Container).GetMethod("Awake", All), prefix: awake);
                harmony.Patch(typeof(Container).GetMethod("IsOwner", All),
                    prefix: new HarmonyMethod(typeof(CustodyInventoryNativeChecks).GetMethod("NativeOwner", All)));
                harmony.Patch(typeof(Container).GetMethod("Save", All),
                    prefix: new HarmonyMethod(typeof(CustodyInventoryNativeChecks).GetMethod("CountSave", All)));
                harmony.Patch(typeof(Container).GetMethod("Load", All),
                    postfix: new HarmonyMethod(typeof(CustodyInventoryNativeChecks).GetMethod("LoadedCache", All)));
                CheckWear(check); CheckAppend(check); CheckPublication(check);
                CheckMaskedReloadRegression(check); CheckClosedCachePublication(check); CheckClosedCacheRollback(check);
            }
            finally {
                foreach (GameObject value in objects) if (value != null) UnityEngine.Object.DestroyImmediate(value);
                foreach (ZDO record in records) typeof(ZDO).GetMethod("Reset", All).Invoke(record, null);
                objects.Clear(); nativeChests.Clear(); records.Clear(); corruptCacheTarget = null; corruptCacheMode = 0;
                harmony.UnpatchSelf();
            }
            check(Player.m_localPlayer == null && Game.instance == null, "custody fixtures release all detached records without changing a live inventory");
        }
        private static ItemDrop.ItemData Item(string name, int count, int x, int y)
        {
            GameObject prefab = PrefabManager.Instance.GetPrefab(name);
            if (prefab == null || prefab.GetComponent<ItemDrop>() == null) throw new InvalidOperationException("Missing native custody fixture item: " + name);
            ItemDrop.ItemData item = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
            item.m_dropPrefab = prefab; item.m_stack = count; item.m_gridPos = new Vector2i(x, y);
            return item;
        }
        private static Inventory Inventory(string name, int width = 4, int height = 10)
        { return new Inventory(name, null, width, height); }
        private static bool Equal(byte[] expected, byte[] actual)
        { return expected == null ? actual == null : actual != null && expected.SequenceEqual(actual); }
        private static bool Rejected(Action action)
        {
            try { action(); return false; }
            catch (Exception error) {
                if (error is InvalidDataException || error is InvalidOperationException || error is AggregateException || error is EndOfStreamException) return true;
                throw;
            }
        }
        private static bool RejectedTruncatedNativeLoad(Action action)
        {
            try { action(); return false; }
            catch (Exception error) {
                Exception cause = error;
                while (cause is TargetInvocationException && cause.InnerException != null) cause = cause.InnerException;
                if (cause is EndOfStreamException) return true;
                throw;
            }
        }
        private static List<ItemDrop.ItemData> Items(byte[][] payloads)
        { return payloads.SelectMany(payload => CustodyInventory.Decode(payload).GetAllItems()).ToList(); }
        private static void CheckWear(Action<bool, string> check)
        {
            foreach (float wear in new[] { .05f, .23f, .53f, 1.06f, 175.63f, .059f, .539f, .1069f, 43.8399f, -.059f, -.53f }) {
                Inventory source = Inventory("Custody fractional native wear");
                ItemDrop.ItemData sword = Item("SwordBronze", 1, 0, 6); sword.m_quality = 3; sword.m_durability = wear;
                sword.m_crafterID = 87654321; sword.m_crafterName = "Владелец ☃";
                sword.m_customData["custody_owner"] = "friend"; sword.m_customData["custody_magic"] = "strict metadata";
                source.GetAllItems().Add(sword);
                byte[] first;
                try { first = CustodyInventory.Capture(source); }
                catch {
                    DiagnoseWearFixture(source, wear);
                    throw;
                }
                Inventory restored = CustodyInventory.Decode(first);
                float restoredWear = restored.GetAllItems()[0].m_durability;
                check(Math.Abs((double)restoredWear - wear) < .010001d,
                    "custody preserves native hundredth wear for exact, fractional and negative durability " + wear.ToString("R"));
                for (int repeat = 0; repeat < 10; ++repeat) {
                    byte[] next = CustodyInventory.Capture(restored);
                    check(CustodyInventory.Equivalent(first, next), "ten repeated native custody captures retain all persisted item fields");
                    restored = CustodyInventory.Decode(next);
                }
                check(restored.GetAllItems()[0].m_durability == restoredWear,
                    "repeated native load/save never loses another hundredth of durability");
                byte[][] target = CustodyInventory.PrepareChestPayloads(first, 2, 1);
                check(Items(target).Single().m_durability == restoredWear,
                    "four-chest preparation also retains the same native wear rather than repeating float truncation");
                check(source.GetAllItems().Count == 1 && ReferenceEquals(source.GetAllItems()[0], sword) && sword.m_durability == wear,
                    "native snapshot stabilization leaves the live source reference and exact fractional wear untouched");
                sword.m_durability = wear + .1f;
                check(!CustodyInventory.Equivalent(first, CustodyInventory.Capture(source)), "real wear changes beyond native precision are rejected");
                sword.m_durability = wear; sword.m_customData["custody_magic"] = "changed";
                check(!CustodyInventory.Equivalent(first, CustodyInventory.Capture(source)), "non-empty enchantment metadata remains strictly protected");
            }
            Inventory order = Inventory("Custody metadata order"); ItemDrop.ItemData armor = Item("HelmetLeather", 1, 0, 0);
            armor.m_customData["custody_z"] = "last"; armor.m_customData["custody_a"] = "first"; order.GetAllItems().Add(armor);
            byte[] before = CustodyInventory.Capture(order);
            armor.m_customData = new Dictionary<string, string>(armor.m_customData.OrderByDescending(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value));
            check(CustodyInventory.Equivalent(before, CustodyInventory.Capture(order)), "dictionary insertion order cannot falsely reject unchanged mod metadata");
            armor.m_crafterID = 31415; armor.m_crafterName = "Different crafter";
            check(!CustodyInventory.Equivalent(before, CustodyInventory.Capture(order)), "actual native crafter metadata remains part of custody identity");
        }
        private static void DiagnoseWearFixture(Inventory source, float requested)
        {
            Debug.Log("[Custody isolated wear fixture] requested=" + requested.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            MethodInfo save = typeof(CustodyInventory).GetMethod("Save", All), identityMethod = typeof(CustodyInventory).GetMethod("ItemIdentity", All);
            MethodInfo centsMethod = typeof(CustodyInventory).GetMethod("DurabilityHundredths", All);
            byte[] bytes = (byte[])save.Invoke(null, new object[] { source });
            var raw = new ZPackage(bytes); int version = raw.ReadInt(); int count = raw.ReadUShort();
            Debug.Log("[Custody isolated wear fixture] native version=" + version + " count=" + count + " encoded first wear=" + raw.ReadInt());
            Inventory decoded = CustodyInventory.Decode(bytes);
            foreach (var entry in new[] { new { Name = "source", Inventory = source }, new { Name = "decoded", Inventory = decoded } })
                foreach (ItemDrop.ItemData item in entry.Inventory.GetAllItems()) {
                    Debug.Log("[Custody isolated wear fixture] " + entry.Name + " prefab=" + item.m_dropPrefab.name
                        + " stack=" + item.m_stack + " quality=" + item.m_quality + " variant=" + item.m_variant
                        + " worldLevel=" + item.m_worldLevel + " pickedUp=" + item.m_pickedUp + " cheated=" + item.m_cheated
                        + " wear=" + item.m_durability.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                        + " cents=" + centsMethod.Invoke(null, new object[] { item.m_durability })
                        + " crafterId=" + item.m_crafterID + " crafterName=" + item.m_crafterName
                        + " grid=" + item.m_gridPos + " equipped=" + item.m_equipped
                        + " identity=" + identityMethod.Invoke(null, new object[] { item }));
                    foreach (var pair in item.m_customData.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                        Debug.Log("[Custody isolated wear fixture] " + entry.Name + " custom " + pair.Key + "=" + pair.Value);
                }
        }
        private static Inventory Current()
        {
            Inventory current = Inventory("Current prisoner's personal gear");
            ItemDrop.ItemData armor = Item("HelmetLeather", 1, 0, 6); armor.m_durability = .539f; armor.m_equipped = true;
            armor.m_customData["eaqs_slot"] = "head"; armor.m_customData["new_owner"] = "current inmate";
            ItemDrop.ItemData ore = Item("Iron", 5, 0, 0); ore.m_customData["new_owner"] = "current inmate";
            current.GetAllItems().AddRange(new[] { armor, ore }); return current;
        }
        private static byte[][] Old(bool full)
        {
            var old = new byte[4][];
            Inventory nested = Inventory("Previous property nested metadata", 2, 1); nested.GetAllItems().Add(Item("Iron", 12, 0, 0));
            string bagBytes = Convert.ToBase64String(CustodyInventory.Capture(nested));
            for (int index = 0; index < 4; ++index) {
                Inventory chest = Inventory("Previous ordinary chest", 2, 1);
                for (int slot = 0; slot < (full ? 2 : index == 0 ? 1 : 0); ++slot) {
                    ItemDrop.ItemData wood = Item("Wood", 11 + index, full ? slot : 1, 0);
                    wood.m_customData["old_owner"] = "previous friend";
                    if (index == 0 && slot == 0) wood.m_customData["fixture#AdventureBackpacks.Components.BackpackComponent"] = bagBytes;
                    chest.GetAllItems().Add(wood);
                }
                old[index] = CustodyInventory.Capture(chest);
            }
            return old;
        }
        private static void CheckAppend(Action<bool, string> check)
        {
            Inventory current = Current(); ItemDrop.ItemData[] references = current.GetAllItems().ToArray();
            byte[] personal = CustodyInventory.Capture(current); byte[][] old = Old(false);
            byte[][] untouched = old.Select(payload => (byte[])payload.Clone()).ToArray();
            byte[][] targets = CustodyInventory.PrepareAdmissionPayloads(old, personal, 2, 1); List<ItemDrop.ItemData> all = Items(targets);
            check(all.Count == 3 && all.Count(item => item.m_customData.ContainsKey("new_owner")) == 2
                && all.Count(item => item.m_customData.ContainsKey("old_owner")) == 1,
                "occupied public chests append every current belonging while retaining previous property when slots fit");
            ItemDrop.ItemData previous = all.Single(item => item.m_customData.ContainsKey("old_owner"));
            ItemDrop.ItemData savedPrevious = CustodyInventory.Decode(old[0]).GetAllItems().Single();
            string bagKey = "fixture#AdventureBackpacks.Components.BackpackComponent";
            check(previous.m_gridPos.x == 1 && previous.m_gridPos.y == 0 && previous.m_stack == 11
                && previous.m_customData[bagKey] == savedPrevious.m_customData[bagKey],
                "append leaves previous chest slot, count and nested inventory bytes intact");
            check(CustodyInventory.Decode(targets[0]).GetAllItems().Any(item => item.m_customData.ContainsKey("new_owner") && item.m_gridPos.x == 0),
                "append selects an actually empty native cell rather than overwriting an occupied slot");
            check(!all.Single(item => item.m_dropPrefab.name == "HelmetLeather").m_customData.ContainsKey("eaqs_slot"),
                "new confiscated equipment removes temporary slot bookkeeping without altering enchantments or ownership data");
            check(old.Zip(untouched, Equal).All(equal => equal) && references.SequenceEqual(current.GetAllItems())
                && references[0].m_equipped && references[0].m_customData["eaqs_slot"] == "head",
                "preparing combined targets changes neither old durable bytes nor the current player's original objects");
            byte[][] full = Old(true); byte[][] replacement = CustodyInventory.PrepareAdmissionPayloads(full, personal, 2, 1);
            check(Items(replacement).Count == 2 && Items(replacement).All(item => item.m_customData.ContainsKey("new_owner"))
                && replacement.Skip(1).All(payload => CustodyInventory.Count(payload) == 0),
                "when combined property exceeds capacity the complete four-chest plan replaces all old contents with current belongings");
            var oversized = Inventory("Oversized current character", 3, 3);
            for (int slot = 0; slot < 9; ++slot) oversized.GetAllItems().Add(Item("Wood", 1, slot % 3, slot / 3));
            ItemDrop.ItemData[] oversizeReferences = oversized.GetAllItems().ToArray(); byte[] oversizedBytes = CustodyInventory.Capture(oversized);
            check(Rejected(() => CustodyInventory.PrepareAdmissionPayloads(full, oversizedBytes, 2, 1))
                && oversizeReferences.SequenceEqual(oversized.GetAllItems()),
                "current inventory exceeding all four chests fails before clearing any original item or replacing old property");
            var invalid = Inventory("Rejected current mod metadata"); ItemDrop.ItemData malformed = Item("Wood", 1, 0, 0);
            malformed.m_customData[bagKey] = "invalid backpack bytes"; invalid.GetAllItems().Add(malformed);
            check(Rejected(() => CustodyInventory.Capture(invalid)) && invalid.GetAllItems().Count == 1
                && ReferenceEquals(invalid.GetAllItems()[0], malformed),
                "a rejected native metadata roundtrip never removes the current player's original item");
        }
        private static ZDO[] Chests(byte[][] payloads)
        {
            var chests = new ZDO[4];
            for (int index = 0; index < 4; ++index) {
                var chest = new ZDO { m_uid = new ZDOID(-643591876, (uint)++identity) }; records.Add(chest); chests[index] = chest;
                typeof(ZDO).GetField("m_prefab", All).SetValue(chest, ArenaBuilder.CustodyPrefab.GetStableHashCode());
                chest.Set(ArenaBuilder.CustodyKey, true); chest.Set("VMP_PP_CustodyIndex", index);
                chest.Set(CustodyInventory.OwnerKey, "previous owner"); chest.Set(CustodyInventory.TokenKey, new string('2', 32));
                chest.Set(CustodyInventory.HashKey, "previous hash"); chest.Set(CustodyInventory.ReleasedKey, true);
                chest.Set(CustodyInventory.PublicKey, false);
                if (payloads[index] != null) chest.Set(ZDOVars.s_items, (byte[])payloads[index].Clone());
            }
            return chests;
        }
        private static CustodyRecord Plan(ZDO[] chests, byte[] personal)
        {
            return new CustodyRecord { AccountId = Account, SentenceId = Token, World = 9999, Stage = CustodyStage.Cleared,
                OriginalPayload = personal, PayloadHash = CustodyInventory.Fingerprint(personal), ClearSequence = 1,
                DepositPayloads = CustodyInventory.PrepareAdmissionPayloads(chests, personal, 2, 1),
                DepositBaselineHashes = CustodyInventory.ChestPayloadFingerprints(chests) };
        }
        private static void Publish(ZDO[] chests, CustodyRecord record, Action<ZDO> reload)
        {
            MethodInfo publish = typeof(CustodyInventory).GetMethod("PublishDeposit", All);
            if (publish == null) throw new MissingMethodException("Production four-chest publication seam is unavailable.");
            // Owner zero avoids creating a fake network manager/session. The
            // production host wrapper supplies its actual session authority.
            try { publish.Invoke(null, new object[] { chests, record, 2, 1, 0L, reload }); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }
        private static void CheckPublication(Action<bool, string> check)
        {
            byte[] personal = CustodyInventory.Capture(Current()); byte[][] old = Old(false);
            ZDO[] chests = Chests(old); ZDOID[] ids = chests.Select(chest => chest.m_uid).ToArray(); CustodyRecord plan = Plan(chests, personal);
            string[] baselines = CustodyInventory.ChestPayloadFingerprints(chests); int callbacks = 0;
            check(Rejected(() => Publish(chests, plan, chest => { if (++callbacks == 2) throw new InvalidOperationException("Controlled second-chest native cache failure"); })),
                "a native callback failure aborts staged four-chest publication");
            check(chests.Select((chest, index) => Equal(chest.GetByteArray(ZDOVars.s_items, null), old[index])
                && chest.m_uid == ids[index] && chest.GetString(CustodyInventory.TokenKey, "") == new string('2', 32)
                && chest.GetString(CustodyInventory.OwnerKey, "") == "previous owner"
                && chest.GetString(CustodyInventory.HashKey, "") == "previous hash"
                && chest.GetBool(CustodyInventory.ReleasedKey, false) && !chest.GetBool(CustodyInventory.PublicKey, true)).All(value => value),
                "failed publication restores exact payload bytes, identities and assignment/access markers for all four chests");
            check(plan.OriginalPayload.SequenceEqual(personal) && baselines.SequenceEqual(plan.DepositBaselineHashes),
                "publication rollback leaves the current character's durable original and immutable plan unchanged");
            callbacks = 0; Publish(chests, plan, chest => ++callbacks);
            check(callbacks == 4 && CustodyInventory.ChestsMatch(chests, plan, 2, 1), "retry publishes the frozen combined targets once after rollback");
            callbacks = 0; Publish(chests, plan, chest => ++callbacks);
            check(callbacks == 0 && chests.Select((chest, index) => chest.m_uid == ids[index]).All(value => value),
                "same-token publication retry is a no-op preserving every original native chest identity");
            ZDO[] partial = Chests(old); CustodyRecord resumed = Plan(partial, personal);
            partial[0].Set(CustodyInventory.OwnerKey, resumed.AccountId); partial[0].Set(CustodyInventory.TokenKey, resumed.SentenceId);
            partial[0].Set(CustodyInventory.HashKey, CustodyInventory.Fingerprint(resumed.DepositPayloads[0]));
            partial[0].Set(CustodyInventory.ReleasedKey, false); partial[0].Set(ZDOVars.s_items, resumed.DepositPayloads[0]);
            callbacks = 0; Publish(partial, resumed, chest => ++callbacks);
            check(callbacks == 3 && CustodyInventory.ChestsMatch(partial, resumed, 2, 1)
                && partial.Sum(chest => CustodyInventory.Count(chest.GetByteArray(ZDOVars.s_items, null))) == 3,
                "crash-style partial retry uses journal targets and never appends another copy of current property");
            ZDO[] changed = Chests(old); CustodyRecord frozen = Plan(changed, personal);
            Inventory empty = Inventory("Changed private baseline", 2, 1); changed[0].Set(ZDOVars.s_items, CustodyInventory.Capture(empty));
            string[] changedBytes = CustodyInventory.ChestPayloadFingerprints(changed); callbacks = 0;
            check(Rejected(() => Publish(changed, frozen, chest => ++callbacks)) && callbacks == 0
                && changedBytes.SequenceEqual(CustodyInventory.ChestPayloadFingerprints(changed)),
                "changed private baseline fails before mutations rather than resurrecting already removed previous property");
            frozen.PublicAccess = true; frozen.OriginalPayload = new byte[] { 1 };
            check(Rejected(() => Publish(changed, frozen, chest => ++callbacks)) && callbacks == 0
                && changedBytes.SequenceEqual(CustodyInventory.ChestPayloadFingerprints(changed)),
                "an already public handoff cannot revalidate or replay immutable property even after ordinary withdrawal or theft");
            changed[0].Set(ZDOVars.s_inUse, 1);
            check(Rejected(() => CustodyInventory.RequireAdmissionChests(changed)), "native remote open-cache flag postpones only the short transfer reservation");
            changed[0].Set(ZDOVars.s_inUse, 0);
            CustodyInventory.RequireAdmissionChests(changed);
            check(true, "occupied chests remain eligible for a new transfer once the native open-cache flag clears");
        }
        private sealed class NativeChest
        {
            internal GameObject Object;
            internal Container Container;
            internal Inventory Inventory;
            internal ZDO Zdo;
        }
        private static List<ItemDrop.ItemData> RawItems(Inventory inventory)
        { return (List<ItemDrop.ItemData>)typeof(Inventory).GetField("m_inventory", All).GetValue(inventory); }
        private static object Invoke(MethodInfo method, object target, params object[] arguments)
        {
            try { return method.Invoke(target, arguments); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }
        private static NativeChest LoadedChest(ZDO zdo)
        {
            var chest = new NativeChest { Zdo = zdo };
            chest.Object = new GameObject("PartyPrison.ClosedCustodyFixture." + zdo.m_uid);
            chest.Object.SetActive(false); objects.Add(chest.Object);
            ZNetView view = chest.Object.AddComponent<ZNetView>(); chest.Container = chest.Object.AddComponent<Container>();
            typeof(ZNetView).GetField("m_zdo", All).SetValue(view, zdo);
            chest.Inventory = Inventory("Closed custody native cache", 2, 1);
            byte[] initial = zdo.GetByteArray(ZDOVars.s_items, null);
            if (initial != null) chest.Inventory.Load(new ZPackage(initial));
            typeof(Container).GetField("m_nview", All).SetValue(chest.Container, view);
            typeof(Container).GetField("m_inventory", All).SetValue(chest.Container, chest.Inventory);
            typeof(Container).GetField("m_width", All).SetValue(chest.Container, 2);
            typeof(Container).GetField("m_height", All).SetValue(chest.Container, 1);
            typeof(Container).GetField("m_lastRevision", All).SetValue(chest.Container, zdo.DataRevision);
            nativeChests.Add(zdo, chest);
            chest.Inventory.m_onChanged += (Action)Delegate.CreateDelegate(typeof(Action), chest.Container, typeof(Container).GetMethod("OnContainerChanged", All));
            Invoke(typeof(CustodyInventory).GetMethod("RegisterContainer", All), null, chest.Container);
            if (!view.IsValid() || !ReferenceEquals(CustodyInventory.ChestForInventory(chest.Inventory), zdo))
                throw new InvalidOperationException("Detached native custody cache was not classified as a closed chest.");
            return chest;
        }
        private static void Reload(ZDO zdo)
        {
            MethodInfo reload = typeof(CustodyInventory).GetMethod("ReloadLoadedChestCache", All);
            if (reload == null) throw new MissingMethodException("Production closed native chest cache validation seam is unavailable.");
            Invoke(reload, null, nativeChests[zdo].Container, zdo);
        }
        private static void RequireCache(byte[] expected, Inventory inventory, bool publicGetter)
        {
            Invoke(typeof(CustodyInventory).GetMethod("RequireEquivalent", All), null,
                CustodyInventory.Decode(expected).GetAllItems(), publicGetter ? (IEnumerable<ItemDrop.ItemData>)inventory.GetAllItems() : RawItems(inventory).ToArray());
        }
        private static bool CacheMatches(byte[] expected, Inventory inventory)
        { return !Rejected(() => RequireCache(expected, inventory, false)); }
        private static FieldInfo LoadingScope()
        { return typeof(Plugin).Assembly.GetType("ValheimModPack.PartyPrison.PrisonGuard", true).GetField("LoadingCustody", All); }
        private static bool Loading(Container container)
        { return (bool)typeof(Container).GetField("m_loading", All).GetValue(container); }
        private static void CheckMaskedReloadRegression(Action<bool, string> check)
        {
            FieldInfo scope = LoadingScope(); int previousScope = (int)scope.GetValue(null);
            foreach (int count in new[] { 0, 1, 2 }) {
                Inventory personal = Inventory("Minimal admission regression");
                if (count > 0) {
                    ItemDrop.ItemData item = Item("Iron", 1, 0, 0); item.m_customData["closed_custody_regression"] = "must survive";
                    personal.GetAllItems().Add(item);
                }
                if (count > 1) personal.GetAllItems().Add(Item("HelmetLeather", 1, 1, 0));
                byte[] payload = CustodyInventory.Capture(personal);
                ZDO zdo = Chests(new[] { payload, payload, payload, payload })[0]; NativeChest chest = LoadedChest(zdo);
                nativeLoads = nativeSaves = 0;
                typeof(Container).GetField("m_lastRevision", All).SetValue(chest.Container, zdo.DataRevision ^ UInt32.MaxValue);
                check((bool)Invoke(typeof(Container).GetMethod("Load", All), chest.Container), "real native closed chest Load executes for zero, one and multiple personal items");
                check(RawItems(chest.Inventory).Count == count && CacheMatches(payload, chest.Inventory)
                    && chest.Inventory.GetAllItems().Count == 0,
                    "actual native closed cache retains personal items while its ordinary getter deliberately masks them");
                bool legacyRejected = Rejected(() => RequireCache(payload, chest.Inventory, true));
                check(legacyRejected == (count != 0),
                    "legacy post-Load public getter comparison reproduces nonempty admission failure while empty admission passes");
                check(nativeLoads == 1 && nativeSaves == 0 && !Loading(chest.Container) && (int)scope.GetValue(null) == previousScope,
                    "patched native inventory load and real Changed callback preserve the save gate and restore the exact private load scope");
                Reload(zdo);
                check(CacheMatches(payload, chest.Inventory) && chest.Inventory.GetAllItems().Count == 0
                    && nativeSaves == 0 && !Loading(chest.Container) && (int)scope.GetValue(null) == previousScope,
                    "production closed chest reload accepts the actual complete cache without opening ordinary player access");
            }
        }
        private static void CheckClosedCachePublication(Action<bool, string> check)
        {
            byte[][] old = Old(false); ZDO[] chests = Chests(old);
            NativeChest[] caches = chests.Select(LoadedChest).ToArray(); byte[] personal = CustodyInventory.Capture(Current());
            CustodyRecord plan = Plan(chests, personal); FieldInfo scope = LoadingScope(); int previousScope = (int)scope.GetValue(null);
            nativeLoads = nativeSaves = 0;
            Publish(chests, plan, Reload);
            check(nativeLoads == 4 && nativeSaves == 0 && CustodyInventory.ChestsMatch(chests, plan, 2, 1),
                "prepared nonempty custody plan publishes through all four real closed native caches with Changed saves suppressed");
            check(caches.Select((chest, index) => CacheMatches(plan.DepositPayloads[index], chest.Inventory)
                && chest.Inventory.GetAllItems().Count == 0 && !ReferenceEquals(chest.Container.GetInventory(), chest.Inventory)
                && !Loading(chest.Container)).All(value => value) && (int)scope.GetValue(null) == previousScope,
                "four-chest deposit verifies real saved belongings and restores masking plus container and projection scopes");
            ItemDrop.ItemData[][] identities = caches.Select(chest => RawItems(chest.Inventory).ToArray()).ToArray();
            nativeLoads = 0; Publish(chests, plan, Reload);
            check(nativeLoads == 0 && nativeSaves == 0 && caches.Select((chest, index) => identities[index].SequenceEqual(RawItems(chest.Inventory))).All(value => value),
                "same-token closed deposit retry is a no-op preserving each real native item reference");
            foreach (ZDO zdo in chests) zdo.Set(CustodyInventory.PublicKey, true);
            check(caches.All(chest => ReferenceEquals(chest.Container.GetInventory(), chest.Inventory)
                && chest.Inventory.GetAllItems().Count == RawItems(chest.Inventory).Count),
                "after handoff all four containers immediately expose their ordinary native inventories");
            caches[0].Inventory.RemoveAll();
            check(nativeSaves == 1 && caches[0].Inventory.GetAllItems().Count == 0
                && CustodyInventory.Count(chests[0].GetByteArray(ZDOVars.s_items, null)) == 0,
                "ordinary public withdrawal executes the real native save callback and persists empty contents once");
            plan.PublicAccess = true; string[] withdrawn = CustodyInventory.ChestPayloadFingerprints(chests); int callbacks = 0;
            check(Rejected(() => Publish(chests, plan, zdo => { ++callbacks; Reload(zdo); })) && callbacks == 0
                && withdrawn.SequenceEqual(CustodyInventory.ChestPayloadFingerprints(chests)),
                "public withdrawal or theft never replays original personal belongings or validates an obsolete chest plan");
            Inventory next = Inventory("Next inmate after theft"); ItemDrop.ItemData nextItem = Item("Iron", 3, 0, 0);
            nextItem.m_customData["new_owner"] = "second inmate"; next.GetAllItems().Add(nextItem);
            CustodyRecord nextPlan = Plan(chests, CustodyInventory.Capture(next)); nextPlan.SentenceId = new string('c', 32);
            foreach (ZDO zdo in chests) zdo.Set(CustodyInventory.PublicKey, false);
            nativeLoads = nativeSaves = 0; Publish(chests, nextPlan, Reload);
            List<ItemDrop.ItemData> remaining = Items(chests.Select(zdo => zdo.GetByteArray(ZDOVars.s_items, null)).ToArray());
            check(nativeLoads == 4 && nativeSaves == 0 && CustodyInventory.ChestsMatch(chests, nextPlan, 2, 1)
                && remaining.Any(item => item.m_customData.ContainsKey("new_owner") && item.m_customData["new_owner"] == "second inmate")
                && remaining.All(item => !item.m_customData.ContainsKey("old_owner")),
                "a later nonempty admission succeeds after theft without resurrecting removed earlier property");
        }
        private static void CheckClosedCacheRollback(Action<bool, string> check)
        {
            byte[][] old = Old(false); ZDO[] chests = Chests(old); NativeChest[] caches = chests.Select(LoadedChest).ToArray();
            byte[] personal = CustodyInventory.Capture(Current()); FieldInfo scope = LoadingScope(); int previousScope = (int)scope.GetValue(null);
            foreach (int mode in new[] { 1, 2 }) {
                CustodyRecord plan = Plan(chests, personal); corruptCacheTarget = caches[0].Container; corruptCacheMode = mode;
                nativeLoads = nativeSaves = 0;
                check(Rejected(() => Publish(chests, plan, Reload)),
                    "real post-load metadata alteration or missing cached belongings rejects publication rather than weakening item validation");
                check(chests.Select((zdo, index) => Equal(zdo.GetByteArray(ZDOVars.s_items, null), old[index])
                    && CacheMatches(old[index], caches[index].Inventory) && !Loading(caches[index].Container)
                    && caches[index].Inventory.GetAllItems().Count == 0).All(value => value)
                    && nativeSaves == 0 && (int)scope.GetValue(null) == previousScope,
                    "failed closed native cache validation restores all original durable contents and actual caches without opening access");
                check(plan.OriginalPayload.SequenceEqual(personal) && corruptCacheMode == 0,
                    "rejected real-cache publication leaves the immutable personal backup unchanged");
            }
            corruptCacheTarget = null;
            ZDO empty = Chests(new byte[4][])[0]; NativeChest absent = LoadedChest(empty);
            RawItems(absent.Inventory).Add(Item("Wood", 9, 0, 0)); nativeSaves = 0;
            Reload(empty);
            check(RawItems(absent.Inventory).Count == 0 && empty.GetByteArray(ZDOVars.s_items, null) == null
                && nativeSaves == 0 && absent.Inventory.GetAllItems().Count == 0 && !Loading(absent.Container)
                && (int)scope.GetValue(null) == previousScope,
                "rollback to a missing original payload clears the real private cache without converting null storage or invoking native Save");
            typeof(Container).GetField("m_loading", All).SetValue(absent.Container, true); Reload(empty);
            check(Loading(absent.Container) && empty.GetByteArray(ZDOVars.s_items, null) == null
                && (int)scope.GetValue(null) == previousScope,
                "empty-cache restoration also preserves an already active native container loading flag");
            typeof(Container).GetField("m_loading", All).SetValue(absent.Container, false);
            empty.Set(ZDOVars.s_items, new byte[] { 1, 2 });
            check(RejectedTruncatedNativeLoad(() => Reload(empty)) && !Loading(absent.Container) && (int)scope.GetValue(null) == previousScope
                && !absent.Inventory.AddItem(Item("Iron", 2, 0, 0)),
                "malformed real native Load restores both scopes and keeps subsequent player writes blocked");
        }
    }
}
