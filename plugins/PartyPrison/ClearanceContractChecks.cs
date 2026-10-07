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

    private static bool UsesField(MethodDefinition method, string type, string name)
    { return method.Body.Instructions.Any(i => i.Operand is FieldReference && ((FieldReference)i.Operand).DeclaringType.FullName == type && ((FieldReference)i.Operand).Name == name); }

    private static bool GenericType(MethodDefinition method, string type)
    { return CallInstructions(method).Select(i => i.Operand).OfType<GenericInstanceMethod>().Any(m => m.GenericArguments.Any(a => a.FullName == type)); }

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
            case Code.Ldc_I4_3: return expected == 3;
            case Code.Ldc_I4_4: return expected == 4;
            case Code.Ldc_I4_5: return expected == 5;
            case Code.Ldc_I4_6: return expected == 6;
            case Code.Ldc_I4_7: return expected == 7;
            case Code.Ldc_I4_8: return expected == 8;
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
        MethodDefinition create = Method(scene, "CreateObject", "ZDO");
        Check(Calls(create, "UnityEngine.Object", "Instantiate") && create.ReturnType.FullName == "UnityEngine.GameObject",
            "Native object-load hook no longer receives the instantiated object after its Awake.");
        TypeDefinition proxy = Type(game, "LocationProxy");
        MethodDefinition spawn = Method(proxy, "SpawnLocation");
        Check(spawn.ReturnType.FullName == "System.Boolean" && UsesField(spawn, proxy.FullName, "m_instance") &&
            Calls(spawn, "ZoneSystem", "SpawnProxyLocation") && Calls(spawn, "UnityEngine.Transform", "SetParent") && Calls(spawn, "ZNetView", "LoadFields"),
            "Saved static-mask replay requires the spawned location to be parented to its proxy before the postfix.");
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
        FieldDefinition viewBlockMask = clearer.Fields.SingleOrDefault(f => f.Name == "ViewBlockMask");
        Check(viewBlockMask != null && viewBlockMask.IsStatic && viewBlockMask.IsInitOnly && viewBlockMask.FieldType.FullName == "System.Int32",
            "Visibility-only collision filtering must use one cached layer mask.");
        MethodDefinition initialization = Method(clearer, ".cctor");
        Check(initialization.Body.Instructions.Any(i => i.OpCode.Code == Code.Ldstr && (string)i.Operand == "viewblock") &&
            Calls(initialization, "UnityEngine.LayerMask", "GetMask"), "The excluded mask must come from the named viewblock layer.");
        MethodDefinition mask = Method(clearer, "get_CollisionMask");
        Check(mask.Body.Instructions.Any(i => i.OpCode.Code == Code.Ldsfld && i.Operand is FieldReference &&
            ((FieldReference)i.Operand).Name == "ViewBlockMask") && mask.Body.Instructions.Any(i => i.OpCode.Code == Code.Not),
            "Collision queries must keep every layer except visibility-only viewblock colliders.");
        Check(query.Previous.Previous != null && query.Previous.Previous.Operand is MethodReference &&
            ((MethodReference)query.Previous.Previous.Operand).DeclaringType.FullName == clearer.FullName &&
            ((MethodReference)query.Previous.Previous.Operand).Name == "get_CollisionMask",
            "Site planning must apply the visibility-only mask to its footprint query.");
        MethodDefinition solid = Method(clearer, "SolidBounds", "UnityEngine.GameObject");
        Instruction ignoreVisibility = Call(solid, clearer.FullName, "IsViewBlock");
        Check(ignoreVisibility.Next != null && (ignoreVisibility.Next.OpCode.Code == Code.Brtrue || ignoreVisibility.Next.OpCode.Code == Code.Brtrue_S) &&
            ignoreVisibility.Next.Operand is Instruction && ((Instruction)ignoreVisibility.Next.Operand).Offset > ignoreVisibility.Offset &&
            Calls(solid, "UnityEngine.Collider", "get_bounds"), "Whole-root size must use physical collider bounds and skip the oversized visibility collider.");
        MethodDefinition isViewBlock = Method(clearer, "IsViewBlock", "UnityEngine.Collider");
        Check(Calls(isViewBlock, "UnityEngine.GameObject", "get_layer") &&
            isViewBlock.Body.Instructions.Any(i => i.OpCode.Code == Code.Shl) &&
            isViewBlock.Body.Instructions.Any(i => i.OpCode.Code == Code.And), "The visibility check must match the collider's own named layer.");
        MethodDefinition bounded = Method(clearer, "RequireBoundedRoot", "UnityEngine.GameObject", "UnityEngine.Bounds",
            "UnityEngine.Vector3", "UnityEngine.Quaternion", "System.Single");
        Check(Calls(bounded, Namespace + "ClearanceFootprint", "ContainsRoot") && bounded.Body.Instructions.Any(i =>
            i.OpCode.Code == Code.Ldc_R8 && (double)i.Operand == 100d),
            "Whole-root clearance must use the tested expanded-footprint geometry and retain its independent height limit.");
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

    private static void ForceClearance(AssemblyDefinition plugin)
    {
        string rootName = Namespace + "SiteClearer";
        TypeDefinition clearer = Type(plugin, rootName), site = Type(plugin, rootName + "/ForceSite"),
            entry = Type(plugin, rootName + "/ForceEntry"), node = Type(plugin, rootName + "/MoveNode"), transaction = Type(plugin, rootName + "/ForceTransaction");
        MethodDefinition plan = Method(clearer, "PlanForce", "UnityEngine.Vector3", "UnityEngine.Quaternion", "System.Single", "System.Single", "System.Single", Namespace + "PrisonRegion");
        List<MethodReference> planCalls = Graph(plugin, plan).SelectMany(CallInstructions).Select(i => (MethodReference)i.Operand).ToList();
        Check(Calls(plan, clearer.FullName, "RequireHost") && Calls(plan, "ZNetScene", "IsAreaReady"), "Force planning must remain host-only and inspect loaded network objects.");
        Check(!planCalls.Any(m => m.Name == "SetActive" || m.Name == "Destroy" || m.Name == "DestroyZDO" || m.Name == "SetOwner" ||
            m.Name == "ClaimOwnership" || m.Name == "SetPosition" || m.Name == "SetRotation" || m.Name == "Move" ||
            m.DeclaringType.FullName == "System.Reflection.MethodBase" && m.Name == "Invoke"), "Force planning must not alter geometry, ownership, saved positions, or inventories.");
        Check(!planCalls.Any(m => m.DeclaringType.FullName == "UnityEngine.Object" && m.Name.StartsWith("Find", StringComparison.Ordinal)),
            "Force inspection must use bounded collision and sector queries instead of global scene scans.");
        MethodDefinition gather = Method(site, "Gather");
        Check(Calls(gather, "UnityEngine.Physics", "OverlapBox") && Calls(gather, "ZDOMan", "FindSectorObjects") &&
            gather.Body.Instructions.Any(i => IsInt(i, 65536)) && gather.Body.Instructions.Any(i => IsInt(i, 8192)),
            "Force clearance must bound both collision and colliderless spawn-source enumeration.");
        Check(GenericType(gather, "Heightmap") && GenericType(gather, "TerrainComp") && Calls(gather, clearer.FullName, "OccupiedPlayer"),
            "Force clearance must preserve terrain compilers and explicitly guard actual players.");
        MethodDefinition preserve = Method(clearer, "PreserveRoot", "UnityEngine.GameObject", "ZDO");
        Check(GenericType(preserve, "ItemDrop") && GenericType(preserve, "Container") && Calls(preserve, "ZDO", "GetByteArray") &&
            Calls(preserve, "ZDO", "GetString") && UsesField(preserve, "ZDOVars", "s_items"),
            "Storage and loose loot must be moved with current byte-array and legacy inventory data intact.");
        Check(Calls(preserve, "Character", "IsTamed") && Calls(preserve, "Character", "IsBoss") && Calls(preserve, "Character", "GetFaction") &&
            UsesField(preserve, "ZDOVars", "s_tamed") && Calls(preserve, clearer.FullName, "HasInventory"),
            "Forced removal must preserve pets, bosses, friendly actors, and modded inventory roots.");
        MethodDefinition select = Method(site, "AddNetwork", "ZNetView", "System.Collections.Generic.Dictionary`2<UnityEngine.GameObject," + entry.FullName + ">");
        Check(Calls(select, clearer.FullName, "PreserveRoot") && Calls(select, "UnityEngine.Transform", "IsChildOf") && GenericType(select, "ZNetView"),
            "Preserved network parents must carry their complete child graph, and removed roots must enumerate separate child ZDOs.");

        MethodDefinition apply = Method(transaction, "Apply");
        Instruction claim = Call(apply, entry.FullName, "Claim"), stage = Call(apply, entry.FullName, "Stage"), record = Call(apply, node.FullName, "RecordStagedRevision");
        Check(claim.Offset < stage.Offset && stage.Offset < record.Offset && Calls(apply, "UnityEngine.Physics", "SyncTransforms"),
            "All object ownership must be claimed before staging, and the post-move revision must then be recorded.");
        MethodDefinition stageEntry = Method(entry, "Stage");
        Check(Calls(stageEntry, "UnityEngine.Transform", "SetParent") && Calls(stageEntry, node.FullName, "Move") &&
            Calls(stageEntry, "UnityEngine.GameObject", "SetActive"), "Force staging must relocate preserved roots and deactivate removable geometry reversibly.");
        MethodDefinition move = Method(node, "Move", "UnityEngine.Vector3");
        Check(Calls(move, "ZDO", "SetPosition") && Calls(move, "ZDO", "SetRotation") && Calls(move, "ZSyncTransform", "SyncNow"),
            "Relocation must update native saved position/rotation and network transform state.");
        List<MethodReference> movingCalls = Graph(plugin, move).SelectMany(CallInstructions).Select(i => (MethodReference)i.Operand).ToList();
        Check(!movingCalls.Any(m => m.DeclaringType.FullName == "Inventory" || m.Name == "SaveInventory" || m.Name == "DropItems"),
            "Moving storage must preserve original ZDO/inventory bytes rather than reload or rewrite its inventory.");
        MethodDefinition stale = Method(node, "Check", "System.Boolean");
        Check(Calls(stale, "ZDOMan", "GetZDO") && Calls(stale, "ZDO", "get_DataRevision") && UsesField(stale, node.FullName, "stagedRevision") &&
            Calls(stale, "ZNetView", "IsOwner") && Calls(stale, "ZDO", "IsOwner"), "Force validation must check immutable identity, staged revision, and native ownership.");
        MethodDefinition restore = Method(entry, "Restore");
        Check(Calls(restore, "UnityEngine.Transform", "SetParent") && Calls(restore, "UnityEngine.Transform", "SetPositionAndRotation") &&
            Calls(restore, node.FullName, "Restore") && Calls(restore, node.FullName, "RestoreOwner"), "Force rollback must restore original hierarchy, pose, and owners.");

        MethodDefinition validate = Method(transaction, "ValidateCommit");
        Instruction packet = Call(validate, clearer.FullName, "MakePayload"), masks = Call(validate, clearer.FullName, "PrepareMasks");
        Check(Calls(validate, clearer.FullName, "RequireWorld") && Calls(validate, entry.FullName, "Check") &&
            UsesField(validate, transaction.FullName, "preparedMasks"), "Force precommit must validate world/state and store its prepared cumulative proxy masks.");
        MethodDefinition prepareMasks = Method(clearer, "PrepareMasks", "System.Collections.Generic.List`1<" + entry.FullName + ">");
        Check(prepareMasks.Body.Variables.Any(v => v.VariableType.FullName == "System.Collections.Generic.Dictionary`2<ZDO,System.Collections.Generic.List`1<System.String>>") &&
            prepareMasks.Body.Variables.Any(v => v.VariableType.FullName == "System.Collections.Generic.Dictionary`2<ZDO,System.Byte[]>"),
            "Static-mask capacity must be computed cumulatively once per shared proxy ZDO.");
        Check(Calls(prepareMasks, clearer.FullName, "ReadMasks") && Calls(prepareMasks, "ZPackage", "Size") &&
            prepareMasks.Body.Instructions.Any(i => IsInt(i, 8192)) && prepareMasks.Body.Instructions.Any(i => IsInt(i, 1048576)) &&
            prepareMasks.Body.Instructions.Count(i => i.OpCode.Code == Code.Throw) >= 3 && !Calls(prepareMasks, "ZDO", "Set"),
            "Cumulative static paths, count, and bytes must be bounded before persistence, without changing proxy data during validation.");
        MethodDefinition payload = Method(clearer, "MakePayload", site.FullName);
        Check(payload.Body.Instructions.Any(i => IsInt(i, 8192)) && payload.Body.Instructions.Any(i => IsInt(i, 1048576)) &&
            payload.Body.Instructions.Count(i => i.OpCode.Code == Code.Throw) >= 2, "Remote move/static packet capacity must be validated before durable prison storage.");
        Check(packet.Offset < masks.Offset, "Force payload and masks must both be prepared by the reversible validation phase.");
        MethodDefinition commit = Method(transaction, "Commit");
        Instruction latch = commit.Body.Instructions.Single(i => i.OpCode.Code == Code.Stfld && i.Operand is FieldReference && ((FieldReference)i.Operand).Name == "committed");
        Instruction deletion = Call(commit, "ZNetScene", "Destroy");
        Check(IsInt(latch.Previous, 1) && latch.Offset < deletion.Offset && UsesField(commit, transaction.FullName, "preparedMasks") &&
            !Calls(commit, clearer.FullName, "PrepareMasks") && !Calls(commit, clearer.FullName, "ReadMasks"),
            "Irreversible commit must persist exactly the already-preflighted cumulative mask blobs before deletion.");
        Check(Calls(commit, "ZDO", "Set") && Calls(commit, "ZDOMan", "ForceSendZDO") && Calls(commit, clearer.FullName, "FlushDestroyedObjects") &&
            Calls(commit, "ZDOMan", "GetZDO") && UsesField(commit, node.FullName, "Id"), "Force commit must persist moves/masks, flush native deletion, and verify immutable removed IDs.");

        MethodDefinition configure = Method(clearer, "ConfigureRegion", Namespace + "PrisonRegion");
        MethodDefinition reapply = Method(clearer, "ReapplyCurrentStaticRegion");
        Check(Calls(configure, "ZNet", "GetWorldUID") && UsesField(configure, clearer.FullName, "staticRegionWorld") &&
            Calls(configure, clearer.FullName, "SameRegion") && Calls(configure, clearer.FullName, "ReapplyCurrentStaticRegion"),
            "Changed received regions must replay loaded static geometry immediately, with identical states avoiding repeated scans.");
        Instruction worldQuery = Call(reapply, "ZNet", "GetWorldUID"), query = Call(reapply, "UnityEngine.Physics", "OverlapBox");
        Check(worldQuery.Offset < query.Offset && UsesField(reapply, clearer.FullName, "staticRegionWorld") &&
            reapply.Body.Instructions.Any(i => i.OpCode.Code == Code.Ret && i.Offset > worldQuery.Offset && i.Offset < query.Offset),
            "Static footprint replay must return before any query when the cached world identity differs.");
        Check(Calls(reapply, clearer.FullName, "get_CollisionMask") && GenericType(reapply, "Heightmap") && GenericType(reapply, "Player") &&
            Calls(reapply, clearer.FullName, "StaticParent"), "Raw static replay must share the physical mask and preserve terrain, players, and ordinary network roots.");
        Check(UsesField(reapply, Namespace + "PrisonRegion", "Center") && UsesField(reapply, Namespace + "PrisonRegion", "HalfHeight") &&
            Calls(reapply, "UnityEngine.Mathf", "Clamp") && !reapply.Body.Instructions.Any(i => i.OpCode.Code == Code.Ldc_R4 && Math.Abs((float)i.Operand) == 10000f),
            "Loaded outdoor-prison replay must stay in its saved vertical volume and exclude virtual dungeon rooms sharing the same X/Z.");
        MethodDefinition replay = Method(clearer, "ReapplyStatic", "UnityEngine.GameObject");
        Check(Calls(replay, clearer.FullName, "ReadMasks") && Calls(replay, clearer.FullName, "ApplyStaticMasks") &&
            replay.Body.Instructions.Any(i => i.OpCode.Code == Code.Ldstr && (string)i.Operand == "VMP_PP_StorageMoved"),
            "Loaded proxy masks and relocated-storage support protection must be restored from saved ZDO data.");
        foreach (KeyValuePair<string, string> hook in new[] {
            new KeyValuePair<string,string>("ForceClearanceSceneLoad", "ReapplyStatic"),
            new KeyValuePair<string,string>("ForceClearanceLocationLoad", "ReapplyStatic"),
            new KeyValuePair<string,string>("ForceClearanceZoneLoad", "ReapplyZone"),
            new KeyValuePair<string,string>("ForceClearanceDungeonLoad", "ReapplyStatic") }) {
            TypeDefinition patch = Type(plugin, Namespace + hook.Key);
            Check(patch.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch") &&
                patch.Methods.Where(m => m.Name == "Postfix").Any(m => Calls(m, clearer.FullName, hook.Value)),
                "Persistent clearance must replay after native scene, location, zone, and dungeon creation: " + hook.Key);
        }
        MethodDefinition remote = Method(clearer, "ApplyRemote", "System.Byte[]");
        Instruction parseEnd = Call(remote, "ZPackage", "GetPos"), firstMove = Call(remote, clearer.FullName, "ApplyPose");
        Check(Calls(remote, "ZNet", "GetWorldUID") && parseEnd.Offset < firstMove.Offset && Calls(remote, "UnityEngine.Physics", "SyncTransforms"),
            "Authenticated remote clearance must validate the entire world-bound packet before moving objects.");
        Instruction detach = Call(remote, "UnityEngine.Transform", "SetParent");
        Check(Calls(remote, "System.Collections.Generic.HashSet`1<UnityEngine.Transform>", "Contains") &&
            Calls(remote, "UnityEngine.Transform", "get_parent") && detach.Previous != null &&
            remote.Body.Instructions.Any(i => (i.OpCode.Code == Code.Brtrue || i.OpCode.Code == Code.Brtrue_S) && i.Offset < detach.Offset &&
                i.Operand is Instruction && ((Instruction)i.Operand).Offset > detach.Offset && ((Instruction)i.Operand).Offset <= firstMove.Offset),
            "Remote relocation must detach only graph roots and preserve descendants whose ancestor is also moved.");
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
        MethodDefinition command = Method(runtime, "BuildPrison");
        Check(Calls(command, runtime.FullName, "CapturePlacement") && Calls(command, runtime.FullName, "BuildPrisonCore"),
            "The manual console route must capture the host's forward placement.");
        Check(!runtime.Methods.Any(m => m.Name == "AutoBuildNearAltars"), "Destructive prison construction must not run in the background.");
        MethodDefinition confirmed = Method(runtime, "BuildConfirmedPrison");
        Check(Calls(confirmed, runtime.FullName, "BuildPrisonCore") && !Calls(confirmed, runtime.FullName, "CapturePlacement"),
            "Confirmed UI construction must use the first-click snapshot.");
        MethodDefinition buildCore = Method(runtime, "BuildPrisonCore", Namespace + "PrisonPlacementPlan");
        Instruction captureOld = Call(buildCore, builder.FullName, "CaptureStructure");
        Instruction storageGuard = Call(buildCore, builder.FullName, "RequireEmptyStoredContainers");
        Instruction buildSite = Call(buildCore, builder.FullName, "BuildAnywhere");
        Instruction removeOld = Call(buildCore, builder.FullName, "RemoveCapturedStructure");
        Instruction oldFlush = Call(buildCore, Namespace + "SiteClearer", "FlushPendingDestruction");
        Instruction saveWorld = Call(buildCore, "ZNet", "Save");
        Check(captureOld.Offset < buildSite.Offset && buildSite.Offset < removeOld.Offset && removeOld.Offset < oldFlush.Offset && oldFlush.Offset < saveWorld.Offset,
            "Rebuild must capture old identities before new pieces exist, remove only that snapshot, flush, then save the world.");
        Instruction[] layoutQueries = CallInstructions(buildCore).Where(i => ((MethodReference)i.Operand).DeclaringType.FullName == builder.FullName
            && ((MethodReference)i.Operand).Name == "LayoutVersion").ToArray();
        bool layoutCanBypassGuard = layoutQueries.Any(query => buildCore.Body.Instructions.Any(i => i.Offset > query.Offset && i.Offset < storageGuard.Offset
            && i.OpCode.FlowControl == FlowControl.Cond_Branch && i.Operand is Instruction && ((Instruction)i.Operand).Offset > storageGuard.Offset));
        Check(storageGuard.Offset < captureOld.Offset && !layoutCanBypassGuard,
            "Any saved prison must pass the all-container inventory guard before capture, regardless of current layout version.");
        MethodDefinition storage = Method(builder, "RequireEmptyStoredContainers", Namespace + "PrisonRegion",
            "System.Collections.Generic.IList`1<ZDO>", "System.Int32", "System.Func`2<System.Int32,UnityEngine.GameObject>", "System.Func`2<ZDO,Container>");
        Check(Calls(storage, builder.FullName, "InsideStructure") && Calls(storage, "ZDO", "GetByteArray") && Calls(storage, "ZDO", "GetString")
            && Calls(storage, "System.Convert", "FromBase64String") && Calls(storage, Namespace + "CustodyInventory", "Count"),
            "Rebuild storage inspection must enforce exact bounds and strictly decode both native and legacy inventory payloads.");
        Check(GenericType(storage, "Container") && Calls(storage, "Container", "IsInUse") && Calls(storage, "Inventory", "GetAllItems"),
            "Loaded open or unsaved nonempty equipment/legacy containers cannot be destroyed during rebuild.");
        Check(storage.Body.Instructions.Any(i => IsInt(i, 2)) && storage.Body.Instructions.Any(i => IsInt(i, 4))
            && storage.Body.Instructions.Count(i => i.OpCode.Code == Code.Throw) >= 6,
            "Older v2+ prisons must fail closed on missing or duplicated permanent custody indexes, unknown prefabs and nonempty kit storage.");
        Check(!Graph(plugin, storage).SelectMany(CallInstructions).Select(i => (MethodReference)i.Operand)
            .Any(m => m.DeclaringType.FullName == "ZDO" && (m.Name == "Set" || m.Name == "SetOwner") || m.Name == "DestroyZDO" || m.Name == "DropItems"),
            "Rebuild validation must inspect without modifying or clearing the previous saved storage.");
        MethodDefinition force = Method(builder, "BuildAnywhere", "UnityEngine.Vector3", "UnityEngine.Quaternion", Namespace + "PrisonRegion",
            "System.Action`1<" + Namespace + "PrisonRegion>", "System.Action`1<System.String>", "System.Action`1<System.Byte[]>");
        Instruction forcePlan = Call(force, Namespace + "TerrainLeveler", "PlanAnywhere");
        Instruction forceClear = Call(force, Namespace + "SiteClearer", "PlanForce");
        Instruction forceStage = Call(force, Namespace + "SiteClearer/Site", "Apply");
        Instruction forceTerrain = Call(force, Namespace + "TerrainLeveler/Site", "Apply");
        Instruction forceBuild = Call(force, builder.FullName, "BuildPrepared");
        Instruction forceValidate = Call(force, Namespace + "SiteClearer/Transaction", "ValidateCommit");
        Instruction forceTerrainCommit = Call(force, Namespace + "TerrainLeveler/Transaction", "Commit");
        Instruction forceClearCommit = Call(force, Namespace + "SiteClearer/Transaction", "Commit");
        Instruction forceStore = CallInstructions(force).Single(i => ((MethodReference)i.Operand).FullName.Contains("System.Action`1<" + Namespace + "PrisonRegion>::Invoke"));
        Check(forcePlan.Offset < forceClear.Offset && forceClear.Offset < forceStage.Offset && forceStage.Offset < forceTerrain.Offset &&
            forceTerrain.Offset < forceBuild.Offset && forceBuild.Offset < forceValidate.Offset && forceValidate.Offset < forceStore.Offset &&
            forceStore.Offset < forceTerrainCommit.Offset && forceTerrainCommit.Offset < forceClearCommit.Offset,
            "Force construction must stage and validate before durable storage, then commit terrain before irreversible removal.");
        Check(Calls(force, Namespace + "TerrainLeveler/Site", "get_WorldExtent") && Calls(force, builder.FullName, "TryRollback") &&
            force.Body.ExceptionHandlers.Count(h => h.HandlerType == ExceptionHandlerType.Finally) >= 2,
            "Force construction must share the exact enclosing footprint and roll back both preparations on failure.");
        Check(Calls(force, Namespace + "TerrainLeveler/Site", "get_LowestGroundHeight") && Calls(force, Namespace + "TerrainLeveler/Site", "get_HighestGroundHeight") &&
            Calls(force, "UnityEngine.Mathf", "Min") && Calls(force, "UnityEngine.Mathf", "Max") &&
            !force.Body.Instructions.Any(i => i.OpCode.Code == Code.Ldc_R4 && Math.Abs((float)i.Operand) == 10000f),
            "Initial clearance must cover the actual old/new floor interval without deleting virtual dungeon contents far above an outdoor plot.");
        Instruction broadcast = CallInstructions(force).Single(i => ((MethodReference)i.Operand).FullName.Contains("System.Action`1<System.Byte[]>::Invoke"));
        Check(forceClearCommit.Offset < broadcast.Offset && force.Body.ExceptionHandlers.Any(h => h.HandlerType == ExceptionHandlerType.Catch &&
            h.TryStart.Offset <= forceClearCommit.Offset && h.TryEnd.Offset > forceClearCommit.Offset),
            "Remote clearance notification must follow successful persistence/removal, with partial irreversible failures reported.");
        MethodDefinition preflight = Method(builder, "RequireClearSite", "UnityEngine.Vector3", "UnityEngine.Quaternion", "System.Single",
            "System.Single", "System.Single", "System.Boolean");
        Instruction collision = Call(preflight, "UnityEngine.Physics", "OverlapBox");
        Check(collision.Previous != null && collision.Previous.Previous != null && collision.Previous.Previous.Operand is MethodReference &&
            ((MethodReference)collision.Previous.Previous.Operand).DeclaringType.FullName == Namespace + "SiteClearer" &&
            ((MethodReference)collision.Previous.Previous.Operand).Name == "get_CollisionMask",
            "The final building preflight must use the same viewblock exclusion as the clearing query.");
    }

    public static int Main(string[] args)
    {
        try {
            if (args.Length != 2) throw new ArgumentException("Expected native Valheim assembly and built PartyPrison assembly.");
            using (AssemblyDefinition game = AssemblyDefinition.ReadAssembly(args[0]))
            using (AssemblyDefinition plugin = AssemblyDefinition.ReadAssembly(args[1])) {
                Native(game); Clearance(plugin); ForceClearance(plugin); Integration(plugin);
            }
            Console.WriteLine("PASS: " + checks + " destructive-clearance and native deletion contracts. No game process launched.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL at clearance contract " + checks + ": " + error.Message); return 1; }
    }
}
