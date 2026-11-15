# CSBridge

## Doel
Een Cities: Skylines MCP-server: de Claude-app (lokaal) kan CS1 op macOS uitlezen en besturen. Twee delen: de mod (C#, HTTP-API in het spel) en een MCP-server (`mcp/`, Node, stdio) die de endpoints als tools aanbiedt. Bedoeld om op GitHub te publiceren: release = DLL + MCP-server; game-DLLs nooit meeleveren; localhost-API beveiligd tegen browsers (Host/Origin-check).

## Technische keuzes
- Unity Mono, .NET 3.5-profiel. CS1 op macOS kan zelf NIET compileren (geen Mono-compiler): bouw met `dotnet build CSBridge.csproj -c Release` -> `CSBridge.dll` in de root, herstart het spel.
- Geen async/await, Tasks, C# 6+ (interpolation, ?., nameof, expression-bodied), geen externe libs.
- JSON handmatig geschreven. Server: TcpListener op achtergrondthread, alleen 127.0.0.1:8080.
- Game-state alleen via SimulationManager.instance.AddAction, nooit direct vanaf netwerkthread.
- Log: `bridge.log` in de modmap (gitignored).

## Structuur
- `src/` mod-code: BridgeMod.cs (IUserMod + LoadingExtensionBase), HttpServer.cs (TcpListener), Endpoints.cs, GameThread.cs (AddAction + wachten), Json.cs, BridgeLog.cs. `CSBridge.csproj` bouwt de DLL; `Directory.Build.props` legt obj/ buiten de repo (~/.cache/csbridge).
- `docs/LESSONS.md` valkuilen; lees dit voor je nieuwe functionaliteit begint.
- `mcp/` Node MCP-server (stdio, @modelcontextprotocol/sdk, ESM). `index.js` heeft tools ping en get_stats; elke nieuwe endpoint = één `tool(...)`-regel. Test zonder Claude-app: JSON-RPC over stdin pipen.

## Ontwikkelloop
`scripts/restart-game.sh` bouwt, sluit het spel af en start het opnieuw. Start: binaire `Cities` direct met `SteamAppId=255710` en `--continuelastsave` (laadt de laatste save, geen launcher of klik nodig); het script pollt /ping en is klaar na ca. 50 s. Plain `open Cities.app` faalt (zie LESSONS). Let op: de herstart gooit niet-opgeslagen voortgang weg.

## Status
- Fase 1-3 klaar (ping, stats, demand, budget, services, traffic, problems, pause, speed, taxes, budget-set).
- Fase 4 (bouwen) getest in het echte spel: GET /prefabs, /map, /terrain, /areas, /progress; POST /building/place|bulldoze, /road/build|upgrade|bulldoze, /zone, /areas/unlock, /unlock. Zones groeien (bevolking steeg), stroom/water via build_road met Power Line / Water Pipe. Alles heeft een MCP-tool.
- Fase 5: districten (/districts, /district/create|paint|delete) en beleid (/policies, /policy). Fase 6: /camera, /screenshot (PNG, via MCP als afbeelding), /view (info-views), /time, /ui. Zelf kijken: set_camera + screenshot.
- Testen: `scripts/restart-game.sh` herlaadt de laatste save (testwijzigingen zijn dus weg tenzij het spel opslaat). Nog te doen: openbaar vervoer-lijnen, tunnel/brug-hoogtes testen, weer, README bijwerken.
- ILSpy: `dotnet tool install ilspycmd --tool-path <dir>`; draaien met `DOTNET_ROOT=/opt/homebrew/Cellar/dotnet/<ver>/libexec`. Controleer spel-API altijd in de decompilatie.

Zie `docs/LESSONS.md` voor valkuilen (lees voor nieuwe functionaliteit).
