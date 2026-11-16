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

## Spel starten via Steam opent de Paradox Launcher
- `open steam://rungameid/255710` start de Paradox Launcher, die een handmatige klik op Play wil. Fix: `open .../Cities_Skylines/Cities.app` direct; werkt (Steam draait al) en laadt de mods uit Addons/Mods gewoon.
- Spel afsluiten: `osascript -e 'tell application "Cities" to quit'`, procesnaam is `Cities`.

## Directe start van Cities.app werkt niet meer (nov 2026)
- **Wat:** `open Cities.app` start en sluit direct af; Player.log: `PlatformServiceNotRunningException: Platform service is not running (Steam-Steam)`, ook zonder argumenten. Eerder (zie boven) werkte dit wel.
- **Fix:** binaire direct draaien met `SteamAppId=255710 SteamGameId=255710 ./Cities --continuelastsave` (in `Cities.app/Contents/MacOS`). Steam initialiseert dan zonder launcher en de laatste save laadt vanzelf. Getest: `scripts/restart-game.sh` is volledig automatisch (~50 s).
- Niet gelukt: via `steam://rungameid` (opent de Paradox Launcher, wil een klik op Resume; `--continuelastsave` meegeven helpt daar niet) en klikken via osascript (terminal heeft geen Toegankelijkheid-rechten).

## /unlock opent popups die de simulatie vastzetten
- **Wat:** na `UnlockAllProgressionMilestones` e.d. staat `SimulationManager.ForcedSimulationPaused` op true (UI-venster); de klok staat stil ook al is `paused:false` en speed 3. Zonder uitlezing van `forced_paused` lijkt het alsof er niets groeit.
- **Fix:** `/stats` en `/pause` tonen `forced_paused`; `/unlock` wacht 1,5 s en zet de forced pause terug; `/pause {"paused":false,"force":true}` doet hetzelfde. Het venster zelf blijft zichtbaar tot iemand het sluit.

## Kaartgrenzen en bouwen
- Bouwbaar gebied is 5x5 tegels van 1920 m: x en z lopen van -4800 tot 4800 (niet 8640). Alleen ontgrendelde tegels zijn bebouwbaar (`OutOfArea` anders; zie `/areas`).
- Gebouwen aan een weg (`PlacementMode.Roadside`) snappen via `BuildingTool.FindClosestZone` (private, via reflectie) naar zoneblokken binnen 8 m; zonder weg weigert `/building/place`.
- Wegen via `NetTool.CreateNode` (statisch): `test:true` eerst, dan `test:false`; het spel splitst lange wegen zelf. Beginpunt van een stroomlijn niet in de footprint van een gebouw leggen (`ObjectCollision`).
- Zonecellen bestaan alleen naast wegen (max 4 diep); `ZoneBlock.SetZone` + `RefreshZoning(blockId)`.
- zsh splitst `$p` niet in woorden: gebruik `${=p}`.
- MCP `postTool`/`getTool` nemen `server` als eerste argument (de oude signatuur klopte niet met de aanroepen).

## Popups blokkeren het spel; sluiten vanuit de mod
- Na het laden staat `GenericTutorialHelper` open; popups kunnen `ForcedSimulationPaused` zetten. Klikken/toetsen sturen vanuit de terminal lukt niet (geen Toegankelijkheid- of schermopname-rechten; `CGEvent` wordt genegeerd).
- Fix: `GET /ui` en `POST /ui/close {"name":...}` (UI-code via `ThreadHelper.dispatcher`, de Unity-hoofdthread; niet via `SimulationManager.AddAction`). `restart-game.sh` sluit de tutorial automatisch.

