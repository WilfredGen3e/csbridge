using ICities;

namespace CSBridge
{
    public class BridgeMod : IUserMod
    {
        public string Name { get { return "CSBridge"; } }
        public string Description { get { return "Local HTTP API for AI agents (127.0.0.1:8080)"; } }

        public void OnEnabled()
        {
            BridgeLog.Init(FindModFolder());
            // Keep simulating when the game window is not the active app (e.g. on another desktop/Space).
            UnityEngine.Application.runInBackground = true;
            BridgeLog.Info("CSBridge " + Endpoints.Version + " enabled");
        }

        /// <summary>The folder of this mod, as the game's plugin manager knows it (null if not found).</summary>
        private static string FindModFolder()
        {
            try
            {
                foreach (ColossalFramework.Plugins.PluginManager.PluginInfo pi in ColossalFramework.Plugins.PluginManager.instance.GetPluginsInfo())
                {
                    foreach (IUserMod mod in pi.GetInstances<IUserMod>())
                        if (mod is BridgeMod) return pi.modPath;
                }
            }
            catch (System.Exception) { }
            return null;
        }

        public void OnDisabled()
        {
            BridgeLog.Info("CSBridge disabled");
        }
    }

    /// <summary>Starts the server when a city is loaded and stops it when it unloads.</summary>
    public class BridgeLoading : LoadingExtensionBase
    {
        private static HttpServer server;

        public override void OnLevelLoaded(LoadMode mode)
        {
            try
            {
                Stop();
                server = new HttpServer();
                Endpoints.Register(server);
                server.Start();
                UiEndpoints.StartWatcher();
            }
            catch (System.Exception ex)
            {
                BridgeLog.Error("Failed to start server", ex);
            }
        }

        public override void OnLevelUnloading() { Stop(); }
        public override void OnReleased() { Stop(); }

        private static void Stop()
        {
            UiEndpoints.StopWatcher();
            GuardEndpoints.Stop();
            if (server != null)
            {
                try { server.Stop(); } catch (System.Exception ex) { BridgeLog.Error("Stop", ex); }
                server = null;
            }
        }
    }
}
