using System;
using System.Collections.Generic;

namespace ValheimModPack.WorldCharacters
{
    // Timeout checks need a stable view because rejecting a peer can trigger
    // disconnect callbacks. Retain only references, never character snapshots.
    // The reusable buffer avoids allocating a new peer array every game frame.
    internal sealed class PeerMaintenance<TKey, TValue>
    {
        private readonly List<TValue> peers = new List<TValue>();
        private readonly double interval;
        private double nextCheck;
        internal PeerMaintenance(double seconds)
        {
            if (Double.IsNaN(seconds) || Double.IsInfinity(seconds) || seconds <= 0)
                throw new ArgumentOutOfRangeException("seconds");
            interval = seconds;
        }
        internal int Count { get { return peers.Count; } }
        internal TValue this[int index] { get { return peers[index]; } }
        internal bool TryCapture(Dictionary<TKey, TValue> current, double now)
        {
            if (now < nextCheck) return false;
            nextCheck = now + interval;
            peers.Clear();
            // Dictionary.ValueCollection's concrete enumerator is a struct.
            // Do not change this to IEnumerable<T> or LINQ on the warm path.
            foreach (TValue peer in current.Values) peers.Add(peer);
            return true;
        }
        internal void Reset() { peers.Clear(); nextCheck = 0; }
    }
}