## Camera en screenshots
- `Application.CaptureScreenshot(path, 1)` (Unity 5) schrijft pas aan het einde van de frame: na het aanroepen wachten tot het bestand bestaat en niet meer groeit. UI/camera-code moet op de hoofdthread (`ThreadHelper.dispatcher`), niet via AddAction.
- `UIView.Show(false)` helpt niet: `CameraController.Update` zet de UI elke frame terug, behalve in `m_freeCamera`-modus. Voor een screenshot zonder UI: tijdelijk `m_freeCamera = true` + `UIView.Show(false)`, daarna terugzetten.
- Blur aan de randen komt van `m_TiltShiftAmount` (SavedFloat) en `m_DOFMode` (SavedInt), privé velden van `CameraController`; via reflectie tijdelijk op 0 zetten.
- Tijd van de dag: `m_dayTimeOffsetFrames = (uur * DAYTIME_HOUR_TO_FRAME - m_referenceFrameIndex) & (DAYTIME_FRAMES - 1)`.
- Het milestone-venster na `/unlock` heet `UnlockingPanel` (modaal); `/unlock` sluit het nu zelf. `/ui` filtert panelen op diepte <= 3 en minimale grootte (anders vind je het niet).
- Screenshots staan in `$TMPDIR/csbridge-screenshots/`; de MCP-tool `screenshot` leest het bestand en geeft het als afbeelding terug.

## Openbaar vervoer
- Een lijn (`TransportManager.CreateLine` + `TransportLine.AddStop`) rijdt pas als hij `Complete` is: sluit hem door als laatste het eerste stoppunt nog eens toe te voegen (index -1). Eerste stop index 0, daarna -1 (aan het eind).
- Straatlijnen (Bus/Tram/Trolleybus): stoppositie = `GetClosestLanePosition` (voetgangersstrook, dan voertuigstrook) + `NetLane.CalculateStopPositionAndDirection(128/255, stopOffset)`: dus het midden van het dichtstbijzijnde wegvak, `fixedPlatform: true`. Stations (trein/metro/schip/vliegtuig): `BuildingAI.CalculateSpawnPosition` (12 seeds, nu dichtstbij), `fixedPlatform: false`.
- Bussen komen uit een depot (`Bus Depot` plaatsen); treinen en metro uit het station zelf. Vervoer begint na enkele spel-dagen (voertuigen worden pas daarna gespawnd).
- Stationssporen: eindknopen van "Train Station Track" zijn `Untouchable` maar wel aan te sluiten (`CountSegments() == 1`); `build_road` met "Train Track" snapt dan op beide stations.
- Het spel maakt virtuele netwerken ("Train Line", "Pedestrian Connection Inside", `TransportLineAI`) aan; `/map` verbergt ze standaard (`include_virtual=1` toont ze).
- Prefabs staan pas als `unlocked` nadat de simulatie een stap heeft gezet na `/unlock` (de milestones worden daar verwerkt), niet meteen.

## Opslaan en laden
- `/save` gebruikt het eigen `SavePanel` (`SaveGame(name)`, hoofdthread) en wacht tot het bestand klaar is; een bestaande naam wordt geweigerd tenzij `overwrite=true`. Nooit overschrijven wat de speler zelf heeft opgeslagen.
- `--continuelastsave` kiest de save met de nieuwste tijdstempel *in de metadata* (`SaveHelper.GetLatestSaveGame`), niet de bestandsdatum. Een gekopieerd `.crp` blijft de interne naam van het origineel houden (bv. `mcptest`) en wordt dus nooit "laatste".
- `/load {name}` doet wat Quick Load doet (`LoadingManager.LoadLevel(meta.assetRef, "Game", "InGame", sim)`), zoekt op de interne assetnaam. De API valt 30-60 s weg; de mod start opnieuw.

## Automatisch popups sluiten mag nooit het pauzemenu raken
- Eerste versie sloot elk paneel dat bij het laden nog niet zichtbaar was: dat was ook `(Library) PauseMenu`, `Menu` en `ChirperPanel`. Gevolg: zwart scherm bij Esc voor de speler. Fix: alleen een vaste lijst bekende popups (`GenericTutorialHelper`, `(Library) TutorialPanel`, `UnlockingPanel`). `GET /ui` toont `auto_dismissed`-tellers; `POST /ui/autodismiss {enabled}` zet het uit.

