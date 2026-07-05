using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Layout.Scrolling;
using Weva.Paint;

namespace Weva.Tests.Layout.Scrolling {
    // CSS Scrollbars Styling Module Level 1 — https://www.w3.org/TR/css-scrollbars-1/
    // Supplementary paint-level and cascade integration tests for:
    //   scrollbar-color  (§3.2) — auto | <color> <color>  (thumb track); Inherited: yes
    //   scrollbar-width  (§3.3) — auto | thin | none;       Inherited: no
    //
    // These tests complement ScrollbarOverscrollCascadeTests.cs (cascade boundary),
    // ScrollbarPaintI14bTests.cs (paint wiring), and ScrollbarLayoutI14bTests.cs
    // (layout reservation). The focus here is on:
    //   1. Paint-level currentColor resolution for scrollbar-color.
    //   2. Single-color form rejected at paint level (cascade stores it, resolver rejects).
    //   3. Explicit thin < auto width comparison (not just absolute values).
    //   4. Two-color named-color round-trip to paint with color channel checks.
    //   5. scrollbar-color: auto keeps the UA default palette (double-check with explicit setting).
    //   6. Chrome reference widths documented inline.
    //
    // Chrome reference (CSS Scrollbars L1 §3.3):
    //   auto: 15px (classic scrollbar on Windows); engine UA default 12px (overlay-style).
    //   thin: ~8px (overlay-style thin). Engine values: auto=12px, thin=8px.
    //   Px values chosen to keep overlay-style scrollbars proportional to Chrome thin/auto ratio.
    public class ScrollbarStylingL1Tests {
        // ── Helpers ───────────────────────────────────────────────────────────────

        static BlockBox MakeBox(double w, double h, string css = null) {
            var b = new BlockBox();
            b.X = 0; b.Y = 0; b.Width = w; b.Height = h;
            b.Element = new Element("div");
            b.Style = new ComputedStyle(b.Element);
            if (!string.IsNullOrEmpty(css)) {
                // Parse "prop: value; ..." and apply each declaration.
                foreach (var decl in css.Split(';')) {
                    var d = decl.Trim();
                    if (string.IsNullOrEmpty(d)) continue;
                    int colon = d.IndexOf(':');
                    if (colon < 0) continue;
                    b.Style.Set(d.Substring(0, colon).Trim(), d.Substring(colon + 1).Trim());
                }
            }
            return b;
        }

        static ScrollState VerticalScrollState(double viewportH, double contentH, double boxWidth) {
            return new ScrollState {
                ViewportHeight = viewportH,
                ViewportWidth = boxWidth - ScrollMath.ScrollbarTrackThicknessPx,
                ScrollHeight = contentH,
                ScrollWidth = boxWidth - ScrollMath.ScrollbarTrackThicknessPx,
                OverflowX = ScrollOverflow.Hidden,
                OverflowY = ScrollOverflow.Scroll,
            };
        }

        static List<FillRectCommand> CollectFills(PaintList list) {
            var fills = new List<FillRectCommand>();
            foreach (var c in list.Commands)
                if (c is FillRectCommand f) fills.Add(f);
            return fills;
        }

        // ── §3.3 scrollbar-width ──────────────────────────────────────────────────

        // CSS Scrollbars L1 §3.3: `thin` must be strictly narrower than `auto`.
        // Chrome reference: auto=15px, thin≈8px. Engine: auto=12px, thin=8px.
        [Test]
        public void ScrollbarWidth_thin_is_strictly_narrower_than_auto() {
            Assert.That(
                ScrollMath.ScrollbarThinThicknessPx,
                Is.LessThan(ScrollMath.ScrollbarTrackThicknessPx),
                "thin scrollbar thickness must be strictly less than auto (12px) per §3.3");
        }

        // Engine value documentation test — pins our px constants vs Chrome.
        // Chrome classic auto: 15px. Chrome overlay thin: ~8px. Our overlay ratio:
        // thin/auto = 8/12 = 0.67, Chrome thin/auto ≈ 8/15 = 0.53. Acceptable for overlay.
        [Test]
        public void ScrollbarWidth_constants_match_documented_px_values() {
            // auto = 12px (overlay-style, engine UA default).
            Assert.That(ScrollMath.ScrollbarTrackThicknessPx, Is.EqualTo(12.0).Within(0.001),
                "auto thickness must be 12px (overlay-style engine default)");
            // thin = 8px (overlay-style thin, proportional to Chrome's 8px overlay thin).
            Assert.That(ScrollMath.ScrollbarThinThicknessPx, Is.EqualTo(8.0).Within(0.001),
                "thin thickness must be 8px (matching Chrome overlay thin)");
        }

