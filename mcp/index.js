#!/usr/bin/env node
// MCP server (stdio) that exposes the CSBridge mod's HTTP API as tools.
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import { readFile } from "node:fs/promises";

const BASE = process.env.CSBRIDGE_URL || "http://127.0.0.1:8080";

async function call(path, options) {
  let res;
  try {
    res = await fetch(BASE + path, { signal: AbortSignal.timeout(8000), ...options });
  } catch (e) {
    throw new Error(
      "Cannot reach the CSBridge mod at " + BASE +
      ". Is Cities: Skylines running with the CSBridge mod enabled and a city loaded?"
    );
  }
  const text = await res.text();
  if (!res.ok) throw new Error("CSBridge returned " + res.status + ": " + text);
  return text;
}

function tool(server, name, description, path) {
  server.tool(name, description, async () => {
    try {
      return { content: [{ type: "text", text: await call(path) }] };
    } catch (e) {
      return { isError: true, content: [{ type: "text", text: e.message }] };
    }
  });
}

import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

// The playbook (mcp/PLAYBOOK.md) tells the model how to play: order of work, building rules, diagnosing. It is sent to the client as
// server instructions and is also available as the get_playbook tool, for clients that ignore instructions.
let PLAYBOOK = "";
try { PLAYBOOK = readFileSync(join(dirname(fileURLToPath(import.meta.url)), "PLAYBOOK.md"), "utf8"); } catch (e) { PLAYBOOK = "(PLAYBOOK.md not found)"; }

const server = new McpServer({ name: "csbridge", version: "0.1.0" }, { instructions: PLAYBOOK });

server.tool(
  "get_city_summary",
  "One compact check of the whole city: stats, demand, key service capacity, problem counts, traffic and whether roads/water/power are one connected network. Use this after each build step instead of five separate calls.",
  {},
  async () => {
    try {
      const get = async (p) => { try { return JSON.parse(await call(p)); } catch (e) { return { error: e.message }; } };
      // One after the other: the mod's HTTP server handles one request at a time and drops parallel ones.
      const results = [];
      for (const p of ["/stats", "/demand", "/services", "/problems", "/traffic", "/networks?kind=road", "/networks?kind=water", "/networks?kind=power"]) results.push(await get(p));
      const [stats, demand, services, problems, traffic, road, water, power] = results;
      const net = (n) => (n.error ? n : { components: n.components, connected: n.connected, ...(n.list && n.list[0] && "has_highway" in n.list[0] ? { highway: n.list.some((c) => c.has_highway) } : {}) });
      const probs = {};
      for (const p of problems.problems || []) probs[p.problem] = p.buildings;
      const out = { stats, demand, traffic, problems: problems.error ? problems : probs, networks: { road: net(road), water: net(water), power: net(power) }, services };
      return { content: [{ type: "text", text: JSON.stringify(out) }] };
    } catch (e) {
      return { isError: true, content: [{ type: "text", text: e.message }] };
    }
  }
);
server.tool("get_playbook", "How to play Cities: Skylines well with these tools: order of work, city design rules, how to build pipes/power/bridges, how to diagnose problems. Read this before building anything.", {}, async () => ({ content: [{ type: "text", text: PLAYBOOK }] }));
tool(server, "ping", "Check that the CSBridge mod is running in Cities: Skylines.", "/ping");
tool(
  server,
  "get_stats",
  "Get basic city stats: population, money, in-game time, paused (bool) and game speed.",
  "/stats"
);
tool(
  server,
  "get_demand",
  "Get zone demand (0-100, like the demand bars in the game): residential, commercial, industrial_and_office, plus the 'actual' values.",
  "/demand"
);
tool(
  server,
  "get_budget",
  "Get the budget: total income/expenses, loan and policy expenses, tax rates (percent), and income/expenses plus day/night budget percent per service. Money values are in game currency.",
  "/budget"
);
tool(
  server,
  "get_services",
  "Get service capacity vs. consumption for the whole city: electricity (MW), water, sewage, garbage, health, education, police, employment.",
  "/services"
);
tool(
  server,
  "get_traffic",
  "Get overall traffic flow in percent (0-100, higher is better), as in the game's traffic info view.",
  "/traffic"
);
tool(
  server,
  "get_problems",
  "Get buildings with problem notifications, grouped by problem type with counts and a few sample buildings (id, prefab, x, z).",
  "/problems"
);

