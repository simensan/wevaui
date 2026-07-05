using System.Collections.Generic;
using Weva.Css.Cascade;
using Weva.Css.Values;

namespace Weva.Layout.Grid {
    public struct GridContainerProperties {
        public GridTemplate Columns;
        public GridTemplate Rows;
        public GridTrackSize[] AutoColumns;
        public GridTrackSize[] AutoRows;
        public GridAutoFlow AutoFlow;
        public double RowGap;
        public double ColumnGap;
        public JustifyContent JustifyContent;
        public AlignContent AlignContent;
        public JustifyItems JustifyItems;
        public AlignItems AlignItems;
        public GridAreasParser.AreasMap Areas;
        // Subgrid flags (CSS Grid Level 2). When set, GridLayout will replace
        // the corresponding template tracks with a slice of the parent grid's
        // tracks before sizing. Per spec these only take effect when the box
        // is a child of another grid container; a non-grid parent makes the
        // subgrid keyword behave like `none`.
        public bool ColumnsSubgrid;
        public bool RowsSubgrid;
        // CSS Grid L2 §6: `grid-auto-rows/columns: subgrid` — implicit tracks
        // beyond the explicit subgrid template also inherit sizing from the
        // parent grid. GridLayout resolves these against parent tracks after
        // placement, cycling through parent tracks starting at the position
        // immediately after the last explicitly-covered parent track.
        public bool AutoRowsSubgrid;
        public bool AutoColumnsSubgrid;

        public static GridContainerProperties From(ComputedStyle style, LengthContext lengthCtx) {
            // Back-compat: treat lengthCtx.BasisPixels as the inline axis;
            // block axis indefinite (percentage row-gap collapses to 0 per
            // CSS Box Alignment L3 §8.3).
            return From(style, lengthCtx, lengthCtx.BasisPixels, blockBasisPx: null);
        }

