// Placemaker: puts the ground back when a build is removed (GPL-3.0). docs/fork/SPEC.md, Workstream A.
//
// Capture: BuildService calls CaptureBefore just before a build's first terraform or paint write, and CaptureAfter once
// the build has settled (the snapshot then goes to ModsData/Placemaker/restore/<BuildGuid>.bin). Restore: the removal
// system calls RequestRestore when a group's record goes; the work runs here a few frames later, after the members'
// Deleted has gone through the game, never in the frame nets are deleted (RESEARCH-NOTES 11-12). A failed or cancelled
// build rolls back from the snapshot it still holds in memory (RequestRollback).
// Phase: ToolUpdate, before BuildService, so an Edit's rebuild never starts in the frame a restore writes (BuildService
// also waits for Busy to clear before it reads the "before" ground of the new build).

using System;
using System.Collections.Generic;
using System.IO;
using Colossal.Mathematics;
using Game;
using Game.Rendering;
using Game.Simulation;
using Placemaker.Core.Restore;
using Placemaker.Game.Settings;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Scripting;

namespace Placemaker.Game.Restore
{
    public sealed partial class LandRestoreSystem : GameSystemBase
    {
        /// <summary>Setting "Put the land back when a build is removed" (default on). Off: no capture, no restore (upstream behaviour).</summary>
        public static bool Enabled = true;

        /// <summary>Frames between the removal of a group's record and the restore (members' Deleted processed, nets gone).</summary>
        private const int RestoreDelayFrames = 8;
        /// <summary>Raw height units still counted as "what the build left" (R16: 1 unit = heightScale / 65535, about 6 cm at 4096 m).</summary>
        private const int HeightTolerance = 2;
        /// <summary>Paint is compared exactly: only brushes change the user splatmap.</summary>
        private const int PaintTolerance = 0;

        private sealed class Job
        {
            public string Guid;
            public int GroupId;
            public LandSnapshot Snapshot;   // in memory (rollback, or a build of this session), else loaded from the sidecar
            public bool Rollback;
            public int DueFrame;
            public string Why;
            /// <summary>When the build was made (receipt BuiltAtTicks): a batch of removals restores newest first.</summary>
            public long BuiltAtTicks;
            /// <summary>Queued by a group removal (a normal restore, or the rollback of a build that failed after Apply).</summary>
            public bool FromRemoval;
        }

        /// <summary>
        /// A removal restore that has waited this long past its due frame runs even while a removal is still pending (a group
        /// can wait for ever on a tool's temporary copy). Never inside a build's capture window.
        /// </summary>
        private const int MaxWaitForRemovalsFrames = 600;

        /// <summary>
        /// Set by BuildService while a build holds a "before" snapshot (from its capture to its "after" capture or cleanup).
        /// A removal restore that ran inside that window would change ground the build already counts as "before", and the
        /// build's own removal would later bring that ground back. Removal restores wait; rollbacks do not (they only undo
        /// a build that never finished, which no capture can include).
        /// </summary>
        public bool CaptureOpen { get; set; }

        /// <summary>Snapshots of builds that failed after Apply: their group removal rolls back everything, not the "changed since" rule.</summary>
        private readonly Dictionary<string, LandSnapshot> _rollbackOnRemoval = new Dictionary<string, LandSnapshot>(StringComparer.Ordinal);
        private Placemaker.Game.Groups.GroupRemovalSystem _removal;

        private readonly List<Job> _jobs = new List<Job>();
        /// <summary>Snapshots of this session by BuildGuid (saves a file read when a build is removed in the session it was built).</summary>
        private readonly Dictionary<string, LandSnapshot> _recent = new Dictionary<string, LandSnapshot>(StringComparer.Ordinal);
        private LandTextures _tex;
        private TerrainSystem _terrain;

        /// <summary>True while a restore is waiting or running.</summary>
        public bool Busy => _jobs.Count > 0;

        /// <summary>The last restore's log line (the game runner reads it).</summary>
        public string LastSummary { get; private set; } = "";

        public static string Folder
        {
            get
            {
                var dir = Path.Combine(ModPaths.DataFolder, "restore");
                try { Directory.CreateDirectory(dir); } catch (Exception) { }
                return dir;
            }
        }

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            _terrain = World.GetOrCreateSystemManaged<TerrainSystem>();
            _tex = new LandTextures(_terrain, World.GetOrCreateSystemManaged<TerrainMaterialSystem>(), World.GetOrCreateSystemManaged<WaterSystem>());
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            // The world is being replaced: pending restores belonged to the old one.
            if (_jobs.Count > 0) Mod.Log.Info($"Placemaker land restore: {_jobs.Count} pending restore(s) dropped with the old world.");
            _jobs.Clear();
            _recent.Clear();
            _rollbackOnRemoval.Clear();
            CaptureOpen = false;
        }

