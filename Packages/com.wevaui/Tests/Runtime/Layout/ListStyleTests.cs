using NUnit.Framework;
using Weva.Dom;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // Coverage for CSS Lists 3 `list-style*` longhands and the
    // `list-style` shorthand. The expanded ListStyleExpandedTests file
    // covers the additional Counter Styles 3 type identifiers, the
    // `list-style-position` longhand, the `list-style-image` longhand,
    // and shorthand parsing in any order. This file keeps the original
    // disc / decimal / none coverage so a regression in the core path
    // still surfaces here.
    public class ListStyleTests {
        // Mirror ListMarkerTests: LayoutTestHelpers.BuiltinUserAgent does
        // not declare the `<ul>` / `<ol>` list-style-type defaults, so
        // tests that rely on default-by-tag opt into this UA sheet.
        const string ListUA = "ul { list-style-type: disc; } ol { list-style-type: decimal; }";

        // Walks the box tree and returns the marker (inline-block BlockBox
        // with no Element identity) at the start of the named li, or null
        // if no marker was injected.
        static BlockBox FindMarker(Box root, int liIndex = 0) {
            int seen = 0;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element?.TagName == "li") {
                    if (seen == liIndex) {
                        if (bb.Children.Count == 0) return null;
                        var first = bb.Children[0];
                        if (first is BlockBox marker && marker.Element == null && marker.IsInlineBlock) {
                            return marker;
                        }
                        return null;
                    }
                    seen++;
                }
            }
            return null;
        }

        static string MarkerText(BlockBox marker) {
            if (marker == null || marker.Children.Count == 0) return null;
            return (marker.Children[0] as TextRun)?.Text;
        }

        // ─── list-style-type ────────────────────────────────────────────

        [Test]
        public void Ul_default_list_style_type_is_disc() {
            var (root, _) = BuildBoxesOnly("<ul><li>x</li></ul>", ListUA);
            var marker = FindMarker(root);
            Assert.That(marker, Is.Not.Null, "ul > li should have a marker box");
            Assert.That(MarkerText(marker), Is.EqualTo("•"));
        }

        [Test]
        public void Ol_default_list_style_type_is_decimal() {
            var (root, _) = BuildBoxesOnly("<ol><li>x</li></ol>", ListUA);
            var marker = FindMarker(root);
            Assert.That(marker, Is.Not.Null);
            Assert.That(MarkerText(marker), Is.EqualTo("1."));
        }

        [Test]
        public void List_style_type_none_suppresses_marker() {
            var (root, _) = BuildBoxesOnly(
                "<ul><li style=\"list-style-type: none\">x</li></ul>", ListUA);
            // Walk the li children manually — FindMarker would also return
            // null if the marker happened to not be the first child, so be
            // explicit and assert there is no Element-less inline-block.
            BlockBox li = null;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element?.TagName == "li") { li = bb; break; }
            }
            Assert.That(li, Is.Not.Null);
            foreach (var c in li.Children) {
                if (c is BlockBox bb && bb.Element == null && bb.IsInlineBlock) {
                    Assert.Fail("list-style-type:none must suppress the marker box");
                }
            }
        }

        // Type-variant coverage now lives in ListStyleExpandedTests
        // (Counter Styles 3 §6 identifiers). The smoke checks for the
        // three core values (disc / decimal / none) are above; the
        // expanded file pins each additional identifier.

        [Test]
        public void Ol_explicit_decimal_round_trips() {
            // Setting decimal explicitly must match the UA-defaulted shape.
            var (root, _) = BuildBoxesOnly(
                "<ol><li style=\"list-style-type: decimal\">x</li></ol>", ListUA);
            var marker = FindMarker(root);
            Assert.That(marker, Is.Not.Null);
            Assert.That(MarkerText(marker), Is.EqualTo("1."));
        }

        // list-style-position and list-style-image longhand coverage has
        // moved to ListStyleExpandedTests. We keep the smoke check that
        // the marker is emitted as the first inline-block child of the
        // li, which is the shape both `outside` (default) and `inside`
        // resolve to in v2.

        [Test]
        public void Default_marker_is_inline_block_first_child_of_li() {
            var (root, _) = BuildBoxesOnly(
                "<ul><li>x</li></ul>", ListUA);
            BlockBox li = null;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element?.TagName == "li") { li = bb; break; }
            }
            Assert.That(li, Is.Not.Null);
            Assert.That(li.Children.Count, Is.GreaterThan(0));
            var first = li.Children[0] as BlockBox;
            Assert.That(first, Is.Not.Null);
            Assert.That(first.Element, Is.Null, "marker has no DOM identity");
            Assert.That(first.IsInlineBlock, Is.True);
        }

        // ─── Counter increment per item ─────────────────────────────────

        [Test]
        public void Ol_three_items_show_1_2_3() {
            var (root, _) = BuildBoxesOnly(
                "<ol><li>a</li><li>b</li><li>c</li></ol>", ListUA);
            string[] expected = { "1.", "2.", "3." };
            int idx = 0;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element?.TagName == "li") {
                    var marker = bb.Children[0] as BlockBox;
                    Assert.That(marker, Is.Not.Null, $"li #{idx} should have a marker");
                    var run = marker.Children[0] as TextRun;
                    Assert.That(run, Is.Not.Null);
                    Assert.That(run.Text, Is.EqualTo(expected[idx]),
                        $"li #{idx} marker should read {expected[idx]}");
                    idx++;
                }
            }
            Assert.That(idx, Is.EqualTo(3));
        }

        // ─── Nested lists ───────────────────────────────────────────────

        [Test]
        public void Nested_ul_default_marker_is_still_disc_in_v1() {
            // Chrome's UA stylesheet rotates marker style by nesting depth
            // (disc → circle → square). v1 has no depth-aware UA rule and
            // `circle` / `square` would fall back to disc anyway, so every
            // nesting level renders U+2022.
            // v1 gap: no nesting-depth UA rotation of list-style-type.
            var (root, _) = BuildBoxesOnly(
                "<ul><li>outer<ul><li>inner</li></ul></li></ul>", ListUA);
            int markerCount = 0;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element == null && bb.IsInlineBlock) {
                    var run = bb.Children.Count > 0 ? bb.Children[0] as TextRun : null;
                    if (run != null && run.Text == "•") markerCount++;
                }
            }
            Assert.That(markerCount, Is.EqualTo(2),
                "outer and inner li should both emit U+2022 disc markers in v1");
        }
    }
}