## Wegen: bruggen, tunnels, richting
- Hoogte per punt als `x,z,e` (meter): brug = positieve `e` over water (12 m werkt), tunnel = negatieve (`-12` geeft `Basic Road Slope` + tunnel). Het spel kiest zelf de brug-/tunnelvariant; de uitgangs- en ingangshelling zit in de eerste/laatste segmenten.
- Begin- en eindrichting van een segment moet de reisrichting `dir` zijn; met een verkeerde richting kloppen de controlepunten niet en krijg je rare bochten of `TooShort`/`SlopeTooSteep`.
- Stroomlijnpalen kunnen niet op een weg staan (`ObjectCollision`): leg ze 22 m naast de wegas. Een lijn begint dus ook niet op een kruispunt.
- Segmenten korter dan ~30 m geven `TooShort`; vlak langs een bestaande weg start je met een knoop van die weg.

## Pijpen en stroom: losse eilanden
- De meeste "het werkt niet"-gevallen waren losse netwerkdelen: controleer met componentenanalyse (knoop-ids uit `/map`) of pijpen, stroomlijnen en wegen één component zijn. Een weg die in het midden van een segment eindigt, maakt geen kruising tenzij hij eindigt op een knoop of er een kruisend stuk overheen ligt.
- Een waterpomp en uitlaat hebben zelf stroom nodig (anders capaciteit 0, ook al zit de pijp goed); op water kan een Advanced Wind Turbine ernaast.
- Gebouwknopen (pomp, uitlaat) zijn `Untouchable` met 0 segmenten: `FindSnap` moet die toestaan (`CountSegments() > 1` overslaan). Leg de pijp zo dat hij *op* de knoop eindigt (begint hij erop, dan geeft het spel vaak `ObjectCollision`) en dat het eerste punt op >=25 m ervoor ligt. `WaterNotConnected` verdwijnt pas na een knoopupdate.
- Pomp stroomopwaarts, uitlaat >=300 m stroomafwaarts (rivier loopt van NO naar ZW); controleer `water.pollution` in `/services`.

