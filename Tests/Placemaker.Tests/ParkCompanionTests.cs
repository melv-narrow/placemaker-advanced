using System;
using System.Collections.Generic;
using Placemaker.Core;
using Placemaker.Core.Build;
using Placemaker.Core.Geometry;
using Unity.Mathematics;
using Xunit;

namespace Placemaker.Tests
{
    public class ParkCompanionTests
    {
        // ── quantisation ──

        [Theory]
        [InlineData(1)] [InlineData(7)] [InlineData(14)] [InlineData(23)] [InlineData(57)] [InlineData(99)] [InlineData(137)] [InlineData(480)]
        public void CountsQuantiseCloselyAndIdempotently(double v)
        {
            int q = ParkPrefabKey.QuantiseCount(v);
            Assert.Equal(q, ParkPrefabKey.QuantiseCount(q));
            Assert.True(Math.Abs(q - v) <= Math.Max(0.5, v * 0.13), $"{v} → {q}");
        }

        [Fact]
        public void QuantisationIsMonotonic()
        {
            int pa = -1, pr = -1, pc = -1, pu = -1;
            for (double v = 0; v < 60000; v += 37)
            {
                int a = ParkPrefabKey.QuantiseCount(v / 100), r = ParkPrefabKey.QuantiseRange(v / 5), c = ParkPrefabKey.QuantiseCapacity(v / 3), u = ParkPrefabKey.QuantiseUpkeep(v);
                Assert.True(a >= pa && r >= pr && c >= pc && u >= pu, v.ToString());
                pa = a; pr = r; pc = c; pu = u;
            }
        }

        [Fact]
        public void PrefabNameRoundTripsAndIsStable()
        {
            var values = new Dictionary<string, double>
            {
                [ParkStatIds.Attractiveness] = 14, [ParkStatIds.Leisure] = 6, [ParkStatIds.Range] = 4300,
                [ParkStatIds.Capacity] = 1950, [ParkStatIds.Entertainment] = 13, [ParkStatIds.Upkeep] = 10600,
            };
            var key = ParkPrefabKey.From(values, false);
            Assert.Equal("Placemaker Park a14 l6 r4250 c2000 m13 u11000", key.Name);
            Assert.True(ParkPrefabKey.TryParse(key.Name, out var back));
            Assert.Equal(key, back);
            var beach = ParkPrefabKey.From(values, true);
            Assert.EndsWith(" b", beach.Name);
            Assert.True(ParkPrefabKey.TryParse(beach.Name, out var b2) && b2.Beach);
            Assert.False(ParkPrefabKey.TryParse("Placemaker Park a14", out _));
            Assert.False(ParkPrefabKey.TryParse("Small Park", out _));
            Assert.False(ParkPrefabKey.TryParse("Placemaker Park a01 l6 r4250 c2000 m13 u11000", out _));   // non-canonical
        }

        [Fact]
        public void NearbyValuesShareOnePrefab()
        {
            var a = new Dictionary<string, double> { [ParkStatIds.Attractiveness] = 61, [ParkStatIds.Range] = 5980, [ParkStatIds.Upkeep] = 41200 };
            var b = new Dictionary<string, double> { [ParkStatIds.Attractiveness] = 59, [ParkStatIds.Range] = 6020, [ParkStatIds.Upkeep] = 40800 };
            Assert.Equal(ParkPrefabKey.From(a, false).Name, ParkPrefabKey.From(b, false).Name);
        }

        // ── frontage ──

        private static float2[] Rect(float w, float d) => new[] { new float2(0, 0), new float2(w, 0), new float2(w, d), new float2(0, d) };

        /// <summary>A straight road of width 12 running along z = <paramref name="z"/>.</summary>
        private static NearbyNet Road(float z, float x0 = -50, float x1 = 150, long id = 7) => new NearbyNet
        {
            ExternalId = id, Category = NetCategory.Road, Width = 12,
            A = new float3(x0, 0, z), B = new float3(x0 + (x1 - x0) / 3, 0, z), C = new float3(x0 + 2 * (x1 - x0) / 3, 0, z), D = new float3(x1, 0, z),
        };

        [Fact]
        public void PicksALotOnTheRoadSideFacingTheRoad()
        {
            var outline = Rect(100, 60);
            var site = ParkCompanionPlanner.Find(outline, new[] { Road(-7) });   // road centre 7 m outside the z = 0 edge
            Assert.True(site.Found);
            Assert.Equal(7, site.Road);
            // front faces −Z (the road): yaw = atan2(0, −1) = π
            Assert.Equal(Math.PI, Math.Abs(site.Yaw), 3);
            // lot touches the road edge (centre = road edge + gap + 4 m) and lies inside the outline
            Assert.Equal(-7 + 6 + ParkCompanionPlanner.CurbGap + 4, site.Position.y, 2);
            Assert.True(Poly.Contains(outline, site.Position));
            Assert.InRange(site.Position.x, 4, 96);
        }

        [Fact]
        public void KeepsClearOfTheBuildsObjectsAndPaths()
        {
            var outline = Rect(100, 60);
            var objects = new List<float2>();
            for (float x = 0; x <= 70; x += 3) objects.Add(new float2(x, 5));   // benches along most of the frontage
            var paths = new List<(float2, float2, float)> { (new float2(85, 0), new float2(85, 60), 2f) };
            var site = ParkCompanionPlanner.Find(outline, new[] { Road(-7) }, objects, paths);
            Assert.True(site.Found);
            foreach (var o in objects) Assert.True(math.distance(o, site.Position) >= 4, "on a bench");
            Assert.True(Math.Abs(site.Position.x - 85) >= 6, "on the path");
        }

