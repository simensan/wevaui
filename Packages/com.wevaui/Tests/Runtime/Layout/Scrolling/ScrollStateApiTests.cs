// CSS Overflow Level 3 §3 — scroll container API tests.
//
// Covers: box.IsScrollContainer, box.ScrollState, ScrollState.ScrollLeft/Top
// (clamped setters), ScrollState.MaxScrollLeft/Top aliases, ClientWidth/Height,
// scrollable-overflow metrics, per-axis overflow, nested containers, abs-pos
// descendants, pool-reset wiring, and clip/visible non-container cases.
//
// Run: cd Tools/TestVerifyAll && dotnet run -c Release -- ScrollStateApiTests

using NUnit.Framework;
using Weva.Layout.Boxes;
using Weva.Layout.Scrolling;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Scrolling {

    /// <summary>
    /// Validates the W2 Phase 1 scroll-container layout-core API:
    /// box.IsScrollContainer, box.ScrollState, ScrollState.ScrollLeft/Top
    /// clamped setters, MaxScrollLeft/Top, ClientWidth/Height, and
    /// scrollable-overflow metrics per CSS Overflow L3 §2/§3.
    /// </summary>
    public class ScrollStateApiTests {

        // ────────────────────────────────────────────────────────────────────
        // Helpers
        // ────────────────────────────────────────────────────────────────────

        static (Box container, ScrollContainer sc) BuildWithScroll(string css, string html) {
            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 600);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);
            Box found = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element != null && b.Element.GetAttribute("class") == "box") {
                    found = b; break;
                }
            }
            return (found, sc);
        }

        // ────────────────────────────────────────────────────────────────────
        // 1. IsScrollContainer — overflow auto → true
        // ────────────────────────────────────────────────────────────────────

        [Test]
        public void IsScrollContainer_true_for_overflow_auto() {
            // CSS Overflow L3 §3: auto establishes a scroll container.
            const string css = ".box { overflow: auto; width: 200px; height: 100px; }";
            const string html = "<div class=\"box\"><div style=\"height:400px\"></div></div>";
            var (box, _) = BuildWithScroll(css, html);
            Assert.That(box, Is.Not.Null, "expected .box");
            Assert.That(box.IsScrollContainer, Is.True,
                "overflow:auto must be a scroll container per CSS Overflow L3 §3");
        }

        // ────────────────────────────────────────────────────────────────────
        // 2. IsScrollContainer — overflow scroll → true
        // ────────────────────────────────────────────────────────────────────

        [Test]
        public void IsScrollContainer_true_for_overflow_scroll() {
            const string css = ".box { overflow: scroll; width: 200px; height: 100px; }";
            const string html = "<div class=\"box\"></div>";
            var (box, _) = BuildWithScroll(css, html);
            Assert.That(box.IsScrollContainer, Is.True,
                "overflow:scroll must be a scroll container per CSS Overflow L3 §3");
        }

        // ────────────────────────────────────────────────────────────────────
        // 3. IsScrollContainer — overflow hidden → true (clips, no UI, still SC)
        // ────────────────────────────────────────────────────────────────────

        [Test]
        public void IsScrollContainer_true_for_overflow_hidden() {
            // CSS Overflow L3 §3: hidden still establishes a scroll container;
            // the only difference from scroll/auto is no scrollbar UI and
            // user input cannot scroll it (but programmatic scroll is allowed).
            const string css = ".box { overflow: hidden; width: 200px; height: 100px; }";
            const string html = "<div class=\"box\"><div style=\"height:400px\"></div></div>";
            var (box, _) = BuildWithScroll(css, html);
            Assert.That(box.IsScrollContainer, Is.True,
                "overflow:hidden is a scroll container per CSS Overflow L3 §3 (clips without UI)");
        }

        // ────────────────────────────────────────────────────────────────────
        // 4. IsScrollContainer — overflow visible → false
        // ────────────────────────────────────────────────────────────────────

        [Test]
        public void IsScrollContainer_false_for_overflow_visible() {
            // CSS Overflow L3 §3: visible is NOT a scroll container.
            const string css = ".box { overflow: visible; width: 200px; height: 100px; }";
            const string html = "<div class=\"box\"><div style=\"height:400px\"></div></div>";
            var (box, _) = BuildWithScroll(css, html);
            Assert.That(box.IsScrollContainer, Is.False,
                "overflow:visible must NOT be a scroll container");
        }

        // ────────────────────────────────────────────────────────────────────
        // 5. box.ScrollState is populated by ScrollLayout for auto containers
        // ────────────────────────────────────────────────────────────────────

        [Test]
        public void ScrollState_populated_on_box_after_scroll_layout() {
            const string css = ".box { overflow: auto; width: 200px; height: 100px; }";
            const string html = "<div class=\"box\"><div style=\"height:400px\"></div></div>";
            var (box, _) = BuildWithScroll(css, html);
            Assert.That(box.ScrollState, Is.Not.Null,
                "ScrollLayout.Run must link box.ScrollState for overflow:auto containers");
        }

        // ────────────────────────────────────────────────────────────────────
        // 6. box.ScrollState is null for overflow:visible containers
        // ────────────────────────────────────────────────────────────────────

        [Test]
        public void ScrollState_null_for_overflow_visible() {
            const string css = ".box { overflow: visible; width: 200px; height: 100px; }";
            const string html = "<div class=\"box\"><div style=\"height:400px\"></div></div>";
            var (box, _) = BuildWithScroll(css, html);
            Assert.That(box.ScrollState, Is.Null,
                "overflow:visible box must not have a ScrollState linked");
        }

        // ────────────────────────────────────────────────────────────────────
        // 7. ScrollState.ScrollHeight > clientHeight when content overflows
        // ────────────────────────────────────────────────────────────────────

        [Test]
        public void Tall_content_in_fixed_height_auto_has_scrollHeight_above_clientHeight() {
            // CSS Overflow L3 §2.2: scrollable-overflow rect ≥ padding box.
            // With 400px of content in a 100px container: scrollHeight ≥ 400,
            // maxScroll = scrollHeight − clientHeight ≥ 300.
            const string css = ".box { overflow: auto; width: 200px; height: 100px; }";
            const string html = "<div class=\"box\"><div style=\"height:400px\"></div></div>";
            var (box, _) = BuildWithScroll(css, html);
            var s = box.ScrollState;
            Assert.That(s, Is.Not.Null);
            Assert.That(s.ScrollHeight, Is.GreaterThanOrEqualTo(400 - 0.001),
                "scrollHeight must cover the tallest child");
            Assert.That(s.ClientHeight, Is.EqualTo(s.ViewportHeight).Within(0.001),
                "ClientHeight alias must equal ViewportHeight");
            Assert.That(s.MaxScrollTop, Is.GreaterThanOrEqualTo(300 - 0.001),
                "MaxScrollTop = scrollHeight − clientHeight ≥ 300");
        }

        // ────────────────────────────────────────────────────────────────────
        // 8. Wide content: scrollWidth > clientWidth, maxScrollLeft correct
        // ────────────────────────────────────────────────────────────────────

        [Test]
        public void Wide_content_overflow_x_auto_has_correct_scrollWidth_and_maxScrollLeft() {
            // Horizontal scroll container: child wider than the box.
            const string css =
                ".box { overflow-x: auto; overflow-y: hidden; width: 100px; height: 200px; }" +
                ".wide { width: 500px; height: 50px; }";
            const string html = "<div class=\"box\"><div class=\"wide\"></div></div>";
            var (box, _) = BuildWithScroll(css, html);
            var s = box.ScrollState;
            Assert.That(s, Is.Not.Null);
            Assert.That(s.ScrollWidth, Is.GreaterThanOrEqualTo(500 - 0.001),
                "scrollWidth must cover the wide child");
            Assert.That(s.MaxScrollLeft, Is.GreaterThanOrEqualTo(400 - 0.001),
                "maxScrollLeft = scrollWidth − clientWidth ≥ 400");
        }

        // ────────────────────────────────────────────────────────────────────
        // 9. No overflow → maxScroll is 0 and clamp prevents scroll
        // ────────────────────────────────────────────────────────────────────

        [Test]
        public void No_overflow_maxScroll_is_zero_and_clamp_prevents_scroll() {
            // When content fits, MaxScrollLeft/Top must be 0.
            const string css = ".box { overflow: auto; width: 200px; height: 100px; }";
            const string html = "<div class=\"box\"><div style=\"height:30px\"></div></div>";
            var (box, _) = BuildWithScroll(css, html);
            var s = box.ScrollState;
            Assert.That(s, Is.Not.Null);
            Assert.That(s.MaxScrollTop,  Is.EqualTo(0).Within(0.001),
                "maxScrollTop must be 0 when content fits");
            Assert.That(s.MaxScrollLeft, Is.EqualTo(0).Within(0.001),
                "maxScrollLeft must be 0 when content fits");
            // Attempting to scroll down: setter must clamp to 0.
            s.ScrollTop = 500;
            Assert.That(s.ScrollTop, Is.EqualTo(0).Within(0.001),
                "ScrollTop setter must clamp to MaxScrollTop (0) when content fits");
        }

        // ────────────────────────────────────────────────────────────────────
        // 10. Negative ScrollTop clamps to 0
        // ────────────────────────────────────────────────────────────────────

        [Test]
        public void ScrollTop_setter_clamps_negative_to_zero() {
            // CSS Overflow L3 §6: scroll position is clamped to [0, maxScroll].
            const string css = ".box { overflow: auto; width: 200px; height: 100px; }";
            const string html = "<div class=\"box\"><div style=\"height:400px\"></div></div>";
            var (box, _) = BuildWithScroll(css, html);
            var s = box.ScrollState;
            s.ScrollTop = -99;
            Assert.That(s.ScrollTop, Is.EqualTo(0).Within(0.001),
                "negative ScrollTop must clamp to 0");
            Assert.That(s.ScrollY,   Is.EqualTo(0).Within(0.001),
                "ScrollY must mirror ScrollTop after clamped set");
        }

        // ────────────────────────────────────────────────────────────────────
        // 11. ScrollTop beyond max clamps to maxScrollTop
        // ────────────────────────────────────────────────────────────────────

        [Test]
        public void ScrollTop_setter_clamps_beyond_max_to_maxScrollTop() {
            const string css = ".box { overflow: auto; width: 200px; height: 100px; }";
            const string html = "<div class=\"box\"><div style=\"height:400px\"></div></div>";
            var (box, _) = BuildWithScroll(css, html);
            var s = box.ScrollState;
            double max = s.MaxScrollTop;
            s.ScrollTop = max + 9999;
            Assert.That(s.ScrollTop, Is.EqualTo(max).Within(0.001),
                "ScrollTop beyond max must clamp to MaxScrollTop");
        }

        // ────────────────────────────────────────────────────────────────────
        // 12. ScrollLeft setter mirrors to box.ScrollX and bumps Version
        // ────────────────────────────────────────────────────────────────────

        [Test]
        public void ScrollLeft_setter_mirrors_to_box_ScrollX_and_bumps_Version() {
            // Setting ScrollLeft must: (a) update state.ScrollX, (b) mirror
            // to box.ScrollX so the paint converter and hit-tester use the
            // correct offset, (c) bump state.Version so paint cache invalidates.
            const string css =
                ".box { overflow-x: auto; overflow-y: hidden; width: 100px; height: 200px; }" +
                ".wide { width: 600px; height: 50px; }";
            const string html = "<div class=\"box\"><div class=\"wide\"></div></div>";
            var (box, _) = BuildWithScroll(css, html);
            var s = box.ScrollState;
            Assert.That(s, Is.Not.Null);
            long versionBefore = s.Version;
            double target = 50.0;
            s.ScrollLeft = target;
            Assert.That(s.ScrollLeft, Is.EqualTo(target).Within(0.001),
                "state.ScrollLeft must reflect the set value");
            Assert.That(s.ScrollX, Is.EqualTo(target).Within(0.001),
                "state.ScrollX must mirror ScrollLeft");
            Assert.That(box.ScrollX, Is.EqualTo(target).Within(0.001),
                "box.ScrollX must be updated so paint converter uses the new offset");
            Assert.That(s.Version, Is.GreaterThan(versionBefore),
                "setting ScrollLeft must bump ScrollState.Version for paint cache invalidation");
        }

        // ────────────────────────────────────────────────────────────────────
        // 13. Abs-positioned descendant extends scrollable overflow
        // ────────────────────────────────────────────────────────────────────

        [Test]
        public void Abs_positioned_descendant_extends_scrollable_overflow() {
            // CSS Overflow L3 §2.2: abs-pos descendants whose containing block
            // is the scroll container contribute to scrollable overflow.
            // Here the container is position:relative so it acts as the CB.
            const string css =
                ".box { overflow: auto; position: relative; width: 100px; height: 100px; }" +
                ".abs { position: absolute; left: 0; top: 200px; width: 50px; height: 50px; }";
            const string html = "<div class=\"box\"><div class=\"abs\"></div></div>";
            var (box, _) = BuildWithScroll(css, html);
            var s = box.ScrollState;
            Assert.That(s, Is.Not.Null);
            // The abs child bottom edge is at 200+50=250px in container space.
            Assert.That(s.ScrollHeight, Is.GreaterThanOrEqualTo(250 - 0.001),
                "abs-pos child whose CB is the scroll container must extend scrollHeight");
            Assert.That(s.MaxScrollTop, Is.GreaterThan(0),
                "scrollable content beyond client height → maxScrollTop > 0");
        }

        // ────────────────────────────────────────────────────────────────────
        // 14. Nested scroll containers are independent
        // ────────────────────────────────────────────────────────────────────

        [Test]
        public void Nested_scroll_containers_have_independent_state() {
            const string css =
                ".outer { overflow: auto; width: 300px; height: 200px; }" +
                ".inner { overflow: auto; width: 200px; height: 100px; }";
            const string html =
                "<div class=\"outer\">" +
                "  <div class=\"inner\"><div style=\"height:400px\"></div></div>" +
                "</div>";
            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 600);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);

            Box outer = null, inner = null;
            foreach (var b in AllBoxes(root)) {
                var cls = b.Element?.GetAttribute("class");
                if (cls == "outer") outer = b;
                if (cls == "inner") inner = b;
            }
            Assert.That(outer, Is.Not.Null);
            Assert.That(inner, Is.Not.Null);
            Assert.That(outer.ScrollState, Is.Not.Null, "outer must have ScrollState");
            Assert.That(inner.ScrollState, Is.Not.Null, "inner must have ScrollState");
            Assert.That(outer.ScrollState, Is.Not.SameAs(inner.ScrollState),
                "nested containers must have independent ScrollState instances");

            // Mutate inner without affecting outer.
            double innerMax = inner.ScrollState.MaxScrollTop;
            inner.ScrollState.ScrollTop = innerMax;
            Assert.That(outer.ScrollState.ScrollTop, Is.EqualTo(0).Within(0.001),
                "mutating inner.ScrollTop must not affect outer.ScrollTop");
        }

        // ────────────────────────────────────────────────────────────────────
        // 15. Per-axis: overflow-x hidden / overflow-y auto
        // ────────────────────────────────────────────────────────────────────

        [Test]
        public void PerAxis_x_hidden_y_auto_creates_scroll_container_but_no_horizontal_track() {
            // CSS Overflow L3 §3: per-axis values: x=hidden + y=auto → SC because
            // one axis is non-visible. No horizontal scrollbar track (CanScrollX false).
            const string css =
                ".box { overflow-x: hidden; overflow-y: auto; width: 200px; height: 100px; }";
            const string html = "<div class=\"box\"><div style=\"height:400px\"></div></div>";
            var (box, _) = BuildWithScroll(css, html);
            Assert.That(box.IsScrollContainer, Is.True,
                "per-axis x=hidden y=auto still establishes a scroll container");
            Assert.That(box.ScrollState, Is.Not.Null);
            Assert.That(box.ScrollState.OverflowX, Is.EqualTo(ScrollOverflow.Hidden));
            Assert.That(box.ScrollState.OverflowY, Is.EqualTo(ScrollOverflow.Auto));
            Assert.That(box.ScrollState.ShowsTrackX, Is.False,
                "hidden x-axis must not show a scrollbar track");
            Assert.That(box.ScrollState.ShowsTrackY, Is.True,
                "auto y-axis with overflow must show a scrollbar track");
        }

        // ────────────────────────────────────────────────────────────────────
        // 16. Padding contributes to scrollable overflow end side
        // ────────────────────────────────────────────────────────────────────

        [Test]
        public void End_side_padding_extends_scrollable_overflow() {
            // CSS Overflow L3 §2.2 quirk: the end-side (right/bottom) padding of
            // the scroll container is included in the scrollable overflow rect so
            // a fully-scrolled view still shows the padding gutter.
            const string css =
                ".box { overflow: auto; width: 100px; height: 100px; " +
                "       padding: 20px; box-sizing: border-box; }" +
                ".child { width: 200px; height: 200px; }";
            const string html = "<div class=\"box\"><div class=\"child\"></div></div>";
            var (box, _) = BuildWithScroll(css, html);
            var s = box.ScrollState;
            Assert.That(s, Is.Not.Null);
            // contentWidth=60px (100-40), child=200px → right=200, +paddingRight=20 → 220.
            // contentHeight=60px, child=200px → bottom=200, +paddingBottom=20 → 220.
            Assert.That(s.ScrollWidth,  Is.EqualTo(220).Within(1.0),
                "scrollWidth must include end-side padding per CSS Overflow L3 §2.2");
            Assert.That(s.ScrollHeight, Is.EqualTo(220).Within(1.0),
                "scrollHeight must include end-side padding per CSS Overflow L3 §2.2");
        }

        // ────────────────────────────────────────────────────────────────────
        // 17. overflow:clip is NOT a scroll container (no ScrollState)
        // ────────────────────────────────────────────────────────────────────

        [Test]
        public void Overflow_clip_is_not_a_scroll_container() {
            // CSS Overflow L3 §3: `clip` is a pure paint-time clip — no scroll
            // container established, no ScrollState, no programmatic scroll.
            const string css = ".box { overflow: clip; width: 200px; height: 100px; }";
            const string html = "<div class=\"box\"><div style=\"height:400px\"></div></div>";
            var (box, _) = BuildWithScroll(css, html);
            Assert.That(box.IsScrollContainer, Is.False,
                "overflow:clip must NOT be a scroll container per CSS Overflow L3 §3");
            Assert.That(box.ScrollState, Is.Null,
                "overflow:clip box must not have a ScrollState");
        }

        // ────────────────────────────────────────────────────────────────────
        // 18. ClientWidth / ClientHeight are the padding-box dimensions
        // ────────────────────────────────────────────────────────────────────

        [Test]
        public void ClientWidth_and_ClientHeight_equal_ViewportWidth_and_Height() {
            // CSS CSSOM View §5: clientWidth/clientHeight = scrollport size
            // = padding box (ViewportWidth/Height after scrollbar reservation).
            const string css = ".box { overflow: auto; width: 200px; height: 100px; }";
            const string html = "<div class=\"box\"><div style=\"height:30px\"></div></div>";
            var (box, _) = BuildWithScroll(css, html);
            var s = box.ScrollState;
            Assert.That(s, Is.Not.Null);
            Assert.That(s.ClientWidth,  Is.EqualTo(s.ViewportWidth).Within(0.001));
            Assert.That(s.ClientHeight, Is.EqualTo(s.ViewportHeight).Within(0.001));
        }

        // ────────────────────────────────────────────────────────────────────
        // 19. ScrollTop setter no-ops when value is unchanged
        // ────────────────────────────────────────────────────────────────────

        [Test]
        public void ScrollTop_setter_noop_when_value_unchanged() {
            // Setter must guard on equality to avoid spurious Version bumps.
            const string css = ".box { overflow: auto; width: 200px; height: 100px; }";
            const string html = "<div class=\"box\"><div style=\"height:400px\"></div></div>";
            var (box, _) = BuildWithScroll(css, html);
            var s = box.ScrollState;
            s.ScrollTop = 50;
            long v = s.Version;
            s.ScrollTop = 50; // same value
            Assert.That(s.Version, Is.EqualTo(v),
                "setting ScrollTop to the same value must not bump Version");
        }

        // ────────────────────────────────────────────────────────────────────
        // 20. MaxScrollLeft and MaxScrollTop alias MaxScrollX / MaxScrollY
        // ────────────────────────────────────────────────────────────────────

        [Test]
        public void MaxScrollLeft_and_MaxScrollTop_alias_MaxScrollX_MaxScrollY() {
            const string css = ".box { overflow: scroll; width: 100px; height: 100px; }";
            const string html =
                "<div class=\"box\">" +
                "  <div style=\"width:400px; height:400px\"></div>" +
                "</div>";
            var (box, _) = BuildWithScroll(css, html);
            var s = box.ScrollState;
            Assert.That(s, Is.Not.Null);
            Assert.That(s.MaxScrollLeft, Is.EqualTo(s.MaxScrollX).Within(0.001),
                "MaxScrollLeft must alias MaxScrollX");
            Assert.That(s.MaxScrollTop,  Is.EqualTo(s.MaxScrollY).Within(0.001),
                "MaxScrollTop must alias MaxScrollY");
        }
    }
}
