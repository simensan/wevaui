using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Scroll Snap Module Level 1 — longhand cascade coverage.
    //
    // ScrollSnapParseTests covers scroll-snap-type and scroll-snap-align at
    // parse level. The remaining seven Scroll Snap properties (scroll-snap-
    // stop, scroll-padding + 4 sides, scroll-margin + 4 sides) were
    // registered in ScrollSnap.cs:131-150 with their initial values but had
    // no direct cascade test.
    //
    // Spec references:
    //   §3 — scroll-snap-type
    //   §5 — scroll-snap-align
    //   §6 — scroll-snap-stop
    //   §7 — scroll-padding-*
    //   §8 — scroll-margin-*
    //
    // All seven are non-inherited per spec.
    public class ScrollSnapLonghandTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        static ComputedStyle Compute(string css) {
            // Make sure the lazy property-registration block has run before
            // the cascade — same hack the parse tests use.
            Weva.Layout.Scrolling.Snap.ScrollSnapProperties.EnsureRegistered();
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        static ComputedStyle ComputeChild(string css) {
            Weva.Layout.Scrolling.Snap.ScrollSnapProperties.EnsureRegistered();
            var doc = Html("<div><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("child"));
        }

        // ── scroll-snap-stop §6 ──────────────────────────────────────────

        [Test]
        public void Scroll_snap_stop_initial_is_normal() {
            // CSS Scroll Snap 1 §6: initial value `normal` (the scroll can
            // pass through this snap-area on a fast scroll).
            var cs = Compute("");
            Assert.That(cs.Get("scroll-snap-stop"), Is.EqualTo("normal"));
        }

        [Test]
        public void Scroll_snap_stop_always_round_trips() {
            // §6: `always` forces the scroll to stop at this snap-area even
            // for a high-momentum fling.
            var cs = Compute("#x { scroll-snap-stop: always; }");
            Assert.That(cs.Get("scroll-snap-stop"), Is.EqualTo("always"));
        }

        [Test]
        public void Scroll_snap_stop_does_not_inherit() {
            var cs = ComputeChild("div { scroll-snap-stop: always; }");
            Assert.That(cs.Get("scroll-snap-stop"), Is.EqualTo("normal"),
                "scroll-snap-stop is non-inherited; child must see initial `normal`");
        }

        // ── scroll-padding-* §7 ──────────────────────────────────────────

        [Test]
        public void Scroll_padding_initial_is_auto() {
            // §7: initial = auto (UA picks an offset, typically 0).
            var cs = Compute("");
            Assert.That(cs.Get("scroll-padding"), Is.EqualTo("auto"));
        }

        [Test]
        public void Scroll_padding_top_initial_is_auto() {
            var cs = Compute("");
            Assert.That(cs.Get("scroll-padding-top"), Is.EqualTo("auto"));
        }

        [Test]
        public void Scroll_padding_shorthand_expands_to_all_four_sides() {
            // CSS Scroll Snap 1 §7: scroll-padding is a 1-4-value shorthand
            // for the four side longhands. A single value sets all four;
            // four values set them in the standard CSS top/right/bottom/
            // left order.
            var cs = Compute("#x { scroll-padding: 12px; }");
            Assert.That(cs.Get("scroll-padding-top"), Is.EqualTo("12px"));
            Assert.That(cs.Get("scroll-padding-right"), Is.EqualTo("12px"));
            Assert.That(cs.Get("scroll-padding-bottom"), Is.EqualTo("12px"));
            Assert.That(cs.Get("scroll-padding-left"), Is.EqualTo("12px"));
        }

        [Test]
        public void Scroll_padding_shorthand_with_four_values_assigns_per_side() {
            // §7: standard top/right/bottom/left order matches the
            // background-position / margin shorthand convention.
            var cs = Compute("#x { scroll-padding: 1px 2px 3px 4px; }");
            Assert.That(cs.Get("scroll-padding-top"), Is.EqualTo("1px"));
            Assert.That(cs.Get("scroll-padding-right"), Is.EqualTo("2px"));
            Assert.That(cs.Get("scroll-padding-bottom"), Is.EqualTo("3px"));
            Assert.That(cs.Get("scroll-padding-left"), Is.EqualTo("4px"));
        }

        [Test]
        public void Scroll_padding_per_side_round_trips() {
            // Individual side longhands cascade independently.
            var cs = Compute(
                "#x { scroll-padding-top: 10px; scroll-padding-right: 20px; " +
                "scroll-padding-bottom: 30px; scroll-padding-left: 40px; }");
            Assert.That(cs.Get("scroll-padding-top"), Is.EqualTo("10px"));
            Assert.That(cs.Get("scroll-padding-right"), Is.EqualTo("20px"));
            Assert.That(cs.Get("scroll-padding-bottom"), Is.EqualTo("30px"));
            Assert.That(cs.Get("scroll-padding-left"), Is.EqualTo("40px"));
        }

        [Test]
        public void Scroll_padding_does_not_inherit() {
            var cs = ComputeChild("div { scroll-padding: 16px; }");
            Assert.That(cs.Get("scroll-padding"), Is.EqualTo("auto"),
                "scroll-padding is non-inherited; child sees the initial `auto`");
        }

        // ── scroll-margin-* §8 ───────────────────────────────────────────

        [Test]
        public void Scroll_margin_initial_is_zero() {
            // §8: initial = 0 (snap-area outset offset is zero by default).
            // Differs from scroll-padding's `auto` initial.
            var cs = Compute("");
            Assert.That(cs.Get("scroll-margin"), Is.EqualTo("0"));
        }

        [Test]
        public void Scroll_margin_top_initial_is_zero() {
            var cs = Compute("");
            Assert.That(cs.Get("scroll-margin-top"), Is.EqualTo("0"));
        }

        [Test]
        public void Scroll_margin_shorthand_expands_negative_values_to_all_sides() {
            // CSS Scroll Snap 1 §8: scroll-margin allows negative values
            // (the snap-area extends past the box outline). The 1-4-value
            // shorthand expands across the four sides like margin.
            var cs = Compute("#x { scroll-margin: -8px; }");
            Assert.That(cs.Get("scroll-margin-top"), Is.EqualTo("-8px"));
            Assert.That(cs.Get("scroll-margin-right"), Is.EqualTo("-8px"));
            Assert.That(cs.Get("scroll-margin-bottom"), Is.EqualTo("-8px"));
            Assert.That(cs.Get("scroll-margin-left"), Is.EqualTo("-8px"));
        }

        [Test]
        public void Scroll_margin_per_side_round_trips() {
            var cs = Compute(
                "#x { scroll-margin-top: 5px; scroll-margin-right: 15px; " +
                "scroll-margin-bottom: 25px; scroll-margin-left: 35px; }");
            Assert.That(cs.Get("scroll-margin-top"), Is.EqualTo("5px"));
            Assert.That(cs.Get("scroll-margin-right"), Is.EqualTo("15px"));
            Assert.That(cs.Get("scroll-margin-bottom"), Is.EqualTo("25px"));
            Assert.That(cs.Get("scroll-margin-left"), Is.EqualTo("35px"));
        }

        [Test]
        public void Scroll_margin_does_not_inherit() {
            var cs = ComputeChild("div { scroll-margin: 20px; }");
            Assert.That(cs.Get("scroll-margin"), Is.EqualTo("0"),
                "scroll-margin is non-inherited; child sees the initial `0`");
        }
    }
}
