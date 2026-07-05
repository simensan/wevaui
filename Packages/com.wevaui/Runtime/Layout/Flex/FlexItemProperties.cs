using Weva.Css.Cascade;
using Weva.Css.Values;

namespace Weva.Layout.Flex {
    internal struct FlexItemProperties {
        public double Grow;
        public double Shrink;
        public FlexBasis Basis;
        public AlignSelf AlignSelf;
        public int Order;

        public static FlexItemProperties From(ComputedStyle style, LengthContext lengthCtx) {
            var p = new FlexItemProperties {
                Grow = 0,
                Shrink = 1,
                Basis = FlexBasis.Auto,
                AlignSelf = AlignSelf.Auto,
                Order = 0
            };
            if (style == null) return p;

            // Per-style parsed cache (ComputedStyle.GetParsed) returns the
            // already-built CssValue. The `flex` shorthand applies first;
            // longhands override below when they parse to a real CssNumber/
            // CssLength/keyword. The previous string-based path treated
            // "0 1 auto" / "0" / "1" / "auto" as "initial, don't override";
            // CSS Flexbox L1 §7 actually says specified longhands always
            // override the shorthand. We rely on cache occupancy: an unset
            // longhand returns null from GetParsed and we keep the shorthand
            // (or default) value.
            var flexParsed = style.GetParsed(CssProperties.FlexId);
            if (flexParsed != null) {
                var sh = FlexShorthand.ParseValue(flexParsed, lengthCtx);
                if (sh.HasValue) {
                    p.Grow = sh.Grow;
                    p.Shrink = sh.Shrink;
                    p.Basis = sh.Basis;
                }
            }

            var growParsed = style.GetParsed(CssProperties.FlexGrowId);
            if (growParsed is CssNumber gn) p.Grow = gn.Value;

            var shrinkParsed = style.GetParsed(CssProperties.FlexShrinkId);
            if (shrinkParsed is CssNumber sn) p.Shrink = sn.Value;

            var basisParsed = style.GetParsed(CssProperties.FlexBasisId);
            if (basisParsed != null && TryReadBasis(basisParsed, lengthCtx, out var b)) {
                p.Basis = b;
            }

            var alignSelfParsed = style.GetParsed(CssProperties.AlignSelfId);
            if (alignSelfParsed != null) p.AlignSelf = ParseAlignSelf(alignSelfParsed, p.AlignSelf);

            var orderParsed = style.GetParsed(CssProperties.OrderId);
            if (orderParsed is CssNumber on) p.Order = (int)on.Value;

            return p;
        }

        // Length-or-keyword dispatch. Keywords "auto" / "content" map to the
        // dedicated FlexBasis kinds; the intrinsic-sizing keywords degrade
        // to Auto since the layout engine doesn't compute intrinsic sizes
        // yet. CssCalc.Evaluate may throw for percent-bearing calcs without
        // a basis in lengthCtx; treat that as "not a basis" so the caller
        // can fall back to the shorthand value.
        static bool TryReadBasis(CssValue v, LengthContext lengthCtx, out FlexBasis basis) {
            if (v is CssLength l) { basis = FlexBasis.Length(l.ToPixels(lengthCtx)); return true; }
            if (v is CssPercentage p) { basis = FlexBasis.Percentage(p.Value); return true; }
            if (v is CssNumber n) { basis = FlexBasis.Length(n.Value); return true; }
            if (v is CssCalc c) {
                try { basis = FlexBasis.Length(c.Evaluate(lengthCtx)); return true; } catch { basis = FlexBasis.Auto; return false; }
            }
            if (v is CssKeyword k) {
                switch (k.Identifier) {
                    case "auto": basis = FlexBasis.Auto; return true;
                    case "content": basis = FlexBasis.Content; return true;
                    case "min-content":
                    case "max-content":
                    case "fit-content":
                        basis = FlexBasis.Auto; return true;
                }
            }
            if (v is CssIdentifier ci) {
                string id = CssStringUtil.ToLowerInvariantOrSame(ci.Name);
                switch (id) {
                    case "auto": basis = FlexBasis.Auto; return true;
                    case "content": basis = FlexBasis.Content; return true;
                    case "min-content":
                    case "max-content":
                    case "fit-content":
                        basis = FlexBasis.Auto; return true;
                }
            }
            basis = FlexBasis.Auto;
            return false;
        }

        static AlignSelf ParseAlignSelf(CssValue v, AlignSelf fallback) {
            // CSS Box Alignment L3: accept a `safe`/`unsafe` positional
            // prefix (e.g. `align-self: safe center`) by unwrapping the
            // 2-token space-separated list to its alignment keyword.
            // Overflow-fallback semantics at layout time are deferred — for
            // v1 `safe` and `unsafe` parse identically.
            // Also handles `first baseline` / `last baseline` — the two-word
            // forms of the baseline alignment keyword per CSS Box Alignment L3 §4.
            // In v1 both map to `baseline` (first-baseline semantics).
            if (v is CssValueList list
                && list.Separator == CssValueListSeparator.Space
                && list.Items.Count == 2) {
                string first = null;
                if (list.Items[0] is CssKeyword fk) first = fk.Identifier;
                else if (list.Items[0] is CssIdentifier fi) first = CssStringUtil.ToLowerInvariantOrSame(fi.Name);
                if (first == "safe" || first == "unsafe") v = list.Items[1];
                else if (first == "first" || first == "last") {
                    string second = null;
                    if (list.Items[1] is CssKeyword sk) second = sk.Identifier;
                    else if (list.Items[1] is CssIdentifier si) second = CssStringUtil.ToLowerInvariantOrSame(si.Name);
                    if (second == "baseline") v = list.Items[1]; // unwrap to the "baseline" token
                }
            }
            string name = null;
            if (v is CssKeyword k) name = k.Identifier;
            else if (v is CssIdentifier id) name = CssStringUtil.ToLowerInvariantOrSame(id.Name);
            if (name == null) return fallback;
            switch (name) {
                case "auto": return AlignSelf.Auto;
                case "stretch": return AlignSelf.Stretch;
                case "flex-start": return AlignSelf.FlexStart;
                case "flex-end": return AlignSelf.FlexEnd;
                case "center": return AlignSelf.Center;
                case "baseline": return AlignSelf.Baseline;
                case "start": return AlignSelf.Start;
                case "end": return AlignSelf.End;
                case "normal": return AlignSelf.Auto;
            }
            return fallback;
        }
    }
}
