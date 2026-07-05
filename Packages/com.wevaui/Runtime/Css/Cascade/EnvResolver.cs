using System.Collections.Generic;
using System.Text;

namespace Weva.Css.Cascade {
    // CSS Environment Variables Module Level 1 — env() resolver.
    //
    // Structurally identical to VariableResolver (var()) — same shape
    // env(<custom-ident> [, <declaration-value> ]?), same parse rules
    // (paren-balanced fallback, recursive resolution), same
    // invalid-at-computed-value-time semantics on lookup failure with
    // no usable fallback. The only difference is the lookup table:
    // env() reads from EnvironmentVariables (runtime/UA-supplied),
    // var() reads from ComputedStyle (author-defined --customs).
    //
    // Per the spec env() variables are not author-defined and do NOT
    // participate in the cascade; they're resolved against a single
    // global registry that the host application can populate.
    internal static class EnvResolver {
        const int MaxDepth = 32;

        // Same guaranteed-invalid sentinel pattern as VariableResolver.
        // An env() that can't resolve AND has no usable fallback taints
        // the whole declaration so the cascade can drop it and fall back
        // to inherited/initial per Custom Properties L1 §3 (env() inherits
        // the same invalid-at-computed-value-time machinery).
        internal static readonly string InvalidValue = "__env_invalid__";

        // Legacy entry point — collapses invalid to "" to match the
        // VariableResolver.Resolve contract (used on custom-property
        // value storage where the cascade engine doesn't have a way
        // to signal "drop this declaration").
        public static string Resolve(string value) {
            if (value == null) return null;
            if (value.IndexOf("env(", System.StringComparison.OrdinalIgnoreCase) < 0) return value;
            string result = ResolveInternal(value, 0);
            return ReferenceEquals(result, InvalidValue) ? "" : result;
        }

        // Cascade-aware entry point — returns false when the env()
        // reference is invalid-at-computed-value-time so the caller
        // can drop the declaration.
        public static bool TryResolve(string value, out string resolved) {
            if (value == null) { resolved = null; return true; }
            if (value.IndexOf("env(", System.StringComparison.OrdinalIgnoreCase) < 0) {
                resolved = value;
                return true;
            }
            string result = ResolveInternal(value, 0);
            if (ReferenceEquals(result, InvalidValue)) {
                resolved = null;
                return false;
            }
            resolved = result;
            return true;
        }

        static string ResolveInternal(string value, int depth) {
            if (value == null) return null;
            if (depth > MaxDepth) return InvalidValue;
            if (value.IndexOf("env(", System.StringComparison.OrdinalIgnoreCase) < 0) return value;

            var sb = new StringBuilder(value.Length);
            int i = 0;
            while (i < value.Length) {
                if (StartsWithCi(value, i, "env(")) {
                    int parenStart = i + 3;
                    int end = FindMatchingParen(value, parenStart);
                    if (end < 0) {
                        sb.Append(value, i, value.Length - i);
                        break;
                    }
                    string inside = value.Substring(parenStart + 1, end - parenStart - 1);
                    string replacement = ResolveEnvCall(inside, depth);
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

        static string ResolveEnvCall(string inside, int depth) {
            SplitEnvArgs(inside, out string name, out string fallback);
            name = name.Trim();
            if (name.Length == 0) return InvalidValue;

            // CSS Environment Variables L1 supports an optional integer
            // index list after the name (`env(name 2 0, fallback)`) for
            // template-area-style multi-dimensional vars. v1 treats the
            // bare ident before any whitespace as the lookup key and
            // ignores trailing indices — they're not used by the
            // safe-area-inset-* canonical names. Trim trailing whitespace
            // and any non-ident continuation off the name.
            int wsIdx = -1;
            for (int j = 0; j < name.Length; j++) {
                if (char.IsWhiteSpace(name[j])) { wsIdx = j; break; }
            }
            if (wsIdx >= 0) name = name.Substring(0, wsIdx);
            if (name.Length == 0) return InvalidValue;

            if (EnvironmentVariables.TryGetValue(name, out var envValue) && envValue != null) {
                // Recurse so an env() value that itself contains
                // env()/var() resolves transitively. Custom-property
                // var()s inside env() values are NOT resolved here —
                // env() values are UA-supplied literals, not authored
                // declarations, and don't go through the cascade.
                string substituted = ResolveInternal(envValue, depth + 1);
                if (!ReferenceEquals(substituted, InvalidValue)) return substituted;
                // Stored env value tainted (contained an unresolvable
                // env() with no fallback). Fall through to THIS env()'s
                // fallback per the same §3 rescue rule var() uses.
            }
            if (fallback != null) {
                string fb = ResolveInternal(fallback, depth + 1);
                if (ReferenceEquals(fb, InvalidValue)) return InvalidValue;
                return fb.Trim();
            }
            return InvalidValue;
        }

        static void SplitEnvArgs(string inside, out string name, out string fallback) {
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
