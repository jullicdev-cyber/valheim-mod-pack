using System;
using UnityEngine;

namespace ValheimModPack.InventoryAdmin
{
    public static class GroupRadiusMotionTests
    {
        private static int checks;
        private static Player player;
        private static GroupRadiusFrame frame;
        private static Vector3 V(float x, float z) { return new Vector3(x, 10, z); }
        private static bool Same(Vector3 a, Vector3 b) { return Math.Abs(a.x - b.x) < 0.0001f && Math.Abs(a.y - b.y) < 0.0001f && Math.Abs(a.z - b.z) < 0.0001f; }
        private static void Check(bool value, string why) { checks++; if (!value) throw new InvalidOperationException(why); }
        private static Vector3 Clip(Vector3 direction, out GroupRadiusMotion.MotionState state)
        { GroupRadiusMotion.FilterMotion(player, player.View, ref direction, out state); return direction; }
        private static Vector3 Clip(Vector3 direction) { GroupRadiusMotion.MotionState state; return Clip(direction, out state); }
        private static void Fresh() { frame.Sequence++; Time.realtimeSinceStartup += 0.25f; }
        private static bool Teleport(Vector3 to, bool distant, out bool result, out GroupRadiusMotion.TeleportState state)
        { result = true; return GroupRadiusMotion.AllowTeleport(player, player.View, to, distant, ref result, out state); }
        public static int Main()
        {
            try { Run(); Console.WriteLine("PASS: " + checks + " group radius motion assertions."); return 0; }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        }
        private static void Run()
        {
            player = Player.m_localPlayer = new Player(); player.transform.position = V(100, 0);
            frame = new GroupRadiusFrame { Active = true, Radius = 100, LeaderPosition = V(0, 0), Sequence = 1 };
            GroupRadiusMotion.Bind(() => frame); Time.realtimeSinceStartup = 1;
            var outward = new Vector3(1, 7, 0); var inward = new Vector3(-1, 7, 0); var tangent = new Vector3(0, 7, 1);
            GroupRadiusMotion.MotionState motion;
            Vector3 clipped = Clip(outward, out motion);
            Check(clipped.x == 0 && clipped.y == 7 && motion.Changed, "boundary removes outward horizontal input and preserves vertical intent");
            GroupRadiusMotion.RestoreMotion(ref clipped, motion);
            Check(Same(clipped, outward), "native input restored after motor consumption (autorun survives policy pause)");
            GroupRadiusMotion.RestoreMotion(ref clipped, motion);
            Check(Same(clipped, outward), "restore is safe when finalizer follows postfix");
            Check(Same(Clip(inward), inward), "inward recovery movement remains unchanged");
            Check(Same(Clip(tangent), tangent), "tangent movement remains unchanged");
            clipped = Clip(new Vector3(1, 3, 1));
            Check(clipped.x == 0 && clipped.y == 3 && clipped.z == 1, "diagonal outward input retains its full tangent component");
            Check(Same(player.transform.position, V(100, 0)), "filter never changes player transform");
            player.transform.position = V(110, 0);
            Check(Clip(outward).x == 0 && Same(Clip(inward), inward), "outside players can always recover inward");
            player.transform.position = V(98, 0); player.Velocity = new Vector3(15, 0, 0);
            Check(Clip(outward).x == 0, "fast native travel starts braking before the edge");
            player.Velocity = Vector3.zero; player.transform.position = V(95, 0);
            Check(Same(Clip(outward), outward), "travel away from boundary is unchanged");
            player.transform.position = V(100, 0);
            var remote = new Player(); remote.transform.position = V(100, 0); var remoteInput = outward;
            GroupRadiusMotion.FilterMotion(remote, remote.View, ref remoteInput, out motion);
            Check(Same(remoteInput, outward) && !motion.Changed && Same(remote.transform.position, V(100, 0)), "remote replica is never filtered or moved");
            var npc = new Character(); remoteInput = outward;
            GroupRadiusMotion.FilterMotion(npc, npc.View, ref remoteInput, out motion);
            Check(Same(remoteInput, outward) && !motion.Changed, "non-player native motor remains untouched");
            player.View.Owner = false; Check(Same(Clip(outward), outward), "local reference alone does not allow a nonowner filter"); player.View.Owner = true;
            player.View.Valid = false; Check(Same(Clip(outward), outward), "invalid network view fails open"); player.View.Valid = true;
            foreach (Action<bool> flag in new Action<bool>[] { value => player.Dead = value, value => player.Teleporting = value,
                value => player.Attached = value, value => player.Sleeping = value, value => player.Cutscene = value, value => player.DebugFlying = value })
            { flag(true); Check(Same(Clip(outward), outward), "busy/special native state remains unchanged"); flag(false); }
            frame.Exempt = true; Check(Same(Clip(outward), outward), "leader/admin exemption is immediate"); frame.Exempt = false;
            frame.Active = false; Check(Same(Clip(outward), outward), "off/single-player/integrity gate releases normal movement"); frame.Active = true;
            Time.realtimeSinceStartup = 2.499f; Check(Clip(outward).x == 0, "leader sequence valid just before 1.5 second TTL");
            Time.realtimeSinceStartup = 2.5f; Check(Same(Clip(outward), outward), "unchanged leader sequence expires at 1.5 seconds");
            for (int i = 0; i < 10; ++i) GroupRadiusMotion.Tick();
            Check(Same(Clip(outward), outward), "render/update ticks never extend stale leader coordinates");
            Fresh(); Check(Clip(outward).x == 0, "new authenticated leader sequence restores constraint");
            frame.LeaderPosition = V(200, 0); Fresh();
            Check(Same(Clip(outward), outward) && Clip(inward).x == 0, "moving leader flips inward/outward directions without teleporting anyone");
            frame.LeaderPosition = V(0, 0); Fresh();
            frame.Radius = Single.NaN; Check(Same(Clip(outward), outward), "invalid radius fails open"); frame.Radius = 100;
            frame.LeaderPosition = new Vector3(Single.PositiveInfinity, 0, 0); Check(Same(Clip(outward), outward), "invalid leader position fails open"); frame.LeaderPosition = V(0, 0);
            frame.Sequence = 0; Check(Same(Clip(outward), outward), "missing sequence cannot activate movement"); Fresh();
            GroupRadiusMotion.Bind(() => { throw new InvalidOperationException("unavailable session"); });
            Check(Same(Clip(outward), outward), "session accessor failure preserves native input");
            GroupRadiusMotion.Bind(() => frame); Fresh();
            bool result; GroupRadiusMotion.TeleportState teleport;
            Check(!Teleport(V(101, 0), true, out result, out teleport) && !result, "ordinary portal beyond circle rejects before native TeleportTo");
            Check(Teleport(V(100, 0), true, out result, out teleport), "portal on exact circle is accepted");
            Check(Teleport(new Vector3(80, 20, 30), true, out result, out teleport), "surface portal inside circle is accepted");
            frame.GraceUntil = Time.realtimeSinceStartup + 10;
            Check(Same(Clip(outward), outward), "activation/leader-change grace preserves movement before expiry");
            Check(!Teleport(V(101, 0), true, out result, out teleport) && !result, "grace never bypasses ordinary portal destination validation");
            frame.GraceUntil = 0;
            player.View.Owner = false;
            Check(Teleport(V(1000, 0), true, out result, out teleport), "nonowner teleport remains native RPC route"); player.View.Owner = true;
            bool remoteResult = true;
            Check(GroupRadiusMotion.AllowTeleport(remote, remote.View, V(1000, 0), true, ref remoteResult, out teleport) && remoteResult,
                "remote teleport is never denied or sent by adapter");
            frame.Exempt = true; Check(Teleport(V(1000, 0), true, out result, out teleport), "leader/admin may use ordinary far portals"); frame.Exempt = false;
            Vector3 logical;
            player.transform.position = V(80, 0); GroupRadiusMotion.Tick();
            Check(GroupRadiusMotion.TryGetLogicalPosition(player, out logical) && Same(logical, V(80, 0)), "surface logical position is actual owned position");
            Check(!GroupRadiusMotion.TryGetLogicalPosition(remote, out logical), "remote transform cannot supply logical leader coordinates");
            Check(Teleport(new Vector3(5000, 5000, 5000), false, out result, out teleport) && teleport.Entering,
                "native dungeon entry may teleport to its unrelated interior coordinates");
            GroupRadiusMotion.RecordTeleport(true, teleport);
            player.Teleporting = true; player.transform.position = new Vector3(5000, 5000, 5000);
            Check(!GroupRadiusMotion.TryGetLogicalPosition(player, out logical), "leader transition is unavailable until native teleport completes");
            player.Teleporting = false;
            Check(GroupRadiusMotion.TryGetLogicalPosition(player, out logical) && Same(logical, V(80, 0)), "dungeon logical coordinates remain known surface entrance");
            Check(Same(Clip(outward), outward), "dungeon walking is never constrained using unrelated interior XZ");
            frame.LeaderPosition = V(-1000, 0); Fresh();
            Check(Teleport(V(82, 0), false, out result, out teleport), "dungeon exit to known entrance allowed even after leader moves");
            Check(!Teleport(V(500, 0), false, out result, out teleport), "unrelated surface destination from interior still obeys radius");
            Check(!Teleport(V(82, 0), true, out result, out teleport), "distant ordinary portal cannot misuse dungeon-door exception");
            GroupRadiusMotion.ResetWorld();
            Check(!GroupRadiusMotion.TryGetLogicalPosition(player, out logical), "unknown interior after context reset does not fabricate surface coordinates");
            Check(Teleport(V(82, 0), false, out result, out teleport), "unknown interior after reconnect always permits ordinary dungeon doorway exit outside the circle");
            Check(!Teleport(V(82, 0), true, out result, out teleport), "unknown interior never exempts a distant ordinary portal outside the circle");
            player.transform.position = V(80, 0); player.Dead = true;
            Check(!GroupRadiusMotion.TryGetLogicalPosition(player, out logical), "dead leader cannot publish an available position"); player.Dead = false;
            var replacement = new Player(); replacement.transform.position = new Vector3(1, 5000, 1); Player.m_localPlayer = replacement;
            Check(!GroupRadiusMotion.TryGetLogicalPosition(replacement, out logical), "new local character never inherits prior dungeon entry");
            Player.m_localPlayer = player; player.transform.position = V(100, 0); frame.LeaderPosition = V(0, 0); Fresh();
            var ship = new Ship(); ship.transform.position = V(90, 0); ship.transform.forward = new Vector3(1, 0, 0);
            player.Controller = new ShipControlls { m_ship = ship }; player.Attached = true;
            Ship.Speed speed = Ship.Speed.Full; GroupRadiusMotion.FilterShipSpeed(ship, ref speed);
            Check(speed == Ship.Speed.Stop, "confirmed local owned pilot lowers outward sail in conservative braking zone");
            frame.GraceUntil = Time.realtimeSinceStartup + 10; speed = Ship.Speed.Full; GroupRadiusMotion.FilterShipSpeed(ship, ref speed);
            Check(speed == Ship.Speed.Full, "boat activation grace preserves current pilot setting");
            frame.GraceUntil = 0;
            var controls = new Vector3(1, 0, 1); GroupRadiusMotion.FilterShipInput(ship, Ship.Speed.Stop, ref controls);
            Check(controls.x == 1 && controls.z == 0, "outward boat throttle blocked while steering preserved");
            controls.z = -1; GroupRadiusMotion.FilterShipInput(ship, Ship.Speed.Stop, ref controls);
            Check(controls.z == -1, "inward boat reverse throttle allowed");
            speed = Ship.Speed.Back; GroupRadiusMotion.FilterShipSpeed(ship, ref speed); Check(speed == Ship.Speed.Back, "inward reverse setting remains unchanged");
            ship.transform.forward = new Vector3(-1, 0, 0); speed = Ship.Speed.Full; GroupRadiusMotion.FilterShipSpeed(ship, ref speed);
            Check(speed == Ship.Speed.Full, "inward sail remains available");
            speed = Ship.Speed.Back; GroupRadiusMotion.FilterShipSpeed(ship, ref speed); Check(speed == Ship.Speed.Stop, "outward reverse paddle stops safely");
            ship.transform.forward = new Vector3(0, 0, 1); speed = Ship.Speed.Full; GroupRadiusMotion.FilterShipSpeed(ship, ref speed);
            Check(speed == Ship.Speed.Full, "tangent boat sailing remains unchanged");
            ship.transform.forward = new Vector3(1, 0, 0); ship.transform.position = V(70, 0); speed = Ship.Speed.Full; GroupRadiusMotion.FilterShipSpeed(ship, ref speed);
            Check(speed == Ship.Speed.Full, "boat inside circle away from braking zone is unchanged"); ship.transform.position = V(90, 0);
            ship.Owner = false; GroupRadiusMotion.FilterShipSpeed(ship, ref speed); Check(speed == Ship.Speed.Full, "remote owned ship is never mutated"); ship.Owner = true;
            ((ShipControlls)player.Controller).User = 999; GroupRadiusMotion.FilterShipSpeed(ship, ref speed); Check(speed == Ship.Speed.Full, "different pilot user cannot authorize speed changes"); ((ShipControlls)player.Controller).User = 42;
            player.Controller = null; GroupRadiusMotion.FilterShipSpeed(ship, ref speed); Check(speed == Ship.Speed.Full, "passenger cannot change another pilot's ship");
            Check(Same(ship.transform.position, V(90, 0)), "boat control adapter never relocates boat or passengers");
            player.Attached = false;
            var diagonal = new Vector3(1, 0, 1); Vector3 boundary = new Vector3(70.71068f, 0, 70.71068f);
            var radial = GroupRadiusMotion.ClipIntent(boundary, Vector3.zero, 100, diagonal, 1);
            Check(Math.Abs(radial.x) < 0.0001f && Math.Abs(radial.z) < 0.0001f, "diagonal circle radial vector is correctly removed");
            var dTangent = new Vector3(-1, 0, 1);
            Check(Same(GroupRadiusMotion.ClipIntent(boundary, Vector3.zero, 100, dTangent, 1), dTangent), "diagonal tangent is preserved");
            Check(Same(GroupRadiusMotion.ClipIntent(Vector3.zero, Vector3.zero, 100, outward, 1), outward), "center has no arbitrary radial direction");
            Localization.instance = new Localization { Language = "Russian" }; Fresh(); Time.realtimeSinceStartup += 5;
            player.Messages.Clear(); frame.Notice = null; Clip(outward);
            Check(player.Messages.Count == 1 && player.Messages[0].Contains("радиуса"), "native notice is localized in Russian");
            int messages = player.Messages.Count; Clip(outward); GroupRadiusMotion.Tick();
            Check(player.Messages.Count == messages, "repeated boundary physics calls do not spam notices");
            Time.realtimeSinceStartup += 5; Fresh(); player.transform.position = V(91, 0); player.Messages.Clear(); GroupRadiusMotion.Tick();
            Check(player.Messages.Count == 1 && player.Messages[0].Contains("91 / 100"), "approaching warning begins around ninety percent and includes leader distance and limit");
            player.Messages.Clear(); Time.realtimeSinceStartup += 5; Fresh(); player.transform.position = V(89, 0); GroupRadiusMotion.Tick();
            Check(player.Messages.Count == 0, "approaching notice is quiet away from boundary");
            // Native swimming lerps motor velocity toward intent by its actual
            // configured coefficient. Exercise coasting after input is clipped.
            player.transform.position = V(90, 0); player.Velocity = new Vector3(4, 0, 0);
            for (int i = 0; i < 500; ++i)
            {
                var swim = new Vector3(1, 0, 0);
                GroupRadiusMotion.FilterMotion(player, player.View, ref swim, out motion, 0.05f, true);
                player.Velocity.x += (swim.x * 4f - player.Velocity.x) * 0.05f;
                var actual = player.transform.position; actual.x += player.Velocity.x * Time.fixedDeltaTime; player.transform.position = actual;
                Time.realtimeSinceStartup += Time.fixedDeltaTime; if (i % 10 == 0) frame.Sequence++;
            }
            Check(player.transform.position.x <= 100f && player.transform.position.x > 95f && Math.Abs(player.Velocity.x) < 0.001f,
                "ordinary native swimming coasts to rest inside circle with actual acceleration margin");
            player.transform.position = V(90, 0); player.Velocity = new Vector3(10, 0, 0);
            for (int i = 0; i < 200; ++i)
            {
                var walk = new Vector3(1, 0, 0);
                GroupRadiusMotion.FilterMotion(player, player.View, ref walk, out motion, 1f, false);
                float target = walk.x * 10f;
                float stepped = player.Velocity.x < target ? Math.Min(target, player.Velocity.x + 1f) : Math.Max(target, player.Velocity.x - 2f);
                player.Velocity.x = (player.Velocity.x + stepped) * 0.5f;
                var actual = player.transform.position; actual.x += player.Velocity.x * Time.fixedDeltaTime; player.transform.position = actual;
                Time.realtimeSinceStartup += Time.fixedDeltaTime; if (i % 10 == 0) frame.Sequence++;
            }
            Check(player.transform.position.x <= 100f && player.transform.position.x > 95f && Math.Abs(player.Velocity.x) < 0.001f,
                "ordinary native walking decelerates to rest inside circle without a velocity rewrite");
            GroupRadiusMotion.Bind(null); Check(Same(Clip(outward), outward), "unbound adapter preserves all native movement");
        }
    }
}
