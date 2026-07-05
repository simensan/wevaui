using System.Collections.Generic;
using Weva.Layout.Boxes;

namespace Weva.Layout.AnchorPositioning {
    // AnchorRegistry — maps `anchor-name` strings to the box that declared them.
    //
    // Real CSS scopes anchor names to the nearest containing block of the
    // declaration; v1 (per the brief) uses document-level scope for simplicity:
    // a re-declaration replaces the prior entry. The registry is rebuilt at the
    // start of each PositioningPass since boxes' positions are not stable
    // across layouts and the lookup needs the current frame's coordinates.
    //
    // The name string keeps the leading double-dash form (e.g. `--tooltip`)
    // for spec faithfulness; lookups normalise via TrimStart('-').
    public sealed class AnchorRegistry {
        static AnchorRegistry() {
            // Ensure `anchor-name` and `position-anchor` are registered with
            // the cascade the moment any layout context constructs the
            // first registry. CssProperties doesn't know about anchor
            // positioning by default; registering here keeps the feature
            // fully additive (no edits to the central CssProperties switch).
            AnchorPositioningProperties.EnsureRegistered();
        }

        readonly Dictionary<string, AnchorEntry> entries = new();

        public int Count => entries.Count;

        public void Register(string name, Box box) {
            if (string.IsNullOrEmpty(name) || box == null) return;
            string key = Normalise(name);
            entries[key] = new AnchorEntry(name, box);
        }

        public bool TryResolve(string name, out AnchorEntry entry) {
            if (string.IsNullOrEmpty(name)) {
                entry = default;
                return false;
            }
            string key = Normalise(name);
            return entries.TryGetValue(key, out entry);
        }

        public void Clear() => entries.Clear();

        public IReadOnlyDictionary<string, AnchorEntry> All => entries;

        static string Normalise(string raw) {
            if (raw == null) return "";
            string s = raw.Trim();
            // Strip the leading `--` custom-property prefix exactly ONCE so
            // "--tip" and "tip" match. Previously this looped while `s[0]
            // == '-'`, which also collapsed `---foo`, `-foo`, and `foo`
            // into the same key — two anchors declared with subtly
            // different names silently overwrote each other.
            if (s.Length >= 2 && s[0] == '-' && s[1] == '-') s = s.Substring(2);
            return s;
        }
    }

    public readonly struct AnchorEntry {
        public string Name { get; }
        public Box Anchor { get; }

        public AnchorEntry(string name, Box anchor) {
            Name = name;
            Anchor = anchor;
        }
    }
}
