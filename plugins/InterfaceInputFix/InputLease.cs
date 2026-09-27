using System;

namespace ValheimModPack.InterfaceInputFix
{
    // One window owns at most one request. It never resets a shared counter.
    public sealed class InputLease
    {
        private readonly Action<bool> block;
        private long generation, closeGeneration;
        private int closeFrame;
        private bool pending;
        public bool Held { get; private set; }
        public InputLease(Action<bool> block) { if (block == null) throw new ArgumentNullException("block"); this.block = block; }
        public void Show()
        {
            generation++; pending = false;
            if (Held) return;
            Held = true;
            try { block(true); }
            catch { Close(); throw; }
        }
        public void Close()
        {
            pending = false; generation++;
            if (!Held) return;
            Held = false; block(false);
        }
        public void ScheduleClose(int frame)
        {
            if (!Held || pending) return;
            closeGeneration = generation; closeFrame = frame; pending = true;
        }
        public bool CloseDue(int frame)
        {
            if (!pending || frame < closeFrame || closeGeneration != generation) return false;
            pending = false; return true;
        }
        // Called only after Jotunn itself has discarded the old GUI's requests.
        public void Forget() { Held = false; pending = false; generation++; }
    }

    public sealed class ReentryGuard
    {
        private bool entered;
        public void Run(Action action)
        {
            if (entered) return;
            entered = true;
            try { action(); } finally { entered = false; }
        }
    }
}
