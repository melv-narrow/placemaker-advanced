// Placemaker: the build's side of "removal puts the land back" (GPL-3.0). docs/fork/SPEC.md, Workstream A.
//
// Before the first terraform or paint write, the ground over the build's footprint is captured; once the build has
// settled, the state it left is captured too and saved under the build's stable id (receipt v3 BuildGuid). Removal then
// restores from it (Game/Restore/LandRestoreSystem). A build that fails or is cancelled after writing rolls back at once.

using System;
using Colossal.Mathematics;
using Placemaker.Core.Restore;
using Placemaker.Game.Restore;
using Unity.Mathematics;

namespace Placemaker.Game.Build
{
    public sealed partial class BuildService
    {
        /// <summary>Margin around everything the build touches: the terraform bank, the pit-check rim and a few texels.</summary>
        private const float LandSnapshotMargin = TerraformBank + 12f + 8f;

        private LandRestoreSystem _landRestore;
        private LandSnapshot _land;
        /// <summary>The stable id of the build in progress (receipt v3), set when its record is created.</summary>
        private string _buildGuid = "";

        private LandRestoreSystem LandRestore => _landRestore ?? (_landRestore = World.GetOrCreateSystemManaged<LandRestoreSystem>());

        /// <summary>
        /// True while ground of a removed or failed build is still to be put back, or a group removal is still running (its
        /// restore follows). A new build waits: its "before" capture must not include ground that is about to change.
        /// </summary>
        private bool LandRestorePending
        {
            get
            {
                if (LandRestore.Busy) return true;
                var removal = World.GetExistingSystemManaged<Placemaker.Game.Groups.GroupRemovalSystem>();
                return removal != null && removal.Pending;
            }
        }

        /// <summary>Called right before the first terraform or paint write of a build with a group.</summary>
        private void BeginLandCapture()
        {
            _land = null;
            if (_work.Terraform.Count == 0 && !HasPaint()) return;   // nothing in the ground to put back
            // A loose build (road grid) has no group and no receipt: nothing can remove it later, but if it fails it still
            // rolls back, so it gets a snapshot under a throwaway id that is never saved.
            string id = LooseBuild || _groupId == 0 || string.IsNullOrEmpty(_buildGuid) ? Guid.NewGuid().ToString("N") : _buildGuid;
            try
            {
                if (!LandBox(out var box)) return;
                _land = LandRestore.CaptureBefore(id, _groupId, box);
                LandRestore.CaptureOpen = _land != null;
            }
            catch (Exception e)
            {
                _land = null;
                Mod.Log.Warn("Placemaker land snapshot failed (build continues; its ground will stay after removal): " + e.Message);
            }
        }

        /// <summary>The build settled: capture what it left and save it. Called from Finish.</summary>
        private void FinishLandCapture()
        {
            if (_land == null) return;
            try { if (!LooseBuild && _groupId != 0) LandRestore.CaptureAfter(_land); }
            catch (Exception e) { Mod.Log.Warn("Placemaker land snapshot (after) failed: " + e.Message); }
            _land = null;
            LandRestore.CaptureOpen = false;
        }

        /// <summary>
        /// The build stops in Cleanup. Not applied: nothing of it stays, so its ground goes back at once. Applied: the group
        /// is rolled back through the removal system, and that removal rolls the ground back completely (not the "changed
        /// since" rule of a normal removal: nothing but this build touched it).
        /// </summary>
        private void CleanupLand()
        {
            if (_land == null) { LandRestore.CaptureOpen = false; return; }
            try
            {
                if (_applied && _groupId != 0 && !LooseBuild) LandRestore.RollbackOnRemoval(_land);
                else LandRestore.RequestRollback(_land, "the build " + _cleanupPhase.ToString().ToLowerInvariant());
            }
            catch (Exception e) { Mod.Log.Warn("Placemaker land rollback could not be queued: " + e.Message); }
            _land = null;
            LandRestore.CaptureOpen = false;
        }

        /// <summary>World box of everything the build can write into the ground: its outline, terraform and paint rings.</summary>
        private bool LandBox(out Bounds2 box)
        {
            bool any = false;
            var lo = new float2(float.MaxValue);
            var hi = new float2(float.MinValue);
            void Add(float3[] ring)
            {
                if (ring == null) return;
                foreach (var p in ring)
                {
                    if (!math.isfinite(p.x) || !math.isfinite(p.z)) continue;
                    lo = math.min(lo, p.xz);
                    hi = math.max(hi, p.xz);
                    any = true;
                }
            }
            Add(_request.Area?.Ring);
            foreach (var l in _request.Plan.Layers)
            {
                if (l == null) continue;
                if (l.Terraform != null) foreach (var t in l.Terraform) Add(t?.Ring);
                if (l.Paint != null) foreach (var p in l.Paint) Add(p?.Ring);
            }
            box = any ? new Bounds2(lo - LandSnapshotMargin, hi + LandSnapshotMargin) : default;
            return any;
        }
    }
}
