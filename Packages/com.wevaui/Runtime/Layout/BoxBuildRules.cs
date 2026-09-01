using System;
using Weva.Css;
using Weva.Css.Values;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout.Text;

namespace Weva.Layout {
    // The spec decisions both box builders have to make identically.
    //
    // There are two builders on purpose: BoxBuilder walks a live DOM, and
    // SnapshotBoxBuilder walks a DomSnapshot's flat node ids (LayoutEngine
    // picks it whenever `ctx.Snapshot` is set, which is the normal path). Their
    // TRAVERSAL genuinely differs — one follows Node.Children, the other
    // integer ids — but the rules they apply while traversing do not, and every
    // rule that lived in both places was a chance for them to drift apart.
    //
    // They did. `position: absolute` blockifies an element's outer display
    // (CSS Display 3 §2.7), and both copies handled only plain `inline`; an
    // absolutely positioned <img> — inline-block by the UA sheet — stayed an
    // inline atom and was shrink-to-fit sized to zero width. Fixing one copy
    // changed nothing observable, because the other one was the copy the
    // layout engine actually ran.
    //
    // So: anything here is the single source of truth, and
    // BoxBuilderParityTests builds a matrix of documents through BOTH builders
    // and fails if the two box trees differ. Add a rule here, not in a builder.
    internal static class BoxBuildRules {
        // ---- display classification ----------------------------------------

        internal static bool IsTableDisplay(string disp) {
            return disp == "table" || disp == "inline-table"
                || disp == "table-row-group" || disp == "table-header-group" || disp == "table-footer-group"
                || disp == "table-row" || disp == "table-cell" || disp == "table-caption"
                || disp == "table-column" || disp == "table-column-group";
        }

        // CSS Display 3 §2.7 — the inline-level outer displays, i.e. the ones
        // that blockify when the box leaves the flow. A missing/empty value is
        // the initial `inline`.
        internal static bool IsInlineLevelDisplay(string disp) {
            return string.IsNullOrEmpty(disp) || disp == "inline" || disp == "inline-block"
                || disp == "inline-flex" || disp == "inline-grid" || disp == "inline-table";
        }

        // §2.7's value table. Only the OUTER display becomes block, so
        // `inline-flex` becomes `flex` and NOT `block` — an absolutely
        // positioned flex container must still lay its children out as flex.
        internal static string Blockified(string disp) {
            switch (disp) {
                case "inline-flex": return "flex";
                case "inline-grid": return "grid";
                case "inline-table": return "table";
                default: return "block";
            }
        }

        // CSS 2.1 §9.7: an out-of-flow or floated box is blockified. Returns
        // `disp` unchanged when the box stays in flow.
        //
        // `blockifyInlines` is set when the parent is a flex or grid container,
        // whose children are blockified anyway (CSS Flexbox §3 / Grid §6.4) and
        // whose items cannot float — the caller handles that case itself, so
        // this rule steps aside.
        internal static string BlockifyForOutOfFlow(string disp, ComputedStyle style, bool blockifyInlines) {
            if (blockifyInlines || !IsInlineLevelDisplay(disp)) return disp;
            string pos = KeywordName(style?.GetParsed(CssProperties.PositionId));
            bool outOfFlow = pos == "absolute" || pos == "fixed";
            if (!outOfFlow) {
                string flt = KeywordName(style?.GetParsed(CssProperties.FloatId));
                outOfFlow = !string.IsNullOrEmpty(flt) && flt != "none";
            }
            return outOfFlow ? Blockified(disp) : disp;
        }

        internal static bool IsMulticolContainer(ComputedStyle style) {
            if (style == null) return false;
            string cc = style.Get(CssProperties.ColumnCountId);
            if (!string.IsNullOrEmpty(cc) && cc != "auto") return true;
            string cw = style.Get(CssProperties.ColumnWidthId);
            if (!string.IsNullOrEmpty(cw) && cw != "auto") return true;
            return false;
        }

        // ---- small value readers -------------------------------------------

        internal static string KeywordName(CssValue parsed) {
            if (parsed is CssKeyword k) return k.Identifier;
            if (parsed is CssIdentifier id) return id.Name;
            return null;
        }

        internal static bool IsAutoOrMissing(CssValue parsed) {
            if (parsed == null) return true;
            string name = KeywordName(parsed);
            return name == "auto";
        }

