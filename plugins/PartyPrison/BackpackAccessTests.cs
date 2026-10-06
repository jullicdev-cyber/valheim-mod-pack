using System;
using System.Diagnostics;
using System.Reflection;

namespace ValheimModPack.PartyPrison
{
    internal static class BackpackAccessTests
    {
        private static int checks, inventoryCalls, dataCalls, componentCalls, serializationCalls;
        public sealed class Item { public bool Bag; public Contents Inventory; public Holder Data; }
        public sealed class Contents { public int Gear, Farm; }
        public sealed class Component { public string Serialize() { ++serializationCalls; return "saved"; } }
        public sealed class VoidComponent { public void Serialize() { ++serializationCalls; } }
        public sealed class InvalidComponent { public int Serialize() { return 1; } }
        public sealed class Holder
        {
            public object Component;
            public T GetOrCreate<T>(string key) where T : class
            { ++componentCalls; return (T)Component; }
        }
        public static class Api
        {
            public static bool IsBackpack(Item item) { return item != null && item.Bag; }
            public static Contents GetBackpackInventory(Item item) { ++inventoryCalls; return item.Bag ? item.Inventory : null; }
        }
        public static class Extensions
        {
            public static Holder Data(Item item) { ++dataCalls; return item.Data; }
        }
        public static class InvalidApi
        {
            public static bool IsBackpack(Item item) { return true; }
            public static object GetBackpackInventory(Item item) { return item.Inventory; }
        }
        private static void Check(bool value, string reason)
        { ++checks; if (!value) throw new Exception("Backpack adapter: " + reason); }
        private static void Refuses(Action action, string reason)
        { try { action(); } catch (Exception) { ++checks; return; } throw new Exception("Backpack adapter accepted: " + reason); }
        private static BackpackAccess<Item, Contents> Bind()
        { return BackpackAccess<Item, Contents>.Create(typeof(Api), typeof(Extensions), typeof(Component)); }
        public static int Main()
        {
            try
            {
                BackpackAccess<Item, Contents> access = Bind();
                var component = new Component(); var bag = new Item { Bag = true, Inventory = new Contents { Gear = 2, Farm = 12 }, Data = new Holder { Component = component } };
                Check(access.IsBackpack(bag) && !access.IsBackpack(new Item()) && !access.IsBackpack(null), "cheap native classification");
                Check(System.Object.ReferenceEquals(access.Inventory(bag), bag.Inventory) && System.Object.ReferenceEquals(access.Component(bag), component), "typed delegates bind the installed API shape and current bag data");
                access.Serialize(component); Check(serializationCalls == 1, "string-returning native serialization delegate");
                bag.Inventory = new Contents { Gear = 1, Farm = 13 }; bag.Data = new Holder { Component = new Component() };
                Check(access.Inventory(bag).Farm == 13 && System.Object.ReferenceEquals(access.Component(bag), bag.Data.Component), "adapter never caches stale inventory or component references");
                Refuses(() => access.Component(new Item { Bag = true }), "missing item data");
                Refuses(() => BackpackAccess<Item, Contents>.Create(typeof(InvalidApi), typeof(Extensions), typeof(Component)), "invalid inventory getter ABI");
                Refuses(() => BackpackAccess<Item, Contents>.Create(typeof(Api), typeof(Extensions), typeof(InvalidComponent)), "invalid serialization ABI");
                var voidAccess = BackpackAccess<Item, Contents>.Create(typeof(Api), typeof(Extensions), typeof(VoidComponent));
                voidAccess.Serialize(new VoidComponent()); Check(serializationCalls == 2, "void-returning native serialization is supported");
                RetryCache(access); Benchmark(access);
                Console.WriteLine("PASS: backpack adapter " + checks + " checks; typed ABI, fresh references, bounded late-load retry and ordinary-item fast path."); return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        }
        private static void RetryCache(BackpackAccess<Item, Contents> access)
        {
            var cache = new BackpackAccessCache<Item, Contents>(); int resolutions = 0; bool loaded = false;
            Func<BackpackAccess<Item, Contents>> resolve = () => { ++resolutions; return loaded ? access : null; };
            Check(cache.Get(0, resolve) == null && resolutions == 1, "absent optional plugin leaves items intact");
            loaded = true;
            Check(cache.Get(.5f, resolve) == null && cache.Get(9.99f, resolve) == null && resolutions == 1, "late plugin lookup stays bounded while absent");
            Check(System.Object.ReferenceEquals(cache.Get(10, resolve), access) && resolutions == 2, "a plugin loaded later is resolved on the next retry");
            Check(System.Object.ReferenceEquals(cache.Get(1000, resolve), access) && resolutions == 2 && cache.Value == access, "successful API binding is cached indefinitely");
            var failed = new BackpackAccessCache<Item, Contents>(); int failures = 0;
            Func<BackpackAccess<Item, Contents>> unavailable = () => { ++failures; throw new InvalidOperationException("Unsupported API"); };
            Refuses(() => failed.Get(0, unavailable), "malformed API is preserved as a failure");
            Refuses(() => failed.Get(.5f, unavailable), "malformed API is not rebound every frame");
            Check(failures == 1 && failed.Get(10, resolve) == access, "API resolution failure can safely recover on a later retry");
        }
        private static void Benchmark(BackpackAccess<Item, Contents> access)
        {
            const int scans = 25000, slots = 40;
            var items = new Item[slots]; for (int i = 0; i < slots; ++i) items[i] = new Item();
            MethodInfo legacy = typeof(Api).GetMethod("GetBackpackInventory"); int before = inventoryCalls;
            var watch = Stopwatch.StartNew();
            for (int scan = 0; scan < scans; ++scan) for (int item = 0; item < slots; ++item) legacy.Invoke(null, new object[] { items[item] });
            watch.Stop(); long oldTicks = watch.ElapsedTicks; int oldCalls = inventoryCalls - before;
            before = inventoryCalls; watch.Restart();
            for (int scan = 0; scan < scans; ++scan) for (int item = 0; item < slots; ++item)
                if (access.IsBackpack(items[item])) access.Inventory(items[item]);
            watch.Stop(); int currentCalls = inventoryCalls - before;
            Check(oldCalls == scans * slots && currentCalls == 0, "ordinary inventory scans call no backpack inventory/persistence methods");
            Console.WriteLine("Managed ordinary-item benchmark (" + scans + " x " + slots + "): reflection getter " + (oldTicks * 1000.0 / Stopwatch.Frequency).ToString("F2") + " ms / " + oldCalls + " calls; cached classification " + watch.Elapsed.TotalMilliseconds.ToString("F2") + " ms / " + currentCalls + " calls.");
        }
    }
}
