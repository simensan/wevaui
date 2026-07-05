using System.Collections.Generic;
using System.Text;

namespace Weva.Css {
    // CSS Nesting Module — flattens StyleRule.NestedRules into the parent
    // stylesheet by combining each child selector with the parent's selector
    // list using `&` as the parent reference.
    //
    // v1 behavior (matches the current spec, post-CSSWG resolution):
    //   - `&` is optional. A bare ident at top of a nested block is treated
    //     as a descendant selector (`.outer { .inner { ... } }`
    //     -> `.outer .inner { ... }`).
    //   - When `&` appears inside a nested selector, it is replaced with each
    //     parent selector (the parent selector list joined by `,` is
    //     wrapped with `:is(...)` if there are multiple alternatives, except
    //     when a single one is present — see CSSNESTING-DRAFT spec).
    //   - Nested @media/@container at-rules wrap each descendant rule's
    //     existing prelude with their own.
    //   - Unlimited nesting depth.
    //
    // Limitations:
    //   - We do NOT compute the spec's full :is()-wrapping with specificity
    //     adjustments; we emit literal joined selectors with comma expansion.
    //     This keeps specificity simple at the cost of a minor spec
    //     deviation that's invisible for typical AI-authored stylesheets.
    public static class NestingExpander {
        // PH2: expansion budget. CombineSelectors is parents × children per
        // nesting level, so a 114-CHARACTER sheet (20 levels of `&,&{`)
        // expanded to 1,048,576 selectors and 30 levels OOM'd the process.
        // Chrome caps nesting products too. The per-sheet count budget stops
        // the cross-product; the length cap stops the `&&` variant (selector
        // STRING doubling per level). Both drop the overflow with a warn-once
        // diagnostic — same degradation contract as EC11 rule drops. Main-
        // thread statics, reset per Expand (parsing is main-thread only).
        const int MaxExpandedSelectorsPerSheet = 65536;
        const int MaxExpandedSelectorLength = 8192;
        static int s_Remaining;
        static bool s_Warned;

        static void WarnBudgetOnce(string what) {
            if (s_Warned) return;
            s_Warned = true;
            Weva.Diagnostics.UICssDiagnostics.Warn(
                "NestingExpander",
                "nesting expansion exceeded the " + what + " budget — overflow selectors dropped");
        }

        public static void Expand(Stylesheet sheet) {
            if (sheet == null || sheet.Rules == null) return;
            s_Remaining = MaxExpandedSelectorsPerSheet;
            s_Warned = false;
            var output = new List<Rule>();
            foreach (var rule in sheet.Rules) {
                ExpandRule(rule, null, output);
            }
            sheet.Rules.Clear();
            sheet.Rules.AddRange(output);
        }

        static void ExpandRule(Rule rule, List<string> parentSelectors, List<Rule> output) {
            switch (rule) {
                case StyleRule sr:
                    ExpandStyleRule(sr, parentSelectors, output);
                    break;
                case MediaRule mr:
                    ExpandMediaRule(mr, parentSelectors, output);
                    break;
                case SupportsRule sup:
                    ExpandSupportsRule(sup, parentSelectors, output);
                    break;
                case ContainerRule cr:
                    ExpandContainerRule(cr, parentSelectors, output);
                    break;
                case LayerRule lr:
                    ExpandLayerRule(lr, parentSelectors, output);
                    break;
                case ScopeRule sc:
                    ExpandScopeRule(sc, parentSelectors, output);
                    break;
                default:
                    if (parentSelectors == null) output.Add(rule);
                    break;
            }
        }

        static void ExpandStyleRule(StyleRule sr, List<string> parentSelectors, List<Rule> output) {
            var combined = CombineSelectors(parentSelectors, sr.Selectors);
            bool hasNested = sr.NestedRules != null && sr.NestedRules.Count > 0;
            // Always emit the rule when it carries declarations OR when it has
            // no nested children. The zero-declaration + no-nested case (an
            // empty `a { }` block) still needs to round-trip as a StyleRule so
            // callers that index into Stylesheet.Rules don't lose it.
            if (sr.Declarations.Count > 0 || !hasNested) {
                var emitted = new StyleRule {
                    Selectors = combined,
                    Declarations = sr.Declarations,
                };
                output.Add(emitted);
            }
            if (hasNested) {
                foreach (var inner in sr.NestedRules) {
                    ExpandRule(inner, combined, output);
                }
            }
        }

        static void ExpandMediaRule(MediaRule mr, List<string> parentSelectors, List<Rule> output) {
            var subOutput = new List<Rule>();
            foreach (var inner in mr.Rules) {
                ExpandRule(inner, parentSelectors, subOutput);
            }
            if (subOutput.Count == 0) return;
            var wrapper = new MediaRule { ConditionText = mr.ConditionText };
            wrapper.Rules.AddRange(subOutput);
            output.Add(wrapper);
        }

        static void ExpandSupportsRule(SupportsRule sup, List<string> parentSelectors, List<Rule> output) {
            var subOutput = new List<Rule>();
            foreach (var inner in sup.Rules) {
                ExpandRule(inner, parentSelectors, subOutput);
            }
            if (subOutput.Count == 0) return;
            var wrapper = new SupportsRule { ConditionText = sup.ConditionText };
            wrapper.Rules.AddRange(subOutput);
            output.Add(wrapper);
        }

