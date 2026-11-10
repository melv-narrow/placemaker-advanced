# Farm economy: research (SPEC C-R1, C-R2)

All engine references point into the decompiled game, version 1.6.3f1, folder
`cs2-decomp/1.6.3f1/game`, written as `Namespace/File.cs:line`. Nothing here is from memory.

## 1. How a vanilla farm works

**Four pieces, linked together.**

1. **The farm "hub" the player places.** In the Specialized Industry menu the player places a *placeholder*
   building: a prefab with `PlaceholderBuilding`, type `ExtractorBuilding`, tied to a special zone
   (`Game.Prefabs/PlaceholderBuilding.cs`). Its prefab carries a sub-area: the field lot.
2. **The farm building that actually works.** When the player places the hub, the tool picks a random level 1
   spawnable building of that zone that fits the lot and creates it *attached* to the hub
   (`Game.Tools/ObjectToolSystem.cs:4391-4410`, `Game.Tools/ObjectToolBaseSystem.cs:1251-1255`). This building's
   `BuildingPropertyData` allows an extractor resource (grain, livestock and so on), which makes it an industrial
   property, an `ExtractorProperty`, "on the market", with an efficiency buffer (`Game.Prefabs/BuildingProperties.cs:63-71`).
3. **The field.** A lot area whose prefab has `ExtractorArea` (`Game.Prefabs/ExtractorArea.cs`). Every field entity
   carries an `Extractor` component (stored amount, concentration, amount harvested, `Game.Areas/Extractor.cs`).
   `AreaResourceSystem` measures the map's natural resource (fertile land for crops) under the field's triangles and
   writes the result into `Extractor` (`Game.Areas/AreaResourceSystem.cs:383-388`, `:540-568`).
4. **The company.** A separate entity with `ExtractorCompany`, `IndustrialProcessData` (what it produces),
   `Resources` (its stock and cash), `WorkProvider` and `Employee` (jobs), `TaxPayer`. When nobody is waiting to
   produce a raw material, `IndustrialSpawnSystem` creates such a company (`Game.Simulation/IndustrialSpawnSystem.cs:156-170`).
   It looks for a free extractor property and scores each by the fertility of the fields of the hub the building is
   attached to; a score of zero means "never move in" (`Game.Buildings/PropertyUtils.cs`, `ExtractorFindCompanyJob.Evaluate`).

**Production.** `ExtractorCompanySystem` runs several times a day for every company that rents a property
(`Game.Simulation/ExtractorCompanySystem.cs:645`). It follows `PropertyRenter` to the building, `Attached.m_Parent`
to the hub, and the hub's `SubArea` buffer to the fields (`:210-243`). Output depends on workers, building
efficiency and fertility (`:213-231`). Each field gives its share and loses some fertility
(`ProcessArea`, `:413-440`; consumption factor `:446-452`). Hub upgrades can add more fields or storage (`:244-290`).

**Jobs.** `ExtractorAISystem` sizes the workforce from the total field area: cells times
`m_MaxWorkersPerCell` / 2, starting at up to 80 workers and growing slowly after that, shrinking when the stock is full
(`Game.Simulation/ExtractorAISystem.cs:96-120`, `:203-205`, `:244-275`; `Game.Simulation/CompanyUtils.cs:45-48`).
Workers are ordinary citizens who commute.

**Where the goods go.** Produced goods are added to the company's stock (`ExtractorCompanySystem.cs:317`). When
storage runs low the company is flagged as an exporter (`:324-334`), and `ResourceExporterSystem` sends delivery trucks
to a buyer or an outside connection (`Game.Simulation/ResourceExporterSystem.cs:127-146`, `:285`).

**Money for the city.** Each production tick books profit as untaxed income at the industrial tax rate of that
resource (`ExtractorCompanySystem.cs:293-303`). `TaxSystem` collects it as industrial tax
(`Game.Simulation/TaxSystem.cs:93-122`, `:342`). The city earns taxes, not sales revenue.

## 2. Minimum set for a field to produce

- A **hub entity** with a `SubArea` buffer listing the fields. The code only reads `Attached.m_Parent` and the
  buffer; it does not check that the hub is a placeholder (`ExtractorCompanySystem.cs:206-210`). But see zoning below.
- **Field entities**: `Lot`, `Geometry` (surface area), `PrefabRef` to a prefab with `ExtractorAreaData`, and
  `Extractor` (`ExtractorCompanySystem.cs:684-705`). Ownership is the hub's `SubArea` entry plus the field's `Owner`.
