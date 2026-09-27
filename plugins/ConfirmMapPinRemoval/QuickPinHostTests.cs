using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ValheimModPack.PinRemoval
{
    internal static class QuickPinHostTests
    {
        private static int checks;
        private const string Preset = "default.raspberries";
        public static int Main()
        {
            try
            {
                GlobalLaunchAndGuards(); PointAndArmedPlacement(); RenameAndStaleContext(); PersistenceAndLanguage();
                System.Console.WriteLine("PASS " + checks + " actual QuickPinController host checks (real preset persistence/localization; Unity/Harmony hosts doubled). ");
                return 0;
            }
            catch (Exception error) { System.Console.Error.WriteLine(error); return 1; }
        }

        private static void GlobalLaunchAndGuards()
        {
            using (var fixture = new Fixture())
            {
                fixture.Frame(KeyCode.LeftControl, KeyCode.P);
                Check(fixture.Controller.IsOpen && Minimap.instance.m_mode == Minimap.MapMode.Small, "Ctrl+P opens without map");
                Check(GUIManager.OwnBlocks == 1 && TextInput.IsVisible(), "picker owns an input block");
                fixture.Window.Place(Preset);
                Check(Minimap.instance.m_pins.Count == 1 && Same(Minimap.instance.m_pins[0].m_pos, new Vector3(1,2,3)), "place here uses player position");
                Check(!fixture.Controller.IsBusy && GUIManager.OwnBlocks == 0, "own input block does not invalidate action and releases after placement");
                Check(fixture.Harmony.Patches == 3, "all three controller hooks registered");
            }
            using (var fixture = new Fixture())
            {
                Action[] enable = { () => Menu.Visible=true, () => InventoryGui.Visible=true, () => UnifiedPopup.Visible=true,
                    () => global::Console.Visible=true, () => Chat.instance.Focus=true, () => Minimap.TextFocused=true,
                    () => GUIManager.ForeignBlocks=1, () => Player.m_localPlayer.Safe=false };
                Action[] disable = { () => Menu.Visible=false, () => InventoryGui.Visible=false, () => UnifiedPopup.Visible=false,
                    () => global::Console.Visible=false, () => Chat.instance.Focus=false, () => Minimap.TextFocused=false,
                    () => GUIManager.ForeignBlocks=0, () => Player.m_localPlayer.Safe=true };
                string[] labels = { "menu", "inventory", "popup", "console", "chat typing", "native map typing", "foreign Jotunn modal", "unsafe player" };
                for (int i=0; i<enable.Length; ++i)
                {
                    enable[i](); fixture.Frame(KeyCode.LeftControl, KeyCode.P);
                    Check(!fixture.Controller.IsOpen, "global launcher ignores " + labels[i]); disable[i]();
                }
                var legacy = new GameObject(); legacy.AddComponent<InputField>().isFocused=true;
                EventSystem.current.currentSelectedGameObject=legacy; fixture.Frame(KeyCode.LeftControl, KeyCode.P);
                Check(!fixture.Controller.IsOpen, "global launcher ignores focused legacy input");
                var modern = new GameObject(); modern.AddComponent<TMP_InputField>().isFocused=true;
                EventSystem.current.currentSelectedGameObject=modern; fixture.Frame(KeyCode.LeftControl, KeyCode.P);
                Check(!fixture.Controller.IsOpen, "global launcher ignores focused TMP input");
                var derived = new GameObject(); derived.AddComponent<GuiInputField>().isFocused=true;
                EventSystem.current.currentSelectedGameObject=derived; fixture.Frame(KeyCode.LeftControl, KeyCode.P);
                Check(!fixture.Controller.IsOpen, "global launcher ignores focused native TMP subclass");
                derived.activeInHierarchy=false; fixture.Frame(KeyCode.LeftControl, KeyCode.P);
                Check(fixture.Controller.IsOpen, "stale focus on inactive field does not trap the global shortcut"); fixture.Controller.Close();
                legacy.GetComponent<InputField>().enabled=false; EventSystem.current.currentSelectedGameObject=legacy;
                fixture.Frame(KeyCode.LeftControl, KeyCode.P);
                Check(fixture.Controller.IsOpen, "stale focus on disabled legacy field does not trap the global shortcut"); fixture.Controller.Close();
                modern.GetComponent<TMP_InputField>().enabled=false; EventSystem.current.currentSelectedGameObject=modern;
                fixture.Frame(KeyCode.LeftControl, KeyCode.P);
                Check(fixture.Controller.IsOpen, "stale focus on disabled TMP field does not trap the global shortcut"); fixture.Controller.Close();
                EventSystem.current.currentSelectedGameObject=null; fixture.Frame(KeyCode.LeftControl, KeyCode.LeftAlt, KeyCode.P);
                Check(!fixture.Controller.IsOpen, "Ctrl+Alt+P does not launch");
                fixture.Frame(KeyCode.RightControl, KeyCode.P);
                Check(fixture.Controller.IsOpen, "right Ctrl supported");
                fixture.Controller.Tick(false);
                Check(!fixture.Controller.IsOpen && GUIManager.OwnBlocks==0, "other map modal closes picker and releases own block");
            }
        }

        private static void PointAndArmedPlacement()
        {
            using (var fixture = new Fixture())
            {
                Minimap.instance.m_mode=Minimap.MapMode.Large;
                ZInput.pointerPosition=new Vector3(12,0,34); fixture.Frame(KeyCode.LeftShift);
                Check(!fixture.Click(), "Shift+click consumes native map click");
                Check(fixture.Controller.IsOpen, "Shift+click opens presets at chosen point");
                ZInput.pointerPosition=new Vector3(999,0,999); fixture.Window.Place(Preset);
                Check(Same(Minimap.instance.m_pins[0].m_pos,new Vector3(12,0,34)), "point target is captured before pointer moves");
                Check(!fixture.DoubleClick(), "following double-click suppressed after placement");
                fixture.Frame(); Check(fixture.DoubleClick(), "ordinary double-click restored after debounce");
                fixture.Frame(KeyCode.LeftControl, KeyCode.P); fixture.Window.Choose(Preset);
                Check(fixture.Controller.IsBusy && !fixture.Controller.IsOpen && Minimap.instance.m_mode==Minimap.MapMode.Large,
                    "choose point arms preset and opens large map");
                Check(!fixture.DoubleClick(), "double-click blocked while placement armed");
                ZInput.pointerPosition=new Vector3(90,1,-25); fixture.Frame();
                Check(!fixture.Click() && Minimap.instance.m_pins.Count==2, "armed left-click creates one pin");
                Check(Same(Minimap.instance.m_pins[1].m_pos,new Vector3(90,1,-25)), "armed placement uses next map click");
                fixture.Frame(KeyCode.LeftControl, KeyCode.P); fixture.Window.Choose(Preset);
                fixture.Frame(KeyCode.Escape);
                Check(!fixture.Controller.IsBusy, "Escape cancels armed placement");
                int before=Minimap.instance.m_pins.Count; fixture.Frame();
                Check(fixture.Click() && Minimap.instance.m_pins.Count==before, "ordinary click after cancel creates no preset pin");
                fixture.Frame(KeyCode.LeftControl, KeyCode.P); fixture.Window.Choose(Preset);
                Minimap.instance.m_mode=Minimap.MapMode.Small; fixture.Frame();
                Check(!fixture.Controller.IsBusy, "closing map cancels armed placement");
                fixture.Frame(KeyCode.LeftControl, KeyCode.P); fixture.Window.Choose(Preset);
                GUIManager.ForeignBlocks=1; fixture.Frame();
                Check(!fixture.Controller.IsBusy && GUIManager.ForeignBlocks==1, "foreign Jotunn modal cancels armed placement without releasing foreign lock");
                GUIManager.ForeignBlocks=0; fixture.Frame(); before=Minimap.instance.m_pins.Count;
                Check(fixture.Click() && Minimap.instance.m_pins.Count==before, "after foreign modal closes the click stays vanilla");
            }
            using (var fixture = new Fixture())
            {
                fixture.Frame(KeyCode.LeftControl, KeyCode.P); fixture.Window.Place(Preset);
                fixture.Frame(KeyCode.LeftControl, KeyCode.P); fixture.Window.Place(Preset);
                Check(Minimap.instance.m_pins.Count==1, "same preset at same point does not duplicate");
                fixture.Frame(KeyCode.LeftControl, KeyCode.P); fixture.Window.Choose(Preset); fixture.Frame();
                PinHistoryController.FailRemember=true; ZInput.pointerPosition=new Vector3(20,0,40);
                Check(!fixture.Click(), "failed metadata write consumes placement");
                Check(Minimap.instance.m_pins.Count==1 && fixture.Errors.Count==1 && !fixture.Controller.IsBusy,
                    "failed metadata write rolls back created pin and closes request");
            }
        }

        private static void RenameAndStaleContext()
        {
            using (var fixture = new Fixture())
            {
                var pin=Minimap.instance.AddPin(new Vector3(4,0,8),Minimap.PinType.Icon0,"Original",true,false,0,Splatform.PlatformUserID.None);
                Minimap.instance.m_mode=Minimap.MapMode.Large; Minimap.instance.Closest=pin;
                fixture.Frame(KeyCode.LeftAlt);
                Check(!fixture.Click() && fixture.Controller.IsOpen && fixture.Window.InitialRename=="Original", "Alt+click selects closest saved pin for rename");
                Action<string> stale=fixture.Window.RenameSave;
                fixture.Window.RenameSave("New name");
                Check(pin.m_name=="New name" && Minimap.instance.MarkerDestroys==1 && Minimap.instance.m_pinUpdateRequired,
                    "rename updates native name and invalidates marker");
                stale("Stale overwrite");
                Check(pin.m_name=="New name", "completed rename callback cannot run again");
                fixture.Frame(KeyCode.LeftAlt); fixture.Click();
                stale=fixture.Window.RenameSave;
                ++ZNet.instance.World; stale("Other world");
                Check(pin.m_name=="New name", "world changed before next tick rejects stale rename");
                fixture.Frame(); stale("Other world 2");
                Check(!fixture.Controller.IsOpen && pin.m_name=="New name", "world transition closes editor and invalidates callbacks");
                fixture.Frame(KeyCode.LeftControl, KeyCode.P); Action<string> oldPlace=fixture.Window.Place;
                Player.m_localPlayer=new Player { Id=888 }; oldPlace(Preset);
                Check(Minimap.instance.m_pins.Count==1, "character switch rejects stale place callback immediately");
            }
            using (var fixture = new Fixture())
            {
                var pin=Minimap.instance.AddPin(new Vector3(4,0,8),Minimap.PinType.Icon0,"Keep",true,false,0,Splatform.PlatformUserID.None);
                Minimap.instance.m_mode=Minimap.MapMode.Large; fixture.Frame(); fixture.Controller.Rename(pin);
                PinHistoryController.FailRemember=true; fixture.Window.RenameSave("Lost");
                Check(pin.m_name=="Keep" && fixture.Controller.IsOpen && fixture.Errors.Count==1, "rename persistence failure keeps original pin and editor");
                PinHistoryController.FailRemember=false; Minimap.instance.RemovePin(pin); fixture.Window.RenameSave("No longer exists");
                Check(pin.m_name=="Keep", "removed pin cannot be renamed through stale callback");
            }
        }

        private static void PersistenceAndLanguage()
        {
            using (var fixture = new Fixture())
            {
                fixture.Frame(KeyCode.LeftControl, KeyCode.P);
                PinPresetStore store=Field<PinPresetStore>(fixture.Controller,"store"); int original=store.Presets.Count;
                fixture.Window.Save(new PinPresetEdit { Name="My harbour", IconType=1, NameChanged=true });
                Check(store.Presets.Count==original+1, "controller save persists a custom preset");
                fixture.Window.Save(new PinPresetEdit { Id=Preset, Name="Ignored display", IconType=6, NameChanged=false });
                Check(store.Find(Preset).LocalizationKey=="Raspberries" && store.Find(Preset).Icon==6, "icon-only save retains localization binding");
                fixture.Window.Save(new PinPresetEdit { Id=Preset, Name="My berries", IconType=6, NameChanged=true });
                Check(store.Find(Preset).LocalizationKey=="" && store.Find(Preset).Name=="My berries", "explicit preset rename becomes literal");
                File.AppendAllText(store.FilePath," ");
                fixture.Window.Save(new PinPresetEdit { Name="Conflicting write",IconType=1,NameChanged=true });
                Check(store.Presets.Count==original+1 && fixture.Errors.Count==1 && fixture.Controller.IsOpen
                    && !String.IsNullOrEmpty(fixture.Window.Status), "external disk change fails safely without closing editor");
            }
            using (var fixture = new Fixture())
            {
                fixture.Frame(KeyCode.LeftControl, KeyCode.P); fixture.Window.Place(Preset);
                var pin=Minimap.instance.m_pins[0]; string original=pin.m_name;
                Check(original=="Raspberries", "placed pin stores readable native label");
                Localization.instance.Language="Russian"; fixture.Frame();
                Check(pin.m_name==original && fixture.Controller.DisplayName(pin)=="Малина" && pin.m_NamePinData.PinNameText.text=="Малина",
                    "language switch localizes caption without changing saved native name");
                Minimap.instance.m_mode=Minimap.MapMode.Large; fixture.Controller.Rename(pin); fixture.Window.RenameSave("Моя ферма");
                Localization.instance.Language="English"; fixture.Frame();
                Check(pin.m_name=="Моя ферма" && fixture.Controller.DisplayName(pin)=="Моя ферма", "custom renamed pin stays literal across languages");
                fixture.Frame(KeyCode.LeftControl, KeyCode.P); fixture.Window.IsEditing=true;
                Localization.instance.Language="Russian"; fixture.Frame();
                Check(fixture.Window.IsEditing && FindEntry(fixture.Window.Entries, Preset).Name=="Малина", "language refresh updates cached entries without ending editor");
            }
        }

        private static PinPresetEntryView FindEntry(IList<PinPresetEntryView> entries,string id)
        { foreach(var entry in entries) if(entry.Id==id) return entry; throw new Exception("Entry missing"); }
        private static bool Same(Vector3 a,Vector3 b) { return (a-b).sqrMagnitude<.00001f; }
        private static void Check(bool result,string message) { if(!result) throw new Exception("Quick pin controller regression: "+message); ++checks; }
        private static T Field<T>(object target,string name) { return (T)target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(target); }

        private sealed class Fixture : IDisposable
        {
            private readonly string path;
            public readonly List<Exception> Errors=new List<Exception>();
            public readonly Harmony Harmony=new Harmony();
            public readonly QuickPinController Controller;
            public PinPresetWindow Window { get { return Field<PinPresetWindow>(Controller,"window"); } }
            public Fixture()
            {
                path=Path.Combine(Path.GetTempPath(),"valheim-quickpin-host-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
                BepInEx.Paths.GameRootPath=path; GUIManager.ForeignBlocks=GUIManager.OwnBlocks=0;
                Input.Keys.Clear(); Time.unscaledTime=100; Minimap.TextFocused=false;
                Menu.Visible=InventoryGui.Visible=UnifiedPopup.Visible=global::Console.Visible=false;
                Chat.instance=new Chat(); TextInput.instance=null; EventSystem.current.currentSelectedGameObject=null;
                Localization.instance.Language="English"; PinHistoryController.FailRemember=false;
                Player.m_localPlayer=new Player(); Player.m_localPlayer.transform.position=new Vector3(1,2,3);
                ZNet.instance=new ZNet(); Minimap.instance=new Minimap();
                Controller=new QuickPinController(Harmony,new PinHistoryController(),typeof(Minimap).GetField("m_pins"),error=>Errors.Add(error));
                Controller.OpenShortcut = () => Input.GetKeyDown(KeyCode.P)
                    && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                    && !Input.GetKey(KeyCode.LeftAlt) && !Input.GetKey(KeyCode.RightAlt);
                Controller.PlaceModifier = () => Input.GetKey(KeyCode.LeftShift);
                Controller.RenameModifier = () => Input.GetKey(KeyCode.LeftAlt);
                Frame();
            }
            public void Frame(params KeyCode[] keys) { Input.Keys.Clear(); foreach(var key in keys) Input.Keys.Add(key); Time.unscaledTime+=1; Controller.Tick(true); }
            public bool Click() { return (bool)typeof(QuickPinController).GetMethod("BeforeClick",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{Minimap.instance}); }
            public bool DoubleClick() { return (bool)typeof(QuickPinController).GetMethod("BeforeDoubleClick",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,null); }
            public void Dispose()
            {
                Controller.Dispose();
                if(!Path.GetFullPath(path).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe test cleanup path");
                Directory.Delete(path,true);
            }
        }
    }
}
