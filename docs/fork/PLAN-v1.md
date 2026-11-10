# Placemaker fork: "builds that are real and clean up after themselves"

Starting plan for Claude Code. Written from a read of the upstream repo
(https://github.com/jackwrichards/placemaker, GPL-3.0, Cities: Skylines II 1.6.2f1 code mod).
Facts below were read from the source and docs. Anything marked **(inference)** was NOT verified in code or in the
game and must be confirmed before building on it.

---

## 0. Working agreement (read first)

1. Read `AGENTS.md` in the repo root before touching anything. Then `docs/ARCHITECTURE.md`, `docs/RESEARCH-NOTES.md`
   and `docs/RESEARCH-NOTES-ADDENDUM.md` (decompiled engine rules, treated by the author as a mandatory checklist),
   `docs/TERRAIN-PAINT.md`, `docs/PARK-STATS.md`.
2. Nothing counts as done until it has been seen working in the real game through the game runner
   (`tools/suite/Show.ps1`, via the queue, with `-Who <task-name>`). Read `Placemaker.log` and the pictures yourself.
   Never report "fixed" from a compile or a unit test alone.
3. Never delete code to remove a feature. Hide it through the shelf (`Recipes/shelf/<category>.json`).
4. Fix mechanisms everywhere they occur, not only in the one screenshot.
5. One git branch per workstream. Small commits. **Do not add `Co-Authored-By` trailers or any co-author line to
   any commit.**
6. License: the mod is GPL-3.0 and merges ParkingLotTool (KruemelGames), ParkManager (tomkpunkt) and Grid Road
   Generator (Ti4goc). The fork stays GPL-3.0, keeps all credits, and adds a NOTICE.md listing the original author
   and every merged project.
7. Tests: `dotnet test Tests/Placemaker.Tests --filter ...` on what you touch only. The full run takes 20+ minutes.
   Build: `dotnet build Placemaker.csproj -c Release -p:Deploy=false`. Compile check only: add `-p:SkipUI=true`.

### Environment caveats
- Needs Windows, PowerShell, Cities: Skylines II 1.6.2f1 installed, and a .NET SDK.
- `AGENTS.md` and some scripts mention the original author's machine paths (`C:\Users\jack\...`). Check
  `tools/suite/*.ps1` and `suite/README.md` and make the paths configurable before running the game runner.
- Decompiled game source is not in the repo. Regenerate with `ilspycmd` from `Cities2_Data\Managed\Game.dll`.
- Known upstream bugs, do not mistake them for regressions: road presets `strict-grid` and `superblocks` build
  nothing; a native crash in `TerrainSystem.CullForCascades` on the 4th build of a long `Show.ps1` park sequence;
  build freezes of 1.4 to 2.7 s; plazas and market squares have shops facing city streets.

### Fork housekeeping (do first, small)
- Fork the repo, add NOTICE.md, keep the LICENSE.
- If this will ever be published, change the mod id and display name so it does not clash with the original.
- Make the runner paths configurable (see above).

---

## 1. Workstream A: removal must restore the land (highest priority)

### Reported symptoms (from a real player)
1. Deleting a placemaker object leaves odd textures that cannot be removed.
2. Deleting it can leave invisible gaps in the land.
3. A road built over those gaps becomes a bridge instead of a flat road.

### What the source shows (verified by reading)
- `Game/Groups/GroupRemovalSystem.cs` removes group members only (nets, areas, objects, companions) in a
  crash-safe phased order. It never touches terrain height or paint. The final pass is the
  `if (pass.Final && !anyTemp)` block, which also calls `ReportLeftovers`.
- `Game/Groups/GroupRegistrySystem.cs` `HandleDeletedMembers` notices that the owner lot was bulldozed and queues
  whole-group removal.
- Terraform (`Game/Build/BuildService.Terrain.cs`) applies the desired ground directly with
  `TerrainSystem.ApplyBrush` (Shift kernel, one generated mask texture per tile). It is write-only. The only
  "undo" is for calibration passes during the build (`_tfReverting`, and a method near line 312 that undoes the last
  pass exactly with the opposite strength). `_tfApplied` holds `Texture, Brush, Area` per applied pass but is only
  kept for the current build. Constants: `TerraformCell` 1.75 m, `TerraformBank` 4 m, `TerraformTolerance` 0.3 m.
- Paint is explicitly permanent: `docs/TERRAIN-PAINT.md` section 8 and the header of
  `Game/Build/BuildService.Paint.cs` say paint "is not undone when a build is removed". The game has no undo in
  game mode. Splatmap facts (same doc, section 2): R8G8 texels, four 4-bit weights, playable splatmap 4096^2 plus
  a 1024^2 world splatmap, both saved with the city.
- `Game/Build/BuildService.Pits.cs` `ScanLivePits` logs bowls 4 to 9 m deep after some builds (parking next to
  shops). `PinMarkerNets` sets `Elevation 0` on marker edges and nodes to stop `GroundHeightSystem` re-fitting them
  and digging the ground, but that protection only lives while the nets live.
- `Game/Preview/SiteCapture.cs` takes a main-thread snapshot of the world before building (height grid, idea from
  Grid Road Generator's HeightSnapshot). **Not verified:** whether any of it is retained after Apply.
- Search for a terrain restore found none.

### Hypotheses to confirm FIRST (inference, do not skip)
- H1: the leftover textures are splatmap paint. But they could also be orphaned Area/Surface entities or decals
  (`SurfaceCloneSystem`, `GhostSurfaceSystem`, `DecalOpacitySystem`, `Core/Decals`). Distinguish them.
- H2: the invisible gaps are leftover height changes (bowls, pits, bank edges). But they could also be orphaned
  marker nets or areas, or terrain clipped to a net that has since been deleted.
- H3: the vanilla road tool auto-raises a segment over a deep height deficit, which is the "bridge". Confirm by
  checking for an `Elevation` component on the resulting edge and its height against the ground.

### Step A0: reproduce and diagnose (no feature code yet)
1. With `Show.ps1`, build a type that terraforms and paints (try parking, a shops type, and a park with
   `-Modes terrain`). Record the terrain map.
2. Remove the group the way a player does (bulldoze the owner lot), then record the terrain map again.
3. Grep `Placemaker.log` for `could not remove everything` / `ReportLeftovers` output.
4. Query the world for leftover entities of every kind: PlacemakerMember, Area/Surface, decals, marker nets, anything
   with the group id. Compare before and after.
5. Lay a vanilla road across the former footprint and inspect the edge for `Elevation`.
6. Write the findings to `docs/REMOVAL-DIAGNOSIS.md`. Update this plan if a hypothesis is wrong.

### Step A1: capture the "before" state at build time
- At Apply, capture for the terraform footprint plus its bank: pre-build heights on the existing raster grid.
  After the terraform verifies, capture post-build heights (or keep the raster targets).
- Capture the splatmap texels of the paint footprint before painting, plus the post-build texels.
  Research spike first: how `PaintRaster` / `BuildService.Paint.cs` write the splatmap, and whether the splatmap
  can be read back from the GPU.
- Persistence: the snapshot must survive save and load. Find how the receipt (v2) and `PlacemakerGroup` are
  persisted and follow the same route, or write a sidecar under `ModsData/Placemaker/` keyed by a stable id stored
  in the receipt (the integer group id may not be unique across saves, check). Keep it bounded: the raster is
  capped at `1 << 20` cells.
- Must be opt-out safe: a setting, default on, and old builds without a snapshot keep today's behavior.

### Step A2: restore on removal
- Trigger in the final removal pass of `GroupRemovalSystem`, after members are gone.
- Heights: for each cell, restore the pre-build height ONLY where the current height still matches the post-build
  height within tolerance. Cells the player has changed since (their own terraforming, a road they built) are left
  alone. Apply through the existing delta -> mask texture -> `ApplyBrush` path and verify against the CPU readback
  with the same wait/stale logic the build uses (apply, wait about 6 frames, measure).
- Paint: restore stored pre-build texels where the current texel still equals the post-build texel. Fallback if
  readback is impossible: erase only the materials the build painted, inside its stored footprint mask
  (`EraseDither` is per-material), and document that player paint of the same material inside the footprint is lost.
- Respect engine ordering rules from RESEARCH-NOTES (phase of Deleted marking, tool Temp copies). Terrain edits
  are not entity edits but run them after the removal pass finishes, never in the same frame as net deletion.
- Log a one-line summary per removal: cells restored, cells skipped as player-modified, max residual.

### Step A3: tests and acceptance
- Unit tests in `Tests/Placemaker.Tests` for the pure part (the restore planner that decides per cell what to
  restore). Keep the planner in `Core/` (pure .NET + Unity.Mathematics), engine calls in `Game/`.
- Game-runner round trip scenario in `suite/scenarios/`: build, remove, diff the terrain map. Acceptance:
  - max height residual inside the footprint <= `TerraformTolerance` (0.3 m);
  - splatmap footprint identical to before;
  - zero leftover entities of the group;
  - a vanilla road laid across the footprint afterwards is a normal ground-level road (no `Elevation` raise, no
    bridge);
  - a build, a player terraform tweak, then removal leaves the player's tweak intact.
- Also run the sequence that crashes upstream (4th build park-garden) before and after, to be sure the change does
  not make the crash easier to hit.

---

## 2. Workstream B: parks must be recognized by the city

### The question
Do people interact with Placemaker parks, and does the city count them as parks?

### What the source shows (verified by reading)
- A Placemaker build is not a vanilla building. Its one selectable thing is an invisible owner lot, which carries no
  park components (`docs/PARK-STATS.md` section 5).
- The owner lot gets a vanilla `AttractivenessProvider`, so it counts for city tourism only
  (`BuildService.FinishParkStats`, setting `ApplyParkStats`, default on). Land value and tourist destinations ignore
  it because they need a `Building` and a road connection.
- Leisure, park coverage (wellbeing), land value and upkeep come from a hidden companion park building:
  `Game/Economy/ParkCompanionSystem.cs`. It is a runtime clone of a vanilla park prefab (sources `PocketPark01`,
  `PocketPark02`, `CityPark05`, `CityPark01`), one cell, flagged `Hidden`, placed at the road frontage, with
  quantised per-build stats. Setting `RealParkStats`, default on. Clone names are persisted in
  `ModsData/Placemaker/park-companions.txt`.
- Consequence: citizens path to the hidden one-cell building at the road edge, not into the park you drew. The doc
  says the companion "must stand on a road edge without colliding with our objects, which some drawn areas cannot
  offer", and lists risks that "need in-game checks". A park with no usable road frontage gets no companion.

### Tasks
- B0 (diagnose, in game): for several park types and areas (with and without road frontage), log whether a
  companion was created and why not if absent. Confirm with the vanilla info panel and the Leisure, Park coverage and
  Tourism overlays whether the city really sees them. Document in `docs/PARK-RECOGNITION.md`.
- B1: add a visible status in the Review panel, "City recognizes this park: yes / no, and why" (UI contract is
  `UI/src/contract.ts`, authoritative).
- B2: fallback placement for areas with no clean frontage (search along the outline, allow a short connector, or
  warn the player). Never create a mesh-less building (engine crash, RESEARCH-NOTES 16).
- B3: make sure companion removal and the lot's `AttractivenessProvider` are cleaned up on group removal and after
  load (`GroupRegistrySystem.SyncParkStats`, `ParkCompanionCleanupSystem`). Check the obsolete-prefab case when the
  mod is uninstalled.
- B4: game-runner scenarios with acceptance: companion exists, has a road connection, citizens' leisure counter
  moves, park coverage appears on adjacent streets.

---

## 3. Workstream C: farms that produce (research first, build last)

### The question
Do farms actually generate resources and revenue for the city?

### What the source shows (verified by reading)
- No. A search of `Game/` found no production, resource or revenue code for farms. `FarmsteadLayer` describes itself
  as "production farmland seen from the road". It builds props, painted soil and row stripes, fences, hedgerows,
  ponds and animal spawners. `UrbanFarm`, `AnimalPark`, `OrchardPark` are likewise scenery or attractions.
- `Game/Build/BuildService.Emit.cs` near line 443 deliberately blocks vanilla extractor and storage lots
  ("Agriculture Lot - Field", "Extractor Lot", warehouse yards) outside farm and industry types, and `area.lot` is
  never automatic. That rule exists because a vanilla field lot has `CanOverrideObjects`, which hid trees, bushes
  and lamps inside it. Any real-farm work must not reintroduce that bug.

### Step C0: research spike (output is a document, not code)
Write `docs/FARM-ECONOMY.md` from the decompiled game (`ilspycmd`), not from memory. Answer:
- How do vanilla farms work: extractor areas and their components, the extractor company, the farm building and
  its sub-areas, what resources come out, and what ground fertility or other natural resource they depend on.
- What the minimum set of entities and components is for a field or pasture to produce and for the company to earn.
- Whether a hidden companion approach (like parks and parking) can work for farms, or whether real vanilla farm
  buildings and extractor area prefabs must be placed.
- The save-compatibility story if the mod is removed.
- Risks: crashes, `CanOverrideObjects` side effects, economy balance.
Present two or three design options with costs, and recommend one.

### Step C1: only after the spike is reviewed
Build the chosen option behind a setting that defaults to **off**, for `farmstead` first. Acceptance in game: the
farm company exists, resources are produced and moved, and the city's economy panels show it; removal leaves no
orphan company or workers.

---

## 4. Suggested order and effort

1. Fork housekeeping, then A0 diagnosis (small, decisive).
2. A1 to A3 terrain and paint restore (medium, contained, fixes the worst complaints, and the round-trip test
   becomes the regression net for everything else).
3. B0 and B1 park recognition diagnosis and visible status (small), then B2 to B4.
4. C0 farm research spike. C1 only after review (large).

## 5. Open questions for the owner

- Is the plan to keep this private, or publish it (affects naming, mod id, NOTICE.md)?
- Preferred scope if time is short: A alone is already a worthwhile release.
- Which game maps and a save with real traffic to use for the park checks?