- A **property building**: a real `Building` with `Attached` (parent = hub), a road connection, an `Efficiency`
  buffer, and a prefab with `BuildingPropertyData`, `SpawnableBuildingData` and, if upgraded, storage data. The
  system reads `m_SpawnableDatas[prefab2]` without checking (`ExtractorCompanySystem.cs:204`); a missing component
  there is a crash risk.
- **Fertility under the field**, or a field prefab with `m_RequireNaturalResource = false`, which skips the fertility
  test (`ExtractorArea.cs` tooltip; `ExtractorCompanySystem.cs:691-701`). On land with no fertility and the default
  setting, no company ever moves in.
- The **company** is created and housed by vanilla itself; nothing extra is needed.

## 3. Hidden companion or real vanilla farm

**A hidden companion can work in principle.** Nothing in the production, property-finding or tax systems excludes
`Hidden` (queries at `ExtractorCompanySystem.cs:645`, `Game.Simulation/IndustrialFindPropertySystem.cs:187-189`,
`TaxSystem.cs:342`). The catch is that a farm companion is not one entity like the park companion. It needs a cloned
hub, a cloned spawnable building attached to it, and a field lot. The spawnable building is checked by
`ZoneCheckSystem`: if it is not attached to a placeholder of the same zone, and no zone blocks support it, it is
condemned (`Game.Buildings/ZoneCheckSystem.cs:284-300`, `:316-332`). So the hub must be a real placeholder whose
zone matches the building.

**What `CanOverrideObjects` does.** Every lot whose `ExtractorArea` is not a forest, and every storage lot, gets the
flag when prefabs initialise (`Game.Prefabs/AreaInitializeSystem.cs:306-311`, `:353-356`). With it, the lot
"overrides" overridable objects (trees, bushes, props, lamps) whose footprint touches the lot's triangles: they are
hidden (`Game.Objects/OverrideSystem.cs:1440-1456`; placement warnings in `Game.Areas/ValidationHelpers.cs:131-175`).
One exception: objects that belong to the same building as the lot are spared (`OverrideSystem.cs:1421-1437`).
Placemaker's trees belong to a Placemaker group, so they are not spared.

**Ways to avoid the hiding, best first:**

1. **Draw fields only over crop beds.** Hiding only happens where an object overlaps a field triangle. Placemaker
   plans both, so it can keep every tree, lamp and prop outside field polygons. Costs nothing, keeps vanilla prefabs.
2. **A cloned field lot with the flag cleared.** The flag is computed once, on prefab creation (query at
   `AreaInitializeSystem.cs:235-245`), so clearing it on the clone's `AreaGeometryData` afterwards should stick.
   The clone can also set `m_MaxObjectArea = 0` (no vanilla crop props) and `m_AllowEditing = false` (lot hidden in
   game, `AreaInitializeSystem.cs:368-371`). Downside: a runtime clone (see section 4).
3. Never clear the flag on the vanilla prefab: that changes every player-built farm in the city.

## 4. Save compatibility after uninstall

Saves store prefabs by name (RESEARCH-NOTES rule 3). Without the mod, a runtime clone's name is unknown, and the
game puts in a stand-in prefab with a "missing object" mesh (`Game.Serialization/InitializeObsoleteSystem.cs:146-150`).

- **Vanilla hub, building and field:** the farm simply stays a normal vanilla farm. Company, workers and taxes keep
  going. Best case.
- **Cloned field lot only:** the field loses its farm data. The company finds no fertility and produces nothing.
  No crash expected, but a dead farm.
- **Cloned hub or building (hidden companion):** the building's stand-in prefab lacks the property and spawnable data
  that `ExtractorCompanySystem.cs:196-204` reads unchecked. That is a plausible native crash on load, and the
  formerly hidden building may show as a "missing object" box. This is the worst case.

## 5. Risks

- **Crashes.** Mesh-less buildings crash (RESEARCH-NOTES 16), so a companion must keep its meshes and use `Hidden`.
  Unchecked component reads at `ExtractorCompanySystem.cs:196-204` mean every prefab in the chain must be complete.
  Prefab rules 1 and 2 (register before `PrefabInitializeSystem`, use three frames later) apply to every clone.
- **`CanOverrideObjects`.** Trees and props vanish inside fields (section 3). Also, vanilla fields spawn their own
  crop props scaled to production (`ExtractorArea.m_ObjectSpawnFactor`, `m_MaxObjectArea`), which may clash with
  Placemaker's planted rows. Vanilla field lots also bring a border fence and a crop surface (AGENTS.md).
