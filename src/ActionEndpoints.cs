using System.Collections.Generic;
using ColossalFramework;

namespace CSBridge
{
    /// <summary>Phase 3: actions. All run on the simulation thread via GameThread.Run.</summary>
    public static class ActionEndpoints
    {
        public static void Register(HttpServer server)
        {
            server.Post("/pause", Pause);
            server.Post("/speed", Speed);
        }

        private static string SimState()
        {
            SimulationManager sim = Singleton<SimulationManager>.instance;
            return new JsonWriter().Obj()
                .Prop("ok", true)
                .Prop("paused", sim.SimulationPaused)
                .Prop("speed", sim.SelectedSimulationSpeed)
                .EndObj().ToString();
        }

        // POST /pause  {"paused": true|false}
        private static HttpResponse Pause(HttpRequest req)
        {
            bool paused = JsonReader.GetBool(JsonReader.ParseFlat(req.Body), "paused");
            string json = GameThread.Run<string>(delegate ()
            {
                Singleton<SimulationManager>.instance.SimulationPaused = paused;
                return SimState();
            }, 3000);
            return HttpResponse.Json(200, json);
        }

        // POST /speed  {"speed": 1|2|3}   NOTE: the game unpauses when the speed is set.
        private static HttpResponse Speed(HttpRequest req)
        {
            int speed = JsonReader.GetInt(JsonReader.ParseFlat(req.Body), "speed", 1, 3);
            string json = GameThread.Run<string>(delegate ()
            {
                Singleton<SimulationManager>.instance.SelectedSimulationSpeed = speed;
                return SimState();
            }, 3000);
            return HttpResponse.Json(200, json);
        }
    }
}
