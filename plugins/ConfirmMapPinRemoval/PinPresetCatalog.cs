using System.Collections.Generic;

namespace ValheimModPack.PinRemoval
{
    public static class PinPresetCatalog
    {
        // Valheim 1.0.16 Minimap's five manually selectable sprites. Special
        // death/bed/player/boss/event types are deliberately not preset choices.
        public static bool IsStockIcon(int icon)
        { return icon == 0 || icon == 1 || icon == 2 || icon == 3 || icon == 6; }

        public static List<PinPreset> Defaults()
        {
            // Stable identities and keys survive language changes. Name is only
            // a readable fallback; selecting a preset resolves its current label.
            return new List<PinPreset> {
                Entry("TrollCave", "Troll Cave", 6),
                Entry("BearDen", "Bear Cave", 6),
                Entry("Raspberries", "Raspberries", 3),
                Entry("Blueberries", "Blueberries", 3),
                Entry("Mushrooms", "Mushroom", 3),
                Entry("Dungeon", "Dungeon", 6),
                Entry("SurtlingSpawn", "Surtling Spawn", 0),
                Entry("Copper", "Copper Deposit", 2),
                Entry("Tin", "Tin Deposit", 2),
                Entry("Iron", "Iron", 2),
                Entry("Silver", "Silver Vein", 2),
                Entry("Cloudberries", "Cloudberries", 3),
                Entry("Portal", "Portal", 6),
                Entry("Base", "Base", 1),
                Entry("BurialChambers", "Burial Chambers", 6),
                Entry("Crypt", "Sunken Crypts", 6),
                Entry("MountainCave", "Frost Caves", 6),
                Entry("InfestedMine", "Infested Mine", 6),
                Entry("TarPit", "Tar Pit", 3),
                Entry("Haldor", "Haldor", 1),
                Entry("Hildir", "Hildir", 1),
                Entry("FulingVillage", "Fuling Village", 1),
                Entry("Boars", "Boar", 3),
                Entry("DragonEgg", "Dragon Egg", 3),
                Entry("Flax", "Flax", 3)
            };
        }
        private static PinPreset Entry(string key, string english, int icon)
        {
            return new PinPreset { Id = "default." + key.ToLowerInvariant(), LocalizationKey = key,
                Name = english, Icon = icon, Builtin = true };
        }
    }
}