        // Paint-level: thin track is visually narrower than auto track.
        [Test]
        public void ScrollbarWidth_thin_painted_width_less_than_auto_painted_width() {
            var boxAuto = MakeBox(200, 100);
            var boxThin = MakeBox(200, 100, "scrollbar-width: thin");

            var stateAuto = VerticalScrollState(100, 500, 200);
            var stateThin = VerticalScrollState(100, 500, 200);

            var listAuto = new PaintList();
            var listThin = new PaintList();
            ScrollbarPaint.Emit(boxAuto, stateAuto, 0, 0, listAuto, null);
            ScrollbarPaint.Emit(boxThin, stateThin, 0, 0, listThin, null);

            var fillsAuto = CollectFills(listAuto);
            var fillsThin = CollectFills(listThin);
            Assert.That(fillsAuto.Count, Is.EqualTo(2), "auto: 2 fill commands");
            Assert.That(fillsThin.Count, Is.EqualTo(2), "thin: 2 fill commands");
            // Track is fills[0] (emitted before thumb).
            Assert.That(
                fillsThin[0].Bounds.Width,
                Is.LessThan(fillsAuto[0].Bounds.Width),
                "thin track paint width must be strictly less than auto track paint width");
        }

        // §3.3: none suppresses both track and thumb paint commands entirely.
        [Test]
        public void ScrollbarWidth_none_produces_no_fills_regardless_of_overflow() {
            var box = MakeBox(200, 100, "scrollbar-width: none");
            var state = VerticalScrollState(100, 500, 200);
            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null);
            Assert.That(CollectFills(list), Is.Empty,
                "scrollbar-width: none must suppress all paint commands (§3.3)");
        }

        // ── §3.2 scrollbar-color ─────────────────────────────────────────────────

