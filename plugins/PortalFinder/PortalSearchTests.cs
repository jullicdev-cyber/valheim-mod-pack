using System;
using System.Collections.Generic;
using ValheimModPack.PortalFinder;

internal static class PortalSearchTests
{
    private static int checks;

    private static void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new Exception(message);
    }

    private static PortalRecord Portal(string id, string name, double x, double y, double z)
    {
        return new PortalRecord { Id = id, Name = name, X = x, Y = y, Z = z };
    }

    private static void Near(double actual, double expected, string message)
    {
        Check(Math.Abs(actual - expected) <= Math.Abs(expected) * 1e-12, message);
    }

    private static void Invalid(PortalRecord invalid, string message)
    {
        Check(PortalSearch.Find(new[] { invalid }, 0, 0) == null, message);
        PortalMatch match = PortalSearch.Find(new[] { invalid, Portal("ok", "Valid", 3, 0, 4) }, 0, 0);
        Check(match != null && match.Portal.Id == "ok" && match.Distance == 5,
            message + " does not hide valid records");
    }

    private static int Main()
    {
        try
        {
            Check(PortalSearch.Find(null, 0, 0) == null, "Absent world returns no match");
            Check(PortalSearch.Find(new PortalRecord[0], 0, 0) == null, "Empty world returns no match");

            PortalRecord nearby = Portal("near", "Near", 3, 999999, 4);
            PortalRecord farther = Portal("far", "Far", 6, 0, 8);
            PortalMatch match = PortalSearch.Find(new[] { farther, nearby }, 0, 0);
            Check(match.Portal.Id == "near" && match.Distance == 5, "Search ignores height and measures XZ distance");
            match = PortalSearch.Find(new[] { Portal("offset", "Offset", 13, -10, -16) }, 10, -20);
            Check(match.Distance == 5, "Distances use the supplied map click position");
            match = PortalSearch.Find(new[] { Portal("far", "Far", 2e8, 0, 0), Portal("remote", "Remote", 1e8, 0, 0) }, 0, 0);
            Check(match.Portal.Id == "remote" && match.Distance == 1e8, "Nearest remote portal has no scan radius cutoff");
            match = PortalSearch.Find(new[] { Portal("origin", "Origin", -12, 600, 9) }, -12, 9);
            Check(match.Distance == 0, "Exact XZ match has zero distance at any height");

            PortalRecord tieLower = Portal("A", "Uppercase ID", -3, 5, 4);
            PortalRecord tieHigher = Portal("a", "Lowercase ID", 3, -5, -4);
            Check(PortalSearch.Find(new[] { tieHigher, tieLower }, 0, 0).Portal.Id == "A", "Equal distances use ordinal ID tie break");
            Check(PortalSearch.Find(new[] { tieLower, tieHigher }, 0, 0).Portal.Id == "A", "Tie winner survives source reversal");

            Invalid(null, "Null records are ignored");
            Invalid(Portal(null, "Missing ID", 0, 0, 0), "Null IDs are ignored");
            Invalid(Portal("", "Empty ID", 0, 0, 0), "Empty IDs are ignored");
            Invalid(Portal(" \t", "Blank ID", 0, 0, 0), "Whitespace IDs are ignored");
            Invalid(Portal("bad", "NaN X", Double.NaN, 0, 0), "NaN X is ignored");
            Invalid(Portal("bad", "NaN Y", 0, Double.NaN, 0), "NaN Y is ignored even though height is excluded");
            Invalid(Portal("bad", "NaN Z", 0, 0, Double.NaN), "NaN Z is ignored");
            Invalid(Portal("bad", "Infinite X", Double.PositiveInfinity, 0, 0), "Infinite X is ignored");
            Invalid(Portal("bad", "Infinite Y", 0, Double.NegativeInfinity, 0), "Infinite Y is ignored");
            Invalid(Portal("bad", "Infinite Z", 0, 0, Double.PositiveInfinity), "Infinite Z is ignored");
            Check(PortalSearch.Find(new[] { nearby }, Double.NaN, 0) == null, "NaN click X is rejected");
            Check(PortalSearch.Find(new[] { nearby }, 0, Double.NaN) == null, "NaN click Z is rejected");
            Check(PortalSearch.Find(new[] { nearby }, Double.PositiveInfinity, 0) == null, "Infinite click X is rejected");
            Check(PortalSearch.Find(new[] { nearby }, 0, Double.NegativeInfinity) == null, "Infinite click Z is rejected");

            match = PortalSearch.Find(new[] { Portal("unnamed", "", 1, 0, 0), farther }, 0, 0);
            Check(match.Portal.Id == "unnamed" && match.Portal.Name == "", "Empty portal tags remain searchable");
            match = PortalSearch.Find(new[] { Portal("null-name", null, 1, 0, 0), farther }, 0, 0);
            Check(match.Portal.Id == "null-name" && match.Portal.Name == null, "Null portal tags remain searchable");

            PortalRecord duplicate = Portal("shared", "Zulu", 1, 0, 0);
            PortalRecord canonical = Portal("shared", "Alpha", 50, 7, 10);
            PortalRecord independent = Portal("independent", "Independent", 20, 0, 0);
            Check(PortalSearch.Find(new[] { duplicate, canonical, independent }, 0, 0).Portal.Id == "independent",
                "Duplicate canonicalization uses ordinal name before distance");
            Check(PortalSearch.Find(new[] { independent, canonical, duplicate }, 0, 0).Portal.Id == "independent",
                "Conflicting duplicate IDs produce the same winner after reversal");
            match = PortalSearch.Find(new[] { duplicate, canonical, canonical }, 0, 0);
            Check(match.Portal.Name == "Alpha" && match.Portal.X == 50 && match.Portal.Y == 7 && match.Portal.Z == 10,
                "Repeated duplicate copies yield one deterministic complete record");
            match = PortalSearch.Find(new[] { Portal("same", "Name", 4, 0, 2), Portal("same", "Name", -4, 8, 3) }, 0, 0);
            Check(match.Portal.X == -4 && match.Portal.Z == 3 && match.Portal.Y == 8, "Duplicate coordinate tie break uses X first");
            match = PortalSearch.Find(new[] { Portal("same", "Name", 4, 0, 2), Portal("same", "Name", 4, 8, -3) }, 0, 0);
            Check(match.Portal.Z == -3 && match.Portal.Y == 8, "Duplicate coordinate tie break uses Z second");
            match = PortalSearch.Find(new[] { Portal("same", "Name", 4, 8, 2), Portal("same", "Name", 4, -8, 2) }, 0, 0);
            Check(match.Portal.Y == -8, "Duplicate coordinate tie break uses Y last");
            match = PortalSearch.Find(new[] { Portal("same", "", 1, 0, 0), Portal("same", null, 2, 0, 0) }, 0, 0);
            Check(match.Portal.Name == null && match.Distance == 2, "Null duplicate name sorts before an empty name");
            match = PortalSearch.Find(new[] { Portal("same", "Broken", Double.NaN, 0, 0), Portal("same", "Valid", 3, 0, 4) }, 0, 0);
            Check(match.Portal.Name == "Valid" && match.Distance == 5, "Invalid duplicate cannot shadow a valid portal");
            match = PortalSearch.Find(new[] { Portal("case", "One", 0, 0, 0), Portal("CASE", "Two", 1, 0, 0) }, 0, 0);
            Check(match.Portal.Id == "case", "Deduplication respects case-sensitive ordinal identity");

            var world = new List<PortalRecord> {
                Portal("D", "South", 0, 100, -10), Portal("B", "East", 10, 5, 0),
                Portal("A", "North", 0, -100, 10), Portal("C", "West", -10, 0, 0),
                Portal("A", "North", 0, -100, 10)
            };
            for (int i = 0; i < world.Count; i++)
            {
                Check(PortalSearch.Find(world, 0, 0).Portal.Id == "A", "Tie and duplicate winner survives rotation " + i);
                PortalRecord first = world[0]; world.RemoveAt(0); world.Add(first);
            }

            PortalRecord mutable = Portal("snapshot", "Original", 3, 7, 4);
            match = PortalSearch.Find(new[] { mutable }, 0, 0);
            Check(!Object.ReferenceEquals(match.Portal, mutable), "Returned portal owns its record");
            Check(mutable.Id == "snapshot" && mutable.Name == "Original" && mutable.X == 3 && mutable.Y == 7 && mutable.Z == 4,
                "Search preserves the source record");
            mutable.Id = "changed"; mutable.Name = "Changed"; mutable.X = 100; mutable.Y = 200; mutable.Z = 300;
            Check(match.Portal.Id == "snapshot" && match.Portal.Name == "Original" && match.Portal.X == 3
                && match.Portal.Y == 7 && match.Portal.Z == 4 && match.Distance == 5,
                "Later caller mutation cannot change the match name, coordinates or distance");
            PortalMatch second = PortalSearch.Find(new[] { mutable }, 0, 0);
            match.Portal.X = -1;
            Check(second.Portal.X == 100 && mutable.X == 100, "Different results and source records do not share mutable state");

            match = PortalSearch.Find(new[] { Portal("A", "Farther", 2e200, 0, 0), Portal("Z", "Closer", 1e200, 0, 0) }, 0, 0);
            Check(match.Portal.Id == "Z", "Squared-distance overflow cannot select a farther ordinal ID");
            Near(match.Distance, 1e200, "Large finite distance is retained after squaring overflows");
            match = PortalSearch.Find(new[] { Portal("A", "Farther", -Double.MaxValue, 0, 0), Portal("Z", "Closer", 0, 0, 0) }, Double.MaxValue, 0);
            Check(match.Portal.Id == "Z" && match.Distance == Double.MaxValue, "Finite coordinates survive subtraction overflow for another candidate");
            match = PortalSearch.Find(new[] { Portal("A", "Farther", -Double.MaxValue, 0, -Double.MaxValue),
                Portal("Z", "Closer", -Double.MaxValue, 0, 0) }, Double.MaxValue, Double.MaxValue);
            Check(match.Portal.Id == "Z" && Double.IsPositiveInfinity(match.Distance), "Nearest selection remains correct when every true distance exceeds double range");
            match = PortalSearch.Find(new[] { Portal("A", "Farther", 2e-200, 0, 0), Portal("Z", "Closer", 1e-200, 0, 0) }, 0, 0);
            Check(match.Portal.Id == "Z", "Squared-distance underflow does not create a false ordinal tie");
            Near(match.Distance, 1e-200, "Tiny nonzero distance is preserved");
            match = PortalSearch.Find(new[] { Portal("zero", "Zero", -0.0, -0.0, -0.0), Portal("zero", "Zero", 0, 0, 0) }, 0, 0);
            Check(BitConverter.DoubleToInt64Bits(match.Portal.X) == 0 && BitConverter.DoubleToInt64Bits(match.Portal.Y) == 0
                && BitConverter.DoubleToInt64Bits(match.Portal.Z) == 0, "Signed-zero duplicates canonicalize identically");

            Console.WriteLine("PASS " + checks + " PortalSearch checks: XZ nearest search, deterministic identities, invalid data and owned snapshots.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
