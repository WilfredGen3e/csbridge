# CSBridge

## Doel
CS1-mod met lokale HTTP-API zodat een AI-agent het spel kan uitlezen en besturen.

## Technische keuzes
- Unity Mono, .NET 3.5-profiel. CS1 op macOS kan zelf NIET compileren (geen Mono-compiler): bouw met `dotnet build CSBridge.csproj -c Release` -> `CSBridge.dll` in de root, herstart het spel.
- Geen async/await, Tasks, C# 6+ (interpolation, ?., nameof, expression-bodied), geen externe libs.
- JSON handmatig geschreven. Server: TcpListener op achtergrondthread, alleen 127.0.0.1:8080.
- Game-state alleen via SimulationManager.instance.AddAction, nooit direct vanaf netwerkthread.
- Log: `bridge.log` in de modmap (gitignored).

## Structuur
- `src/` mod-code (BridgeMod.cs = IUserMod, BridgeLog.cs = logging), `CSBridge.csproj` bouwt de DLL
- `docs/LESSONS.md` valkuilen; lees dit voor je nieuwe functionaliteit begint.
- `mcp/` (fase 5)

## Status
- Fase 0: symlink + scaffold + dotnet-build werken; DLL laden in het spel nog niet bevestigd.

Zie `docs/LESSONS.md` voor valkuilen (lees voor nieuwe functionaliteit).