        // ───────────── capture ─────────────

        /// <summary>
        /// Reads the "before" texels of every ground texture over <paramref name="box"/>. Null when restoring is off or
        /// nothing could be read (the build then behaves as upstream: its ground stays after removal).
        /// </summary>
        public LandSnapshot CaptureBefore(string buildGuid, int groupId, Bounds2 box)
        {
            if (!Enabled || string.IsNullOrEmpty(buildGuid)) return null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var patches = new List<LandPatch>();
            var problems = new List<string>();
            foreach (LandLayer layer in Enum.GetValues(typeof(LandLayer)))
            {
                var t = _tex.TextureOf(layer);
                bool paint = layer == LandLayer.Splat || layer == LandLayer.SplatWorld;
                if (t == null && paint)
                {
                    // Nobody painted this city yet: the user splatmap does not exist, which the game treats as no paint
                    // anywhere. "Before" is all zero; the build's first brush creates the texture and "after" reads it.
                    var size = LandTextures.UncreatedSize(layer);
                    if (!_tex.RectOf(layer, size.x, size.y, box, out var zr)) { problems.Add(layer + ": outside"); continue; }
                    patches.Add(new LandPatch
                    {
                        Layer = layer, X = zr.x, Y = zr.y, Width = zr.width, Height = zr.height,
                        TextureWidth = size.x, TextureHeight = size.y, Before = new ushort[zr.width * zr.height],
                    });
                    continue;
                }
                if (t == null) { problems.Add(layer + ": none"); continue; }
                if (!_tex.RectOf(layer, t, box, out var r)) { problems.Add(layer + ": outside"); continue; }
                var before = _tex.Read(t, r.x, r.y, r.width, r.height, out var err);
                if (before == null) { problems.Add(layer + ": " + err); continue; }
                patches.Add(new LandPatch
                {
                    Layer = layer, X = r.x, Y = r.y, Width = r.width, Height = r.height,
                    TextureWidth = t.width, TextureHeight = t.height, Before = before,
                });
            }
            if (patches.Count == 0 || patches.Find(p => p.Layer == LandLayer.Height) == null)
            {
                Mod.Log.Warn($"Placemaker land snapshot of group {groupId} unavailable ({string.Join("; ", problems)}): its ground will stay after removal.");
                return null;
            }
            var s = new LandSnapshot
            {
                BuildGuid = buildGuid, GroupId = groupId, HeightScale = _terrain.heightScaleOffset.x, Patches = patches.ToArray(),
            };
            long texels = 0;
            foreach (var p in patches) texels += p.Count;
            Mod.Log.Info($"Placemaker land snapshot of group {groupId} (before): {patches.Count} texture(s), {texels} texels, {sw.ElapsedMilliseconds} ms"
                + (problems.Count > 0 ? $"; not captured: {string.Join("; ", problems)}" : "") + ".");
            return s;
        }

