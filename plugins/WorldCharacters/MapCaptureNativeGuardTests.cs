using System;
using System.IO;
using System.Reflection;
using HarmonyLib;

internal static class MapCaptureNativeGuardTests
{
    private static int checks;
    private static void Check(bool condition, string name)
    { ++checks; if (!condition) throw new Exception(name); }
    private static void Prefix() { }

    public static int Main(string[] args)
    {
        // These are ordinary managed DLL reads. No Unity/game method is invoked.
        AppDomain.CurrentDomain.AssemblyResolve += (sender, request) => {
            string filename = new AssemblyName(request.Name).Name + ".dll";
            foreach (string folder in new[] { args[0], args[1] })
            {
                string path = Path.Combine(folder, filename);
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };
        try
        {
            Type native = Assembly.LoadFrom(Path.Combine(args[0], "assembly_valheim.dll")).GetType("Minimap");
            Verify(native, args[2]);
            Console.WriteLine("PASS: " + checks + " real-assembly map guard checks (native IL recognition and foreign Harmony patch fallback). No native method invoked or game process started.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    // Keep Harmony references in this method so the dependency resolver is
    // installed before the CLR binds its references while compiling the method.
    private static void Verify(Type native, string candidate)
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        Type guard = Assembly.LoadFrom(candidate).GetType("ValheimModPack.WorldCharacters.MapCaptureCompatibility");
        Check(guard != null, "Production map compatibility guard must exist");
        MethodInfo canSkip = guard.GetMethod("CanSkipNativeCapture", statics);
        Check((bool)guard.GetField("KnownNative", statics).GetValue(null), "Exact installed native serializer IL is recognized");
        Check((bool)canSkip.Invoke(null, null), "Unpatched supported serializers enable optimization");
        MethodInfo[] methods = { native.GetMethod("GetMapData", all), native.GetMethod("SaveMapData", all),
            native.GetNestedType("<>c", BindingFlags.NonPublic).GetMethod("<GetMapData>b__75_0", all) };
        Harmony harmony = new Harmony("worldcharacters.isolated-map-contract");
        foreach (MethodInfo method in methods)
        {
            try
            {
                harmony.Patch(method, new HarmonyMethod(typeof(MapCaptureNativeGuardTests).GetMethod("Prefix", statics)));
                Check(!(bool)canSkip.Invoke(null, null), "Foreign Harmony intervention on " + method.Name + " must use native fallback");
            }
            finally { harmony.UnpatchSelf(); }
            Check((bool)canSkip.Invoke(null, null), "Harmony patch removal is observed on the next map check: " + method.Name);
        }
    }
}
