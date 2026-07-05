using System;
using System.Collections.Generic;
using System.Globalization;
using Weva.Css;
using Weva.Css.Values;

namespace Weva.Paint.Filters {
    public static class FilterParser {
        // Default drop-shadow color when omitted: black. Callers that want currentColor
        // override via FilterResolver, which knows the style.
        public static FilterChain Parse(string text, LengthContext lengthCtx) {
            return Parse(text, lengthCtx, LinearColor.Black);
        }

        public static FilterChain Parse(string text, LengthContext lengthCtx, LinearColor currentColor) {
            if (string.IsNullOrWhiteSpace(text)) return FilterChain.Empty;
            string trimmed = text.Trim();
            if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase)) return FilterChain.Empty;

            List<CssToken> tokens;
            try {
                tokens = new CssTokenizer(trimmed).Tokenize();
            } catch (CssParseException ex) {
                throw new FilterParseException("Filter tokenization failed: " + ex.Message, ex);
            }

            var reader = new TokenReader(tokens);
            var functions = new List<FilterFunction>();
            reader.SkipWhitespace();
            while (!reader.AtEnd()) {
                var t = reader.Peek();
                // SVG-referenced filters (url(#id)) are out of v1 scope. Skip them
                // gracefully so sibling functions in the same declaration still apply.
                if (t.Kind == CssTokenKind.Url) {
                    reader.Advance();
                    reader.SkipWhitespace();
                    continue;
                }
                if (t.Kind != CssTokenKind.Function) {
                    throw new FilterParseException("Expected filter function, got '" + (t.Text ?? t.Kind.ToString()) + "'");
                }
                string name = CssStringUtil.ToLowerInvariantOrSame(t.Text);
                reader.Advance();
                var argTokens = ReadFunctionArgs(ref reader, name);
                if (name == "url") {
                    reader.SkipWhitespace();
                    continue;
                }
                FilterFunction fn = DispatchFunction(name, argTokens, lengthCtx, currentColor);
                functions.Add(fn);
                reader.SkipWhitespace();
            }

