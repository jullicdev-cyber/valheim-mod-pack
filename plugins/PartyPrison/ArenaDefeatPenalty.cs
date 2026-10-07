using System;
using System.Reflection;
using HarmonyLib;

namespace ValheimModPack.PartyPrison
{
    /// <summary>The skill-loss branch of native Player.OnDeath, without losing custody items.</summary>
    internal static class ArenaDefeatPenalty
    {
        private static readonly MethodInfo HardDeath = AccessTools.Method(typeof(Player), "HardDeath", Type.EmptyTypes);
        private static readonly FieldInfo TimeSinceDeath = AccessTools.Field(typeof(Player), "m_timeSinceDeath");

        internal static void Apply(Player player)
        {
            if (player == null || HardDeath == null || TimeSinceDeath == null || player.GetSkills() == null)
                throw new InvalidOperationException("Native arena death skill APIs are unavailable.");
            // Reuse native cooldown and difficulty/skill-reduction behavior,
            // including patches on Skills.OnDeath. Do not impose a custom rate.
            bool hard = (bool)HardDeath.Invoke(player, null);
            bool reset = ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(GlobalKeys.DeathSkillsReset);
            try {
                if (reset) player.GetSkills().Clear();
                else if (hard) player.GetSkills().OnDeath();
            }
            finally {
                // Vanilla resets this on both hard and soft deaths. ClearHardDeath
                // does the opposite (expires protection), so it must not be used.
                TimeSinceDeath.SetValue(player, 0f);
            }
            if (!hard) player.Message(MessageHud.MessageType.TopLeft, "$msg_softdeath");
        }
    }
}
