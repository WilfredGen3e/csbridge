# CSBridge — Cities: Skylines MCP server

Let an AI (e.g. the Claude desktop app) read and control **Cities: Skylines 1** (macOS, Steam) without screenshots.

Two parts:
1. **Mod** (C#, runs inside the game): reads game state and executes actions, exposed as a local HTTP API on `127.0.0.1:8080`.
2. **MCP server** (`mcp/`, Node, planned): translates MCP tools to calls on that HTTP API. The Claude app starts it from its config.

> Status: early. Phase 1 (`/ping`, `/stats`) in progress. MCP server not built yet.

## Install (mod)
The macOS game cannot compile mod source itself, so a prebuilt `CSBridge.dll` is used.

Build it yourself (needs the dotnet SDK):

    dotnet build CSBridge.csproj -c Release

Then put the folder in the game's Mods directory (a symlink works):

    ln -s ~/Git/CSBridge "$HOME/Library/Application Support/Colossal Order/Cities_Skylines/Addons/Mods/CSBridge"

Restart the game, enable "CSBridge" in Content Manager > Mods, and load a city.
Different game location? `dotnet build -p:GameManaged="/path/to/Cities.app/Contents/Resources/Data/Managed"`.
The game's DLLs are only referenced while building, never redistributed.

## Try it
    curl http://127.0.0.1:8080/ping
    curl http://127.0.0.1:8080/stats

Logs: `bridge.log` in this folder.

## Security
Loopback only. Requests with a non-loopback `Host` or any `Origin` header (i.e. from a browser page) are rejected.

## Roadmap
1. `/ping`, `/stats`
2. Read: `/budget`, `/demand`, `/services`, `/traffic`, `/problems`
3. Actions: `/speed`, `/pause`, `/taxes`, `/budget`, `/building/place`
4. Roads and zones
5. MCP server + GitHub Release (DLL + MCP server)
