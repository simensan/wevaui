using System.Collections.Generic;
using System.Globalization;
using Weva.Css.Cascade;
using Weva.Css.Values;

namespace Weva.Layout.Grid {
    public struct GridItemProperties {
        public GridItemPlacement Placement;
        public JustifySelf JustifySelf;
        public AlignSelf AlignSelf;
        public int Order;

        public static GridItemProperties From(ComputedStyle style, LengthContext lengthCtx) {
            var p = new GridItemProperties {
                Placement = GridItemPlacement.AllAuto,
                JustifySelf = JustifySelf.Auto,
                AlignSelf = AlignSelf.Auto,
                Order = 0
            };
            if (style == null) return p;

            // Reads land via GetParsed first; only the slash-split shorthand
            // path (grid-area / grid-row / grid-column) still reaches for the
            // raw text because shorthand values use the `/` delim which the
            // CssValueParser emits as a CssIdentifier ("/") inside a space
            // separated CssValueList. Splitting the typed list on that token
            // is straightforward — see SplitParsedOnSlash below.
            var areaParsed = style.GetParsed(CssProperties.GridAreaId);
            var rowShParsed = style.GetParsed(CssProperties.GridRowId);
            var colShParsed = style.GetParsed(CssProperties.GridColumnId);
            var rowStartParsed = style.GetParsed(CssProperties.GridRowStartId);
            var rowEndParsed = style.GetParsed(CssProperties.GridRowEndId);
            var colStartParsed = style.GetParsed(CssProperties.GridColumnStartId);
            var colEndParsed = style.GetParsed(CssProperties.GridColumnEndId);

            string areaRaw = style.Get(CssProperties.GridAreaId);

            string areaName = null;
            var rs = GridLineRef.Auto;
            var re = GridLineRef.Auto;
            var cs = GridLineRef.Auto;
            var ce = GridLineRef.Auto;

            // grid-area: at the initial value `auto` we don't touch the
            // longhand-derived refs (cascade fills longhands independently).
            if (!IsInitial(areaRaw)) {
                var parsedParts = SplitParsedOnSlash(areaParsed);
                if (parsedParts != null) {
                    if (parsedParts.Count == 1) {
                        // Single-token grid-area: either a named-area
                        // reference (CssIdentifier) or a single line-ref
                        // applied to all four positions.
                        var only = parsedParts[0];
                        if (TryParseAreaName(only, out var name)) {
                            areaName = name;
                        } else {
                            var p1 = ParseLineRefParsed(only);
                            rs = p1; cs = p1; re = MirrorEnd(p1); ce = MirrorEnd(p1);
                        }
                    } else if (parsedParts.Count == 4) {
                        rs = ParseLineRefParsed(parsedParts[0]);
                        cs = ParseLineRefParsed(parsedParts[1]);
                        re = ParseLineRefParsed(parsedParts[2]);
                        ce = ParseLineRefParsed(parsedParts[3]);
                    }
                } else {
                    // Raw-string fallback: parse tree wasn't a usable value
                    // (e.g., contained a `[name]` bracket form which the
                    // CssValueParser can't represent). Reuse the proven
                    // string path.
                    var parts = SplitTopLevelSlash(areaRaw);
                    if (parts.Count == 1) {
                        string only = parts[0].Trim();
                        if (LooksLikeIdentifier(only)) {
                            areaName = only;
                        } else {
                            var p1 = ParseLineRef(only);
                            rs = p1; cs = p1; re = MirrorEnd(p1); ce = MirrorEnd(p1);
                        }
                    } else if (parts.Count == 4) {
                        rs = ParseLineRef(parts[0]);
                        cs = ParseLineRef(parts[1]);
                        re = ParseLineRef(parts[2]);
                        ce = ParseLineRef(parts[3]);
                    }
                }
            }

            // grid-row / grid-column shorthands: <line> [ / <line> ]. The
            // typed parse tree is either a single line-ref CssValue or a
            // CssValueList (space) carrying a `/` identifier in the middle.
            string rowShRaw = style.Get(CssProperties.GridRowId);
            if (!IsInitial(rowShRaw)) {
                var parts = SplitParsedOnSlash(rowShParsed);
                if (parts != null) {
                    if (parts.Count >= 1) rs = ParseLineRefParsed(parts[0]);
                    re = parts.Count >= 2 ? ParseLineRefParsed(parts[1]) : GridLineRef.Auto;
                } else {
                    var rawParts = SplitTopLevelSlash(rowShRaw);
                    if (rawParts.Count >= 1) rs = ParseLineRef(rawParts[0]);
                    re = rawParts.Count >= 2 ? ParseLineRef(rawParts[1]) : GridLineRef.Auto;
                }
            }
            string colShRaw = style.Get(CssProperties.GridColumnId);
            if (!IsInitial(colShRaw)) {
                var parts = SplitParsedOnSlash(colShParsed);
                if (parts != null) {
                    if (parts.Count >= 1) cs = ParseLineRefParsed(parts[0]);
                    ce = parts.Count >= 2 ? ParseLineRefParsed(parts[1]) : GridLineRef.Auto;
                } else {
                    var rawParts = SplitTopLevelSlash(colShRaw);
                    if (rawParts.Count >= 1) cs = ParseLineRef(rawParts[0]);
                    ce = rawParts.Count >= 2 ? ParseLineRef(rawParts[1]) : GridLineRef.Auto;
                }
            }

            // Per-axis longhands. Each is either a CssNumber (line index), a
            // CssKeyword/CssIdentifier (named line or `auto`), or a
            // CssValueList in the `span <n>` / `span <name>` shape.
            if (TryReadLineRef(style, rowStartParsed, CssProperties.GridRowStartId, out var rsR)) rs = rsR;
            if (TryReadLineRef(style, rowEndParsed, CssProperties.GridRowEndId, out var reR)) re = reR;
            if (TryReadLineRef(style, colStartParsed, CssProperties.GridColumnStartId, out var csR)) cs = csR;
            if (TryReadLineRef(style, colEndParsed, CssProperties.GridColumnEndId, out var ceR)) ce = ceR;

            p.Placement = new GridItemPlacement(rs, re, cs, ce, areaName);

            var jsParsed = style.GetParsed(CssProperties.JustifySelfId);
            var alsParsed = style.GetParsed(CssProperties.AlignSelfId);
            string js = style.Get(CssProperties.JustifySelfId);
            string als = style.Get(CssProperties.AlignSelfId);
            if (IsInitial(js) && IsInitial(als)) {
                string place = style.Get(CssProperties.PlaceSelfId);
                if (!IsInitial(place)) {
                    var split = GridShorthand.SplitPlaceShorthand(place);
                    als = split.a; js = split.b;
                    // Shorthand expansion produced freshly-split strings, so
                    // the typed fast path is no longer applicable.
                    jsParsed = null; alsParsed = null;
                }
            }
            if (!IsInitial(js)) p.JustifySelf = ParseJustifySelfParsed(jsParsed, js, p.JustifySelf);
            if (!IsInitial(als)) p.AlignSelf = ParseAlignSelfParsed(alsParsed, als, p.AlignSelf);

            // `order` is an <integer>; parsed cache returns a CssNumber.
            var orderParsed = style.GetParsed(CssProperties.OrderId);
            if (orderParsed is CssNumber on) {
                p.Order = (int)on.Value;
            } else {
                // Raw fallback retained for parity with the prior path — only
                // hit when the slot is unset or holds a value the typed parser
                // doesn't recognise (none expected for <integer>).
                string orderRaw = style.Get(CssProperties.OrderId);
                if (!string.IsNullOrEmpty(orderRaw) && CssValue.TryParse(orderRaw, out var ov)) {
                    if (ov is CssNumber n) p.Order = (int)n.Value;
                }
            }

            return p;
        }

