using System;
using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;

namespace CSBridge
{
    /// <summary>Phase 4: zoning (paint zones on the cells beside roads) and map tiles (areas).</summary>
    public static class ZoneEndpoints
    {
        public static void Register(HttpServer server)
        {
            server.Post("/zone", Zone);
            server.Get("/areas", Areas);
            server.Post("/areas/unlock", UnlockArea);
        }

        private static readonly string[] ZoneNames = new string[] {
            "ResidentialLow", "ResidentialHigh", "CommercialLow", "CommercialHigh", "Industrial", "Office", "Unzoned" };

        // POST /zone  {"zone":"ResidentialLow", "x1":..,"z1":..,"x2":..,"z2":.., "angle":0, "overwrite":false}
        //   or        {"zone":"Industrial", "segment":123, "side":"both|left|right"}
        // "Unzoned" clears zones. Cells exist only beside roads (up to 4 cells deep), and cells that already
        // have a zone are only changed with overwrite=true (or when clearing).
        private static HttpResponse Zone(HttpRequest req)
        {
            Dictionary<string, string> body = JsonReader.ParseFlat(req.Body);
            string zname;
            if (!body.TryGetValue("zone", out zname)) throw new ApiException(400, "Missing field: zone. One of: " + string.Join(", ", ZoneNames));
            ItemClass.Zone zone = ItemClass.Zone.Unzoned;
            bool found = false;
            foreach (string n in ZoneNames)
                if (string.Equals(n, zname, StringComparison.OrdinalIgnoreCase)) { zone = (ItemClass.Zone)Enum.Parse(typeof(ItemClass.Zone), n); found = true; }
            if (!found) throw new ApiException(400, "Unknown zone '" + zname + "'. One of: " + string.Join(", ", ZoneNames));
            bool overwrite = (body.ContainsKey("overwrite") && JsonReader.GetBool(body, "overwrite")) || zone == ItemClass.Zone.Unzoned;

            bool bySegment = body.ContainsKey("segment");
            float x1 = 0, z1 = 0, x2 = 0, z2 = 0, angle = 0;
            int segment = 0;
            string side = "both";
            if (bySegment)
            {
                segment = JsonReader.GetInt(body, "segment", 1, 36863);
                if (body.ContainsKey("side")) side = body["side"].ToLowerInvariant();
                if (side != "both" && side != "left" && side != "right") throw new ApiException(400, "side must be both, left or right");
            }
            else
            {
                x1 = BuildEndpoints.GetFloat(body, "x1"); z1 = BuildEndpoints.GetFloat(body, "z1");
                x2 = BuildEndpoints.GetFloat(body, "x2"); z2 = BuildEndpoints.GetFloat(body, "z2");
                BuildEndpoints.CheckBounds(x1, z1); BuildEndpoints.CheckBounds(x2, z2);
                angle = BuildEndpoints.GetFloat(body, "angle", 0f) * Mathf.Deg2Rad;
                if (Math.Abs(x2 - x1) > 3000f || Math.Abs(z2 - z1) > 3000f) throw new ApiException(400, "Area too large (max 3000 m per side)");
            }

            string json = GameThread.Run<string>(delegate ()
            {
                if (zone != ItemClass.Zone.Unzoned && !Singleton<UnlockManager>.instance.Unlocked(zone))
                    throw new ApiException(409, "Zone " + zone + " is not unlocked yet");
                ZoneManager zm = Singleton<ZoneManager>.instance;
                int changed = 0;

                if (bySegment)
                {
                    NetManager nm = Singleton<NetManager>.instance;
                    NetSegment s = nm.m_segments.m_buffer[segment];
                    if ((s.m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None) throw new ApiException(404, "No road segment " + segment);
                    List<ushort> blocks = new List<ushort>();
                    if (side != "right") { blocks.Add(s.m_blockStartLeft); blocks.Add(s.m_blockEndLeft); }
                    if (side != "left") { blocks.Add(s.m_blockStartRight); blocks.Add(s.m_blockEndRight); }
                    HashSet<ushort> done = new HashSet<ushort>();
                    foreach (ushort blk in blocks)
                    {
                        if (blk == 0 || !done.Add(blk)) continue;
                        changed += PaintBlock(zm, blk, zone, overwrite, false, 0, 0, 0, 0, 0);
                    }
                }
                else
                {
                    float minX = Mathf.Min(x1, x2), maxX = Mathf.Max(x1, x2), minZ = Mathf.Min(z1, z2), maxZ = Mathf.Max(z1, z2);
                    float pad = (Mathf.Abs(angle) > 0.001f) ? Mathf.Max(maxX - minX, maxZ - minZ) : 0f;
                    int gx0 = Mathf.Max((int)((minX - pad - 46f) / 64f + 75f), 0), gz0 = Mathf.Max((int)((minZ - pad - 46f) / 64f + 75f), 0);
                    int gx1 = Mathf.Min((int)((maxX + pad + 46f) / 64f + 75f), 149), gz1 = Mathf.Min((int)((maxZ + pad + 46f) / 64f + 75f), 149);
                    for (int gz = gz0; gz <= gz1; gz++)
                        for (int gx = gx0; gx <= gx1; gx++)
                        {
                            ushort blk = zm.m_zoneGrid[gz * 150 + gx];
                            int guard = 0;
                            while (blk != 0 && guard++ < 49152)
                            {
                                changed += PaintBlock(zm, blk, zone, overwrite, true, minX, minZ, maxX, maxZ, angle);
                                blk = zm.m_blocks.m_buffer[blk].m_nextGridBlock;
                            }
                        }
                }
                return new JsonWriter().Obj().Prop("ok", true).Prop("zone", zone.ToString()).Prop("cells_changed", changed).EndObj().ToString();
            }, 15000);
            return HttpResponse.Json(200, json);
        }

        private static int PaintBlock(ZoneManager zm, ushort blk, ItemClass.Zone zone, bool overwrite, bool useRect,
            float minX, float minZ, float maxX, float maxZ, float angle)
        {
            ZoneBlock zb = zm.m_blocks.m_buffer[blk];
            int rows = zb.RowCount;
            float ca = Mathf.Cos(zb.m_angle) * 8f, sa = Mathf.Sin(zb.m_angle) * 8f;
            float cxr = (minX + maxX) * 0.5f, czr = (minZ + maxZ) * 0.5f;
            float ra = Mathf.Cos(-angle), rb = Mathf.Sin(-angle);
            int changed = 0;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < 4; c++)
                {
                    if (useRect)
                    {
                        float px = zb.m_position.x + (c - 3.5f) * ca + (r - 3.5f) * sa;
                        float pz = zb.m_position.z + (c - 3.5f) * sa - (r - 3.5f) * ca;
                        float dx = px - cxr, dz = pz - czr;
                        float lx = cxr + dx * ra - dz * rb, lz = czr + dx * rb + dz * ra;   // rotate into the rectangle's frame
                        if (lx < minX || lx > maxX || lz < minZ || lz > maxZ) continue;
                    }
                    ItemClass.Zone cur = zm.m_blocks.m_buffer[blk].GetZone(c, r);
                    if (cur == zone) continue;
                    if (cur != ItemClass.Zone.Unzoned && !overwrite) continue;
                    if (zm.m_blocks.m_buffer[blk].SetZone(c, r, zone)) changed++;
                }
            if (changed > 0) zm.m_blocks.m_buffer[blk].RefreshZoning(blk);
            return changed;
        }

