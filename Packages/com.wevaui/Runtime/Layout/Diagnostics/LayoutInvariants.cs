using System;
using System.Text;
using Weva.Css.Cascade;
using Weva.Layout.Boxes;
using Weva.Layout.Positioning;

namespace Weva.Layout.Diagnostics {
    // Layout invariant checker for DEBUG / test builds.
    // Tests opt in by setting Enabled = true (done once in LayoutTestHelpers static ctor).
    // Check(root) throws InvalidOperationException on the first violation.
    public static class LayoutInvariants {
        public static bool Enabled { get; set; }

        public static void Check(Box root) {
            if (!Enabled || root == null) return;
            CheckSubtree(root);
        }

        static void CheckSubtree(Box box) {
            if (box is LineBox lb) {
                CheckLineBox(lb);
            } else if (box is BlockBox bb) {
                CheckBlockBox(bb);
            } else {
                CheckBaseSize(box);
            }
            foreach (var child in box.Children) CheckSubtree(child);
        }

        // --- Invariant: no negative sizes (allow rounding noise) ---
        static void CheckBaseSize(Box box) {
            if (box.Width < -0.5)
                Fail(box, $"Width={box.Width:F3} < -0.5");
            if (box.Height < -0.5)
                Fail(box, $"Height={box.Height:F3} < -0.5");
        }

        // --- BlockBox: no negative sizes + min-width floor ---
        static void CheckBlockBox(BlockBox bb) {
            CheckBaseSize(bb);
            // min-width: skip for OOF (absolute/fixed) — different containing block.
            if (bb.Position == PositionType.Absolute || bb.Position == PositionType.Fixed) return;
            if (bb.Style == null) return;
            double minWidthPx = ResolveMinWidthPx(bb);
            if (minWidthPx > 0 && bb.Width < minWidthPx - 0.5) {
                // Check flex-shrink; if exactly 0 the container may still shrink if
                // something else stomps the width (not a false positive). We skip
                // only the narrow case of explicit flex-shrink:0 to avoid masking bugs.
                // flex-shrink:0 boxes genuinely can't shrink below their base-size but
                // that base-size already includes min-width per spec — so if Width <
                // minWidth even with flex-shrink:0 that's still a violation we want to see.
                Fail(bb, $"Width={bb.Width:F3} < min-width floor {minWidthPx:F3}");
            }
        }

        // --- LineBox: no negative sizes; child X must not be deeply negative ---
        // NOTE: LineBox.Width is the line's OWN content extent, NOT the container width.
        // text-align: center/right shifts children's X PAST LineBox.Width via OffsetLine
        // without updating LineBox.Width (by design — OffsetLine is a pure translation).
        // Therefore we cannot assert childRight <= LineBox.Width+tol here.
        // We do assert childX >= -1 to catch the probe-corruption class where an
        // absolutely-positioned box's text runs land at large negative X (real bug
        // surfaced in AbsolutePositionTests.Inset_zero_flex_container).
        static void CheckLineBox(LineBox lb) {
            CheckBaseSize(lb);
            foreach (var child in lb.Children) {
                double childX = child.X;
                // -1px tolerance for sub-pixel rounding on right-to-left or
                // letter-spaced layouts. Anything more negative is a real corruption.
                if (childX < -1.0) {
                    Fail(lb, $"LineBox child starts at X={childX:F3} < -1 (child: {BoxLabel(child)})");
                }
            }
        }

        // Returns resolved min-width in px, 0 if auto/unset.
        static double ResolveMinWidthPx(BlockBox bb) {
            if (bb.Style == null) return 0;
            string raw = bb.Style.Get(CssProperties.MinWidthId);
            if (string.IsNullOrEmpty(raw) || raw == "auto" || raw == "0") return 0;
            // Fast path: plain pixel value (most common in tests).
            if (raw.EndsWith("px", StringComparison.OrdinalIgnoreCase)) {
                if (double.TryParse(raw.AsSpan(0, raw.Length - 2),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double px)) {
                    return px;
                }
            }
            // Ignore percentage / em / other relative units — would need containing-block
            // context to resolve reliably here. Only absolute px matters for the
            // test topologies this invariant is designed to catch.
            return 0;
        }

        static string BoxLabel(Box b) {
            if (b is TextRun tr) return $"TextRun(\"{tr.Text}\")";
            if (b is BlockBox bb) return $"BlockBox(tag={bb.Element?.TagName ?? "?"})";
            return b.GetType().Name;
        }

        static void Fail(Box box, string message) {
            var sb = new StringBuilder();
            sb.Append("[LayoutInvariants] ");
            sb.Append(message);
            sb.Append(" | box=");
            sb.Append(BoxLabel(box));
            sb.Append(" X=");
            sb.Append(box.X.ToString("F2"));
            sb.Append(" Y=");
            sb.Append(box.Y.ToString("F2"));
            sb.Append(" W=");
            sb.Append(box.Width.ToString("F2"));
            sb.Append(" H=");
            sb.Append(box.Height.ToString("F2"));
            throw new InvalidOperationException(sb.ToString());
        }
    }
}
