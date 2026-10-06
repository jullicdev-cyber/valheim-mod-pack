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
        private readonly PrisonWire wire = new PrisonWire();
        private CustodyStore custody;
        private int localCustodyStage = -1, localLayoutVersion = 2, waveTier;
        private string localCustodyAccount = "", localCustodyToken = "", localCustodyHash = "", localRecovery = "";
        private long clearSave;
        private float nextOffer;
        private readonly Dictionary<ZRpc, float> custodySent = new Dictionary<ZRpc, float>();
        private readonly Dictionary<ZRpc, string> custodySentTokens = new Dictionary<ZRpc, string>();
        private byte[] cachedOffer;
        private string offerToken = "";
        private string stockedKitToken = "";
        private bool custodyMigrated, admissionReserved;
        private float nextCustodyMigration;
        private readonly List<CustodyRecord> pendingLegacyWithdrawals = new List<CustodyRecord>();
        private float nextLegacyWithdrawal;
        private readonly Dictionary<string, long> clearBaselines = new Dictionary<string, long>();
        private readonly Dictionary<string, long> collectionBaselines = new Dictionary<string, long>();
        private bool LegacyLayout { get { return region != null && (Hosting ? ArenaBuilder.LayoutVersion(region) : localLayoutVersion) < 2; } }
        internal bool PreparingCustody { get { return localSentence != null && !LegacyLayout && (localCustodyStage < (int)CustodyStage.Deposited || localRecovery.Length != 0); } }
        private bool FightReady { get { return Confined && !localSentence.PendingRelease && !PreparingCustody; } }
        private void ResetCustody()
        {
            if (custody != null) { custody.Dispose(); custody = null; }
            custodySent.Clear(); custodySentTokens.Clear(); clearBaselines.Clear(); collectionBaselines.Clear();
            localCustodyStage = -1; localLayoutVersion = 2; localCustodyAccount = localCustodyToken = localCustodyHash = localRecovery = "";
            clearSave = 0; nextOffer = 0; waveTier = 0; cachedOffer = null; offerToken = stockedKitToken = "";
            custodyMigrated = admissionReserved = false; nextCustodyMigration = 0;
            pendingLegacyWithdrawals.Clear(); nextLegacyWithdrawal = 0;
        }
        private ZDO[] Chests()
        {
            var chests = ArenaBuilder.GetCustodyZdos(region).ToArray();
            if (chests.Length != 4) throw new InvalidOperationException(T("Ожидаются четыре железных сундука. Подойдите к тюрьме и дождитесь её загрузки.", "Four iron chests are required. Approach the prison and wait for it to load."));
            return chests;
        }
        private bool HostFightReady(SentenceState state)
        {
            if (state == null || state.PendingRelease) return false;
            if (LegacyLayout) return true;
            CustodyRecord record = custody.FindState(state.AccountId, state.SentenceId);
            return record != null && !record.NeedsRecovery && record.Stage == CustodyStage.Deposited;
        }
        private void WriteCustodyState(BinaryWriter writer, string account, SentenceState state)
        {
            CustodyRecord record = state == null ? custody.FindState(account) : custody.FindState(account, state.SentenceId);
            writer.Write(region == null ? 2 : ArenaBuilder.LayoutVersion(region));
            writer.Write(record == null ? -1 : (int)record.Stage);
            PrisonProtocol.Text(writer, record == null ? "" : record.AccountId);
            PrisonProtocol.Text(writer, record == null ? "" : record.SentenceId);
            PrisonProtocol.Text(writer, record == null ? "" : record.PayloadHash);
            PrisonProtocol.Text(writer, record == null ? "" : record.NeedsRecovery ? record.RecoveryReason : "");
        }
        private void ReadCustodyState(int stage, string account, string token, string hash, string recovery)
        {
            if (stage != -1 && (stage < 1 || stage > 5)) throw new InvalidDataException("Invalid custody stage.");
            if (stage != -1) { SentencePolicy.RequireAccountId(account); Guid id; if (!Guid.TryParseExact(token, "N", out id) || hash.Length != 64) throw new InvalidDataException("Invalid custody identity."); }
            if (token != localCustodyToken) { clearSave = 0; nextOffer = 0; cachedOffer = null; offerToken = ""; }
            localCustodyStage = stage; localCustodyAccount = account; localCustodyToken = token; localCustodyHash = hash; localRecovery = recovery;
        }
        private string CustodyStatus()
        {
            if (localRecovery.Length != 0) return T("Вещи сохранены у хоста. Требуется проверка хранения: ", "Belongings backed up by the host. Custody needs review: ") + localRecovery;
            if (localSentence != null && PreparingCustody) return T("Сохраняем и переносим ваши вещи в четыре железных сундука. Срок начнётся после переноса.", "Saving and moving your belongings to four iron chests. Time starts after transfer.");
            return T("Вещи находятся в четырёх обычных сундуках в прихожей. Снаряжение для боя — в сундуке камеры. Открывайте сундуки клавишей E; добыча остаётся у вас.", "Your belongings are in four ordinary foyer chests. Combat equipment is in the cell chest. Open chests with E; arena loot remains yours.");
        }
        private void ProgressCustody()
        {
            if (Hosting || localSentence == null || LegacyLayout || localRecovery.Length != 0 || !WC.AdministrativeReady) return;
            Player player = Player.m_localPlayer;
            if (localCustodyStage < (int)CustodyStage.Deposited)
            {
                if (InventoryGui.IsVisible()) InventoryGui.instance.Hide();
                if (player == null || player.IsDead() || player.IsTeleporting()) return;
                if (localCustodyStage == (int)CustodyStage.Prepared && CustodyInventory.HasClearReceipt(player, world, localSentence.SentenceId, localCustodyHash))
                {
                    if (clearSave == 0) clearSave = WC.RequestAdministrativeSave();
                    if (WC.IsAdministrativeSaveDurable(clearSave) && Time.realtimeSinceStartup >= nextOffer)
                    { nextOffer = Time.realtimeSinceStartup + 3; ToHost(PrisonProtocol.InventoryCleared, w => { PrisonProtocol.Text(w, localSentence.SentenceId); PrisonProtocol.Text(w, localCustodyHash); w.Write(clearSave); }); }
                }
                else if (localCustodyStage < (int)CustodyStage.Prepared && Time.realtimeSinceStartup >= nextOffer)
                {
                    nextOffer = Time.realtimeSinceStartup + 30;
                    if (cachedOffer == null || offerToken != localSentence.SentenceId)
                    { cachedOffer = CustodyInventory.CaptureForAdmission(player); offerToken = localSentence.SentenceId; }
                    byte[] payload = cachedOffer;
                    ToHost(PrisonProtocol.InventoryOffer, w => { PrisonProtocol.Text(w, localSentence.SentenceId); PrisonProtocol.Blob(w, payload); });
                }
            }
        }
        private bool ServerCustodyMessage(ZNetPeer peer, int kind, BinaryReader reader)
        {
            if (kind != PrisonProtocol.InventoryOffer && kind != PrisonProtocol.InventoryCleared) return false;
            string account = WC.GetAdministrativeOwner(peer), token = PrisonProtocol.Text(reader);
            SentenceState sentence = store.Find(account);
            if (sentence == null || sentence.SentenceId != token || LegacyLayout) return true;
            if (kind == PrisonProtocol.InventoryOffer)
            {
                byte[] payload = PrisonProtocol.Blob(reader); PrisonProtocol.End(reader);
                CustodyRecord record = custody.Find(account, token);
                if (record != null && record.Stage != CustodyStage.Prepared) return true;
                int width, height; ArenaBuilder.ChestDimensions(out width, out height);
                CustodyInventory.PrepareChestPayloads(payload, width, height);
                if (record == null) { CustodyInventory.RequireEmptyChests(Chests()); record = custody.Prepare(account, token, payload); }
                // A retry cannot rebind the immutable backup. Native equipment
                // bookkeeping may have changed since its first serialization;
                // the client validates substantive item equivalence before clear.
                SendClear(peer, record); return true;
            }
            string hash = PrisonProtocol.Text(reader); long durable = reader.ReadInt64(); PrisonProtocol.End(reader);
            CustodyRecord current = custody.Find(account, token); long baseline;
            string key = token + ":" + peer.m_uid;
            if (current == null || current.NeedsRecovery || current.Stage != CustodyStage.Prepared || current.PayloadHash != hash
                || !clearBaselines.TryGetValue(key, out baseline) || durable <= Math.Max(0, baseline) || WC.GetAdministrativeDurableSequence(peer.m_uid) < durable) return true;
            custody.MarkCleared(account, token, durable); nextHost = Time.realtimeSinceStartup; return true;
        }
        private bool ClientCustodyMessage(int kind, BinaryReader reader)
        {
            if (kind != PrisonProtocol.InventoryClear) return false;
            string token = PrisonProtocol.Text(reader), hash = PrisonProtocol.Text(reader); byte[] payload = PrisonProtocol.Blob(reader); PrisonProtocol.End(reader);
            if (localSentence == null || localSentence.SentenceId != token || !WC.AdministrativeReady || CustodyInventory.Fingerprint(payload) != hash) return true;
            if (localCustodyStage >= (int)CustodyStage.Cleared) return true;
            Player player = Player.m_localPlayer;
            if (InventoryGui.IsVisible()) InventoryGui.instance.Hide();
            if (!CustodyInventory.HasClearReceipt(player, world, token, hash)) CustodyInventory.ClearExact(player, payload, world, token);
            localCustodyStage = (int)CustodyStage.Prepared; localCustodyToken = token; localCustodyHash = hash;
            if (clearSave == 0) clearSave = WC.RequestAdministrativeSave(); nextOffer = 0;
            return true;
        }
        private void SendClear(ZNetPeer peer, CustodyRecord record)
        {
            if (record.NeedsRecovery || record.Stage != CustodyStage.Prepared) return;
            if (!CanSendClear(peer, record.SentenceId)) return;
            string key = record.SentenceId + ":" + peer.m_uid;
            if (!clearBaselines.ContainsKey(key)) clearBaselines[key] = WC.GetAdministrativeDurableSequence(peer.m_uid);
            Send(peer.m_rpc, PrisonProtocol.InventoryClear, w => { PrisonProtocol.Text(w, record.SentenceId); PrisonProtocol.Text(w, record.PayloadHash); PrisonProtocol.Blob(w, record.OriginalPayload); });
            // The wire is paced at 256 KiB/s. A large backpack clear must finish
            // before another full copy can enter the queue for this recipient.
            custodySent[peer.m_rpc] = Time.realtimeSinceStartup + Math.Max(5, 5 + record.OriginalPayload.Length / (128f * 1024));
            custodySentTokens[peer.m_rpc] = record.SentenceId;
        }
        private bool CanSendClear(ZNetPeer peer, string token)
        {
            float after; string previousToken;
            return !custodySentTokens.TryGetValue(peer.m_rpc, out previousToken) || previousToken != token
                || !custodySent.TryGetValue(peer.m_rpc, out after) || Time.realtimeSinceStartup >= after;
        }
        private void ForgetCustodyPeer(ZRpc rpc)
        { if (rpc == null) return; custodySent.Remove(rpc); custodySentTokens.Remove(rpc); }
        private void HostCustodyTick()
        {
            if (region == null || LegacyLayout) return;
            if (!custodyMigrated && Time.realtimeSinceStartup >= nextCustodyMigration)
            {
                nextCustodyMigration = Time.realtimeSinceStartup + 10;
                try { MigratePublicChests(); custodyMigrated = true; }
                catch (Exception e) { Report("Prison chest migration paused; original backups retained: " + e.Message); }
            }
            foreach (SentenceState sentence in store.All())
            {
                CustodyRecord state = custody.FindState(sentence.AccountId, sentence.SentenceId);
                if (state == null || state.NeedsRecovery) continue;
                if (state.Stage == CustodyStage.Prepared)
                { ZNetPeer peer = peers.Values.FirstOrDefault(p => Ready(p) && WC.GetAdministrativeOwner(p) == state.AccountId); if (peer != null && CanSendClear(peer, state.SentenceId)) SendClear(peer, custody.Find(state.AccountId, state.SentenceId)); }
                else if (state.Stage == CustodyStage.Cleared)
                {
                    try {
                        int width, height; ArenaBuilder.ChestDimensions(out width, out height); ZDO[] chests = Chests();
                        CustodyRecord record = custody.Find(state.AccountId, state.SentenceId);
                        CustodyInventory.Deposit(chests, record, width, height);
                        network.Save(true, false, false);
                        record = custody.MarkDeposited(record.AccountId, record.SentenceId, CustodyInventory.ChestIdentities(chests));
                        PublishPublicChests(record, chests); EnsureCurrentSentenceKit(record.SentenceId); admissionReserved = false;
                        network.Save(true, false, false);
                    } catch (Exception e) { Report("Custody deposit paused; original backup preserved: " + e.Message); }
                }
                else if (state.Stage == CustodyStage.Deposited)
                { try { if (!state.PublicAccess) { PublishPublicChests(state, Chests()); network.Save(true, false, false); } EnsureCurrentSentenceKit(state.SentenceId); } catch (Exception e) { Report(e.Message); } }
            }
            ResumeLegacyWithdrawals();
            ArenaBuilder.SetExitLocked(region, store.All().Any(s => { CustodyRecord r = custody.FindState(s.AccountId, s.SentenceId); return r == null || r.Stage < CustodyStage.Released || r.NeedsRecovery; }));
        }
        private void PublishPublicChests(CustodyRecord record, ZDO[] chests)
        {
            foreach (ZDO chest in chests)
                if (chest.GetString(CustodyInventory.TokenKey, "") != record.SentenceId) throw new InvalidDataException("Native chest assignment differs from the escrow record.");
            // Durable handoff precedes native access. This record must never
            // project originals again, even after restoring an older world.
            custody.MarkPublicAccess(record.AccountId, record.SentenceId);
            CustodyInventory.SetPublic(chests, true);
        }
        private void EnsureCurrentSentenceKit(string token)
        {
            if (stockedKitToken == token) return;
            ArenaBuilder.EnsureSentenceKit(region, token); ZDO kit = ArenaBuilder.GetKitZdo(region);
            if (kit == null || kit.GetString(ArenaBuilder.KitTokenKey, "") != token) throw new InvalidOperationException("Cell equipment chest is not loaded yet.");
            stockedKitToken = token; network.Save(true, false, false);
        }
        private void MigratePublicChests()
        {
            ZDO[] chests = Chests();
            foreach (CustodyRecord record in custody.AllStates())
            {
                if (record.NeedsRecovery || record.Stage < CustodyStage.Deposited) continue;
                bool assigned = chests.All(c => c.GetString(CustodyInventory.TokenKey, "") == record.SentenceId);
                if (record.PublicAccess)
                {
                    if (assigned) CustodyInventory.SetPublic(chests, true);
                    if (assigned && record.Stage == CustodyStage.Deposited) EnsureCurrentSentenceKit(record.SentenceId);
                    if (record.Stage == CustodyStage.Released)
                    { if (withdrawal.All(record.SentenceId).Any(b => b.PendingId.Length != 0)) TrackLegacyWithdrawal(record); else custody.MarkCollected(record.AccountId, record.SentenceId); }
                    continue;
                }
                if (assigned && !chests.Any(CustodyInventory.IsPublic))
                {
                    if (record.Stage >= CustodyStage.Released)
                    {
                        WithdrawalRecord[] balances = withdrawal.All(record.SentenceId);
                        if (balances.Length != 4 || balances.Any(b => b.NeedsRecovery)) throw new InvalidDataException("Legacy withdrawal balances require host review; no original items replayed.");
                        foreach (WithdrawalRecord balance in balances)
                            CustodyInventory.SetNativeItems(chests[balance.ChestIndex], balance.PendingId.Length == 0 ? balance.RemainingPayload : balance.PendingRemainingPayload);
                    }
                    network.Save(true, false, false);
                }
                custody.MarkPublicAccess(record.AccountId, record.SentenceId);
                if (assigned) { CustodyInventory.SetPublic(chests, true); if (record.Stage == CustodyStage.Deposited) EnsureCurrentSentenceKit(record.SentenceId); }
                if (record.Stage == CustodyStage.Released)
                { if (withdrawal.All(record.SentenceId).Any(b => b.PendingId.Length != 0)) TrackLegacyWithdrawal(record); else custody.MarkCollected(record.AccountId, record.SentenceId); }
            }
            // Unassigned empty chests also behave as native containers.
            if (!admissionReserved && !store.All().Any(s => { CustodyRecord r = custody.FindState(s.AccountId, s.SentenceId); return r == null || r.Stage < CustodyStage.Deposited; })) CustodyInventory.SetPublic(chests, true);
            network.Save(true, false, false);
        }
        private void PrepareAdmissionStorage()
        { RequireHost(); ZDO[] chests = Chests(); CustodyInventory.RequireEmptyChests(chests); CustodyInventory.SetPublic(chests, false); admissionReserved = true; }
        private void CancelAdmissionStorage()
        { if (admissionReserved) { CustodyInventory.SetPublic(Chests(), true); admissionReserved = false; } }
        private bool CompleteHostRelease(string account, string token)
        {
            CustodyRecord record = custody.Find(account, token);
            if (record == null || record.NeedsRecovery || record.Stage < CustodyStage.Deposited) return false;
            if (record.Stage == CustodyStage.Deposited)
            {
                ZDO[] chests = Chests();
                if (!record.PublicAccess) PublishPublicChests(record, chests);
                custody.Release(account, token); custody.MarkCollected(account, token);
                CustodyInventory.SetPublic(chests, true); network.Save(true, false, false);
            }
            ArenaBuilder.SetExitLocked(region, false); return true;
        }
        internal bool CanOpenCustody(Container container, Humanoid actor)
        {
            ZNetView view = container.GetComponent<ZNetView>();
            if (view == null || !view.IsValid()) return false;
            ZDO zdo = view.GetZDO();
            return CustodyInventory.IsPublic(zdo) && actor != null && !PreparingCustody;
        }
        internal bool CanRequestCustody(Container container, long sender, long character)
        {
            ZNetView view = container.GetComponent<ZNetView>(); if (view == null || !view.IsValid()) return false;
            return CustodyInventory.IsPublic(view.GetZDO());
        }
        private void GiveKit()
        {
            if (!FightReady || Player.m_localPlayer == null) return;
            Container chest = ArenaBuilder.GetKitContainer(region);
            if (chest != null && Vector3.Distance(Player.m_localPlayer.transform.position, chest.transform.position) <= 4)
            { window.Hide(); chest.Interact(Player.m_localPlayer, false, false); return; }
            notice = T("Базовое снаряжение лежит в обычном сундуке внутри камеры. Откройте его клавишей E.", "Basic equipment is in the ordinary cell chest. Open it with E.");
        }
        private void AutoWaves()
        {
            if (region == null || Time.realtimeSinceStartup < nextWave) return;
            foreach (ZNetPeer peer in peers.Values)
            {
                SentenceState state = Ready(peer) ? store.Find(WC.GetAdministrativeOwner(peer)) : null; PrisonPoint position;
                if (HostFightReady(state) && Position(peer, out position) && ArenaBuilder.IsInsideArena(region, Vector(position))) { if (ArenaBuilder.LiveMobCount(region) == 0) SpawnWave(waveTier); return; }
            }
        }
    }
}
