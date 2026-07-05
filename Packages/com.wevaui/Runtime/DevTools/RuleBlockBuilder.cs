using System.Collections.Generic;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Selectors;
using Weva.Dom;

namespace Weva.DevTools {
    // Chrome DevTools "Styles" pane — groups cascade matches into ordered rule blocks.
    //
    // Given the output of CascadeEngine.CollectMatchesFor(element), RuleBlockBuilder
    // produces a list of RuleBlock objects ordered winner-first (highest cascade
    // priority block first), each containing its declarations with IsOverridden set
    // correctly — just like Chrome DevTools "Styles" pane.
    //
    // Ordering follows the same axes as CascadeDeclarationComparer (reused here
    // via StyleInspector.CascadeDeclarationComparer.Instance) so the result is
    // stable and consistent with the existing cascade inspector.
    //
    // No Unity APIs — headless-testable.
    public static class RuleBlockBuilder {
        // Build rule blocks for the given element. Requires a CascadeEngine.
        // Returns an empty list when element or cascade is null.
        public static List<RuleBlock> Build(Element element, CascadeEngine cascade,
                                            IElementStateProvider stateProvider = null) {
            var result = new List<RuleBlock>();
            if (element == null || cascade == null) return result;

            var matches = cascade.CollectMatchesFor(element, stateProvider);
            if (matches.Count == 0) return result;

            // Sort ascending (lowest priority first) — same as StyleInspector.
            matches.Sort(CascadeDeclarationComparer.Instance);

            // Determine the cascade winner per property (last entry per property
            // after ascending sort = highest priority).
            var winnerForProp = new Dictionary<string, int>(matches.Count); // prop -> match index
            for (int i = 0; i < matches.Count; i++) {
                var prop = matches[i].Declaration?.Property;
                if (prop != null) winnerForProp[prop] = i;
            }

            // Group matches into rule-identity buckets. A "rule identity" is
            // (SelectorText, Origin, SourceIndex) — all declarations from the same
            // authored rule share the same identity and land in the same block.
            // Inline styles form their own block with SelectorText == null.
            // We preserve declaration order within each block via InRuleIndex.
            var blockMap = new Dictionary<RuleKey, List<int>>(matches.Count);
            var keyOrder = new List<RuleKey>(); // insertion order for later sorting

            for (int i = 0; i < matches.Count; i++) {
                var m = matches[i];
                var key = new RuleKey(m.SelectorText, m.Origin, m.SourceIndex, m.IsInline,
                                      m.LayerOrdinal, m.Specificity);
                if (!blockMap.TryGetValue(key, out var list)) {
                    list = new List<int>(4);
                    blockMap[key] = list;
                    keyOrder.Add(key);
                }
                list.Add(i);
            }

            // Sort keys descending by cascade priority (highest-priority block first).
            // We compare two representative indices — any index from each bucket works
            // because all declarations in one rule share the same origin/layer/specificity/source.
            // We take the MAX index in each bucket as the representative (max = highest-priority
            // declaration in the bucket after ascending sort).
            keyOrder.Sort((a, b) => {
                int repA = GetMaxIndex(blockMap[a]);
                int repB = GetMaxIndex(blockMap[b]);
                // Descending: negate the ascending comparator result.
                return -CascadeDeclarationComparer.Instance.Compare(matches[repA], matches[repB]);
            });

            // Inline style block goes first (per Chrome spec). Separate it out.
            // It will already sort first after the descending sort since inline beats
            // any selector in the same origin, but we guarantee position explicitly.
            // (The sort above handles this correctly; we don't need extra logic.)

            foreach (var key in keyOrder) {
                var indices = blockMap[key];
                // Sort declarations within the block by InRuleIndex (source order).
                indices.Sort((ia, ib) => matches[ia].InRuleIndex.CompareTo(matches[ib].InRuleIndex));

                var decls = new List<RuleDeclaration>(indices.Count);
                foreach (var idx in indices) {
                    var m = matches[idx];
                    string prop = m.Declaration?.Property;
                    bool isWinner = prop != null && winnerForProp.TryGetValue(prop, out var winIdx) && winIdx == idx;
                    bool isOverridden = !isWinner;

                    decls.Add(new RuleDeclaration(
                        prop ?? "",
                        m.Declaration?.ValueText ?? "",
                        m.Declaration?.Important ?? false,
                        isOverridden));
                }

                string originLabel = OriginLabel(key.Origin);
                string selectorText = key.IsInline ? "element.style" : (key.SelectorText ?? "");
                result.Add(new RuleBlock(selectorText, key.IsInline, originLabel, key.Origin, decls));
            }

            return result;
        }

