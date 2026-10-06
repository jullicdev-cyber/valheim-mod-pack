using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Inspects the actual installed game and built plugin. No Unity object or game process is created.
internal static class ClearanceContractChecks
{
    private const string Namespace = "ValheimModPack.PartyPrison.";
    private static int checks;

    private static void Check(bool condition, string description)
    { ++checks; if (!condition) throw new InvalidOperationException(description); }

    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> roots)
    {
        foreach (TypeDefinition type in roots) {
            yield return type;
            foreach (TypeDefinition nested in AllTypes(type.NestedTypes)) yield return nested;
        }
    }

    private static TypeDefinition Type(AssemblyDefinition assembly, string fullName)
    {
        TypeDefinition result = AllTypes(assembly.MainModule.Types).SingleOrDefault(t => t.FullName == fullName);
        Check(result != null, "Missing clearance contract type: " + fullName);
        return result;
    }

    private static MethodDefinition Method(TypeDefinition type, string name, params string[] parameters)
    {
        MethodDefinition[] candidates = type.Methods.Where(m => m.Name == name &&
            m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameters)).ToArray();
        Check(candidates.Length == 1, "Changed clearance method contract: " + type.FullName + "." + name);
        return candidates[0];
    }

    private static IEnumerable<Instruction> CallInstructions(MethodDefinition method)
    {
        return method.HasBody ? method.Body.Instructions.Where(i => i.OpCode.Code == Code.Call ||
            i.OpCode.Code == Code.Callvirt || i.OpCode.Code == Code.Newobj) : Enumerable.Empty<Instruction>();
    }

    private static bool Calls(MethodDefinition method, string type, string name)
    { return CallInstructions(method).Any(i => ((MethodReference)i.Operand).DeclaringType.FullName == type && ((MethodReference)i.Operand).Name == name); }

    private static Instruction Call(MethodDefinition method, string type, string name)
    {
        Instruction[] matches = CallInstructions(method).Where(i => ((MethodReference)i.Operand).DeclaringType.FullName == type &&
            ((MethodReference)i.Operand).Name == name).ToArray();
        Check(matches.Length == 1, "Expected one bounded staging call: " + type + "." + name + " in " + method.FullName);
        return matches[0];
    }

    private static bool IsInt(Instruction instruction, int expected)
    {
        if (instruction == null) return false;
        switch (instruction.OpCode.Code) {
            case Code.Ldc_I4_0: return expected == 0;
            case Code.Ldc_I4_1: return expected == 1;
            case Code.Ldc_I4_2: return expected == 2;
            case Code.Ldc_I4: return (int)instruction.Operand == expected;
            case Code.Ldc_I4_S: return (sbyte)instruction.Operand == expected;
            default: return false;
        }
    }

    private static IEnumerable<MethodDefinition> Graph(AssemblyDefinition assembly, MethodDefinition root)
    {
        Dictionary<string, MethodDefinition> methods = AllTypes(assembly.MainModule.Types).SelectMany(t => t.Methods).ToDictionary(m => m.FullName);
        Queue<MethodDefinition> pending = new Queue<MethodDefinition>(); HashSet<string> visited = new HashSet<string>();
        pending.Enqueue(root);
        while (pending.Count != 0) {
            MethodDefinition method = pending.Dequeue(); if (!visited.Add(method.FullName) || !method.HasBody) continue;
            yield return method;
            foreach (Instruction instruction in CallInstructions(method)) {
                MethodDefinition child;
                if (methods.TryGetValue(((MethodReference)instruction.Operand).FullName, out child)) pending.Enqueue(child);
            }
        }
    }

    private static void Native(AssemblyDefinition game)
    {
        TypeDefinition scene = Type(game, "ZNetScene"), view = Type(game, "ZNetView"), manager = Type(game, "ZDOMan");
        MethodDefinition destroy = Method(scene, "Destroy", "UnityEngine.GameObject");
        Instruction owner = Call(destroy, "ZDO", "IsOwner");
        Instruction erase = Call(destroy, "ZDOMan", "DestroyZDO");
        Check(owner.Offset < erase.Offset && owner.Next != null &&
            (owner.Next.OpCode.Code == Code.Brfalse || owner.Next.OpCode.Code == Code.Brfalse_S) &&
            owner.Next.Operand is Instruction && ((Instruction)owner.Next.Operand).Offset > erase.Offset,
            "Native scene deletion no longer skips persistent deletion when the object is not owned.");
        Check(Calls(destroy, "ZNetView", "ResetZDO") && Calls(destroy, "UnityEngine.Object", "Destroy"),
            "Native scene deletion must remove the network instance and its local representation.");
        Check(!CallInstructions(destroy).Any(i => {
            string name = ((MethodReference)i.Operand).Name;
            return name.Contains("Damage") || name.Contains("Drop") || name.Contains("Spawn");
        }), "Native scene deletion unexpectedly produces combat damage, drops, or spawned objects.");
        foreach (string clearable in new[] { "Character", "CharacterDrop", "Destructible", "TreeBase", "TreeLog", "MineRock", "MineRock5", "Piece", "WearNTear" }) {
            TypeDefinition nativeType = Type(game, clearable);
            Check(!nativeType.Methods.Where(m => m.Name == "OnDestroy").SelectMany(CallInstructions).Any(i => {
                string name = ((MethodReference)i.Operand).Name;
                return name.Contains("Damage") || name.Contains("Drop") || name.Contains("Spawn") || name == "Instantiate";
            }), "A native clearable object's Unity destruction callback now emits damage or loot: " + clearable);
        }
        MethodDefinition persistent = Method(manager, "DestroyZDO", "ZDO");
        Check(Calls(persistent, "ZDO", "IsOwner") && persistent.Body.Instructions.Any(i => i.Operand is FieldReference &&
            ((FieldReference)i.Operand).Name == "m_destroySendList"), "Native world deletion ownership or reliable destruction queue changed.");
        MethodDefinition claim = Method(view, "ClaimOwnership");
        Check(Calls(claim, "ZDO", "SetOwner") && Calls(claim, "ZDOMan", "GetSessionID"),
            "Claiming deletion ownership no longer assigns the current native session.");
        Method(view, "IsValid"); Method(view, "IsOwner"); Method(view, "GetZDO");
        MethodDefinition flush = Method(manager, "SendDestroyed");
        Check(Calls(flush, "ZRoutedRpc", "InvokeRoutedRPC") && flush.Body.Instructions.Any(i =>
            i.OpCode.Code == Code.Ldstr && (string)i.Operand == "DestroyZDO"),
            "Native queued destruction no longer broadcasts the DestroyZDO route.");
        Instruction routeName = flush.Body.Instructions.Single(i => i.OpCode.Code == Code.Ldstr && (string)i.Operand == "DestroyZDO");
        Check(routeName.Previous != null && routeName.Previous.OpCode.Code == Code.Conv_I8 &&
            IsInt(routeName.Previous.Previous, 0), "Queued destruction must broadcast to everybody, including the host.");
        Check(CallInstructions(flush).Any(i => ((MethodReference)i.Operand).Name == "Clear" &&
            i.Previous != null && i.Previous.Operand is FieldReference && ((FieldReference)i.Previous.Operand).Name == "m_destroySendList"),
            "Native deletion queue must be consumed when the batch is flushed.");
        TypeDefinition routed = Type(game, "ZRoutedRpc");
        MethodDefinition invoke = Method(routed, "InvokeRoutedRPC", "System.Int64", "ZDOID", "System.String", "System.Object[]");
        Instruction local = Call(invoke, "ZRoutedRpc", "HandleRoutedRPC");
        Check(local.Previous != null && invoke.Body.Instructions.Any(i => i.Offset < local.Offset && i.OpCode.Code == Code.Ldarg_1 &&
            i.Next != null && (i.Next.OpCode.Code == Code.Brtrue || i.Next.OpCode.Code == Code.Brtrue_S) &&
            i.Next.Operand is Instruction && ((Instruction)i.Next.Operand).Offset > local.Offset),
            "Broadcast native destruction must execute locally before returning, rather than await a later update.");
        MethodDefinition destroyRpc = Method(manager, "RPC_DestroyZDO", "System.Int64", "ZPackage");
        Check(Calls(destroyRpc, "ZDOMan", "HandleDestroyedZDO"), "The destruction route no longer removes the local saved object.");
        MethodDefinition finalize = Method(manager, "HandleDestroyedZDO", "ZDOID");
        Check(Calls(finalize, "ZDOMan", "RemoveFromSector") && Calls(finalize, "ZDOPool", "Release") &&
            finalize.Body.Instructions.Any(i => i.Operand is FieldReference && ((FieldReference)i.Operand).Name == "m_objectsByID"),
            "Native deletion flush must erase saved objects and release their identities.");
        MethodDefinition prepare = Method(manager, "PrepareSave");
        Check(!Calls(prepare, "ZDOMan", "SendDestroyed"),
            "Native world-save queue behavior changed; recheck whether the explicit destruction flush is still required.");
    }

    private static void Clearance(AssemblyDefinition plugin)
    {
        string rootName = Namespace + "SiteClearer";
        TypeDefinition clearer = Type(plugin, rootName), site = Type(plugin, rootName + "/Site"),
            transaction = Type(plugin, rootName + "/Transaction"), entry = Type(plugin, rootName + "/Entry");
        MethodDefinition plan = Method(clearer, "Plan", "UnityEngine.Vector3", "UnityEngine.Quaternion", "System.Single",
            "System.Single", "System.Single", "UnityEngine.Vector3", "System.Boolean");
        List<MethodReference> inspectCalls = Graph(plugin, plan).SelectMany(CallInstructions).Select(i => (MethodReference)i.Operand).ToList();
        Check(inspectCalls.Any(m => m.DeclaringType.FullName == "UnityEngine.Physics" && m.Name == "OverlapBox"),
            "Clearing candidates must use a bounded footprint collision query.");
        Instruction query = Call(plan, "UnityEngine.Physics", "OverlapBox");
        Check(IsInt(query.Previous, 2), "Clearance inspection must include trigger-only loose loot so levelling cannot bury it.");
        Instruction[] itemChecks = CallInstructions(plan).Where(i => i.Operand is GenericInstanceMethod &&
            ((GenericInstanceMethod)i.Operand).GenericArguments.Any(a => a.FullName == "ItemDrop")).ToArray();
        Instruction[] viewChecks = CallInstructions(plan).Where(i => i.Operand is GenericInstanceMethod &&
            ((GenericInstanceMethod)i.Operand).GenericArguments.Any(a => a.FullName == "ZNetView")).ToArray();
        Check(itemChecks.Length >= 1 && viewChecks.Length >= 1 && plan.Body.Instructions.Any(i => i.OpCode.Code == Code.Throw &&
            i.Offset > itemChecks[0].Offset && i.Offset < viewChecks[0].Offset),
            "Loose inventory drops must reject the candidate instead of being destroyed, ignored, or buried by terrain levelling.");
        Check(!inspectCalls.Any(m => m.Name == "SetActive" || m.Name == "Destroy" || m.Name == "DestroyZDO" ||
            m.Name == "ClaimOwnership" || m.Name == "SetOwner" || m.Name == "Apply" ||
            m.DeclaringType.FullName == "System.Reflection.MethodBase" && m.Name == "Invoke"),
            "Candidate search must not stage, claim, destroy, or invoke a destructive reflection helper.");
        Check(!inspectCalls.Any(m => m.DeclaringType.FullName == "UnityEngine.Object" && m.Name.StartsWith("Find", StringComparison.Ordinal)),
            "Clearing inspection must not scan every scene object.");
        Check(plan.Body.Instructions.Any(i => IsInt(i, 512)) && plan.Body.Instructions.Any(i => IsInt(i, 8192)) &&
            plan.Body.Instructions.Count(i => i.OpCode.Code == Code.Throw) >= 3,
            "Unbounded colliders or object counts must reject the candidate instead of partially clearing it.");
        Check(CallInstructions(plan).Count(i => ((MethodReference)i.Operand).DeclaringType.FullName == clearer.FullName &&
            ((MethodReference)i.Operand).Name == "RequireOutsideAltar") == 2 && Calls(plan, clearer.FullName, "RequireBoundedRoot"),
            "The footprint and whole selected roots must both protect the altar and keep bounded overhang.");

        MethodDefinition classify = Method(clearer, "Classify", "UnityEngine.GameObject", "ZNetView", "System.Boolean");
        List<MethodReference> classifyCalls = CallInstructions(classify).Select(i => (MethodReference)i.Operand).ToList();
        foreach (string type in new[] { "Player", "Container", "ZNetView" })
            Check(classifyCalls.OfType<GenericInstanceMethod>().Any(m => m.GenericArguments.Any(a => a.FullName == type)),
                "Clearing classification lost the player, storage, or nested-network protection: " + type);
        foreach (string marker in new[] { "VMP_PP_Protected", "VMP_PP_Mob", "VMP_PP_Armory", "VMP_PP_Custody",
            "VMP_PP_Inmate", "VMP_PP_Exit", "VMP_PP_InnerGate" })
            Check(classify.Body.Instructions.Any(i => i.OpCode.Code == Code.Ldstr && (string)i.Operand == marker),
                "Clearing could delete a marked prison object: " + marker);
        Check(classify.Body.Instructions.Any(i => i.Operand is FieldReference && ((FieldReference)i.Operand).DeclaringType.FullName == "ZDOVars" &&
            ((FieldReference)i.Operand).Name == "s_items") && Calls(classify, "ZDO", "GetString") && Calls(classify, "ZDO", "GetByteArray"),
            "Objects containing current byte-array or legacy string saved inventory data must reject clearing.");
        Check(Calls(classify, "Character", "IsBoss") && Calls(classify, "Character", "IsTamed") &&
            Calls(classify, "Character", "GetFaction") && Calls(classify, "BaseAI", "IsEnemy") &&
            classify.Body.Instructions.Any(i => i.Operand is FieldReference && ((FieldReference)i.Operand).Name == "s_tamed") &&
            classify.Body.Instructions.Any(i => i.Operand is FieldReference && ((FieldReference)i.Operand).Name == "m_startsTamed"),
            "Deleting wild mobs must retain boss, pet, faction, and persisted tame-state protections.");
        Check(Calls(classify, clearer.FullName, "HasInventory") && Calls(classify, "Piece", "GetCreator") &&
            classify.Body.Instructions.Any(i => i.OpCode.Code == Code.Ldarg_2),
            "Classification must guard modded inventories and require explicit permission to remove player-built pieces.");
        MethodDefinition types = Method(clearer, ".cctor");
        foreach (string protectedType in new[] { "BossStone", "OfferingBowl", "DungeonGenerator", "LocationProxy", "Trader", "ItemStand", "ArmorStand", "Fermenter", "Smelter" })
            Check(types.Body.Instructions.Any(i => i.OpCode.Code == Code.Ldstr && (string)i.Operand == protectedType),
                "A world landmark or stored-resource type lost clearance protection: " + protectedType);
        List<MethodReference> allCalls = AllTypes(new[] { clearer }).SelectMany(t => t.Methods).SelectMany(CallInstructions).Select(i => (MethodReference)i.Operand).ToList();
        Check(!allCalls.Any(m => m.Name == "Damage" || m.Name == "ApplyDamage" || m.Name == "RPC_Damage" ||
            m.Name == "GenerateDropList" || m.Name == "DropItems" || m.Name == "CreateDrop" ||
            m.DeclaringType.FullName == "UnityEngine.Object" && m.Name == "Instantiate"),
            "Site cleanup must remove selected native objects directly without combat damage, loot, or replacement spawns.");

        MethodDefinition applySite = Method(site, "Apply");
        Instruction replan = Call(applySite, clearer.FullName, "Plan");
        Instruction applyTransaction = Call(applySite, transaction.FullName, "Apply");
        Check(replan.Offset < applyTransaction.Offset && Calls(applySite, entry.FullName, "Check") && Calls(applySite, transaction.FullName, "Dispose"),
            "The selected plan must recheck newly arrived obstacles and roll back partial staging failures.");
        MethodDefinition apply = Method(transaction, "Apply");
        Instruction claim = Call(apply, entry.FullName, "Claim");
        Instruction deactivate = Call(apply, "UnityEngine.GameObject", "SetActive");
        Check(claim.Offset < deactivate.Offset && IsInt(deactivate.Previous, 0) && Calls(apply, clearer.FullName, "Classify") &&
            Calls(apply, entry.FullName, "Check") && Calls(apply, "UnityEngine.Physics", "SyncTransforms"),
            "Staging must validate and claim every object before deactivating them, then synchronize the collision scene.");
        MethodDefinition validation = Method(transaction, "ValidateCommit");
        Check(Calls(validation, clearer.FullName, "RequireWorld") && Calls(validation, entry.FullName, "Check") &&
            Calls(validation, clearer.FullName, "Classify") && Calls(validation, "ZNetView", "IsOwner") && Calls(validation, "ZDO", "IsOwner"),
            "Precommit validation must preserve world identity, inventory/classification, and ownership gates.");
        MethodDefinition stale = Method(entry, "Check", "System.Boolean");
        Check(Calls(stale, "ZDOMan", "GetZDO") && Calls(stale, "ZDO", "get_DataRevision") &&
            Calls(stale, "UnityEngine.Transform", "get_position") && Calls(stale, "UnityEngine.Transform", "get_rotation") &&
            Calls(stale, "UnityEngine.Transform", "get_lossyScale"), "Stale object identity, revision, or moved collision bounds must block clearing.");
        MethodDefinition restore = Method(entry, "Restore");
        Check(Calls(restore, "ZDO", "SetOwner") && Calls(restore, "UnityEngine.GameObject", "SetActive") &&
            restore.Body.Instructions.Any(i => i.Operand is FieldReference && ((FieldReference)i.Operand).Name == "owner") &&
            restore.Body.Instructions.Any(i => i.Operand is FieldReference && ((FieldReference)i.Operand).Name == "active"),
            "Reversible staging must restore the original owner and activation state, rather than default values.");
        MethodDefinition dispose = Method(transaction, "Dispose");
        Check(Calls(dispose, entry.FullName, "Restore") && dispose.Body.Instructions.Any(i => i.Operand is FieldReference &&
            ((FieldReference)i.Operand).Name == "committed" && i.Next != null &&
            (i.Next.OpCode.Code == Code.Brfalse || i.Next.OpCode.Code == Code.Brfalse_S)),
            "Disposed committed cleanup must not resurrect objects after native destruction begins.");
        MethodDefinition commit = Method(transaction, "Commit");
        Instruction deletion = Call(commit, "ZNetScene", "Destroy");
        Instruction latch = commit.Body.Instructions.Single(i => i.OpCode.Code == Code.Stfld && i.Operand is FieldReference &&
            ((FieldReference)i.Operand).Name == "committed");
        Check(IsInt(latch.Previous, 1) && latch.Offset < deletion.Offset && Calls(commit, transaction.FullName, "ValidateCommit"),
            "The irreversible latch must be set before the first deletion so partial failures cannot roll back committed construction.");
        // Native deletion only queues ZDO IDs. The final implementation must flush
        // the queue and verify immutable saved IDs before the world-save caller runs.
        Instruction[] flushCalls = CallInstructions(commit).Where(i => ((MethodReference)i.Operand).DeclaringType.FullName == clearer.FullName &&
            ((MethodReference)i.Operand).Name == "FlushDestroyedObjects").ToArray();
        Check(flushCalls.Length == 2 && commit.Body.ExceptionHandlers.Any(h => h.HandlerType == ExceptionHandlerType.Catch &&
            h.HandlerStart.Offset <= flushCalls[1].Offset && h.HandlerEnd.Offset > flushCalls[1].Offset),
            "Partial deletion failures must still flush the objects already queued before restoring survivors.");
        Instruction flush = flushCalls[0];
        Check(deletion.Offset < flush.Offset && Calls(commit, "ZDOMan", "GetZDO") &&
            commit.Body.Instructions.Any(i => i.OpCode.Code == Code.Ldfld && i.Operand is FieldReference &&
                ((FieldReference)i.Operand).DeclaringType.FullName == entry.FullName && ((FieldReference)i.Operand).FieldType.FullName == "ZDOID" &&
                i.Offset > flush.Offset), "Commit must flush queued destruction and verify the captured pre-deletion IDs are absent from the saved world.");
        FieldDefinition identity = entry.Fields.Single(f => f.FieldType.FullName == "ZDOID");
        Check(identity.IsInitOnly, "Post-flush verification must use an immutable ID because destroyed ZDOs are released to a pool.");
        MethodDefinition flushMethod = Method(clearer, "FlushDestroyedObjects");
        Check(Calls(flushMethod, "System.Reflection.MethodBase", "Invoke") && types.Body.Instructions.Any(i =>
            i.OpCode.Code == Code.Ldstr && (string)i.Operand == "SendDestroyed"),
            "The native destruction batch is no longer synchronously flushed using cached SendDestroyed metadata.");
    }

    private static void Integration(AssemblyDefinition plugin)
    {
        TypeDefinition builder = Type(plugin, Namespace + "ArenaBuilder");
        MethodDefinition build = Method(builder, "BuildNearAltars", "System.Boolean",
            "System.Action`1<ValheimModPack.PartyPrison.PrisonRegion>", "System.Action`1<System.String>");
        Instruction stage = Call(build, Namespace + "SiteClearer/Site", "Apply");
        Instruction terrain = Call(build, Namespace + "TerrainLeveler/Site", "Apply");
        Instruction construct = Call(build, builder.FullName, "Build");
        Instruction validate = Call(build, Namespace + "SiteClearer/Transaction", "ValidateCommit");
        Instruction terrainCommit = Call(build, Namespace + "TerrainLeveler/Transaction", "Commit");
        Instruction clearCommit = Call(build, Namespace + "SiteClearer/Transaction", "Commit");
        Instruction[] durableWrites = CallInstructions(build).Where(i => ((MethodReference)i.Operand).FullName.Contains("System.Action`1<ValheimModPack.PartyPrison.PrisonRegion>::Invoke")).ToArray();
        Check(durableWrites.Length == 1, "Clearing construction must have exactly one durable prison-region callback.");
        Instruction save = durableWrites[0];
        Check(stage.Offset < terrain.Offset && terrain.Offset < construct.Offset && construct.Offset < validate.Offset &&
            validate.Offset < save.Offset && save.Offset < terrainCommit.Offset && terrainCommit.Offset < clearCommit.Offset,
            "Permanent clearing must follow reversible staging, construction, stale-state validation, durable region persistence, and committed terrain.");
        Check(build.Body.ExceptionHandlers.Count(h => h.HandlerType == ExceptionHandlerType.Finally) >= 2,
            "Both clearing and terrain staging need disposal if building or saving the prison fails.");
        Check(Calls(build, builder.FullName, "TryRollback"), "Failed region persistence must remove the newly built prison before restoring staged objects.");
        Check(build.Body.ExceptionHandlers.Any(h => h.HandlerType == ExceptionHandlerType.Catch &&
            h.TryStart.Offset <= clearCommit.Offset && h.TryEnd.Offset > clearCommit.Offset),
            "A partial permanent-clear failure must be caught after the new prison is committed.");
        Check(CallInstructions(build).Any(i => ((MethodReference)i.Operand).FullName.Contains("System.Action`1<System.String>::Invoke") && i.Offset > clearCommit.Offset),
            "Partial clearing failures must reach the supplied visible error reporter.");

        TypeDefinition runtime = Type(plugin, Namespace + "Plugin");
        foreach (KeyValuePair<string, bool> route in new[] {
            new KeyValuePair<string, bool>("BuildPrison", true), new KeyValuePair<string, bool>("AutoBuildNearAltars", false) }) {
            MethodDefinition method = Method(runtime, route.Key);
            Instruction invoke = Call(method, runtime.FullName, "BuildPrisonCore");
            Check(IsInt(invoke.Previous, route.Value ? 1 : 0),
                "Destructive site clearing must be restricted to manual generation; changed route: " + route.Key);
        }
        MethodDefinition buildCore = Method(runtime, "BuildPrisonCore", "System.Boolean");
        Instruction buildSite = Call(buildCore, builder.FullName, "BuildNearAltars");
        Instruction saveWorld = Call(buildCore, "ZNet", "Save");
        Check(buildSite.Offset < saveWorld.Offset, "The world-save snapshot must follow permanent clearing and its synchronous destruction flush.");
    }

    public static int Main(string[] args)
    {
        try {
            if (args.Length != 2) throw new ArgumentException("Expected native Valheim assembly and built PartyPrison assembly.");
            using (AssemblyDefinition game = AssemblyDefinition.ReadAssembly(args[0]))
            using (AssemblyDefinition plugin = AssemblyDefinition.ReadAssembly(args[1])) {
                Native(game); Clearance(plugin); Integration(plugin);
            }
            Console.WriteLine("PASS: " + checks + " destructive-clearance and native deletion contracts. No game process launched.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL at clearance contract " + checks + ": " + error.Message); return 1; }
    }
}
