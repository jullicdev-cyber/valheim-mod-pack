using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using XPortal.Plus;

internal static class PortalListTests
{
    private static int checks;

    private static void Main()
    {
        CultureInfo original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            SortingAndUnknownDates();
            DistanceAndCurrentPortal();
            SearchAndCulture();
            BiomeGrouping();
            DetachedView();
            Validation();
            Console.WriteLine("AnyPortal+ list checks passed: " + checks);
        }
        finally { Thread.CurrentThread.CurrentCulture = original; }
    }

    private static PortalEntry Entry(string id, string name, string biome, double x, double y, double z, long created)
    {
        return new PortalEntry { Id = id, Name = name, Biome = biome, X = x, Y = y, Z = z, CreatedUtcTicks = created };
    }

    private static List<PortalEntry> Query(IList<PortalEntry> entries, PortalSort sort, bool descending, bool groups)
    {
        return PortalListModel.Query(entries, null, 0, 0, 0, null, sort, descending, groups);
    }

    private static void SortingAndUnknownDates()
    {
        List<PortalEntry> entries = new List<PortalEntry>
        {
            Entry("old", "База", "Meadows", 0, 0, 0, 10),
            Entry("unknown-z", "Остров", "Meadows", 0, 0, 0, 0),
            Entry("new", "Рудник", "Meadows", 0, 0, 0, 30),
            Entry("middle", "Лагерь", "Meadows", 0, 0, 0, 20),
            Entry("unknown-a", "Лес", "Meadows", 0, 0, 0, 0)
        };
        Ids(Query(entries, PortalSort.Created, false, false), "old,middle,new,unknown-a,unknown-z");
        Ids(Query(entries, PortalSort.Created, true, false), "new,middle,old,unknown-a,unknown-z");
        Ids(Query(entries, PortalSort.Name, false, false), "old,middle,unknown-a,unknown-z,new");
        Ids(Query(entries, PortalSort.Name, true, false), "new,unknown-z,unknown-a,middle,old");

        List<PortalEntry> tied = new List<PortalEntry>
        {
            Entry("b", "база", "Meadows", 5, 0, 0, 10),
            Entry("a", "БАЗА", "Meadows", -5, 0, 0, 10)
        };
        foreach (PortalSort sort in Enum.GetValues(typeof(PortalSort)))
        {
            Ids(Query(tied, sort, false, false), "a,b");
            Ids(Query(tied, sort, true, false), "a,b");
        }
    }

    private static void DistanceAndCurrentPortal()
    {
        List<PortalEntry> entries = new List<PortalEntry>
        {
            Entry("self", "Текущий", "Meadows", 100, 10, 100, 0),
            Entry("vertical", "Башня", "Meadows", 100, 30, 100, 0),
            Entry("near", "База", "Meadows", 103, 14, 100, 0),
            Entry("far", "Остров", "Meadows", 200, 10, 100, 0)
        };
        Ids(PortalListModel.Query(entries, "self", 100, 10, 100, "", PortalSort.Distance, false, false), "near,vertical,far");
        Ids(PortalListModel.Query(entries, "self", 100, 10, 100, "", PortalSort.Distance, true, false), "far,vertical,near");
        Check(Math.Abs(PortalListModel.Distance(entries[2], 100, 10, 100) - 5) < 0.000001, "3D distance");
        Check(PortalListModel.Distance(entries[0], 100, 10, 100) == 0, "zero distance");
        PortalEntry huge = Entry("huge", "", "", 1e150, 1e150, 0, 0);
        Check(!double.IsInfinity(PortalListModel.Distance(huge, 0, 0, 0)), "distance scaling avoids squared overflow");
        Check(PortalListModel.Query(entries, "self", 100, 10, 100, "Текущий", PortalSort.Name, false, false).Count == 0,
            "self remains excluded even when search matches");
    }

    private static void SearchAndCulture()
    {
        List<PortalEntry> entries = new List<PortalEntry>
        {
            Entry("ru", "МАЛИНА у реки", "Meadows", 0, 0, 0, 0),
            Entry("accent", "Café du port", "Meadows", 0, 0, 0, 0),
            Entry("plain", "Cafe forest", "BlackForest", 0, 0, 0, 0),
            Entry("other", "Остров", "Ocean", 0, 0, 0, 0),
            Entry("untagged", null, null, 0, 0, 0, 0)
        };
        Ids(PortalListModel.Query(entries, null, 0, 0, 0, "  малина  ", PortalSort.Name, false, false), "ru");
        Ids(PortalListModel.Query(entries, null, 0, 0, 0, "CAFE", PortalSort.Name, false, false), "accent,plain");
        Check(PortalListModel.Query(entries, null, 0, 0, 0, " ", PortalSort.Name, false, false).Count == 5, "blank search includes all portals");
        Check(PortalListModel.Query(entries, null, 0, 0, 0, "Meadows", PortalSort.Name, false, false).Count == 0, "search is portal name only");
        Check(PortalListModel.Query(entries, null, 0, 0, 0, "missing", PortalSort.Name, false, false).Count == 0, "no-match search");

        Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        List<PortalEntry> turkish = new List<PortalEntry> { Entry("i", "İSTANBUL", "Meadows", 0, 0, 0, 0) };
        Ids(PortalListModel.Query(turkish, null, 0, 0, 0, "istanbul", PortalSort.Name, false, false), "i");
        Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
    }

    private static void BiomeGrouping()
    {
        List<PortalEntry> entries = new List<PortalEntry>
        {
            Entry("p", "А", "Plains", 2, 0, 0, 20),
            Entry("m-far", "Я", "Meadows", 10, 0, 0, 10),
            Entry("f", "А", "BlackForest", 1, 0, 0, 30),
            Entry("m-near", "А", "Meadows", 1, 0, 0, 0),
            Entry("o", "А", "Ocean", 1, 0, 0, 0),
            Entry("custom-z", "А", "CustomZ", 1, 0, 0, 0),
            Entry("custom-a", "А", "CustomA", 1, 0, 0, 0)
        };
        Ids(Query(entries, PortalSort.Distance, false, true), "m-near,m-far,f,p,o,custom-a,custom-z");
        Ids(Query(entries, PortalSort.Distance, true, true), "m-far,m-near,f,p,o,custom-a,custom-z");
        Ids(Query(entries, PortalSort.Created, true, true), "m-far,m-near,f,p,o,custom-a,custom-z");
        Ids(Query(entries, PortalSort.Name, false, true), "m-near,m-far,f,p,o,custom-a,custom-z");
        Check(Query(entries, PortalSort.Name, false, false)[0].Id != "m-near", "grouping can be disabled");
    }

    private static void DetachedView()
    {
        PortalEntry original = Entry("1", "База", "Meadows", 3, 4, 5, 6);
        original.Icon = 4;
        List<PortalEntry> input = new List<PortalEntry> { original, Entry("2", "А", "BlackForest", 0, 0, 0, 0) };
        List<PortalEntry> result = Query(input, PortalSort.Name, false, false);
        Check(input[0] == original && input[0].Id == "1", "registry order stays intact");
        PortalEntry detached = result.Find(delegate(PortalEntry entry) { return entry.Id == "1"; });
        Check(detached != original && detached.Icon == 4 && detached.CreatedUtcTicks == 6 && detached.Y == 4, "complete detached copy");
        detached.Name = "Изменено"; detached.Icon = -1; detached.X = 100;
        Check(original.Name == "База" && original.Icon == 4 && original.X == 3, "view edits do not mutate portal registry");
        result.Clear();
        Check(input.Count == 2, "view removal does not remove registry entries");
        Check(PortalListModel.NameDisplay(original) == "База", "icon does not modify portal name");
        Check(PortalListModel.NameDisplay(null) == "", "missing display name is safe");
    }

    private static void Validation()
    {
        ExpectArgument(delegate { Query(null, PortalSort.Name, false, false); }, "null registry");
        ExpectArgument(delegate { Query(new List<PortalEntry> { null }, PortalSort.Name, false, false); }, "null entry");
        ExpectArgument(delegate { Query(new List<PortalEntry> { Entry("", "", "", 0, 0, 0, 0) }, PortalSort.Name, false, false); }, "empty ID");
        ExpectArgument(delegate { Query(new List<PortalEntry> { Entry("1", "", "", 0, 0, 0, 0), Entry("1", "", "", 0, 0, 0, 0) }, PortalSort.Name, false, false); }, "duplicate identity");
        ExpectArgument(delegate { Query(new List<PortalEntry> { Entry("1", "", "", double.NaN, 0, 0, 0) }, PortalSort.Name, false, false); }, "NaN coordinate");
        ExpectArgument(delegate { Query(new List<PortalEntry> { Entry("1", "", "", 0, double.PositiveInfinity, 0, 0) }, PortalSort.Name, false, false); }, "infinite coordinate");
        ExpectArgument(delegate { PortalListModel.Query(new List<PortalEntry>(), null, 0, 0, double.NegativeInfinity, "", PortalSort.Name, false, false); }, "invalid origin");
        ExpectArgument(delegate { Query(new List<PortalEntry> { Entry("1", new string('x', 257), "", 0, 0, 0, 0) }, PortalSort.Name, false, false); }, "name limit");
        ExpectArgument(delegate { PortalListModel.Query(new List<PortalEntry>(), null, 0, 0, 0, new string('x', 257), PortalSort.Name, false, false); }, "search limit");
        ExpectArgument(delegate { Query(new List<PortalEntry> { Entry("1", "", "", 0, 0, 0, -1) }, PortalSort.Created, false, false); }, "negative date");
        ExpectArgument(delegate { Query(new List<PortalEntry> { Entry("1", "", "", 0, 0, 0, DateTime.MaxValue.Ticks + 1) }, PortalSort.Created, false, false); }, "out-of-range date");
        ExpectArgument(delegate { Query(new List<PortalEntry>(), (PortalSort)999, false, false); }, "unknown sort");
        PortalEntry invalidIcon = Entry("1", "", "", 0, 0, 0, 0); invalidIcon.Icon = -2;
        ExpectArgument(delegate { Query(new List<PortalEntry> { invalidIcon }, PortalSort.Name, false, false); }, "invalid icon");

        List<PortalEntry> maximum = new List<PortalEntry>();
        for (int i = 0; i < PortalListModel.MaximumEntries; i++) maximum.Add(Entry(i.ToString(CultureInfo.InvariantCulture), "", "", 0, 0, 0, 0));
        Check(Query(maximum, PortalSort.Name, false, false).Count == PortalListModel.MaximumEntries, "maximum registry is supported");
        maximum.Add(Entry("overflow", "", "", 0, 0, 0, 0));
        ExpectArgument(delegate { Query(maximum, PortalSort.Name, false, false); }, "registry limit");
    }

    private static void Ids(IList<PortalEntry> entries, string expected)
    {
        List<string> ids = new List<string>();
        for (int i = 0; i < entries.Count; i++) ids.Add(entries[i].Id);
        string actual = string.Join(",", ids.ToArray());
        Check(actual == expected, "expected " + expected + ", got " + actual);
    }

    private static void ExpectArgument(Action action, string message)
    {
        try { action(); }
        catch (ArgumentException) { checks++; return; }
        throw new Exception("Expected argument rejection: " + message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks++;
    }
}