- **Economy balance.** A Placemaker farm becomes a full industry: 80 or more jobs for a large field, lowered
  fertility over time, industrial tax income, and an extra supplier that lowers demand for player-built farms.
  On land with no fertility it does nothing.
- **Zoning and specialized industry.** In 1.6.3f1 the hub itself is the specialized industry; there is no separate
  district. Its building must match the hub's zone or it is condemned (section 3).
- **Commuting and trucks.** Workers commute and delivery trucks drive through the build's road. Small builds on
  quiet roads will see real traffic.
- **Removal.** Deleting a build must delete the hub, its attached building and its fields together (rule 11:
  deletes only in PreTool). Whether vanilla removes the company cleanly when its property disappears must be tested
  in C-R3 ("no orphan company or workers").

## 6. Options

**Option A: real vanilla farm placed by Placemaker. Cost: medium.**
Placemaker places a vanilla hub at the build's road frontage, creates the attached spawnable building the way the
object tool does, and draws vanilla field lots only over crop beds. A visible vanilla farm building appears. No
runtime prefabs, so uninstall leaves a normal working farm. Fields on infertile land produce nothing; the tool
should warn.

**Option B: hidden companion farm. Cost: large.**
Cloned hub, cloned spawnable building (meshes kept, `Hidden`), cloned field lot with the flag cleared and
`m_RequireNaturalResource` off, all owned by the Placemaker group. Invisible, works anywhere. Three clones to keep
registered by name, zoning to satisfy, and the worst uninstall story (possible crash).

**Option C: simulated income, no company. Cost: small.**
Keep farms as scenery and pay a small income through the existing park companion's money flow. No goods, trucks or
economy panel entries, so it fails the C-R3 acceptance test. Only worth it if real production is dropped.

**Recommendation: Option A**, behind the C-R3 setting (default off), for `farmstead` first. It uses only vanilla
prefabs, survives uninstall, and solves `CanOverrideObjects` by keeping props out of field polygons. If the visible
vanilla farm building does not fit the look, add a cloned field lot (section 3, way 2) later, not a hidden building.

## 7. Park stats meanwhile (C-R2)

Farms currently get park stats and a park companion (`Core/Build/ParkStats.cs:170-176`). Vanilla farms give no
leisure or park coverage, so a producing farm that also counts as a park double-dips.

**Recommendation:** keep park stats unchanged while the farm economy setting is off, since today's farms are
scenery that people visit. When the setting is on, a producing `farmstead` stops getting the park companion.
`UrbanFarm` and `AnimalPark` keep park stats, since they are visitor places rather than working farms.

## 8. As built (C-R3)

Option A, behind the Options setting **"Farms produce (experimental)"**, off by default.

- A farmstead build emits, in the same definition batch as the rest of the build: the vanilla farm hub
  ("Agriculture Area Placeholder - Grain" for a crop farm, "- Livestock" when pastures and hay outnumber arable fields),
  its level 1 farm building attached the way the object tool does it, and one vanilla field lot over each planned field
  (owned by the hub). The hub's own default field is not emitted.
- The hub stands at the road like the park companion (same frontage search, sized to the hub's lot). If the yard's
  barns take the frontage, they give way. Planned crops, rigs, bales, field trees and animal spawners inside the fields
  are left out: the vanilla fields grow their own crops and animals, and would hide them anyway.
- Hub, building, fields and the fields' own child lots are group members, so removing the farmstead removes the whole
  farm. If any piece is missing at validation (or refused under vanilla rules), every farm piece is cancelled and the
  farmstead is built as scenery.
- A working farm gets no park companion (C-R2). With the setting off, farmsteads are exactly as before.
- After the build the simulation is watched: the fields' natural resource is logged, and if no farm company has moved
  in after a while, a warning says so.

**Seen in the game runner** (`suite/scenarios/farms.json`): crop and dairy farms built on 320 x 240 m plots. Hub,
building and 7 to 11 fields appeared; the member check passed. A farm company moved into the crop farm on a fertile
plot and into the dairy farm even on fields with zero fertility (livestock does not depend on it), so the watcher reports
whether a company actually came, not the fertility alone. Removal left no lot, no field child and no dangling owner
(the field's own child lot needed explicit removal, see `GroupRemovalSystem.DeleteAreaTree`). With the setting off the
farmstead was built as before.

**Not seen yet:** workers being hired, harvests and exports (the runner's `grow` step stops as soon as the city stops
growing, after a few game minutes); behaviour without the mod installed. A removed farm's company goes back to vanilla's
pool of companies looking for a building, like the ones the game itself spawns.
