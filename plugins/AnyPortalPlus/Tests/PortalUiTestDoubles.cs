// Used only by Test-Ui.ps1. Exercises the shipped panel with deterministic GUI objects.
using System;
using System.Collections.Generic;
namespace UnityEngine
{
    public class Object { public static void Destroy(Object value) { var go = value as GameObject; if (go != null) go.SetActive(false); } }
    public class Font : Object { }
    public class Sprite : Object { }
    public struct Vector2 { public float x,y; public Vector2(float x,float y){this.x=x;this.y=y;} public static Vector2 zero {get{return new Vector2();}} public static Vector2 one {get{return new Vector2(1,1);}} }
    public struct Vector3 { public float x,y,z; public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;} public static Vector3 zero{get{return new Vector3();}} public static Vector3 one{get{return new Vector3(1,1,1);}} public static Vector3 operator *(Vector3 v,float f){return new Vector3(v.x*f,v.y*f,v.z*f);} }
    public struct Color { public float r,g,b,a; public Color(float r,float g,float b,float a){this.r=r;this.g=g;this.b=b;this.a=a;} public static Color black{get{return new Color(0,0,0,1);}} public static Color white{get{return new Color(1,1,1,1);}} }
    public struct Rect { public float width,height; }
    public class Component : Object { public GameObject gameObject; public Transform transform{get{return gameObject.transform;}} public T GetComponent<T>() where T:Component{return gameObject.GetComponent<T>();} public T GetComponentInChildren<T>() where T:Component{return gameObject.GetComponentInChildren<T>();} }
    public class Transform : Component
    { public Transform parent; public Vector3 localScale=Vector3.one; public List<Transform> children=new List<Transform>(); public void SetParent(Transform p,bool keep){if(parent!=null)parent.children.Remove(this);parent=p;if(p!=null)p.children.Add(this);} public void SetAsLastSibling(){} public bool IsChildOf(Transform p){return this==p||(parent!=null&&parent.IsChildOf(p));} public Transform Find(string path){string[] parts=path.Split('/');Transform cur=this;foreach(string part in parts){cur=cur.children.Find(t=>t.gameObject.name==part);if(cur==null)return null;}return cur;} }
    public class RectTransform : Transform { public Vector2 anchorMin,anchorMax,pivot,anchoredPosition,sizeDelta,offsetMin,offsetMax; public Rect rect{get{return new Rect{width=sizeDelta.x,height=sizeDelta.y};}} }
    public class GameObject : Object
    { public string name;public int layer;public bool activeSelf=true;public Transform transform;readonly List<Component> components=new List<Component>();public bool activeInHierarchy{get{return activeSelf&&(transform.parent==null||transform.parent.gameObject.activeInHierarchy);}} public GameObject(string name,params Type[] types){this.name=name;transform=new RectTransform{gameObject=this};components.Add(transform);foreach(Type type in types)if(type!=typeof(RectTransform))AddComponent(type);}public void SetActive(bool value){activeSelf=value;}public T AddComponent<T>()where T:Component,new(){return(T)AddComponent(typeof(T));}public Component AddComponent(Type type){var c=(Component)Activator.CreateInstance(type);c.gameObject=this;components.Add(c);return c;}public T GetComponent<T>()where T:Component{foreach(Component c in components)if(c is T)return(T)c;return null;}public T GetComponentInChildren<T>()where T:Component{T c=GetComponent<T>();if(c!=null)return c;foreach(Transform child in transform.children){c=child.gameObject.GetComponentInChildren<T>();if(c!=null)return c;}return null;} }
    public class CanvasGroup : Component {public bool interactable=true,blocksRaycasts=true;}
    public static class Mathf {public static float Min(float a,float b){return Math.Min(a,b);}public static float Max(float a,float b){return Math.Max(a,b);}}
    public static class ColorUtility {public static bool TryParseHtmlString(string s,out Color c){c=Color.white;return s!=null&&s.StartsWith("#");}}
    public enum KeyCode {None,UpArrow,DownArrow,Escape,U,LeftControl}
    public static class Input {public static readonly HashSet<KeyCode> Down=new HashSet<KeyCode>();public static bool GetKeyDown(KeyCode key){return Down.Contains(key);}}
    public static class Time {public static float unscaledTime;public static int frameCount;}
    public enum TextAnchor {MiddleLeft,MiddleRight,MiddleCenter}
    public enum HorizontalWrapMode {Wrap,Overflow}
    public enum VerticalWrapMode {Truncate,Overflow}
}
namespace UnityEngine.EventSystems
{ public sealed class EventSystem {public static EventSystem current=new EventSystem();public UnityEngine.GameObject currentSelectedGameObject;public void SetSelectedGameObject(UnityEngine.GameObject go){currentSelectedGameObject=go;}} }
namespace UnityEngine.UI
{
    public class Event {readonly List<Action> callbacks=new List<Action>();public void AddListener(Action a){callbacks.Add(a);}public void Invoke(){foreach(Action a in callbacks.ToArray())a();} }
    public class Event<T> {readonly List<Action<T>> callbacks=new List<Action<T>>();public void AddListener(Action<T>a){callbacks.Add(a);}public void Invoke(T value){foreach(Action<T>a in callbacks.ToArray())a(value);} }
    public class Graphic:UnityEngine.Component {public bool raycastTarget;public UnityEngine.Color color;}
    public class Text:Graphic {public string text="";public UnityEngine.Font font;public int fontSize,resizeTextMinSize,resizeTextMaxSize;public bool supportRichText,resizeTextForBestFit;public UnityEngine.TextAnchor alignment;public UnityEngine.HorizontalWrapMode horizontalOverflow;public UnityEngine.VerticalWrapMode verticalOverflow;public UnityEngine.RectTransform rectTransform{get{return(UnityEngine.RectTransform)transform;}}}
    public class Image:Graphic {public UnityEngine.Sprite sprite;public bool preserveAspect;}
    public struct ColorBlock {public UnityEngine.Color normalColor;}
    public class Button:UnityEngine.Component {public bool interactable=true;public ColorBlock colors;public Event onClick=new Event();}
    public class InputField:UnityEngine.Component {public enum ContentType{Standard}public int characterLimit;public Text textComponent;public Graphic placeholder;public bool shouldActivateOnSelect,isFocused;public int FocusCalls;string current="";public Event<string>onValueChanged=new Event<string>();public string text{get{return current;}set{current=value;onValueChanged.Invoke(value);}}public void ActivateInputField(){isFocused=true;FocusCalls++;}}
    public class Toggle:UnityEngine.Component {bool current;public Event<bool>onValueChanged=new Event<bool>();public bool isOn{get{return current;}set{current=value;onValueChanged.Invoke(value);}}}
    public class Dropdown:UnityEngine.Component {public class OptionData{public string text;public UnityEngine.Sprite image;public OptionData(string text,UnityEngine.Sprite image=null){this.text=text;this.image=image;}}public List<OptionData>options=new List<OptionData>();public Text captionText,itemText;public Image captionImage,itemImage;public UnityEngine.RectTransform template;public Event<int>onValueChanged=new Event<int>();int current;public int value{get{return current;}set{current=value;onValueChanged.Invoke(value);}}public void ClearOptions(){options.Clear();}public void AddOptions(List<string>list){foreach(string item in list)options.Add(new OptionData(item));}public void RefreshShownValue(){}public void Hide(){UnityEngine.Transform list=transform.Find("Dropdown List");if(list!=null){list.SetParent(null,false);list.gameObject.SetActive(false);}}}
}
namespace BepInEx.Configuration
{ public class ConfigEntry<T>{public T Value;public ConfigEntry(T value){Value=value;}}public struct KeyboardShortcut{public UnityEngine.KeyCode MainKey;public KeyboardShortcut(UnityEngine.KeyCode key){MainKey=key;}public override string ToString(){return MainKey.ToString();}} }
namespace Jotunn {public static class Dummy{} }
namespace Jotunn.Configs
{public class ButtonConfig{public string Name,HintToken;public bool ActiveInGUI,ActiveInCustomGUI,BlockOtherInputs;public BepInEx.Configuration.ConfigEntry<BepInEx.Configuration.KeyboardShortcut>ShortcutConfig;public Jotunn.Managers.InputManager.GamepadButton GamepadButton;public float RepeatDelay;}}
namespace Jotunn.Managers
{
    using UnityEngine;using UnityEngine.UI;
    public class InputManager{public enum GamepadButton{DPadUp,DPadDown}public static InputManager Instance=new InputManager();public List<Jotunn.Configs.ButtonConfig>Buttons=new List<Jotunn.Configs.ButtonConfig>();public void AddButton(string id,Jotunn.Configs.ButtonConfig cfg){Buttons.Add(cfg);}}
    public class GUIManager
    {
        public static GUIManager Instance=new GUIManager();public static GameObject CustomGUIFront=Canvas();public static int UILayer=5,Requests=3;public Font AveriaSerif=new Font(),AveriaSerifBold=new Font();public Color ValheimOrange=Color.white,ValheimBeige=Color.white;public static bool IsHeadless(){return false;}public static void BlockInput(bool value){Requests+=value?1:-1;}
        static GameObject Canvas(){var g=new GameObject("Canvas");((RectTransform)g.transform).sizeDelta=new Vector2(1280,720);return g;}
        static GameObject At(string name,Transform parent,Vector2 pos,float width,float height){var g=new GameObject(name);g.transform.SetParent(parent,false);var r=(RectTransform)g.transform;r.anchoredPosition=pos;r.sizeDelta=new Vector2(width,height);return g;}
        public GameObject CreateWoodpanel(Transform p,Vector2 amin,Vector2 amax,Vector2 pos,float w,float h,bool drag){return At("Wood",p,pos,w,h);}
        public GameObject CreateText(string s,Transform p,Vector2 amin,Vector2 amax,Vector2 pos,Font f,int size,Color c,bool outline,Color oc,float w,float h,bool fitter){var g=At("Text",p,pos,w,h);var t=g.AddComponent<Text>();t.text=s;t.font=f;t.fontSize=size;t.color=c;return g;}
        Text ChildText(Transform p,string name){return CreateText("",p,new Vector2(),new Vector2(),new Vector2(),AveriaSerif,18,Color.white,false,Color.black,100,30,false).GetComponent<Text>();}
        public GameObject CreateButton(string s,Transform p,Vector2 amin,Vector2 amax,Vector2 pos,float w,float h){var g=At("Button",p,pos,w,h);g.AddComponent<Button>();var t=ChildText(g.transform,"Label");t.text=s;return g;}
        public GameObject CreateInputField(Transform p,Vector2 amin,Vector2 amax,Vector2 pos,InputField.ContentType ct,string hint,int size,float w,float h){var g=At("Input",p,pos,w,h);var f=g.AddComponent<InputField>();f.textComponent=ChildText(g.transform,"Text");f.placeholder=ChildText(g.transform,"Placeholder");return g;}
        public GameObject CreateDropDown(Transform p,Vector2 amin,Vector2 amax,Vector2 pos,int size,float w,float h){var g=At("Dropdown",p,pos,w,h);var d=g.AddComponent<Dropdown>();d.captionText=ChildText(g.transform,"Caption");var temp=At("Template",g.transform,new Vector2(),w,260);d.template=(RectTransform)temp.transform;var view=At("Viewport",temp.transform,new Vector2(),w,260);var content=At("Content",view.transform,new Vector2(),w,260);var item=At("Item",content.transform,new Vector2(),w,30);d.itemText=ChildText(item.transform,"ItemLabel");return g;}
        public GameObject CreateToggle(Transform p,float w,float h){var g=At("Toggle",p,new Vector2(),w,h);g.AddComponent<Toggle>();ChildText(g.transform,"Label");return g;}
    }
}
public struct ZDOID{public long User;public uint Number;public ZDOID(long u,uint n){User=u;Number=n;}public static ZDOID None{get{return new ZDOID();}}public bool IsNone(){return User==0&&Number==0;}public override string ToString(){return User+":"+Number;}public static bool operator==(ZDOID a,ZDOID b){return a.User==b.User&&a.Number==b.Number;}public static bool operator!=(ZDOID a,ZDOID b){return !(a==b);}public override bool Equals(object o){return o is ZDOID&&this==(ZDOID)o;}public override int GetHashCode(){return User.GetHashCode()^(int)Number;}}
public class Player{public static Player m_localPlayer=new Player();public bool Dead,Teleporting,Cutscene;public bool IsDead(){return Dead;}public bool IsTeleporting(){return Teleporting;}public bool InCutscene(){return Cutscene;}}
public class ZNet{public static ZNet instance=new ZNet();public long World=777;public long GetWorldUID(){return World;}}
public class ZInput{public static ZInput instance=new ZInput();public static HashSet<string>Down=new HashSet<string>(),Up=new HashSet<string>();public static bool GetButtonDown(string key){return Down.Contains(key);}public static bool GetButtonUp(string key){return Up.Contains(key);}public static void ResetButtonStatus(string key){Down.Remove(key);Up.Remove(key);}}
public class Game{public static Game instance=new Game();public bool m_shuttingDown;}
public class Minimap{public static Minimap instance=new Minimap();}
public class ZoneSystem{public static ZoneSystem instance=new ZoneSystem();public bool NoMap;public bool GetGlobalKey(string key){return key=="nomap"&&NoMap;}}
public class UnifiedPopup{public static bool Visible;public static bool IsVisible(){return Visible;}}
public class UIGroupHandler:UnityEngine.Component{}
public class Heightmap{public enum Biome{None=0,Meadows=1,Swamp=2,Mountain=4,BlackForest=8,Plains=16,AshLands=32,DeepNorth=64,Ocean=256,Mistlands=512}}
public class Localization{public static Localization instance=new Localization();public string Language="English";public string GetSelectedLanguage(){return Language;}public string Localize(string s){return s;}}
namespace XPortal
{
    public static class Mod{public static class Info{public const string Name="AnyPortal+",GUID="yay.spikehimself.xportal";}}
    public static class Environment{public static bool IsHeadless;}
    public static class Log{public static List<object>Errors=new List<object>();public static void Error(object e){Errors.Add(e);}public static void Warning(object e){} }
    public class KnownPortal{public ZDOID Id,Target;public string Name,Colour="#fff";public UnityEngine.Vector3 Location;public long CreatedUtcTicks;public int Biome,Icon=-1;public bool IsDefaultPortal;}
    public class KnownPortalsManager{public static KnownPortalsManager Instance=new KnownPortalsManager();public List<KnownPortal>Portals=new List<KnownPortal>();public List<KnownPortal>GetList(){return new List<KnownPortal>(Portals);}public bool ContainsId(ZDOID id){return Portals.Exists(p=>p.Id==id);}public KnownPortal GetKnownPortalById(ZDOID id){return Portals.Find(p=>p.Id==id);} }
    public class XPortalConfig{public static XPortalConfig Instance=new XPortalConfig();public class ConfigSettings{public bool DisplayPortalColour,HidePortalDistance,PingMapDisabled;public BepInEx.Configuration.ConfigEntry<BepInEx.Configuration.KeyboardShortcut>PreviousPortalShortcut=new BepInEx.Configuration.ConfigEntry<BepInEx.Configuration.KeyboardShortcut>(new BepInEx.Configuration.KeyboardShortcut(UnityEngine.KeyCode.UpArrow)),NextPortalShortcut=new BepInEx.Configuration.ConfigEntry<BepInEx.Configuration.KeyboardShortcut>(new BepInEx.Configuration.KeyboardShortcut(UnityEngine.KeyCode.DownArrow));}public ConfigSettings Local=new ConfigSettings(),Server=new ConfigSettings();}
    public static class XPortal{public static int Submits,Pings;public static ZDOID LastTarget;public static int LastIcon;public static void PortalInfoSubmitted(KnownPortal p,string n,ZDOID target,bool def,int icon){Submits++;LastTarget=target;LastIcon=icon;}public static void PingMapButtonClicked(ZDOID id){Pings++;}}
    public class QueuedAction{static readonly List<Action>pending=new List<Action>();public static void Queue(Action<bool,object>action,int delay=2,object state=null){pending.Add(()=>action(false,state));}public static void Flush(){var copy=pending.ToArray();pending.Clear();foreach(Action a in copy)a();}}
}
namespace XPortal.Plus
{public static class PlusMapMarkers{public static readonly UnityEngine.Sprite Icon=new UnityEngine.Sprite();public static int Marks,Cleanups;public static Action OnCleanup;public static UnityEngine.Sprite GetIconSprite(int icon){return icon<0?null:Icon;}public static bool AddOrUpdate(global::XPortal.KnownPortal p){Marks++;return true;}public static void CleanupWithConfirmation(){Cleanups++;if(OnCleanup!=null)OnCleanup();}}}
