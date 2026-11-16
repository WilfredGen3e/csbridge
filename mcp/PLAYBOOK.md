# How to play Cities: Skylines with CSBridge

You control a running Cities: Skylines 1 through tools. Plan like a city planner, build in small verified steps, and look at the result.

## Save tokens
- Start with `get_city_summary` (one call: stats, demand, problem counts, traffic, road/water/power connectivity) and repeat it after each build step instead of five separate calls.
- `get_map`: radius <= 200, a low `limit`, and filters (`service`, `road`, `prefab`, `include`, `counts_only`). Pipes, power lines and footpaths are hidden unless you pass `all=true` or `road=`. For one building use `get_building`.
- `build_road` answers with segment ids only; `verbose=true` gives details. Use `dry_run=true` to test: it reports what would be removed. A real build lists in `removed` every existing object (poles, lines, buildings) the new road replaced, and an ObjectCollision error names the objects in the way.
- `build_many` runs several roads, zones and buildings in one call; `build_road` with `with_pipe=true` lays the water pipe along the same line.
- `screenshot` with `width=640` costs far fewer tokens than full size. `load_save` waits until the city is ready.
- Before running the game at speed 3 while you plan, switch on `set_guard`: it pauses the game when water, sewage or electricity capacity drops below consumption.
- `get_progress` lists the milestones with their requirement; `list_prefabs` shows `unlock_milestone` for locked items.

## Rules of the house
- Check `ping`, then `get_stats` and `get_areas`. Never overwrite a save you did not create: `save_game` refuses existing names, keep it that way. `load_save` and a game restart drop unsaved progress, so save first.
- Build step by step: one neighbourhood at a time. After each step verify with `get_services`, `get_problems`, `get_demand` and a `screenshot` (set_time 12, set_camera top-down or isometric first).
- Move the camera (`set_camera`) to where you build, so a person watching the game sees it happen.
- Popups (tutorial, milestones) are closed by the mod automatically. If the game seems frozen, check `get_ui` and `get_stats.forced_paused`.
- Coordinates are metres, x = east, z = north, buildable range -4800..4800 on unlocked tiles only (`get_areas`).

## Order of work (matches the Paradox beginner's guide)
1. Look first: `get_terrain`, `get_map` (no arguments for an overview), `get_areas`. Find flat land, the rivers (water flows downhill; note which way) and the highway/outside connections.
2. Utilities before zoning: water intake upstream, sewage outlet at least 300 m downstream (check `water.pollution` in get_services stays 0), power. A pump or outlet needs electricity itself.
3. Road hierarchy: highway -> collector (Medium Road) -> local streets (Basic Road). No residential streets straight onto a highway. Grid blocks of about 96 x 112 m: zone cells exist only beside roads and 4 cells (32 m) deep, so wider blocks stay empty. Use roundabouts/T-junctions, few left turns.
4. Connect the new roads to the highway network (same connected road component) or no one will ever move in. Check this before waiting for growth.
5. Zoning: compact residential (high density in the centre and along the spine, low on the edges), commercial and office as a buffer between homes and industry, industry in a separate area near the highway exit reached by one road. Never scatter industry among homes.
6. Services per neighbourhood: fire, police, clinic/hospital, elementary and high school, parks in the middle of blocks, garbage (landfill near industry, not near homes), cemetery, bus depot and bus lines (`create_transport_line`). Finish one neighbourhood completely before starting the next.
7. Public transport: bus lines on roads (needs a Bus Depot, vehicles start after a few game days); above-ground metro between two `Overground Metro Station 01` joined with `Metro Track Ground 01`. Show off a line with `get_vehicles` + `follow`.
8. Run the game (`set_paused false`, `set_speed 3`), watch demand and problems, adjust taxes (`set_taxes`, lower taxes = more demand) and budgets.

## Building network pieces (build_road)
- Roads, rails, pipes (`Water Pipe`) and power lines (`Power Line`) all use `build_road`. Pipes can run under roads and across open land; they carry water and sewage. Pipes along every road centre line serve the buildings beside it.
- Power: power spreads between nearby buildings; use power lines for distance. Poles cannot stand on a road: put lines about 22 m beside the road, and do not start or end a line on a junction. Wind turbines work on land (`Wind Turbine`), on water (`Advanced Wind Turbine`); coal plants need fuel trucks, avoid them.
- Water intake/outlet: place the building first, then lay the pipe so it ENDS on the building's pipe node (a `Water Pipe Junction` appears about 17-23 m in front of it); start the pipe >= 25 m away. A pipe that starts on that node often fails with ObjectCollision. If `get_problems` shows WaterNotConnected, the node is not joined.
- Bridges and tunnels: give points as `x,z,e` with elevation `e` in metres (12 over a river, -12 for a tunnel) and a bridge/elevated or slope/tunnel prefab; the game picks the variant.
- Segments shorter than ~30 m give TooShort. A road ending in the middle of another segment does not always make a junction: end on an existing node or cross it.
- Most "it does not work" cases are disconnected network parts. Run `check_networks` for road, water and power: each should be ONE component (roads: it must contain the highway). Pass x and z to see which component a place belongs to. `get_building` shows why one building has no power/water.

## Diagnosing
- `get_problems` lists problem types with sample buildings: Electricity, Water, Sewage, NoWorkers, WaterNotConnected, NoFuel.
- Water/sewage capacity 0 -> pump or outlet unpowered or unconnected. Electricity problem on a few buildings -> not in range of the grid.
- Slow growth: no highway connection; too many empty homes (demand collapses); few jobs. New residents arrive at a fixed rate (~40-60 per minute at speed 3). `set_full_demand true` is the game's developer switch that forces demand to 100 for sandbox play; use it only when the user is fine with that.
- Unemployment with open jobs usually means an education mismatch; build schools, mix jobs.

## Do not
- Do not build zones and then leave huge empty blocks, a ring of houses along roads, or industry beside homes: it looks and plays badly.
- Do not use `unlock` for normal play unless the user wants a sandbox; it opens all milestones.
- Do not ask the user for things you can read yourself with the tools.
