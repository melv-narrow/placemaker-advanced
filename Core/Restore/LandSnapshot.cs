// Placemaker: what a build changed in the ground, so removal can put it back (GPL-3.0).
//
// A build writes four GPU textures: the base heightmap (terraform), the world heightmap (the same brush, coarser), and the
// two user splatmaps (terrain paint). None of them is an entity, so group removal never touched them: the land kept its
// banks, pits and paint after the build was gone (docs/fork/SPEC.md, Workstream A). A snapshot holds, per texture, one
// rectangle of raw 16-bit texels over the build's footprint: before the build's first write and after it settled.
// Pure data: the game layer reads and writes the textures (Game/Restore).

using System;

namespace Placemaker.Core.Restore
{
    /// <summary>The textures a build writes. Values are save format (sidecar files): never renumber.</summary>
    public enum LandLayer
    {
        /// <summary>TerrainSystem.heightmap: R16 over the playable area.</summary>
        Height = 0,
        /// <summary>TerrainSystem.worldHeightmap: R16 over the whole world (coarser).</summary>
        WorldHeight = 1,
        /// <summary>TerrainMaterialSystem.UserSplatmap: R8G8 (four 4-bit material weights) over the playable area.</summary>
        Splat = 2,
        /// <summary>TerrainMaterialSystem.UserSplatmapWorld: R8G8 over the whole world.</summary>
        SplatWorld = 3,
    }

    /// <summary>One rectangle of 16-bit texels of one texture, before and after the build.</summary>
    public sealed class LandPatch
    {
        public LandLayer Layer;
        /// <summary>Texel rectangle in the texture (x, y = lower-left texel; Unity texture rows run south to north).</summary>
        public int X, Y, Width, Height;
        /// <summary>Texture size when captured: a restore into a texture of another size is refused.</summary>
        public int TextureWidth, TextureHeight;
        /// <summary>Texels before the build's first write (row-major, Width * Height).</summary>
        public ushort[] Before;
        /// <summary>Texels once the build settled. Null until captured.</summary>
        public ushort[] After;

        public int Count => Width * Height;

        public bool IsValid =>
            Width > 0 && Height > 0 && X >= 0 && Y >= 0 && X + Width <= TextureWidth && Y + Height <= TextureHeight
            && Before != null && Before.Length == Count && (After == null || After.Length == Count);

        /// <summary>Texels the build changed (Before differs from After).</summary>
        public int ChangedCount()
        {
            if (After == null) return 0;
            int n = 0;
            for (int i = 0; i < Before.Length; i++) if (Before[i] != After[i]) n++;
            return n;
        }
    }

    /// <summary>Everything one build changed in the ground, keyed by the build's stable id.</summary>
    public sealed class LandSnapshot
    {
        /// <summary>The build's stable id (receipt v3 BuildGuid, "N" format). Never the group id: that one is reused.</summary>
        public string BuildGuid = "";
        /// <summary>Group id at build time, for logs only.</summary>
        public int GroupId;
        /// <summary>DateTime.UtcNow.Ticks when the "after" state was captured.</summary>
        public long CapturedAtTicks;
        /// <summary>Metres per height unit of the map (TerrainSystem.heightScaleOffset.x): a different map refuses the restore.</summary>
        public float HeightScale;
        public LandPatch[] Patches = Array.Empty<LandPatch>();

        public LandPatch Find(LandLayer layer)
        {
            foreach (var p in Patches) if (p != null && p.Layer == layer) return p;
            return null;
        }

        public bool Complete
        {
            get
            {
                if (Patches.Length == 0) return false;
                foreach (var p in Patches) if (p == null || p.After == null) return false;
                return true;
            }
        }
    }
}