        static bool IsInitial(string raw) {
            return string.IsNullOrEmpty(raw) || raw == "auto";
        }

        static GridLineRef MirrorEnd(GridLineRef start) {
            // grid-area: foo with a single non-name token applies to all four positions.
            // For end positions without a name, this becomes "auto" which auto-resolves
            // to span 1; with an integer it resolves to that exact line.
            return start;
        }

        // Reads a single per-axis longhand. Returns true when the slot is
        // explicitly set (i.e. the cascade put a non-`auto` value there) so
        // the caller doesn't clobber a shorthand-derived ref with the
        // longhand's initial value. Mirrors the prior `!IsInitial(raw)` gate.
        static bool TryReadLineRef(ComputedStyle style, CssValue parsed, int propertyId, out GridLineRef result) {
            string raw = style.Get(propertyId);
            if (IsInitial(raw)) { result = GridLineRef.Auto; return false; }
            if (parsed != null) {
                result = ParseLineRefParsed(parsed);
                return true;
            }
            result = ParseLineRef(raw);
            return true;
        }

        // Walks a parsed CssValue and produces a GridLineRef. Mirrors the
        // semantics of the raw-text ParseLineRef walker:
        //   - CssKeyword/CssIdentifier "auto"             -> Auto
        //   - CssNumber                                    -> IndexValue(n)
        //   - CssIdentifier <name>                         -> NameValue(name)
        //   - CssValueList: walks items, collects span /
        //                   index / name in the same way.
        static GridLineRef ParseLineRefParsed(CssValue v) {
            if (v == null) return GridLineRef.Auto;
            if (v is CssKeyword k && string.Equals(k.Identifier, "auto", System.StringComparison.OrdinalIgnoreCase)) return GridLineRef.Auto;
            if (v is CssIdentifier id) {
                if (string.Equals(id.Name, "auto", System.StringComparison.OrdinalIgnoreCase)) return GridLineRef.Auto;
                if (string.Equals(id.Name, "span", System.StringComparison.OrdinalIgnoreCase)) {
                    // Bare "span" without a count or name -> span 1 per spec.
                    return GridLineRef.Span(1);
                }
                return GridLineRef.NameValue(id.Name);
            }
            if (v is CssNumber n) return GridLineRef.IndexValue((int)n.Value);
            if (v is CssValueList list) {
                bool isSpan = false;
                int? idx = null;
                string name = null;
                for (int i = 0; i < list.Items.Count; i++) {
                    var item = list.Items[i];
                    if (item is CssKeyword kw && string.Equals(kw.Identifier, "span", System.StringComparison.OrdinalIgnoreCase)) {
                        isSpan = true;
                        continue;
                    }
                    if (item is CssIdentifier ii) {
                        if (string.Equals(ii.Name, "span", System.StringComparison.OrdinalIgnoreCase)) {
                            isSpan = true;
                            continue;
                        }
                        // Skip a stray `/` if it leaks here — the shorthand
                        // splitter strips these before calling us, but be
                        // defensive against malformed inputs.
                        if (ii.Name == "/") continue;
                        name = ii.Name;
                        continue;
                    }
                    if (item is CssNumber nn) {
                        idx = (int)nn.Value;
                        continue;
                    }
                    // Unknown sub-token: ignore and continue. The raw fallback
                    // does the same (it ignores tokens that fail both
                    // int.TryParse and the span check).
                }
                if (isSpan) {
                    if (idx.HasValue) return GridLineRef.Span(idx.Value);
                    if (name != null) return GridLineRef.SpanName(name);
                    return GridLineRef.Span(1);
                }
                // `<custom-ident> <integer>`: pick the Nth named line.
                if (name != null && idx.HasValue) return GridLineRef.NameValue(name, idx.Value);
                if (idx.HasValue) return GridLineRef.IndexValue(idx.Value);
                if (name != null) return GridLineRef.NameValue(name);
            }
            return GridLineRef.Auto;
        }

