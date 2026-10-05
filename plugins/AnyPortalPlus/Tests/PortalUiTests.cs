using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;
using XPortal;
using XPortal.UI;
using XPortal.Plus;

internal static class PortalUiTests
{
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new InvalidOperationException(message);}
    static object Get(object o,string field){return o.GetType().GetField(field,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(o);}
    static void Set(object o,string field,object value){o.GetType().GetField(field,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(o,value);}
    static void Call(object o,string method,params object[]args){o.GetType().GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(o,args);}
    static KnownPortal Portal(uint id,string name,int biome,int icon=-1){return new KnownPortal{Id=new ZDOID(1,id),Name=name,Location=new Vector3(id*10,0,0),Biome=biome,Icon=icon};}
    public static int Main()
    {
        var panel=(PortalConfigurationPanel)Activator.CreateInstance(typeof(PortalConfigurationPanel),true);
        var current=Portal(1,"Current",1);var selected=Portal(2,"Selected",8,6);current.Target=selected.Id;
        KnownPortalsManager.Instance.Portals.Add(current);KnownPortalsManager.Instance.Portals.Add(selected);
        for(uint i=3;i<=25;i++)KnownPortalsManager.Instance.Portals.Add(Portal(i,"Portal "+i,(i<16?8:16),i==3?0:-1));
        try
        {
            panel.AddInputs();panel.AddInputs();
            Check(InputManager.Instance.Buttons.Count==2,"navigation registration is idempotent");
            Check(ReferenceEquals(InputManager.Instance.Buttons[0].ShortcutConfig,XPortalConfig.Instance.Local.PreviousPortalShortcut),"previous key has the actual editable config backing");
            Check(ReferenceEquals(InputManager.Instance.Buttons[1].ShortcutConfig,XPortalConfig.Instance.Local.NextPortalShortcut),"next key has the actual editable config backing");
            panel.ConfigurePortal(current);Check(panel.IsActive()&&GUIManager.Requests==4,"opening owns one input lease");panel.Show();Check(GUIManager.Requests==4,"duplicate show is idempotent");
            ((Toggle)Get(panel,"groupByBiomeToggle")).isOn=false; // Explicit plain-list fixture; Unity's default toggle starts checked.
            var rowButtons=(Button[])Get(panel,"rows");var icons=(Image[])Get(panel,"rowIcons");var search=(InputField)Get(panel,"searchInputField");var sort=(Dropdown)Get(panel,"sortDropdown");var chooser=(Dropdown)Get(panel,"iconDropdown");
            Check(rowButtons.Length==10,"bounded reusable portal rows");Check(chooser.options.Count==6&&chooser.value==0&&chooser.options[0].image==null,"default icon is absent and five native choices are available");
            Check(chooser.itemImage!=null&&chooser.captionImage!=null&&chooser.options[5].image!=null,"icon dropdown renders images in options and current caption");
            Check((ZDOID)Get(panel,"selectedTargetId")==selected.Id,"existing destination kept on open");
            search.text="No matches";Check((ZDOID)Get(panel,"selectedTargetId")==selected.Id,"search hiding selection preserves its identity");
            Check(((Text)Get(panel,"emptyLabel")).gameObject.activeSelf,"empty search has explicit feedback");Check(((Button)Get(panel,"applyButton")).interactable,"valid hidden selection can still be applied");
            Check(XPortal.XPortal.Submits==0&&KnownPortalsManager.Instance.Portals.Count==25,"filtering never writes portal connections or registry");
            search.text="Selected";Check(rowButtons[0].gameObject.activeSelf&&!rowButtons[1].gameObject.activeSelf,"filter returns only matching portal");Check(icons[0].gameObject.activeSelf&&icons[0].sprite==PlusMapMarkers.Icon,"selected portal icon is shown separately from name");
            search.text="";sort.value=1;((Button)Get(panel,"directionButton")).onClick.Invoke();
            Check((ZDOID)Get(panel,"selectedTargetId")==selected.Id&&XPortal.XPortal.Submits==0,"sort direction and basis preserve destination without sending writes");
            ((Toggle)Get(panel,"groupByBiomeToggle")).isOn=true;
            var display=(IList)Get(panel,"displayRows");
            for(int i=0;i<display.Count;i++){object row=display[i];object item=Get(row,"Portal");bool spacer=(bool)Get(row,"Spacer");if(i%10==0&&!spacer)Check(item==null,"a grouped page starts with its biome heading");if(i%10==9&&!spacer)Check(item!=null,"no biome heading is orphaned at page bottom");}
            ((Button)Get(panel,"nextButton")).onClick.Invoke();Check((int)Get(panel,"page")==1,"page next advances");
            search.text="Portal 3";Check((int)Get(panel,"page")==0&&!((Button)Get(panel,"nextButton")).interactable,"new search resets and clamps pagination");
            Check(!((Image[])Get(panel,"rowIcons"))[2].gameObject.activeInHierarchy,"unused page rows cannot retain visible icons");
            search.text="Selected";KnownPortalsManager.Instance.Portals.Remove(selected);Call(panel,"RefreshRegistry",true);
            Check((ZDOID)Get(panel,"selectedTargetId")==selected.Id&&!((Button)Get(panel,"applyButton")).interactable,"missing target remains explicit and disables Apply");
            ((Button)Get(panel,"noneButton")).onClick.Invoke();Check((ZDOID)Get(panel,"selectedTargetId")==ZDOID.None&&((Button)Get(panel,"applyButton")).interactable,"None is only changed by explicit action");
            chooser.value=5;Check((int)Get(panel,"selectedIcon")==6&&current.Icon==-1,"icon edit is staged rather than mutating the registry");
            Localization.instance.Language="Russian";Call(panel,"UpdateLocalization");Check(((Text)Get(panel,"nameLabel")).text=="Имя портала"&&chooser.value==5,"language switch keeps current icon selection");
            Check(((Button)Get(panel,"directionButton")).GetComponentInChildren<Text>().text.Contains("Убывание"),"sort direction remains displayed after language switch");
            XPortalConfig.Instance.Local.PreviousPortalShortcut.Value=new BepInEx.Configuration.KeyboardShortcut(KeyCode.U);Call(panel,"UpdateNavigationHint");Check(((Text)Get(panel,"navigationHint")).text.Contains("U"),"navigation hint displays rebound key");
            PlusMapMarkers.OnCleanup=()=>Check(!panel.IsActive()&&GUIManager.Requests==3,"cleanup releases portal canvas and lease before opening confirmation");
            ((Button)Get(panel,"cleanupButton")).onClick.Invoke();Check(PlusMapMarkers.Cleanups==1&&!panel.IsActive(),"cleanup is invoked once after closing");
            panel.Hide(false);Check(GUIManager.Requests==3,"duplicate close preserves foreign leases");
            panel.Show();panel.Hide();panel.Show();XPortal.QueuedAction.Flush();Check(panel.IsActive()&&GUIManager.Requests==4,"standalone repeated Show cancels delayed Hide without another lease");panel.Hide(false);
            panel.Show();GUIManager.Requests=0;panel.OnInputBlockReset();GUIManager.Requests=2;panel.Hide(false);
            Check(GUIManager.Requests==2,"global input reset prevents a late Hide from subtracting foreign requests");GUIManager.Requests=3;
            panel.ConfigurePortal(current);var name=(InputField)Get(panel,"portalNameInputField");name.isFocused=false;search.isFocused=false;
            ZInput.Up.Add("XPortal_DropdownScrollDown");search.isFocused=true;ZDOID before=(ZDOID)Get(panel,"selectedTargetId");panel.HandleInput();Check((ZDOID)Get(panel,"selectedTargetId")==before,"text editing suppresses destination navigation");ZInput.Up.Clear();
            panel.Hide();panel.ConfigurePortal(current);XPortal.QueuedAction.Flush();Check(panel.IsActive(),"reopening cancels delayed close from an older edit session");
            ZInput.instance=null;panel.HandleInput();Check(!panel.IsActive()&&GUIManager.Requests==3,"input subsystem loss closes before native button queries");ZInput.instance=new ZInput();
            panel.ConfigurePortal(current);Player.m_localPlayer.Dead=true;panel.HandleInput();Check(!panel.IsActive()&&GUIManager.Requests==3,"death closes and releases input");Player.m_localPlayer.Dead=false;
            panel.ConfigurePortal(current);ZNet.instance=new ZNet();panel.HandleInput();Check(!panel.IsActive()&&GUIManager.Requests==3,"network session replacement clears UI");
            panel.ConfigurePortal(current);((Button)Get(panel,"noneButton")).onClick.Invoke();((Dropdown)Get(panel,"iconDropdown")).value=5;
            ((Button)Get(panel,"applyButton")).onClick.Invoke();((Button)Get(panel,"applyButton")).onClick.Invoke();
            Check(XPortal.XPortal.Submits==1&&XPortal.XPortal.LastTarget==ZDOID.None&&XPortal.XPortal.LastIcon==6,"Apply sends one explicit destination/icon edit; repeated submission is blocked");
            XPortal.QueuedAction.Flush();Check(!panel.IsActive()&&GUIManager.Requests==3,"applying releases exactly its own lease");
            Check(Log.Errors.Count==0,"callbacks report no unexpected exception");
            Console.WriteLine("AnyPortal+ shipped UI PASS: "+checks+" checks; actual panel source with deterministic native-service doubles.");return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
        finally{panel.Dispose();}
    }
}
