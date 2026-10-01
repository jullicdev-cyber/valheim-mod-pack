using System;
using BepInEx;
using HarmonyLib;

namespace ValheimModPack.PlayerSectorSync
{
    [BepInPlugin(Id, "Player Sector Sync", Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "valheimmodpack.playersectorsync", Version = "1.0.0";
        private Harmony harmony;

        private void Awake()
        {
            harmony = new Harmony(Id);
            PlayerPositionPatch.Warning = message => Logger.LogWarning(message);
            try { harmony.PatchAll(typeof(PlayerPositionPatch)); }
            catch (Exception error)
            {
                try { harmony.UnpatchSelf(); } catch { }
                Logger.LogError("Player Sector Sync was not enabled: " + error);
                return;
            }
            Logger.LogInfo("Player Sector Sync 1.0.0: server character sector notifications enabled.");
        }

        private void OnDestroy()
        {
            try { if (harmony != null) harmony.UnpatchSelf(); }
            catch (Exception error) { Logger.LogWarning("Player Sector Sync could not remove its patches: " + error.GetType().Name); }
            PlayerPositionPatch.Warning = null;
        }
    }
}
