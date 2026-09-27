using System;

namespace ValheimModPack.ExpeditionLoadouts
{
    // Stable prefab identifiers, never localized names or inventory coordinates.
    [Serializable]
    public sealed class SupplyTarget
    {
        public string Prefab;
        public int Quality;
        public int Count;
    }
}
