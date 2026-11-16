using System;
using System.Collections.Generic;
using ColossalFramework;

namespace CSBridge
{
    /// <summary>Phase 4: progression. Uses the same UnlockManager calls as the game's developer UI.</summary>
    public static class UnlockEndpoints
    {
        public static void Register(HttpServer server)
        {
            server.Get("/progress", Progress);
            server.Post("/unlock", Unlock);
        }

        private static string State()
        {
            UnlockManager um = Singleton<UnlockManager>.instance;
            GameAreaManager am = Singleton<GameAreaManager>.instance;
            MilestoneInfo current = um.GetCurrentMilestone();
            JsonWriter w = new JsonWriter().Obj()
                .Prop("current_milestone", current != null ? BuildEndpoints.MilestoneName(current) : null)
                .Prop("unlocked_areas", am.m_areaCount);
            // The progression milestones (population steps) with their requirement, so you know what unlocks when.
            w.ArrIn("milestones");
            try
            {
                MilestoneInfo[] all = um.m_properties.m_progressionMilestones;
                if (all != null)
                    foreach (MilestoneInfo m in all)
                        if (m != null) w.Obj().Prop("milestone", BuildEndpoints.MilestoneName(m)).Prop("unlocked", BuildEndpoints.IsUnlocked(m)).EndObj();
            }
            catch (Exception) { }
            w.EndArr();
            return w.EndObj().ToString();
        }

        // GET /progress -> current milestone and number of unlocked map tiles
        private static HttpResponse Progress(HttpRequest req)
        {
            return HttpResponse.Json(200, GameThread.Run<string>(delegate () { return State(); }, 3000));
        }

        // POST /unlock {"what":"progression|monuments|wonders|areas|all"}
        //   progression: all milestones (services, zones, policies, buildings, map tiles allowed)
        //   monuments / wonders: unique buildings
        //   areas: buy every map tile that can be unlocked (repeats while new tiles become available)
        private static HttpResponse Unlock(HttpRequest req)
        {
            Dictionary<string, string> body = JsonReader.ParseFlat(req.Body);
            string what;
            if (!body.TryGetValue("what", out what)) throw new ApiException(400, "Missing field: what (progression, monuments, wonders, areas, all)");
            what = what.ToLowerInvariant();
            if (what != "progression" && what != "monuments" && what != "wonders" && what != "areas" && what != "all")
                throw new ApiException(400, "what must be progression, monuments, wonders, areas or all");

            string json = GameThread.Run<string>(delegate ()
            {
                UnlockManager um = Singleton<UnlockManager>.instance;
                SimulationManager sim = Singleton<SimulationManager>.instance;
                bool wasForced = sim.ForcedSimulationPaused;
                bool all = what == "all";
                if (all || what == "progression") um.UnlockAllProgressionMilestones();
                if (all || what == "monuments") um.UnlockAllMonumentMilestones();
                if (all || what == "wonders") um.UnlockAllWonderMilestones();
                int tilesBought = 0;
                if (all || what == "areas")
                {
                    GameAreaManager am = Singleton<GameAreaManager>.instance;
                    EconomyManager eco = Singleton<EconomyManager>.instance;
                    bool progress = true;
                    while (progress)
                    {
                        progress = false;
                        for (int idx = 0; idx < 25; idx++)
                        {
                            int tx = idx % 5, tz = idx / 5;
                            if (am.IsUnlocked(tx, tz) || !am.CanUnlock(tx, tz)) continue;
                            int price = am.CalculateTilePrice(idx);
                            if (price != 0 && eco.FetchResource(EconomyManager.Resource.LandPrice, price, ItemClass.Service.None, ItemClass.SubService.None, ItemClass.Level.Level1) != price) continue;
                            if (am.UnlockArea(idx)) { tilesBought++; progress = true; }
                        }
                    }
                }
                // Unlocking pops up panels in the game UI that freeze the simulation; undo that freeze.
                if (!wasForced && sim.ForcedSimulationPaused) sim.ForcedSimulationPaused = false;
                JsonWriter w = new JsonWriter().Obj().Prop("ok", true).Prop("what", what).Prop("tiles_bought", tilesBought);
                MilestoneInfo current = um.GetCurrentMilestone();
                w.Prop("current_milestone", current != null ? current.name : null);
                w.Prop("unlocked_areas", Singleton<GameAreaManager>.instance.m_areaCount);
                return w.EndObj().ToString();
            }, 30000);
            // The popups appear a moment later on the UI thread: check again and release the freeze they cause.
            bool wasForcedBefore = false;
            System.Threading.Thread.Sleep(1500);
            try { UiEndpoints.CloseByName("UnlockingPanel"); } catch (Exception) { /* panel not shown: fine */ }
            GameThread.Run<bool>(delegate ()
            {
                SimulationManager sim = Singleton<SimulationManager>.instance;
                if (sim.ForcedSimulationPaused) sim.ForcedSimulationPaused = false;
                return wasForcedBefore;
            }, 3000);
            return HttpResponse.Json(200, json);
        }
    }
}
