using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using ValheimModPack.ExpeditionLoadouts;

internal static class BackendTests
{
    private static int checks;
    private static Player player;
    private static readonly MethodInfo response=typeof(ChestService).GetMethod("OpenResponse",BindingFlags.Static|BindingFlags.NonPublic);
    private static void Check(bool value,string label){checks++;if(!value)throw new Exception(label);}
    private static void Reset()
    {
        player=new Player();Player.m_localPlayer=player;UnityEngine.Object.Containers.Clear();ObjectDB.instance.Items.Clear();
        Time.frameCount=0;Time.realtimeSinceStartup=0;InventoryGui.instance=new InventoryGui();PrivateArea.Access=true;
        EquipmentAndQuickSlots.API.Rows=5;EquipmentAndQuickSlots.API.Height=6;
        BepInEx.Bootstrap.Chainloader.PluginInfos.Clear();
        BepInEx.Bootstrap.Chainloader.PluginInfos["randyknapp.mods.equipmentandquickslots"]=new object();
        BepInEx.Bootstrap.Chainloader.PluginInfos["goldenrevolver.quick_stack_store"]=new object();
        QuickStackStore.UserConfig.instance.Cells.Clear();QuickStackStore.UserConfig.instance.Items.Clear();
    }
    private static ItemDrop.ItemData Item(string prefab,int count,int x=0,int y=1)
    {
        UnityEngine.GameObject obj;
        if(!ObjectDB.instance.Items.TryGetValue(prefab,out obj))
        {
            obj=new UnityEngine.GameObject();obj.name=prefab;
            var definition=new ItemDrop.ItemData();definition.m_dropPrefab=obj;definition.m_shared.m_name="$"+prefab;
            obj.Components[typeof(ItemDrop)]=new ItemDrop{m_itemData=definition};ObjectDB.instance.Items[prefab]=obj;
        }
        var result=obj.GetComponent<ItemDrop>().m_itemData.Clone();result.m_stack=count;result.m_gridPos=new Vector2i(x,y);return result;
    }
    private static SupplyTarget Target(string name,int count){return new SupplyTarget{Prefab=name,Quality=1,Count=count};}
    private static List<SupplyTarget> Want(string name,int count){return new List<SupplyTarget>{Target(name,count)};}
    private static Container Chest(string name,int count,float distance=1){var c=new Container();c.transform.position=new Vector3(distance,0,0);c.GetInventory().Items.Add(Item(name,count));return c;}
    private static bool Reply(Container c,bool approved,long sender=2,bool ownership=true)
    {
        if(approved&&ownership)c.View.Data.Owner=1;
        return (bool)response.Invoke(null,new object[]{c,sender,approved});
    }
    private static void Next(ChestService service){Time.frameCount++;Time.realtimeSinceStartup+=.1f;service.Tick();}
    private static int Count(string prefab){return ChestService.CountOwned(player,Target(prefab,1));}
    private static void Main()
    {
        Check(TransferPolicy.Deficit(20,5)==15,"deficit");Check(TransferPolicy.Deficit(20,25)==0,"no surplus removal");
        Check(TransferPolicy.Deficit(10000,0)==0,"target bound");Check(TransferPolicy.MoveAmount(7,30,48,50)==2,"partial stack capacity");
        Check(!TransferPolicy.OrdinaryCell(0,0,8,6,5,false),"hotbar excluded");Check(!TransferPolicy.OrdinaryCell(0,5,8,6,5,false),"EAQS excluded");
        Check(TransferPolicy.OrdinaryCell(7,4,8,6,5,false),"extra row usable");Check(!TransferPolicy.OrdinaryCell(0,1,8,6,5,true),"favorite empty excluded");
        Check(!TransferPolicy.ValidTarget(Target("../Wood",5)),"prefab path rejected");
        Check(TransferPolicy.StandardChest("piece_chest_blackmetal")&&!TransferPolicy.StandardChest("piece_chest_modded"),"native chest allowlist");

        Reset();var chest=Chest("Wood",40);player.Inventory.Items.Add(Item("Wood",8));
        using(var service=new ChestService(null))
        {
            Check(service.Begin(player,Want("Wood",20),10),"begin");Check(chest.View.Requests==1&&chest.View.Data.Owner==2,"native handshake without ownership claim");
            Check(Count("Wood")==8,"no mutation before grant");Check(!Reply(chest,true),"response consumed");Next(service);
            Check(Count("Wood")==20&&chest.GetInventory().Items[0].m_stack==28,"exact deficit transferred");
            Check(!chest.IsInUse()&&chest.Saves==1&&!service.IsBusy,"release and persist");Check(service.TotalAdded==12,"received count");
        }

        Reset();chest=Chest("Wood",40);
        using(var service=new ChestService(null)){service.Begin(player,Want("Wood",20),10);Reply(chest,false);Next(service);Check(Count("Wood")==0&&!service.IsBusy,"denial safe");}

        Reset();chest=Chest("Wood",40);
        using(var service=new ChestService(null)){service.Begin(player,Want("Wood",20),10);Reply(chest,true,99,false);Next(service);Check(Count("Wood")==0&&service.IsBusy,"spoofed sender ignored");Reply(chest,true,2,false);Next(service);Check(Count("Wood")==0,"wait ownership replication");chest.View.Data.Owner=1;Next(service);Check(Count("Wood")==20,"replicated ownership transfers");}

        Reset();chest=Chest("Wood",40);
        using(var service=new ChestService(null)){service.Begin(player,Want("Wood",20),10);Reply(chest,true);chest.SetInUse(true);Next(service);Check(Count("Wood")==0&&chest.IsInUse(),"concurrent opener untouched");}

        Reset();chest=Chest("Wood",40);
        using(var service=new ChestService(null)){service.Begin(player,Want("Wood",20),10);chest.transform.position=new Vector3(11,0,0);Reply(chest,true);Next(service);Next(service);Check(Count("Wood")==0,"distance rechecked");}

        Reset();chest=Chest("Wood",40);PrivateArea.Access=false;
        using(var service=new ChestService(null)){service.Begin(player,Want("Wood",20),10);Check(chest.View.Requests==0&&Count("Wood")==0,"ward privacy");}
        Reset();chest=Chest("Wood",40);chest.Access=false;
        using(var service=new ChestService(null)){service.Begin(player,Want("Wood",20),10);Check(chest.View.Requests==0,"personal privacy");}

        Reset();chest=Chest("Wood",40);
        player.Inventory.Items.Add(Item("Wood",5,0,0));player.Inventory.Items.Add(Item("Wood",5,0,5));
        player.Inventory.Items.Add(Item("Wood",5,0,1));QuickStackStore.UserConfig.instance.Cells.Add("0,1");QuickStackStore.UserConfig.instance.Cells.Add("1,1");
        using(var service=new ChestService(null))
        {
            service.Begin(player,Want("Wood",20),10);Reply(chest,true);Next(service);
            Check(Count("Wood")==20,"protected existing stacks counted");Check(player.Inventory.GetItemAt(0,0).m_stack==5,"hotbar unchanged");
            Check(player.Inventory.GetItemAt(0,5).m_stack==5,"EAQS unchanged");Check(player.Inventory.GetItemAt(0,1).m_stack==5,"favorite stack unchanged");
            Check(player.Inventory.GetItemAt(1,1)==null&&player.Inventory.GetItemAt(2,1).m_stack==5,"empty favorite skipped");
        }

        Reset();chest=Chest("Wood",40);player.Inventory.Items.Add(Item("Wood",20,0,5));
        using(var service=new ChestService(null)){service.Begin(player,Want("Wood",20),10);Check(chest.View.Requests==0&&Count("Wood")==20,"EAQS enough no request");}

        Reset();chest=Chest("Wood",40);for(int y=1;y<5;y++)for(int x=0;x<8;x++)player.Inventory.Items.Add(Item("Stone",50,x,y));
        using(var service=new ChestService(null)){service.Begin(player,Want("Wood",20),10);Reply(chest,true);Next(service);Check(Count("Wood")==0&&chest.GetInventory().Items[0].m_stack==40,"full main inventory preserves source");Check(player.Inventory.GetItemAt(0,5)==null,"hidden cell never used when full");}

        Reset();chest=Chest("Wood",40);for(int y=1;y<5;y++)for(int x=0;x<8;x++)if(x!=7||y!=4)player.Inventory.Items.Add(Item("Stone",50,x,y));
        using(var service=new ChestService(null)){service.Begin(player,Want("Wood",20),10);Reply(chest,true);Next(service);Check(player.Inventory.GetItemAt(7,4).m_stack==20,"expanded visible row usable");}

        Reset();chest=Chest("Wood",40);
        using(var service=new ChestService(null)){service.Begin(player,Want("Wood",20),10);service.Cancel();Check(!Reply(chest,true),"late cancelled response consumed");Next(service);Check(Count("Wood")==0,"cancel does not transfer");}

        Reset();chest=Chest("Wood",40);
        using(var service=new ChestService(null)){service.Begin(player,Want("Wood",20),10);Time.realtimeSinceStartup=9;service.Tick();Check(!Reply(chest,true),"late timed-out response consumed");Next(service);Check(Count("Wood")==0,"timeout does not transfer");}

        Reset();chest=Chest("Wood",40);
        using(var service=new ChestService(null)){service.Begin(player,Want("Wood",20),10);Reply(chest,true);player.Dead=true;Next(service);Check(Count("Wood")==0&&!service.IsBusy,"death cancellation");}

        Reset();chest=Chest("Wood",40);
        using(var service=new ChestService(null)){service.Begin(player,Want("Wood",20),10);Reply(chest,true);player.Inventory.Items.Add(Item("Wood",17,0,0));Next(service);Check(Count("Wood")==20&&service.TotalAdded==3,"pickup race recomputes deficit");}

        Reset();chest=Chest("Wood",40);var second=Chest("Wood",40,2);
        using(var service=new ChestService(null)){service.Begin(player,Want("Wood",60),10);Reply(chest,true);Next(service);Check(Count("Wood")==40&&second.View.Requests==1,"serialized next chest");Reply(second,true);Next(service);Check(Count("Wood")==60&&second.GetInventory().Items[0].m_stack==20,"multi-chest exact deficit");}

        Reset();chest=Chest("Wood",40);player.Inventory.ThrowAfterMove=true;
        using(var service=new ChestService(null)){service.Begin(player,Want("Wood",20),10);Reply(chest,true);Next(service);Check(Count("Wood")==20&&chest.GetInventory().Items[0].m_stack==20,"exception does not duplicate consumed quantity");Check(!chest.IsInUse()&&chest.Saves==1&&!service.IsBusy,"exception persisted and released");}

        Reset();chest=Chest("Wood",40);chest.GetInventory().Items[0].m_customData["EpicLoot"]="magic";
        using(var service=new ChestService(null)){service.Begin(player,Want("Wood",20),10);Check(chest.View.Requests==0,"custom item excluded");}

        Reset();chest=Chest("Wood",40);chest.m_rootObjectOverride=new ZNetView();
        using(var service=new ChestService(null)){service.Begin(player,Want("Wood",20),10);Check(chest.View.Requests==0,"backpack override excluded");}
        Reset();chest=Chest("Wood",40);chest.Components[typeof(Ship)]=new Ship();
        using(var service=new ChestService(null)){service.Begin(player,Want("Wood",20),10);Check(chest.View.Requests==0,"ship excluded");}
        Reset();chest=Chest("Wood",40);chest.Components[typeof(TombStone)]=new TombStone();
        using(var service=new ChestService(null)){service.Begin(player,Want("Wood",20),10);Check(chest.View.Requests==0,"grave excluded");}

        Reset();chest=Chest("Wood",40);EquipmentAndQuickSlots.API.Height=7;
        using(var service=new ChestService(null)){Check(!service.Begin(player,Want("Wood",20),10)&&chest.View.Requests==0,"EAQS mismatch fail closed");}

        Reset();chest=Chest("Wood",40);chest.FailRefresh=true;
        using(var service=new ChestService(null)){service.Begin(player,Want("Wood",20),10);Check(chest.View.Requests==0,"stale replica not requested");}
        Reset();chest=Chest("Wood",40);
        using(var service=new ChestService(null)){service.Begin(player,Want("Wood",40),10);Reply(chest,true);Next(service);Check(Count("Wood")==40&&chest.GetInventory().Items.Count==0&&service.TotalAdded==40,"whole native source removed and counted");}
        Reset();chest=Chest("Wood",40);
        using(var service=new ChestService(null)){service.Begin(player,Want("Wood",20),10);service.Cancel();Player.m_localPlayer=null;service.Tick();Check(Reply(chest,false),"world exit clears retired request references");}

        Reset();chest=Chest("Wood",40);
        using(var service=new ChestService(null)){service.Begin(player,Want("Wood",20),10);Reply(chest,true);chest.FailRefresh=true;chest.View.Data.DataRevision++;Next(service);Check(Count("Wood")==0,"stale inventory after ownership grant rejected");}
        Reset();chest=Chest("Wood",40);
        using(var service=new ChestService(null)){service.Begin(player,Want("Wood",20),10);Reply(chest,true);PrivateArea.Access=false;Next(service);Check(Count("Wood")==0,"ward access rechecked after grant");}
        Reset();chest=Chest("Wood",40);
        using(var service=new ChestService(null)){service.Begin(player,Want("Wood",20),10);Reply(chest,true);InventoryGui.instance.Open(chest);Next(service);Check(Count("Wood")==0&&!service.IsBusy,"manual chest UI cancels transfer");}
        Reset();chest=Chest("Wood",40);player.Inventory.Items.Add(Item("Wood",5));QuickStackStore.UserConfig.instance.Items.Add("$Wood");
        using(var service=new ChestService(null)){service.Begin(player,Want("Wood",20),10);Reply(chest,true);Next(service);Check(player.Inventory.GetItemAt(0,1).m_stack==5&&player.Inventory.GetItemAt(1,1).m_stack==15,"favorite item stack unchanged");}
        Reset();chest=Chest("Wood",40);player.Inventory.Items.Add(Item("Wood",5));player.Inventory.Items[0].m_variant=2;
        using(var service=new ChestService(null)){service.Begin(player,Want("Wood",20),10);Reply(chest,true);Next(service);Check(player.Inventory.GetItemAt(0,1).m_stack==5&&player.Inventory.GetItemAt(1,1).m_stack==15,"different variant not merged");}
        Reset();chest=Chest("Wood",40);player.Inventory.ThrowAfterMove=true;
        using(var service=new ChestService(null)){service.Begin(player,Want("Wood",40),10);Reply(chest,true);Next(service);Check(Count("Wood")==40&&chest.GetInventory().Items.Count==0&&chest.Saves==1,"whole-stack callback failure cleans zero source and persists");}
        Reset();chest=Chest("Wood",40);player.Inventory.ThrowAfterMove=true;chest.GetInventory().ThrowAfterRemove=true;
        using(var service=new ChestService(null)){service.Begin(player,Want("Wood",40),10);Reply(chest,true);Next(service);Check(Count("Wood")==40&&chest.GetInventory().Items.Count==0&&chest.Saves==1,"source is persisted even if cleanup callback also throws");Check(!chest.IsInUse()&&!service.IsBusy,"cleanup failure releases chest and cancels safely");}
        Reset();chest=Chest("Wood",40);chest.name="piece_chest_modded";
        using(var service=new ChestService(null)){service.Begin(player,Want("Wood",20),10);Check(chest.View.Requests==0,"nonstandard container protocol excluded");}
        Reset();chest=Chest("Wood",40);
        using(var service=new ChestService(null)){service.Begin(player,new List<SupplyTarget>{Target("Wood",10),Target("Wood",20)},10);Reply(chest,true);Next(service);Check(Count("Wood")==20,"duplicate targets normalized by maximum not summed");}
        Reset();chest=Chest("Wood",40);
        for(int i=0;i<129;i++)Chest("Wood",40);
        using(var service=new ChestService(null))
        {
            service.Begin(player,Want("Wood",9999),10);
            for(int i=0;i<128;i++){Time.realtimeSinceStartup+=9;service.Tick();service.Tick();}
            Check(!service.IsBusy&&!service.Begin(player,Want("Wood",9999),10),"unanswered requests bounded to 128");
        }

        Reset();var normal=Item("Wood",15);player.Inventory.Items.Add(normal);player.Inventory.Items.Add(Item("Wood",5,0,0));player.Inventory.Items.Add(Item("Wood",6,0,5));
        var weapon=Item("Sword",1,1,1);weapon.m_shared.m_itemType=ItemDrop.ItemData.ItemType.OneHandedWeapon;player.Inventory.Items.Add(weapon);
        var snapshot=ChestService.SnapshotInventory(player);Check(snapshot.Count==1&&snapshot[0].Count==20,"snapshot ordinary supplies not gear or hidden slots");
        Console.WriteLine("PASS: "+checks+" loadout backend assertions (production source with engine/network boundary doubles).");
    }
}
