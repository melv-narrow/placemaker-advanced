// Placemaker: a farmstead that produces (GPL-3.0). docs/fork/FARM-ECONOMY.md, Option A; SPEC C-R3.
//
// With "Farms produce" on (default off), a farmstead build also places a real vanilla farm, emitted the way the vanilla
// object tool places one (decompiled 1.6.3f1 ObjectToolSystem / ObjectToolBaseSystem):
//  * the hub: a placeholder building of a specialized agriculture zone (PlaceholderBuildingData, ExtractorBuilding);
//  * the farm building: a level 1 spawnable building of the same zone, created attached to the hub (CreationFlags.Attach,
//    m_Attached = the hub prefab), offset to the back of the hub's lot (FindAttachmentBuildingJob);
//  * the fields: one vanilla field lot per planned field, owned by the hub through an OwnerDefinition, exactly like the
//    hub's own sub-areas (UpdateSubAreas). The hub's default field is NOT emitted: the tool creates sub-areas, not the
//    game, so leaving it out leaves only our fields.
// Vanilla then does the rest: a farm company moves in when the land is fertile, hires workers, produces, exports and
// pays industrial tax. The hub and the building are adopted as group members (top level, created from our definitions);
// the fields are the hub's sub-areas and go with it on removal. Planned objects inside a field or the hub's lot are left
// out: a field hides overlapping trees and props (CanOverrideObjects) and grows its own crops or animals.

