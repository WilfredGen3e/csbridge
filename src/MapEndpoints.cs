using System;
using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;

namespace CSBridge
{
    /// <summary>Phase 4: seeing the city. /map (roads, buildings, zones in an area) and /terrain (height, water).</summary>
    public static class MapEndpoints
    {
        public static void Register(HttpServer server)
        {
            server.Get("/map", Map);
            server.Get("/terrain", Terrain);
        }

        private static string Zn(float v) { return Math.Round(v, 1).ToString(System.Globalization.CultureInfo.InvariantCulture); }

        // GET /map                         -> summary (counts, bounding box of the built-up area)
        // GET /map?x=&z=&radius=&limit=    -> roads, buildings and zone cells in that circle (radius max 2000)
        private static HttpResponse Map(HttpRequest req)
        {
            Dictionary<string, string> q = BuildEndpoints.ParseQuery(req.Query);
            bool area = q.ContainsKey("x") || q.ContainsKey("z") || q.ContainsKey("radius");
            float cx = 0, cz = 0, radius = 0;
            if (area)
            {
                cx = BuildEndpoints.GetFloat(q, "x"); cz = BuildEndpoints.GetFloat(q, "z");
                radius = BuildEndpoints.GetFloat(q, "radius", 300f);
                if (radius <= 0f || radius > 2000f) throw new ApiException(400, "radius must be 1-2000");
                BuildEndpoints.CheckBounds(cx, cz);
            }
            int limit = BuildEndpoints.GetIntQ(q, "limit", 400, 1, 3000);
            bool virt = q.ContainsKey("include_virtual") && q["include_virtual"] != "0" && q["include_virtual"] != "false";
            bool zones = !q.ContainsKey("zones") || (q["zones"] != "0" && q["zones"] != "false");

            string json = GameThread.Run<string>(delegate ()
            {
                NetManager nm = Singleton<NetManager>.instance;
                BuildingManager bm = Singleton<BuildingManager>.instance;
                float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
                int segCount = 0, bldCount = 0;
                JsonWriter roads = new JsonWriter().Arr();
                JsonWriter blds = new JsonWriter().Arr();
                int roadsOut = 0, bldsOut = 0;

                for (int i = 1; i < nm.m_segments.m_buffer.Length; i++)
                {
                    NetSegment s = nm.m_segments.m_buffer[i];
                    if ((s.m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None) continue;
                    NetInfo info = s.Info;
                    if (info == null) continue;
                    // Skip the game's virtual helper networks (transport line paths, pedestrian links) unless asked for.
                    if (!virt && (info.m_netAI is TransportLineAI || info.name.StartsWith("Pedestrian Connection"))) continue;
                    Vector3 a = nm.m_nodes.m_buffer[s.m_startNode].m_position, b = nm.m_nodes.m_buffer[s.m_endNode].m_position;
                    segCount++;
                    if (!area || roadsOut >= limit) continue;
                    Vector3 mid = (a + b) * 0.5f;
                    float dx = mid.x - cx, dz = mid.z - cz;
                    if (dx * dx + dz * dz > (radius + s.m_averageLength) * (radius + s.m_averageLength)) continue;
                    roadsOut++;
                    roads.Obj().Prop("id", i).Prop("road", info.name)
                        .Prop("start_node", s.m_startNode).Prop("end_node", s.m_endNode)
                        .Prop("x1", Math.Round(a.x, 1)).Prop("z1", Math.Round(a.z, 1))
                        .Prop("x2", Math.Round(b.x, 1)).Prop("z2", Math.Round(b.z, 1))
                        .Prop("length", Math.Round(s.m_averageLength, 1)).EndObj();
                }

                for (int i = 1; i < bm.m_buildings.m_buffer.Length; i++)
                {
                    Building b = bm.m_buildings.m_buffer[i];
                    if ((b.m_flags & Building.Flags.Created) == Building.Flags.None) continue;
                    BuildingInfo info = b.Info;
                    if (info == null) continue;
                    if (info.m_class.m_service == ItemClass.Service.None) continue;
                    bldCount++;
                    Vector3 p = b.m_position;
                    minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                    minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z);
                    if (!area || bldsOut >= limit) continue;
                    float dx = p.x - cx, dz = p.z - cz;
                    if (dx * dx + dz * dz > radius * radius) continue;
                    bldsOut++;
                    blds.Obj().Prop("id", i).Prop("prefab", info.name).Prop("service", info.m_class.m_service.ToString())
                        .Prop("x", Math.Round(p.x, 1)).Prop("z", Math.Round(p.z, 1))
                        .Prop("angle_deg", Math.Round(b.m_angle * Mathf.Rad2Deg, 0))
                        .Prop("width", b.Width).Prop("length", b.Length).EndObj();
                }

                JsonWriter w = new JsonWriter().Obj();
                w.Prop("road_segment_count", segCount).Prop("building_count", bldCount);
                if (bldCount > 0)
                    w.ObjIn("bounds").Prop("min_x", Math.Round(minX, 0)).Prop("max_x", Math.Round(maxX, 0))
                        .Prop("min_z", Math.Round(minZ, 0)).Prop("max_z", Math.Round(maxZ, 0)).EndObj();
                if (area)
                {
                    w.Prop("center_x", cx).Prop("center_z", cz).Prop("radius", radius);
                    w.Raw("\"roads\":" + roads.EndArr().ToString());
                    w.Raw("\"buildings\":" + blds.EndArr().ToString());
                    w.Prop("roads_truncated", roadsOut >= limit).Prop("buildings_truncated", bldsOut >= limit);
                    if (zones) WriteZones(w, cx, cz, radius, limit * 4);
                }
                return w.EndObj().ToString();
            }, 15000);
            return HttpResponse.Json(200, json);
        }

