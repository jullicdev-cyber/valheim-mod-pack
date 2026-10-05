using System;
using System.IO;
using System.Text;
using System.Collections.Generic;

namespace ValheimModPack.InventoryAdmin
{
    public sealed class GroupRadiusSettings
    {
        public bool Enabled;
        public float Radius = 500f;
        public string LeaderOwner = GroupRadiusPolicy.LocalHostOwner;
        public HashSet<string> ExemptOwners = new HashSet<string>(StringComparer.Ordinal);
        public long Generation;

        public GroupRadiusSettings Clone()
        {
            return new GroupRadiusSettings { Enabled = Enabled, Radius = Radius, LeaderOwner = LeaderOwner,
                Generation = Generation, ExemptOwners = ExemptOwners == null ? null : new HashSet<string>(ExemptOwners, StringComparer.Ordinal) };
        }
    }

    // An ordinary participant receives only their own exemption. The complete
    // exemption set belongs to settings and is sent only to authorized editors.
    public sealed class GroupRadiusState
    {
        public long World, Generation, LeaderEpoch;
        public bool Enabled, LeaderValid, LeaderAlive, OwnExempt, OwnIsLeader;
        public float Radius = 500f, LeaderX, LeaderY, LeaderZ;
        public string LeaderOwner = GroupRadiusPolicy.LocalHostOwner, LeaderName = "";
        public int ApprovedConnectedCount;
        public GroupRadiusState Clone() { return (GroupRadiusState)MemberwiseClone(); }
    }

    public static class GroupRadiusPolicy
    {
        public const string LocalHostOwner = "local-host";
        public const float MinimumRadius = 50f, MaximumRadius = 10000f, MaximumCoordinate = 100000f;
        public const int MaximumPlayers = 128, MaximumExemptions = 128, MaximumNameBytes = 512;
        public const double FreshnessSeconds = 1.5, RecoveryGraceSeconds = 10;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public static void RequireLeaderOwner(string owner)
        { if (owner != LocalHostOwner) PermissionPolicy.RequireSteamOwner(owner); }

        public static void ValidateSettings(GroupRadiusSettings settings)
        {
            if (settings == null || settings.Generation < 0) throw new InvalidDataException("Invalid group radius settings generation.");
            RequireRadius(settings.Radius); RequireLeaderOwner(settings.LeaderOwner);
            if (settings.ExemptOwners == null || settings.ExemptOwners.Count > MaximumExemptions)
                throw new InvalidDataException("Invalid group radius exemption count.");
            foreach (string owner in settings.ExemptOwners) RequireLeaderOwner(owner);
        }

        public static void ValidateState(GroupRadiusState state)
        {
            if (state == null || state.World == 0 || state.Generation < 0 || state.LeaderEpoch < 0
                || state.ApprovedConnectedCount < 0 || state.ApprovedConnectedCount > MaximumPlayers)
                throw new InvalidDataException("Invalid group radius state identity or count.");
            RequireRadius(state.Radius); RequireLeaderOwner(state.LeaderOwner);
            if (state.LeaderName == null || state.LeaderName.IndexOf('\0') >= 0)
                throw new InvalidDataException("Invalid group radius leader name.");
            try
            {
                if (Utf8.GetByteCount(state.LeaderName) > MaximumNameBytes) throw new InvalidDataException("Oversized group radius leader name.");
            }
            catch (EncoderFallbackException error) { throw new InvalidDataException("Invalid group radius leader text.", error); }
            if (!Coordinate(state.LeaderX) || !Coordinate(state.LeaderY) || !Coordinate(state.LeaderZ)
                || (state.LeaderAlive && !state.LeaderValid))
                throw new InvalidDataException("Invalid group radius leader position or availability.");
        }

        public static void RequireRadius(float radius)
        {
            if (!Finite(radius) || radius < MinimumRadius || radius > MaximumRadius)
                throw new InvalidDataException("Group radius must be between 50 and 10000 metres.");
        }

        public static bool Coordinate(float value)
        { return Finite(value) && value >= -MaximumCoordinate && value <= MaximumCoordinate; }

        public static bool Fresh(double receivedAt, double now)
        { return Time(receivedAt) && Time(now) && now >= receivedAt && now - receivedAt < FreshnessSeconds; }

