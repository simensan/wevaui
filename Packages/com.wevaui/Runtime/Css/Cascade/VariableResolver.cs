using System.Collections.Generic;
using System.Text;

namespace Weva.Css.Cascade {
    internal static class VariableResolver {
        const int MaxDepth = 32;

        // CSS Custom Properties L1 §3 — "guaranteed-invalid value" sentinel. A
        // private reference-equal singleton that ResolveInternal returns when a
        // var() reference cannot be resolved AND has no usable fallback. The
        // top-level entry points detect this sentinel and surface the
        // "invalid-at-computed-value-time" signal to callers via either
        //   - Resolve(...)  → returns "" (legacy contract; preserves existing
        //                     custom-property storage semantics where unresolved
        //                     var()'s become empty per the cycle behaviour
        //                     already encoded for §3.1).
        //   - TryResolve(...) → returns false so the cascade can drop the
        //                       declaration and fall back to the property's
        //                       initial (or inherited) value per §3.
        // The sentinel is NEVER returned to external callers.
        internal static readonly string InvalidValue = "__var_invalid__";

        // Legacy entry point — kept for callers that don't care to distinguish
        // invalid-at-computed-value-time from a resolved value. An unresolvable
        // var() collapses to the empty string here. Use TryResolve when
        // distinguishing matters (non-custom property cascade resolution).
        public static string Resolve(string value, ComputedStyle style) {
            if (value == null) return null;
            if (value.IndexOf("var(", System.StringComparison.OrdinalIgnoreCase) < 0) return value;
            var seen = new HashSet<string>();
            var cycleMembers = new HashSet<string>();
            string result = ResolveInternal(value, style, seen, cycleMembers, 0);
            return ReferenceEquals(result, InvalidValue) ? "" : result;
        }

        // CSS Custom Properties L1 §3: "If a property contains one or more
        // var() functions, and those functions are syntactically valid, the
        // entire property's grammar must be assumed to be valid at parse
        // time. ... At computed-value time, if any var() references are
        // invalid (or contain other invalid var() references), the property
        // containing them is invalid at computed-value time."
        //
        // Returns:
        //   - true with the substituted value when every var() reference
        //     resolved (either to its custom-property value or to a fallback
        //     that itself resolved).
        //   - false (with `resolved == null`) when the declaration becomes
        //     invalid-at-computed-value-time and the cascade must drop it so
        //     the property reverts to its initial (or inherited) value.
        // `value == null` → returns true with `resolved = null` (no var() to
        // resolve; behaves like Resolve).
        // `value` with no `var(` substring → returns true verbatim.
        public static bool TryResolve(string value, ComputedStyle style, out string resolved) {
            if (value == null) { resolved = null; return true; }
            if (value.IndexOf("var(", System.StringComparison.OrdinalIgnoreCase) < 0) {
                resolved = value;
                return true;
            }
            var seen = new HashSet<string>();
            var cycleMembers = new HashSet<string>();
            string result = ResolveInternal(value, style, seen, cycleMembers, 0);
            if (ReferenceEquals(result, InvalidValue)) {
                resolved = null;
                return false;
            }
            resolved = result;
            return true;
        }

        static string ResolveInternal(string value, ComputedStyle style, HashSet<string> seen, HashSet<string> cycleMembers, int depth) {
            if (value == null) return null;
            if (depth > MaxDepth) return InvalidValue;
            if (value.IndexOf("var(", System.StringComparison.OrdinalIgnoreCase) < 0) return value;

            var sb = new StringBuilder(value.Length);
            int i = 0;
            while (i < value.Length) {
                if (StartsWithCi(value, i, "var(")) {
                    int parenStart = i + 3;
                    int end = FindMatchingParen(value, parenStart);
                    if (end < 0) {
                        sb.Append(value, i, value.Length - i);
                        break;
                    }
                    string inside = value.Substring(parenStart + 1, end - parenStart - 1);
                    string replacement = ResolveVarCall(inside, style, seen, cycleMembers, depth);
                    // Propagate the invalid sentinel up the chain: a var() call
                    // anywhere in the value taints the whole declaration per
                    // CSS Custom Properties L1 §3.
                    if (ReferenceEquals(replacement, InvalidValue)) return InvalidValue;
                    sb.Append(replacement);
                    i = end + 1;
                    continue;
                }
                sb.Append(value[i]);
                i++;
            }
            return sb.ToString();
        }

