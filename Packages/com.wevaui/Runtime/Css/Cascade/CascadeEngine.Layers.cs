using System.Collections.Generic;
using Weva.Css.Selectors;

namespace Weva.Css.Cascade {
    // Layer-registry partial. Co-located here so sister tasks editing
    // CascadeEngine.cs (DomSnapshot pooling, incremental layout, paint
    // pooling) don't merge-conflict. The main CascadeEngine.cs hooks
    // CompileRuleNested into the layer-aware path via a hook in this
    // file, so the rule-walking code remains in CascadeEngine.cs and the
    // bookkeeping lives here.
    //
    // CSS Cascade Module Level 5 §6.4.2:
    //   @layer A, B, C;            — declares ordering only.
    //   @layer A { rules }         — defines rules in layer A.
    //   @layer { rules }           — anonymous block; takes the next ordinal.
    //   @layer A.B { rules }       — sub-layer; v1 flattens to "A.B".
    //
    // Ordering rules:
    //   * First mention of a name (statement OR block) reserves its ordinal.
    //   * Re-using a name (e.g. statement declared an ordering, then a block
    //     adds rules) does NOT change the existing ordinal.
    //   * Anonymous blocks always allocate a fresh ordinal at parse time.
    //   * Unlayered rules outrank every layered rule (CssLayer.UnlayeredOrdinal).
    public sealed partial class CascadeEngine {
        readonly Dictionary<string, int> layerOrdinalByName = new();
        // Anonymous + named ordinals are drawn from the same monotone counter
        // so the chronological declaration order is the same as the ordinal
        // order, which matches the spec's "later layers win" rule.
        int nextLayerOrdinal;
        // Walker-local stack frame for nested @layer blocks. CompileRuleNested
        // save-restores this around the LayerRule case so an inner block's
        // effective name is parent dot-joined with the child.
        string currentLayerName;

        // Internal hook the parser-walking code calls when it encounters a
        // LayerRule. Returns the ordinal assigned to (or already held by) the
        // named layer; -1 for the anonymous case meaning "allocate fresh".
        internal int RegisterLayer(string name) {
            if (string.IsNullOrEmpty(name)) {
                int ordinal = nextLayerOrdinal++;
                return ordinal;
            }
            if (layerOrdinalByName.TryGetValue(name, out var existing)) return existing;
            int fresh = nextLayerOrdinal++;
            layerOrdinalByName[name] = fresh;
            return fresh;
        }

        // Pre-registers an ordered list of names from a statement-form rule
        // (`@layer base, components, utilities;`). Any name not yet seen
        // allocates an ordinal; already-seen names keep their existing ordinal
        // so re-declaration is idempotent.
        internal void RegisterLayerOrdering(IReadOnlyList<string> names) {
            if (names == null) return;
            for (int i = 0; i < names.Count; i++) {
                var n = names[i];
                if (string.IsNullOrEmpty(n)) continue;
                if (!layerOrdinalByName.ContainsKey(n)) {
                    layerOrdinalByName[n] = nextLayerOrdinal++;
                }
            }
        }

        public bool TryGetLayerOrdinal(string name, out int ordinal) {
            if (string.IsNullOrEmpty(name)) {
                ordinal = CssLayer.UnlayeredOrdinal;
                return false;
            }
            return layerOrdinalByName.TryGetValue(name, out ordinal);
        }

        public IReadOnlyDictionary<string, int> LayerOrdinals => layerOrdinalByName;

        // True iff any compiled selector contains `:has()`. Computed lazily on
        // first access. The InvalidationTracker reads this via
        // HasAnyHasSelector below so DOM-tree mutations propagate ancestor
        // Style invalidation only when the sheet actually has a `:has()` rule.
        bool? hasAnyHasCached;
        public bool HasAnyHasSelector {
            get {
                if (hasAnyHasCached.HasValue) return hasAnyHasCached.Value;
                bool any = false;
                for (int i = 0; i < compiledSelectors.Count; i++) {
                    if (HasDetector.Contains(compiledSelectors[i])) { any = true; break; }
                }
                hasAnyHasCached = any;
                return any;
            }
        }
    }
}
