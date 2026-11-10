using System;
using Placemaker.Core.Restore;
using Xunit;

namespace Placemaker.Tests
{
    public class LandRestoreTests
    {
        private static ushort[] U(params int[] v) => Array.ConvertAll(v, x => (ushort)x);

        [Fact]
        public void Restores_texels_that_still_show_what_the_build_left()
        {
            var before = U(100, 100, 100, 100);
            var after = U(100, 140, 160, 100);
            var current = U(100, 140, 160, 100);
            var result = LandRestorePlanner.Plan(before, after, current, 0, out var s);
            Assert.Equal(before, result);
            Assert.Equal(2, s.Changed);
            Assert.Equal(2, s.Restored);
            Assert.Equal(0, s.SkippedModified);
        }

        [Fact]
        public void Keeps_texels_changed_since_the_build()
        {
            // texel 1: the player raised it further after the build; texel 2: still as built
            var before = U(100, 100, 100);
            var after = U(100, 140, 160);
            var current = U(100, 190, 160);
            var result = LandRestorePlanner.Plan(before, after, current, 2, out var s);
            Assert.Equal(U(100, 190, 100), result);
            Assert.Equal(1, s.Restored);
            Assert.Equal(1, s.SkippedModified);
            Assert.Equal(50, s.MaxSkippedDelta);
        }

        [Fact]
        public void Tolerance_absorbs_rounding_only()
        {
            var before = U(100, 100);
            var after = U(150, 150);
            var current = U(152, 153);
            var result = LandRestorePlanner.Plan(before, after, current, 2, out var s);
            Assert.Equal(U(100, 153), result);
            Assert.Equal(1, s.Restored);
            Assert.Equal(1, s.SkippedModified);
        }

        [Fact]
        public void Texels_the_build_did_not_change_are_never_written()
        {
            // the player dug a pit where the build left the ground untouched: removal must not fill it
            var before = U(100, 100);
            var after = U(100, 100);
            var current = U(60, 100);
            var result = LandRestorePlanner.Plan(before, after, current, 2, out var s);
            Assert.Null(result);
            Assert.Equal(0, s.Changed);
        }

        [Fact]
        public void Nothing_to_do_returns_null()
        {
            var before = U(100, 100);
            var after = U(120, 100);
            var current = U(100, 100);   // already back (e.g. a second removal of a reloaded save)
            Assert.Null(LandRestorePlanner.Plan(before, after, current, 2, out var s));
            Assert.Equal(1, s.AlreadyBefore);
        }

        [Fact]
        public void Overlapping_newer_build_is_left_alone()
        {
            // A built first (100 -> 140). B was built on top of part of A (140 -> 200 at texel 1). Removing A must keep B.
            var aBefore = U(100, 100, 100);
            var aAfter = U(140, 140, 140);
            var current = U(140, 200, 140);
            var result = LandRestorePlanner.Plan(aBefore, aAfter, current, 2, out var s);
            Assert.Equal(U(100, 200, 100), result);
            Assert.Equal(1, s.SkippedModified);
        }

        [Fact]
        public void Rollback_puts_everything_back()
        {
            var before = U(100, 100, 100);
            var current = U(100, 130, 70);
            var result = LandRestorePlanner.Rollback(before, current, out var s);
            Assert.Equal(before, result);
            Assert.Equal(2, s.Restored);
            Assert.Null(LandRestorePlanner.Rollback(before, (ushort[])before.Clone(), out _));
        }

        [Fact]
        public void Mismatched_lengths_throw()
        {
            Assert.Throws<ArgumentException>(() => LandRestorePlanner.Plan(U(1, 2), U(1), U(1, 2), 0, out _));
        }

        private static LandSnapshot Sample()
        {
            var rnd = new Random(7);
            LandPatch Patch(LandLayer layer, int x, int y, int w, int h, int tex, bool after)
            {
                var b = new ushort[w * h];
                for (int i = 0; i < b.Length; i++) b[i] = (ushort)rnd.Next(0, 65536);
                ushort[] a = null;
                if (after) { a = (ushort[])b.Clone(); for (int i = 0; i < a.Length; i += 3) a[i] = (ushort)(a[i] ^ 0x55); }
                return new LandPatch { Layer = layer, X = x, Y = y, Width = w, Height = h, TextureWidth = tex, TextureHeight = tex, Before = b, After = a };
            }
            return new LandSnapshot
            {
                BuildGuid = "0123456789abcdef0123456789abcdef",
                GroupId = 12,
                CapturedAtTicks = 638000000000000000L,
                HeightScale = 4096f,
                Patches = new[]
                {
                    Patch(LandLayer.Height, 1000, 2000, 40, 30, 4096, true),
                    Patch(LandLayer.WorldHeight, 500, 500, 3, 2, 4096, true),
                    Patch(LandLayer.Splat, 1000, 2000, 40, 30, 4096, false),
                    Patch(LandLayer.SplatWorld, 10, 11, 1, 1, 1024, true),
                },
            };
        }

        [Fact]
        public void Codec_round_trips()
        {
            var s = Sample();
            var d = LandSnapshotCodec.Decode(LandSnapshotCodec.Encode(s));
            Assert.NotNull(d);
            Assert.Equal(s.BuildGuid, d.BuildGuid);
            Assert.Equal(s.GroupId, d.GroupId);
            Assert.Equal(s.CapturedAtTicks, d.CapturedAtTicks);
            Assert.Equal(s.HeightScale, d.HeightScale);
            Assert.Equal(s.Patches.Length, d.Patches.Length);
            for (int i = 0; i < s.Patches.Length; i++)
            {
                var a = s.Patches[i]; var b = d.Patches[i];
                Assert.Equal(a.Layer, b.Layer);
                Assert.Equal((a.X, a.Y, a.Width, a.Height, a.TextureWidth, a.TextureHeight), (b.X, b.Y, b.Width, b.Height, b.TextureWidth, b.TextureHeight));
                Assert.Equal(a.Before, b.Before);
                Assert.Equal(a.After, b.After);
            }
            Assert.False(d.Complete);   // the splat patch has no "after"
            Assert.Same(d.Patches[1], d.Find(LandLayer.WorldHeight));
        }

        [Fact]
        public void Codec_rejects_garbage_and_truncation()
        {
            Assert.Null(LandSnapshotCodec.Decode(null));
            Assert.Null(LandSnapshotCodec.Decode(new byte[] { 1, 2, 3 }));
            Assert.Null(LandSnapshotCodec.Decode(new byte[64]));
            var bytes = LandSnapshotCodec.Encode(Sample());
            var cut = new byte[bytes.Length / 2];
            Array.Copy(bytes, cut, cut.Length);
            Assert.Null(LandSnapshotCodec.Decode(cut));
        }

        [Fact]
        public void Codec_rejects_patch_outside_its_texture()
        {
            var s = Sample();
            s.Patches[0].X = 4090;   // 4090 + 40 > 4096
            Assert.Null(LandSnapshotCodec.Decode(LandSnapshotCodec.Encode(s)));
        }

        [Fact]
        public void File_name_keeps_only_hex()
        {
            Assert.Equal("0123abcdef.bin", LandSnapshotCodec.FileName("0123-ABCDEF"));
            Assert.Equal("ab.bin", LandSnapshotCodec.FileName("..\\..\\ab"));
            Assert.Null(LandSnapshotCodec.FileName(""));
            Assert.Null(LandSnapshotCodec.FileName(null));
        }
    }
}
