using System.Text;
using Weva.Css.Media;

namespace Weva.Css.Cascade {
    internal static class LightDarkResolver {
        public static string Resolve(string value, ColorScheme scheme) {
            if (value == null) return null;
            if (value.IndexOf("light-dark(", System.StringComparison.OrdinalIgnoreCase) < 0) return value;
            return ResolveInternal(value, scheme, 0);
        }

        const int MaxDepth = 16;

        static string ResolveInternal(string value, ColorScheme scheme, int depth) {
            if (value == null) return null;
            if (depth > MaxDepth) return "";
            if (value.IndexOf("light-dark(", System.StringComparison.OrdinalIgnoreCase) < 0) return value;

            var sb = new StringBuilder(value.Length);
            int i = 0;
            while (i < value.Length) {
                if (StartsWithCi(value, i, "light-dark(")) {
                    int parenStart = i + 10;
                    int end = FindMatchingParen(value, parenStart);
                    if (end < 0) {
                        sb.Append(value, i, value.Length - i);
                        break;
                    }
                    string inside = value.Substring(parenStart + 1, end - parenStart - 1);
                    string replacement = ResolveCall(inside, scheme, depth);
                    sb.Append(replacement);
                    i = end + 1;
                    continue;
                }
                sb.Append(value[i]);
                i++;
            }
            return sb.ToString();
        }

        static string ResolveCall(string inside, ColorScheme scheme, int depth) {
            SplitTwoArgs(inside, out string light, out string dark);
            string pick = scheme == ColorScheme.Dark ? dark : light;
            if (pick == null) return "";
            return ResolveInternal(pick.Trim(), scheme, depth + 1);
        }

        static void SplitTwoArgs(string inside, out string a, out string b) {
            int depth = 0;
            for (int i = 0; i < inside.Length; i++) {
                char c = inside[i];
                if (c == '(') depth++;
                else if (c == ')' && depth > 0) depth--;
                else if (c == ',' && depth == 0) {
                    a = inside.Substring(0, i);
                    b = inside.Substring(i + 1);
                    return;
                }
            }
            a = inside;
            b = null;
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
