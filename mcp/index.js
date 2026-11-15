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

await server.connect(new StdioServerTransport());
