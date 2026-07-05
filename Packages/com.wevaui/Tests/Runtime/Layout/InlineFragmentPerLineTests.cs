using System.Collections.Generic;
using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // CSS 2.1 §9.4.2 / CSS Inline 3: an inline element that wraps to N lines
    // generates N inline-box fragments — one per line — each covering only
    // that line's portion of the element's bounds. Paint (background, border,
    // text-decoration), hit-testing, and accessibility all consume the
    // fragment list. F2 regression pins.
    public class InlineFragmentPerLineTests {
        static BlockBox FirstByTag(Box root, string tag) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && !(b is AnonymousBlockBox) && bb.Element?.TagName == tag) return bb;
            }
            return null;
        }

        static List<InlineBox> InlineFragmentsFor(Box root, string tag) {
            var list = new List<InlineBox>();
            foreach (var b in AllBoxes(root)) {
                if (b is InlineBox ib && ib.Element?.TagName == tag) list.Add(ib);
            }
            return list;
        }

        static List<LineBox> LinesIn(BlockBox container) {
            var list = new List<LineBox>();
            foreach (var c in container.Children) if (c is LineBox lb) list.Add(lb);
            return list;
        }

        [Test]
        public void Span_wrapping_two_lines_emits_two_inline_fragments_F2() {
            // 16px mono → 8px per char. "alpha bravo" = 11 chars = 88px,
            // exactly fits. "charlie" alone = 56px also fits but " charlie"
            // appended pushes past 88, so the second word wraps to line 2.
            // Result: exactly two lines.
            var (root, _, _) = Build(
                "<p><span>alpha charlie</span></p>",
                null, 56);
            var p = FirstByTag(root, "p");
            Assert.That(p, Is.Not.Null);
            var lines = LinesIn(p);
            Assert.That(lines.Count, Is.EqualTo(2),
                "fixture must wrap to exactly 2 lines");

            var fragments = InlineFragmentsFor(p, "span");
            Assert.That(fragments.Count, Is.EqualTo(lines.Count),
                "wrapped <span> should emit one InlineBox fragment per line");
        }

        [Test]
        public void Span_two_line_fragments_are_vertically_distinct_and_stacked_F2() {
            var (root, _, _) = Build(
                "<p><span>alpha charlie</span></p>",
                null, 56);
            var p = FirstByTag(root, "p");
            var fragments = InlineFragmentsFor(p, "span");
            Assert.That(fragments.Count, Is.EqualTo(2),
                "this fixture expects exactly two-line wrap");

            // Each fragment lives in its OWN LineBox. Compute absolute-y
            // (line.Y + fragment.Y) for both and assert they're vertically
            // separated by roughly a line height.
            double y0 = fragments[0].Parent is LineBox l0
                ? l0.Y + fragments[0].Y : fragments[0].Y;
            double y1 = fragments[1].Parent is LineBox l1
                ? l1.Y + fragments[1].Y : fragments[1].Y;
            Assert.That(y1, Is.GreaterThan(y0),
                "second fragment must sit below the first");
            Assert.That(y1 - y0, Is.GreaterThan(0.5),
                "fragments must be vertically distinct (not overlapping)");

            // Both fragments share the same originating span Element.
            Assert.That(fragments[0].Element, Is.SameAs(fragments[1].Element));
            // And the same cascade-resolved Style (so decorations / background
            // / border resolve identically on every line).
            Assert.That(fragments[0].Style, Is.SameAs(fragments[1].Style));
        }

        [Test]
        public void Single_line_span_emits_exactly_one_inline_fragment_F2_regression() {
            // Plenty of room: 16px font * 5 chars = 40px well under 800.
            var (root, _, _) = Build(
                "<p><span>hello</span></p>",
                null, 800);
            var p = FirstByTag(root, "p");
            var lines = LinesIn(p);
            Assert.That(lines.Count, Is.EqualTo(1),
                "fixture must stay single-line");

            var fragments = InlineFragmentsFor(p, "span");
            Assert.That(fragments.Count, Is.EqualTo(1),
                "single-line span must NOT regress to multi-fragment");
            Assert.That(fragments[0].Parent, Is.SameAs(lines[0]));
        }

        [Test]
        public void Span_wrapping_three_lines_emits_three_non_overlapping_fragments_F2() {
            // 16px mono → 8px per char. Width 56 fits exactly 7 chars.
            // "alpha bravo charlie" → "alpha" (5=40px, fits), wrap on " bravo"
            // since "alpha bravo" = 11 chars = 88 > 56; line 2 = "bravo"
            // (5 chars = 40, fits), wrap on " charlie" → line 3 = "charlie"
            // (7 chars = 56, exact fit). Three lines.
            var (root, _, _) = Build(
                "<p><span>alpha bravo charlie</span></p>",
                null, 56);
            var p = FirstByTag(root, "p");
            var lines = LinesIn(p);
            Assert.That(lines.Count, Is.EqualTo(3),
                "fixture must wrap to exactly three lines");

            var fragments = InlineFragmentsFor(p, "span");
            Assert.That(fragments.Count, Is.EqualTo(lines.Count),
                "fragment count must match line count");

            // Each fragment's parent must be a distinct LineBox.
            var seenLines = new HashSet<LineBox>();
            for (int i = 0; i < fragments.Count; i++) {
                Assert.That(fragments[i].Parent, Is.InstanceOf<LineBox>(),
                    $"fragment {i} must be parented under a LineBox");
                var lb = (LineBox)fragments[i].Parent;
                Assert.That(seenLines.Add(lb), Is.True,
                    $"fragment {i} must be on a unique line");
            }

            // Compute each fragment's absolute (container-local) vertical
            // band and assert no two bands overlap.
            var bands = new List<(double Top, double Bottom)>(fragments.Count);
            for (int i = 0; i < fragments.Count; i++) {
                var lb = (LineBox)fragments[i].Parent;
                double top = lb.Y + fragments[i].Y;
                double bottom = top + fragments[i].Height;
                bands.Add((top, bottom));
            }
            bands.Sort((a, b) => a.Top.CompareTo(b.Top));
            for (int i = 1; i < bands.Count; i++) {
                Assert.That(bands[i].Top, Is.GreaterThanOrEqualTo(bands[i - 1].Bottom - 0.001),
                    $"fragment vertical bands must not overlap (sorted index {i})");
            }
        }
    }
}
