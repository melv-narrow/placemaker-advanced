using System;
using System.Linq;
using Placemaker.Core;
using Placemaker.Core.Build;
using Placemaker.Core.Tool;
using Unity.Mathematics;
using Xunit;

namespace Placemaker.Tests
{
    public class ParkStatsTests
    {
        /// <summary>An ordinary park: 40 trees and 12 benches per hectare, one amenity, a few play items.</summary>
        private static ParkStatsInput Generic(double areaM2, ParkStatsKind kind = ParkStatsKind.Park)
        {
            double ha = areaM2 / 10000.0;
            return new ParkStatsInput
            {
                Kind = kind, AreaM2 = areaM2,
                Trees = (int)Math.Round(40 * ha), Benches = (int)Math.Round(12 * ha), Amenities = (int)Math.Ceiling(ha), PlayItems = 2,
            };
        }

        [Fact]
        public void VanillaTableHasTheSurveyedParks()
        {
            Assert.True(VanillaParks.All.Count >= 40);
            Assert.True(VanillaParks.Regular.Count >= 35);
            foreach (var p in VanillaParks.All)
            {
                Assert.True(p.AreaM2 >= 64, p.Id);
                Assert.True(p.Attractiveness >= 1 && p.Leisure >= 1 && p.Range > 0 && p.Capacity > 0 && p.Magnitude > 0 && p.Upkeep > 0, p.Id);
            }
            var small = VanillaParks.Find("CityPark01");
            Assert.Equal("Small Park", small.Name);
            Assert.Equal(1024, small.AreaM2);
            Assert.Equal(1, small.Attractiveness);
            Assert.Equal(2500f, small.Range);
        }

        [Fact]
        public void OneHectareGenericParkLandsInsideTheVanillaBandOfComparableParks()
        {
            var r = ParkStatsEstimator.Estimate(Generic(10000));
            Assert.True(r.Applicable);
            var attr = r.Get(ParkStatIds.Attractiveness);
            // Raw, unscaled comparable vanilla parks of 0.5–1.5 ha: Large City Park 10, Multipurpose 13, Large Plaza 15,
            // Community City Park 18, Winding Park 20 — a generic 1 ha park belongs in that family.
            Assert.InRange(attr.Suggested, 10, 20);
            Assert.InRange(attr.Suggested, attr.BandMin, attr.BandMax);
            Assert.False(attr.Clamped);
            foreach (var s in r.Stats)
            {
                Assert.InRange(s.Value, s.BandMin, s.BandMax);
                Assert.True(s.BandMin <= s.BandMax, s.Id);
                Assert.NotEmpty(s.Similar);
                Assert.NotEmpty(s.Formula);
            }
            // The other stats follow vanilla's tiers for that attractiveness.
            Assert.InRange(r.ValueOf(ParkStatIds.Leisure), 3, 7);
            Assert.InRange(r.ValueOf(ParkStatIds.Range), 3000, 5000);
            Assert.InRange(r.ValueOf(ParkStatIds.Capacity), 1600, 2500);
            Assert.InRange(r.ValueOf(ParkStatIds.Entertainment), 12, 15);
            Assert.InRange(r.ValueOf(ParkStatIds.Upkeep), 8500, 14000);
        }

        [Theory]
        [InlineData("CityPark01")]
        [InlineData("CityPark06")]
        [InlineData("CityPark08")]
        [InlineData("CityPark03")]
        [InlineData("CityPark07")]
        [InlineData("CityPark11")]
        [InlineData("RS_CityPark01")]
        public void GenericParksOfVanillaSizesLandNearTheVanillaPark(string id)
        {
            var p = VanillaParks.Find(id);
            var r = ParkStatsEstimator.Estimate(Generic(p.AreaM2));
            double a = r.ValueOf(ParkStatIds.Attractiveness);
            Assert.InRange(a, p.Attractiveness / 2.0, Math.Max(2, p.Attractiveness * 2.0));
            Assert.InRange(r.ValueOf(ParkStatIds.Upkeep), p.Upkeep / 2.0, p.Upkeep * 2.0);
            Assert.InRange(r.ValueOf(ParkStatIds.Capacity), p.Capacity / 2.0, p.Capacity * 2.0);
        }

