using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using WC = ValheimModPack.WorldCharacters.Plugin;

namespace ValheimModPack.PartyPrison
{
    public sealed partial class Plugin
    {
        private int combatFamily, combatDifficulty, combatRevision;
        private string combatToken = "";
        private float nextGearMaintenance, nextStoredGearMaintenance, nextDefeat;
        private readonly Dictionary<ZRpc, float> combatRequests = new Dictionary<ZRpc, float>();
        private readonly Dictionary<ZRpc, float> defeatRequests = new Dictionary<ZRpc, float>();

        private void ResetCombat()
        {
            combatFamily = combatDifficulty = combatRevision = 0; combatToken = "";
            nextGearMaintenance = nextStoredGearMaintenance = nextDefeat = 0;
            combatRequests.Clear(); defeatRequests.Clear();
        }

        private static void ValidateCombatState(int family, int difficulty, string token, int revision)
        {
            CombatCatalog.Get(family, difficulty); Guid id;
            if (revision < 0 || token.Length != 0 && (!Guid.TryParseExact(token, "N", out id) || id == Guid.Empty || revision < 1))
                throw new InvalidDataException("Invalid prison combat equipment identity.");
        }

        private void RefreshHostCombatState()
        {
            int family, difficulty, revision; string token;
            ArenaBuilder.GetCombatChoice(region, out family, out difficulty, out token, out revision);
            SentenceState active = store == null ? null : store.All().FirstOrDefault(s => HostFightReady(s));
            if (active == null || active.SentenceId != token) { token = ""; revision = 0; }
            ReadCombatState(family, difficulty, token, revision);
        }

        private void WriteCombatState(BinaryWriter writer)
        {
            writer.Write(combatFamily); writer.Write(combatDifficulty);
            PrisonProtocol.Text(writer, combatToken); writer.Write(combatRevision);
        }

        private void ReadCombatState(int family, int difficulty, string token, int revision)
        {
            ValidateCombatState(family, difficulty, token, revision);
            if (combatToken != token || combatRevision != revision) nextGearMaintenance = nextStoredGearMaintenance = 0;
            combatFamily = family; combatDifficulty = difficulty; combatToken = token; combatRevision = revision;
        }

        private void MaintainCombatGear()
        {
            float now = Time.realtimeSinceStartup;
            if (Hosting && store != null && !fatalStore && region != null && now >= nextStoredGearMaintenance)
            {
                nextStoredGearMaintenance = now + 1f; RefreshHostCombatState();
                if (ArenaBuilder.ExpireStoredPrisonGear(region, world, combatToken, combatRevision) > 0)
                    network.Save(true, false, false);
            }
            if (!Hosting && !receivedState || !WC.AdministrativeReady || Player.m_localPlayer == null) return;
            if (now < nextGearMaintenance) return;
            nextGearMaintenance = now + .5f;
            // Admission compares an immutable native inventory snapshot. Do not
            // mutate it between capture and the durable custody acknowledgement.
            if (!PreparingCustody)
            {
                int removed = ArenaBuilder.RemoveObsoleteGearWhenSettled(Player.m_localPlayer, world, combatToken, combatRevision);
                removed += CustodyInventory.ExpireBackpackGear(Player.m_localPlayer, world, combatToken, combatRevision);
                if (removed > 0) WC.RequestAdministrativeSave();
            }
        }

        private void RequestCombatChoice(int family, int difficulty)
        {
            CombatCatalog.Get(family, difficulty);
            if (Hosting)
            {
                RequireHost(); SentenceState active = store.All().FirstOrDefault(s => HostFightReady(s));
                if (active == null) throw new InvalidOperationException(T("В тюрьме нет готового к бою заключённого.", "No inmate is ready to fight."));
                ApplyCombatChoice(active, family, difficulty); return;
            }
            if (!FightReady || Player.m_localPlayer == null || !ArenaBuilder.IsInsideCell(region, Player.m_localPlayer.transform.position))
                throw new InvalidOperationException(T("Выбирать мобов и сложность можно из камеры после завершения посадки.", "Choose enemies and difficulty from the cell after admission completes."));
            ToHost(PrisonProtocol.CombatChoice, writer => {
                PrisonProtocol.Text(writer, localSentence.SentenceId); writer.Write(family); writer.Write(difficulty);
            });
            notice = T("Выбор отправлен хосту. Закройте сундук снаряжения.", "Choice sent to the host. Close the equipment chest.");
        }

        private void ApplyCombatChoice(SentenceState sentence, int family, int difficulty)
        {
            if (!Hosting || !HostFightReady(sentence)) return;
            CombatCatalog.Get(family, difficulty);
            ArenaBuilder.ConfigureSentenceKit(region, sentence.SentenceId, family, difficulty);
            ArenaBuilder.ResetAfterDefeat(region);
            nextWave = Time.realtimeSinceStartup + 8f; nextHost = Time.realtimeSinceStartup;
            RefreshHostCombatState();
            ArenaBuilder.ExpireStoredPrisonGear(region, world, combatToken, combatRevision);
            network.Save(true, false, false);
            PrisonCombatLoadout choice = CombatCatalog.Get(family, difficulty);
            notice = T("Противники: ", "Enemies: ") + T(choice.RussianName, choice.EnglishName)
                + T("; сложность ", "; difficulty ") + (difficulty + 1) + T(". Новый набор — в сундуке камеры.", ". New equipment is in the cell chest.");
        }

        private bool ServerCombatMessage(ZNetPeer peer, int kind, BinaryReader reader)
        {
            if (kind != PrisonProtocol.CombatChoice && kind != PrisonProtocol.Defeat) return false;
            string token = PrisonProtocol.Text(reader);
            int family = 0, difficulty = 0;
            if (kind == PrisonProtocol.CombatChoice) { family = reader.ReadInt32(); difficulty = reader.ReadInt32(); CombatCatalog.Get(family, difficulty); }
            PrisonProtocol.End(reader);
            SentenceState sentence = store.Find(WC.GetAdministrativeOwner(peer)); PrisonPoint position;
            if (sentence == null || sentence.SentenceId != token || !HostFightReady(sentence) || !Position(peer, out position)) return true;
            float now = Time.realtimeSinceStartup, previous;
            if (kind == PrisonProtocol.CombatChoice)
            {
                if (!ArenaBuilder.IsInsideCell(region, Vector(position)) || combatRequests.TryGetValue(peer.m_rpc, out previous) && now - previous < 1f) return true;
                combatRequests[peer.m_rpc] = now; ApplyCombatChoice(sentence, family, difficulty);
            }
            else
            {
                if (!ArenaBuilder.ContainsConfinement(region, Vector(position)) || defeatRequests.TryGetValue(peer.m_rpc, out previous) && now - previous < 2f) return true;
                defeatRequests[peer.m_rpc] = now; ArenaBuilder.ResetAfterDefeat(region); nextWave = now + 8f;
            }
            return true;
        }

        private void NotifyDefeat()
        {
            if (localSentence == null || Time.realtimeSinceStartup < nextDefeat) return;
            nextDefeat = Time.realtimeSinceStartup + 2f;
            if (Hosting) { ArenaBuilder.ResetAfterDefeat(region); nextWave = Time.realtimeSinceStartup + 8f; }
            else ToHost(PrisonProtocol.Defeat, writer => PrisonProtocol.Text(writer, localSentence.SentenceId));
        }
    }
}
