using System.Collections.Generic;
using System.Text;
using Weva.Css.Values;
using Weva.Parsing;

namespace Weva.Css {
    public static class CssParser {
        // Fast-path entry for `<element style="...">`. Avoids the
        // Stylesheet / Rule / Selector allocations the regular Parse path
        // costs to wrap the inline text into a synthetic `*{...}` rule —
        // those wrapper structures get thrown away immediately by the
        // caller. Per-frame inline-style refresh on animated elements is
        // ~30% of the cascade allocation footprint without this entry.
        //
        // PA: tokenizer-free implementation. The inline-style grammar is
        // a flat sequence of `property: value;` declarations — no nested
        // rules, no selectors, no at-rules to worry about. A character-
        // level scan that respects paren/string nesting handles every
        // valid inline declaration without paying for a CssTokenizer +
        // ParserContext + a per-token CssToken list. The full tokenizer
        // path remains available as a fallback for callers that need
        // pedantic CSS Syntax §5.3 conformance (which the cascade engine
        // does not — author inline styles are simple by construction).
        public static List<Declaration> ParseInlineDeclarations(string source, List<Declaration> output = null, ParseOptions options = null) {
            options ??= new ParseOptions();
            output ??= new List<Declaration>(4);
            if (string.IsNullOrEmpty(source)) return output;
            int i = 0;
            int n = source.Length;
            while (i < n) {
                i = SkipWsAndComments(source, i, n);
                if (i >= n) break;
                // Property name: ASCII ident chars (incl. `-` and `_`). Stop at `:`.
                int propStart = i;
                while (i < n) {
                    char c = source[i];
                    if (c == ':' || c == ';' || c == '{' || c == '}') break;
                    i++;
                }
                int propEnd = TrimTrailingWs(source, propStart, i);
                if (i >= n || source[i] != ':' || propEnd == propStart) {
                    // Malformed declaration — scan to next `;` and try again.
                    while (i < n && source[i] != ';') i++;
                    if (i < n) i++;
                    continue;
                }
                i++; // skip ':'
                i = SkipWsAndComments(source, i, n);
                // Value: anything until top-level `;` (parens/quotes nested).
                int valStart = i;
                int parenDepth = 0;
                char quote = '\0';
                while (i < n) {
                    char c = source[i];
                    if (quote != '\0') {
                        if (c == '\\' && i + 1 < n) { i += 2; continue; }
                        if (c == quote) quote = '\0';
                        i++;
                        continue;
                    }
                    if (c == '"' || c == '\'') { quote = c; i++; continue; }
                    if (c == '(' || c == '[' || c == '{') { parenDepth++; i++; continue; }
                    if (c == ')' || c == ']' || c == '}') {
                        if (parenDepth == 0) break;
                        parenDepth--;
                        i++;
                        continue;
                    }
                    if (c == ';' && parenDepth == 0) break;
                    if (c == '/' && i + 1 < n && source[i + 1] == '*') {
                        i = SkipCommentRun(source, i, n);
                        continue;
                    }
                    i++;
                }
                int valEnd = TrimTrailingWs(source, valStart, i);
                // !important suffix (case-insensitive, optional whitespace before `!`).
                bool important = false;
                if (valEnd - valStart >= 10) {
                    int impEnd = valEnd;
                    int impStart = impEnd - 10;
                    if (source[impStart] == '!'
                        && EqualsIgnoreCaseAscii(source, impStart + 1, "important")) {
                        int trimmed = TrimTrailingWs(source, valStart, impStart);
                        valEnd = trimmed;
                        important = true;
                    }
                }
                if (valEnd > valStart) {
                    string property = LowerSubstring(source, propStart, propEnd);
                    string valueText = source.Substring(valStart, valEnd - valStart);
                    output.Add(new Declaration(property, valueText, important));
                }
                if (i < n) i++; // skip terminating ';'
            }
            return output;
        }

        static int SkipWsAndComments(string s, int i, int n) {
            while (i < n) {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f') { i++; continue; }
                if (c == '/' && i + 1 < n && s[i + 1] == '*') {
                    i = SkipCommentRun(s, i, n);
                    continue;
                }
                break;
            }
            return i;
        }

        static int SkipCommentRun(string s, int i, int n) {
            i += 2;
            while (i + 1 < n) {
                if (s[i] == '*' && s[i + 1] == '/') return i + 2;
                i++;
            }
            return n;
        }

        static int TrimTrailingWs(string s, int start, int end) {
            while (end > start) {
                char c = s[end - 1];
                if (c != ' ' && c != '\t' && c != '\n' && c != '\r' && c != '\f') break;
                end--;
            }
            return end;
        }

        static bool EqualsIgnoreCaseAscii(string s, int offset, string needle) {
            if (offset + needle.Length > s.Length) return false;
            for (int k = 0; k < needle.Length; k++) {
                char a = s[offset + k];
                char b = needle[k];
                if (a >= 'A' && a <= 'Z') a = (char)(a + 32);
                if (b >= 'A' && b <= 'Z') b = (char)(b + 32);
                if (a != b) return false;
            }
            return true;
        }

        // Lowercases a substring of `s` for use as a property identifier. If
        // every char is already lowercase ASCII, returns the substring as-is —
        // a tighter path than `Substring(...).ToLowerInvariant()` since the
        // common author input is already lowercase.
        static string LowerSubstring(string s, int start, int end) {
            bool needsLower = false;
            for (int k = start; k < end; k++) {
                char c = s[k];
                if (c >= 'A' && c <= 'Z') { needsLower = true; break; }
            }
            if (!needsLower) return s.Substring(start, end - start);
            var sb = new StringBuilder(end - start);
            for (int k = start; k < end; k++) {
                char c = s[k];
                if (c >= 'A' && c <= 'Z') c = (char)(c + 32);
                sb.Append(c);
            }
            return sb.ToString();
        }

        public static Stylesheet Parse(string source, ParseOptions options = null) {
            options ??= new ParseOptions();
            var tokens = new CssTokenizer(source, throwOnError: options.ThrowOnError).Tokenize();
            var ctx = new ParserContext(tokens, options);
            var sheet = new Stylesheet();
            ctx.SkipWhitespace();
            while (!ctx.IsEof()) {
                var rule = ParseTopLevelRule(ctx);
                if (rule != null) sheet.Rules.Add(rule);
                ctx.SkipWhitespace();
            }
            // CSS Nesting Module — flatten StyleRule.NestedRules into top-level
            // rules. Done as a post-parse pass so the parser itself stays
            // pure-syntactic.
            NestingExpander.Expand(sheet);
            return sheet;
        }

        static Rule ParseTopLevelRule(ParserContext ctx) {
            var t = ctx.Current();
            if (t.Kind == CssTokenKind.AtKeyword) {
                return ParseAtRule(ctx);
            }
            return ParseStyleRule(ctx);
        }

        // PH1: hard cap on rule-nesting recursion. ~20,000 nested
        // `@media all{` (about 200KB of hostile CSS) previously drove the
        // ParseAtRule/ParseStyleRule <-> ParseRuleBody mutual recursion into
        // an UNCATCHABLE StackOverflow that kills the editor/player. Real
        // sheets nest a handful of levels; past the cap the whole rule is
        // skipped (EC11 rule-drop semantics) with a diagnostic. Same pattern
        // as VariableResolver/EnvResolver/AttrResolver.
        const int MaxRuleDepth = 128;

        static bool EnterRule(ParserContext ctx) {
            if (ctx.RuleDepth >= MaxRuleDepth) {
                Weva.Diagnostics.UICssDiagnostics.Warn(
                    "CssParser",
                    "rule nesting deeper than " + MaxRuleDepth + " levels — rule skipped");
                SkipUnknownAtRule(ctx);
                return false;
            }
            ctx.RuleDepth++;
            return true;
        }

        static Rule ParseAtRule(ParserContext ctx) {
            if (!EnterRule(ctx)) return null;
            try {
                return ParseAtRuleInner(ctx);
            } finally {
                ctx.RuleDepth--;
            }
        }

