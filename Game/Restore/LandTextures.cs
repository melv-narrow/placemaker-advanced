// Placemaker: reads and writes rectangles of the ground textures a build changes (GPL-3.0).
//
// The four textures (Core/Restore/LandSnapshot.cs) live only on the GPU. Reading: AsyncGPUReadback of one rectangle,
// waited for at once (the rectangles are small: a 500 m build is about 150 x 150 texels). Writing: the texels go into a
// temporary Texture2D of the same format, Graphics.CopyTexture puts them into the rectangle, then the game is told what
// changed the same way its own brushes tell it (decompiled 1.6.3f1):
//  * heights: TerrainSystem.ApplyBrush ends with WaterSystem.TerrainWillChangeFromBrush, OnAreaChanged (ground height of
//    nets, objects and areas is refitted), the private m_TerrainChange/m_TerrainChangeArea pair (GetTerrainBrushUpdate:
//    the cascades and splatmap re-render that area), UpdateMinMax and TriggerAsyncChange (the CPU height copy is read
//    back again). We do the same.
//  * paint: TerrainMaterialSystem.ApplyToSplatmap only calls TerrainSystem.OnAreaChanged. We do the same.
// The proven precedent is SuiteFixture.ReadHeightmap / Replace (whole-map readback and ReplaceHeightmap); a rectangle
// copy avoids rewriting 32 MB and leaves the rest of the map, the water and the world map untouched.

