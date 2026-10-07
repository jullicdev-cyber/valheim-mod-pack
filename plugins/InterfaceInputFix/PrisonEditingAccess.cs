using System;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace ValheimModPack.InterfaceInputFix
{
    internal static class PrisonEditingAccess
    {
        private static Func<GameObject, bool> isProtected;
        private static Func<bool> restricted;
        internal static bool Ready { get { return isProtected != null && restricted != null; } }
        internal static void Bind(Action<string> warn)
        {
            Clear();
            PluginInfo info;
            if (!Chainloader.PluginInfos.TryGetValue("valheimmodpack.partyprison", out info)) return;
            try
            {
                if (info.Metadata.Version != new System.Version("1.5.4") && info.Metadata.Version != new System.Version("1.5.5"))
                    throw new NotSupportedException("reviewed Party Prison 1.5.4 or 1.5.5 required");
                Assembly assembly = info.Instance.GetType().Assembly;
                Type plugin = assembly.GetType("ValheimModPack.PartyPrison.Plugin", true);
                Type arena = assembly.GetType("ValheimModPack.PartyPrison.ArenaBuilder", true);
                MethodInfo protect = AccessTools.Method(arena, "IsProtected", new[] { typeof(GameObject) });
                MethodInfo confined = AccessTools.PropertyGetter(plugin, "Confined"), awaiting = AccessTools.PropertyGetter(plugin, "AwaitingState");
                FieldInfo active = AccessTools.Field(plugin, "Active");
                if (protect == null || !protect.IsStatic || protect.ReturnType != typeof(bool)
                    || active == null || !active.IsStatic || active.FieldType != plugin || !typeof(UnityEngine.Object).IsAssignableFrom(plugin)
                    || confined == null || confined.IsStatic || confined.ReturnType != typeof(bool)
                    || awaiting == null || awaiting.IsStatic || awaiting.ReturnType != typeof(bool))
                    throw new MissingMemberException("Party Prison editing API changed");
                var getter = new DynamicMethod("VMP_PrisonEditingRestricted", typeof(bool), Type.EmptyTypes, plugin.Module, true);
                ILGenerator il = getter.GetILGenerator();
                LocalBuilder current = il.DeclareLocal(plugin);
                Label no = il.DefineLabel(), yes = il.DefineLabel();
                il.Emit(OpCodes.Ldsfld, active); il.Emit(OpCodes.Stloc, current);
                il.Emit(OpCodes.Ldloc, current); il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Call, AccessTools.Method(typeof(UnityEngine.Object), "op_Equality", new[] { typeof(UnityEngine.Object), typeof(UnityEngine.Object) }));
                il.Emit(OpCodes.Brtrue, no);
                il.Emit(OpCodes.Ldloc, current); il.Emit(OpCodes.Call, confined); il.Emit(OpCodes.Brtrue, yes);
                il.Emit(OpCodes.Ldloc, current); il.Emit(OpCodes.Call, awaiting); il.Emit(OpCodes.Brtrue, yes);
                il.MarkLabel(no); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret);
                il.MarkLabel(yes); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ret);
                var readRestricted = (Func<bool>)getter.CreateDelegate(typeof(Func<bool>));
                var readProtected = (Func<GameObject, bool>)Delegate.CreateDelegate(typeof(Func<GameObject, bool>), protect);
                restricted = readRestricted; isProtected = readProtected;
            }
            catch (Exception error)
            {
                Clear();
                if (warn != null) warn("Valheim Plus prison editing protection skipped: " + error.Message);
            }
        }
        internal static bool Restricted { get { return restricted != null && restricted(); } }
        internal static bool Protected(GameObject target) { return isProtected != null && isProtected(target); }
        internal static void Clear() { isProtected = null; restricted = null; }
    }
}
