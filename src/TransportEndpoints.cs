using System;
using System.Collections.Generic;
using ColossalFramework;
using ColossalFramework.Math;
using UnityEngine;

namespace CSBridge
{
    /// <summary>
    /// Phase 7: public transport lines on streets (bus, tram, trolleybus). Stops are put on the nearest suitable
    /// road lane exactly like the game's TransportTool does; a line must be closed (loop) to run.
    /// Vehicles come from depot buildings (place e.g. "Bus Depot" with place_building).
    /// </summary>
    public static class TransportEndpoints
    {
        public static void Register(HttpServer server)
        {
            server.Get("/transport/types", Types);
            server.Get("/transport/lines", Lines);
            server.Post("/transport/line/create", CreateLine);
            server.Post("/transport/line/delete", DeleteLine);
            server.Post("/transport/line/set", SetLine);
        }

        private static bool StreetBased(TransportInfo info)
        {
            return info.m_transportType == TransportInfo.TransportType.Bus
                || info.m_transportType == TransportInfo.TransportType.Tram
                || info.m_transportType == TransportInfo.TransportType.Trolleybus;
        }

        private static TransportInfo FindInfo(string type)
        {
            TransportManager tm = Singleton<TransportManager>.instance;
            foreach (TransportInfo.TransportType t in Enum.GetValues(typeof(TransportInfo.TransportType)))
            {
                if (!string.Equals(t.ToString(), type, StringComparison.OrdinalIgnoreCase)) continue;
                TransportInfo info = tm.GetTransportInfo(t);
                if (info == null) throw new ApiException(404, "No transport info for " + t + " in this game");
                return info;
            }
            throw new ApiException(400, "Unknown transport type '" + type + "'. Street lines: Bus, Tram, Trolleybus (see GET /transport/types)");
        }

        // GET /transport/types
        private static HttpResponse Types(HttpRequest req)
        {
            string json = GameThread.Run<string>(delegate ()
            {
                TransportManager tm = Singleton<TransportManager>.instance;
                JsonWriter w = new JsonWriter().Obj().ArrIn("types");
                foreach (TransportInfo.TransportType t in Enum.GetValues(typeof(TransportInfo.TransportType)))
                {
                    TransportInfo info = tm.GetTransportInfo(t);
                    if (info == null) continue;
                    w.Obj().Prop("type", t.ToString()).Prop("street_based", StreetBased(info))
                        .Prop("unlocked", BuildEndpoints.IsUnlocked(info.GetUnlockMilestone()))
                        .Prop("unlock_milestone", BuildEndpoints.IsUnlocked(info.GetUnlockMilestone()) ? null : BuildEndpoints.MilestoneName(info.GetUnlockMilestone())).Prop("default_ticket_price", info.m_ticketPrice / 100.0).EndObj();
                }
                return w.EndArr().EndObj().ToString();
            }, 5000);
            return HttpResponse.Json(200, json);
        }