        // Splits a parsed value on the `/` identifier token. Returns:
        //   - null  when the parse tree isn't a usable list (caller falls
        //           through to the raw-text splitter, e.g., for `[name]`
        //           bracket forms that the CssValueParser can't represent),
        //   - a List<CssValue> of segments otherwise. Single-segment inputs
        //     wrap the original parse tree; multi-segment inputs wrap each
        //     run as a CssValueList when it spans multiple tokens.
        static List<CssValue> SplitParsedOnSlash(CssValue v) {
            if (v == null) return null;
            // Single value with no list: there's no `/` to split, return as-is.
            if (!(v is CssValueList list)) {
                return new List<CssValue> { v };
            }
            if (list.Separator != CssValueListSeparator.Space) {
                // Comma-separated lists aren't a shape we expect for grid line
                // shorthands — fall back to the raw splitter.
                return null;
            }
            var segs = new List<CssValue>();
            var current = new List<CssValue>();
            for (int i = 0; i < list.Items.Count; i++) {
                var it = list.Items[i];
                if (it is CssIdentifier ident && ident.Name == "/") {
                    segs.Add(FlattenSeg(current));
                    current = new List<CssValue>();
                    continue;
                }
                current.Add(it);
            }
            segs.Add(FlattenSeg(current));
            // Drop trailing/leading empty segs the way the raw splitter does.
            for (int i = segs.Count - 1; i >= 0; i--) {
                if (segs[i] == null) segs.RemoveAt(i);
            }
            return segs;
        }

