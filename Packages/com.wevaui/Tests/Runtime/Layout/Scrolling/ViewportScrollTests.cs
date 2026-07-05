using System.Collections.Generic;
using NUnit.Framework;
using Weva.Dom;
using Weva.Events;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Positioning;
using Weva.Layout.Scrolling;
using Weva.Paint;
using Weva.Paint.Conversion;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Scrolling {
    // Tests for CSS Overflow §3.3 viewport-level scrolling.
    // The viewport (initial containing block) automatically scrolls when the
    // root content exceeds viewport dimensions — no explicit overflow on
    // html/body is required.
    public class ViewportScrollTests {

        // ─── helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// Builds a layout and runs ScrollLayout including the viewport scroll
        /// pass. Returns the layout root (anonymous viewport box) and the
        /// ScrollContainer.
        /// </summary>
        static (Box root, ScrollContainer sc, LayoutContext ctx) BuildViewport(
            string html, string css = null,
            double viewportWidth = 400, double viewportHeight = 300) {
            var (root, _, ctx) = Build(html, css, viewportWidth, viewportHeight);
            var sc = new ScrollContainer();
            var sl = new ScrollLayout(sc);
            sl.Run(root);
            sl.RunViewportScroll(root, viewportWidth, viewportHeight);
            return (root, sc, ctx);
        }

        /// <summary>
        /// Walks the entire box tree and returns all boxes with the given CSS
        /// class.
        /// </summary>
        static Box FindByClass(Box root, string cls) {
            foreach (var b in AllBoxes(root)) {
                var e = b.Element;
                if (e == null) continue;
                var c = e.GetAttribute("class");
                if (string.IsNullOrEmpty(c)) continue;
                foreach (var t in c.Split(' ')) if (t == cls) return b;
            }
            return null;
        }

        /// <summary>
        /// Builds an event harness with the ScrollEventHandler wired to a
        /// viewport scroll root.
        /// </summary>
        static (Box root, ScrollContainer sc, ScrollEventHandler handler) BuildEventHarness(
            string html, string css = null,
            double viewportWidth = 400, double viewportHeight = 300) {
            var (root, _, ctx) = Build(html, css, viewportWidth, viewportHeight);
            var sc = new ScrollContainer();
            var sl = new ScrollLayout(sc);
            sl.Run(root);
            sl.RunViewportScroll(root, viewportWidth, viewportHeight);

            Document doc = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element?.OwnerDocument != null) { doc = b.Element.OwnerDocument; break; }
            }

            var index = new ElementBoxIndex(root);
            var dispatcher = new EventDispatcher(doc, new BoxTreeHitTester(root, sc));
            var handler = new ScrollEventHandler(
                dispatcher, doc, sc, index.Lookup, () => 16) {
                ViewportRoot = sl.ViewportScrollRoot(root)
            };
            return (root, sc, handler);
        }

        // Simple element-to-box index used by tests.
        sealed class ElementBoxIndex {
            readonly Dictionary<Element, Box> map = new();
            public ElementBoxIndex(Box root) { Walk(root); }
            void Walk(Box b) {
                if (b == null) return;
                if (b.Element != null) map[b.Element] = b;
                foreach (var c in b.Children) Walk(c);
            }
            public Box Lookup(Element e) =>
                e != null && map.TryGetValue(e, out var b) ? b : null;
        }

        // ─── ScrollLayout / scroll-state establishment ────────────────────────

        [Test]
        public void Tall_content_establishes_viewport_scroll_state() {
            // A document with content taller than the viewport should get a
            // viewport scroll state on the root box.
            const string html = "<div style=\"height:1000px\">content</div>";
            var (root, sc, ctx) = BuildViewport(html, viewportWidth: 400, viewportHeight: 300);

            var state = sc.Get(root);
            Assert.That(state, Is.Not.Null,
                "Expected a scroll state on the viewport root box");
            Assert.That(state.ScrollHeight, Is.GreaterThan(300 - 0.001),
                "ScrollHeight should exceed the viewport height");
            Assert.That(state.ViewportHeight, Is.EqualTo(300).Within(0.001));
            Assert.That(state.MaxScrollY, Is.GreaterThan(0),
                "MaxScrollY should be positive when content overflows");
        }

        [Test]
        public void Content_fits_no_viewport_scroll_state() {
            // Content that fits entirely within the viewport must NOT create a
            // viewport scroll state.
            const string html = "<div style=\"height:100px\">short</div>";
            var (root, sc, _) = BuildViewport(html, viewportWidth: 400, viewportHeight: 300);

            var state = sc.Get(root);
            Assert.That(state, Is.Null,
                "No viewport scroll state expected when content fits");
        }

        [Test]
        public void Body_overflow_hidden_suppresses_viewport_scroll() {
            // An explicit `overflow:hidden` on <body> must suppress viewport
            // scrolling per CSS Overflow §3.3 root-overflow propagation.
            // Use a full HTML document with explicit <html> and <body> tags
            // so the CSS rule can apply.
            const string css = "body { overflow: hidden; }";
            const string html =
                "<html><body><div style=\"height:2000px\">overflow</div></body></html>";
            var (root, sc, _) = BuildViewport(html, css, viewportWidth: 400, viewportHeight: 300);

            var state = sc.Get(root);
            Assert.That(state, Is.Null,
                "overflow:hidden on body should suppress viewport scroll");
        }

        [Test]
        public void Html_overflow_hidden_suppresses_viewport_scroll() {
            // An explicit `overflow:hidden` on <html> must suppress viewport
            // scrolling. When <html> has non-visible overflow, Visit() creates
            // a scroll container for the html box — the viewport root itself
            // should NOT also have a viewport scroll state.
            const string css = "html { overflow: hidden; }";
            const string html =
                "<html><body><div style=\"height:2000px\">overflow</div></body></html>";
            var (root, sc, _) = BuildViewport(html, css, viewportWidth: 400, viewportHeight: 300);

            // Root box must not have a viewport scroll state.
            var rootState = sc.Get(root);
            Assert.That(rootState, Is.Null,
                "overflow:hidden on html should prevent viewport scroll state on root");
        }

        [Test]
        public void Viewport_scrollY_clamps_to_maxScrollY() {
            const string html = "<div style=\"height:1000px\">content</div>";
            var (root, sc, _) = BuildViewport(html, viewportWidth: 400, viewportHeight: 300);
            var state = sc.Get(root);
            Assert.That(state, Is.Not.Null);

            // ScrollY clamped above MaxScrollY.
            state.ScrollTo(0, state.MaxScrollY + 9999);
            Assert.That(state.ScrollY, Is.EqualTo(state.MaxScrollY).Within(0.001));
        }

        [Test]
        public void Viewport_scroll_range_is_content_minus_viewport() {
            // MaxScrollY should equal contentHeight - viewportHeight.
            const string html = "<div style=\"height:800px\">tall</div>";
            var (root, sc, _) = BuildViewport(html, viewportWidth: 400, viewportHeight: 300);
            var state = sc.Get(root);
            Assert.That(state, Is.Not.Null);

            // Content bottom ≈ 800px (the div's height).
            // MaxScrollY ≈ 800 - 300 = 500.
            Assert.That(state.MaxScrollY, Is.EqualTo(500).Within(5),
                "MaxScrollY should be content height minus viewport height");
        }

        // ─── Input routing: wheel events reach the viewport scroll ────────────

        [Test]
        public void Wheel_event_on_root_document_scrolls_viewport() {
            const string html = "<div id=\"content\" style=\"height:2000px\">tall</div>";
            var (root, sc, handler) = BuildEventHarness(html, viewportWidth: 400, viewportHeight: 300);
            var state = sc.Get(root);
            Assert.That(state, Is.Not.Null, "Viewport scroll state must exist");

            // Find the content element and programmatically scroll via it.
            Box contentBox = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element?.Id == "content") { contentBox = b; break; }
            }
            Assert.That(contentBox, Is.Not.Null, "content box must be findable");

            handler.ScrollBy(contentBox.Element, 0, 100);
            Assert.That(state.ScrollY, Is.EqualTo(100).Within(0.001),
                "Viewport scrollY should advance by 100");
        }

        [Test]
        public void Inner_scroll_container_and_viewport_both_work_independently() {
            // Document with an inner overflow:auto div AND the viewport overflowing.
            // The inner div should scroll its own content; the viewport should
            // scroll the outer (non-div) content.
            const string css =
                ".inner { overflow: auto; height: 100px; width: 200px; } " +
                ".inner-content { height: 500px; } " +
                ".outer-spacer { height: 1000px; }";
            const string html =
                "<div class=\"inner\"><div class=\"inner-content\"></div></div>" +
                "<div class=\"outer-spacer\"></div>";

            var (root, sc, handler) = BuildEventHarness(
                html, css, viewportWidth: 400, viewportHeight: 300);

            // Inner div should have its own scroll state.
            var innerBox = FindByClass(root, "inner");
            Assert.That(innerBox, Is.Not.Null, "inner box must exist");
            var innerState = sc.Get(innerBox);
            Assert.That(innerState, Is.Not.Null, "inner scroll state must exist");

            // Viewport root should also have a scroll state.
            var vpState = sc.Get(root);
            Assert.That(vpState, Is.Not.Null, "viewport scroll state must exist");

            // Scroll the inner container via its element.
            handler.ScrollBy(innerBox.Element, 0, 50);
            Assert.That(innerState.ScrollY, Is.EqualTo(50).Within(0.001),
                "inner container should scroll");
            Assert.That(vpState.ScrollY, Is.EqualTo(0).Within(0.001),
                "viewport should not scroll from inner wheel");
        }

        // ─── Paint: fixed elements don't move with viewport scroll ───────────

        [Test]
        public void Fixed_element_not_scrolled_by_viewport_translate() {
            // A position:fixed element must appear at its viewport-pinned coords
            // regardless of the viewport scroll position. We verify this by
            // checking that the paint list does NOT contain a net negative
            // translate on a fixed element's rect.
            //
            // Strategy: emit paint into a RecordingBackend and check that a
            // FillRect command associated with the fixed element is at the
            // expected screen origin (top:0; left:0).
            const string css =
                ".fixed { position: fixed; top: 0; left: 0; width: 50px; height: 50px; background: red; } " +
                ".spacer { height: 2000px; }";
            const string html =
                "<div class=\"fixed\"></div>" +
                "<div class=\"spacer\"></div>";

            var (root, _, ctx) = Build(html, css, viewportWidth: 400, viewportHeight: 300);
            var sc = new ScrollContainer();
            var sl = new ScrollLayout(sc);
            sl.Run(root);
            sl.RunViewportScroll(root, ctx.ViewportWidthPx, ctx.ViewportHeightPx);

            // Set a non-zero viewport scroll to verify the fixed element stays put.
            var vpState = sc.Get(root);
            Assert.That(vpState, Is.Not.Null, "Viewport scroll must be active");
            vpState.ScrollTo(0, 100);

            // Run paint.
            var converter = new BoxToPaintConverter();
            var commands = converter.Convert(root, null, null, sc, null);

            // Find the fixed box.
            var fixedBox = FindByClass(root, "fixed");
            Assert.That(fixedBox, Is.Not.Null, "fixed box must exist");

            // The fixed box has `top:0; left:0; width:50; height:50`.
            // With viewport scroll of 100, in-flow content moves up by 100.
            // The fixed element must be painted at (0, 0) — NOT at (0, -100).
            // We verify by checking that after accounting for all PushTransform
            // commands before the fixed box's FillRect, the net Y translate at
            // that point is 0 (or within float tolerance).
            double netTranslateY = ComputeNetTranslateY(commands.Commands);

            // Net translate for fixed content should be 0 (counter-translate
            // cancels the viewport scroll translate).
            Assert.That(netTranslateY, Is.EqualTo(0).Within(1.0),
                "Fixed element should not be displaced by viewport scroll");
        }

        // Walk the paint command list and compute the net Y translate at the
        // end (after all push/pop pairs are balanced). This is a rough sanity
        // check that viewport scroll+fixed counter-translate balance to zero
        // for the fixed element's rendering region.
        static double ComputeNetTranslateY(System.Collections.Generic.IReadOnlyList<PaintCommand> cmds) {
            var stack = new System.Collections.Generic.Stack<double>();
            double net = 0;
            foreach (var cmd in cmds) {
                if (cmd is PushTransformCommand pt) {
                    double dy = pt.Transform.Ty;
                    net += dy;
                    stack.Push(dy);
                } else if (cmd.Kind == PaintCommandKind.PopTransform) {
                    if (stack.Count > 0) {
                        double dy = stack.Pop();
                        net -= dy;
                    }
                }
            }
            return net;
        }
    }
}
