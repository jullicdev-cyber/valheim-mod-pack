// Test-only menu probe. Compile separately; never include in the released mod.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimModPack.PartyPrison.NativeVerification
{
    [BepInPlugin("valheimmodpack.partyprison.nativeprobe", "Party Prison isolated native probe", "1.1.0")]
    [BepInDependency("valheimmodpack.partyprison", "1.1.0")]
    public sealed class NativeChecks : BaseUnityPlugin
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private readonly StringBuilder report = new StringBuilder();
        private int checks;
        private static int simulatedInputRequests;
        private static bool simulateInputRequests;
        private static Inventory exactCallbackInventory;
        private static int exactCallbackMode;
        private static int exactCallbackCalls;
        private static bool simulatePlacement, failPlacement;
        private static int placementCaptures, placementBuilds;
        private static PrisonPoint placementHost, placementLook;
        private static PrisonPlacementPlan capturedPlacement, constructedPlacement;
        private static readonly HashSet<GameObject> registryFixtureObjects = new HashSet<GameObject>();
        private static readonly HashSet<ZDO> registryFixtureZdos = new HashSet<ZDO>();
        private float started, prefabAt = -1;
        private bool finished;
        private static string Root { get { return Environment.GetEnvironmentVariable("VMP_PARTYPRISON_PROBE"); } }

        private void Awake()
        {
            if (String.IsNullOrEmpty(Root)) { enabled = false; return; }
            Application.runInBackground = true;
            started = Time.realtimeSinceStartup;
            string expectedRoot = Path.GetFullPath(Path.Combine(Root, "BepInEx"));
            if (!String.Equals(expectedRoot.TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(Paths.BepInExRootPath).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            { Finish("FAIL: Probe refused a BepInEx directory outside its isolated fixture.", 2); return; }
            Utils.SetSaveDataPath(Path.Combine(Root, "Saves"));
            PrefabManager.OnVanillaPrefabsAvailable += PrefabsAvailable;
            Logger.LogInfo("Party Prison probe uses isolated saves and never opens a world: " + Root);
        }

        private void PrefabsAvailable() { prefabAt = Time.realtimeSinceStartup; }

        private void Update()
        {
            if (finished || String.IsNullOrEmpty(Root)) return;
            if (Player.m_localPlayer != null || Game.instance != null)
            { Finish("FAIL: A real game or local player exists; menu-only probe aborted.", 3); return; }
            bool canvasReady = GUIManager.CustomGUIFront != null && GUIManager.Instance.AveriaSerif != null;
            float elapsed = Time.realtimeSinceStartup - started;
            if (canvasReady && elapsed > 2 && (prefabAt >= 0 && Time.realtimeSinceStartup - prefabAt > 2 || elapsed > 15))
            {
                try
                {
                    Run(); Finish("PASS: " + checks + " Party Prison native assertions.\n" + report
                        + "Scope: isolated main menu, full-pack startup, registered patches, native Chat/Console command routing with a safe sentinel, synthetic UI and available prefab definitions.\n"
                        + "UNVERIFIED: live host-look construction, host/client inventory custody, chest retrieval, automatic arena waves, death recovery, reconnect, wall protection and release need an in-game multiplayer test.\n", 0);
                }
                catch (Exception error) { Finish("FAIL: " + error + "\n" + report, 4); }
            }
            else if (elapsed > 82) Finish("FAIL: Timed out waiting for Jotunn's native menu UI.", 5);
        }

        private void Run()
        {
            Check(Player.m_localPlayer == null && Game.instance == null, "no live player, game or user world");
            Check(Chainloader.PluginInfos.ContainsKey(Plugin.Id), "Party Prison plugin is registered");
            Check(Chainloader.PluginInfos[Plugin.Id].Instance != null && Chainloader.PluginInfos[Plugin.Id].Instance.enabled, "Party Prison native plugin is enabled");
            string expectedPath = Path.Combine(Root, "expected-plugins.txt");
            Check(File.Exists(expectedPath), "full-pack expected plugin inventory exists");
            int expected = 0;
            foreach (string line in File.ReadAllLines(expectedPath))
            {
                if (String.IsNullOrWhiteSpace(line)) continue;
                string id = line.Split('\t')[0];
                Check(Chainloader.PluginInfos.ContainsKey(id) && Chainloader.PluginInfos[id].Instance != null, "full-pack plugin loaded: " + id);
                ++expected;
            }
            report.AppendLine("PASS: All " + expected + " inventoried pack plugins loaded with Party Prison.");
            FieldInfo worldObjects = typeof(ZDOMan).GetField("m_objectsByID", All);
            Check(worldObjects != null && worldObjects.FieldType == typeof(Dictionary<ZDOID, ZDO>),
                "installed authoritative ZDO table matches the remote arena count/cleanup adapter");
            Patch(typeof(Character), "CheckDeath", Type.EmptyTypes);
            Patch(typeof(Player), "TeleportTo", new[] { typeof(Vector3), typeof(Quaternion), typeof(bool) });
            Patch(typeof(Player), "PlacePiece", new[] { typeof(Piece), typeof(Vector3), typeof(Quaternion), typeof(bool), typeof(bool) });
            Patch(typeof(WearNTear), "ApplyDamage", new[] { typeof(float), typeof(HitData) });
            Patch(typeof(WearNTear), "RPC_Remove", new[] { typeof(long), typeof(bool) });
            Patch(typeof(Door), "RPC_UseDoor", new[] { typeof(long), typeof(bool) });
            Patch(typeof(Container), "RPC_RequestOpen", new[] { typeof(long), typeof(long) });
            Patch(typeof(Container), "RPC_RequestStack", new[] { typeof(long), typeof(long) });
            Patch(typeof(Container), "RPC_RequestTakeAll", new[] { typeof(long), typeof(long) });
            Patch(typeof(Humanoid), "Pickup", new[] { typeof(GameObject), typeof(bool), typeof(bool) });
            MethodInfo drops = typeof(CharacterDrop).GetMethod("GenerateDropList", All, null, Type.EmptyTypes, null);
            Check(drops != null, "installed native creature loot method exists");
            Patches dropPatches = Harmony.GetPatchInfo(drops);
            Check(dropPatches == null || !dropPatches.Owners.Contains(Plugin.Id), "Party Prison leaves ordinary creature drop generation available for arena farming");
            Patch(typeof(GUIManager), "ResetInputBlock", Type.EmptyTypes);
            Patch(typeof(Inventory), "AddItem", new[] { typeof(ItemDrop.ItemData) });
            Patch(typeof(Inventory), "AddItem", new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int), typeof(bool) });
            Patch(typeof(Inventory), "FindFreeStackItem", new[] { typeof(string), typeof(int), typeof(float) });
            Patch(typeof(Inventory), "StackAll", new[] { typeof(Inventory), typeof(bool) });
            Patch(typeof(Inventory), "GetAllItems", Type.EmptyTypes);
            Patch(typeof(Inventory), "GetAllItems", new[] { typeof(string), typeof(List<ItemDrop.ItemData>) });
            Patch(typeof(Inventory), "GetAllItems", new[] { typeof(ItemDrop.ItemData.ItemType), typeof(List<ItemDrop.ItemData>) });
            Patch(typeof(Inventory), "GetItemAt", new[] { typeof(int), typeof(int) });
            Patch(typeof(Container), "Awake", Type.EmptyTypes);
            Patch(typeof(Container), "AddDefaultItems", Type.EmptyTypes);
            Patch(typeof(Container), "Load", Type.EmptyTypes);
            Patch(typeof(Container), "GetInventory", Type.EmptyTypes);
            Patch(typeof(ItemDrop), "AutoStackItems", Type.EmptyTypes);
            Patch(typeof(TerrainComp), "ApplyToHeightmap", new[] { typeof(Texture2D), typeof(List<float>), typeof(float[]), typeof(float[]), typeof(Heightmap) });
            report.AppendLine("PASS: Required confinement, death, protection and input-reset Harmony patches bind; native creature loot generation remains available.");
            CheckCommandRouting(); CheckGroupRadiusCompatibility(); CheckUi(); CheckInventoryIsolation(); CheckCustodyInventory(); CheckExactWithdrawalInsertion(); CheckCustodyMask(); CheckCustodyRegistry(); CheckForcedTerrainHeights(); CheckForceClearance(); CheckLootPickupGuard(); CheckGeometry(); CheckPrefabs();
            Check(Player.m_localPlayer == null && Game.instance == null, "probe leaves no game or local player registration");
        }

        private void Patch(Type target, string name, Type[] parameters)
        {
            MethodInfo method = target.GetMethod(name, All, null, parameters, null);
            Check(method != null, "installed native target exists: " + target.Name + "." + name);
            Patches patches = Harmony.GetPatchInfo(method);
            Check(patches != null && patches.Owners.Contains(Plugin.Id), "Party Prison Harmony patch registered: " + target.Name + "." + name);
        }

        private void CheckCommandRouting()
        {
            FieldInfo commandField = typeof(Terminal).GetField("commands", All);
            Check(commandField != null && commandField.FieldType == typeof(Dictionary<string, Terminal.ConsoleCommand>),
                "installed native Terminal exposes the expected command registry");
            var commands = (Dictionary<string, Terminal.ConsoleCommand>)commandField.GetValue(null);
            Terminal.ConsoleCommand original;
            Check(commands != null && commands.TryGetValue("prison", out original), "actual full-pack startup registers the prison command");
            // Assign separately because the assertion helper does not establish
            // definite assignment for the old compiler used by the pack.
            original = commands["prison"];
            Check(!original.IsCheat && !original.IsNetwork && !original.OnlyServer && !original.RemoteCommand
                && !original.IsSecret && !original.AllowInDevBuild && !original.HideBehindDevCommands && !original.OnlyAdmin,
                "actual prison registration has no cheat, network, server-only, remote or hidden-command restriction");
            FieldInfo actionField = typeof(Terminal.ConsoleCommand).GetField("action", All);
            FieldInfo failableField = typeof(Terminal.ConsoleCommand).GetField("actionFailable", All);
            var action = actionField == null ? null : actionField.GetValue(original) as Delegate;
            object plugin = Chainloader.PluginInfos[Plugin.Id].Instance;
            Check(action != null && ReferenceEquals(action.Target, plugin) && action.Method == typeof(Plugin).GetMethod("Command", All)
                && failableField != null && failableField.GetValue(original) == null,
                "actual prison registry callback is this loaded plugin's Command method");

            FieldInfo chatSingleton = typeof(Chat).GetField("m_instance", All);
            FieldInfo consoleSingleton = typeof(global::Console).GetField("m_instance", All);
            Check(chatSingleton != null && consoleSingleton != null, "native Chat and Console singleton fields can be preserved by the fixture");
            object oldChat = chatSingleton.GetValue(null), oldConsole = consoleSingleton.GetValue(null);
            bool oldCheat = Terminal.m_cheat;
            Terminal.ConsoleCommand slashAlias;
            bool hadSlashAlias = commands.TryGetValue("/prison", out slashAlias);
            GameObject fixture = new GameObject("Party Prison inactive command route fixture");
            fixture.SetActive(false);
            int dispatches = 0;
            Terminal.ConsoleEventArgs received = null;
            try
            {
                Chat chat = (Chat)CommandTerminalFixture(typeof(Chat), fixture);
                Terminal console = CommandTerminalFixture(typeof(global::Console), fixture);
                Check(!chat.gameObject.activeInHierarchy && !console.gameObject.activeInHierarchy
                    && ReferenceEquals(chatSingleton.GetValue(null), oldChat) && ReferenceEquals(consoleSingleton.GetValue(null), oldConsole),
                    "synthetic Chat and Console stay inactive and never replace native singletons or execute Awake");
                Terminal.m_cheat = false;
                MethodInfo validity = typeof(Terminal.ConsoleCommand).GetMethod("IsValid", All, null, new[] { typeof(Terminal), typeof(bool) }, null);
                Check(validity != null && (bool)validity.Invoke(original, new object[] { chat, false }),
                    "actual prison command is valid in native Chat with cheats disabled and no player or world");
                Check((bool)validity.Invoke(original, new object[] { console, false }),
                    "actual prison command is valid in native Console with cheats disabled and no player or world");
                var sentinel = new Terminal.ConsoleCommand("prison", "Safe menu-only route sentinel",
                    (Terminal.ConsoleEvent)delegate(Terminal.ConsoleEventArgs args) { ++dispatches; received = args; });
                // A future explicit slash alias must never execute a real build
                // in this test. The base Console parser is tested without it.
                commands.Remove("/prison");
                Check(ReferenceEquals(commands["prison"], sentinel), "safe sentinel temporarily replaces only the prison callback");
                MethodInfo chatInput = typeof(Chat).GetMethod("InputText", All, null, Type.EmptyTypes, null);
                MethodInfo consoleInput = typeof(Terminal).GetMethod("InputText", All, null, Type.EmptyTypes, null);
                Check(chatInput != null && consoleInput != null, "native Chat override and base Console InputText methods exist");

                CommandInput(chat, "/prison build"); chatInput.Invoke(chat, null);
                Check(dispatches == 1 && received != null && ReferenceEquals(received.Context, chat)
                    && ReferenceEquals(received.Commmand, sentinel) && received.FullLine == "prison build"
                    && received.Args.SequenceEqual(new[] { "prison", "build" }),
                    "real Chat.InputText strips one slash and dispatches prison/build with the native Chat context");
                Check(CommandBuffer(chat).Count == 0, "successful chat sentinel route needs no rendered chat message or network send");

                CommandInput(console, "prison build"); consoleInput.Invoke(console, null);
                Check(dispatches == 2 && ReferenceEquals(received.Context, console) && received.FullLine == "prison build"
                    && received.Args.SequenceEqual(new[] { "prison", "build" }),
                    "real Console InputText dispatches prison/build with the native Console context");
                Check(CommandBuffer(console).Count == 1 && CommandBuffer(console)[0] == "prison build",
                    "native Console echoes the typed command before dispatch");

                CommandInput(console, "/prison build"); consoleInput.Invoke(console, null);
                Check(dispatches == 2 && CommandBuffer(console).Last().Contains("'/prison' is not a recognized command"),
                    "base Console parser retains the slash and rejects /prison without an explicitly registered alias");

                string missing = "vmp_partyprison_missing_" + Guid.NewGuid().ToString("N");
                Check(!commands.ContainsKey(missing), "unknown-command fixture token is absent from the actual registry");
                int chatLines = CommandBuffer(chat).Count;
                CommandInput(chat, "/" + missing + " build"); chatInput.Invoke(chat, null);
                Check(dispatches == 2 && CommandBuffer(chat).Count == chatLines,
                    "native Chat silently rejects an unknown command token without reaching the prison callback");
                commands.Remove("prison");
                CommandInput(chat, "/prison build"); chatInput.Invoke(chat, null);
                Check(dispatches == 2 && CommandBuffer(chat).Count == chatLines,
                    "an absent prison registration reproduces silent chat failure without running any build");
                commands["prison"] = sentinel;

                CommandInput(chat, "/prison"); chatInput.Invoke(chat, null);
                Check(dispatches == 3 && ReferenceEquals(received.Context, chat) && received.Args.SequenceEqual(new[] { "prison" }),
                    "native Chat dispatches a registered command with a missing subcommand as one argument");
                CommandInput(chat, "/prison  build"); chatInput.Invoke(chat, null);
                Check(dispatches == 4 && received.Args.SequenceEqual(new[] { "prison", "", "build" }),
                    "native command tokenization preserves an empty argument between repeated spaces");
                CommandInput(chat, ""); chatInput.Invoke(chat, null);
                Check(dispatches == 4 && CommandBuffer(chat).Count == chatLines, "empty native chat input returns without dispatch or output");
                Check(Player.m_localPlayer == null && Game.instance == null, "native command route tests never create a live player or world");
            }
            finally
            {
                commands["prison"] = original;
                if (hadSlashAlias) commands["/prison"] = slashAlias; else commands.Remove("/prison");
                Terminal.m_cheat = oldCheat;
                UnityEngine.Object.DestroyImmediate(fixture);
                chatSingleton.SetValue(null, oldChat); consoleSingleton.SetValue(null, oldConsole);
            }
            Check(ReferenceEquals(commands["prison"], original) && Terminal.m_cheat == oldCheat
                && ReferenceEquals(chatSingleton.GetValue(null), oldChat) && ReferenceEquals(consoleSingleton.GetValue(null), oldConsole),
                "command route fixture restores the exact real callback, cheat flag and native Chat/Console singletons");
            Check(hadSlashAlias ? ReferenceEquals(commands["/prison"], slashAlias) : !commands.ContainsKey("/prison"),
                "command route fixture restores the original slash-alias registry state");
            report.AppendLine("PASS: Actual prison flags and callback are valid; native Chat routes /prison build and Console routes prison build through a safe sentinel. Unknown or missing chat registrations are silent; repeated spaces preserve empty arguments. Exact registry and singleton state was restored.");
            report.AppendLine("UNVERIFIED: Command routing in the user's running world, its current registry contents and the real Plugin.Command/build callback were not executed by this menu fixture.");
        }

        private static Terminal CommandTerminalFixture(Type type, GameObject parent)
        {
            var node = new GameObject(type.Name + " inactive route terminal", typeof(RectTransform));
            node.SetActive(false); node.transform.SetParent(parent.transform, false);
            var terminal = (Terminal)node.AddComponent(type);
            foreach (string name in new[] { "m_input", "m_output" })
            {
                FieldInfo field = typeof(Terminal).GetField(name, All);
                if (field == null) throw new InvalidOperationException("Native terminal fixture field missing: " + name);
                var child = new GameObject(name + " inactive route control", typeof(RectTransform));
                child.SetActive(false); child.transform.SetParent(node.transform, false);
                field.SetValue(terminal, child.AddComponent(field.FieldType));
            }
            typeof(Terminal).GetField("m_chatBuffer", All).SetValue(terminal, new List<string>());
            terminal.m_maxVisibleBufferLength = 20;
            return terminal;
        }

        private static void CommandInput(Terminal terminal, string text)
        {
            object input = typeof(Terminal).GetField("m_input", All).GetValue(terminal);
            FieldInfo value = AccessTools.Field(input.GetType(), "m_Text");
            if (value == null) throw new InvalidOperationException("Installed TMP input backing field is unavailable.");
            // Supply text without activating a virtual keyboard or a rendered
            // input control. InputText still calls the actual TMP text getter.
            value.SetValue(input, text);
        }

        private static List<string> CommandBuffer(Terminal terminal)
        { return (List<string>)typeof(Terminal).GetField("m_chatBuffer", All).GetValue(terminal); }

        private void CheckGroupRadiusCompatibility()
        {
            Type motion = AccessTools.TypeByName("ValheimModPack.InventoryAdmin.GroupRadiusMotion");
            MethodInfo target = motion == null ? null : motion.GetMethod("TryFrame", All);
            if (target == null)
            {
                report.AppendLine("UNVERIFIED: Optional InventoryAdmin group-radius motion is absent in this pack; its prison compatibility target was not exercised.");
                return;
            }
            Check(target.ReturnType == typeof(bool) && target.GetParameters().Length == 1 && target.GetParameters()[0].ParameterType.IsByRef,
                "optional installed group-radius endpoint has the expected TryFrame(out frame) shape");
            Patches patches = Harmony.GetPatchInfo(target);
            Check(patches != null && patches.Owners.Contains(Plugin.Id), "Party Prison compatibility is registered on actual InventoryAdmin group-radius endpoint");
            Type compatibility = typeof(Plugin).Assembly.GetType("ValheimModPack.PartyPrison.GroupRadiusCompatibilityPatch", true);
            MethodInfo prefix = compatibility.GetMethod("Prefix", All);
            FieldInfo active = typeof(Plugin).GetField("Active", All);
            FieldInfo localSentence = typeof(Plugin).GetField("localSentence", All);
            FieldInfo region = typeof(Plugin).GetField("region", All);
            PropertyInfo prisonActive = typeof(Plugin).GetProperty("PrisonActive", All);
            Check(prefix != null && active != null && localSentence != null && region != null && prisonActive != null,
                "optional compatibility exposes its actual prefix and sentence-state guard");
            object originalActive = active.GetValue(null);
            object plugin = Chainloader.PluginInfos[Plugin.Id].Instance;
            object originalSentence = localSentence.GetValue(plugin), originalRegion = region.GetValue(plugin);
            try
            {
                Check(Player.m_localPlayer == null && Game.instance == null, "group-radius compatibility fixture has no live player or world");
                active.SetValue(null, plugin);
                localSentence.SetValue(plugin, new SentenceState { RemainingSeconds = 60, PendingRelease = false });
                Check((bool)prisonActive.GetValue(plugin, null), "an active sentence enables the production prison guard without requiring a live player");
                object[] result = { true };
                Check(!(bool)prefix.Invoke(null, result) && !(bool)result[0], "active sentence suppresses optional group-radius motion and returns a false frame");
                localSentence.SetValue(plugin, new SentenceState { RemainingSeconds = 0, PendingRelease = true });
                Check((bool)prisonActive.GetValue(plugin, null), "pending release keeps the production prison guard active");
                result = new object[] { true };
                Check(!(bool)prefix.Invoke(null, result) && !(bool)result[0], "pending release still suppresses group-radius motion until sentence state clears");
                localSentence.SetValue(plugin, null);
                Check(!(bool)prisonActive.GetValue(plugin, null), "cleared sentence disables the production prison guard");
                result = new object[] { true };
                Check((bool)prefix.Invoke(null, result) && (bool)result[0], "after sentence clear the prefix preserves the native group-radius path and result");
                active.SetValue(null, null); result = new object[] { true };
                Check((bool)prefix.Invoke(null, result) && (bool)result[0], "missing prison instance leaves optional group-radius behavior untouched");
            }
            finally
            {
                localSentence.SetValue(plugin, originalSentence); region.SetValue(plugin, originalRegion); active.SetValue(null, originalActive);
            }
            Check(ReferenceEquals(active.GetValue(null), originalActive) && ReferenceEquals(localSentence.GetValue(plugin), originalSentence)
                && ReferenceEquals(region.GetValue(plugin), originalRegion), "group-radius compatibility fixture restores the exact original active instance, sentence and prison region");
            Check(Player.m_localPlayer == null && Game.instance == null, "optional compatibility test leaves no live player or world");
            report.AppendLine("PASS: Optional InventoryAdmin group-radius endpoint is patched; its production prefix suppresses motion for active sentences and pending release, restores the native path after clear, and leaves no fixture state.");
            report.AppendLine("UNVERIFIED: Simultaneous live group-radius enforcement and prison confinement require a host/client game test; the fixture invoked the compatibility prefix only.");
        }

        private static object Field(object value, string name) { return value.GetType().GetField(name, All).GetValue(value); }
        private static void Set(object value, string name, object field) { value.GetType().GetField(name, All).SetValue(value, field); }
        private static void Call(object value, string name, params object[] args)
        {
            try { value.GetType().GetMethod(name, All).Invoke(value, args); }
            catch (TargetInvocationException error) { throw new InvalidOperationException("Native UI call failed: " + name, error.InnerException ?? error); }
        }
        private static int InputCount() { return (int)typeof(GUIManager).GetField("InputBlockRequests", All).GetValue(null); }
        private static bool CountInputRequest(bool __0)
        {
            if (!simulateInputRequests) return true;
            simulatedInputRequests += __0 ? 1 : -1; return false;
        }

        private static bool PlacementCaptureFixture(ref PrisonPlacementPlan __result)
        {
            if (!simulatePlacement) return true;
            ++placementCaptures;
            if (failPlacement) throw new InvalidOperationException("Controlled placement snapshot failure");
            __result = PrisonPlacementPlan.Create(placementHost, placementLook, new PrisonPoint(0, 0, 1));
            capturedPlacement = __result; return false;
        }

        private static bool PlacementBuildFixture(PrisonPlacementPlan __0)
        {
            if (!simulatePlacement) return true;
            ++placementBuilds; constructedPlacement = __0;
            return false; // Observe the real confirmed route without touching any world or terrain.
        }

        private void CheckUi()
        {
            int baseline = InputCount(), imposed = 0, releases = 0, kits = 0, chosenTier = -1;
            bool canFight = true;
            string custody = "";
            string released = null, imposedAccount = null, imposedReason = null;
            double imposedMinutes = 0;
            var players = new List<PrisonPlayerRow>();
            for (int i = 0; i < 10; ++i) players.Add(new PrisonPlayerRow { AccountId = "probe-account-" + i,
                Name = "Игрок <b>" + i + "</b>", Online = i != 1, Sentenced = i == 1, RemainingSeconds = i == 1 ? 77 : 0 });
            SentenceState state = new SentenceState { RemainingSeconds = 77, Reason = "Причина <b>без разметки</b>" };
            object actualPlugin = Chainloader.PluginInfos[Plugin.Id].Instance;
            FieldInfo pendingPlacement = typeof(Plugin).GetField("pendingPlacement", All);
            Check(pendingPlacement != null, "production host retains an explicit placement snapshot for UI confirmation");
            object previousPlacement = pendingPlacement.GetValue(actualPlugin);
            var placementFixture = new Harmony("valheimmodpack.partyprison.nativeprobe.placementfixture");
            placementHost = new PrisonPoint(10, 50, 20); placementLook = new PrisonPoint(0, 0, 1);
            placementCaptures = placementBuilds = 0; capturedPlacement = constructedPlacement = null;
            simulatePlacement = true; failPlacement = false;
            placementFixture.Patch(typeof(Plugin).GetMethod("CapturePlacement", All), prefix:
                new HarmonyMethod(typeof(NativeChecks).GetMethod("PlacementCaptureFixture", All)));
            placementFixture.Patch(typeof(Plugin).GetMethod("BuildPrisonCore", All, null, new[] { typeof(PrisonPlacementPlan) }, null), prefix:
                new HarmonyMethod(typeof(NativeChecks).GetMethod("PlacementBuildFixture", All)));
            var bindings = new PrisonUiBindings {
                CanUse = () => true, IsHost = () => true, CanFight = () => canFight, Players = () => players, LocalSentence = () => state,
                Translate = (ru, en) => ru, Notice = () => "Статус сервера",
                PrepareBuild = () => Call(actualPlugin, "PrepareBuild"), Build = () => Call(actualPlugin, "BuildConfirmedPrison"),
                Kit = () => ++kits, CustodyStatus = () => custody,
                Impose = (account, minutes, reason) => { ++imposed; imposedAccount = account; imposedMinutes = minutes; imposedReason = reason; },
                Release = account => { ++releases; released = account; }, Wave = tier => chosenTier = tier, Move = arena => { }
            };
            PrisonWindow window = new PrisonWindow(bindings);
            try
            {
                // BuildVisuals is pure presentation. Show() is deliberately not
                // called because this fixture has no live Player or ZNet.
                Set(window, "hostPanel", true); Call(window, "BuildVisuals"); Call(window, "Repaint");
                Check(window.IsVisible && InputCount() == baseline, "synthetic host window builds without a player or input lease");
                GameObject panel = (GameObject)Field(window, "panel");
                RectTransform rect = panel.GetComponent<RectTransform>();
                Check(rect.rect.width >= 1079 && rect.rect.height >= 789, "host wooden panel has its intended readable dimensions");
                Button[] rows = (Button[])Field(window, "rows");
                Check(rows.Length == 8 && rows.All(row => row != null && row.interactable), "host displays exactly eight roster rows per page");
                Check(((Text)Field(window, "pagination")).text == "1 / 2", "online players and offline sentences are paginated");
                Check(rows[1].GetComponentInChildren<Text>().text.Contains("офлайн") && rows[1].GetComponentInChildren<Text>().text.Contains("01:17"), "offline active sentence remains visible with server duration");
                Check(((InputField)Field(window, "minutes")).text == "10", "default sentence is ten minutes");
                Check(((InputField)Field(window, "reason")).characterLimit > 0, "reason input is bounded");
                foreach (Text text in panel.GetComponentsInChildren<Text>(true)) Check(!text.supportRichText, "host caption is plain text: " + text.name);
                foreach (Selectable control in panel.GetComponentsInChildren<Selectable>(true))
                {
                    RectTransform controlRect = control.GetComponent<RectTransform>();
                    Check(controlRect != null && controlRect.anchoredPosition.y - controlRect.rect.height / 2 >= -rect.rect.height / 2,
                        "host control stays inside wooden panel: " + control.name);
                }
                Call(window, "Select", 0); Call(window, "Impose");
                Check(imposed == 1 && imposedAccount == "probe-account-0" && imposedMinutes == 10 && !String.IsNullOrEmpty(imposedReason), "host submits selected account, duration and default reason");
                Call(window, "Select", 1);
                Check(!((Button)Field(window, "impose")).interactable && ((Button)Field(window, "release")).interactable, "offline sentence enables release and disables new imprisonment");
                Call(window, "Release"); Check(releases == 1 && released == "probe-account-1", "offline release targets selected account");
                Check(((Button)Field(window, "build")).GetComponentInChildren<Text>().text.Contains("возле меня")
                    && ((Text)Field(window, "buildHint")).text.Contains("32 м") && !((Text)Field(window, "buildHint")).text.Contains("алтар"),
                    "host construction controls explain a fixed look-relative site rather than altar search");
                Call(window, "BuildPrison");
                Check(placementCaptures == 1 && placementBuilds == 0 && (bool)Field(window, "buildArmed")
                    && ((Text)Field(window, "buildHint")).text.Contains("Перед вами"), "first build click captures the placement and asks for confirmation before construction");
                Check(ReferenceEquals(pendingPlacement.GetValue(actualPlugin), capturedPlacement)
                    && capturedPlacement.Origin.X == 10 && capturedPlacement.Origin.Z == 52,
                    "the actual production PrepareBuild stores the first click's immutable host-look snapshot");
                PrisonPlacementPlan firstPlacement = capturedPlacement;
                placementHost = new PrisonPoint(777, 50, 888); placementLook = new PrisonPoint(-1, 0, 0);
                Call(window, "BuildPrison");
                Check(placementBuilds == 1 && placementCaptures == 1 && !(bool)Field(window, "buildArmed")
                    && ReferenceEquals(constructedPlacement, firstPlacement) && pendingPlacement.GetValue(actualPlugin) == null,
                    "the actual confirmed build consumes the original snapshot once despite later host or look changes");
                failPlacement = true; bool preparationFailed = false;
                try { Call(window, "BuildPrison"); } catch (InvalidOperationException) { preparationFailed = true; }
                failPlacement = false;
                Check(preparationFailed && !(bool)Field(window, "buildArmed") && placementBuilds == 1,
                    "a failed first-click capture cannot arm construction or reach the destructive build route");
                Set(window, "page", 1); Call(window, "Repaint");
                Check(rows[0].interactable && rows[1].interactable && !rows[2].interactable && rows[2].GetComponentInChildren<Text>().text == "", "last page clears stale rows");

                // Jotunn deliberately ignores BlockInput outside the main
                // world scene. Temporarily substitute its counter endpoint to
                // test the production Hide/reset logic without opening a world.
                var inputFixture = new Harmony("valheimmodpack.partyprison.nativeprobe.inputfixture");
                simulatedInputRequests = 3; simulateInputRequests = true;
                inputFixture.Patch(typeof(GUIManager).GetMethod("BlockInput", All), new HarmonyMethod(typeof(NativeChecks).GetMethod("CountInputRequest", All)));
                try
                {
                    // An independent lease belongs to another hypothetical modal.
                    GUIManager.BlockInput(true);
                    GUIManager.BlockInput(true); Set(window, "inputOwned", true);
                    window.Hide(); window.Hide();
                    Check(simulatedInputRequests == 4 && !window.IsVisible, "close releases only its own input lease and is idempotent");
                    Set(window, "hostPanel", false); Call(window, "BuildVisuals"); Call(window, "Repaint");
                    Check(((Text)Field(window, "sentence")).text.Contains("01:17") && ((Text)Field(window, "reasonText")).text.Contains(state.Reason), "prisoner panel paints server time and plain reason");
                    Check(((GameObject)Field(window, "panel")).GetComponent<RectTransform>().rect.height >= 779, "expanded prisoner panel fits custody and equipment controls");
                    Check(((Text)Field(window, "custodyText")).text.Contains("железных сундуках") && ((Text)Field(window, "custodyText")).text.Contains("сохраните добычу"), "prisoner panel explains protected chest custody and keeping arena loot on defeat");
                    Check(((Button)Field(window, "kit")).interactable && ((Button)Field(window, "arena")).interactable, "prepared prisoner may request equipment and enter the arena");
                    Call(window, "Kit"); Check(kits == 1, "basic equipment offer calls its server binding once");
                    Call(window, "Wave", 2); Check(chosenTier == 2, "difficulty selection submits the chosen future wave tier");
                    canFight = false; custody = "Вещи сохраняются, подождите."; Call(window, "Repaint");
                    Check(((Text)Field(window, "sentence")).text.Contains("Подготовка") && ((Text)Field(window, "custodyText")).text == custody,
                        "preparation displays its authoritative custody status rather than a running prison countdown");
                    Check(!((Button)Field(window, "kit")).interactable && !((Button)Field(window, "arena")).interactable
                        && ((Button[])Field(window, "tiers")).All(button => !button.interactable), "preparation disables equipment, arena transfer and difficulty controls");
                    Call(window, "Kit"); Check(kits == 1, "equipment request cannot run during custody preparation");
                    canFight = true; custody = ""; Call(window, "Repaint");
                    foreach (Text text in ((GameObject)Field(window, "panel")).GetComponentsInChildren<Text>(true)) Check(!text.supportRichText, "prisoner caption is plain text: " + text.name);
                    state.PendingRelease = true; state.RemainingSeconds = 0; Call(window, "Repaint");
                    Check(((Text)Field(window, "sentence")).text.Contains("завершён") && ((Text)Field(window, "custodyText")).text.Contains("на сундуке"), "pending release explains gate opening and physical chest retrieval without using a client clock");
                    Check(!((Button)Field(window, "kit")).interactable && !((Button)Field(window, "arena")).interactable,
                        "pending release keeps retrieval guidance visible while disabling new arena activity");
                    state = null; custody = "Нажмите E на сундуке для возврата вещей."; Call(window, "Repaint");
                    Check(((Text)Field(window, "sentence")).text.Contains("Хранение") && ((Text)Field(window, "custodyText")).text == custody,
                        "released belongings retain their physical chest retrieval instructions after the active sentence clears");
                    // Model the global reset already removing this panel's old
                    // request. Its remembered lease must not consume another.
                    Set(window, "inputOwned", true); window.HandleInputReset();
                    Check(simulatedInputRequests == 4 && !window.IsVisible, "global-reset handler forgets its old lease without decrementing another modal");
                    GUIManager.BlockInput(false); Check(simulatedInputRequests == 3, "independent modal retains its own release");
                }
                finally { simulateInputRequests = false; inputFixture.UnpatchSelf(); }
                Check(InputCount() == baseline, "synthetic UI restores the initial input counter");
            }
            finally {
                window.Hide(); simulatePlacement = failPlacement = false; placementFixture.UnpatchSelf();
                pendingPlacement.SetValue(actualPlugin, previousPlacement); capturedPlacement = constructedPlacement = null;
            }
            report.AppendLine("PASS: Native wooden host/prisoner panels, eight-row pagination, offline release, reason/duration, actual captured host-look construction confirmation, equipment offer, difficulty selection, custody preparation and chest retrieval, plain names and independent input leases.");
        }

        private void CheckPrefabs()
        {
            string[] pieces = { "stone_floor_2x2", "stone_wall_4x2", "stone_wall_2x1", "iron_wall_2x2", "iron_grate", "piece_dvergr_lantern", "piece_bench01", "piece_chest" };
            string[] mobs = { "Neck", "Greydwarf", "Greydwarf_Elite", "Greydwarf_Shaman", "Skeleton", "Draugr", "Draugr_Elite", "Goblin" };
            string[] items = { "SwordBronze", "MaceBronze", "AxeBronze", "SpearBronze", "BowFineWood", "ShieldWood", "ArrowWood", "ArmorLeatherChest", "ArmorLeatherLegs", "HelmetLeather" };
            int verified = 0; var unavailable = new List<string>();
            foreach (string name in pieces.Concat(mobs).Concat(items))
            {
                GameObject prefab = ZNetScene.instance == null ? null : ZNetScene.instance.GetPrefab(name);
                if (prefab == null && ObjectDB.instance != null && items.Contains(name)) prefab = ObjectDB.instance.GetItemPrefab(name);
                if (prefab == null) prefab = PrefabManager.Instance.GetPrefab(name);
                if (prefab == null) { unavailable.Add(name); continue; }
                if (pieces.Contains(name))
                {
                    Check(prefab.GetComponent<Piece>() != null && prefab.GetComponent<WearNTear>() != null && prefab.GetComponent<ZNetView>() != null, "native prison structure components: " + name);
                    Check(prefab.GetComponentsInChildren<Collider>(true).Any(collider => !collider.isTrigger), "native prison structure has solid collider: " + name);
                    if (name == "iron_grate") Check(prefab.GetComponent<Door>() != null, "native iron grate supports the release gate and walk-through arena door");
                    if (name == "piece_bench01") Check(prefab.GetComponentInChildren<Chair>(true) != null, "native cell bench supports sitting");
                    if (name == "piece_chest")
                    {
                        Container chest = prefab.GetComponent<Container>();
                        Check(chest != null && chest.m_width * chest.m_height >= 24, "native iron chest has a real container with at least 24 inventory slots");
                    }
                }
                else if (mobs.Contains(name))
                {
                    Check(prefab.GetComponent<Character>() != null && prefab.GetComponent<ZNetView>() != null, "native arena creature components: " + name);
                    Check(prefab.GetComponent<CharacterDrop>() != null, "native arena creature defines ordinary farm loot: " + name);
                }
                else Check(prefab.GetComponent<ItemDrop>() != null, "native armory item components: " + name);
                ++verified;
            }
            report.AppendLine("PASS: " + verified + " available prison structure, creature and armory prefab definitions inspected without spawning.");
            if (unavailable.Count > 0) report.AppendLine("UNVERIFIED: Menu did not expose these world prefabs: " + String.Join(", ", unavailable.ToArray()) + ". Their resolution requires a world test.");
        }

        private static ItemDrop.ItemData Arrows(bool loan, int count, int x)
        {
            var item = new ItemDrop.ItemData { m_shared = new ItemDrop.ItemData.SharedData { m_name = "Party Prison probe arrows",
                m_maxStackSize = 100, m_itemType = ItemDrop.ItemData.ItemType.Ammo }, m_quality = 1, m_worldLevel = 0,
                m_stack = count, m_gridPos = new Vector2i(x, 0) };
            if (loan) item.m_customData["VMP_PP_Loan"] = "probe";
            return item;
        }

        private void CheckGeometry()
        {
            var region = new PrisonRegion { Center = new PrisonPoint(0, 4, 0), CellSpawn = new PrisonPoint(-8, 1, 0),
                ArenaSpawn = new PrisonPoint(4, 1, 4), Radius = 18, HalfHeight = 8 };
            Check(ArenaBuilder.ContainsConfinement(region, new Vector3(-8, 1, 0)), "sentence boundary includes the cell");
            Check(!ArenaBuilder.ContainsConfinement(region, new Vector3(0, 1, -8)), "released property lobby lies outside confinement");
            Check(ArenaBuilder.IsInsideArena(region, new Vector3(4, 1, 4)) && !ArenaBuilder.IsInsideArena(region, new Vector3(-8, 1, 0)),
                "automatic waves distinguish the arena from the bench cell");
            Check(ArenaBuilder.IsInsideCell(region, new Vector3(-10, 1, 7)), "bench seat lies inside the protected cell rather than an arena target zone");
            Check(!ArenaBuilder.IsInsideCell(region, new Vector3(4, 1, 4)) && !ArenaBuilder.IsInsideCell(region, new Vector3(-8, 1, -8))
                && !ArenaBuilder.IsInsideCell(region, new Vector3(-4, 1, 0)), "cell safety excludes arena, retrieval lobby and the internal gate opening");
            MethodInfo solidBounds = typeof(ArenaBuilder).GetMethod("SolidBounds", All);
            Check(solidBounds != null, "construction uses its installed collider-bounds adapter");
            foreach (string name in new[] { "iron_grate", "iron_wall_2x2", "stone_wall_2x1", "piece_bench01", "piece_chest" })
            {
                GameObject prefab = PrefabManager.Instance.GetPrefab(name);
                if (prefab == null && ZNetScene.instance != null) prefab = ZNetScene.instance.GetPrefab(name);
                Check(prefab != null, "geometry definition is available: " + name);
                Bounds bounds = (Bounds)solidBounds.Invoke(null, new object[] { prefab });
                Check(bounds.size.x > .01f && bounds.size.y > .01f && bounds.size.z > .01f, "solid construction bounds are nondegenerate: " + name);
                report.AppendLine("GEOMETRY: " + name + " min=" + bounds.min.ToString("F3") + " max=" + bounds.max.ToString("F3") + " size=" + bounds.size.ToString("F3"));
            }
            report.AppendLine("PASS: Cell, arena and public retrieval lobby have distinct production boundaries; native collider dimensions are recorded for door/header placement.");
        }

        private static ItemDrop.ItemData NativeItem(string name, int count, int x, int y)
        {
            GameObject prefab = ObjectDB.instance == null ? null : ObjectDB.instance.GetItemPrefab(name);
            if (prefab == null) prefab = PrefabManager.Instance.GetPrefab(name);
            if (prefab == null || prefab.GetComponent<ItemDrop>() == null) throw new InvalidOperationException("Custody fixture item unavailable: " + name);
            ItemDrop.ItemData item = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
            item.m_dropPrefab = prefab; item.m_stack = count; item.m_gridPos = new Vector2i(x, y); item.m_equipped = false;
            return item;
        }

        private static byte[] SaveDetached(Inventory inventory)
        { var package = new ZPackage(); inventory.Save(package); return package.GetArray(); }

        private static object Backpack(ItemDrop.ItemData item)
        {
            Type extensions = AccessTools.TypeByName("Vapok.Common.Managers.ItemExtensions");
            Type component = AccessTools.TypeByName("AdventureBackpacks.Components.BackpackComponent");
            if (extensions == null || component == null) throw new InvalidOperationException("Actual backpack component API is absent.");
            object data = extensions.GetMethod("Data", new[] { typeof(ItemDrop.ItemData) }).Invoke(null, new object[] { item });
            return data.GetType().GetMethod("GetOrCreate").MakeGenericMethod(component).Invoke(data, new object[] { "" });
        }

        private void Reject(Action action, string reason)
        {
            ++checks;
            try { action(); }
            catch (ArgumentException) { return; }
            catch (InvalidOperationException) { return; }
            catch (InvalidDataException) { return; }
            catch (IOException) { return; }
            throw new InvalidOperationException("Party Prison native accepted: " + reason);
        }

        private void CheckCustodyInventory()
        {
            var source = new Inventory("Party Prison custody fixture", null, 8, 10);
            ItemDrop.ItemData wood = NativeItem("Wood", 40, 0, 0), armor = NativeItem("HelmetLeather", 1, 0, 6);
            armor.m_quality = 2; armor.m_durability = 17.25f; armor.m_crafterID = 987654321; armor.m_crafterName = "Кузнец 🛡"; armor.m_equipped = true;
            armor.m_customData["eaqs_slot"] = "head"; armor.m_customData["eaqs_player"] = "44";
            armor.m_customData["eaqs_parked"] = "0"; armor.m_customData["eaqs_weaponshield"] = "none";
            armor.m_customData["vmp_prison_enchantment_fixture"] = "retained custom enchantment \u2603";
            source.GetAllItems().Add(wood); source.GetAllItems().Add(armor);
            Check(ObjectDB.instance != null, "native custody fixture has the menu item database");
            GameObject bagPrefab = ObjectDB.instance.m_items.FirstOrDefault(prefab => prefab != null && prefab.name.StartsWith("Backpack", StringComparison.Ordinal) && prefab.GetComponent<ItemDrop>() != null);
            Check(bagPrefab != null, "actual Adventure Backpacks item is available for custody fixture");
            ItemDrop.ItemData bag = NativeItem(bagPrefab.name, 1, 1, 6);
            var nested = new Inventory("Party Prison custody nested bag", null, 4, 4); nested.GetAllItems().Add(NativeItem("Iron", 12, 0, 0));
            object component = Backpack(bag); component.GetType().GetMethod("SetInventory").Invoke(component, new object[] { nested });
            component.GetType().GetMethod("Serialize").Invoke(component, null);
            string bagKey = bag.m_customData.Keys.Single(key => key.Contains("AdventureBackpacks.Components.BackpackComponent"));
            string bagValue = bag.m_customData[bagKey]; source.GetAllItems().Add(bag);
            byte[] original = CustodyInventory.Capture(source);
            Check(CustodyInventory.Count(original) == 3 && CustodyInventory.Decode(original).GetAllItems().Any(item => item.m_gridPos.y == 6),
                "custody capture includes native hidden equipment rows and counts the backpack once");
            Check(CustodyInventory.Fingerprint(original).Length == 64, "original belongings have a stable SHA256 custody fingerprint");
            byte[][] chests = CustodyInventory.PrepareChestPayloads(original, 6, 4);
            var belongings = chests.SelectMany(payload => CustodyInventory.Decode(payload).GetAllItems()).ToList();
            Check(chests.Length == 4 && chests.Sum(payload => CustodyInventory.Count(payload)) == 3, "exactly four detached chest payloads retain every root stack without duplicating bag contents");
            ItemDrop.ItemData restoredArmor = belongings.Single(item => item.m_dropPrefab.name == "HelmetLeather");
            Check(!restoredArmor.m_equipped && restoredArmor.m_quality == armor.m_quality && restoredArmor.m_durability == armor.m_durability
                && restoredArmor.m_crafterID == armor.m_crafterID && restoredArmor.m_crafterName == armor.m_crafterName,
                "custody chest armor is unequipped and retains quality, wear and crafter");
            Check(restoredArmor.m_customData["vmp_prison_enchantment_fixture"] == armor.m_customData["vmp_prison_enchantment_fixture"]
                && !new[] { "eaqs_slot", "eaqs_player", "eaqs_parked", "eaqs_weaponshield" }.Any(key => restoredArmor.m_customData.ContainsKey(key)),
                "custody chest gear preserves custom metadata and removes only temporary equipment slot bookkeeping");
            Check(source.GetAllItems().Count == 3 && armor.m_equipped && armor.m_customData["eaqs_slot"] == "head"
                && armor.m_customData["eaqs_player"] == "44" && bag.m_customData[bagKey] == bagValue,
                "preparing detached chest payloads leaves original character items and backpack metadata intact");
            ItemDrop.ItemData restoredBag = belongings.Single(item => item.m_dropPrefab.name == bagPrefab.name);
            Check(restoredBag.m_customData[bagKey] == bagValue, "custody preserves the actual serialized backpack inventory bytes");
            object restoredComponent = Backpack(restoredBag);
            Inventory restoredContents = (Inventory)restoredComponent.GetType().GetMethod("GetInventory").Invoke(restoredComponent, null);
            Check(restoredContents.GetAllItems().Count == 1 && restoredContents.GetAllItems()[0].m_dropPrefab.name == "Iron" && restoredContents.GetAllItems()[0].m_stack == 12,
                "released native backpack component restores its twelve iron rather than creating separate chest stacks");
            var tooMany = new Inventory("Party Prison custody capacity fixture", null, 8, 4);
            for (int i = 0; i < 5; ++i) tooMany.GetAllItems().Add(NativeItem("Wood", 1, i, 0));
            byte[] full = CustodyInventory.Capture(tooMany);
            Reject(() => CustodyInventory.PrepareChestPayloads(full, 1, 1), "five root stacks into four one-slot chests");
            Check(tooMany.GetAllItems().Count == 5 && CustodyInventory.Count(full) == 5, "insufficient chest capacity leaves all original belongings untouched");
            ItemDrop.ItemData loan = NativeItem("SwordBronze", 1, 2, 6); loan.m_customData[ArenaBuilder.LoanKey] = "1"; source.GetAllItems().Add(loan);
            Reject(() => CustodyInventory.Capture(source), "prison loan mixed into personal custody");
            Check(source.GetAllItems().Count == 4 && source.GetAllItems().Contains(loan), "rejected loan capture clears no items");
            source.GetAllItems().Remove(loan);
            var empty = new Inventory("Party Prison emptied protected character fixture", null, 8, 10);
            Check(CustodyInventory.Count(CustodyInventory.Capture(empty)) == 0, "empty post-confiscation inventory has a valid native durable payload");
            var trailing = new byte[original.Length + 1]; Buffer.BlockCopy(original, 0, trailing, 0, original.Length);
            Reject(() => CustodyInventory.Decode(trailing), "trailing native custody bytes");
            var truncated = new byte[original.Length - 1]; Buffer.BlockCopy(original, 0, truncated, 0, truncated.Length);
            Reject(() => CustodyInventory.Decode(truncated), "truncated native custody bytes");
            var corruptBag = new Inventory("Party Prison corrupted bag fixture", null, 8, 4);
            ItemDrop.ItemData malformed = NativeItem("Wood", 1, 0, 0);
            malformed.m_customData["prison_fixture#AdventureBackpacks.Components.BackpackComponent"] = "not valid base64";
            corruptBag.GetAllItems().Add(malformed); byte[] invalidBag = SaveDetached(corruptBag);
            Reject(() => CustodyInventory.Decode(invalidBag), "invalid embedded backpack bytes");
            report.AppendLine("PASS: Detached native custody capture/chest allocation retains hidden equipment rows, item quality/wear/crafter/custom metadata and a real backpack with twelve iron; overflow, loans, trailing/truncated payloads and malformed embedded bags fail without clearing personal items.");
        }

        private void CheckCustodyMask()
        {
            Type guard = typeof(Plugin).Assembly.GetType("ValheimModPack.PartyPrison.PrisonGuard", true);
            FieldInfo field = guard.GetField("Inaccessible", All);
            Check(field != null, "protected native chest access exposes its actual inaccessible inventory mask");
            Inventory mask = field.GetValue(null) as Inventory;
            Check(mask != null && mask.GetAllItems().Count == 0, "native chest access mask begins empty");
            ItemDrop.ItemData personal = NativeItem("Wood", 5, 0, 0);
            List<ItemDrop.ItemData> exposed = mask.GetAllItems(); exposed.Add(personal);
            Check(mask.GetAllItems().Count == 0 && !ReferenceEquals(exposed, mask.GetAllItems()),
                "a mod mutating the exposed mask list does not reach native custody storage");
            var queried = new List<ItemDrop.ItemData>(); mask.GetAllItems(personal.m_shared.m_name, queried);
            Check(queried.Count == 0, "name-filtered native access does not expose masked custody items");
            mask.GetAllItems(personal.m_shared.m_itemType, queried);
            Check(queried.Count == 0, "type-filtered native access does not expose masked custody items");
            Check(mask.GetItemAt(0, 0) == null, "native cell lookup cannot read an item out of masked custody storage");
            Check(!mask.AddItem(personal) && personal.m_stack == 5 && mask.GetAllItems().Count == 0,
                "normal native addition cannot turn a protected chest mask into a writable destination");
            MethodInfo slot = typeof(Inventory).GetMethod("AddItem", All, null, new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int), typeof(bool) }, null);
            Check(!(bool)slot.Invoke(mask, new object[] { personal, 5, 0, 0, false }) && personal.m_stack == 5 && mask.GetAllItems().Count == 0,
                "manual slot addition also leaves masked chest access and source belongings unchanged");
            var source = new Inventory("Party Prison protected mask quick-stack source", null, 8, 4);
            source.GetAllItems().Add(personal);
            Check(mask.StackAll(source, false) == 0 && source.GetAllItems().Contains(personal) && personal.m_stack == 5 && mask.GetAllItems().Count == 0,
                "quick stack cannot deposit personal items into a protected chest access mask");
            mask.MoveAll(source);
            Check(source.GetAllItems().Contains(personal) && personal.m_stack == 5 && mask.GetAllItems().Count == 0,
                "move-all cannot consume source items through a masked custody chest");
            Check(!mask.MoveItemToThis(source, personal, 5, 0, 0) && source.GetAllItems().Contains(personal)
                && personal.m_stack == 5 && mask.GetAllItems().Count == 0, "manual inventory transfer cannot put items into masked custody storage");
            FieldInfo loading = guard.GetField("LoadingCustody", All);
            MethodInfo access = guard.GetMethod("InventoryAccess", All);
            Check(loading != null && access != null, "native chest projection exposes its actual scoped access guard");
            object before = loading.GetValue(null);
            try
            {
                loading.SetValue(null, (int)before + 1);
                Check(!(bool)access.Invoke(null, new object[] { mask }) && !mask.AddItem(personal) && mask.GetAllItems().Count == 0 && personal.m_stack == 5,
                    "native container projection scope never turns the inaccessible mask into a writable inventory");
            }
            finally { loading.SetValue(null, before); }
            Check((int)loading.GetValue(null) == (int)before, "mask fixture restores the exact native chest projection scope");
            report.AppendLine("PASS: The actual inaccessible custody chest mask stays empty across native add, slot-add, quick-stack, move-all and manual transfer; original personal items are preserved.");
        }

        private static bool RegistryFixtureAwake(Component __instance)
        { return __instance == null || !registryFixtureObjects.Contains(__instance.gameObject); }
        private static bool RegistryFixtureRevision(ZDO __instance)
        {
            if (!registryFixtureZdos.Contains(__instance)) return true;
            FieldInfo revision = typeof(ZDO).GetField("<DataRevision>k__BackingField", All);
            revision.SetValue(__instance, unchecked((uint)revision.GetValue(__instance) + 1));
            return false; // Detached ZDO metadata never reaches a world/dirty-sector table.
        }
        private sealed class RegistryFixtureContainer
        {
            internal GameObject Object;
            internal ZNetView View;
            internal Container Container;
            internal Inventory Inventory;
            internal ZDO Zdo;
        }
        private RegistryFixtureContainer RegistryContainer(uint identity, ZNetView rootOverride)
        {
            var result = new RegistryFixtureContainer();
            result.Object = new GameObject("PartyPrison.RegistryFixture." + identity); result.Object.SetActive(false);
            registryFixtureObjects.Add(result.Object);
            result.View = result.Object.AddComponent<ZNetView>();
            result.Container = result.Object.AddComponent<Container>();
            result.Zdo = new ZDO(); result.Zdo.m_uid = new ZDOID(-492103719, identity); registryFixtureZdos.Add(result.Zdo);
            typeof(ZDO).GetField("m_prefab", All).SetValue(result.Zdo, ArenaBuilder.CustodyPrefab.GetStableHashCode());
            typeof(ZNetView).GetField("m_zdo", All).SetValue(result.View, result.Zdo);
            result.Inventory = new Inventory("Party Prison mapped custody fixture", null, 6, 4);
            typeof(Container).GetField("m_inventory", All).SetValue(result.Container, result.Inventory);
            typeof(Container).GetField("m_nview", All).SetValue(result.Container, rootOverride == null ? result.View : rootOverride);
            typeof(Container).GetField("m_rootObjectOverride", All).SetValue(result.Container, rootOverride);
            typeof(Container).GetField("m_width", All).SetValue(result.Container, 6);
            typeof(Container).GetField("m_height", All).SetValue(result.Container, 4);
            Check(result.View.IsValid() && result.Zdo.IsValid(), "registry fixture has detached valid native ZDO metadata without entering a world");
            return result;
        }
        private static List<ItemDrop.ItemData> RawInventory(Inventory inventory)
        { return (List<ItemDrop.ItemData>)typeof(Inventory).GetField("m_inventory", All).GetValue(inventory); }

        private void CheckForcedTerrainHeights()
        {
            Type codec = typeof(Plugin).Assembly.GetType("ValheimModPack.PartyPrison.TerrainOverrides", true);
            MethodInfo encode = codec.GetMethod("Encode", All), decode = codec.GetMethod("Decode", All);
            string key = (string)codec.GetField("Key", All).GetRawConstantValue();
            Check(encode != null && decode != null && key == "VMP_PP_ForcedGround", "native terrain uses the versioned saved override codec");
            MethodInfo apply = typeof(TerrainComp).GetMethod("ApplyToHeightmap", All, null,
                new[] { typeof(Texture2D), typeof(List<float>), typeof(float[]), typeof(float[]), typeof(Heightmap) }, null);
            var fixture = new Harmony("valheimmodpack.partyprison.nativeprobe.terrainfixture");
            GameObject detached = null; ZDO zdo = null, receivedZdo = null;
            try {
                var awake = new HarmonyMethod(typeof(NativeChecks).GetMethod("RegistryFixtureAwake", All)); awake.priority = Priority.First;
                foreach (Type component in new[] { typeof(ZNetView), typeof(Heightmap), typeof(TerrainComp) })
                    fixture.Patch(component.GetMethod("Awake", All), prefix: awake);
                fixture.Patch(typeof(ZDO).GetMethod("IncreaseDataRevision", All),
                    prefix: new HarmonyMethod(typeof(NativeChecks).GetMethod("RegistryFixtureRevision", All)));
                detached = new GameObject("PartyPrison.DetachedTerrainFixture"); detached.SetActive(false);
                registryFixtureObjects.Add(detached); detached.transform.position = new Vector3(500, 100, -500);
                ZNetView view = detached.AddComponent<ZNetView>();
                Heightmap map = detached.AddComponent<Heightmap>(); TerrainComp compiler = detached.AddComponent<TerrainComp>();
                zdo = new ZDO(); zdo.m_uid = new ZDOID(-492103719, 20); registryFixtureZdos.Add(zdo);
                typeof(ZDO).GetField("m_prefab", All).SetValue(zdo, "PartyPrison.DetachedTerrainFixture".GetStableHashCode());
                typeof(ZNetView).GetField("m_zdo", All).SetValue(view, zdo);
                Check(view.IsValid() && zdo.IsValid(), "terrain fixture has valid detached native ZDO metadata without registering a world object");
                Set(map, "m_width", 2); Set(map, "m_scale", 1f);
                Set(compiler, "m_initialized", true); Set(compiler, "m_width", 2); Set(compiler, "m_pitch", 3);
                Set(compiler, "m_nview", view); Set(compiler, "m_hmap", map);
                float[] levels = new float[9]; levels[4] = 100f;
                Set(compiler, "m_levelDelta", levels); Set(compiler, "m_smoothDelta", new float[9]);
                Set(compiler, "m_modifiedHeight", new bool[9]); Set(compiler, "m_modifiedPaint", new bool[9]); Set(compiler, "m_paintMask", new Color[9]);
                float[] baseHeights = Enumerable.Repeat(40f, 9).ToArray();
                List<float> native = Enumerable.Repeat(40f, 9).ToList();
                apply.Invoke(compiler, new object[] { null, native, baseHeights, baseHeights, map });
                Check(native[4] == 48f && native.Where((height, index) => index != 4).All(height => height == 40f),
                    "actual native ApplyToHeightmap retains its eight-metre clamp on an ordinary untagged compiler");

                var forced = new Dictionary<int, float> { { 4, 90f }, { 8, 52f } };
                byte[] record = (byte[])encode.Invoke(null, new object[] { 3, forced });
                var restored = (IDictionary<int, float>)decode.Invoke(null, new object[] { (byte[])record.Clone(), 3 });
                Check(restored.Count == 2 && restored[4] == 90f && restored[8] == 52f,
                    "serialized terrain override clone retains only the selected native vertex indices and absolute local heights");
                zdo.Set(key, record);
                List<float> tagged = Enumerable.Repeat(40f, 9).ToList();
                apply.Invoke(compiler, new object[] { null, tagged, baseHeights, baseHeights, map });
                Check(tagged[4] == 90f && tagged[8] == 52f && tagged.Where((height, index) => index != 4 && index != 8).All(height => height == 40f),
                    "actual registered terrain postfix overrides the native clamp for tagged vertices while preserving every other vertex");
                Check(tagged[4] + map.transform.position.y == 190f, "saved overrides use local height rather than applying the heightmap's world offset twice");

                MethodInfo serialize = typeof(ZDO).GetMethod("Serialize", All, null, new[] { typeof(ZPackage) }, null);
                MethodInfo deserialize = typeof(ZDO).GetMethod("Deserialize", All, null, new[] { typeof(ZPackage) }, null);
                Check(serialize != null && deserialize != null, "native ZDO network serialization methods exist for detached reload verification");
                ZPackage wire = new ZPackage(); serialize.Invoke(zdo, new object[] { wire });
                receivedZdo = new ZDO(); receivedZdo.m_uid = new ZDOID(-492103719, 21); registryFixtureZdos.Add(receivedZdo);
                deserialize.Invoke(receivedZdo, new object[] { new ZPackage(wire.GetArray()) });
                Check(receivedZdo.GetByteArray(key, null) != null && receivedZdo.GetByteArray(key, null).SequenceEqual(record),
                    "the actual native ZDO serialization/deserialization retains the complete saved terrain byte record");
                typeof(ZNetView).GetField("m_zdo", All).SetValue(view, receivedZdo);
                List<float> peerHeights = Enumerable.Repeat(40f, 9).ToList();
                apply.Invoke(compiler, new object[] { null, peerHeights, baseHeights, baseHeights, map });
                Check(peerHeights.SequenceEqual(tagged), "a separately deserialized ZDO reconstructs the same forced terrain as the original host record");
                typeof(ZNetView).GetField("m_zdo", All).SetValue(view, zdo);
                List<float> originalAgain = Enumerable.Repeat(40f, 9).ToList();
                apply.Invoke(compiler, new object[] { null, originalAgain, baseHeights, baseHeights, map });
                Check(originalAgain.SequenceEqual(tagged), "switching the detached view back to the original record preserves the same forced terrain");

                object cacheTable = typeof(TerrainLeveler).GetField("OverrideCaches", All).GetValue(null);
                MethodInfo lookup = cacheTable.GetType().GetMethod("TryGetValue"); object[] lookupArgs = { compiler, null };
                Check((bool)lookup.Invoke(cacheTable, lookupArgs), "native terrain postfix stores its decoded record in a weak compiler cache");
                object cache = lookupArgs[1], previousValues = Field(cache, "Values");
                for (int i = 0; i < 3; ++i) {
                    List<float> again = Enumerable.Repeat(40f, 9).ToList();
                    apply.Invoke(compiler, new object[] { null, again, baseHeights, baseHeights, map });
                    Check(again[4] == 90f && ReferenceEquals(previousValues, Field(cache, "Values")),
                        "unchanged native terrain reconstructions reuse the decoded height record");
                }
                forced[4] = 130f; forced.Remove(8);
                byte[] changed = (byte[])encode.Invoke(null, new object[] { 3, forced }); zdo.Set(key, changed);
                List<float> updated = Enumerable.Repeat(40f, 9).ToList();
                apply.Invoke(compiler, new object[] { null, updated, baseHeights, baseHeights, map });
                Check(updated[4] == 130f && updated[8] == 40f && !ReferenceEquals(previousValues, Field(cache, "Values")),
                    "a new saved record refreshes the cache and stops overriding a removed vertex");
                zdo.Set(key, (byte[])record.Clone()); List<float> reloaded = Enumerable.Repeat(40f, 9).ToList();
                apply.Invoke(compiler, new object[] { null, reloaded, baseHeights, baseHeights, map });
                Check(reloaded[4] == 90f && reloaded[8] == 52f, "restoring a previous serialized record immediately restores the same native forced heights");
                Set(map, "m_width", 3); List<float> incompatible = Enumerable.Repeat(40f, 9).ToList();
                apply.Invoke(compiler, new object[] { null, incompatible, baseHeights, baseHeights, map });
                Check(incompatible[4] == 48f && incompatible[8] == 40f,
                    "an incompatible reconstructed heightmap size preserves native heights instead of writing tagged indices into a different layout");
                Set(map, "m_width", 2);
                zdo.Set(key, new byte[0]); List<float> cleared = Enumerable.Repeat(40f, 9).ToList();
                apply.Invoke(compiler, new object[] { null, cleared, baseHeights, baseHeights, map });
                Check(cleared[4] == 48f && cleared[8] == 40f, "removing the saved override returns the compiler to normal native terrain behavior");
            }
            finally {
                if (detached != null) { UnityEngine.Object.DestroyImmediate(detached); registryFixtureObjects.Remove(detached); }
                if (zdo != null) { typeof(ZDO).GetMethod("Reset", All).Invoke(zdo, null); registryFixtureZdos.Remove(zdo); }
                if (receivedZdo != null) { typeof(ZDO).GetMethod("Reset", All).Invoke(receivedZdo, null); registryFixtureZdos.Remove(receivedZdo); }
                fixture.UnpatchSelf();
            }
            Check(Player.m_localPlayer == null && Game.instance == null, "detached terrain reconstruction and serialization never register a user player or world");
            report.AppendLine("PASS: Actual native ApplyToHeightmap plus the registered postfix preserve tagged absolute heights beyond the native clamp, leave untagged terrain unchanged, reuse cached records and restore/remove serialized records without a user world.");
        }
        private static GameObject ClearanceChild(GameObject parent, string name, bool active)
        {
            var child = new GameObject(name); child.SetActive(active);
            child.transform.SetParent(parent.transform, false); return child;
        }
        private static object ClearanceEntry(Type entryType, GameObject root, ZNetView view, string action, Collider collider)
        {
            Type actionType = typeof(SiteClearer).GetNestedType("ForceAction", All);
            return Activator.CreateInstance(entryType, All, null,
                new object[] { root, view, Enum.Parse(actionType, action), new Bounds(root.transform.position, Vector3.one), collider }, null);
        }
        private static object ClearanceEntries(Type entryType, params object[] entries)
        {
            var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(entryType));
            foreach (object entry in entries) list.Add(entry); return list;
        }
        private static byte[] ClearanceMask(IEnumerable<string> paths)
        {
            string[] values = paths.ToArray(); var package = new ZPackage(); package.Write(1); package.Write(values.Length);
            foreach (string value in values) package.Write(value); return package.GetArray();
        }
        private static string[] ClearanceMaskPaths(byte[] bytes)
        {
            var package = new ZPackage(bytes);
            if (package.ReadInt() != 1) throw new InvalidDataException("Unexpected prepared static-mask version");
            int count = package.ReadInt(); var paths = new List<string>();
            for (int i = 0; i < count; ++i) paths.Add(package.ReadString());
            if (package.GetPos() != package.Size()) throw new InvalidDataException("Unexpected prepared static-mask trailing bytes");
            return paths.ToArray();
        }
        private static bool ClearancePreparationRejected(MethodInfo prepare, object entries)
        {
            try { prepare.Invoke(null, new[] { entries }); return false; }
            catch (TargetInvocationException error) { if (error.InnerException is InvalidDataException) return true; throw; }
        }

        private void CheckForceClearance()
        {
            Type clearer = typeof(SiteClearer), entryType = clearer.GetNestedType("ForceEntry", All);
            MethodInfo pathFor = clearer.GetMethod("StaticPath", All), resolve = clearer.GetMethod("ResolvePath", All),
                prepare = clearer.GetMethod("PrepareMasks", All), replay = clearer.GetMethod("ApplyStaticMasks", All),
                preserve = clearer.GetMethod("PreserveRoot", All);
            string maskKey = (string)clearer.GetField("StaticMaskKey", All).GetRawConstantValue();
            Check(entryType != null && pathFor != null && resolve != null && prepare != null && replay != null && preserve != null,
                "production force-clearance staging, paths, cumulative masks and preservation helpers are available");
            var fixture = new Harmony("valheimmodpack.partyprison.nativeprobe.clearancefixture");
            var objects = new List<GameObject>(); RegistryFixtureContainer proxy = null, container = null;
            try {
                var awake = new HarmonyMethod(typeof(NativeChecks).GetMethod("RegistryFixtureAwake", All)); awake.priority = Priority.First;
                foreach (Type component in new[] { typeof(ZNetView), typeof(Container) }) fixture.Patch(component.GetMethod("Awake", All), prefix: awake);
                fixture.Patch(typeof(ZDO).GetMethod("IncreaseDataRevision", All),
                    prefix: new HarmonyMethod(typeof(NativeChecks).GetMethod("RegistryFixtureRevision", All)));

                var solid = new GameObject("PartyPrison.DetachedStaticColliders"); objects.Add(solid);
                solid.transform.position = new Vector3(1200, 100, 1200);
                BoxCollider first = solid.AddComponent<BoxCollider>(), second = solid.AddComponent<BoxCollider>(), disabled = solid.AddComponent<BoxCollider>(), trigger = solid.AddComponent<BoxCollider>();
                disabled.enabled = false; trigger.isTrigger = true;
                GameObject inactiveChild = ClearanceChild(solid, "inactive child", false);
                BoxCollider inactiveSolid = inactiveChild.AddComponent<BoxCollider>();
                object colliders = ClearanceEntry(entryType, solid, null, "StaticCollider", first);
                Call(colliders, "Check", false); Call(colliders, "Stage"); Call(colliders, "Check", true);
                report.AppendLine("STATIC COLLIDER STAGE: root=" + solid.activeSelf + " first=" + first.enabled + " second=" + second.enabled
                    + " disabled=" + disabled.enabled + " trigger=" + trigger.enabled + " childActive=" + inactiveChild.activeSelf + " childSolid=" + inactiveSolid.enabled);
                Check(solid.activeSelf && !first.enabled && !second.enabled && !disabled.enabled && trigger.enabled
                    && !inactiveChild.activeSelf && inactiveSolid.enabled,
                    "native static-collider staging disables every local solid, keeps trigger state and leaves inactive descendants untouched");
                Call(colliders, "Restore"); Call(colliders, "Check", false);
                Check(solid.activeSelf && first.enabled && second.enabled && !disabled.enabled && trigger.enabled
                    && !inactiveChild.activeSelf && inactiveSolid.enabled && !(bool)Field(colliders, "Staged"),
                    "rollback restores exact mixed enabled states without enabling an originally disabled collider or child");
                Call(colliders, "Stage"); Call(colliders, "Restore");
                Check(first.enabled && second.enabled && !disabled.enabled && trigger.enabled,
                    "repeated native static staging and rollback do not drift the original collider state");

                var part = new GameObject("PartyPrison.DetachedStaticPart"); objects.Add(part);
                GameObject activePartChild = ClearanceChild(part, "active child", true), inactivePartChild = ClearanceChild(part, "inactive child", false);
                object partEntry = ClearanceEntry(entryType, part, null, "StaticPart", null);
                Call(partEntry, "Stage"); Call(partEntry, "Check", true);
                Check(!part.activeSelf && activePartChild.activeSelf && !inactivePartChild.activeSelf,
                    "static-subtree staging changes only the selected root's active state");
                Call(partEntry, "Restore"); Call(partEntry, "Check", false);
                Check(part.activeSelf && activePartChild.activeSelf && !inactivePartChild.activeSelf,
                    "static-subtree rollback retains each descendant's original activeSelf state");
                part.SetActive(false); object inactiveEntry = ClearanceEntry(entryType, part, null, "StaticPart", null);
                Call(inactiveEntry, "Stage"); Call(inactiveEntry, "Restore");
                Check(!part.activeSelf, "rollback never activates a static root that was already inactive");

                proxy = RegistryContainer(40, null); proxy.Object.SetActive(true);
                BoxCollider proxySolid = proxy.Object.AddComponent<BoxCollider>(), proxyTrigger = proxy.Object.AddComponent<BoxCollider>(); proxyTrigger.isTrigger = true;
                GameObject duplicateA = ClearanceChild(proxy.Object, "same name", true), duplicateB = ClearanceChild(proxy.Object, "same name", true);
                duplicateA.transform.localPosition = new Vector3(1, 0, 0); duplicateB.transform.localPosition = new Vector3(2, 0, 0);
                // U+FFFF produces '/' in ordinary base64; nested names also contain path punctuation.
                GameObject nested = ClearanceChild(duplicateB, "\uffff\uffff/section:+ Снег", true);
                string pathA = (string)pathFor.Invoke(null, new object[] { proxy.Object.transform, duplicateA.transform });
                string pathB = (string)pathFor.Invoke(null, new object[] { proxy.Object.transform, nested.transform });
                Check(pathA != pathB && ReferenceEquals(resolve.Invoke(null, new object[] { proxy.Object.transform, pathA }), duplicateA.transform)
                    && ReferenceEquals(resolve.Invoke(null, new object[] { proxy.Object.transform, pathB }), nested.transform),
                    "actual static paths distinguish same-name siblings and round-trip nested Unicode/punctuation names");
                Check(pathB.Split('/').Length == 2 && !pathB.Contains("+"),
                    "URL-safe name encoding cannot inject hierarchy separators into a static path");
                nested.name = "changed child";
                Check(resolve.Invoke(null, new object[] { proxy.Object.transform, pathB }) == null,
                    "a stale stored name cannot target a renamed child at the same sibling index");
                nested.name = "\uffff\uffff/section:+ Снег";
                duplicateA.transform.SetSiblingIndex(1);
                Check(ReferenceEquals(resolve.Invoke(null, new object[] { proxy.Object.transform, pathB }), nested.transform),
                    "a changed sibling index resolves the unique original name and local position instead of a neighboring same-name object");
                duplicateA.transform.SetSiblingIndex(0);
                nested.transform.localPosition = new Vector3(.1f, 0, 0);
                Check(resolve.Invoke(null, new object[] { proxy.Object.transform, pathB }) == null,
                    "a moved static child cannot be selected solely because its old name and sibling index still match");
                nested.transform.localPosition = Vector3.zero;
                string legacyPath = String.Join("/", pathB.Split('/').Select(segment => String.Join(":", segment.Split(':').Take(2).ToArray())).ToArray());
                Check(ReferenceEquals(resolve.Invoke(null, new object[] { proxy.Object.transform, legacyPath }), nested.transform),
                    "the production path resolver keeps existing two-field stored hierarchy paths readable");
                GameObject ambiguous = ClearanceChild(proxy.Object, "same name", true), padding = ClearanceChild(proxy.Object, "different name", true);
                ambiguous.transform.localPosition = duplicateB.transform.localPosition; padding.transform.SetSiblingIndex(0);
                Check(resolve.Invoke(null, new object[] { proxy.Object.transform, pathB }) == null,
                    "ambiguous fallback matches never guess which same-name and same-position static subtree should be hidden");
                UnityEngine.Object.DestroyImmediate(padding); UnityEngine.Object.DestroyImmediate(ambiguous);
                replay.Invoke(null, new object[] { proxy.Object, new[] { "P:", "X:" + pathA, "P:" + pathB } });
                Check(proxy.Object.activeSelf && duplicateA.activeSelf && !nested.activeSelf,
                    "production mask replay preserves the proxy root and unknown-prefix sibling while hiding only its selected nested subtree");
                replay.Invoke(null, new object[] { proxy.Object, new[] { "C:" } });
                Check(proxy.Object.activeSelf && !proxySolid.enabled && proxyTrigger.enabled,
                    "a root collider mask keeps the proxy active and leaves its native trigger enabled");
                nested.SetActive(true); proxySolid.enabled = true;

                object entryA = ClearanceEntry(entryType, duplicateA, proxy.View, "StaticPart", null),
                    entryB = ClearanceEntry(entryType, nested, proxy.View, "StaticCollider", null);
                byte[] originalMask = ClearanceMask(new[] { "P:legacy" }); proxy.Zdo.Set(maskKey, originalMask);
                uint beforeRevision = proxy.Zdo.DataRevision;
                var merged = (IDictionary<ZDO, byte[]>)prepare.Invoke(null, new[] { ClearanceEntries(entryType, entryA, entryB, entryA) });
                string[] mergedPaths = ClearanceMaskPaths(merged[proxy.Zdo]);
                Check(merged.Count == 1 && mergedPaths.Length == 3 && mergedPaths.Contains("P:legacy")
                    && mergedPaths.Contains("P:" + pathA) && mergedPaths.Contains("C:" + pathB),
                    "multiple entries on one native proxy prepare one cumulative record, retaining old paths and deduplicating repeated entries");
                Check(ReferenceEquals(proxy.Zdo.GetByteArray(maskKey, null), originalMask) && proxy.Zdo.DataRevision == beforeRevision,
                    "preparing cumulative masks performs no native ZDO write or revision change");

                proxy.Zdo.Set(maskKey, ClearanceMask(Enumerable.Range(0, 8190).Select(i => "P:old-" + i)));
                merged = (IDictionary<ZDO, byte[]>)prepare.Invoke(null, new[] { ClearanceEntries(entryType, entryA, entryB, entryA) });
                Check(ClearanceMaskPaths(merged[proxy.Zdo]).Length == 8192,
                    "cumulative proxy preparation accepts exactly 8192 distinct paths and a duplicate entry consumes no capacity");
                byte[] countEdge = ClearanceMask(Enumerable.Range(0, 8191).Select(i => "P:old-" + i)); proxy.Zdo.Set(maskKey, countEdge); beforeRevision = proxy.Zdo.DataRevision;
                Check(ClearancePreparationRejected(prepare, ClearanceEntries(entryType, entryA, entryB))
                    && ReferenceEquals(proxy.Zdo.GetByteArray(maskKey, null), countEdge) && proxy.Zdo.DataRevision == beforeRevision,
                    "combined additions crossing the 8192-path limit fail before changing the shared proxy record");

                GameObject longChild = ClearanceChild(proxy.Object, new string('a', 6135), false); longChild.transform.localPosition = new Vector3(100, 0, 0);
                object longEntry = ClearanceEntry(entryType, longChild, proxy.View, "StaticPart", null);
                Check(("P:" + (string)Field(longEntry, "Path")).Length == 8192, "native hierarchy produces the exact accepted static-path length boundary");
                proxy.Zdo.Set(maskKey, new byte[0]); merged = (IDictionary<ZDO, byte[]>)prepare.Invoke(null, new[] { ClearanceEntries(entryType, longEntry) });
                Check(ClearanceMaskPaths(merged[proxy.Zdo]).Single().Length == 8192, "prepared masks retain an exactly 8192-character native hierarchy path");
                longChild.name = new string('a', 6136); object tooLongEntry = ClearanceEntry(entryType, longChild, proxy.View, "StaticPart", null);
                Check(ClearancePreparationRejected(prepare, ClearanceEntries(entryType, tooLongEntry)),
                    "an overlong encoded hierarchy path is rejected before commit instead of truncating or selecting a different subtree");
                longChild.name = new string('a', 6135);

                var byteEdgePaths = Enumerable.Range(0, 126).Select(i => "P:" + i.ToString("D3") + new string('b', 8187)).ToList();
                byteEdgePaths.Add("P:" + new string('c', 7926));
                byte[] byteEdge = ClearanceMask(byteEdgePaths); proxy.Zdo.Set(maskKey, byteEdge);
                merged = (IDictionary<ZDO, byte[]>)prepare.Invoke(null, new[] { ClearanceEntries(entryType, longEntry) });
                Check(merged[proxy.Zdo].Length == 1024 * 1024, "native ZPackage preparation accepts the exact one-MiB cumulative byte boundary");
                beforeRevision = proxy.Zdo.DataRevision;
                Check(ClearancePreparationRejected(prepare, ClearanceEntries(entryType, longEntry, entryA))
                    && ReferenceEquals(proxy.Zdo.GetByteArray(maskKey, null), byteEdge) && proxy.Zdo.DataRevision == beforeRevision,
                    "another shared-proxy addition exceeding one MiB fails before any native record or revision mutation");

                container = RegistryContainer(41, null);
                ItemDrop.ItemData savedItem = NativeItem("Iron", 9, 2, 3); savedItem.m_customData["vmp_clearance_preserved"] = "exact personal metadata";
                RawInventory(container.Inventory).Add(savedItem); byte[] personalBytes = SaveDetached(container.Inventory); container.Zdo.Set(ZDOVars.s_items, personalBytes);
                Check((bool)preserve.Invoke(null, new object[] { container.Object, container.Zdo }),
                    "a native container root is preserved intact instead of selected for deletion");
                var savedOnly = new GameObject("PartyPrison.DetachedSavedInventory"); objects.Add(savedOnly); savedOnly.SetActive(false);
                Check((bool)preserve.Invoke(null, new object[] { savedOnly, container.Zdo }),
                    "saved native inventory bytes protect a root even when no Container component is present");
                Check(ReferenceEquals(container.Zdo.GetByteArray(ZDOVars.s_items, null), personalBytes)
                    && personalBytes.SequenceEqual(SaveDetached(container.Inventory)) && RawInventory(container.Inventory).Count == 1
                    && ReferenceEquals(RawInventory(container.Inventory)[0], savedItem) && savedItem.m_stack == 9
                    && savedItem.m_customData["vmp_clearance_preserved"] == "exact personal metadata",
                    "classification leaves exact native saved bytes, item identity, stack and custom metadata untouched");
                container.Zdo.Set(ZDOVars.s_items, new byte[0]); container.Zdo.Set(ZDOVars.s_items, "legacy saved inventory");
                Check((bool)preserve.Invoke(null, new object[] { savedOnly, container.Zdo }), "legacy native string inventory records receive the same preservation decision");
                container.Zdo.Set(ZDOVars.s_items, String.Empty);
                Check(!(bool)preserve.Invoke(null, new object[] { savedOnly, container.Zdo }), "an ordinary empty root remains eligible for removal");
            }
            finally {
                foreach (GameObject value in objects) if (value != null) UnityEngine.Object.DestroyImmediate(value);
                foreach (RegistryFixtureContainer value in new[] { proxy, container }) if (value != null) {
                    if (value.Object != null) UnityEngine.Object.DestroyImmediate(value.Object);
                    registryFixtureObjects.Remove(value.Object);
                    typeof(ZDO).GetMethod("Reset", All).Invoke(value.Zdo, null); registryFixtureZdos.Remove(value.Zdo);
                }
                fixture.UnpatchSelf();
            }
            Check(Player.m_localPlayer == null && Game.instance == null,
                "detached clearance staging, native mask preparation and item preservation never register a player or user world");
            report.AppendLine("PASS: Detached native collider/subtree staging rolls back exact original states; production mask replay preserves proxy roots; Unicode/sibling paths and cumulative count/path/byte boundaries are checked before mutation; native inventories and saved item bytes retain exact contents.");
        }

        private void CheckCustodyRegistry()
        {
            MethodInfo register = typeof(CustodyInventory).GetMethod("RegisterContainer", All);
            FieldInfo registryField = typeof(CustodyInventory).GetField("InventoryContainers", All);
            Type guard = typeof(Plugin).Assembly.GetType("ValheimModPack.PartyPrison.PrisonGuard", true);
            FieldInfo loading = guard.GetField("LoadingCustody", All), maskField = guard.GetField("Inaccessible", All);
            Check(register != null && registryField != null && loading != null && maskField != null,
                "production inventory identity registry and scoped load guard are available");
            object registry = registryField.GetValue(null); MethodInfo registryLookup = registry.GetType().GetMethod("TryGetValue");
            Check(registry.GetType().GetGenericTypeDefinition() == typeof(System.Runtime.CompilerServices.ConditionalWeakTable<,>),
                "registry uses weak inventory identity keys rather than retaining unloaded container cycles");
            Inventory mask = (Inventory)maskField.GetValue(null); int previousLoading = (int)loading.GetValue(null);
            var fixture = new Harmony("valheimmodpack.partyprison.nativeprobe.registryfixture");
            RegistryFixtureContainer chest = null, ordinaryContainer = null, overrideContainer = null;
            var detached = new Inventory("Party Prison ordinary player-shaped fixture", null, 8, 4);
            ItemDrop.ItemData ordinaryItem = NativeItem("Wood", 5, 0, 0); detached.GetAllItems().Add(ordinaryItem);
            try
            {
                var awake = new HarmonyMethod(typeof(NativeChecks).GetMethod("RegistryFixtureAwake", All)); awake.priority = Priority.First;
                fixture.Patch(typeof(Container).GetMethod("Awake", All), prefix: awake);
                fixture.Patch(typeof(ZNetView).GetMethod("Awake", All), prefix: awake);
                fixture.Patch(typeof(ZDO).GetMethod("IncreaseDataRevision", All),
                    prefix: new HarmonyMethod(typeof(NativeChecks).GetMethod("RegistryFixtureRevision", All)));
                chest = RegistryContainer(1, null); ordinaryContainer = RegistryContainer(2, null);
                ItemDrop.ItemData stored = NativeItem("Wood", 9, 0, 0); RawInventory(chest.Inventory).Add(stored);
                register.Invoke(null, new object[] { chest.Container }); register.Invoke(null, new object[] { ordinaryContainer.Container });
                Check(CustodyInventory.ChestForInventory(chest.Inventory) == null && chest.Inventory.GetAllItems().Contains(stored),
                    "registering an ordinary container leaves its actual native inventory available");
                chest.Zdo.Set(ArenaBuilder.CustodyKey, true);
                Check(ReferenceEquals(CustodyInventory.ChestForInventory(chest.Inventory), chest.Zdo),
                    "a custody marker added after registration immediately protects the same inventory identity");
                Check(ReferenceEquals(chest.Container.GetInventory(), mask), "actual mapped custody Container.GetInventory returns the inaccessible mask");
                Check(chest.Inventory.GetAllItems().Count == 0 && chest.Inventory.GetItemAt(0, 0) == null
                    && chest.Inventory.CountItems(stored.m_shared.m_name, -1, true) == 0,
                    "direct native custody inventory reads and counts do not expose its stored belongings");
                var filtered = new List<ItemDrop.ItemData>(); chest.Inventory.GetAllItems(stored.m_shared.m_name, filtered);
                chest.Inventory.GetAllItems(stored.m_shared.m_itemType, filtered);
                Check(filtered.Count == 0, "name and type queries cannot bypass protection through the registered actual inventory");
                Check(!chest.Inventory.RemoveItem(stored) && !chest.Inventory.RemoveItem(stored, 2) && !chest.Inventory.RemoveItem(0),
                    "native exact, partial and indexed removals reject registered custody storage");
                chest.Inventory.RemoveAll();
                var incoming = NativeItem("Iron", 3, 1, 0);
                Check(!chest.Inventory.AddItem(incoming) && incoming.m_stack == 3 && RawInventory(chest.Inventory).Count == 1
                    && ReferenceEquals(RawInventory(chest.Inventory)[0], stored) && stored.m_stack == 9,
                    "outside-load additions and remove-all preserve exact custody and source item objects");
                MethodInfo slot = typeof(Inventory).GetMethod("AddItem", All, null,
                    new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int), typeof(bool) }, null);
                Check(!(bool)slot.Invoke(chest.Inventory, new object[] { incoming, 3, 1, 0, false }) && incoming.m_stack == 3,
                    "outside-load slot addition cannot write to a registered custody inventory");
                chest.Inventory.MoveAll(detached); detached.MoveAll(chest.Inventory);
                Check(chest.Inventory.StackAll(detached, false) == 0 && detached.StackAll(chest.Inventory, false) == 0
                    && detached.GetAllItems().Contains(ordinaryItem) && ordinaryItem.m_stack == 5 && RawInventory(chest.Inventory).Count == 1,
                    "move-all and quick-stack in either direction leave actual custody and ordinary source items untouched");
                Check(CustodyInventory.ChestForInventory(detached) == null && detached.GetAllItems().Count == 1
                    && ReferenceEquals(detached.GetItemAt(0, 0), ordinaryItem),
                    "an unregistered player-shaped native inventory keeps normal reads and cell access");
                Check(ReferenceEquals(ordinaryContainer.Container.GetInventory(), ordinaryContainer.Inventory)
                    && CustodyInventory.ChestForInventory(ordinaryContainer.Inventory) == null,
                    "registered ordinary container inventory is available through its actual getter");
                var payloadInventory = new Inventory("Party Prison native load payload", null, 6, 4);
                ItemDrop.ItemData payloadItem = NativeItem("Iron", 7, 2, 3);
                payloadItem.m_customData["vmp_registry_native_load"] = "contents-and-custom-data";
                payloadInventory.GetAllItems().Add(payloadItem); byte[] payload = SaveDetached(payloadInventory);
                chest.Zdo.Set(ZDOVars.s_items, payload);
                MethodInfo nativeLoad = typeof(Container).GetMethod("Load", All, null, Type.EmptyTypes, null);
                Check((bool)nativeLoad.Invoke(chest.Container, null), "real native Container.Load executes against the registered custody inventory");
                List<ItemDrop.ItemData> loaded = RawInventory(chest.Inventory);
                Check(loaded.Count == 1 && loaded[0].m_dropPrefab.name == "Iron" && loaded[0].m_stack == 7
                    && loaded[0].m_gridPos == new Vector2i(2, 3) && loaded[0].m_customData["vmp_registry_native_load"] == "contents-and-custom-data",
                    "native custody loading retains stack, slot and custom data despite public inventory read protection");
                Check((int)loading.GetValue(null) == previousLoading && chest.Inventory.GetAllItems().Count == 0,
                    "successful native Load restores the projection scope and immediately masks subsequent ordinary reads");
                Check(!(bool)nativeLoad.Invoke(chest.Container, null) && RawInventory(chest.Inventory).Count == 1
                    && (int)loading.GetValue(null) == previousLoading,
                    "native no-change Load early return preserves both stored contents and exact load scope");
                chest.Zdo.Set(ZDOVars.s_items, new byte[] { 1, 2 }); bool loadFailed = false;
                try { nativeLoad.Invoke(chest.Container, null); }
                catch (TargetInvocationException) { loadFailed = true; }
                Check(loadFailed && (int)loading.GetValue(null) == previousLoading
                    && !(bool)slot.Invoke(chest.Inventory, new object[] { incoming, 3, 1, 0, false }) && incoming.m_stack == 3,
                    "throwing native Load restores the guard and cannot leave later slot writes enabled");
                chest.Zdo.Set(ZDOVars.s_items, payload); nativeLoad.Invoke(chest.Container, null);
                Check(RawInventory(chest.Inventory).Count == 1 && RawInventory(chest.Inventory)[0].m_stack == 7,
                    "a later valid native payload loads completely after a failed projection");
                chest.Zdo.Set(ArenaBuilder.CustodyKey, false);
                Check(CustodyInventory.ChestForInventory(chest.Inventory) == null && chest.Inventory.GetAllItems().Count == 1,
                    "a removed custody marker is read dynamically rather than kept as a permanent cache decision");
                chest.Zdo.Set(ArenaBuilder.CustodyKey, true);
                Check(ReferenceEquals(CustodyInventory.ChestForInventory(chest.Inventory), chest.Zdo),
                    "reapplying custody protection needs no global rescan or identity rebuild");
                Inventory previousInventory = chest.Inventory;
                var replacement = new Inventory("Party Prison mapped custody fixture", null, 6, 4);
                typeof(Container).GetField("m_inventory", All).SetValue(chest.Container, replacement); chest.Inventory = replacement;
                Check(CustodyInventory.ChestForInventory(previousInventory) == null,
                    "replacing a container inventory invalidates the previous identity mapping");
                object[] oldLookup = { previousInventory, null };
                Check(!(bool)registryLookup.Invoke(registry, oldLookup), "a stale replaced inventory entry is removed from the weak identity registry");
                register.Invoke(null, new object[] { chest.Container }); register.Invoke(null, new object[] { chest.Container });
                Check(ReferenceEquals(CustodyInventory.ChestForInventory(replacement), chest.Zdo)
                    && CustodyInventory.ChestForInventory(new Inventory(replacement.GetName(), null, 6, 4)) == null,
                    "duplicate registration is idempotent and identical inventory names/dimensions do not confer custody identity");
                overrideContainer = RegistryContainer(3, chest.View);
                register.Invoke(null, new object[] { overrideContainer.Container });
                Check(ReferenceEquals(CustodyInventory.ChestForInventory(overrideContainer.Inventory), chest.Zdo)
                    && ReferenceEquals(overrideContainer.Container.GetInventory(), mask),
                    "native resolved root override controls custody lookup and masking instead of the component's unrelated local ZDO");
                chest.Zdo.Set(ZDOVars.s_items, payload); nativeLoad.Invoke(overrideContainer.Container, null);
                Check(RawInventory(overrideContainer.Inventory).Count == 1 && RawInventory(overrideContainer.Inventory)[0].m_stack == 7
                    && (int)loading.GetValue(null) == previousLoading,
                    "root-override native Load enters the same scoped custody projection and retains contents");
                typeof(ZNetView).GetField("m_zdo", All).SetValue(chest.View, ordinaryContainer.Zdo);
                Check(CustodyInventory.ChestForInventory(replacement) == null && CustodyInventory.ChestForInventory(overrideContainer.Inventory) == null,
                    "changed live ZDO identity cannot retain the previous world's custody marker through a cached ZDO reference");
                typeof(ZNetView).GetField("m_zdo", All).SetValue(chest.View, chest.Zdo);
                Check(ReferenceEquals(CustodyInventory.ChestForInventory(replacement), chest.Zdo), "restored live view is classified from its current native ZDO");
                var timer = System.Diagnostics.Stopwatch.StartNew(); int ordinaryCount = 0;
                for (int i = 0; i < 5000; ++i)
                { ordinaryCount += detached.GetAllItems().Count; if (ReferenceEquals(detached.GetItemAt(0, 0), ordinaryItem)) ++ordinaryCount; }
                timer.Stop();
                Check(ordinaryCount == 10000 && ordinaryItem.m_stack == 5,
                    "ten thousand hot ordinary inventory reads preserve normal behavior with custody containers registered");
                report.AppendLine("PASS: 10000 ordinary inventory/cell reads completed in " + timer.ElapsedMilliseconds
                    + " ms; the separate production IL contract forbids global object scans on this lookup path.");
                Inventory stale = chest.Inventory;
                UnityEngine.Object.DestroyImmediate(chest.Object);
                Check(CustodyInventory.ChestForInventory(stale) == null, "a destroyed mapped container cannot remain a live custody source");
                object[] destroyedLookup = { stale, null };
                Check(!(bool)registryLookup.Invoke(registry, destroyedLookup), "lookup removes a destroyed container's stale weak-registry entry");
            }
            finally
            {
                // Destroy only fixture objects; release only their detached ZDO
                // records. No native player, world or network is registered.
                foreach (var value in registryFixtureObjects.ToArray()) if (value != null) UnityEngine.Object.DestroyImmediate(value);
                foreach (ZDO zdo in registryFixtureZdos) typeof(ZDO).GetMethod("Reset", All).Invoke(zdo, null);
                registryFixtureObjects.Clear(); registryFixtureZdos.Clear();
                loading.SetValue(null, previousLoading); fixture.UnpatchSelf();
            }
            Check((int)loading.GetValue(null) == previousLoading && Player.m_localPlayer == null && Game.instance == null,
                "registry fixture restores exact load scope and leaves no real world/player registration");
            report.AppendLine("PASS: Actual mapped custody inventories stay hidden and immutable outside native Load; late marker updates, native payload/custom data, early/throwing loads, root overrides, changed inventory/ZDO identities and destroyed-container cleanup are exercised without a user world.");
        }

        private static void ExactInsertionFixture(Inventory __instance)
        {
            if (!ReferenceEquals(__instance, exactCallbackInventory)) return;
            ++exactCallbackCalls;
            if (exactCallbackMode == 1) throw new InvalidOperationException("Controlled native inventory callback failure after insertion");
            if (exactCallbackMode != 2) return;
            ItemDrop.ItemData added = __instance.GetAllItems().FirstOrDefault(item => item.m_customData.ContainsKey("vmp_exact_withdrawal_fixture"));
            if (added != null) { ++added.m_quality; exactCallbackMode = 0; }
        }

        private void CheckExactWithdrawalInsertion()
        {
            var payload = new Inventory("Party Prison exact grant payload", null, 8, 4);
            ItemDrop.ItemData armor = NativeItem("HelmetLeather", 1, 0, 0);
            armor.m_quality = 2; armor.m_durability = 17.25f; armor.m_crafterID = 123456789; armor.m_crafterName = "Кузнец";
            armor.m_customData["vmp_exact_withdrawal_fixture"] = "armor custom metadata \u2603";
            payload.GetAllItems().Add(armor); byte[] grant = CustodyInventory.Capture(payload);
            var empty = new Inventory("Party Prison exact empty recipient", null, 4, 2);
            Check(CustodyInventory.AddExact(empty, 2, grant) && empty.GetAllItems().Count == 1,
                "withdrawal grant inserts exactly one native item in an ordinary empty inventory cell");
            ItemDrop.ItemData received = empty.GetAllItems()[0];
            Check(received.m_quality == armor.m_quality && received.m_durability == armor.m_durability && received.m_crafterID == armor.m_crafterID
                && received.m_crafterName == armor.m_crafterName && received.m_customData["vmp_exact_withdrawal_fixture"] == armor.m_customData["vmp_exact_withdrawal_fixture"],
                "exact withdrawal insertion retains native item quality, wear, crafter and custom metadata");
            var orePayload = new Inventory("Party Prison exact ore payload", null, 8, 4); orePayload.GetAllItems().Add(NativeItem("Iron", 12, 0, 0));
            byte[] oreGrant = CustodyInventory.Capture(orePayload);
            ItemDrop.ItemData personalOre = NativeItem("Iron", 4, 0, 0);
            var oreDestination = new Inventory("Party Prison exact ore recipient", null, 2, 1); oreDestination.GetAllItems().Add(personalOre);
            Check(CustodyInventory.AddExact(oreDestination, 1, oreGrant) && oreDestination.GetAllItems().Count == 2
                && personalOre.m_stack == 4 && oreDestination.GetAllItems().Any(item => !ReferenceEquals(item, personalOre) && item.m_stack == 12),
                "exact withdrawal does not merge its twelve iron into an existing personal stack");
            var full = new Inventory("Party Prison full exact recipient", null, 1, 1); ItemDrop.ItemData occupied = NativeItem("Wood", 7, 0, 0); full.GetAllItems().Add(occupied);
            Check(!CustodyInventory.AddExact(full, 1, grant) && full.GetAllItems().Count == 1 && ReferenceEquals(full.GetAllItems()[0], occupied) && occupied.m_stack == 7,
                "a full recipient refuses withdrawal without changing personal belongings");
            var hidden = new Inventory("Party Prison excluded equipment rows", null, 2, 3);
            Check(!CustodyInventory.AddExact(hidden, 0, grant) && hidden.GetAllItems().Count == 0, "withdrawal cannot use excluded equipment rows as ordinary bag capacity");
            var fixture = new Harmony("valheimmodpack.partyprison.nativeprobe.exactinsertionfixture");
            MethodInfo insert = typeof(Inventory).GetMethod("AddItem", All, null, new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int), typeof(bool) }, null);
            Check(insert != null, "exact native item insertion endpoint exists for withdrawal post-state verification");
            try
            {
                fixture.Patch(insert, postfix: new HarmonyMethod(typeof(NativeChecks).GetMethod("ExactInsertionFixture", All)));
                exactCallbackInventory = new Inventory("Party Prison throwing exact callback recipient", null, 4, 1); exactCallbackMode = 1; exactCallbackCalls = 0;
                Check(CustodyInventory.AddExact(exactCallbackInventory, 1, grant) && exactCallbackInventory.GetAllItems().Count == 1,
                    "an insertion postfix throwing after native mutation still reports the one verified copy as applied");
                Check(exactCallbackCalls > 0, "throwing native insertion postfix fixture actually ran after exact insertion");
                Check(exactCallbackInventory.GetAllItems()[0].m_customData["vmp_exact_withdrawal_fixture"] == armor.m_customData["vmp_exact_withdrawal_fixture"],
                    "callback failure preserves the exact delivered metadata and does not license a duplicate retry");
                exactCallbackMode = 0;
                exactCallbackInventory = new Inventory("Party Prison corrupting exact callback recipient", null, 4, 1);
                ItemDrop.ItemData retained = NativeItem("Wood", 9, 0, 0); retained.m_customData["vmp_personal_fixture"] = "original";
                exactCallbackInventory.GetAllItems().Add(retained); exactCallbackMode = 2; exactCallbackCalls = 0;
                CustodyInsertionException failure = null;
                try { CustodyInventory.AddExact(exactCallbackInventory, 1, grant); }
                catch (CustodyInsertionException error) { failure = error; }
                report.AppendLine("EXACT INSERTION CALLBACK: calls=" + exactCallbackCalls + " mode=" + exactCallbackMode
                    + " failure=" + (failure == null ? "none" : failure.AppliedAmbiguously + ": " + failure.Message)
                    + " items=" + String.Join(", ", exactCallbackInventory.GetAllItems().Select(item => item.m_dropPrefab.name + ":stack=" + item.m_stack + ":quality=" + item.m_quality).ToArray()));
                Check(failure != null && !failure.AppliedAmbiguously, "a callback changing the new grant item triggers an explicit verified rollback");
                Check(exactCallbackInventory.GetAllItems().Count == 1 && ReferenceEquals(exactCallbackInventory.GetAllItems()[0], retained)
                    && retained.m_stack == 9 && retained.m_customData["vmp_personal_fixture"] == "original",
                    "withdrawal rollback removes only the changed new item and preserves the original personal item object and metadata");
            }
            finally { exactCallbackInventory = null; exactCallbackMode = 0; fixture.UnpatchSelf(); }
            Check(payload.GetAllItems().Count == 1 && ReferenceEquals(payload.GetAllItems()[0], armor) && armor.m_quality == 2
                && armor.m_customData["vmp_exact_withdrawal_fixture"] == "armor custom metadata \u2603", "all exact insertion fixtures leave the original grant item untouched");
            report.AppendLine("PASS: Exact native custody withdrawal insertion preserves item data, uses an empty ordinary cell without merging personal stacks, refuses full capacity, recognizes insertion before a controlled native AddItem postfix failure, and rolls back only a corrupted new item.");
        }

        private static bool RestrictedPickupFixture(ref bool __result) { __result = true; return false; }
        private void CheckLootPickupGuard()
        {
            // Model the actor's eligibility only. The production pickup prefix,
            // real native loot prefab and production custody/position guards run
            // unchanged; no Player or world object is constructed by this fixture.
            Type guard = typeof(Plugin).Assembly.GetType("ValheimModPack.PartyPrison.PrisonGuard", true);
            Type pickup = typeof(Plugin).Assembly.GetType("ValheimModPack.PartyPrison.PickupPatch", true);
            MethodInfo restricted = guard.GetMethod("Restricted", All), prefix = pickup.GetMethod("Prefix", All);
            Check(restricted != null && prefix != null, "production prisoner pickup eligibility and prefix are available");
            object plugin = Chainloader.PluginInfos[Plugin.Id].Instance;
            FieldInfo active = typeof(Plugin).GetField("Active", All), sentence = typeof(Plugin).GetField("localSentence", All),
                region = typeof(Plugin).GetField("region", All), stage = typeof(Plugin).GetField("localCustodyStage", All),
                layout = typeof(Plugin).GetField("localLayoutVersion", All), recovery = typeof(Plugin).GetField("localRecovery", All);
            Check(active != null && sentence != null && region != null && stage != null && layout != null && recovery != null,
                "production pickup fixture can preserve exact original custody state");
            object oldActive = active.GetValue(null), oldSentence = sentence.GetValue(plugin), oldRegion = region.GetValue(plugin),
                oldStage = stage.GetValue(plugin), oldLayout = layout.GetValue(plugin), oldRecovery = recovery.GetValue(plugin);
            GameObject loot = NativeItem("Wood", 1, 0, 0).m_dropPrefab;
            Vector3 oldPosition = loot.transform.position;
            var fixture = new Harmony("valheimmodpack.partyprison.nativeprobe.pickupfixture");
            try
            {
                fixture.Patch(restricted, new HarmonyMethod(typeof(NativeChecks).GetMethod("RestrictedPickupFixture", All)));
                active.SetValue(null, plugin); sentence.SetValue(plugin, new SentenceState { RemainingSeconds = 60 });
                region.SetValue(plugin, new PrisonRegion { Center = new PrisonPoint(0, 4, 0), CellSpawn = new PrisonPoint(-8, 1, 0),
                    ArenaSpawn = new PrisonPoint(4, 1, 4), Radius = 18, HalfHeight = 8 });
                stage.SetValue(plugin, 3); layout.SetValue(plugin, 2); recovery.SetValue(plugin, "");
                loot.transform.position = new Vector3(4, 1, 4);
                object[] arguments = { null, loot, true };
                Check((bool)prefix.Invoke(null, arguments) && (bool)arguments[2], "a prepared prisoner may pick up an ordinary native loot prefab inside the arena");
                loot.transform.position = new Vector3(0, 1, -8); arguments = new object[] { null, loot, true };
                Check(!(bool)prefix.Invoke(null, arguments) && !(bool)arguments[2], "prisoner pickup does not reach personal custody items across the public lobby");
                loot.transform.position = new Vector3(4, 1, 4); stage.SetValue(plugin, 1); arguments = new object[] { null, loot, true };
                Check(!(bool)prefix.Invoke(null, arguments) && !(bool)arguments[2], "custody preparation prevents collecting new items before originals are safely deposited");
            }
            finally
            {
                fixture.UnpatchSelf(); loot.transform.position = oldPosition;
                sentence.SetValue(plugin, oldSentence); region.SetValue(plugin, oldRegion); stage.SetValue(plugin, oldStage);
                layout.SetValue(plugin, oldLayout); recovery.SetValue(plugin, oldRecovery); active.SetValue(null, oldActive);
            }
            Check(loot.transform.position == oldPosition && ReferenceEquals(active.GetValue(null), oldActive)
                && ReferenceEquals(sentence.GetValue(plugin), oldSentence) && ReferenceEquals(region.GetValue(plugin), oldRegion)
                && (int)stage.GetValue(plugin) == (int)oldStage && (int)layout.GetValue(plugin) == (int)oldLayout
                && (string)recovery.GetValue(plugin) == (string)oldRecovery, "pickup fixture restores the native prefab position and every previous prison field");
            report.AppendLine("PASS: Production pickup prefix allows a real native loot prefab inside the arena after custody preparation, rejects lobby pickups and preparation-time inventory changes, and restores all fixture state. Actor eligibility was substituted; live player pickup remains unverified.");
        }

        private void CheckInventoryIsolation()
        {
            Type guard = typeof(Plugin).Assembly.GetType("ValheimModPack.PartyPrison.PrisonGuard", true);
            FieldInfo context = guard.GetField("AddingItem", All);
            Check(context != null, "native stack isolation exposes a scoped adding-item context");
            object originalContext = context.GetValue(null);
            MethodInfo find = typeof(Inventory).GetMethod("FindFreeStackItem", All, null, new[] { typeof(string), typeof(int), typeof(float) }, null);
            MethodInfo slot = typeof(Inventory).GetMethod("AddItem", All, null, new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int), typeof(bool) }, null);
            var inventory = new Inventory("Party Prison detached stack fixture", null, 8, 4);
            var personal = Arrows(false, 12, 0); var loan = Arrows(true, 20, 1);
            inventory.GetAllItems().Add(personal); inventory.GetAllItems().Add(loan);
            try
            {
                var incomingLoan = Arrows(true, 3, 2); var incomingPersonal = Arrows(false, 3, 2);
                context.SetValue(null, incomingLoan);
                Check(ReferenceEquals(find.Invoke(inventory, new object[] { loan.m_shared.m_name, 1, 0f }), loan), "loan stack lookup skips an earlier personal stack with the same arrow name");
                context.SetValue(null, incomingPersonal);
                Check(ReferenceEquals(find.Invoke(inventory, new object[] { personal.m_shared.m_name, 1, 0f }), personal), "personal stack lookup skips prison loan ownership");
                var loanOnly = new Inventory("Party Prison loan-only fixture", null, 8, 4);
                loanOnly.GetAllItems().Add(loan);
                Check(find.Invoke(loanOnly, new object[] { personal.m_shared.m_name, 1, 0f }) == null, "personal lookup does not merge into the only loan stack");
                var personalOnly = new Inventory("Party Prison personal-only fixture", null, 8, 4);
                personalOnly.GetAllItems().Add(personal); context.SetValue(null, incomingLoan);
                Check(find.Invoke(personalOnly, new object[] { loan.m_shared.m_name, 1, 0f }) == null, "loan lookup does not merge into the only personal stack");
                context.SetValue(null, originalContext);
                Check(inventory.AddItem(incomingPersonal), "ordinary personal arrows still enter a detached inventory");
                Check(personal.m_stack == 15 && loan.m_stack == 20 && personal.m_customData.Count == 0,
                    "personal addition changes only personal quantity and preserves the loan quantity and personal ownership");
                Check(ReferenceEquals(context.GetValue(null), originalContext), "adding-item finalizer restores previous context after native addition");
                var mixedPersonal = Arrows(false, 4, 3);
                Check(!(bool)slot.Invoke(inventory, new object[] { mixedPersonal, 4, 1, 0, false }), "manual slot transfer rejects personal arrows into a loan stack");
                Check(mixedPersonal.m_stack == 4 && loan.m_stack == 20 && personal.m_stack == 15, "rejected mixed slot transfer consumes neither source nor target arrows");
                var blockedLoan = Arrows(true, 4, 3);
                Check(!inventory.AddItem(blockedLoan) && blockedLoan.m_stack == 4 && personal.m_stack == 15,
                    "a detached non-prison inventory cannot acquire or mix loan arrows");
                var stackSource = new Inventory("Party Prison quick-stack source", null, 8, 4); var sourcePersonal = Arrows(false, 6, 0);
                stackSource.GetAllItems().Add(sourcePersonal);
                Check(inventory.StackAll(stackSource, false) == 0 && sourcePersonal.m_stack == 6 && personal.m_stack == 15 && loan.m_stack == 20,
                    "quick stack encountering prison loans preserves all personal source and target arrows");
                Check(inventory.RemoveItem(loan) && personal.m_stack == 15 && inventory.GetAllItems().Contains(personal),
                    "removing the exact loan stack leaves all fifteen original/personal arrows intact");
            }
            finally { context.SetValue(null, originalContext); }
            report.AppendLine("PASS: Detached native inventories keep personal and prison arrow stacks separate during normal addition, stack lookup, manual slot transfer, quick stack and exact loan removal.");
        }

        private void Check(bool condition, string reason)
        { ++checks; if (!condition) throw new InvalidOperationException("Party Prison native assertion: " + reason); }

        private void Finish(string result, int code)
        {
            if (finished) return; finished = true;
            PrefabManager.OnVanillaPrefabsAvailable -= PrefabsAvailable;
            Logger.LogInfo(result);
            File.WriteAllText(Path.Combine(Root, "result.txt"), result, new UTF8Encoding(false));
            Application.Quit(code);
        }

        private void OnDestroy() { PrefabManager.OnVanillaPrefabsAvailable -= PrefabsAvailable; }
    }
}
