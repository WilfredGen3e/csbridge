using System;
using System.Collections.Generic;
using ColossalFramework;
using ColossalFramework.Math;
using UnityEngine;

namespace CSBridge
{
    /// <summary>
    /// Extra tools: bulldoze an area, traffic per road, ascii zone map, loans, trees/props, terrain editing, traffic lights.
    /// </summary>
    public static class ToolsEndpoints
    {
        public static void Register(HttpServer server)
        {
            server.Post("/bulldoze/area", BulldozeArea);
            server.Get("/traffic/roads", TrafficRoads);
            server.Get("/zones/grid", ZoneGrid);
            server.Get("/loans", Loans);
            server.Post("/loans/take", TakeLoan);
            server.Post("/loans/pay", PayLoan);
            server.Get("/flora/prefabs", FloraPrefabs);
            server.Post("/tree/place", PlaceTree);
            server.Post("/prop/place", PlaceProp);
            server.Post("/terrain/edit", TerrainEdit);
            server.Post("/node/traffic_lights", TrafficLights);
            server.Get("/nodes", Nodes);
        }

        // ---- shared: a circle or rectangle from the request ----
        private struct Area
        {
            public float X1, Z1, X2, Z2;       // rectangle
            public float Cx, Cz, R;            // circle (R > 0)
            public bool Contains(float x, float z)
            {
                if (R > 0f) { float dx = x - Cx, dz = z - Cz; return dx * dx + dz * dz <= R * R; }
                return x >= X1 && x <= X2 && z >= Z1 && z <= Z2;
            }
        }

        private static Area ParseArea(Dictionary<string, string> d, float maxRadius)
        {
            Area a = new Area();
            if (d.ContainsKey("radius"))
            {
                a.Cx = BuildEndpoints.GetFloat(d, "x"); a.Cz = BuildEndpoints.GetFloat(d, "z"); a.R = BuildEndpoints.GetFloat(d, "radius");
                BuildEndpoints.CheckBounds(a.Cx, a.Cz);
                if (a.R <= 0f || a.R > maxRadius) throw new ApiException(400, "radius must be 1-" + maxRadius);
            }
            else
            {
                float x1 = BuildEndpoints.GetFloat(d, "x1"), z1 = BuildEndpoints.GetFloat(d, "z1"), x2 = BuildEndpoints.GetFloat(d, "x2"), z2 = BuildEndpoints.GetFloat(d, "z2");
                BuildEndpoints.CheckBounds(x1, z1); BuildEndpoints.CheckBounds(x2, z2);
                a.X1 = Math.Min(x1, x2); a.X2 = Math.Max(x1, x2); a.Z1 = Math.Min(z1, z2); a.Z2 = Math.Max(z1, z2);
                if ((a.X2 - a.X1) > maxRadius * 2f || (a.Z2 - a.Z1) > maxRadius * 2f) throw new ApiException(400, "area too large (max " + (maxRadius * 2f) + " m per side)");
            }
            return a;
        }

        private static bool Flag(Dictionary<string, string> d, string key)
        {
            return d.ContainsKey(key) && JsonReader.GetBool(d, key);
        }

