using System.Collections.Generic;
using NUnit.Framework;
using Weva.Dom;
using Weva.Events;
using static Weva.Tests.Events.EventTestHelpers;

namespace Weva.Tests.Events {
    // Spatial-focus picker used for D-pad / arrow-key navigation in game
    // UIs. Tests pin the half-plane filter (only candidates in the
    // movement direction are considered), the alignment-penalty score
    // (perpendicular offset costs more than parallel distance), the
    // entry-point fallback when nothing is focused, and graceful handling
    // of missing layout data.
    public class DirectionalNavigationTests {
        sealed class RectMap {
            readonly Dictionary<Element, NavRect> map = new();
            public void Add(Element e, double x, double y, double w, double h) {
                map[e] = new NavRect(x, y, w, h);
            }
            public NavRect? Of(Element e) => map.TryGetValue(e, out var r) ? r : (NavRect?)null;
        }

        static (DirectionalNavigation nav, EventDispatcher disp, Document doc, RectMap rects) Build(string html) {
            var doc = Html(html);
            var hit = new FakeHitTester();
            var disp = new EventDispatcher(doc, hit);
            var rects = new RectMap();
            var nav = new DirectionalNavigation(disp, doc, rects.Of);
            return (nav, disp, doc, rects);
        }

        [Test]
        public void Move_down_picks_element_directly_below_focused() {
            var (nav, disp, doc, rects) = Build(
                "<button id=\"a\"></button><button id=\"b\"></button><button id=\"c\"></button>");
            var a = ById(doc, "a");
            var b = ById(doc, "b");
            var c = ById(doc, "c");
            rects.Add(a, 0, 0, 50, 30);
            rects.Add(b, 0, 60, 50, 30);
            rects.Add(c, 0, 120, 50, 30);

            disp.Focus(a);
            var pick = nav.MoveFocus(NavDirection.Down);
            Assert.That(pick, Is.SameAs(b));
            Assert.That(disp.FocusedElement, Is.SameAs(b));
        }

        [Test]
        public void Move_up_at_top_returns_null() {
            var (nav, disp, doc, rects) = Build(
                "<button id=\"a\"></button><button id=\"b\"></button>");
            var a = ById(doc, "a");
            var b = ById(doc, "b");
            rects.Add(a, 0, 0, 50, 30);
            rects.Add(b, 0, 60, 50, 30);

            disp.Focus(a);
            var pick = nav.MoveFocus(NavDirection.Up);
            Assert.That(pick, Is.Null);
            Assert.That(disp.FocusedElement, Is.SameAs(a));
        }

        [Test]
        public void Alignment_beats_raw_distance() {
            // Layout:
            //   [a] (anchor)
            //   [b] far below directly aligned
            //   [c] closer to a but offset way to the right
            // Expected: pressing Down picks `b` because the perpendicular
            // penalty makes off-axis `c` lose despite being closer.
            var (nav, disp, doc, rects) = Build(
                "<button id=\"a\"></button><button id=\"b\"></button><button id=\"c\"></button>");
            var a = ById(doc, "a");
            var b = ById(doc, "b");
            var c = ById(doc, "c");
            rects.Add(a, 0, 0, 40, 30);
            rects.Add(b, 0, 200, 40, 30);
            rects.Add(c, 200, 50, 40, 30);

            disp.Focus(a);
            Assert.That(nav.MoveFocus(NavDirection.Down), Is.SameAs(b));
        }

        [Test]
        public void Right_skips_elements_on_left_side() {
            var (nav, disp, doc, rects) = Build(
                "<button id=\"left\"></button><button id=\"mid\"></button><button id=\"right\"></button>");
            var left = ById(doc, "left");
            var mid = ById(doc, "mid");
            var right = ById(doc, "right");
            rects.Add(left, 0, 0, 50, 30);
            rects.Add(mid, 100, 0, 50, 30);
            rects.Add(right, 200, 0, 50, 30);

            disp.Focus(mid);
            var pick = nav.MoveFocus(NavDirection.Right);
            Assert.That(pick, Is.SameAs(right));
        }

        [Test]
        public void Left_skips_elements_on_right_side() {
            var (nav, disp, doc, rects) = Build(
                "<button id=\"left\"></button><button id=\"mid\"></button><button id=\"right\"></button>");
            var left = ById(doc, "left");
            var mid = ById(doc, "mid");
            var right = ById(doc, "right");
            rects.Add(left, 0, 0, 50, 30);
            rects.Add(mid, 100, 0, 50, 30);
            rects.Add(right, 200, 0, 50, 30);

            disp.Focus(mid);
            var pick = nav.MoveFocus(NavDirection.Left);
            Assert.That(pick, Is.SameAs(left));
        }

        [Test]
        public void No_current_focus_picks_topmost_leftmost_entry() {
            // Out-of-box behavior: opening a menu with nothing focused and
            // pressing any direction lights up the first focusable.
            var (nav, disp, doc, rects) = Build(
                "<button id=\"top\"></button><button id=\"bottom\"></button>");
            var top = ById(doc, "top");
            var bottom = ById(doc, "bottom");
            rects.Add(top, 100, 50, 40, 30);
            rects.Add(bottom, 100, 200, 40, 30);

            var pick = nav.MoveFocus(NavDirection.Down);
            Assert.That(pick, Is.SameAs(top));
        }

