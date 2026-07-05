using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Backgrounds and Borders Level 3 §4.2 / CSS2.1 §8.5.3 —
    // border-style keyword round-trips.
    //
    // The spec defines nine border-style keywords:
    //   none, hidden, dotted, dashed, solid, double, groove, ridge, inset, outset
    //
    // `none` and `hidden` both suppress the border (hidden has higher priority in
    // table border conflict resolution). The visual keywords (dotted through outset)
    // select a rendering style; the paint-side resolution is handled by
    // BorderResolverTests. This file pins parse-level acceptance and cascade
    // round-trips for all nine keywords against `border-top-style` (the canonical
    // physical longhand), plus initial-value checks on all four physical sides.
    //
    // Non-inheritance is verified for the top-style longhand; by CssProperties
    // registration all four sides share the same property descriptor (non-inherited,
    // initial `none`).
    public class BorderStyleKeywordTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        static ComputedStyle ComputeChild(string css) {
            var doc = Html("<div><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("child"));
        }

        // ── initial values ────────────────────────────────────────────────

        [Test]
        public void Border_top_style_initial_is_none() {
            // CSS Backgrounds 3 §4.2: initial value `none` for all sides.
            var cs = Compute("");
            Assert.That(cs.Get("border-top-style"), Is.EqualTo("none"));
        }

        [Test]
        public void Border_right_style_initial_is_none() {
            var cs = Compute("");
            Assert.That(cs.Get("border-right-style"), Is.EqualTo("none"));
        }

        [Test]
        public void Border_bottom_style_initial_is_none() {
            var cs = Compute("");
            Assert.That(cs.Get("border-bottom-style"), Is.EqualTo("none"));
        }

        [Test]
        public void Border_left_style_initial_is_none() {
            var cs = Compute("");
            Assert.That(cs.Get("border-left-style"), Is.EqualTo("none"));
        }

        // ── all nine keywords ─────────────────────────────────────────────

        [Test]
        public void Border_style_none_round_trips() {
            // `none`: no border; border-width computes to 0.
            var cs = Compute("#x { border-top-style: none; }");
            Assert.That(cs.Get("border-top-style"), Is.EqualTo("none"));
        }

        [Test]
        public void Border_style_hidden_round_trips() {
            // `hidden`: same as none but takes priority in table border conflict.
            var cs = Compute("#x { border-top-style: hidden; }");
            Assert.That(cs.Get("border-top-style"), Is.EqualTo("hidden"));
        }

        [Test]
        public void Border_style_dotted_round_trips() {
            // `dotted`: series of round dots.
            var cs = Compute("#x { border-top-style: dotted; }");
            Assert.That(cs.Get("border-top-style"), Is.EqualTo("dotted"));
        }

        [Test]
        public void Border_style_dashed_round_trips() {
            // `dashed`: series of short square-ended dashes.
            var cs = Compute("#x { border-top-style: dashed; }");
            Assert.That(cs.Get("border-top-style"), Is.EqualTo("dashed"));
        }

        [Test]
        public void Border_style_solid_round_trips() {
            // `solid`: single unbroken line.
            var cs = Compute("#x { border-top-style: solid; }");
            Assert.That(cs.Get("border-top-style"), Is.EqualTo("solid"));
        }

        [Test]
        public void Border_style_double_round_trips() {
            // `double`: two lines with a gap; total width = border-width.
            var cs = Compute("#x { border-top-style: double; }");
            Assert.That(cs.Get("border-top-style"), Is.EqualTo("double"));
        }

        [Test]
        public void Border_style_groove_round_trips() {
            // `groove`: carved-into-the-page 3-D effect.
            var cs = Compute("#x { border-top-style: groove; }");
            Assert.That(cs.Get("border-top-style"), Is.EqualTo("groove"));
        }

        [Test]
        public void Border_style_ridge_round_trips() {
            // `ridge`: coming-out-of-the-page 3-D effect (opposite of groove).
            var cs = Compute("#x { border-top-style: ridge; }");
            Assert.That(cs.Get("border-top-style"), Is.EqualTo("ridge"));
        }

        [Test]
        public void Border_style_inset_round_trips() {
            // `inset`: entire box appears embedded in the page.
            var cs = Compute("#x { border-top-style: inset; }");
            Assert.That(cs.Get("border-top-style"), Is.EqualTo("inset"));
        }

        [Test]
        public void Border_style_outset_round_trips() {
            // `outset`: entire box appears raised from the page (opposite of inset).
            var cs = Compute("#x { border-top-style: outset; }");
            Assert.That(cs.Get("border-top-style"), Is.EqualTo("outset"));
        }

        // ── per-side independence ─────────────────────────────────────────

        [Test]
        public void Each_side_can_carry_different_style() {
            // All four physical longhands are independent — setting one does
            // not affect the others.
            var cs = Compute("#x { border-top-style: solid; border-right-style: dashed; " +
                             "border-bottom-style: dotted; border-left-style: double; }");
            Assert.That(cs.Get("border-top-style"), Is.EqualTo("solid"));
            Assert.That(cs.Get("border-right-style"), Is.EqualTo("dashed"));
            Assert.That(cs.Get("border-bottom-style"), Is.EqualTo("dotted"));
            Assert.That(cs.Get("border-left-style"), Is.EqualTo("double"));
        }

        // ── non-inheritance ───────────────────────────────────────────────

        [Test]
        public void Border_top_style_does_not_inherit() {
            // CSS Backgrounds 3 §4.2: border-style longhands are not inherited.
            var cs = ComputeChild("div { border-top-style: solid; }");
            Assert.That(cs.Get("border-top-style"), Is.EqualTo("none"),
                "border-top-style is non-inherited; child sees initial none");
        }
    }
}
