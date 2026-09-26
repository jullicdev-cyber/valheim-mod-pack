using System;

namespace ValheimModPack.PinRemoval
{
    public sealed class Confirmation<T> where T : class
    {
        private readonly T target;
        private bool resolved;
        public Confirmation(T target) { this.target = target; }
        public void Cancel() { resolved = true; }
        public bool Confirm(Func<T, bool> stillValid, Action<T> remove)
        {
            if (resolved) return false;
            resolved = true;
            if (target == null || !stillValid(target)) return false;
            remove(target);
            return true;
        }
    }
}
