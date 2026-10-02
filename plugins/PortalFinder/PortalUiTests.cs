using System;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using ValheimModPack.PortalFinder;

public static class PortalUiTests
{
    private static int checks;
    private static void Check(bool passed,string name) { checks++; if(!passed) throw new Exception(name); }
    private static readonly BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    private static object Get(Plugin plugin,string field) { return typeof(Plugin).GetField(field,Private).GetValue(plugin); }
    private static void Call(Plugin plugin,string method) { typeof(Plugin).GetMethod(method,Private).Invoke(plugin,null); }
    private static bool Prefix(string nested,Minimap map)
    { return (bool)typeof(Plugin).GetNestedType(nested,BindingFlags.NonPublic).GetMethod("Prefix",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[] { map }); }
    private static void Click(Plugin plugin,string button)
    { var obj=(GameObject)Get(plugin,button); EventSystem.current.currentSelectedGameObject=obj; obj.GetComponent<Button>().onClick.Invoke(); }
    private static void Tick(Plugin plugin) { Call(plugin,"Update"); Input.Down.Clear(); }
    private static string Caption(Plugin plugin) { return ((Text)Get(plugin,"caption")).text; }
    private static string PointLabel(Plugin plugin) { return ((GameObject)Get(plugin,"pointButton")).GetComponentInChildren<Text>().text; }
    private static string OwnLabel(Plugin plugin) { return ((GameObject)Get(plugin,"ownButton")).GetComponentInChildren<Text>().text; }
    private static bool NativeInput(string patch)
    {
        object[] args={true};
        return (bool)typeof(Plugin).GetNestedType(patch,BindingFlags.NonPublic).GetMethod("Prefix",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,args);
    }
    public static void Main()
    {
        var nativeOrdered=new KeyboardShortcut(KeyCode.J,KeyCode.RightControl,KeyCode.RightShift);
        var storedModifiers=new System.Collections.Generic.List<KeyCode>(nativeOrdered.Modifiers);
        Check(storedModifiers[0]==KeyCode.RightShift && storedModifiers[1]==KeyCode.RightControl,
            "shortcut boundary stores Shift before Ctrl like the real BepInEx constructor");
        Check(Shortcut.Label(nativeOrdered)=="Ctrl+Shift+J","Ctrl+Shift display order survives numeric modifier sorting");
        Check(Shortcut.Label(new KeyboardShortcut(KeyCode.J,KeyCode.RightCommand,KeyCode.RightAlt,KeyCode.LeftControl,KeyCode.RightShift))=="Ctrl+Shift+Alt+Cmd+J"
            && Shortcut.Label(new KeyboardShortcut(KeyCode.J,KeyCode.LeftShift,KeyCode.LeftCommand,KeyCode.LeftAlt,KeyCode.RightControl))=="Ctrl+Shift+Alt+Cmd+J",
            "permuted right and left modifier families produce the same familiar display order");
        Check(Shortcut.Label(new KeyboardShortcut(KeyCode.G,KeyCode.F,KeyCode.RightCommand,KeyCode.Alpha7,KeyCode.RightAlt,KeyCode.RightControl,KeyCode.RightShift))=="Ctrl+Shift+Alt+Cmd+7+F+G",
            "other required keys follow modifier families in deterministic numeric order");
        var unchanged=new System.Collections.Generic.List<KeyCode>(nativeOrdered.Modifiers);
        Check(unchanged.Count==storedModifiers.Count && unchanged[0]==storedModifiers[0] && unchanged[1]==storedModifiers[1],
            "display ordering does not mutate stored modifier sides or their matching order");
        var plugin=new Plugin(); var player=new Player(); var map=new Minimap { Offset=new Vector3(1000,0,2000) };
        var quickPlugin=new ValheimModPack.PinRemoval.Plugin();
        BepInEx.Bootstrap.Chainloader.PluginInfos["valheimmodpack.confirmpinremoval"]=new BepInEx.PluginInfo { Instance=quickPlugin };
        Player.m_localPlayer=player; Minimap.instance=map; ZNet.instance=new ZNet();
        ZDOMan.instance.Portals.Add(new ZDO { m_uid=new ZDOID(1),Name="<b>Home</b>",Position=new Vector3(10,300,0) });
        ZDOMan.instance.Portals.Add(new ZDO { m_uid=new ZDOID(2),Name="Point portal",Position=new Vector3(1005,0,2005) });
        var saved=map.AddPin(new Vector3(99,0,99),Minimap.PinType.Icon4,"Saved map pin",true,false);
        try
        {
            Call(plugin,"Awake"); Tick(plugin);
            Check((bool)Get(plugin,"ready") && plugin.Logger.Errors.Count==0,"production plugin initializes against test boundaries");
            Check(((GameObject)Get(plugin,"ownButton")).activeSelf && PointLabel(plugin)=="Nearest portal to a point\n[Ctrl+Shift+J]"
                && OwnLabel(plugin)=="Nearest portal to me\n[Ctrl+J]","initial two-line map labels name portals explicitly and show both working defaults");
            var ownObject=(GameObject)Get(plugin,"ownButton"); var pointObject=(GameObject)Get(plugin,"pointButton");
            var resultObject=(GameObject)Get(plugin,"captionObject");
            Check(ownObject.TestWidth==420 && ownObject.TestHeight==50 && ownObject.TestPosition.x==-220 && ownObject.TestPosition.y==-185
                && pointObject.TestWidth==420 && pointObject.TestHeight==50 && pointObject.TestPosition.x==-220 && pointObject.TestPosition.y==-245
                && resultObject.TestWidth==420 && resultObject.TestHeight==96 && resultObject.TestPosition.y==-320,
                "portal actions and result use the shared nonoverlapping right-hand map rows");
            foreach(var button in new[]{ownObject,pointObject})
            {
                var label=button.GetComponentInChildren<Text>();
                Check(!label.supportRichText && !label.raycastTarget && label.alignment==TextAnchor.MiddleCenter
                    && label.horizontalOverflow==HorizontalWrapMode.Wrap && label.verticalOverflow==VerticalWrapMode.Truncate
                    && label.fontSize==17 && label.resizeTextForBestFit && label.resizeTextMinSize==14 && label.resizeTextMaxSize==17,
                    "two-line launcher font fits long current combinations without dropping below fourteen points");
            }
            Check(!((Text)Get(plugin,"caption")).supportRichText,"portal result names cannot execute rich text");
            Check(Prefix("PointClickPatch",map) && NativeInput("DownPatch") && NativeInput("HeldPatch"),"idle shortcuts preserve ordinary map and native input");
            Input.Held.UnionWith(new[]{KeyCode.J,KeyCode.RightControl}); Input.Down.Add(KeyCode.J); Tick(plugin);
            Check(Get(plugin,"marker")!=null && Caption(plugin).Contains("from you"),"default Ctrl+J searches from player position with either control side");
            Input.Held.Add(KeyCode.RightShift); Input.Down.Add(KeyCode.J); Tick(plugin);
            Check((bool)Get(plugin,"armed") && PointLabel(plugin)=="Cancel portal point selection\n[Ctrl+Shift+J]",
                "Ctrl+Shift+J arms point selection without also triggering Ctrl+J");
            Input.Down.Add(KeyCode.J); Tick(plugin);
            Check(!(bool)Get(plugin,"armed") && PointLabel(plugin)=="Nearest portal to a point\n[Ctrl+Shift+J]","the same point shortcut cancels with explicit portal wording");
            Input.Held.Clear();
            int reads=ZDOMan.instance.Reads; Tick(plugin); Tick(plugin);
            Check(ZDOMan.instance.Reads==reads,"idle UI does not poll world portal records");
            Click(plugin,"pointButton"); Check((bool)Get(plugin,"armed") && Caption(plugin)=="Left-click a point to find its nearest portal.","point button arms explicit nearest portal selection");
            ZInput.pointerPosition=new Vector3(5,5,0);
            Check(!Prefix("PointClickPatch",map),"armed map selection consumes one native left click");
            var marker=(Minimap.PinData)Get(plugin,"marker");
            Check(marker!=null && marker.m_name=="Point portal" && !marker.m_save,"screen point conversion chooses the target portal and creates a temporary pin");
            Check(map.ShownPoint.x==1005 && map.ShownPoint.z==2005 && Caption(plugin).StartsWith("Portal: Point portal\n") && Caption(plugin).Contains("from the selected point"),"result names the portal explicitly, is centered and distance uses the selected point");
            Check(!(bool)Get(plugin,"armed") && Prefix("PointClickPatch",map),"following ordinary click is restored");
            bool doubleAllowed=Prefix("DoubleClickPatch",map);
            if(doubleAllowed) { map.NativeDoubleClicks++; map.AddPin(new Vector3(),Minimap.PinType.Icon4,"Native double pin",true,false); }
            Check(!doubleAllowed && map.NativeDoubleClicks==0 && map.Pins.Count==2,"selection suppresses native double-click pin creation");
            Time.unscaledTime+=0.6f; Check(Prefix("DoubleClickPatch",map),"ordinary double-click returns after suppression expires");
            map.m_mode=Minimap.MapMode.Small; Tick(plugin);
            Check(map.Pins.Count==1 && map.Pins[0]==saved && Get(plugin,"marker")==null,"map close removes only the owned temporary pin");
            Check(Caption(plugin)=="" && PointLabel(plugin)=="Nearest portal to a point\n[Ctrl+Shift+J]","map close clears cached result and armed labels");
            map.m_mode=Minimap.MapMode.Large; Tick(plugin);
            Check(Caption(plugin)=="" && ((GameObject)Get(plugin,"ownButton")).activeSelf,"reopen has no stale result");
            Click(plugin,"ownButton");
            Check(((Minimap.PinData)Get(plugin,"marker")).m_name=="‹b›Home‹/b›" && Caption(plugin).Contains("10 m from you"),"own-position button uses planar distance and safe name rendering");
            ZNet.instance.World=2; Tick(plugin);
            Check(Get(plugin,"marker")==null && Get(plugin,"result")==null && Caption(plugin)=="" && map.Pins.Count==1,"world transition clears old view without touching saved pins");
            Click(plugin,"pointButton"); TextInput.Visible=true; Tick(plugin);
            Check(!(bool)Get(plugin,"armed") && Prefix("PointClickPatch",map) && !((GameObject)Get(plugin,"ownButton")).activeSelf,"modal input cancels selection and protects map clicks");
            TextInput.Visible=false; Tick(plugin); Click(plugin,"pointButton");
            var field=new GameObject(); field.AddComponent<InputField>().isFocused=true; EventSystem.current.currentSelectedGameObject=field; Tick(plugin);
            Check(!(bool)Get(plugin,"armed") && Prefix("PointClickPatch",map),"focused text fields cancel and protect map selection");
            EventSystem.current.currentSelectedGameObject=null; Tick(plugin); Click(plugin,"pointButton");
            Input.Down.Add(KeyCode.Escape); Tick(plugin);
            Check(!(bool)Get(plugin,"armed") && Caption(plugin)=="" && PointLabel(plugin)=="Nearest portal to a point\n[Ctrl+Shift+J]","Escape resets picker state and visible labels");
            Click(plugin,"pointButton"); quickPlugin.quick.IsBusy=true;
            Check(!TextInput.IsVisible() && Prefix("PointClickPatch",map),"a quick pin awaiting its click blocks finder consumption even with its window closed");
            Tick(plugin);
            Check(!(bool)Get(plugin,"armed") && !((GameObject)Get(plugin,"ownButton")).activeSelf
                && !((GameObject)Get(plugin,"pointButton")).activeSelf,"quick pin ownership cancels finder point selection and hides both actions");
            Check(Caption(plugin)=="" && map.Pins.Count==1 && map.Pins.Contains(saved),"quick pin coordination preserves saved pins and clears picker instruction");
            quickPlugin.quick.IsBusy=false; Tick(plugin);
            Check(((GameObject)Get(plugin,"ownButton")).activeSelf && PointLabel(plugin)=="Nearest portal to a point\n[Ctrl+Shift+J]"
                && !(bool)Get(plugin,"armed"),"finder returns normally after quick pin releases its pending click");
            ZDOMan.instance.Portals[0].Name=""; Click(plugin,"ownButton");
            marker=(Minimap.PinData)Get(plugin,"marker"); Check(marker.m_name=="Unnamed portal","empty portal name uses current UI language");
            int centers=map.Centers; Localization.instance.Language="Russian"; Tick(plugin);
            var translated=(Minimap.PinData)Get(plugin,"marker");
            Check(translated!=marker && translated.m_name=="Портал без названия" && !translated.m_save,"language change refreshes the ephemeral map label");
            Check(map.Pins.Count==2 && map.Pins.Contains(saved) && map.Centers==centers && Caption(plugin).StartsWith("Портал: Портал без названия\n") && Caption(plugin).Contains("от тебя"),"language refresh preserves saved pins and map position and explicitly names the portal");
            var playerShortcut=(ConfigEntry<KeyboardShortcut>)Get(plugin,"nearMe");
            var pointShortcut=(ConfigEntry<KeyboardShortcut>)Get(plugin,"nearPoint");
            playerShortcut.Value=new KeyboardShortcut(KeyCode.Alpha7,KeyCode.RightControl,KeyCode.LeftControl,KeyCode.RightShift,KeyCode.RightAlt,KeyCode.RightCommand);
            pointShortcut.Value=new KeyboardShortcut(KeyCode.KeypadEnter,KeyCode.RightControl,KeyCode.RightAlt);
            int bindingReads=ZDOMan.instance.Reads; marker=(Minimap.PinData)Get(plugin,"marker"); Tick(plugin);
            Check(OwnLabel(plugin)=="Ближайший портал ко мне\n[Ctrl+Shift+Alt+Cmd+7]" && PointLabel(plugin)=="Ближайший портал к точке\n[Ctrl+Alt+KeypadEnter]",
                "rebindings update both visible labels immediately without a language change; modifier sides normalize and duplicates collapse");
            Check(ZDOMan.instance.Reads==bindingReads && map.Centers==centers && Get(plugin,"marker")==marker,
                "updating button hints does not repeat portal search, recenter the map or replace its result marker");
            Click(plugin,"pointButton");
            Check(PointLabel(plugin)=="Отменить выбор точки портала\n[Ctrl+Alt+KeypadEnter]" && Caption(plugin)=="Выбери ЛКМ точку для поиска ближайшего портала.","armed label keeps the same current point binding and the localized hint explains nearest portal search");
            pointShortcut.Value=new KeyboardShortcut(KeyCode.None); playerShortcut.Value=new KeyboardShortcut(KeyCode.None); Tick(plugin);
            Check(PointLabel(plugin)=="Отменить выбор точки портала\nКлавиша не назначена" && OwnLabel(plugin)=="Ближайший портал ко мне\nКлавиша не назначена",
                "None is shown explicitly while preserving mouse buttons and current picker state");
            Click(plugin,"pointButton"); Click(plugin,"ownButton");
            Check(!(bool)Get(plugin,"armed") && Get(plugin,"marker")!=null,"unbound portal actions remain available through their map buttons");
            Localization.instance.Language="English"; Tick(plugin);
            Check(OwnLabel(plugin)=="Nearest portal to me\nUnbound" && PointLabel(plugin)=="Nearest portal to a point\nUnbound",
                "unassigned hint is localized on language switch");
            Localization.instance.Language="Russian"; Tick(plugin);
            ((ConfigEntry<KeyboardShortcut>)Get(plugin,"nearMe")).Value=new KeyboardShortcut(KeyCode.F,KeyCode.LeftControl);
            Input.Held.Add(KeyCode.F); Input.Held.Add(KeyCode.RightControl); Input.Down.Add(KeyCode.F);
            Tick(plugin); Check(!NativeInput("DownPatch") && !NativeInput("HeldPatch"),"rebound shortcut consumes native input and accepts opposite modifier side");
            Input.Held.Clear(); Check(NativeInput("DownPatch") && NativeInput("HeldPatch"),"native input restores after shortcut release");
            plugin.enabled=false; Call(plugin,"OnDisable");
            Check(Get(plugin,"marker")==null && map.Pins.Count==1 && !((GameObject)Get(plugin,"pointButton")).activeSelf,"disable removes the owned marker and hides actions");
            Check(Prefix("PointClickPatch",map) && Prefix("DoubleClickPatch",map),"disabled plugin preserves native map handlers");
            Check(plugin.Logger.Errors.Count==0,"integration sequence reports no plugin errors");
        }
        finally { Call(plugin,"OnDestroy"); }
        Check(Prefix("PointClickPatch",map),"destroy clears static handler ownership");
        System.Console.WriteLine("PASS: "+checks+" production Portal Finder UI/controller assertions; no Unity execution or Harmony detours.");
    }
}