        [Test]
        public void Disabled_elements_are_skipped() {
            var (nav, disp, doc, rects) = Build(
                "<button id=\"a\"></button><button id=\"b\" disabled></button><button id=\"c\"></button>");
            var a = ById(doc, "a");
            var b = ById(doc, "b");
            var c = ById(doc, "c");
            rects.Add(a, 0, 0, 40, 30);
            rects.Add(b, 0, 60, 40, 30);
            rects.Add(c, 0, 120, 40, 30);

            disp.Focus(a);
            // Down skips disabled `b` and lands on `c`.
            Assert.That(nav.MoveFocus(NavDirection.Down), Is.SameAs(c));
        }

        [Test]
        public void Elements_without_layout_rect_are_excluded() {
            // Items not yet laid out (or display:none) return null from
            // rectOf; they shouldn't pollute candidate scoring.
            var (nav, disp, doc, rects) = Build(
                "<button id=\"a\"></button><button id=\"b\"></button><button id=\"c\"></button>");
            var a = ById(doc, "a");
            var b = ById(doc, "b");  // intentionally no rect
            var c = ById(doc, "c");
            rects.Add(a, 0, 0, 40, 30);
            // b is hidden (no rect)
            rects.Add(c, 0, 120, 40, 30);

            disp.Focus(a);
            Assert.That(nav.MoveFocus(NavDirection.Down), Is.SameAs(c));
        }

        [Test]
        public void Custom_perpendicular_penalty_changes_choice() {
            // With high penalty, perfectly-aligned distant `b` wins.
            // With penalty 0, near-but-off-axis `c` wins.
            var (nav, disp, doc, rects) = Build(
                "<button id=\"a\"></button><button id=\"b\"></button><button id=\"c\"></button>");
            var a = ById(doc, "a");
            var b = ById(doc, "b");
            var c = ById(doc, "c");
            rects.Add(a, 0, 0, 40, 30);
            rects.Add(b, 0, 300, 40, 30);
            rects.Add(c, 100, 100, 40, 30);

            disp.Focus(a);
            nav.PerpendicularPenalty = 5.0;
            Assert.That(nav.MoveFocus(NavDirection.Down), Is.SameAs(b));

            disp.Focus(a);
            nav.PerpendicularPenalty = 0.0;
            Assert.That(nav.MoveFocus(NavDirection.Down), Is.SameAs(c));
        }

        [Test]
        public void IsHidden_filter_excludes_elements() {
            var (nav, disp, doc, rects) = Build(
                "<button id=\"a\"></button><button id=\"b\"></button><button id=\"c\"></button>");
            var a = ById(doc, "a");
            var b = ById(doc, "b");
            var c = ById(doc, "c");
            rects.Add(a, 0, 0, 40, 30);
            rects.Add(b, 0, 60, 40, 30);
            rects.Add(c, 0, 120, 40, 30);

            nav.IsHidden = e => e == b;
            disp.Focus(a);
            Assert.That(nav.MoveFocus(NavDirection.Down), Is.SameAs(c));
        }

        [Test]
        public void FindNearest_does_not_change_focus() {
            var (nav, disp, doc, rects) = Build(
                "<button id=\"a\"></button><button id=\"b\"></button>");
            var a = ById(doc, "a");
            var b = ById(doc, "b");
            rects.Add(a, 0, 0, 40, 30);
            rects.Add(b, 0, 60, 40, 30);

            disp.Focus(a);
            var pick = nav.FindNearest(NavDirection.Down, a);
            Assert.That(pick, Is.SameAs(b));
            Assert.That(disp.FocusedElement, Is.SameAs(a), "FindNearest should not actually focus");
        }

        [Test]
        public void NavRect_geometry_computed_from_constructor() {
            var r = new NavRect(10, 20, 100, 50);
            Assert.That(r.Left, Is.EqualTo(10));
            Assert.That(r.Top, Is.EqualTo(20));
            Assert.That(r.Width, Is.EqualTo(100));
            Assert.That(r.Height, Is.EqualTo(50));
            Assert.That(r.Right, Is.EqualTo(110));
            Assert.That(r.Bottom, Is.EqualTo(70));
            Assert.That(r.CenterX, Is.EqualTo(60));
            Assert.That(r.CenterY, Is.EqualTo(45));
        }

        [Test]
        public void Null_dispatcher_or_doc_or_rectof_throws() {
            var doc = Html("<div></div>");
            var disp = new EventDispatcher(doc, new FakeHitTester());
            Assert.Throws<System.ArgumentNullException>(
                () => new DirectionalNavigation(null, doc, _ => null));
            Assert.Throws<System.ArgumentNullException>(
                () => new DirectionalNavigation(disp, null, _ => null));
            Assert.Throws<System.ArgumentNullException>(
                () => new DirectionalNavigation(disp, doc, null));
        }
    }
}
