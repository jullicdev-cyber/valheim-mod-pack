// Test-only detached skills/player components. Never ship this in PartyPrison.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;

namespace ValheimModPack.PartyPrison.NativeVerification
{
    public static class DeathNativeChecks
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static readonly HashSet<GameObject> objects = new HashSet<GameObject>();
        private static readonly HashSet<Skills> ownedSkills = new HashSet<Skills>();
        private static readonly Dictionary<Player, List<string>> messages = new Dictionary<Player, List<string>>();
        private static ZoneSystem zone;
        private static Skills failingSkills;
        private static int deathCalls;

        private sealed class Actor
        {
            internal GameObject Object;
            internal Player Player;
            internal Skills Skills;
            internal Inventory Inventory;
            internal Dictionary<Skills.SkillType, Skills.Skill> Values;
        }
        private static bool SkipLifecycle(Component __instance)
        { return __instance == null || !objects.Contains(__instance.gameObject); }
        private static bool UseDetachedZone(ref ZoneSystem __result)
        { __result = zone; return false; }
        private static bool RecordMessage(Player __instance, string __1)
        {
            List<string> recorded;
            if (!messages.TryGetValue(__instance, out recorded)) return true;
            recorded.Add(__1); return false;
        }
        private static void ObserveDeath(Skills __instance)
        {
            if (!ownedSkills.Contains(__instance)) return;
            ++deathCalls;
            if (ReferenceEquals(__instance, failingSkills)) throw new IOException("Injected native skill-loss failure.");
        }
        private static FieldInfo Field(Type type, string name)
        {
            FieldInfo result = AccessTools.Field(type, name);
            if (result == null) throw new MissingFieldException(type.FullName, name);
            return result;
        }
        private static Actor NewActor()
        {
            var root = new GameObject("PartyPrison.DetachedDeathFixture"); root.SetActive(false); objects.Add(root);
            Player player = root.AddComponent<Player>(); Skills skills = root.AddComponent<Skills>();
            ownedSkills.Add(skills); messages.Add(player, new List<string>());
            Field(typeof(Skills), "m_player").SetValue(skills, player);
            Field(typeof(Skills), "m_DeathLowerFactor").SetValue(skills, .05f);
            Field(typeof(Player), "m_skills").SetValue(player, skills);
            Field(typeof(Player), "m_hardDeathCooldown").SetValue(player, 600f);
            var inventory = new Inventory("PartyPrison.DeathPersonalItems", null, 8, 8);
            Field(typeof(Humanoid), "m_inventory").SetValue(player, inventory);
            var values = (Dictionary<Skills.SkillType, Skills.Skill>)Field(typeof(Skills), "m_skillData").GetValue(skills);
            values.Clear();
            values.Add(Skills.SkillType.Run, new Skills.Skill(new Skills.SkillDef { m_skill = Skills.SkillType.Run }) { m_level = 80f, m_accumulator = 12f });
            values.Add(Skills.SkillType.Bows, new Skills.Skill(new Skills.SkillDef { m_skill = Skills.SkillType.Bows }) { m_level = 40f, m_accumulator = 22f });
            foreach (string prefabName in new[] { "SwordSilver", "HelmetDrake", "Wood" })
            {
                GameObject prefab = PrefabManager.Instance.GetPrefab(prefabName);
                if (!prefab || !prefab.GetComponent<ItemDrop>()) throw new InvalidOperationException("Death fixture native item missing: " + prefabName);
                ItemDrop.ItemData item = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
                item.m_dropPrefab = prefab; item.m_stack = prefabName == "Wood" ? 17 : 1;
                item.m_quality = Math.Min(3, item.m_shared.m_maxQuality); item.m_durability = 11f;
                item.m_equipped = prefabName == "HelmetDrake";
                item.m_gridPos = new Vector2i(inventory.GetAllItems().Count, 0);
                item.m_customData = new Dictionary<string, string> { { "vmp_death_personal_fixture", "Preserve original metadata \u2603" } };
                inventory.GetAllItems().Add(item);
            }
            return new Actor { Object = root, Player = player, Skills = skills, Inventory = inventory, Values = values };
        }
        private static byte[] ItemBytes(Actor actor)
        { var package = new ZPackage(); actor.Inventory.Save(package); return package.GetArray(); }
        private static void Apply(MethodInfo apply, Actor actor)
        {
            try { apply.Invoke(null, new object[] { actor.Player }); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }
        public static void Run(Action<bool, string> check)
        {
            check(Player.m_localPlayer == null && Game.instance == null, "death fixture starts outside any real player or world");
            var fixture = new Harmony("valheimmodpack.partyprison.nativeprobe.deathskills");
            var created = new List<Actor>(); GameObject zoneObject = null;
            float originalRate = Game.m_skillReductionRate;
            ZoneSystem originalZone = ZoneSystem.instance;
            FieldInfo timer = Field(typeof(Player), "m_timeSinceDeath");
            Type penalty = typeof(Plugin).Assembly.GetType("ValheimModPack.PartyPrison.ArenaDefeatPenalty", true);
            MethodInfo apply = penalty.GetMethod("Apply", All);
            check(apply != null, "production arena skill-loss adapter exists");
            try
            {
                var patched = new HashSet<MethodInfo>();
                foreach (Type type in new[] { typeof(Player), typeof(Humanoid), typeof(Character), typeof(Skills), typeof(ZoneSystem) })
                    foreach (string name in new[] { "Awake", "OnDestroy" })
                    {
                        MethodInfo method = AccessTools.Method(type, name, Type.EmptyTypes);
                        if (method == null || !patched.Add(method)) continue;
                        var prefix = new HarmonyMethod(typeof(DeathNativeChecks).GetMethod("SkipLifecycle", All)); prefix.priority = Priority.First;
                        fixture.Patch(method, prefix: prefix);
                    }
                fixture.Patch(AccessTools.PropertyGetter(typeof(ZoneSystem), "instance"),
                    prefix: new HarmonyMethod(typeof(DeathNativeChecks).GetMethod("UseDetachedZone", All)));
                fixture.Patch(AccessTools.Method(typeof(Player), "Message", new[] { typeof(MessageHud.MessageType), typeof(string), typeof(int), typeof(Sprite), typeof(bool) }),
                    prefix: new HarmonyMethod(typeof(DeathNativeChecks).GetMethod("RecordMessage", All)));
                var observer = new HarmonyMethod(typeof(DeathNativeChecks).GetMethod("ObserveDeath", All)); observer.priority = Priority.First;
                fixture.Patch(AccessTools.Method(typeof(Skills), "OnDeath", Type.EmptyTypes), prefix: observer);
                zoneObject = new GameObject("PartyPrison.DetachedDeathKeys"); zoneObject.SetActive(false); objects.Add(zoneObject);
                zone = zoneObject.AddComponent<ZoneSystem>();
                var keys = (HashSet<GlobalKeys>)Field(typeof(ZoneSystem), "m_globalKeysEnums").GetValue(zone); keys.Clear();
                foreach (float rate in new[] { 0f, .5f, 1f, 2f })
                {
                    Game.m_skillReductionRate = rate;
                    Actor baseline = NewActor(), actual = NewActor(); created.Add(baseline); created.Add(actual);
                    baseline.Skills.OnDeath();
                    byte[] before = ItemBytes(actual); ItemDrop.ItemData[] personal = actual.Inventory.GetAllItems().ToArray();
                    timer.SetValue(actual.Player, 601f); deathCalls = 0; Apply(apply, actual);
                    check(deathCalls == 1, "one hard defeat calls the full patched native skill-loss chain once: world rate " + rate);
                    check(Math.Abs(actual.Values[Skills.SkillType.Run].m_level - baseline.Values[Skills.SkillType.Run].m_level) < .0001f
                        && Math.Abs(actual.Values[Skills.SkillType.Bows].m_level - baseline.Values[Skills.SkillType.Bows].m_level) < .0001f,
                        "arena loss equals real native OnDeath percentages under current pack patches and world multiplier " + rate);
                    check(actual.Values.Values.All(value => value.m_accumulator == 0), "hard death clears each skill's accumulated progress like vanilla");
                    check((float)timer.GetValue(actual.Player) == 0f, "hard death starts native skill protection cooldown");
                    check(ItemBytes(actual).SequenceEqual(before) && actual.Inventory.GetAllItems().SequenceEqual(personal),
                        "skill penalty preserves equipped personal silver gear, resource stack, durability, quality and custom metadata");
                    float savedLevel = actual.Values[Skills.SkillType.Run].m_level;
                    deathCalls = 0; Apply(apply, actual);
                    check(deathCalls == 0 && actual.Values[Skills.SkillType.Run].m_level == savedLevel,
                        "immediate repeat defeat cannot apply a second hard penalty");
                    check(messages[actual.Player].Contains("$msg_softdeath") && (float)timer.GetValue(actual.Player) == 0f,
                        "soft defeat shows protection notice and restarts cooldown");
                }
                Game.m_skillReductionRate = 1f;
                foreach (float since in new[] { 599f, 600f, 601f })
                {
                    Actor actor = NewActor(); created.Add(actor); timer.SetValue(actor.Player, since); deathCalls = 0;
                    Apply(apply, actor);
                    check(deathCalls == (since > 600 ? 1 : 0), "native cooldown boundary uses strict greater-than at " + since + " seconds");
                    check((float)timer.GetValue(actor.Player) == 0f, "both sides of cooldown boundary reset elapsed death time");
                    check(messages[actor.Player].Contains("$msg_softdeath") == (since <= 600), "soft-death notice follows native boundary");
                }
                keys.Add(GlobalKeys.DeathSkillsReset);
                foreach (float since in new[] { 0f, 601f })
                {
                    Actor actor = NewActor(); created.Add(actor); byte[] before = ItemBytes(actor);
                    timer.SetValue(actor.Player, since); deathCalls = 0; Apply(apply, actor);
                    check(actor.Values.Count == 0 && deathCalls == 0, "world reset-skills rule clears skills instead of percentage loss, including soft death");
                    check((float)timer.GetValue(actor.Player) == 0f && ItemBytes(actor).SequenceEqual(before), "world reset skill rule also resets cooldown and retains personal items");
                    check(messages[actor.Player].Contains("$msg_softdeath") == (since == 0), "reset-key world retains the vanilla soft-death notice");
                }
                keys.Clear();
                Actor failure = NewActor(); created.Add(failure); timer.SetValue(failure.Player, 601f); failingSkills = failure.Skills;
                bool refused = false;
                try { Apply(apply, failure); } catch (IOException) { refused = true; }
                check(refused && (float)timer.GetValue(failure.Player) == 0f, "native skill handler failure cannot leave a stale expired cooldown");
                check(failure.Values[Skills.SkillType.Run].m_level == 80f, "controlled pre-loss failure grants no duplicated partial skill reduction");
                failingSkills = null;
                check(created.All(actor => !actor.Object.activeInHierarchy) && Player.m_localPlayer == null && Game.instance == null,
                    "all tested players remain detached inactive components with no world, local identity, tombstone or respawn");
            }
            finally
            {
                failingSkills = null; Game.m_skillReductionRate = originalRate;
                foreach (Actor actor in created) if (actor.Object) UnityEngine.Object.DestroyImmediate(actor.Object);
                if (zoneObject) UnityEngine.Object.DestroyImmediate(zoneObject);
                zone = null; messages.Clear(); ownedSkills.Clear(); objects.Clear(); fixture.UnpatchSelf();
            }
            check(Game.m_skillReductionRate == originalRate && ReferenceEquals(ZoneSystem.instance, originalZone)
                && Player.m_localPlayer == null && Game.instance == null, "death fixture restores native world-rule getter, multiplier and every global registration");
        }
    }
}
