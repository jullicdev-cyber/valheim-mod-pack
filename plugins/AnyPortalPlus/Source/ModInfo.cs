// AnyPortal+ fork changes, 2026-10-06. Original XPortal by SpikeHimself; GPL-3.0.
namespace Mod
{
    public static class Info
    {
        // This is *the* place to edit plugin details. Everywhere else will be generated based on this info.
        public const string GUID = "yay.spikehimself.xportal";
        public const string HarmonyGUID = GUID + ".harmony";
        // AnyPortal+ fork, modified 2026-10-06. Keep the GUID for world/API migration.
        public const string Author = "SpikeHimself; Valheim Mod Pack contributors";
        public const string Name = "AnyPortal+";
        public const string GitHubRepo = "jullicdev-cyber/valheim-mod-pack";
        public const string Version = "1.3.0";
        public const string Description = "AnyPortal+ fork of XPortal: search, sorting, biomes, portal icons and tracked map markers.";
        public const string WebsiteUrl = "https://github.com/" + GitHubRepo;
        // The fork has no independent Nexus release; do not advertise upstream updates.
        public const int NexusId = 0;
        public const string BepInExPackVersion = "5.4.2333";
        public const string JotunnVersion = Jotunn.Main.Version;
    }
}
