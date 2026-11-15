# CSBridge — Cities: Skylines MCP server

Let an AI (e.g. the Claude desktop app) read and control **Cities: Skylines 1** (macOS, Steam) without screenshots.

Two parts:
1. **Mod** (C#, runs inside the game): reads game state and executes actions, exposed as a local HTTP API on `127.0.0.1:8080`.
2. **MCP server** (`mcp/`, Node, planned): translates MCP tools to calls on that HTTP API. The Claude app starts it from its config.

> Status: early. Phases 1 and 2 done (read-only endpoints); MCP tools for all of them.

## Install (mod)
The macOS game cannot compile mod source itself, so a prebuilt `CSBridge.dll` is used.

Build it yourself (needs the dotnet SDK):

    dotnet build CSBridge.csproj -c Release

Then put the folder in the game's Mods directory (a symlink works):

    ln -s ~/Git/CSBridge "$HOME/Library/Application Support/Colossal Order/Cities_Skylines/Addons/Mods/CSBridge"

Restart the game, enable "CSBridge" in Content Manager > Mods, and load a city.
Different game location? `dotnet build -p:GameManaged="/path/to/Cities.app/Contents/Resources/Data/Managed"`.
The game's DLLs are only referenced while building, never redistributed.

## Development loop
    scripts/restart-game.sh        # build, quit the game, start it again (loses unsaved progress)

Then load a city; the API starts when a city is loaded.

## Try it
    curl http://127.0.0.1:8080/ping
    curl http://127.0.0.1:8080/stats
    curl -X POST -d '{"paused":true}' http://127.0.0.1:8080/pause
    curl -X POST -d '{"speed":2}' http://127.0.0.1:8080/speed   # also unpauses

Logs: `bridge.log` in this folder.

## Install (MCP server, Claude desktop app)
    cd mcp && npm install

Add to `~/Library/Application Support/Claude/claude_desktop_config.json` (use your own path) and restart the Claude app:

    {
      "mcpServers": {
        "csbridge": { "command": "node", "args": ["/Users/YOU/Git/CSBridge/mcp/index.js"] }
      }
    }

Optional env var `CSBRIDGE_URL` (default `http://127.0.0.1:8080`). Start the game and load a city first.

## Security
Loopback only. Requests with a non-loopback `Host` or any `Origin` header (i.e. from a browser page) are rejected.

## Roadmap
1. `/ping`, `/stats`
2. Read: `/demand`, `/budget`, `/services`, `/traffic`, `/problems` (done)
3. Actions: `/pause`, `/speed` (done); `/taxes`, `/budget`, `/building/place` (todo)
4. Roads and zones
5. MCP server + GitHub Release (DLL + MCP server)
