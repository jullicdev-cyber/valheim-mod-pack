using System;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace ValheimModPack.InventoryAdmin
{
    // Active is supplied by the authenticated session adapter after its own
    // membership, administrator, leader-availability and activation-grace checks.
    // Sequence advances only for a newly received leader position, not UI ticks.
    public sealed class GroupRadiusFrame
    {
        public bool Active, Exempt;
        public float Radius, GraceUntil;
        public Vector3 LeaderPosition;
        public long Sequence;
        public Action<string> Notice;
    }

    public static class GroupRadiusMotion
    {
        public const float FreshnessSeconds = 1.5f;
        private const float InteriorHeight = 3000f, DungeonExitRange = 25f;
        private static Func<GroupRadiusFrame> readFrame;
        private static Player observedPlayer;
        private static Vector3 surfaceEntry;
        private static bool haveSurfaceEntry;
        private static long lastSequence;
        private static bool haveSequence;
        private static float sequenceAt, noticeAt;

        public static void Bind(Func<GroupRadiusFrame> accessor)
        { readFrame = accessor; ResetWorld(); }

        public static void ResetWorld()
        {
            observedPlayer = null; surfaceEntry = Vector3.zero; haveSurfaceEntry = false;
            lastSequence = 0; haveSequence = false; sequenceAt = 0; noticeAt = 0;
        }

        // Keep the entry even while enforcement is disabled or the local player
        // is the exempt leader. Interior coordinates are unrelated to surface XZ.
        public static void Tick()
        {
            try
            {
                Player player = Player.m_localPlayer;
                Observe(player);
                GroupRadiusFrame frame;
                if (!TryFrame(out frame) || player == null || player.IsDead() || player.IsTeleporting()) return;
                Vector3 point;
                if (!TryGetLogicalPosition(player, out point)) return;
                if (Outside(point, frame.LeaderPosition, frame.Radius + 2f))
                    Notice(player, frame, Time.realtimeSinceStartup < frame.GraceUntil
                        ? T("Лидер или радиус группы изменились. Вернись к группе в течение отсрочки; затем движение от лидера будет ограничено.",
                            "The leader or group radius changed. Return to the group during the grace period; outward travel will then be restricted.")
                        : T("Ты за пределами радиуса группы. Двигайся к лидеру; транспорт и отбрасывание могут вынести за границу.",
                            "You are outside the group radius. Move toward the leader; transport and knockback can carry you past the boundary."));
                else if (Outside(point, frame.LeaderPosition, frame.Radius * 0.9f))
                {
                    double dx = (double)point.x - frame.LeaderPosition.x, dz = (double)point.z - frame.LeaderPosition.z;
                    string distance = Math.Ceiling(Math.Sqrt(dx * dx + dz * dz)).ToString(CultureInfo.InvariantCulture);
                    string radius = frame.Radius.ToString("0", CultureInfo.InvariantCulture);
                    Notice(player, frame, T("Приближаешься к границе группы: ", "Approaching the group boundary: ") + distance + " / " + radius + T(" м до лидера.", " m from the leader."));
                }
            }
            catch { } // An optional travel restriction must not break native motion.
        }

        public static bool TryGetLogicalPosition(Player player, out Vector3 point)
        {
            point = Vector3.zero;
            try
            {
                if (!LocalOwner(player, player == null ? null : player.GetComponent<ZNetView>()) || player.IsDead() || player.IsTeleporting()) return false;
                if (!Observe(player)) return false;
                Vector3 actual = player.transform.position;
                if (!Interior(actual)) { point = actual; return true; }
                if (!haveSurfaceEntry) return false;
                point = surfaceEntry; return true;
            }
            catch { return false; }
        }

        private static bool Observe(Player player)
        {
            if (!ReferenceEquals(player, observedPlayer))
            { observedPlayer = player; haveSurfaceEntry = false; surfaceEntry = Vector3.zero; }
            if (player == null) return false;
            Vector3 point = player.transform.position;
            if (!Finite(point)) return false;
            if (!player.IsDead() && !player.IsTeleporting() && !Interior(point))
            { surfaceEntry = point; haveSurfaceEntry = true; }
            return true;
        }

        private static bool LocalOwner(Player player, ZNetView view)
        { return player != null && ReferenceEquals(player, Player.m_localPlayer) && view != null && view.IsValid() && view.IsOwner(); }

        private static bool TryFrame(out GroupRadiusFrame frame)
        {
            frame = readFrame == null ? null : readFrame();
            if (frame == null || frame.Sequence <= 0 || !Finite(frame.LeaderPosition) || !Finite(frame.Radius) || frame.Radius <= 0f || frame.Radius > 100000f) return false;
            float now = Time.realtimeSinceStartup;
            if (!haveSequence || frame.Sequence != lastSequence)
            { haveSequence = true; lastSequence = frame.Sequence; sequenceAt = now; }
            return frame.Active && !frame.Exempt && now >= sequenceAt && now - sequenceAt < FreshnessSeconds;
        }

        internal static Vector3 ClipIntent(Vector3 point, Vector3 leader, float radius, Vector3 intent, float margin)
        {
            if (!Finite(point) || !Finite(leader) || !Finite(intent) || !Finite(radius) || radius <= 0f) return intent;
            float dx = point.x - leader.x, dz = point.z - leader.z;
            float distance = (float)Math.Sqrt((double)dx * dx + (double)dz * dz);
            if (distance < 0.001f || distance < radius - Math.Max(0f, margin)) return intent;
            float nx = dx / distance, nz = dz / distance;
            float outward = intent.x * nx + intent.z * nz;
            if (outward <= 0f) return intent;
            // Remove only the radial outward component. Native acceleration,
            // ground contact, gravity, damage and vertical motion stay untouched.
            intent.x -= nx * outward; intent.z -= nz * outward;
            return intent;
        }

        internal struct MotionState { internal bool Changed; internal Vector3 Original; }
        internal static void FilterMotion(Character character, ZNetView view, ref Vector3 intent, out MotionState state,
            float acceleration = 1f, bool swimming = false)
        {
            state = new MotionState();
            try
            {
                Player player = character as Player; GroupRadiusFrame frame;
                if (!LocalOwner(player, view) || player.IsDead() || player.IsTeleporting() || player.IsAttached() || player.IsSleeping()
                    || player.InCutscene() || player.IsDebugFlying() || !TryFrame(out frame) || Time.realtimeSinceStartup < frame.GraceUntil) return;
                Vector3 point = player.transform.position;
                if (!Finite(point) || Interior(point)) return;
                Vector3 velocity = player.GetVelocity();
                float speed = Finite(velocity) ? (float)Math.Sqrt((double)velocity.x * velocity.x + (double)velocity.z * velocity.z) : 0f;
                float step = Math.Min(0.1f, Math.Max(0.001f, Time.fixedDeltaTime));
                float braking;
                if (swimming)
                {
                    float blend = Math.Min(1f, Math.Max(0.001f, Finite(acceleration) ? acceleration : 0.05f));
                    braking = speed * step * (1f - blend) / blend;
                }
                else
                {
                    float change = Math.Max(0.001f, Finite(acceleration) ? acceleration : 1f);
                    braking = speed * speed * step / (2f * change);
                }
                float margin = Math.Min(frame.Radius * 0.2f, Math.Max(0.75f, braking + 0.5f));
                Vector3 clipped = ClipIntent(point, frame.LeaderPosition, frame.Radius, intent, margin);
                if (clipped.x == intent.x && clipped.z == intent.z) return;
                state.Changed = true; state.Original = intent; intent = clipped;
                Notice(player, frame, T("Граница радиуса группы. Можно идти вдоль границы или к лидеру.", "Group radius boundary. You can move along the boundary or toward the leader."));
            }
            catch { if (state.Changed) { intent = state.Original; state.Changed = false; } }
        }

        internal static void RestoreMotion(ref Vector3 intent, MotionState state)
        { if (state.Changed) intent = state.Original; }

        // Both native motors read m_moveDir. Restore it even after exceptions so
        // autorun/input state is preserved when the center moves or policy pauses.
        [HarmonyPatch(typeof(Character), "UpdateWalking")]
        private static class WalkingPatch
        {
            private static void Prefix(Character __instance, ZNetView ___m_nview, float ___m_acceleration, ref Vector3 ___m_moveDir, out MotionState __state)
            { FilterMotion(__instance, ___m_nview, ref ___m_moveDir, out __state, ___m_acceleration, false); }
            private static void Postfix(ref Vector3 ___m_moveDir, MotionState __state) { RestoreMotion(ref ___m_moveDir, __state); }
            private static Exception Finalizer(Exception __exception, ref Vector3 ___m_moveDir, MotionState __state)
            { RestoreMotion(ref ___m_moveDir, __state); return __exception; }
        }
        [HarmonyPatch(typeof(Character), "UpdateSwimming")]
        private static class SwimmingPatch
        {
            private static void Prefix(Character __instance, ZNetView ___m_nview, float ___m_swimAcceleration, ref Vector3 ___m_moveDir, out MotionState __state)
            { FilterMotion(__instance, ___m_nview, ref ___m_moveDir, out __state, ___m_swimAcceleration, true); }
            private static void Postfix(ref Vector3 ___m_moveDir, MotionState __state) { RestoreMotion(ref ___m_moveDir, __state); }
            private static Exception Finalizer(Exception __exception, ref Vector3 ___m_moveDir, MotionState __state)
            { RestoreMotion(ref ___m_moveDir, __state); return __exception; }
        }

        internal struct TeleportState { internal bool Entering; internal Player Player; internal Vector3 From; }
        internal static bool AllowTeleport(Player player, ZNetView view, Vector3 destination, bool distant, ref bool result, out TeleportState state)
        {
            state = new TeleportState();
            try
            {
                if (!LocalOwner(player, view)) return true;
                Observe(player); Vector3 source = player.transform.position;
                if (Finite(source) && Finite(destination) && !Interior(source) && Interior(destination))
                    state = new TeleportState { Entering = true, Player = player, From = source };
                GroupRadiusFrame frame;
                if (player.IsDead() || !TryFrame(out frame) || !Finite(destination) || Interior(destination)) return true;
                // An ordinary dungeon doorway may always return to its known
                // entrance, even if the leader moved while the party was inside.
                // A character loaded inside has no recorded entrance; allow its
                // ordinary doorway exit rather than trapping it in the dungeon.
                if (!distant && Interior(source) && (!haveSurfaceEntry || !Outside(destination, surfaceEntry, DungeonExitRange))) return true;
                if (!Outside(destination, frame.LeaderPosition, frame.Radius)) return true;
                result = false;
                Notice(player, frame, T("Портал ведёт за пределы радиуса группы. Дождись лидера или выбери другой портал.",
                    "This portal leads outside the group radius. Wait for the leader or choose another portal."));
                return false;
            }
            catch { return true; }
        }

        internal static void RecordTeleport(bool accepted, TeleportState state)
        {
            if (!accepted || !state.Entering || !ReferenceEquals(state.Player, Player.m_localPlayer)) return;
            observedPlayer = state.Player; surfaceEntry = state.From; haveSurfaceEntry = true;
        }

        [HarmonyPatch(typeof(Player), "TeleportTo")]
        private static class TeleportPatch
        {
            private static bool Prefix(Player __instance, ZNetView ___m_nview, Vector3 pos, bool distantTeleport, ref bool __result, out TeleportState __state)
            { return AllowTeleport(__instance, ___m_nview, pos, distantTeleport, ref __result, out __state); }
            private static void Postfix(bool __result, TeleportState __state) { RecordTeleport(__result, __state); }
        }

        private static bool LocalPilot(Ship ship, out Player player, out GroupRadiusFrame frame)
        {
            player = Player.m_localPlayer; frame = null;
            if (ship == null || !ship.IsOwner() || !LocalOwner(player, player == null ? null : player.GetComponent<ZNetView>())
                || player.IsDead() || player.IsTeleporting() || player.IsSleeping() || player.InCutscene() || player.IsDebugFlying()
                || !TryFrame(out frame) || Time.realtimeSinceStartup < frame.GraceUntil) return false;
            ShipControlls controls = player.GetDoodadController() as ShipControlls;
            return controls != null && ReferenceEquals(controls.m_ship, ship) && controls.IsValid() && controls.GetUser() == player.GetPlayerID();
        }

        private static bool OutwardThrottle(Ship ship, GroupRadiusFrame frame, bool backwards)
        {
            Vector3 position = ship.transform.position, direction = ship.transform.forward;
            if (!Finite(position) || !Finite(direction) || Interior(position)) return false;
            float dx = position.x - frame.LeaderPosition.x, dz = position.z - frame.LeaderPosition.z;
            float distance = (float)Math.Sqrt((double)dx * dx + (double)dz * dz);
            float margin = Math.Min(50f, frame.Radius * 0.2f);
            if (distance < frame.Radius - margin || distance < 0.001f) return false;
            float outward = (direction.x * dx + direction.z * dz) / distance;
            return backwards ? outward < -0.01f : outward > 0.01f;
        }

        internal static void FilterShipSpeed(Ship ship, ref Ship.Speed speed)
        {
            try
            {
                Player player; GroupRadiusFrame frame;
                if (speed == Ship.Speed.Stop || !LocalPilot(ship, out player, out frame) || !OutwardThrottle(ship, frame, speed == Ship.Speed.Back)) return;
                // Native UpdateControlls publishes the owner's setting normally.
                // No Stop RPC, velocity changes, force injection or detachment.
                speed = Ship.Speed.Stop;
                Notice(player, frame, T("Радиус группы: парус убран и тяга остановлена. Поверни к лидеру; лодку ещё может нести по инерции.",
                    "Group radius: sail and throttle stopped. Turn toward the leader; the boat may still drift."));
            }
            catch { }
        }

        internal static void FilterShipInput(Ship ship, Ship.Speed speed, ref Vector3 input)
        {
            try
            {
                Player player; GroupRadiusFrame frame;
                if (!LocalPilot(ship, out player, out frame)) return;
                // Reducing an existing setting remains available. Steering is
                // always passed through so the pilot can turn back safely.
                bool blocked = input.z > 0.5f && speed != Ship.Speed.Back && OutwardThrottle(ship, frame, false)
                    || input.z < -0.5f && (speed == Ship.Speed.Stop || speed == Ship.Speed.Back) && OutwardThrottle(ship, frame, true);
                if (blocked) input.z = 0f;
            }
            catch { }
        }

        [HarmonyPatch(typeof(Ship), "UpdateControlls")]
        private static class ShipSpeedPatch
        { private static void Prefix(Ship __instance, ref Ship.Speed ___m_speed) { FilterShipSpeed(__instance, ref ___m_speed); } }
        [HarmonyPatch(typeof(Ship), "ApplyControlls")]
        private static class ShipInputPatch
        { private static void Prefix(Ship __instance, Ship.Speed ___m_speed, ref Vector3 dir) { FilterShipInput(__instance, ___m_speed, ref dir); } }

        private static bool Outside(Vector3 point, Vector3 leader, float radius)
        { double x = (double)point.x - leader.x, z = (double)point.z - leader.z; return x * x + z * z > (double)radius * radius; }
        private static bool Interior(Vector3 point) { return point.y > InteriorHeight; }
        private static bool Finite(float value) { return !Single.IsNaN(value) && !Single.IsInfinity(value); }
        private static bool Finite(Vector3 value)
        { return Finite(value.x) && Finite(value.y) && Finite(value.z) && Math.Abs(value.x) <= 100000f && Math.Abs(value.y) <= 100000f && Math.Abs(value.z) <= 100000f; }
        private static string T(string ru, string en)
        { return Localization.instance != null && Localization.instance.GetSelectedLanguage() == "Russian" ? ru : en; }
        private static void Notice(Player player, GroupRadiusFrame frame, string message)
        {
            float now = Time.realtimeSinceStartup;
            if (now < noticeAt) return;
            noticeAt = now + 4f;
            try
            {
                if (frame.Notice != null) frame.Notice(message);
                else player.Message(MessageHud.MessageType.Center, message, 0, null, false);
            }
            catch { }
        }
    }
}
