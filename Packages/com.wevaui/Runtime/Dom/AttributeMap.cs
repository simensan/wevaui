using System;
using System.Collections;
using System.Collections.Generic;

namespace Weva.Dom {
    public sealed class AttributeMap : IEnumerable<KeyValuePair<string, string>> {
        // HTML attribute names are case-insensitive (HTML §13.1.2.3). Store
        // names in the canonical lowercased form so `attr["ID"] = "foo"`
        // and `attr["id"]` agree, and so selector matching (which calls
        // GetAttribute with the parsed lowercase name) doesn't disagree
        // with the author's casing on the source markup.
        readonly List<string> order = new();
        // Parallel to `order`: the value at each declaration slot, kept in
        // sync by SetValue/Remove so ValueAt(i) needs no dictionary hash.
        readonly List<string> orderValues = new();
        readonly Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        // Raw author/binding source. Bound attribute values are materialized into
        // values, but rescans still need the original `{{ }}` template.
        readonly Dictionary<string, string> sources = new(StringComparer.OrdinalIgnoreCase);

        // Internal change hook so an owning Element can react. Args: (name, oldValue, newValue).
        // oldValue == null means "did not exist before"; newValue == null means "removed".
        internal Action<string, string, string> OnChanged;

        public int Count => order.Count;

        public string this[string name] {
            get {
                if (name == null) return null;
                return values.TryGetValue(name, out var v) ? v : null;
            }
            set {
                SetValue(name, value, false);
            }
        }

        public bool Contains(string name) => name != null && values.ContainsKey(name);

        // Indexed read-only access for hot-path consumers (e.g. DomSnapshot.Refill)
        // that need per-attribute iteration without paying for the allocating
        // IEnumerator<KeyValuePair<...>>. Returns the attribute name at slot
        // i in declaration order.
        public string NameAt(int i) => order[i];

        // O(1) value-by-slot companion to NameAt. The by-name indexer hashes
        // through the OrdinalIgnoreCase comparer - measured at ~550us per
        // 2004-node DomSnapshot.Refill on Unity Mono (PERF-1 bisect); this
        // parallel list removes every per-attribute hash from that walk.
        public string ValueAt(int i) => orderValues[i];

        internal string Source(string name) {
            if (name == null) return null;
            string canonical = Canonicalize(name);
            return sources.TryGetValue(canonical, out var source)
                ? source
                : (values.TryGetValue(canonical, out var value) ? value : null);
        }

        internal void SetValuePreservingSource(string name, string value) {
            SetValue(name, value, true);
        }

        public bool Remove(string name) {
            if (name == null) return false;
            string canonical = Canonicalize(name);
            if (!values.TryGetValue(canonical, out var oldValue)) return false;
            values.Remove(canonical);
            sources.Remove(canonical);
            // List.Remove is case-sensitive — find the canonical entry
            // explicitly. Iteration order matters because consumers iterate
            // `order` for declaration-order preserving emits.
            for (int i = 0; i < order.Count; i++) {
                if (StringComparer.OrdinalIgnoreCase.Equals(order[i], canonical)) {
                    order.RemoveAt(i);
                    orderValues.RemoveAt(i);
                    break;
                }
            }
            OnChanged?.Invoke(canonical, oldValue, null);
            return true;
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() {
            foreach (var name in order) yield return new KeyValuePair<string, string>(name, values[name]);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        void SetValue(string name, string value, bool preserveSource) {
            if (name == null) return;
            string canonical = Canonicalize(name);
            bool existed = values.TryGetValue(canonical, out var oldValue);
            bool sourceExists = sources.ContainsKey(canonical);

            if (existed && oldValue == value) {
                if (!preserveSource || !sourceExists) {
                    sources[canonical] = value;
                }
                return;
            }

            if (!existed) {
                order.Add(canonical);
                orderValues.Add(value);
            } else {
                // Update the slot value in place (same index as `order`).
                for (int i = 0; i < order.Count; i++) {
                    if (StringComparer.OrdinalIgnoreCase.Equals(order[i], canonical)) {
                        orderValues[i] = value;
                        break;
                    }
                }
            }
            values[canonical] = value;
            if (!preserveSource || !sourceExists) {
                sources[canonical] = value;
            }
            OnChanged?.Invoke(canonical, existed ? oldValue : null, value);
        }

        // Lowercase only when the input isn't already lowercase. Avoids a
        // string allocation on the common path where the parser emits
        // canonical lowercase tokens already.
        static string Canonicalize(string s) {
            for (int i = 0; i < s.Length; i++) {
                char c = s[i];
                if (c >= 'A' && c <= 'Z') return s.ToLowerInvariant();
            }
            return s;
        }
    }
}
