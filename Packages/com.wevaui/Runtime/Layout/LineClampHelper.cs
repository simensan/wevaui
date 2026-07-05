using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Layout.Boxes;
using Weva.Layout.Text;

namespace Weva.Layout {
    // CSS Overflow Module Level 4 §6 — multi-line ellipsis via `line-clamp`.
    //
    // Activates when the IFC container's `line-clamp` (or legacy
    // `-webkit-line-clamp`) resolves to a positive integer AND the
    // populated line count exceeds it. The Nth (last visible) line is
    // truncated with "…" appended; lines past N are removed and their
    // boxes returned to the pool.
    //
    // v1 simplifications vs spec:
    //   - Trigger relaxes the CSS Overflow L4 requirement that an explicit
    //     `overflow: hidden` is set. The IFC container's role as the
    //     containing block already makes the truncation visible, and
    //     authors typically pair line-clamp with overflow:hidden anyway.
    //   - We append "…" at the end of the Nth line's last TextRun without
    //     re-fitting the rest of the line. If the appended ellipsis
    //     would push the line past its width budget, EllipsisHelper's
    //     single-line path is NOT consulted — that's a separate concern
    //     for `text-overflow: ellipsis` on a nowrap line. line-clamp's
    //     v1 contract: the ellipsis lands where the last visible run ends.
    //
    // Multi-line ellipsis composition with `text-overflow: ellipsis` is
    // intentionally NOT combined — authors choose one or the other. This
    // matches Chrome's behaviour where the legacy `-webkit-line-clamp`
    // path supersedes text-overflow handling.
    internal static class LineClampHelper {
        const string EllipsisGlyph = "…";

        public static void ApplyIfNeeded(BlockBox container, LayoutContext ctx, BoxPool pool) {
            if (container == null || container.Style == null) return;
            int n = ResolveClampLineCount(container.Style);
            if (n <= 0) return;

            // Walk children once to count LineBoxes and locate the Nth.
            // Non-LineBox children (inline-block atoms re-attached later by
            // AttachInlineFragmentsToLines) don't count for line-clamp.
            int seenLines = 0;
            int nthIndex = -1;
            int firstClippedIndex = -1;
            var children = container.ChildList;
            for (int i = 0; i < children.Count; i++) {
                if (!(children[i] is LineBox)) continue;
                seenLines++;
                if (seenLines == n) nthIndex = i;
                else if (seenLines == n + 1 && firstClippedIndex < 0) firstClippedIndex = i;
            }
            if (firstClippedIndex < 0) return; // Container already fits.

            // Append "…" to the Nth line's last TextRun (or replace it).
            if (nthIndex >= 0 && children[nthIndex] is LineBox nthLine) {
                AppendEllipsisToLine(nthLine, container.Style, ctx);
            }

            // Remove and recycle lines past N. Walk from end so index shifts
            // don't matter.
            for (int i = children.Count - 1; i >= firstClippedIndex; i--) {
                if (!(children[i] is LineBox lb)) continue;
                RecycleLineContent(lb, pool);
                container.RemoveChildAt(i);
                pool.Recycle(lb);
            }
        }

        static int ResolveClampLineCount(ComputedStyle style) {
            // CSS Overflow L4 §6 spec form first; fall back to the legacy
            // -webkit-prefixed alias that most existing author CSS uses.
            string raw = style.Get(CssProperties.GetId("line-clamp"));
            if (string.IsNullOrEmpty(raw) || raw == "none") {
                raw = style.Get(CssProperties.GetId("-webkit-line-clamp"));
            }
            if (string.IsNullOrEmpty(raw) || raw == "none") return 0;
            // Strip whitespace; line-clamp's grammar is a single <integer>.
            // "auto" is not yet supported (spec talks about it but no major
            // engine ships it as of writing).
            var trimmed = raw.Trim();
            if (int.TryParse(trimmed, System.Globalization.NumberStyles.Integer,
                             System.Globalization.CultureInfo.InvariantCulture, out var n)
                && n > 0) {
                return n;
            }
            return 0;
        }

        // Appends "…" to the last TextRun on `line`. If the line has no
        // TextRun (e.g. only inline-block atoms), synthesize one at the end
        // of the line so authors still see truncation feedback.
        static void AppendEllipsisToLine(LineBox line, ComputedStyle containerStyle, LayoutContext ctx) {
            var lineChildren = line.ChildList;
            TextRun donor = null;
            for (int i = lineChildren.Count - 1; i >= 0; i--) {
                if (lineChildren[i] is TextRun tr) { donor = tr; break; }
            }
            if (donor == null) return;
            // Resolve donor font for measurement.
            double fontSize = donor.FontSize > 0
                ? donor.FontSize
                : StyleResolver.FontSizePx(donor.Style ?? containerStyle, null, ctx);
            string family = !string.IsNullOrEmpty(donor.FontFamily)
                ? donor.FontFamily
                : containerStyle?.Get("font-family");
            var metrics = ctx.GetMetrics(family);
            if (metrics == null) return;
            // Append "…" and recompute the donor's width + the line's width.
            string newText = donor.Text + EllipsisGlyph;
            donor.Width = metrics.Measure(newText, fontSize);
            donor.Text = newText;
            // Line width = max content right. We can't trust pre-recorded
            // line.Width because we just expanded the donor.
            line.Width = MaxContentRight(line);
        }

        static double MaxContentRight(LineBox line) {
            double max = 0;
            var children = line.ChildList;
            for (int i = 0; i < children.Count; i++) {
                double r;
                var c = children[i];
                if (c is TextRun tr) r = tr.X + tr.Width;
                else if (c is BlockBox bb) r = bb.X + bb.Width + bb.MarginRight;
                else continue;
                if (r > max) max = r;
            }
            return max;
        }

        // Returns the line's child boxes to the pool before the line itself
        // is recycled. Mirrors BoxPool's normal RecycleSubtree pattern; we
        // walk back-to-front so RemoveChildAt's index shifts don't matter.
        static void RecycleLineContent(LineBox line, BoxPool pool) {
            for (int i = line.ChildList.Count - 1; i >= 0; i--) {
                var child = line.ChildList[i];
                line.RemoveChildAt(i);
                pool.Recycle(child);
            }
        }
    }
}
