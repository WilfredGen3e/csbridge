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
- `mcp/` Node MCP-server (stdio, @modelcontextprotocol/sdk, ESM). `index.js` heeft tools ping en get_stats; elke nieuwe endpoint = één `tool(...)`-regel. `mcp/PLAYBOOK.md` = speelhandleiding die de server als `instructions` meestuurt (en als tool `get_playbook`); werk die bij als je iets leert over hoe je het spel goed speelt. Test zonder Claude-app: JSON-RPC over stdin pipen.

## Ontwikkelloop
`scripts/restart-game.sh` bouwt, sluit het spel af en start het opnieuw. Start: binaire `Cities` direct met `SteamAppId=255710` en `--continuelastsave` (laadt de laatste save, geen launcher of klik nodig); het script pollt /ping en is klaar na ca. 50 s. Plain `open Cities.app` faalt (zie LESSONS). Let op: de herstart gooit niet-opgeslagen voortgang weg.

## Status
- Fase 1-3 klaar (ping, stats, demand, budget, services, traffic, problems, pause, speed, taxes, budget-set).
- Fase 4 (bouwen) getest in het echte spel: GET /prefabs, /map, /terrain, /areas, /progress; POST /building/place|bulldoze, /road/build|upgrade|bulldoze, /zone, /areas/unlock, /unlock. Zones groeien (bevolking steeg), stroom/water via build_road met Power Line / Water Pipe. Alles heeft een MCP-tool.
- Fase 5: districten (/districts, /district/create|paint|delete) en beleid (/policies, /policy). Fase 6: /camera, /screenshot (PNG, via MCP als afbeelding), /view (info-views), /time, /ui. Zelf kijken: set_camera + screenshot.
- Testen: `scripts/restart-game.sh` herlaadt de laatste save (testwijzigingen zijn dus weg tenzij het spel opslaat). Fase 7: /transport/types|lines, /transport/line/create|delete|set (bus/tram/trolleybus op wegen; trein/metro/schip/vliegtuig tussen stationsgebouwen; trein getest, metro/tram niet). Fase 8: /save, /saves, /load, /demand/full, /ui/autodismiss (met MCP-tools); bruggen/tunnels via `x,z,e` getest; pijp/stroom snapt aan gebouwknopen. Fase 9: /networks (componenten van wegen/pijpen/stroom), /building, /weather, /vehicles, /camera/follow; bovengrondse metro getest (trein rijdt). Nog te doen: MCP in de Claude-app testen, ondergrondse metro/tram, park- en luchthavenbeleid.
- Fase 10 (tokenbesparing, in het spel getest: get_map-filters/compact, dry_run + botsende objecten, build_many, get_city_summary, screenshot width, load_save wacht; guard niet langdurig getest): get_map filters/compact, build_road dry_run/removed/verbose/with_pipe, build_many, get_city_summary, screenshot width, load_save wacht, /guard (auto-pauze), milestones in /progress, unlock_milestone in /prefabs.
- Fase 11: parkgebieden (/parks, /park/create|paint|delete, park-beleid via /policy park=; niet te testen zonder Parklife-DLC), /unlock what=transport, unlock_milestone in prefabs/transport/progress. Metro/trein ontgrendelen door een station te bouwen; ondergrondse metro (stations als netwerk) gebouwd maar sporen verbinden gaf SlopeTooSteep: nog open.
- ILSpy: `dotnet tool install ilspycmd --tool-path <dir>`; draaien met `DOTNET_ROOT=/opt/homebrew/Cellar/dotnet/<ver>/libexec`. Controleer spel-API altijd in de decompilatie.

Zie `docs/LESSONS.md` voor valkuilen (lees voor nieuwe functionaliteit).
