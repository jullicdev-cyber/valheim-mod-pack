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
        private float nextOffer, custodyPreparationAt = -1, nextAdmissionFailed;
        private string custodyPreparationToken = "", failedAdmissionToken = "", failedAdmissionReason = "";
        private readonly Dictionary<ZRpc, float> custodySent = new Dictionary<ZRpc, float>();
        private readonly Dictionary<ZRpc, string> custodySentTokens = new Dictionary<ZRpc, string>();
        private byte[] cachedOffer;
        private string offerToken = "";
        private string stockedKitToken = "";
        private bool custodyMigrated, admissionReserved, emergencyChestsPending;
        private float nextEmergencyChests;
        private float nextCustodyMigration;
        private readonly List<CustodyRecord> pendingLegacyWithdrawals = new List<CustodyRecord>();
        private float nextLegacyWithdrawal;
        private readonly Dictionary<string, long> clearBaselines = new Dictionary<string, long>();
        private readonly Dictionary<string, long> collectionBaselines = new Dictionary<string, long>();
        private bool LegacyLayout { get { return region != null && (Hosting ? ArenaBuilder.LayoutVersion(region) : localLayoutVersion) < 2; } }
        internal bool PreparingCustody { get { return localSentence != null && !localSentence.EmergencyRelease && !LegacyLayout && (localCustodyStage < (int)CustodyStage.Deposited || localRecovery.Length != 0); } }
        private bool CanFinishLocalRelease { get { return localSentence != null && localSentence.PendingRelease && (localSentence.EmergencyRelease || LegacyLayout || localCustodyStage >= (int)CustodyStage.Deposited); } }
        private bool FightReady { get { return Confined && !localSentence.PendingRelease && !PreparingCustody; } }
        private void ResetCustody()
        {
            if (custody != null) { custody.Dispose(); custody = null; }
            custodySent.Clear(); custodySentTokens.Clear(); clearBaselines.Clear(); collectionBaselines.Clear();
            localCustodyStage = -1; localLayoutVersion = 2; localCustodyAccount = localCustodyToken = localCustodyHash = localRecovery = "";
            clearSave = 0; nextOffer = 0; waveTier = 0; cachedOffer = null; offerToken = stockedKitToken = "";
            custodyPreparationAt = -1; custodyPreparationToken = ""; nextAdmissionFailed = 0;
            failedAdmissionToken = failedAdmissionReason = "";
            custodyMigrated = admissionReserved = false; nextCustodyMigration = 0;
            emergencyChestsPending = false; nextEmergencyChests = 0;
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
            return record != null && !record.Closed && !record.NeedsRecovery && record.Stage == CustodyStage.Deposited;
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
            if (localSentence != null && localSentence.EmergencyRelease) return T("Принудительное освобождение: управление разблокировано. Сохраняем завершение; личные вещи и добыча сохраняются.", "Emergency release: controls unlocked. Saving completion; personal belongings and loot are preserved.");
            if (localRecovery.Length != 0) return T("Вещи сохранены у хоста. Требуется проверка хранения: ", "Belongings backed up by the host. Custody needs review: ") + localRecovery;
            if (localSentence != null && PreparingCustody) return T("Сохраняем и переносим ваши вещи в четыре железных сундука. Срок начнётся после переноса.", "Saving and moving your belongings to four iron chests. Time starts after transfer.");
            return T("Вещи находятся в четырёх обычных сундуках в прихожей. Снаряжение для боя — в сундуке камеры. Открывайте сундуки клавишей E; добыча остаётся у вас.", "Your belongings are in four ordinary foyer chests. Combat equipment is in the cell chest. Open chests with E; arena loot remains yours.");
        }
        private void ProgressCustody()
        {
            if (Hosting || localSentence == null || localSentence.PendingRelease || LegacyLayout) return;
            if (localCustodyStage >= (int)CustodyStage.Deposited) { custodyPreparationAt = -1; custodyPreparationToken = ""; return; }
            if (custodyPreparationToken != localSentence.SentenceId)
            { custodyPreparationToken = localSentence.SentenceId; custodyPreparationAt = Time.realtimeSinceStartup; nextAdmissionFailed = 0; }
            if (failedAdmissionToken == localSentence.SentenceId) { AdmissionFailed(failedAdmissionReason); return; }
            if (localRecovery.Length != 0) { AdmissionFailed(localRecovery); return; }
            if (Time.realtimeSinceStartup - custodyPreparationAt >= 90)
            { AdmissionFailed("Inventory transfer did not complete within 90 seconds; original belongings retained for safe cancellation."); return; }
            if (!WC.AdministrativeReady) return;
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
                    {
                        try { cachedOffer = CustodyInventory.CaptureForAdmission(player); offerToken = localSentence.SentenceId; }
                        catch (Exception e) { AdmissionFailed(e.Message); return; }
                    }
                    byte[] payload = cachedOffer;
                    ToHost(PrisonProtocol.InventoryOffer, w => { PrisonProtocol.Text(w, localSentence.SentenceId); PrisonProtocol.Blob(w, payload); });
                }
            }
        }
        private bool ServerCustodyMessage(ZNetPeer peer, int kind, BinaryReader reader)
        {
            if (kind != PrisonProtocol.InventoryOffer && kind != PrisonProtocol.InventoryCleared && kind != PrisonProtocol.AdmissionFailed) return false;
            string account = WC.GetAdministrativeOwner(peer), token = PrisonProtocol.Text(reader);
            SentenceState sentence = store.Find(account);
            if (sentence == null || sentence.PendingRelease || sentence.SentenceId != token || LegacyLayout) return true;
            if (kind == PrisonProtocol.AdmissionFailed)
            {
                string reason = PrisonProtocol.Text(reader); PrisonProtocol.End(reader);
                if (System.Text.Encoding.UTF8.GetByteCount(reason) > 512) throw new InvalidDataException("Admission failure reason exceeds its limit.");
                CustodyRecord currentState = custody.FindState(account, token);
                if (currentState != null && currentState.Stage >= CustodyStage.Deposited && !currentState.NeedsRecovery) return true;
                SaveEmergencyRelease(account); Report("Prison admission cancelled safely: " + reason); return true;
            }
            if (kind == PrisonProtocol.InventoryOffer)
            {
                byte[] payload = PrisonProtocol.Blob(reader); PrisonProtocol.End(reader);
                CustodyRecord record = custody.Find(account, token);
                if (record != null && record.Stage != CustodyStage.Prepared) return true;
                int width, height; ArenaBuilder.ChestDimensions(out width, out height);
                try { CustodyInventory.PrepareChestPayloads(payload, width, height); }
                catch (CustodyCapacityException e) {
                    // Only the current player's capacity is terminal. No
                    // clear request or journal deposit has happened yet.
                    SaveEmergencyRelease(account); CancelAdmissionStorage();
                    Report(T("Посадка отменена: все вещи игрока не помещаются в четыре сундука. Инвентарь сохранён. ",
                        "Admission cancelled: the player's belongings do not fit four chests. Inventory preserved. ") + e.Message);
                    return true;
                }
                if (!admissionReserved) PrepareAdmissionStorage();
                if (record == null) {
                    ZDO[] chests = Chests();
                    byte[][] targets = CustodyInventory.PrepareAdmissionPayloads(chests, payload, width, height);
                    string[] baselines = CustodyInventory.ChestPayloadFingerprints(chests);
                    record = custody.Prepare(account, token, payload, targets, baselines);
                }
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
            if (localSentence == null || localSentence.PendingRelease || failedAdmissionToken == token || localSentence.SentenceId != token || !WC.AdministrativeReady || CustodyInventory.Fingerprint(payload) != hash) return true;
            if (localCustodyStage >= (int)CustodyStage.Cleared) return true;
            Player player = Player.m_localPlayer;
            if (player == null || player.IsDead() || player.IsTeleporting()) return true;
            if (InventoryGui.IsVisible()) InventoryGui.instance.Hide();
            if (!CustodyInventory.HasClearReceipt(player, world, token, hash))
            {
                try { CustodyInventory.ClearExact(player, payload, world, token); }
                catch (Exception e) { AdmissionFailed(e.Message); return true; }
            }
            localCustodyStage = (int)CustodyStage.Prepared; localCustodyToken = token; localCustodyHash = hash;
            if (clearSave == 0) clearSave = WC.RequestAdministrativeSave(); nextOffer = 0;
            return true;
        }
        private void AdmissionFailed(string reason)
        {
            if (localSentence == null || localSentence.PendingRelease || localCustodyStage >= (int)CustodyStage.Deposited || Time.realtimeSinceStartup < nextAdmissionFailed) return;
            nextAdmissionFailed = Time.realtimeSinceStartup + 3;
            if (String.IsNullOrEmpty(reason)) reason = "Inventory preparation failed; belongings were not replayed.";
            while (System.Text.Encoding.UTF8.GetByteCount(reason) > 512) reason = reason.Substring(0, reason.Length - 1);
            failedAdmissionToken = localSentence.SentenceId; failedAdmissionReason = reason;
            Report(T("Подготовка заключения остановлена; запрошено безопасное освобождение: ", "Admission stopped; safe release requested: ") + reason);
            ToHost(PrisonProtocol.AdmissionFailed, w => { PrisonProtocol.Text(w, localSentence.SentenceId); PrisonProtocol.Text(w, reason); });
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
            if (emergencyChestsPending && Time.realtimeSinceStartup >= nextEmergencyChests && !admissionReserved && !custody.HasOutstanding)
                ReopenEmergencyChests();
            if (!custodyMigrated && !admissionReserved && Time.realtimeSinceStartup >= nextCustodyMigration
                && !custody.AllStates().Any(r => !r.Closed && !r.PublicAccess && r.Stage < CustodyStage.Deposited))
            {
                nextCustodyMigration = Time.realtimeSinceStartup + 10;
                try { MigratePublicChests(); custodyMigrated = true; }
                catch (Exception e) { Report("Prison chest migration paused; original backups retained: " + e.Message); }
            }
            foreach (SentenceState sentence in store.All())
            {
                CustodyRecord state = custody.FindState(sentence.AccountId, sentence.SentenceId);
                // Version 1.3 could pardon before the first inventory offer,
                // leaving PendingRelease unable to reach Deposited forever.
                // Promote that old durable decision to explicit cancellation.
                if (sentence.PendingRelease && !sentence.EmergencyRelease
                    && (state == null || state.NeedsRecovery || state.Stage < CustodyStage.Deposited))
                { store.RequestEmergencyRelease(true, sentence.AccountId); nextHost = Time.realtimeSinceStartup; continue; }
                if (sentence.EmergencyRelease || state != null && state.Closed) continue;
                if (state == null || state.NeedsRecovery) continue;
                if (state.Stage == CustodyStage.Prepared)
                {
                    try {
                        if (!admissionReserved) PrepareAdmissionStorage();
                        ZNetPeer peer = peers.Values.FirstOrDefault(p => Ready(p) && WC.GetAdministrativeOwner(p) == state.AccountId);
                        if (peer != null && CanSendClear(peer, state.SentenceId)) SendClear(peer, custody.Find(state.AccountId, state.SentenceId));
                    } catch (Exception e) { Report("Custody reservation paused; original inventory preserved: " + e.Message); }
                }
                else if (state.Stage == CustodyStage.Cleared)
                {
                    try {
                        if (!admissionReserved) PrepareAdmissionStorage();
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
                {
                    try {
                        // Journal handoff can succeed before one native access
                        // flag or kit setup fails. Resume only access and kit.
                        if (!state.PublicAccess || admissionReserved) { PublishPublicChests(state, Chests()); network.Save(true, false, false); }
                        EnsureCurrentSentenceKit(state.SentenceId); admissionReserved = false;
                    } catch (Exception e) { Report(e.Message); }
                }
            }
            ResumeLegacyWithdrawals();
            ArenaBuilder.SetExitLocked(region, store.All().Any(s => { if (s.EmergencyRelease) return false; CustodyRecord r = custody.FindState(s.AccountId, s.SentenceId); return r == null || !r.Closed && (r.Stage < CustodyStage.Released || r.NeedsRecovery); }));
        }
        private void PublishPublicChests(CustodyRecord record, ZDO[] chests)
        {
            PublishCustodyAccess(record, chests,
                () => { custody.MarkPublicAccess(record.AccountId, record.SentenceId); },
                values => CustodyInventory.SetPublic(values, true));
        }
        private static void PublishCustodyAccess(CustodyRecord record, ZDO[] chests, Action markDurable, Action<ZDO[]> open)
        {
            foreach (ZDO chest in chests)
                if (chest.GetString(CustodyInventory.TokenKey, "") != record.SentenceId) throw new InvalidDataException("Native chest assignment differs from the escrow record.");
            // Durable handoff precedes native access. This record must never
            // project originals again, even after restoring an older world.
            if (!record.PublicAccess) markDurable();
            open(chests);
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
                if (record.Closed || record.NeedsRecovery || record.Stage < CustodyStage.Deposited) continue;
                bool assigned = chests.All(c => c.GetString(CustodyInventory.TokenKey, "") == record.SentenceId);
                if (record.PublicAccess)
                {
                    if (assigned) CustodyInventory.SetPublic(chests, true);
                    if (assigned && record.Stage == CustodyStage.Deposited) EnsureCurrentSentenceKit(record.SentenceId);
                    if (record.Stage == CustodyStage.Released) custody.MarkCollected(record.AccountId, record.SentenceId);
                    continue;
                }
                if (assigned && !chests.Any(CustodyInventory.IsPublic))
                {
                    if (record.Stage >= CustodyStage.Released)
                    {
                        WithdrawalRecord[] balances = LegacyWithdrawals().All(record.SentenceId);
                        if (balances.Length != 4 || balances.Any(b => b.NeedsRecovery)) throw new InvalidDataException("Legacy withdrawal balances require host review; no original items replayed.");
                        foreach (WithdrawalRecord balance in balances)
                            CustodyInventory.SetNativeItems(chests[balance.ChestIndex], balance.PendingId.Length == 0 ? balance.RemainingPayload : balance.PendingRemainingPayload);
                    }
                    network.Save(true, false, false);
                }
                custody.MarkPublicAccess(record.AccountId, record.SentenceId);
                if (assigned) { CustodyInventory.SetPublic(chests, true); if (record.Stage == CustodyStage.Deposited) EnsureCurrentSentenceKit(record.SentenceId); }
                if (record.Stage == CustodyStage.Released) custody.MarkCollected(record.AccountId, record.SentenceId);
            }
            // Unassigned empty chests also behave as native containers.
            if (!admissionReserved && !store.All().Any(s => { CustodyRecord r = custody.FindState(s.AccountId, s.SentenceId); return r == null || r.Stage < CustodyStage.Deposited; })) CustodyInventory.SetPublic(chests, true);
            network.Save(true, false, false);
        }
        private void PrepareAdmissionStorage()
        { RequireHost(); ZDO[] chests = Chests(); CustodyInventory.RequireAdmissionChests(chests); CustodyInventory.SetPublic(chests, false); admissionReserved = true; }
        private void CancelAdmissionStorage()
        { if (admissionReserved) { admissionReserved = false; ReopenEmergencyChests(); } }
        private bool CompleteHostRelease(string account, string token)
        { return CompleteHostRelease(account, token, false, 0); }
        private bool CompleteHostRelease(string account, string token, bool clientCleared, long durable)
        {
            SentenceState sentence = store.Find(account);
            if (sentence != null && sentence.SentenceId == token && sentence.EmergencyRelease)
                return CompleteEmergencyCustodyRelease(account, token, clientCleared, durable);
            CustodyRecord record = custody.Find(account, token);
            if (record == null || record.Closed || record.Stage < CustodyStage.Deposited) return false;
            if (!record.PublicAccess || record.NeedsRecovery)
                return CompleteEmergencyCustodyRelease(account, token, clientCleared, durable);
            if (record.Stage == CustodyStage.Deposited) custody.Release(account, token);
            if (record.Stage <= CustodyStage.Released) custody.MarkCollected(account, token);
            // Native contents have no debt to an escrow journal. Release does
            // not inspect missing, emptied, stolen or reassigned chest items.
            admissionReserved = false;
            try { ArenaBuilder.SetExitLocked(region, false); }
            catch (Exception e) { Report("Release saved; exit gate cleanup deferred: " + e.Message); }
            ReopenPublicChests(); return true;
        }
        private void ForceRelease(string account)
        { RequireHost(); SaveEmergencyRelease(account); }
        private void SaveEmergencyRelease(string account)
        {
            if (!Hosting || store == null || fatalStore || store.Faulted) throw new UnauthorizedAccessException("Only the authoritative host may cancel imprisonment.");
            SentenceState sentence = store.RequestEmergencyRelease(true, account);
            if (sentence == null) { notice = T("У игрока нет действующего заключения.", "This player has no active sentence."); return; }
            nextHost = Time.realtimeSinceStartup;
            // Unlocking is secondary to the durable cancellation. Missing or
            // broken prison pieces must never prevent this release decision.
            try { if (region != null) { ArenaBuilder.SetExitLocked(region, false); ArenaBuilder.CancelPendingWave(); ArenaBuilder.CleanupMobs(region, true); } }
            catch (Exception e) { Report("Emergency release saved; prison cleanup deferred: " + e.Message); }
            notice = T("Назначено принудительное освобождение. Управление разблокируется при получении состояния; отключённый игрок освобождается при входе. Личные вещи не удаляются.", "Emergency release saved. Controls unlock on receipt; offline players are released on reconnect. Personal belongings are not removed.");
        }
        private bool CompleteEmergencyCustodyRelease(string account, string token, bool clientCleared, long durable)
        {
            CustodyRecord record = custody.Find(account, token);
            if (record == null || record.Closed) { admissionReserved = false; ReopenEmergencyChests(); return true; }
            string recovery = "";
            if (!record.PublicAccess && !record.NeedsRecovery)
            {
                if (record.Stage == CustodyStage.Prepared && clientCleared)
                {
                    if (durable <= 0) throw new InvalidDataException("Emergency confiscation recovery needs a durable character save.");
                    record = custody.MarkCleared(account, token, durable);
                }
                if (record.Stage == CustodyStage.Cleared || record.Stage == CustodyStage.Deposited)
                {
                    try
                    {
                        ZDO[] chests = Chests();
                        if (record.Stage == CustodyStage.Cleared)
                        {
                            int width, height; ArenaBuilder.ChestDimensions(out width, out height);
                            CustodyInventory.Deposit(chests, record, width, height); network.Save(true, false, false);
                            record = custody.MarkDeposited(account, token, CustodyInventory.ChestIdentities(chests));
                        }
                        PublishPublicChests(record, chests); network.Save(true, false, false);
                        record = custody.Find(account, token);
                    }
                    catch (Exception e)
                    {
                        recovery = "Emergency release: confiscated belongings retained in host custody journal; " + e.Message;
                        if (recovery.Length > 512) recovery = recovery.Substring(0, 512);
                        Report(T("Игрок освобождён; личные вещи сохранены в резервной копии у хоста: ", "Player released; belongings retained in the host backup: ") + CustodyBackupPath(token));
                    }
                }
                // Prepared without a receipt leaves the real inventory intact.
                // Keep its backup archived without materializing another copy.
            }
            else if (record.NeedsRecovery)
            {
                recovery = record.RecoveryReason;
                Report(T("Игрок освобождён; резервная копия личных вещей: ", "Player released; personal belongings backup: ") + CustodyBackupPath(token));
            }
            custody.CloseEmergency(account, token, recovery);
            admissionReserved = false;
            ReopenEmergencyChests(); return true;
        }
        private void ReopenEmergencyChests()
        {
            try { CustodyInventory.SetPublic(Chests(), true); network.Save(true, false, false); emergencyChestsPending = false; }
            catch (Exception e) { emergencyChestsPending = true; nextEmergencyChests = Time.realtimeSinceStartup + 3; Report("Emergency release finished; native chests reopen when loaded: " + e.Message); }
        }
        private void ReopenPublicChests()
        {
            // Public handoff is already durable. Refresh native access without
            // another whole-world checkpoint on the ordinary release path.
            try { CustodyInventory.SetPublic(Chests(), true); emergencyChestsPending = false; }
            catch (Exception e) { emergencyChestsPending = true; nextEmergencyChests = Time.realtimeSinceStartup + 3; Report("Release saved; native chests reopen when loaded: " + e.Message); }
        }
        private string CustodyBackupPath(string token)
        { return Path.Combine(Path.GetDirectoryName(store.StatePath), "custody-world-" + world.ToString("x16"), token + ".custody"); }
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
            string use = Localization.instance == null ? "Use" : Localization.instance.Localize("$KEY_Use");
            notice = T("Снаряжение и паёк лежат в сундуке камеры. Закройте окно, подойдите к сундуку и нажмите ", "Equipment and rations are in the cell chest. Close this window, approach it and press ") + use + ".";
        }
        private void AutoWaves()
        {
            if (region == null) return;
            foreach (ZNetPeer peer in peers.Values)
            {
                SentenceState state = Ready(peer) ? store.Find(WC.GetAdministrativeOwner(peer)) : null; PrisonPoint position;
                if (HostFightReady(state) && Position(peer, out position) && ArenaBuilder.IsInsideArena(region, Vector(position))) {
                    int family, difficulty, revision; string token; ArenaBuilder.GetCombatChoice(region, out family, out difficulty, out token, out revision);
                    if (token != state.SentenceId || revision < 1) return;
                    // Award a finished wave before the next-wave cooldown, and
                    // only for fully spawned mobs from this sentence/run.
                    ArenaBuilder.CompleteWaveIfDefeated(region, token, revision);
                    if (Time.realtimeSinceStartup >= nextWave && ArenaBuilder.CanSpawnWave(region) && ArenaBuilder.LiveMobCount(region) == 0) SpawnWave(waveTier);
                    return;
                }
            }
        }
    }
}
