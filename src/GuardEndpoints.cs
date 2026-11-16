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
        // "Pause while I think": when idle_pause_s > 0 the game pauses itself after that many seconds without any API request
        // and continues with the next request. A pause set by the player or by the water/power guard is never touched.
        private static int idlePauseSeconds;
        private static long lastRequestTicks = DateTime.UtcNow.Ticks;
        private static bool pausedByIdle;

        public static void Touch(string path)
        {
            if (path == "/ping") return;
            lastRequestTicks = DateTime.UtcNow.Ticks;
            if (!pausedByIdle) return;
            pausedByIdle = false;
            try
            {
                SimulationManager sim = Singleton<SimulationManager>.instance;
                sim.AddAction(delegate () { sim.SimulationPaused = false; });
            }
            catch (Exception) { }
        }

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
            if (!enabled && idlePauseSeconds <= 0) return;
            try
            {
                SimulationManager sim = Singleton<SimulationManager>.instance;
                sim.AddAction(delegate ()
                {
                    try
                    {
                        if (idlePauseSeconds > 0 && !sim.SimulationPaused && !pausedByIdle
                            && (DateTime.UtcNow.Ticks - lastRequestTicks) > idlePauseSeconds * 10000000L)
                        {
                            sim.SimulationPaused = true;
                            pausedByIdle = true;
                            BridgeLog.Info("Idle pause: no API request for " + idlePauseSeconds + " s");
                            return;
                        }
                        if (!enabled) return;
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
            return HttpResponse.Json(200, new JsonWriter().Obj().Prop("enabled", enabled).Prop("idle_pause_s", idlePauseSeconds)
                .Prop("paused_by_idle", pausedByIdle).Prop("last_trigger", lastTrigger).EndObj().ToString());
        }

        // POST /guard {"enabled":true|false, "idle_pause_s":0-600}
        //   enabled: pause when water/sewage/electricity run short; idle_pause_s: pause after that many seconds without a request
        private static HttpResponse Set(HttpRequest req)
        {
            System.Collections.Generic.Dictionary<string, string> body = JsonReader.ParseFlat(req.Body);
            if (body.ContainsKey("enabled")) enabled = JsonReader.GetBool(body, "enabled");
            if (body.ContainsKey("idle_pause_s")) idlePauseSeconds = JsonReader.GetInt(body, "idle_pause_s", 0, 600);
            lastRequestTicks = DateTime.UtcNow.Ticks;
            Stop();
            if (enabled || idlePauseSeconds > 0) timer = new Timer(Tick, null, 3000, 3000);
            lock (lk) { strikes = 0; if (!enabled) lastTrigger = null; }
            return Status(req);
        }
    }
}