        [Fact]
        public void NoRoadMeansNoSiteWithAReason()
        {
            var outline = Rect(100, 60);
            var none = ParkCompanionPlanner.Find(outline, new NearbyNet[0]);
            Assert.False(none.Found);
            Assert.Equal(ParkCompanionPlanner.NoRoadKey, none.ReasonKey);
            // a road 40 m away does not front the area
            Assert.False(ParkCompanionPlanner.Find(outline, new[] { Road(-40) }).Found);
            // paths are not roads
            var path = Road(-3); path.Category = NetCategory.Pathway;
            Assert.False(ParkCompanionPlanner.Find(outline, new[] { path }).Found);
            // a frontage fully taken by objects → no site
            var crowd = new List<float2>();
            for (float x = 0; x <= 100; x += 2) for (float z = 0; z <= 60; z += 2) crowd.Add(new float2(x, z));
            Assert.False(ParkCompanionPlanner.Find(outline, new[] { Road(-7) }, crowd).Found);
        }

        [Fact]
        public void EachFailureSaysWhy()
        {
            var outline = Rect(100, 60);
            Assert.Equal(ParkCompanionPlanner.WhyNoRoad, ParkCompanionPlanner.Find(outline, new NearbyNet[0]).DetailKey);
            Assert.Equal(ParkCompanionPlanner.WhyNotAlongRoad, ParkCompanionPlanner.Find(outline, new[] { Road(-40) }).DetailKey);
            var tiny = Rect(6, 6);
            Assert.Equal(ParkCompanionPlanner.WhyShortEdges, ParkCompanionPlanner.Find(tiny, new[] { Road(-7) }).DetailKey);
            // a strip only 5 m deep along the road: the 8 m lot never fits inside
            Assert.Equal(ParkCompanionPlanner.WhyNoRoom, ParkCompanionPlanner.Find(Rect(100, 5), new[] { Road(-7) }).DetailKey);
            var crowd = new List<float2>();
            for (float x = 0; x <= 100; x += 2) for (float z = 0; z <= 60; z += 2) crowd.Add(new float2(x, z));
            Assert.Equal(ParkCompanionPlanner.WhyBlocked, ParkCompanionPlanner.Find(outline, new[] { Road(-7) }, crowd).DetailKey);
            var ok = ParkCompanionPlanner.Find(outline, new[] { Road(-7) });
            Assert.Equal("", ok.DetailKey);
            // an outline 4 m short of the kerb still gets a lot: its front reaches over the verge to the road
            var shortSite = ParkCompanionPlanner.Find(outline, new[] { Road(-11) });
            Assert.True(shortSite.Found);
            Assert.Equal(-11 + 6 + ParkCompanionPlanner.CurbGap + 4, shortSite.Position.y, 2);
            // an outline that stops 10 m short of the kerb: not along a road (the player is told to draw up to it)
            Assert.Equal(ParkCompanionPlanner.WhyNotAlongRoad, ParkCompanionPlanner.Find(outline, new[] { Road(-16) }).DetailKey);
        }

        [Fact]
        public void FindIsDeterministic()
        {
            var outline = Rect(80, 80);
            var nets = new[] { Road(-7), Road(87, id: 9) };
            var a = ParkCompanionPlanner.Find(outline, nets);
            var b = ParkCompanionPlanner.Find(outline, nets);
            Assert.Equal(a.Position, b.Position);
            Assert.Equal(a.Road, b.Road);
        }

        // ── which stats reach the game ──

        [Fact]
        public void CompanionMakesEveryStatRealAndNoRoadFallsBackToAttractiveness()
        {
            var input = new ParkStatsInput { Kind = ParkStatsKind.Park, AreaM2 = 6000, Trees = 20 };
            var yes = ParkStatsEstimator.WithCompanion(ParkStatsEstimator.Estimate(input), true, ParkCompanionPlanner.Find(Rect(100, 60), new[] { Road(-7) }));
            Assert.Equal(ParkCompanionState.Yes, yes.Companion);
            Assert.All(yes.Stats, s => Assert.True(s.Applied, s.Id));

            var noRoad = ParkStatsEstimator.WithCompanion(ParkStatsEstimator.Estimate(input), true, ParkCompanionPlanner.Find(Rect(100, 60), new NearbyNet[0]));
            Assert.Equal(ParkCompanionState.NoRoad, noRoad.Companion);
            Assert.All(noRoad.Stats, s => Assert.Equal(s.Id == ParkStatIds.Attractiveness, s.Applied));

            var off = ParkStatsEstimator.WithCompanion(ParkStatsEstimator.Estimate(input), false, default);
            Assert.Equal(ParkCompanionState.Off, off.Companion);
            Assert.All(off.Stats, s => Assert.Equal(s.Id == ParkStatIds.Attractiveness, s.Applied));

            var key = ParkStatsEstimator.KeyOf(yes);
            Assert.Equal(ParkPrefabKey.QuantiseCount(yes.ValueOf(ParkStatIds.Attractiveness)), key.Attractiveness);
        }
    }
}
