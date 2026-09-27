using System;

namespace ValheimModPack.ChestSearch
{
    // Jotunn's BlockInput is a shared counter. Own exactly one increment, even
    // if its camera update throws after incrementing, and never reset the counter.
    public sealed class InputBlockLease : IDisposable
    {
        private readonly Action<bool> set;
        public bool Held { get; private set; }
        public InputBlockLease(Action<bool> set)
        {
            if (set == null) throw new ArgumentNullException("set");
            this.set = set;
        }
        public void Acquire()
        {
            if (Held) return;
            Held = true;
            try { set(true); }
            catch
            {
                try { Release(); } catch { }
                throw;
            }
        }
        public void Release()
        {
            if (!Held) return;
            Held = false;
            set(false);
        }
        public void Dispose() { Release(); }
    }
}
