# CSBridge

Cities: Skylines 1 mod (macOS, Steam) that runs a local HTTP API (127.0.0.1:8080)
so an AI agent can read and control the game without screenshots.

## Install
Symlink this folder into the game's Mods directory:

    ln -s ~/Git/CSBridge "$HOME/Library/Application Support/Colossal Order/Cities_Skylines/Addons/Mods/CSBridge"

The macOS game cannot compile mod source itself, so build the DLL first:

    dotnet build CSBridge.csproj -c Release

Restart the game and enable "CSBridge" in Content Manager > Mods.
Logs go to `bridge.log` in this folder.

## Phases
1. PoC: /ping, /stats
2. Read: /budget, /demand, /services, /traffic, /problems
3. Actions: /speed, /pause, /taxes, /budget, /building/place
4. Roads and zones
5. MCP server (`mcp/`)
