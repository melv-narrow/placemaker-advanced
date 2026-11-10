# Park recognition (B0) and what changed (B1 to B5)

Does the city count a Placemaker park as a park? Measured in the game (1.6.3f1) with the game runner, scenario set
`suite/scenarios/parks.json` (run `20261110-*-parks`).

## How it works (unchanged from upstream)
A Placemaker build is not a vanilla building. The city sees it through two things:
- the invisible owner lot gets the game's attractiveness component: it counts for tourism only;
- a hidden copy of a small vanilla park building (the "companion", cloned from `PocketPark01`) stands inside the drawn
  area at a side that faces a road, within 6 m of the kerb. It carries leisure, park coverage, land value and upkeep,
  exactly like a vanilla park. Citizens walk to it, not into the drawn park.

## Results

| Build | Companion | Road connection | City sees |
|---|---|---|---|
| Park drawn on a whole block (bucket) | created | yes, at once | a park |
| Plaza on a whole block | created | yes | a park |
| Adventure playground on a whole block | created | yes | a park |
| Park drawn as a 70 x 60 m rectangle in the middle of a block | none: no side within 6 m of a road | | tourism only |

The log now says exactly why a build has no companion (before, every failure had the same message):
`group 2 has no road frontage for a park companion (Placemaker.ParkStats.Why.NotAlongRoad, 10 net(s) within 30 m)`.

## What changed
- **B1, reasons.** The frontage search reports one of: no road within 30 m, no side at least 8 m long, no side along a
  road (draw up to the kerb), sides along the road too narrow for the 8 m lot, or every spot taken by paths, objects or
  zoning gaps. Review shows it in the "No road edge" tooltip. It is kept with the build (receipt) for later.
- **B2, connection check.** After the companion is created, Placemaker checks that the game connected it to its road
  (`Building.m_RoadEdge`) once the simulation has run, and logs the result. A companion that stands unconnected is
  recorded as such (it would give no leisure or coverage).
- **B3, Manage panel.** Selecting a park build shows "Counts as a park": Yes / Not yet / No: not connected / Tourism
  only / No: park stats are off, with the reason as a tooltip.
- **B4, no clean frontage.** Update (C-R3): an outline a few metres short of the kerb now works. The lot's front may
  reach straight over the verge to the road (never sideways past the outline), which is what the road connection needs.
  Earlier attempt: A relaxed search (accepting an outline up to 12 m from the kerb) was tried and dropped: the
  companion's front must touch the road for the game to connect it, so a lot pulled inside such an outline would stand
  unconnected. Instead the player gets the specific reason and what to do ("draw the area up to the kerb").
- **B5, repair.** On load, a companion that was requested but never created, or created and lost since, is requested
  again where the build placed it (the position is now kept in the receipt; builds from before the fork have none and
  keep tourism only). Changing the two park settings in Options now applies at once instead of on the next load.

## Uninstall (B6) and the companions file (B7)
- Uninstall: "Remove everything Placemaker built" (Options, Maintenance) removes every build with its companion and,
  with "Put the land back" on, its ground changes. That is the clean way out. A save made with companions and loaded
  without the mod keeps hidden buildings whose prefab is missing; this case was not run in the game.
- `ModsData/Placemaker/park-companions.txt` lists the companion prefabs to recreate before a save loads. It is shared
  by every save on the machine, so pruning it from one city could break another. Decision: never prune automatically.
  It grows by one short line per distinct stat combination, which stays small.

## Not verified in the game
- Citizens' leisure counter moving and the park coverage overlay: the runner city has few citizens and the run is
  short. The companion is a real vanilla park building with a road connection, which is what the game's leisure and
  coverage systems read.
- Loading a save without the mod (obsolete companion prefabs).
