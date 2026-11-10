// Placemaker — phase-correct removal of a whole group (GPL-3.0).
// Ordering comes from Core/Build/RemovalPlanner; the engine reasons are from ParkingLotTool's
// measured crash notes (KruemelGames, GPL-3.0) and ParkManager's ParkBundleCleanup (tomkpunkt):
//
// Phase: PreTool. Deleted marked here is seen by SubElementDeleteSystem (PostTool, cascades to
// sub-objects/lanes of the edge), by Game.Net.ReferencesSystem (Modification2B, removes the edge
// from its nodes' ConnectedEdge buffers) and by everything after it. Marking later than 2B leaves
// dangling node references → native crash (RESEARCH-NOTES 11–12).

using System;
using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Net;
using Game.Tools;
using Placemaker.Core.Build;
using Unity.Collections;
using Unity.Entities;
using UnityEngine.Scripting;

namespace Placemaker.Game.Groups
{
    public sealed partial class GroupRemovalSystem : GameSystemBase
    {
        private const int MaxPassesWithoutProgress = 600;
        private EntityQuery _requestQuery;
        private EntityQuery _memberQuery;
        private const int ToolReleaseFrames = 30;
        private GroupRegistrySystem _registry;
        private ToolSystem _toolSystem;
        private EntityQuery _tempQuery;

        /// <summary>True while any group is waiting to be or being removed (the land restore waits for a whole batch).</summary>
        public bool Pending => !_requestQuery.IsEmptyIgnoreFilter;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            _registry = World.GetOrCreateSystemManaged<GroupRegistrySystem>();
            _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            _tempQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Temp>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            _requestQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PlacemakerGroup>(), ComponentType.ReadWrite<PlacemakerRemoval>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            _memberQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PlacemakerMember>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            RequireForUpdate(_requestQuery);
        }

        [Preserve]
        protected override void OnUpdate()
        {
            using var requests = _requestQuery.ToEntityArray(Allocator.Temp);
            if (requests.Length == 0) return;
            // One group per frame keeps each pass ≤ 32 parts in total.
            var record = requests[0];
            try
            {
                ProcessGroup(record);
            }
            catch (Exception e)
            {
                Mod.Log.Error(e, "Placemaker group removal pass failed; will retry next frame");
                var r = EntityManager.GetComponentData<PlacemakerRemoval>(record);
                r.Passes++;
                EntityManager.SetComponentData(record, r);
                if (r.Passes > MaxPassesWithoutProgress) GiveUp(record);
            }
        }