        [Fact]
        public void MoreTreesOrWaterOrFeaturesNeverMakeAParkLessAttractive()
        {
            foreach (double area in new[] { 300.0, 2500, 10000, 40000, 120000 })
            {
                var baseIn = Generic(area);
                double a0 = ParkStatsEstimator.Estimate(baseIn).ValueOf(ParkStatIds.Attractiveness);
                double l0 = ParkStatsEstimator.Estimate(baseIn).ValueOf(ParkStatIds.Leisure);
                double prev = a0;
                for (int extra = 0; extra <= 400; extra += 50)
                {
                    var i = Generic(area); i.Trees += extra;
                    double a = ParkStatsEstimator.Estimate(i).ValueOf(ParkStatIds.Attractiveness);
                    Assert.True(a >= prev, $"trees +{extra} at {area} m²: {a} < {prev}");
                    prev = a;
                }
                prev = a0;
                for (double w = 0; w <= area * 0.5; w += area * 0.05)
                {
                    var i = Generic(area); i.WaterM2 = w;
                    double a = ParkStatsEstimator.Estimate(i).ValueOf(ParkStatIds.Attractiveness);
                    Assert.True(a >= prev, $"water {w} at {area} m²");
                    prev = a;
                }
                var rich = Generic(area); rich.Centerpieces += 3; rich.Amenities += 4; rich.PlayItems += 10; rich.Benches += 30;
                var rr = ParkStatsEstimator.Estimate(rich);
                Assert.True(rr.ValueOf(ParkStatIds.Attractiveness) >= a0);
                Assert.True(rr.ValueOf(ParkStatIds.Leisure) >= l0);
            }
        }

        [Fact]
        public void BiggerParksAreNotLessAttractive()
        {
            double prev = 0;
            foreach (double area in new[] { 64.0, 256, 1000, 2500, 5000, 10000, 20000, 50000, 100000 })
            {
                double a = ParkStatsEstimator.Estimate(Generic(area)).ValueOf(ParkStatIds.Attractiveness);
                Assert.True(a >= prev, $"{area} m²: {a} < {prev}");
                prev = a;
            }
            Assert.True(prev <= ParkStatsEstimator.MaxSuggestedAttractiveness);
        }

        [Fact]
        public void TinyParksStayAtPocketParkLevels()
        {
            var r = ParkStatsEstimator.Estimate(Generic(120));
            Assert.Equal(1, r.ValueOf(ParkStatIds.Attractiveness));
            Assert.InRange(r.ValueOf(ParkStatIds.Upkeep), 200, 1500);
            Assert.InRange(r.ValueOf(ParkStatIds.Range), 300, 2500);
            Assert.Equal(8, r.ValueOf(ParkStatIds.Entertainment));
        }

        [Fact]
        public void PlazasRateHigherPerAreaAndSportsGiveMoreLeisure()
        {
            var park = ParkStatsEstimator.Estimate(Generic(5000));
            var plaza = Generic(5000, ParkStatsKind.Plaza); plaza.PavedM2 = 3500;
            var sports = Generic(5000, ParkStatsKind.Sports); sports.SportsItems = 12;
            var rp = ParkStatsEstimator.Estimate(plaza);
            var rs = ParkStatsEstimator.Estimate(sports);
            Assert.True(rp.ValueOf(ParkStatIds.Attractiveness) > park.ValueOf(ParkStatIds.Attractiveness));
            Assert.True(rs.ValueOf(ParkStatIds.Leisure) > park.ValueOf(ParkStatIds.Leisure));
            Assert.Contains(rp.Get(ParkStatIds.Attractiveness).Similar, kv => kv.Key.Kind == VanillaParkKind.Plaza);
        }

