using System;
using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;

namespace CSBridge
{
    /// <summary>Phase 2: /services, /traffic, /problems. Same getters the game's own info panels use.</summary>
    public static class CityEndpoints
    {
        public static void Register(HttpServer server)
        {
            server.Get("/services", Services);
            server.Get("/traffic", Traffic);
            server.Get("/problems", Problems);
        }

        private static HttpResponse Services(HttpRequest req)
        {
            string json = GameThread.Run<string>(delegate ()
            {
                // District 0 is the whole city.
                District d = Singleton<DistrictManager>.instance.m_districts.m_buffer[0];
                JsonWriter w = new JsonWriter().Obj();

                // Electricity: raw units / 1000 = MW (as shown in the game's info panel).
                w.ObjIn("electricity")
                    .Prop("capacity_mw", d.GetElectricityCapacity() / 1000.0)
                    .Prop("consumption_mw", d.GetElectricityConsumption() / 1000.0)
                    .Prop("heating_capacity", d.GetHeatingCapacity())
                    .Prop("heating_consumption", d.GetHeatingConsumption())
                    .EndObj();

                w.ObjIn("water")
                    .Prop("capacity", d.GetWaterCapacity())
                    .Prop("consumption", d.GetWaterConsumption())
                    .Prop("storage_amount", d.GetWaterStorageAmount())
                    .Prop("storage_capacity", d.GetWaterStorageCapacity())
                    .Prop("pollution", d.GetWaterPollution())
                    .EndObj();

                w.ObjIn("sewage")
                    .Prop("capacity", d.GetSewageCapacity())
                    .Prop("accumulation", d.GetSewageAccumulation())
                    .EndObj();

                w.ObjIn("garbage")
                    .Prop("capacity", d.GetGarbageCapacity())
                    .Prop("accumulation", d.GetGarbageAccumulation())
                    .Prop("amount_stored", d.GetGarbageAmount())
                    .Prop("incineration_capacity", d.GetIncinerationCapacity())
                    .Prop("piles", d.GetGarbagePiles())
                    .EndObj();

                w.ObjIn("health")
                    .Prop("heal_capacity", d.GetHealCapacity())
                    .Prop("sick", d.GetSickCount())
                    .Prop("dead_waiting", d.GetDeadAmount())
                    .Prop("deathcare_capacity", d.GetDeadCapacity())
                    .Prop("cremate_capacity", d.GetCremateCapacity())
                    .EndObj();

                w.ObjIn("education")
                    .Prop("elementary_capacity", d.GetEducation1Capacity()).Prop("elementary_need", d.GetEducation1Need())
                    .Prop("highschool_capacity", d.GetEducation2Capacity()).Prop("highschool_need", d.GetEducation2Need())
                    .Prop("university_capacity", d.GetEducation3Capacity()).Prop("university_need", d.GetEducation3Need())
                    .EndObj();

                w.ObjIn("police")
                    .Prop("criminals", d.GetCriminalAmount())
                    .Prop("capacity", d.GetCriminalCapacity())
                    .EndObj();

                w.ObjIn("employment")
                    .Prop("workers", d.GetWorkerCount())
                    .Prop("workplaces", d.GetWorkplaceCount())
                    .Prop("unemployment_percent", d.GetUnemployment())
                    .EndObj();

                return w.EndObj().ToString();
            }, 3000);
            return HttpResponse.Json(200, json);
        }

        private static HttpResponse Traffic(HttpRequest req)
        {
            string json = GameThread.Run<string>(delegate ()
            {
                // 0..100, the "average traffic flow" meter in the game's traffic info view.
                return new JsonWriter().Obj()
                    .Prop("flow_percent", (long)Singleton<VehicleManager>.instance.m_lastTrafficFlow)
                    .EndObj().ToString();
            }, 3000);
            return HttpResponse.Json(200, json);
        }

        private class Group
        {
            public int Count;
            public List<string> Samples = new List<string>();
        }

        private static int QueryInt(string query, string key, int def, int max)
        {
            foreach (string part in query.Split('&'))
            {
                string[] kv = part.Split('=');
                int v;
                if (kv.Length == 2 && kv[0] == key && int.TryParse(kv[1], out v)) return Math.Max(0, Math.Min(max, v));
            }
            return def;
        }

        private static void AddProblemBits(ulong bits, Type enumType, Building b, ushort id, int sampleLimit,
            Dictionary<string, Group> groups, ref bool major, ref bool fatal)
        {
            foreach (object o in Enum.GetValues(enumType))
            {
                ulong v = Convert.ToUInt64(o);
                if (v == 0 || (v & (v - 1)) != 0 || (bits & v) == 0) continue; // single-bit flags present only
                string name = o.ToString();
                if (name == "MajorProblem") { major = true; continue; }
                if (name == "FatalProblem") { fatal = true; continue; }
                Group g;
                if (!groups.TryGetValue(name, out g)) { g = new Group(); groups[name] = g; }
                g.Count++;
                if (g.Samples.Count < sampleLimit)
                {
                    BuildingInfo info = b.Info;
                    g.Samples.Add(new JsonWriter().Obj()
                        .Prop("id", id)
                        .Prop("prefab", info != null ? info.name : null)
                        .Prop("x", Math.Round(b.m_position.x, 1))
                        .Prop("z", Math.Round(b.m_position.z, 1))
                        .EndObj().ToString());
                }
            }
        }

        private static HttpResponse Problems(HttpRequest req)
        {
            int sampleLimit = QueryInt(req.Query, "samples", 3, 20);
            string json = GameThread.Run<string>(delegate ()
            {
                Building[] buf = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
                Dictionary<string, Group> groups = new Dictionary<string, Group>();
                int withProblems = 0, major = 0, fatal = 0;

                for (int i = 1; i < buf.Length; i++)
                {
                    Building b = buf[i];
                    if ((b.m_flags & Building.Flags.Created) == 0 || !b.m_problems.IsNotNone) continue;
                    withProblems++;
                    bool isMajor = false, isFatal = false;
                    AddProblemBits(Convert.ToUInt64((Notification.Problem1)b.m_problems), typeof(Notification.Problem1), b, (ushort)i, sampleLimit, groups, ref isMajor, ref isFatal);
                    AddProblemBits(Convert.ToUInt64((Notification.Problem2)b.m_problems), typeof(Notification.Problem2), b, (ushort)i, sampleLimit, groups, ref isMajor, ref isFatal);
                    if (isMajor) major++;
                    if (isFatal) fatal++;
                }

                JsonWriter w = new JsonWriter().Obj()
                    .Prop("buildings_with_problems", withProblems)
                    .Prop("major", major).Prop("fatal", fatal)
                    .ArrIn("problems");
                foreach (KeyValuePair<string, Group> kv in groups)
                {
                    w.Obj().Prop("problem", kv.Key).Prop("buildings", kv.Value.Count);
                    w.ArrIn("samples");
                    foreach (string s in kv.Value.Samples) w.Raw(s);
                    w.EndArr().EndObj();
                }
                return w.EndArr().EndObj().ToString();
            }, 3000);
            return HttpResponse.Json(200, json);
        }
    }
}