function postTool(server, name, description, shape, path) {
  server.tool(name, description, shape, async (args) => {
    try {
      const text = await call(path, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(args),
      });
      return { content: [{ type: "text", text }] };
    } catch (e) {
      return { isError: true, content: [{ type: "text", text: e.message }] };
    }
  });
}

postTool(
  server,
  "set_paused",
  "Pause or unpause the game. Returns the new paused state and speed.",
  { paused: z.boolean().describe("true to pause, false to resume"), force: z.boolean().optional().describe("Also clear the forced pause set by game dialogs (check forced_paused in get_stats)") },
  "/pause"
);
postTool(
  server,
  "set_speed",
  "Set the game speed (1 = normal, 2 = fast, 3 = fastest). Note: setting the speed also unpauses the game.",
  { speed: z.number().int().min(1).max(3).describe("Game speed 1-3") },
  "/speed"
);
postTool(
  server,
  "set_taxes",
  "Set tax rates in percent (0-29) for one or more zone types. Applies to all building levels. Returns the resulting rates.",
  {
    residential_low: z.number().int().min(0).max(29).optional(),
    residential_high: z.number().int().min(0).max(29).optional(),
    commercial_low: z.number().int().min(0).max(29).optional(),
    commercial_high: z.number().int().min(0).max(29).optional(),
    industrial: z.number().int().min(0).max(29).optional(),
    office: z.number().int().min(0).max(29).optional(),
  },
  "/taxes"
);
postTool(
  server,
  "set_service_budget",
  "Set the budget percentage (50-150) of a public service for day and/or night. Services: Road, Electricity, Water, Garbage, HealthCare, FireDepartment, PoliceDepartment, Education, PublicTransport, Beautification, Monument, Disaster.",
  {
    service: z.string().describe("Service name, e.g. Road or HealthCare"),
    day: z.number().int().min(50).max(150).optional(),
    night: z.number().int().min(50).max(150).optional(),
  },
  "/budget"
);

function getTool(server, name, description, shape, path) {
  server.tool(name, description, shape, async (args) => {
    try {
      const qs = new URLSearchParams();
      for (const [k, v] of Object.entries(args)) if (v !== undefined) qs.set(k, String(v));
      const q = qs.toString();
      return { content: [{ type: "text", text: await call(path + (q ? "?" + q : "")) }] };
    } catch (e) {
      return { isError: true, content: [{ type: "text", text: e.message }] };
    }
  });
}

const COORD = "Coordinates are in metres; the buildable map runs from -4800 to 4800 on both x and z (x = east, z = north). Only unlocked map tiles can be built on (see get_areas).";

