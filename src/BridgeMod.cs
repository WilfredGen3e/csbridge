using ICities;

namespace CSBridge
{
    public class BridgeMod : IUserMod
    {
        public string Name { get { return "CSBridge"; } }
        public string Description { get { return "Local HTTP API for AI agents (127.0.0.1:8080)"; } }

        public void OnEnabled()
        {
            BridgeLog.Init(null);
            // Keep simulating when the game window is not the active app (e.g. on another desktop/Space).
            UnityEngine.Application.runInBackground = true;
            BridgeLog.Info("CSBridge " + Endpoints.Version + " enabled");
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
            if (server != null)
            {
                try { server.Stop(); } catch (System.Exception ex) { BridgeLog.Error("Stop", ex); }
                server = null;
            }
        }
    }
}
