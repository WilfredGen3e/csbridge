using System;
using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;

namespace CSBridge
{
    /// <summary>Phase 5: districts (painted areas with their own policies) and city/district policies.</summary>
    public static class DistrictEndpoints
    {
        public static void Register(HttpServer server)
        {
            server.Get("/districts", Districts);
            server.Post("/district/create", Create);
            server.Post("/district/paint", Paint);
            server.Post("/district/delete", Delete);
            server.Get("/policies", Policies);
            server.Post("/policy", SetPolicy);
        }

        private static void CheckDistrictId(int id, bool allowZero)
        {
            if (id < (allowZero ? 0 : 1) || id > 127) throw new ApiException(400, "district must be " + (allowZero ? 0 : 1) + "-127");
        }

        private static bool Exists(DistrictManager dm, int id)
        {
            return id == 0 || (dm.m_districts.m_buffer[id].m_flags & District.Flags.Created) != District.Flags.None;
        }

        // GET /districts -> all districts with name, population and policy count (district 0 = the whole city)
        private static HttpResponse Districts(HttpRequest req)
        {
            string json = GameThread.Run<string>(delegate ()
            {
                DistrictManager dm = Singleton<DistrictManager>.instance;
                int[] cells = new int[128];
                DistrictManager.Cell[] grid = dm.m_districtGrid;
                for (int c = 0; c < grid.Length; c++) cells[grid[c].m_district1]++;
                JsonWriter w = new JsonWriter().Obj().ArrIn("districts");
                for (int i = 0; i < 128; i++)
                {
                    if (!Exists(dm, i)) continue;
                    District d = dm.m_districts.m_buffer[i];
                    w.Obj().Prop("id", i).Prop("name", i == 0 ? "City" : dm.GetDistrictName(i))
                        .Prop("population", (long)d.m_populationData.m_finalCount)
                        .Prop("area_cells", i == 0 ? -1 : cells[i])
                        .Prop("happiness", d.m_finalHappiness).Prop("crime_rate", d.m_finalCrimeRate)
                        .EndObj();
                }
                return w.EndArr().EndObj().ToString();
            }, 5000);
            return HttpResponse.Json(200, json);
        }

        private static void PaintCircleOrLine(byte id, bool erase, float x1, float z1, float x2, float z2, float radius)
        {
            Vector3 a = new Vector3(x1, 0f, z1), b = new Vector3(x2, 0f, z2);
            DistrictTool.ApplyBrush(DistrictTool.Layer.Districts, erase ? (byte)0 : id, radius, a, b);
        }

        // POST /district/create {"name":"Old Town","x":100,"z":50,"radius":120}   (x2,z2 optional: paints a line-shaped area)
        private static HttpResponse Create(HttpRequest req)
        {
            Dictionary<string, string> body = JsonReader.ParseFlat(req.Body);
            string name = body.ContainsKey("name") ? body["name"] : null;
            float x = BuildEndpoints.GetFloat(body, "x"), z = BuildEndpoints.GetFloat(body, "z");
            float x2 = BuildEndpoints.GetFloat(body, "x2", x), z2 = BuildEndpoints.GetFloat(body, "z2", z);
            float radius = BuildEndpoints.GetFloat(body, "radius", 100f);
            BuildEndpoints.CheckBounds(x, z); BuildEndpoints.CheckBounds(x2, z2);
            if (radius < 10f || radius > 1000f) throw new ApiException(400, "radius must be 10-1000");

            string json = GameThread.Run<string>(delegate ()
            {
                DistrictManager dm = Singleton<DistrictManager>.instance;
                byte id;
                if (!dm.CreateDistrict(out id)) throw new ApiException(409, "Cannot create more districts (limit 127)");
                PaintCircleOrLine(id, false, x, z, x2, z2, radius);
                if (!string.IsNullOrEmpty(name))
                {
                    IEnumerator<bool> e = dm.SetDistrictName(id, name);
                    while (e.MoveNext()) { }
                }
                return new JsonWriter().Obj().Prop("ok", true).Prop("district", id).Prop("name", dm.GetDistrictName(id)).EndObj().ToString();
            }, 10000);
            return HttpResponse.Json(200, json);
        }

        // POST /district/paint {"district":3,"x":..,"z":..,"radius":80,"x2":..,"z2":..,"erase":false}
        // Adds the area to a district (or removes it from districts with erase=true). Cells are 19.2 m.
        private static HttpResponse Paint(HttpRequest req)
        {
            Dictionary<string, string> body = JsonReader.ParseFlat(req.Body);
            bool erase = body.ContainsKey("erase") && JsonReader.GetBool(body, "erase");
            int id = erase && !body.ContainsKey("district") ? 0 : JsonReader.GetInt(body, "district", 1, 127);
            float x = BuildEndpoints.GetFloat(body, "x"), z = BuildEndpoints.GetFloat(body, "z");
            float x2 = BuildEndpoints.GetFloat(body, "x2", x), z2 = BuildEndpoints.GetFloat(body, "z2", z);
            float radius = BuildEndpoints.GetFloat(body, "radius", 100f);
            BuildEndpoints.CheckBounds(x, z); BuildEndpoints.CheckBounds(x2, z2);
            if (radius < 10f || radius > 1000f) throw new ApiException(400, "radius must be 10-1000");
            string json = GameThread.Run<string>(delegate ()
            {
                DistrictManager dm = Singleton<DistrictManager>.instance;
                if (id != 0 && !Exists(dm, id)) throw new ApiException(404, "No district " + id + " (see GET /districts)");
                PaintCircleOrLine((byte)id, erase, x, z, x2, z2, radius);
                return new JsonWriter().Obj().Prop("ok", true).Prop("district", id).Prop("erased", erase).EndObj().ToString();
            }, 10000);
            return HttpResponse.Json(200, json);
        }

