using System.Collections.Generic;
using Weva.Css.Cascade.Shorthands;
using Weva.Css.Selectors;
using Weva.Dom;

namespace Weva.Css.Cascade {
    // Backdrop pseudo-element cascade. Per the CSS Fullscreen / HTML Living
    // Standard model, `::backdrop` is the styling hook for the box rendered
    // beneath an element in the top layer. In v1 the top-layer hosts are an
    // open modal `<dialog>` (`data-modal`) and any open popover element
    // (`[popover][data-popover-open]`). BoxBuilder calls ComputeBackdrop to
    // get the cascaded style for the synthetic backdrop box it injects.
    //
    // Inheritance: CSS Fullscreen specifies that `::backdrop` only inherits
    // from itself; there is no `::backdrop` parent in our v1 model, so every
    // non-set inherited property falls back to its initial value. We bake in
    // a small UA defaults set after author cascade — `position: fixed`,
    // `top/right/bottom/left: 0`, `display: block`, `box-sizing: border-box`
    // — so the backdrop fills the viewport regardless of what the author
    // wrote. Authors can still override `background`, `opacity`, etc. via
    // `::backdrop { ... }`.
    //
    // Caching: not cached on the engine. BoxBuilder rebuilds the box tree
    // each layout pass; the cost of rebuilding a backdrop ComputedStyle is
    // proportional to the number of `::backdrop` rules in the stylesheet,
    // which is typically zero or one. If this becomes a hot path we can key
    // a per-host backdrop cache on (host.Version, mediaContextVersion,
    // stateVersion).
    public sealed partial class CascadeEngine {
        public ComputedStyle ComputeBackdrop(Element host, IElementStateProvider stateProvider = null) {
            if (host == null) return null;
            var state = stateProvider ?? NullStateProvider.Instance;

            scratch.ResetPerElement();
            var matches = scratch.Matches;
            CollectBackdropMatches(host, state, matches);

            ExpandShorthandMatchesInto(matches, scratch.ExpandedMatches, scratch);
            var expanded = scratch.ExpandedMatches;
            expanded.Sort(CompareForCascadeDelegate);

            var style = new ComputedStyle(null, 192);
            var perPropertyWinner = scratch.PerPropertyWinner;
            for (int i = 0; i < expanded.Count; i++) {
                var m = expanded[i];
                // Per-property keyword validation: skip invalid declarations so
                // the cascade falls back to the next-lower-priority match.
                if (!CssPropertyKeywordValidator.IsValidValue(m.Declaration.PropertyId, m.Declaration.ValueText)) {
                    continue;
                }
                perPropertyWinner[m.Declaration.Property] = m;
            }

            // Resolve var()/CSS-wide keywords using the backdrop style itself as
            // the variable lookup scope. Per-self inheritance: there is no parent
            // backdrop, so KeywordResolver receives null and `inherit` collapses
            // to the property's initial value (per CssProperty.InitialValue), and
            // `unset` does likewise for non-inherited properties.
            var rawValues = scratch.RawValues;
            foreach (var kv in perPropertyWinner) {
                rawValues[kv.Key] = kv.Value.Declaration.ValueText;
            }

            // Resolve custom properties first, same shape as ComputeFor but no
            // parent inheritance.
            foreach (var kv in rawValues) {
                if (!CssProperties.IsCustomProperty(kv.Key)) continue;
                string resolved = KeywordResolver.Resolve(kv.Key, kv.Value, null);
                style.Set(kv.Key, resolved);
            }

            var customsResolved = scratch.CustomsResolved;
            foreach (var kv in style.Enumerate()) {
                if (!CssProperties.IsCustomProperty(kv.Key)) continue;
                string resolvedCustom = VariableResolver.Resolve(kv.Value, style);
                resolvedCustom = EnvResolver.Resolve(resolvedCustom);
                resolvedCustom = AttrResolver.Resolve(resolvedCustom, host);
                resolvedCustom = LightDarkResolver.Resolve(resolvedCustom, ResolveEffectiveColorScheme(style, mediaContext));
                customsResolved[kv.Key] = resolvedCustom;
            }
            foreach (var kv in customsResolved) {
                style.Set(kv.Key, kv.Value);
            }

            // Mirror ComputeFor's regular-property resolution chain so a
            // ::backdrop rule like `background: light-dark(...)` or
            // `content: attr(data-label)` actually resolves before being
            // handed to the painter. The prior path only ran var() and
            // KeywordResolver, so light-dark() / attr() bled through as a
            // literal function string and downstream consumers saw an
            // invalid color / unparsable content.
            foreach (var kv in rawValues) {
                if (CssProperties.IsCustomProperty(kv.Key)) continue;
                // CSS Custom Properties L1 §3 — invalid-at-computed-value-time:
                // an unresolvable var() with no fallback drops the entire
                // declaration; FillInherited below falls back to the
                // property's initial value (the backdrop has no parent).
                if (!VariableResolver.TryResolve(kv.Value, style, out string withVars)) {
                    continue;
                }
                // env() resolved at the same phase as var(); unresolvable
                // env() with no fallback ALSO drops the declaration.
                if (!EnvResolver.TryResolve(withVars, out string withEnv)) {
                    continue;
                }
                string withAttr = AttrResolver.Resolve(withEnv, host);
                string withLightDark = LightDarkResolver.Resolve(withAttr, ResolveEffectiveColorScheme(style, mediaContext));
                string resolved = KeywordResolver.Resolve(kv.Key, withLightDark, null);
                style.Set(kv.Key, resolved);
            }

            // Force UA defaults that make the backdrop fill the viewport. These
            // override anything the author cascaded, which is intentional: a
            // backdrop that doesn't cover the viewport is broken — authors who
            // want different geometry should style the dialog or popover itself,
            // not its backdrop. Box-sizing pinned to border-box matches the
            // project default so border declarations behave predictably.
            style.Set("position", "fixed");
            style.Set("top", "0");
            style.Set("right", "0");
            style.Set("bottom", "0");
            style.Set("left", "0");
            style.Set("display", "block");
            style.Set("box-sizing", "border-box");

            // Fill remaining initials. Backdrop only inherits from itself, and
            // there is no parent backdrop in v1 — pass null parent so every
            // unset property gets its CssProperty.InitialValue.
            FillInherited(style, null);

            return style;
        }

