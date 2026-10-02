using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using ValheimModPack.PinRemoval;

public static class SuggestionShortcutGateTests
{
    private static int checks;
    private static void Check(bool value,string message) { ++checks; if(!value) throw new Exception(message); }
    private sealed class Fixture : IDisposable
    {
        internal readonly List<int> Actions=new List<int>();
        internal readonly List<Exception> Errors=new List<Exception>();
        internal KeyboardShortcut[] Bindings=new[]{new KeyboardShortcut(KeyCode.G,KeyCode.LeftControl),
            new KeyboardShortcut(KeyCode.G,KeyCode.LeftControl,KeyCode.LeftShift),new KeyboardShortcut(KeyCode.G,KeyCode.LeftControl,KeyCode.LeftAlt)};
        internal bool Allowed=true,QueueThrows;
        internal SuggestionShortcutGate Gate;
        internal Fixture()
        {
            Input.Held.Clear(); Input.Down.Clear(); Time.frameCount=0; Time.inFixedTimeStep=false; ZInput.s_IsRebindActive=false; ZInput.NewInput();
            Gate=new SuggestionShortcutGate(new Harmony("test"),()=>Allowed,()=>Bindings,
                action=>{ Actions.Add(action); if(QueueThrows) throw new InvalidOperationException("queue failed"); },Errors.Add);
        }
        internal void Frame(params KeyCode[] keys)
        {
            var next=new HashSet<KeyCode>(keys); Input.Down.Clear();
            foreach(var key in next) if(!Input.Held.Contains(key)) Input.Down.Add(key);
            Input.Held.Clear(); foreach(var key in next) Input.Held.Add(key);
            ++Time.frameCount; ZInput.Stage();
        }
        public void Dispose() { Gate.Dispose(); Gate.Dispose(); Check(Harmony.Count==0,"dispose removes only gate patches"); }
    }
    public static int Main()
    {
        try
        {
            for(int action=1;action<=3;++action)
            using(var f=new Fixture())
            {
                if(action==1) f.Frame(KeyCode.G,KeyCode.LeftControl,KeyCode.W,KeyCode.Space);
                if(action==2) f.Frame(KeyCode.G,KeyCode.RightControl,KeyCode.RightShift,KeyCode.W,KeyCode.Space);
                if(action==3) f.Frame(KeyCode.G,KeyCode.LeftControl,KeyCode.RightAlt,KeyCode.W,KeyCode.Space);
                Input.Down.Clear();
                Check(!ZInput.GetButtonDown("GP"),"cached GP is captured before native consumption after raw edge expires");
                Check(f.Actions.Count==1&&f.Actions[0]==action,"configured accept/next/dismiss queue exactly one correct action");
                Check(ZInput.GetButton("Forward")&&ZInput.GetButtonDown("Jump"),"unrelated held movement and jump edge survive");
                Check(!ZInput.GetButton("Crouch")&&!ZInput.GetButton("RightCrouch"),"captured modifier family is suppressed");
                Check(!ZInput.Button("GP").AnyEdge&&!ZInput.Button("Crouch").AnyEdge,"both dynamic and fixed native caches are drained");
                f.Gate.Tick(); ZInput.GetButtonDown("GP");
                Check(f.Actions.Count==1&&f.Errors.Count==0,"repeated polling cannot enqueue duplicate actions or errors");
                f.Frame(KeyCode.W); f.Allowed=false; ZInput.FixedUpdate(.02f);
                Check(!ZInput.GetButtonUp("GP")&&ZInput.GetButton("Forward"),"release is consumed without stopping movement after HUD hides");
                f.Frame(KeyCode.W); f.Frame(KeyCode.G,KeyCode.W);
                Check(ZInput.GetButtonDown("GP"),"plain native key works again after captured release frame");
            }
            foreach(bool fixedTick in new[]{false,true})
            using(var f=new Fixture())
            {
                f.Bindings[0]=new KeyboardShortcut(KeyCode.F,KeyCode.LeftControl); ZInput.Button("GP").Rebind(KeyCode.F); f.Gate.Reset();
                f.Frame(KeyCode.F,KeyCode.LeftControl,KeyCode.W); Input.Down.Clear();
                if(fixedTick) ZInput.FixedUpdate(.02f); else ZInput.Update(.02f);
                f.Frame(KeyCode.W); Input.Down.Clear(); f.Allowed=false; f.Gate.Tick();
                Check(f.Actions.Count==1&&f.Actions[0]==1,"native dynamic/fixed tick captures quick tap before controller Update");
                Check(!ZInput.GetButtonDown("GP")&&!ZInput.GetButtonUp("GP"),"quick-tap cached GP cannot replay after HUD closes");
                Check(ZInput.GetButton("Forward"),"quick tap leaves held movement available");
            }
            using(var f=new Fixture())
            {
                f.Frame(KeyCode.LeftControl,KeyCode.G,KeyCode.W); ZInput.Button("JoyButtonB").GamepadHeld=true; ZInput.Stage();
                Check(!ZInput.GetButtonDown("Crouch"),"crouch-first query captures chord before native Player.Update");
                Check(f.Actions.Count==1&&ZInput.GetButtonDown("JoyButtonB"),"unrelated gamepad input survives capture");
                Time.inFixedTimeStep=true;
                Check(!ZInput.GetButtonDown("GP")&&ZInput.GetButtonDown("JoyButtonB"),"fixed GP cache clears while fixed gamepad cache survives");
                Time.inFixedTimeStep=false;
                Check(!ZInput.GetButtonDown("NullVirtual")&&f.Errors.Count==0,"virtual buttons with no InputAction are safely ignored");
                Check(ZInput.GetButton("HotbarUse")&&f.Errors.Count==0,"unbound native HotbarUse retains its synthetic input without interrupting capture");
            }
            using(var f=new Fixture())
            {
                f.Frame(KeyCode.G); Check(ZInput.GetButtonDown("GP")&&f.Actions.Count==0,"missing modifier does not claim a normal native press");
                f.Frame(KeyCode.G,KeyCode.LeftControl); f.Gate.Tick();
                Check(f.Actions.Count==0,"adding missing modifier to rejected held stroke does not activate shortcut");
                f.Frame(); f.Gate.Tick(); f.Frame(KeyCode.G,KeyCode.LeftControl); ZInput.GetButtonDown("GP");
                Check(f.Actions.Count==1,"fresh valid stroke after release is accepted");
            }
            using(var f=new Fixture())
            {
                f.Frame(KeyCode.G,KeyCode.LeftControl,KeyCode.LeftShift,KeyCode.LeftAlt); f.Gate.Tick();
                Check(f.Actions.Count==0&&ZInput.GetButtonDown("GP"),"extra modifier families reject all configured chords without swallowing native input");
                f.Frame(KeyCode.G,KeyCode.LeftControl); f.Gate.Tick();
                Check(f.Actions.Count==0,"removing an extra modifier does not turn a held stroke into an action");
            }
            using(var f=new Fixture())
            {
                f.Allowed=false; f.Frame(KeyCode.G,KeyCode.LeftControl); Check(ZInput.GetButtonDown("GP"),"blocked UI context preserves native input");
                f.Allowed=true; f.Gate.Tick(); f.Frame(KeyCode.G,KeyCode.LeftControl); f.Gate.Tick();
                Check(f.Actions.Count==0,"closing UI while shortcut remains held cannot enqueue an action");
                f.Frame(); f.Gate.Tick(); f.Frame(KeyCode.G,KeyCode.LeftControl); f.Gate.Tick(); Check(f.Actions.Count==1,"fresh stroke works after UI context becomes valid");
            }
            using(var f=new Fixture())
            {
                f.Frame(KeyCode.F,KeyCode.LeftControl); ZInput.Button("GP").Rebind(KeyCode.F); ZInput.Stage();
                f.Bindings[0]=new KeyboardShortcut(KeyCode.F,KeyCode.LeftControl); f.Gate.Tick();
                Check(f.Actions.Count==0,"rebinding an already held main key primes without activating");
                f.Frame(); f.Gate.Tick(); f.Frame(KeyCode.F,KeyCode.RightControl); Check(!ZInput.GetButtonDown("GP")&&f.Actions.Count==1,"rebound native effective path and either Ctrl side are consumed");
                f.Bindings[0]=new KeyboardShortcut(KeyCode.H,KeyCode.LeftAlt); f.Gate.Reset();
                Check(!ZInput.GetButton("GP"),"rebinding during captured hold does not release the old stroke into guardian power");
                f.Frame(); f.Gate.Tick(); f.Frame(KeyCode.F); Check(ZInput.GetButtonDown("GP"),"old native binding restores after release");
            }
            using(var f=new Fixture())
            {
                f.Frame(KeyCode.G,KeyCode.LeftControl); f.Gate.Tick(); f.Gate.Reset();
                Check(f.Actions.Count==1&&!ZInput.GetButton("GP"),"session reset primes existing stroke and retains its ownership");
                ZInput.NewInput(); ZInput.Stage(); f.Gate.Tick();
                Check(f.Actions.Count==1&&!ZInput.GetButtonDown("GP"),"new native input instance cannot replay captured held key");
            }
            using(var f=new Fixture())
            {
                f.Bindings=new[]{new KeyboardShortcut(KeyCode.None),new KeyboardShortcut(KeyCode.None),new KeyboardShortcut(KeyCode.None)}; f.Gate.Reset();
                f.Frame(KeyCode.G,KeyCode.LeftControl); Check(ZInput.GetButtonDown("GP")&&ZInput.GetButtonDown("Crouch")&&f.Actions.Count==0,"None bindings leave every native keyboard action intact");
            }
            using(var f=new Fixture())
            {
                f.Bindings=new[]{new KeyboardShortcut(KeyCode.LeftControl),new KeyboardShortcut(KeyCode.None),new KeyboardShortcut(KeyCode.None)}; f.Gate.Reset();
                f.Frame(KeyCode.RightControl,KeyCode.W); Check(!ZInput.GetButtonDown("RightCrouch"),"modifier used as main key captures either side");
                Check(f.Actions.Count==1&&ZInput.GetButton("Forward"),"crouch-key remap does not freeze forward movement");
            }
            using(var f=new Fixture())
            {
                f.Bindings[1]=f.Bindings[2]=f.Bindings[0]; f.Gate.Reset(); f.Frame(KeyCode.G,KeyCode.LeftControl); f.Gate.Tick(); f.Gate.Tick();
                Check(f.Actions.Count==1&&f.Actions[0]==1,"duplicate shortcuts deterministically prefer accept and never queue secondary actions");
            }
            using(var f=new Fixture())
            {
                ZInput.s_IsRebindActive=true; f.Frame(KeyCode.G,KeyCode.LeftControl); f.Gate.Tick();
                Check(f.Actions.Count==0&&ZInput.GetButtonDown("GP"),"native rebind UI is never captured");
                ZInput.s_IsRebindActive=false; f.Frame(KeyCode.G,KeyCode.LeftControl); f.Gate.Tick(); Check(f.Actions.Count==0,"exiting native rebind UI does not activate held key");
            }
            using(var f=new Fixture())
            {
                f.QueueThrows=true; f.Frame(KeyCode.G,KeyCode.LeftControl); Check(!ZInput.GetButtonDown("GP"),"queue exception cannot leak captured native action");
                Check(f.Errors.Count==1,"queue exception is reported once");
                f.Gate.Tick(); Check(f.Actions.Count==1&&f.Errors.Count==1,"failed queue is not retried on held stroke");
            }
            using(var f=new Fixture())
            {
                f.Frame(KeyCode.W); f.Gate.Tick(); int reads=Input.Reads;
                for(int i=0;i<200;++i) ZInput.GetButton("Forward");
                Check(Input.Reads==reads,"idle same-frame gamebutton queries do not repeatedly poll or allocate binding snapshots");
                Input.Held.Add(KeyCode.G); Input.Held.Add(KeyCode.LeftControl); Input.Down.Clear(); ZInput.Update(.02f);
                Check(f.Actions.Count==1&&!ZInput.GetButtonDown("GP"),"later native input phase in same rendered frame forces resampling");
            }
            using(var f=new Fixture())
            {
                var allocationMethod=typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread",BindingFlags.Public|BindingFlags.Static);
                if(allocationMethod!=null)
                {
                    var allocated=(Func<long>)Delegate.CreateDelegate(typeof(Func<long>),allocationMethod);
                    var poll=(Action<bool>)Delegate.CreateDelegate(typeof(Action<bool>),f.Gate,typeof(SuggestionShortcutGate).GetMethod("Poll",BindingFlags.NonPublic|BindingFlags.Instance));
                    f.Gate.Tick(); poll(false); f.Gate.Reset(); f.Gate.Tick();
                    long before=allocated();
                    for(int i=0;i<10000;++i) poll(false);
                    Check(allocated()==before,"10,000 idle cached native-query polls allocate zero managed bytes");
                    before=allocated();
                    for(int i=0;i<10000;++i) f.Gate.Reset();
                    Check(allocated()==before,"10,000 unchanged hidden-HUD resets allocate zero managed bytes");
                }
            }
            Console.WriteLine("PASS: "+checks+" suggestion shortcut gate assertions."); return 0;
        }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
