using System;
using System.Collections.Generic;
using System.IO;

namespace ValheimModPack.PartyPrison
{
    // No game types: the host supplies positions and authenticated platform accounts.
    public struct PrisonPoint
    {
        public double X, Y, Z;
        public PrisonPoint(double x, double y, double z) { X = x; Y = y; Z = z; }
    }

    public sealed class PrisonRegion
    {
        public PrisonPoint Center, CellSpawn, ArenaSpawn;
        public double Radius, HalfHeight;

        public PrisonRegion Copy()
        {
            return new PrisonRegion { Center = Center, CellSpawn = CellSpawn, ArenaSpawn = ArenaSpawn,
                Radius = Radius, HalfHeight = HalfHeight };
        }

        public bool Contains(PrisonPoint point)
        {
            if (!SentencePolicy.IsFinitePoint(point)) return false;
            double x = point.X - Center.X, z = point.Z - Center.Z;
            return x * x + z * z <= Radius * Radius && Math.Abs(point.Y - Center.Y) <= HalfHeight;
        }
    }

    public sealed class SentenceState
    {
        public string AccountId = "", SentenceId = "", PlayerName = "", Reason = "";
        public long Revision;
        public double RemainingSeconds;
        public PrisonPoint ReturnPosition;
        public bool PendingRelease, EmergencyRelease;

        public SentenceState Copy()
        {
            return new SentenceState { AccountId = AccountId, SentenceId = SentenceId, PlayerName = PlayerName,
                Reason = Reason, Revision = Revision, RemainingSeconds = RemainingSeconds,
                ReturnPosition = ReturnPosition, PendingRelease = PendingRelease, EmergencyRelease = EmergencyRelease };
        }
    }

    public static class SentencePolicy
    {
        public const double MaximumDurationSeconds = 30 * 24 * 60 * 60;
        public const double MaximumTickSeconds = 5;
        public const int MaximumSentences = 512;
        public const int MaximumAccountCharacters = 64;
        public const int MaximumNameCharacters = 100;
        public const int MaximumReasonCharacters = 300;
        private const double MaximumCoordinate = 1000000;

        public static bool IsFinite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }

        public static bool IsFinitePoint(PrisonPoint point)
        {
            return IsFinite(point.X) && IsFinite(point.Y) && IsFinite(point.Z)
                && Math.Abs(point.X) <= MaximumCoordinate && Math.Abs(point.Y) <= MaximumCoordinate
                && Math.Abs(point.Z) <= MaximumCoordinate;
        }

        public static void RequireAccountId(string value)
        {
            // The native adapter obtains this value from the authenticated peer,
            // never from a character name, a character ID, or RPC payload identity.
            if (value == null || value.Length > MaximumAccountCharacters)
                throw new InvalidDataException("Authenticated platform account is required.");
            if (value.StartsWith("Steam_", StringComparison.Ordinal))
            {
                if (value.Length != 23) throw new InvalidDataException("Invalid Steam account.");
                ulong account;
                if (!UInt64.TryParse(value.Substring(6), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out account) || account == 0)
                    throw new InvalidDataException("Invalid Steam account.");
                return;
            }
            if (value.StartsWith("PlayFab_", StringComparison.Ordinal))
            {
                int length = value.Length - 8;
                if (length < 16 || length > 32) throw new InvalidDataException("Invalid PlayFab account.");
                bool nonzero = false;
                for (int i = 8; i < value.Length; ++i)
                {
                    char c = value[i];
                    if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F')))
                        throw new InvalidDataException("PlayFab account must use canonical uppercase hexadecimal.");
                    nonzero |= c != '0';
                }
                if (!nonzero) throw new InvalidDataException("Invalid PlayFab account.");
                return;
            }
            throw new InvalidDataException("Unsupported authenticated account provider.");
        }

        public static void RequireRegion(PrisonRegion region)
        {
            if (region == null || !IsFinitePoint(region.Center) || !IsFinitePoint(region.CellSpawn)
                || !IsFinitePoint(region.ArenaSpawn) || !IsFinite(region.Radius) || region.Radius < 6 || region.Radius > 100
                || !IsFinite(region.HalfHeight) || region.HalfHeight < 4 || region.HalfHeight > 100
                || !region.Contains(region.CellSpawn) || !region.Contains(region.ArenaSpawn))
                throw new InvalidDataException("Invalid prison region or spawn position.");
        }

        public static void RequireDuration(double seconds)
        {
            if (!IsFinite(seconds) || seconds < 1 || seconds > MaximumDurationSeconds)
                throw new InvalidDataException("Prison time must be from one second to thirty days of active play.");
        }

        public static void RequireTick(double seconds)
        {
            if (!IsFinite(seconds) || seconds < 0 || seconds > MaximumTickSeconds)
                throw new InvalidDataException("Active play tick must be between zero and five seconds.");
        }

        public static void RequireSentence(SentenceState sentence)
        {
            if (sentence == null) throw new InvalidDataException("Missing sentence.");
            RequireAccountId(sentence.AccountId);
            Guid token;
            if (sentence.SentenceId == null || !Guid.TryParseExact(sentence.SentenceId, "N", out token)
                || token == Guid.Empty || sentence.SentenceId != token.ToString("N") || sentence.Revision < 1
                || !IsFinite(sentence.RemainingSeconds) || sentence.RemainingSeconds < 0
                || sentence.RemainingSeconds > MaximumDurationSeconds || !IsFinitePoint(sentence.ReturnPosition)
                || sentence.PendingRelease != (sentence.RemainingSeconds == 0) || sentence.EmergencyRelease && !sentence.PendingRelease)
                throw new InvalidDataException("Invalid sentence state.");
            RequireText(sentence.PlayerName, MaximumNameCharacters, "player name");
            RequireText(sentence.Reason, MaximumReasonCharacters, "sentence reason");
        }

        internal static void RequireText(string value, int maximum, string label)
        {
            if (value == null || value.Length > maximum) throw new InvalidDataException("Invalid " + label + ".");
            for (int i = 0; i < value.Length; ++i)
            {
                char c = value[i];
                if (Char.IsControl(c) || Char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)
                    throw new InvalidDataException("Invalid control character in " + label + ".");
                if (Char.IsHighSurrogate(c))
                {
                    if (++i >= value.Length || !Char.IsLowSurrogate(value[i]))
                        throw new InvalidDataException("Invalid Unicode in " + label + ".");
                }
                else if (Char.IsLowSurrogate(c)) throw new InvalidDataException("Invalid Unicode in " + label + ".");
            }
        }
    }
}
