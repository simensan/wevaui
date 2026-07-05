using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Layout.Boxes;
using Weva.Layout.Text;

namespace Weva.Layout {
    // CSS Text Overflow Module Level 3 — single-line ellipsis support.
    //
    // Triggered when ALL of the following hold on the IFC container:
    //   - overflow / overflow-x / overflow-y is one of: hidden | scroll | clip | auto
    //   - white-space is nowrap
    //   - text-overflow is ellipsis
    //
    // Multi-line ellipsis (line-clamp) is a v2 concern.
    internal static class EllipsisHelper {
        const string EllipsisGlyph = "…";

        public static void ApplyIfNeeded(BlockBox container, double availableWidth, LayoutContext ctx, BoxPool pool) {
            if (container == null || container.Style == null) return;
            if (!ShouldApply(container.Style)) return;

            double contentW = container.ContentWidth;
            if (contentW < 0) contentW = 0;

            foreach (var child in container.Children) {
                if (!(child is LineBox line)) continue;
                if (line.Width <= contentW + 1e-9) continue;
                TruncateLine(line, contentW, container.Style, ctx, pool);
            }
        }

        // Allocation-free predicate dispatch: every NormalizedX helper used to
        // run raw.Trim().ToLowerInvariant() on the slot string, allocating two
        // copies per call. This fires once per ellipsis-eligible container per
        // layout pass — adds up on chat / list UIs with many overflowing
        // single-line rows.
        static bool ShouldApply(ComputedStyle style) {
            if (style == null) return false;
            string textOverflow = style.Get(CssProperties.TextOverflowId);
            if (string.IsNullOrEmpty(textOverflow)) return false;
            if (!CssStringUtil.EqualsIgnoreCaseTrimmed(textOverflow, "ellipsis")) return false;
            string ws = style.Get(CssProperties.WhiteSpaceId);
            if (string.IsNullOrEmpty(ws)) return false;
            if (!CssStringUtil.EqualsIgnoreCaseTrimmed(ws, "nowrap")) return false;
            return ClipsOverflow(style);
        }

        static bool ClipsOverflow(ComputedStyle style) {
            // CSS Overflow L3 / Text Overflow L3: text-overflow is an inline-axis
            // concern. For horizontal-tb (v1's only writing mode) the inline axis
            // is overflow-x. The `overflow` shorthand sets both, so it qualifies.
            if (IsClipping(style.Get(CssProperties.OverflowId))) return true;
            if (IsClipping(style.Get(CssProperties.OverflowXId))) return true;
            return false;
        }

        static bool IsClipping(string v) {
            if (string.IsNullOrEmpty(v)) return false;
            return CssStringUtil.EqualsIgnoreCaseTrimmed(v, "hidden")
                || CssStringUtil.EqualsIgnoreCaseTrimmed(v, "scroll")
                || CssStringUtil.EqualsIgnoreCaseTrimmed(v, "clip")
                || CssStringUtil.EqualsIgnoreCaseTrimmed(v, "auto");
        }