        internal static double ReadSimplePx(string raw, double fallback) {
            if (string.IsNullOrEmpty(raw)) return fallback;
            if (!raw.EndsWith("px")) return fallback;
            if (double.TryParse(raw.AsSpan(0, raw.Length - 2),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double v)) return v;
            return fallback;
        }

        // Mirrors BlockLayout.IsBorderBox, readable from either builder without
        // taking a dependency on the layout pass.
        internal static bool IsBorderBox(ComputedStyle style) {
            if (style == null) return false;
            var v = style.GetParsed(CssProperties.BoxSizingId);
            if (v is CssKeyword k) return k.Identifier == "border-box";
            if (v is CssIdentifier id) return id.Name == "border-box";
            return style.Get(CssProperties.BoxSizingId) == "border-box";
        }

        internal static bool HasAttr(Element e, string name) {
            return e.Attributes != null && e.Attributes.Contains(name);
        }

        internal static bool TryParseIntAttr(string s, out int value) {
            value = 0;
            if (string.IsNullOrEmpty(s)) return false;
            return int.TryParse(s, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        // ---- field-sizing (CSS Basic User Interface L4 §13) ------------------

        // Per-character width used when no live metrics are available. 8px at
        // 16px font-size is exact for MonoFontMetrics, so headless tests are
        // deterministic.
        internal const double StubCharWidthPx = 8.0;
        // A caret needs somewhere to sit past the last glyph.
        internal const double FieldSizingCaretPaddingPx = 4.0;

        // `field-sizing: content` replaces an input's fixed UA width with the
        // intrinsic width of its value (or placeholder, so an empty field still
        // has a caret's worth of room). Writes the result into the element's
        // ComputedStyle as a px `width`, the same trick MaybeApplyImgIntrinsicSize
        // uses; ApplyBoxModel then applies min/max clamping as usual.
        //
        // v1 scope: `<input>` only. Widget-sized types (checkbox, radio, range,
        // buttons, file, color, image) size from the widget, not the text.
        internal static void ApplyFieldSizingWidth(Element e, ComputedStyle style, IFontMetrics metrics) {
            if (e == null || style == null) return;
            if (e.TagName != "input") return;
            string inputType = e.GetAttribute("type");
            if (!string.IsNullOrEmpty(inputType)) {
                string t = CssStringUtil.ToLowerInvariantOrSame(inputType);
                if (t == "checkbox" || t == "radio" || t == "range"
                    || t == "submit" || t == "button" || t == "reset"
                    || t == "image" || t == "file" || t == "color") {
                    return;
                }
            }
            string fieldSizing = style.Get("field-sizing");
            if (string.IsNullOrEmpty(fieldSizing) || fieldSizing != "content") return;

            string value = e.GetAttribute("value") ?? "";
            if (value.Length == 0) {
                string ph = e.GetAttribute("placeholder");
                if (!string.IsNullOrEmpty(ph)) value = ph;
            }

            double textWidth;
            if (metrics != null && value.Length > 0) {
                double fontSize = 16.0;
                string fsRaw = style.Get(CssProperties.FontSizeId);
                if (!string.IsNullOrEmpty(fsRaw) && fsRaw.EndsWith("px")) {
                    if (double.TryParse(fsRaw.AsSpan(0, fsRaw.Length - 2),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double fsVal) && fsVal > 0) {
                        fontSize = fsVal;
                    }
                }
                textWidth = metrics.Measure(value, fontSize);
            } else {
                textWidth = value.Length * StubCharWidthPx;
            }

            // The UA sheet makes inputs border-box, so the `width` written here
            // is the OUTER size and must carry padding + border.
            double padL = ReadSimplePx(style.Get(CssProperties.PaddingLeftId), 8.0);
            double padR = ReadSimplePx(style.Get(CssProperties.PaddingRightId), 8.0);
            double bordL = ReadSimplePx(style.Get(CssProperties.BorderLeftWidthId), 1.0);
            double bordR = ReadSimplePx(style.Get(CssProperties.BorderRightWidthId), 1.0);

            double intrinsicWidth = IsBorderBox(style)
                ? textWidth + FieldSizingCaretPaddingPx + padL + padR + bordL + bordR
                : textWidth + FieldSizingCaretPaddingPx;
            if (intrinsicWidth < 0) intrinsicWidth = 0;

            style.Set("width", intrinsicWidth.ToString("F2",
                System.Globalization.CultureInfo.InvariantCulture) + "px");
        }
    }
}