using System;
using System.Collections.Generic;
using Game.Areas;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Error = Game.Tools.Error;
using Placemaker.Core;
using Placemaker.Core.Build;
using Placemaker.Core.Geometry;
using Placemaker.Game.Groups;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace Placemaker.Game.Build
{
    public sealed partial class BuildService
    {
        /// <summary>Setting "Farms produce" (default off): farmstead builds place a working vanilla farm.</summary>
        public static bool FarmEconomy = false;

        private const float CellSize = 8f;   // a building lot cell

        private sealed class FarmJob
        {
            public int Layer;
            public Entity Hub, Building, Field;
            public string HubName, BuildingName, FieldName;
            public int2 HubLot, BuildingLot;
            public float2 Site;
            public float Yaw;
            public float2[] Lot;
            public List<float2[]> Fields;
            public bool Livestock;
            public int Skipped;
            public bool ClearedLot;
            public bool Covers(float2 p) => Poly.Contains(Lot, p) || FarmLayout.InsideAny(Fields, p, 1.5f);
        }

        private FarmJob _farm;
        /// <summary>A farm was emitted with this build (the park companion is then left out, SPEC C-R2).</summary>
        private bool _farmBuilt;
        /// <summary>The farm was refused at validation: the build is emitted once more without it (as scenery).</summary>
        private bool _farmRefused;

        private sealed class FarmPrefab
        {
            public Entity Hub, Building, Field;
            public int2 HubLot, BuildingLot;
            public bool Livestock;
            public string Name = "";
        }

        /// <summary>Called once before the build's objects are emitted: decides whether and where the farm goes.</summary>
        private void PlanFarm()
        {
            _farm = null;
            _farmBuilt = false;
            if (!FarmEconomy || LooseBuild || _groupId == 0 || _farmRefused) return;
            int layer = -1;
            for (int i = 0; i < _request.Plan.Layers.Count; i++)
                if (FarmLayout.IsFarm(_request.Plan.Layers[i])) { layer = i; break; }
            if (layer < 0) return;
            try
            {
                var plan = _request.Plan.Layers[layer];
                var fields = FarmLayout.Fields(plan);
                if (fields.Count == 0) { Mod.Log.Info("Placemaker farm: the farmstead has no field large enough for a vanilla field; no farm."); return; }
                bool livestock = FarmLayout.Livestock(plan);
                var pick = PickFarmPrefab(livestock);
                if (pick == null) { NoteSkip("Placemaker.Farm.NoPrefab", null, IssueSeverity.Warning, 0); Mod.Log.Warn("Placemaker farm: no vanilla farm hub with a field and a level 1 building was found; no farm."); return; }

                var outline = BuildOutline();
                if (outline == null) return;
                var ring3 = new float3[outline.Length];
                for (int i = 0; i < outline.Length; i++) ring3[i] = new float3(outline[i].x, 0f, outline[i].y);
                var nets = new Preview.SiteCapture(EntityManager, World).NetsAround(ring3, 30f);
                var objects = new List<float2>();
                var paths = new List<(float2, float2, float)>();
                ParkCompanionPlanner.Obstacles(_request.Plan, objects, paths);
                // Objects in the fields go anyway; they must not push the hub away.
                objects.RemoveAll(p => FarmLayout.InsideAny(fields, p, 1.5f));
                float width = pick.HubLot.x * CellSize, depth = pick.HubLot.y * CellSize;
                var site = ParkCompanionPlanner.FindLot(outline, nets, objects, paths, width, depth);
                bool cleared = false;
                if (!site.Found)
                {
                    // The yard's barns and silos take the frontage: put the farm there and leave them out instead.
                    site = ParkCompanionPlanner.FindLot(outline, nets, null, paths, width, depth);
                    cleared = site.Found;
                }
                if (!site.Found)
                {
                    NoteSkip("Placemaker.Farm.NoFrontage", null, IssueSeverity.Warning, 0);
                    Mod.Log.Warn($"Placemaker farm: no road frontage for a {pick.HubLot.x}x{pick.HubLot.y} farm lot ({site.DetailKey}: {site.Trace}; outline {outline.Length} points); the farmstead stays scenery.");
                    return;
                }
                var lot = FarmLayout.LotRect(site.Position, site.Yaw, width, depth);
                // A field under the hub's lot would overlap the farm building: those parts of the plan are not fields.
                fields.RemoveAll(f => Overlaps(f, lot));
                if (fields.Count == 0) { Mod.Log.Info("Placemaker farm: every field lies under the farm's lot; no farm."); return; }
                _farm = new FarmJob
                {
                    Layer = layer, Hub = pick.Hub, Building = pick.Building, Field = pick.Field,
                    HubName = _prefabs.GetPrefabName(pick.Hub), BuildingName = _prefabs.GetPrefabName(pick.Building), FieldName = _prefabs.GetPrefabName(pick.Field),
                    HubLot = pick.HubLot, BuildingLot = pick.BuildingLot, Site = site.Position, Yaw = site.Yaw, Lot = lot,
                    Fields = fields, Livestock = pick.Livestock, ClearedLot = cleared,
                };
            }
            catch (Exception e)
            {
                _farm = null;
                Mod.Log.Warn("Placemaker farm planning failed (the farmstead is built as scenery): " + e.Message);
            }
        }

        private static bool Overlaps(float2[] field, float2[] lot)
        {
            foreach (var p in lot) if (Poly.Contains(field, p)) return true;
            foreach (var p in field) if (Poly.Contains(lot, p)) return true;
            // an edge of the field crossing the lot with no corner inside either
            for (int i = 0; i < field.Length; i++)
                for (int j = 0; j < lot.Length; j++)
                    if (Cross(field[i], field[(i + 1) % field.Length], lot[j], lot[(j + 1) % lot.Length])) return true;
            return false;
        }

        private static bool Cross(float2 a, float2 b, float2 c, float2 d)
        {
            float Side(float2 p, float2 q, float2 r) => (q.x - p.x) * (r.y - p.y) - (q.y - p.y) * (r.x - p.x);
            float d1 = Side(c, d, a), d2 = Side(c, d, b), d3 = Side(a, b, c), d4 = Side(a, b, d);
            return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
        }

        /// <summary>True when a planned object of the farm layer is left out (inside a field or the farm's lot).</summary>
        private bool SkippedForFarm(WorkItem w, PlannedObject o)
        {
            if (_farm == null || w.Layer != _farm.Layer || o == null) return false;
            if (!_farm.Covers(o.Position.xz)) return false;
            _farm.Skipped++;
            return true;
        }

        /// <summary>Emits the hub, its attached farm building and the field lots (after the build's own objects).</summary>
        private void EmitFarm()
        {
            if (_farm == null) return;
            try
            {
                var f = _farm;
                var rotation = quaternion.RotateY(f.Yaw);
                var hubPos = new float3(f.Site.x, GroundHeight(f.Site), f.Site.y);
                int seed = (int)Seeds.Mix((uint)_groupId, 0xFA4Bu);

                var hub = NewDefinition();
                EntityManager.AddComponentData(hub, new CreationDefinition { m_Prefab = f.Hub, m_RandomSeed = seed });
                EntityManager.AddComponent<Updated>(hub);
                EntityManager.AddComponentData(hub, ObjectAt(hubPos, rotation));

                // FindAttachmentBuildingJob: the building sits at the back of the hub's lot.
                var offset = new float3(0f, 0f, (f.HubLot.y - f.BuildingLot.y) * CellSize * 0.5f);
                var bPos = hubPos + math.rotate(rotation, offset);
                bPos.y = GroundHeight(bPos.xz);
                var building = NewDefinition();
                EntityManager.AddComponentData(building, new CreationDefinition
                {
                    m_Prefab = f.Building, m_Attached = f.Hub, m_Flags = CreationFlags.Attach, m_RandomSeed = seed + 1,
                });
                EntityManager.AddComponent<Updated>(building);
                EntityManager.AddComponentData(building, ObjectAt(bPos, rotation));
                // AdoptStrays recovers a tag lost on Apply only for prefabs this build used.
                _usedPrefabs.Add(f.Hub);
                _usedPrefabs.Add(f.Building);

                var owner = new OwnerDefinition { m_Prefab = f.Hub, m_Position = hubPos, m_Rotation = rotation };
                int fields = 0;
                foreach (var ring in f.Fields)
                {
                    var def = NewDefinition();
                    EntityManager.AddComponentData(def, new CreationDefinition { m_Prefab = f.Field, m_RandomSeed = seed + 2 + fields });
                    EntityManager.AddComponent<Updated>(def);
                    EntityManager.AddComponentData(def, owner);
                    var nodes = EntityManager.AddBuffer<global::Game.Areas.Node>(def);
                    // Counter-clockwise like every area the game draws (AreaToolSystem); the plan's rings may run either way.
                    var r = Poly.SignedArea(ring) < 0f ? Reversed(ring) : ring;
                    nodes.ResizeUninitialized(r.Length + 1);
                    for (int i = 0; i < r.Length; i++) nodes[i] = new global::Game.Areas.Node(new float3(r[i].x, GroundHeight(r[i]), r[i].y), float.MinValue);
                    nodes[r.Length] = nodes[0];
                    fields++;
                }
                _farmBuilt = true;
                // The hub and the building are top-level objects and the fields are planned areas of this build: the
                // member check counts them (else "extra" members, or a false "built twice" on a small farm).
                _emittedObjects += 2;
                _emittedAreas += fields;
                double ha = 0;
                foreach (var ring in f.Fields) ha += math.abs(Poly.SignedArea(ring)) / 10000.0;
                Mod.Log.Info($"Placemaker farm: hub '{f.HubName}' ({f.HubLot.x}x{f.HubLot.y}) with '{f.BuildingName}' and {fields} '{f.FieldName}' field(s), {ha:F1} ha, "
                    + $"{(f.Livestock ? "livestock" : "crops")}, at {f.Site}; {f.Skipped} planned object(s) left out of the fields and the farm lot"
                    + (f.ClearedLot ? " (the yard gave way for the farm lot)" : "") + ".");
            }
            catch (Exception e)
            {
                _farmBuilt = false;
                Mod.Log.Warn("Placemaker farm emission failed (the farmstead is built as scenery): " + e.Message);
            }
        }

        private static float2[] Reversed(float2[] ring)
        {
            var r = (float2[])ring.Clone();
            Array.Reverse(r);
            return r;
        }

        private static ObjectDefinition ObjectAt(float3 p, quaternion rotation) => new ObjectDefinition
        {
            m_Position = p, m_LocalPosition = p, m_Rotation = rotation, m_LocalRotation = rotation,
            m_Scale = new float3(1f), m_Intensity = 1f, m_Probability = 100, m_PrefabSubIndex = -1, m_ParentMesh = -1,
        };

        /// <summary>
        /// After Apply: the field lots are the hub's sub-areas (Owner), so adoption skips them, and removing the hub did not
        /// take every one with it (game runner: 5 of 7 fields stayed). They become explicit members, removed with the group.
        /// The hub goes on the fertility watch.
        /// </summary>
        private void AdoptFarm()
        {
            if (!_farmBuilt || _farm == null) return;
            Entity hub = Entity.Null;
            foreach (var e in _tagged)
                if (EntityManager.Exists(e) && !EntityManager.HasComponent<Deleted>(e) && EntityManager.HasComponent<PrefabRef>(e)
                    && EntityManager.GetComponentData<PrefabRef>(e).m_Prefab == _farm.Hub) { hub = e; break; }
            if (hub == Entity.Null)
            {
                _farmBuilt = false;   // the park companion is not left out for a farm that does not exist
                Mod.Log.Warn("Placemaker farm: the hub was not built; the farmstead is scenery.");
                return;
            }
            // The fields, and every lot below them: a vanilla "Agriculture Lot" makes its own "Agriculture Lot - Field" child
            // (a slave area). Removal left most of those children behind with their owner gone (game runner), so every level
            // is claimed and removed explicitly.
            int fields = 0, children = 0;
            var queue = new List<(Entity area, int depth)>();
            void Below(Entity owner, int depth)
            {
                if (!EntityManager.HasBuffer<global::Game.Areas.SubArea>(owner)) return;
                var subs = EntityManager.GetBuffer<global::Game.Areas.SubArea>(owner, true);
                for (int i = 0; i < subs.Length; i++) queue.Add((subs[i].m_Area, depth));
            }
            Below(hub, 0);
            for (int q = 0; q < queue.Count && q < 1000; q++)
            {
                var (a, depth) = queue[q];
                if (!EntityManager.Exists(a) || EntityManager.HasComponent<Deleted>(a) || !EntityManager.HasComponent<global::Game.Areas.Area>(a)) continue;
                Tag(a, MemberKind.Area, MemberRole.Generic);
                if (depth == 0) fields++; else children++;
                Below(a, depth + 1);
            }
            _emittedAreas += children;   // planned with their fields (the member check)
            Mod.Log.Info($"Placemaker farm: hub, farm building, {fields} field lot(s) and {children} lot(s) below them belong to group {_groupId}.");
            try { World.GetOrCreateSystemManaged<Economy.FarmWatchSystem>().Add(_groupId, hub); }
            catch (Exception e) { Mod.Log.Warn("Placemaker farm fertility watch not started: " + e.Message); }
        }

        /// <summary>
        /// Validation frame: the farm's temporaries have no plan key, so the verdicts leave them alone. A farm only works whole:
        /// without its hub the fields would become lots without an owner (FindOwnersSystem leaves Owner null), and the farm
        /// building is linked by Attached, not as a sub-object, so cancelling the hub alone would leave it. When the hub or
        /// the building did not appear, or (vanilla rules, no anarchy) any piece is refused, every piece is cancelled and the
        /// farmstead is built as scenery.
        /// </summary>
        private bool FarmVerdict()
        {
            if (!_farmBuilt || _farm == null) return false;
            var pieces = new List<Entity>();
            bool hub = false, building = false, refused = false;
            using (var temps = _tempQuery.ToEntityArray(Unity.Collections.Allocator.Temp))
                foreach (var e in temps)
                {
                    // only this build's own new pieces: a city farm next to the build has temporaries too (delete or
                    // update copies, m_Original set), and must neither stand in for our hub nor be cancelled
                    if (EntityManager.GetComponentData<Temp>(e).m_Original != Entity.Null) continue;
                    var prefab = EntityManager.GetComponentData<PrefabRef>(e).m_Prefab;
                    bool isHub = prefab == _farm.Hub, isBuilding = prefab == _farm.Building;
                    bool isField = EntityManager.HasComponent<Owner>(e) && FarmOwned(e);
                    if (!isHub && !isBuilding && !isField) continue;
                    pieces.Add(e);
                    hub |= isHub; building |= isBuilding;
                    if (!IgnoreOwnCollisions && EntityManager.HasComponent<Error>(e)) refused = true;
                }
            if (hub && building && !refused) return false;
            // The planned crops and props in the fields and the farm lot were left out for the farm: cancelling the farm alone
            // would leave those places bare. The whole build is emitted once more, without the farm (DoResetForRetry clears
            // every temporary first).
            _farmBuilt = false;
            _farmRefused = true;
            NoteSkip("Placemaker.Farm.Refused", null, IssueSeverity.Warning, 0);
            Mod.Log.Warn($"Placemaker farm left out ({(!hub ? "no hub" : !building ? "no farm building" : "the game refused a piece")}, {pieces.Count} piece(s)); the build goes again as scenery.");
            Go(Step.ResetForRetry);
            return true;
        }

        /// <summary>A temporary area owned (directly or through another area) by the farm's hub temporary.</summary>
        private bool FarmOwned(Entity e)
        {
            for (int depth = 0; depth < 4 && EntityManager.HasComponent<Owner>(e); depth++)
            {
                e = EntityManager.GetComponentData<Owner>(e).m_Owner;
                if (e == Entity.Null || !EntityManager.Exists(e) || !EntityManager.HasComponent<PrefabRef>(e)) return false;
                if (EntityManager.GetComponentData<PrefabRef>(e).m_Prefab == _farm.Hub)
                    return EntityManager.HasComponent<Temp>(e) && EntityManager.GetComponentData<Temp>(e).m_Original == Entity.Null;   // our new hub only
            }
            return false;
        }

        /// <summary>Facts kept with the build (receipt), for the Manage panel and the game runner.</summary>
        private void FinishFarm(GroupRecord rec)
        {
            if (!_farmBuilt || _farm == null) return;
            rec.Facts["farm.fields"] = _farm.Fields.Count;
            rec.Facts["farm.livestock"] = _farm.Livestock ? 1 : 0;
        }

        // ───────────── vanilla farm prefabs ─────────────

        private static List<FarmPrefab> s_farmPrefabs;

        /// <summary>
        /// A hub of a specialized agriculture zone (placeholder ExtractorBuilding whose sub-area is a fertile-land field
        /// lot) and the largest level 1 building of its zone that fits its lot. Livestock or crops as asked, else any.
        /// </summary>
        private FarmPrefab PickFarmPrefab(bool livestock)
        {
            // Prefab entities belong to one world: rescan when the cache is empty or any entry went stale.
            if (s_farmPrefabs == null || s_farmPrefabs.Count == 0 || s_farmPrefabs.Exists(p => !Valid(p))) s_farmPrefabs = FindFarmPrefabs();
            // A crop farmstead grows grain first (its fields are grain, vegetable rows, fallow and hay); then any crop hub.
            string want = livestock ? "Livestock" : "Grain";
            foreach (var p in s_farmPrefabs) if (p.Name.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0) return p;
            foreach (var p in s_farmPrefabs) if (p.Livestock == livestock) return p;
            return s_farmPrefabs.Count > 0 ? s_farmPrefabs[0] : null;
        }

        private bool Valid(FarmPrefab p) =>
            EntityManager.Exists(p.Hub) && EntityManager.Exists(p.Building) && EntityManager.Exists(p.Field)
            && EntityManager.HasComponent<PrefabData>(p.Hub) && EntityManager.HasComponent<PrefabData>(p.Building) && EntityManager.HasComponent<PrefabData>(p.Field);

        private List<FarmPrefab> FindFarmPrefabs()
        {
            var list = new List<FarmPrefab>();
            var em = EntityManager;
            // Spawnable level 1 buildings by zone, with what they produce.
            var byZone = new Dictionary<Entity, List<(Entity prefab, int2 lot, global::Game.Economy.Resource made)>>();
            using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<SpawnableBuildingData>(), ComponentType.ReadOnly<BuildingData>(),
                ComponentType.ReadOnly<BuildingPropertyData>(), ComponentType.ReadOnly<PrefabData>()))
            using (var all = q.ToEntityArray(Allocator.Temp))
                foreach (var e in all)
                {
                    var s = em.GetComponentData<SpawnableBuildingData>(e);
                    if (s.m_Level != 1 || s.m_ZonePrefab == Entity.Null) continue;
                    if (!byZone.TryGetValue(s.m_ZonePrefab, out var l)) byZone[s.m_ZonePrefab] = l = new List<(Entity, int2, global::Game.Economy.Resource)>();
                    l.Add((e, em.GetComponentData<BuildingData>(e).m_LotSize, em.GetComponentData<BuildingPropertyData>(e).m_AllowedManufactured));
                }
            using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<PlaceholderBuildingData>(), ComponentType.ReadOnly<BuildingData>(), ComponentType.ReadOnly<PrefabData>()))
            using (var all = q.ToEntityArray(Allocator.Temp))
                foreach (var hub in all)
                {
                    var ph = em.GetComponentData<PlaceholderBuildingData>(hub);
                    if (ph.m_Type != BuildingType.ExtractorBuilding || !em.HasBuffer<global::Game.Prefabs.SubArea>(hub)) continue;
                    var field = FertileField(hub);
                    if (field == Entity.Null || !byZone.TryGetValue(ph.m_ZonePrefab, out var candidates)) continue;
                    var hubLot = em.GetComponentData<BuildingData>(hub).m_LotSize;
                    Entity best = Entity.Null; int2 bestLot = default; int bestArea = -1; global::Game.Economy.Resource made = 0;
                    foreach (var c in candidates)
                    {
                        if (!math.all(c.lot <= hubLot)) continue;
                        int area = c.lot.x * c.lot.y;
                        // deterministic: the largest that fits, ties by prefab index
                        if (area > bestArea || (area == bestArea && c.prefab.Index < best.Index)) { best = c.prefab; bestLot = c.lot; bestArea = area; made = c.made; }
                    }
                    if (best == Entity.Null) continue;
                    var agri = global::Game.Economy.Resource.Grain | global::Game.Economy.Resource.Vegetables | global::Game.Economy.Resource.Livestock | global::Game.Economy.Resource.Cotton;
                    if ((made & agri) == 0) continue;
                    list.Add(new FarmPrefab
                    {
                        Hub = hub, Building = best, Field = field, HubLot = hubLot, BuildingLot = bestLot, Name = _prefabs.GetPrefabName(hub) ?? "",
                        // Every agriculture hub shares one level 1 building that may make any farm good; what the hub's zone grows is
                        // in its name ("Agriculture Area Placeholder - Grain / Livestock / Vegetable / Cotton", 1.6.3f1).
                        Livestock = (_prefabs.GetPrefabName(hub) ?? "").IndexOf("Livestock", StringComparison.OrdinalIgnoreCase) >= 0,
                    });
                }
            list.Sort((a, b) => a.Hub.Index.CompareTo(b.Hub.Index));
            var names = new List<string>();
            foreach (var p in list) names.Add($"{_prefabs.GetPrefabName(p.Hub)} ({(p.Livestock ? "livestock" : "crops")}, {_prefabs.GetPrefabName(p.Building)}, {_prefabs.GetPrefabName(p.Field)})");
            Mod.Log.Info($"Placemaker farm prefabs: {list.Count} vanilla farm hub(s): {string.Join("; ", names)}.");
            return list;
        }

        /// <summary>The hub's fertile-land field lot prefab (resolving an area placeholder to its first variant), or Null.</summary>
        private Entity FertileField(Entity hub)
        {
            var em = EntityManager;
            var subs = em.GetBuffer<global::Game.Prefabs.SubArea>(hub, true);
            for (int i = 0; i < subs.Length; i++)
            {
                var p = subs[i].m_Prefab;
                if (em.HasBuffer<PlaceholderObjectElement>(p))
                {
                    var el = em.GetBuffer<PlaceholderObjectElement>(p, true);
                    if (el.Length == 0) continue;
                    p = el[0].m_Object;
                }
                if (!em.HasComponent<ExtractorAreaData>(p) || !em.HasComponent<AreaGeometryData>(p)) continue;
                if (em.GetComponentData<AreaGeometryData>(p).m_Type != AreaType.Lot) continue;
                if (em.GetComponentData<ExtractorAreaData>(p).m_MapFeature != MapFeature.FertileLand) continue;
                if (!em.HasComponent<AreaData>(p) || !em.GetComponentData<AreaData>(p).m_Archetype.Valid) continue;
                return p;
            }
            return Entity.Null;
        }
    }
}
