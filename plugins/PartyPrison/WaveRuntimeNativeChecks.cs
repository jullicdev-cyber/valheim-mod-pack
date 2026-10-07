// Test-only detached native ZDOs/characters; never include in the released mod.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;

namespace ValheimModPack.PartyPrison.NativeVerification
{
    public static class WaveRuntimeNativeChecks
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private const string Token = "caf341ac70a047e4ac0149896f160f88", OtherToken = "ca0544a45e244341888374a2ed90879d";
        private static readonly HashSet<ZDO> records = new HashSet<ZDO>();
        private static readonly HashSet<GameObject> nodes = new HashSet<GameObject>();
        private static uint identity;
        private static readonly MethodInfo Read = Method("ReadWaveProgressZdo", typeof(ZDO));
        private static readonly MethodInfo Write = Method("WriteWaveProgressZdo", typeof(ZDO), typeof(ArenaWaveProgress));
        private static readonly MethodInfo Current = Method("IsCurrentWaveMob", typeof(ZDO), typeof(string), typeof(int), typeof(long));
        private static readonly MethodInfo Live = Method("IsLiveCurrentWaveMob", typeof(ZDO), typeof(string), typeof(int), typeof(long));

        private static MethodInfo Method(string name, params Type[] parameters)
        {
            MethodInfo method = typeof(ArenaBuilder).GetMethod(name, All, null, parameters, null);
            if (method == null) throw new MissingMethodException(typeof(ArenaBuilder).FullName, name);
            return method;
        }
        private static object Invoke(MethodInfo method, params object[] arguments)
        {
            try { return method.Invoke(null, arguments); }
            catch (TargetInvocationException error) { throw error.InnerException; }
        }
        private static bool DetachedRevision(ZDO __instance)
        {
            if (!records.Contains(__instance)) return true;
            FieldInfo revision = typeof(ZDO).GetField("<DataRevision>k__BackingField", All);
            revision.SetValue(__instance, unchecked((uint)revision.GetValue(__instance) + 1)); return false;
        }
        private static bool SkipDetachedForceSend(ZDOID __0)
        { return !records.Any(record => record.m_uid == __0); }
        private static bool SkipLifecycle(Component __instance)
        { return __instance == null || !nodes.Contains(__instance.gameObject); }

