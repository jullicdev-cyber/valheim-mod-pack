// Test-only plugin: real Steam API loopback, no world or character is loaded.
using System;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx;
using Steamworks;
using UnityEngine;

namespace ValheimModPack.NordicRadioTests
{
    [BepInPlugin("valheimmodpack.nordicradio.steamprobe", "Radio native Steam probe", "1.0.0")]
    public sealed class NativeSteamProbe : BaseUnityPlugin
    {
        private const int Channel = 0x564D52;
        private readonly IntPtr[] incoming = new IntPtr[8];
        private readonly byte[] payload = new byte[] { 0, 3, 5, 7, 11, 13 };
        private Callback<SteamNetworkingMessagesSessionRequest_t> requests;
        private Callback<SteamNetworkingMessagesSessionFailed_t> failures;
        private SteamNetworkingIdentity identity;
        private int receivedKinds;
        private float started, sent;
        private bool sending, finished;
        private string details = "";
        private string Root { get { return Environment.GetEnvironmentVariable("NORDICRADIO_STEAM_PROBE"); } }
        private void Awake()
        {
            if (String.IsNullOrEmpty(Root)) { enabled = false; return; }
            started = Time.realtimeSinceStartup;
            Utils.SetSaveDataPath(Path.Combine(Root, "Saves"));
        }
        private void Update()
        {
            if (finished) return;
            try
            {
                if (!SteamManager.Initialized)
                { if (Time.realtimeSinceStartup - started > 40) Finish("FAIL Steam initialization timed out"); return; }
                if (!sending)
                {
                    var steamId = SteamUser.GetSteamID();
                    identity.SetSteamID(steamId);
                    var identity64 = new SteamNetworkingIdentity(); identity64.SetSteamID64(steamId.m_SteamID);
                    if (identity.GetSteamID64() != steamId.m_SteamID || identity64.GetSteamID64() != steamId.m_SteamID)
                        throw new Exception("SetSteamID and SetSteamID64 native identity round trips differ");
                    requests = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(request =>
                    {
                        if (request.m_identityRemote.GetSteamID64() == identity.GetSteamID64())
                            SteamNetworkingMessages.AcceptSessionWithUser(ref request.m_identityRemote);
                    });
                    failures = Callback<SteamNetworkingMessagesSessionFailed_t>.Create(failure =>
                        details += " Failure=" + failure.m_info.m_eEndReason + ":" + failure.m_info.m_szEndDebug);
                    sending = true; sent = Time.realtimeSinceStartup;
                    Send(ref identity, 0); Send(ref identity64, 1);
                }
                int count = SteamNetworkingMessages.ReceiveMessagesOnChannel(Channel, incoming, incoming.Length);
                Exception receiveError = null;
                for (int i = 0; i < count; i++)
                {
                    IntPtr pointer = incoming[i]; incoming[i] = IntPtr.Zero;
                    if (pointer == IntPtr.Zero) continue;
                    try
                    {
                        var message = SteamNetworkingMessage_t.FromIntPtr(pointer);
                        if (message.m_identityPeer.GetSteamID64() != identity.GetSteamID64()) continue;
                        if (message.m_nChannel != Channel || message.m_cbSize != payload.Length)
                            throw new Exception("Native message layout/channel mismatch");
                        var bytes = new byte[message.m_cbSize]; Marshal.Copy(message.m_pData, bytes, 0, bytes.Length);
                        if (bytes[0] > 1) throw new Exception("Unknown identity test payload");
                        for (int j = 1; j < bytes.Length; j++) if (bytes[j] != payload[j]) throw new Exception("Corrupt native payload");
                        receivedKinds |= 1 << bytes[0];
                    }
                    catch (Exception error) { if (receiveError == null) receiveError = error; }
                    finally { SteamNetworkingMessage_t.Release(pointer); }
                }
                if (receiveError != null) throw receiveError;
                if (receivedKinds == 3)
                    Finish("PASS native Steam Messages loopback: both SetSteamID and SetSteamID64 identities sent and received distinct verified payloads; native identity, channel, layout and release checked.\nThis does not test a remote PC, NAT traversal or a live game listener.");
                if (!finished && Time.realtimeSinceStartup - sent > 25)
                {
                    SteamNetConnectionInfo_t info; SteamNetConnectionRealTimeStatus_t status;
                    var state = SteamNetworkingMessages.GetSessionConnectionInfo(ref identity, out info, out status);
                    Finish("FAIL Steam loopback timeout: " + state + " reason=" + info.m_eEndReason + " " + info.m_szEndDebug + details);
                }
            }
            catch (Exception error) { Finish("FAIL " + error); }
        }
        private void Send(ref SteamNetworkingIdentity target, byte kind)
        {
            var bytes = (byte[])payload.Clone(); bytes[0] = kind;
            GCHandle pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var result = SteamNetworkingMessages.SendMessageToUser(ref target, pin.AddrOfPinnedObject(),
                    (uint)bytes.Length, Constants.k_nSteamNetworkingSend_ReliableNoNagle
                    | Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession, Channel);
                if (result != EResult.k_EResultOK) throw new Exception("Send identity " + kind + ": " + result);
            }
            finally { pin.Free(); }
        }
        private void Finish(string result)
        {
            if (finished) return; finished = true;
            File.WriteAllText(Path.Combine(Root, "result.txt"), result);
            Logger.LogInfo(result);
            if (requests != null) requests.Dispose();
            if (failures != null) failures.Dispose();
            if (sending) SteamNetworkingMessages.CloseChannelWithUser(ref identity, Channel);
            Application.Quit();
        }
    }
}
