using System;
using HarmonyLib;
using UnityEngine;
using ValheimModPack.NordicRadio;

internal static class MusicDuckingTests
{
    private static int assertions;
    private static void Equal(float actual, float expected, string message)
    {
        assertions++;
        if (Single.IsNaN(actual) || Math.Abs(actual - expected) > 0.00001f)
            throw new Exception(message + ": expected " + expected + ", got " + actual);
    }
    private static void True(bool condition, string message)
    { assertions++; if (!condition) throw new Exception(message); }
    public static int Main()
    {
        try
        {
            var source = new AudioSource { volume = 0.6f };
            var manager = new MusicMan(source);
            var duck = new MusicDucking();
            True(Harmony.Prefix.priority == Priority.First && Harmony.Postfix.priority == Priority.Last, "cooperative patch order");
            Equal(duck.CurrentFactor, 1, "starts unmodified");
            manager.Frame(null);
            duck.Update(1, 0.2f, 0.1f);
            Equal(duck.CurrentFactor, 0.8f, "smooth attack");
            Equal(source.volume, 0.48f, "attack multiplies live baseline");
            for (int i = 0; i < 5; i++) duck.Update(1, 0.2f, 0.1f);
            Equal(source.volume, 0.12f, "nearby full duck reaches twenty percent");
            for (int i = 0; i < 1000; i++)
            {
                manager.Frame(null);
                duck.Update(1, 0.2f, 0.016f);
            }
            Equal(source.volume, 0.12f, "branches without volume assignment never compound");
            manager.Frame(0.35f);
            Equal(source.volume, 0.07f, "fresh vanilla fade/settings volume used");
            manager.Frame(0);
            Equal(source.volume, 0, "user mute preserved");
            manager.Frame(0.7f);
            Equal(source.volume, 0.14f, "user unmute preserved");
            duck.Update(0, 0.2f, 0.1f);
            Equal(source.volume, 0.175f, "release is gradual");
            for (int i = 0; i < 20; i++) duck.Update(0, 0.2f, 0.1f);
            Equal(source.volume, 0.7f, "leaving restores latest settings");
            for (int i = 0; i < 10; i++) duck.Update(0.1f, 0.2f, 0.1f);
            Equal(duck.CurrentFactor, 0.6f, "distance blends strength");
            Equal(source.volume, 0.42f, "farther horn ducks less");
            duck.Update(1, 0.2f, Single.NaN);
            Equal(duck.CurrentFactor, 0.6f, "invalid time cannot corrupt state");
            duck.Update(1, 0.2f, -5);
            Equal(duck.CurrentFactor, 0.6f, "negative time ignored");
            duck.Update(Single.NaN, Single.NaN, 0.1f);
            Equal(duck.CurrentFactor, 0.65f, "invalid presence safely releases");
            duck.Reset();
            Equal(source.volume, 0.7f, "reset immediately restores baseline");
            Equal(duck.CurrentFactor, 1, "reset clears envelope");
            manager.Frame(0.8f);
            for (int i = 0; i < 5; i++) duck.Update(1, 0.2f, 0.1f);
            var replacement = new AudioSource { volume = 0.4f };
            manager.SetSource(replacement);
            manager.Frame(null);
            Equal(source.volume, 0.8f, "replaced music source restored");
            Equal(replacement.volume, 0.08f, "new music source inherits duck");
            replacement.volume = 0.9f;
            duck.Update(1, 0.2f, 0.1f);
            Equal(replacement.volume, 0.18f, "external source update becomes baseline");
            duck.Dispose();
            Equal(replacement.volume, 0.9f, "dispose restores external baseline");
            True(Harmony.Prefix == null && Harmony.Postfix == null, "dispose unpatches owned hooks");
            duck.Dispose();
            Equal(replacement.volume, 0.9f, "dispose is idempotent");
            duck = new MusicDucking();
            manager.Frame(null);
            for (int i = 0; i < 5; i++) duck.Update(1, 0.2f, 0.1f);
            replacement.volume = 0.55f;
            duck.Dispose();
            Equal(replacement.volume, 0.55f, "dispose never overwrites a newer external value");
            duck = new MusicDucking();
            manager.Frame(null);
            for (int i = 0; i < 5; i++) duck.Update(1, 1, 0.1f);
            Equal(replacement.volume, 0.55f, "config target one disables ducking");
            manager.SetSource(null);
            manager.Frame(null);
            duck.Update(1, 0.2f, 0.1f);
            duck.Reset();
            duck.Dispose();
            True(true, "scene without music source is safe");
            Console.WriteLine("Music ducking: " + assertions + " assertions passed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
