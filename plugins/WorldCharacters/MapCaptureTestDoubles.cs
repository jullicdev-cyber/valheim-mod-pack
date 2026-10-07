using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace HarmonyLib
{
    internal sealed class Patches { internal readonly List<string> Owners = new List<string>(); }
    internal static class Harmony
    {
        internal static string ForeignOwner;
        internal static bool ThrowOnRead;
        internal static Patches GetPatchInfo(MethodInfo method)
        {
            if (ThrowOnRead) throw new System.InvalidOperationException("Unavailable patch metadata");
            if (ForeignOwner == null) return null;
            var patches = new Patches(); patches.Owners.Add(ForeignOwner); return patches;
        }
    }
}

internal sealed class ZNet
{
    internal bool PublicPosition;
    public bool IsReferencePositionPublic() { return PublicPosition; }
}
internal sealed class Minimap
{
    public int m_textureSize = 16;
    private BitArray m_explored = new BitArray(129), m_exploredOthers = new BitArray(129);
    private List<PinData> m_pins = new List<PinData>();
    internal BitArray Own { get { return m_explored; } }
    internal BitArray Shared { get { return m_exploredOthers; } }
    internal List<PinData> SavedPins { get { return m_pins; } }
    internal void Grids(int length) { m_explored = new BitArray(length); m_exploredOthers = new BitArray(length); }
    internal void InvalidShared() { m_exploredOthers = null; }
    internal enum PinType { Icon0, Icon1, Icon2 }
    internal struct Point { internal float x, y, z; }
    internal struct Author
    {
        internal string Value;
        public override string ToString() { return Value ?? ""; }
    }
    internal sealed class PinData
    {
        public string m_name = "Pin";
        public Point m_pos;
        public PinType m_type;
        public bool m_save = true, m_checked;
        public long m_ownerID;
        public Author m_author;
        internal object Icon;
    }
}