        static string ResolveVarCall(string inside, ComputedStyle style, HashSet<string> seen, HashSet<string> cycleMembers, int depth) {
            SplitVarArgs(inside, out string name, out string fallback);
            name = name.Trim();
            if (name.Length == 0) return InvalidValue;
            if (!name.StartsWith("--")) {
                // Malformed first argument (not a custom-property name). Per
                // spec the var() is invalid; honour the fallback if present,
                // otherwise propagate invalid up.
                if (fallback == null) return InvalidValue;
                string fb = ResolveInternal(fallback, style, seen, cycleMembers, depth + 1);
                if (ReferenceEquals(fb, InvalidValue)) return InvalidValue;
                return fb.Trim();
            }

            // CSS Custom Properties L1 §3.1 — once we've identified `name` as
            // a member of a cycle in THIS resolution pass, every subsequent
            // var() reference to it must be invalid regardless of the
            // reference's own fallback. The fallback would otherwise "rescue"
            // the cycle member through an open stack frame (A16). Outer
            // consumers (not in the cycle) still see invalid and may apply
            // their OWN consumer-side fallback per §3 — that path runs
            // through the customValue.Length > 0 check below since cycle
            // members store as the empty string after this pass completes.
            if (cycleMembers.Contains(name)) {
                return InvalidValue;
            }

            if (seen.Contains(name)) {
                // CSS Custom Properties L1 §3.1: "If there is a cycle in the
                // dependency graph, all the custom properties in the cycle
                // must compute to their initial value (the guaranteed-invalid
                // value)." When we hit a name already on the stack, we've
                // closed a cycle — every name currently on the stack PLUS
                // this name is a cycle member. Recording them here lets the
                // unwinding frames detect their own cycle membership (via the
                // check above + the post-substitute check below) and refuse
                // to apply their own fallback.
                foreach (var n in seen) cycleMembers.Add(n);
                cycleMembers.Add(name);
                return InvalidValue;
            }

            // Mark `name` as seen for the ENTIRE call (both the substitution
            // recurse and the fallback recurse). Previously only the
            // substitution branch added to `seen`, so a fallback that
            // re-references the same custom property by name didn't
            // short-circuit and walked all the way to MaxDepth=32. Now any
            // recursion that re-enters this name (via the substituted
            // value OR via its own fallback) terminates immediately.
            seen.Add(name);
            try {
                if (style != null && style.TryGet(name, out var customValue) && customValue != null && customValue.Length > 0) {
                    string substituted = ResolveInternal(customValue, style, seen, cycleMembers, depth + 1);
                    if (!ReferenceEquals(substituted, InvalidValue)) return substituted;
                    // The stored custom value resolved to invalid. If WE are
                    // now a known cycle member (the recurse just promoted us),
                    // §3.1 forbids fallback rescue — propagate invalid.
                    if (cycleMembers.Contains(name)) return InvalidValue;
                    // Otherwise the invalidation came from a non-cycle source
                    // (unresolvable nested var() with no fallback, etc.) and
                    // §3 lets our own fallback rescue the reference.
                }
                if (fallback != null) {
                    string fb = ResolveInternal(fallback, style, seen, cycleMembers, depth + 1);
                    if (ReferenceEquals(fb, InvalidValue)) return InvalidValue;
                    return fb.Trim();
                }
                return InvalidValue;
            } finally {
                seen.Remove(name);
            }
        }

        static void SplitVarArgs(string inside, out string name, out string fallback) {
            int depth = 0;
            for (int i = 0; i < inside.Length; i++) {
                char c = inside[i];
                if (c == '(') depth++;
                else if (c == ')' && depth > 0) depth--;
                else if (c == ',' && depth == 0) {
                    name = inside.Substring(0, i);
                    fallback = inside.Substring(i + 1);
                    return;
                }
            }
            name = inside;
            fallback = null;
        }

        static int FindMatchingParen(string s, int openIdx) {
            int depth = 0;
            for (int i = openIdx; i < s.Length; i++) {
                char c = s[i];
                if (c == '(') depth++;
                else if (c == ')') {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        static bool StartsWithCi(string s, int idx, string token) {
            if (idx + token.Length > s.Length) return false;
            for (int j = 0; j < token.Length; j++) {
                char a = s[idx + j];
                char b = token[j];
                if (char.ToLowerInvariant(a) != char.ToLowerInvariant(b)) return false;
            }
            return true;
        }
    }
}
