using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Events;
using Weva.Forms;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Scrolling;
using Weva.Layout.Text;
using Weva.Parsing;
using Weva.Reactive;

namespace Weva.Tests.Forms {
    // In-editor report (2026-07, audit-validation page §7-10): "can't write in
    // the input boxes". The engine-side pipeline is: pointer-down → scroll-
    // aware hit test → FocusFromPointer → InputController caret placement →
    // DispatchTextInput("TextInput" key) → Model.Insert. This test drives the
    // WHOLE chain headlessly against the validation page's structure (input
    // inside a scrollable .page, scrolled below the fold, with a live
    // ScrollEventHandler competing for the pointer stream) to prove where the
    // in-editor failure is NOT — pinning the engine half so the Unity bridge
    // half (InputSystemKeyboardSource) can be reasoned about separately.
    public class ScrolledPageInputFocusTests {
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        const string Html =
            "<div class=\"page\">"
            + "<div class=\"tall\">spacer</div>"
            + "<input class=\"in\" type=\"text\" value=\"\">"
            + "<div class=\"tail\">below</div>"
            + "</div>";

        const string Css = @"
            * { box-sizing: border-box; }
            .page { height: 400px; overflow-y: auto; }
            .tall { height: 900px; }
            .in { width: 300px; height: 28px; }
            .tail { height: 500px; }";

        [Test]
        public void Click_focuses_and_typing_edits_an_input_below_the_fold() {
            var doc = HtmlParser.Parse(Html);
            var engine = new CascadeEngine(new List<OriginatedStylesheet> {
                UserAgentStylesheet.Parse(), Author(Css),
            });
            engine.ComputeAll(doc);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ResultMap) styles[kv.Key] = kv.Value;
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 800, ViewportHeightPx = 400,
                RootFontSizePx = 16, DpiPixelsPerInch = 96,
                Snapshot = engine.LastSnapshot, SnapshotStyles = engine.Styles,
            };
            var le = new LayoutEngine(new MonoFontMetrics());
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            System.Func<Element, ComputedStyle> styleOf =
                e => styles.TryGetValue(e, out var cs) ? cs : null;
            var root = le.Layout(doc, styleOf, ctx, tracker);

            var input = doc.GetElementsByTagName("input").First();
            var pageEl = FindByClass(root, "page").Element;
            var pageBox = FindByClass(root, "page");
            var inputBox = FindByClass(root, "in") ?? FindBoxFor(root, input);
            Assert.That(inputBox, Is.Not.Null, "input box not laid out (harness UA gap?)");

            // Scroll the page so the input is inside the 400px viewport.
            var scrolls = le.ScrollContainer;
            var state = scrolls.GetOrCreate(pageBox);
            Assert.That(state.CanScrollY, Is.True, ".page must be a live scroll container");
            double scrollY = 700;
            state.ScrollY = scrollY;
            pageBox.ScrollY = scrollY;

            var hitTester = new BoxTreeHitTester(root, scrolls);
            var dispatcher = new EventDispatcher(doc, hitTester, new FakeUIClock());
            System.Func<Element, Box> elementToBox = e => FindBoxFor(root, e);
            dispatcher.ElementToBox = elementToBox;

            // Live scroll handler exactly like the real lifecycle — it listens
            // in the CAPTURE phase and must not eat the tap or the keystrokes.
            using var scrollHandler = new ScrollEventHandler(
                dispatcher, doc, scrolls, elementToBox, () => 16, () => 0);

            var ctrl = new InputController(input, dispatcher) { ElementToBox = elementToBox };
            ctrl.Wire();

            // The input's on-screen position: absolute layout Y minus scroll.
            double absY = AbsoluteTop(inputBox);
            double screenY = absY - scrollY + inputBox.Height / 2;
            double screenX = AbsoluteLeftOf(inputBox) + 20;
            Assert.That(screenY, Is.InRange(0, 400), "test geometry: input must be inside the viewport");

            Assert.That(hitTester.HitTest(screenX, screenY), Is.SameAs(input),
                "scroll-aware hit test must resolve the click to the input");

            dispatcher.DispatchPointerDown(screenX, screenY, 0, KeyModifiers.None);
            dispatcher.DispatchPointerUp(screenX, screenY, 0, KeyModifiers.None);

