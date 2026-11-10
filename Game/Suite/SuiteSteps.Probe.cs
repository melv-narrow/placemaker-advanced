// Game runner (fork, Workstream A diagnosis): two steps that look at what a build leaves behind.
//  * "probe": reads the ground textures (heights and paint, the GPU copies the game saves) over the build's outline and a
//    margin, and counts what stands there: the group's members, areas, objects, nets, and nodes or edges marked as
//    elevated. With "compare" it reports what changed since an earlier probe of the same scenario. It judges nothing.
//  * "road": lays a vanilla road straight across the build's outline (or where it was) through the runner's fixture tool,
//    then records how the game made it: each new edge's Elevation and how far its curve stands above the ground.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using Game.Simulation;
using Game.Tools;
using Newtonsoft.Json.Linq;
using Placemaker.Core.Restore;
using Placemaker.Game.Groups;
using Placemaker.Game.Restore;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace Placemaker.Game.Suite
{
    /// <summary>One probe's raw data (kept per scenario for a later "compare").</summary>
    internal sealed class ProbeData
    {
        public RectInt2 HeightRect, SplatRect;
        public ushort[] Heights, Splat;
        public Dictionary<string, int> Counts = new Dictionary<string, int>(StringComparer.Ordinal);
    }

    internal struct RectInt2 { public int X, Y, W, H; }

    public sealed partial class SuiteRunner
    {
        private LandTextures _probeTextures;

        private LandTextures ProbeTextures => _probeTextures ?? (_probeTextures = new LandTextures(
            _world.GetOrCreateSystemManaged<TerrainSystem>(), _world.GetOrCreateSystemManaged<TerrainMaterialSystem>(), _world.GetOrCreateSystemManaged<WaterSystem>()));

        private IEnumerator StepProbe(ScenarioState st, JObject step, JObject rec)
        {
            yield return Frames(30);   // ground written by a build or a restore reaches the textures within a few frames
            string name = (string)step["name"] ?? "probe" + rec["i"];
            string compare = (string)step["compare"];
            float margin = (float?)step["margin"] ?? 20f;
            var (lo, hi) = ProbeBox(st, margin);
            var box = new Bounds2(lo, hi);
            var data = new ProbeData();
            var tex = ProbeTextures;
            var terrain = _world.GetOrCreateSystemManaged<TerrainSystem>();
            float metresPerUnit = terrain.heightScaleOffset.x / 65535f;

            foreach (var layer in new[] { LandLayer.Height, LandLayer.Splat })
            {
                var t = tex.TextureOf(layer);
                if (t == null || !tex.RectOf(layer, t, box, out var r)) { rec[layer + "Error"] = "no texture or outside"; continue; }
                var px = tex.Read(t, r.x, r.y, r.width, r.height, out var err);
                if (px == null) { rec[layer + "Error"] = err; continue; }
                var rr = new RectInt2 { X = r.x, Y = r.y, W = r.width, H = r.height };
                if (layer == LandLayer.Height) { data.Heights = px; data.HeightRect = rr; }
                else { data.Splat = px; data.SplatRect = rr; }
            }
            CountWorld(st, box, data.Counts, rec);
            rec["counts"] = JObject.FromObject(data.Counts);
            rec["texels"] = new JObject { ["height"] = data.Heights?.Length ?? 0, ["paint"] = data.Splat?.Length ?? 0 };
            var restore = _world.GetExistingSystemManaged<LandRestoreSystem>();
            if (restore != null && !string.IsNullOrEmpty(restore.LastSummary)) rec["lastRestore"] = restore.LastSummary;

            if (!string.IsNullOrEmpty(compare))
            {
                if (!st.Probes.TryGetValue(compare, out var old)) rec["error"] = $"no earlier probe '{compare}' in this scenario";
                else
                {
                    var diff = new JObject();
                    if (old.Heights != null && data.Heights != null && Same(old.HeightRect, data.HeightRect))
                    {
                        int changed = 0, maxD = 0;
                        for (int i = 0; i < old.Heights.Length; i++)
                        {
                            int d = Math.Abs(old.Heights[i] - data.Heights[i]);
                            if (d > 2) changed++;
                            if (d > maxD) maxD = d;
                        }
                        diff["heightTexelsChanged"] = changed;
                        diff["heightMaxChangeM"] = Math.Round(maxD * metresPerUnit, 2);
                    }
                    if (old.Splat != null && data.Splat != null && Same(old.SplatRect, data.SplatRect))
                    {
                        int changed = 0;
                        for (int i = 0; i < old.Splat.Length; i++) if (old.Splat[i] != data.Splat[i]) changed++;
                        diff["paintTexelsChanged"] = changed;
                    }
                    var counts = new JObject();
                    foreach (var k in data.Counts.Keys.Union(old.Counts.Keys))
                    {
                        old.Counts.TryGetValue(k, out var a);
                        data.Counts.TryGetValue(k, out var b);
                        if (a != b) counts[k] = b - a;
                    }
                    diff["counts"] = counts;
                    rec["since"] = compare;
                    rec["diff"] = diff;
                }
            }
            st.Probes[name] = data;
            Say($"probe {name}: {rec["counts"].ToString(Newtonsoft.Json.Formatting.None)}"
                + (rec["diff"] != null ? $" | since {compare}: {rec["diff"].ToString(Newtonsoft.Json.Formatting.None)}" : "")
                + (rec["lastRestore"] != null ? $" | last restore: {rec["lastRestore"]}" : ""));
        }

        private static bool Same(RectInt2 a, RectInt2 b) => a.X == b.X && a.Y == b.Y && a.W == b.W && a.H == b.H;

        private (float2 lo, float2 hi) ProbeBox(ScenarioState st, float margin)
        {
            if (st.Ring != null && st.Ring.Length > 2) return Bounds(st.Ring, margin);
            var b = st.S.Block;
            return (new float2(b.X0, b.Z0) - margin, new float2(b.X1, b.Z1) + margin);
        }

        /// <summary>What stands in the box: the group's members by kind, and every area, object, edge and node, with the elevated ones.</summary>
        private void CountWorld(ScenarioState st, Bounds2 box, Dictionary<string, int> c, JObject rec)
        {
            var em = _world.EntityManager;
            bool In(float2 p) => p.x >= box.min.x && p.x <= box.max.x && p.y >= box.min.y && p.y <= box.max.y;
            void Inc(string k) => c[k] = (c.TryGetValue(k, out var n) ? n : 0) + 1;

            using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<PlacemakerMember>(), ComponentType.Exclude<Deleted>()))
            using (var all = q.ToEntityArray(Allocator.Temp))
                foreach (var e in all)
                    if (st.Group != 0 && em.GetComponentData<PlacemakerMember>(e).GroupId == st.Group) Inc("members." + _lookup.KindOf(e));

            using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<global::Game.Areas.Area>(), ComponentType.ReadOnly<global::Game.Areas.Node>(),
                ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>()))
            using (var all = q.ToEntityArray(Allocator.Temp))
                foreach (var e in all)
                {
                    var nodes = em.GetBuffer<global::Game.Areas.Node>(e, true);
                    if (nodes.Length == 0 || !In(nodes[0].m_Position.xz)) continue;
                    Inc(em.HasComponent<global::Game.Areas.Surface>(e) ? "areas.surface" : em.HasComponent<global::Game.Areas.Lot>(e) ? "areas.lot" : "areas.other");
                }

            using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<global::Game.Objects.Object>(), ComponentType.ReadOnly<global::Game.Objects.Transform>(),
                ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>()))
            using (var all = q.ToEntityArray(Allocator.Temp))
                foreach (var e in all)
                    if (In(em.GetComponentData<global::Game.Objects.Transform>(e).m_Position.xz))
                        Inc(em.HasComponent<global::Game.Buildings.Building>(e) ? "objects.building" : "objects.other");

            var elevated = new JArray();
            using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>()))
            using (var all = q.ToEntityArray(Allocator.Temp))
                foreach (var e in all)
                {
                    var mid = MathUtils.Position(em.GetComponentData<Curve>(e).m_Bezier, 0.5f);
                    if (!In(mid.xz)) continue;
                    Inc("edges");
                    if (em.HasComponent<Elevation>(e))
                    {
                        Inc("edges.elevated");
                        if (elevated.Count < 20) elevated.Add($"edge {_lookup.PrefabName(e)} at {mid.x:F0},{mid.z:F0}: {em.GetComponentData<Elevation>(e).m_Elevation}");
                    }
                }
            using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<global::Game.Net.Node>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>()))
            using (var all = q.ToEntityArray(Allocator.Temp))
                foreach (var e in all)
                {
                    var p = em.GetComponentData<global::Game.Net.Node>(e).m_Position;
                    if (!In(p.xz)) continue;
                    Inc("nodes");
                    if (em.HasComponent<Elevation>(e))
                    {
                        var el = em.GetComponentData<Elevation>(e).m_Elevation;
                        bool member = em.HasComponent<PlacemakerMember>(e);
                        bool owned = em.HasComponent<Owner>(e);
                        Inc(el.x == 0f && el.y == 0f && !owned ? (member ? "nodes.pinnedMember" : "nodes.pinnedForeign") : "nodes.elevated");
                        if (elevated.Count < 20) elevated.Add($"node {_lookup.PrefabName(e)} at {p.x:F0},{p.z:F0}: {el}{(member ? " (member)" : "")}{(owned ? " (owned)" : "")}");
                    }
                }
            if (elevated.Count > 0) rec["elevated"] = elevated;
        }

        /// <summary>A vanilla road straight across the build's box (east-west or north-south through its middle).</summary>
        private IEnumerator StepRoad(ScenarioState st, JObject step, JObject rec)
        {
            string dir = ((string)step["dir"] ?? "eastwest").ToLowerInvariant();
            string prefabName = (string)step["prefab"] ?? "Small Road";
            float margin = (float?)step["margin"] ?? 10f;
            var (lo, hi) = ProbeBox(st, margin);
            float2 mid = (lo + hi) * 0.5f;
            float2 a2 = dir == "northsouth" ? new float2(mid.x, lo.y) : new float2(lo.x, mid.y);
            float2 b2 = dir == "northsouth" ? new float2(mid.x, hi.y) : new float2(hi.x, mid.y);
            var em = _world.EntityManager;
            Entity prefab = _fixture.Prefabs.TryGetValue(prefabName, out var pf) && em.HasComponent<NetGeometryData>(pf) ? pf : Entity.Null;
            if (prefab == Entity.Null) { rec["error"] = $"no road prefab '{prefabName}'"; yield break; }
            var before = new HashSet<Entity>();
            using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<Edge>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>()))
            using (var all = q.ToEntityArray(Allocator.Temp))
                foreach (var e in all) before.Add(e);

            yield return CloseTool();
            yield return ApplyWithFixtureTool(t =>
            {
                var g = em.GetComponentData<NetGeometryData>(prefab);
                float len = math.distance(a2, b2);
                float max = g.m_EdgeLengthRange.max > 1f ? g.m_EdgeLengthRange.max : 200f;
                int pieces = math.max(1, (int)math.ceil(len / (max * 0.95f)));
                var random = new Unity.Mathematics.Random(0xB0AD5u);
                for (int k = 0; k < pieces; k++)
                {
                    float2 p = math.lerp(a2, b2, k / (float)pieces), q = math.lerp(a2, b2, (k + 1) / (float)pieces);
                    var a = new float3(p.x, _fixture.H(p.x, p.y), p.y);
                    var b = new float3(q.x, _fixture.H(q.x, q.y), q.y);
                    var curve = NetUtils.StraightCurve(a, b);
                    var start = new CoursePos
                    {
                        m_Position = a, m_Rotation = NetUtils.GetNodeRotation(MathUtils.StartTangent(curve)), m_CourseDelta = 0f,
                        m_Flags = (k == 0 ? CoursePosFlags.IsFirst : 0) | CoursePosFlags.IsLeft | CoursePosFlags.IsRight, m_ParentMesh = -1,
                    };
                    var end = new CoursePos
                    {
                        m_Position = b, m_Rotation = NetUtils.GetNodeRotation(MathUtils.EndTangent(curve)), m_CourseDelta = 1f,
                        m_Flags = (k == pieces - 1 ? CoursePosFlags.IsLast : 0) | CoursePosFlags.IsLeft | CoursePosFlags.IsRight, m_ParentMesh = -1,
                    };
                    var def = t.NewDefinition();
                    em.AddComponentData(def, new CreationDefinition { m_Prefab = prefab, m_RandomSeed = random.NextInt() });
                    em.AddComponent<Updated>(def);
                    em.AddComponentData(def, new NetCourse { m_Curve = curve, m_Length = MathUtils.Length(curve), m_FixedIndex = -1, m_StartPosition = start, m_EndPosition = end });
                }
                return pieces;
            }, $"vanilla road {prefabName} {dir} across the build");
            yield return Frames(30);
            yield return OpenTool();

            var hd = _world.GetOrCreateSystemManaged<TerrainSystem>().GetHeightData(true);
            var edges = new JArray();
            int elevated = 0;
            float worstAbove = 0f;
            using (var q = em.CreateEntityQuery(ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>()))
            using (var all = q.ToEntityArray(Allocator.Temp))
                foreach (var e in all)
                {
                    if (before.Contains(e) || em.GetComponentData<PrefabRef>(e).m_Prefab != prefab) continue;
                    var c = em.GetComponentData<Curve>(e).m_Bezier;
                    float above = 0f;
                    for (int s = 0; s <= 16; s++)
                    {
                        var p = MathUtils.Position(c, s / 16f);
                        above = math.max(above, p.y - TerrainUtils.SampleHeight(ref hd, p));
                    }
                    bool el = em.HasComponent<Elevation>(e);
                    if (el) elevated++;
                    worstAbove = math.max(worstAbove, above);
                    edges.Add(new JObject
                    {
                        ["at"] = $"{MathUtils.Position(c, 0.5f).x:F0},{MathUtils.Position(c, 0.5f).z:F0}",
                        ["elevation"] = el ? em.GetComponentData<Elevation>(e).m_Elevation.ToString() : "none",
                        ["maxAboveGroundM"] = Math.Round(above, 2),
                    });
                }
            rec["edges"] = edges;
            rec["elevatedEdges"] = elevated;
            rec["maxAboveGroundM"] = Math.Round(worstAbove, 2);
            Say($"road {prefabName} {dir}: {edges.Count} new edge(s), {elevated} with Elevation (bridge-like), curve up to {worstAbove:F2} m above the ground");
            if (edges.Count == 0) rec["error"] = "the road was not built";
        }
    }
}
