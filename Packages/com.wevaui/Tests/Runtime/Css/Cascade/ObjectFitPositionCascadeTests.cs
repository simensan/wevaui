using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Images Module Level 3 §5–§6 — `object-fit` and `object-position` cascade.
    //
    // `object-fit` controls how a replaced element (e.g. <img>) is fitted
    // into its content box. `object-position` offsets the resulting rect
    // inside the box (default centre–centre).
    //
    // Spec:
    //   https://www.w3.org/TR/css-images-3/#the-object-fit
    //   https://www.w3.org/TR/css-images-3/#the-object-position
    //
    // object-fit:
    //   Initial:    fill
    //   Inherited:  NO
    //   Values:     fill | contain | cover | none | scale-down
    //
    // object-position:
    //   Initial:    50% 50%
    //   Inherited:  NO
    //   Values:     <bg-position>  (keywords + lengths + percentages)
    //
    // These tests cover only the CASCADE boundary (parse → cascade → Get).
    // The paint-time geometry produced by ApplyObjectFit / BoxToPaintConverter
    // is covered in Tests/Runtime/Paint/Conversion/.
    public class ObjectFitPositionCascadeTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<img id=\"x\" />");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        static ComputedStyle ComputeChild(string css) {
            var doc = Html("<div id=\"p\"><img id=\"x\" /></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            engine.Compute(doc.GetElementById("p"));
            return engine.Compute(doc.GetElementById("x"));
        }

        // ── object-fit: registration ───────────────────────────────────────

        [Test]
        public void Object_fit_is_registered() {
            Assert.That(CssProperties.GetId("object-fit"), Is.GreaterThanOrEqualTo(0));
        }

        // ── object-fit: initial value ──────────────────────────────────────

        [Test]
        public void Object_fit_initial_is_fill() {
            // CSS Images L3 §5: the default fit is `fill` — the image is
            // stretched to fill the content box without preserving aspect ratio.
            var cs = Compute("");
            Assert.That(cs.Get("object-fit"), Is.EqualTo("fill"));
        }

        // ── object-fit: all five keyword round-trips ───────────────────────

        [Test]
        public void Object_fit_fill_round_trips() {
            var cs = Compute("#x { object-fit: fill; }");
            Assert.That(cs.Get("object-fit"), Is.EqualTo("fill"));
        }

        [Test]
        public void Object_fit_contain_round_trips() {
            // `contain`: scale the image uniformly to fit; may show letterbox
            // areas in the remaining space. Preserves aspect ratio.
            var cs = Compute("#x { object-fit: contain; }");
            Assert.That(cs.Get("object-fit"), Is.EqualTo("contain"));
        }

        [Test]
        public void Object_fit_cover_round_trips() {
            // `cover`: scale uniformly to cover the box; image may be clipped.
            var cs = Compute("#x { object-fit: cover; }");
            Assert.That(cs.Get("object-fit"), Is.EqualTo("cover"));
        }

        [Test]
        public void Object_fit_none_round_trips() {
            // `none`: no scaling — image rendered at its natural size.
            var cs = Compute("#x { object-fit: none; }");
            Assert.That(cs.Get("object-fit"), Is.EqualTo("none"));
        }

        [Test]
        public void Object_fit_scale_down_round_trips() {
            // `scale-down`: min(none, contain) — image is not scaled up.
            var cs = Compute("#x { object-fit: scale-down; }");
            Assert.That(cs.Get("object-fit"), Is.EqualTo("scale-down"));
        }

        // ── object-fit: non-inheritance ────────────────────────────────────

        [Test]
        public void Object_fit_does_not_inherit() {
            // CSS Images L3 §5: Inherited: no. Child sees initial value `fill`.
            var cs = ComputeChild("div { object-fit: cover; }");
            Assert.That(cs.Get("object-fit"), Is.EqualTo("fill"),
                "object-fit is non-inherited; child must see initial 'fill'");
        }

        // ── object-fit: CSS-wide keywords ─────────────────────────────────

        [Test]
        public void Object_fit_initial_keyword_resolves_to_fill() {
            var cs = Compute("#x { object-fit: contain; } " +
                             "#x { object-fit: initial; }");
            Assert.That(cs.Get("object-fit"), Is.EqualTo("fill"),
                "initial keyword must resolve to spec initial 'fill'");
        }

        [Test]
        public void Object_fit_important_overrides_higher_specificity() {
            var doc = Html("<img id=\"x\" class=\"a\" />");
            var engine = new CascadeEngine(new[] {
                Author("img { object-fit: cover !important; } " +
                       "#x.a { object-fit: none; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("object-fit"), Is.EqualTo("cover"),
                "!important on lower-specificity rule must win");
        }

        // ══════════════════════════════════════════════════════════════════
        // object-position
        // ══════════════════════════════════════════════════════════════════

        // ── Registration ───────────────────────────────────────────────────

        [Test]
        public void Object_position_is_registered() {
            Assert.That(CssProperties.GetId("object-position"), Is.GreaterThanOrEqualTo(0));
        }

        // ── Initial value ──────────────────────────────────────────────────

        [Test]
        public void Object_position_initial_is_50pct_50pct() {
            // CSS Images L3 §6: initial value is `50% 50%` (centre).
            var cs = Compute("");
            Assert.That(cs.Get("object-position"), Is.EqualTo("50% 50%"));
        }

        // ── Keyword forms ──────────────────────────────────────────────────

        [Test]
        public void Object_position_top_left_round_trips() {
            var cs = Compute("#x { object-position: top left; }");
            Assert.That(cs.Get("object-position"), Is.EqualTo("top left"));
        }

        [Test]
        public void Object_position_bottom_right_round_trips() {
            var cs = Compute("#x { object-position: bottom right; }");
            Assert.That(cs.Get("object-position"), Is.EqualTo("bottom right"));
        }

        [Test]
        public void Object_position_center_round_trips() {
            // Single keyword `center` maps to `center center` per spec §6.
            var cs = Compute("#x { object-position: center; }");
            Assert.That(cs.Get("object-position"), Is.EqualTo("center"));
        }

        [Test]
        public void Object_position_top_center_round_trips() {
            var cs = Compute("#x { object-position: top center; }");
            Assert.That(cs.Get("object-position"), Is.EqualTo("top center"));
        }

        // ── Length and percentage forms ────────────────────────────────────

        [Test]
        public void Object_position_px_values_round_trip() {
            var cs = Compute("#x { object-position: 10px 20px; }");
            Assert.That(cs.Get("object-position"), Is.EqualTo("10px 20px"));
        }

        [Test]
        public void Object_position_percentage_values_round_trip() {
            var cs = Compute("#x { object-position: 25% 75%; }");
            Assert.That(cs.Get("object-position"), Is.EqualTo("25% 75%"));
        }

        [Test]
        public void Object_position_mixed_keyword_and_px_round_trips() {
            var cs = Compute("#x { object-position: left 20px; }");
            Assert.That(cs.Get("object-position"), Is.EqualTo("left 20px"));
        }

        // ── Non-inheritance ────────────────────────────────────────────────

        [Test]
        public void Object_position_does_not_inherit() {
            // CSS Images L3 §6: Inherited: no. Child sees initial `50% 50%`.
            var cs = ComputeChild("div { object-position: top left; }");
            Assert.That(cs.Get("object-position"), Is.EqualTo("50% 50%"),
                "object-position is non-inherited; child must see initial '50% 50%'");
        }

        // ── object-fit and object-position independence ────────────────────

        [Test]
        public void Object_fit_and_object_position_are_independent_cascade_slots() {
            var cs = Compute("#x { object-fit: cover; object-position: top left; }");
            Assert.That(cs.Get("object-fit"), Is.EqualTo("cover"));
            Assert.That(cs.Get("object-position"), Is.EqualTo("top left"));
        }
    }
}
