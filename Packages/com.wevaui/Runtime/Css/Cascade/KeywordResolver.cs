using System;
using System.Collections.Generic;

namespace Weva.Css.Cascade {
    internal static class KeywordResolver {
        public static bool IsCssWideKeyword(string value) {
            if (value == null) return false;
            return Match(value, "inherit") || Match(value, "initial") || Match(value, "unset") || Match(value, "revert") || Match(value, "revert-layer");
        }

        // CSS Cascade L4 §7.5 — CSS-wide keyword resolution. The `revert` /
        // `revert-layer` rollback resolution requires the full per-element
        // match list and is performed by the caller via PreResolveRollback
        // BEFORE calling this; once rollback has substituted the winning
        // value, only the four "plain" keywords survive into Resolve.
        // If `revert` / `revert-layer` reach Resolve, the rollback found no
        // lower-priority match (no UA/user-origin rule, no lower layer) and
        // the spec collapses them to `initial`.
        public static string Resolve(string property, string value, ComputedStyle parent) {
            if (value == null) return null;
            string trimmed = value.Trim();
            if (Match(trimmed, "inherit")) return ResolveInherit(property, parent);
            if (Match(trimmed, "initial")) return CssProperties.InitialValueOf(property);
            if (Match(trimmed, "unset")) {
                if (CssProperties.IsInherited(property)) return ResolveInherit(property, parent);
                return CssProperties.InitialValueOf(property);
            }
            if (Match(trimmed, "revert")) return CssProperties.InitialValueOf(property);
            if (Match(trimmed, "revert-layer")) return CssProperties.InitialValueOf(property);
            return value;
        }

        // CSS Cascade L5 §7.4 / §7.5 — rollback resolution. Substitutes the
        // value text of the appropriate lower-priority match for the property
        // when the winner's value is `revert` or `revert-layer`. Returns the
        // rolled-back value text (which may itself be `inherit` / `initial` /
        // even another `revert` for chains) so the caller can re-run the
        // standard CSS-wide keyword resolver on the result.
        //
        // Returns null when no rollback target exists — caller leaves the
        // original `revert` / `revert-layer` token in place, and Resolve()
        // above maps it to initial per spec.
        //
        // `expanded` is the full per-element match list in cascade order
        // (last entry for each property is the winner). `winner` is the
        // current winning match for `property`.
        public static string PreResolveRollback(string property, string value,
                List<MatchedDeclaration> expanded, MatchedDeclaration winner) {
            if (value == null) return null;
            // Tight loop on chained `revert` / `revert-layer` (the UA value
            // might itself be `revert`, which collapses to the next-lower
            // origin, etc.). Capped at 4 hops — deeper chains are pathological.
            string current = value;
            MatchedDeclaration currentWinner = winner;
            for (int depth = 0; depth < 4; depth++) {
                if (current == null) return null;
                string trimmed = current.Trim();
                if (Match(trimmed, "revert")) {
                    var rb = RollbackRevert(property, expanded, currentWinner);
                    if (rb.Value == null) return current; // initial fallback applied downstream
                    current = rb.Value;
                    currentWinner = rb.Match;
                    continue;
                }
                if (Match(trimmed, "revert-layer")) {
                    var rb = RollbackRevertLayer(property, expanded, currentWinner);
                    if (rb.Value == null) return current;
                    current = rb.Value;
                    currentWinner = rb.Match;
                    continue;
                }
                return current;
            }
            return current;
        }

        // `revert`: roll back to the latest match at an origin BELOW the
        // current winner's origin (Author > User > UA in normal order). The
        // !important inversion is intentionally NOT honoured in v1 — the
        // pinned tests in CssWideKeywordTests cover normal-priority cases
        // only.
        static (string Value, MatchedDeclaration Match) RollbackRevert(string property,
                List<MatchedDeclaration> expanded, MatchedDeclaration winner) {
            DeclarationOrigin winnerOrigin = winner.Origin;
            // Latest match (i.e. highest cascade priority) at any origin
            // strictly below winnerOrigin. Scan backwards so the first hit
            // is the answer.
            for (int i = expanded.Count - 1; i >= 0; i--) {
                var m = expanded[i];
                if (m.Declaration.Property != property) continue;
                if (m.Origin >= winnerOrigin) continue;
                return (m.Declaration.ValueText, m);
            }
            return (null, default);
        }

        // `revert-layer`: roll back to the latest match at the SAME origin
        // but at a layer with a strictly lower ordinal. The highest such
        // layer's latest match wins; if none, fall through to revert
        // behaviour (drop the origin entirely).
        static (string Value, MatchedDeclaration Match) RollbackRevertLayer(string property,
                List<MatchedDeclaration> expanded, MatchedDeclaration winner) {
            DeclarationOrigin winnerOrigin = winner.Origin;
            int winnerLayer = winner.LayerOrdinal;
            // Pass 1 — find the largest layer ordinal below winnerLayer at winnerOrigin.
            int maxLowerLayer = -1;
            bool found = false;
            for (int i = 0; i < expanded.Count; i++) {
                var m = expanded[i];
                if (m.Declaration.Property != property) continue;
                if (m.Origin != winnerOrigin) continue;
                if (m.LayerOrdinal >= winnerLayer) continue;
                if (!found || m.LayerOrdinal > maxLowerLayer) {
                    maxLowerLayer = m.LayerOrdinal;
                    found = true;
                }
            }
            if (!found) {
                // No lower-layer match at this origin → fall through to revert.
                return RollbackRevert(property, expanded, winner);
            }
            // Pass 2 — latest match (highest cascade priority) at (winnerOrigin, maxLowerLayer).
            MatchedDeclaration best = default;
            string value = null;
            for (int i = 0; i < expanded.Count; i++) {
                var m = expanded[i];
                if (m.Declaration.Property != property) continue;
                if (m.Origin != winnerOrigin) continue;
                if (m.LayerOrdinal != maxLowerLayer) continue;
                best = m;
                value = m.Declaration.ValueText;
            }
            return (value, best);
        }

        static string ResolveInherit(string property, ComputedStyle parent) {
            if (parent != null && parent.TryGet(property, out var pv)) return pv;
            return CssProperties.InitialValueOf(property);
        }

        static bool Match(string a, string b) {
            return a.Equals(b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