using System;
using System.Reflection;
using Colossal.Mathematics;
using Game.Rendering;
using Game.Simulation;
using Placemaker.Core.Restore;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Placemaker.Game.Restore
{
    internal sealed class LandTextures
    {
        private readonly TerrainSystem _terrain;
        private readonly TerrainMaterialSystem _materials;
        private readonly WaterSystem _water;

        private static readonly FieldInfo s_terrainChange = typeof(TerrainSystem).GetField("m_TerrainChange", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo s_terrainChangeArea = typeof(TerrainSystem).GetField("m_TerrainChangeArea", BindingFlags.Instance | BindingFlags.NonPublic);
        private static bool s_warnedReflection;

        public LandTextures(TerrainSystem terrain, TerrainMaterialSystem materials, WaterSystem water)
        {
            _terrain = terrain;
            _materials = materials;
            _water = water;
        }

        /// <summary>The texture behind a layer, or null when the game has none right now (e.g. no world map, paint never used).</summary>
        public Texture TextureOf(LandLayer layer)
        {
            try
            {
                switch (layer)
                {
                    case LandLayer.Height: return _terrain.heightmap;
                    case LandLayer.WorldHeight: return _terrain.worldHeightmap is RenderTexture w ? w : null;
                    // Not created yet (nobody painted this city), the game hands out Texture2D.blackTexture (4x4 RGBA32):
                    // only the real R8G8 render texture counts.
                    case LandLayer.Splat: return _materials.UserSplatmap as RenderTexture;
                    case LandLayer.SplatWorld: return _materials.UserSplatmapWorld as RenderTexture;
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>The world rectangle a layer's texture covers: the playable area or the whole world.</summary>
        public void Coverage(LandLayer layer, out float2 offset, out float2 size)
        {
            bool world = layer == LandLayer.WorldHeight || layer == LandLayer.SplatWorld;
            offset = world ? _terrain.worldOffset : _terrain.playableOffset;
            size = world ? _terrain.worldSize : _terrain.playableArea;
        }

        /// <summary>
        /// Size of the user splatmaps when the game has not created them yet (TerrainMaterialSystem kSplatmapSize and
        /// kWorldSplatmapSize, 1.6.3f1). A city nobody painted has no user splatmap at all; the first paint brush creates it.
        /// </summary>
        public static int2 UncreatedSize(LandLayer layer) => layer == LandLayer.SplatWorld ? new int2(1024) : new int2(4096);

        /// <summary>The texel rectangle of <paramref name="tex"/> over a world box (padded by 2 texels), or false when it misses the texture.</summary>
        public bool RectOf(LandLayer layer, Texture tex, Bounds2 box, out RectInt rect)
        {
            rect = default;
            return tex != null && RectOf(layer, tex.width, tex.height, box, out rect);
        }

        public bool RectOf(LandLayer layer, int width, int height, Bounds2 box, out RectInt rect)
        {
            rect = default;
            Coverage(layer, out var offset, out var size);
            if (width <= 0 || height <= 0 || size.x <= 0f || size.y <= 0f) return false;
            float2 lo = (box.min - offset) / size * new float2(width, height);
            float2 hi = (box.max - offset) / size * new float2(width, height);
            int x0 = math.max(0, (int)math.floor(lo.x) - 2), y0 = math.max(0, (int)math.floor(lo.y) - 2);
            int x1 = math.min(width, (int)math.ceil(hi.x) + 2), y1 = math.min(height, (int)math.ceil(hi.y) + 2);
            if (x1 <= x0 || y1 <= y0) return false;
            rect = new RectInt(x0, y0, x1 - x0, y1 - y0);
            return true;
        }

        /// <summary>The world box of a texel rectangle (for the game's change notifications).</summary>
        public Bounds2 WorldOf(LandLayer layer, LandPatch p)
        {
            Coverage(layer, out var offset, out var size);
            var texel = size / new float2(p.TextureWidth, p.TextureHeight);
            return new Bounds2(offset + new float2(p.X, p.Y) * texel, offset + new float2(p.X + p.Width, p.Y + p.Height) * texel);
        }

        /// <summary>Reads one rectangle (16 bits per texel: R16 heights or R8G8 paint). Null on failure.</summary>
        public ushort[] Read(Texture tex, int x, int y, int w, int h, out string error)
        {
            error = null;
            if (tex == null) { error = "no texture"; return null; }
            try
            {
                var req = AsyncGPUReadback.Request(tex, 0, x, w, y, h, 0, 1);
                req.WaitForCompletion();
                if (req.hasError) { error = "GPU readback failed"; return null; }
                var data = req.GetData<ushort>();
                if (data.Length != w * h) { error = $"readback returned {data.Length} texels, expected {w * h}"; return null; }
                return data.ToArray();
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return null;
            }
        }

        /// <summary>Writes one rectangle into a render texture of the same 16-bit format. False (and why) on failure.</summary>
        public bool Write(Texture tex, int x, int y, int w, int h, ushort[] texels, out string error)
        {
            error = null;
            if (!(tex is RenderTexture rt)) { error = "not a render texture"; return false; }
            if (texels == null || texels.Length != w * h) { error = "wrong texel count"; return false; }
            Texture2D tmp = null;
            try
            {
                tmp = new Texture2D(w, h, rt.graphicsFormat, UnityEngine.Experimental.Rendering.TextureCreationFlags.None);
                tmp.SetPixelData(texels, 0);
                tmp.Apply(false, false);
                Graphics.CopyTexture(tmp, 0, 0, 0, 0, w, h, rt, 0, 0, x, y);
                return true;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return false;
            }
            finally
            {
                if (tmp != null) UnityEngine.Object.Destroy(tmp);
            }
        }

        /// <summary>After a height write: the same notifications TerrainSystem.ApplyBrush gives (see the header).</summary>
        public void HeightsChanged(Bounds2 area)
        {
            try { _water.TerrainWillChangeFromBrush(area); } catch (Exception e) { Mod.Log.Warn("Placemaker land restore: water notice failed: " + e.Message); }
            _terrain.OnAreaChanged(in area);
            MarkTerrainChange(area);
            try { _terrain.UpdateMinMax(default, area); } catch (Exception e) { Mod.Log.Warn("Placemaker land restore: min/max update failed: " + e.Message); }
            _terrain.TriggerAsyncChange();
        }

        /// <summary>After a paint write: what TerrainMaterialSystem.ApplyToSplatmap does.</summary>
        public void PaintChanged(Bounds2 area)
        {
            _terrain.OnAreaChanged(in area);
        }

        private void MarkTerrainChange(Bounds2 area)
        {
            if (s_terrainChange == null || s_terrainChangeArea == null)
            {
                if (!s_warnedReflection)
                {
                    s_warnedReflection = true;
                    Mod.Log.Warn("Placemaker land restore: TerrainSystem.m_TerrainChange not found in this game version; the restored ground may only redraw after the next terrain change.");
                }
                return;
            }
            try
            {
                bool pending = (bool)s_terrainChange.GetValue(_terrain);
                var a = new float4(area.min, area.max);
                if (pending)
                {
                    var cur = (float4)s_terrainChangeArea.GetValue(_terrain);
                    a = new float4(math.min(cur.xy, a.xy), math.max(cur.zw, a.zw));
                }
                s_terrainChangeArea.SetValue(_terrain, a);
                s_terrainChange.SetValue(_terrain, true);
            }
            catch (Exception e)
            {
                if (!s_warnedReflection) { s_warnedReflection = true; Mod.Log.Warn("Placemaker land restore: terrain change notice failed: " + e.Message); }
            }
        }
    }
}
