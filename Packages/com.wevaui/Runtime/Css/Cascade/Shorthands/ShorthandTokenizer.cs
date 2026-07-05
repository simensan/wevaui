using System.Collections.Generic;
using System.Text;

namespace Weva.Css.Cascade.Shorthands {
    // Lightweight tokenizer for shorthand values. Splits a string into space-separated
    // top-level tokens, treating parenthesised groups (linear-gradient(...), rgb(...),
    // url(...), calc(...)) as a single token, and emitting comma and slash as their
    // own tokens. Quoted strings are preserved as one token including their quotes.
    internal static class ShorthandTokenizer {
        // PA: pooled overload — caller-provided output list is reused.
        // Equivalent to Tokenize(value) but skips the inner `new List<string>`
        // allocation that fires per call in the hot inline-shorthand path.
        public static void TokenizeInto(string value, List<string> output) {
            output.Clear();
            if (string.IsNullOrEmpty(value)) return;
            TokenizeImpl(value, output);
        }

        public static List<string> Tokenize(string value) {
            var result = new List<string>();
            if (string.IsNullOrEmpty(value)) return result;
            TokenizeImpl(value, result);
            return result;
        }

        static void TokenizeImpl(string value, List<string> result) {
            int i = 0;
            int n = value.Length;
            while (i < n) {
                char c = value[i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f') { i++; continue; }
                if (c == ',') { result.Add(","); i++; continue; }
                if (c == '/') { result.Add("/"); i++; continue; }
                if (c == '"' || c == '\'') {
                    int start = i;
                    char quote = c;
                    i++;
                    while (i < n && value[i] != quote) {
                        if (value[i] == '\\' && i + 1 < n) i += 2;
                        else i++;
                    }
                    if (i < n) i++;
                    result.Add(value.Substring(start, i - start));
                    continue;
                }
                int tokStart = i;
                int depth = 0;
                while (i < n) {
                    char ch = value[i];
                    if (depth == 0 && (ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r' || ch == '\f' || ch == ',' || ch == '/')) break;
                    if (ch == '(') depth++;
                    else if (ch == ')' && depth > 0) depth--;
                    else if ((ch == '"' || ch == '\'') && depth > 0) {
                        char q = ch;
                        i++;
                        while (i < n && value[i] != q) {
                            if (value[i] == '\\' && i + 1 < n) i += 2;
                            else i++;
                        }
                        if (i < n) i++;
                        continue;
                    }
                    i++;
                }
                result.Add(value.Substring(tokStart, i - tokStart));
            }
        }

        // PA: pooled overload — output `groups` is reused and each inner
        // List<string> is rented from `innerPool` so steady-state hot calls
        // allocate zero. `innerPool` is grown lazily.
        public static void SplitOnCommaInto(List<string> tokens, List<List<string>> groups, List<List<string>> innerPool, ref int innerPoolUsed) {
            groups.Clear();
            List<string> current = RentInner(innerPool, ref innerPoolUsed);
            for (int i = 0; i < tokens.Count; i++) {
                string t = tokens[i];
                if (t == ",") {
                    groups.Add(current);
                    current = RentInner(innerPool, ref innerPoolUsed);
                    continue;
                }
                current.Add(t);
            }
            groups.Add(current);
        }

        static List<string> RentInner(List<List<string>> pool, ref int used) {
            List<string> inner;
            if (used < pool.Count) {
                inner = pool[used];
                inner.Clear();
            } else {
                inner = new List<string>(4);
                pool.Add(inner);
            }
            used++;
            return inner;
        }

        // Splits tokens on comma at the top level into a list of token-lists.
        public static List<List<string>> SplitOnComma(List<string> tokens) {
            var groups = new List<List<string>>();
            var current = new List<string>();
            foreach (var t in tokens) {
                if (t == ",") {
                    groups.Add(current);
                    current = new List<string>();
                    continue;
                }
                current.Add(t);
            }
            groups.Add(current);
            return groups;
        }

        // PA: list-typed Join — calls List<T>.Enumerator (struct, no alloc)
        // instead of going through IEnumerable. Single-element fast-path
        // returns the token verbatim with no StringBuilder allocation.
        public static string Join(List<string> tokens) {
            if (tokens.Count == 0) return "";
            if (tokens.Count == 1) return tokens[0];
            var sb = new StringBuilder();
            for (int i = 0; i < tokens.Count; i++) {
                if (i > 0) sb.Append(' ');
                sb.Append(tokens[i]);
            }
            return sb.ToString();
        }

        // Joins tokens back with single spaces.
        public static string Join(IEnumerable<string> tokens) {
            var sb = new StringBuilder();
            bool first = true;
            foreach (var t in tokens) {
                if (!first) sb.Append(' ');
                sb.Append(t);
                first = false;
            }
            return sb.ToString();
        }
    }
}
