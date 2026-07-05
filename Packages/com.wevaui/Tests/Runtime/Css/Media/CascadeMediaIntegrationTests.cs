using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Media;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Media {
    public class CascadeMediaIntegrationTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        [Test]
        public void Min_width_rule_applies_when_viewport_meets_threshold() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(
                new[] { Author("@media (min-width: 600px) { #x { color: red; } }") },
                MediaContext.Default(800, 600));
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Min_width_rule_does_not_apply_when_viewport_below_threshold() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(
                new[] { Author("@media (min-width: 600px) { #x { color: red; } }") },
                MediaContext.Default(400, 600));
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("black"));
        }

        [Test]
        public void Default_rule_and_media_rule_combine_when_both_apply() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(
                new[] { Author(
                    "#x { color: green; font-size: 12px; }" +
                    "@media (min-width: 600px) { #x { color: red; } }") },
                MediaContext.Default(800, 600));
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
            Assert.That(cs.Get("font-size"), Is.EqualTo("12px"));
        }

        [Test]
        public void Switching_media_context_between_calls_changes_outcome() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(
                new[] { Author("@media (min-width: 600px) { #x { color: red; } }") },
                MediaContext.Default(400, 600));
            var before = engine.Compute(doc.GetElementById("x"));
            Assert.That(before.Get("color"), Is.EqualTo("black"));

            long versionBefore = engine.MediaContextVersion;
            engine.MediaContext = MediaContext.Default(800, 600);
            Assert.That(engine.MediaContextVersion, Is.GreaterThan(versionBefore));

            var after = engine.Compute(doc.GetElementById("x"));
            Assert.That(after.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Top_level_or_applies_when_either_alternative_matches() {
            var doc = Html("<div id=\"x\"></div>");
            string sheet = "@media (min-width: 600px), (orientation: portrait) { #x { color: red; } }";

            var landscapeBig = new CascadeEngine(
                new[] { Author(sheet) }, MediaContext.Default(800, 600));
            Assert.That(landscapeBig.Compute(doc.GetElementById("x")).Get("color"), Is.EqualTo("red"));

            var portraitSmall = new CascadeEngine(
                new[] { Author(sheet) }, MediaContext.Default(400, 800));
            Assert.That(portraitSmall.Compute(doc.GetElementById("x")).Get("color"), Is.EqualTo("red"));

            var landscapeSmall = new CascadeEngine(
                new[] { Author(sheet) }, MediaContext.Default(500, 400));
            Assert.That(landscapeSmall.Compute(doc.GetElementById("x")).Get("color"), Is.EqualTo("black"));
        }

        [Test]
        public void Not_query_reverses_inclusion() {
            var doc = Html("<div id=\"x\"></div>");
            string sheet = "@media not (min-width: 600px) { #x { color: red; } }";

            var big = new CascadeEngine(new[] { Author(sheet) }, MediaContext.Default(800, 600));
            Assert.That(big.Compute(doc.GetElementById("x")).Get("color"), Is.EqualTo("black"));

            var small = new CascadeEngine(new[] { Author(sheet) }, MediaContext.Default(400, 600));
            Assert.That(small.Compute(doc.GetElementById("x")).Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Default_context_keeps_legacy_always_apply_behavior() {
            // A 9999px threshold should still match the implicit default surface so that
            // tests written before the evaluator existed continue to pass.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("@media (min-width: 9999px) { #x { color: red; } }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Media_max_width_excludes_rule_above_viewport() {
            // Audit regression: `@media (max-width: 600px)` MUST drop its inner
            // rules when the viewport's width is wider than 600px. Pairs with the
            // existing min-width coverage above to lock both ends of the range.
            var doc = Html("<div id=\"x\"></div>");
            string sheet = "@media (max-width: 600px) { #x { color: red; } }";

            var wide = new CascadeEngine(new[] { Author(sheet) }, MediaContext.Default(800, 600));
            Assert.That(wide.Compute(doc.GetElementById("x")).Get("color"), Is.EqualTo("black"));

            var narrow = new CascadeEngine(new[] { Author(sheet) }, MediaContext.Default(400, 600));
            Assert.That(narrow.Compute(doc.GetElementById("x")).Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Initial_media_context_version_is_nonzero_and_bumps_on_set() {
            var engine = new CascadeEngine(System.Array.Empty<OriginatedStylesheet>());
            long v0 = engine.MediaContextVersion;
            engine.MediaContext = MediaContext.Default(1024, 768);
            Assert.That(engine.MediaContextVersion, Is.GreaterThan(v0));
            engine.MediaContext = MediaContext.Default(640, 480);
            Assert.That(engine.MediaContextVersion, Is.GreaterThan(v0 + 1));
        }

        [Test]
        public void Viewport_resize_only_bumps_media_version_when_query_match_set_changes() {
            var engine = new CascadeEngine(
                new[] { Author("@media (min-width: 600px) { #x { color: red; } }") },
                MediaContext.Default(800, 600));
            long v0 = engine.MediaContextVersion;

            bool stableWideChanged = engine.SetMediaContextForViewportResize(MediaContext.Default(900, 600));
            Assert.That(stableWideChanged, Is.False);
            Assert.That(engine.MediaContextVersion, Is.EqualTo(v0));

            bool crossedChanged = engine.SetMediaContextForViewportResize(MediaContext.Default(500, 600));
            Assert.That(crossedChanged, Is.True);
            Assert.That(engine.MediaContextVersion, Is.GreaterThan(v0));

            long v1 = engine.MediaContextVersion;
            bool stableNarrowChanged = engine.SetMediaContextForViewportResize(MediaContext.Default(400, 600));
            Assert.That(stableNarrowChanged, Is.False);
            Assert.That(engine.MediaContextVersion, Is.EqualTo(v1));
        }

        // Audit CX4 (2026-07): SetMediaContextForViewportResize bumped
        // mediaContextVersion but — unlike SetMediaContext/BumpEnvironmentVersion —
        // did not clear shapeCache/matchedPropsCache. Media filtering happens
        // BEFORE match lists are cached, so after a breakpoint crossing every
        // element cache-missed on the bumped version but was served the stale
        // match set by shape key: @media styles froze at the pre-resize
        // breakpoint for the rest of the session. Production path:
        // WevaDocument.ApplyViewportSize.
        [Test]
        public void Viewport_resize_crossing_a_breakpoint_recomputes_media_styles() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(
                new[] { Author("@media (min-width: 1000px) { div { color: red; } }") },
                MediaContext.Default(1200, 800));
            var x = doc.GetElementById("x");

            Assert.That(engine.Compute(x).Get("color"), Is.EqualTo("red"),
                "sanity: rule active at 1200px");

            bool crossed = engine.SetMediaContextForViewportResize(MediaContext.Default(500, 800));
            Assert.That(crossed, Is.True, "sanity: 1200 -> 500 crosses the 1000px breakpoint");

            Assert.That(engine.Compute(x).Get("color"), Is.EqualTo("black"),
                "min-width:1000px rule must deactivate after resizing to 500px — a stale " +
                "shape-cache match set freezes @media styles at the old breakpoint (audit CX4)");

            // And back up: re-activation must work symmetrically.
            bool crossedBack = engine.SetMediaContextForViewportResize(MediaContext.Default(1400, 800));
            Assert.That(crossedBack, Is.True);
            Assert.That(engine.Compute(x).Get("color"), Is.EqualTo("red"),
                "rule must re-activate after resizing back above the breakpoint");
        }
    }
}
