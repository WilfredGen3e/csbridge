using System;
using System.Threading;
using ColossalFramework;

namespace CSBridge
{
    /// <summary>
    /// Safety net while the game runs unattended: when water, sewage or electricity capacity drops below consumption
    /// on two checks in a row, the guard pauses the game and remembers why (GET /guard). The game cannot burn months
    /// of game time without water while the AI is busy. Off by default; POST /guard {"enabled":true}.
    /// </summary>
    public static class GuardEndpoints
    {
        private static Timer timer;
        private static bool enabled;
        private static int strikes;
        private static string lastTrigger = null;
        private static readonly object lk = new object();

        public static void Register(HttpServer server)
        {
            server.Get("/guard", Status);
            server.Post("/guard", Set);
        }

        public static void Stop()
        {
            if (timer != null) { timer.Dispose(); timer = null; }
        }

        private static string Check()
        {
            District d = Singleton<DistrictManager>.instance.m_districts.m_buffer[0];
            if (d.GetWaterConsumption() > 0 && d.GetWaterCapacity() < d.GetWaterConsumption()) return "water capacity " + d.GetWaterCapacity() + " < consumption " + d.GetWaterConsumption();
            if (d.GetSewageAccumulation() > 0 && d.GetSewageCapacity() < d.GetSewageAccumulation()) return "sewage capacity " + d.GetSewageCapacity() + " < " + d.GetSewageAccumulation();
            if (d.GetElectricityConsumption() > 0 && d.GetElectricityCapacity() < d.GetElectricityConsumption()) return "electricity capacity " + (d.GetElectricityCapacity() / 1000) + " MW < consumption " + (d.GetElectricityConsumption() / 1000) + " MW";
            return null;
        }

        private static void Tick(object o)
        {
            if (!enabled) return;
            try
            {
                SimulationManager sim = Singleton<SimulationManager>.instance;
                sim.AddAction(delegate ()
                {
                    try
                    {
                        if (sim.SimulationPaused) return;
                        string problem = Check();
                        lock (lk)
                        {
                            if (problem == null) { strikes = 0; return; }
                            if (++strikes < 2) return;
                            strikes = 0;
                            lastTrigger = problem + " at " + sim.m_currentGameTime.ToString("yyyy-MM-dd HH:mm");
                        }
                        sim.SimulationPaused = true;
                        BridgeLog.Info("Guard paused the game: " + lastTrigger);
                    }
                    catch (Exception ex) { BridgeLog.Error("guard", ex); }
                });
            }
            catch (Exception) { }
        }

        // GET /guard -> {"enabled":..,"last_trigger":..}
        private static HttpResponse Status(HttpRequest req)
        {
            return HttpResponse.Json(200, new JsonWriter().Obj().Prop("enabled", enabled).Prop("last_trigger", lastTrigger).EndObj().ToString());
        }

        // POST /guard {"enabled":true|false}
        private static HttpResponse Set(HttpRequest req)
        {
            enabled = JsonReader.GetBool(JsonReader.ParseFlat(req.Body), "enabled");
            Stop();
            if (enabled) timer = new Timer(Tick, null, 3000, 3000);
            lock (lk) { strikes = 0; if (!enabled) lastTrigger = null; }
            return Status(req);
        }
    }
}
