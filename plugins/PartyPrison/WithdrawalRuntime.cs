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
        private int collectingChest = -1;
        private float nextCollection, nextGrantAck;
        private string grantId = "", grantToken = "";
        private int grantIndex;
        private long grantSave;
        private readonly Dictionary<string, long> withdrawalBaselines = new Dictionary<string, long>();
        private void ResetWithdrawal()
        {
            if (withdrawal != null) { withdrawal.Dispose(); withdrawal = null; }
            collectingChest = -1; nextCollection = nextGrantAck = 0; grantSave = 0; grantId = grantToken = ""; withdrawalBaselines.Clear();
        }
        internal void CollectFromChest(Container container, Humanoid actor)
        {
            if (!CanOpenCustody(container, actor)) { notice = T("Личные вещи доступны владельцу после окончания срока.", "Belongings are available to their owner after the sentence."); return; }
            ZDO chest = container.GetComponent<ZNetView>().GetZDO();
            collectingChest = chest.GetInt(ArenaBuilder.CustodyIndexKey, -1); nextCollection = 0;
            notice = T("Забираем вещи из сундука. Если инвентарь заполнится, освободите место и нажмите E ещё раз.", "Collecting belongings. If inventory fills, make room and press E again.");
        }
        private void ProgressWithdrawal()
        {
            if (Hosting || !WC.AdministrativeReady) return;
            if (grantSave != 0 && WC.IsAdministrativeSaveDurable(grantSave) && Time.realtimeSinceStartup >= nextGrantAck)
            {
                nextGrantAck = Time.realtimeSinceStartup + 2;
                ToHost(PrisonProtocol.WithdrawalAck, w => { PrisonProtocol.Text(w, grantToken); w.Write(grantIndex); PrisonProtocol.Text(w, grantId); w.Write(grantSave); });
            }
            if (collectingChest < 0 || localCustodyStage != (int)CustodyStage.Released || localRecovery.Length != 0 || Time.realtimeSinceStartup < nextCollection) return;
            nextCollection = Time.realtimeSinceStartup + 3;
            ToHost(PrisonProtocol.WithdrawalRequest, w => { PrisonProtocol.Text(w, localCustodyToken); w.Write(collectingChest); });
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
                withdrawal.Commit(token, index, id); withdrawalBaselines.Remove(key); ProjectWithdrawal(custodyRecord); nextHost = Time.realtimeSinceStartup; return true;
            }
            PrisonProtocol.End(reader); PrisonPoint position; ZDO[] chests = Chests();
            if (!Position(peer, out position) || Vector3.Distance(Vector(position), chests[index].GetPosition()) > 6) return true;
            if (record.PendingId.Length == 0)
            {
                Inventory remaining = CustodyInventory.Decode(record.RemainingPayload);
                ItemDrop.ItemData first = remaining.GetAllItems().FirstOrDefault();
                if (first == null)
                { Send(peer.m_rpc, PrisonProtocol.WithdrawalGrant, w => { PrisonProtocol.Text(w, token); w.Write(index); PrisonProtocol.Text(w, ""); PrisonProtocol.Blob(w, new byte[0]); }); return true; }
                Inventory one = new Inventory("Custody item", null, 1, 1); ItemDrop.ItemData item = first.Clone(); item.m_equipped = false;
                item.m_gridPos = new Vector2i(0, 0); one.GetAllItems().Add(item);
                remaining.RemoveItem(first);
                record = withdrawal.Begin(token, index, CustodyInventory.Capture(remaining), CustodyInventory.Capture(one));
            }
            string baselineKey = record.PendingId + ":" + peer.m_uid;
            if (!withdrawalBaselines.ContainsKey(baselineKey)) withdrawalBaselines[baselineKey] = WC.GetAdministrativeDurableSequence(peer.m_uid);
            Send(peer.m_rpc, PrisonProtocol.WithdrawalGrant, w => { PrisonProtocol.Text(w, token); w.Write(index); PrisonProtocol.Text(w, record.PendingId); PrisonProtocol.Blob(w, record.PendingPayload); });
            return true;
        }
        private bool ClientWithdrawalMessage(int kind, BinaryReader reader)
        {
            if (kind != PrisonProtocol.WithdrawalGrant) return false;
            string token = PrisonProtocol.Text(reader); int index = reader.ReadInt32(); string id = PrisonProtocol.Text(reader); byte[] bytes = PrisonProtocol.Blob(reader); PrisonProtocol.End(reader);
            if (!WC.AdministrativeReady || localCustodyStage != (int)CustodyStage.Released || token != localCustodyToken || index < 0 || index > 3) return true;
            if (id.Length == 0)
            { collectingChest = -1; grantSave = 0; grantId = ""; notice = T("Этот сундук пуст. Проверьте остальные три сундука.", "This chest is empty. Check the other three chests."); return true; }
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
                    collectingChest = -1;
                    if (!e.AppliedAmbiguously) player.m_customData.Remove(receipt);
                    try { WC.RequestAdministrativeSave(); } catch { }
                    throw;
                }
                if (!added) { player.m_customData.Remove(receipt); collectingChest = -1; notice = T("Инвентарь заполнен. Освободите ячейку и нажмите E на сундуке ещё раз.", "Inventory full. Free a slot and press E on the chest again."); return true; }
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
        private void ProjectWithdrawal(CustodyRecord custodyRecord)
        {
            ZDO[] chests = Chests();
            foreach (WithdrawalRecord record in withdrawal.All(custodyRecord.SentenceId))
            {
                ZDO chest = chests[record.ChestIndex]; chest.SetOwner(ZNet.GetUID());
                chest.Set(CustodyInventory.OwnerKey, custodyRecord.AccountId); chest.Set(CustodyInventory.TokenKey, custodyRecord.SentenceId);
                chest.Set(CustodyInventory.ReleasedKey, true); chest.Set(CustodyInventory.HashKey, CustodyInventory.Fingerprint(record.RemainingPayload));
                chest.Set(ZDOVars.s_items, record.RemainingPayload);
            }
        }
        private void ProjectOriginalLocked(CustodyRecord record, ZDO[] chests, int width, int height)
        {
            // Before release no item can have been issued. The durable escrow
            // therefore repairs a stale world view without creating spendable copies.
            byte[][] bytes = CustodyInventory.PrepareChestPayloads(record.OriginalPayload, width, height);
            for (int i = 0; i < 4; ++i)
            {
                ZDO chest = chests[i]; chest.SetOwner(ZNet.GetUID()); chest.Set(CustodyInventory.OwnerKey, record.AccountId);
                chest.Set(CustodyInventory.TokenKey, record.SentenceId); chest.Set(CustodyInventory.ReleasedKey, false);
                chest.Set(CustodyInventory.HashKey, CustodyInventory.Fingerprint(bytes[i])); chest.Set(ZDOVars.s_items, bytes[i]);
            }
        }
        private void ReconcileWithdrawals(CustodyRecord record)
        {
            WithdrawalRecord[] existing = withdrawal.All(record.SentenceId);
            if (existing.Length != 4 || existing.Any(r => r.NeedsRecovery))
            { custody.RequireRecovery(record.AccountId, record.SentenceId, "Withdrawal balance is incomplete or requires recovery; no items replayed."); return; }
            int width, height; ArenaBuilder.ChestDimensions(out width, out height);
            withdrawal.Ensure(record, CustodyInventory.PrepareChestPayloads(record.OriginalPayload, width, height));
            ProjectWithdrawal(record);
            var records = withdrawal.All(record.SentenceId);
            if (records.Length == 4 && records.All(r => !r.NeedsRecovery && r.PendingId.Length == 0 && CustodyInventory.Count(r.RemainingPayload) == 0))
            { custody.MarkCollected(record.AccountId, record.SentenceId); network.Save(true, false, false); }
        }
    }
}
