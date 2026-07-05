using System.Collections.Generic;

namespace Weva.Paint.Conversion {
    // Shared slice-eviction for the process-static parse caches (gradients,
    // gradient brushes, filter chains, negative caches). These were
    // drop-new-on-overflow: once full they NEVER accepted a new entry, so an
    // animated value (e.g. `brightness(0.8342)` or a `currentcolor` gradient
    // producing one novel raw string per frame) would flood the cap and
    // permanently lock every legitimate reusable value out of caching for the
    // rest of the session — they re-parsed every frame forever (audit P16).
    //
    // Slice-eviction drops a fixed fraction when full so new entries can always
    // land. Eviction is correctness-neutral (a dropped entry just re-parses
    // once on its next use), so WHICH entries go is arbitrary — we take the
    // first `batch` the collection enumerates. This beats both the drop-new
    // lockout and a wholesale Clear() cliff: a reusable value caught in an
    // eviction re-parses once and is immediately re-cached, while single-use
    // animated values churn through. All callers are single-threaded by the
    // Unity main-thread convention (the resolvers AssertMainThread upstream),
    // so the shared scratch buffer is safe.
    internal static class ParseCacheEviction {
        static readonly List<object> s_Scratch = new List<object>(64);

        // Ensures `dict` has room for one more entry: if it is at/over `cap`,
        // evicts `cap/4` (min 1) arbitrary entries. Call right before an Add.
        public static void EnsureRoom<TKey, TValue>(Dictionary<TKey, TValue> dict, int cap) {
            if (dict.Count < cap) return;
            int batch = cap >> 2;
            if (batch < 1) batch = 1;
            s_Scratch.Clear();
            foreach (var k in dict.Keys) {
                s_Scratch.Add(k);
                if (s_Scratch.Count >= batch) break;
            }
            for (int i = 0; i < s_Scratch.Count; i++) dict.Remove((TKey)s_Scratch[i]);
            s_Scratch.Clear();
        }

        // HashSet variant for the negative ("don't cache this") sets.
        public static void EnsureRoom<T>(HashSet<T> set, int cap) {
            if (set.Count < cap) return;
            int batch = cap >> 2;
            if (batch < 1) batch = 1;
            s_Scratch.Clear();
            foreach (var k in set) {
                s_Scratch.Add(k);
                if (s_Scratch.Count >= batch) break;
            }
            for (int i = 0; i < s_Scratch.Count; i++) set.Remove((T)s_Scratch[i]);
            s_Scratch.Clear();
        }
    }
}