// ---- Phase 4: seeing the city and building ----
getTool(
  server,
  "get_map",
  "Look at the city. Without arguments: counts and the bounding box of built-up area. With x, z and radius (max 2000): road segments and buildings in that circle plus zoned cells. Output is compact by default: roads=[id,type,x1,z1,x2,z2], buildings=[id,prefab,service,x,z,angle]; pipes, power lines and footpaths are hidden (their count is reported) unless you ask with all=true or road=\"Water Pipe\". Use small radii (<=200) and filters to save tokens: counts_only=true gives only per-type counts, include=roads,buildings,zones picks what to list, service=Electricity / prefab=Pole / road=Power Line filter by name part. " + COORD,
  {
    x: z.number().optional(), z: z.number().optional(),
    radius: z.number().min(1).max(2000).optional().describe("Default 300 when x/z are given"),
    limit: z.number().int().min(1).max(3000).optional().describe("Max roads and max buildings returned (default 200)"),
    counts_only: z.boolean().optional().describe("Only per-type counts, no lists"),
    include: z.string().optional().describe("Comma list of roads,buildings,zones (default all)"),
    road: z.string().optional().describe("Only road segments whose type contains this text, e.g. Power Line, Water Pipe, Medium; also shows hidden network types"),
    service: z.string().optional().describe("Only buildings of this service, e.g. Electricity, PoliceDepartment, Residential"),
    prefab: z.string().optional().describe("Only buildings whose prefab name contains this text"),
    all: z.boolean().optional().describe("Also list pipes, power lines and footpaths"),
    verbose: z.boolean().optional().describe("Objects with named fields instead of compact arrays (3x larger)"),
    zones: z.boolean().optional().describe("Include zoned cells (default true; turn off to save tokens)"),
    include_virtual: z.boolean().optional().describe("Also list the game's virtual helper networks (transport line paths, pedestrian links); default false"),
  },
  "/map"
);
getTool(
  server,
  "get_terrain",
  "Ground height, water height and whether a point is water. " + COORD,
  { x: z.number(), z: z.number() },
  "/terrain"
);
getTool(
  server,
  "list_prefabs",
  "List buildable prefabs. kind=building (service buildings, parks, monuments, growables...) or kind=road (roads, rails, pipes, power lines, paths). Filter by name text and/or service (e.g. PoliceDepartment, Electricity, Water, Road, PublicTransport, Beautification); unlocked=true hides locked ones. Buildings show size in cells (8 m) and placement mode (Roadside = must be beside a road).",
  {
    kind: z.enum(["building", "road"]).optional(),
    filter: z.string().optional(), service: z.string().optional(),
    unlocked: z.boolean().optional(), limit: z.number().int().min(1).max(500).optional(),
  },
  "/prefabs"
);
postTool(
  server,
  "place_building",
  "Place a building. Roadside buildings (service buildings) snap to the road side within 8 m of the target; other buildings go exactly at x,z. The game's own validation applies (collisions, slope, water, money). Returns the building id. " + COORD,
  { prefab: z.string().describe("Exact name from list_prefabs"), x: z.number(), z: z.number(), angle: z.number().optional().describe("Degrees (ignored for roadside buildings)") },
  "/building/place"
);
postTool(server, "set_full_demand", "Switch the game's developer option \"Full Demand\" on or off: residential, commercial and workplace demand are held at 100 so zoned land fills up. Use it when growth stalls on low demand (sandbox play).", { enabled: z.boolean() }, "/demand/full");
getTool(server, "get_saves", "List the savegames (name, modified time, size), newest first.", {}, "/saves");
postTool(server, "save_game", "Save the city under a new name. Refuses an existing name unless overwrite is true (never overwrite the player's own saves). The save becomes the 'last save'.", { name: z.string(), overwrite: z.boolean().optional() }, "/save");
getTool(server, "check_networks", "Check whether a network is one connected piece. kind = road, water (pipes) or power. Lists the connected components with size and bounding box; a healthy city has ONE per kind (roads: the component must also have_highway, or nobody moves in). With x and z it says which component the nearest node belongs to. Use this whenever water, power or growth does not work.", { kind: z.enum(["road", "water", "power"]), x: z.number().optional(), z: z.number().optional() }, "/networks");
getTool(server, "get_building", "Details of one building by id (from get_map): prefab, service, position, flags, problems, electricity/water/sewage ok, citizens, workers and workplaces, happiness, health, buffers.", { id: z.number().int() }, "/building");
tool(server, "get_weather", "Current and target weather: rain, fog, cloud (0-1), temperature in degrees C.", "/weather");
postTool(server, "set_weather", "Set the target weather (all optional): rain 0-1, fog 0-1, cloud 0-1, temperature -30..50. Keeps the weather system on; changes fade in and out slowly.", { rain: z.number().min(0).max(1).optional(), fog: z.number().min(0).max(1).optional(), cloud: z.number().min(0).max(1).optional(), temperature: z.number().min(-30).max(50).optional() }, "/weather");
getTool(server, "get_vehicles", "Vehicles (buses, trains, cars, trucks) near a point: id, type, position, speed. Use an id with follow to watch it. " + COORD, { x: z.number(), z: z.number(), radius: z.number().min(1).max(2000).optional(), limit: z.number().int().min(1).max(100).optional() }, "/vehicles");
postTool(server, "follow", "Make the camera follow a vehicle (from get_vehicles), a building or a citizen instance; stop=true ends it. Good for showing how a bus line or a train runs.", { vehicle: z.number().int().optional(), building: z.number().int().optional(), citizen_instance: z.number().int().optional(), stop: z.boolean().optional() }, "/camera/follow");
server.tool(
  "load_save",
  "Load a savegame by name (see get_saves). Drops the current city: save first if you want to keep it. The API is gone for about 30-90 s while the city loads; by default this tool waits until the game answers again and reports how long it took (wait=false returns at once).",
  { name: z.string(), wait: z.boolean().optional().describe("Wait until the loaded city is ready (default true)") },
  async (args) => {
    try {
      const first = await call("/load", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ name: args.name }) });
      if (args.wait === false) return { content: [{ type: "text", text: first }] };
      const t0 = Date.now();
      // First wait for the old city to go away (the API stops answering while the new one loads), at most 20 s.
      let wentDown = false;
      while (!wentDown && Date.now() - t0 < 20000) {
        await new Promise((r) => setTimeout(r, 1000));
        try { const pong = await fetch(BASE + "/ping", { signal: AbortSignal.timeout(1500) }); if (!pong.ok) wentDown = true; } catch (e) { wentDown = true; }
      }
      while (Date.now() - t0 < 240000) {
        try {
          const pong = await fetch(BASE + "/ping", { signal: AbortSignal.timeout(3000) });
          if (pong.ok) {
            const stats = await call("/stats");
            return { content: [{ type: "text", text: JSON.stringify({ ok: true, loaded: args.name, ready_after_s: Math.round((Date.now() - t0) / 1000), api_went_down: wentDown, stats: JSON.parse(stats) }) }] };
          }
        } catch (e) { /* still loading */ }
        await new Promise((r) => setTimeout(r, 2000));
      }
      return { isError: true, content: [{ type: "text", text: "Timed out after 240 s: the game still does not answer. Check the game window (get_ui cannot be reached while loading)." }] };
    } catch (e) {
      return { isError: true, content: [{ type: "text", text: e.message }] };
    }
  }
);
postTool(server, "set_guard", "Safety net while the game runs: enabled=true pauses the game by itself when water, sewage or electricity capacity falls below consumption twice in a row (the reason is returned by get_guard and written to the log). Turn it on before you run the game at speed 3 and leave to think.", { enabled: z.boolean() }, "/guard");
tool(server, "get_guard", "Is the safety guard on, and did it pause the game (last_trigger with reason and game time)?", "/guard");
postTool(server, "bulldoze_building", "Remove a building by id (from get_map).", { id: z.number().int() }, "/building/bulldoze");
const BUILD_ROAD_SHAPE = {
  road: z.string().describe("Exact name from list_prefabs kind=road, e.g. Basic Road"),
  points: z.string().optional(), curve: z.string().optional(),
  x1: z.number().optional(), z1: z.number().optional(), x2: z.number().optional(), z2: z.number().optional(),
  cx: z.number().optional(), cz: z.number().optional(),
  elevation: z.number().optional(), reverse: z.boolean().optional().describe("Flip direction (one-way roads)"),
  snap: z.number().min(0).max(30).optional().describe("Default 8"),
  dry_run: z.boolean().optional().describe("Only test: report whether it would work and which existing objects would be removed, build nothing"),
  verbose: z.boolean().optional().describe("Per-segment details (cost, snapping, end node); default is just the segment ids"),
  with_pipe: z.boolean().optional().describe("Also lay a Water Pipe along the same centre line (water + sewage for the buildings beside the road)"),
};

