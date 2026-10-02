using System;

namespace ValheimModPack.InventoryAdmin
{
    internal sealed class AdminInputLease
    {
        private readonly Action<bool> change;
        internal bool Held { get; private set; }
        internal AdminInputLease(Action<bool> change) { this.change = change; }
        internal void Acquire()
        {
            if (Held) return;
            Held = true;
            try { change(true); }
            catch { try { Release(); } catch { } throw; }
        }
        internal void Release()
        {
            if (!Held) return;
            Held = false; change(false);
        }
        internal void ForgetAfterGlobalReset() { Held = false; }
    }
}
