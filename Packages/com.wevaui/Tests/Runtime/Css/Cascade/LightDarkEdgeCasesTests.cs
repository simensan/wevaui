using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Media;
using Weva.Diagnostics;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // Extended coverage for light-dark() / color-scheme interactions.
    //
    // The base LightDarkResolverTests cover happy-path static-cascade
    // substitution (light scheme picks first arg, dark picks second).
    // This file pins runtime ColorScheme switching, inheritance, the
    // various color-value forms LightDarkResolver must pass through
    // verbatim, and the @media (prefers-color-scheme) integration.
    //
    // v1 notes: `color-scheme` is not (yet) a registered CSS property
    // in the engine — declarations spill into the unknown-property
    // side dictionary and are NOT consulted when LightDarkResolver
    // decides which arm of `light-dark()` wins. The single source of
    // truth is MediaContext.ColorScheme. The tests below ENCODE that
    // current behavior rather than the eventual spec-compliant one;
    // each such test carries a `// v1:` comment marking the gap.
    public class LightDarkEdgeCasesTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        static CascadeEngine BuildEngine(string css, ColorScheme scheme) {
            var sheets = new[] { Author(css) };
            var media = MediaContext.Default(800, 600).WithColorScheme(scheme);
            return new CascadeEngine(sheets, media);
        }

        // The engine emits an unknown-property warning when a stylesheet
        // declares `color-scheme: ...`. The diagnostic side-channel is
        // independent of correctness of the cascade output, so we silence
        // it for the duration of each test; otherwise LogAssert's policy
        // would turn the warning into a test failure.
        bool diagnosticsWereEnabled;

        [SetUp]
        public void DisableDiagnostics() {
            diagnosticsWereEnabled = UICssDiagnostics.Enabled;
            UICssDiagnostics.Enabled = false;
            UICssDiagnostics.ResetForTests();
        }

        [TearDown]
        public void RestoreDiagnostics() {
            UICssDiagnostics.Enabled = diagnosticsWereEnabled;
        }

        // ---------------------------------------------------------------
        // color-scheme cascade
        // ---------------------------------------------------------------

        [Test]
        public void Color_scheme_normal_treats_as_light() {
            // v1: `color-scheme: normal` is parsed and stored as an unknown
            // property but does NOT influence light-dark() resolution.
            // LightDarkResolver consults MediaContext.ColorScheme only —
            // MediaContext.Default(...) is Light, so the light arg wins.
            var doc = Html("<div id=\"x\"></div>");
            var engine = BuildEngine(
                "#x { color-scheme: normal; color: light-dark(white, black); }",
                ColorScheme.Light);
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("white"));
        }

        [Test]
        public void Color_scheme_dark_resolves_to_dark_arg() {
            // v1: Author's `color-scheme: dark` declaration is ignored by
            // LightDarkResolver; the dark arg only wins because the test
            // sets MediaContext.ColorScheme = Dark explicitly.
            var doc = Html("<div id=\"x\"></div>");
            var engine = BuildEngine(
                "#x { color-scheme: dark; color: light-dark(white, black); }",
                ColorScheme.Dark);
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("black"));
        }

        [Test]
        public void Color_scheme_light_dark_picks_based_on_user_preference() {
            // `color-scheme: light dark` declares the element supports both
            // schemes and lets the UA pick per user preference. In this
            // engine the user preference is encoded in MediaContext, which
            // here is Dark — so the dark arm wins.
            // v1: `color-scheme: light dark` itself is stored as unknown
            // and does not gate the resolution.
            var doc = Html("<div id=\"x\"></div>");
            var engine = BuildEngine(
                "#x { color-scheme: light dark; color: light-dark(white, black); }",
                ColorScheme.Dark);
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("black"));
        }

        [Test]
        public void Color_scheme_inherits_from_parent() {
            // Fixed in #257 follow-up: `color-scheme` is registered as
            // inherited, so FillInherited propagates it from parent to
            // child. The light-dark() resolver still consults
            // MediaContext.ColorScheme for the active scheme (the
            // per-element scheme override is the remaining gap, tracked
            // separately).
            var doc = Html("<div id=\"root\"><div id=\"child\"></div></div>");
            var engine = BuildEngine(
                "#root { color-scheme: dark; }" +
                "#child { color: light-dark(white, black); }",
                ColorScheme.Dark);

            var root = engine.Compute(doc.GetElementById("root"));
            Assert.That(root.Get("color-scheme"), Is.EqualTo("dark"),
                "color-scheme is stored on the element it was declared on.");

            var child = engine.Compute(doc.GetElementById("child"));
            Assert.That(child.Get("color-scheme"), Is.EqualTo("dark"),
                "color-scheme inherits from parent via the cascade.");
            // Light-dark resolution on the child still uses MediaContext.
            Assert.That(child.Get("color"), Is.EqualTo("black"));
        }

        [Test]
        public void Color_scheme_only_keyword_disables_alternates() {
            // Fixed in #257: `color-scheme: only dark` pins the scheme
            // regardless of MediaContext.ColorScheme. ResolveEffective-
            // ColorScheme in CascadeEngine reads the element's color-
            // scheme value and short-circuits the MediaContext fallback
            // when `only` is present.
            var doc = Html("<div id=\"x\"></div>");
            var engine = BuildEngine(
                "#x { color-scheme: only dark; color: light-dark(white, black); }",
                ColorScheme.Dark);
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("black"));

            // Flip MediaContext to Light: `only dark` keeps the dark arm.
            engine.MediaContext = engine.MediaContext.WithColorScheme(ColorScheme.Light);
            engine.InvalidateAll();
            var flipped = engine.Compute(doc.GetElementById("x"));
            Assert.That(flipped.Get("color"), Is.EqualTo("black"),
                "`color-scheme: only dark` pins the scheme; MediaContext flip is ignored.");
        }

        // ---------------------------------------------------------------
        // light-dark() with various color types
        // ---------------------------------------------------------------

        [Test]
        public void Light_dark_with_hex_args() {
            var doc = Html("<div id=\"x\"></div>");
            var light = BuildEngine("#x { color: light-dark(#fff, #000); }", ColorScheme.Light);
            Assert.That(light.Compute(doc.GetElementById("x")).Get("color"), Is.EqualTo("#fff"));

            var dark = BuildEngine("#x { color: light-dark(#fff, #000); }", ColorScheme.Dark);
            Assert.That(dark.Compute(doc.GetElementById("x")).Get("color"), Is.EqualTo("#000"));
        }

        [Test]
        public void Light_dark_with_rgb_args() {
            var doc = Html("<div id=\"x\"></div>");
            var light = BuildEngine(
                "#x { color: light-dark(rgb(255,255,255), rgb(0,0,0)); }",
                ColorScheme.Light);
            // Inner rgb() commas are inside parens; LightDarkResolver's
            // SplitTwoArgs ignores them. Whitespace adjacent to the outer
            // comma is preserved by Resolve so we trim equivalently.
            Assert.That(
                light.Compute(doc.GetElementById("x")).Get("color"),
                Is.EqualTo("rgb(255,255,255)"));

            var dark = BuildEngine(
                "#x { color: light-dark(rgb(255,255,255), rgb(0,0,0)); }",
                ColorScheme.Dark);
            Assert.That(
                dark.Compute(doc.GetElementById("x")).Get("color"),
                Is.EqualTo("rgb(0,0,0)"));
        }

        [Test]
        public void Light_dark_with_named_colors() {
            var doc = Html("<div id=\"x\"></div>");
            var light = BuildEngine("#x { color: light-dark(white, black); }", ColorScheme.Light);
            Assert.That(light.Compute(doc.GetElementById("x")).Get("color"), Is.EqualTo("white"));

            var dark = BuildEngine("#x { color: light-dark(white, black); }", ColorScheme.Dark);
            Assert.That(dark.Compute(doc.GetElementById("x")).Get("color"), Is.EqualTo("black"));
        }

        [Test]
        public void Light_dark_with_oklab_args() {
            var doc = Html("<div id=\"x\"></div>");
            var light = BuildEngine(
                "#x { color: light-dark(oklab(0.8 0 0), oklab(0.2 0 0)); }",
                ColorScheme.Light);
            Assert.That(
                light.Compute(doc.GetElementById("x")).Get("color"),
                Is.EqualTo("oklab(0.8 0 0)"));

            var dark = BuildEngine(
                "#x { color: light-dark(oklab(0.8 0 0), oklab(0.2 0 0)); }",
                ColorScheme.Dark);
            Assert.That(
                dark.Compute(doc.GetElementById("x")).Get("color"),
                Is.EqualTo("oklab(0.2 0 0)"));
        }

        [Test]
        public void Light_dark_inside_color_mix() {
            // LightDarkResolver substitutes the inner light-dark() call in
            // place; the outer color-mix() string then flows through the
            // cascade unchanged (color-mix is resolved later, at paint).
            var doc = Html("<div id=\"x\"></div>");
            var light = BuildEngine(
                "#x { color: color-mix(in srgb, light-dark(red, blue), white); }",
                ColorScheme.Light);
            Assert.That(
                light.Compute(doc.GetElementById("x")).Get("color"),
                Is.EqualTo("color-mix(in srgb, red, white)"));

            var dark = BuildEngine(
                "#x { color: color-mix(in srgb, light-dark(red, blue), white); }",
                ColorScheme.Dark);
            Assert.That(
                dark.Compute(doc.GetElementById("x")).Get("color"),
                Is.EqualTo("color-mix(in srgb, blue, white)"));
        }

        // ---------------------------------------------------------------
        // Inheritance of light-dark
        // ---------------------------------------------------------------

        [Test]
        public void Light_dark_resolved_color_inherits_normally() {
            // The parent's `color: light-dark(red, green)` is resolved at
            // the parent's cascade pass to whichever arm matches the
            // active scheme; the child has no own `color` rule so
            // FillInherited copies the parent's already-resolved string.
            var doc = Html("<div id=\"parent\"><span id=\"child\"></span></div>");
            var engine = BuildEngine(
                "#parent { color: light-dark(red, green); }",
                ColorScheme.Dark);
            var child = engine.Compute(doc.GetElementById("child"));
            Assert.That(child.Get("color"), Is.EqualTo("green"),
                "Child inherits the parent's already-resolved light-dark() arm.");
        }

        // ---------------------------------------------------------------
        // prefers-color-scheme media query interaction
        // ---------------------------------------------------------------

        [Test]
        public void Prefers_color_scheme_dark_matches_media_query() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = BuildEngine(
                "@media (prefers-color-scheme: dark) { #x { color: red; } }",
                ColorScheme.Dark);
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Prefers_color_scheme_light_matches_media_query() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = BuildEngine(
                "@media (prefers-color-scheme: light) { #x { color: red; } }",
                ColorScheme.Light);
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));

            // And it should miss when the scheme is Dark.
            engine.MediaContext = engine.MediaContext.WithColorScheme(ColorScheme.Dark);
            engine.InvalidateAll();
            var afterFlip = engine.Compute(doc.GetElementById("x"));
            Assert.That(afterFlip.Get("color"), Is.Not.EqualTo("red"),
                "Light-only @media rule must not apply in Dark scheme.");
        }
    }
}
