// Menu-only test helper: compiled into the isolated native probe, never the mod.
using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace ValheimModPack.PartyPrison.NativeVerification
{
    public static class RecoveryNativeChecks
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private static readonly HashSet<GameObject> DetachedPlayers = new HashSet<GameObject>();
        private static readonly HashSet<ZDO> PublicationZdos = new HashSet<ZDO>();
        private static Type backpackApi;
        public static bool BackpackFixtureSkipped { get; private set; }

        public static void Run(Action<bool, string> check)
        {
            BackpackFixtureSkipped = false;
            backpackApi = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("AdventureBackpacks.API.ABAPI", false)).FirstOrDefault(type => type != null);
            CheckEmergencyState(check);
            CheckDurableReceipt(check);
            CheckPublicationRetry(check);
            CheckNestedBackpackGear(check);
        }

        private static void CheckEmergencyState(Action<bool, string> check)
        {
            object plugin = Chainloader.PluginInfos[Plugin.Id].Instance;
            string[] names = { "localSentence", "region", "localCustodyStage", "localLayoutVersion", "localRecovery", "network" };
            var fields = names.Select(name => typeof(Plugin).GetField(name, All)).ToArray();
            check(fields.All(field => field != null), "recovery fixture resolves the actual plugin state fields");
            object[] before = fields.Select(field => field.GetValue(plugin)).ToArray();
            PropertyInfo confined = typeof(Plugin).GetProperty("Confined", All), preparing = typeof(Plugin).GetProperty("PreparingCustody", All), finish = typeof(Plugin).GetProperty("CanFinishLocalRelease", All), active = typeof(Plugin).GetProperty("PrisonActive", All);
            check(confined != null && preparing != null && finish != null, "recovery fixture resolves production confinement and release predicates");
            try
            {
                string token = Guid.NewGuid().ToString("N");
                var sentence = new SentenceState { SentenceId = token, AccountId = "Steam_76561198000000001", RemainingSeconds = 0, PendingRelease = true, Revision = 2 };
                fields[0].SetValue(plugin, sentence);
                fields[1].SetValue(plugin, new PrisonRegion { Center = new PrisonPoint(0, 2, 0), CellSpawn = new PrisonPoint(-8, 2, 0), ArenaSpawn = new PrisonPoint(4, 2, 0), Radius = 20, HalfHeight = 8 });
                fields[2].SetValue(plugin, -1); fields[3].SetValue(plugin, 4); fields[4].SetValue(plugin, ""); fields[5].SetValue(plugin, null);
                check((bool)confined.GetValue(plugin, null) && (bool)preparing.GetValue(plugin, null) && !(bool)finish.GetValue(plugin, null), "legacy pending release without custody reproduces the blocked admission state");
                sentence.EmergencyRelease = true;
                check(!(bool)confined.GetValue(plugin, null) && !(bool)preparing.GetValue(plugin, null) && (bool)finish.GetValue(plugin, null), "emergency pending release immediately unlocks control and permits cleanup without custody or a local player");
                check(!(bool)active.GetValue(plugin, null), "emergency release also disables the prison-wide administration guard");
                fields[4].SetValue(plugin, "Missing prison chests"); fields[2].SetValue(plugin, (int)CustodyStage.Prepared);
                check(!(bool)preparing.GetValue(plugin, null) && (bool)finish.GetValue(plugin, null), "an emergency release also bypasses prepared custody recovery locks");
                Inventory empty = new Inventory("Prison emergency stale packet", null, 8, 4); byte[] original = Save(empty);
                using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
                {
                    WriteText(writer, token); WriteText(writer, CustodyInventory.Fingerprint(original)); writer.Write(original.Length); writer.Write(original); writer.Flush(); stream.Position = 0;
                    using (var reader = new BinaryReader(stream))
                    {
                        MethodInfo method = typeof(Plugin).GetMethod("ClientCustodyMessage", All);
                        check((bool)method.Invoke(plugin, new object[] { 6, reader }) && stream.Position == stream.Length, "a stale inventory clear is fully consumed after emergency release without touching a player");
                    }
                }
                check((int)fields[2].GetValue(plugin) == (int)CustodyStage.Prepared, "ignored stale clear cannot advance cancelled custody");
            }
            finally { for (int i = 0; i < fields.Length; ++i) fields[i].SetValue(plugin, before[i]); }
            check(fields.Select((field, i) => System.Object.Equals(field.GetValue(plugin), before[i])).All(same => same), "recovery fixture restores every original plugin field");
        }

        private static bool DetachedPlayerLifecycle(Player __instance)
        { return __instance == null || !DetachedPlayers.Contains(__instance.gameObject); }

        private static bool DetachedPublicationRevision(ZDO __instance)
        {
            if (!PublicationZdos.Contains(__instance)) return true;
            FieldInfo revision = typeof(ZDO).GetField("<DataRevision>k__BackingField", All);
            revision.SetValue(__instance, unchecked((uint)revision.GetValue(__instance) + 1)); return false;
        }
        private static void CheckPublicationRetry(Action<bool, string> check)
        {
            string root = Environment.GetEnvironmentVariable("VMP_PARTYPRISON_PROBE");
            if (String.IsNullOrEmpty(root)) throw new InvalidOperationException("Public handoff fixture requires its isolated probe directory.");
            check(Player.m_localPlayer == null && Game.instance == null, "handoff retry fixture has no live world or player");
            MethodInfo publish = typeof(Plugin).GetMethod("PublishCustodyAccess", All);
            check(publish != null && publish.IsStatic, "public handoff fixture uses the production access-only publication seam");
            var fixture = new Harmony("valheimmodpack.partyprison.publicationretry." + Guid.NewGuid().ToString("N"));
            ZDO[] chests = new ZDO[4];
            try {
                fixture.Patch(typeof(ZDO).GetMethod("IncreaseDataRevision", All),
                    prefix: new HarmonyMethod(typeof(RecoveryNativeChecks).GetMethod("DetachedPublicationRevision", All)));
                string token = Guid.NewGuid().ToString("N"), account = "Steam_76561198000000032";
                for (int index = 0; index < chests.Length; ++index) {
                    ZDO chest = new ZDO(); chest.m_uid = new ZDOID(-92873501, (uint)(index + 1)); PublicationZdos.Add(chest); chests[index] = chest;
                    chest.Set(CustodyInventory.TokenKey, token); chest.Set(CustodyInventory.PublicKey, false);
                    // Deliberately opaque bytes: public access must not decode,
                    // compare or reconstruct already handed-off inventories.
                    chest.Set(ZDOVars.s_items, new byte[] { 91, (byte)index });
                }
                using (var store = new CustodyStore(Path.Combine(root, "RecoveryPublication-" + token), 57891)) {
                    store.Prepare(account, token, new byte[] { 1 }); store.MarkCleared(account, token, 1);
                    store.MarkDeposited(account, token, chests.Select(chest => chest.m_uid.ToString()).ToArray());
                    CustodyRecord state = store.FindState(account, token);
                    int durableMarks = 0, flagWrites = 0;
                    Action mark = () => { ++durableMarks; store.MarkPublicAccess(account, token); };
                    Action<ZDO[]> partial = values => {
                        for (int index = 0; index < values.Length; ++index) {
                            if (index == 1) throw new InvalidOperationException("Controlled partial native access failure");
                            values[index].Set(CustodyInventory.PublicKey, true); ++flagWrites;
                        }
                    };
                    bool failed = false;
                    try { publish.Invoke(null, new object[] { state, chests, mark, partial }); }
                    catch (TargetInvocationException error) {
                        if (!(error.InnerException is InvalidOperationException)) throw;
                        failed = true;
                    }
                    check(failed && durableMarks == 1 && flagWrites == 1 && store.FindState(account, token).PublicAccess
                        && CustodyInventory.IsPublic(chests[0]) && chests.Skip(1).All(chest => !CustodyInventory.IsPublic(chest)),
                        "a partial native flag failure follows durable handoff and leaves a retryable access boundary");
                    // Ordinary access can change one opened chest before retry.
                    chests[0].Set(ZDOVars.s_items, new byte[] { 19 });
                    byte[][] afterTheft = chests.Select(chest => (byte[])chest.GetByteArray(ZDOVars.s_items, null).Clone()).ToArray();
                    state = store.FindState(account, token);
                    Action<ZDO[]> reopen = values => {
                        foreach (ZDO chest in values) { chest.Set(CustodyInventory.PublicKey, true); ++flagWrites; }
                    };
                    publish.Invoke(null, new object[] { state, chests, mark, reopen });
                    check(durableMarks == 1 && chests.All(CustodyInventory.IsPublic)
                        && chests.Select((chest, index) => chest.GetByteArray(ZDOVars.s_items, null).SequenceEqual(afterTheft[index])).All(same => same),
                        "public retry reopens all assigned chests without replaying or inspecting items changed by ordinary access");
                    publish.Invoke(null, new object[] { state, chests, mark, reopen });
                    check(durableMarks == 1 && !store.HasOutstanding && chests.All(CustodyInventory.IsPublic),
                        "repeated public flag repair creates no new journal handoff or collection debt");
                    string previous = chests[3].GetString(CustodyInventory.TokenKey, ""); chests[3].Set(CustodyInventory.TokenKey, Guid.NewGuid().ToString("N"));
                    int writesBefore = flagWrites; failed = false;
                    try { publish.Invoke(null, new object[] { state, chests, mark, reopen }); }
                    catch (TargetInvocationException error) { if (!(error.InnerException is InvalidDataException)) throw; failed = true; }
                    check(failed && flagWrites == writesBefore && durableMarks == 1,
                        "access-only retry refuses a chest assigned to another sentence before changing any native flags");
                    chests[3].Set(CustodyInventory.TokenKey, previous);
                }
            }
            finally {
                foreach (ZDO chest in PublicationZdos) typeof(ZDO).GetMethod("Reset", All).Invoke(chest, null);
                PublicationZdos.Clear(); fixture.UnpatchSelf();
            }
            check(Player.m_localPlayer == null && Game.instance == null, "handoff retry fixture releases all detached native records");
        }

        private static void CheckDurableReceipt(Action<bool, string> check)
        {
            var fixture = new Harmony("valheimmodpack.partyprison.recoveryreceipt." + Guid.NewGuid().ToString("N"));
            var prefix = new HarmonyMethod(typeof(RecoveryNativeChecks).GetMethod("DetachedPlayerLifecycle", All));
            MethodInfo awake = AccessTools.Method(typeof(Player), "Awake"), destroy = AccessTools.Method(typeof(Player), "OnDestroy");
            GameObject node = null;
            try
            {
                fixture.Patch(awake, prefix: prefix); if (destroy != null) fixture.Patch(destroy, prefix: prefix);
                node = new GameObject("PartyPrison.RecoveryReceipt.DetachedPlayer"); node.SetActive(false); DetachedPlayers.Add(node);
                Player player = node.AddComponent<Player>();
                FieldInfo custom = AccessTools.Field(typeof(Player), "m_customData");
                var data = new Dictionary<string, string>(); custom.SetValue(player, data);
                const long world = 123; string token = Guid.NewGuid().ToString("N"), hash = new string('a', 64);
                data[CustodyInventory.ReceiptKey] = world.ToString("x16") + ":" + token + ":" + hash;
                string found;
                check(CustodyInventory.HasClearReceipt(player, world, token) && CustodyInventory.TryGetClearReceiptHash(player, world, token, out found) && found == hash, "emergency acknowledgement reads the exact durable world/token receipt without transient state metadata");
                check(!CustodyInventory.HasClearReceipt(player, world, token, ""), "the emergency receipt helper covers an empty transient custody hash");
                check(!CustodyInventory.HasClearReceipt(player, world + 1, token) && !CustodyInventory.HasClearReceipt(player, world, Guid.NewGuid().ToString("N")), "a receipt from another world or sentence cannot authorize cleanup");
                data[CustodyInventory.ReceiptKey] = world.ToString("x16") + ":" + token + ":" + new string('X', 64);
                check(!CustodyInventory.HasClearReceipt(player, world, token), "a malformed durable confiscation hash is rejected");
                check(Player.m_localPlayer == null, "detached receipt fixture never becomes a live local player");
            }
            finally
            {
                if (node != null) { UnityEngine.Object.DestroyImmediate(node); DetachedPlayers.Remove(node); }
                fixture.UnpatchSelf();
            }
        }

        private static ItemDrop.ItemData Item(string prefabName, int count, int x, int y)
        {
            GameObject prefab = ObjectDB.instance.GetItemPrefab(prefabName);
            if (prefab == null) throw new InvalidOperationException("Missing native recovery fixture item: " + prefabName);
            ItemDrop.ItemData item = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
            item.m_dropPrefab = prefab; item.m_stack = count; item.m_gridPos = new Vector2i(x, y); item.m_equipped = false;
            if (item.m_customData == null) item.m_customData = new Dictionary<string, string>();
            return item;
        }

        private static object Backpack(ItemDrop.ItemData item)
        {
            if (backpackApi == null) throw new InvalidOperationException("Actual AdventureBackpacks API is unavailable for its optional fixture.");
            Type extensions = backpackApi.Assembly.GetType("Vapok.Common.Managers.ItemExtensions", true), component = backpackApi.Assembly.GetType("AdventureBackpacks.Components.BackpackComponent", true);
            object holder = extensions.GetMethod("Data", new[] { typeof(ItemDrop.ItemData) }).Invoke(null, new object[] { item });
            return holder.GetType().GetMethod("GetOrCreate").MakeGenericMethod(component).Invoke(holder, new object[] { "" });
        }
        private static void PutBackpackContents(ItemDrop.ItemData bag, Inventory contents)
        {
            object component = Backpack(bag); component.GetType().GetMethod("SetInventory").Invoke(component, new object[] { contents });
            component.GetType().GetMethod("Serialize").Invoke(component, null);
        }
        private static Inventory Contents(ItemDrop.ItemData bag)
        { object component = Backpack(bag); return (Inventory)component.GetType().GetMethod("GetInventory").Invoke(component, null); }
        private static byte[] Save(Inventory inventory)
        { var package = new ZPackage(); inventory.Save(package); return package.GetArray(); }
        private static void WriteText(BinaryWriter writer, string value)
        { byte[] bytes = Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }

        private static void CheckNestedBackpackGear(Action<bool, string> check)
        {
            if (backpackApi == null) { BackpackFixtureSkipped = true; return; }
            check(backpackApi.Assembly.GetType("Vapok.Common.Managers.ItemExtensions", false) != null
                && backpackApi.Assembly.GetType("AdventureBackpacks.Components.BackpackComponent", false) != null,
                "optional recovery backpack fixture uses the exact AdventureBackpacks embedded persistence API");
            GameObject prefab = null;
            foreach (GameObject candidate in ObjectDB.instance.m_items.Where(item => item != null && item.name.StartsWith("Backpack", StringComparison.Ordinal) && item.GetComponent<ItemDrop>() != null))
            {
                Inventory actual = Contents(Item(candidate.name, 1, 0, 0));
                if (actual.GetWidth() * actual.GetHeight() >= 5) { prefab = candidate; break; }
            }
            check(prefab != null, "actual Adventure Backpacks prefab is available for equipment expiration");
            const long world = 123; string token = Guid.NewGuid().ToString("N");
            Inventory root = new Inventory("Recovery root", null, 8, 4);
            ItemDrop.ItemData innerBag = InitializeNativeItem(Item(prefab.name, 1, 0, 0)), outerBag = InitializeNativeItem(Item(prefab.name, 1, 0, 0));
            Inventory nativeCapacity = Contents(innerBag), outer = Contents(outerBag);
            Inventory inner = new Inventory("Recovery inner bag", null, nativeCapacity.GetWidth(), nativeCapacity.GetHeight());
            check(inner.GetWidth() * inner.GetHeight() >= 5, "nested fixture uses the backpack's actual configured native capacity");
            ItemDrop.ItemData farm = Item("Iron", 12, 0, 0), personal = Item("SwordBronze", 1, 0, 0), old = Item("SwordBronze", 1, 0, 0), current = Item("HelmetLeather", 1, 0, 0), legacy = Item("HelmetLeather", 1, 0, 0);
            personal.m_customData["recovery_fixture_personal"] = "preserved";
            old.m_customData[ArenaBuilder.GearKey] = PrisonGearPolicy.Tag(world, token, 1); current.m_customData[ArenaBuilder.GearKey] = PrisonGearPolicy.Tag(world, token, 2); legacy.m_customData[ArenaBuilder.KitStockKey] = token;
            foreach (ItemDrop.ItemData item in new[] { farm, personal, old, current, legacy })
                check(inner.AddItem(item), "native backpack fixture admits each resource and equipment item into a valid slot");
            // Match real kit creation and native container loading, including
            // lazy Epic Loot metadata, before testing persistence or expiry.
            inner.Load(new ZPackage(Save(inner)));
            check(inner.GetAllItems().Count == 5, "all five native fixture stacks survive ordinary equipment initialization");
            PutBackpackContents(innerBag, inner); check(outer.AddItem(innerBag), "native outer backpack admits the inner bag");
            outer.Load(new ZPackage(Save(outer))); PutBackpackContents(outerBag, outer); check(root.AddItem(outerBag), "native root admits the outer backpack");
            root.Load(new ZPackage(Save(root))); outerBag = root.GetAllItems().Single();
            Inventory initialInner = Contents(Contents(outerBag).GetAllItems().Single());
            check(initialInner.GetAllItems().Count == 5 && initialInner.GetAllItems().Any(item => item.m_customData.ContainsKey(ArenaBuilder.GearKey) && item.m_customData[ArenaBuilder.GearKey] == PrisonGearPolicy.Tag(world, token, 2)), "valid native nested bags retain the complete source kit before any expiration");
            check(CustodyInventory.HasObsoleteBackpackGear(root, world, token, 2), "a read-only nested gear check finds obsolete equipment before a public chest ownership claim");
            check(CustodyInventory.ExpireBackpackGear(root, world, token, 2) == 2, "nested backpack expiration removes only obsolete and legacy issued gear");
            check(!CustodyInventory.HasObsoleteBackpackGear(root, world, token, 2), "a read-only nested check is clear after expiry without removing current issued gear");
            Inventory nativeInner = Contents(Contents(outerBag).GetAllItems().Single());
            check(nativeInner.GetAllItems().Count == 3 && nativeInner.GetAllItems().Any(item => item.m_dropPrefab.name == "Iron" && item.m_stack == 12)
                && nativeInner.GetAllItems().Any(item => item.m_customData.ContainsKey("recovery_fixture_personal")) && nativeInner.GetAllItems().Any(item => item.m_customData.ContainsKey(ArenaBuilder.GearKey)), "farm, personal same-prefab weapon and current issued armor survive nested expiration");
            Inventory reloaded = DecodeWithDiagnostics(Save(root)); ItemDrop.ItemData restoredOuterBag = reloaded.GetAllItems().Single();
            Inventory restoredInner = Contents(Contents(restoredOuterBag).GetAllItems().Single());
            check(restoredInner.GetAllItems().Count == 3 && restoredInner.GetAllItems().Any(item => item.m_dropPrefab.name == "Iron" && item.m_stack == 12), "nested equipment removal is durable in the outer bag native item payload");
            check(CustodyInventory.ExpireBackpackGear(reloaded, world, "", 0) == 1, "release removes the remaining issued armor inside a nested backpack");
            restoredInner = Contents(Contents(restoredOuterBag).GetAllItems().Single());
            check(restoredInner.GetAllItems().Count == 2 && restoredInner.GetAllItems().Any(item => item.m_customData.ContainsKey("recovery_fixture_personal")), "release retains farm and the player's own weapon inside nested bags");
            check(CustodyInventory.ExpireBackpackGear(reloaded, world, "", 0) == 0, "repeated nested release cleanup is idempotent");
        }
        private static ItemDrop.ItemData InitializeNativeItem(ItemDrop.ItemData item)
        {
            var native = new Inventory("Recovery native item initialization", null, 8, 4);
            if (!native.AddItem(item)) throw new InvalidOperationException("Native recovery item initialization refused its item.");
            native.Load(new ZPackage(Save(native))); return native.GetAllItems().Single();
        }
        private static Inventory DecodeWithDiagnostics(byte[] bytes)
        {
            try { return CustodyInventory.Decode(bytes); }
            catch (InvalidDataException failure)
            {
                var loaded = new Inventory("Recovery diagnostic", null, 255, 255); loaded.Load(new ZPackage(bytes));
                var details = new StringBuilder(); InventoryDiff(bytes, Save(loaded), "root", 0, details);
                throw new InvalidDataException(failure.Message + " Recovery fixture metadata diff: " + details, failure);
            }
        }
        private static void InventoryDiff(byte[] left, byte[] right, string path, int depth, StringBuilder details)
        {
            var l = ReadItems(left); var r = ReadItems(right);
            details.Append(path).Append(" counts ").Append(l.Count).Append('/').Append(r.Count).Append("; ");
            for (int i = 0; i < Math.Min(l.Count, r.Count); ++i)
            {
                foreach (string key in l[i].Custom.Keys.Union(r[i].Custom.Keys))
                {
                    string lv, rv; l[i].Custom.TryGetValue(key, out lv); r[i].Custom.TryGetValue(key, out rv);
                    if (lv == rv) continue;
                    details.Append(path).Append('[').Append(i).Append("] ").Append(key).Append(" lengths=").Append(lv == null ? -1 : lv.Length).Append('/').Append(rv == null ? -1 : rv.Length).Append("; ");
                    if (depth < 4 && key.Contains("AdventureBackpacks.Components.BackpackComponent") && !String.IsNullOrEmpty(lv) && !String.IsNullOrEmpty(rv))
                        InventoryDiff(Convert.FromBase64String(lv), Convert.FromBase64String(rv), path + "/bag", depth + 1, details);
                }
            }
        }
        private static List<ValheimModPack.WorldCharacters.StoredItem> ReadItems(byte[] bytes)
        { using (var stream = new MemoryStream(bytes, false)) using (var reader = new BinaryReader(stream)) return ValheimModPack.WorldCharacters.NativeInventory.ReadInventory(reader); }
    }
}
