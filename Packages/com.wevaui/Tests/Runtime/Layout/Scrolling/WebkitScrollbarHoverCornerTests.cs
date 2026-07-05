using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Layout.Scrolling;
using Weva.Paint;
using Weva.Parsing;

namespace Weva.Tests.Layout.Scrolling {
    // ::-webkit-scrollbar-thumb:hover, ::-webkit-scrollbar-thumb:active, and
    // ::-webkit-scrollbar-corner paint support (I14d follow-up).
    //
    // Chrome semantics implemented:
    //   - ::-webkit-scrollbar-thumb:hover — applied when pointer is over the thumb
    //     rect on that axis (tracked via ScrollState.ThumbHoveredX/Y).
    //   - ::-webkit-scrollbar-thumb:active — applied during an active drag on that
    //     axis (tracked via ScrollState.ThumbActiveX/Y).
    //   - Chrome keeps hover + active both during drag; so ThumbActiveY=true also
    //     implies ThumbHoveredY=true in the drag handler.
    //   - Per-axis independence: vertical hover does NOT affect horizontal thumb.
    //   - ::-webkit-scrollbar-corner — filled only when both axes show scrollbars
    //     AND an authored corner background-color exists.
    //   - Corner color resolution supports named, function, and currentcolor.
    //   - Corner thickness matches the ::-webkit-scrollbar width (per-element).
    //   - Regressions: existing webkit/L1 precedence unaffected by new plumbing.
    public class WebkitScrollbarHoverCornerTests {
        // ── Helpers ───────────────────────────────────────────────────────────

        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));
        static Document Html(string html) => HtmlParser.Parse(html);

        static CascadeEngine Engine(string css) =>
            new CascadeEngine(new[] { Author(css) });

        static BlockBox MakeBox(double w, double h, Element element = null) {
            var b = new BlockBox();
            b.X = 0; b.Y = 0; b.Width = w; b.Height = h;
            b.Element = element ?? new Element("div");
            b.Style = new ComputedStyle(b.Element);
            return b;
        }

        // Vertical scroll state (content overflows vertically only).
        static ScrollState VerticalScrollState(double viewportH, double contentH, double boxWidth) =>
            new ScrollState {
                ViewportHeight = viewportH,
                ViewportWidth = boxWidth - ScrollMath.ScrollbarTrackThicknessPx,
                ScrollHeight = contentH,
                ScrollWidth = boxWidth - ScrollMath.ScrollbarTrackThicknessPx,
                OverflowX = ScrollOverflow.Hidden,
                OverflowY = ScrollOverflow.Scroll,
            };

        // Horizontal scroll state (content overflows horizontally only).
        static ScrollState HorizontalScrollState(double viewportW, double contentW, double boxHeight) =>
            new ScrollState {
                ViewportWidth = viewportW,
                ViewportHeight = boxHeight - ScrollMath.ScrollbarTrackThicknessPx,
                ScrollWidth = contentW,
                ScrollHeight = boxHeight - ScrollMath.ScrollbarTrackThicknessPx,
                OverflowX = ScrollOverflow.Scroll,
                OverflowY = ScrollOverflow.Hidden,
            };

        // Dual-axis scroll state (overflows both axes).
        static ScrollState DualScrollState(double vpW, double vpH, double contentW, double contentH) {
            double thickness = ScrollMath.ScrollbarTrackThicknessPx;
            return new ScrollState {
                ViewportWidth = vpW - thickness,
                ViewportHeight = vpH - thickness,
                ScrollWidth = contentW,
                ScrollHeight = contentH,
                OverflowX = ScrollOverflow.Scroll,
                OverflowY = ScrollOverflow.Scroll,
            };
        }

        static List<FillRectCommand> CollectFills(PaintList list) {
            var fills = new List<FillRectCommand>();
            foreach (var c in list.Commands)
                if (c is FillRectCommand f) fills.Add(f);
            return fills;
        }

        // Simple state provider that reports a fixed state for one element.
        sealed class FixedStateProvider : IElementStateProvider {
            readonly Element target;
            readonly ElementState state;
            public FixedStateProvider(Element target, ElementState state) {
                this.target = target;
                this.state = state;
            }
            public ElementState GetState(Element e) =>
                ReferenceEquals(e, target) ? state : ElementState.None;
        }

        // ── Parser: ::-webkit-scrollbar-thumb:hover must parse ────────────────

        [Test]
        public void Parser_accepts_webkit_scrollbar_thumb_hover_selector() {
            Assert.DoesNotThrow(
                () => SelectorParser.Parse("::-webkit-scrollbar-thumb:hover"),
                "'::-webkit-scrollbar-thumb:hover' must parse without error");
        }

        [Test]
        public void Parser_accepts_webkit_scrollbar_thumb_active_selector() {
            Assert.DoesNotThrow(
                () => SelectorParser.Parse("::-webkit-scrollbar-thumb:active"),
                "'::-webkit-scrollbar-thumb:active' must parse without error");
        }

        [Test]
        public void Parser_accepts_class_qualified_webkit_thumb_hover() {
            Assert.DoesNotThrow(
                () => SelectorParser.Parse(".scroller::-webkit-scrollbar-thumb:hover"),
                "class-qualified '::-webkit-scrollbar-thumb:hover' must parse without error");
        }

        [Test]
        public void Parser_accepts_webkit_scrollbar_track_hover() {
            // Track hover is valid CSS even if we don't implement it in the painter;
            // the parser must accept it without throwing.
            Assert.DoesNotThrow(
                () => SelectorParser.Parse("::-webkit-scrollbar-track:hover"),
                "'::-webkit-scrollbar-track:hover' must parse without error");
        }

        [Test]
        public void Parser_accepts_webkit_scrollbar_corner_hover() {
            Assert.DoesNotThrow(
                () => SelectorParser.Parse("::-webkit-scrollbar-corner:hover"),
                "'::-webkit-scrollbar-corner:hover' must parse without error");
        }

        // ── Hover style applied when thumb is hovered ─────────────────────────

        // When ThumbHoveredY is true and a :hover rule exists, the hover color
        // overrides the base thumb color for the vertical thumb.
        [Test]
        public void Webkit_thumb_hover_color_applies_when_thumb_hovered_vertical() {
            var doc = Html("<div id=\"s\"></div>");
            var host = doc.GetElementById("s");
            var engine = Engine(
                "#s::-webkit-scrollbar-thumb { background-color: blue; }" +
                "#s::-webkit-scrollbar-thumb:hover { background-color: red; }");
            var box = MakeBox(200, 100, host);
            var state = VerticalScrollState(100, 500, 200);
            state.ThumbHoveredY = true; // pointer is over the vertical thumb

            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null, engine);
            var fills = CollectFills(list);
            Assert.That(fills.Count, Is.EqualTo(2),
                "webkit scrollbar must emit track + thumb");
            // Thumb (fills[1]) must be red from :hover rule, not blue from base.
            Assert.That(fills[1].Brush.Color.R, Is.GreaterThan(0.5f),
                "thumb with ThumbHoveredY=true must use :hover color (red)");
            Assert.That(fills[1].Brush.Color.B, Is.LessThan(0.1f),
                "thumb with ThumbHoveredY=true must NOT use base color (blue)");
        }

        // When ThumbHoveredY is false, the base thumb color applies.
        [Test]
        public void Webkit_thumb_base_color_applies_when_thumb_not_hovered() {
            var doc = Html("<div id=\"s\"></div>");
            var host = doc.GetElementById("s");
            var engine = Engine(
                "#s::-webkit-scrollbar-thumb { background-color: blue; }" +
                "#s::-webkit-scrollbar-thumb:hover { background-color: red; }");
            var box = MakeBox(200, 100, host);
            var state = VerticalScrollState(100, 500, 200);
            // ThumbHoveredY defaults to false — base style must apply.

            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null, engine);
            var fills = CollectFills(list);
            Assert.That(fills.Count, Is.EqualTo(2));
            // Thumb must be blue (base), not red (:hover).
            Assert.That(fills[1].Brush.Color.B, Is.GreaterThan(0.5f),
                "thumb with ThumbHoveredY=false must use base color (blue)");
            Assert.That(fills[1].Brush.Color.R, Is.LessThan(0.1f),
                "thumb with ThumbHoveredY=false must NOT use :hover color (red)");
        }

        // ── Hover persists during drag (:active) ──────────────────────────────

        // When ThumbActiveY is true (drag in progress), :active color applies.
        [Test]
        public void Webkit_thumb_active_color_applies_during_drag() {
            var doc = Html("<div id=\"s\"></div>");
            var host = doc.GetElementById("s");
            // Use lime (#00ff00) for unambiguous G=1.0 in linear light.
            var engine = Engine(
                "#s::-webkit-scrollbar-thumb { background-color: gray; }" +
                "#s::-webkit-scrollbar-thumb:active { background-color: lime; }");
            var box = MakeBox(200, 100, host);
            var state = VerticalScrollState(100, 500, 200);
            state.ThumbActiveY = true; // drag in progress
            state.ThumbHoveredY = true; // Chrome keeps hover during drag

            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null, engine);
            var fills = CollectFills(list);
            Assert.That(fills.Count, Is.EqualTo(2));
            // Thumb must be lime (:active), not gray (base).
            // Gray has R≈G≈B in linear; lime has R=0,G=1,B=0 in linear.
            Assert.That(fills[1].Brush.Color.G, Is.GreaterThan(0.9f),
                "thumb with ThumbActiveY=true must use :active color (lime, G≈1)");
            Assert.That(fills[1].Brush.Color.R, Is.LessThan(0.1f),
                "thumb with ThumbActiveY=true must NOT use base color (gray has R>0)");
            Assert.That(fills[1].Brush.Color.B, Is.LessThan(0.1f),
                "thumb with ThumbActiveY=true must NOT use base color (gray has B>0)");
        }

        // When both :hover and :active exist, :active (higher specificity as it
        // appears later in source) wins when ThumbActiveY is true.
        [Test]
        public void Webkit_thumb_active_beats_hover_when_both_authored() {
            var doc = Html("<div id=\"s\"></div>");
            var host = doc.GetElementById("s");
            var engine = Engine(
                "#s::-webkit-scrollbar-thumb { background-color: gray; }" +
                "#s::-webkit-scrollbar-thumb:hover { background-color: yellow; }" +
                "#s::-webkit-scrollbar-thumb:active { background-color: orange; }");
            var box = MakeBox(200, 100, host);
            var state = VerticalScrollState(100, 500, 200);
            state.ThumbActiveY = true;
            state.ThumbHoveredY = true;

            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null, engine);
            var fills = CollectFills(list);
            Assert.That(fills.Count, Is.EqualTo(2));
            // orange: R≈1 G≈0.65 B=0. Both :hover and :active are injected;
            // cascade picks the last authored (source-order wins among equal specificity).
            // Orange has high R and moderate G, no B.
            Assert.That(fills[1].Brush.Color.B, Is.LessThan(0.05f),
                "thumb must be orange (:active), not yellow (:hover)");
            Assert.That(fills[1].Brush.Color.R, Is.GreaterThan(0.5f),
                "thumb must be orange (:active) — high R");
        }

        // ── Per-axis independence ─────────────────────────────────────────────

        // Vertical thumb hovered, horizontal not — vertical uses :hover, horizontal uses base.
        [Test]
        public void Webkit_thumb_hover_is_per_axis_independent() {
            var doc = Html("<div id=\"s\"></div>");
            var host = doc.GetElementById("s");
            var engine = Engine(
                "#s::-webkit-scrollbar { width: 12px; height: 12px; }" +
                "#s::-webkit-scrollbar-thumb { background-color: blue; }" +
                "#s::-webkit-scrollbar-thumb:hover { background-color: red; }");
            var box = MakeBox(200, 200, host);
            // Dual-axis: both scrollbars visible.
            var state = DualScrollState(200, 200, 800, 800);
            state.ThumbHoveredY = true;  // vertical hovered
            // ThumbHoveredX = false (not hovered)

            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null, engine);
            var fills = CollectFills(list);
            // Should have: V-track, V-thumb, H-track, H-thumb (4 fills, possibly 5 with corner).
            // We check at minimum 4 fills.
            Assert.That(fills.Count, Is.GreaterThanOrEqualTo(4),
                "dual-axis scrollbar must emit at least 4 fills (2 tracks + 2 thumbs)");
            // fills[0]=V-track, fills[1]=V-thumb (red), fills[2]=H-track, fills[3]=H-thumb (blue).
            Assert.That(fills[1].Brush.Color.R, Is.GreaterThan(0.5f),
                "vertical thumb (hovered) must be red");
            Assert.That(fills[1].Brush.Color.B, Is.LessThan(0.1f),
                "vertical thumb (hovered) must NOT be blue");
            Assert.That(fills[3].Brush.Color.B, Is.GreaterThan(0.5f),
                "horizontal thumb (not hovered) must be blue (base)");
            Assert.That(fills[3].Brush.Color.R, Is.LessThan(0.1f),
                "horizontal thumb (not hovered) must NOT be red");
        }

        // Horizontal thumb hovered, vertical not.
        [Test]
        public void Webkit_thumb_hover_horizontal_axis_independent() {
            var doc = Html("<div id=\"s\"></div>");
            var host = doc.GetElementById("s");
            var engine = Engine(
                "#s::-webkit-scrollbar { width: 12px; height: 12px; }" +
                "#s::-webkit-scrollbar-thumb { background-color: blue; }" +
                "#s::-webkit-scrollbar-thumb:hover { background-color: red; }");
            var box = MakeBox(200, 200, host);
            var state = DualScrollState(200, 200, 800, 800);
            // ThumbHoveredY = false (vertical not hovered)
            state.ThumbHoveredX = true; // horizontal hovered

            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null, engine);
            var fills = CollectFills(list);
            Assert.That(fills.Count, Is.GreaterThanOrEqualTo(4));
            // fills[1] = V-thumb = blue (not hovered)
            Assert.That(fills[1].Brush.Color.B, Is.GreaterThan(0.5f),
                "vertical thumb (not hovered) must be blue (base)");
            Assert.That(fills[1].Brush.Color.R, Is.LessThan(0.1f),
                "vertical thumb (not hovered) must NOT be red");
            // fills[3] = H-thumb = red (hovered)
            Assert.That(fills[3].Brush.Color.R, Is.GreaterThan(0.5f),
                "horizontal thumb (hovered) must be red");
            Assert.That(fills[3].Brush.Color.B, Is.LessThan(0.1f),
                "horizontal thumb (hovered) must NOT be blue");
        }

        // ── Corner: painted only when both axes scroll + corner rule authored ──

        [Test]
        public void Corner_painted_when_both_axes_scroll_and_corner_rule_authored() {
            var doc = Html("<div id=\"s\"></div>");
            var host = doc.GetElementById("s");
            // Use lime (#00ff00) for unambiguous G=1.0 in linear light.
            var engine = Engine(
                "#s::-webkit-scrollbar { width: 12px; height: 12px; }" +
                "#s::-webkit-scrollbar-corner { background-color: lime; }");
            var box = MakeBox(200, 200, host);
            var state = DualScrollState(200, 200, 800, 800);

            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null, engine);
            var fills = CollectFills(list);
            // Should have: V-track, V-thumb, H-track, H-thumb, corner = 5 fills.
            Assert.That(fills.Count, Is.EqualTo(5),
                "dual-axis + corner rule must emit 5 fills (track+thumb × 2 + corner)");
            // Last fill is the corner (lime: R=0, G=1, B=0 in linear).
            Assert.That(fills[4].Brush.Color.G, Is.GreaterThan(0.9f),
                "corner fill must be lime (G≈1.0 in linear)");
            Assert.That(fills[4].Brush.Color.R, Is.LessThan(0.1f),
                "corner fill must NOT be red");
        }

        [Test]
        public void Corner_not_painted_when_corner_rule_is_absent() {
            var doc = Html("<div id=\"s\"></div>");
            var host = doc.GetElementById("s");
            // No corner rule authored — corner must NOT be painted.
            var engine = Engine(
                "#s::-webkit-scrollbar { width: 12px; height: 12px; }" +
                "#s::-webkit-scrollbar-thumb { background-color: gray; }");
            var box = MakeBox(200, 200, host);
            var state = DualScrollState(200, 200, 800, 800);

            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null, engine);
            var fills = CollectFills(list);
            // Should have: 4 fills (no corner).
            Assert.That(fills.Count, Is.EqualTo(4),
                "dual-axis without corner rule must emit exactly 4 fills (no corner)");
        }

        [Test]
        public void Corner_not_painted_when_only_one_axis_scrolls() {
            var doc = Html("<div id=\"s\"></div>");
            var host = doc.GetElementById("s");
            var engine = Engine(
                "#s::-webkit-scrollbar { width: 12px; height: 12px; }" +
                "#s::-webkit-scrollbar-corner { background-color: green; }");
            var box = MakeBox(200, 100, host);
            // Only vertical overflow — no corner.
            var state = VerticalScrollState(100, 500, 200);

            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null, engine);
            var fills = CollectFills(list);
            // Should have: V-track + V-thumb = 2 fills. No corner without both axes.
            Assert.That(fills.Count, Is.EqualTo(2),
                "single-axis scrollbar must not paint the corner even when a corner rule exists");
        }

        // ── Corner color resolution ───────────────────────────────────────────

        [Test]
        public void Corner_color_resolved_from_named_color() {
            var doc = Html("<div id=\"s\"></div>");
            var host = doc.GetElementById("s");
            var engine = Engine(
                "#s::-webkit-scrollbar { width: 12px; height: 12px; }" +
                "#s::-webkit-scrollbar-corner { background-color: navy; }");
            var box = MakeBox(200, 200, host);
            var state = DualScrollState(200, 200, 800, 800);

            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null, engine);
            var fills = CollectFills(list);
            Assert.That(fills.Count, Is.EqualTo(5),
                "corner fill expected");
            // Navy: R=0, G=0, B non-trivial (linear ~0.216).
            Assert.That(fills[4].Brush.Color.B, Is.GreaterThan(0.1f),
                "corner (navy) must have non-trivial B channel");
            Assert.That(fills[4].Brush.Color.R, Is.LessThan(0.01f),
                "corner (navy) must have zero R");
        }

        [Test]
        public void Corner_color_resolved_from_rgb_function() {
            var doc = Html("<div id=\"s\"></div>");
            var host = doc.GetElementById("s");
            var engine = Engine(
                "#s::-webkit-scrollbar { width: 12px; height: 12px; }" +
                "#s::-webkit-scrollbar-corner { background-color: rgb(255,0,0); }");
            var box = MakeBox(200, 200, host);
            var state = DualScrollState(200, 200, 800, 800);

            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null, engine);
            var fills = CollectFills(list);
            Assert.That(fills.Count, Is.EqualTo(5));
            Assert.That(fills[4].Brush.Color.R, Is.GreaterThan(0.5f),
                "corner rgb(255,0,0) must have high R");
            Assert.That(fills[4].Brush.Color.B, Is.LessThan(0.1f),
                "corner rgb(255,0,0) must have low B");
        }

        // ── Corner respects per-element webkit scrollbar thickness ────────────

        [Test]
        public void Corner_size_matches_webkit_scrollbar_width() {
            var doc = Html("<div id=\"s\"></div>");
            var host = doc.GetElementById("s");
            // Custom 8px scrollbar — corner should be 8×8.
            var engine = Engine(
                "#s::-webkit-scrollbar { width: 8px; height: 8px; }" +
                "#s::-webkit-scrollbar-corner { background-color: red; }");
            var box = MakeBox(200, 200, host);
            var state = new ScrollState {
                ViewportWidth  = 200 - 8,
                ViewportHeight = 200 - 8,
                ScrollWidth    = 800,
                ScrollHeight   = 800,
                OverflowX      = ScrollOverflow.Scroll,
                OverflowY      = ScrollOverflow.Scroll,
            };

            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null, engine);
            var fills = CollectFills(list);
            Assert.That(fills.Count, Is.EqualTo(5));
            // Corner must be 8×8 (matching the webkit scrollbar width/height).
            Assert.That(fills[4].Bounds.Width, Is.EqualTo(8.0).Within(0.01),
                "corner width must equal ::-webkit-scrollbar width (8px)");
            Assert.That(fills[4].Bounds.Height, Is.EqualTo(8.0).Within(0.01),
                "corner height must equal ::-webkit-scrollbar height (8px)");
        }

        // ── Regression: existing webkit/L1 precedence unaffected ─────────────

        [Test]
        public void Hover_plumbing_does_not_break_webkit_L1_precedence() {
            // Regression: when :hover plumbing is present, webkit must still
            // override L1 scrollbar-color when webkit rules match the element.
            var doc = Html("<div id=\"s\"></div>");
            var host = doc.GetElementById("s");
            var engine = Engine(
                "#s::-webkit-scrollbar-thumb { background-color: red; }" +
                "#s::-webkit-scrollbar-thumb:hover { background-color: darkred; }");
            var box = MakeBox(200, 100, host);
            box.Style.Set("scrollbar-color", "green purple"); // L1 — must be ignored
            var state = VerticalScrollState(100, 500, 200);
            // ThumbHoveredY is false: base (red) must apply, NOT L1 green.

            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null, engine);
            var fills = CollectFills(list);
            Assert.That(fills.Count, Is.EqualTo(2));
            Assert.That(fills[1].Brush.Color.R, Is.GreaterThan(0.5f),
                "webkit thumb must win over L1 scrollbar-color");
            Assert.That(fills[1].Brush.Color.G, Is.LessThan(0.3f),
                "L1 green must be suppressed by webkit thumb rule");
        }

        [Test]
        public void Ignored_webkit_pseudo_elements_parse_without_error_regression() {
            // Regression: adding hover bucket must not break corner/button/resizer parse.
            Assert.DoesNotThrow(
                () => SelectorParser.Parse("::-webkit-scrollbar-corner"),
                "'::-webkit-scrollbar-corner' must still parse without error");
            Assert.DoesNotThrow(
                () => SelectorParser.Parse("::-webkit-scrollbar-button"),
                "'::-webkit-scrollbar-button' must still parse without error");
            Assert.DoesNotThrow(
                () => SelectorParser.Parse("::-webkit-scrollbar-resizer"),
                "'::-webkit-scrollbar-resizer' must still parse without error");
        }

        // ── CascadeEngine routes :hover rules to thumb bucket ─────────────────

        [Test]
        public void CascadeEngine_routes_thumb_hover_rule_to_thumb_bucket() {
            var doc = Html("<div id=\"s\"></div>");
            var host = doc.GetElementById("s");
            var engine = Engine(
                "#s::-webkit-scrollbar-thumb:hover { background-color: red; }");
            // When state provider reports hover=true, ComputeWebkitScrollbarThumb
            // must return a non-null style (the :hover rule matches).
            var hoverState = new FixedStateProvider(host, ElementState.Hover);
            var style = engine.ComputeWebkitScrollbarThumb(host, hoverState);
            Assert.That(style, Is.Not.Null,
                "ComputeWebkitScrollbarThumb must return non-null when :hover rule matches hovered element");
        }

        [Test]
        public void CascadeEngine_thumb_hover_rule_does_not_match_non_hovered() {
            var doc = Html("<div id=\"s\"></div>");
            var host = doc.GetElementById("s");
            var engine = Engine(
                "#s::-webkit-scrollbar-thumb:hover { background-color: red; }");
            // Only a :hover rule exists; non-hovered element must return null
            // (no base thumb rule).
            var style = engine.ComputeWebkitScrollbarThumb(host, null);
            Assert.That(style, Is.Null,
                "ComputeWebkitScrollbarThumb must return null for non-hovered element with only a :hover rule");
        }

        // ── Corner: CascadeEngine routes corner rules correctly ───────────────

        [Test]
        public void CascadeEngine_routes_corner_rule_to_corner_bucket() {
            var doc = Html("<div id=\"s\"></div>");
            var host = doc.GetElementById("s");
            var engine = Engine(
                "#s::-webkit-scrollbar-corner { background-color: green; }");
            var style = engine.ComputeWebkitScrollbarCorner(host);
            Assert.That(style, Is.Not.Null,
                "ComputeWebkitScrollbarCorner must return non-null when a matching corner rule exists");
        }

        [Test]
        public void CascadeEngine_corner_returns_null_when_no_rule() {
            var doc = Html("<div id=\"s\"></div>");
            var host = doc.GetElementById("s");
            var engine = Engine(
                "#s::-webkit-scrollbar-thumb { background-color: red; }");
            var style = engine.ComputeWebkitScrollbarCorner(host);
            Assert.That(style, Is.Null,
                "ComputeWebkitScrollbarCorner must return null when no corner rule matches");
        }
    }
}
