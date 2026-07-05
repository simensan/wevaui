using System.Globalization;
using System.Text;
using Weva.Css.Values;
using Weva.Dom;

namespace Weva.Css.Cascade {
    internal static class AttrResolver {
        public static string Resolve(string value, Element element) {
            if (value == null) return null;
            if (element == null) return value;
            if (value.IndexOf("attr(", System.StringComparison.OrdinalIgnoreCase) < 0) return value;
            return ResolveInternal(value, element, 0);
        }

        const int MaxDepth = 16;

        static string ResolveInternal(string value, Element element, int depth) {
            if (value == null) return null;
            if (depth > MaxDepth) return "";
            if (value.IndexOf("attr(", System.StringComparison.OrdinalIgnoreCase) < 0) return value;

            var sb = new StringBuilder(value.Length);
            int i = 0;
            while (i < value.Length) {
                if (StartsWithCi(value, i, "attr(")) {
                    int parenStart = i + 4;
                    int end = FindMatchingParen(value, parenStart);
                    if (end < 0) {
                        sb.Append(value, i, value.Length - i);
                        break;
                    }
                    string inside = value.Substring(parenStart + 1, end - parenStart - 1);
                    string replacement = ResolveAttrCall(inside, element, depth);
                    sb.Append(replacement);
                    i = end + 1;
                    continue;
                }
                sb.Append(value[i]);
                i++;
            }
            return sb.ToString();
        }

        static string ResolveAttrCall(string inside, Element element, int depth) {
            SplitArgs(inside, out string head, out string fallback);
            head = head.Trim();
            if (head.Length == 0) {
                return fallback != null ? ResolveInternal(fallback.Trim(), element, depth + 1) : "";
            }
            // head: "<name>" or "<name> <type>"
            int sp = -1;
            for (int k = 0; k < head.Length; k++) {
                if (head[k] == ' ' || head[k] == '\t') { sp = k; break; }
            }
            string name;
            string type;
            if (sp < 0) {
                name = head;
                type = "string";
            } else {
                name = head.Substring(0, sp).Trim();
                type = CssStringUtil.ToLowerInvariantOrSame(head.Substring(sp + 1).Trim());
                if (type.Length == 0) type = "string";
            }

            string attrValue = element.GetAttribute(name);
            if (attrValue == null) {
                return fallback != null ? ResolveInternal(fallback.Trim(), element, depth + 1) : "";
            }
            if (TryFormat(attrValue, type, out string formatted)) {
                return formatted;
            }
            return fallback != null ? ResolveInternal(fallback.Trim(), element, depth + 1) : "";
        }

