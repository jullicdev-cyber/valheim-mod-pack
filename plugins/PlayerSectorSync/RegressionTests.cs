using System;
using ValheimModPack.PlayerSectorSync;
using UnityEngine;

internal static class RegressionTests
{
    private static int assertions;
    private static readonly Vector3 Origin=new Vector3(0,0,0), Far=new Vector3(4096,0,0);
    private static void Check(bool condition, string name) { ++assertions; if(!condition) throw new Exception(name); }
    private static ZDO Setup(bool guest)
    {
        ZDOMan.instance=new ZDOMan(); ZNet.instance=new ZNet();
        var zdo=new ZDO(guest ? 20 : 10, guest ? 2 : 1, Origin);
        ZNet.instance.LocalPlayerCharacterID=new ZDOID(10);
        var friend=new ZNetPeer { m_uid=3, m_characterID=new ZDOID(30), RefPos=Origin };
        friend.Known.Add(zdo.m_uid); ZNet.instance.Peers.Add(friend);
        if(guest)
        {
            var owner=new ZNetPeer { m_uid=2, m_characterID=zdo.m_uid, RefPos=Origin };
            owner.Known.Add(zdo.m_uid); ZNet.instance.Peers.Add(owner);
        }
        return zdo;
    }
    private static void Move(ZDO zdo, Vector3 target)
    {
        PlayerPositionPatch.MoveState state;
        PlayerPositionPatch.Prefix(zdo, target, out state);
        zdo.InternalSetPosition(target);
        PlayerPositionPatch.Postfix(zdo, state);
    }
    private static void ReproduceOldGhost()
    {
        ZDO zdo=Setup(false); var peer=ZNet.instance.Peers[0]; var receiver=new Receiver(peer,zdo);
        zdo.InternalSetPosition(Far); receiver.SendNativeData(zdo,0); receiver.Trigger();
        Check(ZDOMan.instance.Notifications==1,"native sector event exists but reads old position");
        Check(peer.Invalid.Count==0,"old near position suppresses native invalidation");
        Check(receiver.Visual && receiver.DisplayedPosition.x==0,"unpatched friend keeps old ghost");
        Check(receiver.Animations==1,"unpatched ghost still receives separate animations");
    }
    private static void FixedMove(bool guest)
    {
        ZDO zdo=Setup(guest); var peer=ZNet.instance.Peers[0]; var receiver=new Receiver(peer,zdo);
        object inventory=zdo.Inventory; Move(zdo,Far);
        Check(ZDOMan.instance.Notifications==2,"corrected notification occurs once after native notification");
        Check(zdo.Position.x==Far.x && zdo.Revision==1,"native movement and revision untouched");
        Check(peer.Invalid.Contains(zdo.m_uid),"corrected position queues native invalidation");
        Check(!peer.Known.Contains(zdo.m_uid),"native peer cache releases old sector record");
        receiver.SendNativeData(zdo,0);
        Check(!receiver.Visual,"native receiver removes remote ghost visual");
        Check(receiver.WorldZdoAlive && receiver.DestroyedWorldZdos==0,"remote cleanup does not destroy character ZDO");
        Check(object.ReferenceEquals(inventory,zdo.Inventory) && zdo.Inventory.Count==1 && zdo.Inventory[0]=="SilverArmor","inventory identity and items unchanged");
        if(guest)
        {
            var owner=ZNet.instance.Peers[1]; var ownView=new Receiver(owner,zdo);
            Check(owner.Invalid.Count==0 && owner.Known.Contains(zdo.m_uid),"native owner-peer safeguard retained");
            ownView.SendNativeData(zdo,0);
            Check(ownView.Visual && ownView.WorldZdoAlive,"owning player's current character not removed");
        }
        peer.RefPos=Far; receiver.SendNativeData(zdo,0);
        Check(receiver.Visual && receiver.DisplayedPosition.x==Far.x,"native player instance returns at real position when peer comes nearby");
        Move(zdo,Origin); Check(peer.Invalid.Contains(zdo.m_uid),"return teleport also corrects stale sector");
    }
    private static void Guards()
    {
        ZDO zdo=Setup(false); Move(zdo,new Vector3(1,0,1));
        Check(ZDOMan.instance.Notifications==0 && zdo.Position.x==1,"same sector movement runs original with no correction");
        zdo=Setup(false); var stranger=new ZDO(999,1,Origin); Move(stranger,Far);
        Check(ZDOMan.instance.Notifications==1,"unregistered entities retain only their native event");
        zdo=Setup(false); ZNet.instance.Server=false; Move(zdo,Far);
        Check(ZDOMan.instance.Notifications==0 && zdo.Position.x==Far.x,"clients do not invoke server corrections");
        zdo=Setup(false); ZNet.instance.Peers.Clear(); Move(zdo,Far);
        Check(ZDOMan.instance.Notifications==1,"single player only retains native event");
        zdo=Setup(true); ZNet.instance.LocalPlayerCharacterID=new ZDOID(0); Move(zdo,Far);
        Check(ZDOMan.instance.Notifications==2,"dedicated server recognizes guest character without local player");
        zdo=Setup(true); ZNet.instance.Peers[1].Ready=false; Move(zdo,Far);
        Check(ZDOMan.instance.Notifications==1,"unready peer identity not considered current character");
        zdo=Setup(false); zdo.m_uid=new ZDOID(0); Move(zdo,Far);
        Check(ZDOMan.instance.Notifications==1,"none ID never treated as player");
        zdo=Setup(false); var nearby=new Vector3(65,0,0); Move(zdo,nearby);
        Check(ZDOMan.instance.Notifications==2 && ZNet.instance.Peers[0].Invalid.Count==0,"crossing sector within active area does not remove visible player");
        zdo=Setup(false); ZNet.instance.Peers.Insert(0,null); Move(zdo,Far);
        Check(ZDOMan.instance.Notifications==2,"null peer tolerated");
    }
    private static void InFlightGuards()
    {
        PlayerPositionPatch.MoveState state; ZDO zdo=Setup(false);
        PlayerPositionPatch.Prefix(zdo,Far,out state); PlayerPositionPatch.Postfix(zdo,state);
        Check(ZDOMan.instance.Notifications==0,"skipped native movement cannot enqueue correction");
        zdo=Setup(false); PlayerPositionPatch.Prefix(zdo,Far,out state); zdo.Position=Far;
        ZNet.instance=new ZNet(); PlayerPositionPatch.Postfix(zdo,state);
        Check(ZDOMan.instance.Notifications==0,"changed network instance invalidates state");
        zdo=Setup(false); PlayerPositionPatch.Prefix(zdo,Far,out state); zdo.Position=Far;
        ZDOMan.instance=new ZDOMan(); PlayerPositionPatch.Postfix(zdo,state);
        Check(ZDOMan.instance.Notifications==0,"changed manager invalidates state");
        zdo=Setup(false); PlayerPositionPatch.Prefix(zdo,Far,out state); zdo.Position=Far;
        ZNet.instance.Server=false; PlayerPositionPatch.Postfix(zdo,state);
        Check(ZDOMan.instance.Notifications==0,"server role loss cancels correction");
        zdo=Setup(false); PlayerPositionPatch.Prefix(zdo,Far,out state); zdo.Position=Far;
        ZNet.instance.LocalPlayerCharacterID=new ZDOID(11); PlayerPositionPatch.Postfix(zdo,state);
        Check(ZDOMan.instance.Notifications==0,"character replacement cannot invalidate old player");
        zdo=Setup(false); PlayerPositionPatch.Prefix(zdo,new Vector3(float.NaN,0,0),out state);
        Check(!state.Active,"NaN requested position ignored by patch");
        PlayerPositionPatch.Prefix(zdo,new Vector3(0,float.PositiveInfinity,0),out state);
        Check(!state.Active,"infinite requested position ignored by patch");
        PlayerPositionPatch.Prefix(zdo,Far,out state); zdo.Position=new Vector3(float.NaN,0,0);
        PlayerPositionPatch.Postfix(zdo,state); Check(ZDOMan.instance.Notifications==0,"invalid final position cannot notify peers");
        zdo=Setup(false); ZNet.instance.Peers=null; PlayerPositionPatch.Prefix(zdo,Far,out state);
        Check(!state.Active,"null peer list tolerated");
        ZNet.instance=null; PlayerPositionPatch.Prefix(zdo,Far,out state); Check(!state.Active,"no network tolerated");
        zdo=Setup(false); ZDOMan.instance=null; PlayerPositionPatch.Prefix(zdo,Far,out state); Check(!state.Active,"no manager tolerated");
        zdo=Setup(false); PlayerPositionPatch.Prefix(null,Far,out state); Check(!state.Active,"null ZDO tolerated");
    }
    private static void QueuesAndErrors()
    {
        ZDO zdo=Setup(false); var peer=ZNet.instance.Peers[0]; var receiver=new Receiver(peer,zdo);
        Move(zdo,Far); receiver.SendNativeData(zdo,10241);
        Check(receiver.Visual && peer.Invalid.Contains(zdo.m_uid),"native saturated queue delays but retains correction");
        receiver.SendNativeData(zdo,8193);
        Check(receiver.Visual && peer.Invalid.Contains(zdo.m_uid),"native two KiB minimum budget remains respected");
        receiver.SendNativeData(zdo,8192);
        Check(!receiver.Visual && peer.Invalid.Count==0,"queued correction works when native budget recovers");
        int warnings=0; PlayerPositionPatch.Warning=message=>++warnings;
        zdo=Setup(false); ZNet.instance.ThrowOnPeers=true;
        PlayerPositionPatch.MoveState state; PlayerPositionPatch.Prefix(zdo,Far,out state);
        Check(!state.Active,"prefix failure does not interfere with native method");
        zdo=Setup(false); PlayerPositionPatch.Prefix(zdo,Far,out state); zdo.Position=Far;
        ZDOMan.instance.ThrowOnNotify=true; PlayerPositionPatch.Postfix(zdo,state);
        PlayerPositionPatch.Postfix(zdo,state);
        Check(warnings==1,"repeated correction failures produce only one warning");
    }
    private static void LoggerFailure()
    {
        int warnings=0;
        PlayerPositionPatch.Warning=message=>{ ++warnings; throw new Exception("logger fixture"); };
        ZDO zdo=Setup(false); ZNet.instance.ThrowOnPeers=true;
        PlayerPositionPatch.MoveState state; PlayerPositionPatch.Prefix(zdo,Far,out state);
        Check(warnings==1 && !state.Active,"logger callback actually throws and remains contained");
        PlayerPositionPatch.Prefix(zdo,Far,out state);
        Check(warnings==1,"failed logger also remains limited to one attempt");
    }
    public static int Main(string[] args)
    {
        try {
            if(args.Length==1 && args[0]=="logger-failure") LoggerFailure();
            else { ReproduceOldGhost(); FixedMove(false); FixedMove(true); Guards(); InFlightGuards(); QueuesAndErrors(); }
            Console.WriteLine("Player Sector Sync: "+assertions+" managed regression assertions passed."); return 0; }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