        /// <summary>Reads the "after" texels and writes the sidecar. False when that failed (the build is kept; its ground will stay).</summary>
        public bool CaptureAfter(LandSnapshot s)
        {
            if (s == null) return false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var p in s.Patches)
            {
                var t = _tex.TextureOf(p.Layer);
                if (t == null || t.width != p.TextureWidth || t.height != p.TextureHeight) { p.After = (ushort[])p.Before.Clone(); continue; }
                p.After = _tex.Read(t, p.X, p.Y, p.Width, p.Height, out var err);
                if (p.After == null)
                {
                    Mod.Log.Warn($"Placemaker land snapshot of group {s.GroupId}: {p.Layer} after the build unreadable ({err}); its ground will stay after removal.");
                    return false;
                }
            }
            s.CapturedAtTicks = DateTime.UtcNow.Ticks;
            var parts = new List<string>();
            foreach (var p in s.Patches) parts.Add($"{p.Layer} {p.ChangedCount()}");
            string file = SidecarPath(s.BuildGuid);
            if (file == null) { _recent[s.BuildGuid] = s; return false; }
            try
            {
                var bytes = LandSnapshotCodec.Encode(s);
                var tmp = file + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                if (File.Exists(file)) File.Replace(tmp, file, null);
                else File.Move(tmp, file);
                _recent[s.BuildGuid] = s;
                Mod.Log.Info($"Placemaker land snapshot of group {s.GroupId} (after): texels changed by the build: {string.Join(", ", parts)}; "
                    + $"{bytes.Length / 1024.0:F1} KB in restore/{Path.GetFileName(file)}, {sw.ElapsedMilliseconds} ms.");
                return true;
            }
            catch (Exception e)
            {
                _recent[s.BuildGuid] = s;   // this session can still restore it
                Mod.Log.Warn($"Placemaker land snapshot of group {s.GroupId} could not be saved ({e.Message}): it restores in this session only.");
                return false;
            }
        }

        private static string SidecarPath(string guid)
        {
            var name = LandSnapshotCodec.FileName(guid);
            return name == null ? null : Path.Combine(Folder, name);
        }

        // ───────────── restore requests ─────────────

        /// <summary>A group was removed: put back what its build changed (by the build's id; nothing if it has no snapshot).</summary>
        public void RequestRestore(string buildGuid, int groupId, long builtAtTicks, string why)
        {
            if (string.IsNullOrEmpty(buildGuid)) return;
            foreach (var j in _jobs) if (j.Guid == buildGuid) return;
            if (_rollbackOnRemoval.TryGetValue(buildGuid, out var failed))
            {
                // The build failed after Apply and is being rolled back through the removal: everything goes back.
                _rollbackOnRemoval.Remove(buildGuid);
                _jobs.Add(new Job { Guid = buildGuid, GroupId = groupId, Snapshot = failed, Rollback = true, FromRemoval = true, DueFrame = UnityEngine.Time.frameCount + RestoreDelayFrames, Why = why, BuiltAtTicks = builtAtTicks });
                return;
            }
            if (!Enabled)
            {
                Mod.Log.Info($"Placemaker land restore of group {groupId} skipped: the setting is off.");
                return;
            }
            _jobs.Add(new Job { Guid = buildGuid, GroupId = groupId, FromRemoval = true, DueFrame = UnityEngine.Time.frameCount + RestoreDelayFrames, Why = why, BuiltAtTicks = builtAtTicks });
        }

        /// <summary>
        /// A build failed or was cancelled before Apply, after its first ground write: everything in the snapshot goes
        /// back on the next update (no nets are being deleted, and it must land before the player can start another build).
        /// </summary>
        public void RequestRollback(LandSnapshot s, string why)
        {
            if (s == null) return;
            _jobs.Add(new Job { Guid = s.BuildGuid, GroupId = s.GroupId, Snapshot = s, Rollback = true, DueFrame = UnityEngine.Time.frameCount, Why = why });
        }

        /// <summary>A build failed after Apply: its group is being removed, and that removal must roll back everything.</summary>
        public void RollbackOnRemoval(LandSnapshot s)
        {
            if (s != null && !string.IsNullOrEmpty(s.BuildGuid)) _rollbackOnRemoval[s.BuildGuid] = s;
        }

        private bool RemovalInProgress
        {
            get
            {
                if (_removal == null) _removal = World.GetExistingSystemManaged<Placemaker.Game.Groups.GroupRemovalSystem>();
                return _removal != null && _removal.Pending;
            }
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (_jobs.Count == 0) return;
            int frame = UnityEngine.Time.frameCount;
            // Rollbacks first and at once. Removal restores wait for a quiet moment: no build holding a "before" capture,
            // and no group removal still running, so a batch (Remove everything) is ordered as a whole, newest build first.
            // Restoring an older build before a newer one on top of it would leave the older build's ground under the newer.
            // Only a rollback of a build that never reached Apply skips the gates: no capture can include its ground.
            int pick = _jobs.FindIndex(j => j.Rollback && !j.FromRemoval && frame >= j.DueFrame);
            if (pick < 0)
            {
                if (CaptureOpen) return;
                bool starving = _jobs.Exists(j => frame - j.DueFrame > MaxWaitForRemovalsFrames);
                if (RemovalInProgress && !starving) return;
                for (int i = 0; i < _jobs.Count; i++)
                {
                    if (frame < _jobs[i].DueFrame) continue;
                    if (pick < 0 || _jobs[i].BuiltAtTicks > _jobs[pick].BuiltAtTicks) pick = i;
                }
                if (pick < 0) return;
            }
            var job = _jobs[pick];
            _jobs.RemoveAt(pick);
            try { Run(job); }
            catch (Exception e) { Mod.Log.Error(e, $"Placemaker land restore of group {job.GroupId} failed; its ground stays as it is"); }
        }

        private LandSnapshot Load(Job job)
        {
            if (job.Snapshot != null) return job.Snapshot;
            if (_recent.TryGetValue(job.Guid, out var s)) return s;
            var file = SidecarPath(job.Guid);
            if (file == null || !File.Exists(file)) return null;
            s = LandSnapshotCodec.Decode(File.ReadAllBytes(file));
            if (s != null && s.BuildGuid != job.Guid) return null;   // never apply another build's file
            return s;
        }

        private void Run(Job job)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var s = Load(job);
            if (s == null)
            {
                LastSummary = $"group {job.GroupId}: no land snapshot (built before this version, restoring was off, or the file is gone); ground left as it is";
                Mod.Log.Info("Placemaker land restore " + LastSummary + ".");
                return;
            }
            if (!job.Rollback && !Enabled)
            {
                LastSummary = $"group {job.GroupId}: the setting was turned off before the restore ran; ground left as it is";
                Mod.Log.Info("Placemaker land restore " + LastSummary + ".");
                return;
            }
            if (!job.Rollback && !s.Complete)
            {
                LastSummary = $"group {job.GroupId}: the snapshot has no 'after' state; ground left as it is";
                Mod.Log.Warn("Placemaker land restore " + LastSummary + ".");
                return;
            }
            if (math.abs(s.HeightScale - _terrain.heightScaleOffset.x) > 0.01f)
            {
                LastSummary = $"group {job.GroupId}: the snapshot is from a map with another height scale ({s.HeightScale} m, this map {_terrain.heightScaleOffset.x} m); ground left as it is";
                Mod.Log.Warn("Placemaker land restore " + LastSummary + ".");
                return;
            }
            var parts = new List<string>();
            float metresPerUnit = _terrain.heightScaleOffset.x / 65535f;
            foreach (var p in s.Patches)
            {
                bool height = p.Layer == LandLayer.Height || p.Layer == LandLayer.WorldHeight;
                var t = _tex.TextureOf(p.Layer);
                if (t == null) { parts.Add($"{p.Layer}: the texture does not exist now, skipped"); continue; }
                if (t.width != p.TextureWidth || t.height != p.TextureHeight) { parts.Add($"{p.Layer}: texture changed size, skipped"); continue; }
                var current = _tex.Read(t, p.X, p.Y, p.Width, p.Height, out var err);
                if (current == null) { parts.Add($"{p.Layer}: unreadable ({err})"); continue; }
                LandRestoreStats st;
                var write = job.Rollback
                    ? LandRestorePlanner.Rollback(p.Before, current, out st)
                    : LandRestorePlanner.Plan(p.Before, p.After, current, height ? HeightTolerance : PaintTolerance, out st);
                if (write == null) { parts.Add($"{p.Layer}: {Describe(st, height, metresPerUnit)}"); continue; }
                if (!_tex.Write(t, p.X, p.Y, p.Width, p.Height, write, out err)) { parts.Add($"{p.Layer}: write failed ({err})"); continue; }
                var area = _tex.WorldOf(p.Layer, p);
                if (height) _tex.HeightsChanged(area); else _tex.PaintChanged(area);
                // Read back what the GPU now holds: the residual is the honest measure of the restore.
                var check = _tex.Read(t, p.X, p.Y, p.Width, p.Height, out err);
                int residual = check == null ? -1 : LandRestorePlanner.MaxDifference(check, write);
                string res = residual < 0 ? "residual unknown" : height ? $"residual {residual * metresPerUnit:F2} m" : $"residual {residual}";
                parts.Add($"{p.Layer}: {Describe(st, height, metresPerUnit)}; {res}");
            }
            LastSummary = $"group {job.GroupId} ({(job.Rollback ? "rollback" : "removal")}, {job.Why}): " + string.Join("; ", parts) + $"; {sw.ElapsedMilliseconds} ms";
            Mod.Log.Info("Placemaker land restore " + LastSummary + ".");
        }

        private static string Describe(LandRestoreStats st, bool height, float metresPerUnit)
        {
            if (st.Changed == 0) return "nothing changed by the build";
            string skipped = st.SkippedModified == 0 ? "" : height
                ? $", {st.SkippedModified} left as changed since (up to {st.MaxSkippedDelta * metresPerUnit:F2} m)"
                : $", {st.SkippedModified} left as repainted since";
            return $"{st.Restored} of {st.Changed} restored" + skipped + (st.AlreadyBefore > 0 ? $", {st.AlreadyBefore} already back" : "");
        }
    }
}
