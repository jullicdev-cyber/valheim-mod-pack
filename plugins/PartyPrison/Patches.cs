using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValheimModPack.PartyPrison
{
    internal static class PrisonGuard
    {
        internal static bool Local(Humanoid actor)
        { return Plugin.Active != null && actor != null && actor == Player.m_localPlayer; }
        internal static bool Restricted(Humanoid actor)
        { return Local(actor) && (Plugin.Active.Confined || Plugin.Active.AwaitingState); }
        internal static bool Protected(Component component)
        {
            if (component == null) return false;
            if (ArenaBuilder.IsProtected(component.gameObject)) return true;
            return Plugin.Active != null && Plugin.Active.Region != null && Plugin.Active.Region.Contains(Plugin.Point(component.transform.position));
        }
        internal static bool LoanDestination(Inventory target, ItemDrop.ItemData item)
        { return !Plugin.IsLoan(item) || Plugin.Active != null && Plugin.Active.Confined && Player.m_localPlayer != null && ReferenceEquals(target, Player.m_localPlayer.GetInventory()); }
        [ThreadStatic] internal static ItemDrop.ItemData AddingItem;
        [ThreadStatic] internal static int LoadingCustody;
        internal static readonly Inventory Inaccessible = new Inventory("Locked custody", null, 1, 0);
        internal static bool InventoryAccess(Inventory inventory)
        { return !ReferenceEquals(inventory, Inaccessible) && CustodyInventory.ChestForInventory(inventory) == null; }
    }
    [HarmonyPatch(typeof(ZNet), "RPC_ServerSyncedPlayerData")]
    internal static class PositionPatch
    { private static void Postfix(ZRpc rpc) { if (Plugin.Active != null) Plugin.Active.RememberPosition(rpc); } }
    [HarmonyPatch(typeof(ZNet), "Disconnect")]
    internal static class DisconnectPatch
    { private static void Prefix(ZNetPeer peer) { if (Plugin.Active != null) Plugin.Active.RemovePeer(peer); } }
    [HarmonyPatch(typeof(Player), "SetControls")]
    internal static class ControlsPatch
    {
        private static void Prefix(Player __instance, object[] __args)
        {
            if (!PrisonGuard.Local(__instance) || !Plugin.Active.AwaitingState && !Plugin.Active.PreparingCustody && !Plugin.Active.WindowVisible) return;
            for (int i = 0; i < __args.Length; ++i)
            { if (__args[i] is bool) __args[i] = false; else if (__args[i] is Vector3) __args[i] = Vector3.zero; }
        }
    }
    [HarmonyPatch(typeof(Player), "PlacePiece")]
    internal static class BuildPatch
    { private static bool Prefix(Player __instance) { return !PrisonGuard.Restricted(__instance); } }
    [HarmonyPatch(typeof(Player), "RemovePiece")]
    internal static class DismantlePatch
    { private static bool Prefix(Player __instance, ref bool __result) { if (!PrisonGuard.Restricted(__instance)) return true; __result = false; return false; } }
    [HarmonyPatch(typeof(Player), "TeleportTo")]
    internal static class TeleportPatch
    {
        private static bool Prefix(Player __instance, Vector3 __0, ref bool __result)
        { if (!PrisonGuard.Local(__instance) || Plugin.Active.AllowTeleport(__0)) return true; __result = false; return false; }
    }
    [HarmonyPatch(typeof(Character), "RPC_TeleportTo")]
    internal static class TeleportRpcPatch
    { private static bool Prefix(Character __instance, Vector3 __1) { return __instance != Player.m_localPlayer || Plugin.Active == null || Plugin.Active.AllowTeleport(__1); } }
    [HarmonyPatch(typeof(Character), "CheckDeath")]
    internal static class DefeatPatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Character __instance)
        {
            if (__instance != Player.m_localPlayer || Plugin.Active == null || !Plugin.Active.Confined || __instance.GetHealth() > 0) return true;
            Plugin.Active.Enforce(true); return false;
        }
    }
    [HarmonyPatch(typeof(Character), "ApplyDamage")]
    internal static class CellSafetyPatch
    {
        private static bool Prefix(Character __instance, HitData __0)
        {
            Character attacker = __0 == null ? null : __0.GetAttacker();
            if (Plugin.Active != null && __instance == Player.m_localPlayer && Plugin.Active.Confined
                && Plugin.Active.CellSafe(Plugin.Point(__instance.transform.position))
                && (attacker == null || !ArenaBuilder.IsMob(attacker.gameObject))) return false;
            if (attacker != null)
            {
                var view = attacker.GetComponent<ZNetView>();
                if (view != null && view.IsValid() && view.GetZDO().GetBool(Plugin.InmateKey, false) && !ArenaBuilder.IsMob(__instance.gameObject)) return false;
            }
            return true;
        }
    }
    [HarmonyPatch(typeof(WearNTear), "ApplyDamage")]
    internal static class StructureDamagePatch
    { private static bool Prefix(WearNTear __instance, ref bool __result) { if (!PrisonGuard.Protected(__instance)) return true; __result = false; return false; } }
    [HarmonyPatch(typeof(WearNTear), "RPC_Remove")]
    internal static class StructureRemoveRpcPatch
    { private static bool Prefix(WearNTear __instance) { return !PrisonGuard.Protected(__instance); } }
    [HarmonyPatch(typeof(WearNTear), "Remove")]
    internal static class StructureRemovePatch
    { private static bool Prefix(WearNTear __instance) { return !PrisonGuard.Protected(__instance); } }
    [HarmonyPatch(typeof(WearNTear), "Destroy")]
    internal static class StructureDestroyPatch
    { private static bool Prefix(WearNTear __instance) { return !PrisonGuard.Protected(__instance); } }
    [HarmonyPatch(typeof(Piece), "CanBeRemoved")]
    internal static class PieceRemovalPatch
    { private static bool Prefix(Piece __instance, ref bool __result) { if (!PrisonGuard.Protected(__instance)) return true; __result = false; return false; } }
    [HarmonyPatch(typeof(Destructible), "RPC_Damage")]
    internal static class ObjectDamagePatch
    { private static bool Prefix(Destructible __instance) { return !PrisonGuard.Protected(__instance); } }
    [HarmonyPatch(typeof(TreeBase), "RPC_Damage")]
    internal static class TreeDamagePatch
    { private static bool Prefix(TreeBase __instance) { return !PrisonGuard.Protected(__instance); } }
    [HarmonyPatch(typeof(TreeLog), "RPC_Damage")]
    internal static class LogDamagePatch
    { private static bool Prefix(TreeLog __instance) { return !PrisonGuard.Protected(__instance); } }
    [HarmonyPatch(typeof(MineRock5), "RPC_Damage")]
    internal static class RockDamagePatch
    { private static bool Prefix(MineRock5 __instance) { return !PrisonGuard.Protected(__instance); } }
    [HarmonyPatch(typeof(TerrainComp), "DoOperation")]
    internal static class TerrainPatch
    {
        private static bool Prefix(Vector3 __0)
        {
            // A TerrainComp is centred on its terrain tile; the operation's
            // position, not that tile's centre, decides whether it affects jail.
            return Plugin.Active == null || Plugin.Active.Region == null
                || !Plugin.Active.Region.Contains(Plugin.Point(__0));
        }
    }
    [HarmonyPatch(typeof(Humanoid), "Pickup")]
    internal static class PickupPatch
    {
        private static bool Prefix(Humanoid __instance, GameObject __0, ref bool __result)
        {
            if (__0 == null) return true;
            ItemDrop drop = __0.GetComponent<ItemDrop>(); if (drop == null) return true;
            bool loan = Plugin.IsLoan(drop.m_itemData) || ArenaBuilder.IsArmory(__0);
            if (loan && (!PrisonGuard.Local(__instance) || !Plugin.Active.Confined || Plugin.Active.PreparingCustody)
                || !loan && PrisonGuard.Restricted(__instance) && (Plugin.Active.PreparingCustody || !ArenaBuilder.ContainsConfinement(Plugin.Active.Region, __0.transform.position)))
            { __result = false; return false; }
            if (loan && __instance.GetInventory().GetAllItems().Any(item => Plugin.IsLoan(item) && item.m_dropPrefab == drop.m_itemData.m_dropPrefab)) { __result = false; return false; }
            return true;
        }
    }
    [HarmonyPatch(typeof(Humanoid), "DropItem")]
    internal static class DropPatch
    {
        private static bool Prefix(Humanoid __instance, ItemDrop.ItemData __1, ref bool __result)
        { if (!PrisonGuard.Restricted(__instance) && !Plugin.IsLoan(__1)) return true; __result = false; return false; }
    }
    [HarmonyPatch(typeof(Container), "Interact")]
    internal static class ContainerPatch
    { private static bool Prefix(Container __instance, Humanoid __0, ref bool __result) { if (ArenaBuilder.IsCustody(__instance.gameObject) || PrisonGuard.Local(__0) && Plugin.Active.PreparingCustody) { __result = false; return false; } return true; } }
    [HarmonyPatch(typeof(Door), "Interact")]
    internal static class DoorPatch
    { private static bool Prefix(Door __instance, Humanoid __0, ref bool __result) { if (ArenaBuilder.IsExitGate(__instance.gameObject)) { __result = false; return false; } if (ArenaBuilder.IsInnerGate(__instance.gameObject) && PrisonGuard.Restricted(__0) && !Plugin.Active.PreparingCustody) return true; if (!PrisonGuard.Restricted(__0)) return true; __result = false; return false; } }
    [HarmonyPatch(typeof(Door), "RPC_UseDoor")]
    internal static class ExitGateRpcPatch
    { private static bool Prefix(Door __instance) { return !ArenaBuilder.IsExitGate(__instance.gameObject); } }
    [HarmonyPatch(typeof(Container), "RPC_RequestOpen")]
    internal static class CustodyOpenRpcPatch
    { private static bool Prefix(Container __instance) { return !ArenaBuilder.IsCustody(__instance.gameObject); } }
    [HarmonyPatch(typeof(Container), "RPC_RequestStack")]
    internal static class CustodyStackRpcPatch
    { private static bool Prefix(Container __instance) { return !ArenaBuilder.IsCustody(__instance.gameObject); } }
    [HarmonyPatch(typeof(Container), "RPC_RequestTakeAll")]
    internal static class CustodyTakeAllRpcPatch
    { private static bool Prefix(Container __instance) { return !ArenaBuilder.IsCustody(__instance.gameObject); } }
    [HarmonyPatch(typeof(Container), "GetInventory")]
    internal static class CustodyInventoryAccessPatch
    {
        private static bool Prefix(Container __instance, ref Inventory __result)
        {
            CustodyInventory.RegisterContainer(__instance);
            if (!CustodyInventory.IsCustodyContainer(__instance)) return true;
            __result = PrisonGuard.Inaccessible; return false;
        }
    }
    [HarmonyPatch(typeof(Container), "Awake")]
    internal static class CustodyContainerRegistrationPatch
    { private static void Postfix(Container __instance) { CustodyInventory.RegisterContainer(__instance); } }
    [HarmonyPatch(typeof(Container), "AddDefaultItems")]
    internal static class CustodyDefaultItemsRegistrationPatch
    {
        // Awake assigns m_inventory before this call, but its postfix has not
        // run yet. Register before any native default-item inventory writes.
        private static void Prefix(Container __instance) { CustodyInventory.RegisterContainer(__instance); }
    }
    [HarmonyPatch(typeof(Container), "Load")]
    internal static class CustodyNativeLoadPatch
    {
        private static void Prefix(Container __instance, out bool __state)
        {
            CustodyInventory.RegisterContainer(__instance);
            __state = CustodyInventory.IsCustodyContainer(__instance); if (__state) ++PrisonGuard.LoadingCustody;
        }
        private static Exception Finalizer(Exception __exception, bool __state) { if (__state) --PrisonGuard.LoadingCustody; return __exception; }
    }
    [HarmonyPatch(typeof(Container), "GetHoverText")]
    internal static class CustodyHoverPatch
    { private static void Postfix(Container __instance, ref string __result) { if (ArenaBuilder.IsCustody(__instance.gameObject)) __result = Plugin.T("Личные вещи\n[<color=yellow>E</color>] Забрать после освобождения", "Personal belongings\n[<color=yellow>E</color>] Collect after release"); } }
    [HarmonyPatch(typeof(Inventory), "RemoveItem", new Type[] { typeof(ItemDrop.ItemData) })]
    internal static class CustodyRemovePatch
    { private static bool Prefix(Inventory __instance, ref bool __result) { if (PrisonGuard.InventoryAccess(__instance)) return true; __result = false; return false; } }
    [HarmonyPatch(typeof(Inventory), "RemoveItem", new Type[] { typeof(int) })]
    internal static class CustodyRemoveIndexPatch
    { private static bool Prefix(Inventory __instance, ref bool __result) { if (PrisonGuard.InventoryAccess(__instance)) return true; __result = false; return false; } }
    [HarmonyPatch(typeof(Inventory), "GetAllItems", new Type[0])]
    internal static class CustodyReadItemsPatch
    { private static bool Prefix(Inventory __instance, ref List<ItemDrop.ItemData> __result) { if (PrisonGuard.InventoryAccess(__instance)) return true; __result = new List<ItemDrop.ItemData>(); return false; } }
    [HarmonyPatch(typeof(Inventory), "GetAllItems", new Type[] { typeof(string), typeof(List<ItemDrop.ItemData>) })]
    internal static class CustodyReadNamedItemsPatch
    { private static bool Prefix(Inventory __instance) { return PrisonGuard.InventoryAccess(__instance); } }
    [HarmonyPatch(typeof(Inventory), "GetAllItems", new Type[] { typeof(ItemDrop.ItemData.ItemType), typeof(List<ItemDrop.ItemData>) })]
    internal static class CustodyReadTypedItemsPatch
    { private static bool Prefix(Inventory __instance) { return PrisonGuard.InventoryAccess(__instance); } }
    [HarmonyPatch(typeof(Inventory), "GetItemAt", new Type[] { typeof(int), typeof(int) })]
    internal static class CustodyReadCellPatch
    { private static bool Prefix(Inventory __instance, ref ItemDrop.ItemData __result) { if (PrisonGuard.InventoryAccess(__instance)) return true; __result = null; return false; } }
    [HarmonyPatch(typeof(Inventory), "CountItems", new Type[] { typeof(string), typeof(int), typeof(bool) })]
    internal static class CustodyCountItemsPatch
    { private static bool Prefix(Inventory __instance, ref int __result) { if (PrisonGuard.InventoryAccess(__instance)) return true; __result = 0; return false; } }
    [HarmonyPatch(typeof(Inventory), "RemoveItem", new Type[] { typeof(ItemDrop.ItemData), typeof(int) })]
    internal static class CustodyRemoveAmountPatch
    { private static bool Prefix(Inventory __instance, ref bool __result) { if (PrisonGuard.InventoryAccess(__instance)) return true; __result = false; return false; } }
    [HarmonyPatch(typeof(Inventory), "RemoveItem", new Type[] { typeof(string), typeof(int), typeof(int), typeof(bool) })]
    internal static class CustodyRemoveNamePatch
    { private static bool Prefix(Inventory __instance) { return PrisonGuard.InventoryAccess(__instance); } }
    [HarmonyPatch(typeof(Inventory), "RemoveAll")]
    internal static class CustodyRemoveAllPatch
    { private static bool Prefix(Inventory __instance) { return PrisonGuard.InventoryAccess(__instance); } }
    [HarmonyPatch(typeof(InventoryGui), "Show")]
    internal static class PreparingInventoryPatch
    { private static bool Prefix() { return Plugin.Active == null || !Plugin.Active.PreparingCustody; } }
    [HarmonyPatch(typeof(Inventory), "MoveItemToThis", new Type[] { typeof(Inventory), typeof(ItemDrop.ItemData) })]
    internal static class ItemTransferPatch
    { private static bool Prefix(Inventory __instance, Inventory __0, ItemDrop.ItemData __1) { return PrisonGuard.InventoryAccess(__instance) && PrisonGuard.InventoryAccess(__0) && PrisonGuard.LoanDestination(__instance, __1); } }
    [HarmonyPatch(typeof(Inventory), "MoveItemToThis", new Type[] { typeof(Inventory), typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int) })]
    internal static class StackTransferPatch
    { private static bool Prefix(Inventory __instance, Inventory __0, ItemDrop.ItemData __1, ref bool __result) { if (PrisonGuard.InventoryAccess(__instance) && PrisonGuard.InventoryAccess(__0) && PrisonGuard.LoanDestination(__instance, __1)) return true; __result = false; return false; } }
    [HarmonyPatch(typeof(Inventory), "MoveAll")]
    internal static class AllTransferPatch
    { private static bool Prefix(Inventory __instance, Inventory __0) { return PrisonGuard.InventoryAccess(__instance) && PrisonGuard.InventoryAccess(__0) && __0.GetAllItems().All(item => PrisonGuard.LoanDestination(__instance, item)); } }
    [HarmonyPatch(typeof(Inventory), "AddItem", new Type[] { typeof(ItemDrop.ItemData) })]
    internal static class AddItemPatch
    {
        private static bool Prefix(Inventory __instance, ItemDrop.ItemData __0, ref bool __result, out ItemDrop.ItemData __state)
        {
            __state = PrisonGuard.AddingItem;
            if (!PrisonGuard.InventoryAccess(__instance) || !PrisonGuard.LoanDestination(__instance, __0)) { __result = false; return false; }
            PrisonGuard.AddingItem = __0; return true;
        }
        private static Exception Finalizer(Exception __exception, ItemDrop.ItemData __state)
        { PrisonGuard.AddingItem = __state; return __exception; }
    }
    [HarmonyPatch(typeof(Inventory), "FindFreeStackItem")]
    internal static class StackIsolationPatch
    {
        private static bool Prefix(Inventory __instance, string __0, int __1, float __2, ref ItemDrop.ItemData __result)
        {
            // Vanilla stacking ignores custom data. Preserve the ownership of
            // personal arrows when the same prefab exists in the prison armory.
            bool loan = Plugin.IsLoan(PrisonGuard.AddingItem);
            if (!loan && !__instance.GetAllItems().Any(Plugin.IsLoan)) return true;
            __result = __instance.GetAllItems().FirstOrDefault(item => item.m_shared.m_name == __0 && item.m_quality == __1
                && item.m_worldLevel == __2 && item.m_stack < item.m_shared.m_maxStackSize && Plugin.IsLoan(item) == loan);
            return false;
        }
    }
    [HarmonyPatch(typeof(Inventory), "AddItem", new Type[] { typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int), typeof(bool) })]
    internal static class SlotStackIsolationPatch
    {
        private static bool Prefix(Inventory __instance, ItemDrop.ItemData __0, int __2, int __3, ref bool __result)
        {
            ItemDrop.ItemData existing = __instance.GetAllItems().FirstOrDefault(item => item.m_gridPos.x == __2 && item.m_gridPos.y == __3);
            if (!ReferenceEquals(__instance, PrisonGuard.Inaccessible) && (PrisonGuard.InventoryAccess(__instance) || PrisonGuard.LoadingCustody != 0) && PrisonGuard.LoanDestination(__instance, __0) && (existing == null || Plugin.IsLoan(existing) == Plugin.IsLoan(__0))) return true;
            __result = false; return false;
        }
    }
    [HarmonyPatch(typeof(Inventory), "StackAll")]
    internal static class QuickStackIsolationPatch
    {
        private static bool Prefix(Inventory __instance, Inventory __0, ref int __result)
        { if (PrisonGuard.InventoryAccess(__instance) && PrisonGuard.InventoryAccess(__0) && !__instance.GetAllItems().Any(Plugin.IsLoan) && !__0.GetAllItems().Any(Plugin.IsLoan)) return true; __result = 0; return false; }
    }
    [HarmonyPatch(typeof(ItemDrop), "AutoStackItems")]
    internal static class WorldStackIsolationPatch
    { private static bool Prefix(ItemDrop __instance) { return !ArenaBuilder.IsArmory(__instance.gameObject) && !PrisonGuard.Protected(__instance); } }
    [HarmonyPatch]
    internal static class GroupRadiusCompatibilityPatch
    {
        private static MethodBase RadiusMethod()
        { return AccessTools.Method("ValheimModPack.InventoryAdmin.GroupRadiusMotion:TryFrame"); }
        private static bool Prepare() { return RadiusMethod() != null; }
        private static MethodBase TargetMethod() { return RadiusMethod(); }
        private static bool Prefix(ref bool __result)
        {
            // The party leader may be far from the prison. Suspend that optional
            // movement constraint until release is saved, without changing roles
            // or persisting a radius exemption for the prisoner.
            if (Plugin.Active == null || !Plugin.Active.PrisonActive) return true;
            __result = false; return false;
        }
    }
    [HarmonyPatch(typeof(Jotunn.Managers.GUIManager), "ResetInputBlock")]
    internal static class InputResetPatch
    { private static void Postfix() { if (Plugin.Active != null) Plugin.Active.ResetInput(); } }
}
