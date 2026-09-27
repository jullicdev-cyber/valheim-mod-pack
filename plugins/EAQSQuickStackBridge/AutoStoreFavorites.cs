using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
namespace ValheimModPack
{
    // Only Azu's manual player-store dispatch and its existing favorite filters are patched.
    // Ground auto-pull uses a different TryStore overload and is intentionally untouched.
    internal sealed class AutoStoreFavorites : IDisposable
    {
        internal const string Azu = "Azumatt.AzuAutoStore";
        private const string Quick = "goldenrevolver.quick_stack_store", Eaqs = "randyknapp.mods.equipmentandquickslots";
        private static AutoStoreFavorites active;
        private readonly Action<string> info;
        private readonly Action<Exception> report;
        private Harmony guards, filters;
        private MethodInfo getConfig, isFavorite;
        private FavoriteStateLink stateLink;
        private bool present, ready, warned, reported, disposed;
        internal bool Ready { get { return ready; } }
        internal AutoStoreFavorites(Action<string> info, Action<Exception> report) { this.info = info; this.report = report; }
        internal void InstallGuards()
        {
            if (disposed || guards != null) return;
            if (!Chainloader.PluginInfos.ContainsKey(Azu)) return;
            present = true; active = this; ready = false;
            guards = new Harmony(Plugin.Id + ".azustore-guard");
            try
            {
                // The Update guard is installed in Awake, before any player can press K.
                // It also provides a safe fallback when an unsupported version changes dispatch.
                var update = Required("AzuAutoStore.AzuAutoStorePlugin", "Update", Type.EmptyTypes, typeof(void), false);
                guards.Patch(update, prefix: new HarmonyMethod(typeof(AutoStoreFavorites), "BeforeUpdate"));
                var store = Required("AzuAutoStore.Util.Functions", "TryStore", Type.EmptyTypes, typeof(void), true);
                guards.Patch(store, prefix: new HarmonyMethod(typeof(AutoStoreFavorites), "BeforeStore"));
                var one = Required("AzuAutoStore.Util.Functions", "TryStoreThisItem", new[] { typeof(ItemDrop.ItemData), typeof(Inventory) }, typeof(void), true);
                guards.Patch(one, prefix: new HarmonyMethod(typeof(AutoStoreFavorites), "BeforeStore"));
            }
            catch (Exception error)
            {
                // If even Update changed, disabling that component blocks its shortcut processing.
                // Existing guard hooks are deliberately retained; no unsafe partial enable occurs.
                Chainloader.PluginInfos[Azu].Instance.enabled = false;
                Fail(error);
            }
        }
        internal void Initialize()
        {
            if (disposed || !present || reported || ready) return;
            try
            {
                RequireVersion(Azu, "3.1.6"); RequireVersion(Quick, "1.4.15"); RequireVersion(Eaqs, "3.1.3");
                Type config = AccessTools.TypeByName("QuickStackStore.UserConfig");
                if (config == null) throw new MissingMemberException("Quick Stack user config type changed");
                getConfig = Required("QuickStackStore.UserConfig", "GetPlayerConfig", new[] { typeof(long) }, config, true);
                isFavorite = Required("QuickStackStore.UserConfig", "IsItemNameOrSlotFavorited", new[] { typeof(ItemDrop.ItemData) }, typeof(bool), false);
                Type azuConfig = AccessTools.TypeByName("AzuAutoStore.Patches.Favoriting.UserConfig");
                if (azuConfig == null) throw new MissingMemberException("Azu favorite config type changed");
                MethodInfo azuGet = Required("AzuAutoStore.Patches.Favoriting.UserConfig", "GetPlayerConfig", new[] { typeof(long) }, azuConfig, true);
                MethodInfo quickSave = Required("QuickStackStore.UserConfig", "Save", Type.EmptyTypes, typeof(void), false);
                MethodInfo azuSave = Required("AzuAutoStore.Patches.Favoriting.UserConfig", "Save", Type.EmptyTypes, typeof(void), false);
                stateLink = new FavoriteStateLink(Paths.ConfigPath, getConfig, azuGet, quickSave,
                    Field(config, "favoritedSlots", typeof(HashSet<Vector2i>)), Field(config, "favoritedItems", typeof(HashSet<string>)),
                    Field(azuConfig, "_favoritedSlots", typeof(HashSet<Vector2i>)), Field(azuConfig, "_favoritedItems", typeof(HashSet<string>)));
                var targets = new List<MethodInfo>();
                foreach (string type in new[] { "VanillaContainers", "BackpackContainer", "kgDrawer", "mkzDrawer" })
                    targets.Add(Required("AzuAutoStore.Interfaces." + type, "CantStoreFavorite", new[] { typeof(ItemDrop.ItemData), azuConfig }, typeof(bool), true));
                filters = new Harmony(Plugin.Id + ".azustore-favorites");
                foreach (var target in targets) filters.Patch(target, prefix: new HarmonyMethod(typeof(AutoStoreFavorites), "BeforeFavoriteFilter"));
                filters.Patch(getConfig, prefix: new HarmonyMethod(typeof(AutoStoreFavorites), "BeforeGetConfig"), postfix: new HarmonyMethod(typeof(AutoStoreFavorites), "AfterQuickConfig"));
                filters.Patch(azuGet, prefix: new HarmonyMethod(typeof(AutoStoreFavorites), "BeforeGetConfig"), postfix: new HarmonyMethod(typeof(AutoStoreFavorites), "AfterAzuConfig"));
                filters.Patch(azuSave, prefix: new HarmonyMethod(typeof(AutoStoreFavorites), "BeforeAzuSave"));
                filters.Patch(quickSave, postfix: new HarmonyMethod(typeof(AutoStoreFavorites), "AfterQuickSave"));
                ready = true;
                info("AzuAutoStore K respects Quick Stack favorite slots/types and EAQS equipment/hidden rows. Azu and Quick Stack share live favorites and the canonical three-list Quick Stack save format.");
            }
            catch (Exception error)
            {
                if (filters != null) filters.UnpatchSelf();
                Fail(error);
            }
        }
        private static MethodInfo Required(string typeName, string name, Type[] args, Type result, bool isStatic)
        {
            Type type = AccessTools.TypeByName(typeName);
            MethodInfo method = type == null ? null : AccessTools.Method(type, name, args);
            if (method == null || method.ReturnType != result || method.IsStatic != isStatic)
                throw new MissingMethodException(typeName + ":" + name + " changed; manual storing is blocked");
            return method;
        }
        private static FieldInfo Field(Type type, string name, Type fieldType)
        {
            FieldInfo field = AccessTools.Field(type, name);
            if (field == null || field.IsStatic || field.FieldType != fieldType) throw new MissingFieldException(type.FullName, name);
            return field;
        }
        private static void BeforeGetConfig(long __0)
        {
            if (active == null || !active.ready) return;
            try { active.stateLink.ObserveBeforeGet(__0); }
            catch (Exception error) { active.Fail(error); }
        }
        private static void AfterQuickConfig(long __0, object __result)
        {
            if (active == null || !active.ready) return;
            try { active.stateLink.Bind(__0, __result, null); }
            catch (Exception error) { active.Fail(error); }
        }
        private static void AfterAzuConfig(long __0, object __result)
        {
            if (active == null || !active.ready) return;
            try { active.stateLink.Bind(__0, null, __result); }
            catch (Exception error) { active.Fail(error); }
        }
        private static bool BeforeAzuSave(object __instance)
        {
            if (active == null || !active.ready) return false;
            try { active.stateLink.SaveAzu(__instance); }
            catch (Exception error) { active.Fail(error); }
            return false; // Azu's two-list writer must never truncate the canonical three-list file.
        }
        private static void AfterQuickSave(object __instance)
        {
            if (active == null || !active.ready) return;
            try { active.stateLink.AfterQuickSave(__instance); }
            catch (Exception error) { active.Fail(error); }
        }
        private static void RequireVersion(string id, string expected)
        {
            if (!Chainloader.PluginInfos.ContainsKey(id) || Chainloader.PluginInfos[id].Metadata.Version != new System.Version(expected))
                throw new NotSupportedException("Azu favorite bridge requires " + id + " " + expected + "; manual storing is blocked");
        }
        private static bool BeforeUpdate()
        {
            if (active != null && active.ready) return true;
            if (active != null) active.WarnOnce();
            return false;
        }
        private static bool BeforeStore()
        {
            if (active == null || !active.ready) { if (active != null) active.WarnOnce(); return false; }
            try
            {
                Player player = Player.m_localPlayer;
                if (player == null) return false;
                Inventory inventory = player.GetInventory();
                if (inventory == null || SlotPolicy.IsProtected(0, 0, inventory.GetWidth(), inventory.GetHeight(),
                    EquipmentAndQuickSlots.API.GetVisibleRows(), EquipmentAndQuickSlots.API.GetFullHeight()))
                    throw new InvalidOperationException("EAQS inventory dimensions disagree; manual storing is blocked");
                if (active.ConfigFor(player) == null) throw new InvalidOperationException("Quick Stack favorite data is unavailable");
                return true;
            }
            catch (Exception error) { active.Fail(error); return false; }
        }
        private object ConfigFor(Player player)
        {
            long id = player.GetPlayerID();
            if (id == 0) throw new InvalidOperationException("Player identity is not ready");
            object config = getConfig.Invoke(null, new object[] { id });
            if (!ready) throw new InvalidOperationException("Favorite synchronization failed; manual storing is blocked");
            return config;
        }
        private static bool BeforeFavoriteFilter(ItemDrop.ItemData __0, ref bool __result)
        {
            // A drop or a chest item can have the same grid coordinates as a favorite slot.
            // Only exact references in the local player's main inventory may gain this protection.
            Player player = Player.m_localPlayer;
            if (player == null || __0 == null || player.GetInventory() == null
                || !player.GetInventory().GetAllItems().Contains(__0)) return true;
            if (active == null || !active.ready) { __result = true; return false; }
            try
            {
                var inventory = player.GetInventory();
                bool protectedItem = __0.m_equipped || __0.m_shared == null
                    || SlotPolicy.IsProtected(__0.m_gridPos.x, __0.m_gridPos.y, inventory.GetWidth(), inventory.GetHeight(),
                        EquipmentAndQuickSlots.API.GetVisibleRows(), EquipmentAndQuickSlots.API.GetFullHeight());
                if (!protectedItem)
                {
                    object config = active.ConfigFor(player);
                    if (config == null) throw new InvalidOperationException("Quick Stack favorite data is unavailable");
                    protectedItem = (bool)active.isFavorite.Invoke(config, new object[] { __0 });
                }
                if (protectedItem) { __result = true; return false; }
                return true; // Preserve Azu favorites, hotbar preference, permissions and container rules.
            }
            catch (Exception error) { active.Fail(error); __result = true; return false; }
        }
        private void Fail(Exception error)
        {
            ready = false;
            if (!reported) { reported = true; report(error); }
            WarnOnce();
        }
        private void WarnOnce()
        {
            if (warned || Player.m_localPlayer == null) return;
            warned = true;
            Player.m_localPlayer.Message(MessageHud.MessageType.Center,
                "AzuAutoStore: выгрузка временно заблокирована для защиты избранного. Проверьте журнал BepInEx.", 0, null);
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            ready = false;
            if (filters != null) filters.UnpatchSelf();
            if (guards != null) guards.UnpatchSelf();
            if (ReferenceEquals(active, this)) active = null;
        }
    }
}
