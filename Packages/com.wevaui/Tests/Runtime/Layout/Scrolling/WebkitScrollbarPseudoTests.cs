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
    // WebKit scrollbar pseudo-element support.
    //
    // Chrome ships ::-webkit-scrollbar, ::-webkit-scrollbar-thumb, and
    // ::-webkit-scrollbar-track as non-standard but widely-adopted hooks for
    // styling native scrollbars. Many bundled game samples author these rules;
    // previously they were dropped by the selector parser ("Unknown pseudo-element").
    //
    // This suite pins the full contract:
    //   1. Parser keeps webkit-scrollbar selectors (no drop, no error).
    //   2. ::-webkit-scrollbar { width: Npx } → per-element thickness override
    //      (vertical and horizontal).
    //   3. ::-webkit-scrollbar-thumb { background-color } → thumb color.
    //   4. ::-webkit-scrollbar-track { background-color } → track color.
    //   5. Webkit presence disables CSS Scrollbars L1 (scrollbar-color /
    //      scrollbar-width) for that element — Chrome precedence.
    //   6. Elements without webkit rules keep L1 behavior (control group).
    //   7. Invalid / unresolvable values fall back gracefully (UA defaults).
    //   8. Thumb border-radius from ::-webkit-scrollbar-thumb { border-radius }.
    //   9. ::-webkit-scrollbar-corner / -button / -resizer parse without error
    //      (paint-ignored, no regression).
    //  10. Selector-level tests: class/id qualifiers on webkit pseudo-elements.
    public class WebkitScrollbarPseudoTests {
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

        static ScrollState VerticalScrollState(double viewportH, double contentH, double boxWidth) =>
            new ScrollState {
                ViewportHeight = viewportH,
                ViewportWidth = boxWidth - ScrollMath.ScrollbarTrackThicknessPx,
                ScrollHeight = contentH,
                ScrollWidth = boxWidth - ScrollMath.ScrollbarTrackThicknessPx,
                OverflowX = ScrollOverflow.Hidden,
                OverflowY = ScrollOverflow.Scroll,
            };

        static ScrollState HorizontalScrollState(double viewportW, double contentW, double boxHeight) =>
            new ScrollState {
                ViewportWidth = viewportW,
                ViewportHeight = boxHeight - ScrollMath.ScrollbarTrackThicknessPx,
                ScrollWidth = contentW,
                ScrollHeight = boxHeight - ScrollMath.ScrollbarTrackThicknessPx,
                OverflowX = ScrollOverflow.Scroll,
                OverflowY = ScrollOverflow.Hidden,
            };

        static List<FillRectCommand> CollectFills(PaintList list) {
            var fills = new List<FillRectCommand>();
            foreach (var c in list.Commands)
                if (c is FillRectCommand f) fills.Add(f);
            return fills;
        }

        // ── 1. Parser keeps webkit-scrollbar selectors ────────────────────────

        // ::-webkit-scrollbar must parse without throwing.
        [Test]
        public void Parser_accepts_webkit_scrollbar_selector() {
            // Should not throw SelectorParseException.
            Assert.DoesNotThrow(() => SelectorParser.Parse("::-webkit-scrollbar"),
                "'::-webkit-scrollbar' must parse without error");
        }

        // ::-webkit-scrollbar-thumb must parse without throwing.
        [Test]
        public void Parser_accepts_webkit_scrollbar_thumb_selector() {
            Assert.DoesNotThrow(() => SelectorParser.Parse("::-webkit-scrollbar-thumb"),
                "'::-webkit-scrollbar-thumb' must parse without error");
        }

        // ::-webkit-scrollbar-track must parse without throwing.
        [Test]
        public void Parser_accepts_webkit_scrollbar_track_selector() {
            Assert.DoesNotThrow(() => SelectorParser.Parse("::-webkit-scrollbar-track"),
                "'::-webkit-scrollbar-track' must parse without error");
        }

        // Qualified selectors (.scroll::-webkit-scrollbar) must parse.
        [Test]
        public void Parser_accepts_class_qualified_webkit_scrollbar_selector() {
            Assert.DoesNotThrow(() => SelectorParser.Parse(".scroll::-webkit-scrollbar"),
                "class-qualified '::-webkit-scrollbar' must parse without error");
        }

        // ::-webkit-scrollbar-corner / -button / -resizer: parse-and-ignore.
        [Test]
        public void Parser_accepts_ignored_webkit_scrollbar_pseudo_elements() {
            Assert.DoesNotThrow(() => SelectorParser.Parse("::-webkit-scrollbar-corner"),
                "'::-webkit-scrollbar-corner' must parse without error");
            Assert.DoesNotThrow(() => SelectorParser.Parse("::-webkit-scrollbar-button"),
                "'::-webkit-scrollbar-button' must parse without error");
            Assert.DoesNotThrow(() => SelectorParser.Parse("::-webkit-scrollbar-resizer"),
                "'::-webkit-scrollbar-resizer' must parse without error");
        }

        // CascadeEngine routes webkit rules into their buckets — verify via
        // ComputeWebkitScrollbar returning a non-null style when a rule matches.
        [Test]
        public void CascadeEngine_routes_webkit_scrollbar_rule_to_dedicated_bucket() {
            var doc = Html("<div id=\"s\"></div>");
            var engine = Engine("#s::-webkit-scrollbar { width: 10px; }");
            var host = doc.GetElementById("s");
            // ComputeWebkitScrollbar must return non-null when a rule matches.
            var style = engine.ComputeWebkitScrollbar(host);
            Assert.That(style, Is.Not.Null,
                "ComputeWebkitScrollbar must return a style when a matching rule exists");
        }

        // When no webkit rule matches, ComputeWebkitScrollbar returns null.
        [Test]
        public void CascadeEngine_returns_null_webkit_scrollbar_when_no_rule_matches() {
            var doc = Html("<div id=\"s\"></div>");
            var engine = Engine("#other::-webkit-scrollbar { width: 10px; }");
            var host = doc.GetElementById("s");
            var style = engine.ComputeWebkitScrollbar(host);
            Assert.That(style, Is.Null,
                "ComputeWebkitScrollbar must return null when no matching rule exists");
        }

        // ── 2. ::-webkit-scrollbar { width } → thickness override ────────────

        // Vertical scrollbar: ::-webkit-scrollbar { width: 6px } → 6px track.
        [Test]
        public void WebkitScrollbar_width_overrides_vertical_track_thickness() {
            var doc = Html("<div id=\"s\"></div>");
            var host = doc.GetElementById("s");
            var engine = Engine("#s::-webkit-scrollbar { width: 6px; }");
            var box = MakeBox(200, 100, host);
            var state = VerticalScrollState(100, 500, 200);
            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null, engine);
            var fills = CollectFills(list);
            Assert.That(fills.Count, Is.EqualTo(2),
                "webkit scrollbar must emit track + thumb (2 fills)");
            Assert.That(fills[0].Bounds.Width, Is.EqualTo(6.0).Within(0.001),
                "::-webkit-scrollbar { width: 6px } must set vertical track width to 6px");
        }

        // Horizontal scrollbar: ::-webkit-scrollbar { height: 4px } → 4px track.
        [Test]
        public void WebkitScrollbar_height_overrides_horizontal_track_thickness() {
            var doc = Html("<div id=\"s\"></div>");
            var host = doc.GetElementById("s");
            var engine = Engine("#s::-webkit-scrollbar { height: 4px; }");
            var box = MakeBox(200, 100, host);
            // Horizontal scroll state: ShowsTrackX, not ShowsTrackY.
            var state = HorizontalScrollState(200, 800, 100);
            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null, engine);
            var fills = CollectFills(list);
            // Track + thumb = 2 fills.
            Assert.That(fills.Count, Is.EqualTo(2),
                "webkit scrollbar must emit track + thumb for horizontal track");
            Assert.That(fills[0].Bounds.Height, Is.EqualTo(4.0).Within(0.001),
                "::-webkit-scrollbar { height: 4px } must set horizontal track height to 4px");
        }

        // ── 3. Thumb color from ::-webkit-scrollbar-thumb ────────────────────

        [Test]
        public void WebkitScrollbarThumb_background_color_applies_to_thumb() {
            var doc = Html("<div id=\"s\"></div>");
            var host = doc.GetElementById("s");
            // background-color: red → thumb red; no track rule → UA track color.
            var engine = Engine("#s::-webkit-scrollbar-thumb { background-color: red; }");
            var box = MakeBox(200, 100, host);
            var state = VerticalScrollState(100, 500, 200);
            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null, engine);
            var fills = CollectFills(list);
            Assert.That(fills.Count, Is.EqualTo(2));
            // fills[1] = thumb = red.
            Assert.That(fills[1].Brush.Color.R, Is.GreaterThan(0.5f),
                "thumb (fills[1]) must be red (high R)");
            Assert.That(fills[1].Brush.Color.B, Is.LessThan(0.1f),
                "thumb (fills[1]) must be red (low B)");
        }

        // ── 4. Track color from ::-webkit-scrollbar-track ────────────────────

        [Test]
        public void WebkitScrollbarTrack_background_color_applies_to_track() {
            var doc = Html("<div id=\"s\"></div>");
            var host = doc.GetElementById("s");
            // background-color: blue → track blue; no thumb rule → UA thumb color.
            var engine = Engine("#s::-webkit-scrollbar-track { background-color: blue; }");
            var box = MakeBox(200, 100, host);
            var state = VerticalScrollState(100, 500, 200);
            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null, engine);
            var fills = CollectFills(list);
            Assert.That(fills.Count, Is.EqualTo(2));
            // fills[0] = track = blue.
            Assert.That(fills[0].Brush.Color.B, Is.GreaterThan(0.5f),
                "track (fills[0]) must be blue (high B)");
            Assert.That(fills[0].Brush.Color.R, Is.LessThan(0.1f),
                "track (fills[0]) must be blue (low R)");
        }

        // Both thumb and track authored together.
        [Test]
        public void WebkitScrollbar_thumb_and_track_colors_apply_together() {
            var doc = Html("<div id=\"s\"></div>");
            var host = doc.GetElementById("s");
            var engine = Engine(
                "#s::-webkit-scrollbar-thumb { background-color: red; }" +
                "#s::-webkit-scrollbar-track { background-color: blue; }");
            var box = MakeBox(200, 100, host);
            var state = VerticalScrollState(100, 500, 200);
            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null, engine);
            var fills = CollectFills(list);
            Assert.That(fills.Count, Is.EqualTo(2));
            // fills[0] = track = blue.
            Assert.That(fills[0].Brush.Color.B, Is.GreaterThan(0.5f), "track must be blue (high B)");
            Assert.That(fills[0].Brush.Color.R, Is.LessThan(0.1f), "track must be blue (low R)");
            // fills[1] = thumb = red.
            Assert.That(fills[1].Brush.Color.R, Is.GreaterThan(0.5f), "thumb must be red (high R)");
            Assert.That(fills[1].Brush.Color.B, Is.LessThan(0.1f), "thumb must be red (low B)");
        }

        // ── 5. Webkit presence disables CSS Scrollbars L1 (precedence) ────────

        // When webkit rules are present, scrollbar-color is IGNORED.
        [Test]
        public void Webkit_presence_overrides_scrollbar_color_L1() {
            var doc = Html("<div id=\"s\"></div>");
            var host = doc.GetElementById("s");
            // scrollbar-color: green purple (L1) — should be ignored.
            // webkit thumb = red → overrides.
            var engine = Engine("#s::-webkit-scrollbar-thumb { background-color: red; }");
            var box = MakeBox(200, 100, host);
            // Also set scrollbar-color on the box style (L1).
            box.Style.Set("scrollbar-color", "green purple");
            var state = VerticalScrollState(100, 500, 200);
            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null, engine);
            var fills = CollectFills(list);
            Assert.That(fills.Count, Is.EqualTo(2));
            // Thumb must be red (webkit), NOT green (L1).
            Assert.That(fills[1].Brush.Color.R, Is.GreaterThan(0.5f),
                "webkit thumb color must win over scrollbar-color: green purple");
            Assert.That(fills[1].Brush.Color.G, Is.LessThan(0.3f),
                "L1 green thumb must be suppressed by webkit");
        }

        // When webkit rules are present, scrollbar-width is IGNORED.
        [Test]
        public void Webkit_presence_overrides_scrollbar_width_L1() {
            var doc = Html("<div id=\"s\"></div>");
            var host = doc.GetElementById("s");
            // webkit: 6px width; L1: thin (8px) — webkit must win.
            var engine = Engine("#s::-webkit-scrollbar { width: 6px; }");
            var box = MakeBox(200, 100, host);
            box.Style.Set("scrollbar-width", "thin"); // 8px via L1 — must be ignored
            var state = VerticalScrollState(100, 500, 200);
            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null, engine);
            var fills = CollectFills(list);
            Assert.That(fills.Count, Is.EqualTo(2));
            // Track width must be 6px (webkit), not 8px (L1 thin).
            Assert.That(fills[0].Bounds.Width, Is.EqualTo(6.0).Within(0.001),
                "webkit width:6px must override scrollbar-width:thin (8px)");
        }

        // ── 6. Elements without webkit rules keep L1 behavior (control) ───────

        // An element with NO webkit rules must still apply L1 scrollbar-color.
        [Test]
        public void Without_webkit_rules_l1_scrollbar_color_still_applies() {
            // The cascade has a webkit rule but it targets #other, not #s.
            var doc = Html("<div id=\"s\"></div><div id=\"other\"></div>");
            var host = doc.GetElementById("s");
            var engine = Engine("#other::-webkit-scrollbar-thumb { background-color: yellow; }");
            var box = MakeBox(200, 100, host);
            box.Style.Set("scrollbar-color", "red blue"); // L1 on #s
            var state = VerticalScrollState(100, 500, 200);
            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null, engine);
            var fills = CollectFills(list);
            Assert.That(fills.Count, Is.EqualTo(2));
            // #s has no webkit rules — L1 red/blue must apply.
            Assert.That(fills[0].Brush.Color.B, Is.GreaterThan(0.5f),
                "L1 track color blue must apply when no webkit rules match the element");
            Assert.That(fills[1].Brush.Color.R, Is.GreaterThan(0.5f),
                "L1 thumb color red must apply when no webkit rules match the element");
        }

        // Passing a null cascade falls through to L1 as if no webkit rules exist.
        [Test]
        public void Null_cascade_falls_through_to_l1_behavior() {
            var box = MakeBox(200, 100);
            box.Style.Set("scrollbar-color", "red blue");
            var state = VerticalScrollState(100, 500, 200);
            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null, null);
            var fills = CollectFills(list);
            Assert.That(fills.Count, Is.EqualTo(2));
            // L1 colors must apply.
            Assert.That(fills[0].Brush.Color.B, Is.GreaterThan(0.5f), "track blue from L1");
            Assert.That(fills[1].Brush.Color.R, Is.GreaterThan(0.5f), "thumb red from L1");
        }

        // ── 7. Invalid / unresolvable values fall back gracefully ─────────────

        // ::-webkit-scrollbar { width: auto } — "auto" is not a px value; falls
        // back to L1 thickness (no webkit thickness override).
        [Test]
        public void WebkitScrollbar_width_auto_falls_back_to_l1_thickness() {
            var doc = Html("<div id=\"s\"></div>");
            var host = doc.GetElementById("s");
            var engine = Engine("#s::-webkit-scrollbar { width: auto; }");
            var box = MakeBox(200, 100, host);
            // No L1 scrollbar-width → UA default 12px.
            var state = VerticalScrollState(100, 500, 200);
            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null, engine);
            var fills = CollectFills(list);
            Assert.That(fills.Count, Is.EqualTo(2));
            // Thickness must be the UA default 12px (webkit "auto" is ignored at px level).
            Assert.That(fills[0].Bounds.Width, Is.EqualTo(ScrollMath.ScrollbarTrackThicknessPx).Within(0.001),
                "webkit width:auto must fall back to UA default thickness (12px)");
        }

        // Unrecognized color in ::-webkit-scrollbar-thumb keeps UA thumb color.
        [Test]
        public void WebkitScrollbarThumb_invalid_color_keeps_ua_thumb_color() {
            // "background-color: NOTACOLOR" — TryResolve fails; thumb stays UA default.
            var doc = Html("<div id=\"s\"></div>");
            var host = doc.GetElementById("s");
            var engine = Engine("#s::-webkit-scrollbar-thumb { background-color: NOTACOLOR; }");
            var box = MakeBox(200, 100, host);
            // Reference: no webkit rule at all — UA defaults.
            var boxRef = MakeBox(200, 100);
            var state = VerticalScrollState(100, 500, 200);
            var stateRef = VerticalScrollState(100, 500, 200);
            var list = new PaintList();
            var listRef = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null, engine);
            ScrollbarPaint.Emit(boxRef, stateRef, 0, 0, listRef, null, null);
            var fills = CollectFills(list);
            var fillsRef = CollectFills(listRef);
            Assert.That(fills.Count, Is.EqualTo(2));
            Assert.That(fillsRef.Count, Is.EqualTo(2));
            // Thumb colors must match the UA default (within tolerance).
            Assert.That(fills[1].Brush.Color.R, Is.EqualTo(fillsRef[1].Brush.Color.R).Within(0.01f),
                "Invalid webkit thumb color must fall back to UA thumb color");
        }

        // ── 8. Thumb border-radius ────────────────────────────────────────────

        // ::-webkit-scrollbar-thumb { border-radius: 4px } → thumb has non-zero radii.
        [Test]
        public void WebkitScrollbarThumb_border_radius_applied_to_thumb_fill() {
            var doc = Html("<div id=\"s\"></div>");
            var host = doc.GetElementById("s");
            var engine = Engine(
                "#s::-webkit-scrollbar-thumb { background-color: red; border-radius: 4px; }");
            var box = MakeBox(200, 100, host);
            var state = VerticalScrollState(100, 500, 200);
            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null, engine);
            var fills = CollectFills(list);
            Assert.That(fills.Count, Is.EqualTo(2));
            // fills[1] = thumb: Radii must be non-zero.
            Assert.That(fills[1].Radii.TopLeft.XRadius, Is.GreaterThan(0.0),
                "thumb with border-radius: 4px must have non-zero corner radii");
        }

        // Without border-radius, thumb radii are zero.
        [Test]
        public void WebkitScrollbarThumb_no_border_radius_has_zero_radii() {
            var doc = Html("<div id=\"s\"></div>");
            var host = doc.GetElementById("s");
            var engine = Engine("#s::-webkit-scrollbar-thumb { background-color: red; }");
            var box = MakeBox(200, 100, host);
            var state = VerticalScrollState(100, 500, 200);
            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null, engine);
            var fills = CollectFills(list);
            Assert.That(fills.Count, Is.EqualTo(2));
            Assert.That(fills[1].Radii.TopLeft.XRadius, Is.EqualTo(0.0).Within(0.001),
                "thumb without border-radius must have zero corner radii");
        }

        // Border-radius must be clamped to half the smaller dimension.
        [Test]
        public void WebkitScrollbarThumb_border_radius_clamped_to_half_dimension() {
            var doc = Html("<div id=\"s\"></div>");
            var host = doc.GetElementById("s");
            // Huge radius → must be clamped to thickness/2 = 6px.
            var engine = Engine(
                "#s::-webkit-scrollbar { width: 12px; }" +
                "#s::-webkit-scrollbar-thumb { background-color: red; border-radius: 9999px; }");
            var box = MakeBox(200, 100, host);
            var state = VerticalScrollState(100, 500, 200);
            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null, engine);
            var fills = CollectFills(list);
            Assert.That(fills.Count, Is.EqualTo(2));
            // Radius must be clamped to at most min(thickness, thumbH) / 2.
            double thumbH = fills[1].Bounds.Height;
            double maxRadius = System.Math.Min(12.0, thumbH) * 0.5;
            Assert.That(fills[1].Radii.TopLeft.XRadius, Is.LessThanOrEqualTo(maxRadius + 0.001),
                "thumb border-radius must be clamped to half the smaller dimension");
        }

        // ── 9. Selector with all three pseudo-elements together ───────────────

        // Full webkit scrollbar styling: width + thumb + track all applied.
        [Test]
        public void Full_webkit_scrollbar_styling_applies_width_thumb_and_track() {
            var doc = Html("<div id=\"s\"></div>");
            var host = doc.GetElementById("s");
            var engine = Engine(
                "#s::-webkit-scrollbar { width: 6px; }" +
                "#s::-webkit-scrollbar-thumb { background-color: navy; border-radius: 3px; }" +
                "#s::-webkit-scrollbar-track { background-color: white; }");
            var box = MakeBox(200, 100, host);
            var state = VerticalScrollState(100, 500, 200);
            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null, engine);
            var fills = CollectFills(list);
            Assert.That(fills.Count, Is.EqualTo(2));
            // Width: 6px.
            Assert.That(fills[0].Bounds.Width, Is.EqualTo(6.0).Within(0.001), "width must be 6px");
            // Track: white (R≈G≈B≈1 in linear).
            Assert.That(fills[0].Brush.Color.R, Is.GreaterThan(0.5f), "track must be white (high R)");
            Assert.That(fills[0].Brush.Color.G, Is.GreaterThan(0.5f), "track must be white (high G)");
            // Thumb: navy (B non-trivial in linear, linear navy B≈0.216).
            Assert.That(fills[1].Brush.Color.B, Is.GreaterThan(0.1f),
                "thumb (navy) must have non-trivial B channel");
            Assert.That(fills[1].Brush.Color.R, Is.LessThan(0.01f),
                "thumb (navy) must have zero R");
            // Border-radius: non-zero.
            Assert.That(fills[1].Radii.TopLeft.XRadius, Is.GreaterThan(0.0),
                "thumb border-radius: 3px must produce non-zero radii");
        }

        // ── 10. Ignored pseudo-elements don't pollute other rules ─────────────

        // A stylesheet with webkit-corner/button/resizer rules plus a valid
        // webkit-scrollbar-thumb rule: the latter must still match.
        [Test]
        public void Ignored_webkit_pseudo_elements_do_not_block_thumb_rule() {
            var doc = Html("<div id=\"s\"></div>");
            var host = doc.GetElementById("s");
            var engine = Engine(
                "#s::-webkit-scrollbar-corner { background: #000; }" +
                "#s::-webkit-scrollbar-button { background: #000; }" +
                "#s::-webkit-scrollbar-thumb { background-color: red; }");
            var box = MakeBox(200, 100, host);
            var state = VerticalScrollState(100, 500, 200);
            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null, engine);
            var fills = CollectFills(list);
            Assert.That(fills.Count, Is.EqualTo(2));
            // Thumb must still be red (webkit-corner/-button don't interfere).
            Assert.That(fills[1].Brush.Color.R, Is.GreaterThan(0.5f),
                "webkit-thumb rule must apply even when corner/button rules are present");
        }

        // ── Regression: original Emit() overload still works without cascade ──

        [Test]
        public void Original_emit_overload_works_without_cascade_regression() {
            var box = MakeBox(200, 100);
            box.Style.Set("scrollbar-color", "red blue");
            var state = VerticalScrollState(100, 500, 200);
            var list = new PaintList();
            // The zero-argument overload (no cascade): must not throw.
            ScrollbarPaint.Emit(box, state, 0, 0, list, null);
            var fills = CollectFills(list);
            Assert.That(fills.Count, Is.EqualTo(2),
                "original Emit overload must still apply L1 scrollbar-color");
            Assert.That(fills[0].Brush.Color.B, Is.GreaterThan(0.5f), "track blue from L1");
            Assert.That(fills[1].Brush.Color.R, Is.GreaterThan(0.5f), "thumb red from L1");
        }
    }
}
