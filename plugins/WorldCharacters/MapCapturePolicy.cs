using System;
using System.Collections;

namespace ValheimModPack.WorldCharacters
{
    // Compare the packed exploration grids and saved pin fields, not the native
    // eight-million-byte expansion/compression. Every value is an owned copy.
    internal sealed class MapCaptureSnapshot
    {
        private readonly object context;
        private readonly int textureSize, ownLength, sharedLength;
        private readonly int[] own, shared;
        private readonly byte[] pins;
        private readonly bool publicPosition;

        internal MapCaptureSnapshot(object context, int textureSize, BitArray own, BitArray shared,
            byte[] pins, bool publicPosition)
        {
            if (context == null || own == null || shared == null || pins == null)
                throw new ArgumentNullException("map snapshot");
            this.context = context; this.textureSize = textureSize;
            ownLength = own.Length; sharedLength = shared.Length;
            this.own = Pack(own); this.shared = Pack(shared);
            this.pins = (byte[])pins.Clone(); this.publicPosition = publicPosition;
        }

        private static int[] Pack(BitArray bits)
        {
            var words = new int[bits.Length / 32 + (bits.Length % 32 == 0 ? 0 : 1)];
            bits.CopyTo(words, 0);
            // Unused final-word padding is not part of the native map.
            if (words.Length != 0 && bits.Length % 32 != 0)
                words[words.Length - 1] &= (int)((1u << (bits.Length % 32)) - 1);
            return words;
        }

        internal bool Same(MapCaptureSnapshot other)
        {
            if (other == null || !ReferenceEquals(context, other.context) || textureSize != other.textureSize
                || ownLength != other.ownLength || sharedLength != other.sharedLength || publicPosition != other.publicPosition
                || own.Length != other.own.Length || shared.Length != other.shared.Length || pins.Length != other.pins.Length) return false;
            for (int i = 0; i < own.Length; ++i) if (own[i] != other.own[i]) return false;
            for (int i = 0; i < shared.Length; ++i) if (shared[i] != other.shared[i]) return false;
            for (int i = 0; i < pins.Length; ++i) if (pins[i] != other.pins[i]) return false;
            return true;
        }
    }

    internal sealed class MapCapturePolicy
    {
        private MapCaptureSnapshot admitted;
        internal bool RequiresCapture(MapCaptureSnapshot current, bool forced)
        { return forced || current == null || !current.Same(admitted); }

        // Call only after the complete snapshot was admitted to the durable
        // writer. A capacity-skipped capture must remain dirty for the next save.
        internal void Admit(MapCaptureSnapshot captured) { admitted = captured; }
        internal void Reset() { admitted = null; }
    }
}
