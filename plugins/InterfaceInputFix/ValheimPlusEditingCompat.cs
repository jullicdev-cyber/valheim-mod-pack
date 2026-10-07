using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace ValheimModPack.InterfaceInputFix
{
    // The packaged AEM dereferences a missing Piece on terrain hits and after removal.
    // Keep vendor target/access checks and guard missing objects and prison restrictions.
    internal static class ValheimPlusEditingCompat
    {
        private const string Vendor = "org.bepinex.plugins.valheim_plus";
        private static Harmony patches;
        private static FieldInfo hitPiece, exists, active, forceExit;
        private static MethodInfo resetInfo, raycast;
        private static Func<Piece> target;
        private static Func<bool> editing;
        private static Action stop;
        internal static bool Ready { get { return patches != null; } }

        internal static void Install(Action<string> warn = null)
        {
            if (Ready) return;
            PluginInfo info;
            if (!Chainloader.PluginInfos.TryGetValue(Vendor, out info)) return;
            if (info.Metadata.Version != new System.Version("0.10.2.0"))
                throw new NotSupportedException("Valheim Plus editing adapter requires 0.10.2.0");
            Type type = info.Instance.GetType().Assembly.GetType("ValheimPlus.AEM", true);
            hitPiece = StaticField(type, "HitPiece", typeof(Piece));
            exists = StaticField(type, "isInExistence", typeof(bool));
            active = StaticField(type, "isActive", typeof(bool));
            forceExit = StaticField(type, "forceExitNextIteration", typeof(bool));
            resetInfo = StaticMethod(type, "resetObjectInfo", typeof(void));
            MethodInfo ray = StaticMethod(type, "ExecuteRayCast", typeof(bool), typeof(Player));
            raycast = ray;
            MethodInfo reset = StaticMethod(type, "resetObjectTransform", typeof(void));
            MethodInfo check = StaticMethod(type, "hitPieceStillExists", typeof(bool));
            MethodInfo validate = StaticMethod(type, "isValidRayCastTarget", typeof(bool));
            MethodInfo start = StaticMethod(type, "startMode", typeof(void));
            MethodInfo run = StaticMethod(type, "run", typeof(void));
            MethodInfo work = StaticMethod(type, "listenToHotKeysAndDoWork", typeof(void));
            target = FieldGetter<Piece>(hitPiece); editing = FieldGetter<bool>(active);
            stop = StopAction(type);
            PrisonEditingAccess.Bind(warn);
            var owner = new Harmony(Plugin.Id + ".valheimplus.editing");
            try
            {
                owner.Patch(ray, prefix: new HarmonyMethod(typeof(ValheimPlusEditingCompat), "BeforeRaycast"),
                    transpiler: new HarmonyMethod(typeof(ValheimPlusEditingCompat), "GuardRaycast"));
                owner.Patch(reset, prefix: new HarmonyMethod(typeof(ValheimPlusEditingCompat), "BeforeReset"));
                owner.Patch(check, prefix: new HarmonyMethod(typeof(ValheimPlusEditingCompat), "BeforeExists"));
                owner.Patch(validate, prefix: new HarmonyMethod(typeof(ValheimPlusEditingCompat), "BeforeValidate"));
                owner.Patch(start, prefix: new HarmonyMethod(typeof(ValheimPlusEditingCompat), "BeforeWork"));
                owner.Patch(run, prefix: new HarmonyMethod(typeof(ValheimPlusEditingCompat), "BeforeRun"));
                owner.Patch(work, prefix: new HarmonyMethod(typeof(ValheimPlusEditingCompat), "BeforeWork"));
                patches = owner;
            }
            catch
            {
                // Harmony removes prefixes before transpilers in UnpatchSelf. Drop a
                // rejected transpiler first so rebuilding cannot invoke it during rollback.
                owner.Unpatch(ray, HarmonyPatchType.Transpiler, owner.Id); owner.UnpatchSelf(); throw;
            }
        }
        private static FieldInfo StaticField(Type type, string name, Type expected)
        {
            FieldInfo field = AccessTools.Field(type, name);
            if (field == null || !field.IsStatic || field.FieldType != expected)
                throw new MissingFieldException(type.FullName, name);
            return field;
        }
        private static MethodInfo StaticMethod(Type type, string name, Type result, params Type[] args)
        {
            MethodInfo method = AccessTools.Method(type, name, args);
            if (method == null || !method.IsStatic || method.ReturnType != result)
                throw new MissingMethodException(type.FullName, name);
            return method;
        }
        private static Func<T> FieldGetter<T>(FieldInfo field)
        {
            var getter = new DynamicMethod("VMP_AEM_" + field.Name, typeof(T), Type.EmptyTypes, field.DeclaringType.Module, true);
            getter.GetILGenerator().Emit(OpCodes.Ldsfld, field); getter.GetILGenerator().Emit(OpCodes.Ret);
            return (Func<T>)getter.CreateDelegate(typeof(Func<T>));
        }
        private static Action StopAction(Type type)
        {
            var method = new DynamicMethod("VMP_AEM_StopInvalidTarget", typeof(void), Type.EmptyTypes, type.Module, true);
            ILGenerator il = method.GetILGenerator(); il.Emit(OpCodes.Call, resetInfo);
            foreach (var field in new[] { active, forceExit, exists }) { il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stsfld, field); }
            il.Emit(OpCodes.Ret); return (Action)method.CreateDelegate(typeof(Action));
        }
        private static bool EditableTarget()
        {
            Piece piece = target();
            return piece != null && !PrisonEditingAccess.Restricted && !PrisonEditingAccess.Protected(piece.gameObject);
        }
        private static bool BeforeRaycast(ref bool __result)
        {
            if (!PrisonEditingAccess.Restricted) return true;
            stop(); __result = false; return false;
        }
        private static bool BeforeRun()
        {
            if (!PrisonEditingAccess.Restricted && (!editing() || EditableTarget())) return true;
            stop(); return false;
        }
        private static bool BeforeWork()
        {
            if (EditableTarget()) return true;
            stop(); return false;
        }
        private static bool BeforeReset() { return BeforeWork(); }
        private static bool BeforeValidate(ref bool __result) { return BeforeExists(ref __result); }
        private static bool BeforeExists(ref bool __result)
        {
            if (EditableTarget()) return true;
            stop(); __result = false; return false;
        }
        private static IEnumerable<CodeInstruction> GuardRaycast(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var original = new List<CodeInstruction>(instructions);
            int index = -1, count = 0;
            for (int i = 0; i < original.Count; i++)
                if (original[i].opcode == OpCodes.Stsfld && Equals(original[i].operand, hitPiece)) { index = i; count++; }
            if (count != 1 || index + 1 >= original.Count)
                throw new InvalidOperationException("Expected exactly one Valheim Plus AEM HitPiece assignment");
            // No exception-region return is safe here; fail closed if the vendor layout changes.
            for (int i = 0; i <= index; i++)
                if (original[i].blocks.Count != 0)
                    throw new InvalidOperationException("Valheim Plus AEM raycast exception layout changed");
            Label valid = generator.DefineLabel();
            original[index + 1].labels.Add(valid);
            original.InsertRange(index + 1, new[] {
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ValheimPlusEditingCompat), "EditableTarget")),
                new CodeInstruction(OpCodes.Brtrue, valid),
                new CodeInstruction(OpCodes.Call, resetInfo),
                new CodeInstruction(OpCodes.Ldc_I4_0),
                new CodeInstruction(OpCodes.Ret)
            });
            return original;
        }
        internal static void Dispose()
        {
            if (patches != null) { patches.Unpatch(raycast, HarmonyPatchType.Transpiler, patches.Id); patches.UnpatchSelf(); }
            patches = null; hitPiece = exists = active = forceExit = null; resetInfo = raycast = null;
            target = null; editing = null; stop = null; PrisonEditingAccess.Clear();
        }
    }
}