        // E2: row-gap percentages resolve against the container's block-axis
        // size (height in horizontal writing modes); column-gap against the
        // inline-axis size (width). Pass both explicitly so the resolver
        // doesn't conflate them with lengthCtx.BasisPixels (which the caller
        // uses for inline-axis content-width resolution and may not match
        // the block axis the row-gap percentage should target).
        public static GridContainerProperties From(ComputedStyle style, LengthContext lengthCtx, double? inlineBasisPx, double? blockBasisPx) {
            var p = new GridContainerProperties {
                Columns = GridTemplate.Empty,
                Rows = GridTemplate.Empty,
                AutoColumns = new[] { GridTrackSize.Auto },
                AutoRows = new[] { GridTrackSize.Auto },
                AutoFlow = GridAutoFlow.Row,
                RowGap = 0,
                ColumnGap = 0,
                JustifyContent = JustifyContent.Start,
                AlignContent = AlignContent.Start,
                JustifyItems = JustifyItems.Stretch,
                AlignItems = AlignItems.Stretch,
                Areas = GridAreasParser.AreasMap.Empty
            };
            if (style == null) return p;

            // TODO: GetParsed migration — grid track lists (grid-template-rows/
            // columns, grid-auto-rows/columns, grid-template, grid shorthand)
            // use the `fr` flex factor unit and `[name]` bracket syntax which
            // aren't representable as typed CssValue today (CssLengthUnit has
            // no `fr`, the value parser rejects `[`/`]`). GridTrackParser still
            // tokenises the raw text itself, so we keep the raw-string read
            // path here. Migrating these would also require teaching
            // CssValueParser about flex units and named-line brackets.
            string shTemplate = style.Get(CssProperties.GridTemplateId);

            string cols = style.Get(CssProperties.GridTemplateColumnsId);
            string rows = style.Get(CssProperties.GridTemplateRowsId);

            if (IsTemplateInitial(cols) && IsTemplateInitial(rows) && !IsTemplateInitial(shTemplate)) {
                var split = GridShorthand.SplitTemplate(shTemplate);
                if (split.rows != null) rows = split.rows;
                if (split.columns != null) cols = split.columns;
            }

            string shGrid = style.Get("grid");
            if (IsTemplateInitial(cols) && IsTemplateInitial(rows) && !IsTemplateInitial(shGrid)) {
                var split = GridShorthand.SplitTemplate(shGrid);
                if (split.rows != null) rows = split.rows;
                if (split.columns != null) cols = split.columns;
            }

            if (!IsTemplateInitial(cols)) {
                if (Weva.Layout.Subgrid.SubgridTrackResolver.IsSubgridKeyword(cols)) {
                    p.ColumnsSubgrid = true;
                } else {
                    try { p.Columns = GridTrackParser.Parse(cols, lengthCtx); }
                    catch (GridTrackParser.ParseException) { p.Columns = GridTemplate.Empty; }
                }
            }
            if (!IsTemplateInitial(rows)) {
                if (Weva.Layout.Subgrid.SubgridTrackResolver.IsSubgridKeyword(rows)) {
                    p.RowsSubgrid = true;
                } else {
                    try { p.Rows = GridTrackParser.Parse(rows, lengthCtx); }
                    catch (GridTrackParser.ParseException) { p.Rows = GridTemplate.Empty; }
                }
            }

            string autoCols = style.Get(CssProperties.GridAutoColumnsId);
            if (!string.IsNullOrEmpty(autoCols)) {
                // CSS Grid L2 §6: `subgrid` as the value of grid-auto-columns
                // marks implicit tracks as inheriting parent sizing. Flag set
                // here; the actual track array is resolved in GridLayout after
                // the parent grid's track positions are known.
                if (Weva.Layout.Subgrid.SubgridTrackResolver.IsSubgridKeyword(autoCols)) {
                    p.AutoColumnsSubgrid = true;
                } else {
                    try {
                        var t = GridTrackParser.Parse(autoCols, lengthCtx);
                        if (t.Tracks.Count > 0) p.AutoColumns = ToArray(t.Tracks);
                    }
                    // DD7 / IF1: per CSS Grid L1 / CSS Cascade L5 invalid-at-
                    // computed-value-time, an unparseable grid-auto-columns value
                    // reverts to the initial value (a single `auto` track) — not
                    // the cascaded value. Mirror the grid-template-columns/rows
                    // failure mode above (which clears to GridTemplate.Empty) by
                    // resetting to the initial-value sentinel set at line 44.
                    catch (GridTrackParser.ParseException) { p.AutoColumns = new[] { GridTrackSize.Auto }; }
                }
            }
            string autoRowsRaw = style.Get(CssProperties.GridAutoRowsId);
            if (!string.IsNullOrEmpty(autoRowsRaw)) {
                // CSS Grid L2 §6: `subgrid` as the value of grid-auto-rows
                // marks implicit tracks as inheriting parent sizing.
                if (Weva.Layout.Subgrid.SubgridTrackResolver.IsSubgridKeyword(autoRowsRaw)) {
                    p.AutoRowsSubgrid = true;
                } else {
                    try {
                        var t = GridTrackParser.Parse(autoRowsRaw, lengthCtx);
                        if (t.Tracks.Count > 0) p.AutoRows = ToArray(t.Tracks);
                    }
                    // DD7 / IF1: see grid-auto-columns above.
                    catch (GridTrackParser.ParseException) { p.AutoRows = new[] { GridTrackSize.Auto }; }
                }
            }

            // grid-auto-flow: keyword shorthand, possibly two tokens ("row
            // dense"/"column dense"). The parsed form is either a single
            // CssKeyword/CssIdentifier or a CssValueList of two; GridShorthand
            // still tokenises by space so feed it whatever shape we got.
            var flowParsed = style.GetParsed(CssProperties.GridAutoFlowId);
            string flow = flowParsed != null ? flowParsed.Raw : style.Get(CssProperties.GridAutoFlowId);
            p.AutoFlow = GridShorthand.ParseAutoFlow(flow, GridAutoFlow.Row);

            // Gap longhands: typed length resolution avoids the
            // CssValue.TryParse round-trip on every layout pass.
            // CSS Box Alignment L3 §8.3 (E2): column-gap percentages resolve
            // against the inline-axis container size, row-gap against the
            // block-axis size. Build axis-specific length contexts so the
            // percentage basis swap is local to gap resolution.
            var inlineCtx = lengthCtx;
            inlineCtx.BasisPixels = inlineBasisPx;
            var blockCtx = lengthCtx;
            blockCtx.BasisPixels = blockBasisPx;
            double gapCol = ResolveGapParsed(style.GetParsed(CssProperties.GapId), inlineCtx);
            double gapRow = ResolveGapParsed(style.GetParsed(CssProperties.GapId), blockCtx);
            double rowGap = ResolveGapParsed(style.GetParsed(CssProperties.RowGapId), blockCtx);
            double colGap = ResolveGapParsed(style.GetParsed(CssProperties.ColumnGapId), inlineCtx);

            if (rowGap < 0 && colGap < 0) {
                // `gap` is the shorthand for row-gap + column-gap. It can be
                // a single length (handled above by `gap`) or a list of two
                // (split here). GridShorthand.SplitGap walks the raw string,
                // so use the raw text — the gap shorthand has no `fr`-style
                // gotchas, but rebuilding the split via typed CssValueList
                // would just duplicate code. Each split half resolves against
                // its own axis basis per §8.3.
                string gapShorthand = style.Get(CssProperties.GapId);
                if (!string.IsNullOrEmpty(gapShorthand)) {
                    var split = GridShorthand.SplitGap(gapShorthand);
                    if (split.column != null) {
                        rowGap = ResolveGap(split.row, blockCtx);
                        colGap = ResolveGap(split.column, inlineCtx);
                    }
                }
            }
            p.RowGap = rowGap >= 0 ? rowGap : gapRow >= 0 ? gapRow : 0;
            p.ColumnGap = colGap >= 0 ? colGap : gapCol >= 0 ? gapCol : 0;

            // place-content / place-items shorthands and their longhands.
            // Single-token longhands take the typed CssKeyword/CssIdentifier
            // fast path via ParseKeyword*Parsed — no Trim/ToLowerInvariant
            // alloc per dispatch. The multi-token place-* shorthand still
            // peels via GridShorthand.SplitPlaceShorthand on the raw text
            // because the existing splitter already understands the layered
            // "safe"/"unsafe" prefixes.
            var jcParsed = style.GetParsed(CssProperties.JustifyContentId);
            var acParsed = style.GetParsed(CssProperties.AlignContentId);
            string jc = style.Get(CssProperties.JustifyContentId);
            string ac = style.Get(CssProperties.AlignContentId);
            if (IsContentInitial(jc) && IsContentInitial(ac)) {
                string placeContent = style.Get(CssProperties.PlaceContentId);
                if (!string.IsNullOrEmpty(placeContent) && placeContent != "normal") {
                    var split = GridShorthand.SplitPlaceShorthand(placeContent);
                    ac = split.a; jc = split.b;
                    // Shorthand expansion produced fresh strings, so the
                    // typed fast path no longer applies for this iteration.
                    jcParsed = null; acParsed = null;
                }
            }
            if (!string.IsNullOrEmpty(jc)) p.JustifyContent = ParseJustifyContentParsed(jcParsed, jc, p.JustifyContent);
            if (!string.IsNullOrEmpty(ac)) p.AlignContent = ParseAlignContentParsed(acParsed, ac, p.AlignContent);

            var jiParsed = style.GetParsed(CssProperties.JustifyItemsId);
            var aiParsed = style.GetParsed(CssProperties.AlignItemsId);
            string ji = style.Get(CssProperties.JustifyItemsId);
            string ai = style.Get(CssProperties.AlignItemsId);
            if (IsItemsInitial(ji) && IsItemsInitial(ai)) {
                string placeItems = style.Get(CssProperties.PlaceItemsId);
                if (!string.IsNullOrEmpty(placeItems) && placeItems != "normal legacy") {
                    var split = GridShorthand.SplitPlaceShorthand(placeItems);
                    ai = split.a; ji = split.b;
                    jiParsed = null; aiParsed = null;
                }
            }
            if (!IsItemsInitial(ji)) p.JustifyItems = ParseJustifyItemsParsed(jiParsed, ji, p.JustifyItems);
            if (!string.IsNullOrEmpty(ai)) p.AlignItems = ParseAlignItemsParsed(aiParsed, ai, p.AlignItems);

            // grid-template-areas: a CssValueList of CssString items, e.g.
            //   `"a a" "b b"`. GridAreasParser already tokenises the raw
            //   text (and supports both quoted-string and identifier forms),
            //   so reuse the raw read here.
            string areas = style.Get(CssProperties.GridTemplateAreasId);
            if (!IsTemplateInitial(areas)) {
                try { p.Areas = GridAreasParser.Parse(areas); }
                catch (GridAreasParser.ParseException) { p.Areas = GridAreasParser.AreasMap.Empty; }
            }

            return p;
        }

