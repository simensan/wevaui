using Weva.Css.Cascade;
using Weva.Css.Values;

namespace Weva.Layout.Flex {
    internal struct FlexProperties {
        public FlexDirection Direction;
        public FlexWrap Wrap;
        public JustifyContent JustifyContent;
        public AlignItems AlignItems;
        public AlignContent AlignContent;
        public double RowGap;
        public double ColumnGap;

        public bool IsRow => Direction == FlexDirection.Row || Direction == FlexDirection.RowReverse;
        public bool IsReverse => Direction == FlexDirection.RowReverse || Direction == FlexDirection.ColumnReverse;
        public bool IsWrapReverse => Wrap == FlexWrap.WrapReverse;
        public double MainGap => IsRow ? ColumnGap : RowGap;
        public double CrossGap => IsRow ? RowGap : ColumnGap;

        public static FlexProperties From(ComputedStyle style, LengthContext lengthCtx) {
            // Back-compat overload: lengthCtx.BasisPixels acts as the
            // inline-axis basis; block-axis basis is treated as indefinite
            // (percentage row-gap collapses to 0 per spec). Test-only call
            // sites that construct LengthContext.Default land here.
            return From(style, lengthCtx, lengthCtx.BasisPixels, blockBasisPx: null);
        }

        // E2: row-gap percentages resolve against the container's block-axis
        // size per CSS Box Alignment L3 §8.3 — height in horizontal writing
        // modes. column-gap resolves against the inline-axis size (width).
        // Pass inlineBasisPx/blockBasisPx independently — `lengthCtx.BasisPixels`
        // is the main-axis basis (font-relative / calc fallback) and may not
        // match the inline axis in column-direction flex.
        //
        // Indefinite container axis: pass null. The percentage gap resolves
        // to 0 per spec (ResolveGap's BasisPixels.HasValue == false branch).
        public static FlexProperties From(ComputedStyle style, LengthContext lengthCtx, double? inlineBasisPx, double? blockBasisPx) {
            var p = new FlexProperties {
                Direction = FlexDirection.Row,
                Wrap = FlexWrap.NoWrap,
                JustifyContent = JustifyContent.FlexStart,
                AlignItems = AlignItems.Stretch,
                AlignContent = AlignContent.Stretch,
                RowGap = 0,
                ColumnGap = 0
            };
            if (style == null) return p;

            // Per-style parsed cache (ComputedStyle.GetParsed) returns the
            // already-built CssValue without re-running CssValue.TryParse —
            // O(1) array lookup after the first read, no dictionary probe.
            // Keyword-typed properties dispatch on CssKeyword/CssIdentifier
            // names; the parser lowercases keyword identifiers so switch
            // labels stay lowercase.
            //
            // The cascade fills every property to its initial value via
            // CascadeEngine.FillInherited, so unset properties still come
            // back as CssKeyword("row")/("nowrap")/etc. To preserve the
            // original "shorthand wins over initial-valued longhands"
            // semantics we read the raw string for the gate check (still
            // O(1)) and skip the longhand override when it matches the
            // initial value.
            ApplyFlexFlow(style, ref p);

            string dirRaw = style.Get(CssProperties.FlexDirectionId);
            if (!string.IsNullOrEmpty(dirRaw) && dirRaw != "row") {
                var dir = style.GetParsed(CssProperties.FlexDirectionId);
                if (dir != null) ApplyDirection(dir, ref p);
            }

            string wrapRaw = style.Get(CssProperties.FlexWrapId);
            if (!string.IsNullOrEmpty(wrapRaw) && wrapRaw != "nowrap") {
                var wrap = style.GetParsed(CssProperties.FlexWrapId);
                if (wrap != null) ApplyWrap(wrap, ref p);
            }

            var jc = style.GetParsed(CssProperties.JustifyContentId);
            if (jc != null) p.JustifyContent = ParseJustify(jc, p.JustifyContent);

            var ai = style.GetParsed(CssProperties.AlignItemsId);
            if (ai != null) p.AlignItems = ParseAlignItems(ai, p.AlignItems);

            var ac = style.GetParsed(CssProperties.AlignContentId);
            if (ac != null) p.AlignContent = ParseAlignContent(ac, p.AlignContent);

            // CSS Box Alignment L3 §8.3: column-gap percentages resolve
            // against the inline-axis container size (width in horizontal
            // writing modes); row-gap against the block-axis size (height).
            // Build per-axis contexts so the `gap` shorthand can resolve
            // each longhand with the correct basis (the shorthand may carry
            // a percentage that means different things for the two axes).
            // When either basis is null (indefinite container axis) the
            // spec calls for the percentage to contribute 0 — the existing
            // ResolveGap fallback (BasisPixels.HasValue == false → 0)
            // preserves that behavior.
            var inlineCtx = lengthCtx;
            inlineCtx.BasisPixels = inlineBasisPx;
            var blockCtx = lengthCtx;
            blockCtx.BasisPixels = blockBasisPx;
            double gapCol = ResolveGap(style.GetParsed(CssProperties.GapId), inlineCtx);
            double gapRow = ResolveGap(style.GetParsed(CssProperties.GapId), blockCtx);
            double rowGap = ResolveGap(style.GetParsed(CssProperties.RowGapId), blockCtx);
            double colGap = ResolveGap(style.GetParsed(CssProperties.ColumnGapId), inlineCtx);
            p.RowGap = rowGap >= 0 ? rowGap : gapRow >= 0 ? gapRow : 0;
            p.ColumnGap = colGap >= 0 ? colGap : gapCol >= 0 ? gapCol : 0;

            ApplyDirectionality(style, ref p);
            return p;
        }