        // ---- POST /bulldoze/area ----
        // {"x1":..,"z1":..,"x2":..,"z2":..} or {"x":..,"z":..,"radius":..}; "what": "buildings,roads,trees,props" (default buildings,roads);
        // optional filters "service", "prefab" (buildings), "road" (road type contains); "dry_run": true only counts.
        private static HttpResponse BulldozeArea(HttpRequest req)
        {
            Dictionary<string, string> body = JsonReader.ParseFlat(req.Body);
            Area area = ParseArea(body, 1500f);
            string what = body.ContainsKey("what") ? "," + body["what"].ToLowerInvariant().Replace(" ", "") + "," : ",buildings,roads,";
            bool dry = Flag(body, "dry_run");
            string svc = body.ContainsKey("service") ? body["service"] : null, prefab = body.ContainsKey("prefab") ? body["prefab"] : null, roadF = body.ContainsKey("road") ? body["road"] : null;
            string json = GameThread.Run<string>(delegate ()
            {
                int nb = 0, nr = 0, nt = 0, np = 0;
                List<string> sample = new List<string>();
                if (what.Contains(",buildings,"))
                {
                    BuildingManager bm = Singleton<BuildingManager>.instance;
                    for (int i = 1; i < bm.m_buildings.m_buffer.Length; i++)
                    {
                        Building b = bm.m_buildings.m_buffer[i];
                        if ((b.m_flags & Building.Flags.Created) == Building.Flags.None || (b.m_flags & Building.Flags.Untouchable) != Building.Flags.None) continue;
                        BuildingInfo info = b.Info;
                        if (info == null || !area.Contains(b.m_position.x, b.m_position.z)) continue;
                        if (svc != null && info.m_class.m_service.ToString().IndexOf(svc, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (prefab != null && info.name.IndexOf(prefab, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        nb++;
                        if (sample.Count < 12) sample.Add("building " + i + " " + info.name);
                        if (!dry) bm.ReleaseBuilding((ushort)i);
                    }
                }
                if (what.Contains(",roads,"))
                {
                    NetManager nm = Singleton<NetManager>.instance;
                    for (int i = 1; i < nm.m_segments.m_buffer.Length; i++)
                    {
                        NetSegment s = nm.m_segments.m_buffer[i];
                        if ((s.m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None || (s.m_flags & NetSegment.Flags.Untouchable) != NetSegment.Flags.None) continue;
                        NetInfo info = s.Info;
                        if (info == null || info.m_netAI is TransportLineAI || info.name.StartsWith("Pedestrian Connection")) continue;
                        Vector3 mid = (nm.m_nodes.m_buffer[s.m_startNode].m_position + nm.m_nodes.m_buffer[s.m_endNode].m_position) * 0.5f;
                        if (!area.Contains(mid.x, mid.z)) continue;
                        if (roadF != null && info.name.IndexOf(roadF, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        nr++;
                        if (sample.Count < 12) sample.Add("segment " + i + " " + info.name);
                        if (!dry) nm.ReleaseSegment((ushort)i, false);
                    }
                }
                if (what.Contains(",trees,"))
                {
                    TreeManager tm = Singleton<TreeManager>.instance;
                    for (uint i = 1; i < tm.m_trees.m_buffer.Length; i++)
                    {
                        if ((tm.m_trees.m_buffer[i].m_flags & (ushort)TreeInstance.Flags.Created) == 0) continue;
                        Vector3 p = tm.m_trees.m_buffer[i].Position;
                        if (!area.Contains(p.x, p.z)) continue;
                        nt++;
                        if (!dry) tm.ReleaseTree(i);
                    }
                }
                if (what.Contains(",props,"))
                {
                    PropManager pm = Singleton<PropManager>.instance;
                    for (int i = 1; i < pm.m_props.m_buffer.Length; i++)
                    {
                        if ((pm.m_props.m_buffer[i].m_flags & (ushort)PropInstance.Flags.Created) == 0) continue;
                        Vector3 p = pm.m_props.m_buffer[i].Position;
                        if (!area.Contains(p.x, p.z)) continue;
                        np++;
                        if (!dry) pm.ReleaseProp((ushort)i);
                    }
                }
                JsonWriter w = new JsonWriter().Obj().Prop("ok", true).Prop("dry_run", dry)
                    .Prop(dry ? "would_remove_buildings" : "buildings_removed", nb).Prop(dry ? "would_remove_segments" : "segments_removed", nr)
                    .Prop(dry ? "would_remove_trees" : "trees_removed", nt).Prop(dry ? "would_remove_props" : "props_removed", np);
                w.ArrIn("sample"); foreach (string x in sample) w.Str(x); w.EndArr();
                return w.EndObj().ToString();
            }, 30000);
            return HttpResponse.Json(200, json);
        }

        // ---- GET /traffic/roads?x=&z=&radius=&limit=15 : the busiest road segments (density 0-255) ----
        private static HttpResponse TrafficRoads(HttpRequest req)
        {
            Dictionary<string, string> q = BuildEndpoints.ParseQuery(req.Query);
            float cx = BuildEndpoints.GetFloat(q, "x", 0f), cz = BuildEndpoints.GetFloat(q, "z", 0f), radius = BuildEndpoints.GetFloat(q, "radius", 0f);
            bool whole = radius <= 0f;
            int limit = BuildEndpoints.GetIntQ(q, "limit", 15, 1, 200);
            string json = GameThread.Run<string>(delegate ()
            {
                NetManager nm = Singleton<NetManager>.instance;
                List<KeyValuePair<int, int>> list = new List<KeyValuePair<int, int>>();
                long sum = 0; int n = 0;
                for (int i = 1; i < nm.m_segments.m_buffer.Length; i++)
                {
                    NetSegment s = nm.m_segments.m_buffer[i];
                    if ((s.m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None || s.Info == null || s.Info.m_class.m_service != ItemClass.Service.Road) continue;
                    if (s.Info.m_netAI is TransportLineAI) continue;
                    Vector3 mid = (nm.m_nodes.m_buffer[s.m_startNode].m_position + nm.m_nodes.m_buffer[s.m_endNode].m_position) * 0.5f;
                    if (!whole) { float dx = mid.x - cx, dz = mid.z - cz; if (dx * dx + dz * dz > radius * radius) continue; }
                    sum += s.m_trafficDensity; n++;
                    if (s.m_trafficDensity > 0) list.Add(new KeyValuePair<int, int>(i, s.m_trafficDensity));
                }
                list.Sort(delegate (KeyValuePair<int, int> a, KeyValuePair<int, int> b) { return b.Value.CompareTo(a.Value); });
                JsonWriter w = new JsonWriter().Obj().Prop("segments_checked", n).Prop("average_density", n > 0 ? Math.Round((double)sum / n, 1) : 0.0);
                w.Raw("\"format\":\"[segment,density 0-255,road,x,z]; 255 = jammed\"");
                w.ArrIn("busiest");
                for (int k = 0; k < list.Count && k < limit; k++)
                {
                    NetSegment s = nm.m_segments.m_buffer[list[k].Key];
                    Vector3 mid = (nm.m_nodes.m_buffer[s.m_startNode].m_position + nm.m_nodes.m_buffer[s.m_endNode].m_position) * 0.5f;
                    w.Raw("[" + list[k].Key + "," + list[k].Value + ",\"" + s.Info.name + "\"," + (int)mid.x + "," + (int)mid.z + "]");
                }
                return w.EndArr().EndObj().ToString();
            }, 10000);
            return HttpResponse.Json(200, json);
        }

        // ---- GET /zones/grid?x=&z=&radius=160&cell=8 : the area as text (north at the top) ----
        private static HttpResponse ZoneGrid(HttpRequest req)
        {
            Dictionary<string, string> q = BuildEndpoints.ParseQuery(req.Query);
            float cx = BuildEndpoints.GetFloat(q, "x"), cz = BuildEndpoints.GetFloat(q, "z");
            float radius = BuildEndpoints.GetFloat(q, "radius", 160f);
            float cell = BuildEndpoints.GetFloat(q, "cell", 8f);
            BuildEndpoints.CheckBounds(cx, cz);
            if (cell < 4f || cell > 64f) throw new ApiException(400, "cell must be 4-64 m");
            if (radius < 16f || radius > 640f || (2f * radius / cell) > 160f) throw new ApiException(400, "radius 16-640 and at most 160 cells per side (raise cell)");
            string json = GameThread.Run<string>(delegate ()
            {
                int n = (int)(2f * radius / cell);
                float x0 = cx - radius, z0 = cz - radius;
                char[] g = new char[n * n];
                for (int i = 0; i < g.Length; i++) g[i] = ' ';
                Action<float, float, char, bool> put = delegate (float x, float z, char c, bool force)
                {
                    int gx = (int)((x - x0) / cell), gz = (int)((z - z0) / cell);
                    if (gx < 0 || gz < 0 || gx >= n || gz >= n) return;
                    int idx = gz * n + gx;
                    if (force || g[idx] == ' ' || g[idx] == '.') g[idx] = c;
                };
                // zone cells (8 x 8 m)
                ZoneManager zm = Singleton<ZoneManager>.instance;
                int gx0 = Mathf.Max((int)((cx - radius - 46f) / 64f + 75f), 0), gz0 = Mathf.Max((int)((cz - radius - 46f) / 64f + 75f), 0);
                int gx1 = Mathf.Min((int)((cx + radius + 46f) / 64f + 75f), 149), gz1 = Mathf.Min((int)((cz + radius + 46f) / 64f + 75f), 149);
                for (int gz = gz0; gz <= gz1; gz++)
                    for (int gx = gx0; gx <= gx1; gx++)
                    {
                        ushort blk = zm.m_zoneGrid[gz * 150 + gx];
                        int guard = 0;
                        while (blk != 0 && guard++ < 49152)
                        {
                            ZoneBlock zb = zm.m_blocks.m_buffer[blk];
                            float ca = Mathf.Cos(zb.m_angle) * 8f, sa = Mathf.Sin(zb.m_angle) * 8f;
                            for (int r = 0; r < zb.RowCount; r++)
                                for (int c = 0; c < 4; c++)
                                {
                                    float px = zb.m_position.x + (c - 3.5f) * ca + (r - 3.5f) * sa;
                                    float pz = zb.m_position.z + (c - 3.5f) * sa - (r - 3.5f) * ca;
                                    put(px, pz, ZoneChar(zb.GetZone(c, r)), false);
                                }
                            blk = zb.m_nextGridBlock;
                        }
                    }
                // buildings (footprint) then roads
                BuildingManager bm = Singleton<BuildingManager>.instance;
                for (int i = 1; i < bm.m_buildings.m_buffer.Length; i++)
                {
                    Building b = bm.m_buildings.m_buffer[i];
                    if ((b.m_flags & Building.Flags.Created) == Building.Flags.None || b.Info == null) continue;
                    if (Math.Abs(b.m_position.x - cx) > radius + 60f || Math.Abs(b.m_position.z - cz) > radius + 60f) continue;
                    ItemClass.Service sv = b.Info.m_class.m_service;
                    char ch = (sv == ItemClass.Service.Residential) ? 'h' : (sv == ItemClass.Service.Commercial) ? 'k' : (sv == ItemClass.Service.Industrial) ? 'f' : (sv == ItemClass.Service.Office) ? 'o' : 'S';
                    float hw = b.Width * 4f, hl = b.Length * 4f, ang = b.m_angle, cs = Mathf.Cos(ang), sn = Mathf.Sin(ang);
                    float step = Math.Min(cell, 4f);
                    for (float u = -hw; u <= hw; u += step)
                        for (float v = -hl; v <= hl; v += step)
                            put(b.m_position.x + u * cs + v * sn, b.m_position.z - u * sn + v * cs, ch, true);
                }
                NetManager nm = Singleton<NetManager>.instance;
                for (int i = 1; i < nm.m_segments.m_buffer.Length; i++)
                {
                    NetSegment s = nm.m_segments.m_buffer[i];
                    if ((s.m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None || s.Info == null || s.Info.m_class.m_service != ItemClass.Service.Road || s.Info.m_netAI is TransportLineAI) continue;
                    if (s.Info.name.IndexOf("Pedestrian", StringComparison.Ordinal) >= 0) continue;
                    Vector3 a = nm.m_nodes.m_buffer[s.m_startNode].m_position, b2 = nm.m_nodes.m_buffer[s.m_endNode].m_position;
                    if (Math.Max(a.x, b2.x) < x0 || Math.Min(a.x, b2.x) > x0 + n * cell || Math.Max(a.z, b2.z) < z0 || Math.Min(a.z, b2.z) > z0 + n * cell) continue;
                    int steps = Math.Max(2, (int)(s.m_averageLength / (cell * 0.5f)));
                    for (int k = 0; k <= steps; k++) { Vector3 p = Vector3.Lerp(a, b2, (float)k / steps); put(p.x, p.z, '#', true); }
                }
                JsonWriter w = new JsonWriter().Obj().Prop("x", cx).Prop("z", cz).Prop("radius", radius).Prop("cell_m", cell);
                w.Prop("legend", "# road | zone (empty): r=ResidentialLow R=ResidentialHigh c=CommercialLow C=CommercialHigh I=Industrial O=Office .=unzoned | building: h=home k=shop f=factory o=office S=service | north is up");
                w.ArrIn("rows");
                for (int gz = n - 1; gz >= 0; gz--) w.Str(new string(g, gz * n, n).TrimEnd());
                return w.EndArr().EndObj().ToString();
            }, 15000);
            return HttpResponse.Json(200, json);
        }

        private static char ZoneChar(ItemClass.Zone z)
        {
            switch (z)
            {
                case ItemClass.Zone.ResidentialLow: return 'r';
                case ItemClass.Zone.ResidentialHigh: return 'R';
                case ItemClass.Zone.CommercialLow: return 'c';
                case ItemClass.Zone.CommercialHigh: return 'C';
                case ItemClass.Zone.Industrial: return 'I';
                case ItemClass.Zone.Office: return 'O';
                default: return '.';
            }
        }

        // ---- loans ----
        // GET /loans -> bank offers (bank, offer, amount, interest %, length in weeks) and the loans taken
        private static HttpResponse Loans(HttpRequest req)
        {
            string json = GameThread.Run<string>(delegate ()
            {
                EconomyManager em = Singleton<EconomyManager>.instance;
                JsonWriter w = new JsonWriter().Obj().ArrIn("offers");
                for (int bank = 0; bank < 8; bank++)
                {
                    EconomyManager.LoanInfo[] infos;
                    if (!em.GetLoanInfo(bank, out infos) || infos == null) break;
                    for (int i = 0; i < infos.Length; i++)
                        w.Obj().Prop("bank", bank).Prop("offer", i).Prop("amount", infos[i].m_amount / 100).Prop("interest_percent", infos[i].m_interest / 100.0).Prop("length_weeks", infos[i].m_length).EndObj();
                }
                w.EndArr().ArrIn("loans");
                for (int i = 0; i < 8; i++)
                {
                    EconomyManager.Loan l;
                    if (!em.GetLoan(i, out l)) continue;
                    w.Obj().Prop("slot", i).Prop("taken", l.m_amountTaken / 100).Prop("left", l.m_amountLeft / 100).Prop("interest_percent", l.m_interestRate / 100.0).Prop("length_weeks", l.m_length).EndObj();
                }
                return w.EndArr().EndObj().ToString();
            }, 5000);
            return HttpResponse.Json(200, json);
        }

        // POST /loans/take {"bank":0,"offer":1}
        private static HttpResponse TakeLoan(HttpRequest req)
        {
            Dictionary<string, string> body = JsonReader.ParseFlat(req.Body);
            int bank = JsonReader.GetInt(body, "bank", 0, 7), offer = JsonReader.GetInt(body, "offer", 0, 15);
            string json = GameThread.Run<string>(delegate ()
            {
                EconomyManager em = Singleton<EconomyManager>.instance;
                EconomyManager.LoanInfo[] infos;
                if (!em.GetLoanInfo(bank, out infos) || offer >= infos.Length) throw new ApiException(404, "No such loan offer (see GET /loans)");
                int slot = -1;
                for (int i = 0; i < 8; i++) { EconomyManager.Loan l; if (!em.GetLoan(i, out l)) { slot = i; break; } }
                if (slot < 0) throw new ApiException(409, "All loan slots are in use");
                IEnumerator<bool> e = em.TakeNewLoan(slot, infos[offer].m_amount, infos[offer].m_interest, infos[offer].m_length);
                bool ok = false;
                while (e.MoveNext()) ok = e.Current;
                if (!ok) throw new ApiException(409, "The bank refused the loan");
                return new JsonWriter().Obj().Prop("ok", true).Prop("slot", slot).Prop("amount", infos[offer].m_amount / 100).EndObj().ToString();
            }, 5000);
            return HttpResponse.Json(200, json);
        }

        // POST /loans/pay {"slot":0}  (pays off the rest now)
        private static HttpResponse PayLoan(HttpRequest req)
        {
            int slot = JsonReader.GetInt(JsonReader.ParseFlat(req.Body), "slot", 0, 7);
            string json = GameThread.Run<string>(delegate ()
            {
                EconomyManager em = Singleton<EconomyManager>.instance;
                EconomyManager.Loan l;
                if (!em.GetLoan(slot, out l)) throw new ApiException(404, "No loan in slot " + slot);
                IEnumerator<bool> e = em.PayLoanNow(slot);
                bool paid = false;
                while (e.MoveNext()) paid = e.Current;
                return new JsonWriter().Obj().Prop("ok", true).Prop("fully_paid", paid).EndObj().ToString();
            }, 5000);
            return HttpResponse.Json(200, json);
        }

        // ---- trees and props ----
        // GET /flora/prefabs?kind=tree|prop&filter=text&limit=60
        private static HttpResponse FloraPrefabs(HttpRequest req)
        {
            Dictionary<string, string> q = BuildEndpoints.ParseQuery(req.Query);
            string kind = q.ContainsKey("kind") ? q["kind"].ToLowerInvariant() : "tree";
            string filter = q.ContainsKey("filter") ? q["filter"] : null;
            int limit = BuildEndpoints.GetIntQ(q, "limit", 60, 1, 500);
            string json = GameThread.Run<string>(delegate ()
            {
                JsonWriter w = new JsonWriter().Obj().ArrIn("prefabs");
                int n = 0, total = 0;
                if (kind == "prop")
                {
                    int cnt = PrefabCollection<PropInfo>.LoadedCount();
                    for (uint i = 0; i < cnt; i++)
                    {
                        PropInfo p = PrefabCollection<PropInfo>.GetLoaded(i);
                        if (p == null || (filter != null && p.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                        total++;
                        if (n < limit) { w.Str(p.name); n++; }
                    }
                }
                else
                {
                    int cnt = PrefabCollection<TreeInfo>.LoadedCount();
                    for (uint i = 0; i < cnt; i++)
                    {
                        TreeInfo t = PrefabCollection<TreeInfo>.GetLoaded(i);
                        if (t == null || (filter != null && t.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                        total++;
                        if (n < limit) { w.Str(t.name); n++; }
                    }
                }
                return w.EndArr().Prop("matching", total).EndObj().ToString();
            }, 5000);
            return HttpResponse.Json(200, json);
        }

        private static Vector3 Scatter(float cx, float cz, float radius, System.Random rnd)
        {
            double a = rnd.NextDouble() * Math.PI * 2.0, d = Math.Sqrt(rnd.NextDouble()) * radius;
            return new Vector3((float)(cx + Math.Cos(a) * d), 0f, (float)(cz + Math.Sin(a) * d));
        }

        // POST /tree/place {"prefab":"Oak Tree","x":..,"z":..,"count":1,"radius":0}   (count > 1: random spots within radius)
        private static HttpResponse PlaceTree(HttpRequest req)
        {
            Dictionary<string, string> body = JsonReader.ParseFlat(req.Body);
            string name = body.ContainsKey("prefab") ? body["prefab"] : null;
            if (string.IsNullOrEmpty(name)) throw new ApiException(400, "Missing field: prefab (see /flora/prefabs?kind=tree)");
            float x = BuildEndpoints.GetFloat(body, "x"), z = BuildEndpoints.GetFloat(body, "z"), radius = BuildEndpoints.GetFloat(body, "radius", 0f);
            int count = body.ContainsKey("count") ? JsonReader.GetInt(body, "count", 1, 500) : 1;
            BuildEndpoints.CheckBounds(x, z);
            string json = GameThread.Run<string>(delegate ()
            {
                TreeInfo info = PrefabCollection<TreeInfo>.FindLoaded(name);
                if (info == null) throw new ApiException(404, "No tree prefab '" + name + "' (see /flora/prefabs?kind=tree)");
                SimulationManager sim = Singleton<SimulationManager>.instance;
                System.Random rnd = new System.Random();
                int made = 0;
                for (int i = 0; i < count; i++)
                {
                    Vector3 p = count == 1 || radius <= 0f ? new Vector3(x, 0f, z) : Scatter(x, z, radius, rnd);
                    p.y = Singleton<TerrainManager>.instance.SampleRawHeightSmooth(p);
                    uint id;
                    if (Singleton<TreeManager>.instance.CreateTree(out id, ref sim.m_randomizer, info, p, count == 1)) made++;
                }
                return new JsonWriter().Obj().Prop("ok", made > 0).Prop("placed", made).Prop("prefab", info.name).EndObj().ToString();
            }, 10000);
            return HttpResponse.Json(200, json);
        }

        // POST /prop/place {"prefab":"Fence","x":..,"z":..,"angle_deg":0,"count":1,"radius":0}
        private static HttpResponse PlaceProp(HttpRequest req)
        {
            Dictionary<string, string> body = JsonReader.ParseFlat(req.Body);
            string name = body.ContainsKey("prefab") ? body["prefab"] : null;
            if (string.IsNullOrEmpty(name)) throw new ApiException(400, "Missing field: prefab (see /flora/prefabs?kind=prop)");
            float x = BuildEndpoints.GetFloat(body, "x"), z = BuildEndpoints.GetFloat(body, "z"), radius = BuildEndpoints.GetFloat(body, "radius", 0f), ang = BuildEndpoints.GetFloat(body, "angle_deg", 0f);
            int count = body.ContainsKey("count") ? JsonReader.GetInt(body, "count", 1, 500) : 1;
            BuildEndpoints.CheckBounds(x, z);
            string json = GameThread.Run<string>(delegate ()
            {
                PropInfo info = PrefabCollection<PropInfo>.FindLoaded(name);
                if (info == null) throw new ApiException(404, "No prop prefab '" + name + "' (see /flora/prefabs?kind=prop)");
                SimulationManager sim = Singleton<SimulationManager>.instance;
                System.Random rnd = new System.Random();
                int made = 0;
                for (int i = 0; i < count; i++)
                {
                    Vector3 p = count == 1 || radius <= 0f ? new Vector3(x, 0f, z) : Scatter(x, z, radius, rnd);
                    p.y = Singleton<TerrainManager>.instance.SampleRawHeightSmooth(p);
                    ushort id;
                    float a = (count == 1 ? ang : (float)(rnd.NextDouble() * 360.0)) * Mathf.Deg2Rad;
                    if (Singleton<PropManager>.instance.CreateProp(out id, ref sim.m_randomizer, info, p, a, count == 1)) made++;
                }
                return new JsonWriter().Obj().Prop("ok", made > 0).Prop("placed", made).Prop("prefab", info.name).EndObj().ToString();
            }, 10000);
            return HttpResponse.Json(200, json);
        }

        // ---- POST /terrain/edit ----
        // {"mode":"flatten|raise|lower|smooth","x":..,"z":..,"radius":..,"height":(flatten target m, default = height at the centre),"amount":(raise/lower, m),"falloff":0.3}
        // Terrain is a 1081x1081 grid of 16 m cells (heights in 1/64 m). Buildings and roads that stood there are not moved.
        private static HttpResponse TerrainEdit(HttpRequest req)
        {
            Dictionary<string, string> body = JsonReader.ParseFlat(req.Body);
            string mode = body.ContainsKey("mode") ? body["mode"].ToLowerInvariant() : "flatten";
            float x = BuildEndpoints.GetFloat(body, "x"), z = BuildEndpoints.GetFloat(body, "z"), radius = BuildEndpoints.GetFloat(body, "radius", 60f);
            float amount = BuildEndpoints.GetFloat(body, "amount", 5f), falloff = BuildEndpoints.GetFloat(body, "falloff", 0.3f);
            BuildEndpoints.CheckBounds(x, z);
            if (mode != "flatten" && mode != "raise" && mode != "lower" && mode != "smooth") throw new ApiException(400, "mode must be flatten, raise, lower or smooth");
            if (radius < 8f || radius > 400f) throw new ApiException(400, "radius must be 8-400");
            if (falloff < 0f || falloff > 1f) throw new ApiException(400, "falloff must be 0-1");
            string json = GameThread.Run<string>(delegate ()
            {
                TerrainManager tm = Singleton<TerrainManager>.instance;
                ushort[] h = tm.RawHeights;
                float target = body.ContainsKey("height") ? BuildEndpoints.GetFloat(body, "height") : tm.SampleRawHeightSmooth(new Vector3(x, 0f, z));
                int ci = (int)Math.Round(x / 16f) + 540, cj = (int)Math.Round(z / 16f) + 540, rr = (int)Math.Ceiling(radius / 16f) + 1;
                int minI = Math.Max(ci - rr, 0), maxI = Math.Min(ci + rr, 1080), minJ = Math.Max(cj - rr, 0), maxJ = Math.Min(cj + rr, 1080);
                ushort[] copy = (ushort[])h.Clone();
                int changed = 0;
                for (int j = minJ; j <= maxJ; j++)
                    for (int i = minI; i <= maxI; i++)
                    {
                        float wx = (i - 540) * 16f, wz = (j - 540) * 16f;
                        float d = Mathf.Sqrt((wx - x) * (wx - x) + (wz - z) * (wz - z));
                        if (d > radius) continue;
                        float inner = radius * (1f - falloff);
                        float wgt = d <= inner || falloff <= 0f ? 1f : Mathf.SmoothStep(1f, 0f, (d - inner) / (radius - inner));
                        float old = copy[j * 1081 + i];
                        float nv;
                        if (mode == "flatten") nv = Mathf.Lerp(old, target * 64f, wgt);
                        else if (mode == "raise") nv = old + amount * 64f * wgt;
                        else if (mode == "lower") nv = old - amount * 64f * wgt;
                        else
                        {
                            float sum = 0f; int c = 0;
                            for (int dj = -1; dj <= 1; dj++) for (int di = -1; di <= 1; di++)
                            { int ii = i + di, jj = j + dj; if (ii < 0 || jj < 0 || ii > 1080 || jj > 1080) continue; sum += copy[jj * 1081 + ii]; c++; }
                            nv = Mathf.Lerp(old, sum / c, wgt);
                        }
                        h[j * 1081 + i] = (ushort)Mathf.Clamp(Mathf.RoundToInt(nv), 0, 65535);
                        changed++;
                    }
                TerrainModify.UpdateArea(minI, minJ, maxI, maxJ, true, true, true);
                return new JsonWriter().Obj().Prop("ok", true).Prop("mode", mode).Prop("grid_points_changed", changed)
                    .Prop("height_at_centre", Math.Round(tm.SampleRawHeightSmooth(new Vector3(x, 0f, z)), 1)).EndObj().ToString();
            }, 20000);
            return HttpResponse.Json(200, json);
        }

        // GET /nodes?x=&z=&radius=30 -> network nodes near a point (id, type, height, elevation, underground, segment count); for diagnosing joins
        private static HttpResponse Nodes(HttpRequest req)
        {
            Dictionary<string, string> q = BuildEndpoints.ParseQuery(req.Query);
            float cx = BuildEndpoints.GetFloat(q, "x"), cz = BuildEndpoints.GetFloat(q, "z"), radius = BuildEndpoints.GetFloat(q, "radius", 30f);
            if (radius <= 0f || radius > 300f) throw new ApiException(400, "radius must be 1-300");
            string json = GameThread.Run<string>(delegate ()
            {
                NetManager nm = Singleton<NetManager>.instance;
                JsonWriter w = new JsonWriter().Obj().ArrIn("nodes");
                int n = 0;
                for (int i = 1; i < nm.m_nodes.m_buffer.Length && n < 60; i++)
                {
                    NetNode nd = nm.m_nodes.m_buffer[i];
                    if ((nd.m_flags & NetNode.Flags.Created) == NetNode.Flags.None || nd.Info == null) continue;
                    float dx = nd.m_position.x - cx, dz = nd.m_position.z - cz;
                    if (dx * dx + dz * dz > radius * radius) continue;
                    n++;
                    w.Obj().Prop("id", i).Prop("type", nd.Info.name).Prop("x", Math.Round(nd.m_position.x, 1)).Prop("y", Math.Round(nd.m_position.y, 1)).Prop("z", Math.Round(nd.m_position.z, 1))
                        .Prop("ground_y", Math.Round(Singleton<TerrainManager>.instance.SampleRawHeightSmooth(nd.m_position), 1))
                        .Prop("elevation", nd.m_elevation).Prop("underground", (nd.m_flags & NetNode.Flags.Underground) != 0)
                        .Prop("segments", nd.CountSegments()).Prop("flags", nd.m_flags.ToString()).EndObj();
                }
                return w.EndArr().EndObj().ToString();
            }, 5000);
            return HttpResponse.Json(200, json);
        }

        // ---- POST /node/traffic_lights {"x":..,"z":..,"enabled":true} or {"node":123,"enabled":..} ----
        private static HttpResponse TrafficLights(HttpRequest req)
        {
            Dictionary<string, string> body = JsonReader.ParseFlat(req.Body);
            bool enabled = body.ContainsKey("enabled") ? JsonReader.GetBool(body, "enabled") : true;
            int nodeId = body.ContainsKey("node") ? JsonReader.GetInt(body, "node", 1, 36863) : 0;
            float x = nodeId == 0 ? BuildEndpoints.GetFloat(body, "x") : 0f, z = nodeId == 0 ? BuildEndpoints.GetFloat(body, "z") : 0f;
            string json = GameThread.Run<string>(delegate ()
            {
                NetManager nm = Singleton<NetManager>.instance;
                int id = nodeId;
                if (id == 0)
                {
                    float best = 12f;
                    for (int i = 1; i < nm.m_nodes.m_buffer.Length; i++)
                    {
                        NetNode n = nm.m_nodes.m_buffer[i];
                        if ((n.m_flags & NetNode.Flags.Created) == NetNode.Flags.None || n.Info == null || n.Info.m_class.m_service != ItemClass.Service.Road) continue;
                        if (n.CountSegments() < 3) continue;
                        float d = Mathf.Sqrt((n.m_position.x - x) * (n.m_position.x - x) + (n.m_position.z - z) * (n.m_position.z - z));
                        if (d < best) { best = d; id = i; }
                    }
                    if (id == 0) throw new ApiException(404, "No road junction within 12 m of (" + x + ", " + z + ")");
                }
                NetNode node = nm.m_nodes.m_buffer[id];
                if ((node.m_flags & NetNode.Flags.Created) == NetNode.Flags.None) throw new ApiException(404, "No node " + id);
                if (node.CountSegments() < 3) throw new ApiException(409, "Traffic lights only work on junctions with 3 or more roads");
                NetNode.Flags f = nm.m_nodes.m_buffer[id].m_flags | NetNode.Flags.CustomTrafficLights;
                if (enabled) f |= NetNode.Flags.TrafficLights; else f &= ~NetNode.Flags.TrafficLights;
                nm.m_nodes.m_buffer[id].m_flags = f;
                nm.UpdateNode((ushort)id);
                return new JsonWriter().Obj().Prop("ok", true).Prop("node", id).Prop("traffic_lights", (nm.m_nodes.m_buffer[id].m_flags & NetNode.Flags.TrafficLights) != 0)
                    .Prop("x", Math.Round(node.m_position.x, 0)).Prop("z", Math.Round(node.m_position.z, 0)).EndObj().ToString();
            }, 5000);
            return HttpResponse.Json(200, json);
        }
    }
}