        private void ProcessGroup(Entity record)
        {
            var group = EntityManager.GetComponentData<PlacemakerGroup>(record);
            var removal = EntityManager.GetComponentData<PlacemakerRemoval>(record);

            var entities = new List<Entity>();
            bool anyTemp = false;
            using (var all = _memberQuery.ToEntityArray(Allocator.Temp))
            using (var tags = _memberQuery.ToComponentDataArray<PlacemakerMember>(Allocator.Temp))
                for (int i = 0; i < all.Length; i++)
                {
                    if (tags[i].GroupId != group.GroupId) continue;
                    // A member currently being edited by a tool (Temp copy) would race with the tool.
                    if (EntityManager.HasComponent<Temp>(all[i])) { anyTemp = true; continue; }
                    entities.Add(all[i]);
                }
            if (anyTemp && entities.Count == 0) return;

            // Crash guard (playtest 4b: native crash right after bulldozing a group): a tool's temporary
            // copy (Temp.m_Original) of a member — e.g. the bulldozer still hovering a path, or a node
            // preview — keeps referencing it. Deleting the original under it leaves the tool's jobs
            // reading a dead entity. Wait for the tool to drop its copies; if it does not within
            // ToolReleaseFrames, switch to the default tool once (its next Clear removes them).
            if (ReferencedByTemporaries(entities))
            {
                removal.Passes++;
                if (removal.Passes == ToolReleaseFrames && _toolSystem.activeTool != null && !(_toolSystem.activeTool is DefaultToolSystem))
                {
                    Mod.Log.Info($"Placemaker: a tool still previews parts of group {group.GroupId}; switching to the default tool to remove it safely.");
                    _toolSystem.activeTool = World.GetOrCreateSystemManaged<DefaultToolSystem>();
                }
                if (removal.Passes > MaxPassesWithoutProgress) { GiveUp(record); return; }
                EntityManager.SetComponentData(record, removal);
                return;
            }

            var byId = new Dictionary<long, Entity>(entities.Count);
            var members = new List<RemovalMember>(entities.Count);
            var nodeEdges = new Dictionary<long, List<long>>();
            foreach (var e in entities)
            {
                long id = Id(e);
                byId[id] = e;
                var tag = EntityManager.GetComponentData<PlacemakerMember>(e);
                var kind = Classify(e, tag);
                var m = new RemovalMember { Id = id, Kind = kind };
                if (kind == MemberKind.Edge)
                {
                    var edge = EntityManager.GetComponentData<Edge>(e);
                    m.StartNode = Id(edge.m_Start);
                    m.EndNode = Id(edge.m_End);
                }
                else if (kind == MemberKind.Node)
                {
                    var list = new List<long>();
                    if (EntityManager.HasBuffer<ConnectedEdge>(e))
                    {
                        var buf = EntityManager.GetBuffer<ConnectedEdge>(e, true);
                        for (int i = 0; i < buf.Length; i++)
                        {
                            var ce = buf[i].m_Edge;
                            if (ce == Entity.Null || !EntityManager.Exists(ce) || EntityManager.HasComponent<Deleted>(ce)) continue;
                            list.Add(Id(ce));
                        }
                    }
                    nodeEdges[id] = list;
                }
                members.Add(m);
            }

            var pass = RemovalPlanner.NextPass(members, nodeEdges);
            var refresh = new HashSet<Entity>();
            foreach (var id in pass.Delete)
            {
                if (!byId.TryGetValue(id, out var e) || !EntityManager.Exists(e) || EntityManager.HasComponent<Deleted>(e)) continue;   // idempotent
                if (EntityManager.HasComponent<Edge>(e))
                {
                    var edge = EntityManager.GetComponentData<Edge>(e);
                    refresh.Add(edge.m_Start);
                    refresh.Add(edge.m_End);
                }
                PrepareDelete(e);
                EntityManager.AddComponent<Deleted>(e);
            }
            foreach (var id in pass.Release)
                if (byId.TryGetValue(id, out var e) && EntityManager.Exists(e))
                {
                    EntityManager.RemoveComponent<PlacemakerMember>(e);
                    refresh.Add(e);
                }
            RefreshSurvivors(refresh);

            if (pass.Delete.Count > 0 || pass.Release.Count > 0) removal.Passes = 0;
            else removal.Passes++;

            if (pass.Final && !anyTemp)
            {
                // Record goes one pass after the last member so both cascades never stack (PM).
                if (removal.Passes >= 1)
                {
                    RequestLandRestore(group.GroupId, "removed");
                    EntityManager.AddComponent<Deleted>(record);
                    Mod.Log.Info($"Placemaker removed group {group.GroupId}."
                        + (_unpinned > 0 ? $" {_unpinned} city road node(s) un-pinned (the build's ground pin removed)." : ""));
                    _unpinned = 0;
                    ReportLeftovers(group.GroupId);
                    _registry.MarkDirty();
                    return;
                }
            }
            if (removal.Passes > MaxPassesWithoutProgress) { GiveUp(record); return; }
            EntityManager.SetComponentData(record, removal);
            _registry.MarkDirty();
        }

        private bool ReferencedByTemporaries(List<Entity> members)
        {
            if (_tempQuery.IsEmptyIgnoreFilter) return false;
            var set = new HashSet<Entity>(members);
            using var temps = _tempQuery.ToComponentDataArray<Temp>(Allocator.Temp);
            for (int i = 0; i < temps.Length; i++)
                if (temps[i].m_Original != Entity.Null && set.Contains(temps[i].m_Original)) return true;
            return false;
        }

        private MemberKind Classify(Entity e, PlacemakerMember tag)
        {
            // Trust live components over the stored kind: the tag survives replacements by tools.
            if (EntityManager.HasComponent<Edge>(e)) return MemberKind.Edge;
            if (EntityManager.HasComponent<global::Game.Net.Node>(e)) return MemberKind.Node;
            if (tag.Kind == MemberKind.Auxiliary) return MemberKind.Auxiliary;
            if (EntityManager.HasComponent<global::Game.Areas.Area>(e)) return MemberKind.Area;
            if (EntityManager.HasComponent<global::Game.Objects.Object>(e)) return MemberKind.Object;
            return MemberKind.Other;
        }

