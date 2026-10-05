// AnyPortal+ fork changes, 2026-10-06. Original XPortal by SpikeHimself; GPL-3.0.
namespace XPortal.RPC.Client
{
    internal static class ClientEvents
    {
        private const string DBG_ISSERVER = "because I am the server";

        /// <summary>
        /// The server sent us all of the portals it knows
        /// </summary>
        /// <param name="sender">The server</param>
        /// <param name="pkg">A ZPackage containing a number followed by a list of packaged portals</param>
        /// <param name="reason">The reason that was given for the Resync Request</param>
        internal static void RPC_Resync(long sender, ZPackage pkg, string reason)
        {
            if (!Plus.PlusRpcAuthority.IsServerSender(sender)) return;
            if (Environment.IsServer)
            {
                Log.Debug($"Ignoring resync package {DBG_ISSERVER}");
                return;
            }

            Log.Info($"Received resync package from server, because: {reason}");
            try { KnownPortalsManager.Instance.UpdateFromResyncPackage(pkg); }
            catch (System.Exception error) { Log.Warning("AnyPortal+ ignored an invalid portal snapshot: " + error.Message); }
        }

        /// <summary>
        /// The server sent us a KnownPortal that was added or updated
        /// </summary>
        /// <param name="sender">The server</param>
        /// <param name="pkg">A ZPackage containing the KnownPortal that was added or updated</param>
        internal static void RPC_SyncPortal(long sender, ZPackage pkg)
        {
            if (!Plus.PlusRpcAuthority.IsServerSender(sender)) return;
            if (Environment.IsServer)
            {
                Log.Debug($"Ignoring portal update {DBG_ISSERVER}");
                return;
            }

            try
            {
                var incomingPortal = new KnownPortal(pkg);
                Log.Debug($"Received update to portal `{incomingPortal.GetFriendlyName()}` from server");
                KnownPortalsManager.Instance.AddOrUpdate(incomingPortal);
            }
            catch (System.Exception error) { Log.Warning("AnyPortal+ ignored an invalid portal update: " + error.Message); }
        }

        /// <summary>
        /// The server sent us a package containing all config settings
        /// </summary>
        /// <param name="sender">The server</param>
        /// <param name="pkg">A ZPackage containing all config settings</param>
        internal static void RPC_Config(long sender, ZPackage pkg)
        {
            if (!Plus.PlusRpcAuthority.IsServerSender(sender) || pkg == null || pkg.Size() > 4096) return;
            Log.Info("Received XPortal Config from server");
            try { XPortalConfig.Instance.ReceiveServerConfig(pkg); }
            catch (System.Exception error) { Log.Warning("AnyPortal+ ignored an invalid config: " + error.Message); }
        }
    }
}
