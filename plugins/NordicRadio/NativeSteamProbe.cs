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
        private readonly byte[] payload = new byte[] { 1, 3, 5, 7, 11, 13 };
        private Callback<SteamNetworkingMessagesSessionRequest_t> requests;
        private Callback<SteamNetworkingMessagesSessionFailed_t> failures;
        private SteamNetworkingIdentity identity;
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
                    identity.SetSteamID(SteamUser.GetSteamID());
                    requests = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(request =>
                    {
                        if (request.m_identityRemote.GetSteamID64() == identity.GetSteamID64())
                            SteamNetworkingMessages.AcceptSessionWithUser(ref request.m_identityRemote);
                    });
                    failures = Callback<SteamNetworkingMessagesSessionFailed_t>.Create(failure =>
                        details += " Failure=" + failure.m_info.m_eEndReason + ":" + failure.m_info.m_szEndDebug);
                    GCHandle pin = GCHandle.Alloc(payload, GCHandleType.Pinned);
                    try
                    {
                        var result = SteamNetworkingMessages.SendMessageToUser(ref identity, pin.AddrOfPinnedObject(),
                            (uint)payload.Length, Constants.k_nSteamNetworkingSend_ReliableNoNagle
                            | Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession, Channel);
                        if (result != EResult.k_EResultOK) throw new Exception("Send: " + result);
                    }
                    finally { pin.Free(); }
                    sending = true; sent = Time.realtimeSinceStartup;
                }
                int count = SteamNetworkingMessages.ReceiveMessagesOnChannel(Channel, incoming, incoming.Length);
                for (int i = 0; i < count; i++)
                {
                    IntPtr pointer = incoming[i];
                    try
                    {
                        var message = SteamNetworkingMessage_t.FromIntPtr(pointer);
                        if (message.m_identityPeer.GetSteamID64() != identity.GetSteamID64()) continue;
                        if (message.m_nChannel != Channel || message.m_cbSize != payload.Length)
                            throw new Exception("Native message layout/channel mismatch");
                        var bytes = new byte[message.m_cbSize]; Marshal.Copy(message.m_pData, bytes, 0, bytes.Length);
                        for (int j = 0; j < bytes.Length; j++) if (bytes[j] != payload[j]) throw new Exception("Corrupt native payload");
                        Finish("PASS native Steam Messages loopback: real send, receive, identity, channel, payload and release.\nThis does not test a remote PC or NAT traversal.");
                    }
                    finally { SteamNetworkingMessage_t.Release(pointer); }
                }
                if (!finished && Time.realtimeSinceStartup - sent > 25)
                {
                    SteamNetConnectionInfo_t info; SteamNetConnectionRealTimeStatus_t status;
                    var state = SteamNetworkingMessages.GetSessionConnectionInfo(ref identity, out info, out status);
                    Finish("FAIL Steam loopback timeout: " + state + " reason=" + info.m_eEndReason + " " + info.m_szEndDebug + details);
                }
            }
            catch (Exception error) { Finish("FAIL " + error); }
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