        [Fact]
        public void OverridesAreClampedToTheVanillaRangeOnlyWhenAsked()
        {
            var settings = new ParkStatsSettings();
            settings.Overrides[ParkStatIds.Attractiveness] = 400;
            var on = ParkStatsEstimator.Estimate(Generic(10000), settings).Get(ParkStatIds.Attractiveness);
            Assert.True(on.Overridden);
            Assert.True(on.Clamped);
            Assert.Equal(on.BandMax, on.Value);

            settings.KeepVanilla = false;
            var off = ParkStatsEstimator.Estimate(Generic(10000), settings).Get(ParkStatIds.Attractiveness);
            Assert.Equal(400, off.Value);
            Assert.False(off.Clamped);

            settings.Overrides[ParkStatIds.Attractiveness] = 1e9;   // never beyond the hard limit
            Assert.Equal(off.Max, ParkStatsEstimator.Estimate(Generic(10000), settings).ValueOf(ParkStatIds.Attractiveness));
        }

        [Fact]
        public void EstimateIsDeterministic()
        {
            var a = UiModels.ParkStats(ParkStatsEstimator.Estimate(Generic(7300))).ToString();
            var b = UiModels.ParkStats(ParkStatsEstimator.Estimate(Generic(7300))).ToString();
            Assert.Equal(a, b);
        }

        [Fact]
        public void KindFollowsTheFirstFillLayer()
        {
            Assert.Equal(ParkStatsKind.Park, ParkStatsInput.KindOf(Recipe.Single(GeneratorRegistry.Get("park"), 1), out _));
            Assert.Equal(ParkStatsKind.Plaza, ParkStatsInput.KindOf(Recipe.Single(GeneratorRegistry.Get("plaza"), 1), out _));
            Assert.Equal(ParkStatsKind.NotApplicable, ParkStatsInput.KindOf(Recipe.Single(GeneratorRegistry.Get("parking"), 1), out _));
            Assert.Equal(ParkStatsKind.NotApplicable, ParkStatsInput.KindOf(Recipe.Single(GeneratorRegistry.Get("roads"), 1), out _));
            Assert.Equal(ParkStatsKind.NotApplicable, ParkStatsInput.KindOf(null, out _));
            foreach (var g in GeneratorRegistry.All)
                if (GeneratorGroups.Of(g) == "commercial" || GeneratorGroups.Of(g) == "industrial")
                    Assert.Equal(ParkStatsKind.NotApplicable, ParkStatsInput.KindOf(Recipe.Single(g, 1), out _));
        }

        [Fact]
        public void InputCountsWhatThePlanContains()
        {
            var plan = new ComposedPlan();
            var l = new AreaPlan { GeneratorId = "park" };
            for (int i = 0; i < 7; i++) l.Objects.Add(new PlannedObject { Role = ObjectRole.Tree });
            l.Objects.Add(new PlannedObject { Role = ObjectRole.Bench });
            l.Objects.Add(new PlannedObject { Role = ObjectRole.Furniture, Category = AssetCategories.Playground });
            l.Objects.Add(new PlannedObject { Role = ObjectRole.Furniture, Category = AssetCategories.SportsGoal });
            l.Objects.Add(new PlannedObject { Role = ObjectRole.Furniture, Category = AssetCategories.Restroom });
            l.Objects.Add(new PlannedObject { Role = ObjectRole.Decal, Category = AssetCategories.DecalLeaves });
            l.Surfaces.Add(new PlannedSurface { Role = SurfaceRole.Water, Ring = Square(10) });
            l.Surfaces.Add(new PlannedSurface { Role = SurfaceRole.Paving, Ring = Square(20) });
            plan.Layers.Add(l);
            var input = ParkStatsInput.From(plan, 5000, Recipe.Single(GeneratorRegistry.Get("park"), 1));
            Assert.Equal(ParkStatsKind.Park, input.Kind);
            Assert.Equal(7, input.Trees);
            Assert.Equal(1, input.Benches);
            Assert.Equal(1, input.PlayItems);
            Assert.Equal(1, input.SportsItems);
            Assert.Equal(1, input.Amenities);
            Assert.Equal(100, input.WaterM2, 3);
            Assert.Equal(400, input.PavedM2, 3);
        }

