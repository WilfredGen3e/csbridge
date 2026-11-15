#!/usr/bin/env node
// MCP server (stdio) that exposes the CSBridge mod's HTTP API as tools.
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";

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

const server = new McpServer({ name: "csbridge", version: "0.1.0" });

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
  { paused: z.boolean().describe("true to pause, false to resume") },
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

await server.connect(new StdioServerTransport());