        /// <summary>Per-kind preparation before Deleted (PLT lessons).</summary>
        private void PrepareDelete(Entity e)
        {
            DeleteAreaTree(e, 0);
            // (fork, C-R3) A farm hub's working building is linked by Attachment, not as a sub-object: it goes in the same
            // pass, so nothing reads Attached.m_Parent of a destroyed hub.
            if (EntityManager.HasComponent<global::Game.Objects.Attachment>(e))
            {
                var att = EntityManager.GetComponentData<global::Game.Objects.Attachment>(e).m_Attached;
                if (att != Entity.Null && EntityManager.Exists(att) && !EntityManager.HasComponent<Deleted>(att) && !EntityManager.HasComponent<Temp>(att)
                    && EntityManager.HasComponent<PlacemakerMember>(att))
                {
                    DeleteAreaTree(att, 0);
                    EntityManager.AddComponent<Deleted>(att);
                }
            }
            var tag = EntityManager.GetComponentData<PlacemakerMember>(e);
            if (tag.Role == MemberRole.Companion && EntityManager.HasComponent<global::Game.Companies.WorkProvider>(e))
            {
                // Employees leave cleanly instead of pointing at a dead workplace.
                var wp = EntityManager.GetComponentData<global::Game.Companies.WorkProvider>(e);
                wp.m_MaxWorkers = 0;
                EntityManager.SetComponentData(e, wp);
            }
            if (EntityManager.HasBuffer<global::Game.Net.SubLane>(e) && tag.Role == MemberRole.ParkingBay)
            {
                // Parking lanes owned by the decal go with it this frame (no orphan lane for LaneSystem).
                var lanes = EntityManager.GetBuffer<global::Game.Net.SubLane>(e, true);
                var copy = new List<Entity>(lanes.Length);
                for (int i = 0; i < lanes.Length; i++) copy.Add(lanes[i].m_SubLane);
                foreach (var lane in copy)
                    if (EntityManager.Exists(lane) && !EntityManager.HasComponent<Deleted>(lane))
                        EntityManager.AddComponent<Deleted>(lane);
            }
        }

        /// <summary>
        /// (fork, C-R3) Every area below a deleted member goes in the same pass, children before the frame ends: vanilla
        /// SubElementDeleteSystem cascades one level only, so a vanilla field's own "Field" child, or a field the player added
        /// to a farm hub after the build, stayed behind with its owner gone (game runner).
        /// </summary>
        private void DeleteAreaTree(Entity owner, int depth)
        {
            if (depth > 4 || !EntityManager.HasBuffer<global::Game.Areas.SubArea>(owner)) return;
            var subs = EntityManager.GetBuffer<global::Game.Areas.SubArea>(owner, true);
            var list = new List<Entity>(subs.Length);
            for (int i = 0; i < subs.Length; i++) list.Add(subs[i].m_Area);
            foreach (var a in list)
            {
                if (a == Entity.Null || !EntityManager.Exists(a) || EntityManager.HasComponent<Deleted>(a) || EntityManager.HasComponent<Temp>(a)) continue;
                DeleteAreaTree(a, depth + 1);
                EntityManager.AddComponent<Deleted>(a);
            }
        }

        /// <summary>
        /// Surviving nodes (foreign split nodes, released nodes) and their remaining edges get
        /// Updated so CS2 recomputes node geometry/composition once our edges are gone.
        /// The ConnectedEdge buffer is copied first: structural changes invalidate it.
        /// </summary>
        private void RefreshSurvivors(HashSet<Entity> nodes)
        {
            foreach (var n in nodes)
            {
                if (n == Entity.Null || !EntityManager.Exists(n) || EntityManager.HasComponent<Deleted>(n)
                    || EntityManager.HasComponent<Temp>(n) || !EntityManager.HasComponent<global::Game.Net.Node>(n)) continue;
                UnpinSurvivor(n);
                var edges = new List<Entity>();
                if (EntityManager.HasBuffer<ConnectedEdge>(n))
                {
                    var buf = EntityManager.GetBuffer<ConnectedEdge>(n, true);
                    for (int i = 0; i < buf.Length; i++) edges.Add(buf[i].m_Edge);
                }
                EntityManager.AddComponent<Updated>(n);
                foreach (var edge in edges)
                    if (EntityManager.Exists(edge) && !EntityManager.HasComponent<Deleted>(edge) && !EntityManager.HasComponent<Temp>(edge))
                        EntityManager.AddComponent<Updated>(edge);
            }
        }

