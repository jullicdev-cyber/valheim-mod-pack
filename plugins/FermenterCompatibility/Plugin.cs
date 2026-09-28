using System;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace ValheimModPack.FermenterCompatibility
{
    [BepInPlugin(Id, "Fermenter Compatibility", Version)]
    [BepInDependency(Crafty, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(Plus, BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "valheimmodpack.fermentercompatibility", Version = "1.0.0";
        private const string Crafty = "Azumatt.AzuCraftyBoxes";
        private const string Plus = "org.bepinex.plugins.valheim_plus";
        private Harmony craftyPatches, plusPatches;
        public bool Patched { get; private set; }
        public bool PlusPatched { get; private set; }
        private void Awake()
        {
            craftyPatches = new Harmony(Id + ".crafty");
            plusPatches = new Harmony(Id + ".plus");
            Patched = Install(Crafty, "1.8.24", "AzuCraftyBoxes.Patches.SearchContainersAsWell",
                new[] { typeof(Fermenter), typeof(Inventory), typeof(ItemDrop.ItemData).MakeByRefType() }, "BeforeSearch", craftyPatches);
            PlusPatched = Install(Plus, "0.10.2.0", "ValheimPlus.GameClasses.ApplyFermenterItemCountChanges",
                new[] { typeof(Fermenter.ItemConversion).MakeByRefType() }, "BeforeConversion", plusPatches);
            Logger.LogInfo("Fermenter compatibility: CraftyBoxes=" + Patched + ", ValheimPlus=" + PlusPatched);
        }
        private bool Install(string id, string version, string typeName, Type[] arguments, string prefix, Harmony patches)
        {
            PluginInfo info;
            if (!Chainloader.PluginInfos.TryGetValue(id, out info)) return false;
            if (info.Metadata.Version != new System.Version(version))
            { Logger.LogWarning("Fermenter compatibility requires reviewed " + id + " " + version + "; patch skipped."); return false; }
            try
            {
                Type type = info.Instance.GetType().Assembly.GetType(typeName, true);
                MethodInfo target = AccessTools.Method(type, "Postfix", arguments);
                if (target == null || !target.IsStatic || target.ReturnType != typeof(void)) throw new MissingMethodException(typeName + " changed");
                patches.Patch(target, prefix: new HarmonyMethod(typeof(Plugin), prefix));
                return true;
            }
            catch (Exception error)
            {
                patches.UnpatchSelf();
                Logger.LogError("Fermenter compatibility failed for " + id + ": " + error);
                return false;
            }
        }
        private static bool BeforeSearch(Fermenter __0, Inventory __1)
        {
            Player player = Player.m_localPlayer;
            return __0 != null && __1 != null && player != null && ReferenceEquals(__1, player.GetInventory());
        }
        // Vanilla legitimately returns null when an item has no fermenter recipe.
        private static bool BeforeConversion(Fermenter.ItemConversion __0) { return __0 != null; }
        private void OnDestroy()
        {
            if (craftyPatches != null) craftyPatches.UnpatchSelf();
            if (plusPatches != null) plusPatches.UnpatchSelf();
            Patched = PlusPatched = false;
        }
    }
}
