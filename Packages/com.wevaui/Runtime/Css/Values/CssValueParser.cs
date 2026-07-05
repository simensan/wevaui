using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Weva.Css.Values {
    public static class CssValueParser {
        public static CssValue Parse(string text) {
            if (text == null) text = "";
            // Per-pass parse cache: layout parses the same property value text
            // (e.g. "0", "auto", "16px", "1em") thousands of times. Inside a
            // CssValuePoolScope the result of every Parse() is memoized so the
            // second hit on the same string skips the tokenizer + parse-tree
            // allocations entirely.
            if (CssValuePool.TryGetCachedParse(text, out var cached)) {
                return cached;
            }
            var tokens = new CssTokenizer(text).Tokenize();
            var reader = new TokenReader(tokens, text);
            var value = ParseTopLevel(ref reader, text);
            reader.SkipWhitespace();
            if (!reader.AtEnd()) {
                var t = reader.Peek();
                throw new CssValueParseException("Unexpected token after value: '" + (t.Text ?? "") + "'", t.Column);
            }
            CssValuePool.CachePutParsed(text, value);
            return value;
        }

        static CssValue ParseTopLevel(ref TokenReader reader, string source) {
            var commaSegments = new List<List<CssValue>>();
            var current = new List<CssValue>();

            reader.SkipWhitespace();
            while (!reader.AtEnd()) {
                var t = reader.Peek();
                if (t.Kind == CssTokenKind.Comma) {
                    reader.Advance();
                    reader.SkipWhitespace();
                    commaSegments.Add(current);
                    current = new List<CssValue>();
                    continue;
                }
                var v = ParseSingle(ref reader);
                if (v != null) {
                    current.Add(v);
                }
                reader.SkipWhitespace();
            }
            commaSegments.Add(current);

            if (commaSegments.Count == 1) {
                var only = commaSegments[0];
                if (only.Count == 0) {
                    throw new CssValueParseException("Empty value", 1);
                }
                if (only.Count == 1) return only[0];
                return new CssValueList(only, CssValueListSeparator.Space);
            }

            var items = new List<CssValue>();
            foreach (var seg in commaSegments) {
                if (seg.Count == 0) {
                    throw new CssValueParseException("Empty value between commas", 1);
                }
                if (seg.Count == 1) items.Add(seg[0]);
                else items.Add(new CssValueList(seg, CssValueListSeparator.Space));
            }
            return new CssValueList(items, CssValueListSeparator.Comma);
        }

        static CssValue ParseSingle(ref TokenReader reader) {
            var t = reader.Peek();
            switch (t.Kind) {
                case CssTokenKind.Ident: {
                    reader.Advance();
                    string lower = CssStringUtil.ToLowerInvariantOrSame(t.Text);
                    if (lower == "currentcolor") {
                        return new CssKeyword("currentcolor", t.Text);
                    }
                    if (CssColor.TryFromName(lower, out var named)) {
                        return new CssColor(named.R, named.G, named.B, named.A, t.Text);
                    }
                    return new CssKeyword(t.Text);
                }
                case CssTokenKind.Number: {
                    reader.Advance();
                    return CssValuePool.RentNumber(t.Number, t.Text);
                }
                case CssTokenKind.Dimension: {
                    reader.Advance();
                    string unitLower = CssStringUtil.ToLowerInvariantOrSame(t.Unit);
                    // CSS Values 4 §6.1: angle dimensions live in their own
                    // type rather than the length bucket so length consumers
                    // don't have to special-case unit categories.
                    if (CssAngle.TryParseUnit(unitLower, out var angleUnit)) {
                        return new CssAngle(t.Number, angleUnit, t.Text);
                    }
                    if (!CssLength.TryParseUnit(unitLower, out var unit)) {
                        throw new CssValueParseException("Unknown length unit '" + t.Unit + "'", t.Column);
                    }
                    return CssValuePool.RentLength(t.Number, unit, t.Text);
                }
                case CssTokenKind.Percentage: {
                    reader.Advance();
                    return CssValuePool.RentPercentage(t.Number, t.Text);
                }
                case CssTokenKind.Hash: {
                    reader.Advance();
                    return CssColor.FromHex(t.Text, t.Column);
                }
                case CssTokenKind.String: {
                    reader.Advance();
                    char quote = reader.OriginalQuoteAt(t);
                    return new CssString(t.Text, quote);
                }
                case CssTokenKind.Url: {
                    reader.Advance();
                    return new CssUrl(t.Text);
                }
                case CssTokenKind.Function: {
                    return ParseFunction(ref reader);
                }
                case CssTokenKind.Delim: {
                    reader.Advance();
                    return new CssIdentifier(t.Text, t.Text);
                }
                case CssTokenKind.LParen: {
                    return ParseParenGroup(ref reader);
                }
                default:
                    throw new CssValueParseException("Unexpected token '" + (t.Text ?? t.Kind.ToString()) + "'", t.Column);
            }
        }

        static CssValue ParseParenGroup(ref TokenReader reader) {
            var open = reader.Peek();
            reader.Advance();
            var items = new List<CssValue>();
            reader.SkipWhitespace();
            while (!reader.AtEnd() && reader.Peek().Kind != CssTokenKind.RParen) {
                if (reader.Peek().Kind == CssTokenKind.Comma) {
                    reader.Advance();
                    reader.SkipWhitespace();
                    continue;
                }
                var v = ParseSingle(ref reader);
                if (v != null) items.Add(v);
                reader.SkipWhitespace();
            }
            if (reader.AtEnd()) throw new CssValueParseException("Unmatched '(' — missing ')'", open.Column);
            reader.Advance();
            return new CssFunctionCall("", items);
        }

        static CssValue ParseFunction(ref TokenReader reader) {
            var fnTok = reader.Peek();
            string name = fnTok.Text;
            string lower = CssStringUtil.ToLowerInvariantOrSame(name);
            reader.Advance();

            if (lower == "var") {
                return ParseVar(ref reader, fnTok.Column);
            }
            if (lower == "calc") {
                return ParseCalc(ref reader, fnTok.Column);
            }
            if (IsMathFunctionName(lower)) {
                return ParseMathFunction(ref reader, lower, fnTok.Column);
            }
            if (lower == "rgb" || lower == "rgba") {
                return ParseRgb(ref reader, lower, fnTok.Column);
            }
            if (lower == "hsl" || lower == "hsla") {
                return ParseHsl(ref reader, lower, fnTok.Column);
            }
            if (lower == "hwb") {
                return ParseHwb(ref reader, fnTok.Column);
            }
            if (lower == "oklab") {
                return ParseOklab(ref reader, fnTok.Column);
            }
            if (lower == "oklch") {
                return ParseOklch(ref reader, fnTok.Column);
            }
            if (lower == "lab") {
                return ParseLab(ref reader, fnTok.Column);
            }
            if (lower == "lch") {
                return ParseLch(ref reader, fnTok.Column);
            }
            if (lower == "color-mix") {
                return ParseColorMix(ref reader, fnTok.Column);
            }
            if (lower == "color") {
                return ParseColorFunction(ref reader, fnTok.Column);
            }
            if (lower == "url") {
                return ParseUrlFn(ref reader, fnTok.Column);
            }
            return ParseGenericFunction(ref reader, name, fnTok.Column);
        }

        static CssValue ParseGenericFunction(ref TokenReader reader, string name, int col) {
            var args = new List<CssValue>();
            var current = new List<CssValue>();
            reader.SkipWhitespace();
            while (!reader.AtEnd() && reader.Peek().Kind != CssTokenKind.RParen) {
                if (reader.Peek().Kind == CssTokenKind.Comma) {
                    reader.Advance();
                    reader.SkipWhitespace();
                    if (current.Count == 0) {
                        throw new CssValueParseException("Empty argument in " + name + "()", reader.Peek().Column);
                    }
                    args.Add(current.Count == 1 ? current[0] : new CssValueList(current, CssValueListSeparator.Space));
                    current = new List<CssValue>();
                    continue;
                }
                var v = ParseSingle(ref reader);
                if (v != null) current.Add(v);
                reader.SkipWhitespace();
            }
            if (reader.AtEnd()) throw new CssValueParseException("Unterminated function " + name + "()", col);
            reader.Advance();
            if (current.Count > 0) {
                args.Add(current.Count == 1 ? current[0] : new CssValueList(current, CssValueListSeparator.Space));
            }
            return new CssFunctionCall(name, args);
        }

        static CssValue ParseUrlFn(ref TokenReader reader, int col) {
            reader.SkipWhitespace();
            var t = reader.Peek();
            if (t.Kind != CssTokenKind.String) {
                throw new CssValueParseException("Expected string inside url()", t.Column);
            }
            reader.Advance();
            string href = t.Text;
            reader.SkipWhitespace();
            if (reader.AtEnd() || reader.Peek().Kind != CssTokenKind.RParen) {
                throw new CssValueParseException("Expected ')' to close url()", col);
            }
            reader.Advance();
            return new CssUrl(href);
        }

        static CssValue ParseVar(ref TokenReader reader, int col) {
            reader.SkipWhitespace();
            var nameTok = reader.Peek();
            if (nameTok.Kind != CssTokenKind.Ident || !nameTok.Text.StartsWith("--")) {
                throw new CssValueParseException("var() expects a custom property name starting with --", nameTok.Column);
            }
            reader.Advance();
            string name = nameTok.Text;
            reader.SkipWhitespace();
            CssValue fallback = null;
            if (!reader.AtEnd() && reader.Peek().Kind == CssTokenKind.Comma) {
                reader.Advance();
                reader.SkipWhitespace();
                fallback = ParseFallbackValue(ref reader);
            }
            reader.SkipWhitespace();
            if (reader.AtEnd() || reader.Peek().Kind != CssTokenKind.RParen) {
                throw new CssValueParseException("Expected ')' to close var()", col);
            }
            reader.Advance();
            return new CssVariableReference(name, fallback);
        }

        static CssValue ParseFallbackValue(ref TokenReader reader) {
            var commaSegments = new List<List<CssValue>>();
            var current = new List<CssValue>();
            reader.SkipWhitespace();
            while (!reader.AtEnd() && reader.Peek().Kind != CssTokenKind.RParen) {
                if (reader.Peek().Kind == CssTokenKind.Comma) {
                    reader.Advance();
                    reader.SkipWhitespace();
                    commaSegments.Add(current);
                    current = new List<CssValue>();
                    continue;
                }
                var v = ParseSingle(ref reader);
                if (v != null) current.Add(v);
                reader.SkipWhitespace();
            }
            commaSegments.Add(current);

            if (commaSegments.Count == 1) {
                var only = commaSegments[0];
                if (only.Count == 0) return null;
                if (only.Count == 1) return only[0];
                return new CssValueList(only, CssValueListSeparator.Space);
            }
            var items = new List<CssValue>();
            foreach (var seg in commaSegments) {
                if (seg.Count == 0) continue;
                if (seg.Count == 1) items.Add(seg[0]);
                else items.Add(new CssValueList(seg, CssValueListSeparator.Space));
            }
            return new CssValueList(items, CssValueListSeparator.Comma);
        }

        static CssValue ParseRgb(ref TokenReader reader, string fnName, int col) {
            var startIndex = reader.SaveIndex();
            reader.SkipWhitespace();

            // CSS Color L5 §4: `rgb(from <color> R G B [/ A])` derives channels
            // from a source color. v1 supports literal-channel slots only:
            // `r`/`g`/`b` identifiers (or `alpha` for the slash slot), `none`,
            // or a `<number>`/`<percentage>` override. Calc expressions that
            // reference channel identifiers are out of scope until the calc
            // evaluator grows a channel-binding context.
            if (TryConsumeFromKeyword(ref reader)) {
                return ParseRgbRelative(ref reader, fnName, col, startIndex);
            }

            var ch1 = ReadColorChannel(ref reader, out bool isPct1);
            reader.SkipWhitespace();

            bool legacy = false;
            if (!reader.AtEnd() && reader.Peek().Kind == CssTokenKind.Comma) {
                legacy = true;
                reader.Advance();
                reader.SkipWhitespace();
            }
            var ch2 = ReadColorChannel(ref reader, out bool isPct2);
            reader.SkipWhitespace();
            if (legacy) {
                ExpectComma(ref reader);
            }
            var ch3 = ReadColorChannel(ref reader, out bool isPct3);
            reader.SkipWhitespace();

            double alpha = 1.0;
            if (!reader.AtEnd() && reader.Peek().Kind != CssTokenKind.RParen) {
                if (legacy) {
                    ExpectComma(ref reader);
                    alpha = ReadAlpha(ref reader);
                } else {
                    var slash = reader.Peek();
                    if (slash.Kind != CssTokenKind.Delim || slash.Text != "/") {
                        throw new CssValueParseException("Expected '/' before alpha in modern rgb()", slash.Column);
                    }
                    reader.Advance();
                    reader.SkipWhitespace();
                    alpha = ReadAlpha(ref reader);
                }
            }
            reader.SkipWhitespace();
            if (reader.AtEnd() || reader.Peek().Kind != CssTokenKind.RParen) {
                throw new CssValueParseException("Expected ')' to close " + fnName + "()", col);
            }
            reader.Advance();

            bool channelsPercent = isPct1 || isPct2 || isPct3;
            string raw = reader.SubstringFrom(startIndex, fnName);
            return CssColor.FromRgb(ch1, ch2, ch3, alpha, channelsPercent, raw);
        }

        static bool TryConsumeFromKeyword(ref TokenReader reader) {
            int saved = reader.SaveIndex();
            reader.SkipWhitespace();
            if (reader.AtEnd()) { reader.RestoreIndex(saved); return false; }
            var t = reader.Peek();
            if (t.Kind != CssTokenKind.Ident || !CssStringUtil.EqualsIgnoreCase(t.Text, "from")) {
                reader.RestoreIndex(saved);
                return false;
            }
            reader.Advance();
            return true;
        }

        static CssValue ParseRgbRelative(ref TokenReader reader, string fnName, int col, int startIndex) {
            reader.SkipWhitespace();
            CssColor source = ParseColorComponent(ref reader);
            reader.SkipWhitespace();
            // CSS Color L5 §4: in `rgb(from C ...)` channels resolve in sRGB
            // byte units (0..255); alpha lives in [0,1].
            var bindings = CalcChannelBindings.Create();
            bindings.Set("r", source.R);
            bindings.Set("g", source.G);
            bindings.Set("b", source.B);
            bindings.Set("alpha", source.A);
            var channelNames = RelChannelNames_Rgb;
            reader.SetRelativeColorChannels(channelNames);

            double r = ReadRelativeChannelExpr(ref reader, bindings, RelSlot.RgbChannel);
            reader.SkipWhitespace();
            double g = ReadRelativeChannelExpr(ref reader, bindings, RelSlot.RgbChannel);
            reader.SkipWhitespace();
            double b = ReadRelativeChannelExpr(ref reader, bindings, RelSlot.RgbChannel);
            reader.SkipWhitespace();

            double alpha = source.A;
            if (!reader.AtEnd() && reader.Peek().Kind != CssTokenKind.RParen) {
                var slash = reader.Peek();
                if (slash.Kind != CssTokenKind.Delim || slash.Text != "/") {
                    throw new CssValueParseException("Expected '/' before alpha in " + fnName + "()", slash.Column);
                }
                reader.Advance();
                reader.SkipWhitespace();
                alpha = ReadRelativeChannelExpr(ref reader, bindings, RelSlot.Alpha);
            }
            reader.SetRelativeColorChannels(null);
            reader.SkipWhitespace();
            if (reader.AtEnd() || reader.Peek().Kind != CssTokenKind.RParen) {
                throw new CssValueParseException("Expected ')' to close " + fnName + "()", col);
            }
            reader.Advance();
            string raw = reader.SubstringFrom(startIndex, fnName);
            return CssColor.FromRgb(r, g, b, alpha, false, raw);
        }

        // CSS Color L5 §4: which numeric meaning a relative-color channel slot
        // takes (controls how literal numbers/percentages are interpreted, and
        // how `none` resolves). Channels themselves come from the bindings
        // pre-populated by the caller — this enum only governs literal slot
        // scaling.
        enum RelSlot {
            RgbChannel,      // 0..255, percentage = % * 2.55
            Alpha,           // 0..1,   percentage = % / 100
            HueDeg,          // degrees (allows <angle> dimensions)
            UnitPercent,     // 0..100 stored as 0..100 (HSL S/L, HWB W/B)
            LabLightness,    // 0..100,  percentage = % (no rescale)
            LabAxis,         // ±125,    percentage = % * 1.25
            LchChroma,       // 0..150,  percentage = % * 1.5
            OklabLightness,  // 0..1,    percentage = % / 100
            OklabAxis,       // ±0.4,    percentage = % / 100 * 0.4
            OklchChroma,     // 0..0.4,  percentage = % / 100 * 0.4
            ColorFnChannel,  // color() channel: 0..1, percentage = % / 100
        }

        // CSS Color L5 §4: in a relative-color slot, the channel expression is
        // any of {channel-ident, `none`, <number>, <percentage>, calc(...) /
        // math function referencing channel idents}. This helper unifies the
        // parsing across `rgb/hsl/hwb/lab/lch/oklab/oklch/color()`; the SLOT
        // enum governs how literal numerics + `none` map onto the target
        // numeric range.
        static double ReadRelativeChannelExpr(ref TokenReader reader, CalcChannelBindings bindings, RelSlot slot) {
            var t = reader.Peek();
            if (t.Kind == CssTokenKind.Ident) {
                string lower = CssStringUtil.ToLowerInvariantOrSame(t.Text);
                if (lower == "none") {
                    reader.Advance();
                    // CSS Color 4 §4.4: `none` resolves to 0 for the analogous
                    // numeric channel (matching H10's existing behaviour).
                    return 0;
                }
                if (bindings.TryGet(lower, out double channelVal)) {
                    reader.Advance();
                    return channelVal;
                }
                throw new CssValueParseException("Expected channel ident / none / number in relative-color slot", t.Column);
            }
            if (t.Kind == CssTokenKind.Number) {
                reader.Advance();
                return t.Number;
            }
            if (t.Kind == CssTokenKind.Percentage) {
                reader.Advance();
                return ScalePercentage(t.Number, slot);
            }
            if (t.Kind == CssTokenKind.Dimension && slot == RelSlot.HueDeg) {
                // Allow `<angle>` dimensions (deg/turn/rad/grad) in hue slots.
                return ReadHue(ref reader);
            }
            if (t.Kind == CssTokenKind.Function) {
                string fnLower = CssStringUtil.ToLowerInvariantOrSame(t.Text);
                if (fnLower == "calc" || IsMathFunctionName(fnLower)) {
                    int col = t.Column;
                    reader.Advance();
                    CssValue v = fnLower == "calc"
                        ? ParseCalc(ref reader, col)
                        : ParseMathFunction(ref reader, fnLower, col);
                    if (v is CssCalc cc) {
                        // Evaluate against an empty length-context (relative
                        // color calc() math is unitless / channel-scale; no
                        // length unit should appear).
                        return cc.Evaluate(LengthContext.Default, bindings);
                    }
                    throw new CssValueParseException("Expected calc() result in relative-color slot", col);
                }
            }
            throw new CssValueParseException("Expected channel in relative-color slot", t.Column);
        }

        static double ScalePercentage(double pct, RelSlot slot) {
            switch (slot) {
                case RelSlot.RgbChannel: return pct / 100.0 * 255.0;
                case RelSlot.Alpha: return pct / 100.0;
                case RelSlot.HueDeg: return pct;  // unusual but allowed; treat as degrees
                case RelSlot.UnitPercent: return pct;
                case RelSlot.LabLightness: return pct;
                case RelSlot.LabAxis: return pct / 100.0 * 125.0;
                case RelSlot.LchChroma: return pct / 100.0 * 150.0;
                case RelSlot.OklabLightness: return pct / 100.0;
                case RelSlot.OklabAxis: return pct / 100.0 * 0.4;
                case RelSlot.OklchChroma: return pct / 100.0 * 0.4;
                case RelSlot.ColorFnChannel: return pct / 100.0;
            }
            return pct;
        }

        static readonly HashSet<string> RelChannelNames_Rgb = new HashSet<string> { "r", "g", "b", "alpha" };
        static readonly HashSet<string> RelChannelNames_Hsl = new HashSet<string> { "h", "s", "l", "alpha" };
        static readonly HashSet<string> RelChannelNames_Hwb = new HashSet<string> { "h", "w", "b", "alpha" };
        static readonly HashSet<string> RelChannelNames_Lab = new HashSet<string> { "l", "a", "b", "alpha" };
        static readonly HashSet<string> RelChannelNames_Lch = new HashSet<string> { "l", "c", "h", "alpha" };
        static readonly HashSet<string> RelChannelNames_Oklab = new HashSet<string> { "l", "a", "b", "alpha" };
        static readonly HashSet<string> RelChannelNames_Oklch = new HashSet<string> { "l", "c", "h", "alpha" };
        // color() channel idents per CSS Color L5 §4: RGB-family spaces expose
        // r/g/b; xyz spaces expose x/y/z. We register the union and rely on
        // the parser to use the right map per resolved space.
        static readonly HashSet<string> RelChannelNames_ColorRgbFamily = new HashSet<string> { "r", "g", "b", "alpha" };
        static readonly HashSet<string> RelChannelNames_ColorXyz = new HashSet<string> { "x", "y", "z", "alpha" };

        // CSS Color L5 §4: relative-color decomposition helpers. The engine
        // stores source colors as sRGB bytes (CssColor.R/G/B/A), so each
        // relative path converts FROM sRGB TO the host function's output
        // space. Lossy for OOG colors but spec-conformant for v1.
        static void DecomposeSourceToHsl(CssColor src, out double h, out double s, out double l) {
            double rN = src.R / 255.0;
            double gN = src.G / 255.0;
            double bN = src.B / 255.0;
            double max = System.Math.Max(rN, System.Math.Max(gN, bN));
            double min = System.Math.Min(rN, System.Math.Min(gN, bN));
            l = (max + min) / 2.0;
            double d = max - min;
            if (d < 1e-9) { h = 0; s = 0; l *= 100.0; return; }
            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
            double hh;
            if (max == rN) hh = (gN - bN) / d + (gN < bN ? 6 : 0);
            else if (max == gN) hh = (bN - rN) / d + 2;
            else hh = (rN - gN) / d + 4;
            h = hh * 60.0;
            // CSS Color 4 §4: HSL emits s/l in % (i.e. 0..100), not 0..1.
            s *= 100.0;
            l *= 100.0;
        }

        static void DecomposeSourceToHwb(CssColor src, out double h, out double w, out double bk) {
            double rN = src.R / 255.0;
            double gN = src.G / 255.0;
            double bN = src.B / 255.0;
            DecomposeSourceToHsl(src, out h, out _, out _);
            w = System.Math.Min(rN, System.Math.Min(gN, bN)) * 100.0;
            bk = (1.0 - System.Math.Max(rN, System.Math.Max(gN, bN))) * 100.0;
        }

        // CSS Color 4 §11: sRGB -> linear sRGB -> OKLab matrix pair (D65 throughout).
        static void DecomposeSourceToOklab(CssColor src, out double L, out double a, out double b) {
            double lr = CssColor.SrgbToLinear(src.R / 255.0);
            double lg = CssColor.SrgbToLinear(src.G / 255.0);
            double lb = CssColor.SrgbToLinear(src.B / 255.0);
            CssColor.LinearRgbToOklab(lr, lg, lb, out L, out a, out b);
        }

        static void DecomposeSourceToOklch(CssColor src, out double L, out double c, out double h) {
            DecomposeSourceToOklab(src, out L, out double a, out double b);
            c = System.Math.Sqrt(a * a + b * b);
            if (c < 1e-9) { h = 0; return; }
            h = System.Math.Atan2(b, a) * 180.0 / System.Math.PI;
            if (h < 0) h += 360.0;
        }

        // CSS Color 4 §10.1: sRGB -> linear sRGB -> XYZ(D65) -> Bradford D65->D50 -> Lab(D50).
        static void DecomposeSourceToLab(CssColor src, out double L, out double a, out double b) {
            double lr = CssColor.SrgbToLinear(src.R / 255.0);
            double lg = CssColor.SrgbToLinear(src.G / 255.0);
            double lb = CssColor.SrgbToLinear(src.B / 255.0);
            double x65 = 0.4123907992659595  * lr + 0.35758433938387796 * lg + 0.1804807884018343  * lb;
            double y65 = 0.21263900587151036 * lr + 0.7151686787677559  * lg + 0.07219231536073371 * lb;
            double z65 = 0.01933081871559185 * lr + 0.11919477979462599 * lg + 0.9505321522496606  * lb;
            double x50 =  1.0479298208405488   * x65 +  0.022946793341019088 * y65 + -0.05019222954313557 * z65;
            double y50 =  0.029627815688159344 * x65 +  0.990434484573249    * y65 + -0.01707382502938514 * z65;
            double z50 = -0.009243058152591178 * x65 +  0.015055144896577836 * y65 +  0.7518742899580008  * z65;
            const double Xn = 0.96422, Yn = 1.0, Zn = 0.82521;
            double fx = LabF(x50 / Xn);
            double fy = LabF(y50 / Yn);
            double fz = LabF(z50 / Zn);
            L = 116.0 * fy - 16.0;
            a = 500.0 * (fx - fy);
            b = 200.0 * (fy - fz);
        }

        static double LabF(double t) {
            const double delta = 6.0 / 29.0;
            if (t > delta * delta * delta) return System.Math.Pow(t, 1.0 / 3.0);
            return t / (3.0 * delta * delta) + 16.0 / 116.0;
        }

        static void DecomposeSourceToLch(CssColor src, out double L, out double c, out double h) {
            DecomposeSourceToLab(src, out L, out double a, out double b);
            c = System.Math.Sqrt(a * a + b * b);
            if (c < 1e-9) { h = 0; return; }
            h = System.Math.Atan2(b, a) * 180.0 / System.Math.PI;
            if (h < 0) h += 360.0;
        }

        // CSS Color 4 §15: relative `color()` channel-bindings depend on the
        // resolved colorspace. RGB-family spaces expose r/g/b; XYZ spaces
        // expose x/y/z. The engine stores source colors as sRGB bytes so the
        // forward conversion goes sRGB -> linear sRGB -> target.
        static bool TryDecomposeSourceToColorSpace(CssColor src, string spaceLower, out double c1, out double c2, out double c3, out bool isXyz) {
            isXyz = false;
            c1 = c2 = c3 = 0;
            if (spaceLower == "srgb") {
                c1 = src.R / 255.0; c2 = src.G / 255.0; c3 = src.B / 255.0;
                return true;
            }
            double lr = CssColor.SrgbToLinear(src.R / 255.0);
            double lg = CssColor.SrgbToLinear(src.G / 255.0);
            double lb = CssColor.SrgbToLinear(src.B / 255.0);
            if (spaceLower == "srgb-linear") {
                c1 = lr; c2 = lg; c3 = lb;
                return true;
            }
            double x65 = 0.4123907992659595  * lr + 0.35758433938387796 * lg + 0.1804807884018343  * lb;
            double y65 = 0.21263900587151036 * lr + 0.7151686787677559  * lg + 0.07219231536073371 * lb;
            double z65 = 0.01933081871559185 * lr + 0.11919477979462599 * lg + 0.9505321522496606  * lb;
            if (spaceLower == "xyz" || spaceLower == "xyz-d65") {
                isXyz = true;
                c1 = x65; c2 = y65; c3 = z65;
                return true;
            }
            if (spaceLower == "xyz-d50") {
                isXyz = true;
                c1 =  1.0479298208405488   * x65 +  0.022946793341019088 * y65 + -0.05019222954313557 * z65;
                c2 =  0.029627815688159344 * x65 +  0.990434484573249    * y65 + -0.01707382502938514 * z65;
                c3 = -0.009243058152591178 * x65 +  0.015055144896577836 * y65 +  0.7518742899580008  * z65;
                return true;
            }
            if (spaceLower == "display-p3") {
                // XYZ(D65) -> linear display-p3 (CSS Color 4 §17 inverse matrix).
                double pr =  2.4934969119414245  * x65 + -0.9313836179191236 * y65 + -0.4027107844507168 * z65;
                double pg = -0.8294889695615747  * x65 +  1.7626640603183465 * y65 +  0.0236246858419436 * z65;
                double pb =  0.0358458302437845  * x65 + -0.0761723892680418 * y65 +  0.9568845240076872 * z65;
                c1 = CssColor.LinearToSrgb(pr);
                c2 = CssColor.LinearToSrgb(pg);
                c3 = CssColor.LinearToSrgb(pb);
                return true;
            }
            // Fallback: sRGB channels for unsupported target spaces.
            c1 = src.R / 255.0; c2 = src.G / 255.0; c3 = src.B / 255.0;
            return true;
        }

        static CssValue ParseHslRelative(ref TokenReader reader, string fnName, int col, int startIndex) {
            reader.SkipWhitespace();
            CssColor src = ParseColorComponent(ref reader);
            reader.SkipWhitespace();
            DecomposeSourceToHsl(src, out double hSrc, out double sSrc, out double lSrc);
            var bindings = CalcChannelBindings.Create();
            bindings.Set("h", hSrc);
            bindings.Set("s", sSrc);
            bindings.Set("l", lSrc);
            bindings.Set("alpha", src.A);
            reader.SetRelativeColorChannels(RelChannelNames_Hsl);
            double h = ReadRelativeChannelExpr(ref reader, bindings, RelSlot.HueDeg);
            reader.SkipWhitespace();
            double s = ReadRelativeChannelExpr(ref reader, bindings, RelSlot.UnitPercent);
            reader.SkipWhitespace();
            double l = ReadRelativeChannelExpr(ref reader, bindings, RelSlot.UnitPercent);
            reader.SkipWhitespace();
            double alpha = src.A;
            if (!reader.AtEnd() && reader.Peek().Kind != CssTokenKind.RParen) {
                var slash = reader.Peek();
                if (slash.Kind != CssTokenKind.Delim || slash.Text != "/") {
                    throw new CssValueParseException("Expected '/' before alpha in " + fnName + "()", slash.Column);
                }
                reader.Advance();
                reader.SkipWhitespace();
                alpha = ReadRelativeChannelExpr(ref reader, bindings, RelSlot.Alpha);
            }
            reader.SetRelativeColorChannels(null);
            reader.SkipWhitespace();
            if (reader.AtEnd() || reader.Peek().Kind != CssTokenKind.RParen) {
                throw new CssValueParseException("Expected ')' to close " + fnName + "()", col);
            }
            reader.Advance();
            string raw = reader.SubstringFrom(startIndex, fnName);
            return CssColor.FromHsl(h, s, l, alpha, raw);
        }

        static CssValue ParseHwbRelative(ref TokenReader reader, int col, int startIndex) {
            reader.SkipWhitespace();
            CssColor src = ParseColorComponent(ref reader);
            reader.SkipWhitespace();
            DecomposeSourceToHwb(src, out double hSrc, out double wSrc, out double bkSrc);
            var bindings = CalcChannelBindings.Create();
            bindings.Set("h", hSrc);
            bindings.Set("w", wSrc);
            bindings.Set("b", bkSrc);
            bindings.Set("alpha", src.A);
            reader.SetRelativeColorChannels(RelChannelNames_Hwb);
            double h = ReadRelativeChannelExpr(ref reader, bindings, RelSlot.HueDeg);
            reader.SkipWhitespace();
            double w = ReadRelativeChannelExpr(ref reader, bindings, RelSlot.UnitPercent);
            reader.SkipWhitespace();
            double bk = ReadRelativeChannelExpr(ref reader, bindings, RelSlot.UnitPercent);
            reader.SkipWhitespace();
            double alpha = src.A;
            if (!reader.AtEnd() && reader.Peek().Kind != CssTokenKind.RParen) {
                var slash = reader.Peek();
                if (slash.Kind != CssTokenKind.Delim || slash.Text != "/") {
                    throw new CssValueParseException("Expected '/' before alpha in hwb()", slash.Column);
                }
                reader.Advance();
                reader.SkipWhitespace();
                alpha = ReadRelativeChannelExpr(ref reader, bindings, RelSlot.Alpha);
            }
            reader.SetRelativeColorChannels(null);
            reader.SkipWhitespace();
            if (reader.AtEnd() || reader.Peek().Kind != CssTokenKind.RParen) {
                throw new CssValueParseException("Expected ')' to close hwb()", col);
            }
            reader.Advance();
            string raw = reader.SubstringFrom(startIndex, "hwb");
            return CssColor.FromHwb(h, w, bk, alpha, raw);
        }

        static CssValue ParseOklabRelative(ref TokenReader reader, int col, int startIndex) {
            reader.SkipWhitespace();
            CssColor src = ParseColorComponent(ref reader);
            reader.SkipWhitespace();
            DecomposeSourceToOklab(src, out double LSrc, out double aSrc, out double bSrc);
            var bindings = CalcChannelBindings.Create();
            bindings.Set("l", LSrc);
            bindings.Set("a", aSrc);
            bindings.Set("b", bSrc);
            bindings.Set("alpha", src.A);
            reader.SetRelativeColorChannels(RelChannelNames_Oklab);
            double L = ReadRelativeChannelExpr(ref reader, bindings, RelSlot.OklabLightness);
            reader.SkipWhitespace();
            double a = ReadRelativeChannelExpr(ref reader, bindings, RelSlot.OklabAxis);
            reader.SkipWhitespace();
            double b = ReadRelativeChannelExpr(ref reader, bindings, RelSlot.OklabAxis);
            reader.SkipWhitespace();
            double alpha = src.A;
            if (!reader.AtEnd() && reader.Peek().Kind != CssTokenKind.RParen) {
                var slash = reader.Peek();
                if (slash.Kind != CssTokenKind.Delim || slash.Text != "/") {
                    throw new CssValueParseException("Expected '/' before alpha in oklab()", slash.Column);
                }
                reader.Advance();
                reader.SkipWhitespace();
                alpha = ReadRelativeChannelExpr(ref reader, bindings, RelSlot.Alpha);
            }
            reader.SetRelativeColorChannels(null);
            reader.SkipWhitespace();
            if (reader.AtEnd() || reader.Peek().Kind != CssTokenKind.RParen) {
                throw new CssValueParseException("Expected ')' to close oklab()", col);
            }
            reader.Advance();
            string raw = reader.SubstringFrom(startIndex, "oklab");
            return CssColor.FromOklab(L, a, b, alpha, raw);
        }

        static CssValue ParseOklchRelative(ref TokenReader reader, int col, int startIndex) {
            reader.SkipWhitespace();
            CssColor src = ParseColorComponent(ref reader);
            reader.SkipWhitespace();
            DecomposeSourceToOklch(src, out double LSrc, out double cSrc, out double hSrc);
            var bindings = CalcChannelBindings.Create();
            bindings.Set("l", LSrc);
            bindings.Set("c", cSrc);
            bindings.Set("h", hSrc);
            bindings.Set("alpha", src.A);
            reader.SetRelativeColorChannels(RelChannelNames_Oklch);
            double L = ReadRelativeChannelExpr(ref reader, bindings, RelSlot.OklabLightness);
            reader.SkipWhitespace();
            double c = ReadRelativeChannelExpr(ref reader, bindings, RelSlot.OklchChroma);
            reader.SkipWhitespace();
            double h = ReadRelativeChannelExpr(ref reader, bindings, RelSlot.HueDeg);
            reader.SkipWhitespace();
            double alpha = src.A;
            if (!reader.AtEnd() && reader.Peek().Kind != CssTokenKind.RParen) {
                var slash = reader.Peek();
                if (slash.Kind != CssTokenKind.Delim || slash.Text != "/") {
                    throw new CssValueParseException("Expected '/' before alpha in oklch()", slash.Column);
                }
                reader.Advance();
                reader.SkipWhitespace();
                alpha = ReadRelativeChannelExpr(ref reader, bindings, RelSlot.Alpha);
            }
            reader.SetRelativeColorChannels(null);
            reader.SkipWhitespace();
            if (reader.AtEnd() || reader.Peek().Kind != CssTokenKind.RParen) {
                throw new CssValueParseException("Expected ')' to close oklch()", col);
            }
            reader.Advance();
            string raw = reader.SubstringFrom(startIndex, "oklch");
            return CssColor.FromOklch(L, c, h, alpha, raw);
        }

        static CssValue ParseLabRelative(ref TokenReader reader, int col, int startIndex) {
            reader.SkipWhitespace();
            CssColor src = ParseColorComponent(ref reader);
            reader.SkipWhitespace();
            DecomposeSourceToLab(src, out double LSrc, out double aSrc, out double bSrc);
            var bindings = CalcChannelBindings.Create();
            bindings.Set("l", LSrc);
            bindings.Set("a", aSrc);
            bindings.Set("b", bSrc);
            bindings.Set("alpha", src.A);
            reader.SetRelativeColorChannels(RelChannelNames_Lab);
            double L = ReadRelativeChannelExpr(ref reader, bindings, RelSlot.LabLightness);
            reader.SkipWhitespace();
            double a = ReadRelativeChannelExpr(ref reader, bindings, RelSlot.LabAxis);
            reader.SkipWhitespace();
            double b = ReadRelativeChannelExpr(ref reader, bindings, RelSlot.LabAxis);
            reader.SkipWhitespace();
            double alpha = src.A;
            if (!reader.AtEnd() && reader.Peek().Kind != CssTokenKind.RParen) {
                var slash = reader.Peek();
                if (slash.Kind != CssTokenKind.Delim || slash.Text != "/") {
                    throw new CssValueParseException("Expected '/' before alpha in lab()", slash.Column);
                }
                reader.Advance();
                reader.SkipWhitespace();
                alpha = ReadRelativeChannelExpr(ref reader, bindings, RelSlot.Alpha);
            }
            reader.SetRelativeColorChannels(null);
            reader.SkipWhitespace();
            if (reader.AtEnd() || reader.Peek().Kind != CssTokenKind.RParen) {
                throw new CssValueParseException("Expected ')' to close lab()", col);
            }
            reader.Advance();
            string raw = reader.SubstringFrom(startIndex, "lab");
            return CssColor.FromLab(L, a, b, alpha, raw);
        }

        static CssValue ParseLchRelative(ref TokenReader reader, int col, int startIndex) {
            reader.SkipWhitespace();
            CssColor src = ParseColorComponent(ref reader);
            reader.SkipWhitespace();
            DecomposeSourceToLch(src, out double LSrc, out double cSrc, out double hSrc);
            var bindings = CalcChannelBindings.Create();
            bindings.Set("l", LSrc);
            bindings.Set("c", cSrc);
            bindings.Set("h", hSrc);
            bindings.Set("alpha", src.A);
            reader.SetRelativeColorChannels(RelChannelNames_Lch);
            double L = ReadRelativeChannelExpr(ref reader, bindings, RelSlot.LabLightness);
            reader.SkipWhitespace();
            double c = ReadRelativeChannelExpr(ref reader, bindings, RelSlot.LchChroma);
            reader.SkipWhitespace();
            double h = ReadRelativeChannelExpr(ref reader, bindings, RelSlot.HueDeg);
            reader.SkipWhitespace();
            double alpha = src.A;
            if (!reader.AtEnd() && reader.Peek().Kind != CssTokenKind.RParen) {
                var slash = reader.Peek();
                if (slash.Kind != CssTokenKind.Delim || slash.Text != "/") {
                    throw new CssValueParseException("Expected '/' before alpha in lch()", slash.Column);
                }
                reader.Advance();
                reader.SkipWhitespace();
                alpha = ReadRelativeChannelExpr(ref reader, bindings, RelSlot.Alpha);
            }
            reader.SetRelativeColorChannels(null);
            reader.SkipWhitespace();
            if (reader.AtEnd() || reader.Peek().Kind != CssTokenKind.RParen) {
                throw new CssValueParseException("Expected ')' to close lch()", col);
            }
            reader.Advance();
            string raw = reader.SubstringFrom(startIndex, "lch");
            return CssColor.FromLch(L, c, h, alpha, raw);
        }

        static CssValue ParseColorFunctionRelative(ref TokenReader reader, int col, int startIndex, string spaceLower) {
            reader.SkipWhitespace();
            CssColor src = ParseColorComponent(ref reader);
            reader.SkipWhitespace();
            TryDecomposeSourceToColorSpace(src, spaceLower, out double c1Src, out double c2Src, out double c3Src, out bool isXyz);
            var bindings = CalcChannelBindings.Create();
            if (isXyz) {
                bindings.Set("x", c1Src);
                bindings.Set("y", c2Src);
                bindings.Set("z", c3Src);
                reader.SetRelativeColorChannels(RelChannelNames_ColorXyz);
            } else {
                bindings.Set("r", c1Src);
                bindings.Set("g", c2Src);
                bindings.Set("b", c3Src);
                reader.SetRelativeColorChannels(RelChannelNames_ColorRgbFamily);
            }
            bindings.Set("alpha", src.A);
            double c1 = ReadRelativeChannelExpr(ref reader, bindings, RelSlot.ColorFnChannel);
            reader.SkipWhitespace();
            double c2 = ReadRelativeChannelExpr(ref reader, bindings, RelSlot.ColorFnChannel);
            reader.SkipWhitespace();
            double c3 = ReadRelativeChannelExpr(ref reader, bindings, RelSlot.ColorFnChannel);
            reader.SkipWhitespace();
            double alpha = src.A;
            if (!reader.AtEnd() && reader.Peek().Kind != CssTokenKind.RParen) {
                var slash = reader.Peek();
                if (slash.Kind != CssTokenKind.Delim || slash.Text != "/") {
                    throw new CssValueParseException("Expected '/' before alpha in color()", slash.Column);
                }
                reader.Advance();
                reader.SkipWhitespace();
                alpha = ReadRelativeChannelExpr(ref reader, bindings, RelSlot.Alpha);
            }
            reader.SetRelativeColorChannels(null);
            reader.SkipWhitespace();
            if (reader.AtEnd() || reader.Peek().Kind != CssTokenKind.RParen) {
                throw new CssValueParseException("Expected ')' to close color()", col);
            }
            reader.Advance();
            string raw = reader.SubstringFrom(startIndex, "color");
            return CssColor.FromColorFunction(spaceLower, c1, c2, c3, alpha, raw);
        }

        static double ReadColorChannel(ref TokenReader reader, out bool isPercent) {
            var t = reader.Peek();
            if (t.Kind == CssTokenKind.Number) {
                reader.Advance();
                isPercent = false;
                return t.Number;
            }
            if (t.Kind == CssTokenKind.Percentage) {
                reader.Advance();
                isPercent = true;
                return t.Number;
            }
            throw new CssValueParseException("Expected number or percentage for color channel", t.Column);
        }

        static double ReadAlpha(ref TokenReader reader) {
            var t = reader.Peek();
            if (t.Kind == CssTokenKind.Number) {
                reader.Advance();
                return t.Number;
            }
            if (t.Kind == CssTokenKind.Percentage) {
                reader.Advance();
                return t.Number / 100.0;
            }
            throw new CssValueParseException("Expected alpha value (number or percentage)", t.Column);
        }

        static void ExpectComma(ref TokenReader reader) {
            reader.SkipWhitespace();
            var t = reader.Peek();
            if (t.Kind != CssTokenKind.Comma) {
                throw new CssValueParseException("Expected ','", t.Column);
            }
            reader.Advance();
            reader.SkipWhitespace();
        }

        static CssValue ParseHsl(ref TokenReader reader, string fnName, int col) {
            var startIndex = reader.SaveIndex();
            reader.SkipWhitespace();

            if (TryConsumeFromKeyword(ref reader)) {
                return ParseHslRelative(ref reader, fnName, col, startIndex);
            }

            double hue = ReadHue(ref reader);
            reader.SkipWhitespace();

            bool legacy = false;
            if (!reader.AtEnd() && reader.Peek().Kind == CssTokenKind.Comma) {
                legacy = true;
                reader.Advance();
                reader.SkipWhitespace();
            }

            var sat = reader.Peek();
            if (sat.Kind != CssTokenKind.Percentage) {
                throw new CssValueParseException("Expected saturation as percentage in " + fnName + "()", sat.Column);
            }
            double s = sat.Number;
            reader.Advance();
            reader.SkipWhitespace();
            if (legacy) ExpectComma(ref reader);

            var light = reader.Peek();
            if (light.Kind != CssTokenKind.Percentage) {
                throw new CssValueParseException("Expected lightness as percentage in " + fnName + "()", light.Column);
            }
            double l = light.Number;
            reader.Advance();
            reader.SkipWhitespace();

            double alpha = 1.0;
            if (!reader.AtEnd() && reader.Peek().Kind != CssTokenKind.RParen) {
                if (legacy) {
                    ExpectComma(ref reader);
                    alpha = ReadAlpha(ref reader);
                } else {
                    var slash = reader.Peek();
                    if (slash.Kind != CssTokenKind.Delim || slash.Text != "/") {
                        throw new CssValueParseException("Expected '/' before alpha in modern hsl()", slash.Column);
                    }
                    reader.Advance();
                    reader.SkipWhitespace();
                    alpha = ReadAlpha(ref reader);
                }
            }
            reader.SkipWhitespace();
            if (reader.AtEnd() || reader.Peek().Kind != CssTokenKind.RParen) {
                throw new CssValueParseException("Expected ')' to close " + fnName + "()", col);
            }
            reader.Advance();
            string raw = reader.SubstringFrom(startIndex, fnName);
            return CssColor.FromHsl(hue, s, l, alpha, raw);
        }

        static CssValue ParseHwb(ref TokenReader reader, int col) {
            var startIndex = reader.SaveIndex();
            reader.SkipWhitespace();
            if (TryConsumeFromKeyword(ref reader)) {
                return ParseHwbRelative(ref reader, col, startIndex);
            }
            double hue = ReadHue(ref reader);
            reader.SkipWhitespace();
            var wTok = reader.Peek();
            if (wTok.Kind != CssTokenKind.Percentage) {
                throw new CssValueParseException("Expected whiteness as percentage in hwb()", wTok.Column);
            }
            double w = wTok.Number;
            reader.Advance();
            reader.SkipWhitespace();
            var bTok = reader.Peek();
            if (bTok.Kind != CssTokenKind.Percentage) {
                throw new CssValueParseException("Expected blackness as percentage in hwb()", bTok.Column);
            }
            double b = bTok.Number;
            reader.Advance();
            reader.SkipWhitespace();
            double alpha = ReadOptionalSlashAlpha(ref reader, "hwb");
            reader.SkipWhitespace();
            if (reader.AtEnd() || reader.Peek().Kind != CssTokenKind.RParen) {
                throw new CssValueParseException("Expected ')' to close hwb()", col);
            }
            reader.Advance();
            string raw = reader.SubstringFrom(startIndex, "hwb");
            return CssColor.FromHwb(hue, w, b, alpha, raw);
        }

        static CssValue ParseOklab(ref TokenReader reader, int col) {
            var startIndex = reader.SaveIndex();
            reader.SkipWhitespace();
            if (TryConsumeFromKeyword(ref reader)) {
                return ParseOklabRelative(ref reader, col, startIndex);
            }
            double L = ReadOklabLightness(ref reader);
            reader.SkipWhitespace();
            double a = ReadOklabAxis(ref reader);
            reader.SkipWhitespace();
            double b = ReadOklabAxis(ref reader);
            reader.SkipWhitespace();
            double alpha = ReadOptionalSlashAlpha(ref reader, "oklab");
            reader.SkipWhitespace();
            if (reader.AtEnd() || reader.Peek().Kind != CssTokenKind.RParen) {
                throw new CssValueParseException("Expected ')' to close oklab()", col);
            }
            reader.Advance();
            string raw = reader.SubstringFrom(startIndex, "oklab");
            return CssColor.FromOklab(L, a, b, alpha, raw);
        }

        static CssValue ParseOklch(ref TokenReader reader, int col) {
            var startIndex = reader.SaveIndex();
            reader.SkipWhitespace();
            if (TryConsumeFromKeyword(ref reader)) {
                return ParseOklchRelative(ref reader, col, startIndex);
            }
            double L = ReadOklabLightness(ref reader);
            reader.SkipWhitespace();
            double c = ReadOklchChroma(ref reader);
            reader.SkipWhitespace();
            double h = ReadHue(ref reader);
            reader.SkipWhitespace();
            double alpha = ReadOptionalSlashAlpha(ref reader, "oklch");
            reader.SkipWhitespace();
            if (reader.AtEnd() || reader.Peek().Kind != CssTokenKind.RParen) {
                throw new CssValueParseException("Expected ')' to close oklch()", col);
            }
            reader.Advance();
            string raw = reader.SubstringFrom(startIndex, "oklch");
            return CssColor.FromOklch(L, c, h, alpha, raw);
        }

        static CssValue ParseLab(ref TokenReader reader, int col) {
            var startIndex = reader.SaveIndex();
            reader.SkipWhitespace();
            if (TryConsumeFromKeyword(ref reader)) {
                return ParseLabRelative(ref reader, col, startIndex);
            }
            double L = ReadLabLightness(ref reader);
            reader.SkipWhitespace();
            double a = ReadLabAxis(ref reader);
            reader.SkipWhitespace();
            double b = ReadLabAxis(ref reader);
            reader.SkipWhitespace();
            double alpha = ReadOptionalSlashAlpha(ref reader, "lab");
            reader.SkipWhitespace();
            if (reader.AtEnd() || reader.Peek().Kind != CssTokenKind.RParen) {
                throw new CssValueParseException("Expected ')' to close lab()", col);
            }
            reader.Advance();
            string raw = reader.SubstringFrom(startIndex, "lab");
            return CssColor.FromLab(L, a, b, alpha, raw);
        }

        static CssValue ParseLch(ref TokenReader reader, int col) {
            var startIndex = reader.SaveIndex();
            reader.SkipWhitespace();
            if (TryConsumeFromKeyword(ref reader)) {
                return ParseLchRelative(ref reader, col, startIndex);
            }
            double L = ReadLabLightness(ref reader);
            reader.SkipWhitespace();
            double c = ReadLchChroma(ref reader);
            reader.SkipWhitespace();
            double h = ReadHue(ref reader);
            reader.SkipWhitespace();
            double alpha = ReadOptionalSlashAlpha(ref reader, "lch");
            reader.SkipWhitespace();
            if (reader.AtEnd() || reader.Peek().Kind != CssTokenKind.RParen) {
                throw new CssValueParseException("Expected ')' to close lch()", col);
            }
            reader.Advance();
            string raw = reader.SubstringFrom(startIndex, "lch");
            return CssColor.FromLch(L, c, h, alpha, raw);
        }

        // CSS Color 4 §10.1: CIELab lightness is a <number> in [0,100] or a <percentage>
        // where 100% == 100. (Distinct from oklab where 100% == 1.0.)
        static double ReadLabLightness(ref TokenReader reader) {
            var t = reader.Peek();
            if (t.Kind == CssTokenKind.Percentage) {
                reader.Advance();
                return t.Number;
            }
            if (t.Kind == CssTokenKind.Number) {
                reader.Advance();
                return t.Number;
            }
            throw new CssValueParseException("Expected lightness (number or %)", t.Column);
        }

        static double ReadLabAxis(ref TokenReader reader) {
            var t = reader.Peek();
            if (t.Kind == CssTokenKind.Percentage) {
                // CSS Color 4 §10.1: 100% maps to ±125 on the a/b axes for CIELab.
                reader.Advance();
                return t.Number / 100.0 * 125.0;
            }
            if (t.Kind == CssTokenKind.Number) {
                reader.Advance();
                return t.Number;
            }
            throw new CssValueParseException("Expected lab axis value", t.Column);
        }

        static double ReadLchChroma(ref TokenReader reader) {
            var t = reader.Peek();
            if (t.Kind == CssTokenKind.Percentage) {
                // CSS Color 4 §10.2: 100% maps to 150 chroma in CIELCh.
                reader.Advance();
                return t.Number / 100.0 * 150.0;
            }
            if (t.Kind == CssTokenKind.Number) {
                reader.Advance();
                return t.Number;
            }
            throw new CssValueParseException("Expected chroma value", t.Column);
        }

        static double ReadOklabLightness(ref TokenReader reader) {
            var t = reader.Peek();
            if (t.Kind == CssTokenKind.Percentage) {
                reader.Advance();
                return t.Number / 100.0;
            }
            if (t.Kind == CssTokenKind.Number) {
                reader.Advance();
                return t.Number;
            }
            throw new CssValueParseException("Expected lightness (number or %)", t.Column);
        }

        static double ReadOklabAxis(ref TokenReader reader) {
            var t = reader.Peek();
            if (t.Kind == CssTokenKind.Percentage) {
                // CSS Color 4: 100% maps to ±0.4 on the a/b axes for OKLab.
                reader.Advance();
                return t.Number / 100.0 * 0.4;
            }
            if (t.Kind == CssTokenKind.Number) {
                reader.Advance();
                return t.Number;
            }
            throw new CssValueParseException("Expected oklab axis value", t.Column);
        }

        static double ReadOklchChroma(ref TokenReader reader) {
            var t = reader.Peek();
            if (t.Kind == CssTokenKind.Percentage) {
                // CSS Color 4: 100% maps to 0.4 chroma in OKLCh.
                reader.Advance();
                return t.Number / 100.0 * 0.4;
            }
            if (t.Kind == CssTokenKind.Number) {
                reader.Advance();
                return t.Number;
            }
            throw new CssValueParseException("Expected chroma value", t.Column);
        }

        static double ReadOptionalSlashAlpha(ref TokenReader reader, string fnName) {
            if (reader.AtEnd() || reader.Peek().Kind == CssTokenKind.RParen) return 1.0;
            var slash = reader.Peek();
            if (slash.Kind != CssTokenKind.Delim || slash.Text != "/") {
                throw new CssValueParseException("Expected '/' before alpha in " + fnName + "()", slash.Column);
            }
            reader.Advance();
            reader.SkipWhitespace();
            return ReadAlpha(ref reader);
        }

        // CSS Color 4 §15: `color(<colorspace> <c1> <c2> <c3> [ / <alpha> ])`. Channels
        // are <number> or <percentage> in the [0, 1] range (100% == 1). Only sRGB-family
        // spaces are wired up downstream; CssColor.FromColorFunction rejects the rest.
        static CssValue ParseColorFunction(ref TokenReader reader, int col) {
            var startIndex = reader.SaveIndex();
            reader.SkipWhitespace();
            // CSS Color L5 §4: `color(<space> from <C> c1 c2 c3 [/ alpha])`.
            // Note the spec order is `<space> from ...` — the colorspace
            // identifier precedes the `from` keyword.
            var spaceTok = reader.Peek();
            if (spaceTok.Kind != CssTokenKind.Ident) {
                throw new CssValueParseException("Expected colorspace identifier in color()", spaceTok.Column);
            }
            string spaceLower = CssStringUtil.ToLowerInvariantOrSame(spaceTok.Text);
            reader.Advance();
            reader.SkipWhitespace();
            if (TryConsumeFromKeyword(ref reader)) {
                return ParseColorFunctionRelative(ref reader, col, startIndex, spaceLower);
            }
            double c1 = ReadColorFunctionChannel(ref reader);
            reader.SkipWhitespace();
            double c2 = ReadColorFunctionChannel(ref reader);
            reader.SkipWhitespace();
            double c3 = ReadColorFunctionChannel(ref reader);
            reader.SkipWhitespace();
            double alpha = ReadOptionalSlashAlpha(ref reader, "color");
            reader.SkipWhitespace();
            if (reader.AtEnd() || reader.Peek().Kind != CssTokenKind.RParen) {
                throw new CssValueParseException("Expected ')' to close color()", col);
            }
            reader.Advance();
            string raw = reader.SubstringFrom(startIndex, "color");
            return CssColor.FromColorFunction(spaceLower, c1, c2, c3, alpha, raw);
        }

        // color() channels: number is taken as-is in [0,1]; percentage maps 100% -> 1.0.
        static double ReadColorFunctionChannel(ref TokenReader reader) {
            var t = reader.Peek();
            if (t.Kind == CssTokenKind.Number) {
                reader.Advance();
                return t.Number;
            }
            if (t.Kind == CssTokenKind.Percentage) {
                reader.Advance();
                return t.Number / 100.0;
            }
            throw new CssValueParseException("Expected number or percentage for color() channel", t.Column);
        }

        static CssValue ParseColorMix(ref TokenReader reader, int col) {
            var startIndex = reader.SaveIndex();
            reader.SkipWhitespace();
            // Expect: in <space>
            var inTok = reader.Peek();
            CssColorSpace space = CssColorSpace.Oklab;
            CssHueInterpolationMethod hueMethod = CssHueInterpolationMethod.Shorter;
            if (inTok.Kind == CssTokenKind.Ident && CssStringUtil.EqualsIgnoreCase(inTok.Text, "in")) {
                reader.Advance();
                reader.SkipWhitespace();
                var spaceTok = reader.Peek();
                if (spaceTok.Kind != CssTokenKind.Ident) {
                    throw new CssValueParseException("Expected color space name after 'in' in color-mix()", spaceTok.Column);
                }
                if (!ColorMixer.TryParseSpaceName(spaceTok.Text, out space)) {
                    throw new CssValueParseException("Unknown color space '" + spaceTok.Text + "' in color-mix()", spaceTok.Column);
                }
                reader.Advance();
                reader.SkipWhitespace();
                // Optional <hue-interpolation-method>: "<shorter|longer|increasing|decreasing> hue".
                if (ColorMixer.IsCylindricalSpace(space)) {
                    var maybeMethod = reader.Peek();
                    if (maybeMethod.Kind == CssTokenKind.Ident && ColorMixer.TryParseHueMethod(maybeMethod.Text, out var parsedMethod)) {
                        int methodSave = reader.SaveIndex();
                        reader.Advance();
                        reader.SkipWhitespace();
                        var hueTok = reader.Peek();
                        if (hueTok.Kind == CssTokenKind.Ident && CssStringUtil.EqualsIgnoreCase(hueTok.Text, "hue")) {
                            reader.Advance();
                            reader.SkipWhitespace();
                            hueMethod = parsedMethod;
                        } else {
                            reader.RestoreIndex(methodSave);
                        }
                    }
                }
                ExpectComma(ref reader);
            }
            CssColor a = ReadColorMixComponent(ref reader, out double weightA, out bool aHadWeight);
            reader.SkipWhitespace();
            ExpectComma(ref reader);
            CssColor b = ReadColorMixComponent(ref reader, out double weightB, out bool bHadWeight);
            reader.SkipWhitespace();
            if (!aHadWeight && !bHadWeight) {
                weightA = 0.5; weightB = 0.5;
            } else if (aHadWeight && !bHadWeight) {
                weightB = 1.0 - weightA;
                if (weightB < 0) weightB = 0;
            } else if (!aHadWeight && bHadWeight) {
                weightA = 1.0 - weightB;
                if (weightA < 0) weightA = 0;
            }
            if (reader.AtEnd() || reader.Peek().Kind != CssTokenKind.RParen) {
                throw new CssValueParseException("Expected ')' to close color-mix()", col);
            }
            reader.Advance();
            string raw = reader.SubstringFrom(startIndex, "color-mix");
            return ColorMixer.Mix(a, b, space, weightA, weightB, hueMethod, raw);
        }

        static CssColor ReadColorMixComponent(ref TokenReader reader, out double weight, out bool hadWeight) {
            weight = 0.5;
            hadWeight = false;
            reader.SkipWhitespace();
            // Try percentage-first form: "<pct> <color>"
            var t = reader.Peek();
            if (t.Kind == CssTokenKind.Percentage) {
                reader.Advance();
                weight = t.Number / 100.0;
                hadWeight = true;
                reader.SkipWhitespace();
            }
            var color = ParseColorComponent(ref reader);
            reader.SkipWhitespace();
            // Or postfix form: "<color> <pct>"
            if (!hadWeight && !reader.AtEnd() && reader.Peek().Kind == CssTokenKind.Percentage) {
                var pTok = reader.Peek();
                reader.Advance();
                weight = pTok.Number / 100.0;
                hadWeight = true;
            }
            return color;
        }

        static CssColor ParseColorComponent(ref TokenReader reader) {
            var t = reader.Peek();
            if (t.Kind == CssTokenKind.Hash) {
                reader.Advance();
                return CssColor.FromHex(t.Text, t.Column);
            }
            if (t.Kind == CssTokenKind.Ident) {
                reader.Advance();
                if (CssColor.TryFromName(t.Text, out var named)) {
                    return new CssColor(named.R, named.G, named.B, named.A, t.Text);
                }
                throw new CssValueParseException("Unknown color name '" + t.Text + "'", t.Column);
            }
            if (t.Kind == CssTokenKind.Function) {
                var v = ParseFunction(ref reader);
                if (v is CssColor cc) return cc;
                throw new CssValueParseException("Expected a color in color-mix()", t.Column);
            }
            throw new CssValueParseException("Expected color in color-mix()", t.Column);
        }

        static double ReadHue(ref TokenReader reader) {
            var t = reader.Peek();
            if (t.Kind == CssTokenKind.Number) {
                reader.Advance();
                return t.Number;
            }
            if (t.Kind == CssTokenKind.Dimension) {
                reader.Advance();
                string u = CssStringUtil.ToLowerInvariantOrSame(t.Unit);
                switch (u) {
                    case "deg": return t.Number;
                    case "turn": return t.Number * 360.0;
                    case "rad": return t.Number * (180.0 / System.Math.PI);
                    case "grad": return t.Number * (360.0 / 400.0);
                }
                throw new CssValueParseException("Unknown hue unit '" + t.Unit + "'", t.Column);
            }
            throw new CssValueParseException("Expected hue value", t.Column);
        }

        static CssValue ParseCalc(ref TokenReader reader, int col) {
            var startIndex = reader.SaveIndex();
            reader.SkipWhitespace();
            var node = ParseCalcExpression(ref reader);
            reader.SkipWhitespace();
            if (reader.AtEnd() || reader.Peek().Kind != CssTokenKind.RParen) {
                throw new CssValueParseException("Expected ')' to close calc()", col);
            }
            reader.Advance();
            string raw = reader.SubstringFrom(startIndex, "calc");
            return new CssCalc(node, raw);
        }

        // CSS Values L4 §10: min(), max(), clamp() are math functions whose
        // arguments are comma-separated <calc-sum> expressions. We parse them
        // into a CalcMathNode and wrap in a CssCalc so the existing length
        // resolver path (StyleResolver.ResolveLength) handles them uniformly.
        static CssValue ParseMathFunction(ref TokenReader reader, string fnLower, int col) {
            var startIndex = reader.SaveIndex();
            reader.SkipWhitespace();

            // round() accepts an optional <rounding-strategy> keyword as the
            // leading argument (CSS Values L4 §10.7.1).
            var strategy = CalcRoundingStrategy.Nearest;
            if (fnLower == "round" && !reader.AtEnd() && reader.Peek().Kind == CssTokenKind.Ident) {
                string ident = CssStringUtil.ToLowerInvariantOrSame(reader.Peek().Text);
                if (TryParseRoundingStrategy(ident, out var s)) {
                    strategy = s;
                    reader.Advance();
                    reader.SkipWhitespace();
                    if (reader.AtEnd() || reader.Peek().Kind != CssTokenKind.Comma) {
                        throw new CssValueParseException("Expected ',' after round() strategy", col);
                    }
                    reader.Advance();
                }
            }

            var args = new List<CalcNode>();
            while (true) {
                reader.SkipWhitespace();
                var arg = ParseCalcExpression(ref reader);
                args.Add(arg);
                reader.SkipWhitespace();
                if (reader.AtEnd()) {
                    throw new CssValueParseException("Unterminated " + fnLower + "()", col);
                }
                if (reader.Peek().Kind == CssTokenKind.Comma) {
                    reader.Advance();
                    continue;
                }
                break;
            }
            if (reader.AtEnd() || reader.Peek().Kind != CssTokenKind.RParen) {
                throw new CssValueParseException("Expected ')' to close " + fnLower + "()", col);
            }
            reader.Advance();

            CalcMathFunction func;
            switch (fnLower) {
                case "min": func = CalcMathFunction.Min; break;
                case "max": func = CalcMathFunction.Max; break;
                case "clamp": func = CalcMathFunction.Clamp; break;
                case "round": func = CalcMathFunction.Round; break;
                case "mod": func = CalcMathFunction.Mod; break;
                case "rem": func = CalcMathFunction.Rem; break;
                case "pow": func = CalcMathFunction.Pow; break;
                case "sqrt": func = CalcMathFunction.Sqrt; break;
                case "hypot": func = CalcMathFunction.Hypot; break;
                case "log": func = CalcMathFunction.Log; break;
                case "exp": func = CalcMathFunction.Exp; break;
                case "abs": func = CalcMathFunction.Abs; break;
                case "sign": func = CalcMathFunction.Sign; break;
                case "sin": func = CalcMathFunction.Sin; break;
                case "cos": func = CalcMathFunction.Cos; break;
                case "tan": func = CalcMathFunction.Tan; break;
                case "asin": func = CalcMathFunction.Asin; break;
                case "acos": func = CalcMathFunction.Acos; break;
                case "atan": func = CalcMathFunction.Atan; break;
                case "atan2": func = CalcMathFunction.Atan2; break;
                default: throw new CssValueParseException("Unknown math function " + fnLower, col);
            }

            ValidateMathArgCount(func, fnLower, args.Count, col);

            string raw = reader.SubstringFrom(startIndex, fnLower);
            var node = strategy == CalcRoundingStrategy.Nearest
                ? new CalcMathNode(func, args)
                : new CalcMathNode(func, args, strategy);
            return new CssCalc(node, raw);
        }

        static void ValidateMathArgCount(CalcMathFunction func, string fnLower, int count, int col) {
            switch (func) {
                case CalcMathFunction.Clamp:
                    if (count != 3) throw new CssValueParseException("clamp() requires exactly 3 arguments (MIN, VAL, MAX)", col);
                    return;
                case CalcMathFunction.Min:
                case CalcMathFunction.Max:
                case CalcMathFunction.Hypot:
                    if (count == 0) throw new CssValueParseException(fnLower + "() requires at least 1 argument", col);
                    return;
                case CalcMathFunction.Round:
                case CalcMathFunction.Mod:
                case CalcMathFunction.Rem:
                case CalcMathFunction.Pow:
                case CalcMathFunction.Atan2:
                    if (count != 2) throw new CssValueParseException(fnLower + "() requires exactly 2 arguments", col);
                    return;
                case CalcMathFunction.Sqrt:
                case CalcMathFunction.Exp:
                case CalcMathFunction.Abs:
                case CalcMathFunction.Sign:
                case CalcMathFunction.Sin:
                case CalcMathFunction.Cos:
                case CalcMathFunction.Tan:
                case CalcMathFunction.Asin:
                case CalcMathFunction.Acos:
                case CalcMathFunction.Atan:
                    if (count != 1) throw new CssValueParseException(fnLower + "() requires exactly 1 argument", col);
                    return;
                case CalcMathFunction.Log:
                    if (count != 1 && count != 2) throw new CssValueParseException("log() requires 1 or 2 arguments", col);
                    return;
            }
        }

        static bool TryParseRoundingStrategy(string lower, out CalcRoundingStrategy strategy) {
            switch (lower) {
                case "nearest": strategy = CalcRoundingStrategy.Nearest; return true;
                case "up": strategy = CalcRoundingStrategy.Up; return true;
                case "down": strategy = CalcRoundingStrategy.Down; return true;
                case "to-zero": strategy = CalcRoundingStrategy.ToZero; return true;
            }
            strategy = CalcRoundingStrategy.Nearest;
            return false;
        }

        static bool IsMathFunctionName(string lower) {
            switch (lower) {
                case "min":
                case "max":
                case "clamp":
                case "round":
                case "mod":
                case "rem":
                case "pow":
                case "sqrt":
                case "hypot":
                case "log":
                case "exp":
                case "abs":
                case "sign":
                case "sin":
                case "cos":
                case "tan":
                case "asin":
                case "acos":
                case "atan":
                case "atan2":
                    return true;
            }
            return false;
        }

        // PH1: hard cap on calc() nesting. `calc(` + thousands of `(` drove
        // the Expression <-> Term <-> Factor mutual recursion into an
        // UNCATCHABLE StackOverflow — and this parse runs lazily at
        // value-RESOLVE time, so one property value could crash a shipped
        // app. Over-depth throws CssValueParseException, which the TryParse
        // machinery already treats as "invalid value" (declaration dropped).
        const int MaxCalcDepth = 64;

        static CalcNode ParseCalcExpression(ref TokenReader reader) {
            if (++reader.CalcDepth > MaxCalcDepth) {
                reader.CalcDepth--;
                throw new CssValueParseException(
                    "calc() nesting deeper than " + MaxCalcDepth + " levels", 1);
            }
            try {
                return ParseCalcExpressionInner(ref reader);
            } finally {
                reader.CalcDepth--;
            }
        }

        static CalcNode ParseCalcExpressionInner(ref TokenReader reader) {
            var left = ParseCalcTerm(ref reader);
            while (true) {
                bool hadWsBefore = reader.HasWhitespaceBeforeNextNonWs();
                if (reader.AtEnd()) break;
                var t = reader.Peek();
                if (t.Kind == CssTokenKind.Dimension || t.Kind == CssTokenKind.Number || t.Kind == CssTokenKind.Percentage) {
                    string text = t.Text ?? "";
                    if (text.Length > 0 && (text[0] == '+' || text[0] == '-')) {
                        throw new CssValueParseException("calc() requires whitespace around '" + text[0] + "'", t.Column);
                    }
                    break;
                }
                if (t.Kind != CssTokenKind.Delim) break;
                if (t.Text != "+" && t.Text != "-") break;
                if (!hadWsBefore) {
                    throw new CssValueParseException("calc() requires whitespace around '" + t.Text + "'", t.Column);
                }
                int beforeOp = reader.SaveIndex();
                reader.Advance();
                bool hadWsAfter = reader.HasWhitespaceBeforeNextNonWs();
                if (!hadWsAfter) {
                    throw new CssValueParseException("calc() requires whitespace around '" + t.Text + "'", t.Column);
                }
                var right = ParseCalcTerm(ref reader);
                CalcOp op = t.Text == "+" ? CalcOp.Add : CalcOp.Sub;
                left = new CalcBinaryNode(op, left, right);
            }
            return left;
        }

        static CalcNode ParseCalcTerm(ref TokenReader reader) {
            var left = ParseCalcFactor(ref reader);
            while (true) {
                int saved = reader.SaveIndex();
                reader.SkipWhitespace();
                if (reader.AtEnd()) {
                    reader.RestoreIndex(saved);
                    break;
                }
                var t = reader.Peek();
                if (t.Kind != CssTokenKind.Delim || (t.Text != "*" && t.Text != "/")) {
                    reader.RestoreIndex(saved);
                    break;
                }
                reader.Advance();
                reader.SkipWhitespace();
                var right = ParseCalcFactor(ref reader);
                CalcOp op = t.Text == "*" ? CalcOp.Mul : CalcOp.Div;
                left = new CalcBinaryNode(op, left, right);
            }
            return left;
        }

        static CalcNode ParseCalcFactor(ref TokenReader reader) {
            reader.SkipWhitespace();
            if (reader.AtEnd()) {
                throw new CssValueParseException("Unexpected end of calc() expression", 1);
            }
            var t = reader.Peek();
            if (t.Kind == CssTokenKind.LParen) {
                reader.Advance();
                var inner = ParseCalcExpression(ref reader);
                reader.SkipWhitespace();
                if (reader.AtEnd() || reader.Peek().Kind != CssTokenKind.RParen) {
                    throw new CssValueParseException("Unmatched '(' in calc()", t.Column);
                }
                reader.Advance();
                return inner;
            }
            if (t.Kind == CssTokenKind.Function && CssStringUtil.EqualsIgnoreCase(t.Text, "var")) {
                reader.Advance();
                var v = ParseVar(ref reader, t.Column);
                return new CalcVariableNode((CssVariableReference)v);
            }
            if (t.Kind == CssTokenKind.Function && CssStringUtil.EqualsIgnoreCase(t.Text, "calc")) {
                reader.Advance();
                reader.SkipWhitespace();
                var inner = ParseCalcExpression(ref reader);
                reader.SkipWhitespace();
                if (reader.AtEnd() || reader.Peek().Kind != CssTokenKind.RParen) {
                    throw new CssValueParseException("Unmatched 'calc(' in calc()", t.Column);
                }
                reader.Advance();
                return inner;
            }
            if (t.Kind == CssTokenKind.Function) {
                string fnLower = CssStringUtil.ToLowerInvariantOrSame(t.Text);
                if (IsMathFunctionName(fnLower)) {
                    reader.Advance();
                    var math = (CssCalc)ParseMathFunction(ref reader, fnLower, t.Column);
                    return math.Expression;
                }
            }
            if (t.Kind == CssTokenKind.Number) {
                reader.Advance();
                return new CalcNumberNode(CssValuePool.RentNumber(t.Number, t.Text));
            }
            if (t.Kind == CssTokenKind.Dimension) {
                reader.Advance();
                string unitLower = CssStringUtil.ToLowerInvariantOrSame(t.Unit);
                // CSS Values L4 §10.8: <angle> dimensions are valid inside math
                // functions (sin/cos/tan accept them). Convert to canonical
                // degrees so the evaluator handles them uniformly.
                if (CssAngle.TryParseUnit(unitLower, out var angleUnit)) {
                    var ang = new CssAngle(t.Number, angleUnit, t.Text);
                    return new CalcAngleNode(ang.ToDegrees(), t.Text);
                }
                if (!CssLength.TryParseUnit(unitLower, out var unit)) {
                    throw new CssValueParseException("Unknown length unit '" + t.Unit + "' in calc()", t.Column);
                }
                return new CalcLengthNode(CssValuePool.RentLength(t.Number, unit, t.Text));
            }
            if (t.Kind == CssTokenKind.Percentage) {
                reader.Advance();
                return new CalcPercentageNode(CssValuePool.RentPercentage(t.Number, t.Text));
            }
            // CSS Color L5 §4: channel-ident reference inside a relative-color
            // calc() slot. The host color-parser registers the legal channel
            // names on the TokenReader before recursing into ParseCalc; here we
            // pick them up and emit a CalcChannelNode (typed as <number> via
            // ClassifyType so `r + 20` is accepted by the type checker).
            if (t.Kind == CssTokenKind.Ident) {
                string lower = CssStringUtil.ToLowerInvariantOrSame(t.Text);
                if (reader.IsRelativeColorChannel(lower)) {
                    reader.Advance();
                    return new CalcChannelNode(lower);
                }
            }
            throw new CssValueParseException("Unexpected token '" + (t.Text ?? t.Kind.ToString()) + "' in calc()", t.Column);
        }

        struct TokenReader {
            readonly List<CssToken> tokens;
            readonly string source;
            int index;
            // PH1: calc() nesting depth — see ParseCalcExpression.
            public int CalcDepth;
            // CSS Color L5 §4: when non-null, identifiers in this set are
            // resolved as channel-ident references (CalcChannelNode) inside
            // calc() factors. Set by the relative-color parser path; cleared
            // when leaving the relative-color slot.
            HashSet<string> relativeColorChannels;

            public TokenReader(List<CssToken> tokens, string source) {
                this.tokens = tokens;
                this.source = source;
                index = 0;
                CalcDepth = 0;
                relativeColorChannels = null;
            }

            public void SetRelativeColorChannels(HashSet<string> channels) {
                relativeColorChannels = channels;
            }

            public bool IsRelativeColorChannel(string name) {
                if (relativeColorChannels == null) return false;
                return relativeColorChannels.Contains(name);
            }

            public bool AtEnd() {
                int i = index;
                while (i < tokens.Count && tokens[i].Kind == CssTokenKind.Whitespace) i++;
                return i >= tokens.Count || tokens[i].Kind == CssTokenKind.Eof;
            }

            public CssToken Peek() {
                int i = index;
                while (i < tokens.Count && tokens[i].Kind == CssTokenKind.Whitespace) i++;
                if (i >= tokens.Count) {
                    return tokens[tokens.Count - 1];
                }
                return tokens[i];
            }

            public void Advance() {
                while (index < tokens.Count && tokens[index].Kind == CssTokenKind.Whitespace) index++;
                if (index < tokens.Count && tokens[index].Kind != CssTokenKind.Eof) index++;
            }

            public void SkipWhitespace() {
                while (index < tokens.Count && tokens[index].Kind == CssTokenKind.Whitespace) index++;
            }

            public bool SkipWhitespaceReturning() {
                bool any = false;
                while (index < tokens.Count && tokens[index].Kind == CssTokenKind.Whitespace) {
                    any = true;
                    index++;
                }
                return any;
            }

            public int SaveIndex() => index;

            public void RestoreIndex(int saved) { index = saved; }

            public bool HasWhitespaceBeforeNextNonWs() {
                int i = index;
                bool hasWs = false;
                while (i < tokens.Count && tokens[i].Kind == CssTokenKind.Whitespace) {
                    hasWs = true;
                    i++;
                }
                return hasWs;
            }

            public char OriginalQuoteAt(CssToken t) {
                if (t.Line == 1) {
                    int idx = t.Column - 1;
                    if (idx >= 0 && idx < source.Length) {
                        char c = source[idx];
                        if (c == '"' || c == '\'') return c;
                    }
                }
                return '"';
            }

            public string SubstringFrom(int savedIndex, string fnName) {
                var sb = new StringBuilder();
                sb.Append(fnName);
                sb.Append('(');
                bool needsSpace = false;
                int end = index;
                if (end > 0 && end <= tokens.Count && tokens[end - 1].Kind == CssTokenKind.RParen) {
                    end -= 1;
                }
                for (int i = savedIndex; i < end; i++) {
                    var tk = tokens[i];
                    if (tk.Kind == CssTokenKind.Whitespace) {
                        needsSpace = true;
                        continue;
                    }
                    if (tk.Kind == CssTokenKind.Eof) break;
                    if (needsSpace && sb.Length > fnName.Length + 1 && sb[sb.Length - 1] != '(') {
                        sb.Append(' ');
                    }
                    needsSpace = false;
                    AppendToken(sb, tk);
                }
                if (sb.Length > 0 && sb[sb.Length - 1] == ' ') sb.Length -= 1;
                sb.Append(')');
                return sb.ToString();
            }

            static void AppendToken(StringBuilder sb, CssToken t) {
                switch (t.Kind) {
                    case CssTokenKind.Number:
                    case CssTokenKind.Dimension:
                    case CssTokenKind.Percentage:
                        sb.Append(t.Text);
                        return;
                    case CssTokenKind.Hash:
                        sb.Append('#');
                        sb.Append(t.Text);
                        return;
                    case CssTokenKind.AtKeyword:
                        sb.Append('@');
                        sb.Append(t.Text);
                        return;
                    case CssTokenKind.String:
                        sb.Append('"');
                        sb.Append(t.Text);
                        sb.Append('"');
                        return;
                    case CssTokenKind.Function:
                        sb.Append(t.Text);
                        sb.Append('(');
                        return;
                    case CssTokenKind.Url:
                        sb.Append("url(");
                        sb.Append(t.Text);
                        sb.Append(')');
                        return;
                    default:
                        sb.Append(t.Text ?? "");
                        return;
                }
            }
        }
    }
}
