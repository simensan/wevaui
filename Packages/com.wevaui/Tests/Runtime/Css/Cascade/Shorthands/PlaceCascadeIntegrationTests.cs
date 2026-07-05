using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade.Shorthands {
    // CSS Box Alignment L3 — place-* shorthand cascade-integration tests.
    //
    // PlaceShorthandTests exercises the PlaceShorthandExpander in isolation.
    // These tests run the full cascade pipeline so they verify that the
    // shorthand is registered, expands correctly, and that individual
    // longhand overrides take precedence per the cascade ordering rules.
    //
    // Coverage:
    //   place-content  — 1-value and 2-value forms via cascade
    //   place-items    — 1-value and 2-value forms via cascade
    //   place-self     — 1-value and 2-value forms via cascade
    //   longhand override of a place-* shorthand
    //   non-inheritance of place-* longhands
    public class PlaceCascadeIntegrationTests {
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

        // ══════════════════════════════════════════════════════════════════
        // place-content
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Place_content_one_value_sets_both_longhands_via_cascade() {
            // 1-value form: both align-content and justify-content get the
            // same value through the cascade expansion path.
            var cs = Compute("#x { place-content: center; }");
            Assert.That(cs.Get("align-content"), Is.EqualTo("center"));
            Assert.That(cs.Get("justify-content"), Is.EqualTo("center"));
        }

        [Test]
        public void Place_content_two_values_via_cascade() {
            // 2-value form: first token = align-content (block axis),
            // second = justify-content (inline axis).
            var cs = Compute("#x { place-content: space-between end; }");
            Assert.That(cs.Get("align-content"), Is.EqualTo("space-between"));
            Assert.That(cs.Get("justify-content"), Is.EqualTo("end"));
        }

        [Test]
        public void Place_content_stretch_one_value_via_cascade() {
            var cs = Compute("#x { place-content: stretch; }");
            Assert.That(cs.Get("align-content"), Is.EqualTo("stretch"));
            Assert.That(cs.Get("justify-content"), Is.EqualTo("stretch"));
        }

        [Test]
        public void Align_content_longhand_overrides_place_content_shorthand() {
            // A later longhand must win over an earlier shorthand per cascade.
            var cs = Compute("#x { place-content: start; align-content: end; }");
            Assert.That(cs.Get("align-content"), Is.EqualTo("end"),
                "longhand declared after shorthand must win");
            Assert.That(cs.Get("justify-content"), Is.EqualTo("start"),
                "justify-content was only set by the shorthand");
        }

        // ══════════════════════════════════════════════════════════════════
        // place-items
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Place_items_one_value_sets_both_longhands_via_cascade() {
            var cs = Compute("#x { place-items: stretch; }");
            Assert.That(cs.Get("align-items"), Is.EqualTo("stretch"));
            Assert.That(cs.Get("justify-items"), Is.EqualTo("stretch"));
        }

        [Test]
        public void Place_items_two_values_via_cascade() {
            var cs = Compute("#x { place-items: start end; }");
            Assert.That(cs.Get("align-items"), Is.EqualTo("start"));
            Assert.That(cs.Get("justify-items"), Is.EqualTo("end"));
        }

        [Test]
        public void Place_items_baseline_via_cascade() {
            // `baseline` is a valid align-items keyword.
            var cs = Compute("#x { place-items: baseline; }");
            Assert.That(cs.Get("align-items"), Is.EqualTo("baseline"));
            Assert.That(cs.Get("justify-items"), Is.EqualTo("baseline"));
        }

        [Test]
        public void Justify_items_longhand_overrides_place_items_shorthand() {
            var cs = Compute("#x { place-items: center; justify-items: end; }");
            Assert.That(cs.Get("align-items"), Is.EqualTo("center"));
            Assert.That(cs.Get("justify-items"), Is.EqualTo("end"),
                "longhand declared after shorthand must win");
        }

        // ══════════════════════════════════════════════════════════════════
        // place-self
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Place_self_one_value_sets_both_longhands_via_cascade() {
            var cs = Compute("#x { place-self: flex-end; }");
            Assert.That(cs.Get("align-self"), Is.EqualTo("flex-end"));
            Assert.That(cs.Get("justify-self"), Is.EqualTo("flex-end"));
        }

        [Test]
        public void Place_self_two_values_via_cascade() {
            // First token = align-self; second = justify-self.
            var cs = Compute("#x { place-self: center start; }");
            Assert.That(cs.Get("align-self"), Is.EqualTo("center"));
            Assert.That(cs.Get("justify-self"), Is.EqualTo("start"));
        }

        [Test]
        public void Place_self_auto_one_value_via_cascade() {
            // `auto` is the initial / "use parent's align-items" value.
            var cs = Compute("#x { place-self: auto; }");
            Assert.That(cs.Get("align-self"), Is.EqualTo("auto"));
            Assert.That(cs.Get("justify-self"), Is.EqualTo("auto"));
        }

        [Test]
        public void Align_self_longhand_overrides_place_self_shorthand() {
            var cs = Compute("#x { place-self: stretch; align-self: center; }");
            Assert.That(cs.Get("align-self"), Is.EqualTo("center"),
                "longhand declared after shorthand must win");
            Assert.That(cs.Get("justify-self"), Is.EqualTo("stretch"));
        }

        // ══════════════════════════════════════════════════════════════════
        // Non-inheritance — longhands set by place-* are non-inherited
        // ══════════════════════════════════════════════════════════════════

        [Test]
        public void Longhands_set_by_place_items_do_not_inherit() {
            // place-items expands to align-items + justify-items, both
            // non-inherited; the child must see their initial values.
            var cs = ComputeChild("div { place-items: center; }");
            Assert.That(cs.Get("align-items"), Is.EqualTo("stretch"),
                "align-items initial is `stretch`; child must not inherit from parent");
            Assert.That(cs.Get("justify-items"), Is.EqualTo("legacy"),
                "justify-items initial is `legacy`; child must not inherit from parent");
        }
    }
}