        static bool IsTemplateInitial(string raw) {
            return string.IsNullOrEmpty(raw) || raw == "none" || raw == "auto";
        }

        static bool IsContentInitial(string raw) {
            return string.IsNullOrEmpty(raw) || raw == "flex-start" || raw == "stretch" || raw == "normal";
        }

        static bool IsItemsInitial(string raw) {
            return string.IsNullOrEmpty(raw) || raw == "legacy" || raw == "normal" || raw == "stretch";
        }

        static GridTrackSize[] ToArray(IReadOnlyList<GridTrackSize> tracks) {
            var arr = new GridTrackSize[tracks.Count];
            for (int i = 0; i < tracks.Count; i++) arr[i] = tracks[i];
            return arr;
        }

        // Typed gap resolver: reads CssLength / CssNumber / CssPercentage /
        // CssCalc directly off the cached parse tree. Returns -1 to signal
        // "unset / normal" so the caller can fall through to the shorthand.
        static double ResolveGapParsed(CssValue v, LengthContext lengthCtx) {
            if (v == null) return -1;
            // `normal` parses to CssKeyword; per CSS Box Alignment L3 §8.3
            // the used value is 0 for grid containers, but the caller treats
            // -1 as "fall through to shorthand", which matches the prior
            // raw-string ResolveGap behaviour.
            if (v is CssKeyword k && k.Identifier == "normal") return -1;
            if (v is CssIdentifier id && string.Equals(id.Name, "normal", System.StringComparison.OrdinalIgnoreCase)) return -1;
            if (v is CssLength l) return l.ToPixels(lengthCtx);
            if (v is CssNumber n) return n.Value;
            if (v is CssPercentage p) {
                if (lengthCtx.BasisPixels.HasValue) return p.Value * 0.01 * lengthCtx.BasisPixels.Value;
                return 0;
            }
            if (v is CssCalc c) {
                try { return c.Evaluate(lengthCtx); } catch { return -1; }
            }
            return -1;
        }