        // §3.2: first color = thumb, second color = track. Verify paint assignment.
        // CSS: scrollbar-color: red blue → thumb=red, track=blue.
        // Paint order: track emitted BEFORE thumb (fills[0]=track=blue, fills[1]=thumb=red).
        [Test]
        public void ScrollbarColor_first_token_is_thumb_second_is_track() {
            var box = MakeBox(200, 100, "scrollbar-color: red blue");
            var state = VerticalScrollState(100, 500, 200);
            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null);
            var fills = CollectFills(list);
            Assert.That(fills.Count, Is.EqualTo(2), "two fill commands expected");
            // fills[0] = track = blue → R low, B high.
            Assert.That(fills[0].Brush.Color.R, Is.LessThan(0.1f),
                "track fill (fills[0]) must be blue (low R)");
            Assert.That(fills[0].Brush.Color.B, Is.GreaterThan(0.5f),
                "track fill (fills[0]) must be blue (high B)");
            // fills[1] = thumb = red → R high, B low.
            Assert.That(fills[1].Brush.Color.R, Is.GreaterThan(0.5f),
                "thumb fill (fills[1]) must be red (high R)");
            Assert.That(fills[1].Brush.Color.B, Is.LessThan(0.1f),
                "thumb fill (fills[1]) must be red (low B)");
        }

        // §3.2: `auto` leaves the UA default palette in place.
        // Explicit `auto` must produce the same colors as not setting the property.
        [Test]
        public void ScrollbarColor_auto_keeps_ua_default_track_color() {
            var boxDefault = MakeBox(200, 100);          // no scrollbar-color
            var boxAuto = MakeBox(200, 100, "scrollbar-color: auto");

            var stateDefault = VerticalScrollState(100, 500, 200);
            var stateAuto    = VerticalScrollState(100, 500, 200);

            var listDefault = new PaintList();
            var listAuto    = new PaintList();
            ScrollbarPaint.Emit(boxDefault, stateDefault, 0, 0, listDefault, null);
            ScrollbarPaint.Emit(boxAuto, stateAuto, 0, 0, listAuto, null);

            var fd = CollectFills(listDefault);
            var fa = CollectFills(listAuto);
            Assert.That(fd.Count, Is.EqualTo(2));
            Assert.That(fa.Count, Is.EqualTo(2));
            // Track colors must be identical.
            Assert.That(fa[0].Brush.Color.R, Is.EqualTo(fd[0].Brush.Color.R).Within(0.001f),
                "scrollbar-color: auto must not change the UA track color");
            Assert.That(fa[0].Brush.Color.G, Is.EqualTo(fd[0].Brush.Color.G).Within(0.001f));
            Assert.That(fa[0].Brush.Color.B, Is.EqualTo(fd[0].Brush.Color.B).Within(0.001f));
        }

        // §3.2: Single-color form is spec-invalid. The cascade engine stores it
        // as-authored (pass-through); the paint resolver (TryResolveScrollbarColors)
        // rejects token counts != 2 and falls back to the UA palette.
        [Test]
        public void ScrollbarColor_single_color_falls_back_to_ua_palette_at_paint_level() {
            var boxSingle  = MakeBox(200, 100, "scrollbar-color: navy"); // spec-invalid: 1 token
            var boxDefault = MakeBox(200, 100);                          // no property

            var stateSingle  = VerticalScrollState(100, 500, 200);
            var stateDefault = VerticalScrollState(100, 500, 200);

            var listSingle  = new PaintList();
            var listDefault = new PaintList();
            ScrollbarPaint.Emit(boxSingle, stateSingle, 0, 0, listSingle, null);
            ScrollbarPaint.Emit(boxDefault, stateDefault, 0, 0, listDefault, null);

            var fs = CollectFills(listSingle);
            var fd = CollectFills(listDefault);
            Assert.That(fs.Count, Is.EqualTo(2),
                "single-color scrollbar-color still emits track+thumb (paint falls back to UA)");
            // Colors must match the UA defaults — the invalid single-color value was rejected.
            Assert.That(fs[0].Brush.Color.R, Is.EqualTo(fd[0].Brush.Color.R).Within(0.001f),
                "single-color form must fall back to UA track color (not navy)");
            Assert.That(fs[1].Brush.Color.R, Is.EqualTo(fd[1].Brush.Color.R).Within(0.001f),
                "single-color form must fall back to UA thumb color");
        }

        // §3.2 + CSS Color 4 §3: currentColor resolves to the element's `color` value.
        // Verify the paint resolver uses the element color as the resolved value.
        //
        // Color channel expectations (linear sRGB, IEC 61966-2-1):
        //   green (#008000): linear G ≈ 0.216, R = 0, B = 0.
        //   white (#ffffff): linear R = G = B = 1.0.
        //   navy  (#000080): linear B ≈ 0.216, R = 0, G = 0.
        //
        // The test uses ">0.1" rather than ">0.3" because linear green/navy channel
        // values are ~0.216 after sRGB gamma decode (v = ((0.502+0.055)/1.055)^2.4).
        [Test]
        public void ScrollbarColor_currentcolor_thumb_resolves_to_element_color() {
            // Set color=green on the element and use currentcolor as the thumb.
            // Expected: thumb painted green (linear G≈0.216, R=0), track painted white.
            var box = MakeBox(200, 100, "scrollbar-color: currentcolor white; color: green");
            var state = VerticalScrollState(100, 500, 200);
            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null);

            var fills = CollectFills(list);
            Assert.That(fills.Count, Is.EqualTo(2), "two fills expected");
            // fills[0]=track=white → R,G,B all high.
            Assert.That(fills[0].Brush.Color.R, Is.GreaterThan(0.5f),
                "track (white) must have high R");
            Assert.That(fills[0].Brush.Color.G, Is.GreaterThan(0.5f),
                "track (white) must have high G");
            // fills[1]=thumb=green → G non-trivial, R zero.
            // linear green G ≈ 0.216; threshold 0.1 is conservative but unambiguous.
            Assert.That(fills[1].Brush.Color.G, Is.GreaterThan(0.1f),
                "thumb (currentcolor=green) must have non-trivial G channel (linear ≈ 0.216)");
            Assert.That(fills[1].Brush.Color.R, Is.LessThan(0.01f),
                "thumb (currentcolor=green) must have zero R");
        }

        // §3.2: currentColor as the TRACK color (second token).
        [Test]
        public void ScrollbarColor_currentcolor_track_resolves_to_element_color() {
            // Track = currentcolor → element's color = navy (#000080, linear B≈0.216).
            // Thumb = white.
            var box = MakeBox(200, 100, "scrollbar-color: white currentcolor; color: navy");
            var state = VerticalScrollState(100, 500, 200);
            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null);

            var fills = CollectFills(list);
            Assert.That(fills.Count, Is.EqualTo(2), "two fills expected");
            // fills[0]=track=navy → B non-trivial, R=0.
            // linear navy B ≈ 0.216; threshold 0.1 is conservative but unambiguous.
            Assert.That(fills[0].Brush.Color.B, Is.GreaterThan(0.1f),
                "track (currentcolor=navy) must have non-trivial B channel (linear ≈ 0.216)");
            Assert.That(fills[0].Brush.Color.R, Is.LessThan(0.01f),
                "track (currentcolor=navy) must have zero R");
            // fills[1]=thumb=white → R,G,B all high.
            Assert.That(fills[1].Brush.Color.R, Is.GreaterThan(0.5f),
                "thumb (white) must have high R");
        }

        // §3.1 / §3.3 interaction: width:none with explicit scrollbar-color has no effect
        // (suppression takes priority over color).
        [Test]
        public void ScrollbarWidth_none_suppresses_even_when_scrollbar_color_is_set() {
            var box = MakeBox(200, 100, "scrollbar-width: none; scrollbar-color: red blue");
            var state = VerticalScrollState(100, 500, 200);
            var list = new PaintList();
            ScrollbarPaint.Emit(box, state, 0, 0, list, null);
            Assert.That(CollectFills(list), Is.Empty,
                "scrollbar-width: none must suppress paint even when scrollbar-color is explicit");
        }

        // ── §3.1 inherited / §3.3 non-inherited in cascade ──────────────────────

        // scrollbar-width is non-inherited: verify the registry flag (regression guard).
        [Test]
        public void ScrollbarWidth_is_registered_as_non_inherited() {
            Assert.That(CssProperties.IsInherited("scrollbar-width"), Is.False,
                "scrollbar-width must be non-inherited per CSS Scrollbars L1 §3.3");
        }

        // scrollbar-color is inherited: verify the registry flag (regression guard).
        [Test]
        public void ScrollbarColor_is_registered_as_inherited() {
            Assert.That(CssProperties.IsInherited("scrollbar-color"), Is.True,
                "scrollbar-color must be inherited per CSS Scrollbars L1 §3.2");
        }
    }
}
