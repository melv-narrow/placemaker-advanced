using System;
using System.Collections.Generic;
using Placemaker.Core;
using Placemaker.Core.Build;
using Placemaker.Core.Geometry;
using Unity.Mathematics;
using Xunit;

namespace Placemaker.Tests
{
    public class FarmLayoutTests
    {
        private static float3[] Ring3(params float[] xz)
        {
            var r = new float3[xz.Length / 2];
            for (int i = 0; i < r.Length; i++) r[i] = new float3(xz[2 * i], 0, xz[2 * i + 1]);
            return r;
        }

        private static AreaPlan Farm()
        {
            var p = new AreaPlan { GeneratorId = FarmLayout.FarmsteadId };
            p.Regions.Add(new PlannedRegion { Ring = Ring3(0, 0, 50, 0, 50, 40, 0, 40), Tags = new[] { "place:field", "field" }, Key = "field:0" });
            p.Regions.Add(new PlannedRegion { Ring = Ring3(60, 0, 70, 0, 70, 10, 60, 10), Tags = new[] { "place:field", "field" }, Key = "field:1" });   // 100 m²: too small
            p.Regions.Add(new PlannedRegion { Ring = Ring3(0, 50, 50, 50, 50, 90, 0, 90), Tags = new[] { "farmyard" }, Key = "farmyard" });
            return p;
        }

        [Fact]
        public void FieldsAreTheFieldRegionsLargeEnoughForAVanillaField()
        {
            var fields = FarmLayout.Fields(Farm());
            Assert.Single(fields);
            Assert.Equal(2000f, math.abs(Poly.SignedArea(fields[0])), 1);
            Assert.Empty(FarmLayout.Fields(new AreaPlan { GeneratorId = "park", Regions = Farm().Regions }));
        }

        [Fact]
        public void LivestockWhenPasturesOutnumberArableFields()
        {
            var p = Farm();
            p.Facts["pastureFields"] = 3; p.Facts["arableFields"] = 1;
            Assert.True(FarmLayout.Livestock(p));
            p.Facts["arableFields"] = 5;
            Assert.False(FarmLayout.Livestock(p));
        }

        [Fact]
        public void LotRectIsCentredWithItsFrontTowardYaw()
        {
            var lot = FarmLayout.LotRect(new float2(10, 20), 0f, 16f, 32f);
            Assert.Equal(512f, math.abs(Poly.SignedArea(lot)), 1);
            Assert.True(Poly.Contains(lot, new float2(10, 20)));
            Assert.True(Poly.Contains(lot, new float2(10, 35)));    // 15 m toward +Z (front half of the 32 m depth)
            Assert.False(Poly.Contains(lot, new float2(19, 20)));   // 9 m sideways, beyond the 8 m half width
        }

        [Fact]
        public void InsideAnyWithMargin()
        {
            var fields = FarmLayout.Fields(Farm());
            Assert.True(FarmLayout.InsideAny(fields, new float2(25, 20)));
            Assert.False(FarmLayout.InsideAny(fields, new float2(51, 20)));
            Assert.True(FarmLayout.InsideAny(fields, new float2(51, 20), 1.5f));
        }

        private static NearbyNet Road(float z) => new NearbyNet
        {
            ExternalId = 7, Category = NetCategory.Road, Width = 12,
            A = new float3(-50, 0, z), B = new float3(50, 0, z), C = new float3(150, 0, z), D = new float3(250, 0, z),
        };

        [Fact]
        public void LotFrontNeverReachesBesideTheOutline()
        {
            // a trapezoid narrowing toward the road: its front side is 20 m wide, 4 m short of the kerb
            var outline = new[] { new float2(40, 3), new float2(60, 3), new float2(100, 100), new float2(0, 100) };
            var site = ParkCompanionPlanner.FindLot(outline, new[] { Road(-7) }, null, null, 32f, 32f);
            if (site.Found)
            {
                var lot = FarmLayout.LotRect(site.Position, site.Yaw, 32f, 32f);
                foreach (var c in lot)
                {
                    // every corner is inside, or straight in front of the outline toward the road
                    bool ok = Poly.Contains(outline, c) || Poly.DistanceToBoundary(outline, c) <= 0.75f
                        || Poly.Contains(outline, c + new float2(0, ParkCompanionPlanner.MaxFrontageGap + 0.75f));
                    Assert.True(ok, $"corner {c} reaches beside the outline");
                }
            }
        }

        [Fact]
        public void FindLotFitsALargeLotAtTheRoad()
        {
            var outline = new[] { new float2(0, 0), new float2(200, 0), new float2(200, 150), new float2(0, 150) };
            var site = ParkCompanionPlanner.FindLot(outline, new[] { Road(-7) }, null, null, 32f, 48f);
            Assert.True(site.Found);
            // front at the kerb: centre = road edge (-1) + gap + half depth
            Assert.Equal(-7 + 6 + ParkCompanionPlanner.CurbGap + 24, site.Position.y, 2);
            var lot = FarmLayout.LotRect(site.Position, site.Yaw, 32f, 48f);
            foreach (var c in lot) Assert.True(Poly.Contains(outline, c) || Poly.DistanceToBoundary(outline, c) < 0.8f);
            // the same outline is too shallow for a 160 m deep lot
            Assert.False(ParkCompanionPlanner.FindLot(outline, new[] { Road(-7) }, null, null, 32f, 160f).Found);
            // the 8 m companion search is unchanged
            Assert.Equal(-7 + 6 + ParkCompanionPlanner.CurbGap + 4, ParkCompanionPlanner.Find(outline, new[] { Road(-7) }).Position.y, 2);
        }
    }
}