        static double ResolveGap(string raw, LengthContext lengthCtx) {
            if (string.IsNullOrEmpty(raw) || raw == "normal") return -1;
            if (!CssValue.TryParse(raw, out var v)) return -1;
            if (v is CssLength l) return l.ToPixels(lengthCtx);
            if (v is CssNumber n) return n.Value;
            if (v is CssPercentage p) {
                if (lengthCtx.BasisPixels.HasValue) return p.Value * 0.01 * lengthCtx.BasisPixels.Value;
                return 0;
            }
            if (v is CssCalc c) {
                try { return c.Evaluate(lengthCtx); } catch { return -1; }
            }
            return -1;
        }

        // Typed keyword dispatch helpers. When the parse tree is a single
        // CssKeyword/CssIdentifier we read its Identifier/Name directly —
        // CssKeyword.Identifier is already lowercased on construction, so
        // the lowercase/trim alloc the raw-string path needs is avoided.
        // CssIdentifier preserves source casing; we lowercase on miss only.
        // Anything else (CssValueList for multi-token shorthand inputs,
        // null when parse failed) falls back through to the raw-string
        // ParseJustifyContent / ParseAlignContent / etc. — identical
        // behaviour to the prior code path.
        static bool TryGetKeywordIdent(CssValue v, out string ident) {
            if (v is CssKeyword k) { ident = k.Identifier; return true; }
            if (v is CssIdentifier id) { ident = id.Name; return true; }
            ident = null;
            return false;
        }

