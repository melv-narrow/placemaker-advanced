# Removal diagnosis (A0)

Why removing a Placemaker build left marks in the land, and what the fork changed. Measured in the real game
(Cities: Skylines II 1.6.3f1) with the game runner, scenario set `suite/scenarios/restore.json`. Each type is built and
removed twice: once with the new setting "Put the land back when a build is removed" off (upstream behaviour) and
once on.

Two runner steps were added for this (`suite/schema.json`, `Game/Suite/SuiteSteps.Probe.cs`):
- `probe` reads the game's own ground textures (the heightmap and the paint splatmaps, from the GPU, which is what the
  save stores) over the build and a 20 m margin, and counts what stands there. With `compare` it reports the
  difference from an earlier probe.
- `road` lays a vanilla road straight across where the build was and reports whether the game made any of it elevated
  (bridge-like) and how far its curve stands above the ground.

## The three player reports

| Report | Cause found | Status |
|---|---|---|
| Odd textures that cannot be removed | Terrain paint (user splatmap). Upstream never undoes it by design (`BuildService.Paint.cs` header). No orphaned surfaces or decals: the probe counts zero areas and zero objects of the group after removal. | Fixed: paint is restored texel for texel |
| Invisible gaps in the land | Terraform heights stay after removal (banks, cuts and fills). | Fixed: heights are restored texel for texel |
| A road over the gaps becomes a bridge | Two contributors: (1) the leftover height difference; (2) a city road node next to the build kept the build's "pinned" elevation marker after removal. | Both fixed; see H3 |

## Results (runs `20261110-093245-restore` and `20261110-093726-restore-2`)

| Build | Restore off: left after removal | Restore on: after removal |
|---|---|---|
| Strip mall (flat) | 440 paint texels | 0 changed; 440 of 440 paint texels restored, residual 0 |
| Parking (6 % slope) | 580 height texels, up to 0.81 m | 0 changed; 1,143 of 1,143 restored, residual 0.00 m |
| Garden park (rough ground) | 802 height texels up to 3.63 m, 63 paint texels | 0 changed; 1,027 height and 56 paint texels restored |
| Parking on hills (7 m amplitude) | 1,363 height texels, up to 7.63 m | 0 changed; 1,388 of 1,388 restored |
| Strip mall, first paint in a fresh city | | 296 of 296 paint texels restored |
| Garden park: build, Edit (rebuild), remove | | old ground back before the rebuild read it; final removal leaves 0 changed texels |

Capture and restore cost 1 to 23 ms per build; snapshot files are 0.1 to 3.6 KB.

## Hypotheses

**H1, the leftover textures are paint.** Confirmed. Probe counts after removal show no surfaces, decals or other
areas of the group left (`areas.*` and `members.*` back to the "before" numbers). The mall build changed 440 paint
texels; with restore off they stay after removal, with restore on they all go back.

**H2, the gaps are leftover heights.** Confirmed. Parking on a 6 % slope, restore off: after removal 578 height
texels still differ from before the build, by up to 0.81 m. Restore on: 1,143 of 1,143 changed texels restored,
residual 0.00 m, and the "removed vs before" probe shows 0 changed texels.

**H3, the bridge.** Two things were found:
- `BuildService.PinMarkerNets` gives the end nodes of the build's invisible marker paths an `Elevation(0)` component so
  the game does not re-fit them to the ground. It also did this to the city road's node where a lot drive joins it.
  That node is not part of the group, so removal left it pinned. Vanilla never keeps an all-zero `Elevation` on a
  top-level node (decompiled `GenerateNodesSystem`, around line 1485), so the node kept counting as elevated after the
  build was gone. Every removal in the test run found exactly one such node. The removal now takes the pin off
  (`GroupRemovalSystem.UnpinSurvivor`, logged as "city road node(s) un-pinned").
- A vanilla road laid across the old footprint: 0 elevated edges in every scenario, restore off or on, with the curve
  at most 0.35 to 0.57 m above the ground. On the test sites the leftover height difference was under 1 m, not
  enough for the game to raise the road. The player's bridges are most likely over the deeper bowls upstream logs
  as 4 to 9 m (`ScanLivePits`), which the restore now removes as well. The hills scenarios in the set build on
  steeper ground to cover this: with restore off the hills parking left ground up to 7.63 m off after removal.
- On the hills sites the road laid after a restore can come out partly as a bridge (2 edges, curve up to 11.5 m above
  the ground). That is the original valley between two hills, back where it was before the build: the runner lays
  one straight segment across, and the game raises it over a valley as it would on untouched land.

## Edit (rebuild)
An Edit removes the old group and builds the new one. The rebuild now waits until the old group's ground is back
(`BuildService.DoWaitReplaced`), and the log shows the order: old restore, then the new build's "before" capture.
Note: the new design is planned on the ground as it was while the old build stood (the tool's site capture), so its
terraform targets can differ from the first build's. That is upstream behaviour and not a regression; the removal of
the rebuilt version still restores the original land exactly.

## What else the probes showed
- Removal leaves one extra node on the site (the split point on the city road where the lot drive joined). It is a
  vanilla junction, not elevated, and not part of the group: expected.
- `ReportLeftovers` stayed silent: no member of any group survived removal.
- Capture and restore are cheap: 4 to 23 ms per build, snapshot files under 1 KB for these sizes.

## Bugs found while testing the fix (fixed before the final run)
- In a city nobody had painted yet, the game has no paint texture and hands out a 4x4 placeholder; the first paint
  of a fresh city was not captured. The capture now treats that as "no paint anywhere" and still records it.
- The world heightmap (the coarse map beyond the playable area) is not a writable texture on this map; it is skipped.
  The build's terraform does not reach it inside the playable area, so nothing is lost.

## Not verified in the game (and why)
- Save, quit, load, then remove: the runner has no save/load step. The snapshot is a file keyed by the build's id in
  the receipt (`ModsData/Placemaker/restore/<id>.bin`); the load path is covered by unit tests of the codec and the
  receipt v3 round trip.
- A player's own terraforming after a build, then removal: the runner cannot terraform like a player. The rule (only
  texels still showing what the build left are restored) is covered by unit tests, including the overlapping build
  case.
