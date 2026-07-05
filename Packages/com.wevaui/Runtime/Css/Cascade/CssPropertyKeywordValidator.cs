using System;
using System.Collections.Generic;

namespace Weva.Css.Cascade {
    // CSS Syntax 3 §5 / CSS Cascade L5 §3 — per-property keyword validation.
    //
    // A declaration whose value is syntactically invalid for its property is
    // treated as if not specified: the cascade skips it and falls through to
    // the next-lower-priority matching declaration (or to the initial value
    // when no valid declaration exists).
    //
    // v1 scope: keyword-only (or keyword-list) properties where the registry
    // can enumerate the complete allowed-keyword set. Properties without
    // metadata keep pass-through. The architecture allows more properties to
    // opt in by adding an entry to the keyword table below — no code changes
    // required for additional properties.
    //
    // BYPASS rules (must never be treated as invalid):
    //   1. CSS-wide keywords: initial / inherit / unset / revert / revert-layer
    //      — handled by KeywordResolver; always valid for any property.
    //   2. Values containing var() — validation must be deferred until
    //      computed-value time because the substituted value may be valid.
    //   3. Custom properties (--foo) — any value is valid per CSS Custom
    //      Properties L1 §2.
    //   4. Properties with no keyword entry — validation is not attempted.
    internal static class CssPropertyKeywordValidator {
        // Per-property id keyword sets. Populated at type-init after
        // CssProperties has assigned stable ids. Each set uses
        // StringComparer.OrdinalIgnoreCase so "Multiply" == "multiply".
        static readonly Dictionary<int, HashSet<string>> keywordSets
            = new Dictionary<int, HashSet<string>>();

        // Properties that accept a comma-separated list of the SAME keyword
        // set (e.g. animation-composition: replace, add, accumulate).
        // Validation splits on ',' and validates each token.
        static readonly HashSet<int> commaListProperties = new HashSet<int>();

        static CssPropertyKeywordValidator() {
            Build();
        }

        // Returns true when the declaration value is valid (or not subject to
        // v1 validation), false when it is an invalid keyword that should be
        // discarded.
        //
        // Contract:
        //   - Returns true (pass-through) for custom properties, unvalidated
        //     properties, CSS-wide keywords, and values containing var().
        //   - Returns false only when a known keyword-only property has a
        //     value that is NOT in its allowed set AND is not a CSS-wide
        //     keyword AND contains no var() reference.
        public static bool IsValidValue(int propertyId, string value) {
            if (propertyId < 0) return true; // custom or unknown property
            if (value == null) return true;

            // Fast-path: no keyword entry for this property — pass-through.
            if (!keywordSets.TryGetValue(propertyId, out var allowed)) return true;

            string trimmed = value.Trim();
            if (trimmed.Length == 0) return true;

            // Bypass 1: CSS-wide keywords are always valid.
            if (KeywordResolver.IsCssWideKeyword(trimmed)) return true;

            // Bypass 2: value contains var() or attr() — defer validation to
            // computed-value time. The substituted value may be valid even if
            // the raw text (with the function call intact) isn't in the keyword set.
            // Combined single-pass: only do the substring scans when a '(' exists at all.
            if (ContainsSubstitutionMarker(trimmed)) return true;

            // Comma-list properties: validate each token.
            if (commaListProperties.Contains(propertyId)) {
                return AllTokensValid(trimmed, allowed);
            }

            return allowed.Contains(trimmed);
        }

        // Overload for callers that have the property name but not the id.
        // Used by the cascade engine when propertyId is not cached yet.
        public static bool IsValidValue(string propertyName, string value) {
            if (string.IsNullOrEmpty(propertyName)) return true;
            int id = CssProperties.GetId(propertyName);
            return IsValidValue(id, value);
        }

        // ── Private helpers ───────────────────────────────────────────────

        static bool AllTokensValid(string value, HashSet<string> allowed) {
            // Simple comma-split respecting that there are no nested functions
            // in keyword-only comma lists (animation-composition, blend-modes).
            int start = 0;
            int len = value.Length;
            for (int i = 0; i <= len; i++) {
                if (i == len || value[i] == ',') {
                    int tokenLen = i - start;
                    if (tokenLen > 0) {
                        // Trim ASCII whitespace around the token.
                        int s = start;
                        int e = i;
                        while (s < e && (value[s] == ' ' || value[s] == '\t')) s++;
                        while (e > s && (value[e - 1] == ' ' || value[e - 1] == '\t')) e--;
                        if (e > s) {
                            string token = value.Substring(s, e - s);
                            // CSS-wide keywords are valid in any list position.
                            if (!KeywordResolver.IsCssWideKeyword(token) && !allowed.Contains(token)) {
                                return false;
                            }
                        }
                    }
                    start = i + 1;
                }
            }
            return true;
        }

        // Single-pass substitution-marker check. Returns true when `value`
        // contains var() or attr() — meaning validation must be deferred to
        // computed-value time. The fast-path '(' guard makes the common case
        // (no parens at all) a single cheap ordinal scan with zero substring
        // allocation, avoiding two OrdinalIgnoreCase IndexOf calls per keyword.
        static bool ContainsSubstitutionMarker(string value) {
            // No '(' → neither var( nor attr( can be present.
            if (value.IndexOf('(') < 0) return false;
            return value.IndexOf("var(", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("attr(", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static HashSet<string> Keywords(params string[] kws) {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in kws) set.Add(k);
            return set;
        }

        static void Register(int id, bool isCommaList, HashSet<string> kws) {
            if (id < 0) return;
            keywordSets[id] = kws;
            if (isCommaList) commaListProperties.Add(id);
        }

        static void Build() {
            // ── animation-composition ─────────────────────────────────────
            // CSS Animations L2 §3.6: replace | add | accumulate
            Register(CssProperties.AnimationCompositionId, isCommaList: true,
                Keywords("replace", "add", "accumulate"));

            // ── background-blend-mode ──────────────────────────────────────
            // CSS Compositing L1 §6.3: <blend-mode>#
            // Same set as mix-blend-mode (§6.1) plus the spec lists normal.
            var blendModeKws = BlendModeKeywords();
            Register(CssProperties.BackgroundBlendModeId, isCommaList: true, blendModeKws);

            // ── mix-blend-mode ─────────────────────────────────────────────
            // CSS Compositing L1 §6.1: <blend-mode>  (single value, no comma list)
            Register(CssProperties.MixBlendModeId, isCommaList: false, BlendModeKeywords());

            // ── visibility ─────────────────────────────────────────────────
            // CSS 2.1 §11.2: visible | hidden | collapse
            Register(CssProperties.VisibilityId, isCommaList: false,
                Keywords("visible", "hidden", "collapse"));
        }

        // CSS Compositing and Blending L1 §6 <blend-mode> value set.
        // This set is shared by background-blend-mode and mix-blend-mode.
        // The spec lists: normal | multiply | screen | overlay | darken |
        // lighten | color-dodge | color-burn | hard-light | soft-light |
        // difference | exclusion | hue | saturation | color | luminosity
        // Plus widely-supported extensions: plus-darker | plus-lighter
        static HashSet<string> BlendModeKeywords() {
            return Keywords(
                "normal", "multiply", "screen", "overlay",
                "darken", "lighten", "color-dodge", "color-burn",
                "hard-light", "soft-light", "difference", "exclusion",
                "hue", "saturation", "color", "luminosity",
                "plus-darker", "plus-lighter");
        }
    }
}
