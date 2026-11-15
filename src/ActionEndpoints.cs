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
            server.Post("/taxes", Taxes);
            server.Post("/budget", Budget);
        }

        private static string SimState()
        {
            SimulationManager sim = Singleton<SimulationManager>.instance;
            return new JsonWriter().Obj()
                .Prop("ok", true)
                .Prop("paused", sim.SimulationPaused)
                .Prop("forced_paused", sim.ForcedSimulationPaused)
                .Prop("speed", sim.SelectedSimulationSpeed)
                .EndObj().ToString();
        }

        // POST /pause  {"paused": true|false, "force": true}
        // force=true also clears the "forced" pause that game dialogs/menus set (see forced_paused in /stats).
        private static HttpResponse Pause(HttpRequest req)
        {
            Dictionary<string, string> body = JsonReader.ParseFlat(req.Body);
            bool paused = JsonReader.GetBool(body, "paused");
            bool force = body.ContainsKey("force") && JsonReader.GetBool(body, "force");
            string json = GameThread.Run<string>(delegate ()
            {
                Singleton<SimulationManager>.instance.SimulationPaused = paused;
                if (force && !paused) Singleton<SimulationManager>.instance.ForcedSimulationPaused = false;
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

        // Tax range of the in-game sliders (0-29%); not verifiable from code, so treat as the game's UI limit.
        private const int TaxMin = 0, TaxMax = 29;
        // Budget slider range in the game UI (50-150%).
        private const int BudgetMin = 50, BudgetMax = 150;

        private static readonly string[] TaxKeys = new string[] {
            "residential_low", "residential_high", "commercial_low", "commercial_high", "industrial", "office" };

        // POST /taxes  {"residential_low": 9, "industrial": 12, ...}  (any subset; sets all building levels)
        private static HttpResponse Taxes(HttpRequest req)
        {
            Dictionary<string, string> body = JsonReader.ParseFlat(req.Body);
            if (body.Count == 0) throw new ApiException(400, "Provide at least one of: " + string.Join(", ", TaxKeys));
            Dictionary<string, int> rates = new Dictionary<string, int>();
            foreach (string key in body.Keys)
            {
                if (System.Array.IndexOf(TaxKeys, key) < 0)
                    throw new ApiException(400, "Unknown tax '" + key + "'. Valid: " + string.Join(", ", TaxKeys));
                rates[key] = JsonReader.GetInt(body, key, TaxMin, TaxMax);
            }

            string json = GameThread.Run<string>(delegate ()
            {
                EconomyManager eco = Singleton<EconomyManager>.instance;
                const ItemClass.Level L = ItemClass.Level.None; // None = all levels
                foreach (KeyValuePair<string, int> kv in rates)
                {
                    int r = kv.Value;
                    switch (kv.Key)
                    {
                        case "residential_low":
                            eco.SetTaxRate(ItemClass.Service.Residential, ItemClass.SubService.ResidentialLow, L, r);
                            eco.SetTaxRate(ItemClass.Service.Residential, ItemClass.SubService.ResidentialLowEco, L, r);
                            break;
                        case "residential_high":
                            eco.SetTaxRate(ItemClass.Service.Residential, ItemClass.SubService.ResidentialHigh, L, r);
                            eco.SetTaxRate(ItemClass.Service.Residential, ItemClass.SubService.ResidentialHighEco, L, r);
                            eco.SetTaxRate(ItemClass.Service.Residential, ItemClass.SubService.ResidentialWallToWall, L, r);
                            break;
                        case "commercial_low":
                            eco.SetTaxRate(ItemClass.Service.Commercial, ItemClass.SubService.CommercialLow, L, r);
                            break;
                        case "commercial_high":
                            eco.SetTaxRate(ItemClass.Service.Commercial, ItemClass.SubService.CommercialHigh, L, r);
                            eco.SetTaxRate(ItemClass.Service.Commercial, ItemClass.SubService.CommercialWallToWall, L, r);
                            break;
                        case "industrial":
                            eco.SetTaxRate(ItemClass.Service.Industrial, ItemClass.SubService.IndustrialGeneric, L, r);
                            break;
                        case "office":
                            eco.SetTaxRate(ItemClass.Service.Office, ItemClass.SubService.OfficeGeneric, L, r);
                            eco.SetTaxRate(ItemClass.Service.Office, ItemClass.SubService.OfficeWallToWall, L, r);
                            eco.SetTaxRate(ItemClass.Service.Office, ItemClass.SubService.OfficeFinancial, L, r);
                            break;
                    }
                }
                JsonWriter w = new JsonWriter().Obj().Prop("ok", true);
                EconomyEndpoints.WriteTaxes(w, eco);
                return w.EndObj().ToString();
            }, 3000);
            return HttpResponse.Json(200, json);
        }

        // POST /budget  {"service":"Road","day":100,"night":90}  (day and/or night; 50-150)
        private static HttpResponse Budget(HttpRequest req)
        {
            Dictionary<string, string> body = JsonReader.ParseFlat(req.Body);
            string name;
            if (!body.TryGetValue("service", out name)) throw new ApiException(400, "Missing field: service");

            ItemClass.Service? found = null;
            List<string> valid = new List<string>();
            foreach (ItemClass.Service s in EconomyEndpoints.PublicServices)
            {
                valid.Add(s.ToString());
                if (string.Equals(s.ToString(), name, System.StringComparison.OrdinalIgnoreCase)) found = s;
            }
            if (found == null)
                throw new ApiException(400, "Unknown service '" + name + "'. Valid: " + string.Join(", ", valid.ToArray()));
            if (!body.ContainsKey("day") && !body.ContainsKey("night"))
                throw new ApiException(400, "Provide 'day' and/or 'night' (" + BudgetMin + "-" + BudgetMax + ")");
            int day = body.ContainsKey("day") ? JsonReader.GetInt(body, "day", BudgetMin, BudgetMax) : -1;
            int night = body.ContainsKey("night") ? JsonReader.GetInt(body, "night", BudgetMin, BudgetMax) : -1;
            ItemClass.Service service = found.Value;

            string json = GameThread.Run<string>(delegate ()
            {
                EconomyManager eco = Singleton<EconomyManager>.instance;
                if (day >= 0) SetBudget(eco, service, day, false);
                if (night >= 0) SetBudget(eco, service, night, true);
                return new JsonWriter().Obj().Prop("ok", true).Prop("service", service.ToString())
                    .Prop("budget_day_percent", eco.GetBudget(service, ItemClass.SubService.None, false))
                    .Prop("budget_night_percent", eco.GetBudget(service, ItemClass.SubService.None, true))
                    .EndObj().ToString();
            }, 3000);
            return HttpResponse.Json(200, json);
        }

        private static void SetBudget(EconomyManager eco, ItemClass.Service service, int value, bool night)
        {
            eco.SetBudget(service, ItemClass.SubService.None, value, night);
            // The game's budget panel keeps Disaster in sync with the fire department.
            if (service == ItemClass.Service.FireDepartment)
                eco.SetBudget(ItemClass.Service.Disaster, ItemClass.SubService.None, value, night);
        }
    }
}
