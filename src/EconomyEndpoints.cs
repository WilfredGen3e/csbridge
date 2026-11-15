using ColossalFramework;

namespace CSBridge
{
    /// <summary>Phase 2: /demand and /budget.</summary>
    public static class EconomyEndpoints
    {
        private static readonly ItemClass.Service[] PrivateServices = new ItemClass.Service[] {
            ItemClass.Service.Residential, ItemClass.Service.Commercial,
            ItemClass.Service.Industrial, ItemClass.Service.Office };

        private static readonly ItemClass.Service[] PublicServices = new ItemClass.Service[] {
            ItemClass.Service.Road, ItemClass.Service.Electricity, ItemClass.Service.Water,
            ItemClass.Service.Garbage, ItemClass.Service.HealthCare, ItemClass.Service.FireDepartment,
            ItemClass.Service.PoliceDepartment, ItemClass.Service.Education, ItemClass.Service.PublicTransport,
            ItemClass.Service.Beautification, ItemClass.Service.Monument, ItemClass.Service.Disaster };

        public static void Register(HttpServer server)
        {
            server.Get("/demand", Demand);
            server.Get("/budget", Budget);
        }

        private static HttpResponse Demand(HttpRequest req)
        {
            string json = GameThread.Run<string>(delegate ()
            {
                ZoneManager zm = Singleton<ZoneManager>.instance;
                // 0..100, same scale as the demand bars in the game UI.
                return new JsonWriter().Obj()
                    .Prop("residential", zm.m_residentialDemand)
                    .Prop("commercial", zm.m_commercialDemand)
                    .Prop("industrial_and_office", zm.m_workplaceDemand)
                    .Prop("actual_residential", zm.m_actualResidentialDemand)
                    .Prop("actual_commercial", zm.m_actualCommercialDemand)
                    .Prop("actual_workplace", zm.m_actualWorkplaceDemand)
                    .EndObj().ToString();
            }, 3000);
            return HttpResponse.Json(200, json);
        }

        private static HttpResponse Budget(HttpRequest req)
        {
            string json = GameThread.Run<string>(delegate ()
            {
                EconomyManager eco = Singleton<EconomyManager>.instance;
                JsonWriter w = new JsonWriter().Obj();

                long inc, exp;
                eco.GetIncomeAndExpenses(ItemClass.Service.None, ItemClass.SubService.None, ItemClass.Level.None, out inc, out exp);
                w.ObjIn("total")
                    .Prop("income", inc / 100).Prop("expenses", exp / 100)
                    .Prop("loan_expenses", eco.GetLoanExpenses() / 100)
                    .Prop("policy_expenses", eco.GetPolicyExpenses() / 100)
                    .EndObj();

                w.ObjIn("taxes_percent")
                    .Prop("residential_low", eco.GetTaxRate(ItemClass.Service.Residential, ItemClass.SubService.ResidentialLow, ItemClass.Level.Level1))
                    .Prop("residential_high", eco.GetTaxRate(ItemClass.Service.Residential, ItemClass.SubService.ResidentialHigh, ItemClass.Level.Level1))
                    .Prop("commercial_low", eco.GetTaxRate(ItemClass.Service.Commercial, ItemClass.SubService.CommercialLow, ItemClass.Level.Level1))
                    .Prop("commercial_high", eco.GetTaxRate(ItemClass.Service.Commercial, ItemClass.SubService.CommercialHigh, ItemClass.Level.Level1))
                    .Prop("industrial", eco.GetTaxRate(ItemClass.Service.Industrial, ItemClass.SubService.IndustrialGeneric, ItemClass.Level.Level1))
                    .Prop("office", eco.GetTaxRate(ItemClass.Service.Office, ItemClass.SubService.OfficeGeneric, ItemClass.Level.Level1))
                    .EndObj();

                w.ArrIn("services");
                foreach (ItemClass.Service s in PrivateServices)
                {
                    eco.GetIncomeAndExpenses(s, ItemClass.SubService.None, ItemClass.Level.None, out inc, out exp);
                    w.Obj().Prop("service", s.ToString()).Prop("kind", "private")
                        .Prop("income", inc / 100).Prop("expenses", exp / 100).EndObj();
                }
                foreach (ItemClass.Service s in PublicServices)
                {
                    eco.GetIncomeAndExpenses(s, ItemClass.SubService.None, ItemClass.Level.None, out inc, out exp);
                    w.Obj().Prop("service", s.ToString()).Prop("kind", "public")
                        .Prop("income", inc / 100).Prop("expenses", exp / 100)
                        .Prop("budget_day_percent", eco.GetBudget(s, ItemClass.SubService.None, false))
                        .Prop("budget_night_percent", eco.GetBudget(s, ItemClass.SubService.None, true))
                        .EndObj();
                }
                w.EndArr();
                return w.EndObj().ToString();
            }, 3000);
            return HttpResponse.Json(200, json);
        }
    }
}