        static Rule ParseAtRuleInner(ParserContext ctx) {
            var atTok = ctx.Current();
            string name = CssStringUtil.ToLowerInvariantOrSame(atTok.Text);
            ctx.Advance();

            switch (name) {
                case "media":
                    return ParseMediaRule(ctx, atTok);
                case "supports":
                    return ParseSupportsRule(ctx, atTok);
                case "container":
                    return ParseContainerRule(ctx, atTok);
                case "keyframes":
                    return ParseKeyframesRule(ctx, atTok);
                case "import":
                    return ParseImportRule(ctx, atTok);
                case "layer":
                    return ParseLayerRule(ctx, atTok);
                case "scope":
                    return ParseScopeRule(ctx, atTok);
                case "font-face":
                    return ParseFontFaceRule(ctx, atTok);
                case "property":
                    return ParsePropertyRule(ctx, atTok);
                case "namespace":
                    // CSS Namespaces 3 — informational only in v1. We accept the
                    // syntax so SVG/XML stylesheets parse, but the prefix→URI
                    // map isn't materialized (selector-side namespace handling
                    // already drops the prefix and matches on the local name).
                    SkipUntilSemicolonOrBrace(ctx);
                    return null;
                default:
                    Weva.Diagnostics.UICssDiagnostics.Warn(
                        "CssParser",
                        "unknown at-rule '@" + name + "' skipped");
                    SkipUnknownAtRule(ctx);
                    return null;
            }
        }

        // CSS Fonts 4 §11 — full `@font-face` descriptor parsing.
        // Reads all standard descriptors: font-family, src (multi-entry with
        // local() and format() support), font-weight (value or range),
        // font-style (normal|italic|oblique [angle[range]]), font-stretch
        // (keyword or percentage, optional range), unicode-range (U+ ranges
        // with wildcard support), and font-display.
        // Runtime-honoured: font-family + first url() from src.
        // Parse-only (stored on AST, not yet consumed by runtime):
        //   font-weight, font-style, font-stretch, unicode-range, font-display,
        //   local() entries, format() hints, secondary src entries.
        static FontFaceRule ParseFontFaceRule(ParserContext ctx, CssToken atTok) {
            ctx.SkipWhitespace();
            if (ctx.IsEof() || ctx.Current().Kind != CssTokenKind.LBrace) {
                if (ctx.Options.ThrowOnError)
                    throw new CssParseException("Expected '{' after @font-face", atTok.Line, atTok.Column);
                SkipUntilSemicolonOrBrace(ctx);
                return null;
            }
            var braceTok = ctx.Current();
            ctx.Advance();
            var decls = new List<Declaration>();
            ParseDeclarations(ctx, decls, braceTok);
            var rule = new FontFaceRule();
            foreach (var d in decls) {
                switch (d.Property) {
                    case "font-family":
                        if (rule.FontFamily == null)
                            rule.FontFamily = StripFamilyQuotes(d.ValueText);
                        break;
                    case "src":
                        if (rule.SrcList.Count == 0)
                            ParseFontSrcList(d.ValueText, rule);
                        break;
                    case "font-weight":
                        if (rule.WeightMin == null)
                            ParseFontWeightDescriptor(d.ValueText, rule);
                        break;
                    case "font-style":
                        if (rule.FontStyle == FontFaceStyleValue.Normal && rule.ObliqueAngleMin == 0 && rule.ObliqueAngleMax == 0)
                            ParseFontStyleDescriptor(d.ValueText, rule);
                        break;
                    case "font-stretch":
                        if (rule.StretchMin == null)
                            ParseFontStretchDescriptor(d.ValueText, rule);
                        break;
                    case "unicode-range":
                        if (rule.UnicodeRange.Count == 0)
                            ParseUnicodeRange(d.ValueText, rule);
                        break;
                    case "font-display":
                        rule.FontDisplay = ParseFontDisplayValue(d.ValueText);
                        break;
                }
            }
            if (!string.IsNullOrEmpty(rule.FontFamily) && !string.IsNullOrEmpty(rule.Src)) {
                // CSS Fonts L4 §11 — pass the full descriptor set through so
                // FontResolver can select the right face for font-weight/style.
                // WeightMin/Max default to full range (1–1000) when omitted;
                // FontStyle maps Italic/Oblique to isItalic=true.
                float wMin = rule.WeightMin.HasValue ? rule.WeightMin.Value : 1f;
                float wMax = rule.WeightMax.HasValue ? rule.WeightMax.Value : 1000f;
                bool isItalic = rule.FontStyle == FontFaceStyleValue.Italic
                             || rule.FontStyle == FontFaceStyleValue.Oblique;
                RegisterFontFace(rule.FontFamily, rule.Src, wMin, wMax, isItalic);
            }
            return rule;
        }

        // Parse the `src` descriptor value into an ordered list of FontSrcEntry.
        // Grammar: <font-src># where <font-src> is:
        //   url(<uri>) [format(<type>)]
        //   local(<name>)
        // The first url() path is also stored in rule.Src for runtime back-compat.
        static void ParseFontSrcList(string raw, FontFaceRule rule) {
            if (string.IsNullOrEmpty(raw)) return;
            // Split at top-level commas
            var entries = SplitTopLevelCommas(raw);
            foreach (var entry in entries) {
                string s = entry.Trim();
                if (string.IsNullOrEmpty(s)) continue;
                var srcEntry = TryParseFontSrcEntry(s);
                if (srcEntry != null) {
                    rule.SrcList.Add(srcEntry);
                    // First url() wins for back-compat Src field
                    if (rule.Src == null && srcEntry.Url != null)
                        rule.Src = srcEntry.Url;
                }
            }
        }

        // Parse a single src entry: `url(...) [format(...)]` or `local(...)`.
        static FontSrcEntry TryParseFontSrcEntry(string s) {
            if (StartsWithIdent(s, "local", out int afterLocal)) {
                // local(<name>) — strip parens + optional quotes
                string inner = ExtractParenContent(s, afterLocal);
                if (inner == null) return null;
                inner = StripQuotes(inner.Trim());
                return new FontSrcEntry { LocalName = inner };
            }
            if (StartsWithIdent(s, "url", out int afterUrl)) {
                // url(<path>) [format(<type>)]
                string urlInner = ExtractParenContent(s, afterUrl);
                if (urlInner == null) return null;
                string path = StripQuotes(urlInner.Trim());
                // skip past the url(...) part to look for format(...)
                int closeParen = FindCloseParen(s, afterUrl);
                string format = null;
                if (closeParen >= 0) {
                    string rest = s.Substring(closeParen + 1).TrimStart();
                    if (StartsWithIdent(rest, "format", out int afterFormat)) {
                        string fmtInner = ExtractParenContent(rest, afterFormat);
                        if (fmtInner != null) {
                            format = StripQuotes(fmtInner.Trim()).ToLowerInvariant();
                        }
                    }
                }
                return new FontSrcEntry { Url = path, Format = format };
            }
            return null;
        }

        // Returns the position just past "identName" if s starts with identName(,
        // otherwise returns -1 via out param. Case-insensitive.
        static bool StartsWithIdent(string s, string ident, out int parenPos) {
            parenPos = -1;
            if (s.Length < ident.Length) return false;
            for (int i = 0; i < ident.Length; i++) {
                char a = s[i];
                char b = ident[i];
                if (a >= 'A' && a <= 'Z') a = (char)(a + 32);
                if (b >= 'A' && b <= 'Z') b = (char)(b + 32);
                if (a != b) return false;
            }
            // After the ident, allow optional whitespace then '('
            int pos = ident.Length;
            while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t')) pos++;
            if (pos >= s.Length || s[pos] != '(') return false;
            parenPos = pos;
            return true;
        }

        // Extracts the content inside the first '(' ... ')' pair starting at parenPos.
        // Handles nested parens. Returns null if unclosed.
        static string ExtractParenContent(string s, int parenPos) {
            if (parenPos < 0 || parenPos >= s.Length || s[parenPos] != '(') return null;
            int start = parenPos + 1;
            int depth = 1;
            int i = start;
            while (i < s.Length && depth > 0) {
                if (s[i] == '(') depth++;
                else if (s[i] == ')') depth--;
                if (depth > 0) i++;
                else break;
            }
            if (depth != 0) return null;
            return s.Substring(start, i - start);
        }

        // Finds position of matching ')' for '(' at parenPos.
        static int FindCloseParen(string s, int parenPos) {
            if (parenPos < 0 || parenPos >= s.Length || s[parenPos] != '(') return -1;
            int depth = 1;
            int i = parenPos + 1;
            while (i < s.Length && depth > 0) {
                if (s[i] == '(') depth++;
                else if (s[i] == ')') depth--;
                i++;
            }
            return depth == 0 ? i - 1 : -1;
        }

        // Strips one layer of matching quotes from a string.
        static string StripQuotes(string s) {
            if (s == null || s.Length < 2) return s ?? "";
            char first = s[0];
            char last = s[s.Length - 1];
            if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
                return s.Substring(1, s.Length - 2);
            return s;
        }