        // E6b: CSS Box Alignment L3 §6.2 allows a `safe`/`unsafe` positional
        // prefix before the alignment keyword (e.g. `justify-content: safe
        // center`). The prefix changes overflow behavior — `safe` falls back
        // to start when content would overflow; `unsafe` clips. We don't yet
        // honor the overflow-fallback semantics at layout time (deferred); for
        // v1 `safe` and `unsafe` parse identically. Peel the 2-token list to
        // the alignment keyword so downstream dispatch sees "center" not the
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
        // alignment keyword. Returns the trimmed remainder; non-prefixed
        // strings are returned unchanged.
        static string StripOverflowPositionPrefix(string s) {
            if (string.IsNullOrEmpty(s)) return s;
            string t = s.Trim();
            // Match `safe <kw>` or `unsafe <kw>` (single whitespace gap
            // between tokens; identifier-only following token). Use a manual
            // walk to avoid an allocation for the common no-prefix case.
            int spaceIdx = -1;
            for (int i = 0; i < t.Length; i++) {
                if (char.IsWhiteSpace(t[i])) { spaceIdx = i; break; }
            }
            if (spaceIdx <= 0) return s;
            string head = t.Substring(0, spaceIdx);
            string headLower = CssStringUtil.ToLowerInvariantOrSame(head);
            if (headLower != "safe" && headLower != "unsafe") return s;
            // Skip the run of whitespace and return the tail.
            int tailStart = spaceIdx + 1;
            while (tailStart < t.Length && char.IsWhiteSpace(t[tailStart])) tailStart++;
            if (tailStart >= t.Length) return s;
            return t.Substring(tailStart);
        }

        static JustifyContent ParseJustifyContentParsed(CssValue parsed, string raw, JustifyContent fallback) {
            parsed = UnwrapOverflowPosition(parsed);
            if (TryGetKeywordIdent(parsed, out var ident)) {
                switch (CssStringUtil.ToLowerInvariantOrSame(ident)) {
                    case "start":
                    case "flex-start":
                    case "left":
                    case "normal": return JustifyContent.Start;
                    case "end":
                    case "flex-end":
                    case "right": return JustifyContent.End;
                    case "center": return JustifyContent.Center;
                    case "stretch": return JustifyContent.Stretch;
                    case "space-between": return JustifyContent.SpaceBetween;
                    case "space-around": return JustifyContent.SpaceAround;
                    case "space-evenly": return JustifyContent.SpaceEvenly;
                }
                return fallback;
            }
            return ParseJustifyContent(raw, fallback);
        }

        static AlignContent ParseAlignContentParsed(CssValue parsed, string raw, AlignContent fallback) {
            parsed = UnwrapOverflowPosition(parsed);
            if (TryGetKeywordIdent(parsed, out var ident)) {
                switch (CssStringUtil.ToLowerInvariantOrSame(ident)) {
                    case "start":
                    case "flex-start":
                    case "normal": return AlignContent.Start;
                    case "end":
                    case "flex-end": return AlignContent.End;
                    case "center": return AlignContent.Center;
                    case "stretch": return AlignContent.Stretch;
                    case "space-between": return AlignContent.SpaceBetween;
                    case "space-around": return AlignContent.SpaceAround;
                    case "space-evenly": return AlignContent.SpaceEvenly;
                }
                return fallback;
            }
            return ParseAlignContent(raw, fallback);
        }

