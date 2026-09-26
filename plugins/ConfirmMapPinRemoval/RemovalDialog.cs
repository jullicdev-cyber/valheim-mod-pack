using System;
namespace ValheimModPack.PinRemoval
{
    public interface IDialogView
    {
        void Show(string name, Action confirm, Action cancel);
        void Hide();
    }
    public sealed class RemovalDialog<T> where T : class
    {
        private readonly IDialogView view;
        private readonly Action<Exception> report;
        private Confirmation<T> request;
        private Func<T, bool> validate;
        private Action<T> remove;
        private T target;
        public bool IsOpen { get { return request != null; } }
        public RemovalDialog(IDialogView view, Action<Exception> report)
        { this.view = view; this.report = report; }
        public void Open(T target, string name, Func<T, bool> validate, Action<T> remove)
        {
            if (IsOpen || target == null) return;
            this.target = target; this.validate = validate; this.remove = remove;
            var current = new Confirmation<T>(target);
            request = current;
            try { view.Show(name, () => Finish(current, true), () => Finish(current, false)); }
            catch (Exception error) { Cancel(); report(error); }
        }
        public void ValidateContext()
        {
            if (!IsOpen) return;
            try { if (!validate(target)) Cancel(); }
            catch (Exception error) { Cancel(); report(error); }
        }
        public void Cancel() { if (IsOpen) Finish(request, false); }
        private void Finish(Confirmation<T> current, bool confirmed)
        {
            if (!ReferenceEquals(request, current)) return;
            var check = validate; var delete = remove;
            request = null; target = null; validate = null; remove = null;
            try
            {
                // If releasing UI/input fails, do not delete the target.
                view.Hide();
                if (confirmed) current.Confirm(check, delete); else current.Cancel();
            }
            catch (Exception error) { current.Cancel(); report(error); }
        }
    }
}
