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
  "Look at the city. Without arguments: counts and the bounding box of built-up area. With x, z and radius (max 2000): road segments (id, road type, endpoints), buildings (id, prefab, position, angle, size) and zoned cells in that circle. " + COORD,
  {
    x: z.number().optional(), z: z.number().optional(),
    radius: z.number().min(1).max(2000).optional().describe("Default 300 when x/z are given"),
    limit: z.number().int().min(1).max(3000).optional().describe("Max roads and max buildings returned (default 400)"),
    zones: z.boolean().optional().describe("Include zoned cells (default true)"),
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
postTool(server, "load_save", "Load a savegame by name (see get_saves). Drops the current city: save first if you want to keep it. The API is unavailable for about 30-60 s while the city loads.", { name: z.string() }, "/load");
postTool(server, "bulldoze_building", "Remove a building by id (from get_map).", { id: z.number().int() }, "/building/bulldoze");
postTool(
  server,
  "build_road",
  "Build roads or other networks (rails, pipes, power lines) with the game's own tool: ends snap to existing junctions/segments within `snap` metres and cross at intersections. Give `points` (\"x,z;x,z;...\", 2-64 points, one straight segment per pair) or x1,z1,x2,z2, optionally a curve control point (curve=\"x,z\" or cx,cz) for exactly 2 points. Use bridge/elevated/tunnel prefab names plus `elevation` (metres, negative for tunnels) for non-ground roads. Long roads are split into segments by the game. Partial success is reported. " + COORD,
  {
    road: z.string().describe("Exact name from list_prefabs kind=road, e.g. Basic Road"),
    points: z.string().optional(), curve: z.string().optional(),
    x1: z.number().optional(), z1: z.number().optional(), x2: z.number().optional(), z2: z.number().optional(),
    cx: z.number().optional(), cz: z.number().optional(),
    elevation: z.number().optional(), reverse: z.boolean().optional().describe("Flip direction (one-way roads)"),
    snap: z.number().min(0).max(30).optional().describe("Default 8"),
  },
  "/road/build"
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
  "Unlock content using the game's developer unlock calls: progression (all milestones: services, zones, policies, buildings), monuments, wonders, areas (buy all buyable map tiles) or all. Needed before building locked things (list_prefabs shows unlocked=false).",
  { what: z.enum(["progression", "monuments", "wonders", "areas", "all"]) },
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
  "List policies and whether each is enabled/unlocked, for the whole city (district=0) or one district. type: Services, Taxation, CityPlanning, Special, Specialization, Event.",
  { district: z.number().int().optional(), type: z.string().optional(), unlocked: z.boolean().optional() },
  "/policies"
);
postTool(
  server,
  "set_policy",
  "Enable or disable a policy (name from get_policies) for the whole city (district omitted or 0) or one district. Policies have upkeep costs.",
  { policy: z.string(), enabled: z.boolean(), district: z.number().int().optional() },
  "/policy"
);

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
  { hide_ui: z.boolean().optional().describe("Default true") },
  async (args) => {
    try {
      const qs = args.hide_ui === false ? "?hide_ui=0" : "";
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
