using System;
using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;

namespace CSBridge
{
    /// <summary>
    /// Park areas (Parklife DLC and relatives: parks, zoo, amusement park, nature reserve, industry areas, campus areas) and their
    /// policies. A park area is painted like a district; its type decides which buildings and policies belong to it.
    /// Airport areas are not supported (the game paints them with a terrain brush).
    /// </summary>
    public static class ParkEndpoints
    {
        public static void Register(HttpServer server)
        {
            server.Get("/parks", List);
            server.Post("/park/create", Create);
            server.Post("/park/paint", Paint);
            server.Post("/park/delete", Delete);
        }

        private static bool Exists(DistrictManager dm, int id)
        {
            return id > 0 && id < dm.m_parks.m_buffer.Length && dm.m_parks.m_buffer[id].m_flags != DistrictPark.Flags.None && (dm.m_parks.m_buffer[id].m_flags & DistrictPark.Flags.Created) != 0;
        }

        private static DistrictPark.ParkType ParseType(string name)
        {
            foreach (DistrictPark.ParkType t in Enum.GetValues(typeof(DistrictPark.ParkType)))
                if (t != DistrictPark.ParkType.None && string.Equals(t.ToString(), name, StringComparison.OrdinalIgnoreCase)) return t;
            throw new ApiException(400, "Unknown park type '" + name + "'. Types: Generic, AmusementPark, Zoo, NatureReserve, Industry, Farming, Forestry, Ore, Oil, GenericCampus, TradeSchool, LiberalArts, University, PedestrianZone");
        }

        // GET /parks -> all park areas
        private static HttpResponse List(HttpRequest req)
        {
            string json = GameThread.Run<string>(delegate ()
            {
                DistrictManager dm = Singleton<DistrictManager>.instance;
                JsonWriter w = new JsonWriter().Obj().ArrIn("parks");
                for (int i = 1; i < dm.m_parks.m_buffer.Length; i++)
                {
                    if (!Exists(dm, i)) continue;
                    DistrictPark p = dm.m_parks.m_buffer[i];
                    w.Obj().Prop("id", i).Prop("name", dm.GetParkName(i)).Prop("type", p.m_parkType.ToString()).Prop("level", p.m_parkLevel.ToString())
                        .Prop("main_gate", p.m_mainGate).Prop("ticket_price", p.m_ticketPrice).EndObj();
                }
                return w.EndArr().EndObj().ToString();
            }, 5000);
            return HttpResponse.Json(200, json);
        }

        private static void PaintBrush(byte id, bool erase, float x1, float z1, float x2, float z2, float radius)
        {
            DistrictTool.ApplyBrush(DistrictTool.Layer.Parks, erase ? (byte)0 : id, radius, new Vector3(x1, 0f, z1), new Vector3(x2, 0f, z2));
        }

        // POST /park/create {"type":"Generic","name":"Stadspark","x":..,"z":..,"radius":80,"x2":..,"z2":..}
        // Creates a park area of the given type and paints it. Place the matching Main Gate building inside it afterwards.
        private static HttpResponse Create(HttpRequest req)
        {
            Dictionary<string, string> body = JsonReader.ParseFlat(req.Body);
            string typeName = body.ContainsKey("type") ? body["type"] : "Generic";
            DistrictPark.ParkType type = ParseType(typeName);
            string name = body.ContainsKey("name") ? body["name"] : null;
            float x = BuildEndpoints.GetFloat(body, "x"), z = BuildEndpoints.GetFloat(body, "z");
            float x2 = BuildEndpoints.GetFloat(body, "x2", x), z2 = BuildEndpoints.GetFloat(body, "z2", z);
            float radius = BuildEndpoints.GetFloat(body, "radius", 80f);
            BuildEndpoints.CheckBounds(x, z); BuildEndpoints.CheckBounds(x2, z2);
            if (radius < 10f || radius > 1000f) throw new ApiException(400, "radius must be 10-1000");
            string json = GameThread.Run<string>(delegate ()
            {
                DistrictManager dm = Singleton<DistrictManager>.instance;
                if (!dm.CheckParkLimits()) throw new ApiException(409, "Cannot create more park areas");
                byte id;
                if (!dm.CreatePark(out id, type, DistrictPark.ParkLevel.Level1)) throw new ApiException(409, "Cannot create a park area (limit reached)");
                PaintBrush(id, false, x, z, x2, z2, radius);
                if (!string.IsNullOrEmpty(name))
                {
                    IEnumerator<bool> e = dm.SetParkName(id, name);
                    while (e.MoveNext()) { }
                }
                return new JsonWriter().Obj().Prop("ok", true).Prop("park", id).Prop("name", dm.GetParkName(id)).Prop("type", type.ToString()).EndObj().ToString();
            }, 10000);
            return HttpResponse.Json(200, json);
        }

        // POST /park/paint {"park":3,"x":..,"z":..,"radius":60,"x2":..,"z2":..,"erase":false}
        private static HttpResponse Paint(HttpRequest req)
        {
            Dictionary<string, string> body = JsonReader.ParseFlat(req.Body);
            bool erase = body.ContainsKey("erase") && JsonReader.GetBool(body, "erase");
            int id = erase && !body.ContainsKey("park") ? 0 : JsonReader.GetInt(body, "park", 1, 255);
            float x = BuildEndpoints.GetFloat(body, "x"), z = BuildEndpoints.GetFloat(body, "z");
            float x2 = BuildEndpoints.GetFloat(body, "x2", x), z2 = BuildEndpoints.GetFloat(body, "z2", z);
            float radius = BuildEndpoints.GetFloat(body, "radius", 60f);
            BuildEndpoints.CheckBounds(x, z); BuildEndpoints.CheckBounds(x2, z2);
            if (radius < 10f || radius > 1000f) throw new ApiException(400, "radius must be 10-1000");
            string json = GameThread.Run<string>(delegate ()
            {
                DistrictManager dm = Singleton<DistrictManager>.instance;
                if (id != 0 && !Exists(dm, id)) throw new ApiException(404, "No park area " + id + " (see GET /parks)");
                if (id != 0 && dm.m_parks.m_buffer[id].IsAirport) throw new ApiException(400, "Airport areas are not supported");
                PaintBrush((byte)id, erase, x, z, x2, z2, radius);
                return new JsonWriter().Obj().Prop("ok", true).Prop("park", id).Prop("erased", erase).EndObj().ToString();
            }, 10000);
            return HttpResponse.Json(200, json);
        }

        // POST /park/delete {"park":3}
        private static HttpResponse Delete(HttpRequest req)
        {
            int id = JsonReader.GetInt(JsonReader.ParseFlat(req.Body), "park", 1, 255);
            string json = GameThread.Run<string>(delegate ()
            {
                DistrictManager dm = Singleton<DistrictManager>.instance;
                if (!Exists(dm, id)) throw new ApiException(404, "No park area " + id);
                string name = dm.GetParkName(id);
                dm.ReleasePark((byte)id);
                return new JsonWriter().Obj().Prop("ok", true).Prop("park", id).Prop("name", name).EndObj().ToString();
            }, 5000);
            return HttpResponse.Json(200, json);
        }

        /// <summary>Is a park policy set in this park area? (used by /policies and /policy)</summary>
        public static bool IsSet(DistrictPolicies.Policies p, int park)
        {
            return Singleton<DistrictManager>.instance.IsParkPolicySet(p, (byte)park);
        }

        public static bool Exists(int park) { return Exists(Singleton<DistrictManager>.instance, park); }
    }
}