            if (functions.Count == 0) return FilterChain.Empty;
            return new FilterChain(functions);
        }

        static List<CssToken> ReadFunctionArgs(ref TokenReader reader, string fnName) {
            var args = new List<CssToken>();
            int depth = 1;
            while (!reader.AtEnd()) {
                var t = reader.Peek();
                if (t.Kind == CssTokenKind.Function || t.Kind == CssTokenKind.LParen) {
                    depth++;
                    args.Add(t);
                    reader.Advance();
                    continue;
                }
                if (t.Kind == CssTokenKind.RParen) {
                    depth--;
                    reader.Advance();
                    if (depth == 0) return args;
                    args.Add(t);
                    continue;
                }
                args.Add(t);
                reader.Advance();
            }
            throw new FilterParseException("Unterminated function '" + fnName + "(...)'");
        }

        static FilterFunction DispatchFunction(string name, List<CssToken> args, LengthContext ctx, LinearColor currentColor) {
            switch (name) {
                case "blur": return ParseBlur(args, ctx);
                case "brightness": return new BrightnessFilter(ParseAmount(args, "brightness"));
                case "contrast": return new ContrastFilter(ParseAmount(args, "contrast"));
                case "grayscale": return new GrayscaleFilter(ParseAmount(args, "grayscale"));
                case "opacity": return new OpacityFilter(ParseAmount(args, "opacity"));
                case "saturate": return new SaturateFilter(ParseAmount(args, "saturate"));
                case "hue-rotate": return new HueRotateFilter(ParseAngle(args, "hue-rotate"));
                case "invert": return new InvertFilter(ParseAmount(args, "invert"));
                case "sepia": return new SepiaFilter(ParseAmount(args, "sepia"));
                case "drop-shadow": return ParseDropShadow(args, ctx, currentColor);
            }
            throw new FilterParseException("Unknown filter function '" + name + "'");
        }

        static BlurFilter ParseBlur(List<CssToken> args, LengthContext ctx) {
            var pruned = StripWhitespace(args);
            if (pruned.Count != 1) {
                throw new FilterParseException("blur() requires exactly one length argument");
            }
            var t = pruned[0];
            if (t.Kind == CssTokenKind.Dimension) {
                if (!CssLength.TryParseUnit(CssStringUtil.ToLowerInvariantOrSame(t.Unit), out var unit)) {
                    throw new FilterParseException("blur(): unknown length unit '" + t.Unit + "'");
                }
                double px = new CssLength(t.Number, unit).ToPixels(ctx);
                return new BlurFilter(px);
            }
            // Per spec, blur requires a length; a unitless 0 is sometimes accepted.
            if (t.Kind == CssTokenKind.Number && t.Number == 0) {
                return new BlurFilter(0);
            }
            throw new FilterParseException("blur() requires a <length> argument (e.g. blur(5px))");
        }

        static double ParseAmount(List<CssToken> args, string fnName) {
            var pruned = StripWhitespace(args);
            if (pruned.Count != 1) {
                throw new FilterParseException(fnName + "() requires exactly one number or percentage");
            }
            var t = pruned[0];
            if (t.Kind == CssTokenKind.Number) return t.Number;
            if (t.Kind == CssTokenKind.Percentage) return t.Number * 0.01;
            throw new FilterParseException(fnName + "() requires a <number> or <percentage> (got '" + (t.Text ?? "") + "')");
        }

        static double ParseAngle(List<CssToken> args, string fnName) {
            var pruned = StripWhitespace(args);
            if (pruned.Count != 1) {
                throw new FilterParseException(fnName + "() requires exactly one angle argument");
            }
            var t = pruned[0];
            if (t.Kind == CssTokenKind.Number) {
                // Per modern spec, unit-less number is treated as degrees.
                return t.Number;
            }
            if (t.Kind == CssTokenKind.Dimension) {
                string u = CssStringUtil.ToLowerInvariantOrSame(t.Unit);
                switch (u) {
                    case "deg": return t.Number;
                    case "turn": return t.Number * 360.0;
                    case "rad": return t.Number * (180.0 / Math.PI);
                    case "grad": return t.Number * 0.9;
                }
                throw new FilterParseException(fnName + "(): unknown angle unit '" + t.Unit + "'");
            }
            throw new FilterParseException(fnName + "() requires an <angle>");
        }

        static DropShadowFilter ParseDropShadow(List<CssToken> args, LengthContext ctx, LinearColor currentColor) {
            // drop-shadow(<length> <length> [<length>] [<color>])
            // We walk tokens left-to-right, collecting up to 3 lengths and a color.
            double? offX = null, offY = null, blur = null;
            LinearColor color = currentColor;
            bool sawColor = false;

            int i = 0;
            while (i < args.Count) {
                var t = args[i];
                if (t.Kind == CssTokenKind.Whitespace) { i++; continue; }
                if (TryConsumeLength(args, ref i, ctx, out var px)) {
                    if (offX == null) offX = px;
                    else if (offY == null) offY = px;
                    else if (blur == null) blur = px;
                    else throw new FilterParseException("drop-shadow(): too many length arguments");
                    continue;
                }
                if (TryConsumeColor(args, ref i, currentColor, out var c)) {
                    if (sawColor) throw new FilterParseException("drop-shadow(): multiple colors");
                    color = c;
                    sawColor = true;
                    continue;
                }
                throw new FilterParseException("drop-shadow(): unexpected token '" + (t.Text ?? t.Kind.ToString()) + "'");
            }

            if (offX == null || offY == null) {
                throw new FilterParseException("drop-shadow() requires at least <offset-x> and <offset-y>");
            }
            return new DropShadowFilter(offX.Value, offY.Value, blur ?? 0.0, color);
        }

        static bool TryConsumeLength(List<CssToken> args, ref int i, LengthContext ctx, out double pixels) {
            pixels = 0;
            var t = args[i];
            if (t.Kind == CssTokenKind.Dimension) {
                if (!CssLength.TryParseUnit(CssStringUtil.ToLowerInvariantOrSame(t.Unit), out var unit)) return false;
                pixels = new CssLength(t.Number, unit).ToPixels(ctx);
                i++;
                return true;
            }
            if (t.Kind == CssTokenKind.Number) {
                // CSS strictly requires units on lengths except for 0; we accept 0 unitless.
                if (t.Number == 0) {
                    pixels = 0;
                    i++;
                    return true;
                }
            }
            return false;
        }

        static bool TryConsumeColor(List<CssToken> args, ref int i, LinearColor currentColor, out LinearColor color) {
            color = currentColor;
            var t = args[i];
            if (t.Kind == CssTokenKind.Hash) {
                var c = CssColor.FromHex(t.Text, t.Column);
                color = LinearColor.FromCssColor(c);
                i++;
                return true;
            }
            if (t.Kind == CssTokenKind.Ident) {
                string lower = CssStringUtil.ToLowerInvariantOrSame(t.Text);
                if (lower == "currentcolor") {
                    color = currentColor;
                    i++;
                    return true;
                }
                if (lower == "transparent") {
                    color = LinearColor.Transparent;
                    i++;
                    return true;
                }
                if (CssColor.TryFromName(lower, out var named)) {
                    color = LinearColor.FromCssColor(named);
                    i++;
                    return true;
                }
                return false;
            }
            if (t.Kind == CssTokenKind.Function) {
                string fname = CssStringUtil.ToLowerInvariantOrSame(t.Text);
                if (fname == "rgb" || fname == "rgba" || fname == "hsl" || fname == "hsla") {
                    // Reconstruct the function-call slice and parse via CssValueParser.
                    int start = i;
                    int depth = 1;
                    i++;
                    while (i < args.Count && depth > 0) {
                        var tt = args[i];
                        if (tt.Kind == CssTokenKind.Function || tt.Kind == CssTokenKind.LParen) depth++;
                        else if (tt.Kind == CssTokenKind.RParen) depth--;
                        i++;
                        if (depth == 0) break;
                    }
                    if (depth != 0) throw new FilterParseException("drop-shadow(): unterminated color function");
                    string raw = ReconstructText(args, start, i);
                    if (!CssValue.TryParse(raw, out var v)) {
                        throw new FilterParseException("drop-shadow(): could not parse color '" + raw + "'");
                    }
                    if (v is CssColor cc) {
                        color = LinearColor.FromCssColor(cc);
                        return true;
                    }
                    throw new FilterParseException("drop-shadow(): expected color, got '" + raw + "'");
                }
                return false;
            }
            return false;
        }

        static string ReconstructText(List<CssToken> args, int start, int endExclusive) {
            var sb = new System.Text.StringBuilder();
            for (int k = start; k < endExclusive; k++) {
                var t = args[k];
                switch (t.Kind) {
                    case CssTokenKind.Function: sb.Append(t.Text).Append('('); break;
                    case CssTokenKind.LParen: sb.Append('('); break;
                    case CssTokenKind.RParen: sb.Append(')'); break;
                    case CssTokenKind.Comma: sb.Append(','); break;
                    case CssTokenKind.Whitespace: sb.Append(' '); break;
                    case CssTokenKind.Number: sb.Append(t.Text ?? t.Number.ToString("R", CultureInfo.InvariantCulture)); break;
                    case CssTokenKind.Percentage: sb.Append(t.Text ?? (t.Number.ToString("R", CultureInfo.InvariantCulture) + "%")); break;
                    case CssTokenKind.Dimension: sb.Append(t.Text ?? (t.Number.ToString("R", CultureInfo.InvariantCulture) + (t.Unit ?? ""))); break;
                    case CssTokenKind.Hash: sb.Append('#').Append(t.Text); break;
                    case CssTokenKind.Ident: sb.Append(t.Text); break;
                    default: sb.Append(t.Text ?? ""); break;
                }
            }
            return sb.ToString();
        }

        static List<CssToken> StripWhitespace(List<CssToken> tokens) {
            var list = new List<CssToken>();
            foreach (var t in tokens) {
                if (t.Kind != CssTokenKind.Whitespace) list.Add(t);
            }
            return list;
        }

        struct TokenReader {
            readonly List<CssToken> tokens;
            int pos;
            public TokenReader(List<CssToken> t) { tokens = t; pos = 0; }
            public bool AtEnd() {
                return pos >= tokens.Count || tokens[pos].Kind == CssTokenKind.Eof;
            }
            public CssToken Peek() => tokens[pos];
            public void Advance() { pos++; }
            public void SkipWhitespace() {
                while (pos < tokens.Count && tokens[pos].Kind == CssTokenKind.Whitespace) pos++;
            }
        }
    }
}
