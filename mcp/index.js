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

function postTool(name, description, shape, path) {
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

await server.connect(new StdioServerTransport());
