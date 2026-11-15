#!/usr/bin/env node
// MCP server (stdio) that exposes the CSBridge mod's HTTP API as tools.
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";

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

await server.connect(new StdioServerTransport());
