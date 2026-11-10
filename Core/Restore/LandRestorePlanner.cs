// Placemaker: decides, texel by texel, what removal puts back (GPL-3.0).
//
// Rule: a texel goes back to its "before" value only where the ground still shows exactly what the build left ("after",
// within a tolerance). Anything else changed it since: the player's own terraforming or paint, or a newer build that
// overlaps this one. Those texels are left alone, so removing an old build never undoes later work (SPEC A-R6, A-R11).

using System;

namespace Placemaker.Core.Restore
{
    public struct LandRestoreStats
    {
        /// <summary>Texels the build changed (before != after).</summary>
        public int Changed;
        /// <summary>Texels put back to their "before" value.</summary>
        public int Restored;
        /// <summary>Texels the build changed but something changed again since: left as they are.</summary>
        public int SkippedModified;
        /// <summary>Texels the build changed that already show their "before" value again: nothing to do.</summary>
        public int AlreadyBefore;
        /// <summary>Largest |current - after| among the skipped texels, in raw units (how far the later change went).</summary>
        public int MaxSkippedDelta;

        public override string ToString() =>
            $"{Restored} restored, {SkippedModified} skipped as changed since (max {MaxSkippedDelta} units), {AlreadyBefore} already back, of {Changed} changed by the build";
    }

    public static class LandRestorePlanner
    {
        /// <summary>
        /// Returns the texels to write (a copy of <paramref name="current"/> with restored texels set to "before"), or null
        /// when nothing needs writing. <paramref name="tolerance"/> is in raw units: |current - after| &lt;= tolerance still
        /// counts as "what the build left".
        /// </summary>
        public static ushort[] Plan(ushort[] before, ushort[] after, ushort[] current, int tolerance, out LandRestoreStats stats)
        {
            if (before == null) throw new ArgumentNullException(nameof(before));
            if (after == null) throw new ArgumentNullException(nameof(after));
            if (current == null) throw new ArgumentNullException(nameof(current));
            if (before.Length != after.Length || before.Length != current.Length)
                throw new ArgumentException("before, after and current must cover the same texels");
            stats = default;
            ushort[] result = null;
            for (int i = 0; i < before.Length; i++)
            {
                if (before[i] == after[i]) continue;
                stats.Changed++;
                int cur = current[i];
                if (cur == before[i]) { stats.AlreadyBefore++; continue; }
                int delta = Math.Abs(cur - after[i]);
                if (delta > tolerance)
                {
                    stats.SkippedModified++;
                    if (delta > stats.MaxSkippedDelta) stats.MaxSkippedDelta = delta;
                    continue;
                }
                if (result == null) result = (ushort[])current.Clone();
                result[i] = before[i];
                stats.Restored++;
            }
            return result;
        }

        /// <summary>
        /// A rollback of a build that never finished (failed or cancelled after its first ground write): every texel goes
        /// back to "before", because nothing else could have changed the ground in between. Null when nothing differs.
        /// </summary>
        public static ushort[] Rollback(ushort[] before, ushort[] current, out LandRestoreStats stats)
        {
            if (before == null) throw new ArgumentNullException(nameof(before));
            if (current == null) throw new ArgumentNullException(nameof(current));
            if (before.Length != current.Length) throw new ArgumentException("before and current must cover the same texels");
            stats = default;
            ushort[] result = null;
            for (int i = 0; i < before.Length; i++)
            {
                if (before[i] == current[i]) continue;
                stats.Changed++;
                if (result == null) result = (ushort[])current.Clone();
                result[i] = before[i];
                stats.Restored++;
            }
            return result;
        }

        /// <summary>Largest |a - b| over two texel arrays (raw units), for checks and logs.</summary>
        public static int MaxDifference(ushort[] a, ushort[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return -1;
            int m = 0;
            for (int i = 0; i < a.Length; i++) { int d = Math.Abs(a[i] - b[i]); if (d > m) m = d; }
            return m;
        }
    }
}
