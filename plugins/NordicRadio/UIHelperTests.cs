using System;
using System.IO;
using System.Reflection;

internal static class UIHelperTests
{
    private static int checks;
    private static MethodInfo title;
    private static MethodInfo clock;
    private static MethodInfo status;

    private static void Equal(object actual, object expected)
    {
        ++checks;
        if (!Object.Equals(actual, expected))
            throw new Exception("Expected: " + expected + "; actual: " + actual);
    }
    private static string Safe(string text, int max)
    {
        return (string)title.Invoke(null, new object[] { text, max });
    }
    private static string Clock(double seconds)
    {
        return (string)clock.Invoke(null, new object[] { seconds });
    }
    private static string Status(string text, bool russian)
    {
        return (string)status.Invoke(null, new object[] { text, russian });
    }

    private static int Main(string[] directories)
    {
        try
        {
            if (directories.Length < 2) throw new ArgumentException("Test output and dependency directories required");
            // Resolving managed references does not start Unity or call its native engine.
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs args)
            {
                string filename = new AssemblyName(args.Name).Name + ".dll";
                foreach (string directory in directories)
                {
                    string candidate = Path.Combine(directory, filename);
                    if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
                }
                return null;
            };
            Assembly assembly = Assembly.LoadFrom(Path.Combine(directories[0], "UIContract.dll"));
            Type window = assembly.GetType("ValheimModPack.NordicRadio.RadioWindow", true);
            title = window.GetMethod("SafeTitle", BindingFlags.NonPublic | BindingFlags.Static);
            clock = window.GetMethod("Clock", BindingFlags.NonPublic | BindingFlags.Static);
            status = window.GetMethod("LocalStatus", BindingFlags.NonPublic | BindingFlags.Static);

            Equal(Safe(null, 10), "");
            Equal(Safe("", 10), "");
            Equal(Safe("x", 0), "");
            Equal(Safe("  A\nB\tC  ", 30), "A B C");
            Equal(Safe("one\u202Etwo\u2069", 30), "onetwo");
            // Markup stays literal. The UI separately disables Text.supportRichText.
            Equal(Safe("<color=red>song</color>", 50), "<color=red>song</color>");
            Equal(Safe("Скандинавия", 30), "Скандинавия");
            Equal(Safe("Музыка", 3), "Муз…");
            Equal(Safe("a\u0301bc", 1), "a\u0301…");
            Equal(Safe("\ud83c\udfb5song", 1), "\ud83c\udfb5…");
            Equal(Safe("abc", 3), "abc");
            Equal(Safe("a\0b", 30), "ab");
            Equal(Clock(0), "0:00");
            Equal(Clock(-5), "0:00");
            Equal(Clock(59.99), "0:59");
            Equal(Clock(60), "1:00");
            Equal(Clock(3601), "60:01");
            Equal(Clock(Double.NaN), "--:--");
            Equal(Clock(Double.PositiveInfinity), "--:--");
            Equal(Clock(Double.MaxValue), "35791394:07");
            Equal(Status(null, true), "");
            Equal(Status("Ready", false), "Ready");
            Equal(Status("Ready", true), "Готово");
            Equal(Status("Downloading 37%", true), "Загрузка 37%");
            Equal(Status("Scanning MP3 files...", true), "Сканирование MP3...");
            Equal(Status("MP3 scan failed: disk unavailable", true), "Ошибка чтения музыки: disk unavailable");
            Equal(Status("Download failed: timeout", true), "Ошибка загрузки: timeout");
            Equal(Status("Unexpected host status", true), "Unexpected host status");
            Console.WriteLine("PASS " + checks + " RadioWindow formatting checks (actual compiled source; UI runtime not exercised)");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
