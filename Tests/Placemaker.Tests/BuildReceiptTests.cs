using System.Collections.Generic;
using Placemaker.Core;
using Placemaker.Core.Build;
using Unity.Mathematics;
using Xunit;

namespace Placemaker.Tests
{
    public class BuildReceiptTests
    {
        private static GroupRecord Sample()
        {
            var root = new RecipeStep { Layer = "roads", Seed = 42, Enabled = true };
            root.Settings.Values["blockSize"] = 80.0;
            root.Settings.Values["avenues"] = true;
            root.Settings.Values["street"] = "Small Road";
            root.Settings.Values["mix"] = new[] { "Oak01", "Linden02" };
            root.Settings.Values["nothing"] = null;
            root.Settings.Values["intLike"] = 7;   // ints collapse to double
            root.Locked.Add("blockSize");
            root.Markers.Add(new EdgeMarker { Kind = "entrance", RingSegment = 2, RingT = 0.25f, Position = new float3(1, 2, 3) });
            var child = new RecipeStep { Layer = "park", Target = "block", Seed = 7 };
            child.Children.Add(new RecipeStep { Layer = "restrooms" });
            root.Children.Add(child);
            root.RegionOverrides["block:3"] = new RecipeStep { Layer = "plaza", Enabled = false };

            var area = new AreaShape { Ring = new[] { new float3(0, 1, 0), new float3(100, 2, 0), new float3(100, 3, 80) }, Source = AreaSource.Block };
            area.Attachments.Add(new BoundaryAttachment
            {
                Kind = AttachmentKind.RoadCrossing, ExternalId = (5L << 32) | 1234, ExternalT = 0.4f,
                Position = new float3(50, 2, 0), Direction = new float3(1, 0, 0), RoadWidth = 12, RingSegment = 0, RingT = 0.5f,
            });

            var r = new GroupRecord
            {
                GroupId = 17, RecipeId = "roads-with-parks", RecipeNameKey = "Placemaker.Recipe.RoadsParks", CustomName = "Ünïcode Park ✓",
                Flags = GroupFlags.PermanentTerrain | GroupFlags.ParkingEconomy, BuiltAtTicks = 638000000000000000L,
                Cost = 123456.5, Upkeep = 789.25,
                Recipe = new Recipe { Id = "roads-with-parks", NameKey = "k", Name = "", Icon = "coui://x.svg", FormatVersion = 3, Root = root },
                Area = area,
            };
            r.Stats.Add(new PlanStat("Placemaker.Stat.Trees", 41, "count"));
            r.Facts["parking.capacity"] = 312;
            r.PrefabNames.Add("Pedestrian Pathway");
            r.PrefabNames.Add("Oak01");
            return r;
        }

        [Fact]
        public void RoundTripPreservesEverything()
        {
            var a = Sample();
            var bytes = ReceiptCodec.Encode(a);
            var b = ReceiptCodec.Decode(bytes);
            Assert.NotNull(b);
            Assert.Equal(a.GroupId, b.GroupId);
            Assert.Equal(a.CustomName, b.CustomName);
            Assert.Equal(a.Flags, b.Flags);
            Assert.Equal(a.BuiltAtTicks, b.BuiltAtTicks);
            Assert.Equal(a.Cost, b.Cost);
            Assert.Equal(a.Upkeep, b.Upkeep);
            Assert.Single(b.Stats);
            Assert.Equal("count", b.Stats[0].Unit);
            Assert.Equal(312, b.Facts["parking.capacity"]);
            Assert.Equal(a.PrefabNames, b.PrefabNames);

            var rs = b.Recipe.Root;
            Assert.Equal("roads", rs.Layer);
            Assert.Equal(42u, rs.Seed);
            Assert.True(rs.Settings.ContentEquals(a.Recipe.Root.Settings) == false); // int 7 became double 7.0
            Assert.Equal(80.0, rs.Settings.Num("blockSize", 0));
            Assert.True(rs.Settings.B("avenues", false));
            Assert.Equal("Small Road", rs.Settings.S("street", ""));
            Assert.Equal(new[] { "Oak01", "Linden02" }, rs.Settings.L("mix"));
            Assert.Equal(7.0, rs.Settings.Num("intLike", 0));
            Assert.True(rs.Settings.Values.ContainsKey("nothing"));
            Assert.Contains("blockSize", rs.Locked);
            Assert.Equal(new float3(1, 2, 3), rs.Markers[0].Position);
            Assert.Equal("park", rs.Children[0].Layer);
            Assert.Equal("restrooms", rs.Children[0].Children[0].Layer);
            Assert.False(rs.RegionOverrides["block:3"].Enabled);
            Assert.Equal(3, b.Recipe.FormatVersion);

            Assert.Equal(AreaSource.Block, b.Area.Source);
            Assert.Equal(a.Area.Ring, b.Area.Ring);
            Assert.Equal(a.Area.Attachments[0].ExternalId, b.Area.Attachments[0].ExternalId);
            Assert.Equal(12f, b.Area.Attachments[0].RoadWidth);
        }

