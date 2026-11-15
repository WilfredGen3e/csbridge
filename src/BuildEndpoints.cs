using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using ColossalFramework;
using ColossalFramework.Math;
using UnityEngine;

namespace CSBridge
{
    /// <summary>Phase 4: prefabs, placing and bulldozing buildings. Validation mirrors the game's BuildingTool.</summary>
    public static class BuildEndpoints
    {
        public static void Register(HttpServer server)
        {
            server.Get("/prefabs", Prefabs);
            server.Post("/building/place", PlaceBuilding);
            server.Post("/building/bulldoze", BulldozeBuilding);
        }

        // ---- query helpers -------------------------------------------------------------

        public static Dictionary<string, string> ParseQuery(string query)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query)) return d;
            foreach (string pair in query.Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                string k = eq >= 0 ? pair.Substring(0, eq) : pair;
                string v = eq >= 0 ? pair.Substring(eq + 1) : "";
                d[Uri.UnescapeDataString(k)] = Uri.UnescapeDataString(v.Replace('+', ' '));
            }
            return d;
        }

        public static float GetFloat(Dictionary<string, string> d, string key)
        {
            string s;
            if (!d.TryGetValue(key, out s)) throw new ApiException(400, "Missing field: " + key);
            float f;
            if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f) || float.IsNaN(f) || float.IsInfinity(f))
                throw new ApiException(400, "Field '" + key + "' must be a number");
            return f;
        }

        public static float GetFloat(Dictionary<string, string> d, string key, float fallback)
        {
            return d.ContainsKey(key) ? GetFloat(d, key) : fallback;
        }

        public static int GetIntQ(Dictionary<string, string> d, string key, int fallback, int min, int max)
        {
            string s;
            if (!d.TryGetValue(key, out s)) return fallback;
            int v;
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) || v < min || v > max)
                throw new ApiException(400, "Field '" + key + "' must be an integer " + min + "-" + max);
            return v;
        }

        /// <summary>The playable map is 17.28 km wide: coordinates run from -8640 to 8640.</summary>
        public static void CheckBounds(float x, float z)
        {
            if (Math.Abs(x) > 8640f || Math.Abs(z) > 8640f)
                throw new ApiException(400, "Position outside the map (x and z must be within -8640..8640)");
        }

        // ---- prefabs -------------------------------------------------------------------

        public static BuildingInfo FindBuilding(string name)
        {
            BuildingInfo partial = null;
            int partialCount = 0;
            int n = PrefabCollection<BuildingInfo>.LoadedCount();
            for (int i = 0; i < n; i++)
            {
                BuildingInfo info = PrefabCollection<BuildingInfo>.GetLoaded((uint)i);
                if (info == null || info.name == null) continue;
                if (string.Equals(info.name, name, StringComparison.OrdinalIgnoreCase)) return info;
                if (info.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) { partial = info; partialCount++; }
            }
            if (partialCount == 1) return partial;
            if (partialCount > 1) throw new ApiException(400, "Prefab name '" + name + "' is ambiguous (" + partialCount + " matches); use the exact name from /prefabs");
            throw new ApiException(404, "No building prefab '" + name + "'. Use /prefabs?kind=building&filter=...");
        }

        public static NetInfo FindNet(string name)
        {
            NetInfo partial = null;
            int partialCount = 0;
            int n = PrefabCollection<NetInfo>.LoadedCount();
            for (int i = 0; i < n; i++)
            {
                NetInfo info = PrefabCollection<NetInfo>.GetLoaded((uint)i);
                if (info == null || info.name == null) continue;
                if (string.Equals(info.name, name, StringComparison.OrdinalIgnoreCase)) return info;
                if (info.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) { partial = info; partialCount++; }
            }
            if (partialCount == 1) return partial;
            if (partialCount > 1) throw new ApiException(400, "Road name '" + name + "' is ambiguous (" + partialCount + " matches); use the exact name from /prefabs?kind=road");
            throw new ApiException(404, "No road prefab '" + name + "'. Use /prefabs?kind=road&filter=...");
        }

        private static bool IsUnlocked(MilestoneInfo m)
        {
            return m == null || Singleton<UnlockManager>.instance.Unlocked(m);
        }

        // GET /prefabs?kind=building|road&filter=text&service=Name&unlocked=1&limit=50
        private static HttpResponse Prefabs(HttpRequest req)
        {
            Dictionary<string, string> q = ParseQuery(req.Query);
            string kind = q.ContainsKey("kind") ? q["kind"].ToLowerInvariant() : "building";
            if (kind != "building" && kind != "road") throw new ApiException(400, "kind must be 'building' or 'road'");
            string filter = q.ContainsKey("filter") ? q["filter"] : null;
            string service = q.ContainsKey("service") ? q["service"] : null;
            bool onlyUnlocked = q.ContainsKey("unlocked") && q["unlocked"] != "0" && q["unlocked"] != "false";
            int limit = GetIntQ(q, "limit", 50, 1, 500);

            string json = GameThread.Run<string>(delegate ()
            {
                JsonWriter w = new JsonWriter().Obj();
                int count = 0, total = 0;
                w.ArrIn("prefabs");
                if (kind == "building")
                {
                    int n = PrefabCollection<BuildingInfo>.LoadedCount();
                    for (int i = 0; i < n; i++)
                    {
                        BuildingInfo b = PrefabCollection<BuildingInfo>.GetLoaded((uint)i);
                        if (b == null || b.name == null || b.m_class == null) continue;
                        if (filter != null && b.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (service != null && !string.Equals(b.m_class.m_service.ToString(), service, StringComparison.OrdinalIgnoreCase)) continue;
                        bool unlocked = IsUnlocked(b.GetUnlockMilestone());
                        if (onlyUnlocked && !unlocked) continue;
                        total++;
                        if (count >= limit) continue;
                        count++;
                        int cost = 0;
                        try { cost = b.m_buildingAI.GetConstructionCost() / 100; } catch (Exception) { }
                        w.Obj().Prop("name", b.name)
                            .Prop("service", b.m_class.m_service.ToString())
                            .Prop("subservice", b.m_class.m_subService.ToString())
                            .Prop("level", b.m_class.m_level.ToString())
                            .Prop("width", b.m_cellWidth).Prop("length", b.m_cellLength)
                            .Prop("placement", b.m_placementMode.ToString())
                            .Prop("cost", cost)
                            .Prop("unlocked", unlocked)
                            .EndObj();
                    }
                }
                else
                {
                    int n = PrefabCollection<NetInfo>.LoadedCount();
                    for (int i = 0; i < n; i++)
                    {
                        NetInfo r = PrefabCollection<NetInfo>.GetLoaded((uint)i);
                        if (r == null || r.name == null || r.m_class == null) continue;
                        if (filter != null && r.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (service != null && !string.Equals(r.m_class.m_service.ToString(), service, StringComparison.OrdinalIgnoreCase)) continue;
                        bool unlocked = IsUnlocked(r.GetUnlockMilestone());
                        if (onlyUnlocked && !unlocked) continue;
                        total++;
                        if (count >= limit) continue;
                        count++;
                        int minE, maxE;
                        r.m_netAI.GetElevationLimits(out minE, out maxE);
                        w.Obj().Prop("name", r.name)
                            .Prop("service", r.m_class.m_service.ToString())
                            .Prop("subservice", r.m_class.m_subService.ToString())
                            .Prop("width", r.m_halfWidth * 2f)
                            .Prop("one_way", r.m_hasBackwardVehicleLanes != r.m_hasForwardVehicleLanes)
                            .Prop("can_bridge_or_tunnel", minE != maxE)
                            .Prop("unlocked", unlocked)
                            .EndObj();
                    }
                }
                w.EndArr();
                w.Prop("returned", count).Prop("matching", total);
                return w.EndObj().ToString();
            }, 5000);
            return HttpResponse.Json(200, json);
        }

        // ---- placing buildings ---------------------------------------------------------

        private static readonly MethodInfo FindClosestZone = typeof(BuildingTool).GetMethod(
            "FindClosestZone", BindingFlags.Instance | BindingFlags.NonPublic);

        // POST /building/place  {"prefab":"Police Station","x":100,"z":-200,"angle":0}  (angle in degrees, optional)
        private static HttpResponse PlaceBuilding(HttpRequest req)
        {
            Dictionary<string, string> body = JsonReader.ParseFlat(req.Body);
            string name;
            if (!body.TryGetValue("prefab", out name) || name.Length == 0) throw new ApiException(400, "Missing field: prefab");
            float x = GetFloat(body, "x"), z = GetFloat(body, "z");
            float angleDeg = GetFloat(body, "angle", 0f);
            CheckBounds(x, z);

            string json = GameThread.Run<string>(delegate ()
            {
                BuildingInfo info = FindBuilding(name);
                if (!IsUnlocked(info.GetUnlockMilestone()))
                    throw new ApiException(409, "'" + info.name + "' is not unlocked yet");
                if (info.m_buildingAI.WorksAsNet())
                    throw new ApiException(400, "'" + info.name + "' is a network-like building and cannot be placed with this endpoint");

                Vector3 pos = new Vector3(x, 0f, z);
                pos.y = Singleton<TerrainManager>.instance.SampleRawHeightSmoothWithWater(pos, false, 0f);
                float angle = angleDeg * Mathf.Deg2Rad;

                if (info.m_placementMode == BuildingInfo.PlacementMode.Roadside)
                {
                    // Like the game: snap to the nearest zone block (i.e. a road side) within 8 m of the target.
                    float minD = 8f, min2 = 1000000f;
                    Vector3 snapPos = pos;
                    float snapAngle = angle;
                    ZoneManager zm = Singleton<ZoneManager>.instance;
                    BuildingTool tool = ToolsModifierControl.GetTool<BuildingTool>();
                    int x0 = Mathf.Max((int)((x - 8f - 46f) / 64f + 75f), 0), z0 = Mathf.Max((int)((z - 8f - 46f) / 64f + 75f), 0);
                    int x1 = Mathf.Min((int)((x + 8f + 46f) / 64f + 75f), 149), z1 = Mathf.Min((int)((z + 8f + 46f) / 64f + 75f), 149);
                    for (int gz = z0; gz <= z1; gz++)
                    {
                        for (int gx = x0; gx <= x1; gx++)
                        {
                            ushort blk = zm.m_zoneGrid[gz * 150 + gx];
                            int guard = 0;
                            while (blk != 0 && guard++ < 49152)
                            {
                                object[] args = new object[] { info, blk, pos, minD, min2, snapPos, snapAngle };
                                FindClosestZone.Invoke(tool, args);
                                minD = (float)args[3]; min2 = (float)args[4]; snapPos = (Vector3)args[5]; snapAngle = (float)args[6];
                                blk = zm.m_blocks.m_buffer[blk].m_nextGridBlock;
                            }
                        }
                    }
                    if (minD >= 8f)
                        throw new ApiException(409, "'" + info.name + "' must be placed beside a road: no road side within 8 m of (" + x + ", " + z + ")");
                    pos = snapPos; angle = snapAngle;
                }

                float minY, maxY, buildingY, waterH = 0f;
                Building.SampleBuildingHeight(pos, angle, info.m_cellWidth, info.m_cellLength, info, out minY, out maxY, out buildingY, ref waterH);
                pos.y = buildingY;

                Segment3 connection = default(Segment3);
                int production, cost;
                ToolBase.ToolErrors err = info.m_buildingAI.CheckBuildPosition(0, ref pos, ref angle, waterH, 0f, ref connection, out production, out cost);
                err |= BuildingTool.CheckSpace(info, info.m_placementMode, 0, pos, minY, buildingY + info.m_collisionHeight, angle,
                    info.m_cellWidth, info.m_cellLength, true, new ulong[1024], new ulong[1024]);
                if (maxY - minY > info.m_maxHeightOffset) err |= ToolBase.ToolErrors.SlopeTooSteep;
                if (err != ToolBase.ToolErrors.None)
                    throw new ApiException(409, "Cannot place '" + info.name + "' there: " + err);

                SimulationManager sim = Singleton<SimulationManager>.instance;
                if (cost > 0 && Singleton<EconomyManager>.instance.PeekResource(EconomyManager.Resource.Construction, cost) != cost)
                    throw new ApiException(409, "Not enough money (cost " + (cost / 100) + ")");
                ushort id;
                if (!Singleton<BuildingManager>.instance.CreateBuilding(out id, ref sim.m_randomizer, info, pos, angle, 0, sim.m_currentBuildIndex))
                    throw new ApiException(409, "The game refused to create '" + info.name + "' (too many buildings or no room)");
                sim.m_currentBuildIndex++;
                if (cost > 0) Singleton<EconomyManager>.instance.FetchResource(EconomyManager.Resource.Construction, cost, info.m_class);
                info.m_buildingAI.PlacementSucceeded();

                return new JsonWriter().Obj().Prop("ok", true).Prop("id", id).Prop("prefab", info.name)
                    .Prop("x", pos.x).Prop("z", pos.z).Prop("angle_deg", angle * Mathf.Rad2Deg).Prop("cost", cost / 100)
                    .EndObj().ToString();
            }, 8000);
            return HttpResponse.Json(200, json);
        }

        // POST /building/bulldoze  {"id": 1234}
        private static HttpResponse BulldozeBuilding(HttpRequest req)
        {
            Dictionary<string, string> body = JsonReader.ParseFlat(req.Body);
            int id = JsonReader.GetInt(body, "id", 1, 49151);
            string json = GameThread.Run<string>(delegate ()
            {
                BuildingManager bm = Singleton<BuildingManager>.instance;
                Building b = bm.m_buildings.m_buffer[id];
                if ((b.m_flags & Building.Flags.Created) == Building.Flags.None)
                    throw new ApiException(404, "No building with id " + id);
                if ((b.m_flags & Building.Flags.Untouchable) != Building.Flags.None)
                    throw new ApiException(409, "Building " + id + " is part of a larger structure and cannot be bulldozed on its own");
                string prefab = b.Info != null ? b.Info.name : null;
                bm.ReleaseBuilding((ushort)id);
                return new JsonWriter().Obj().Prop("ok", true).Prop("id", id).Prop("prefab", prefab).EndObj().ToString();
            }, 5000);
            return HttpResponse.Json(200, json);
        }
    }
}
