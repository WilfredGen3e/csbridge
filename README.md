# CSBridge — Cities: Skylines MCP server

Let an AI (e.g. the Claude desktop app) **play and manage Cities: Skylines 1** (macOS, Steam): read the city, build roads and buildings, zone, set taxes and policies, run public transport, and look at the result with screenshots.

Two parts:
1. **Mod** (C#, runs inside the game): exposes the game as a small HTTP API on `127.0.0.1:8080`.
2. **MCP server** (`mcp/`, Node): translates MCP tools into calls on that API. The Claude app starts it from its config.

> Status: working and tested on macOS with game version 1.21.1. Windows/Linux are untested (the mod itself is plain Unity Mono code; only the helper script is macOS-specific).

## What it can do

| Area | Tools |
|---|---|
| **Look** | `get_map` (roads, buildings, zones in an area), `get_terrain`, `screenshot` (returns the image), `get_camera` / `set_camera`, `get_views` / `set_view` (traffic, electricity, ... overlays), `set_time` |
| **Read the city** | `get_stats`, `get_demand`, `get_budget`, `get_services`, `get_traffic`, `get_problems`, `get_progress` |
| **Build** | `list_prefabs`, `place_building`, `bulldoze_building`, `build_road` (roads, rails, pipes, power lines; snaps to junctions; per-point elevation `x,z,e` for bridges and tunnels), `upgrade_road`, `bulldoze_road` |
| **Zones & land** | `zone` (paint/clear), `get_areas`, `unlock_area` |
| **Manage** | `set_taxes`, `set_service_budget`, `get_policies` / `set_policy`, districts (`create_district`, `paint_district`, `delete_district`, `get_districts`) |
| **Transport** | `create_transport_line` (bus/tram/trolleybus on roads; train/metro/ship/air between stations), `get_transport_lines`, `set_transport_line`, `delete_transport_line` |
| **Game** | `set_paused`, `set_speed`, `unlock` (milestones, monuments, map tiles), `set_full_demand`, `get_ui` / `close_ui` / `set_auto_dismiss` |
| **Saves** | `get_saves`, `save_game` (never overwrites unless asked), `load_save` |

Coordinates are metres; the buildable map runs from -4800 to 4800 on x and z. Buildings and roads go through the game's own placement code (collision, slope, water, cost, unlocks), so invalid placements are refused with the game's error.

The server sends `mcp/PLAYBOOK.md` to the client as instructions (also the `get_playbook` tool): order of work, city-design rules from the Paradox beginner's guide, how to lay pipes/power/bridges, how to diagnose problems. Edit that file to change how the AI plays.

A typical session: `list_prefabs` → `build_road` → `zone` → `place_building` (power, water, services) → `set_speed 3` → `screenshot` → adjust.

## Install (mod)
The macOS game cannot compile mod source itself, so a prebuilt `CSBridge.dll` is used.

Build it yourself (needs the .NET SDK, no extra packages):

    dotnet build CSBridge.csproj -c Release

Then put the folder in the game's Mods directory (a symlink works):

    ln -s "$PWD" "$HOME/Library/Application Support/Colossal Order/Cities_Skylines/Addons/Mods/CSBridge"

Start the game, enable "CSBridge" in Content Manager > Mods, and load a city.
Different game location? `dotnet build -p:GameManaged="/path/to/Cities.app/Contents/Resources/Data/Managed"`.
The game's DLLs are only referenced while building, never redistributed.

## Install (MCP server, Claude desktop app)
    cd mcp && npm install

Add to `~/Library/Application Support/Claude/claude_desktop_config.json` (use your own path) and restart the Claude app:

    {
      "mcpServers": {
        "csbridge": { "command": "node", "args": ["/path/to/csbridge/mcp/index.js"] }
      }
    }

Optional env var `CSBRIDGE_URL` (default `http://127.0.0.1:8080`). Start the game and load a city first.

## Development loop (macOS)
    scripts/restart-game.sh        # build, quit the game, start it again with the last save, wait for the API

This runs the game binary directly with `SteamAppId` and `--continuelastsave`, so no Paradox Launcher click is needed, and it closes the tutorial popup. **It loses unsaved progress** (the last save is reloaded) — also a handy way to undo test builds. Steam must be running.

Test without the Claude app by piping JSON-RPC to `node mcp/index.js`, or use plain HTTP:

    curl http://127.0.0.1:8080/ping
    curl "http://127.0.0.1:8080/prefabs?kind=building&service=PoliceDepartment"
    curl -X POST -d '{"road":"Basic Road","x1":0,"z1":0,"x2":200,"z2":0}' http://127.0.0.1:8080/road/build

Logs: `bridge.log` in the mod folder. Pitfalls found while building this are in `docs/LESSONS.md`; architecture notes for contributors are in `CLAUDE.md`.

## Notes and limits
- `unlock` uses the same calls as the game's developer UI (all milestones at once). It is meant for sandbox play; I have not checked how it interacts with achievements.
- Metro and tram lines are implemented but untested. Park/airport policies are not supported. Weather and following vehicles are not implemented.
- Bus/tram stops are placed in the middle of the nearest road segment (as the game's own tool does). Vehicles only start running after a few game days.
- Popups: the mod closes tutorial and milestone windows by itself (a fixed list, never your menus); switch off with `set_auto_dismiss`.
- Game state is only touched on the simulation thread (`SimulationManager.AddAction`); UI and camera calls go through the main-thread dispatcher.

## Security
Loopback only (127.0.0.1). Requests with a non-loopback `Host` or any `Origin` header (i.e. from a web page in a browser) are rejected, so a website cannot drive your game. There is no authentication beyond that: any local process can call the API.

## Credits and license
MIT, see `LICENSE`. Inspired by the existing [cities-skylines-mcp](https://github.com/jacklenzotti/cities-skylines-mcp) (MIT); CSBridge is an independent implementation with a different architecture (HTTP + Node, no external libraries in the mod).
