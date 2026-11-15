using System;
using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;

namespace CSBridge
{
    /// <summary>Phase 9: diagnosing a city. /networks (connected parts of roads, pipes, power lines), /building (one building in detail), /weather.</summary>
    public static class DiagnosticsEndpoints
    {
        public static void Register(HttpServer server)
        {
            server.Get("/networks", Networks);
            server.Get("/building", BuildingInfoEndpoint);
            server.Get("/weather", GetWeather);
            server.Post("/weather", SetWeather);
            server.Get("/vehicles", Vehicles);
            server.Post("/camera/follow", Follow);
        }

        // GET /vehicles?x=&z=&radius=&limit=  -> vehicles (id, type, position, speed) near a point, to pick one for /camera/follow
        private static HttpResponse Vehicles(HttpRequest req)
        {
            Dictionary<string, string> q = BuildEndpoints.ParseQuery(req.Query);
            float cx = BuildEndpoints.GetFloat(q, "x"), cz = BuildEndpoints.GetFloat(q, "z");
            float radius = BuildEndpoints.GetFloat(q, "radius", 200f);
            int limit = q.ContainsKey("limit") ? JsonReader.GetInt(q, "limit", 1, 100) : 20;
            string json = GameThread.Run<string>(delegate ()
            {
                Vehicle[] buf = Singleton<VehicleManager>.instance.m_vehicles.m_buffer;
                JsonWriter w = new JsonWriter().Obj().ArrIn("vehicles");
                int n = 0;
                for (int i = 1; i < buf.Length && n < limit; i++)
                {
                    Vehicle v = buf[i];
                    if ((v.m_flags & Vehicle.Flags.Created) == 0 || v.Info == null || v.m_leadingVehicle != 0) continue;
                    Vector3 p = v.GetLastFramePosition();
                    float dx = p.x - cx, dz = p.z - cz;
                    if (dx * dx + dz * dz > radius * radius) continue;
                    w.Obj().Prop("id", i).Prop("type", v.Info.name).Prop("service", v.Info.m_class.m_service.ToString())
                        .Prop("x", R(p.x)).Prop("z", R(p.z)).Prop("speed", R(v.GetLastFrameVelocity().magnitude)).EndObj();
                    n++;
                }
                return w.EndArr().EndObj().ToString();
            }, 5000);
            return HttpResponse.Json(200, json);
        }

        // POST /camera/follow {"vehicle":id} | {"building":id} | {"citizen_instance":id} | {"stop":true}
        // The camera follows that object until you move the camera again or call stop. Ids: /vehicles, /map.
        private static HttpResponse Follow(HttpRequest req)
        {
            Dictionary<string, string> b = JsonReader.ParseFlat(req.Body);
            bool stop = b.ContainsKey("stop") && JsonReader.GetBool(b, "stop");
            InstanceID id = InstanceID.Empty;
            string what = "none";
            if (!stop)
            {
                if (b.ContainsKey("vehicle")) { id.Vehicle = (ushort)JsonReader.GetInt(b, "vehicle", 1, 65535); what = "vehicle"; }
                else if (b.ContainsKey("building")) { id.Building = (ushort)JsonReader.GetInt(b, "building", 1, 49151); what = "building"; }
                else if (b.ContainsKey("citizen_instance")) { id.CitizenInstance = (ushort)JsonReader.GetInt(b, "citizen_instance", 1, 65535); what = "citizen_instance"; }
                else throw new ApiException(400, "Give vehicle, building, citizen_instance or stop:true");
            }
            string json = null;
            Exception error = null;
            System.Threading.ManualResetEvent done = new System.Threading.ManualResetEvent(false);
            ColossalFramework.Threading.ThreadHelper.dispatcher.Dispatch(delegate ()
            {
                try
                {
                    CameraController c = ToolsModifierControl.cameraController;
                    if (c == null) throw new ApiException(503, "Camera is not available (no city loaded?)");
                    if (stop) c.ClearTarget();
                    else c.SetTarget(id, Vector3.zero, true);
                    json = new JsonWriter().Obj().Prop("ok", true).Prop("following", stop ? "none" : what).EndObj().ToString();
                }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            });
            if (!done.WaitOne(5000)) throw new GameThread.TimeoutException("Game did not respond within 5000 ms");
            if (error != null) throw error;
            return HttpResponse.Json(200, json);
        }

        private static double R(float v) { return Math.Round(v, 1); }

