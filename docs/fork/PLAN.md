# placemaker-advanced: plan

What and why: `SPEC.md`. Original brief: `PLAN-v1.md`. One branch per workstream, small commits, no AI attribution.
Each step ends with evidence: log lines, pictures, or a document. A step that changes the world is not done until
seen in the game runner.

## Status (2026-11-10)

| Phase | State | Evidence |
|---|---|---|
| 0 Foundation | Done (mod id and name wait for the publish decision) | compile on 1.6.3f1, runner on this machine, NOTICE.md |
| 1 Diagnosis | Done | `REMOVAL-DIAGNOSIS.md`, `PARK-RECOGNITION.md` |
| 2 Land restore (A) | Done, seen in the game | runs `*-restore`, `*-restore-2`; save/load and player-terraform cases covered by unit tests only |
| 3 Park recognition (B) | Done, seen in the game | runs `*-parks`; citizen leisure counters and uninstall load not run |
| 4 Farms (C) | Built behind "Farms produce" (off by default), seen in the game | runs `*-farms`; hiring, harvests and exports not seen (runner cannot run the simulation long enough) |

## Phase 0: foundation (branch `fork/foundation`)
1. Commit upstream as-is plus `docs/fork/` and the fork `CLAUDE.md` section. Create the GitHub fork under
   melv-narrow (`gh auth switch -u melv-narrow` first), push.
2. F1: fix the `Queue<>` reference for 1.6.3f1 in `Placemaker.csproj` (and Tests, Gallery, MockHost if affected).
   Compile check clean.
3. F2: install .NET SDK 9 (user action) and `ilspycmd`; run one filtered test set. Decompile 1.6.3f1 `Game.dll` to a
   stable folder outside the repo (path in `CLAUDE.md`).
4. F3: one config source for paths (env vars `PLACEMAKER_RESULTS_ROOT`, `CS2_GAME_PATH`, with defaults computed from
   the repo and the Steam registry key). Update the 6 scripts plus Run-Suite/Launch. First `Show.ps1 -Type park` run.
5. F4: `NOTICE.md`. Mod id and name change waits for the answer to open question 1.
6. F5: startup log scan on 1.6.3f1 for missing prefabs and palette entries.

## Phase 1: diagnosis (branches `fork/a0-diagnose`, `fork/b0-diagnose`, can run side by side)
**A0** (output `docs/fork/REMOVAL-DIAGNOSIS.md`):
1. Show.ps1: build parking, a shops type and a park with `-Modes terrain`; capture heightmap (GPU) and terrain map.
2. Bulldoze the owner lot; capture again; diff texels.
3. Grep log for `ReportLeftovers` / `could not remove everything`; query leftover entities by group id.
4. Lay a vanilla road across; inspect the edge and its nodes for `Elevation`; check if any node is a city node
   pinned by `PinMarkerNets`.
5. Confirm or reject H1 (paint), H2 (height), H3 (Elevation on nodes vs height deficit).

**Spikes in A0** (each a short section in the diagnosis doc):
- S1 splatmap: reflect `m_UserSplatmap` / `m_UserSplatmapWorld`, AsyncGPUReadback a region, write a region back
  (CopyTexture / blit), confirm it persists through save and load.
- S2 heightmap write-back: `ReplaceHeightmap` in a live city with water, traffic and buildings; watch for water
  surges, world map changes and frame cost. Compare with the ApplyBrush delta route. Pick Option 1 or 2 (SPEC 2).

**B0** (output `docs/fork/PARK-RECOGNITION.md`): several park types with and without frontage; companion created or
reason; road connection; vanilla overlays and info panel.

Checkpoint: owner reviews both docs and answers SPEC open questions before Phase 2.

## Phase 2: Workstream A build (branch `fork/a-restore`)
1. A-R1: `BuildGuid` in receipt v3 (append-only, old receipts read as "no guid").
2. Core: `RestorePlanner` (pure): input pre/post/current texel arrays + tolerance, output restore set + stats.
   Snapshot codec (delta-only, compressed, versioned). xUnit tests incl. overlap and player-edit cases.
3. Game: capture pre (before first terrain or paint write) and post (after settle); sidecar write via
   `ModPaths.WriteAllTextAtomic`-style atomic binary write.
4. Game: restore job queued from removal final block and `GiveUp`; runs after a frame gap in BuildService; chosen
   write-back method; paint restore or fallback.
5. A-R8 Elevation cleanup; A-R9 rebuild gating; A-R10 failed-build restore.
6. A-R12 setting, A-R13 log line, A-R14 "remove all", A-R15 sidecar housekeeping.
7. Round-trip scenarios in `suite/scenarios/` for SPEC 2 acceptance 1 to 10. Run, read logs and pictures.

## Phase 3: Workstream B build (branch `fork/b-parks`)
B-R1 reasons, B-R2 connection check, B-R3 UI (contract.ts first), B-R5 load repair and live toggles, B-R4 fallback,
B-R6 uninstall test, B-R7 pruning decision. Game runner acceptance per SPEC 3.

## Phase 4: Workstream C research (branch `fork/c-research`)
C-R1 document from the 1.6.3f1 decompile, C-R2 decision. Owner review. C-R3 only after approval.

## Effort guide
Phase 0 small; Phase 1 small to medium (spikes carry the risk); Phase 2 medium; Phase 3 small to medium;
Phase 4 research medium, build large.