        /// <summary>
        /// SPEC A-R8: BuildService.PinMarkerNets gives both end nodes of our marker edges Elevation(0) so the game does not
        /// refit them to the ground, including a city road's node where a lot drive joins it. Vanilla never leaves a
        /// top-level node with an all-zero Elevation (GenerateNodesSystem keeps a zero one only for owned or mesh-parented
        /// nodes), so a surviving city node with exactly that is our pin: it goes with the group. Without this the city
        /// node keeps counting as "elevated" after the build is gone.
        /// </summary>
        private void UnpinSurvivor(Entity n)
        {
            if (EntityManager.HasComponent<PlacemakerMember>(n) || EntityManager.HasComponent<global::Game.Common.Owner>(n)) return;
            if (!EntityManager.HasComponent<global::Game.Net.Elevation>(n)) return;
            // Another build's marker edge (or a not yet deleted one of this group) still joins here: that pin is still needed.
            if (EntityManager.HasBuffer<ConnectedEdge>(n))
            {
                var buf = EntityManager.GetBuffer<ConnectedEdge>(n, true);
                for (int i = 0; i < buf.Length; i++)
                {
                    var edge = buf[i].m_Edge;
                    if (EntityManager.Exists(edge) && !EntityManager.HasComponent<Deleted>(edge) && EntityManager.HasComponent<PlacemakerMember>(edge)) return;
                }
            }
            var el = EntityManager.GetComponentData<global::Game.Net.Elevation>(n).m_Elevation;
            if (el.x != 0f || el.y != 0f) return;
            EntityManager.RemoveComponent<global::Game.Net.Elevation>(n);
            _unpinned++;
        }

        private int _unpinned;

        /// <summary>
        /// Playtest 4b: removal must account for everything. Anything still tagged with the group after
        /// its record goes (a member a tool held as Temp copy, a kind removal does not handle) is
        /// logged per kind and released, so it never points at a dead group.
        /// </summary>
        private void ReportLeftovers(int groupId)
        {
            var left = new Dictionary<MemberKind, int>();
            var release = new List<Entity>();
            using (var all = _memberQuery.ToEntityArray(Allocator.Temp))
            using (var tags = _memberQuery.ToComponentDataArray<PlacemakerMember>(Allocator.Temp))
                for (int i = 0; i < all.Length; i++)
                {
                    if (tags[i].GroupId != groupId) continue;
                    if (EntityManager.HasComponent<Deleted>(all[i])) continue;
                    var k = Classify(all[i], tags[i]);
                    left[k] = (left.TryGetValue(k, out var n) ? n : 0) + 1;
                    if (!EntityManager.HasComponent<Temp>(all[i])) release.Add(all[i]);
                }
            if (left.Count == 0) return;
            var parts = new List<string>();
            foreach (var kv in left) parts.Add($"{kv.Value} {kv.Key}");
            Mod.Log.Warn($"Placemaker could not remove everything of group {groupId}: {string.Join(", ", parts)} left in the city.");
            foreach (var e in release) EntityManager.RemoveComponent<PlacemakerMember>(e);
        }

        private void GiveUp(Entity record)
        {
            // Something keeps the remaining members alive (e.g. edited forever by another tool).
            // Drop the record and leave the rest as vanilla objects rather than looping forever.
            var g = EntityManager.GetComponentData<PlacemakerGroup>(record);
            Mod.Log.Warn($"Placemaker could not finish removing group {g.GroupId}; remaining elements stay in the city.");
            // No land restore here: the elements left behind stand on the build's ground, and putting the old ground back
            // under them would bury or float them. The ground stays with them (logged).
            Mod.Log.Info($"Placemaker: the ground of group {g.GroupId} stays as built, under the elements left in the city.");
            using (var all = _memberQuery.ToEntityArray(Allocator.Temp))
            using (var tags = _memberQuery.ToComponentDataArray<PlacemakerMember>(Allocator.Temp))
                for (int i = 0; i < all.Length; i++)
                    if (tags[i].GroupId == g.GroupId && !EntityManager.HasComponent<Temp>(all[i]))
                        EntityManager.RemoveComponent<PlacemakerMember>(all[i]);
            EntityManager.AddComponent<Deleted>(record);
            _registry.MarkDirty();
        }

        /// <summary>Hands the group's build id to the land restore (it runs a few frames later, after the members are gone).</summary>
        private void RequestLandRestore(int groupId, string why)
        {
            try
            {
                var rec = _registry.GetRecord(groupId);
                if (rec == null) return;
                if (string.IsNullOrEmpty(rec.BuildGuid))
                {
                    if ((rec.Flags & GroupFlags.PermanentTerrain) != 0)
                        Mod.Log.Info($"Placemaker: group {groupId} was built before land snapshots existed; its terraform and paint stay.");
                    return;
                }
                World.GetOrCreateSystemManaged<Placemaker.Game.Restore.LandRestoreSystem>().RequestRestore(rec.BuildGuid, groupId, rec.BuiltAtTicks, why);
            }
            catch (Exception e)
            {
                Mod.Log.Warn($"Placemaker land restore of group {groupId} could not be requested: {e.Message}");
            }
        }

        internal static long Id(Entity e) => e == Entity.Null ? 0 : ((long)e.Version << 32) | (uint)e.Index;
    }
}