        // POST /district/delete {"district":3}
        private static HttpResponse Delete(HttpRequest req)
        {
            int id = JsonReader.GetInt(JsonReader.ParseFlat(req.Body), "district", 1, 127);
            string json = GameThread.Run<string>(delegate ()
            {
                DistrictManager dm = Singleton<DistrictManager>.instance;
                if (!Exists(dm, id)) throw new ApiException(404, "No district " + id);
                string name = dm.GetDistrictName(id);
                dm.ReleaseDistrict((byte)id);
                return new JsonWriter().Obj().Prop("ok", true).Prop("district", id).Prop("name", name).EndObj().ToString();
            }, 5000);
            return HttpResponse.Json(200, json);
        }

        private static bool IsSet(DistrictManager dm, DistrictPolicies.Policies p, int district)
        {
            return district > 0 ? dm.IsDistrictPolicySet(p, (byte)district) : dm.IsCityPolicySet(p);
        }

        private static DistrictPolicies.Policies FindPolicy(string name)
        {
            foreach (DistrictPolicies.Policies p in Enum.GetValues(typeof(DistrictPolicies.Policies)))
                if (p != DistrictPolicies.Policies.None && string.Equals(p.ToString(), name, StringComparison.OrdinalIgnoreCase)) return p;
            throw new ApiException(404, "Unknown policy '" + name + "' (see GET /policies)");
        }

        // GET /policies?district=0&type=Services&unlocked=1   (district 0 = city-wide; type: Services, Taxation, CityPlanning, Special, Specialization, Event, Park)
        private static HttpResponse Policies(HttpRequest req)
        {
            Dictionary<string, string> q = BuildEndpoints.ParseQuery(req.Query);
            int district = BuildEndpoints.GetIntQ(q, "district", 0, 0, 127);
            string type = q.ContainsKey("type") ? q["type"] : null;
            bool onlyUnlocked = q.ContainsKey("unlocked") && q["unlocked"] != "0" && q["unlocked"] != "false";
            string json = GameThread.Run<string>(delegate ()
            {
                DistrictManager dm = Singleton<DistrictManager>.instance;
                if (!Exists(dm, district)) throw new ApiException(404, "No district " + district);
                UnlockManager um = Singleton<UnlockManager>.instance;
                JsonWriter w = new JsonWriter().Obj().Prop("district", district).ArrIn("policies");
                foreach (DistrictPolicies.Policies p in Enum.GetValues(typeof(DistrictPolicies.Policies)))
                {
                    if (p == DistrictPolicies.Policies.None) continue;
                    DistrictPolicies.Types t = (DistrictPolicies.Types)((int)p >> 5);
                    if (type != null && !string.Equals(t.ToString(), type, StringComparison.OrdinalIgnoreCase)) continue;
                    bool unlocked = um.Unlocked(p);
                    if (onlyUnlocked && !unlocked) continue;
                    w.Obj().Prop("name", p.ToString()).Prop("type", t.ToString())
                        .Prop("enabled", IsSet(dm, p, district)).Prop("unlocked", unlocked).EndObj();
                }
                return w.EndArr().EndObj().ToString();
            }, 5000);
            return HttpResponse.Json(200, json);
        }

        // POST /policy {"policy":"HeavyTrafficBan","enabled":true,"district":0}   (district 0 or omitted = whole city)
        private static HttpResponse SetPolicy(HttpRequest req)
        {
            Dictionary<string, string> body = JsonReader.ParseFlat(req.Body);
            string name;
            if (!body.TryGetValue("policy", out name)) throw new ApiException(400, "Missing field: policy (see GET /policies)");
            if (!body.ContainsKey("enabled")) throw new ApiException(400, "Missing field: enabled (true/false)");
            bool enabled = JsonReader.GetBool(body, "enabled");
            int district = body.ContainsKey("district") ? JsonReader.GetInt(body, "district", 0, 127) : 0;
            string json = GameThread.Run<string>(delegate ()
            {
                DistrictManager dm = Singleton<DistrictManager>.instance;
                if (!Exists(dm, district)) throw new ApiException(404, "No district " + district);
                DistrictPolicies.Policies p = FindPolicy(name);
                DistrictPolicies.Types t = (DistrictPolicies.Types)((int)p >> 5);
                if (enabled && !Singleton<UnlockManager>.instance.Unlocked(p))
                    throw new ApiException(409, "Policy " + p + " is not unlocked yet");
                if (t == DistrictPolicies.Types.Park) throw new ApiException(400, "Park policies need a park area and are not supported yet");
                if (enabled) { if (district > 0) dm.SetDistrictPolicy(p, (byte)district); else dm.SetCityPolicy(p); }
                else { if (district > 0) dm.UnsetDistrictPolicy(p, (byte)district); else dm.UnsetCityPolicy(p); }
                return new JsonWriter().Obj().Prop("ok", true).Prop("policy", p.ToString()).Prop("district", district)
                    .Prop("enabled", IsSet(dm, p, district)).EndObj().ToString();
            }, 5000);
            return HttpResponse.Json(200, json);
        }
    }
}