        // Splits `raw` at top-level commas (not inside parentheses or quotes).
        static List<string> SplitTopLevelCommas(string raw) {
            var result = new List<string>();
            int start = 0;
            int depth = 0;
            char quote = '\0';
            for (int i = 0; i < raw.Length; i++) {
                char c = raw[i];
                if (quote != '\0') {
                    if (c == '\\' && i + 1 < raw.Length) { i++; continue; }
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c == '"' || c == '\'') { quote = c; continue; }
                if (c == '(') { depth++; continue; }
                if (c == ')') { if (depth > 0) depth--; continue; }
                if (c == ',' && depth == 0) {
                    result.Add(raw.Substring(start, i - start));
                    start = i + 1;
                }
            }
            result.Add(raw.Substring(start));
            return result;
        }

        // CSS Fonts 4 §11.3 — font-weight descriptor.
        // <number> | <number> <number>  (variable-axis range)
        // Keywords: normal=400, bold=700
        static void ParseFontWeightDescriptor(string raw, FontFaceRule rule) {
            if (string.IsNullOrEmpty(raw)) return;
            string s = raw.Trim();
            // Keyword shortcuts
            string sl = s.ToLowerInvariant();
            if (sl == "normal") { rule.WeightMin = rule.WeightMax = 400f; return; }
            if (sl == "bold") { rule.WeightMin = rule.WeightMax = 700f; return; }
            // One or two numbers
            var parts = SplitWhitespace(s);
            if (parts.Count >= 1 && TryParseFloat(parts[0], out float v1)) {
                rule.WeightMin = v1;
                rule.WeightMax = v1;
                if (parts.Count >= 2 && TryParseFloat(parts[1], out float v2))
                    rule.WeightMax = v2;
            }
        }

        // CSS Fonts 4 §11.4 — font-style descriptor.
        // normal | italic | oblique [<angle> [<angle>]]
        // Angle unit: deg (others are valid CSS but oblique range is deg in practice)
        static void ParseFontStyleDescriptor(string raw, FontFaceRule rule) {
            if (string.IsNullOrEmpty(raw)) return;
            string s = raw.Trim().ToLowerInvariant();
            if (s == "normal") { rule.FontStyle = FontFaceStyleValue.Normal; return; }
            if (s == "italic") { rule.FontStyle = FontFaceStyleValue.Italic; return; }
            if (s.StartsWith("oblique", System.StringComparison.Ordinal)) {
                rule.FontStyle = FontFaceStyleValue.Oblique;
                // optional angles after "oblique"
                string rest = raw.Trim().Substring(7).Trim();
                var parts = SplitWhitespace(rest);
                if (parts.Count >= 1 && TryParseAngleDeg(parts[0], out float a1)) {
                    rule.ObliqueAngleMin = a1;
                    rule.ObliqueAngleMax = a1;
                    if (parts.Count >= 2 && TryParseAngleDeg(parts[1], out float a2))
                        rule.ObliqueAngleMax = a2;
                } else {
                    // CSS default oblique range: 14deg
                    rule.ObliqueAngleMin = 14f;
                    rule.ObliqueAngleMax = 14f;
                }
            }
        }

        // CSS Fonts 4 §11.5 — font-stretch descriptor.
        // <percentage> | <percentage> <percentage>
        // Keywords map to percentage values per spec Table 1.
        static void ParseFontStretchDescriptor(string raw, FontFaceRule rule) {
            if (string.IsNullOrEmpty(raw)) return;
            string s = raw.Trim().ToLowerInvariant();
            // Keywords → percentages
            switch (s) {
                case "ultra-condensed": rule.StretchMin = rule.StretchMax = 50f; return;
                case "extra-condensed": rule.StretchMin = rule.StretchMax = 62.5f; return;
                case "condensed":       rule.StretchMin = rule.StretchMax = 75f; return;
                case "semi-condensed":  rule.StretchMin = rule.StretchMax = 87.5f; return;
                case "normal":          rule.StretchMin = rule.StretchMax = 100f; return;
                case "semi-expanded":   rule.StretchMin = rule.StretchMax = 112.5f; return;
                case "expanded":        rule.StretchMin = rule.StretchMax = 125f; return;
                case "extra-expanded":  rule.StretchMin = rule.StretchMax = 150f; return;
                case "ultra-expanded":  rule.StretchMin = rule.StretchMax = 200f; return;
            }
            // One or two percentage values
            var parts = SplitWhitespace(raw.Trim());
            if (parts.Count >= 1 && TryParsePercentage(parts[0], out float v1)) {
                rule.StretchMin = v1;
                rule.StretchMax = v1;
                if (parts.Count >= 2 && TryParsePercentage(parts[1], out float v2))
                    rule.StretchMax = v2;
            }
        }

        // CSS Fonts 4 §11.7 — unicode-range descriptor.
        // <urange># where each token is:
        //   U+HHHH         single codepoint
        //   U+HHHH-HHHH    range
        //   U+HH??         wildcard (? = any hex digit)
        static void ParseUnicodeRange(string raw, FontFaceRule rule) {
            if (string.IsNullOrEmpty(raw)) return;
            var parts = SplitTopLevelCommas(raw);
            foreach (var part in parts) {
                string token = part.Trim().ToUpperInvariant();
                if (!token.StartsWith("U+", System.StringComparison.Ordinal)) continue;
                string body = token.Substring(2);
                // Check for range: U+HHHH-HHHH
                int dash = body.IndexOf('-');
                if (dash > 0) {
                    string lo = body.Substring(0, dash);
                    string hi = body.Substring(dash + 1);
                    if (TryParseHex(lo, out int loVal) && TryParseHex(hi, out int hiVal))
                        rule.UnicodeRange.Add((loVal, hiVal));
                    continue;
                }
                // Wildcard: U+4?? — replace each '?' with '0' for start and 'F' for end
                if (body.IndexOf('?') >= 0) {
                    string loStr = body.Replace('?', '0');
                    string hiStr = body.Replace('?', 'F');
                    if (TryParseHex(loStr, out int loVal) && TryParseHex(hiStr, out int hiVal))
                        rule.UnicodeRange.Add((loVal, hiVal));
                    continue;
                }
                // Single codepoint
                if (TryParseHex(body, out int cpVal))
                    rule.UnicodeRange.Add((cpVal, cpVal));
            }
        }

        // CSS Fonts 4 §11.8 — font-display descriptor.
        // auto | block | swap | fallback | optional
        static FontDisplayValue ParseFontDisplayValue(string raw) {
            if (string.IsNullOrEmpty(raw)) return FontDisplayValue.Auto;
            switch (raw.Trim().ToLowerInvariant()) {
                case "auto":     return FontDisplayValue.Auto;
                case "block":    return FontDisplayValue.Block;
                case "swap":     return FontDisplayValue.Swap;
                case "fallback": return FontDisplayValue.Fallback;
                case "optional": return FontDisplayValue.Optional;
                default:         return FontDisplayValue.Auto; // invalid → drop (treat as auto)
            }
        }

        // Split a string on ASCII whitespace.
        static List<string> SplitWhitespace(string s) {
            var result = new List<string>();
            int i = 0;
            while (i < s.Length) {
                while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
                if (i >= s.Length) break;
                int start = i;
                while (i < s.Length && s[i] != ' ' && s[i] != '\t' && s[i] != '\n' && s[i] != '\r') i++;
                result.Add(s.Substring(start, i - start));
            }
            return result;
        }

