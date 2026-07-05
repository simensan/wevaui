using System;
using System.Collections.Generic;
using System.Globalization;
using Weva.Animation;
using Weva.Css.Values;

namespace Weva.Css.Animation {
    public sealed class KeyframesResolver {
        readonly Dictionary<string, KeyframeAnimation> cache = new();

        public KeyframesResolver(IEnumerable<Stylesheet> stylesheets) {
            if (stylesheets == null) return;
            foreach (var sheet in stylesheets) {
                if (sheet == null) continue;
                IngestSheet(sheet);
            }
        }

        public KeyframeAnimation ResolveByName(string name) {
            if (string.IsNullOrEmpty(name)) return null;
            return cache.TryGetValue(name, out var anim) ? anim : null;
        }

        public bool Contains(string name) => name != null && cache.ContainsKey(name);

        public int Count => cache.Count;

        void IngestSheet(Stylesheet sheet) {
            if (sheet.Rules == null) return;
            foreach (var rule in sheet.Rules) {
                Ingest(rule);
            }
        }

        void Ingest(Rule rule) {
            if (rule is KeyframesRule kr) {
                var anim = ToAnimation(kr);
                if (anim != null) cache[kr.Name] = anim;
                return;
            }
            // @keyframes declared inside any container at-rule needs to be
            // discoverable by name. Mirror NestingExpander's descent across
            // all rule types that hold a Rules list — @media / @layer /
            // @scope / @supports / @container. Without the @supports and
            // @container arms, authors who organized animations inside
            // `@supports (animation: x) { @keyframes spin { ... } }` got
            // a silent `animation-name: spin` no-op.
            if (rule is MediaRule mr) {
                foreach (var inner in mr.Rules) Ingest(inner);
            } else if (rule is LayerRule lr) {
                foreach (var inner in lr.Rules) Ingest(inner);
            } else if (rule is ScopeRule sr) {
                foreach (var inner in sr.Rules) Ingest(inner);
            } else if (rule is SupportsRule supp) {
                foreach (var inner in supp.Rules) Ingest(inner);
            } else if (rule is ContainerRule cr) {
                foreach (var inner in cr.Rules) Ingest(inner);
            }
        }

        static KeyframeAnimation ToAnimation(KeyframesRule kr) {
            if (kr == null || string.IsNullOrEmpty(kr.Name)) return null;
            var frames = new List<Keyframe>();
            foreach (var block in kr.Blocks) {
                foreach (double pos in ParseSelectorPositions(block.Selector)) {
                    var props = new Dictionary<string, string>();
                    foreach (var d in block.Declarations) {
                        if (d == null || string.IsNullOrEmpty(d.Property)) continue;
                        props[d.Property] = d.ValueText ?? "";
                    }
                    frames.Add(new Keyframe(pos, props));
                }
            }
            return new KeyframeAnimation(kr.Name, frames);
        }

        static IEnumerable<double> ParseSelectorPositions(string selector) {
            if (string.IsNullOrWhiteSpace(selector)) yield break;
            var parts = selector.Split(',');
            foreach (var raw in parts) {
                string s = CssStringUtil.ToLowerInvariantOrSame(raw.Trim());
                if (s == "from") { yield return 0; continue; }
                if (s == "to") { yield return 1; continue; }
                if (s.EndsWith("%") &&
                    double.TryParse(s.AsSpan(0, s.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double pct)) {
                    double p = pct / 100.0;
                    // CSS Animations L1 §3: "A keyframe selector that is
                    // less than 0% or higher than 100% is invalid and is
                    // ignored." Clamping (instead of ignoring) pinned
                    // author-mistyped out-of-range frames onto the 0%/100%
                    // endpoints, defeating the implicit endpoint synthesis
                    // that uses the element's computed style.
                    if (p < 0 || p > 1) continue;
                    yield return p;
                }
            }
        }
    }
}