        // flex-flow is a shorthand of flex-direction + flex-wrap. The parser
        // produces a space-separated CssValueList for the two-token form, or
        // a single CssKeyword/CssIdentifier for the one-token form. Walk the
        // items by type to extract direction/wrap; absent tokens are left at
        // their initial values, matching CSS Flexbox L1 §6.1.
        static void ApplyFlexFlow(ComputedStyle style, ref FlexProperties p) {
            // Bail when flex-flow is unset / at the initial "row nowrap"
            // so the cascade-filled initial value doesn't clobber a
            // subsequent explicit flex-direction/flex-wrap longhand. The
            // raw-string compare is O(1) and avoids touching the parsed
            // cache for the dominant initial-value case.
            string raw = style.Get(CssProperties.FlexFlowId);
            if (string.IsNullOrEmpty(raw) || raw == "row nowrap") return;
            var ff = style.GetParsed(CssProperties.FlexFlowId);
            if (ff == null) return;
            if (ff is CssValueList list && list.Separator == CssValueListSeparator.Space) {
                for (int i = 0; i < list.Items.Count; i++) {
                    ApplyFlexFlowToken(list.Items[i], ref p);
                }
                return;
            }
            ApplyFlexFlowToken(ff, ref p);
        }

        static void ApplyFlexFlowToken(CssValue v, ref FlexProperties p) {
            string name = KeywordName(v);
            if (name == null) return;
            if (TryParseDirectionName(name, out var d)) { p.Direction = d; return; }
            if (TryParseWrapName(name, out var w)) { p.Wrap = w; return; }
        }

        static void ApplyDirection(CssValue v, ref FlexProperties p) {
            string name = KeywordName(v);
            if (name != null && TryParseDirectionName(name, out var d)) p.Direction = d;
        }

        static void ApplyDirectionality(ComputedStyle style, ref FlexProperties p) {
            if (!StyleResolver.IsRtl(style)) return;
            string writingMode = style.Get(CssProperties.WritingModeId);
            if (!string.IsNullOrEmpty(writingMode)
                && !CssStringUtil.EqualsIgnoreCaseTrimmed(writingMode, "horizontal-tb")) {
                return;
            }
            if (p.Direction == FlexDirection.Row) p.Direction = FlexDirection.RowReverse;
            else if (p.Direction == FlexDirection.RowReverse) p.Direction = FlexDirection.Row;
        }

        static void ApplyWrap(CssValue v, ref FlexProperties p) {
            string name = KeywordName(v);
            if (name != null && TryParseWrapName(name, out var w)) p.Wrap = w;
        }

        // Extracts the lowercase identifier from CssKeyword/CssIdentifier
        // without allocating. CssKeyword.Identifier is already lowercased by
        // the parser; CssIdentifier.Name preserves source case, so we lower
        // it only on the identifier path.
        static string KeywordName(CssValue v) {
            if (v is CssKeyword k) return k.Identifier;
            if (v is CssIdentifier id) return CssStringUtil.ToLowerInvariantOrSame(id.Name);
            return null;
        }

        // CSS Box Alignment L3 allows a `safe`/`unsafe` positional prefix
        // before the alignment keyword (e.g. `align-items: safe center`).
        // The prefix changes overflow behavior — `safe` falls back to start
        // when content would overflow; `unsafe` clips. We don't yet honor
        // the overflow-fallback semantics at layout time (deferred), but we
        // accept the syntax: unwrap the space-separated 2-token form to the
        // alignment keyword so downstream switches see "center" not
        // CssValueList. Single-token forms pass through unchanged.
        //
        // Also handles CSS Box Alignment L3 §4 `first baseline` / `last baseline`
        // two-word forms: strip the leading `first`/`last` and return `baseline`
        // as a synthetic keyword. In v1 both map to first-baseline behaviour.
        static CssValue UnwrapOverflowPosition(CssValue v) {
            if (v is CssValueList list
                && list.Separator == CssValueListSeparator.Space
                && list.Items.Count == 2) {
                string first = KeywordName(list.Items[0]);
                if (first == "safe" || first == "unsafe") return list.Items[1];
                // CSS Box Alignment L3 §4: `first baseline` and `last baseline`
                // are the explicit two-word forms of the baseline keyword.
                // In v1 both map to `baseline` (first-baseline semantics).
                if (first == "first" || first == "last") {
                    string second = KeywordName(list.Items[1]);
                    if (second == "baseline") return list.Items[1]; // returns the "baseline" token
                }
            }
            return v;
        }

