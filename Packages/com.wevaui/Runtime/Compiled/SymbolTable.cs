using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Weva.Compiled {
    // Bidirectional string <-> int interner. Symbol id 0 is reserved for the
    // empty / missing sentinel; the first interned string returns id 1. This
    // is intentional so default(int) on a snapshot field reads as "absent."
    internal sealed class SymbolTable {
        readonly Dictionary<string, int> ids = new();
        readonly List<string> strings = new();
        // PERF-1 (Mono Refill): substring interning used to do an O(N) linear
        // scan over EVERY interned string per class token — Unity Mono's slow
        // char compares made DomSnapshot.Refill 11× slower than CoreCLR
        // (3201µs vs 279µs on the same code). This hash index buckets ids by
        // an FNV-1a hash computed over the chars, so the substring probe is a
        // single dictionary lookup + (usually) one candidate compare, with no
        // Substring allocation on the hit path. The same hash function runs
        // over full strings on insert and (start,length) windows on probe, so
        // a window always lands in its full string's bucket.
        readonly Dictionary<int, List<int>> hashBuckets = new();

        // PERF-1 (Mono Refill, part 2): every Refill re-interns the SAME
        // string INSTANCES (tag names, attribute names/values are stable
        // references on an unchanged DOM), and Mono's per-char string
        // hashing made that the dominant remaining cost. This identity map
        // resolves a previously-seen reference with a pointer hash — no
        // char access at all. Capped so transient instances (StringBuilder
        // output, substrings) can't grow it unboundedly; on overflow the
        // value-equality path below still resolves correctly.
        const int RefFastPathCap = 4096;
        readonly Dictionary<string, int> byRef = new(ReferenceComparer.Instance);

        sealed class ReferenceComparer : IEqualityComparer<string> {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public bool Equals(string a, string b) => ReferenceEquals(a, b);
            public int GetHashCode(string s) => RuntimeHelpers.GetHashCode(s);
        }

        public SymbolTable() {
            // Reserve slot 0 -> "" so Get(0) is safe and Intern("") returns 0.
            strings.Add("");
            ids[""] = 0;
            AddToBucket("", 0);
        }

        public int Count => strings.Count;

        public int Intern(string s) {
            if (s == null) return 0;
            if (byRef.TryGetValue(s, out var fast)) return fast;
            if (ids.TryGetValue(s, out var id)) {
                if (byRef.Count < RefFastPathCap) byRef[s] = id;
                return id;
            }
            id = strings.Count;
            strings.Add(s);
            ids[s] = id;
            AddToBucket(s, id);
            if (byRef.Count < RefFastPathCap) byRef[s] = id;
            return id;
        }

        public int Intern(string source, int start, int length) {
            if (source == null || length <= 0) return 0;
            if (start == 0 && length == source.Length) return Intern(source);
            int hash = HashChars(source, start, length);
            if (hashBuckets.TryGetValue(hash, out var bucket)) {
                for (int b = 0; b < bucket.Count; b++) {
                    var existing = strings[bucket[b]];
                    if (existing.Length != length) continue;
                    bool same = true;
                    for (int j = 0; j < length; j++) {
                        if (existing[j] != source[start + j]) { same = false; break; }
                    }
                    if (same) return bucket[b];
                }
            }
            return Intern(source.Substring(start, length));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string Get(int id) {
            if ((uint)id >= (uint)strings.Count) return null;
            return strings[id];
        }

        public bool TryGet(string s, out int id) {
            if (s == null) { id = 0; return true; }
            return ids.TryGetValue(s, out id);
        }

        void AddToBucket(string s, int id) {
            int hash = HashChars(s, 0, s.Length);
            if (!hashBuckets.TryGetValue(hash, out var bucket)) {
                bucket = new List<int>(1);
                hashBuckets[hash] = bucket;
            }
            bucket.Add(id);
        }

        // FNV-1a over the raw chars. NOT string.GetHashCode: that can't be
        // computed over a (start,length) window without allocating, and its
        // value differs per runtime/process (randomized hashing).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int HashChars(string s, int start, int length) {
            unchecked {
                int h = (int)2166136261;
                int end = start + length;
                for (int i = start; i < end; i++) {
                    h = (h ^ s[i]) * 16777619;
                }
                return h;
            }
        }
    }
}
