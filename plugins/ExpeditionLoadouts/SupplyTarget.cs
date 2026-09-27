using System;

namespace ValheimModPack.ExpeditionLoadouts
{
    // Stable prefab identifiers, never localized names or inventory coordinates.
    [Serializable]
    public sealed class SupplyTarget
    {
        public string Prefab;
        public int Quality;
        public int Variant;
        public int WorldLevel;
        public int Count;
    }

    public sealed class InventoryCapture
    {
        public readonly System.Collections.Generic.List<SupplyTarget> Items = new System.Collections.Generic.List<SupplyTarget>();
        public int IncludedSlots, SkippedCustomData, SkippedUnsupported, SkippedInvalid, SkippedLimit;
        public int SkippedSlots { get { return SkippedCustomData + SkippedUnsupported + SkippedInvalid + SkippedLimit; } }
    }
}
