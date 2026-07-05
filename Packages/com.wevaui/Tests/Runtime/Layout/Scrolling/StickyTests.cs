using NUnit.Framework;
using Weva.Layout.Boxes;
using Weva.Layout.Positioning;
using Weva.Layout.Scrolling;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Positioning.PositioningTestHelpers;

namespace Weva.Tests.Layout.Scrolling {
    public class StickyTests {
        static (Box root, ScrollContainer sc, BlockBox sticky, BlockBox host) BuildSticky(string css, string html) {
            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 600);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);
            new StickyResolver(sc).Resolve(root);
            BlockBox sticky = FirstByClass(root, "sticky");
            BlockBox host = FirstByClass(root, "host");
            return (root, sc, sticky, host);
        }

        [Test]
        public void Sticky_inside_visible_parent_uses_relative_offset() {
            const string css = ".host { height: 200px; } .sticky { position: sticky; top: 25px; height: 30px; width: 100px; }";
            const string html = "<div class=\"host\"><div class=\"sticky\"></div></div>";
            var (_, _, sticky, _) = BuildSticky(css, html);
            // No scroll ancestor: behaves like relative — top:25px shifts the
            // box by 25 in StickyOffsetY.
            Assert.That(sticky.StickyOffsetY, Is.EqualTo(25).Within(0.001));
        }

        [Test]
        public void Sticky_inside_overflow_auto_with_zero_scroll_does_not_pin() {
            const string css = ".host { overflow: auto; height: 200px; width: 200px; } " +
                               ".sticky { position: sticky; top: 0; height: 30px; width: 100px; } " +
                               ".filler { height: 400px; }";
            const string html = "<div class=\"host\"><div class=\"sticky\"></div><div class=\"filler\"></div></div>";
            var (_, _, sticky, _) = BuildSticky(css, html);
            // At scrollY=0 and natural Y=0, sticky offset is 0.
            Assert.That(sticky.StickyOffsetY, Is.EqualTo(0).Within(0.001));
        }

        [Test]
        public void Sticky_pins_when_scroll_passes_natural_top() {
            const string css = ".host { overflow: auto; height: 200px; width: 200px; } " +
                               ".lead { height: 100px; } " +
                               ".sticky { position: sticky; top: 0; height: 30px; width: 100px; } " +
                               ".filler { height: 400px; }";
            const string html = "<div class=\"host\"><div class=\"lead\"></div><div class=\"sticky\"></div><div class=\"filler\"></div></div>";
            var (root, sc, sticky, host) = BuildSticky(css, html);
            var state = sc.Get(host);
            // Scroll past lead (100px tall) — sticky should pin to top of host.
            state.ScrollY = 150;
            new StickyResolver(sc).Resolve(root);
            // Pinned-Y in container space = scrollY + specifiedTop = 150;
            // natural-Y = 100 (after lead). Offset = 150 - 100 = 50.
            Assert.That(sticky.StickyOffsetY, Is.EqualTo(50).Within(0.001));
        }

        [Test]
        public void Sticky_with_top_offset_pins_at_offset_from_edge() {
            const string css = ".host { overflow: auto; height: 200px; width: 200px; } " +
                               ".lead { height: 100px; } " +
                               ".sticky { position: sticky; top: 10px; height: 30px; width: 100px; } " +
                               ".filler { height: 400px; }";
            const string html = "<div class=\"host\"><div class=\"lead\"></div><div class=\"sticky\"></div><div class=\"filler\"></div></div>";
            var (root, sc, sticky, host) = BuildSticky(css, html);
            var state = sc.Get(host);
            state.ScrollY = 150;
            new StickyResolver(sc).Resolve(root);
            // Pinned-Y = 150 + 10 = 160; natural = 100; offset = 60.
            Assert.That(sticky.StickyOffsetY, Is.EqualTo(60).Within(0.001));
        }

        [Test]
        public void Sticky_stops_pinning_after_containing_block_passes() {
            const string css = ".host { overflow: auto; height: 200px; width: 200px; } " +
                               ".cb { height: 200px; } " +
                               ".sticky { position: sticky; top: 0; height: 30px; width: 100px; } " +
                               ".tail { height: 600px; }";
            const string html = "<div class=\"host\"><div class=\"cb\"><div class=\"sticky\"></div></div><div class=\"tail\"></div></div>";
            var (root, sc, sticky, host) = BuildSticky(css, html);
            var state = sc.Get(host);
            // Scroll way past the containing block — sticky should be released
            // and the offset should clamp so it doesn't escape its CB.
            state.ScrollY = 500;
            new StickyResolver(sc).Resolve(root);
            // Constrained: pinnedY = 500; maxY = cb.Top + cb.Height - sticky.Height = 0 + 200 - 30 = 170.
            // naturalY = 0 (sticky at start of cb). Offset = 170 - 0 = 170.
            Assert.That(sticky.StickyOffsetY, Is.EqualTo(170).Within(0.001));
        }

        [Test]
        public void Sticky_clamp_uses_containing_block_content_edge_not_border_box() {
            // CSS Positioned Layout L3 §6.3: the CB of a sticky element is
            // its parent's content edge. With padding:20 + border:5 around
            // the CB (content height = 150, border-box height = 200), the
            // pin clamp must be content-box: maxY = (paddingTop+borderTop)
            // + contentHeight - childHeight = 25 + 150 - 30 = 145.
            // naturalY for the sticky = 25 (its in-flow position inside
            // cb's content area). Pre-fix (border-box clamp) would yield
            // offset 170 - 25 = 145; post-fix yields 145 - 25 = 120.
            const string css = ".host { overflow: auto; height: 200px; width: 200px; } " +
                               ".cb { padding: 20px; border: 5px solid black; height: 150px; } " +
                               ".sticky { position: sticky; top: 0; height: 30px; width: 100px; } " +
                               ".tail { height: 600px; }";
            const string html = "<div class=\"host\"><div class=\"cb\"><div class=\"sticky\"></div></div><div class=\"tail\"></div></div>";
            var (root, sc, sticky, host) = BuildSticky(css, html);
            var state = sc.Get(host);
            state.ScrollY = 500;
            new StickyResolver(sc).Resolve(root);
            Assert.That(sticky.StickyOffsetY, Is.EqualTo(120).Within(0.001));
        }

        [Test]
        public void Sticky_with_bottom_offset_pins_to_bottom_edge() {
            const string css = ".host { overflow: auto; height: 100px; width: 200px; } " +
                               ".sticky { position: sticky; bottom: 0; height: 30px; width: 100px; } " +
                               ".filler { height: 400px; }";
            const string html = "<div class=\"host\"><div class=\"sticky\"></div><div class=\"filler\"></div></div>";
            var (root, sc, sticky, host) = BuildSticky(css, html);
            var state = sc.Get(host);
            // ScrollY = 0. The sticky element's natural Y = 0. The viewport
            // bottom = 0 + viewportHeight. To pin to bottom: pinnedY =
            // viewportBottom - 30. Since natural=0 < pinnedY, pin upward.
            new StickyResolver(sc).Resolve(root);
            double expectedPinnedY = state.ViewportHeight - 30;
            Assert.That(sticky.StickyOffsetY, Is.EqualTo(expectedPinnedY).Within(0.001));
        }

        [Test]
        public void Sticky_with_both_top_and_bottom_prefers_top_when_both_apply() {
            const string css = ".host { overflow: auto; height: 200px; width: 200px; } " +
                               ".lead { height: 100px; } " +
                               ".sticky { position: sticky; top: 0; bottom: 0; height: 30px; width: 100px; } " +
                               ".filler { height: 400px; }";
            const string html = "<div class=\"host\"><div class=\"lead\"></div><div class=\"sticky\"></div><div class=\"filler\"></div></div>";
            var (root, sc, sticky, host) = BuildSticky(css, html);
            var state = sc.Get(host);
            state.ScrollY = 150;
            new StickyResolver(sc).Resolve(root);
            // Top wins (single-axis simplification): pin at scrollY + 0 = 150.
            // natural=100, offset=50.
            Assert.That(sticky.StickyOffsetY, Is.EqualTo(50).Within(0.001));
        }

        [Test]
        public void Sticky_natural_layout_position_unchanged_by_resolver() {
            const string css = ".host { overflow: auto; height: 200px; width: 200px; } " +
                               ".lead { height: 100px; } " +
                               ".sticky { position: sticky; top: 0; height: 30px; width: 100px; }";
            const string html = "<div class=\"host\"><div class=\"lead\"></div><div class=\"sticky\"></div></div>";
            var (_, sc, sticky, host) = BuildSticky(css, html);
            // sticky.Y is the natural in-flow position, regardless of any scroll.
            double naturalY = sticky.Y;
            sc.Get(host).ScrollY = 80;
            new StickyResolver(sc).Resolve(host);
            Assert.That(sticky.Y, Is.EqualTo(naturalY).Within(0.001));
        }

        [Test]
        public void Sticky_position_type_is_recognized() {
            const string css = ".sticky { position: sticky; top: 0; height: 30px; }";
            const string html = "<div class=\"sticky\"></div>";
            var (_, _, sticky, _) = BuildSticky(css, html);
            Assert.That(sticky.Position, Is.EqualTo(PositionType.Sticky));
        }

        [Test]
        public void Multiple_sticky_descendants_each_resolved() {
            const string css = ".host { overflow: auto; height: 200px; width: 200px; } " +
                               ".s1 { position: sticky; top: 0; height: 30px; } " +
                               ".s2 { position: sticky; top: 0; height: 30px; } " +
                               ".gap { height: 100px; } " +
                               ".tail { height: 400px; }";
            const string html = "<div class=\"host\"><div class=\"s1 sticky\"></div><div class=\"gap\"></div><div class=\"s2\"></div><div class=\"tail\"></div></div>";
            var (root, sc, _, host) = BuildSticky(css, html);
            var state = sc.Get(host);
            state.ScrollY = 50;
            new StickyResolver(sc).Resolve(root);
            // First sticky pinned at top: offset = 50 - 0 = 50.
            // Second sticky still in flow: natural=130, viewportTop=50,
            // pinnedY=50, pinnedY < natural, so offset = 0.
            BlockBox s1 = FirstByClass(root, "s1");
            BlockBox s2 = null;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element != null) {
                    var c = bb.Element.GetAttribute("class");
                    if (c == "s2") { s2 = bb; break; }
                }
            }
            Assert.That(s1.StickyOffsetY, Is.EqualTo(50).Within(0.001));
            Assert.That(s2, Is.Not.Null);
            Assert.That(s2.StickyOffsetY, Is.EqualTo(0).Within(0.001));
        }
    }
}
