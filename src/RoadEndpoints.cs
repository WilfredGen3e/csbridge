using System;
using System.Collections.Generic;
using ColossalFramework;
using ColossalFramework.Math;
using UnityEngine;

namespace CSBridge
{
    /// <summary>
    /// Phase 4: roads and other networks via the game's own NetTool.CreateNode (snapping to existing nodes,
    /// splitting segments at junctions, validation and cost handled by the game).
    /// </summary>
    public static class RoadEndpoints
    {
        public static void Register(HttpServer server)
        {
            server.Post("/road/build", Build);
            server.Post("/road/upgrade", Upgrade);
            server.Post("/road/bulldoze", Bulldoze);
        }

        private static readonly FastList<NetTool.NodePosition> NodeBuffer = new FastList<NetTool.NodePosition>();

        private struct Snap
        {
            public ushort Node, Segment;
            public Vector3 Pos;
            public Vector3 Dir;
        }

        private static Vector3 Ground(float x, float z)
        {
            Vector3 p = new Vector3(x, 0f, z);
            p.y = Singleton<TerrainManager>.instance.SampleRawHeightSmoothWithWater(p, false, 0f);
            return p;
        }

        private static float Dist2D(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>Find an existing node (preferred) or segment of the same service close to the point.</summary>
        private static Snap FindSnap(NetInfo info, Vector3 p, float radius)
        {
            NetManager nm = Singleton<NetManager>.instance;
            Snap best = new Snap();
            best.Pos = p;
            float bestD = radius;
            for (int i = 1; i < nm.m_nodes.m_buffer.Length; i++)
            {
                NetNode n = nm.m_nodes.m_buffer[i];
                if ((n.m_flags & NetNode.Flags.Created) == NetNode.Flags.None || n.Info == null) continue;
                if (n.Info.m_class.m_service != info.m_class.m_service) continue;
                // Station/building nodes are untouchable, except free ends (0 or 1 segments), e.g. station track ends and the pipe connection of water pumps.
                if ((n.m_flags & NetNode.Flags.Untouchable) != NetNode.Flags.None && n.CountSegments() > 1) continue;
                float d = Dist2D(n.m_position, p);
                if (d < bestD) { bestD = d; best.Node = (ushort)i; best.Segment = 0; best.Pos = n.m_position; }
            }
            if (best.Node != 0) return best;
            bestD = radius;
            for (int i = 1; i < nm.m_segments.m_buffer.Length; i++)
            {
                NetSegment s = nm.m_segments.m_buffer[i];
                if ((s.m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None || s.Info == null) continue;
                if (s.Info.m_class.m_service != info.m_class.m_service) continue;
                if ((s.m_flags & NetSegment.Flags.Untouchable) != NetSegment.Flags.None) continue;
                Vector3 a = nm.m_nodes.m_buffer[s.m_startNode].m_position, b = nm.m_nodes.m_buffer[s.m_endNode].m_position;
                if (Dist2D((a + b) * 0.5f, p) > s.m_averageLength * 0.5f + radius + 4f) continue;
                Vector3 pos, dir;
                s.GetClosestPositionAndDirection(p, out pos, out dir);
                float d = Dist2D(pos, p);
                if (d < bestD) { bestD = d; best.Node = 0; best.Segment = (ushort)i; best.Pos = pos; best.Dir = dir; }
            }
            return best;
        }

        private static NetTool.ControlPoint MakePoint(Snap s, Vector3 dir, float elevation)
        {
            NetTool.ControlPoint cp = new NetTool.ControlPoint();
            cp.m_position = s.Pos;
            cp.m_node = s.Node;
            cp.m_segment = s.Segment;
            cp.m_elevation = elevation;
            // Like the game's own tool: a point snapped to an existing node takes that node's height and elevation
            // (needed to join underground station tracks, which lie at -12 m, from a point given without elevation).
            if (s.Node != 0)
            {
                NetNode n = Singleton<NetManager>.instance.m_nodes.m_buffer[s.Node];
                cp.m_elevation = n.m_elevation;
                if (n.Info != null && n.Info.m_netAI != null && n.Info.m_netAI.IsUnderground()) cp.m_elevation = -cp.m_elevation;
                cp.m_position = n.m_position;
            }
            cp.m_direction = dir;
            return cp;
        }

        private static List<Vector3> ParsePoints(string s, List<float> elevations, float defaultElevation)
        {
            List<Vector3> pts = new List<Vector3>();
            foreach (string pair in s.Split(';'))
            {
                string t = pair.Trim();
                if (t.Length == 0) continue;
                string[] xz = t.Split(',');
                float x, z, e = defaultElevation;
                if ((xz.Length != 2 && xz.Length != 3) ||
                    !float.TryParse(xz[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x) ||
                    !float.TryParse(xz[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out z) ||
                    (xz.Length == 3 && !float.TryParse(xz[2].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out e)))
                    throw new ApiException(400, "points must look like \"x,z;x,z;...\" (optionally \"x,z,elevation\")");
                BuildEndpoints.CheckBounds(x, z);
                pts.Add(Ground(x, z));
                elevations.Add(e);
            }
            return pts;
        }

        // Objects the last CreateNode test pass found in the way (the game would remove them on a real build,
        // or refuse with ObjectCollision when they are important). Read from the tool controller's private masks.
        internal static List<string> CollidingObjects()
        {
            List<string> list = new List<string>();
            try
            {
                ToolController tc = Singleton<ToolManager>.instance.m_properties;
                System.Reflection.BindingFlags f = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                ulong[] segs = (ulong[])typeof(ToolController).GetField("m_collidingSegments2", f).GetValue(tc);
                ulong[] blds = (ulong[])typeof(ToolController).GetField("m_collidingBuildings2", f).GetValue(tc);
                NetManager nm = Singleton<NetManager>.instance;
                BuildingManager bm = Singleton<BuildingManager>.instance;
                for (int i = 0; i < segs.Length && list.Count < 20; i++)
                {
                    if (segs[i] == 0) continue;
                    for (int j = 0; j < 64; j++)
                    {
                        if ((segs[i] & (ulong)(1L << j)) == 0) continue;
                        int id = (i << 6) | j;
                        if (id == 0 || id >= nm.m_segments.m_buffer.Length || (nm.m_segments.m_buffer[id].m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None) continue;
                        NetInfo ni = nm.m_segments.m_buffer[id].Info;
                        list.Add("segment " + id + " (" + (ni != null ? ni.name : "?") + ")");
                    }
                }
                for (int i = 0; i < blds.Length && list.Count < 20; i++)
                {
                    if (blds[i] == 0) continue;
                    for (int j = 0; j < 64; j++)
                    {
                        if ((blds[i] & (ulong)(1L << j)) == 0) continue;
                        int id = (i << 6) | j;
                        if (id == 0 || id >= bm.m_buildings.m_buffer.Length || (bm.m_buildings.m_buffer[id].m_flags & Building.Flags.Created) == Building.Flags.None) continue;
                        BuildingInfo bi = bm.m_buildings.m_buffer[id].Info;
                        Vector3 pos = bm.m_buildings.m_buffer[id].m_position;
                        list.Add("building " + id + " (" + (bi != null ? bi.name : "?") + " at " + (int)pos.x + "," + (int)pos.z + ")");
                    }
                }
            }
            catch (Exception ex) { BridgeLog.Error("CollidingObjects", ex); }
            return list;
        }

        // POST /road/build
        //   {"road":"Basic Road","points":"x,z;x,z;x,z", "curve":"cx,cz" (only with 2 points), "elevation":0, "reverse":false, "snap":8}
        // or {"road":..., "x1":..,"z1":..,"x2":..,"z2":..,"cx":..,"cz":..}
        // Each pair of consecutive points becomes one segment; ends snap to existing nodes/segments within `snap` metres.
        private static HttpResponse Build(HttpRequest req)
        {
            Dictionary<string, string> body = JsonReader.ParseFlat(req.Body);
            string name;
            if (!body.TryGetValue("road", out name) || name.Length == 0) throw new ApiException(400, "Missing field: road");
            List<Vector3> pts;
            List<float> elevs = new List<float>();
            float elevation = BuildEndpoints.GetFloat(body, "elevation", 0f);
            string ps;
            if (body.TryGetValue("points", out ps)) pts = ParsePoints(ps, elevs, elevation);
            else
            {
                float x1 = BuildEndpoints.GetFloat(body, "x1"), z1 = BuildEndpoints.GetFloat(body, "z1");
                float x2 = BuildEndpoints.GetFloat(body, "x2"), z2 = BuildEndpoints.GetFloat(body, "z2");
                BuildEndpoints.CheckBounds(x1, z1); BuildEndpoints.CheckBounds(x2, z2);
                pts = new List<Vector3>();
                pts.Add(Ground(x1, z1)); pts.Add(Ground(x2, z2));
                elevs.Add(elevation); elevs.Add(elevation);
            }
            if (pts.Count < 2) throw new ApiException(400, "Need at least 2 points");
            if (pts.Count > 64) throw new ApiException(400, "At most 64 points per call");

            bool hasCurve = false;
            Vector3 curve = Vector3.zero;
            string cs;
            if (body.TryGetValue("curve", out cs))
            {
                string[] xz = cs.Split(',');
                float cx, cz;
                if (xz.Length != 2 || !float.TryParse(xz[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out cx) ||
                    !float.TryParse(xz[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out cz))
                    throw new ApiException(400, "curve must look like \"x,z\"");
                curve = Ground(cx, cz); hasCurve = true;
            }
            else if (body.ContainsKey("cx") && body.ContainsKey("cz"))
            {
                curve = Ground(BuildEndpoints.GetFloat(body, "cx"), BuildEndpoints.GetFloat(body, "cz")); hasCurve = true;
            }
            if (hasCurve && pts.Count != 2) throw new ApiException(400, "A curve control point only works with exactly 2 points");
            bool reverse = body.ContainsKey("reverse") && JsonReader.GetBool(body, "reverse");
            float snap = BuildEndpoints.GetFloat(body, "snap", 8f);
            if (snap < 0f || snap > 30f) throw new ApiException(400, "snap must be 0-30");
            bool dryRun = body.ContainsKey("dry_run") && JsonReader.GetBool(body, "dry_run");
            bool verbose = body.ContainsKey("verbose") && JsonReader.GetBool(body, "verbose");   // per-segment details (cost, snapping, end node)

            string json = GameThread.Run<string>(delegate ()
            {
                NetInfo info = BuildEndpoints.FindNet(name);
                if (!BuildEndpoints.IsUnlocked(info.GetUnlockMilestone()))
                    throw new ApiException(409, "'" + info.name + "' is not unlocked yet");
                JsonWriter w = new JsonWriter().Obj();
                JsonWriter segs = new JsonWriter().Arr();
                int built = 0;
                long totalCost = 0;
                string failure = null;
                List<string> removed = new List<string>();
                for (int i = 0; i + 1 < pts.Count; i++)
                {
                    Snap a = FindSnap(info, pts[i], snap), b = FindSnap(info, pts[i + 1], snap);
                    Vector3 dir = pts[i + 1] - pts[i]; dir.y = 0f;
                    if (dir.sqrMagnitude < 1f) { failure = "segment " + i + ": points are too close together"; break; }
                    dir.Normalize();
                    float e1 = elevs[i], e2 = elevs[i + 1];
                    NetTool.ControlPoint sp = MakePoint(a, dir, e1);
                    NetTool.ControlPoint ep = MakePoint(b, dir, e2);
                    NetTool.ControlPoint mp = ep;
                    mp.m_node = 0; mp.m_segment = 0;
                    mp.m_position = hasCurve ? curve : (sp.m_position + ep.m_position) * 0.5f;
                    mp.m_elevation = (e1 + e2) * 0.5f;

                    ushort node, seg; int cost, prod;
                    ToolBase.ToolErrors err = NetTool.CreateNode(info, sp, mp, ep, NodeBuffer, 1000, true, false, true, true, reverse, false, 0, out node, out seg, out cost, out prod);
                    // The test pass leaves the objects in the way in the tool's collision masks: report them by id.
                    List<string> hit = CollidingObjects();
                    if (err != ToolBase.ToolErrors.None)
                    {
                        failure = "segment " + i + ": " + err + (hit.Count > 0 ? " (in the way: " + string.Join(", ", hit.ToArray()) + ")" : "");
                        break;
                    }
                    foreach (string h in hit) removed.Add(h);
                    if (dryRun) { built++; totalCost += cost / 100; continue; }
                    err = NetTool.CreateNode(info, sp, mp, ep, NodeBuffer, 1000, false, false, true, true, reverse, false, 0, out node, out seg, out cost, out prod);
                    if (err != ToolBase.ToolErrors.None) { failure = "segment " + i + ": " + err; break; }
                    built++;
                    totalCost += cost / 100;
                    if (!verbose) { segs.Raw(seg.ToString()); continue; }
                    segs.Obj().Prop("segment", seg).Prop("end_node", node)
                        .Prop("snapped_start", a.Node != 0 ? "node" : (a.Segment != 0 ? "segment" : "none"))
                        .Prop("snapped_end", b.Node != 0 ? "node" : (b.Segment != 0 ? "segment" : "none"))
                        .Prop("cost", cost / 100).EndObj();
                }
                w.Prop("ok", failure == null).Prop("road", info.name).Prop(dryRun ? "would_build" : "built", built).Prop("cost", totalCost);
                if (dryRun) w.Prop("dry_run", true);
                w.ArrIn(dryRun ? "would_remove" : "removed");
                foreach (string r in removed) w.Str(r);
                w.EndArr();
                w.Raw("\"segments\":" + segs.EndArr().ToString());
                if (failure != null) w.Prop("error", failure);
                string result = w.EndObj().ToString();
                if (failure != null && built == 0) throw new ApiException(409, "Cannot build road: " + failure);
                return result;
            }, 15000);
            return HttpResponse.Json(200, json);
        }

        // POST /road/upgrade {"segment": 123, "road": "Medium Road"}  -> changes the road type of an existing segment
        private static HttpResponse Upgrade(HttpRequest req)
        {
            Dictionary<string, string> body = JsonReader.ParseFlat(req.Body);
            int id = JsonReader.GetInt(body, "segment", 1, 36863);
            string name;
            if (!body.TryGetValue("road", out name) || name.Length == 0) throw new ApiException(400, "Missing field: road");
            bool reverse = body.ContainsKey("reverse") && JsonReader.GetBool(body, "reverse");
            string json = GameThread.Run<string>(delegate ()
            {
                NetInfo info = BuildEndpoints.FindNet(name);
                NetManager nm = Singleton<NetManager>.instance;
                NetSegment s = nm.m_segments.m_buffer[id];
                if ((s.m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None) throw new ApiException(404, "No road segment " + id);
                NetNode a = nm.m_nodes.m_buffer[s.m_startNode], b = nm.m_nodes.m_buffer[s.m_endNode];
                NetTool.ControlPoint sp = new NetTool.ControlPoint(), ep = new NetTool.ControlPoint(), mp = new NetTool.ControlPoint();
                sp.m_node = s.m_startNode; sp.m_position = a.m_position; sp.m_direction = s.m_startDirection;
                ep.m_node = s.m_endNode; ep.m_position = b.m_position; ep.m_direction = -s.m_endDirection;
                mp.m_segment = (ushort)id; mp.m_position = (a.m_position + b.m_position) * 0.5f;
                ushort node, seg; int cost, prod;
                ToolBase.ToolErrors err = NetTool.CreateNode(info, sp, mp, ep, NodeBuffer, 1000, true, false, true, true, reverse, false, 0, out node, out seg, out cost, out prod);
                if (err != ToolBase.ToolErrors.None) throw new ApiException(409, "Cannot upgrade segment " + id + " to '" + info.name + "': " + err);
                err = NetTool.CreateNode(info, sp, mp, ep, NodeBuffer, 1000, false, false, true, true, reverse, false, 0, out node, out seg, out cost, out prod);
                if (err != ToolBase.ToolErrors.None) throw new ApiException(409, "Cannot upgrade segment " + id + ": " + err);
                return new JsonWriter().Obj().Prop("ok", true).Prop("segment", seg).Prop("road", info.name).Prop("cost", cost / 100).EndObj().ToString();
            }, 8000);
            return HttpResponse.Json(200, json);
        }

        // POST /road/bulldoze {"segment": 123}
        private static HttpResponse Bulldoze(HttpRequest req)
        {
            Dictionary<string, string> body = JsonReader.ParseFlat(req.Body);
            int id = JsonReader.GetInt(body, "segment", 1, 36863);
            string json = GameThread.Run<string>(delegate ()
            {
                NetManager nm = Singleton<NetManager>.instance;
                NetSegment s = nm.m_segments.m_buffer[id];
                if ((s.m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None) throw new ApiException(404, "No road segment " + id);
                if ((s.m_flags & NetSegment.Flags.Untouchable) != NetSegment.Flags.None)
                    throw new ApiException(409, "Segment " + id + " belongs to a building or map feature and cannot be bulldozed");
                string road = s.Info != null ? s.Info.name : null;
                nm.ReleaseSegment((ushort)id, false);
                return new JsonWriter().Obj().Prop("ok", true).Prop("segment", id).Prop("road", road).EndObj().ToString();
            }, 5000);
            return HttpResponse.Json(200, json);
        }
    }
}