        public static bool Active(GroupRadiusState state, double receivedAt, double now)
        {
            if (state == null || !Fresh(receivedAt, now) || !state.Enabled || state.ApprovedConnectedCount <= 1
                || !state.LeaderValid || !state.LeaderAlive || state.OwnExempt || state.OwnIsLeader) return false;
            try { ValidateState(state); return true; }
            catch (InvalidDataException) { return false; }
        }

        public static float WarningDistance(float radius)
        { RequireRadius(radius); return radius * 0.9f; }

        public static double DistanceXZ(float x, float z, float centerX, float centerZ)
        {
            if (!Coordinate(x) || !Coordinate(z) || !Coordinate(centerX) || !Coordinate(centerZ))
                throw new InvalidDataException("Invalid horizontal group radius coordinates.");
            double dx = (double)x - centerX, dz = (double)z - centerZ;
            return Math.Sqrt(dx * dx + dz * dz);
        }

        public static bool Warning(GroupRadiusState state, double receivedAt, double now, float x, float z)
        {
            return Active(state, receivedAt, now) && Coordinate(x) && Coordinate(z)
                && DistanceXZ(x, z, state.LeaderX, state.LeaderZ) >= WarningDistance(state.Radius);
        }

        // Used for input or velocity at the boundary. Removing the outward
        // radial component leaves tangential motion and motion toward the leader.
        public static bool ConstrainOutward(float centerX, float centerZ, float radius, float x, float z,
            float moveX, float moveZ, out float constrainedX, out float constrainedZ)
        {
            constrainedX = moveX; constrainedZ = moveZ;
            RequireRadius(radius);
            if (!Coordinate(centerX) || !Coordinate(centerZ) || !Coordinate(x) || !Coordinate(z)
                || !Finite(moveX) || !Finite(moveZ)) return false;
            double rx = (double)x - centerX, rz = (double)z - centerZ;
            double distance = Math.Sqrt(rx * rx + rz * rz);
            if (distance < radius || distance == 0) return false;
            double nx = rx / distance, nz = rz / distance, outward = nx * moveX + nz * moveZ;
            if (outward <= 0) return false;
            constrainedX = (float)(moveX - nx * outward); constrainedZ = (float)(moveZ - nz * outward);
            return true;
        }

        // Used for a proposed horizontal displacement. Crossing from inside
        // clips the positive radial component; ordinary tangent travel is kept
        // with the small inward adjustment required by the circular boundary.
        public static bool ConstrainMovement(GroupRadiusState state, double receivedAt, double now, float x, float z,
            float moveX, float moveZ, out float constrainedX, out float constrainedZ)
        {
            constrainedX = moveX; constrainedZ = moveZ;
            if (!Active(state, receivedAt, now) || !Coordinate(x) || !Coordinate(z) || !Finite(moveX) || !Finite(moveZ)) return false;
            double rx = (double)x - state.LeaderX, rz = (double)z - state.LeaderZ;
            double distance = Math.Sqrt(rx * rx + rz * rz), radius = state.Radius;
            if (distance >= radius)
                return ConstrainOutward(state.LeaderX, state.LeaderZ, state.Radius, x, z, moveX, moveZ, out constrainedX, out constrainedZ);
            double endX = rx + moveX, endZ = rz + moveZ;
            if (endX * endX + endZ * endZ <= radius * radius) return false;
            double moveLength = Math.Sqrt((double)moveX * moveX + (double)moveZ * moveZ);
            if (distance == 0)
            {
                if (moveLength == 0) return false;
                constrainedX = (float)(moveX * radius / moveLength); constrainedZ = (float)(moveZ * radius / moveLength); return true;
            }
            double nx = rx / distance, nz = rz / distance, outward = nx * moveX + nz * moveZ;
            if (outward <= 0) return false;
            double tangentX = moveX - nx * outward, tangentZ = moveZ - nz * outward;
            double tangentSquared = tangentX * tangentX + tangentZ * tangentZ;
            if (tangentSquared <= radius * radius)
            {
                double permittedOutward = Math.Sqrt(Math.Max(0, radius * radius - tangentSquared)) - distance;
                constrainedX = (float)(tangentX + nx * permittedOutward); constrainedZ = (float)(tangentZ + nz * permittedOutward);
            }
            else
            {
                // A very large step cannot preserve its full tangent inside a
                // finite circle. Stop at the first intersection of the step.
                double a = (double)moveX * moveX + (double)moveZ * moveZ;
                double b = 2 * (rx * moveX + rz * moveZ), c = distance * distance - radius * radius;
                double fraction = (-b + Math.Sqrt(Math.Max(0, b * b - 4 * a * c))) / (2 * a);
                fraction = Math.Max(0, Math.Min(1, fraction));
                constrainedX = (float)(moveX * fraction); constrainedZ = (float)(moveZ * fraction);
            }
            return true;
        }

