// Test-only detached inventories and ZDO records; no world or character is opened.
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
    public static class KitStorageNativeChecks
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static readonly HashSet<ZDO> records = new HashSet<ZDO>();
        private static readonly HashSet<GameObject> objects = new HashSet<GameObject>();
        private static Container nativeContainer;
        private static int nativeSaves;
        private static int nextId;

        private static bool DetachedRevision(ZDO __instance)
        {
            if (!records.Contains(__instance)) return true;
            FieldInfo revision = typeof(ZDO).GetField("<DataRevision>k__BackingField", All);
            revision.SetValue(__instance, unchecked((uint)revision.GetValue(__instance) + 1)); return false;
        }
        private static bool SkipAwake(Component __instance)
        { return __instance == null || !objects.Contains(__instance.gameObject); }
        private static bool NativeOwner(Container __instance, ref bool __result)
        { if (__instance != nativeContainer) return true; __result = true; return false; }
        private static void CountSave(Container __instance) { if (__instance == nativeContainer) ++nativeSaves; }

        public static void Run(Action<bool, string> check)
        {
            check(Player.m_localPlayer == null && Game.instance == null, "strict kit fixture never opens a character or world");
            var harmony = new Harmony("valheimmodpack.partyprison.nativeprobe.kitstorage");
            try {
                harmony.Patch(typeof(ZDO).GetMethod("IncreaseDataRevision", All), prefix: new HarmonyMethod(typeof(KitStorageNativeChecks).GetMethod("DetachedRevision", All)));
                harmony.Patch(typeof(ZNetView).GetMethod("Awake", All), prefix: new HarmonyMethod(typeof(KitStorageNativeChecks).GetMethod("SkipAwake", All)));
                harmony.Patch(typeof(Container).GetMethod("Awake", All), prefix: new HarmonyMethod(typeof(KitStorageNativeChecks).GetMethod("SkipAwake", All)));
                harmony.Patch(typeof(Container).GetMethod("IsOwner", All), prefix: new HarmonyMethod(typeof(KitStorageNativeChecks).GetMethod("NativeOwner", All)));
                harmony.Patch(typeof(Container).GetMethod("Save", All), prefix: new HarmonyMethod(typeof(KitStorageNativeChecks).GetMethod("CountSave", All)));
                CheckRejectedStorage(check); CheckReplacement(check); CheckRollback(check); CheckNativeCallbacks(check);
            }
            finally {
                foreach (GameObject value in objects) if (value != null) UnityEngine.Object.DestroyImmediate(value);
                foreach (ZDO record in records) typeof(ZDO).GetMethod("Reset", All).Invoke(record, null);
                objects.Clear(); records.Clear(); nativeContainer = null; harmony.UnpatchSelf();
            }
            check(Player.m_localPlayer == null && Game.instance == null, "strict kit fixtures release all detached native records and callbacks");
        }

        private static Inventory Inventory(string name = "Kit storage native", int width = 8, int height = 4)
        { return new Inventory(name, null, width, height); }
        private static byte[] Save(Inventory inventory)
        { var package = new ZPackage(); inventory.Save(package); return package.GetArray(); }
        private static ItemDrop.ItemData Item(string prefabName, int count, int x, int y)
        {
            GameObject prefab = PrefabManager.Instance.GetPrefab(prefabName);
            if (prefab == null || prefab.GetComponent<ItemDrop>() == null) throw new InvalidOperationException("Missing fixture item: " + prefabName);
            ItemDrop.ItemData item = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
            item.m_dropPrefab = prefab; item.m_stack = count; item.m_gridPos = new Vector2i(x, y);
            item.m_customData = item.m_customData == null ? new Dictionary<string, string>() : new Dictionary<string, string>(item.m_customData);
            return item;
        }
        private static Inventory Personal()
        {
            Inventory value = Inventory();
            ItemDrop.ItemData sword = Item("SwordBronze", 1, 0, 0); sword.m_quality = 2; sword.m_durability = 17.25f;
            sword.m_crafterID = 12345; sword.m_crafterName = "Кузнец"; sword.m_customData["kit_personal"] = "preserve ☃";
            ItemDrop.ItemData food = Item("Sausages", 7, 1, 0); food.m_customData["kit_personal_food"] = "ordinary";
            ItemDrop.ItemData wood = Item("Wood", 13, 2, 0);
            ItemDrop.ItemData loan = Item(CombatCatalog.FoodPrefab("Sausages"), 1, 3, 0);
            loan.m_customData[ArenaBuilder.GearKey] = PrisonGearPolicy.Tag(6789, new string('4', 32), 1);
            loan.m_customData[ArenaBuilder.KitStockKey] = new string('4', 32);
            value.GetAllItems().AddRange(new[] { sword, food, wood, loan });
            // Warm native/vendor load bookkeeping before exact local rollback
            // byte assertions; the durable source must never be rewritten.
            value.Load(new ZPackage(Save(value))); return value;
        }
        private static ZDO Record(byte[] payload, string legacy = null)
        {
            var zdo = new ZDO { m_uid = new ZDOID(-643591875, (uint)++nextId) }; records.Add(zdo);
            typeof(ZDO).GetField("m_prefab", All).SetValue(zdo, ArenaBuilder.CustodyPrefab.GetStableHashCode());
            if (payload != null) zdo.Set(ZDOVars.s_items, payload);
            if (legacy != null) zdo.Set(ZDOVars.s_items, legacy);
            return zdo;
        }
        private static bool Rejected(Action action)
        { try { action(); return false; } catch (Exception error) { if (error is InvalidDataException || error is InvalidOperationException || error is EndOfStreamException || error is AggregateException) return true; throw; } }
        private static bool Equal(byte[] a, byte[] b) { return a == null ? b == null : b != null && a.SequenceEqual(b); }
        private static byte[] UnknownPrefab(byte[] original)
        {
            byte[] altered = (byte[])original.Clone();
            using (var stream = new MemoryStream(altered, true)) using (var reader = new BinaryReader(stream)) {
                int version = reader.ReadInt32(); if (version != 108 && version != 109 || reader.ReadUInt16() == 0) throw new InvalidOperationException("Unexpected native fixture format.");
                reader.ReadInt32(); reader.ReadByte(); reader.ReadByte(); reader.ReadByte(); int flags = reader.ReadByte();
                if ((flags & 4) != 0) reader.ReadUInt16(); if ((flags & 8) != 0) reader.ReadUInt16();
                if ((flags & 16) != 0) reader.ReadInt32(); if ((flags & 32) != 0) { reader.ReadInt64(); reader.ReadString(); }
                if ((flags & 64) == 0) throw new InvalidOperationException("Missing native fixture prefab field.");
                Array.Copy(BitConverter.GetBytes("vmp_missing_kit_fixture_item".GetStableHashCode()), 0, altered, (int)stream.Position, 4);
            }
            return altered;
        }

        private static void CheckRejectedStorage(Action<bool, string> check)
        {
            byte[] valid = Save(Personal());
            foreach (byte[] bad in new[] { UnknownPrefab(valid), new byte[] { 1, 2, 3 }, valid.Concat(new byte[] { 123 }).ToArray() }) {
                ZDO zdo = Record(bad); uint revision = zdo.DataRevision;
                check(Rejected(() => new PrisonKitStorage(zdo, Inventory())), "unknown, malformed or trailing personal storage is rejected before native Load");
                check(Equal(bad, zdo.GetByteArray(ZDOVars.s_items, null)) && zdo.DataRevision == revision,
                    "failed strict preflight preserves original item bytes and native ZDO revision");
            }
            Inventory hidden = Personal(); hidden.GetAllItems()[0].m_gridPos = new Vector2i(8, 0);
            byte[] hiddenBytes = Save(hidden); ZDO hiddenZdo = Record(hiddenBytes);
            check(Rejected(() => new PrisonKitStorage(hiddenZdo, Inventory())) && Equal(hiddenBytes, hiddenZdo.GetByteArray(ZDOVars.s_items, null)),
                "a hidden slot beyond the current native container dimensions fails closed without deleting its saved item");
            ZDO corruptLegacy = Record(null, "invalid base64?!");
            check(Rejected(() => new PrisonKitStorage(corruptLegacy, Inventory())) && corruptLegacy.GetString(ZDOVars.s_items, "") == "invalid base64?!"
                && corruptLegacy.GetByteArray(ZDOVars.s_items, null) == null, "corrupt legacy storage stays untouched rather than becoming an empty kit");
            ZDO legacy = Record(null, Convert.ToBase64String(valid));
            Inventory legacyCopy = new PrisonKitStorage(legacy, Inventory()).WorkingCopy(Inventory());
            check(legacyCopy.GetAllItems().Count == 4 && legacy.GetByteArray(ZDOVars.s_items, null) == null,
                "valid legacy base64 storage supplies a strict detached candidate without premature native conversion");
            ZDO authoritative = Record(valid, "stale invalid legacy value"); Inventory source = Personal();
            check(new PrisonKitStorage(authoritative, source).WorkingCopy(source).GetAllItems().Count == 4,
                "native byte payload remains authoritative when a stale legacy string coexists");
            ZDO changed = Record(valid); Inventory different = Personal(); different.GetAllItems()[0].m_customData["kit_personal"] = "altered";
            var transaction = new PrisonKitStorage(changed, different);
            check(Rejected(() => transaction.WorkingCopy(different)) && Equal(valid, changed.GetByteArray(ZDOVars.s_items, null)),
                "a local native load that changed personal metadata cannot replace authoritative storage");
        }

        private static void CheckReplacement(Action<bool, string> check)
        {
            Inventory source = Personal(); byte[] before = Save(source); ZDO zdo = Record(before);
            var storage = new PrisonKitStorage(zdo, source); Inventory candidate = storage.WorkingCopy(source);
            check(ArenaBuilder.RemoveObsoleteInventoryGear(candidate, 6789, new string('4', 32), 2) == 1,
                "equipment mutation removes only an expired loan ration from its detached candidate");
            ItemDrop.ItemData food = Item(CombatCatalog.FoodPrefab("TurnipStew"), 1, 3, 0);
            food.m_customData[ArenaBuilder.GearKey] = PrisonGearPolicy.Tag(6789, new string('4', 32), 2); candidate.GetAllItems().Add(food);
            int writes = 0; storage.PublishCore(source, candidate, action => action(), () => { ++writes; zdo.Set(ZDOVars.s_items, Save(source)); }, null);
            check(writes == 1 && source.GetAllItems().Count == 4 && source.GetAllItems().Any(item => item.m_dropPrefab.name == food.m_dropPrefab.name),
                "a proven candidate is published exactly once with the current separate food prefab");
            ItemDrop.ItemData sword = source.GetAllItems().Single(item => item.m_customData.ContainsKey("kit_personal"));
            check(sword.m_quality == 2 && sword.m_durability == 17.25f && sword.m_crafterID == 12345 && sword.m_crafterName == "Кузнец"
                && sword.m_customData["kit_personal"] == "preserve ☃", "strict equipment replacement retains personal quality, wear, crafter and arbitrary custom metadata");
            check(source.GetAllItems().Any(item => item.m_dropPrefab.name == "Sausages" && item.m_stack == 7)
                && source.GetAllItems().Any(item => item.m_dropPrefab.name == "Wood" && item.m_stack == 13), "personal food and farm resources survive kit replacement unchanged");
            check(Equal(Save(source), zdo.GetByteArray(ZDOVars.s_items, null)) && !Equal(before, zdo.GetByteArray(ZDOVars.s_items, null)),
                "successful native backing bytes match the validated replacement rather than an old snapshot");
            Inventory full = Inventory("full", 1, 1); full.GetAllItems().Add(Item("Wood", 1, 0, 0)); byte[] fullBytes = Save(full); ZDO fullZdo = Record(fullBytes);
            Inventory fullCopy = new PrisonKitStorage(fullZdo, full).WorkingCopy(full);
            check(!fullCopy.AddItem(Item("SwordBronze", 1, 0, 0)) && Equal(fullBytes, fullZdo.GetByteArray(ZDOVars.s_items, null)) && Equal(fullBytes, Save(full)),
                "insufficient candidate capacity preserves the live inventory and original stored personal items");
        }

        private static void CheckRollback(Action<bool, string> check)
        {
            for (int mode = 0; mode < 4; ++mode) {
                Inventory source = Personal(); byte[] before = Save(source); ZDO zdo = Record(before);
                var storage = new PrisonKitStorage(zdo, source); Inventory candidate = storage.WorkingCopy(source); candidate.GetAllItems().RemoveAt(3);
                int calls = 0, currentMode = mode; bool reload = false;
                check(Rejected(() => storage.PublishCore(source, candidate, action => {
                    ++calls; action();
                    if (calls == 1 && currentMode == 0) source.GetAllItems()[0].m_customData["kit_personal"] = "vendor mutation";
                    if (calls == 2 && currentMode == 3) throw new InvalidOperationException("simulated rollback callback failure");
                }, () => {
                    if (currentMode == 1) throw new InvalidOperationException("simulated save failure");
                    zdo.Set(ZDOVars.s_items, Save(Inventory()));
                    if (currentMode == 3) throw new InvalidOperationException("simulated save failure after write");
                }, force => reload = force)), "metadata mutation, save exception, silent shortened persistence and rollback failure cannot commit a kit");
                check(Equal(before, zdo.GetByteArray(ZDOVars.s_items, null)), "transaction rollback restores exact original durable bytes even after the vendor wrote a shortened payload");
                if (mode != 3) check(Equal(before, Save(source)) && !reload, "successful rollback restores the original native inventory without requesting another reload");
                else check(reload, "a failed local rollback forces a native reload while retaining the complete durable inventory");
            }
            Inventory empty = Inventory(); byte[] original = Save(Personal()); string legacy = Convert.ToBase64String(original); ZDO legacyZdo = Record(null, legacy);
            var legacyStorage = new PrisonKitStorage(legacyZdo, empty); Inventory legacyCandidate = legacyStorage.WorkingCopy(empty);
            check(Rejected(() => legacyStorage.PublishCore(empty, legacyCandidate, action => action(), () => {
                legacyZdo.Set(ZDOVars.s_items, Save(empty)); throw new InvalidOperationException("legacy publication failure");
            }, null)) && legacyZdo.GetByteArray(ZDOVars.s_items, null) == null && legacyZdo.GetString(ZDOVars.s_items, "") == legacy && empty.GetAllItems().Count == 0,
                "failed legacy conversion restores original string storage and removes any prematurely added native bytes");
        }

        private static void CheckNativeCallbacks(Action<bool, string> check)
        {
            Inventory source = Personal(); ZDO zdo = Record(Save(source));
            var gameObject = new GameObject("PartyPrison.NativeFixture.KitStorage"); objects.Add(gameObject); gameObject.SetActive(false);
            ZNetView view = gameObject.AddComponent<ZNetView>(); nativeContainer = gameObject.AddComponent<Container>();
            typeof(ZNetView).GetField("m_zdo", All).SetValue(view, zdo);
            typeof(Container).GetField("m_nview", All).SetValue(nativeContainer, view);
            typeof(Container).GetField("m_inventory", All).SetValue(nativeContainer, source);
            source.m_onChanged += (Action)Delegate.CreateDelegate(typeof(Action), nativeContainer, typeof(Container).GetMethod("OnContainerChanged", All));
            var storage = new PrisonKitStorage(zdo, source); Inventory candidate = storage.WorkingCopy(source); candidate.GetAllItems().RemoveAt(3);
            nativeSaves = 0; storage.Publish(nativeContainer, candidate);
            check(nativeSaves == 1 && !(bool)typeof(Container).GetField("m_loading", All).GetValue(nativeContainer),
                "real native Container callbacks are suppressed during verification then saved once with loading state restored");
            check(Equal(Save(source), zdo.GetByteArray(ZDOVars.s_items, null))
                && (uint)typeof(Container).GetField("m_lastRevision", All).GetValue(nativeContainer) == zdo.DataRevision,
                "native container publication writes the real backing ZDO and synchronizes its loaded revision");
        }
    }
}