        // GET /areas  -> the 5x5 map tiles: which are unlocked, which can be bought and the price
        private static HttpResponse Areas(HttpRequest req)
        {
            string json = GameThread.Run<string>(delegate ()
            {
                GameAreaManager am = Singleton<GameAreaManager>.instance;
                JsonWriter w = new JsonWriter().Obj().Prop("unlocked_count", am.m_areaCount).Prop("max", 25);
                w.ArrIn("tiles");
                for (int idx = 0; idx < 25; idx++)
                {
                    int tx = idx % 5, tz = idx / 5;
                    bool unlocked = am.IsUnlocked(tx, tz);
                    bool can = !unlocked && am.CanUnlock(tx, tz);
                    w.Obj().Prop("index", idx)
                        .Prop("min_x", (tx - 2.5f) * 1920f).Prop("max_x", (tx - 1.5f) * 1920f)
                        .Prop("min_z", (tz - 2.5f) * 1920f).Prop("max_z", (tz - 1.5f) * 1920f)
                        .Prop("unlocked", unlocked).Prop("can_unlock", can);
                    if (can) w.Prop("price", am.CalculateTilePrice(idx) / 100);
                    w.EndObj();
                }
                return w.EndArr().EndObj().ToString();
            }, 5000);
            return HttpResponse.Json(200, json);
        }

        // POST /areas/unlock {"index": 12}
        private static HttpResponse UnlockArea(HttpRequest req)
        {
            int idx = JsonReader.GetInt(JsonReader.ParseFlat(req.Body), "index", 0, 24);
            string json = GameThread.Run<string>(delegate ()
            {
                GameAreaManager am = Singleton<GameAreaManager>.instance;
                int tx = idx % 5, tz = idx / 5;
                if (am.IsUnlocked(tx, tz)) throw new ApiException(409, "Tile " + idx + " is already unlocked");
                if (!am.CanUnlock(tx, tz)) throw new ApiException(409, "Tile " + idx + " cannot be unlocked yet (must border an unlocked tile; more tiles open with milestones)");
                int price = am.CalculateTilePrice(idx);
                EconomyManager eco = Singleton<EconomyManager>.instance;
                if (price != 0 && eco.FetchResource(EconomyManager.Resource.LandPrice, price, ItemClass.Service.None, ItemClass.SubService.None, ItemClass.Level.Level1) != price)
                    throw new ApiException(409, "Not enough money (price " + (price / 100) + ")");
                if (!am.UnlockArea(idx)) throw new ApiException(500, "The game failed to unlock tile " + idx);
                return new JsonWriter().Obj().Prop("ok", true).Prop("index", idx).Prop("price", price / 100).Prop("unlocked_count", am.m_areaCount).EndObj().ToString();
            }, 8000);
            return HttpResponse.Json(200, json);
        }
    }
}