        static JustifyItems ParseJustifyItemsParsed(CssValue parsed, string raw, JustifyItems fallback) {
            parsed = UnwrapOverflowPosition(parsed);
            if (TryGetKeywordIdent(parsed, out var ident)) {
                switch (CssStringUtil.ToLowerInvariantOrSame(ident)) {
                    case "start":
                    case "flex-start":
                    case "left":
                    case "self-start": return JustifyItems.Start;
                    case "end":
                    case "flex-end":
                    case "right":
                    case "self-end": return JustifyItems.End;
                    case "center": return JustifyItems.Center;
                    case "stretch":
                    case "normal":
                    case "legacy": return JustifyItems.Stretch;
                }
                return fallback;
            }
            return ParseJustifyItems(raw, fallback);
        }

        static AlignItems ParseAlignItemsParsed(CssValue parsed, string raw, AlignItems fallback) {
            parsed = UnwrapOverflowPosition(parsed);
            if (TryGetKeywordIdent(parsed, out var ident)) {
                switch (CssStringUtil.ToLowerInvariantOrSame(ident)) {
                    case "start":
                    case "flex-start":
                    case "self-start": return AlignItems.Start;
                    case "end":
                    case "flex-end":
                    case "self-end": return AlignItems.End;
                    case "center": return AlignItems.Center;
                    case "stretch":
                    case "normal": return AlignItems.Stretch;
                }
                return fallback;
            }
            return ParseAlignItems(raw, fallback);
        }

        static JustifyContent ParseJustifyContent(string s, JustifyContent fallback) {
            s = StripOverflowPositionPrefix(s);
            switch (CssStringUtil.ToLowerInvariantOrSame(s.Trim())) {
                case "start":
                case "flex-start":
                case "left":
                case "normal": return JustifyContent.Start;
                case "end":
                case "flex-end":
                case "right": return JustifyContent.End;
                case "center": return JustifyContent.Center;
                case "stretch": return JustifyContent.Stretch;
                case "space-between": return JustifyContent.SpaceBetween;
                case "space-around": return JustifyContent.SpaceAround;
                case "space-evenly": return JustifyContent.SpaceEvenly;
            }
            return fallback;
        }

        static AlignContent ParseAlignContent(string s, AlignContent fallback) {
            s = StripOverflowPositionPrefix(s);
            switch (CssStringUtil.ToLowerInvariantOrSame(s.Trim())) {
                case "start":
                case "flex-start":
                case "normal": return AlignContent.Start;
                case "end":
                case "flex-end": return AlignContent.End;
                case "center": return AlignContent.Center;
                case "stretch": return AlignContent.Stretch;
                case "space-between": return AlignContent.SpaceBetween;
                case "space-around": return AlignContent.SpaceAround;
                case "space-evenly": return AlignContent.SpaceEvenly;
            }
            return fallback;
        }

        static JustifyItems ParseJustifyItems(string s, JustifyItems fallback) {
            s = StripOverflowPositionPrefix(s);
            switch (CssStringUtil.ToLowerInvariantOrSame(s.Trim())) {
                case "start":
                case "flex-start":
                case "left":
                case "self-start": return JustifyItems.Start;
                case "end":
                case "flex-end":
                case "right":
                case "self-end": return JustifyItems.End;
                case "center": return JustifyItems.Center;
                case "stretch":
                case "normal":
                case "legacy": return JustifyItems.Stretch;
            }
            return fallback;
        }

        static AlignItems ParseAlignItems(string s, AlignItems fallback) {
            s = StripOverflowPositionPrefix(s);
            switch (CssStringUtil.ToLowerInvariantOrSame(s.Trim())) {
                case "start":
                case "flex-start":
                case "self-start": return AlignItems.Start;
                case "end":
                case "flex-end":
                case "self-end": return AlignItems.End;
                case "center": return AlignItems.Center;
                case "stretch":
                case "normal": return AlignItems.Stretch;
            }
            return fallback;
        }
    }
}
