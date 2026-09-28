// Test-only BepInEx plugin; never included in the shipped radio DLL.
// Exercises native Steam P2P self-loopback with Valheim's real listener active.
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using ValheimModPack.NordicRadio;

namespace ValheimModPack.NordicRadioSmoke
{
    [BepInPlugin("valheimmodpack.nordicradio.socketprobe", "Nordic Radio native socket probe", "1.0.0")]
    [BepInDependency("valheimmodpack.nordicradio", "1.3.1")]
    public sealed class NativeSteamSocketProbe : BaseUnityPlugin
    {
        private static int vanillaIncoming;
        private readonly IntPtr[] messages = new IntPtr[8];
        private ZSteamSocket gameListener;
        private HSteamListenSocket listener = HSteamListenSocket.Invalid;
        private HSteamNetConnection client = HSteamNetConnection.Invalid, server = HSteamNetConnection.Invalid;
        private Callback<SteamNetConnectionStatusChangedCallback_t> changed;
        private Harmony counterPatch;
        private Type guard;
        private bool started, finished, clientConnected, serverConnected, sent;
        private float since, sendTime;
        private int received;
        private string report = "";
        private static string Root { get { return Environment.GetEnvironmentVariable("NORDICRADIO_SOCKET_PROBE_ROOT"); } }
        private void Awake()
        {
            if (String.IsNullOrEmpty(Root)) { enabled = false; return; }
            Utils.SetSaveDataPath(Path.Combine(Root, "Saves"));
            since = Time.realtimeSinceStartup;
        }
        private void Guard(string method, params object[] args)
        {
            MethodInfo target = guard.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (target == null) throw new MissingMethodException("Missing guard " + method);
            target.Invoke(null, args);
        }
        private static void CountVanillaIncoming(HSteamNetConnection __0) { vanillaIncoming++; }
        private void StartProbe()
        {
            var self = SteamUser.GetSteamID();
            if (self.m_SteamID == 0) throw new InvalidOperationException("Steam user not ready");
            // Match real startup: Valheim registers its callback delegate before
            // a ready game peer causes the radio's guard to be installed.
            gameListener = new ZSteamSocket();
            if (!gameListener.StartHost()) throw new InvalidOperationException("Cannot open native Valheim listener on port 0");
            guard = typeof(Plugin).Assembly.GetType("ValheimModPack.NordicRadio.RadioSocketCallbackGuard", true);
            Guard("Install");
            counterPatch = new Harmony("valheimmodpack.nordicradio.native-socket-counter");
            var target = AccessTools.Method(typeof(ZSteamSocket), "OnNewConnection", new[] { typeof(HSteamNetConnection) });
            if (target == null) throw new MissingMethodException("Native ZSteamSocket.OnNewConnection changed");
            counterPatch.Patch(target, prefix: new HarmonyMethod(typeof(NativeSteamSocketProbe), "CountVanillaIncoming"));
            changed = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnChanged);
            var transport = typeof(Plugin).Assembly.GetType("ValheimModPack.NordicRadio.SteamSocketRadioTransport", true);
            int port = (int)transport.GetField("VirtualPort", BindingFlags.Static | BindingFlags.NonPublic).GetRawConstantValue();
            listener = SteamNetworkingSockets.CreateListenSocketP2P(port, 0, null);
            if (listener == HSteamListenSocket.Invalid) throw new InvalidOperationException("Native music listener creation failed");
            Guard("AddListener", listener);
            var identity = new SteamNetworkingIdentity(); identity.SetSteamID64(self.m_SteamID);
            client = SteamNetworkingSockets.ConnectP2P(ref identity, port, 0, null);
            if (client == HSteamNetConnection.Invalid) throw new InvalidOperationException("Native music self-loopback connection creation failed");
            Guard("AddConnection", client);
            started = true;
            report += "Native Valheim port 0 and separate radio port " + port + " opened concurrently.\n";
        }
        private void OnChanged(SteamNetConnectionStatusChangedCallback_t data)
        {
            try
            {
                bool ownIncoming = data.m_info.m_hListenSocket == listener && listener != HSteamListenSocket.Invalid;
                if (!ownIncoming && data.m_hConn != client && data.m_hConn != server) return;
                if (ownIncoming && data.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting)
                {
                    if (data.m_info.m_identityRemote.GetSteamID64() != SteamUser.GetSteamID().m_SteamID)
                        throw new InvalidOperationException("Unexpected loopback identity");
                    server = data.m_hConn; Guard("AddConnection", server);
                    if (SteamNetworkingSockets.AcceptConnection(server) != EResult.k_EResultOK)
                        throw new InvalidOperationException("Native accept failed");
                }
                if (data.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected)
                {
                    if (data.m_hConn == client) clientConnected = true;
                    if (data.m_hConn == server) serverConnected = true;
                }
                if (data.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally
                    || data.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer)
                    throw new InvalidOperationException("Native connection ended: " + data.m_info.m_eEndReason + " " + data.m_info.m_szEndDebug);
            }
            catch (Exception error) { Finish("FAIL " + error, 2); }
        }
        private static byte[] Payload(int number)
        {
            byte[] data = new byte[32768];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)((i * 31 + number) & 255);
            return data;
        }
        private void Update()
        {
            if (finished) return;
            try
            {
                if (Time.realtimeSinceStartup - since > 45) { Finish("FAIL native socket probe timed out\n" + report, 2); return; }
                if (!started)
                {
                    if (Time.realtimeSinceStartup - since < 8) return;
                    StartProbe();
                }
                if (vanillaIncoming != 0) throw new InvalidOperationException("Valheim attempted to accept a radio connection");
                if (!clientConnected || !serverConnected) return;
                if (!sent)
                {
                    if (client == server) throw new InvalidOperationException("Loopback must have separate connection endpoints");
                    for (int n = 0; n < 4; n++)
                    {
                        byte[] data = Payload(n); GCHandle pin = GCHandle.Alloc(data, GCHandleType.Pinned);
                        try
                        {
                            long number;
                            if (SteamNetworkingSockets.SendMessageToConnection(server, pin.AddrOfPinnedObject(), (uint)data.Length,
                                Constants.k_nSteamNetworkingSend_ReliableNoNagle, out number) != EResult.k_EResultOK)
                                throw new InvalidOperationException("Native send failed");
                        }
                        finally { pin.Free(); }
                    }
                    sent = true; sendTime = Time.realtimeSinceStartup;
                }
                int count = SteamNetworkingSockets.ReceiveMessagesOnConnection(client, messages, messages.Length);
                for (int i = 0; i < count; i++)
                {
                    IntPtr pointer = messages[i]; messages[i] = IntPtr.Zero;
                    try
                    {
                        var message = SteamNetworkingMessage_t.FromIntPtr(pointer);
                        if (message.m_conn != client || message.m_cbSize != 32768) throw new InvalidOperationException("Native message shape mismatch");
                        var bytes = new byte[message.m_cbSize]; Marshal.Copy(message.m_pData, bytes, 0, bytes.Length);
                        byte[] expected = Payload(received++);
                        for (int j = 0; j < bytes.Length; j++) if (bytes[j] != expected[j]) throw new InvalidOperationException("Native message contents/order mismatch");
                    }
                    finally { SteamNetworkingMessage_t.Release(pointer); }
                }
                if (received != 4) return;
                if (gameListener.GetSendQueueSize() != 0 || vanillaIncoming != 0) throw new InvalidOperationException("Radio modified gameplay socket state");
                Finish("PASS native SteamNetworkingSockets: two loopback endpoints, 128 KiB reliable data verified, "
                    + "Valheim listener active with zero stolen connections and zero gameplay queue bytes.\n"
                    + report + "Native loopback transfer seconds=" + (Time.realtimeSinceStartup - sendTime).ToString("F3")
                    + ". This does not validate two-PC NAT/relay routing.\n", 0);
            }
            catch (Exception error) { Finish("FAIL " + error + "\n" + report, 2); }
        }
        private void Finish(string text, int code)
        {
            if (finished) return;
            finished = true;
            try
            {
                if (changed != null) changed.Dispose();
                if (client != HSteamNetConnection.Invalid) { Guard("RetireConnection", client); SteamNetworkingSockets.CloseConnection(client, 1000, "Probe complete", false); }
                if (server != HSteamNetConnection.Invalid) { Guard("RetireConnection", server); SteamNetworkingSockets.CloseConnection(server, 1000, "Probe complete", false); }
                if (listener != HSteamListenSocket.Invalid) { Guard("RetireListener", listener); SteamNetworkingSockets.CloseListenSocket(listener); }
                if (gameListener != null) gameListener.Dispose();
                if (counterPatch != null) counterPatch.UnpatchSelf();
            }
            catch (Exception error) { text += "FAIL cleanup: " + error.Message + "\n"; code = 2; }
            File.WriteAllText(Path.Combine(Root, "result.txt"), text);
            Logger.LogInfo(text); Application.Quit(code);
        }
    }
}