        [Fact]
        public void EncodingIsDeterministic()
        {
            Assert.Equal(ReceiptCodec.Encode(Sample()), ReceiptCodec.Encode(Sample()));
        }

        [Fact]
        public void GarbageDecodesToNullInsteadOfThrowing()
        {
            Assert.Null(ReceiptCodec.Decode(null));
            Assert.Null(ReceiptCodec.Decode(new byte[] { 1, 2, 3 }));
            var bytes = ReceiptCodec.Encode(Sample());
            var truncated = new byte[bytes.Length / 3];
            System.Array.Copy(bytes, truncated, truncated.Length);
            Assert.Null(ReceiptCodec.Decode(truncated));
            var rnd = new System.Random(1);
            for (int i = 0; i < 200; i++)
            {
                var b = (byte[])bytes.Clone();
                b[8 + rnd.Next(b.Length - 8)] ^= (byte)(1 + rnd.Next(255));
                ReceiptCodec.Decode(b);   // must not throw
            }
        }

        [Fact]
        public void TrailingBytesFromNewerVersionsAreIgnored()
        {
            var bytes = ReceiptCodec.Encode(Sample());
            var longer = new byte[bytes.Length + 16];
            bytes.CopyTo(longer, 0);
            var r = ReceiptCodec.Decode(longer);
            Assert.NotNull(r);
            Assert.Equal(17, r.GroupId);
        }

        [Fact]
        public void MissingRecipeAndAreaRoundTripAsNull()
        {
            var r = new GroupRecord { GroupId = 3 };
            var b = ReceiptCodec.Decode(ReceiptCodec.Encode(r));
            Assert.NotNull(b);
            Assert.Null(b.Recipe);
            Assert.Null(b.Area);
        }

        [Fact]
        public void CorruptRecipeBlockKeepsGroupMetadata()
        {
            var bytes = ReceiptCodec.Encode(Sample());
            // Find the recipe block: it starts right after the prefab names. Corrupt its first settings tag
            // by brute force: flip bytes in the second half until the recipe vanishes but the group survives.
            bool sawDegraded = false;
            for (int i = bytes.Length / 2; i < bytes.Length && !sawDegraded; i++)
            {
                var b = (byte[])bytes.Clone();
                b[i] = 0xFF;
                var r = ReceiptCodec.Decode(b);
                if (r != null && r.GroupId == 17 && (r.Recipe == null || r.Area == null)) sawDegraded = true;
            }
            Assert.True(sawDegraded);
        }

        [Fact]
        public void BuildGuidRoundTrips()
        {
            var a = Sample();
            a.BuildGuid = "0123456789abcdef0123456789abcdef";
            var b = ReceiptCodec.Decode(ReceiptCodec.Encode(a));
            Assert.Equal(a.BuildGuid, b.BuildGuid);
            Assert.Equal(a.BuildGuid, a.Clone().BuildGuid);
        }

        [Fact]
        public void Version2ReceiptDecodesWithoutBuildGuid()
        {
            // A v2 receipt ends after the park stats block: same bytes as v3 minus the last block, with version 2.
            var a = Sample();
            a.BuildGuid = "abc";
            var v3 = ReceiptCodec.Encode(a);
            // the v3 block is: block length (4) + string length (4) + "abc" (3)
            var v2 = new byte[v3.Length - (4 + 4 + 3)];
            System.Array.Copy(v3, v2, v2.Length);
            v2[4] = 2; v2[5] = 0; v2[6] = 0; v2[7] = 0;
            var r = ReceiptCodec.Decode(v2);
            Assert.NotNull(r);
            Assert.Equal("", r.BuildGuid);
            Assert.Equal(17, r.GroupId);
            Assert.NotNull(r.Recipe);
        }

        [Fact]
        public void CloneIsDeep()
        {
            var a = Sample();
            var c = a.Clone();
            c.Recipe.Root.Settings.Values["blockSize"] = 1.0;
            Assert.Equal(80.0, a.Recipe.Root.Settings.Num("blockSize", 0));
        }
    }
}