        void CollectBackdropMatches(Element host, IElementStateProvider state, List<MatchedDeclaration> matches) {
            for (int ri = 0; ri < backdropRules.Count; ri++) {
                var rule = backdropRules[ri];
                if (rule.Media != null && !rule.Media.Evaluate(mediaContext)) continue;
                if (rule.Container != null && !BackdropContainerMatches(host, rule)) continue;
                Element scopeRoot = null;
                if (rule.Scope != null) {
                    scopeRoot = rule.Scope.FindScopeRoot(host, state);
                    if (scopeRoot == null) continue;
                }
                if (!SelectorMatcher.MatchesPseudoElement(rule.Selector, "backdrop", host, state, scopeRoot)) continue;
                string selectorText = rule.Selector.SourceText;
                int declIndex = 0;
                foreach (var decl in rule.Declarations) {
                    matches.Add(new MatchedDeclaration(decl, rule.Origin, rule.Selector.Specificity, rule.SourceIndex, false, declIndex, rule.LayerOrdinal, selectorText));
                    declIndex++;
                }
            }
        }

        // Uncached container resolution for backdrop rules. The main path's
        // (element, ruleIdx) cache keys would collide with regular rules' indices
        // because both lists number from 0, so backdrop matches go through a
        // direct ContainerResolver call. Backdrop rules are typically zero or one
        // per stylesheet — caching wouldn't pay off anyway.
        bool BackdropContainerMatches(Element host, CompiledRule rule) {
            var ctx = elementToBoxLookup != null
                ? Weva.Css.Container.ContainerResolver.Resolve(host, rule.ContainerName, elementToBoxLookup)
                : Weva.Css.Container.ContainerContext.None;
            return rule.Container.Evaluate(ctx);
        }
    }
}