async function buildRoad(args) {
  const { with_pipe, ...rest } = args;
  const post = (body) => call("/road/build", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
  const out = JSON.parse(await post(rest));
  if (with_pipe && out.ok !== false && !rest.dry_run) {
    try { out.pipe = JSON.parse(await post({ ...rest, road: "Water Pipe", elevation: 0, verbose: false })); }
    catch (e) { out.pipe = { ok: false, error: e.message }; }
  }
  return out;
}

server.tool(
  "build_road",
  "Build roads or other networks (rails, pipes, power lines) with the game's own tool: ends snap to existing junctions/segments within `snap` metres and cross at intersections. Give `points` (\"x,z;x,z;...\", 2-64 points, one straight segment per pair) or x1,z1,x2,z2, optionally a curve control point (curve=\"x,z\" or cx,cz) for exactly 2 points. Use bridge/elevated/tunnel prefab names plus `elevation` (metres, negative for tunnels) for non-ground roads. Long roads are split into segments by the game. The answer lists any existing objects the road removed (`removed`) and, when it fails with ObjectCollision, which objects are in the way. Use dry_run=true to test first. " + COORD,
  BUILD_ROAD_SHAPE,
  async (args) => {
    try { return { content: [{ type: "text", text: JSON.stringify(await buildRoad(args)) }] }; }
    catch (e) { return { isError: true, content: [{ type: "text", text: e.message }] }; }
  }
);

server.tool(
  "build_many",
  "Run several build steps in one call to save round trips and tokens. ops = array of objects with kind \"road\" (same fields as build_road, incl. with_pipe), \"zone\" (same as zone) or \"building\" (prefab, x, z, angle_deg). Steps run in order; a failing step is reported and the rest still run unless stop_on_error is true. The answer is one short line per step.",
  { ops: z.array(z.record(z.any())).min(1).max(40), stop_on_error: z.boolean().optional() },
  async (args) => {
    const lines = [];
    for (let i = 0; i < args.ops.length; i++) {
      const { kind, ...op } = args.ops[i];
      let line;
      let failed = false;
      try {
        if (kind === "road") {
          const r = await buildRoad(op);
          failed = r.ok === false;
          line = { ok: r.ok, built: r.built ?? r.would_build, removed: r.removed, error: r.error, pipe: r.pipe ? (r.pipe.ok ? "ok" : r.pipe.error) : undefined };
        } else if (kind === "zone" || kind === "building") {
          const path = kind === "zone" ? "/zone" : "/building/place";
          const r = JSON.parse(await call(path, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(op) }));
          line = { ok: r.ok !== false, ...(kind === "building" ? { id: r.id } : { cells_changed: r.cells_changed }) };
        } else { failed = true; line = { ok: false, error: "unknown kind " + kind }; }
      } catch (e) { failed = true; line = { ok: false, error: e.message }; }
      lines.push(JSON.stringify({ step: i, kind, ...line }));
      if (failed && args.stop_on_error) break;
    }
    return { content: [{ type: "text", text: lines.join("\n") }] };
  }
);
postTool(server, "upgrade_road", "Change the road type of an existing segment (e.g. Basic Road -> Medium Road).", { segment: z.number().int(), road: z.string(), reverse: z.boolean().optional() }, "/road/upgrade");
postTool(server, "bulldoze_road", "Remove a road segment by id (from get_map).", { segment: z.number().int() }, "/road/bulldoze");
postTool(
  server,
  "zone",
  "Paint zones on the cells beside roads (cells are 8x8 m, up to 4 deep, and only exist next to roads). Give a rectangle (x1,z1,x2,z2, optional angle in degrees) or a road segment id with side both/left/right. zone: ResidentialLow, ResidentialHigh, CommercialLow, CommercialHigh, Industrial, Office or Unzoned (clears). Already-zoned cells only change with overwrite=true. Buildings then grow by themselves when there is demand (see get_demand) and the game is running.",
  {
    zone: z.string(), x1: z.number().optional(), z1: z.number().optional(), x2: z.number().optional(), z2: z.number().optional(),
    angle: z.number().optional(), segment: z.number().int().optional(), side: z.enum(["both", "left", "right"]).optional(),
    overwrite: z.boolean().optional(),
  },
  "/zone"
);
tool(server, "get_areas", "The 5x5 map tiles (1920 m each): which are unlocked, which can be bought and at what price.", "/areas");
postTool(server, "unlock_area", "Buy a map tile by index (0-24, from get_areas).", { index: z.number().int().min(0).max(24) }, "/areas/unlock");
tool(server, "get_progress", "Current milestone and number of unlocked map tiles.", "/progress");
postTool(
  server,
  "unlock",
  "Unlock content using the game's developer unlock calls: progression (population milestones), transport (the separate service/feature milestones that progression leaves closed: metro, trains, some services; check list_prefabs unlocked=false / unlock_milestone), monuments, wonders, areas (buy all buyable map tiles) or all. Needed before building locked things (list_prefabs shows unlocked=false).",
  { what: z.enum(["progression", "transport", "monuments", "wonders", "areas", "all"]) },
  "/unlock"
);