        private static void LineJson(JsonWriter w, TransportManager tm, int id)
        {
            TransportLine l = tm.m_lines.m_buffer[id];
            JsonWriter stops = new JsonWriter().Arr();
            NetManager nm = Singleton<NetManager>.instance;
            ushort first = l.m_stops, s = first;
            int guard = 0;
            while (s != 0 && guard++ < 256)
            {
                Vector3 p = nm.m_nodes.m_buffer[s].m_position;
                stops.Raw("[" + Math.Round(p.x, 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
                    Math.Round(p.z, 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "]");
                s = TransportLine.GetNextStop(s);
                if (s == first) break;
            }
            w.Obj().Prop("id", id).Prop("name", tm.GetLineName((ushort)id))
                .Prop("type", l.Info != null ? l.Info.m_transportType.ToString() : null)
                .Prop("number", l.m_lineNumber)
                .Prop("stops", l.CountStops((ushort)id)).Prop("complete", l.Complete)
                .Prop("vehicles", l.CountVehicles((ushort)id))
                .Prop("length", Math.Round(l.m_totalLength, 0))
                .Prop("passengers", (long)l.m_passengers.m_residentPassengers.m_finalCount + (long)l.m_passengers.m_touristPassengers.m_finalCount)
                .Prop("budget_percent", l.m_budget).Prop("ticket_price", l.m_ticketPrice / 100.0)
                .Prop("flags", l.m_flags.ToString())
                .Raw("\"stop_positions\":" + stops.EndArr().ToString());
            w.EndObj();
        }

        // GET /transport/lines -> all lines with stops, vehicles, length, passengers
        private static HttpResponse Lines(HttpRequest req)
        {
            string json = GameThread.Run<string>(delegate ()
            {
                TransportManager tm = Singleton<TransportManager>.instance;
                JsonWriter w = new JsonWriter().Obj().ArrIn("lines");
                for (int i = 1; i < tm.m_lines.m_buffer.Length; i++)
                {
                    TransportLine l = tm.m_lines.m_buffer[i];
                    if ((l.m_flags & TransportLine.Flags.Created) == TransportLine.Flags.None) continue;
                    if ((l.m_flags & TransportLine.Flags.Temporary) != TransportLine.Flags.None) continue;
                    LineJson(w, tm, i);
                }
                return w.EndArr().EndObj().ToString();
            }, 8000);
            return HttpResponse.Json(200, json);
        }

        /// <summary>Find the stop position on the road lane nearest to (x,z), as TransportTool.GetStopPosition does.</summary>
        private static bool StreetStop(TransportInfo info, Vector3 target, out Vector3 stopPos)
        {
            NetManager nm = Singleton<NetManager>.instance;
            stopPos = target;
            ushort bestSeg = 0;
            float bestD = 40f;
            for (int i = 1; i < nm.m_segments.m_buffer.Length; i++)
            {
                NetSegment s = nm.m_segments.m_buffer[i];
                if ((s.m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None || s.Info == null) continue;
                if ((s.Info.m_vehicleTypes & info.m_vehicleType) == VehicleInfo.VehicleType.None) continue;
                if ((s.m_flags & NetSegment.Flags.Untouchable) != NetSegment.Flags.None) continue;
                Vector3 a = nm.m_nodes.m_buffer[s.m_startNode].m_position, b = nm.m_nodes.m_buffer[s.m_endNode].m_position;
                if (Vector3.Distance(new Vector3((a.x + b.x) * 0.5f, 0, (a.z + b.z) * 0.5f), new Vector3(target.x, 0, target.z)) > s.m_averageLength * 0.5f + 45f) continue;
                Vector3 pos = s.GetClosestPosition(target);
                float d = Mathf.Sqrt((pos.x - target.x) * (pos.x - target.x) + (pos.z - target.z) * (pos.z - target.z));
                if (d < bestD) { bestD = d; bestSeg = (ushort)i; }
            }
            if (bestSeg == 0) return false;
            NetSegment seg = nm.m_segments.m_buffer[bestSeg];
            Vector3 p2; uint laneId2; int laneIdx2; float off2;
            if (!seg.GetClosestLanePosition(target, NetInfo.LaneType.Pedestrian, VehicleInfo.VehicleType.None, info.vehicleCategory, info.m_vehicleType, out p2, out laneId2, out laneIdx2, out off2))
                return false;
            Vector3 p3; uint laneId3; int laneIdx3; float off3;
            if (!seg.GetClosestLanePosition(p2, NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle, info.m_vehicleType, info.vehicleCategory, out p3, out laneId3, out laneIdx3, out off3))
                return false;
            NetInfo.Lane lane = seg.Info.m_lanes[laneIdx3];
            float stopOffset = lane.m_stopOffset;
            if ((seg.m_flags & NetSegment.Flags.Invert) != NetSegment.Flags.None) stopOffset = -stopOffset;
            Vector3 dir;
            nm.m_lanes.m_buffer[laneId3].CalculateStopPositionAndDirection(128f / 255f, stopOffset, out stopPos, out dir);
            return true;
        }

        /// <summary>Stop on a station building of this transport type (nearest to the target), like TransportTool for buildings.</summary>
        private static bool StationStop(TransportInfo info, Vector3 target, out Vector3 stopPos, out ushort buildingId)
        {
            BuildingManager bm = Singleton<BuildingManager>.instance;
            stopPos = target;
            buildingId = 0;
            float bestD = 150f;
            for (int i = 1; i < bm.m_buildings.m_buffer.Length; i++)
            {
                Building b = bm.m_buildings.m_buffer[i];
                if ((b.m_flags & Building.Flags.Created) == Building.Flags.None || b.Info == null) continue;
                if ((b.m_flags & Building.Flags.Untouchable) != Building.Flags.None) continue;
                TransportInfo t1 = b.Info.m_buildingAI.GetTransportLineInfo(), t2 = b.Info.m_buildingAI.GetSecondaryTransportLineInfo();
                bool match = (t1 != null && t1.m_transportType == info.m_transportType && t1.m_vehicleType == info.m_vehicleType)
                    || (t2 != null && t2.m_transportType == info.m_transportType && t2.m_vehicleType == info.m_vehicleType);
                if (!match) continue;
                float d = Mathf.Sqrt((b.m_position.x - target.x) * (b.m_position.x - target.x) + (b.m_position.z - target.z) * (b.m_position.z - target.z));
                if (d < bestD) { bestD = d; buildingId = (ushort)i; }
            }
            if (buildingId == 0) return false;
            Building bld = bm.m_buildings.m_buffer[buildingId];
            VehicleInfo vehicle = Singleton<VehicleManager>.instance.GetRandomVehicleInfo(ref Singleton<SimulationManager>.instance.m_randomizer,
                info.m_class.m_service, info.m_class.m_subService, info.m_class.m_level, info.m_vehicleType);
            if (vehicle == null) return false;
            bool found = false;
            Vector3 best = Vector3.zero;
            for (int i = 0; i < 12; i++)
            {
                Randomizer r = new Randomizer((ulong)i);
                Vector3 pos, tgt;
                bld.Info.m_buildingAI.CalculateSpawnPosition(buildingId, ref bm.m_buildings.m_buffer[buildingId], ref r, vehicle, out pos, out tgt);
                if (!found || Vector3.SqrMagnitude(pos - target) < Vector3.SqrMagnitude(best - target)) { best = pos; found = true; }
            }
            stopPos = best;
            return found;
        }

        // POST /transport/line/create {"type":"Bus","stops":"x,z;x,z;x,z","name":"Line 1"}
        // 2-32 stops, in order; the line is closed back to the first stop automatically.
        private static HttpResponse CreateLine(HttpRequest req)
        {
            Dictionary<string, string> body = JsonReader.ParseFlat(req.Body);
            string type;
            if (!body.TryGetValue("type", out type)) throw new ApiException(400, "Missing field: type (Bus, Tram, Trolleybus)");
            string ps;
            if (!body.TryGetValue("stops", out ps)) throw new ApiException(400, "Missing field: stops (\"x,z;x,z;...\")");
            List<Vector3> pts = new List<Vector3>();
            foreach (string pair in ps.Split(';'))
            {
                string t = pair.Trim();
                if (t.Length == 0) continue;
                string[] xz = t.Split(',');
                float x, z;
                if (xz.Length != 2 ||
                    !float.TryParse(xz[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x) ||
                    !float.TryParse(xz[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out z))
                    throw new ApiException(400, "stops must look like \"x,z;x,z;...\"");
                BuildEndpoints.CheckBounds(x, z);
                pts.Add(new Vector3(x, 0f, z));
            }
            if (pts.Count < 2 || pts.Count > 32) throw new ApiException(400, "A line needs 2-32 stops");
            string name = body.ContainsKey("name") ? body["name"] : null;

            string json = GameThread.Run<string>(delegate ()
            {
                TransportInfo info = FindInfo(type);
                bool street = StreetBased(info);
                if (info.m_transportType == TransportInfo.TransportType.Pedestrian || info.m_transportType == TransportInfo.TransportType.EvacuationBus ||
                    info.m_transportType == TransportInfo.TransportType.Taxi || info.m_transportType == TransportInfo.TransportType.Post)
                    throw new ApiException(400, type + " is not a player-drawn line type");
                if (!BuildEndpoints.IsUnlocked(info.GetUnlockMilestone())) throw new ApiException(409, type + " is not unlocked yet");
                TransportManager tm = Singleton<TransportManager>.instance;
                SimulationManager sim = Singleton<SimulationManager>.instance;

                List<Vector3> positions = new List<Vector3>();
                List<bool> stopFixed = new List<bool>();
                for (int i = 0; i < pts.Count; i++)
                {
                    Vector3 sp;
                    ushort stationBuilding = 0;
                    bool ok = street ? StreetStop(info, pts[i], out sp) : StationStop(info, pts[i], out sp, out stationBuilding);
                    if (!ok)
                        throw new ApiException(409, "Stop " + i + " at (" + pts[i].x + ", " + pts[i].z + "): " + (street
                            ? "no suitable road within 40 m (" + type + " needs a road it can drive on)"
                            : "no " + type + " station building within 150 m (place one with place_building first)"));
                    positions.Add(sp);
                    stopFixed.Add(street);
                }

                ushort lineId;
                if (!tm.CreateLine(out lineId, ref sim.m_randomizer, info, true))
                    throw new ApiException(409, "Cannot create more transport lines");
                string failure = null;
                for (int i = 0; i < positions.Count && failure == null; i++)
                {
                    if (!tm.m_lines.m_buffer[lineId].AddStop(lineId, i == 0 ? 0 : -1, positions[i], stopFixed[i]))
                        failure = "The game refused stop " + i + " (too close to the previous stop, or no route between the stops)";
                }
                if (failure == null && !tm.m_lines.m_buffer[lineId].AddStop(lineId, -1, positions[0], stopFixed[0]))
                    failure = "Could not close the line back to the first stop (no route back)";
                if (failure != null)
                {
                    tm.ReleaseLine(lineId);
                    throw new ApiException(409, failure);
                }
                tm.m_lines.m_buffer[lineId].SetActive(true, true);
                if (!string.IsNullOrEmpty(name))
                {
                    IEnumerator<bool> e = tm.SetLineName(lineId, name);
                    while (e.MoveNext()) { }
                }
                JsonWriter lw = new JsonWriter();
                LineJson(lw, tm, lineId);
                return new JsonWriter().Obj().Prop("ok", true).Raw("\"line\":" + lw.ToString()).EndObj().ToString();
            }, 15000);
            return HttpResponse.Json(200, json);
        }

        // POST /transport/line/delete {"line": 3}
        private static HttpResponse DeleteLine(HttpRequest req)
        {
            int id = JsonReader.GetInt(JsonReader.ParseFlat(req.Body), "line", 1, 255);
            string json = GameThread.Run<string>(delegate ()
            {
                TransportManager tm = Singleton<TransportManager>.instance;
                if ((tm.m_lines.m_buffer[id].m_flags & TransportLine.Flags.Created) == TransportLine.Flags.None) throw new ApiException(404, "No line " + id);
                tm.ReleaseLine((ushort)id);
                return new JsonWriter().Obj().Prop("ok", true).Prop("line", id).EndObj().ToString();
            }, 5000);
            return HttpResponse.Json(200, json);
        }

        // POST /transport/line/set {"line":3,"budget":120,"ticket_price":150,"active":true}  (any subset; budget % 50-150; ticket price in cents)
        private static HttpResponse SetLine(HttpRequest req)
        {
            Dictionary<string, string> body = JsonReader.ParseFlat(req.Body);
            int id = JsonReader.GetInt(body, "line", 1, 255);
            int budget = body.ContainsKey("budget") ? JsonReader.GetInt(body, "budget", 50, 150) : -1;
            int price = body.ContainsKey("ticket_price") ? JsonReader.GetInt(body, "ticket_price", 0, 10000) : -1;
            bool hasActive = body.ContainsKey("active");
            bool active = hasActive && JsonReader.GetBool(body, "active");
            string json = GameThread.Run<string>(delegate ()
            {
                TransportManager tm = Singleton<TransportManager>.instance;
                if ((tm.m_lines.m_buffer[id].m_flags & TransportLine.Flags.Created) == TransportLine.Flags.None) throw new ApiException(404, "No line " + id);
                if (budget >= 0) tm.m_lines.m_buffer[id].m_budget = (ushort)budget;
                if (price >= 0) tm.m_lines.m_buffer[id].m_ticketPrice = (ushort)price;
                if (hasActive) tm.m_lines.m_buffer[id].SetActive(active, active);
                JsonWriter lw = new JsonWriter();
                LineJson(lw, tm, id);
                return new JsonWriter().Obj().Prop("ok", true).Raw("\"line\":" + lw.ToString()).EndObj().ToString();
            }, 5000);
            return HttpResponse.Json(200, json);
        }
    }
}