        // Truncates the line in-place: walks runs left-to-right, finds the
        // furthest position where what already fits + ellipsis still fits in
        // contentW, replaces the trailing run's text with truncated + "…",
        // and discards every subsequent run on the line.
        static void TruncateLine(LineBox line, double contentW, ComputedStyle containerStyle, LayoutContext ctx, BoxPool pool) {
            // Choose the font from the line's last text run; if there is none,
            // fall back to the container's font.
            TextRun donor = LastTextRun(line);
            IFontMetrics donorMetrics;
            double donorFontSize;
            string donorFamily;
            string donorColor;
            ComputedStyle donorStyle;
            if (donor != null) {
                donorFontSize = donor.FontSize > 0 ? donor.FontSize : StyleResolver.FontSizePx(donor.Style ?? containerStyle, null, ctx);
                donorFamily = !string.IsNullOrEmpty(donor.FontFamily) ? donor.FontFamily : containerStyle?.Get("font-family");
                donorColor = !string.IsNullOrEmpty(donor.Color) ? donor.Color : containerStyle?.Get("color");
                donorStyle = donor.Style ?? containerStyle;
                donorMetrics = ctx.GetMetrics(donorFamily);
            } else {
                donorFontSize = StyleResolver.FontSizePx(containerStyle, null, ctx);
                donorFamily = containerStyle?.Get("font-family");
                donorColor = containerStyle?.Get("color");
                donorStyle = containerStyle;
                donorMetrics = ctx.GetMetrics(donorFamily);
            }
            if (donorMetrics == null) return;

            double ellipsisWidth = donorMetrics.Measure(EllipsisGlyph, donorFontSize);
            double budget = contentW - ellipsisWidth;
            if (budget < 0) budget = 0;

            // Walk runs left-to-right accumulating XOffsets. We snip at the
            // first run whose right edge exceeds the budget. Inline-block
            // atoms (BlockBox children of the line) participate in fragment
            // X positions but we don't truncate them; if they overflow we
            // replace them with ellipsis at their start.
            int snipIndex = -1;
            int snipKeepChars = 0;
            double snipNewWidth = 0;
            for (int i = 0; i < line.Children.Count; i++) {
                var child = line.Children[i];
                double childRight;
                if (child is TextRun tr) {
                    childRight = tr.X + tr.Width;
                    if (childRight <= budget + 1e-9) continue;
                    int keep = LargestPrefixThatFits(donorMetrics, donorFontSize, tr.Text, budget - tr.X);
                    snipIndex = i;
                    snipKeepChars = keep;
                    snipNewWidth = donorMetrics.Measure(SafeSubstring(tr.Text, 0, keep), donorFontSize);
                    break;
                }
                if (child is BlockBox bb) {
                    childRight = bb.X + bb.Width + bb.MarginRight;
                    if (childRight <= budget + 1e-9) continue;
                    snipIndex = i;
                    snipKeepChars = -1;
                    snipNewWidth = 0;
                    break;
                }
            }

            if (snipIndex < 0) {
                // Whole line fits within budget already — only the ellipsis itself
                // pushes it over. Append ellipsis at the end of the last run.
                if (donor != null) {
                    string newText = donor.Text + EllipsisGlyph;
                    double newW = donorMetrics.Measure(newText, donorFontSize);
                    donor.Text = newText;
                    donor.Width = newW;
                } else {
                    AppendEllipsisRun(line, 0, donorStyle, donorFamily, donorFontSize, donorColor, donorMetrics, ellipsisWidth, pool);
                }
                line.Width = ContentRight(line);
                return;
            }

            var snipChild = line.Children[snipIndex];
            double snipBaseX;
            if (snipChild is TextRun trS) {
                if (snipKeepChars > 0) {
                    string kept = SafeSubstring(trS.Text, 0, snipKeepChars);
                    trS.Text = kept + EllipsisGlyph;
                    trS.Width = snipNewWidth + ellipsisWidth;
                    snipBaseX = trS.X;
                } else {
                    // Nothing of the run fits even before the ellipsis; replace
                    // the run with a pure ellipsis run sitting at trS.X.
                    snipBaseX = trS.X;
                    trS.Text = EllipsisGlyph;
                    trS.Width = ellipsisWidth;
                }
            } else if (snipChild is BlockBox bb) {
                snipBaseX = bb.X - bb.MarginLeft;
                AppendEllipsisRun(line, snipBaseX, donorStyle, donorFamily, donorFontSize, donorColor, donorMetrics, ellipsisWidth, pool);
                line.RemoveChildAt(snipIndex);
                snipIndex = line.Children.Count - 1;
            } else {
                snipBaseX = 0;
            }

            // Remove every child after the snip point. Iterate from the end so
            // index shifts don't matter.
            for (int i = line.Children.Count - 1; i > snipIndex; i--) {
                line.RemoveChildAt(i);
            }

            line.Width = ContentRight(line);
        }

        static double ContentRight(LineBox line) {
            double max = 0;
            foreach (var c in line.Children) {
                double r;
                if (c is TextRun tr) r = tr.X + tr.Width;
                else if (c is BlockBox bb) r = bb.X + bb.Width + bb.MarginRight;
                else continue;
                if (r > max) max = r;
            }
            return max;
        }

        static TextRun LastTextRun(LineBox line) {
            for (int i = line.Children.Count - 1; i >= 0; i--) {
                if (line.Children[i] is TextRun tr) return tr;
            }
            return null;
        }

        static int LargestPrefixThatFits(IFontMetrics metrics, double fontSize, string text, double maxWidth) {
            if (string.IsNullOrEmpty(text)) return 0;
            if (maxWidth <= 0) return 0;
            int lo = 0;
            int hi = text.Length;
            int best = 0;
            while (lo <= hi) {
                int mid = (lo + hi) >> 1;
                int snapped = SnapToGraphemeBoundary(text, mid);
                if (snapped <= 0) {
                    lo = mid + 1;
                    continue;
                }
                double w = metrics.Measure(SafeSubstring(text, 0, snapped), fontSize);
                if (w <= maxWidth + 1e-9) {
                    if (snapped > best) best = snapped;
                    lo = mid + 1;
                } else {
                    hi = mid - 1;
                }
            }
            return best;
        }

        static int SnapToGraphemeBoundary(string s, int pos) {
            if (pos <= 0) return 0;
            if (pos >= s.Length) return s.Length;
            if (char.IsHighSurrogate(s[pos - 1]) && char.IsLowSurrogate(s[pos])) return pos - 1;
            return pos;
        }

        static string SafeSubstring(string s, int start, int len) {
            if (string.IsNullOrEmpty(s) || len <= 0) return "";
            if (start < 0) start = 0;
            if (start + len > s.Length) len = s.Length - start;
            if (len <= 0) return "";
            return s.Substring(start, len);
        }

        static void AppendEllipsisRun(LineBox line, double xPos, ComputedStyle style, string family, double fontSize, string color, IFontMetrics metrics, double width, BoxPool pool) {
            var run = pool.AllocateTextRun();
            run.Text = EllipsisGlyph;
            run.Style = style;
            run.FontFamily = family;
            run.FontSize = fontSize;
            run.Color = color;
            run.X = xPos;
            run.Width = width;
            run.Height = metrics != null ? metrics.LineHeight(fontSize) : fontSize * StyleResolver.DefaultLineHeightFactor;
            line.AddChild(run);
        }
    }
}
