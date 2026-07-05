using System;
using Weva.Css;

namespace Weva.Components.Scoping {
    public static class StylesheetScoper {
        public static ScopedStylesheet Scope(Stylesheet input, string scopeId) {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (string.IsNullOrEmpty(scopeId)) throw new ArgumentException("scopeId required", nameof(scopeId));

            var rewritten = new Stylesheet();
            for (int i = 0; i < input.Rules.Count; i++) {
                var ruleOut = ScopeRule(input.Rules[i], scopeId);
                if (ruleOut != null) rewritten.Rules.Add(ruleOut);
            }
            return new ScopedStylesheet(ScopeId.From(scopeId), input, rewritten);
        }

        public static ScopedStylesheet Scope(Stylesheet input, ScopeId scopeId) {
            if (scopeId.IsEmpty) throw new ArgumentException("scopeId is empty", nameof(scopeId));
            return Scope(input, scopeId.Value);
        }

        static Rule ScopeRule(Rule rule, string scopeId) {
            switch (rule) {
                case StyleRule sr: return ScopeStyleRule(sr, scopeId);
                case MediaRule mr: return ScopeMediaRule(mr, scopeId);
                case LayerRule lr: return ScopeLayerRule(lr, scopeId);
                case ScopeRule scr: return ScopeScopeRule(scr, scopeId);
                case SupportsRule supp: return ScopeSupportsRule(supp, scopeId);
                case ContainerRule cr: return ScopeContainerRule(cr, scopeId);
                case KeyframesRule kr: return kr;
                case ImportRule ir: return ir;
                default: return rule;
            }
        }

        static StyleRule ScopeStyleRule(StyleRule sr, string scopeId) {
            var result = new StyleRule();
            result.Declarations = sr.Declarations;
            for (int i = 0; i < sr.Selectors.Count; i++) {
                var rewrittenList = SelectorScoper.Scope(sr.Selectors[i], scopeId);
                for (int j = 0; j < rewrittenList.Count; j++) {
                    result.Selectors.Add(rewrittenList[j]);
                }
            }
            return result;
        }

        static MediaRule ScopeMediaRule(MediaRule mr, string scopeId) {
            var result = new MediaRule();
            result.ConditionText = mr.ConditionText;
            for (int i = 0; i < mr.Rules.Count; i++) {
                var inner = ScopeRule(mr.Rules[i], scopeId);
                if (inner != null) result.Rules.Add(inner);
            }
            return result;
        }

        // Descend through any container at-rule that wraps StyleRules so
        // the inner selectors get scope-rewritten. Without these, authors
        // who organized component styles inside `@supports`, `@layer`,
        // `@scope`, or `@container` got un-scoped selectors that leaked
        // outside the component boundary.
        static LayerRule ScopeLayerRule(LayerRule lr, string scopeId) {
            var result = new LayerRule { IsBlock = lr.IsBlock, Names = lr.Names };
            for (int i = 0; i < lr.Rules.Count; i++) {
                var inner = ScopeRule(lr.Rules[i], scopeId);
                if (inner != null) result.Rules.Add(inner);
            }
            return result;
        }

        static ScopeRule ScopeScopeRule(ScopeRule scr, string scopeId) {
            var result = new ScopeRule {
                ScopeStartSelectors = scr.ScopeStartSelectors,
                ScopeEndSelectors = scr.ScopeEndSelectors,
            };
            for (int i = 0; i < scr.Rules.Count; i++) {
                var inner = ScopeRule(scr.Rules[i], scopeId);
                if (inner != null) result.Rules.Add(inner);
            }
            return result;
        }

        static SupportsRule ScopeSupportsRule(SupportsRule supp, string scopeId) {
            var result = new SupportsRule { ConditionText = supp.ConditionText };
            for (int i = 0; i < supp.Rules.Count; i++) {
                var inner = ScopeRule(supp.Rules[i], scopeId);
                if (inner != null) result.Rules.Add(inner);
            }
            return result;
        }

        static ContainerRule ScopeContainerRule(ContainerRule cr, string scopeId) {
            var result = new ContainerRule { Name = cr.Name, ConditionText = cr.ConditionText };
            for (int i = 0; i < cr.Rules.Count; i++) {
                var inner = ScopeRule(cr.Rules[i], scopeId);
                if (inner != null) result.Rules.Add(inner);
            }
            return result;
        }
    }
}