// ---- Phase 5: districts and policies ----
tool(server, "get_districts", "List districts (id, name, population, area_cells of 19.2 m each, happiness, crime). District 0 is the whole city. Note: new districts get high ids (e.g. 127).", "/districts");
postTool(
  server,
  "create_district",
  "Create a district by painting a round area (radius 10-1000 m) at x,z; add x2,z2 to paint a line-shaped area instead. Districts have their own policies and statistics. " + COORD,
  { name: z.string().optional(), x: z.number(), z: z.number(), radius: z.number().optional().describe("Default 100"), x2: z.number().optional(), z2: z.number().optional() },
  "/district/create"
);
postTool(
  server,
  "paint_district",
  "Add an area to an existing district (round, or line-shaped with x2,z2), or erase=true to remove the area from any district.",
  { district: z.number().int().optional(), x: z.number(), z: z.number(), radius: z.number().optional(), x2: z.number().optional(), z2: z.number().optional(), erase: z.boolean().optional() },
  "/district/paint"
);
postTool(server, "delete_district", "Delete a district by id.", { district: z.number().int() }, "/district/delete");
getTool(
  server,
  "get_policies",
  "List policies and whether each is enabled/unlocked, for the whole city (district=0) or one district. type: Services, Taxation, CityPlanning, Special, Specialization, Event, Park. With park=<id> (from get_parks) it lists the policies of that park area.",
  { district: z.number().int().optional(), park: z.number().int().optional(), type: z.string().optional(), unlocked: z.boolean().optional() },
  "/policies"
);
postTool(
  server,
  "set_policy",
  "Enable or disable a policy (name from get_policies) for the whole city (district omitted or 0), one district, or - for park policies such as AnimalEthics, ParkAdvertisement, FireworksBan - one park area (park=<id>). Policies have upkeep costs.",
  { policy: z.string(), enabled: z.boolean(), district: z.number().int().optional(), park: z.number().int().optional() },
  "/policy"
);

