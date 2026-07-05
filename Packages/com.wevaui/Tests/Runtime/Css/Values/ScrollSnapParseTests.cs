using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout.Scrolling.Smooth;
using Weva.Layout.Scrolling.Snap;
using Weva.Parsing;

namespace Weva.Tests.Css.Values {
    // Parse-level regression coverage for scroll-snap-type / scroll-snap-align
    // / scroll-behavior. These properties are owned by Layout.Scrolling but
    // registered with CssProperties on first use (ScrollSnapProperties /
    // SmoothScrollProperties .EnsureRegistered). The cascade engine must
    // round-trip the author string into ComputedStyle.Get(<propId>).
    //
    // Why parse-level? The engine currently parses these at the resolver
    // layer (SnapParser.ParseType / SmoothScrollAnimator) rather than at
    // cascade time, so the cascade just stores raw strings. These tests
    // pin the round-trip contract so any future migration to typed
    // CssValue storage stays compatible with author CSS.
    public class ScrollSnapParseTests {
        static ScrollSnapParseTests() {
            // Both registries must be set up before CascadeEngine sees a rule
            // mentioning these property names, otherwise the parser drops the
            // declaration as unknown.
            ScrollSnapProperties.EnsureRegistered();
            SmoothScrollProperties.EnsureRegistered();
        }

        static OriginatedStylesheet Author(string s) =>
            OriginatedStylesheet.Author(CssParser.Parse(s));
        static Document Html(string s) => HtmlParser.Parse(s);

        static ComputedStyle Compute(string css) {
            // Single element with id="x" so we can pull the computed style
            // back out by id; keeps each test self-contained without a
            // shared fixture.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author("#x { " + css + " }") });
            return engine.Compute(doc.GetElementById("x"));
        }

        // ---- scroll-snap-type ----

        [Test]
        public void Scroll_snap_type_none_parses() {
            var cs = Compute("scroll-snap-type: none;");
            Assert.That(cs.Get("scroll-snap-type"), Is.EqualTo("none"));
        }

        [Test]
        public void Scroll_snap_type_x_mandatory_parses() {
            // v1: the cascade stores `x mandatory` verbatim — SnapParser does
            // the axis+strictness split downstream. No typed CssValue
            // representation yet.
            var cs = Compute("scroll-snap-type: x mandatory;");
            Assert.That(cs.Get("scroll-snap-type"), Is.EqualTo("x mandatory"));
        }

        [Test]
        public void Scroll_snap_type_y_proximity_parses() {
            var cs = Compute("scroll-snap-type: y proximity;");
            Assert.That(cs.Get("scroll-snap-type"), Is.EqualTo("y proximity"));
        }

        [Test]
        public void Scroll_snap_type_both_mandatory_parses() {
            var cs = Compute("scroll-snap-type: both mandatory;");
            Assert.That(cs.Get("scroll-snap-type"), Is.EqualTo("both mandatory"));
        }

        // ---- scroll-snap-align ----

        [Test]
        public void Scroll_snap_align_start_parses() {
            var cs = Compute("scroll-snap-align: start;");
            Assert.That(cs.Get("scroll-snap-align"), Is.EqualTo("start"));
        }

        [Test]
        public void Scroll_snap_align_end_parses() {
            var cs = Compute("scroll-snap-align: end;");
            Assert.That(cs.Get("scroll-snap-align"), Is.EqualTo("end"));
        }

        [Test]
        public void Scroll_snap_align_center_parses() {
            var cs = Compute("scroll-snap-align: center;");
            Assert.That(cs.Get("scroll-snap-align"), Is.EqualTo("center"));
        }

        [Test]
        public void Scroll_snap_align_none_parses() {
            var cs = Compute("scroll-snap-align: none;");
            Assert.That(cs.Get("scroll-snap-align"), Is.EqualTo("none"));
        }

        // ---- scroll-behavior ----

        [Test]
        public void Scroll_behavior_auto_parses() {
            var cs = Compute("scroll-behavior: auto;");
            Assert.That(cs.Get("scroll-behavior"), Is.EqualTo("auto"));
        }

        [Test]
        public void Scroll_behavior_smooth_parses() {
            var cs = Compute("scroll-behavior: smooth;");
            Assert.That(cs.Get("scroll-behavior"), Is.EqualTo("smooth"));
        }
    }
}
