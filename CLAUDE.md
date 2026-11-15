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

## Status
- Mod laadt in het spel (bevestigd). Fase 1 klaar en getest (/ping, /stats; geld is oneindig bij Stefan, dus money = long.Max/100). MCP-server draait in de Claude-app. Fase 2: /demand en /budget gebouwd en werkend (waarden nog te vergelijken met het budgetpaneel); Fase 2 compleet: ook /services, /traffic, /problems werken (District-getters van het spel, VehicleManager.m_lastTrafficFlow, Building.m_problems). Fase 3 gestart: POST /pause en /speed werken (JsonReader.cs voor flat JSON-bodies, ApiException voor 4xx). Volgende: /taxes, /budget, daarna /building/place.
- ILSpy: `dotnet tool install ilspycmd --tool-path <dir>`; draaien met `DOTNET_ROOT=/opt/homebrew/Cellar/dotnet/<ver>/libexec`. Controleer spel-API altijd in de decompilatie.

Zie `docs/LESSONS.md` voor valkuilen (lees voor nieuwe functionaliteit).