// ---- park areas (parks, zoo, amusement park, nature reserve, industry and campus areas) ----
tool(server, "get_parks", "List park areas (id, name, type, level, main gate building, ticket price). Park policies are set per park area with set_policy park=<id>.", "/parks");
postTool(
  server,
  "create_park",
  "Create a park area and paint it round at x,z (radius 10-1000; add x2,z2 for a line-shaped area). type: Generic, AmusementPark, Zoo, NatureReserve, Industry, Farming, Forestry, Ore, Oil, GenericCampus, TradeSchool, LiberalArts, University, PedestrianZone (airports are not supported). The area gets its level and policies when its Main Gate building stands inside it (place_building, see list_prefabs filter=Gate); attractions are then placed inside. " + COORD,
  { type: z.string().optional().describe("Default Generic"), name: z.string().optional(), x: z.number(), z: z.number(), radius: z.number().optional().describe("Default 80"), x2: z.number().optional(), z2: z.number().optional() },
  "/park/create"
);
postTool(server, "paint_park", "Extend a park area (round or line-shaped with x2,z2) or erase=true to remove the area from park areas.", { park: z.number().int().optional(), x: z.number(), z: z.number(), radius: z.number().optional(), x2: z.number().optional(), z2: z.number().optional(), erase: z.boolean().optional() }, "/park/paint");
postTool(server, "delete_park", "Delete a park area by id (its buildings stay).", { park: z.number().int() }, "/park/delete");

