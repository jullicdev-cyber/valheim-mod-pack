// Managed fixture: real Harmony and actual adapter source; no game or Unity process.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace UnityEngine
{
    public class Object
    {
        public bool Destroyed;
        public static bool operator ==(Object a, Object b)
        { bool an = ReferenceEquals(a, null) || a.Destroyed, bn = ReferenceEquals(b, null) || b.Destroyed; return an && bn || !an && !bn && ReferenceEquals(a, b); }
        public static bool operator !=(Object a, Object b) { return !(a == b); }
        public override bool Equals(object value) { return ReferenceEquals(this, value); }
        public override int GetHashCode() { return base.GetHashCode(); }
    }
    public class Transform { public int position; }
    public class GameObject : Object { public bool Protected, NonemptyContainer; }
}
public class Piece : UnityEngine.Object
{
    public readonly UnityEngine.GameObject gameObject = new UnityEngine.GameObject();
    private readonly UnityEngine.Transform value = new UnityEngine.Transform();
    public UnityEngine.Transform transform { get { if (Destroyed) throw new InvalidOperationException("destroyed Piece"); return value; } }
}
public class Player { public Piece Target; }
namespace BepInEx
{
    public class PluginInfo { public Metadata Metadata = new Metadata(); public object Instance; }
    public class Metadata { public Version Version; }
}
namespace BepInEx.Bootstrap
{
    public static class Chainloader { public static readonly Dictionary<string, BepInEx.PluginInfo> PluginInfos = new Dictionary<string, BepInEx.PluginInfo>(); }
}
namespace ValheimPlus
{
    public class VendorPlugin { }
    public static class AEM
    {
        public static Piece HitPiece;
        public static bool isActive, isInExistence, forceExitNextIteration, Allowed = true, ThrowValidation, Confirm;
        public static int HitPoint, InitialPosition, ValidationCalls, ResetCalls, ClearCalls, Starts, WorkCalls, Runs, Clones, Destroys;
        public static object HitObject;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool ExecuteRayCast(Player player)
        {
            HitPoint = 17; HitObject = player;
            HitPiece = player.Target;
            InitialPosition = HitPiece.transform.position;
            return isValidRayCastTarget();
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool isValidRayCastTarget()
        { ValidationCalls++; if (ThrowValidation) throw new ApplicationException("native validator error"); return Allowed; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void resetObjectTransform() { ResetCalls++; HitPiece.transform.position = InitialPosition; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool hitPieceStillExists() { if (isActive) isInExistence = true; return isInExistence; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void resetObjectInfo() { ClearCalls++; HitPiece = null; HitPoint = InitialPosition = 0; HitObject = null; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void startMode() { Starts++; isActive = true; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void run() { Runs++; if (isActive && hitPieceStillExists()) listenToHotKeysAndDoWork(); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void listenToHotKeysAndDoWork()
        { WorkCalls++; HitPiece.transform.position++; if (Confirm) { Clones++; Destroys++; } }
    }
}
namespace ValheimModPack.PartyPrison
{
    public sealed class Plugin : UnityEngine.Object
    {
        internal static Plugin Active;
        public bool IsConfined, IsAwaiting;
        internal bool Confined { get { return IsConfined; } }
        internal bool AwaitingState { get { return IsAwaiting; } }
    }
    public static class ArenaBuilder
    { public static bool IsProtected(UnityEngine.GameObject target) { return target != null && target.Protected; } }
}
namespace ValheimModPack.InterfaceInputFix
{
    internal static class Plugin { public const string Id = "valheimmodpack.interfaceinputfix"; }
    internal static class ValheimPlusEditingTests
    {
        private const string Vendor = "org.bepinex.plugins.valheim_plus";
        private const string Owner = Plugin.Id + ".valheimplus.editing";
        private static int checks;
        private static void Check(bool value, string label) { checks++; if (!value) throw new Exception(label); }
        private static bool HasOwner(MethodInfo method, string owner)
        { var info = Harmony.GetPatchInfo(method); return info != null && info.Owners.Contains(owner); }
        public static int Main()
        {
            try
            {
                // All three regressions fail on the original fixture, before any patch.
                bool rayThrew = false, resetThrew = false;
                try { ValheimPlus.AEM.ExecuteRayCast(new Player()); } catch (NullReferenceException) { rayThrew = true; }
                try { ValheimPlus.AEM.resetObjectTransform(); } catch (NullReferenceException) { resetThrew = true; }
                ValheimPlus.AEM.isActive = true;
                Check(rayThrew && resetThrew && ValheimPlus.AEM.hitPieceStillExists(), "baseline reproduces ray/reset/existence defects");
                ValheimPlusEditingCompat.Install();
                Check(!ValheimPlusEditingCompat.Ready, "optional vendor absence is a no-op");
                var info = new BepInEx.PluginInfo { Instance = new ValheimPlus.VendorPlugin() };
                BepInEx.Bootstrap.Chainloader.PluginInfos.Add(Vendor, info);
                info.Metadata.Version = new Version("0.10.3.0");
                bool versionRejected = false;
                try { ValheimPlusEditingCompat.Install(); } catch (NotSupportedException) { versionRejected = true; }
                Check(versionRejected && !ValheimPlusEditingCompat.Ready, "unsupported version cannot install");
                info.Metadata.Version = new Version("0.10.2.0");
                ValheimPlusEditingCompat.Install();
                var ray = AccessTools.Method(typeof(ValheimPlus.AEM), "ExecuteRayCast");
                var reset = AccessTools.Method(typeof(ValheimPlus.AEM), "resetObjectTransform");
                var exists = AccessTools.Method(typeof(ValheimPlus.AEM), "hitPieceStillExists");
                Check(ValheimPlusEditingCompat.Ready && HasOwner(ray, Owner) && HasOwner(reset, Owner) && HasOwner(exists, Owner), "actual Harmony patches installed");
                ValheimPlusEditingCompat.Install();
                Check(Harmony.GetPatchInfo(ray).Transpilers.Count == 1, "installation is idempotent");
                int validations = ValheimPlus.AEM.ValidationCalls, clears = ValheimPlus.AEM.ClearCalls;
                Check(!ValheimPlus.AEM.ExecuteRayCast(new Player()), "terrain collider without a Piece returns false");
                Check(ValheimPlus.AEM.HitPiece == null && ValheimPlus.AEM.HitObject == null && ValheimPlus.AEM.HitPoint == 0
                    && ValheimPlus.AEM.ClearCalls == clears + 1 && ValheimPlus.AEM.ValidationCalls == validations,
                    "missing ray target uses vendor reset and avoids native dereference");
                int resets = ValheimPlus.AEM.ResetCalls;
                ValheimPlus.AEM.resetObjectTransform();
                Check(ValheimPlus.AEM.ResetCalls == resets, "missing target does not enter transform reset");
                Check(!ValheimPlus.AEM.hitPieceStillExists() && !ValheimPlus.AEM.isInExistence, "stale active target is rejected");
                var piece = new Piece(); piece.transform.position = 23;
                Check(ValheimPlus.AEM.ExecuteRayCast(new Player { Target = piece }) && ValheimPlus.AEM.InitialPosition == 23
                    && ValheimPlus.AEM.ValidationCalls == validations + 1, "valid target runs original snapshot and validator");
                ValheimPlus.AEM.Allowed = false;
                Check(!ValheimPlus.AEM.ExecuteRayCast(new Player { Target = piece }) && ValheimPlus.AEM.ValidationCalls == validations + 2,
                    "native access denial is preserved");
                ValheimPlus.AEM.ThrowValidation = true;
                bool validationPropagated = false;
                try { ValheimPlus.AEM.ExecuteRayCast(new Player { Target = piece }); } catch (ApplicationException) { validationPropagated = true; }
                Check(validationPropagated, "unrelated native exception is not swallowed");
                ValheimPlus.AEM.ThrowValidation = false;
                piece.transform.position = 99; ValheimPlus.AEM.resetObjectTransform();
                Check(piece.transform.position == 23 && ValheimPlus.AEM.ResetCalls == resets + 1, "valid target reset is unchanged");
                ValheimPlus.AEM.startMode();
                Check(ValheimPlus.AEM.hitPieceStillExists(), "existing target uses vendor existence path");
                piece.Destroyed = true;
                Check(!ValheimPlus.AEM.hitPieceStillExists(), "Unity-destroyed target is rejected although managed reference remains");
                ValheimPlus.AEM.resetObjectTransform();
                Check(ValheimPlus.AEM.ResetCalls == resets + 1, "Unity-destroyed target avoids transform reset");
                Check(!ValheimPlus.AEM.ExecuteRayCast(new Player { Target = piece }) && ValheimPlus.AEM.HitPiece == null,
                    "Unity-destroyed ray target clears stale info");
                CheckPrisonScenarios();
                CheckLayout();
                ValheimPlusEditingCompat.Dispose();
                Check(!ValheimPlusEditingCompat.Ready && !HasOwner(ray, Owner) && !HasOwner(reset, Owner) && !HasOwner(exists, Owner), "dispose removes adapter hooks");
                var foreign = new Harmony("vmp.aem.fixture.foreign");
                try
                {
                    foreign.Patch(ray, transpiler: new HarmonyMethod(typeof(ValheimPlusEditingTests), "DuplicateAssignment") { priority = Priority.First });
                    bool installRejected = false;
                    try { ValheimPlusEditingCompat.Install(); } catch (Exception) { installRejected = true; }
                    Check(installRejected && !ValheimPlusEditingCompat.Ready && !HasOwner(ray, Owner)
                        && !HasOwner(reset, Owner) && !HasOwner(exists, Owner), "real failed transpiler installation rolls back all own hooks; rejected="
                        + installRejected + "; ready=" + ValheimPlusEditingCompat.Ready + "; ray=" + HasOwner(ray, Owner)
                        + "; reset=" + HasOwner(reset, Owner) + "; exists=" + HasOwner(exists, Owner));
                    Check(HasOwner(ray, "vmp.aem.fixture.foreign"), "rollback preserves foreign owner");
                }
                finally { foreign.UnpatchSelf(); }
                if (Environment.GetEnvironmentVariable("VMP_AEM_BASELINE") == "1")
                    Check(!ValheimPlus.AEM.ExecuteRayCast(new Player()), "regression must fail against unpatched baseline");
                System.Console.WriteLine("Valheim Plus editing: " + checks + " actual-source/real-Harmony assertions passed; original null/prison bypass baselines reproduced; marker, sentence, active confirm, normal container, optional API and owner rollback checked.");
                return 0;
            }
            catch (Exception e) { System.Console.Error.WriteLine(e); return 1; }
            finally { ValheimPlusEditingCompat.Dispose(); }
        }
        private static void CheckLayout()
        {
            var method = AccessTools.Method(typeof(ValheimPlusEditingCompat), "GuardRaycast");
            var dynamic = new DynamicMethod("layout", typeof(bool), Type.EmptyTypes, typeof(ValheimPlusEditingTests), true);
            var field = AccessTools.Field(typeof(ValheimPlus.AEM), "HitPiece");
            bool rejected = false;
            try { method.Invoke(null, new object[] { new[] { new CodeInstruction(OpCodes.Ret) }, dynamic.GetILGenerator() }); }
            catch (TargetInvocationException e) { rejected = e.InnerException is InvalidOperationException; }
            Check(rejected, "unknown layout with no assignment is rejected");
            rejected = false;
            try { method.Invoke(null, new object[] { new[] { new CodeInstruction(OpCodes.Stsfld, field), new CodeInstruction(OpCodes.Stsfld, field), new CodeInstruction(OpCodes.Ret) }, dynamic.GetILGenerator() }); }
            catch (TargetInvocationException e) { rejected = e.InnerException is InvalidOperationException; }
            Check(rejected, "multiple target assignments cannot silently accept a vendor change");
            var assignment = new CodeInstruction(OpCodes.Stsfld, field);
            assignment.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
            rejected = false;
            try { method.Invoke(null, new object[] { new[] { assignment, new CodeInstruction(OpCodes.Ret) }, dynamic.GetILGenerator() }); }
            catch (TargetInvocationException e) { rejected = e.InnerException is InvalidOperationException; }
            Check(rejected, "changed exception region cannot receive an unsafe return");
        }
        private static void CheckPrisonScenarios()
        {
            const string prisonId = "valheimmodpack.partyprison";
            ValheimPlusEditingCompat.Dispose();
            var prison = new ValheimModPack.PartyPrison.Plugin();
            ValheimModPack.PartyPrison.Plugin.Active = prison;
            var info = new BepInEx.PluginInfo { Instance = prison };
            info.Metadata.Version = new Version("1.5.5");
            BepInEx.Bootstrap.Chainloader.PluginInfos.Add(prisonId, info);
            var wall = new Piece(); wall.gameObject.Protected = true;
            ValheimPlus.AEM.Allowed = true; ValheimPlus.AEM.Confirm = true;
            int clones = ValheimPlus.AEM.Clones, destroys = ValheimPlus.AEM.Destroys;
            Check(ValheimPlus.AEM.ExecuteRayCast(new Player { Target = wall }), "unpatched vendor accepts a prison piece");
            ValheimPlus.AEM.startMode(); ValheimPlus.AEM.listenToHotKeysAndDoWork();
            Check(ValheimPlus.AEM.Clones == clones + 1 && ValheimPlus.AEM.Destroys == destroys + 1,
                "unpatched active confirm bypasses prison protection");
            if (Environment.GetEnvironmentVariable("VMP_AEM_PRISON_BASELINE") == "1")
                Check(!ValheimPlus.AEM.ExecuteRayCast(new Player { Target = wall }), "protected target rejection must fail against unpatched prison baseline");
            ValheimPlusEditingCompat.Install();
            Check(PrisonEditingAccess.Ready, "reviewed prison API delegates bound");
            int validations = ValheimPlus.AEM.ValidationCalls;
            Check(!ValheimPlus.AEM.ExecuteRayCast(new Player { Target = wall }) && ValheimPlus.AEM.HitPiece == null
                && ValheimPlus.AEM.ValidationCalls == validations, "normal player cannot select a marked prison piece");
            ValheimPlus.AEM.HitPiece = wall; int starts = ValheimPlus.AEM.Starts;
            ValheimPlus.AEM.startMode();
            Check(!ValheimPlus.AEM.isActive && ValheimPlus.AEM.Starts == starts, "direct start on protected target is denied");
            ValheimPlus.AEM.HitPiece = wall;
            Check(!ValheimPlus.AEM.isValidRayCastTarget() && ValheimPlus.AEM.ValidationCalls == validations,
                "direct validation cannot bypass prison protection");
            var chest = new Piece(); chest.gameObject.NonemptyContainer = true;
            Check(ValheimPlus.AEM.ExecuteRayCast(new Player { Target = chest }), "normal nonempty chest retains vendor editing behavior");
            ValheimPlus.AEM.startMode(); clones = ValheimPlus.AEM.Clones; destroys = ValheimPlus.AEM.Destroys;
            int before = chest.transform.position, work = ValheimPlus.AEM.WorkCalls;
            ValheimPlus.AEM.run();
            Check(chest.transform.position == before + 1 && ValheimPlus.AEM.WorkCalls == work + 1
                && ValheimPlus.AEM.Clones == clones + 1 && ValheimPlus.AEM.Destroys == destroys + 1,
                "normal active transform/confirm continues unchanged");
            // A sentence can arrive after AEM entry but before this frame's confirm.
            prison.IsConfined = true; before = chest.transform.position;
            clones = ValheimPlus.AEM.Clones; destroys = ValheimPlus.AEM.Destroys; work = ValheimPlus.AEM.WorkCalls;
            ValheimPlus.AEM.forceExitNextIteration = true;
            ValheimPlus.AEM.run();
            Check(!ValheimPlus.AEM.isActive && !ValheimPlus.AEM.forceExitNextIteration && !ValheimPlus.AEM.isInExistence
                && ValheimPlus.AEM.HitPiece == null && chest.transform.position == before && ValheimPlus.AEM.WorkCalls == work
                && ValheimPlus.AEM.Clones == clones && ValheimPlus.AEM.Destroys == destroys,
                "new sentence stops active confirm before any native work");
            Check(!ValheimPlus.AEM.ExecuteRayCast(new Player { Target = chest }), "confined local player cannot enter on an ordinary piece");
            ValheimPlus.AEM.HitPiece = chest; ValheimPlus.AEM.isActive = true;
            ValheimPlus.AEM.listenToHotKeysAndDoWork();
            Check(!ValheimPlus.AEM.isActive && chest.transform.position == before && ValheimPlus.AEM.Clones == clones,
                "direct work entry cannot bypass a new sentence");
            prison.IsConfined = false; prison.IsAwaiting = true;
            ValheimPlus.AEM.HitPiece = chest; ValheimPlus.AEM.isActive = true;
            ValheimPlus.AEM.resetObjectTransform();
            Check(!ValheimPlus.AEM.isActive && chest.transform.position == before, "awaiting custody state cannot edit via reset");
            Check(!ValheimPlus.AEM.ExecuteRayCast(new Player { Target = chest }), "awaiting custody state blocks selection");
            prison.IsAwaiting = false;
            ValheimPlus.AEM.HitPiece = wall; ValheimPlus.AEM.isActive = true;
            before = wall.transform.position;
            ValheimPlus.AEM.listenToHotKeysAndDoWork();
            Check(!ValheimPlus.AEM.isActive && wall.transform.position == before && ValheimPlus.AEM.Clones == clones,
                "already-active marked target cannot be moved or confirmed");
            // Dictionary metadata changes do not cause a per-frame rebind/type lookup.
            info.Instance = "unexpected plugin instance"; prison.IsConfined = true;
            Check(!ValheimPlus.AEM.ExecuteRayCast(new Player { Target = chest }), "cached delegate reads current sentence without type search");
            info.Instance = prison; prison.IsConfined = false; prison.Destroyed = true;
            Check(ValheimPlus.AEM.ExecuteRayCast(new Player { Target = chest }), "Unity-destroyed prison plugin instance is not a stale restriction");
            prison.Destroyed = false;
            ValheimPlusEditingCompat.Dispose(); info.Metadata.Version = new Version("1.5.4");
            ValheimPlusEditingCompat.Install();
            Check(PrisonEditingAccess.Ready && !ValheimPlus.AEM.ExecuteRayCast(new Player { Target = wall }), "reviewed 1.5.4 API also protects marked pieces");
            ValheimPlusEditingCompat.Dispose(); info.Metadata.Version = new Version("1.6.0");
            var warnings = new List<string>(); ValheimPlusEditingCompat.Install(warnings.Add);
            Check(ValheimPlusEditingCompat.Ready && !PrisonEditingAccess.Ready && warnings.Count == 1,
                "unknown prison version explicitly skips only prison integration");
            Check(ValheimPlus.AEM.ExecuteRayCast(new Player { Target = wall }) && !ValheimPlus.AEM.ExecuteRayCast(new Player()),
                "version mismatch preserves ordinary vendor behavior and null guards");
            ValheimPlusEditingCompat.Dispose(); info.Metadata.Version = new Version("1.5.5"); info.Instance = "API missing";
            warnings.Clear(); ValheimPlusEditingCompat.Install(warnings.Add);
            Check(ValheimPlusEditingCompat.Ready && !PrisonEditingAccess.Ready && warnings.Count == 1,
                "changed prison API is reported without losing null guards");
            ValheimPlusEditingCompat.Dispose(); BepInEx.Bootstrap.Chainloader.PluginInfos.Remove(prisonId);
            ValheimModPack.PartyPrison.Plugin.Active = null; ValheimPlusEditingCompat.Install();
            Check(!PrisonEditingAccess.Ready && ValheimPlus.AEM.ExecuteRayCast(new Player { Target = wall }), "optional prison absence restores null-only editing");
            var gone = new Piece(); gone.Destroyed = true;
            ValheimPlus.AEM.HitPiece = gone; ValheimPlus.AEM.isActive = true; work = ValheimPlus.AEM.WorkCalls;
            ValheimPlus.AEM.run();
            Check(!ValheimPlus.AEM.isActive && ValheimPlus.AEM.HitPiece == null && ValheimPlus.AEM.WorkCalls == work,
                "destroyed active target stops before native transform/confirm");
        }
        private static IEnumerable<CodeInstruction> DuplicateAssignment(IEnumerable<CodeInstruction> instructions)
        {
            var field = AccessTools.Field(typeof(ValheimPlus.AEM), "HitPiece");
            foreach (var instruction in instructions)
            {
                yield return instruction;
                if (instruction.opcode == OpCodes.Stsfld && Equals(instruction.operand, field))
                { yield return new CodeInstruction(OpCodes.Ldsfld, field); yield return new CodeInstruction(OpCodes.Stsfld, field); }
            }
        }
    }
}
