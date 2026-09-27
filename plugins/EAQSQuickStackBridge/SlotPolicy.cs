namespace ValheimModPack
{
    public static class SlotPolicy
    {
        // Fail closed when the two mods disagree about inventory geometry.
        public static bool IsProtected(int x, int y, int width, int height, int visibleRows, int fullHeight)
        {
            return width != 8 || visibleRows < 4 || fullHeight <= visibleRows || fullHeight > 32 || height != fullHeight
                || x < 0 || x >= width || y < 0 || y >= visibleRows;
        }
    }
}
