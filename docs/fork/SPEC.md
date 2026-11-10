# placemaker-advanced: specification

Fork of `jackwrichards/placemaker` (GPL-3.0) owned by GitHub account `melv-narrow`. Target game:
Cities: Skylines II **1.6.3f1**. This spec says *what* the fork must do and how we know it works. The order of work
is in `PLAN.md`. The original brief is `PLAN-v1.md`; this spec supersedes it where they differ.

Every fact below was checked in the source on 2026-11-10 (file:line given). Items marked **(spike)** are unproven and
have a research step in the plan before any feature code depends on them.

---

## 0. Ground rules

- GPL-3.0 stays. `NOTICE.md` credits the original author and the three merged projects (ParkingLotTool,
  ParkManager, Grid Road Generator).
- Never delete code to remove a feature; hide it via `Recipes/shelf/<category>.json`.
- Done means seen in the real game through the game runner, with `Placemaker.log` and pictures read. A compile or
  unit test alone is never "done".
- Pure logic lives in `Core/` with xUnit tests; engine calls live in `Game/`.
- New persisted data is versioned (version first, append only), per RESEARCH-NOTES rule 5.
- Every new behaviour that changes the world has a setting. Old builds without the new data keep today's behaviour.
- No AI attribution in commits or PRs.

## 1. Workstream F: fork foundation

