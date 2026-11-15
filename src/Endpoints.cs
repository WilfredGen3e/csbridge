using System;
using ColossalFramework;

namespace CSBridge
{
    public static class Endpoints
    {
        public const string Version = "0.1.0";

        public static void Register(HttpServer server)
        {
            server.Get("/ping", Ping);
            server.Get("/stats", Stats);
            EconomyEndpoints.Register(server);
            CityEndpoints.Register(server);
            ActionEndpoints.Register(server);
            BuildEndpoints.Register(server);
            MapEndpoints.Register(server);
            RoadEndpoints.Register(server);
            ZoneEndpoints.Register(server);
            UnlockEndpoints.Register(server);
            UiEndpoints.Register(server);
            DistrictEndpoints.Register(server);
            CameraEndpoints.Register(server);
            TransportEndpoints.Register(server);
            SaveEndpoints.Register(server);
            DiagnosticsEndpoints.Register(server);
        }

        private static HttpResponse Ping(HttpRequest req)
        {
            // Deliberately no game access: proves the server itself is alive.
            return HttpResponse.Json(200, new JsonWriter()
                .Obj().Prop("ok", true).Prop("mod", "CSBridge").Prop("version", Version).EndObj().ToString());
        }

        private static HttpResponse Stats(HttpRequest req)
        {
            string json = GameThread.Run<string>(delegate ()
            {
                SimulationManager sim = Singleton<SimulationManager>.instance;
                EconomyManager eco = Singleton<EconomyManager>.instance;
                DistrictManager dm = Singleton<DistrictManager>.instance;

                long population = dm.m_districts.m_buffer[0].m_populationData.m_finalCount;
                long money = eco.LastCashAmount / 100;

                return new JsonWriter().Obj()
                    .Prop("population", population)
                    .Prop("money", money)
                    .Prop("game_time", sim.m_currentGameTime.ToString("yyyy-MM-dd HH:mm"))
                    .Prop("paused", sim.SimulationPaused)
                    .Prop("forced_paused", sim.ForcedSimulationPaused)
                    .Prop("frame", (long)sim.m_currentFrameIndex)
                    .Prop("speed", sim.SelectedSimulationSpeed)
                    .EndObj().ToString();
            }, 3000);
            return HttpResponse.Json(200, json);
        }
    }
}