        static CssValue FlattenSeg(List<CssValue> items) {
            if (items.Count == 0) return null;
            if (items.Count == 1) return items[0];
            return new CssValueList(items, CssValueListSeparator.Space);
        }

        // Single-token grid-area can be either a name (no `auto`, no
        // span/number) or a single line-ref. The typed CssIdentifier path
        // already filtered `auto`/`span` via ParseLineRefParsed; here we
        // detect "this is a plain name" so the caller can route to areaName
        // instead of the line-ref. Matches LooksLikeIdentifier's intent.
        static bool TryParseAreaName(CssValue v, out string name) {
            name = null;
            if (v is CssIdentifier id) {
                string s = id.Name;
                if (string.IsNullOrEmpty(s)) return false;
                if (string.Equals(s, "auto", System.StringComparison.OrdinalIgnoreCase)) return false;
                if (string.Equals(s, "span", System.StringComparison.OrdinalIgnoreCase)) return false;
                if (!LooksLikeIdentifier(s)) return false;
                name = s;
                return true;
            }
            if (v is CssKeyword k) {
                string s = k.Identifier;
                if (string.IsNullOrEmpty(s)) return false;
                if (s == "auto" || s == "span") return false;
                if (!LooksLikeIdentifier(s)) return false;
                name = s;
                return true;
            }
            return false;
        }