        static bool TryParseDirectionName(string s, out FlexDirection d) {
            switch (s) {
                case "row": d = FlexDirection.Row; return true;
                case "row-reverse": d = FlexDirection.RowReverse; return true;
                case "column": d = FlexDirection.Column; return true;
                case "column-reverse": d = FlexDirection.ColumnReverse; return true;
            }
            d = FlexDirection.Row;
            return false;
        }

        static bool TryParseWrapName(string s, out FlexWrap w) {
            switch (s) {
                case "nowrap": w = FlexWrap.NoWrap; return true;
                case "wrap": w = FlexWrap.Wrap; return true;
                case "wrap-reverse": w = FlexWrap.WrapReverse; return true;
            }
            w = FlexWrap.NoWrap;
            return false;
        }

        static JustifyContent ParseJustify(CssValue v, JustifyContent fallback) {
            v = UnwrapOverflowPosition(v);
            string name = KeywordName(v);
            if (name == null) return fallback;
            switch (name) {
                case "flex-start": return JustifyContent.FlexStart;
                case "flex-end": return JustifyContent.FlexEnd;
                case "center": return JustifyContent.Center;
                case "space-between": return JustifyContent.SpaceBetween;
                case "space-around": return JustifyContent.SpaceAround;
                case "space-evenly": return JustifyContent.SpaceEvenly;
                case "start": return JustifyContent.Start;
                case "end": return JustifyContent.End;
                case "left": return JustifyContent.FlexStart;
                case "right": return JustifyContent.FlexEnd;
                case "normal": return JustifyContent.FlexStart;
            }
            return fallback;
        }

        static AlignItems ParseAlignItems(CssValue v, AlignItems fallback) {
            v = UnwrapOverflowPosition(v);
            string name = KeywordName(v);
            if (name == null) return fallback;
            switch (name) {
                case "stretch": return AlignItems.Stretch;
                case "flex-start": return AlignItems.FlexStart;
                case "flex-end": return AlignItems.FlexEnd;
                case "center": return AlignItems.Center;
                case "baseline": return AlignItems.Baseline;
                case "start": return AlignItems.Start;
                case "end": return AlignItems.End;
                case "normal": return AlignItems.Stretch;
            }
            return fallback;
        }

        static AlignContent ParseAlignContent(CssValue v, AlignContent fallback) {
            v = UnwrapOverflowPosition(v);
            string name = KeywordName(v);
            if (name == null) return fallback;
            switch (name) {
                case "stretch": return AlignContent.Stretch;
                case "flex-start": return AlignContent.FlexStart;
                case "flex-end": return AlignContent.FlexEnd;
                case "center": return AlignContent.Center;
                case "space-between": return AlignContent.SpaceBetween;
                case "space-around": return AlignContent.SpaceAround;
                case "space-evenly": return AlignContent.SpaceEvenly;
                case "start": return AlignContent.FlexStart;
                case "end": return AlignContent.FlexEnd;
                case "normal": return AlignContent.Stretch;
            }
            return fallback;
        }

        // Returns -1 when the slot is unset / keyword "normal" / unparseable
        // so the caller can fall back to a wider shorthand (e.g. row-gap
        // inherits from `gap` when only `gap` was set). CSS Box Alignment
        // L3 §8 specifies that `normal` on flex containers computes to 0;
        // upstream tests rely on the `-1 == fallback` signaling pattern so
        // we preserve it here.
        static double ResolveGap(CssValue v, LengthContext lengthCtx) {
            if (v == null) return -1;
            if (v is CssLength l) return l.ToPixels(lengthCtx);
            if (v is CssNumber n) return n.Value;
            if (v is CssPercentage p) {
                if (lengthCtx.BasisPixels.HasValue) return p.Value * 0.01 * lengthCtx.BasisPixels.Value;
                return 0;
            }
            if (v is CssCalc c) {
                try { return c.Evaluate(lengthCtx); } catch { return -1; }
            }
            if (v is CssKeyword k) {
                if (k.Identifier == "normal") return -1;
            }
            return -1;
        }
    }
}
