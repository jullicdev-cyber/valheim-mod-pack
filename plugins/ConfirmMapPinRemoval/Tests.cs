using System;
using System.Collections.Generic;
using ValheimModPack.PinRemoval;
public static class Tests
{
    private static int checks;
    private static void Check(bool condition) { checks++; if (!condition) throw new Exception("Confirmation regression at assertion " + checks); }
    private sealed class View : IDialogView
    {
        public Action Yes, No;
        public int Shows, Hides;
        public bool Visible, FailShow, FailHide;
        public void Show(string name, Action yes, Action no)
        {
            Shows++; Visible = true; Yes = yes; No = no;
            if (FailShow) throw new Exception("show failed after allocating UI");
        }
        public void Hide()
        {
            Hides++; Visible = false;
            if (FailHide) throw new Exception("hide failed");
        }
    }
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
        DialogTests(); LabelTests();
        Console.WriteLine("OK: " + checks + " confirmation, dialog lifecycle and label assertions.");
    }
    private static void DialogTests()
    {
        object a = new object(), b = new object();
        var pins = new List<object> { a, b };
        var errors = new List<Exception>();
        var view = new View();
        var dialog = new RemovalDialog<object>(view, errors.Add);
        int deletes = 0;
        Action<object> remove = p => {
            Check(!view.Visible && !dialog.IsOpen); // UI released before deletion.
            pins.Remove(p); deletes++;
        };
        dialog.Open(null, "", pins.Contains, remove);
        Check(!dialog.IsOpen && view.Shows == 0);
        dialog.Open(a, "A", pins.Contains, remove);
        Check(dialog.IsOpen && deletes == 0);
        Action oldYes = view.Yes, oldNo = view.No;
        dialog.Open(b, "B", pins.Contains, remove);
        Check(view.Shows == 1); // Repeated click cannot retarget.
        view.No(); view.Yes(); dialog.Cancel();
        Check(!dialog.IsOpen && pins.Count == 2 && view.Hides == 1);
        dialog.Open(b, "B", pins.Contains, remove);
        oldYes(); oldNo();
        Check(dialog.IsOpen && view.Visible && view.Hides == 1); // Stale callbacks cannot close new window.
        view.Yes(); view.Yes(); view.No();
        Check(pins.Count == 1 && pins[0] == a && deletes == 1);
        dialog.Open(a, "A", pins.Contains, remove);
        pins.Clear(); pins.Add(b); view.Yes();
        Check(pins.Count == 1 && pins[0] == b && deletes == 1);
        bool sameWorld = true;
        dialog.Open(b, "B", p => sameWorld && pins.Contains(p), remove);
        sameWorld = false; dialog.ValidateContext(); view.Yes();
        Check(!dialog.IsOpen && pins.Count == 1 && deletes == 1);
        dialog.Open(b, "B", p => { throw new Exception("map disappeared"); }, remove);
        dialog.ValidateContext();
        Check(!dialog.IsOpen && !view.Visible && errors.Count == 1);
        dialog.Open(b, "B", p => { throw new Exception("confirm validation failed"); }, remove);
        view.Yes(); Check(!dialog.IsOpen && deletes == 1 && errors.Count == 2);
        view.FailShow = true;
        dialog.Open(b, "B", pins.Contains, remove);
        Check(!dialog.IsOpen && !view.Visible && errors.Count == 3);
        view.Yes(); Check(deletes == 1);
        view.FailShow = false; view.FailHide = true;
        dialog.Open(b, "B", pins.Contains, remove); view.Yes();
        Check(!dialog.IsOpen && deletes == 1 && errors.Count == 4);
        view.FailHide = false;
        int attempts = 0;
        dialog.Open(b, "B", pins.Contains, p => { attempts++; throw new Exception("delete failed"); });
        view.Yes(); view.Yes();
        Check(!dialog.IsOpen && !view.Visible && attempts == 1 && errors.Count == 5);
        dialog.Open(b, "B", pins.Contains, remove); dialog.ValidateContext(); view.Yes();
        Check(pins.Count == 0 && deletes == 2); // Recovery after an error.
    }
    private static void LabelTests()
    {
        Check(PinLabel.Format(null, true) == "без названия");
        Check(PinLabel.Format("  \n\t", false) == "unnamed");
        Check(PinLabel.Format("<color=red>База</color>\n\t у реки", true) == "База у реки");
        Check(PinLabel.Format("A < B", false) == "A < B");
        Check(PinLabel.Format(new String('я', 81), true) == new String('я', 80) + "…");
        Check(PinLabel.Format(new String('x', 79) + "е\u0301z", true).EndsWith("е\u0301…"));
        Check(PinLabel.Format(new String('x', 79) + "\U0001F332z", true).EndsWith("\U0001F332…"));
    }
}
