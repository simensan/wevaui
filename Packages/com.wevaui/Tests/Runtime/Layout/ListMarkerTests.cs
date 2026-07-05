using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // CSS Lists 3 §3 — `<li>` inside `<ul>`/`<ol>` gets a marker box at the
    // start of its children. Driven by `list-style-type` on the li (default
    // "disc", overridden to "decimal" by the UA stylesheet for descendants
    // of `<ol>`). v1 implements only "disc", "decimal", and "none".
    public class ListMarkerTests {
        // Local UA stylesheet that sets the list-item / list-style-type bits
        // the marker logic looks at — the shared LayoutTestHelpers.BuiltinUserAgent
        // is intentionally minimal and doesn't include list-style-type rules.
        const string ListUA = "ul { list-style-type: disc; } ol { list-style-type: decimal; }";

        [Test]
        public void Ul_li_renders_disc_bullet_before_content() {
            var (root, _) = BuildBoxesOnly("<ul><li>x</li><li>y</li></ul>", ListUA);
            int markers = 0;
            int lis = 0;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element?.TagName == "li") {
                    lis++;
                    // First child of the li should be the marker — an
                    // inline-block BlockBox with a single TextRun "•".
                    Assert.That(bb.Children.Count, Is.GreaterThan(0));
                    var first = bb.Children[0];
                    Assert.That(first, Is.InstanceOf<BlockBox>(), "li's first child should be the marker block");
                    var marker = (BlockBox)first;
                    Assert.That(marker.Element, Is.Null, "marker has no DOM identity");
                    Assert.That(marker.IsInlineBlock, Is.True);
                    Assert.That(marker.Children.Count, Is.EqualTo(1));
                    var run = marker.Children[0] as TextRun;
                    Assert.That(run, Is.Not.Null);
                    Assert.That(run.Text, Is.EqualTo("•"));
                    markers++;
                }
            }
            Assert.That(lis, Is.EqualTo(2));
            Assert.That(markers, Is.EqualTo(2));
        }

        [Test]
        public void Ol_li_renders_decimal_index() {
            var (root, _) = BuildBoxesOnly("<ol><li>x</li><li>y</li></ol>", ListUA);
            string[] expected = { "1.", "2." };
            int liIdx = 0;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element?.TagName == "li") {
                    var marker = bb.Children[0] as BlockBox;
                    Assert.That(marker, Is.Not.Null);
                    var run = marker.Children[0] as TextRun;
                    Assert.That(run, Is.Not.Null);
                    Assert.That(run.Text, Is.EqualTo(expected[liIdx]));
                    liIdx++;
                }
            }
            Assert.That(liIdx, Is.EqualTo(2));
        }

        [Test]
        public void List_style_type_none_suppresses_marker() {
            var (root, _) = BuildBoxesOnly(
                "<ul><li style=\"list-style-type: none\">x</li></ul>",
                ListUA);
            BlockBox li = null;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element?.TagName == "li") li = bb;
            }
            Assert.That(li, Is.Not.Null);
            // No injected marker means the li's first child is either a
            // TextRun ("x") or no anonymous BlockBox at index 0. Either way,
            // there should be no Element-less inline-block BlockBox child.
            foreach (var c in li.Children) {
                if (c is BlockBox bb && bb.Element == null && bb.IsInlineBlock) {
                    Assert.Fail("Expected no marker box, found one");
                }
            }
        }
    }
}
