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
            BridgeLog.Info("CSBridge enabled");
        }

        public void OnDisabled()
        {
            BridgeLog.Info("CSBridge disabled");
        }
    }
}
