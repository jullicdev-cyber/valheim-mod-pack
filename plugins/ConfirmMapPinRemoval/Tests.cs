using System;
using System.Collections.Generic;
using ValheimModPack.PinRemoval;
public static class Tests
{
    private static void Check(bool condition) { if (!condition) throw new Exception("Confirmation regression"); }
    public static void Main()
    {
        object a = new object(), b = new object();
        var pins = new List<object> { a, b };
        int deletes = 0;
        Action<object> remove = p => { pins.Remove(p); deletes++; };
        var cancel = new Confirmation<object>(a);
        cancel.Cancel(); Check(!cancel.Confirm(pins.Contains, remove)); Check(pins.Count == 2);
        var yes = new Confirmation<object>(a);
        Check(yes.Confirm(pins.Contains, remove)); Check(pins.Count == 1 && pins[0] == b);
        Check(!yes.Confirm(pins.Contains, remove) && deletes == 1);
        var stale = new Confirmation<object>(b);
        pins.Clear(); pins.Add(new object());
        Check(!stale.Confirm(pins.Contains, remove) && pins.Count == 1 && deletes == 1);
        var empty = new Confirmation<object>(null);
        Check(!empty.Confirm(pins.Contains, remove));
        Console.WriteLine("OK: cancel preserves pins; yes deletes only captured pin once; stale/null targets do not delete replacements.");
    }
}