        static void ExpandContainerRule(ContainerRule cr, List<string> parentSelectors, List<Rule> output) {
            var subOutput = new List<Rule>();
            foreach (var inner in cr.Rules) {
                ExpandRule(inner, parentSelectors, subOutput);
            }
            if (subOutput.Count == 0) return;
            var wrapper = new ContainerRule { Name = cr.Name, ConditionText = cr.ConditionText };
            wrapper.Rules.AddRange(subOutput);
            output.Add(wrapper);
        }

        static void ExpandScopeRule(ScopeRule sc, List<string> parentSelectors, List<Rule> output) {
            var subOutput = new List<Rule>();
            foreach (var inner in sc.Rules) {
                ExpandRule(inner, parentSelectors, subOutput);
            }
            if (subOutput.Count == 0) return;
            var wrapper = new ScopeRule {
                ScopeStartSelectors = sc.ScopeStartSelectors,
                ScopeEndSelectors = sc.ScopeEndSelectors,
            };
            wrapper.Rules.AddRange(subOutput);
            output.Add(wrapper);
        }

        static void ExpandLayerRule(LayerRule lr, List<string> parentSelectors, List<Rule> output) {
            // Layer rules within a nested context are unusual — we preserve them
            // verbatim by passing through, which the cascade engine then resolves
            // at compile time.
            var subOutput = new List<Rule>();
            foreach (var inner in lr.Rules) {
                ExpandRule(inner, parentSelectors, subOutput);
            }
            var wrapper = new LayerRule { IsBlock = lr.IsBlock };
            wrapper.Names.AddRange(lr.Names);
            wrapper.Rules.AddRange(subOutput);
            output.Add(wrapper);
        }

        // Combines parent selector list with child selector list per the CSS
        // Nesting spec. For each parent × child pair, replace every standalone
        // `&` in the child with the parent. If the child contains no `&`, it
        // is treated as a descendant of the parent (`parent child`).
        public static List<string> CombineSelectors(List<string> parents, List<string> children) {
            var result = new List<string>();
            if (children == null || children.Count == 0) {
                if (parents != null) result.AddRange(parents);
                return result;
            }
            if (parents == null || parents.Count == 0) {
                // Top-level rule — children are emitted as-is, except `&` at
                // top level matches nothing per spec; we just leave it.
                result.AddRange(children);
                return result;
            }
            for (int p = 0; p < parents.Count; p++) {
                for (int c = 0; c < children.Count; c++) {
                    // PH2: enforce the per-sheet product budget and the
                    // per-selector length cap (see field docs).
                    if (s_Remaining <= 0) {
                        WarnBudgetOnce("selector-count");
                        return result;
                    }
                    var combined = CombineOne(parents[p], children[c]);
                    if (combined.Length > MaxExpandedSelectorLength) {
                        WarnBudgetOnce("selector-length");
                        continue;
                    }
                    s_Remaining--;
                    result.Add(combined);
                }
            }
            return result;
        }

        // Inserts the parent selector at every `&` in the child. If the child
        // contains no `&`, prefix with `parent ` (descendant). Tracks
        // string/paren/bracket nesting so `&` inside `:is(&.x)` is handled too.
        static string CombineOne(string parent, string child) {
            if (string.IsNullOrEmpty(child)) return parent;
            bool hasAmp = ContainsTopLevelAmpersand(child);
            if (!hasAmp) {
                // CSS Nesting (current draft): a bare nested selector that does
                // not start with a combinator implicitly references the parent
                // via descendant relation.
                return parent + " " + child;
            }
            return ReplaceAmpersand(child, parent);
        }

        static bool ContainsTopLevelAmpersand(string s) {
            int parenDepth = 0;
            int bracketDepth = 0;
            char strQuote = '\0';
            for (int i = 0; i < s.Length; i++) {
                char c = s[i];
                if (strQuote != '\0') {
                    if (c == '\\' && i + 1 < s.Length) { i++; continue; }
                    if (c == strQuote) strQuote = '\0';
                    continue;
                }
                if (c == '"' || c == '\'') { strQuote = c; continue; }
                if (c == '(') parenDepth++;
                else if (c == ')' && parenDepth > 0) parenDepth--;
                else if (c == '[') bracketDepth++;
                else if (c == ']' && bracketDepth > 0) bracketDepth--;
                else if (c == '&') return true;
            }
            return false;
        }

        static string ReplaceAmpersand(string child, string parent) {
            var sb = new StringBuilder(child.Length + parent.Length);
            char strQuote = '\0';
            for (int i = 0; i < child.Length; i++) {
                char c = child[i];
                if (strQuote != '\0') {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < child.Length) { sb.Append(child[++i]); continue; }
                    if (c == strQuote) strQuote = '\0';
                    continue;
                }
                if (c == '"' || c == '\'') { strQuote = c; sb.Append(c); continue; }
                if (c == '&') {
                    sb.Append(parent);
                    continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
