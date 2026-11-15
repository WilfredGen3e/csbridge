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

## Gebruik de getters en velden van de spel-UI
- Voor stats niet zelf aggregeren: `DistrictManager.m_districts.m_buffer[0]` (district 0 = hele stad) heeft getters als `GetElectricityCapacity()` die ook de info-panelen gebruiken. Zoek de formule op door het bijbehorende `*InfoViewPanel` te decompileren (ilspycmd).
- Elektriciteit: ruwe waarde / 1000 = MW (zoals het paneel). Verkeersflow: `VehicleManager.m_lastTrafficFlow` (0-100).
- Gebouwproblemen: `Building.m_problems` is een `Notification.ProblemStruct`; cast naar `Problem1`/`Problem2` (ulong-flags) en loop over losse bits. `MajorProblem`/`FatalProblem` zijn ernst-bits, geen soort probleem.
- zsh: `echo == x` faalt (`=` expansie); quote het.
- `unemployment_percent` is 100 bij 0 werkenden en 0 banen (spel deelt kennelijk anders door nul); interpreteer niet zonder `workers`/`workplaces`.

## Snelheid zetten zet de pauze uit
- `SimulationManager.SelectedSimulationSpeed` (setter) zet `m_simulationPaused` op false. Wil je pauzeren op een snelheid: eerst `/speed`, daarna `/pause`. Staat ook in de MCP-tool-beschrijving.
- Setters van SimulationManager vanaf de simulatiedraad (via AddAction) werken zonder problemen.