// ---- Phase 6: camera, screenshots, views ----
tool(server, "get_camera", "Camera focus point (x, y, z), size (view distance in metres, smaller = closer), heading and pitch in degrees.", "/camera");
postTool(
  server,
  "set_camera",
  "Move the camera. x,z = focus point; size 40-3000 (view distance; ~350 shows a few blocks, ~1500 a district); pitch 0-90 (90 = straight down like a map, 45-50 = isometric); heading 0-360. Moves instantly by default. " + COORD,
  { x: z.number().optional(), z: z.number().optional(), size: z.number().min(40).max(3000).optional(), pitch: z.number().min(0).max(90).optional(), heading: z.number().optional(), instant: z.boolean().optional() },
  "/camera"
);
server.tool(
  "screenshot",
  "Take a screenshot of the game view and return it as an image, so you can see what you built. By default the UI and the blur effects are hidden. Position the camera first with set_camera; use set_view for info views (traffic, electricity, zones...) and set_time for daylight.",
  { hide_ui: z.boolean().optional().describe("Default true"), width: z.number().int().min(160).max(2048).optional().describe("Shrink the image to this width in pixels (e.g. 640): far fewer tokens per picture. Default: full size") },
  async (args) => {
    try {
      const qp = new URLSearchParams();
      if (args.hide_ui === false) qp.set("hide_ui", "0");
      if (args.width) qp.set("width", String(args.width));
      const qs = qp.toString() ? "?" + qp.toString() : "";
      const info = JSON.parse(await call("/screenshot" + qs));
      const data = await readFile(info.path);
      return { content: [
        { type: "image", data: data.toString("base64"), mimeType: "image/png" },
        { type: "text", text: "Saved at " + info.path },
      ] };
    } catch (e) {
      return { isError: true, content: [{ type: "text", text: e.message }] };
    }
  }
);
tool(server, "get_views", "List the info views (map overlays) and which one is active.", "/view");
postTool(
  server,
  "set_view",
  "Switch the map overlay, like the game's info view buttons: None (normal), Traffic, Electricity, Water, Happiness, Density, LandValue, Pollution, Garbage, Health, FireSafety, CrimeRate, Education, Districts, Connections, ... (full list from get_views).",
  { mode: z.string() },
  "/view"
);
postTool(server, "set_time", "Set the time of day (0-24, e.g. 12 for noon) without changing the date. Handy for daylight screenshots.", { hour: z.number().min(0).max(23.99) }, "/time");

// ---- Phase 7: public transport ----
tool(server, "get_transport_types", "Transport types in this game (Bus, Metro, Train, Ship, Airplane, ...) with whether they are unlocked and street_based (bus/tram/trolleybus run on roads; the rest need station buildings).", "/transport/types");
tool(server, "get_transport_lines", "All transport lines: id, name, type, stops (positions), vehicles, length, passengers, budget and ticket price.", "/transport/lines");
postTool(
  server,
  "create_transport_line",
  "Create a public transport line. stops = \"x,z;x,z;...\" (2-32, in order; the line is closed back to the first stop). Bus/Tram/Trolleybus: stops go on the middle of the nearest suitable road segment within 40 m. Train/Metro/Ship/Airplane: each stop is the nearest matching station building within 150 m (place stations with place_building and connect them with build_road tracks first; a Train Station connects via its Train Station Track ends). Vehicles come from depots (Bus Depot etc.) and start after a few game days. " + COORD,
  { type: z.string().describe("Bus, Tram, Trolleybus, Train, Metro, Ship, Airplane..."), stops: z.string(), name: z.string().optional() },
  "/transport/line/create"
);
postTool(server, "delete_transport_line", "Delete a transport line by id.", { line: z.number().int() }, "/transport/line/delete");
postTool(
  server,
  "set_transport_line",
  "Change a line: budget (50-150 %), ticket_price (in cents) and/or active (true/false).",
  { line: z.number().int(), budget: z.number().int().min(50).max(150).optional(), ticket_price: z.number().int().min(0).optional(), active: z.boolean().optional() },
  "/transport/line/set"
);

tool(server, "get_ui", "List the popup windows currently open in the game UI (name, size). A tutorial or milestone popup can freeze the game; close it with close_ui.", "/ui");
postTool(server, "close_ui", "Close an open popup window by name (from get_ui), e.g. GenericTutorialHelper.", { name: z.string() }, "/ui/close");
postTool(server, "set_auto_dismiss", "Switch the mod's automatic closing of tutorial/milestone popups (GenericTutorialHelper, TutorialPanel, UnlockingPanel) on or off. It never touches menus you open yourself. get_ui shows what was dismissed.", { enabled: z.boolean() }, "/ui/autodismiss");

await server.connect(new StdioServerTransport());
