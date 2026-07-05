using System.Collections.Generic;

namespace Weva.Layout {
    // Slice-eviction for the layout measurement caches (InlineLayout's
    // fastMeasureCache, LineBreaker's measureCache / measureWindowCache).
    // These were full-Clear()-on-overflow: a layout with pathological text
    // variety (> cap distinct measure keys) wiped the whole cache at the cap
    // and then re-measured EVERY run from a cold cache — flipping from a high
    // hit rate to zero, repeatedly (audit L15).
    //
    // Slice-eviction drops a fixed fraction so the working set survives across
    // the cap boundary. Eviction is correctness-neutral (a dropped key just
    // re-measures once), so WHICH keys go is arbitrary — we take the first
    // `cap/4` the dictionary enumerates. Mirrors Paint.Conversion's
    // ParseCacheEviction but lives in Weva.Layout to respect the layout->paint
    // dependency direction (layout must not depend on paint). The per-eviction
    // array alloc is fine: eviction fires at most once per cap/4 inserts, not
    // per insert, so it never lands on the steady-state per-run hot path.
    internal static class LayoutCacheEviction {
        // Ensures `dict` has room for one more entry: if at/over `cap`, evicts
        // cap/4 (min 1) arbitrary entries. Call right before an Add.
        public static void EnsureRoom<TKey, TValue>(Dictionary<TKey, TValue> dict, int cap) {
            if (dict.Count < cap) return;
            int batch = cap >> 2;
            if (batch < 1) batch = 1;
            var keys = new TKey[batch];
            int n = 0;
            foreach (var k in dict.Keys) {
                keys[n++] = k;
                if (n >= batch) break;
            }
            for (int i = 0; i < n; i++) dict.Remove(keys[i]);
        }
    }
}