        private static float3[] Square(float s) => new[] { new float3(0, 0, 0), new float3(s, 0, 0), new float3(s, 0, s), new float3(0, 0, s) };

        [Fact]
        public void SummaryCarriesParkStatsForParksOnly()
        {
            var plan = new ComposedPlan();
            plan.Layers.Add(new AreaPlan { GeneratorId = "park" });
            var park = UiModels.Summary(new SummaryInput { Plan = plan, Recipe = Recipe.Single(GeneratorRegistry.Get("park"), 1), AreaM2 = 8000 });
            Assert.True(park.Has("parkStats"));
            var ps = park["parkStats"];
            Assert.Equal(ParkStatIds.All.Length, ps["stats"].Items.Count);
            Assert.True(ps["keepVanilla"].AsBool());
            var parking = UiModels.Summary(new SummaryInput { Plan = plan, Recipe = Recipe.Single(GeneratorRegistry.Get("parking"), 1), AreaM2 = 8000 });
            Assert.False(parking.Has("parkStats"));
        }

        [Fact]
        public void ReceiptKeepsParkStatsAndPutsThemBackOnTheRecipe()
        {
            var recipe = Recipe.Single(GeneratorRegistry.Get("park"), 3);
            recipe.ParkStats = new ParkStatsSettings { KeepVanilla = false };
            recipe.ParkStats.Overrides[ParkStatIds.Attractiveness] = 42;
            var result = ParkStatsEstimator.Estimate(Generic(10000), recipe.ParkStats);
            var rec = new GroupRecord
            {
                GroupId = 9, Recipe = recipe, Area = new AreaShape { Ring = Square(100) },
                ParkStats = ParkStatsRecord.Of(result, recipe.ParkStats),
            };
            var back = ReceiptCodec.Decode(ReceiptCodec.Encode(rec));
            Assert.NotNull(back.ParkStats);
            Assert.False(back.ParkStats.Settings.KeepVanilla);
            Assert.Equal(42, back.ParkStats.Settings.Overrides[ParkStatIds.Attractiveness]);
            Assert.Equal(42, back.ParkStats.Applied[ParkStatIds.Attractiveness]);
            Assert.Equal((int)ParkStatsKind.Park, back.ParkStats.Kind);
            Assert.NotNull(back.Recipe.ParkStats);
            Assert.True(back.Recipe.ParkStats.ContentEquals(recipe.ParkStats));
            // Clone (used for Edit) keeps them too.
            Assert.True(recipe.Clone().ParkStats.ContentEquals(recipe.ParkStats));
        }

        [Fact]
        public void ReceiptWithoutParkStatsStillDecodes()
        {
            var rec = new GroupRecord { GroupId = 4, Recipe = Recipe.Single(GeneratorRegistry.Get("park"), 1) };
            var bytes = ReceiptCodec.Encode(rec);
            var back = ReceiptCodec.Decode(bytes);
            Assert.NotNull(back);
            Assert.Null(back.ParkStats);
            Assert.Null(back.Recipe.ParkStats);
            // A v1 reader's view: the record cut before the v2 block still decodes (newer saves load in older builds).
            int v3Block = 4 + 4;   // int length + empty BuildGuid string
            int v2Block = 4 + 1;   // int length + "has park stats" flag
            var cut = bytes.Take(bytes.Length - v3Block - v2Block).ToArray();
            Assert.NotNull(ReceiptCodec.Decode(cut));
        }
    }
}