| ID | Requirement | Acceptance |
|---|---|---|
| F1 | Builds against 1.6.3f1. Today the compile check fails with 3x `CS1069 Queue<>` (1.6.3 moved `Queue<T>` into the game's mscorlib). | `dotnet build Placemaker.csproj -c Release -p:Deploy=false -p:SkipUI=true` clean. |
| F2 | Unit tests run on this machine (they target net9; only SDK 8 is installed). | A filtered `dotnet test` run passes. |
| F3 | Game runner works on this machine: no `C:\Users\jack\...` default (6 scripts: Show, Send-Suite, Run-Suite, Contact-Suite, GameQueue, ModsInstall), game path from one configurable place instead of hardcoded Steam path (Run-Suite.ps1:84, Launch.ps1:11). | `Show.ps1 -Type park` completes and writes results inside this repo's `.forensics/`. |
| F4 | Fork identity: new mod id and display name (only if published), `NOTICE.md`, README note. | Mod loads in game under its own name, no clash with an installed original. |
| F5 | 1.6.2 data still valid on 1.6.3f1: VanillaParks table, park prefab values, terrain palette, decal and road names. | Startup log reports no missing prefab or palette entry. |

## 2. Workstream A: removal restores the land (highest priority)

### Facts that shape the design
- Removal (`GroupRemovalSystem`, PreTool) deletes members only. Final block at GroupRemovalSystem.cs:173-184; a
  second exit, `GiveUp`, at :284. Neither touches terrain or paint.
- Terraform writes with `TerrainSystem.ApplyBrush(Shift, ...)` (BuildService.Terrain.cs:308). `_tfApplied` holds only
  the last unmeasured pass. The raster stores targets and weights, **not** pre-build heights, so the post-build
  height of a bank cell cannot be derived; it must be measured.
- CPU heights (`GetHeightData`) include clipping to roads, buildings and lots, which vanishes when those nets go.
  The GPU base heightmap (`TerrainSystem.heightmap`, 4096^2 R16) does not. A proven read and write path exists:
  `SuiteFixture.ReadHeightmap` (AsyncGPUReadback) and `Replace` (`TerrainSystem.ReplaceHeightmap`)
  (Game/Suite/SuiteFixture.cs:228-264), used today by the game runner to restore test blocks.
- Paint is GPU-only (`TerrainMaterialSystem.ApplyBrush` with a stamp mask, BuildService.Paint.cs:191-227). Splatmaps
  are private fields `m_UserSplatmap` (4096^2) and `m_UserSplatmapWorld` (1024^2). No splatmap readback exists.
- Leftover "odd textures" are almost certainly paint: surface and decal clone systems create prefabs only, and
  instances are tagged as group members (BuildService.Adopt.cs:296-359).
- `PinMarkerNets` adds `Elevation(0)` to **both end nodes** of marker edges without checking group membership
  (BuildService.Pits.cs:104-109). A city node joined to a marker edge keeps it after removal: a candidate cause for
  the "road becomes a bridge" report.
- Group id is an `int`, `max + 1` (GroupRegistrySystem.cs:311-321): reused after the highest group is removed and
  not unique across cities. The receipt codec caps lists at `1_000_000` (ReceiptCodec.cs:23).

### Requirements

| ID | Requirement |
|---|---|
| A-R1 | Each build gets a stable `BuildGuid` (receipt v3). Sidecars and snapshots are keyed by it, never by group id. |
| A-R2 | Before the first terraform or paint write, capture the **GPU base heightmap**, world heightmap and both user splatmaps over outline + terraform and paint rings + 24 m. Builds that neither terraform nor paint write nothing to the ground and get no snapshot. *(As built.)* |
| A-R3 | After the build settles, capture the same texels again (post state). |
| A-R4 | **(spike)** Capture splatmap texels (both maps) for the paint footprint before and after painting. If readback or region write-back is not reliable, use the fallback in A-R7. |
| A-R5 | Snapshots are stored as a Deflate-compressed, versioned sidecar `ModsData/Placemaker/restore/<BuildGuid>.bin` holding the full before and after rectangles (unchanged texels compress to almost nothing). Measured 0.1 to 3.6 KB per build; size logged. *(As built.)* |
| A-R6 | On removal (final block; **not** on `GiveUp`, where elements left in the city stand on the build's ground), queue a restore that runs a few frames after the members' `Deleted` is processed, from `BuildService` (which already owns apply-wait-measure). Per texel: restore the pre value only where current equals post (exact for R16 texels, small tolerance allowed); otherwise skip as player-modified. |
| A-R7 | Paint: restore pre texels where current equals post. Fallback if A-R4 fails: per-material `EraseDither` inside the stored paint mask only, documented as also erasing player paint of the same material in that mask. |
| A-R8 | After removal, `Elevation` added by `PinMarkerNets` to nodes that are not group members is removed. Pinning stops marking foreign nodes, or records which ones it marked. |
| A-R9 | Edit and rebuild (`DoWaitReplaced`, BuildService.cs:362-367) waits until the old build's restore has finished before the new build reads "before" ground. |
| A-R10 | Builds that fail or are cancelled after their first terrain write also restore (today `DoCleanup` without `_applied` leaves terraform and paint, BuildService.cs:654-673). |
| A-R11 | Overlapping builds: removing an older build never undoes a newer one. Guaranteed by the current == post rule; covered by a test. |
| A-R12 | Setting `RestoreLandOnRemove`, default on. Builds with no snapshot behave as today, logged once per removal. |
| A-R13 | One log line per removal: texels restored, skipped as modified, max residual (m), paint result. *(As built.)* |
| A-R14 | A "Remove all Placemaker builds" action (Settings) so a player can leave the mod with clean land. |
| A-R15 | Sidecars are never deleted automatically: an older save of the same city may still hold the build and remove it again later. They are a few KB each. *(As built; a manual clean-up can come later.)* |
| A-R16 | Ordering *(added after review)*: a build waits before its "before" capture while any restore or group removal is pending; removal restores wait while a build holds a capture and until no removal is in progress, then run newest build first; a failed build rolls back completely (before or after Apply). |

### Write-back method **(spike decides)**
- **Option 1, ReplaceHeightmap patch** (preferred if safe): read current base heightmap, patch footprint texels,
  `ReplaceHeightmap`. Exact, already used by the runner. Unknown: side effects on water, world map, and cost of a
  32 MB full-map write in a live city.
- **Option 2, ApplyBrush delta**: convert per-texel deltas to Shift mask textures, apply, wait, measure (the build's
  own loop). Proven in a live city but approximate (0.3 m tolerance, bank smoothing).

### Acceptance (game runner round trip, `suite/scenarios/`)
1. Build parking, a shops type and a park with `-Modes terrain`; remove by bulldozing the owner lot.
2. Max height residual inside outline + rim <= 0.3 m (Option 2) or exactly 0 texels changed (Option 1).
3. Splatmap footprint identical to before (or, on fallback, none of the build's materials left in its mask).
4. Zero entities left carrying the group id, and `ReportLeftovers` silent.
5. A vanilla road laid across the old footprint has no `Elevation` and sits on the ground.
6. Build, then a player terraform inside the footprint, then removal: the player's change survives.
7. Build A, build B overlapping, remove A: B's ground unchanged.
8. Save, quit, load, remove: restore still works (sidecar found by GUID).
9. Edit (rebuild) of a terraforming park: no compounding dip or mound.
10. The upstream crash sequence (4th build of park-garden) is no easier to hit than before.

## 3. Workstream B: the city recognises parks

### Facts
- Owner lot gets `AttractivenessProvider` (tourism only, BuildService.ParkStats.cs:123-137).
- Leisure, park coverage and land value come from a hidden 1x1 clone of a vanilla park (`ParkCompanionSystem`),
  placed **inside** the outline at an edge within 6 m of a road (Core/Build/ParkCompanion.cs:116-205). Only the
  first source prefab found is used.
- No frontage is logged, but under one reason key for every failure (BuildService.ParkStats.cs:102-107).
- Review already shows a prediction: `ParkStatsView.companion: "yes" | "noRoad" | "off"` (UI/src/contract.ts:464).
- Gaps: no road-connection check after creation; a companion never created (state 0) or lost (state 1) is not
  re-requested on load; settings toggles apply only after reload; uninstall leaves companions with obsolete prefabs
  (untested); the lot's AttractivenessProvider stays; `park-companions.txt` only grows.

### Requirements

| ID | Requirement |
|---|---|
| B-R1 | Frontage search returns a specific reason: no road within range, no edge long enough, lot does not fit, blocked by objects or paths, blocked by zoning. Logged and shown. |
| B-R2 | After creation, verify the companion has a road connection (vanilla `Building.m_RoadEdge` or equivalent); log result. |
| B-R3 | Review keeps the prediction and adds the reason. Managed groups list gets a post-build status: recognised / tourism only / not recognised, with reason (`ManagedGroup.stats`, contract.ts:484-491). |
| B-R4 | *(As built: warn only.)* A relaxed search was tried and dropped, because the building's front must touch the road to be connected. The player gets the specific reason and "draw the area up to the kerb". Original: fallback when there is no clean frontage: try more outline positions and sizes; if none, warn clearly. Never a mesh-less building (RESEARCH-NOTES 16). Connector road only if it is low-risk, decided in B0. |
| B-R5 | On load, re-request missing companions (state 0 or 1). Settings toggles apply live. |
| B-R6 | Uninstall path tested: obsolete-prefab load does not crash; documented steps; the "remove all" action (A-R14) also covers it. |
| B-R7 | `park-companions.txt` is never pruned (shared by every save on the machine). *(Decided in B0.)* |

### Acceptance
Game runner, several park types, with and without road frontage: companion exists or a specific reason is shown; the
companion has a road connection; vanilla info panel, Leisure and Park coverage overlays show it; citizens' leisure
counter moves in a city with traffic.

## 4. Workstream C: farms that produce (research first)

### Facts
- No production, resource or revenue code exists. Farmstead, UrbanFarm and AnimalPark are scenery in group "farms";
  OrchardPark is group "parks". All extend `LeisureLayer`, so farms today get **park** stats and a park companion
  (Core/Build/ParkStats.cs:170-176).
- The Emit rule (BuildService.Emit.cs:443-454) blocks vanilla extractor/storage lots outside farms and industry, but
  farms are exempt: a farm recipe naming a lot explicitly would bring back the `CanOverrideObjects` hiding bug.

### Requirements
- C-R1: `docs/fork/FARM-ECONOMY.md` from decompiled 1.6.3f1 code: how vanilla farms produce, minimum entity set,
  hidden-companion feasibility, save compatibility on uninstall, risks. Two or three options with costs and a
  recommendation.
- C-R2: decide whether farms keep park stats.
- C-R3: only after review, build the chosen option for `farmstead` behind a setting defaulting **off**. Acceptance:
  company exists, produces, moves resources, shows in economy panels; removal leaves no orphan company or workers;
  trees and props inside fields stay visible.

## 5. Out of scope (known upstream bugs, not regressions)
Roads presets strict-grid and superblocks build nothing; build freezes of 1.4 to 2.7 s; plazas and market squares
face city streets. The `CullForCascades` crash is only in scope as acceptance item A-10.

## 6. Open questions for the owner
1. Publish on Paradox Mods eventually, or private? (Decides F4.)
2. Is a release with Workstream A alone acceptable?
3. A save with real traffic for B checks (B acceptance needs citizens).
4. If the splatmap spike fails, is the paint fallback acceptable (player paint of the same material inside the
   build's footprint is erased on removal)?