        // ---------------------------------------------------------------- /networks
        private class Comp
        {
            public int Segments, Nodes;
            public float MinX = float.MaxValue, MaxX = float.MinValue, MinZ = float.MaxValue, MaxZ = float.MinValue;
            public bool Highway;
            public HashSet<string> Types = new HashSet<string>();
        }

        private static int Find(int[] parent, int a)
        {
            while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; }
            return a;
        }

        private static bool InKind(string kind, NetInfo info)
        {
            if (info == null || info.m_class == null) return false;
            ItemClass.Service s = info.m_class.m_service;
            if (kind == "road") return s == ItemClass.Service.Road && info.m_netAI != null && !(info.m_netAI is TransportLineAI);
            if (kind == "water") return s == ItemClass.Service.Water;
            if (kind == "power") return s == ItemClass.Service.Electricity;
            return false;
        }

        // GET /networks?kind=road|water|power[&x=&z=]
        // Groups all segments of that kind into connected components (shared nodes). A healthy city has ONE component per kind
        // (roads: one that also contains the highway). With x and z it also tells which component the nearest node belongs to.
        private static HttpResponse Networks(HttpRequest req)
        {
            Dictionary<string, string> q = BuildEndpoints.ParseQuery(req.Query);
            string kind;
            if (!q.TryGetValue("kind", out kind)) throw new ApiException(400, "Missing query: kind=road|water|power");
            kind = kind.ToLowerInvariant();
            if (kind != "road" && kind != "water" && kind != "power") throw new ApiException(400, "kind must be road, water or power");
            bool hasPoint = q.ContainsKey("x") && q.ContainsKey("z");
            float px = hasPoint ? BuildEndpoints.GetFloat(q, "x") : 0f, pz = hasPoint ? BuildEndpoints.GetFloat(q, "z") : 0f;

            string json = GameThread.Run<string>(delegate ()
            {
                NetManager nm = Singleton<NetManager>.instance;
                NetSegment[] segs = nm.m_segments.m_buffer;
                NetNode[] nodes = nm.m_nodes.m_buffer;
                int[] parent = new int[nodes.Length];
                for (int i = 0; i < parent.Length; i++) parent[i] = i;
                List<int> used = new List<int>();
                for (int i = 1; i < segs.Length; i++)
                {
                    NetSegment s = segs[i];
                    if ((s.m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None || !InKind(kind, s.Info)) continue;
                    used.Add(i);
                    int a = Find(parent, s.m_startNode), b = Find(parent, s.m_endNode);
                    if (a != b) parent[a] = b;
                }
                Dictionary<int, Comp> comps = new Dictionary<int, Comp>();
                HashSet<int> seenNodes = new HashSet<int>();
                foreach (int i in used)
                {
                    NetSegment s = segs[i];
                    int root = Find(parent, s.m_startNode);
                    Comp c;
                    if (!comps.TryGetValue(root, out c)) { c = new Comp(); comps[root] = c; }
                    c.Segments++;
                    if (seenNodes.Add(s.m_startNode)) c.Nodes++;
                    if (seenNodes.Add(s.m_endNode)) c.Nodes++;
                    Vector3 a = nodes[s.m_startNode].m_position, b = nodes[s.m_endNode].m_position;
                    c.MinX = Mathf.Min(c.MinX, Mathf.Min(a.x, b.x)); c.MaxX = Mathf.Max(c.MaxX, Mathf.Max(a.x, b.x));
                    c.MinZ = Mathf.Min(c.MinZ, Mathf.Min(a.z, b.z)); c.MaxZ = Mathf.Max(c.MaxZ, Mathf.Max(a.z, b.z));
                    string name = s.Info.name;
                    if (c.Types.Count < 8) c.Types.Add(name);
                    if (name.IndexOf("Highway", StringComparison.OrdinalIgnoreCase) >= 0) c.Highway = true;
                }
                int pointRoot = -1;
                if (hasPoint)
                {
                    float best = float.MaxValue;
                    foreach (int i in used)
                    {
                        NetSegment s = segs[i];
                        ushort[] ends = new ushort[] { s.m_startNode, s.m_endNode };
                        foreach (ushort n in ends)
                        {
                            Vector3 p = nodes[n].m_position;
                            float d = (p.x - px) * (p.x - px) + (p.z - pz) * (p.z - pz);
                            if (d < best) { best = d; pointRoot = Find(parent, n); }
                        }
                    }
                }
                List<KeyValuePair<int, Comp>> list = new List<KeyValuePair<int, Comp>>(comps);
                list.Sort(delegate (KeyValuePair<int, Comp> x, KeyValuePair<int, Comp> y) { return y.Value.Segments.CompareTo(x.Value.Segments); });
                JsonWriter w = new JsonWriter().Obj().Prop("kind", kind).Prop("components", list.Count)
                    .Prop("connected", list.Count <= 1);
                if (hasPoint)
                {
                    int idx = -1;
                    for (int i = 0; i < list.Count; i++) if (list[i].Key == pointRoot) idx = i;
                    w.Prop("point_in_component", idx);
                }
                w.ArrIn("list");
                int shown = 0;
                for (int i = 0; i < list.Count && shown < 25; i++, shown++)
                {
                    Comp c = list[i].Value;
                    w.Obj().Prop("index", i).Prop("segments", c.Segments).Prop("nodes", c.Nodes)
                        .Prop("min_x", R(c.MinX)).Prop("max_x", R(c.MaxX)).Prop("min_z", R(c.MinZ)).Prop("max_z", R(c.MaxZ));
                    if (kind == "road") w.Prop("has_highway", c.Highway);
                    w.ArrIn("types");
                    foreach (string t in c.Types) w.Raw("\"" + t.Replace("\"", "") + "\"");
                    w.EndArr().EndObj();
                }
                return w.EndArr().EndObj().ToString();
            }, 10000);
            return HttpResponse.Json(200, json);
        }

        // ---------------------------------------------------------------- /building
        private static void ProblemNames(ulong bits, Type enumType, List<string> into)
        {
            foreach (object o in Enum.GetValues(enumType))
            {
                ulong v = Convert.ToUInt64(o);
                if (v == 0 || (v & (v - 1)) != 0 || (bits & v) == 0) continue;
                into.Add(o.ToString());
            }
        }

        // GET /building?id=123 : prefab, service, position, flags, problems, buffers and citizens of one building (ids come from get_map)
        private static HttpResponse BuildingInfoEndpoint(HttpRequest req)
        {
            Dictionary<string, string> q = BuildEndpoints.ParseQuery(req.Query);
            int id = JsonReader.GetInt(q, "id", 1, 49151);
            string json = GameThread.Run<string>(delegate ()
            {
                Building b = Singleton<BuildingManager>.instance.m_buildings.m_buffer[id];
                if ((b.m_flags & Building.Flags.Created) == Building.Flags.None) throw new ApiException(404, "No building " + id);
                BuildingInfo info = b.Info;
                List<string> problems = new List<string>();
                ProblemNames(Convert.ToUInt64((Notification.Problem1)b.m_problems), typeof(Notification.Problem1), problems);
                ProblemNames(Convert.ToUInt64((Notification.Problem2)b.m_problems), typeof(Notification.Problem2), problems);
                int workers = 0, workplaces = 0;
                if (info != null && info.m_buildingAI != null)
                {
                    PrivateBuildingAI pai = info.m_buildingAI as PrivateBuildingAI;
                    if (pai != null)
                    {
                        int w0, w1, w2, w3;
                        pai.CalculateWorkplaceCount((ItemClass.Level)b.m_level, new ColossalFramework.Math.Randomizer(id), b.Width, b.Length, out w0, out w1, out w2, out w3);
                        workplaces = w0 + w1 + w2 + w3;
                    }
                    uint unit = b.m_citizenUnits;
                    int guard = 0;
                    CitizenUnit[] units = Singleton<CitizenManager>.instance.m_units.m_buffer;
                    while (unit != 0 && guard++ < 512)
                    {
                        if ((units[unit].m_flags & CitizenUnit.Flags.Work) != 0)
                        {
                            for (int k = 0; k < 5; k++) if (units[unit].GetCitizen(k) != 0) workers++;
                        }
                        unit = units[unit].m_nextUnit;
                    }
                }
                JsonWriter w = new JsonWriter().Obj().Prop("id", id)
                    .Prop("prefab", info != null ? info.name : null)
                    .Prop("service", info != null ? info.m_class.m_service.ToString() : null)
                    .Prop("subservice", info != null ? info.m_class.m_subService.ToString() : null)
                    .Prop("level", info != null ? info.m_class.m_level.ToString() : null)
                    .Prop("x", R(b.m_position.x)).Prop("z", R(b.m_position.z))
                    .Prop("angle_deg", R(b.m_angle * Mathf.Rad2Deg))
                    .Prop("width", b.Width).Prop("length", b.Length)
                    .Prop("flags", b.m_flags.ToString())
                    .Prop("electricity_ok", problems.IndexOf("Electricity") < 0 && problems.IndexOf("ElectricityNotConnected") < 0)
                    .Prop("water_ok", problems.IndexOf("Water") < 0 && problems.IndexOf("WaterNotConnected") < 0)
                    .Prop("sewage_ok", problems.IndexOf("Sewage") < 0)
                    .Prop("citizens", b.m_citizenCount)
                    .Prop("workers", workers).Prop("workplaces", workplaces)
                    .Prop("happiness", b.m_happiness).Prop("health", b.m_health)
                    .Prop("production_rate", b.m_productionRate)
                    .Prop("electricity_buffer", b.m_electricityBuffer).Prop("water_buffer", b.m_waterBuffer)
                    .Prop("sewage_buffer", b.m_sewageBuffer).Prop("garbage_buffer", b.m_garbageBuffer)
                    .Prop("fire_intensity", b.m_fireIntensity);
                w.ArrIn("problems");
                foreach (string p in problems) w.Raw("\"" + p + "\"");
                w.EndArr();
                return w.EndObj().ToString();
            }, 5000);
            return HttpResponse.Json(200, json);
        }

        // ---------------------------------------------------------------- /weather
        private static HttpResponse GetWeather(HttpRequest req)
        {
            string json = GameThread.Run<string>(delegate ()
            {
                WeatherManager wm = Singleton<WeatherManager>.instance;
                return new JsonWriter().Obj()
                    .Prop("weather_enabled", wm.m_enableWeather).Prop("forced", wm.m_forceWeatherOn > 0f)
                    .Prop("rain", Math.Round(wm.m_currentRain, 2)).Prop("target_rain", Math.Round(wm.m_targetRain, 2))
                    .Prop("fog", Math.Round(wm.m_currentFog, 2)).Prop("target_fog", Math.Round(wm.m_targetFog, 2))
                    .Prop("cloud", Math.Round(wm.m_currentCloud, 2))
                    .Prop("temperature", Math.Round(wm.m_currentTemperature, 1))
                    .EndObj().ToString();
            }, 5000);
            return HttpResponse.Json(200, json);
        }

        // POST /weather {"rain":0..1, "fog":0..1, "cloud":0..1, "temperature":-20..45}  (all optional)
        // Sets the target weather and keeps the weather system on while the game runs (rain/fog fade in and out slowly).
        private static HttpResponse SetWeather(HttpRequest req)
        {
            Dictionary<string, string> b = JsonReader.ParseFlat(req.Body);
            if (b.Count == 0) throw new ApiException(400, "Give at least one of rain, fog, cloud, temperature");
            float rain = b.ContainsKey("rain") ? BuildEndpoints.GetFloat(b, "rain") : -1f;
            float fog = b.ContainsKey("fog") ? BuildEndpoints.GetFloat(b, "fog") : -1f;
            float cloud = b.ContainsKey("cloud") ? BuildEndpoints.GetFloat(b, "cloud") : -1f;
            bool hasTemp = b.ContainsKey("temperature");
            float temp = hasTemp ? BuildEndpoints.GetFloat(b, "temperature") : 0f;
            if (rain > 1f || fog > 1f || cloud > 1f) throw new ApiException(400, "rain, fog and cloud are 0..1");
            if (hasTemp && (temp < -30f || temp > 50f)) throw new ApiException(400, "temperature is -30..50 degrees C");
            string json = GameThread.Run<string>(delegate ()
            {
                WeatherManager wm = Singleton<WeatherManager>.instance;
                wm.m_forceWeatherOn = 100000f;
                if (rain >= 0f) wm.m_targetRain = rain;
                if (fog >= 0f) wm.m_targetFog = fog;
                if (cloud >= 0f) wm.m_targetCloud = cloud;
                if (hasTemp) wm.m_targetTemperature = temp;
                return new JsonWriter().Obj().Prop("ok", true).Prop("target_rain", Math.Round(wm.m_targetRain, 2))
                    .Prop("target_fog", Math.Round(wm.m_targetFog, 2)).Prop("target_cloud", Math.Round(wm.m_targetCloud, 2))
                    .Prop("target_temperature", Math.Round(wm.m_targetTemperature, 1)).EndObj().ToString();
            }, 5000);
            return HttpResponse.Json(200, json);
        }
    }
}