        // Zone cells (8x8 m) inside the circle that are zoned, grouped as compact "x,z,zone" strings.
        private static void WriteZones(JsonWriter w, float cx, float cz, float radius, int maxCells)
        {
            ZoneManager zm = Singleton<ZoneManager>.instance;
            Dictionary<string, int> counts = new Dictionary<string, int>();
            JsonWriter cells = new JsonWriter().Arr();
            int n = 0, free = 0;
            int x0 = Mathf.Max((int)((cx - radius - 46f) / 64f + 75f), 0), z0 = Mathf.Max((int)((cz - radius - 46f) / 64f + 75f), 0);
            int x1 = Mathf.Min((int)((cx + radius + 46f) / 64f + 75f), 149), z1 = Mathf.Min((int)((cz + radius + 46f) / 64f + 75f), 149);
            for (int gz = z0; gz <= z1; gz++)
                for (int gx = x0; gx <= x1; gx++)
                {
                    ushort blk = zm.m_zoneGrid[gz * 150 + gx];
                    int guard = 0;
                    while (blk != 0 && guard++ < 49152)
                    {
                        ZoneBlock zb = zm.m_blocks.m_buffer[blk];
                        int rows = zb.RowCount;
                        float ca = Mathf.Cos(zb.m_angle) * 8f, sa = Mathf.Sin(zb.m_angle) * 8f;
                        for (int r = 0; r < rows; r++)
                            for (int c = 0; c < 4; c++)
                            {
                                float px = zb.m_position.x + (c - 3.5f) * ca + (r - 3.5f) * sa;
                                float pz = zb.m_position.z + (c - 3.5f) * sa - (r - 3.5f) * ca;
                                float dx = px - cx, dz = pz - cz;
                                if (dx * dx + dz * dz > radius * radius) continue;
                                ItemClass.Zone z = zb.GetZone(c, r);
                                if (z == ItemClass.Zone.Unzoned) { free++; continue; }
                                string zn = z.ToString();
                                int cnt; counts.TryGetValue(zn, out cnt); counts[zn] = cnt + 1;
                                if (n++ < maxCells) cells.Raw("[" + Zn(px) + "," + Zn(pz) + ",\"" + zn + "\"]");
                            }
                        blk = zb.m_nextGridBlock;
                    }
                }
            w.ObjIn("zones");
            w.Prop("unzoned_cells", free);
            w.ObjIn("zoned_counts");
            foreach (KeyValuePair<string, int> kv in counts) w.Prop(kv.Key, kv.Value);
            w.EndObj();
            w.Raw("\"cells\":" + cells.EndArr().ToString());
            w.EndObj();
        }

        // GET /terrain?x=&z=   -> ground height, water height and whether it is water
        private static HttpResponse Terrain(HttpRequest req)
        {
            Dictionary<string, string> q = BuildEndpoints.ParseQuery(req.Query);
            float x = BuildEndpoints.GetFloat(q, "x"), z = BuildEndpoints.GetFloat(q, "z");
            BuildEndpoints.CheckBounds(x, z);
            string json = GameThread.Run<string>(delegate ()
            {
                TerrainManager tm = Singleton<TerrainManager>.instance;
                Vector3 p = new Vector3(x, 0f, z);
                float ground = tm.SampleRawHeightSmooth(p);
                float water = tm.WaterLevel(new Vector2(x, z));
                return new JsonWriter().Obj().Prop("x", x).Prop("z", z)
                    .Prop("ground_height", Math.Round(ground, 1)).Prop("water_height", Math.Round(water, 1))
                    .Prop("is_water", water > ground + 0.5f).EndObj().ToString();
            }, 3000);
            return HttpResponse.Json(200, json);
        }
    }
}
