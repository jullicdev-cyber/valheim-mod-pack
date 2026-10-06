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
        private float nextOffer, nextSite;
        private readonly Dictionary<ZRpc, float> custodySent = new Dictionary<ZRpc, float>();
        private readonly Dictionary<string, long> clearBaselines = new Dictionary<string, long>();
        private readonly Dictionary<string, long> collectionBaselines = new Dictionary<string, long>();
        private bool LegacyLayout { get { return region != null && (Hosting ? ArenaBuilder.LayoutVersion(region) : localLayoutVersion) < ArenaBuilder.CurrentLayoutVersion; } }
        internal bool PreparingCustody { get { return localSentence != null && !LegacyLayout && (localCustodyStage < (int)CustodyStage.Deposited || localRecovery.Length != 0); } }
        private bool FightReady { get { return Confined && !localSentence.PendingRelease && !PreparingCustody; } }
        private void ResetCustody()
        {
            if (custody != null) { custody.Dispose(); custody = null; }
            custodySent.Clear(); clearBaselines.Clear(); collectionBaselines.Clear();
            localCustodyStage = -1; localLayoutVersion = 2; localCustodyAccount = localCustodyToken = localCustodyHash = localRecovery = "";
            clearSave = 0; nextOffer = nextSite = 0; waveTier = 0;
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
            CustodyRecord record = custody.Find(state.AccountId, state.SentenceId);
            return record != null && !record.NeedsRecovery && record.Stage == CustodyStage.Deposited;
        }
        private void WriteCustodyState(BinaryWriter writer, string account, SentenceState state)
        {
            CustodyRecord record = state == null ? custody.Find(account) : custody.Find(account, state.SentenceId);
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
            if (token != localCustodyToken) { clearSave = 0; nextOffer = 0; }
            localCustodyStage = stage; localCustodyAccount = account; localCustodyToken = token; localCustodyHash = hash; localRecovery = recovery;
        }
        private string CustodyStatus()
        {
            if (localRecovery.Length != 0) return T("Вещи сохранены у хоста. Требуется проверка хранения: ", "Belongings backed up by the host. Custody needs review: ") + localRecovery;
            if (localSentence != null && PreparingCustody) return T("Сохраняем и переносим ваши вещи в четыре железных сундука. Срок начнётся после переноса.", "Saving and moving your belongings to four iron chests. Time starts after transfer.");
            if (localCustodyStage == (int)CustodyStage.Released) return T("Решётка открыта. Нажмите E на каждом из четырёх сундуков: вещи выдаются, пока есть свободные ячейки. Добыча остаётся у вас.", "The grille is open. Press E on each of the four chests: items are delivered while slots are free. Your loot remains yours.");
            return T("Личные вещи заперты в четырёх сундуках до освобождения. Добычу с арены вы сохраняете.", "Belongings are locked in four chests until release. Arena loot is yours to keep.");
        }
        private void ProgressCustody()
        {
            if (Hosting || localSentence == null || LegacyLayout || localRecovery.Length != 0 || !WC.AdministrativeReady) return;
            Player player = Player.m_localPlayer;
            if (localCustodyStage < (int)CustodyStage.Deposited)
            {
                if (InventoryGui.IsVisible()) InventoryGui.instance.Hide();
                if (localCustodyStage == (int)CustodyStage.Prepared && CustodyInventory.HasClearReceipt(player, world, localSentence.SentenceId, localCustodyHash))
                {
                    if (clearSave == 0) clearSave = WC.RequestAdministrativeSave();
                    if (WC.IsAdministrativeSaveDurable(clearSave) && Time.realtimeSinceStartup >= nextOffer)
                    { nextOffer = Time.realtimeSinceStartup + 3; ToHost(PrisonProtocol.InventoryCleared, w => { PrisonProtocol.Text(w, localSentence.SentenceId); PrisonProtocol.Text(w, localCustodyHash); w.Write(clearSave); }); }
                }
                else if (localCustodyStage < (int)CustodyStage.Cleared && Time.realtimeSinceStartup >= nextOffer)
                {
                    nextOffer = Time.realtimeSinceStartup + 30;
                    byte[] payload = CustodyInventory.Capture(player);
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
                if (record.PayloadHash != CustodyInventory.Fingerprint(payload)) throw new InvalidDataException("Inventory changed while preparing custody; no items cleared.");
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
            clearSave = WC.RequestAdministrativeSave(); nextOffer = 0;
            return true;
        }
        private void SendClear(ZNetPeer peer, CustodyRecord record)
        {
            if (record.NeedsRecovery || record.Stage != CustodyStage.Prepared) return;
            float previous; if (custodySent.TryGetValue(peer.m_rpc, out previous) && Time.realtimeSinceStartup - previous < 30) return;
            string key = record.SentenceId + ":" + peer.m_uid;
            if (!clearBaselines.ContainsKey(key)) clearBaselines[key] = WC.GetAdministrativeDurableSequence(peer.m_uid);
            Send(peer.m_rpc, PrisonProtocol.InventoryClear, w => { PrisonProtocol.Text(w, record.SentenceId); PrisonProtocol.Text(w, record.PayloadHash); PrisonProtocol.Blob(w, record.OriginalPayload); });
            custodySent[peer.m_rpc] = Time.realtimeSinceStartup;
        }
        private void HostCustodyTick()
        {
            if (region == null) { AutoBuildNearAltars(); return; }
            if (LegacyLayout) return;
            foreach (CustodyRecord record in custody.All())
            {
                if (record.NeedsRecovery) continue;
                if (record.Stage == CustodyStage.Prepared)
                {
                    ZNetPeer peer = peers.Values.FirstOrDefault(p => Ready(p) && WC.GetAdministrativeOwner(p) == record.AccountId);
                    if (peer != null) SendClear(peer, record);
                }
                if (record.Stage == CustodyStage.Cleared)
                {
                    try {
                        int width, height; ArenaBuilder.ChestDimensions(out width, out height); ZDO[] chests = Chests();
                        CustodyInventory.Deposit(chests, record, width, height);
                        network.Save(true, false, false);
                        custody.MarkDeposited(record.AccountId, record.SentenceId, CustodyInventory.ChestIdentities(chests));
                    } catch (Exception e) { Report("Custody deposit paused; original backup preserved: " + e.Message); }
                }
                if (record.Stage == CustodyStage.Released)
                {
                    ReconcileWithdrawals(record);
                }
                if (record.Stage == CustodyStage.Deposited)
                {
                    int width, height; ArenaBuilder.ChestDimensions(out width, out height);
                    ZDO[] chests = Chests();
                    if (!CustodyInventory.ChestsMatch(chests, record, width, height)) ProjectOriginalLocked(record, chests, width, height);
                }
                if (record.Stage == CustodyStage.Collected && Chests().Any(c => c.GetString(CustodyInventory.TokenKey, "") == record.SentenceId)) ProjectWithdrawal(record);
            }
            ArenaBuilder.SetExitLocked(region, store.All().Any(s => { CustodyRecord r = custody.Find(s.AccountId, s.SentenceId); return r == null || r.Stage < CustodyStage.Released || r.NeedsRecovery; }));
        }
        private bool CompleteHostRelease(string account, string token)
        {
            CustodyRecord record = custody.Find(account, token);
            if (record == null || record.NeedsRecovery || record.Stage < CustodyStage.Deposited) return false;
            if (record.Stage == CustodyStage.Deposited)
            {
                int width, height; ArenaBuilder.ChestDimensions(out width, out height); ZDO[] chests = Chests();
                if (!CustodyInventory.ChestsMatch(chests, record, width, height)) { custody.RequireRecovery(account, token, "Locked chest contents changed before release."); return false; }
                // The journal commits before chest access. Released records are
                // never used as a source for automatic item replay after a crash.
                CustodyRecord released = record.Copy(); released.Stage = CustodyStage.Released;
                withdrawal.Ensure(released, CustodyInventory.PrepareChestPayloads(record.OriginalPayload, width, height));
                custody.Release(account, token); CustodyInventory.SetReleased(chests, account, token); network.Save(true, false, false);
            }
            ArenaBuilder.SetExitLocked(region, false); return true;
        }
        internal bool CanOpenCustody(Container container, Humanoid actor)
        {
            ZNetView view = container.GetComponent<ZNetView>();
            if (view == null || !view.IsValid()) return false;
            ZDO zdo = view.GetZDO();
            if (!zdo.GetBool(CustodyInventory.ReleasedKey, false)) return false;
            if (actor != Player.m_localPlayer || Confined || AwaitingState) return false;
            return localCustodyStage == (int)CustodyStage.Released && localRecovery.Length == 0
                && zdo.GetString(CustodyInventory.OwnerKey, "") == localCustodyAccount && zdo.GetString(CustodyInventory.TokenKey, "") == localCustodyToken;
        }
        internal bool CanRequestCustody(Container container, long sender, long character)
        {
            ZNetView view = container.GetComponent<ZNetView>(); if (view == null || !view.IsValid()) return false;
            if (!Hosting) return Player.m_localPlayer != null && sender == ZNet.GetUID() && character == Player.m_localPlayer.GetPlayerID() && CanOpenCustody(container, Player.m_localPlayer);
            if (custody == null) return false;
            ZDO zdo = view.GetZDO(); string account = zdo.GetString(CustodyInventory.OwnerKey, ""), token = zdo.GetString(CustodyInventory.TokenKey, "");
            ZNetPeer peer = peers.Values.FirstOrDefault(p => p.m_uid == sender && Ready(p) && WC.GetAdministrativeOwner(p) == account && WC.GetAdministrativeCharacter(p.m_uid) == character);
            if (peer == null || token.Length != 32) return false;
            CustodyRecord record = custody.Find(account, token);
            return record != null && !record.NeedsRecovery && record.Stage == CustodyStage.Released && zdo.GetBool(CustodyInventory.ReleasedKey, false);
        }
        private void GiveKit()
        {
            if (!FightReady || Player.m_localPlayer == null) return;
            Inventory inventory = Player.m_localPlayer.GetInventory(); int granted = 0;
            foreach (string name in new[] { "ArmorLeatherChest", "ArmorLeatherLegs", "HelmetLeather", "SwordBronze", "AxeBronze", "MaceBronze", "SpearBronze", "ShieldWood", "BowFineWood", "ArrowWood" })
            {
                if (inventory.GetAllItems().Any(held => IsLoan(held) && held.m_dropPrefab != null && held.m_dropPrefab.name == name)) continue;
                GameObject prefab = ZNetScene.instance.GetPrefab(name); ItemDrop drop = prefab == null ? null : prefab.GetComponent<ItemDrop>();
                if (drop == null) continue; ItemDrop.ItemData item = drop.m_itemData.Clone(); item.m_dropPrefab = prefab;
                item.m_customData = new Dictionary<string, string>(item.m_customData); item.m_customData[LoanKey] = "1"; item.m_equipped = false;
                item.m_stack = name == "ArrowWood" ? 100 : 1; item.m_durability = item.GetMaxDurability();
                if (!inventory.AddItem(item)) break; ++granted;
            }
            if (granted > 0) WC.RequestAdministrativeSave();
            notice = T("Выдано базовое снаряжение: ", "Basic equipment granted: ") + granted + T(". Наденьте броню в инвентаре. При освобождении снаряжение тюрьмы убирается, добыча остаётся.", ". Equip armor in your inventory. Prison equipment is removed on release; loot remains.");
        }
        private void AutoWaves()
        {
            if (region == null || Time.realtimeSinceStartup < nextWave || ArenaBuilder.LiveMobCount(region) != 0) return;
            foreach (ZNetPeer peer in peers.Values)
            {
                SentenceState state = Ready(peer) ? store.Find(WC.GetAdministrativeOwner(peer)) : null; PrisonPoint position;
                if (HostFightReady(state) && Position(peer, out position) && ArenaBuilder.IsInsideArena(region, Vector(position))) { SpawnWave(waveTier); return; }
            }
        }
        private void AutoBuildNearAltars()
        {
            if (!WC.AdministrativeReady || Player.m_localPlayer == null || ZoneSystem.instance == null || Time.realtimeSinceStartup < nextSite) return;
            nextSite = Time.realtimeSinceStartup + 60;
            ZoneSystem.LocationInstance temple;
            if (!ZoneSystem.instance.FindClosestLocation("StartTemple", Player.m_localPlayer.transform.position, out temple)) return;
            Vector3 delta = temple.m_position - Player.m_localPlayer.transform.position; delta.y = 0;
            if (delta.sqrMagnitude > 140 * 140) return;
            try { BuildPrison(); } catch (Exception e) { Report(e.Message); }
        }
    }
}