        private static bool Finite(float value) { return !Single.IsNaN(value) && !Single.IsInfinity(value); }
        private static bool Time(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value) && value >= 0; }
    }

    public enum GroupRadiusRecoveryStatus { None, Grace, Ready, Attempted }

    // Pure grace scheduling when a participant starts outside the circle or the
    // leader jumps. Ready means ordinary outward filtering may resume; this
    // class never requests or performs an automatic return or teleport.
    public sealed class GroupRadiusRecovery
    {
        private long world, generation, epoch;
        private string owner = "";
        private float centerX, centerZ;
        private bool hasCenter, spent, pending;
        private double readyAt;
        public GroupRadiusRecoveryStatus Status { get; private set; }
        public double GraceUntil { get { return pending ? readyAt : 0; } }

        public GroupRadiusRecoveryStatus Observe(GroupRadiusState state, double receivedAt, double now, float x, float z)
        {
            if (!GroupRadiusPolicy.Active(state, receivedAt, now) || !GroupRadiusPolicy.Coordinate(x) || !GroupRadiusPolicy.Coordinate(z))
            { pending = false; Status = GroupRadiusRecoveryStatus.None; return Status; }
            bool newContext = !hasCenter || world != state.World || generation != state.Generation || owner != state.LeaderOwner;
            bool jumped = !newContext && (epoch != state.LeaderEpoch
                || GroupRadiusPolicy.DistanceXZ(centerX, centerZ, state.LeaderX, state.LeaderZ) > Math.Max(50, state.Radius * 0.25));
            if (newContext || jumped)
            {
                world = state.World; generation = state.Generation; owner = state.LeaderOwner; epoch = state.LeaderEpoch;
                spent = false; pending = true; readyAt = now + GroupRadiusPolicy.RecoveryGraceSeconds;
            }
            centerX = state.LeaderX; centerZ = state.LeaderZ; hasCenter = true;
            if (pending && now < readyAt) { Status = GroupRadiusRecoveryStatus.Grace; return Status; }
            if (GroupRadiusPolicy.DistanceXZ(x, z, state.LeaderX, state.LeaderZ) <= state.Radius)
            { Status = GroupRadiusRecoveryStatus.None; return Status; }
            if (spent) { Status = GroupRadiusRecoveryStatus.Attempted; return Status; }
            Status = GroupRadiusRecoveryStatus.Ready;
            return Status;
        }

        public bool TryConsumeRecovery()
        {
            if (!pending || Status != GroupRadiusRecoveryStatus.Ready || spent) return false;
            spent = true; pending = false; Status = GroupRadiusRecoveryStatus.Attempted; return true;
        }

        public void Reset()
        { world = generation = epoch = 0; owner = ""; hasCenter = spent = pending = false; readyAt = 0; Status = GroupRadiusRecoveryStatus.None; }
    }

    public static class GroupRadiusCodec
    {
        public const int MaximumPacketBytes = 16 * 1024;
        private const int Magic = 0x49414731, Version = 1;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public static byte[] EncodeSettings(GroupRadiusSettings settings)
        {
            GroupRadiusPolicy.ValidateSettings(settings);
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Utf8))
            {
                Header(writer, 1); writer.Write(settings.Generation); writer.Write(settings.Enabled); writer.Write(settings.Radius);
                PolicyBinary.WriteString(writer, settings.LeaderOwner, 40);
                var owners = new List<string>(settings.ExemptOwners); owners.Sort(StringComparer.Ordinal); writer.Write(owners.Count);
                foreach (string owner in owners) PolicyBinary.WriteString(writer, owner, 40);
                writer.Flush(); return Finish(stream);
            }
        }

        public static GroupRadiusSettings DecodeSettings(byte[] bytes)
        {
            try
            {
                using (var stream = Open(bytes))
                using (var reader = new BinaryReader(stream, Utf8))
                {
                    Header(reader, 1);
                    var settings = new GroupRadiusSettings { Generation = reader.ReadInt64(), Enabled = Flag(reader), Radius = reader.ReadSingle(),
                        LeaderOwner = PolicyBinary.ReadString(reader, 40) };
                    int count = reader.ReadInt32();
                    if (count < 0 || count > GroupRadiusPolicy.MaximumExemptions) throw new InvalidDataException("Invalid group radius exemption count.");
                    for (int i = 0; i < count; ++i)
                        if (!settings.ExemptOwners.Add(PolicyBinary.ReadString(reader, 40))) throw new InvalidDataException("Duplicate group radius exemption.");
                    End(stream); GroupRadiusPolicy.ValidateSettings(settings); return settings;
                }
            }
            catch (EndOfStreamException error) { throw new InvalidDataException("Truncated group radius settings packet.", error); }
            catch (DecoderFallbackException error) { throw new InvalidDataException("Invalid group radius settings text.", error); }
        }

        public static byte[] EncodeState(GroupRadiusState state)
        {
            GroupRadiusPolicy.ValidateState(state);
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Utf8))
            {
                Header(writer, 2); writer.Write(state.World); writer.Write(state.Generation); writer.Write(state.Enabled); writer.Write(state.Radius);
                PolicyBinary.WriteString(writer, state.LeaderOwner, 40); PolicyBinary.WriteString(writer, state.LeaderName, GroupRadiusPolicy.MaximumNameBytes);
                writer.Write(state.ApprovedConnectedCount); writer.Write(state.LeaderValid); writer.Write(state.LeaderAlive);
                writer.Write(state.LeaderX); writer.Write(state.LeaderY); writer.Write(state.LeaderZ); writer.Write(state.LeaderEpoch);
                writer.Write(state.OwnExempt); writer.Write(state.OwnIsLeader); writer.Flush(); return Finish(stream);
            }
        }

        public static GroupRadiusState DecodeState(byte[] bytes)
        {
            try
            {
                using (var stream = Open(bytes))
                using (var reader = new BinaryReader(stream, Utf8))
                {
                    Header(reader, 2);
                    var state = new GroupRadiusState { World = reader.ReadInt64(), Generation = reader.ReadInt64(), Enabled = Flag(reader), Radius = reader.ReadSingle(),
                        LeaderOwner = PolicyBinary.ReadString(reader, 40), LeaderName = PolicyBinary.ReadString(reader, GroupRadiusPolicy.MaximumNameBytes),
                        ApprovedConnectedCount = reader.ReadInt32(), LeaderValid = Flag(reader), LeaderAlive = Flag(reader),
                        LeaderX = reader.ReadSingle(), LeaderY = reader.ReadSingle(), LeaderZ = reader.ReadSingle(), LeaderEpoch = reader.ReadInt64(),
                        OwnExempt = Flag(reader), OwnIsLeader = Flag(reader) };
                    End(stream); GroupRadiusPolicy.ValidateState(state); return state;
                }
            }
            catch (EndOfStreamException error) { throw new InvalidDataException("Truncated group radius state packet.", error); }
            catch (DecoderFallbackException error) { throw new InvalidDataException("Invalid group radius leader text.", error); }
        }

        private static void Header(BinaryWriter writer, int kind) { writer.Write(Magic); writer.Write(Version); writer.Write(kind); }
        private static void Header(BinaryReader reader, int kind)
        { if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version || reader.ReadInt32() != kind) throw new InvalidDataException("Unknown group radius wire format."); }
        private static bool Flag(BinaryReader reader)
        { byte value = reader.ReadByte(); if (value > 1) throw new InvalidDataException("Invalid group radius boolean."); return value == 1; }
        private static MemoryStream Open(byte[] bytes)
        { if (bytes == null || bytes.Length < 12 || bytes.Length > MaximumPacketBytes) throw new InvalidDataException("Invalid group radius packet size."); return new MemoryStream(bytes, false); }
        private static byte[] Finish(MemoryStream stream)
        { if (stream.Length > MaximumPacketBytes) throw new InvalidDataException("Oversized group radius packet."); return stream.ToArray(); }
        private static void End(MemoryStream stream)
        { if (stream.Position != stream.Length) throw new InvalidDataException("Trailing group radius packet data."); }
    }
}