## Groei: waarom de bevolking niet komt
- Nieuwe inwoners komen via de snelweg: zonder wegverbinding met het snelwegnet (zelfde wegcomponent) komt niemand. Controleer dit eerst.
- Woningvraag (`ZoneManager.CalculateResidentialDemand`) zakt als er veel lege woningen zijn; instroom is een vast tempo (~40-60 per minuut op snelheid 3), ook met genoeg huizen. Meer industrie en banen helpen; `POST /demand/full` zet de ontwikkelaarsoptie "Full Demand" aan als de vraag vastloopt.
- Spreid niet: grote lege blokken met huizen alleen langs de weg geven een kaal raster. Bouw een compacte wijk (blokken ~96x112 m), industrie apart, handel/kantoor als buffer (zie de Beginner's guide op de Paradox-wiki).

## Diagnose, weer, camera volgen, metro
- `/networks` = union-find over `NetSegment.m_startNode/m_endNode` per service (Road zonder `TransportLineAI`, Water, Electricity). Eén component per soort = gezond; voor wegen moet de component een `Highway` bevatten (anders komt niemand). Dit had me vandaag uren handwerk bespaard.
- Weer: `WeatherManager.m_targetRain/Fog/Cloud/Temperature`; het weer draait alleen als `m_enableWeather` of `m_forceWeatherOn > 0` (die loopt per simstap 0.001 af, dus 100000 zetten).
- Camera volgen: `CameraController.SetTarget(InstanceID, Vector3.zero, true)` op de hoofdthread; `ClearTarget()` stopt het.
- Werknemers van een gebouw: `CitizenUnit` met flag `Work` in `m_citizenUnits`; arbeidsplaatsen via `PrivateBuildingAI.CalculateWorkplaceCount`.
- Metro bovengronds werkt: `Overground Metro Station 01` (roadside, 16x4), sporen `Metro Track Ground 01` tussen de stationssporen (eindknopen snappen), daarna `create_transport_line type=Metro`; na een paar spel-dagen rijdt er een trein. Tram-prefabs ontbreken in deze installatie.


## ObjectCollision zonder oorzaak / stil verdwijnende objecten
- **Wat ging mis:** `build_road` meldde "ok" terwijl er palen en een stroomlijn waren weggehaald; ObjectCollision noemde niet wat botste.
- **Waarom:** `NetTool.CreateNode` (test=true) vult de botsmaskers van `ToolController` (privé `m_collidingSegments2`/`m_collidingBuildings2`, bitmasks per id). Niet-belangrijke objecten worden bij het echte bouwen weggehaald, belangrijke geven ObjectCollision.
- **Fix:** na de testpas de maskers via reflection lezen (`RoadEndpoints.CollidingObjects`) en teruggeven als `removed`/`would_remove` of in de foutmelding. Ook voor `place_building`.
- **Let op:** `get_map` verbergt nu pijpen/stroomlijnen/paden standaard en geeft compacte arrays; `verbose=true` of `all=true` voor de oude uitvoer.

## Metro/trein "niet vrij te spelen" met unlock
- **Wat ging mis:** `unlock` liet metro en trein dicht (`Metro Track Ground 01` unlocked=false), `get_progress` toonde `current_milestone: null`.
- **Waarom:** die zijn geen bevolkingsmijlpalen maar bouwmijlpalen: sporen vragen "Construct any metro station", de metrolijn "Construct Metro Tracks", treinlijn "Pass 2 requirements". Een ManualMilestone/CombinedMilestone laat zich niet forceren.
- **Fix:** eerst een station-gebouw plaatsen, dan sporen, dan de lijn. `unlock_milestone` in `/prefabs` en `/transport/types` toont de eis; `/progress` toont de bevolkingsmijlpalen.

## MakePoint: direction vergeten = overal TooShort
- **Wat ging mis:** na een refactor van `MakePoint` gaf elke weg `TooShort`, ook 100 m lange.
- **Waarom:** `cp.m_direction = dir` was uit de functie gevallen; zonder richting rekent NetTool met een nulvector.
- **Fix:** richting altijd zetten. Bij snappen op een bestaand knooppunt neemt het punt nu hoogte en elevatie van dat knooppunt over (ondergronds negatief), zoals de eigen NetTool.

## Mod-HTTP-server is single-threaded
- Parallelle requests (Promise.all in `get_city_summary`) lieten er een deel mislukken met "Cannot reach the mod". Doe MCP-aanroepen naar de mod na elkaar.

## Opslaan van andermans stad
- `/save` slaat altijd de nu geladen stad op: controleer eerst met `get_stats` dat het jouw stad is (de gebruiker kan zelf een andere stad hebben geladen). Zet `set_full_demand`, `set_guard` e.d. nooit aan in een stad die je niet zelf bouwt.

## Ondergrondse metro (niet opgelost)
- **Wat ging mis:** `Metro Station Below Ground Island` / `Metro Station Track` zijn alleen netwerken: ze maken geen stationsgebouw, dus `create_transport_line` vindt geen station. De uiteinden van het eiland-station liggen op maaiveld (Elevation 12, niet Underground) en zijn te stijl om er een `Metro Track` aan te koppelen (SlopeTooSteep). De gebouwen `Underground-Ground-Elevated Metro Hub`, `Integrated Metro Station`, `Metro Entrance` gaven in de testkaart ObjectCollision/SlopeTooSteep.
- **Wat wel werkt:** bovengrondse metro (`Overground Metro Station 01` + `Metro Track Ground 01`). Gebruik `/nodes?x=&z=` om hoogte, elevatie en Underground-vlag van knooppunten te zien.
- **Mogelijke weg verder:** de Hub op vlak, vrij terrein naast een eigen weg proberen (eerst `edit_terrain flatten`).

## Tools die iets slopen of veranderen
- `/bulldoze/area` verwijdert ook bestaande bomen: altijd eerst `dry_run`. `/terrain/edit` verplaatst geen wegen of gebouwen. Beide hebben geen undo: sla eerst op onder een nieuwe naam.
