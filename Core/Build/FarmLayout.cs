// Placemaker: what a farmstead plan means for a real vanilla farm (GPL-3.0). docs/fork/FARM-ECONOMY.md, Option A.
//
// With the setting "Farms produce" on, a farmstead build also places a vanilla farm: a hub (the placeholder building of
// a specialized agriculture zone) at the road, the level 1 farm building attached to it, and one vanilla field lot per
// planned field. The fields are the plan's own field regions (FarmsteadLayer, tag "field"). A vanilla field hides every
// tree, bush and prop that overlaps it (CanOverrideObjects) and grows its own crops or animals, so the planned objects
// inside the fields and inside the hub's lot are left out. Pure: the game layer resolves prefabs and emits definitions.

using System;
using System.Collections.Generic;
using Placemaker.Core.Geometry;
using Placemaker.Core.Layers.Types.Farmstead;
using Unity.Mathematics;

namespace Placemaker.Core.Build
{
    public static class FarmLayout
    {
        public const string FarmsteadId = "farmstead";
        /// <summary>A field smaller than this makes no vanilla lot (the game's own fields are hectares).</summary>
        public const float MinFieldArea = 400f;

        /// <summary>True for a layer that can become a real farm.</summary>
        public static bool IsFarm(AreaPlan layer) => layer != null && layer.GeneratorId == FarmsteadId;

        /// <summary>The field rings (XZ) of a farmstead layer, large enough for a vanilla field lot.</summary>
        public static List<float2[]> Fields(AreaPlan layer)
        {
            var list = new List<float2[]>();
            if (!IsFarm(layer)) return list;
            foreach (var r in layer.Regions)
            {
                if (r?.Ring == null || r.Ring.Length < 3 || r.Tags == null || Array.IndexOf(r.Tags, FarmsteadLayer.FieldTag) < 0) continue;
                var ring = new float2[r.Ring.Length];
                for (int i = 0; i < ring.Length; i++) ring[i] = r.Ring[i].xz;
                if (math.abs(Poly.SignedArea(ring)) >= MinFieldArea) list.Add(ring);
            }
            return list;
        }

        /// <summary>Livestock farm (more pasture and hay than arable fields), else a crop farm.</summary>
        public static bool Livestock(AreaPlan layer)
        {
            if (layer == null) return false;
            layer.Facts.TryGetValue("pastureFields", out var pasture);
            layer.Facts.TryGetValue("arableFields", out var arable);
            return pasture > arable;
        }

        /// <summary>Corners of a lot centred at <paramref name="centre"/>, front (facing +Z at yaw 0) toward the road.</summary>
        public static float2[] LotRect(float2 centre, float yaw, float width, float depth)
        {
            var fwd = new float2(math.sin(yaw), math.cos(yaw));
            var side = new float2(fwd.y, -fwd.x);
            float hw = width * 0.5f, hd = depth * 0.5f;
            return new[]
            {
                centre + fwd * hd + side * hw, centre + fwd * hd - side * hw,
                centre - fwd * hd - side * hw, centre - fwd * hd + side * hw,
            };
        }

        /// <summary>True when <paramref name="p"/> is inside any of the rings, or within <paramref name="margin"/> of one.</summary>
        public static bool InsideAny(List<float2[]> rings, float2 p, float margin = 0f)
        {
            if (rings == null) return false;
            foreach (var r in rings)
                if (Poly.Contains(r, p) || (margin > 0f && Poly.DistanceToBoundary(r, p) <= margin)) return true;
            return false;
        }
    }
}