            dispatcher.DispatchTextInput("h");
            dispatcher.DispatchTextInput("i");
            Assert.That(input.GetAttribute("value"), Is.EqualTo("hi"),
                "typing after a click on a scrolled-into-view input must edit the value");
        }

        [Test]
        public void Drag_selection_inside_the_input_never_arms_page_pan() {
            var doc = HtmlParser.Parse(Html);
            var engine = new CascadeEngine(new List<OriginatedStylesheet> {
                UserAgentStylesheet.Parse(), Author(Css),
            });
            engine.ComputeAll(doc);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ResultMap) styles[kv.Key] = kv.Value;
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 800, ViewportHeightPx = 400,
                RootFontSizePx = 16, DpiPixelsPerInch = 96,
                Snapshot = engine.LastSnapshot, SnapshotStyles = engine.Styles,
            };
            var le = new LayoutEngine(new MonoFontMetrics());
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            System.Func<Element, ComputedStyle> styleOf =
                e => styles.TryGetValue(e, out var cs) ? cs : null;
            var root = le.Layout(doc, styleOf, ctx, tracker);

            var input = doc.GetElementsByTagName("input").First();
            input.SetAttribute("value", "select me with a long drag sweep");
            var pageBox = FindByClass(root, "page");
            var inputBox = FindByClass(root, "in") ?? FindBoxFor(root, input);

            var scrolls = le.ScrollContainer;
            var state = scrolls.GetOrCreate(pageBox);
            double scrollY = 700;
            state.ScrollY = scrollY;
            pageBox.ScrollY = scrollY;

            var hitTester = new BoxTreeHitTester(root, scrolls);
            var dispatcher = new EventDispatcher(doc, hitTester, new FakeUIClock());
            System.Func<Element, Box> elementToBox = e => FindBoxFor(root, e);
            dispatcher.ElementToBox = elementToBox;

            using var scrollHandler = new ScrollEventHandler(
                dispatcher, doc, scrolls, elementToBox, () => 16, () => 0);
            // Production wiring (UIDocumentBuilder): the momentum animator is
            // what arms touch drag-pan — without it the old bug can't repro.
            scrollHandler.MomentumAnimator =
                new Weva.Layout.Scrolling.Smooth.ScrollMomentum(scrolls);

            var ctrl = new InputController(input, dispatcher) { ElementToBox = elementToBox };
            ctrl.Wire();
            // Production wires the measurer via FormControlsRegistry; the drag
            // mapping needs it (8px per char, monospace-style).
            ctrl.Model.SetMeasureSubstring((t, s, n) => 8.0 * n);

            double screenY = AbsoluteTop(inputBox) - scrollY + inputBox.Height / 2;
            double startX = AbsoluteLeftOf(inputBox) + 10;

            dispatcher.DispatchPointerDown(startX, screenY, 0, KeyModifiers.None);
            // Sweep well past TouchDragSlopPx (8) — Chrome keeps extending the
            // selection; the page must not pan and the moves must keep
            // reaching the input.
            for (double dx = 10; dx <= 60; dx += 10) {
                dispatcher.DispatchPointerMove(startX + dx, screenY, KeyModifiers.None);
            }
            dispatcher.DispatchPointerUp(startX + 60, screenY, 0, KeyModifiers.None);

            Assert.That(state.ScrollY, Is.EqualTo(scrollY).Within(0.01),
                "a drag that starts inside a text input must never pan the page");
            var sel = ctrl.Model.Selection;
            Assert.That(sel.End - sel.Start, Is.GreaterThanOrEqualTo(5),
                "the full 60px sweep must keep extending the selection (pre-fix it froze " +
                "once the drag-pan armed at the 8px slop and consumed every later move)");
        }

        [Test]
        public void Caret_keys_consumed_by_the_input_do_not_scroll_the_page() {
            var doc = HtmlParser.Parse(Html);
            var engine = new CascadeEngine(new List<OriginatedStylesheet> {
                UserAgentStylesheet.Parse(), Author(Css),
            });
            engine.ComputeAll(doc);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ResultMap) styles[kv.Key] = kv.Value;
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 800, ViewportHeightPx = 400,
                RootFontSizePx = 16, DpiPixelsPerInch = 96,
                Snapshot = engine.LastSnapshot, SnapshotStyles = engine.Styles,
            };
            var le = new LayoutEngine(new MonoFontMetrics());
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            System.Func<Element, ComputedStyle> styleOf =
                e => styles.TryGetValue(e, out var cs) ? cs : null;
            var root = le.Layout(doc, styleOf, ctx, tracker);

            var input = doc.GetElementsByTagName("input").First();
            input.SetAttribute("value", "some value");
            var pageBox = FindByClass(root, "page");

            var scrolls = le.ScrollContainer;
            var state = scrolls.GetOrCreate(pageBox);
            double scrollY = 700;
            state.ScrollY = scrollY;
            pageBox.ScrollY = scrollY;

            var hitTester = new BoxTreeHitTester(root, scrolls);
            var dispatcher = new EventDispatcher(doc, hitTester, new FakeUIClock());
            System.Func<Element, Box> elementToBox = e => FindBoxFor(root, e);
            dispatcher.ElementToBox = elementToBox;
            using var scrollHandler = new ScrollEventHandler(
                dispatcher, doc, scrolls, elementToBox, () => 16, () => 0);
            var ctrl = new InputController(input, dispatcher) { ElementToBox = elementToBox };
            ctrl.Wire();
            dispatcher.Focus(input);

            // Chrome: keys consumed by the focused editable never scroll.
            dispatcher.DispatchKeyDown("End", "End", KeyModifiers.None, false);
            Assert.That(state.ScrollY, Is.EqualTo(scrollY).Within(0.01),
                "End moved the caret — it must not ALSO jump the page to the bottom");
            dispatcher.DispatchKeyDown("Home", "Home", KeyModifiers.None, false);
            Assert.That(state.ScrollY, Is.EqualTo(scrollY).Within(0.01),
                "Home must not jump the page to the top");
            dispatcher.DispatchKeyDown("ArrowLeft", "ArrowLeft", KeyModifiers.None, false);
            dispatcher.DispatchKeyDown("ArrowRight", "ArrowRight", KeyModifiers.None, false);
            Assert.That(state.ScrollY, Is.EqualTo(scrollY).Within(0.01),
                "arrow keys owned by the caret must not scroll the page");

            // The page must still scroll for keys the input does NOT consume
            // when a scroll container itself is the key target: blur the
            // input and route a PageDown from the page.
            dispatcher.Focus(null);
            dispatcher.DispatchKeyDown("PageDown", "PageDown", KeyModifiers.None, false);
            // (target falls back to the root; the handler walks up from there)
        }

        [Test]
        public void Typing_does_not_reset_the_page_scroll() {
            // In-editor report: every keystroke scrolled the page back to the
            // top. A keystroke does three things: writes the value attribute,
            // sets the UserInteracted pseudo-class flag (:user-invalid gating)
            // and moves the caret — the first two invalidate style/layout, and
            // the following relayout must PRESERVE the scroll position of the
            // ancestor scroll container.
            var doc = HtmlParser.Parse(Html);
            var engine = new CascadeEngine(new List<OriginatedStylesheet> {
                UserAgentStylesheet.Parse(), Author(Css),
            });
            engine.ComputeAll(doc);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ResultMap) styles[kv.Key] = kv.Value;
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 800, ViewportHeightPx = 400,
                RootFontSizePx = 16, DpiPixelsPerInch = 96,
                Snapshot = engine.LastSnapshot, SnapshotStyles = engine.Styles,
            };
            var le = new LayoutEngine(new MonoFontMetrics());
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            System.Func<Element, ComputedStyle> styleOf =
                e => styles.TryGetValue(e, out var cs) ? cs : null;
            var root = le.Layout(doc, styleOf, ctx, tracker);
            tracker.Clear();

            var input = doc.GetElementsByTagName("input").First();
            var pageEl = FindByClass(root, "page").Element;
            var pageBox = FindByClass(root, "page");
            var scrolls = le.ScrollContainer;
            var state = scrolls.GetOrCreate(pageBox);
            double scrollY = 700;
            state.ScrollY = scrollY;
            pageBox.ScrollY = scrollY;

            var hitTester = new BoxTreeHitTester(root, scrolls);
            var dispatcher = new EventDispatcher(doc, hitTester, new FakeUIClock());
            System.Func<Element, Box> elementToBox = e => FindBoxFor(root, e);
            dispatcher.ElementToBox = elementToBox;
            var ctrl = new InputController(input, dispatcher, null, tracker) { ElementToBox = elementToBox };
            ctrl.Wire();
            dispatcher.Focus(input);

            // Type one character (the real chain: edits the value attribute,
            // marks UserInteracted, moves the caret), then run the next
            // frame's layout like the lifecycle would. The audit-validation
            // page has :has() rules, which force a FULL cascade recompute on
            // any pseudo-class/attribute change — fresh ComputedStyle objects
            // for every element, the deepest rebuild the live page can take.
            dispatcher.DispatchTextInput("h");
            tracker.MarkDirty(input, InvalidationKind.PseudoClassState);
            // STRUCTURE-level dirt forces the deepest rebuild path (layout
            // cache bypassed, every box remade fresh) — the shape a :has()/
            // attribute-selector sheet can push an attribute write into.
            tracker.MarkDirty(doc, InvalidationKind.Structure);
            engine.ComputeAll(doc);
            styles.Clear();
            foreach (var kv in engine.ResultMap) styles[kv.Key] = kv.Value;
            ctx.Snapshot = engine.LastSnapshot;
            ctx.SnapshotStyles = null;
            root = le.Layout(doc, styleOf, ctx, tracker);
            tracker.Clear();

            var pageAfter = FindBoxFor(root, pageEl);
            var stateAfter = scrolls.GetOrCreate(pageAfter);
            Assert.That(stateAfter.ScrollY, Is.EqualTo(scrollY).Within(0.01),
                "a keystroke's relayout must preserve the ancestor scroll position");
            Assert.That(pageAfter.ScrollY, Is.EqualTo(scrollY).Within(0.01),
                "the box mirror of the scroll offset must survive too");

            // In-editor find (round 2): the STATE survived but the PAINT
            // stopped applying it — the page RENDERED at the top with zero
            // scroll-state mutation (no [Weva.Scroll] breadcrumb fired). The
            // painted output is the ground truth: the frame after a keystroke
            // must still contain the scroll translate.
            var conv = new Weva.Paint.Conversion.BoxToPaintConverter();
            var paint = conv.Convert(root, tracker, elementToBox, scrolls, null);
            bool foundScrollTranslate = false;
            foreach (var cmd in paint.Commands) {
                if (cmd is Weva.Paint.PushTransformCommand pt
                    && System.Math.Abs(pt.Transform.Ty + scrollY) < 0.01) {
                    foundScrollTranslate = true;
                    break;
                }
            }
            Assert.That(foundScrollTranslate, Is.True,
                "the painted frame after a keystroke must translate the scroll " +
                "container content by -scrollY (the visual scroll position)");
        }

        [Test]
        public void Scroll_container_element_maps_to_exactly_one_box() {
            // Two live boxes sharing the scroll container's Element would
            // split the scroll pipeline: the wheel writes one box's state,
            // some paints read the other's (born at 0) — the exact shape of
            // the in-editor typing-scrolls-to-top mystery.
            var doc = HtmlParser.Parse(Html);
            var engine = new CascadeEngine(new List<OriginatedStylesheet> {
                UserAgentStylesheet.Parse(), Author(Css),
            });
            engine.ComputeAll(doc);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ResultMap) styles[kv.Key] = kv.Value;
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 800, ViewportHeightPx = 400,
                RootFontSizePx = 16, DpiPixelsPerInch = 96,
                Snapshot = engine.LastSnapshot, SnapshotStyles = engine.Styles,
            };
            var le = new LayoutEngine(new MonoFontMetrics());
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            var root = le.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx, tracker);
            var pageEl = FindByClass(root, "page").Element;
            int count = 0;
            void Count(Box b) {
                if (ReferenceEquals(b.Element, pageEl)) count++;
                foreach (var c in b.ChildList) Count(c);
            }
            Count(root);
            Assert.That(count, Is.EqualTo(1),
                "a scroll container element must map to exactly one live box");
        }

        static double AbsoluteTop(Box box) {
            double y = 0;
            for (var b = box; b != null; b = b.Parent) y += b.Y;
            return y;
        }

        static double AbsoluteLeftOf(Box box) {
            double x = 0;
            for (var b = box; b != null; b = b.Parent) x += b.X;
            return x;
        }

        static Box FindByClass(Box root, string className) {
            if (root.Element != null && (root.Element.ClassName ?? "") == className) return root;
            foreach (var c in root.ChildList) {
                var hit = FindByClass(c, className);
                if (hit != null) return hit;
            }
            return null;
        }

        static Box FindBoxFor(Box root, Element el) {
            if (ReferenceEquals(root.Element, el)) return root;
            foreach (var c in root.ChildList) {
                var hit = FindBoxFor(c, el);
                if (hit != null) return hit;
            }
            return null;
        }
    }
}
