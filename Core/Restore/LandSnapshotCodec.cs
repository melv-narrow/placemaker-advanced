// Placemaker: sidecar file format for a LandSnapshot (GPL-3.0).
//
// One file per build: ModsData/Placemaker/restore/<BuildGuid>.bin. Not in the save itself: the receipt blob is stored one
// byte per buffer element and capped at 1e6 counted items (ReceiptCodec), and a city with many builds would carry
// megabytes of texels in every save. The save keeps only the BuildGuid; the file is found by it.
// Layout: magic, version, then a Deflate-compressed body. Version first, fields only appended (RESEARCH-NOTES 5).

using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Placemaker.Core.Restore
{
    public static class LandSnapshotCodec
    {
        /// <summary>"PMLS" little-endian.</summary>
        public const uint Magic = 0x534C4D50;
        public const int CurrentVersion = 1;

        // Corrupt data must never make us allocate gigabytes: a 4096^2 texture is the largest thing a patch can cover.
        private const int MaxPatches = 16;
        private const int MaxTexels = 4096 * 4096;
        private const int MaxStringBytes = 256;

        public static byte[] Encode(LandSnapshot s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            using (var ms = new MemoryStream())
            {
                using (var head = new BinaryWriter(ms, new UTF8Encoding(false), true))
                {
                    head.Write(Magic);
                    head.Write(CurrentVersion);
                }
                using (var z = new DeflateStream(ms, CompressionLevel.Optimal, true))
                using (var w = new BinaryWriter(z, new UTF8Encoding(false), true))
                {
                    Str(w, s.BuildGuid);
                    w.Write(s.GroupId);
                    w.Write(s.CapturedAtTicks);
                    w.Write(s.HeightScale);
                    var patches = s.Patches ?? Array.Empty<LandPatch>();
                    w.Write(patches.Length);
                    foreach (var p in patches)
                    {
                        w.Write((int)p.Layer);
                        w.Write(p.X); w.Write(p.Y); w.Write(p.Width); w.Write(p.Height);
                        w.Write(p.TextureWidth); w.Write(p.TextureHeight);
                        Texels(w, p.Before);
                        w.Write(p.After != null);
                        if (p.After != null) Texels(w, p.After);
                    }
                }
                return ms.ToArray();
            }
        }

        /// <summary>Decodes a snapshot. Returns null for unreadable or inconsistent data (never throws).</summary>
        public static LandSnapshot Decode(byte[] data)
        {
            if (data == null || data.Length < 8) return null;
            try
            {
                using (var ms = new MemoryStream(data, false))
                {
                    using (var head = new BinaryReader(ms, new UTF8Encoding(false), true))
                    {
                        if (head.ReadUInt32() != Magic) return null;
                        if (head.ReadInt32() < 1) return null;
                    }
                    using (var z = new DeflateStream(ms, CompressionMode.Decompress))
                    using (var r = new BinaryReader(z, new UTF8Encoding(false)))
                    {
                        var s = new LandSnapshot
                        {
                            BuildGuid = Str(r),
                            GroupId = r.ReadInt32(),
                            CapturedAtTicks = r.ReadInt64(),
                            HeightScale = r.ReadSingle(),
                        };
                        int n = r.ReadInt32();
                        if (n < 0 || n > MaxPatches) return null;
                        s.Patches = new LandPatch[n];
                        for (int i = 0; i < n; i++)
                        {
                            var p = new LandPatch
                            {
                                Layer = (LandLayer)r.ReadInt32(),
                                X = r.ReadInt32(), Y = r.ReadInt32(), Width = r.ReadInt32(), Height = r.ReadInt32(),
                                TextureWidth = r.ReadInt32(), TextureHeight = r.ReadInt32(),
                            };
                            if (p.Width <= 0 || p.Height <= 0 || (long)p.Width * p.Height > MaxTexels) return null;
                            p.Before = Texels(r, p.Count);
                            if (r.ReadBoolean()) p.After = Texels(r, p.Count);
                            if (p.Before == null || !p.IsValid) return null;
                            s.Patches[i] = p;
                        }
                        return s;
                    }
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The sidecar file name for a build id (only hex digits survive, so an id can never escape the folder).</summary>
        public static string FileName(string buildGuid)
        {
            var sb = new StringBuilder(buildGuid?.Length ?? 0);
            if (buildGuid != null)
                foreach (char c in buildGuid)
                    if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')) sb.Append(char.ToLowerInvariant(c));
            return sb.Length == 0 ? null : sb + ".bin";
        }

        private static void Texels(BinaryWriter w, ushort[] t)
        {
            w.Write(t.Length);
            var bytes = new byte[t.Length * 2];
            Buffer.BlockCopy(t, 0, bytes, 0, bytes.Length);
            w.Write(bytes);
        }

        private static ushort[] Texels(BinaryReader r, int expected)
        {
            int n = r.ReadInt32();
            if (n != expected) return null;
            var bytes = r.ReadBytes(n * 2);
            if (bytes.Length != n * 2) return null;
            var t = new ushort[n];
            Buffer.BlockCopy(bytes, 0, t, 0, bytes.Length);
            return t;
        }

        private static void Str(BinaryWriter w, string s)
        {
            var b = Encoding.UTF8.GetBytes(s ?? "");
            if (b.Length > MaxStringBytes) throw new ArgumentException("string too long for a land snapshot");
            w.Write(b.Length);
            w.Write(b);
        }

        private static string Str(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > MaxStringBytes) throw new InvalidDataException("bad string length");
            return Encoding.UTF8.GetString(r.ReadBytes(n));
        }
    }
}