        public static void Run(Action<bool, string> check)
        {
            check(Player.m_localPlayer == null && Game.instance == null, "wave runtime fixtures never enter a world or create a live character");
            Character[] before = Character.GetAllCharacters().ToArray();
            var fixture = new Harmony("valheimmodpack.partyprison.nativeprobe.wave-runtime");
            try {
                fixture.Patch(typeof(ZDO).GetMethod("IncreaseDataRevision", All),
                    prefix: new HarmonyMethod(typeof(WaveRuntimeNativeChecks).GetMethod("DetachedRevision", All)));
                fixture.Patch(typeof(ZDOMan).GetMethod("ForceSendZDO", All, null, new[] { typeof(ZDOID) }, null),
                    prefix: new HarmonyMethod(typeof(WaveRuntimeNativeChecks).GetMethod("SkipDetachedForceSend", All)) { priority = Priority.First });
                var lifecycle = new HarmonyMethod(typeof(WaveRuntimeNativeChecks).GetMethod("SkipLifecycle", All)) { priority = Priority.First };
                var seen = new HashSet<MethodInfo>();
                foreach (Type type in new[] { typeof(ZNetView), typeof(Character), typeof(Humanoid), typeof(Player) })
                    foreach (string name in new[] { "Awake", "OnDestroy" }) {
                        MethodInfo method = AccessTools.Method(type, name, Type.EmptyTypes);
                        if (method != null && seen.Add(method)) fixture.Patch(method, prefix: lifecycle);
                    }
                CheckProgressPersistence(check); CheckWaveMobIdentity(check); CheckAlliances(check);
            }
            finally {
                foreach (GameObject node in nodes.ToArray()) UnityEngine.Object.DestroyImmediate(node);
                nodes.Clear();
                foreach (ZDO record in records) typeof(ZDO).GetMethod("Reset", All).Invoke(record, null);
                records.Clear(); fixture.UnpatchSelf();
            }
            check(Player.m_localPlayer == null && Game.instance == null && Character.GetAllCharacters().SequenceEqual(before),
                "wave fixtures leave native player, world and character registrations unchanged");
        }
        private static ZDO Record(string prefab)
        {
            var record = new ZDO { m_uid = new ZDOID(-643591880, ++identity) };
            records.Add(record); typeof(ZDO).GetField("m_prefab", All).SetValue(record, prefab.GetStableHashCode());
            return record;
        }
        private static ZDO NativeRoundtrip(ZDO record)
        {
            var package = new ZPackage(); record.Serialize(package);
            ZDO restored = Record("WaveFixture.Placeholder"); restored.Deserialize(new ZPackage(package.GetArray())); return restored;
        }
        private static ArenaWaveProgress Load(ZDO record)
        { return (ArenaWaveProgress)Invoke(Read, record); }
        private static void Store(ZDO record, ArenaWaveProgress state)
        { Invoke(Write, record, state); }
        private static bool Same(ArenaWaveProgress first, ArenaWaveProgress second)
        {
            return first.Completed == second.Completed && first.Expected == second.Expected && first.Spawned == second.Spawned
                && first.LastSerial == second.LastSerial && first.ActiveSerial == second.ActiveSerial;
        }
        private static bool Rejected(Action action)
        {
            try { action(); return false; }
            catch (ArgumentException) { return true; }
        }
        private static byte[] PersonalChestItems()
        {
            GameObject prefab = PrefabManager.Instance.GetPrefab("Iron");
            if (prefab == null || prefab.GetComponent<ItemDrop>() == null) throw new InvalidOperationException("Missing native fixture iron prefab.");
            ItemDrop.ItemData item = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
            item.m_dropPrefab = prefab; item.m_stack = 7; item.m_gridPos = new Vector2i(3, 2);
            item.m_customData["personal_owner"] = "observer";
            var inventory = new Inventory("Wave fixture personal cell chest contents", null, 6, 4); inventory.GetAllItems().Add(item);
            var package = new ZPackage(); inventory.Save(package); return package.GetArray();
        }
        private static void CheckProgressPersistence(Action<bool, string> check)
        {
            ZDO kit = Record(ArenaBuilder.CustodyPrefab); byte[] personal = PersonalChestItems();
            string legacyPersonal = Convert.ToBase64String(personal);
            kit.Set(ArenaBuilder.KitKey, true); kit.Set(ArenaBuilder.KitTokenKey, Token);
            kit.Set(ArenaBuilder.KitRevisionKey, 7); kit.Set(ArenaBuilder.KitFamilyKey, CombatCatalog.MixedFamily);
            kit.Set(ArenaBuilder.KitDifficultyKey, 0); kit.Set(ZDOVars.s_items, personal);
            kit.Set(ZDOVars.s_items, legacyPersonal);
            kit.Set("fixture_private_chest_note", "Keep every personal item.");
            Store(kit, new ArenaWaveProgress());
            for (int wave = 1; wave <= 10; ++wave) {
                ArenaWaveProgress state = Load(kit);
                int grade = ArenaWaveProgressionPolicy.Difficulty(0, state.Completed), count = grade + 2;
                check(grade == (wave <= 4 ? 0 : wave <= 8 ? 1 : 2), "native saved completed count chooses the fifth and ninth wave grades correctly");
                Store(kit, ArenaWaveProgressionPolicy.Start(state, count));
                for (int spawned = 0; spawned < count; ++spawned) {
                    Store(kit, ArenaWaveProgressionPolicy.SpawnOne(Load(kit)));
                    ArenaWaveProgress expected = Load(kit); kit = NativeRoundtrip(kit);
                    check(Same(Load(kit), expected), "actual native ZDO serialization preserves active serial and every accepted spawn");
                    if (spawned + 1 < count) {
                        ArenaWaveProgress partial = ArenaWaveProgressionPolicy.Resume(Load(kit));
                        check(partial.ActiveSerial == 0 && partial.Completed == wave - 1
                            && partial.LastSerial == wave && partial.Expected == 0 && partial.Spawned == 0,
                            "a partial saved native queue reconciles without credit after host restart");
                    }
                }
                state = Load(kit);
                check(Same(ArenaWaveProgressionPolicy.Resume(state), state), "a fully spawned native saved wave survives reconnect");
                Store(kit, ArenaWaveProgressionPolicy.Complete(state, 1, false));
                check(Load(kit).Completed == wave - 1, "a surviving current wave mob prevents a saved completion credit");
                Store(kit, ArenaWaveProgressionPolicy.Complete(Load(kit), 0, true));
                check(Load(kit).Completed == wave - 1, "a matching pending native queue prevents a saved completion credit");
                Store(kit, ArenaWaveProgressionPolicy.Complete(Load(kit), 0, false));
                kit = NativeRoundtrip(kit); state = Load(kit);
                check(state.Completed == wave && state.ActiveSerial == 0 && state.Expected == 0 && state.Spawned == 0,
                    "defeated wave credit and idle transition survive actual native replication serialization");
                Store(kit, ArenaWaveProgressionPolicy.Complete(state, 0, false));
                check(Load(kit).Completed == wave && kit.GetInt(ArenaBuilder.KitRevisionKey, 0) == 7
                    && kit.GetInt(ArenaBuilder.KitDifficultyKey, -1) == 0 && kit.GetInt(ArenaBuilder.KitFamilyKey, -1) == CombatCatalog.MixedFamily
                    && kit.GetString(ArenaBuilder.KitTokenKey, "") == Token,
                    "duplicate saved completion ticks never double-count or alter the current equipment epoch/base choice");
                check(kit.GetByteArray(ZDOVars.s_items, null).SequenceEqual(personal)
                    && kit.GetString(ZDOVars.s_items, "") == legacyPersonal && kit.GetString("fixture_private_chest_note", "") == "Keep every personal item.",
                    "progress publication preserves exact personal chest bytes and unrelated native metadata");
            }
            Store(kit, ArenaWaveProgressionPolicy.Start(Load(kit), 4));
            Store(kit, ArenaWaveProgressionPolicy.SpawnOne(Load(kit)));
            ArenaWaveProgress partialSaved = Load(NativeRoundtrip(kit));
            Store(kit, ArenaWaveProgressionPolicy.Resume(partialSaved));
            Store(kit, ArenaWaveProgressionPolicy.Complete(Load(kit), 0, false));
            check(Load(kit).Completed == 10 && Load(kit).LastSerial == 11 && Load(kit).ActiveSerial == 0,
                "partial saved spawn cancellation cannot become an eleventh victory when old mobs disappear");
            ArenaWaveProgress before = Load(kit); ArenaWaveProgress invalid = before.Copy(); invalid.Expected = 9;
            check(Rejected(() => Store(kit, invalid)) && Same(before, Load(kit))
                && kit.GetByteArray(ZDOVars.s_items, null).SequenceEqual(personal) && kit.GetString(ZDOVars.s_items, "") == legacyPersonal,
                "production native progress writer rejects a corrupt state before changing any persisted fields");
            kit.Set("VMP_PP_WaveSpawned", -1);
            check(Rejected(() => Load(kit)), "production native progress reader rejects corrupted saved spawn counts");
        }
        private static ZDO Mob(string token, int revision, long serial, float health = 100f)
        {
            ZDO mob = Record("Skeleton"); mob.Set(ArenaBuilder.MobKey, true); mob.Set(ArenaBuilder.MobSentenceKey, token);
            mob.Set(ArenaBuilder.MobRunRevisionKey, revision); mob.Set(ArenaBuilder.MobWaveSerialKey, serial);
            mob.Set(ZDOVars.s_health, health); return mob;
        }
        private static bool IsCurrent(ZDO record, string token, int revision, long serial)
        { return (bool)Invoke(Current, record, token, revision, serial); }
        private static bool IsLive(ZDO record, string token, int revision, long serial)
        { return (bool)Invoke(Live, record, token, revision, serial); }
        private static void CheckWaveMobIdentity(Action<bool, string> check)
        {
            ZDO current = Mob(Token, 7, 12), otherSentence = Mob(OtherToken, 7, 12);
            ZDO oldRun = Mob(Token, 6, 12), futureRun = Mob(Token, 8, 12), oldWave = Mob(Token, 7, 11), futureWave = Mob(Token, 7, 13);
            ZDO ordinary = Record("Skeleton"), dead = Mob(Token, 7, 12), zeroHealth = Mob(Token, 7, 12, 0f), negativeHealth = Mob(Token, 7, 12, -3f);
            dead.Set(ZDOVars.s_dead, true);
            foreach (ZDO wrong in new[] { otherSentence, oldRun, futureRun, oldWave, futureWave, ordinary })
                check(!IsCurrent(wrong, Token, 7, 12) && !IsLive(wrong, Token, 7, 12),
                    "production native wave membership rejects old/foreign/future mob identities and ordinary creatures");
            check(!IsCurrent(null, Token, 7, 12) && !IsLive(null, Token, 7, 12), "missing native mob records cannot join the active wave");
            check(IsCurrent(current, Token, 7, 12) && IsLive(current, Token, 7, 12), "exact saved sentence, revision and serial identify a living wave mob");
            foreach (ZDO corpse in new[] { dead, zeroHealth, negativeHealth })
                check(IsCurrent(corpse, Token, 7, 12) && !IsLive(corpse, Token, 7, 12),
                    "native dead flag and zero/negative health exclude defeated mobs while retaining historical wave membership");
            ZDO restored = NativeRoundtrip(current);
            check(IsCurrent(restored, Token, 7, 12) && IsLive(restored, Token, 7, 12),
                "current wave mob identity survives native ZDO replication and a new detached object identity");
            current.Set(ArenaBuilder.MobKey, false);
            check(!IsCurrent(current, Token, 7, 12) && !IsLive(current, Token, 7, 12), "a marker alone cannot classify an unmarked native creature as a prison mob");
            restored.Set(ZDOVars.s_health, 0f); restored = NativeRoundtrip(restored);
            ZDO[] all = { restored, otherSentence, oldRun, futureRun, oldWave, futureWave, ordinary, dead, zeroHealth, negativeHealth };
            int alive = all.Count(record => IsLive(record, Token, 7, 12));
            var complete = new ArenaWaveProgress { LastSerial = 12, ActiveSerial = 12, Expected = 2, Spawned = 2, Completed = 3 };
            check(alive == 0 && ArenaWaveProgressionPolicy.Complete(complete, alive, false).Completed == 4,
                "living foreign and orphaned native mobs cannot distort the active wave's exact completion count");
        }
        private static Character CharacterFixture(string name, string prefab, ZDO record, bool player)
        {
            GameObject node = new GameObject(name); node.SetActive(false); nodes.Add(node);
            ZNetView view = node.AddComponent<ZNetView>(); typeof(ZNetView).GetField("m_zdo", All).SetValue(view, record);
            Character character = player ? (Character)node.AddComponent<Player>() : node.AddComponent<Character>();
            typeof(Character).GetField("m_nview", All).SetValue(character, view);
            GameObject native = PrefabManager.Instance.GetPrefab(prefab);
            if (native == null || native.GetComponent<Character>() == null) throw new InvalidOperationException("Missing native alliance character prefab: " + prefab);
            character.m_faction = player ? Character.Faction.Players : native.GetComponent<Character>().m_faction;
            character.m_group = ""; return character;
        }
        private static bool NativeEnemy(Character left, Character right)
        {
            MethodInfo native = typeof(BaseAI).GetMethod("IsEnemy", All, null, new[] { typeof(Character), typeof(Character) }, null);
            return (bool)Invoke(native, left, right);
        }
        private static void CheckAlliances(Action<bool, string> check)
        {
            MethodInfo native = typeof(BaseAI).GetMethod("IsEnemy", All, null, new[] { typeof(Character), typeof(Character) }, null);
            Patches patches = Harmony.GetPatchInfo(native);
            check(patches != null && patches.Prefixes.Any(patch => patch.owner == Plugin.Id
                && patch.PatchMethod.DeclaringType.FullName == "ValheimModPack.PartyPrison.PrisonEnemyAlliancePatch"),
                "production alliance prefix is installed on the actual native static BaseAI.IsEnemy method");
            Character forestTemplate = PrefabManager.Instance.GetPrefab("Greyling").GetComponent<Character>();
            Character undeadTemplate = PrefabManager.Instance.GetPrefab("Skeleton").GetComponent<Character>();
            Character.Faction forestFaction = forestTemplate.m_faction, undeadFaction = undeadTemplate.m_faction;
            string forestGroup = forestTemplate.m_group, undeadGroup = undeadTemplate.m_group;
            ZDO first = Record("Greyling"), second = Record("Skeleton");
            Character forest = CharacterFixture("PartyPrison.WaveFixture.Forest", "Greyling", first, false);
            Character undead = CharacterFixture("PartyPrison.WaveFixture.Undead", "Skeleton", second, false);
            check(forest.m_faction != undead.m_faction && NativeEnemy(forest, undead) && NativeEnemy(undead, forest),
                "actual unmarked native forest and undead factions reproduce cross-family hostility");
            first.Set(ArenaBuilder.MobKey, true); second.Set(ArenaBuilder.MobKey, true);
            first.Set(ArenaBuilder.MobSentenceKey, Token); second.Set(ArenaBuilder.MobSentenceKey, Token);
            check(ArenaBuilder.AreAlliedPrisonMobs(forest, undead) && !NativeEnemy(forest, undead) && !NativeEnemy(undead, forest),
                "persisted same-sentence prison mobs bypass actual native faction hostility in both directions");
            check(!forest.GetComponent<ZNetView>().IsOwner() && !undead.GetComponent<ZNetView>().IsOwner(),
                "the alliance is derived on non-owner peers rather than from a host-only faction assignment");
            ZDO restored = NativeRoundtrip(second); typeof(ZNetView).GetField("m_zdo", All).SetValue(undead.GetComponent<ZNetView>(), restored);
            check(!NativeEnemy(forest, undead) && !NativeEnemy(undead, forest), "native replicated/reloaded mob markers preserve mixed enemy alliances");
            restored.Set(ArenaBuilder.MobSentenceKey, OtherToken);
            check(!ArenaBuilder.AreAlliedPrisonMobs(forest, undead) && NativeEnemy(forest, undead),
                "mobs assigned to another sentence retain ordinary native faction hostility");
            restored.Set(ArenaBuilder.MobSentenceKey, Token); restored.Set(ArenaBuilder.MobKey, false);
            check(!ArenaBuilder.AreAlliedPrisonMobs(forest, undead) && NativeEnemy(forest, undead),
                "ordinary mobs with a coincidental sentence field retain their original native behavior");
            restored.Set(ArenaBuilder.MobKey, true); restored.Set(ArenaBuilder.MobSentenceKey, "");
            check(!ArenaBuilder.AreAlliedPrisonMobs(forest, undead) && NativeEnemy(forest, undead),
                "legacy unassigned arena markers never form an unintended alliance");
            restored.Set(ArenaBuilder.MobSentenceKey, Token);
            Character player = CharacterFixture("PartyPrison.WaveFixture.Player", "Greyling", Mob(Token, 7, 12), true);
            check(!ArenaBuilder.AreAlliedPrisonMobs(forest, player) && !ArenaBuilder.AreAlliedPrisonMobs(player, undead)
                && NativeEnemy(forest, player) && NativeEnemy(player, undead),
                "even a player with matching synthetic prison markers remains a native enemy of mixed arena mobs");
            check(!ArenaBuilder.AreAlliedPrisonMobs(null, undead) && !ArenaBuilder.AreAlliedPrisonMobs(forest, null),
                "missing character fixtures cannot create an alliance");
            typeof(ZNetView).GetField("m_zdo", All).SetValue(undead.GetComponent<ZNetView>(), null);
            check(!ArenaBuilder.AreAlliedPrisonMobs(forest, undead), "invalid native character views cannot form prison alliances");
            check(forest.m_faction == forestFaction && undead.m_faction == undeadFaction
                && forestTemplate.m_faction == forestFaction && undeadTemplate.m_faction == undeadFaction
                && forestTemplate.m_group == forestGroup && undeadTemplate.m_group == undeadGroup,
                "mixed enemy alliances never mutate instance factions or global native prefab faction/group definitions");
        }
    }
}
