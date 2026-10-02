using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimModPack.PinRemoval
{
    // A nonmodal shortcut owns only the physical keys of its captured stroke.
    // Pin creation belongs to the controller's later Update, never these patches.
    public sealed class SuggestionShortcutGate : IDisposable
    {
        private static SuggestionShortcutGate active;
        private static readonly Func<KeyCode, bool> ReadHeld = delegate(KeyCode key) { return ZInput.GetKey(key, false); };
        private static readonly Func<KeyCode, bool> ReadDown = delegate(KeyCode key) { return ZInput.GetKeyDown(key, false); };
        private readonly Harmony harmony;
        private readonly Func<bool> canCapture;
        private readonly Func<KeyboardShortcut[]> shortcuts;
        private readonly Action<int> queueAction;
        private readonly Action<Exception> report;
        private readonly SuggestionButtonCache cache = new SuggestionButtonCache();
        private readonly List<MethodBase> buttons = new List<MethodBase>();
        private readonly List<MethodBase> ticks = new List<MethodBase>();
        private readonly Dictionary<KeyCode, int> owned = new Dictionary<KeyCode, int>();
        private readonly List<KeyCode> ownedScratch = new List<KeyCode>(8);
        private static readonly KeyboardShortcut[] Empty = new KeyboardShortcut[0];
        private readonly SuggestionStroke[] strokes = new[] { new SuggestionStroke(), new SuggestionStroke(), new SuggestionStroke() };
        private KeyboardShortcut[] configured = Empty;
        private int strokeCount;
        private int pollFrame = -1;
        private object input;
        private bool disposed;

        public SuggestionShortcutGate(Harmony harmony, Func<bool> canCapture,
            Func<KeyboardShortcut[]> shortcuts, Action<int> queueAction, Action<Exception> report)
        {
            if (harmony == null) throw new ArgumentNullException("harmony");
            if (canCapture == null) throw new ArgumentNullException("canCapture");
            if (shortcuts == null) throw new ArgumentNullException("shortcuts");
            if (queueAction == null) throw new ArgumentNullException("queueAction");
            if (active != null) throw new InvalidOperationException("A pin suggestion shortcut gate is already active");
            this.harmony = harmony; this.canCapture = canCapture; this.shortcuts = shortcuts;
            this.queueAction = queueAction; this.report = report;
            try
            {
                Reset(); active = this;
                foreach (string name in new[] { "GetButton", "GetButtonDown", "GetButtonUp" })
                {
                    MethodInfo method = RequiredMethod(name, typeof(string));
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(SuggestionShortcutGate), "BeforeButton") { priority = Priority.First });
                    buttons.Add(method);
                }
                foreach (string name in new[] { "Update", "FixedUpdate" })
                {
                    MethodInfo method = RequiredMethod(name, typeof(float));
                    harmony.Patch(method, postfix: new HarmonyMethod(typeof(SuggestionShortcutGate), "AfterNativeTick") { priority = Priority.First });
                    ticks.Add(method);
                }
            }
            catch { Dispose(); throw; }
        }

        private static MethodInfo RequiredMethod(string name, params Type[] parameters)
        {
            MethodInfo method = AccessTools.Method(typeof(ZInput), name, parameters);
            if (method == null) throw new MissingMethodException(typeof(ZInput).FullName, name);
            return method;
        }

        public void Tick()
        {
            if (disposed) return;
            // Force another sample after either native input phase, even in the
            // same rendered frame: InputSystem may have advanced since a query.
            try { Poll(true); if (owned.Count != 0) cache.Consume(owned.Keys); }
            catch (Exception error) { Report(error); }
        }

        // Re-prime on session changes. Already claimed keys stay owned until release,
        // even after the HUD closes or a binding changes, so buffered edges cannot replay.
        public void Reset()
        {
            if (disposed) return;
            input = cache.Input;
            configured = shortcuts() ?? Empty;
            strokeCount = Math.Min(3, configured.Length);
            for (int i = 0; i < strokeCount; ++i)
            {
                if (!strokes[i].Same(configured[i])) strokes[i].Bind(configured[i]);
                strokes[i].Prime(Time.frameCount, ReadDown, ReadHeld);
            }
            pollFrame = -1;
            ExpireOwned(Time.frameCount);
            if (owned.Count != 0) cache.Consume(owned.Keys);
        }

        private bool ExpireOwned(int frame)
        {
            bool changed = false;
            ownedScratch.Clear();
            foreach (KeyCode key in owned.Keys) ownedScratch.Add(key);
            for (int i = 0; i < ownedScratch.Count; ++i)
            {
                KeyCode key = ownedScratch[i];
                if (owned[key] >= 0 && frame > owned[key]) { owned.Remove(key); continue; }
                if (!SuggestionStroke.Held(key, ReadHeld) && owned[key] < 0)
                { owned[key] = frame; changed = true; }
            }
            return changed;
        }

        private void Poll(bool force)
        {
            object currentInput = cache.Input;
            if (!ReferenceEquals(input, currentInput)) { Reset(); return; }
            KeyboardShortcut[] next = shortcuts() ?? Empty;
            int frame = Time.frameCount;
            if (!force && frame == pollFrame && ReferenceEquals(next, configured)) return;
            if (strokeCount != Math.Min(3, next.Length)) { Reset(); return; }
            configured = next; pollFrame = frame;
            bool changed = ExpireOwned(frame);
            // Observe every binding even in blocked UI. Closing that UI, adding a
            // missing modifier, or rebinding a held key must not invent a fresh press.
            for (int i = 0; i < strokeCount; ++i)
            {
                if (!strokes[i].Same(configured[i]))
                { strokes[i].Bind(configured[i]); strokes[i].Prime(frame, ReadDown, ReadHeld); }
                strokes[i].Observe(frame, ReadDown, ReadHeld);
            }
            if (!ZInput.s_IsRebindActive && canCapture())
            {
                for (int i = 0; i < strokeCount; ++i)
                {
                    SuggestionStroke stroke = strokes[i];
                    if (!stroke.Matches(ReadHeld)) continue;
                    owned[SuggestionStroke.Family(stroke.Binding.MainKey)] = -1;
                    foreach (KeyCode modifier in stroke.Binding.Modifiers)
                        owned[SuggestionStroke.Family(modifier)] = -1;
                    cache.Consume(owned.Keys);
                    queueAction(i + 1);
                    break;
                }
            }
            if (changed) cache.Consume(owned.Keys);
        }

        private static bool BeforeButton(string __0, ref bool __result)
        {
            SuggestionShortcutGate gate = active;
            if (gate == null || gate.disposed) return true;
            try
            {
                gate.Poll(false);
                if (gate.owned.Count == 0) return true;
                if (!gate.cache.Consume(__0, gate.owned.Keys)) return true;
                __result = false; return false;
            }
            catch (Exception error) { gate.Report(error); return true; }
        }

        private static void AfterNativeTick()
        {
            if (active != null) active.Tick();
        }

        private void Report(Exception error)
        {
            if (report != null) { try { report(error); } catch { } }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (ReferenceEquals(active, this)) active = null;
            try { if (owned.Count != 0) cache.Consume(owned.Keys); }
            catch (Exception error) { Report(error); }
            finally
            {
                MethodInfo prefix = AccessTools.Method(typeof(SuggestionShortcutGate), "BeforeButton");
                MethodInfo postfix = AccessTools.Method(typeof(SuggestionShortcutGate), "AfterNativeTick");
                foreach (MethodBase method in buttons) harmony.Unpatch(method, prefix);
                foreach (MethodBase method in ticks) harmony.Unpatch(method, postfix);
                buttons.Clear(); ticks.Clear(); owned.Clear(); ownedScratch.Clear(); strokeCount = 0;
            }
        }
    }

    internal sealed class SuggestionStroke
    {
        internal KeyboardShortcut Binding;
        private IEnumerable<KeyCode> sourceModifiers;
        private KeyCode[] modifiers;
        private bool wasHeld, wasDown, edge;
        private int pressFrame = -1;

        internal bool Same(KeyboardShortcut binding)
        {
            if (modifiers == null || Binding.MainKey != binding.MainKey) return false;
            if (ReferenceEquals(sourceModifiers, binding.Modifiers)) return true;
            int i = 0;
            foreach (KeyCode modifier in binding.Modifiers)
            { if (i == modifiers.Length || modifiers[i++] != modifier) return false; }
            if (i != modifiers.Length) return false;
            sourceModifiers = binding.Modifiers; return true;
        }

        internal void Bind(KeyboardShortcut binding)
        {
            sourceModifiers = binding.Modifiers; modifiers = sourceModifiers.ToArray();
            Binding = new KeyboardShortcut(binding.MainKey, modifiers);
        }

        internal void Prime(int frame, Func<KeyCode, bool> down, Func<KeyCode, bool> held)
        {
            wasHeld = Held(Binding.MainKey, held); wasDown = Held(Binding.MainKey, down); edge = false;
            pressFrame = wasHeld || wasDown ? frame : -1;
        }

        internal void Observe(int frame, Func<KeyCode, bool> down, Func<KeyCode, bool> held)
        {
            bool mainHeld = Held(Binding.MainKey, held), mainDown = Held(Binding.MainKey, down);
            bool fresh = !wasHeld && (mainHeld || (mainDown && !wasDown));
            wasHeld = mainHeld; wasDown = mainDown;
            edge = Binding.MainKey != KeyCode.None && ZInput.IsKeyCodeValid(Binding.MainKey) && fresh && pressFrame != frame;
            if (edge) pressFrame = frame;
        }

        internal bool Matches(Func<KeyCode, bool> held)
        {
            return edge && MapControls.Matches(Binding,
                delegate(KeyCode key) { return Family(key) == Family(Binding.MainKey); }, held);
        }

        internal static KeyCode Family(KeyCode key)
        {
            if (key == KeyCode.RightControl) return KeyCode.LeftControl;
            if (key == KeyCode.RightShift) return KeyCode.LeftShift;
            if (key == KeyCode.RightAlt) return KeyCode.LeftAlt;
            if (key == KeyCode.RightCommand) return KeyCode.LeftCommand;
            return key;
        }

        internal static IEnumerable<KeyCode> Members(KeyCode key)
        {
            key = Family(key); yield return key;
            if (key == KeyCode.LeftControl) yield return KeyCode.RightControl;
            if (key == KeyCode.LeftShift) yield return KeyCode.RightShift;
            if (key == KeyCode.LeftAlt) yield return KeyCode.RightAlt;
            if (key == KeyCode.LeftCommand) yield return KeyCode.RightCommand;
        }

        internal static bool Held(KeyCode key, Func<KeyCode, bool> read)
        {
            key = Family(key);
            if (key == KeyCode.None) return false;
            return read(key) || (key == KeyCode.LeftControl && read(KeyCode.RightControl))
                || (key == KeyCode.LeftShift && read(KeyCode.RightShift))
                || (key == KeyCode.LeftAlt && read(KeyCode.RightAlt))
                || (key == KeyCode.LeftCommand && read(KeyCode.RightCommand));
        }
    }

    // Valheim 1.0.16 uses InputSystem effective paths, not the older m_key field.
    // Use the game's own key-to-path conversion, including current native rebinds.
    internal sealed class SuggestionButtonCache
    {
        private static readonly FieldInfo Instance = RequiredField(typeof(ZInput), "m_instance");
        private static readonly FieldInfo Buttons = RequiredField(typeof(ZInput), "m_buttons");
        private static readonly Type ButtonType = typeof(ZInput).GetNestedType("ButtonDef", BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo ButtonAction = RequiredField(ButtonType, "<ButtonAction>k__BackingField");
        private static readonly MethodInfo ActionPath = AccessTools.Method(ButtonType, "GetActionPath", new[] { typeof(bool) });
        private static readonly MethodInfo KeyPath = AccessTools.Method(typeof(ZInput), "KeyCodeToPath", new[] { typeof(KeyCode), typeof(bool) });
        private static readonly FieldInfo[] Held = Fields("m_heldDynamic", "m_heldFixed");
        private static readonly FieldInfo[] WasPressed = Fields("m_wasPressedDynamic", "m_wasPressedFixed");
        private static readonly FieldInfo[] Edges = Fields("m_pressedDynamic", "m_pressedFixed", "m_releasedDynamic", "m_releasedFixed");
        private static readonly object[] Effective = new object[] { true };
        private readonly Dictionary<KeyCode, string> paths = new Dictionary<KeyCode, string>();
        private readonly HashSet<string> ownedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal SuggestionButtonCache()
        {
            if (ActionPath == null || KeyPath == null) throw new MissingMethodException("ZInput button binding path contract is unavailable");
        }

        internal object Input { get { return Instance.GetValue(null); } }

        private static FieldInfo RequiredField(Type type, string name)
        {
            FieldInfo field = AccessTools.Field(type, name);
            if (field == null) throw new MissingFieldException(type == null ? "ZInput.ButtonDef" : type.FullName, name);
            return field;
        }

        private static FieldInfo[] Fields(params string[] names)
        { return names.Select(delegate(string name) { return RequiredField(ButtonType, name); }).ToArray(); }

        private IDictionary Values()
        {
            object input = Input;
            return input == null ? null : Buttons.GetValue(input) as IDictionary;
        }

        private HashSet<string> Paths(IEnumerable<KeyCode> keys)
        {
            HashSet<string> result = ownedPaths; result.Clear();
            foreach (KeyCode key in keys)
            {
                AddPath(key, result);
                if (key == KeyCode.LeftControl) AddPath(KeyCode.RightControl, result);
                if (key == KeyCode.LeftShift) AddPath(KeyCode.RightShift, result);
                if (key == KeyCode.LeftAlt) AddPath(KeyCode.RightAlt, result);
                if (key == KeyCode.LeftCommand) AddPath(KeyCode.RightCommand, result);
                // InputSystem also permits generic modifier-family bindings.
                if (key == KeyCode.LeftControl) result.Add("<Keyboard>/ctrl");
                if (key == KeyCode.LeftShift) result.Add("<Keyboard>/shift");
                if (key == KeyCode.LeftAlt) result.Add("<Keyboard>/alt");
                if (key == KeyCode.LeftCommand) result.Add("<Keyboard>/meta");
            }
            return result;
        }

        private void AddPath(KeyCode key, HashSet<string> result)
        {
            string path;
            if (!paths.TryGetValue(key, out path))
            {
                path = KeyPath.Invoke(null, new object[] { key, false }) as string;
                paths[key] = path;
            }
            if (!String.IsNullOrEmpty(path)) result.Add(path);
        }

        internal void Consume(IEnumerable<KeyCode> keys)
        {
            HashSet<string> owned = Paths(keys); if (owned.Count == 0) return;
            IDictionary values = Values(); if (values == null) return;
            foreach (DictionaryEntry pair in values)
                if (Matches(pair.Value, owned)) ConsumeState(pair.Value);
        }

        internal bool Consume(string name, IEnumerable<KeyCode> keys)
        {
            HashSet<string> owned = Paths(keys); if (owned.Count == 0 || String.IsNullOrEmpty(name)) return false;
            IDictionary values = Values();
            if (values == null || !values.Contains(name) || !Matches(values[name], owned)) return false;
            ConsumeState(values[name]); return true;
        }

        private static bool Matches(object button, HashSet<string> owned)
        {
            if (button == null || ButtonAction.GetValue(button) == null) return false;
            try
            {
                string path = ActionPath.Invoke(button, Effective) as string;
                return !String.IsNullOrEmpty(path) && owned.Contains(path);
            }
            catch (TargetInvocationException error)
            {
                // Native virtual actions such as HotbarUse can have no bindings.
                // GetActionPath indexes binding zero; they have no keyboard key
                // belonging to our chord and must keep their independent state.
                if (error.InnerException is IndexOutOfRangeException || error.InnerException is ArgumentOutOfRangeException
                    || error.InnerException is NullReferenceException) return false;
                throw;
            }
        }

        private static void ConsumeState(object button)
        {
            foreach (FieldInfo edge in Edges) edge.SetValue(button, false);
            for (int i = 0; i < Held.Length; ++i) WasPressed[i].SetValue(button, Held[i].GetValue(button));
        }
    }
}