        static int GetMaxIndex(List<int> indices) {
            int max = indices[0];
            for (int i = 1; i < indices.Count; i++) {
                if (indices[i] > max) max = indices[i];
            }
            return max;
        }

        static string OriginLabel(DeclarationOrigin origin) {
            switch (origin) {
                case DeclarationOrigin.UserAgent: return "user agent";
                case DeclarationOrigin.User:      return "user";
                default:                          return "author";
            }
        }

        // Rule-identity key. Inline styles have IsInline=true; their SelectorText is null.
        readonly struct RuleKey : System.IEquatable<RuleKey> {
            public readonly string SelectorText;
            public readonly DeclarationOrigin Origin;
            public readonly int SourceIndex;
            public readonly bool IsInline;
            public readonly int LayerOrdinal;
            public readonly Css.Selectors.Specificity Specificity;

            public RuleKey(string selectorText, DeclarationOrigin origin, int sourceIndex,
                           bool isInline, int layerOrdinal,
                           Css.Selectors.Specificity specificity) {
                SelectorText = selectorText;
                Origin = origin;
                SourceIndex = sourceIndex;
                IsInline = isInline;
                LayerOrdinal = layerOrdinal;
                Specificity = specificity;
            }

            public bool Equals(RuleKey other) {
                return IsInline == other.IsInline
                    && Origin == other.Origin
                    && SourceIndex == other.SourceIndex
                    && SelectorText == other.SelectorText;
            }

            public override bool Equals(object obj) => obj is RuleKey k && Equals(k);

            public override int GetHashCode() {
                unchecked {
                    int h = IsInline.GetHashCode();
                    h = (h * 397) ^ ((int)Origin);
                    h = (h * 397) ^ SourceIndex;
                    h = (h * 397) ^ (SelectorText != null ? SelectorText.GetHashCode() : 0);
                    return h;
                }
            }
        }
    }

    // A Chrome-style "rule block" in the Styles pane.
    public sealed class RuleBlock {
        // The selector as authored (e.g. ".card .title") or "element.style" for inline.
        public readonly string SelectorText;

        // True when this block represents the element's inline style attribute.
        public readonly bool IsInlineStyle;

        // Human-readable origin label: "author", "user agent", or "user".
        public readonly string OriginLabel;

        // The cascade origin enum value.
        public readonly DeclarationOrigin Origin;

        // Declarations in this rule, in source order within the rule.
        public readonly IReadOnlyList<RuleDeclaration> Declarations;

        internal RuleBlock(string selectorText, bool isInlineStyle, string originLabel,
                           DeclarationOrigin origin, List<RuleDeclaration> declarations) {
            SelectorText = selectorText;
            IsInlineStyle = isInlineStyle;
            OriginLabel = originLabel;
            Origin = origin;
            Declarations = declarations;
        }
    }

    // One CSS declaration within a RuleBlock.
    public sealed class RuleDeclaration {
        public readonly string Property;
        public readonly string ValueText;
        public readonly bool Important;

        // True when another higher-priority declaration wins this property.
        // Chrome renders overridden declarations with strikethrough.
        public readonly bool IsOverridden;

        internal RuleDeclaration(string property, string valueText, bool important, bool isOverridden) {
            Property = property;
            ValueText = valueText;
            Important = important;
            IsOverridden = isOverridden;
        }
    }
}