        static bool TryParseFloat(string s, out float value) {
            return float.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        // Parses a CSS angle with unit: "14deg" → 14f. Supports deg only for now;
        // other units (rad, grad, turn) would need conversion.
        static bool TryParseAngleDeg(string s, out float degrees) {
            degrees = 0;
            if (string.IsNullOrEmpty(s)) return false;
            string lower = s.ToLowerInvariant();
            if (lower.EndsWith("deg")) {
                return TryParseFloat(s.Substring(0, s.Length - 3), out degrees);
            }
            if (lower.EndsWith("rad")) {
                float rad;
                if (!TryParseFloat(s.Substring(0, s.Length - 3), out rad)) return false;
                degrees = rad * (180f / (float)System.Math.PI);
                return true;
            }
            if (lower.EndsWith("grad")) {
                float grad;
                if (!TryParseFloat(s.Substring(0, s.Length - 4), out grad)) return false;
                degrees = grad * 0.9f;
                return true;
            }
            if (lower.EndsWith("turn")) {
                float turn;
                if (!TryParseFloat(s.Substring(0, s.Length - 4), out turn)) return false;
                degrees = turn * 360f;
                return true;
            }
            // bare number treated as degrees (lenient)
            return TryParseFloat(s, out degrees);
        }

        // Parses a CSS <percentage> token: "75%" → 75f.
        static bool TryParsePercentage(string s, out float value) {
            value = 0;
            if (string.IsNullOrEmpty(s) || s[s.Length - 1] != '%') return false;
            return TryParseFloat(s.Substring(0, s.Length - 1), out value);
        }

        // Parses a hex string (no 0x prefix) to an integer.
        static bool TryParseHex(string s, out int value) {
            value = 0;
            if (string.IsNullOrEmpty(s)) return false;
            int result = 0;
            foreach (char c in s) {
                int digit;
                if (c >= '0' && c <= '9') digit = c - '0';
                else if (c >= 'A' && c <= 'F') digit = 10 + (c - 'A');
                else if (c >= 'a' && c <= 'f') digit = 10 + (c - 'a');
                else return false;
                result = (result << 4) | digit;
            }
            value = result;
            return true;
        }

        // CSS Properties and Values API Level 1 — `@property` rule.
        // Parses the at-rule:
        //
        //   @property --my-prop {
        //     syntax: "<length>";
        //     initial-value: 0px;
        //     inherits: false;
        //   }
        //
        // All three descriptors (`syntax`, `initial-value`, `inherits`) are required.
        // If any is absent or `syntax`/`inherits` carries an invalid value, the rule
        // is discarded (returns null) per CSS Properties & Values L1 §3.
        // The prelude must be a valid custom property name (starting with `--`).
        static AtPropertyRule ParsePropertyRule(ParserContext ctx, CssToken atTok) {
            ctx.SkipWhitespace();
            // Read the custom property name from the prelude.
            string name = null;
            if (!ctx.IsEof() && ctx.Current().Kind == CssTokenKind.Ident) {
                name = ctx.Current().Text;
                ctx.Advance();
            }
            ctx.SkipWhitespace();
            if (ctx.IsEof() || ctx.Current().Kind != CssTokenKind.LBrace) {
                if (ctx.Options.ThrowOnError)
                    throw new CssParseException("Expected '{' after @property name", atTok.Line, atTok.Column);
                SkipUntilSemicolonOrBrace(ctx);
                return null;
            }
            // Validate name.
            if (string.IsNullOrEmpty(name) || !name.StartsWith("--", System.StringComparison.Ordinal)) {
                // Not a custom property name — discard per spec.
                SkipUnknownAtRule(ctx);
                return null;
            }
            var braceTok = ctx.Current();
            ctx.Advance();
            var decls = new List<Declaration>();
            ParseDeclarations(ctx, decls, braceTok);
            string syntax = null;
            string initialValue = null;
            string inheritsText = null;
            foreach (var d in decls) {
                switch (d.Property) {
                    case "syntax":
                        if (syntax == null) syntax = d.ValueText;
                        break;
                    case "initial-value":
                        if (initialValue == null) initialValue = d.ValueText;
                        break;
                    case "inherits":
                        if (inheritsText == null) inheritsText = d.ValueText;
                        break;
                }
            }
            var descriptor = Weva.Css.Cascade.PropertyDescriptor.TryCreate(name, syntax, initialValue, inheritsText);
            if (descriptor == null) {
                // At least one required descriptor was missing or invalid — discard.
                return null;
            }
            return new AtPropertyRule {
                Name = descriptor.Name,
                Syntax = descriptor.Syntax,
                InitialValue = descriptor.InitialValue,
                Inherits = descriptor.Inherits
            };
        }

        // Registers the @font-face source with the runtime font resolver.
        // Uses direct reference rather than reflection: the CSS assembly and
        // the Text assembly are both part of the same Weva.Runtime asmdef
        // (com.wevaui), so there is no layering barrier requiring reflection.
        // The NET8_0_OR_GREATER guard is kept only because TestVerifyAll
        // compiles with net8.0 while shipping a stub FontResolver that
        // satisfies the reference (Runtime/Text/** is excluded from that csproj).
        //
        // CSS Fonts L4 §11 — full descriptor pass-through:
        //   weightMin / weightMax — the face's declared weight range (1–1000 if absent)
        //   isItalic              — true for italic or oblique faces
        static void RegisterFontFace(
            string family, string src,
            float weightMin, float weightMax, bool isItalic)
        {
#if NET8_0_OR_GREATER
            var type = typeof(CssParser).Assembly.GetType("Weva.Text.TextCore.FontResolver");
            var method = type?.GetMethod(
                "RegisterFontFace",
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Static);
            method?.Invoke(null, new object[] { family, src, weightMin, weightMax, isItalic });
#else
            Weva.Text.TextCore.FontResolver.RegisterFontFace(family, src, weightMin, weightMax, isItalic);
#endif
        }

        // Strips one layer of single or double quotes from a font-family
        // descriptor value. CSS allows both `font-family: "Inter"` and
        // `font-family: Inter` for unquoted single-token names; we accept the
        // first non-comma-delimited token. Multi-token comma-separated lists
        // are uncommon in @font-face but if present we keep the head only.
        static string StripFamilyQuotes(string raw) {
            if (string.IsNullOrEmpty(raw)) return null;
            string head = raw;
            int comma = head.IndexOf(',');
            if (comma >= 0) head = head.Substring(0, comma);
            head = head.Trim();
            if (head.Length >= 2) {
                char first = head[0];
                char last = head[head.Length - 1];
                if ((first == '"' && last == '"') || (first == '\'' && last == '\'')) {
                    head = head.Substring(1, head.Length - 2).Trim();
                }
            }
            return head.Length == 0 ? null : head;
        }

        // Pulls the path out of `url("...")` / `url(...)`. Stops at the first
        // url() it finds; format() hints, additional comma-separated sources,
        // and `local(...)` entries are ignored. Returns null when no url() is
        // present (e.g. `src: local(SomeFont)` only — not a v1 supported case).
        static string ExtractFirstUrl(string raw) {
            if (string.IsNullOrEmpty(raw)) return null;
            int idx = 0;
            while (idx < raw.Length) {
                int u = IndexOfIdent(raw, "url", idx);
                if (u < 0) return null;
                int paren = u + 3;
                while (paren < raw.Length && char.IsWhiteSpace(raw[paren])) paren++;
                if (paren >= raw.Length || raw[paren] != '(') { idx = u + 3; continue; }
                int end = raw.IndexOf(')', paren + 1);
                if (end < 0) return null;
                string inner = raw.Substring(paren + 1, end - paren - 1).Trim();
                if (inner.Length >= 2) {
                    char first = inner[0];
                    char last = inner[inner.Length - 1];
                    if ((first == '"' && last == '"') || (first == '\'' && last == '\'')) {
                        inner = inner.Substring(1, inner.Length - 2).Trim();
                    }
                }
                return inner.Length == 0 ? null : inner;
            }
            return null;
        }

        static int IndexOfIdent(string s, string ident, int start) {
            int n = s.Length;
            int m = ident.Length;
            for (int i = start; i + m <= n; i++) {
                if (i > 0) {
                    char prev = s[i - 1];
                    if (char.IsLetterOrDigit(prev) || prev == '-' || prev == '_') continue;
                }
                bool match = true;
                for (int j = 0; j < m; j++) {
                    char a = s[i + j];
                    char b = ident[j];
                    if (char.ToLowerInvariant(a) != char.ToLowerInvariant(b)) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        // CSS Cascade Module Level 5 — `@scope`.
        //   `@scope (.start) { ... }`                     — scope-start only
        //   `@scope (.start) to (.end) { ... }`           — start + end
        //   `@scope { ... }`                              — implicit (no start/end)
        // Selector lists inside the parentheses are comma-separated. We collect
        // them as raw text and split inside ScopeRule consumers for tokenization
        // parity with @media/@container.
        static ScopeRule ParseScopeRule(ParserContext ctx, CssToken atTok) {
            var rule = new ScopeRule();
            ctx.SkipWhitespace();
            if (!ctx.IsEof() && ctx.Current().Kind == CssTokenKind.LParen) {
                ctx.Advance();
                string startList = ReadUntilUnnestedRParen(ctx);
                AddCommaList(rule.ScopeStartSelectors, startList);
            }
            ctx.SkipWhitespace();
            if (!ctx.IsEof() && ctx.Current().Kind == CssTokenKind.Ident
                && ctx.Current().Text.Equals("to", System.StringComparison.OrdinalIgnoreCase)) {
                ctx.Advance();
                ctx.SkipWhitespace();
                if (!ctx.IsEof() && ctx.Current().Kind == CssTokenKind.LParen) {
                    ctx.Advance();
                    string endList = ReadUntilUnnestedRParen(ctx);
                    AddCommaList(rule.ScopeEndSelectors, endList);
                }
                ctx.SkipWhitespace();
            }
            if (ctx.IsEof() || ctx.Current().Kind != CssTokenKind.LBrace) {
                if (ctx.Options.ThrowOnError)
                    throw new CssParseException("Expected '{' after @scope prelude", atTok.Line, atTok.Column);
                return null;
            }
            ctx.Advance();
            ctx.SkipWhitespace();
            while (!ctx.IsEof() && ctx.Current().Kind != CssTokenKind.RBrace) {
                var inner = ParseTopLevelRule(ctx);
                if (inner != null) rule.Rules.Add(inner);
                ctx.SkipWhitespace();
            }
            if (ctx.IsEof()) {
                if (ctx.Options.ThrowOnError)
                    throw new CssParseException("Unterminated @scope block", atTok.Line, atTok.Column);
                return rule;
            }
            ctx.Advance();
            return rule;
        }

        static string ReadUntilUnnestedRParen(ParserContext ctx) {
            var sb = new StringBuilder();
            int depth = 0;
            while (!ctx.IsEof()) {
                var t = ctx.Current();
                if (t.Kind == CssTokenKind.RParen) {
                    if (depth == 0) {
                        ctx.Advance();
                        break;
                    }
                    depth--;
                } else if (t.Kind == CssTokenKind.LParen || t.Kind == CssTokenKind.Function) {
                    depth++;
                }
                sb.Append(GetTokenSource(t));
                ctx.Advance();
            }
            return sb.ToString().Trim();
        }

        static void AddCommaList(List<string> output, string raw) {
            if (string.IsNullOrEmpty(raw)) return;
            int start = 0;
            int parenDepth = 0;
            for (int i = 0; i < raw.Length; i++) {
                char c = raw[i];
                if (c == '(') parenDepth++;
                else if (c == ')' && parenDepth > 0) parenDepth--;
                else if (c == ',' && parenDepth == 0) {
                    var seg = raw.Substring(start, i - start).Trim();
                    if (seg.Length > 0) output.Add(seg);
                    start = i + 1;
                }
            }
            var tail = raw.Substring(start).Trim();
            if (tail.Length > 0) output.Add(tail);
        }

        // CSS Cascade Module Level 5 §6.4.2.
        //   `@layer A, B, C;` — statement form, declares ordering only.
        //   `@layer A { ... }` — block form, named.
        //   `@layer { ... }` — block form, anonymous.
        //   `@layer A.B { ... }` — sub-layer name; v1 keeps the dotted path
        //                          as a single flat layer name.
        static LayerRule ParseLayerRule(ParserContext ctx, CssToken atTok) {
            string prelude = ReadPreludeText(ctx);
            var rule = new LayerRule();
            if (!ctx.IsEof() && ctx.Current().Kind == CssTokenKind.LBrace) {
                ctx.Advance();
                rule.IsBlock = true;
                string name = string.IsNullOrEmpty(prelude) ? null : prelude.Trim();
                rule.Names.Add(string.IsNullOrEmpty(name) ? null : name);
                ctx.SkipWhitespace();
                while (!ctx.IsEof() && ctx.Current().Kind != CssTokenKind.RBrace) {
                    var inner = ParseTopLevelRule(ctx);
                    if (inner != null) rule.Rules.Add(inner);
                    ctx.SkipWhitespace();
                }
                if (ctx.IsEof()) {
                    if (ctx.Options.ThrowOnError)
                        throw new CssParseException("Unterminated @layer block", atTok.Line, atTok.Column);
                    return rule;
                }
                ctx.Advance();
                return rule;
            }
            // Statement form: comma-separated names, terminated by `;`.
            rule.IsBlock = false;
            foreach (var n in SplitCommaList(prelude)) {
                rule.Names.Add(n);
            }
            if (!ctx.IsEof() && ctx.Current().Kind == CssTokenKind.Semicolon) {
                ctx.Advance();
            }
            return rule;
        }

        static List<string> SplitCommaList(string raw) {
            var result = new List<string>();
            if (string.IsNullOrEmpty(raw)) return result;
            int start = 0;
            for (int i = 0; i < raw.Length; i++) {
                if (raw[i] == ',') {
                    var seg = raw.Substring(start, i - start).Trim();
                    if (seg.Length > 0) result.Add(seg);
                    start = i + 1;
                }
            }
            var tail = raw.Substring(start).Trim();
            if (tail.Length > 0) result.Add(tail);
            return result;
        }

        // CSS Conditional 3 §3 — `@supports (cond) { rules… }`.
        // v1 stub: parse but never evaluate the condition; emit all inner
        // rules so authors writing feature-query fallbacks at least get
        // their CSS into the cascade. Mirrors ParseMediaRule's prelude /
        // brace handling exactly.
        static SupportsRule ParseSupportsRule(ParserContext ctx, CssToken atTok) {
            string condition = ReadPreludeText(ctx);
            if (ctx.IsEof() || ctx.Current().Kind != CssTokenKind.LBrace) {
                if (ctx.Options.ThrowOnError)
                    throw new CssParseException("Expected '{' after @supports prelude", atTok.Line, atTok.Column);
                return null;
            }
            ctx.Advance();
            var rule = new SupportsRule { ConditionText = condition };
            ctx.SkipWhitespace();
            while (!ctx.IsEof() && ctx.Current().Kind != CssTokenKind.RBrace) {
                var inner = ParseTopLevelRule(ctx);
                if (inner != null) rule.Rules.Add(inner);
                ctx.SkipWhitespace();
            }
            if (ctx.IsEof()) {
                if (ctx.Options.ThrowOnError)
                    throw new CssParseException("Unterminated @supports block", atTok.Line, atTok.Column);
                return rule;
            }
            ctx.Advance();
            return rule;
        }

        static MediaRule ParseMediaRule(ParserContext ctx, CssToken atTok) {
            string condition = ReadPreludeText(ctx);
            if (ctx.IsEof() || ctx.Current().Kind != CssTokenKind.LBrace) {
                if (ctx.Options.ThrowOnError)
                    throw new CssParseException("Expected '{' after @media prelude", atTok.Line, atTok.Column);
                return null;
            }
            ctx.Advance();
            var rule = new MediaRule { ConditionText = condition };
            ctx.SkipWhitespace();
            while (!ctx.IsEof() && ctx.Current().Kind != CssTokenKind.RBrace) {
                var inner = ParseTopLevelRule(ctx);
                if (inner != null) rule.Rules.Add(inner);
                ctx.SkipWhitespace();
            }
            if (ctx.IsEof()) {
                if (ctx.Options.ThrowOnError)
                    throw new CssParseException("Unterminated @media block", atTok.Line, atTok.Column);
                return rule;
            }
            ctx.Advance();
            return rule;
        }

        static ContainerRule ParseContainerRule(ParserContext ctx, CssToken atTok) {
            // The prelude carries an optional name plus the parenthesized condition;
            // ContainerQueryParser pulls them apart at cascade time so the parser stays
            // tokenizer-only here. We recover the name eagerly so the textual round-trip
            // is preserved on ContainerRule.Name independent of cascade-time parsing.
            string prelude = ReadPreludeText(ctx);
            string name = null;
            string condition = prelude;
            if (!string.IsNullOrEmpty(prelude)) {
                int parenStart = FindFirstTopLevelLParen(prelude);
                if (parenStart > 0) {
                    string headRaw = prelude.Substring(0, parenStart);
                    string head = headRaw.Trim();
                    // A container name must be whitespace-separated from the
                    // parenthesized condition (`@container sidebar (width > 0)`).
                    // When the ident is glued directly to '(' it is a FUNCTION
                    // query — `style(--foo: bar)` — not a name, so it must stay
                    // in the condition for ContainerQueryParser to recognise.
                    bool gluedToParen = headRaw.Length > 0
                        && !char.IsWhiteSpace(headRaw[headRaw.Length - 1]);
                    if (head.Length > 0 && IsBareIdent(head) && !gluedToParen) {
                        string lower = CssStringUtil.ToLowerInvariantOrSame(head);
                        if (lower != "not" && lower != "and" && lower != "or") {
                            name = head;
                            condition = prelude.Substring(parenStart).Trim();
                        }
                    }
                }
            }
            if (ctx.IsEof() || ctx.Current().Kind != CssTokenKind.LBrace) {
                if (ctx.Options.ThrowOnError)
                    throw new CssParseException("Expected '{' after @container prelude", atTok.Line, atTok.Column);
                return null;
            }
            ctx.Advance();
            var rule = new ContainerRule { Name = name, ConditionText = condition };
            ctx.SkipWhitespace();
            while (!ctx.IsEof() && ctx.Current().Kind != CssTokenKind.RBrace) {
                var inner = ParseTopLevelRule(ctx);
                if (inner != null) rule.Rules.Add(inner);
                ctx.SkipWhitespace();
            }
            if (ctx.IsEof()) {
                if (ctx.Options.ThrowOnError)
                    throw new CssParseException("Unterminated @container block", atTok.Line, atTok.Column);
                return rule;
            }
            ctx.Advance();
            return rule;
        }

        static int FindFirstTopLevelLParen(string s) {
            for (int i = 0; i < s.Length; i++) {
                if (s[i] == '(') return i;
            }
            return -1;
        }

        static bool IsBareIdent(string s) {
            if (string.IsNullOrEmpty(s)) return false;
            char c0 = s[0];
            if (!(char.IsLetter(c0) || c0 == '_' || c0 == '-')) return false;
            for (int i = 1; i < s.Length; i++) {
                char c = s[i];
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-')) return false;
            }
            return true;
        }

        static KeyframesRule ParseKeyframesRule(ParserContext ctx, CssToken atTok) {
            ctx.SkipWhitespace();
            string name = "";
            if (!ctx.IsEof()) {
                var nt = ctx.Current();
                if (nt.Kind == CssTokenKind.Ident || nt.Kind == CssTokenKind.String) {
                    name = nt.Text;
                    ctx.Advance();
                }
            }
            ctx.SkipWhitespace();
            if (ctx.IsEof() || ctx.Current().Kind != CssTokenKind.LBrace) {
                if (ctx.Options.ThrowOnError)
                    throw new CssParseException("Expected '{' after @keyframes name", atTok.Line, atTok.Column);
                return null;
            }
            ctx.Advance();
            var rule = new KeyframesRule { Name = name };
            ctx.SkipWhitespace();
            while (!ctx.IsEof() && ctx.Current().Kind != CssTokenKind.RBrace) {
                var block = ParseKeyframeBlock(ctx);
                if (block != null) rule.Blocks.Add(block);
                ctx.SkipWhitespace();
            }
            if (ctx.IsEof()) {
                if (ctx.Options.ThrowOnError)
                    throw new CssParseException("Unterminated @keyframes block", atTok.Line, atTok.Column);
                return rule;
            }
            ctx.Advance();
            return rule;
        }

        static KeyframeBlock ParseKeyframeBlock(ParserContext ctx) {
            var startTok = ctx.Current();
            string selector = ReadPreludeText(ctx);
            if (ctx.IsEof() || ctx.Current().Kind != CssTokenKind.LBrace) {
                if (ctx.Options.ThrowOnError)
                    throw new CssParseException("Expected '{' in keyframe block", startTok.Line, startTok.Column);
                SkipToNextRule(ctx);
                return null;
            }
            var braceTok = ctx.Current();
            ctx.Advance();
            var block = new KeyframeBlock { Selector = selector };
            ParseDeclarations(ctx, block.Declarations, braceTok);
            return block;
        }

        static ImportRule ParseImportRule(ParserContext ctx, CssToken atTok) {
            ctx.SkipWhitespace();
            string href = "";
            if (!ctx.IsEof()) {
                var t = ctx.Current();
                if (t.Kind == CssTokenKind.String) {
                    href = t.Text;
                    ctx.Advance();
                } else if (t.Kind == CssTokenKind.Url) {
                    href = t.Text;
                    ctx.Advance();
                } else if (t.Kind == CssTokenKind.Function
                           && string.Equals(t.Text, "url", System.StringComparison.OrdinalIgnoreCase)) {
                    // `url("...")` — the tokenizer rewinds and emits Function
                    // + String + RParen when the url body is quoted (see
                    // CssTokenizer.ConsumeUrl). The raw-form `url(no-quotes)`
                    // emits a single Url token above. Both reach this parser
                    // path; this branch handles the quoted form so authors
                    // can write `@import url("x.css")` interchangeably with
                    // `@import "x.css"` and `@import url(x.css)`.
                    ctx.Advance();
                    ctx.SkipWhitespace();
                    if (!ctx.IsEof() && ctx.Current().Kind == CssTokenKind.String) {
                        href = ctx.Current().Text;
                        ctx.Advance();
                        ctx.SkipWhitespace();
                        if (!ctx.IsEof() && ctx.Current().Kind == CssTokenKind.RParen) {
                            ctx.Advance();
                        }
                    } else {
                        if (ctx.Options.ThrowOnError)
                            throw new CssParseException("Expected string inside url() in @import", atTok.Line, atTok.Column);
                        SkipUntilSemicolonOrBrace(ctx);
                        return null;
                    }
                } else {
                    if (ctx.Options.ThrowOnError)
                        throw new CssParseException("Expected URL or string in @import", atTok.Line, atTok.Column);
                    SkipUntilSemicolonOrBrace(ctx);
                    return null;
                }
            }
            ctx.SkipWhitespace();
            bool hasLayer = false;
            string layerName = null;
            if (!ctx.IsEof()) {
                var lt = ctx.Current();
                if (lt.Kind == CssTokenKind.Function
                    && string.Equals(lt.Text, "layer", System.StringComparison.OrdinalIgnoreCase)) {
                    ctx.Advance();
                    var nameSb = new StringBuilder();
                    bool closed = false;
                    while (!ctx.IsEof()) {
                        var nt = ctx.Current();
                        if (nt.Kind == CssTokenKind.RParen) { ctx.Advance(); closed = true; break; }
                        if (nt.Kind == CssTokenKind.Semicolon
                            || nt.Kind == CssTokenKind.LBrace
                            || nt.Kind == CssTokenKind.RBrace) break;
                        nameSb.Append(GetTokenSource(nt));
                        ctx.Advance();
                    }
                    if (!closed) {
                        if (ctx.Options.ThrowOnError)
                            throw new CssParseException("Unterminated layer() in @import", atTok.Line, atTok.Column);
                        SkipUntilSemicolonOrBrace(ctx);
                        return null;
                    }
                    hasLayer = true;
                    layerName = nameSb.ToString().Trim();
                    if (layerName.Length == 0) layerName = null;
                } else if (lt.Kind == CssTokenKind.Ident
                           && string.Equals(lt.Text, "layer", System.StringComparison.OrdinalIgnoreCase)) {
                    ctx.Advance();
                    hasLayer = true;
                }
            }
            ctx.SkipWhitespace();
            bool hasSupports = false;
            string supportsText = null;
            if (!ctx.IsEof()) {
                var st = ctx.Current();
                if (st.Kind == CssTokenKind.Function
                    && string.Equals(st.Text, "supports", System.StringComparison.OrdinalIgnoreCase)) {
                    ctx.Advance();
                    var supSb = new StringBuilder();
                    int depth = 1;
                    bool closed = false;
                    while (!ctx.IsEof()) {
                        var nt = ctx.Current();
                        if (nt.Kind == CssTokenKind.LParen || nt.Kind == CssTokenKind.Function) {
                            depth++;
                            supSb.Append(GetTokenSource(nt));
                            ctx.Advance();
                            continue;
                        }
                        if (nt.Kind == CssTokenKind.RParen) {
                            depth--;
                            if (depth == 0) { ctx.Advance(); closed = true; break; }
                            supSb.Append(GetTokenSource(nt));
                            ctx.Advance();
                            continue;
                        }
                        if (nt.Kind == CssTokenKind.Semicolon
                            || nt.Kind == CssTokenKind.LBrace
                            || nt.Kind == CssTokenKind.RBrace) break;
                        supSb.Append(GetTokenSource(nt));
                        ctx.Advance();
                    }
                    if (!closed) {
                        if (ctx.Options.ThrowOnError)
                            throw new CssParseException("Unterminated supports() in @import", atTok.Line, atTok.Column);
                        SkipUntilSemicolonOrBrace(ctx);
                        return null;
                    }
                    hasSupports = true;
                    supportsText = supSb.ToString().Trim();
                    if (supportsText.Length == 0) supportsText = null;
                }
            }
            var sb = new StringBuilder();
            while (!ctx.IsEof()) {
                var t = ctx.Current();
                if (t.Kind == CssTokenKind.Semicolon) { ctx.Advance(); break; }
                if (t.Kind == CssTokenKind.LBrace || t.Kind == CssTokenKind.RBrace) break;
                sb.Append(GetTokenSource(t));
                ctx.Advance();
            }
            // Note: the imported sheet is resolved + spliced by AtImportLoader
            // at document-build time (not here). The parser is a pure syntactic
            // pass; we just record the href + media clause on the AST node.
            return new ImportRule {
                Href = href,
                MediaConditionText = sb.ToString().Trim(),
                HasLayer = hasLayer,
                Layer = layerName,
                HasSupports = hasSupports,
                SupportsText = supportsText
            };
        }

        static void SkipUnknownAtRule(ParserContext ctx) {
            int depth = 0;
            while (!ctx.IsEof()) {
                var t = ctx.Current();
                if (t.Kind == CssTokenKind.Semicolon && depth == 0) {
                    ctx.Advance();
                    return;
                }
                if (t.Kind == CssTokenKind.LBrace) {
                    depth++;
                    ctx.Advance();
                    continue;
                }
                if (t.Kind == CssTokenKind.RBrace) {
                    if (depth == 0) return;
                    depth--;
                    ctx.Advance();
                    if (depth == 0) return;
                    continue;
                }
                ctx.Advance();
            }
        }

        static StyleRule ParseStyleRule(ParserContext ctx) {
            if (!EnterRule(ctx)) return null; // PH1 — see EnterRule
            try {
                return ParseStyleRuleInner(ctx);
            } finally {
                ctx.RuleDepth--;
            }
        }

        static StyleRule ParseStyleRuleInner(ParserContext ctx) {
            var startTok = ctx.Current();
            var selectors = ReadSelectorList(ctx);
            if (ctx.IsEof() || ctx.Current().Kind != CssTokenKind.LBrace) {
                if (ctx.Options.ThrowOnError)
                    throw new CssParseException("Expected '{' after selector list", startTok.Line, startTok.Column);
                SkipToNextRule(ctx);
                return null;
            }
            var braceTok = ctx.Current();
            ctx.Advance();
            var rule = new StyleRule { Selectors = selectors };
            ParseRuleBody(ctx, rule, braceTok);
            return rule;
        }

        // CSS Nesting Module: a style-rule body is a mix of declarations and
        // nested rules. We dispatch by peeking at the first non-whitespace
        // token of each entry — if it looks like the start of a selector
        // (starts with `&`, structural punctuation, or an ident NOT followed by
        // a colon-valued declaration) we parse it as a nested rule. Otherwise
        // we attempt a declaration. Nested at-rules (`@media`, `@container`)
        // also land here.
        static void ParseRuleBody(ParserContext ctx, StyleRule parent, CssToken openTok) {
            ctx.SkipWhitespace();
            while (!ctx.IsEof() && ctx.Current().Kind != CssTokenKind.RBrace) {
                var declStart = ctx.Current();
                if (declStart.Kind == CssTokenKind.AtKeyword) {
                    var inner = ParseAtRule(ctx);
                    if (inner != null) parent.NestedRules.Add(inner);
                    ctx.SkipWhitespace();
                    continue;
                }
                if (LooksLikeNestedSelector(ctx)) {
                    var nested = ParseStyleRule(ctx);
                    if (nested != null) parent.NestedRules.Add(nested);
                    ctx.SkipWhitespace();
                    continue;
                }
                bool ok = TryParseDeclaration(ctx, parent.Declarations);
                if (!ok) {
                    if (ctx.Options.ThrowOnError) {
                        throw new CssParseException("Invalid declaration", declStart.Line, declStart.Column);
                    }
                    SkipDeclaration(ctx);
                }
                ctx.SkipWhitespace();
            }
            if (ctx.IsEof()) {
                if (ctx.Options.ThrowOnError)
                    throw new CssParseException("Unterminated declaration block", openTok.Line, openTok.Column);
                return;
            }
            ctx.Advance();
        }

        // Lookahead: does the current token sequence form a nested rule (selector
        // + `{` + body) or a declaration (ident + `:` + value + `;|}`)?
        // Per the CSS Nesting spec, `&` always starts a selector. A bare ident
        // followed by `:` is a declaration; a bare ident followed by other
        // selector punctuation is a nested rule.
        static bool LooksLikeNestedSelector(ParserContext ctx) {
            var t = ctx.Current();
            // `&` only starts a nested selector.
            if (t.Kind == CssTokenKind.Delim && t.Text == "&") return true;
            // Structural selector punctuation always indicates a selector.
            if (t.Kind == CssTokenKind.Hash) return true;
            if (t.Kind == CssTokenKind.LBracket) return true;
            if (t.Kind == CssTokenKind.Colon) return true;
            if (t.Kind == CssTokenKind.Delim) {
                if (t.Text == "." || t.Text == "*" || t.Text == ">" || t.Text == "+" || t.Text == "~") return true;
            }
            // Ident: peek beyond any whitespace for the next non-whitespace token.
            // If it's `:` followed by an ident OR a value-token, it's a declaration.
            // Otherwise (selector punctuation, comma, or `{`), it's a nested rule.
            if (t.Kind == CssTokenKind.Ident) {
                int peek = ctx.Index + 1;
                while (peek < ctx.Tokens.Count && ctx.Tokens[peek].Kind == CssTokenKind.Whitespace) peek++;
                if (peek >= ctx.Tokens.Count) return false;
                var next = ctx.Tokens[peek];
                if (next.Kind == CssTokenKind.Colon) {
                    // `ident:` could be a declaration OR `tag:hover` style nested
                    // selector. Distinguish by checking what follows the colon.
                    // If the next ident is a known pseudo (handled elsewhere) or a
                    // colon (for `::`), we treat as a nested selector. Simpler
                    // heuristic: if the second non-ws token after the colon is
                    // `{`, `,`, or another selector punctuation, it's a nested
                    // rule; otherwise a declaration.
                    int p2 = peek + 1;
                    if (p2 < ctx.Tokens.Count && ctx.Tokens[p2].Kind == CssTokenKind.Colon) {
                        // `::` — pseudo-element; nested selector.
                        return true;
                    }
                    // Walk to first `{`, `}`, or `;` at top level.
                    int depth = 0;
                    for (int i = p2; i < ctx.Tokens.Count; i++) {
                        var tk = ctx.Tokens[i];
                        if (tk.Kind == CssTokenKind.LParen || tk.Kind == CssTokenKind.Function) depth++;
                        else if (tk.Kind == CssTokenKind.RParen) depth--;
                        else if (depth == 0) {
                            if (tk.Kind == CssTokenKind.LBrace) return true;
                            if (tk.Kind == CssTokenKind.Semicolon) return false;
                            if (tk.Kind == CssTokenKind.RBrace) return false;
                            if (tk.Kind == CssTokenKind.Eof) return false;
                        }
                    }
                    return false;
                }
                if (next.Kind == CssTokenKind.Delim
                    && (next.Text == "." || next.Text == ">" || next.Text == "+" || next.Text == "~")) return true;
                if (next.Kind == CssTokenKind.Hash || next.Kind == CssTokenKind.LBracket || next.Kind == CssTokenKind.Comma) return true;
                if (next.Kind == CssTokenKind.LBrace) return true;
                if (next.Kind == CssTokenKind.Ident) {
                    // Two idents in a row (descendant combinator).
                    return true;
                }
            }
            return false;
        }

        static List<string> ReadSelectorList(ParserContext ctx) {
            var result = new List<string>();
            var sb = new StringBuilder();
            int parenDepth = 0;
            int bracketDepth = 0;
            while (!ctx.IsEof()) {
                var t = ctx.Current();
                if (t.Kind == CssTokenKind.LBrace && parenDepth == 0 && bracketDepth == 0) break;
                if (t.Kind == CssTokenKind.RBrace && parenDepth == 0 && bracketDepth == 0) break;
                if (t.Kind == CssTokenKind.Semicolon && parenDepth == 0 && bracketDepth == 0) break;
                if (t.Kind == CssTokenKind.Comma && parenDepth == 0 && bracketDepth == 0) {
                    AddSelector(result, sb);
                    sb.Clear();
                    ctx.Advance();
                    continue;
                }
                if (t.Kind == CssTokenKind.LParen || t.Kind == CssTokenKind.Function) parenDepth++;
                if (t.Kind == CssTokenKind.RParen) parenDepth--;
                if (t.Kind == CssTokenKind.LBracket) bracketDepth++;
                if (t.Kind == CssTokenKind.RBracket) bracketDepth--;
                sb.Append(GetTokenSource(t));
                ctx.Advance();
            }
            AddSelector(result, sb);
            return result;
        }

        static void AddSelector(List<string> list, StringBuilder sb) {
            string s = sb.ToString().Trim();
            if (s.Length > 0) list.Add(s);
        }

        static string ReadPreludeText(ParserContext ctx) {
            var sb = new StringBuilder();
            int parenDepth = 0;
            while (!ctx.IsEof()) {
                var t = ctx.Current();
                if (t.Kind == CssTokenKind.LBrace && parenDepth == 0) break;
                if (t.Kind == CssTokenKind.RBrace && parenDepth == 0) break;
                if (t.Kind == CssTokenKind.Semicolon && parenDepth == 0) break;
                if (t.Kind == CssTokenKind.LParen || t.Kind == CssTokenKind.Function) parenDepth++;
                if (t.Kind == CssTokenKind.RParen) parenDepth--;
                sb.Append(GetTokenSource(t));
                ctx.Advance();
            }
            return sb.ToString().Trim();
        }

        static void ParseDeclarations(ParserContext ctx, List<Declaration> output, CssToken openTok) {
            ctx.SkipWhitespace();
            while (!ctx.IsEof() && ctx.Current().Kind != CssTokenKind.RBrace) {
                var declStart = ctx.Current();
                bool ok = TryParseDeclaration(ctx, output);
                if (!ok) {
                    if (ctx.Options.ThrowOnError) {
                        throw new CssParseException("Invalid declaration", declStart.Line, declStart.Column);
                    }
                    SkipDeclaration(ctx);
                }
                ctx.SkipWhitespace();
            }
            if (ctx.IsEof()) {
                if (ctx.Options.ThrowOnError)
                    throw new CssParseException("Unterminated declaration block", openTok.Line, openTok.Column);
                return;
            }
            ctx.Advance();
        }

        static bool TryParseDeclaration(ParserContext ctx, List<Declaration> output) {
            ctx.SkipWhitespace();
            if (ctx.IsEof()) return false;
            var nameTok = ctx.Current();
            if (nameTok.Kind != CssTokenKind.Ident) {
                return false;
            }
            string property = CssStringUtil.ToLowerInvariantOrSame(nameTok.Text);
            ctx.Advance();
            ctx.SkipWhitespace();
            if (ctx.IsEof() || ctx.Current().Kind != CssTokenKind.Colon) {
                return false;
            }
            ctx.Advance();

            var sb = new StringBuilder();
            int parenDepth = 0;
            int bracketDepth = 0;
            bool sawNonWs = false;
            while (!ctx.IsEof()) {
                var t = ctx.Current();
                if (t.Kind == CssTokenKind.Semicolon && parenDepth == 0 && bracketDepth == 0) break;
                if (t.Kind == CssTokenKind.RBrace && parenDepth == 0 && bracketDepth == 0) break;
                if (t.Kind == CssTokenKind.LBrace && parenDepth == 0 && bracketDepth == 0) {
                    return false;
                }
                if (t.Kind == CssTokenKind.LParen || t.Kind == CssTokenKind.Function) parenDepth++;
                if (t.Kind == CssTokenKind.RParen) parenDepth--;
                if (t.Kind == CssTokenKind.LBracket) bracketDepth++;
                if (t.Kind == CssTokenKind.RBracket) bracketDepth--;
                if (t.Kind != CssTokenKind.Whitespace) sawNonWs = true;
                sb.Append(GetTokenSource(t));
                ctx.Advance();
            }
            if (!ctx.IsEof() && ctx.Current().Kind == CssTokenKind.Semicolon) {
                ctx.Advance();
            }

            string raw = sb.ToString().Trim();
            bool important = StripImportant(ref raw);
            if (!sawNonWs) {
                return false;
            }
            output.Add(new Declaration {
                Property = property,
                ValueText = raw,
                Important = important
            });
            return true;
        }

        static bool StripImportant(ref string value) {
            int bang = FindTopLevelImportantBang(value);
            if (bang < 0) return false;
            string after = value.Substring(bang + 1).Trim();
            if (!after.Equals("important", System.StringComparison.OrdinalIgnoreCase)) return false;
            value = value.Substring(0, bang).TrimEnd();
            return true;
        }

        static int FindTopLevelImportantBang(string value) {
            int parenDepth = 0;
            int bracketDepth = 0;
            char strQuote = '\0';
            int candidate = -1;
            for (int i = 0; i < value.Length; i++) {
                char c = value[i];
                if (strQuote != '\0') {
                    if (c == '\\' && i + 1 < value.Length) { i++; continue; }
                    if (c == strQuote) strQuote = '\0';
                    continue;
                }
                if (c == '"' || c == '\'') { strQuote = c; continue; }
                if (c == '(') parenDepth++;
                else if (c == ')' && parenDepth > 0) parenDepth--;
                else if (c == '[') bracketDepth++;
                else if (c == ']' && bracketDepth > 0) bracketDepth--;
                else if (c == '!' && parenDepth == 0 && bracketDepth == 0) {
                    candidate = i;
                }
            }
            return candidate;
        }

        static void SkipDeclaration(ParserContext ctx) {
            int parenDepth = 0;
            int braceDepth = 0;
            while (!ctx.IsEof()) {
                var t = ctx.Current();
                if (t.Kind == CssTokenKind.Semicolon && parenDepth == 0 && braceDepth == 0) {
                    ctx.Advance();
                    return;
                }
                if (t.Kind == CssTokenKind.RBrace && parenDepth == 0 && braceDepth == 0) {
                    return;
                }
                if (t.Kind == CssTokenKind.LBrace) braceDepth++;
                if (t.Kind == CssTokenKind.RBrace && braceDepth > 0) braceDepth--;
                if (t.Kind == CssTokenKind.LParen || t.Kind == CssTokenKind.Function) parenDepth++;
                if (t.Kind == CssTokenKind.RParen && parenDepth > 0) parenDepth--;
                ctx.Advance();
            }
        }

        static void SkipToNextRule(ParserContext ctx) {
            int depth = 0;
            while (!ctx.IsEof()) {
                var t = ctx.Current();
                if (t.Kind == CssTokenKind.LBrace) {
                    depth++;
                    ctx.Advance();
                    continue;
                }
                if (t.Kind == CssTokenKind.RBrace) {
                    ctx.Advance();
                    if (depth == 0) return;
                    depth--;
                    if (depth == 0) return;
                    continue;
                }
                if (t.Kind == CssTokenKind.Semicolon && depth == 0) {
                    ctx.Advance();
                    return;
                }
                ctx.Advance();
            }
        }

        static void SkipUntilSemicolonOrBrace(ParserContext ctx) {
            while (!ctx.IsEof()) {
                var t = ctx.Current();
                if (t.Kind == CssTokenKind.Semicolon) { ctx.Advance(); return; }
                if (t.Kind == CssTokenKind.LBrace || t.Kind == CssTokenKind.RBrace) return;
                ctx.Advance();
            }
        }

        static string GetTokenSource(CssToken t) {
            switch (t.Kind) {
                case CssTokenKind.Whitespace: return " ";
                case CssTokenKind.Ident: return t.Text;
                case CssTokenKind.Function: return t.Text + "(";
                case CssTokenKind.AtKeyword: return "@" + t.Text;
                case CssTokenKind.Hash: return "#" + t.Text;
                case CssTokenKind.String: return "\"" + EscapeString(t.Text) + "\"";
                case CssTokenKind.Number: return t.Text;
                case CssTokenKind.Percentage: return t.Text;
                case CssTokenKind.Dimension: return t.Text;
                case CssTokenKind.Url: return "url(" + t.Text + ")";
                case CssTokenKind.Delim: return t.Text;
                case CssTokenKind.Comma: return ",";
                case CssTokenKind.Colon: return ":";
                case CssTokenKind.Semicolon: return ";";
                case CssTokenKind.LBrace: return "{";
                case CssTokenKind.RBrace: return "}";
                case CssTokenKind.LParen: return "(";
                case CssTokenKind.RParen: return ")";
                case CssTokenKind.LBracket: return "[";
                case CssTokenKind.RBracket: return "]";
                default: return "";
            }
        }

        static string EscapeString(string s) {
            var sb = new StringBuilder(s.Length);
            foreach (var c in s) {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else sb.Append(c);
            }
            return sb.ToString();
        }

        sealed class ParserContext {
            public readonly List<CssToken> Tokens;
            public readonly ParseOptions Options;
            // PH1: shared nesting-depth counter for the rule recursion
            // (ParseAtRule / ParseStyleRule / ParseRuleBody). See EnterRule.
            public int RuleDepth;
            int index;

            public ParserContext(List<CssToken> tokens, ParseOptions options) {
                Tokens = tokens;
                Options = options;
            }

            public int Index => index;

            public CssToken Current() => Tokens[index];
            public void Advance() { if (index < Tokens.Count - 1) index++; }
            public bool IsEof() => Tokens[index].Kind == CssTokenKind.Eof;

            public void SkipWhitespace() {
                while (Tokens[index].Kind == CssTokenKind.Whitespace) {
                    if (index >= Tokens.Count - 1) break;
                    index++;
                }
            }
        }
    }
}