        static bool TryFormat(string raw, string type, out string formatted) {
            formatted = null;
            if (type == "string") {
                formatted = raw;
                return true;
            }
            if (type == "raw-string") {
                formatted = raw;
                return true;
            }
            // CSS Values L4 §6.3 — <ident> type: attribute value is used as a
            // CSS identifier token verbatim. A valid CSS ident starts with a letter
            // or hyphen, followed by letters, digits, hyphens, or underscores.
            // We accept any non-empty, whitespace-trimmed attribute value here and
            // let the property's downstream parser reject it if it isn't a legal
            // ident for that property — the same permissive stance Chrome takes.
            if (type == "ident") {
                string t = raw.Trim();
                if (t.Length == 0) return false;
                formatted = t;
                return true;
            }
            if (type == "color") {
                string t = raw.Trim();
                if (t.Length == 0) return false;
                if (t[0] == '#') {
                    for (int k = 1; k < t.Length; k++) {
                        char c = t[k];
                        bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                        if (!hex) return false;
                    }
                    int len = t.Length - 1;
                    if (len != 3 && len != 4 && len != 6 && len != 8) return false;
                    formatted = t;
                    return true;
                }
                int paren = t.IndexOf('(');
                if (paren > 0 && t[t.Length - 1] == ')') {
                    formatted = t;
                    return true;
                }
                if (CssColor.TryFromName(CssStringUtil.ToLowerInvariantOrSame(t), out _)) {
                    formatted = t;
                    return true;
                }
                return false;
            }
            string trimmed = raw.Trim();
            switch (type) {
                case "length":
                    return TryFormatDimensionClass(trimmed, LengthUnits, false, out formatted);
                case "angle":
                    return TryFormatDimensionClass(trimmed, AngleUnits, false, out formatted);
                case "time":
                    return TryFormatDimensionClass(trimmed, TimeUnits, false, out formatted);
                case "frequency":
                    return TryFormatDimensionClass(trimmed, FrequencyUnits, true, out formatted);
                case "flex":
                    return TryFormatDimensionClass(trimmed, FlexUnits, false, out formatted);
                case "integer":
                    return TryFormatInteger(trimmed, out formatted);
                // CSS Values L4 §6.3 — <percentage> type: attribute value is a
                // bare number; formatted with a trailing %. Mirrors the "%" shorthand
                // alias below but uses the canonical type-keyword spelling.
                case "percentage": {
                    if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double pct)) return false;
                    formatted = pct.ToString("R", CultureInfo.InvariantCulture) + "%";
                    return true;
                }
            }
            if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) {
                return false;
            }
            string num = n.ToString("R", CultureInfo.InvariantCulture);
            switch (type) {
                case "number":
                    formatted = num;
                    return true;
                case "px":
                case "em":
                case "rem":
                case "vw":
                case "vh":
                case "vmin":
                case "vmax":
                case "%":
                    formatted = type == "%" ? num + "%" : num + type;
                    return true;
            }
            return false;
        }

        static readonly string[] LengthUnits = {
            "px", "em", "rem", "ex", "ch", "cap", "ic", "lh", "rlh",
            "vw", "vh", "vmin", "vmax", "vi", "vb",
            "svw", "svh", "lvw", "lvh", "dvw", "dvh",
            "cm", "mm", "in", "pt", "pc", "q"
        };
        static readonly string[] AngleUnits = { "deg", "grad", "rad", "turn" };
        static readonly string[] TimeUnits = { "ms", "s" };
        static readonly string[] FrequencyUnits = { "khz", "hz" };
        static readonly string[] FlexUnits = { "fr" };

        static bool TryFormatDimensionClass(string trimmed, string[] units, bool caseSensitive, out string formatted) {
            formatted = null;
            if (trimmed.Length == 0) return false;
            string probe = caseSensitive ? trimmed : CssStringUtil.ToLowerInvariantOrSame(trimmed);
            for (int u = 0; u < units.Length; u++) {
                string unit = units[u];
                if (probe.Length <= unit.Length) continue;
                if (!probe.EndsWith(unit, System.StringComparison.Ordinal)) continue;
                string numPart = trimmed.Substring(0, trimmed.Length - unit.Length).Trim();
                if (!double.TryParse(numPart, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) continue;
                formatted = trimmed;
                return true;
            }
            return false;
        }

        static bool TryFormatInteger(string trimmed, out string formatted) {
            formatted = null;
            if (trimmed.Length == 0) return false;
            int start = 0;
            if (trimmed[0] == '+' || trimmed[0] == '-') start = 1;
            if (start >= trimmed.Length) return false;
            for (int k = start; k < trimmed.Length; k++) {
                char c = trimmed[k];
                if (c < '0' || c > '9') return false;
            }
            formatted = trimmed;
            return true;
        }

        static void SplitArgs(string inside, out string head, out string fallback) {
            int depth = 0;
            for (int i = 0; i < inside.Length; i++) {
                char c = inside[i];
                if (c == '(') depth++;
                else if (c == ')' && depth > 0) depth--;
                else if (c == ',' && depth == 0) {
                    head = inside.Substring(0, i);
                    fallback = inside.Substring(i + 1);
                    return;
                }
            }
            head = inside;
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
