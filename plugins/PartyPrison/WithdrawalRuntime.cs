using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
using WC = ValheimModPack.WorldCharacters.Plugin;

namespace ValheimModPack.PartyPrison
{
    public sealed partial class Plugin
    {
        private CustodyWithdrawal withdrawal;
        private float nextGrantAck;
        private string grantId = "", grantToken = "";
        private int grantIndex;
        private long grantSave;
        private readonly Dictionary<string, long> withdrawalBaselines = new Dictionary<string, long>();
        private readonly Dictionary<string, float> legacyGrantAfter = new Dictionary<string, float>();
        private void ResetWithdrawal()
        {
            if (withdrawal != null) { withdrawal.Dispose(); withdrawal = null; }
            nextGrantAck = 0; grantSave = 0; grantId = grantToken = ""; withdrawalBaselines.Clear(); legacyGrantAfter.Clear();
        }
        internal void CollectFromChest(Container container, Humanoid actor)
        {
            notice = T("Откройте сундук клавишей E, как обычный сундук.", "Open this ordinary chest with E.");
        }
        private void ProgressWithdrawal()
        {
            if (Hosting || !WC.AdministrativeReady) return;
            if (grantSave != 0 && WC.IsAdministrativeSaveDurable(grantSave) && Time.realtimeSinceStartup >= nextGrantAck)
            {
                nextGrantAck = Time.realtimeSinceStartup + 2;
                ToHost(PrisonProtocol.WithdrawalAck, w => { PrisonProtocol.Text(w, grantToken); w.Write(grantIndex); PrisonProtocol.Text(w, grantId); w.Write(grantSave); });
            }
        }
        private bool ServerWithdrawalMessage(ZNetPeer peer, int kind, BinaryReader reader)
        {
            if (kind != PrisonProtocol.WithdrawalRequest && kind != PrisonProtocol.WithdrawalAck) return false;
            string token = PrisonProtocol.Text(reader); int index = reader.ReadInt32();
            string account = WC.GetAdministrativeOwner(peer);
            if (index < 0 || index > 3 || token.Length != 32) throw new InvalidDataException("Invalid custody withdrawal identity.");
            CustodyRecord custodyRecord = custody.Find(account, token);
            if (custodyRecord == null || custodyRecord.NeedsRecovery || custodyRecord.Stage != CustodyStage.Released) return true;
            WithdrawalRecord record = withdrawal.Find(token, index);
            if (record == null || record.NeedsRecovery) return true;
            if (kind == PrisonProtocol.WithdrawalAck)
            {
                string id = PrisonProtocol.Text(reader); long durable = reader.ReadInt64(); PrisonProtocol.End(reader); long baseline;
                string key = id + ":" + peer.m_uid;
                if (record.PendingId != id || !withdrawalBaselines.TryGetValue(key, out baseline) || durable <= Math.Max(0, baseline) || WC.GetAdministrativeDurableSequence(peer.m_uid) < durable) return true;
                withdrawal.Commit(token, index, id); withdrawalBaselines.Remove(key);
                if (custodyRecord.PublicAccess && !withdrawal.All(token).Any(r => r.PendingId.Length != 0)) custody.MarkCollected(account, token);
                nextHost = Time.realtimeSinceStartup; return true;
            }
            // Native chests own all new withdrawals. Only an already reserved
            // pre-upgrade grant can be completed, never a fresh ledger grant.
            PrisonProtocol.End(reader);
            return true;
        }
        private bool ClientWithdrawalMessage(int kind, BinaryReader reader)
        {
            if (kind != PrisonProtocol.WithdrawalGrant) return false;
            string token = PrisonProtocol.Text(reader); int index = reader.ReadInt32(); string id = PrisonProtocol.Text(reader); byte[] bytes = PrisonProtocol.Blob(reader); PrisonProtocol.End(reader);
            if (!WC.AdministrativeReady || localCustodyStage != (int)CustodyStage.Released || token != localCustodyToken || index < 0 || index > 3) return true;
            if (id.Length == 0)
            { grantSave = 0; grantId = ""; return true; }
            Guid guid; if (!Guid.TryParseExact(id, "N", out guid) || CustodyInventory.Count(bytes) != 1) throw new InvalidDataException("Invalid personal item grant.");
            Player player = Player.m_localPlayer; string receipt = "VMP_PP_Withdrawal_" + id;
            string expected = world.ToString("x16") + ":" + token + ":" + CustodyInventory.Fingerprint(bytes), existing;
            if (player.m_customData.TryGetValue(receipt, out existing))
            { if (existing != expected) throw new InvalidDataException("Personal item receipt mismatch."); }
            else
            {
                player.m_customData[receipt] = "RECOVERY:" + expected;
                bool added;
                try { added = CustodyInventory.AddExact(player.GetInventory(), VisibleInventoryRows(player), bytes); }
                catch (CustodyInsertionException e)
                {
                    if (!e.AppliedAmbiguously) player.m_customData.Remove(receipt);
                    try { WC.RequestAdministrativeSave(); } catch { }
                    throw;
                }
                if (!added) { player.m_customData.Remove(receipt); notice = T("Освободите ячейку: завершаем возврат вещи из предыдущей версии тюрьмы.", "Free a slot to finish returning an item reserved by the previous prison version."); return true; }
                player.m_customData[receipt] = expected;
            }
            if (id != grantId || grantSave == 0)
            { grantSave = 0; grantId = id; grantToken = token; grantIndex = index; grantSave = WC.RequestAdministrativeSave(); nextGrantAck = 0; }
            return true;
        }
        private static int VisibleInventoryRows(Player player)
        {
            Type eaqs = HarmonyLib.AccessTools.TypeByName("EquipmentAndQuickSlots.API");
            if (eaqs != null)
            {
                MethodInfo method = eaqs.GetMethod("GetVisibleRows", BindingFlags.Public | BindingFlags.Static);
                if (method != null) { object result = method.Invoke(null, null); if (result is int && (int)result > 0) return Math.Min((int)result, player.GetInventory().GetHeight()); }
            }
            return Math.Min(4, player.GetInventory().GetHeight());
        }
        private void TrackLegacyWithdrawal(CustodyRecord record)
        {
            if (!pendingLegacyWithdrawals.Any(r => r.SentenceId == record.SentenceId)) pendingLegacyWithdrawals.Add(record.StateCopy());
        }
        private void ResumeLegacyWithdrawals()
        {
            if (pendingLegacyWithdrawals.Count == 0 || Time.realtimeSinceStartup < nextLegacyWithdrawal) return;
            nextLegacyWithdrawal = Time.realtimeSinceStartup + 3;
            foreach (CustodyRecord custodyRecord in pendingLegacyWithdrawals.ToArray())
            {
                CustodyRecord state = custody.FindState(custodyRecord.AccountId, custodyRecord.SentenceId);
                if (state == null || state.Stage != CustodyStage.Released || state.NeedsRecovery)
                { pendingLegacyWithdrawals.Remove(custodyRecord); continue; }
                WithdrawalRecord[] balances = withdrawal.All(custodyRecord.SentenceId);
                if (!balances.Any(b => b.PendingId.Length != 0))
                { custody.MarkCollected(state.AccountId, state.SentenceId); pendingLegacyWithdrawals.Remove(custodyRecord); continue; }
                ZNetPeer peer = peers.Values.FirstOrDefault(p => Ready(p) && WC.GetAdministrativeOwner(p) == state.AccountId);
                if (peer == null) continue;
                foreach (WithdrawalRecord balance in balances)
                {
                    if (balance.PendingId.Length == 0 || balance.NeedsRecovery) continue;
                    string key = balance.PendingId + ":" + peer.m_uid;
                    float after; if (legacyGrantAfter.TryGetValue(key, out after) && Time.realtimeSinceStartup < after) break;
                    if (!withdrawalBaselines.ContainsKey(key)) withdrawalBaselines[key] = WC.GetAdministrativeDurableSequence(peer.m_uid);
                    Send(peer.m_rpc, PrisonProtocol.WithdrawalGrant, w => { PrisonProtocol.Text(w, state.SentenceId); w.Write(balance.ChestIndex); PrisonProtocol.Text(w, balance.PendingId); PrisonProtocol.Blob(w, balance.PendingPayload); });
                    legacyGrantAfter[key] = Time.realtimeSinceStartup + Math.Max(5, 5 + balance.PendingPayload.Length / (128f * 1024));
                    // The client has one durable grant ACK slot. Finish one
                    // reserved item before sending another chest's reservation.
                    break;
                }
            }
        }
    }
}