        static List<string> SplitTopLevelSlash(string s) {
            var parts = new List<string>();
            int start = 0;
            int depthParen = 0, depthBrack = 0;
            for (int i = 0; i < s.Length; i++) {
                char c = s[i];
                if (c == '(') depthParen++;
                else if (c == ')') depthParen--;
                else if (c == '[') depthBrack++;
                else if (c == ']') depthBrack--;
                else if (c == '/' && depthParen == 0 && depthBrack == 0) {
                    parts.Add(s.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
            parts.Add(s.Substring(start).Trim());
            for (int i = parts.Count - 1; i >= 0; i--) {
                if (parts[i].Length == 0) parts.RemoveAt(i);
            }
            return parts;
        }

        static GridLineRef ParseLineRef(string s) {
            s = s.Trim();
            if (string.IsNullOrEmpty(s)) return GridLineRef.Auto;
            string lower = CssStringUtil.ToLowerInvariantOrSame(s);
            if (lower == "auto") return GridLineRef.Auto;

            var parts = new List<string>();
            int i = 0;
            while (i < s.Length) {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                if (i >= s.Length) break;
                int start = i;
                while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
                parts.Add(s.Substring(start, i - start));
            }

            bool isSpan = false;
            int? idx = null;
            string name = null;

            foreach (var raw in parts) {
                if (string.Equals(raw, "span", System.StringComparison.OrdinalIgnoreCase)) {
                    isSpan = true;
                    continue;
                }
                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) {
                    idx = v;
                    continue;
                }
                name = raw;
            }

            if (isSpan) {
                if (idx.HasValue) return GridLineRef.Span(idx.Value);
                if (name != null) return GridLineRef.SpanName(name);
                return GridLineRef.Span(1);
            }
            // `<custom-ident> <integer>`: pick the Nth named line.
            if (name != null && idx.HasValue) return GridLineRef.NameValue(name, idx.Value);
            if (idx.HasValue) return GridLineRef.IndexValue(idx.Value);
            if (name != null) return GridLineRef.NameValue(name);
            return GridLineRef.Auto;
        }

        static bool LooksLikeIdentifier(string s) {
            if (string.IsNullOrEmpty(s)) return false;
            char c = s[0];
            if (c != '_' && !char.IsLetter(c) && c != '-') return false;
            if (s == "auto") return false;
            for (int i = 0; i < s.Length; i++) {
                char ch = s[i];
                if (!(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-')) return false;
            }
            return true;
        }

        // Typed-keyword dispatch for the two *-self longhands. Single-token
        // CssKeyword/CssIdentifier inputs short-circuit the raw-string
        // trim+lowercase+switch path. Anything else (incl. malformed
        // multi-token values that the cascade still let through) falls
        // through to the prior raw-string parser.
        static bool TryGetKeywordIdent(CssValue v, out string ident) {
            if (v is CssKeyword k) { ident = k.Identifier; return true; }
            if (v is CssIdentifier id) { ident = id.Name; return true; }
            ident = null;
            return false;
        }

        // E6b: CSS Box Alignment L3 §6.2 allows a `safe`/`unsafe` positional
        // prefix before the alignment keyword (e.g. `align-self: safe end`).
        // Overflow-fallback semantics at layout time are deferred — for v1
        // `safe` and `unsafe` parse identically. Peel the 2-token list to the
        // alignment keyword so downstream dispatch sees "end" not the
        // CssValueList. Single-token forms pass through unchanged.
        static CssValue UnwrapOverflowPosition(CssValue v) {
            if (v is CssValueList list
                && list.Separator == CssValueListSeparator.Space
                && list.Items.Count == 2) {
                string first = null;
                if (list.Items[0] is CssKeyword fk) first = fk.Identifier;
                else if (list.Items[0] is CssIdentifier fi) first = CssStringUtil.ToLowerInvariantOrSame(fi.Name);
                if (first == "safe" || first == "unsafe") return list.Items[1];
            }
            return v;
        }

        // E6b string-path counterpart: peels a leading `safe `/`unsafe ` token
        // off the raw value so the existing keyword switch matches the
        // alignment keyword.
        static string StripOverflowPositionPrefix(string s) {
            if (string.IsNullOrEmpty(s)) return s;
            string t = s.Trim();
            int spaceIdx = -1;
            for (int i = 0; i < t.Length; i++) {
                if (char.IsWhiteSpace(t[i])) { spaceIdx = i; break; }
            }
            if (spaceIdx <= 0) return s;
            string head = t.Substring(0, spaceIdx);
            string headLower = CssStringUtil.ToLowerInvariantOrSame(head);
            if (headLower != "safe" && headLower != "unsafe") return s;
            int tailStart = spaceIdx + 1;
            while (tailStart < t.Length && char.IsWhiteSpace(t[tailStart])) tailStart++;
            if (tailStart >= t.Length) return s;
            return t.Substring(tailStart);
        }

        static JustifySelf ParseJustifySelfParsed(CssValue parsed, string raw, JustifySelf fallback) {
            parsed = UnwrapOverflowPosition(parsed);
            if (TryGetKeywordIdent(parsed, out var ident)) {
                switch (CssStringUtil.ToLowerInvariantOrSame(ident)) {
                    case "auto":
                    case "normal": return JustifySelf.Auto;
                    case "start":
                    case "flex-start":
                    case "left":
                    case "self-start": return JustifySelf.Start;
                    case "end":
                    case "flex-end":
                    case "right":
                    case "self-end": return JustifySelf.End;
                    case "center": return JustifySelf.Center;
                    case "stretch": return JustifySelf.Stretch;
                }
                return fallback;
            }
            return ParseJustifySelf(raw, fallback);
        }

        static AlignSelf ParseAlignSelfParsed(CssValue parsed, string raw, AlignSelf fallback) {
            parsed = UnwrapOverflowPosition(parsed);
            if (TryGetKeywordIdent(parsed, out var ident)) {
                switch (CssStringUtil.ToLowerInvariantOrSame(ident)) {
                    case "auto":
                    case "normal": return AlignSelf.Auto;
                    case "start":
                    case "flex-start":
                    case "self-start": return AlignSelf.Start;
                    case "end":
                    case "flex-end":
                    case "self-end": return AlignSelf.End;
                    case "center": return AlignSelf.Center;
                    case "stretch": return AlignSelf.Stretch;
                }
                return fallback;
            }
            return ParseAlignSelf(raw, fallback);
        }

        static JustifySelf ParseJustifySelf(string s, JustifySelf fallback) {
            s = StripOverflowPositionPrefix(s);
            switch (CssStringUtil.ToLowerInvariantOrSame(s.Trim())) {
                case "auto":
                case "normal": return JustifySelf.Auto;
                case "start":
                case "flex-start":
                case "left":
                case "self-start": return JustifySelf.Start;
                case "end":
                case "flex-end":
                case "right":
                case "self-end": return JustifySelf.End;
                case "center": return JustifySelf.Center;
                case "stretch": return JustifySelf.Stretch;
            }
            return fallback;
        }

        static AlignSelf ParseAlignSelf(string s, AlignSelf fallback) {
            s = StripOverflowPositionPrefix(s);
            switch (CssStringUtil.ToLowerInvariantOrSame(s.Trim())) {
                case "auto":
                case "normal": return AlignSelf.Auto;
                case "start":
                case "flex-start":
                case "self-start": return AlignSelf.Start;
                case "end":
                case "flex-end":
                case "self-end": return AlignSelf.End;
                case "center": return AlignSelf.Center;
                case "stretch": return AlignSelf.Stretch;
            }
            return fallback;
        }
    }
}
