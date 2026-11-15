# Lessons

## CS1 op macOS compileert geen mod-broncode
- **Wat ging mis:** mod stond niet in Content Manager. Player.log: `Error running .../Cities.app/Contents/Frameworks/Mono/bin/mono ... gmcs.exe (Cannot find the specified file)`.
- **Waarom:** de macOS-build bevat de Mono-compiler niet, dus `Source/*.cs` in de modmap wordt nooit gecompileerd.
- **Fix:** zelf bouwen naar `CSBridge.dll` met `dotnet build CSBridge.csproj -c Release` (referenties naar de DLLs van het spel, LangVersion 4). De DLL staat in de repo-root en wordt via de symlink geladen. Na elke codewijziging opnieuw bouwen en het spel herstarten.
- Bron staat in `src/` (niet `Source/`) zodat het spel niet nog eens probeert te compileren.
- `GenerateTargetFrameworkAttribute=false` is nodig, Unity's mscorlib kent dat attribuut niet.
- Debuggen: `~/Library/Logs/Unity/Player.log`, zoek op `CSBridge`.

## Spel laadt de DLL uit obj/
- **Wat:** Player.log toonde `Loading .../CSBridge/obj/Release/CSBridge.dll`: het spel scant de modmap recursief en pakte de tussenkopie.
- **Fix:** `Directory.Build.props` zet `BaseIntermediateOutputPath` naar `~/.cache/csbridge/obj/`, zodat er maar één DLL in de modmap staat.

## AddAction draait ook als het spel gepauzeerd is
- Getest (fase 1): `/stats` antwoordt direct terwijl `paused: true`. Lezen en acties via `SimulationManager.AddAction` werken dus op pauze.
