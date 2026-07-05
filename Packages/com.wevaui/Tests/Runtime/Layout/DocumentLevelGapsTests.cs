using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using Weva.Parsing;
using Weva.Text.TextCore;

namespace Weva.Tests.Layout {
    // Regression suite for the v0.8 audit's three actionable document-level
    // gaps: rem-against-cascaded-html-font-size, body→canvas background
    // propagation, and minimal @font-face → FontResolver bridge.
    public class DocumentLevelGapsTests {
        // We deliberately bypass the user-agent stylesheet (which sets
        // the root `font-size: 16px`) so the
        // tests can observe authoring overrides without UA noise.
        static (Document doc, Dictionary<Element, ComputedStyle> styles) BuildStyles(string html, string css) {
            var doc = HtmlParser.Parse(html);
            var sheets = new List<OriginatedStylesheet>();
            sheets.Add(OriginatedStylesheet.Author(CssParser.Parse(css)));
            var engine = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            return (doc, styles);
        }

        static Element FindByTag(Node n, string tag) {
            if (n is Element e && e.TagName == tag) return e;
            foreach (var c in n.Children) {
                var f = FindByTag(c, tag);
                if (f != null) return f;
            }
            return null;
        }

        [Test]
        public void Rem_resolves_against_author_overridden_html_font_size() {
            // Author overrides html font-size to 20px. Inner div uses `2rem`
            // for its width — should compute to 40px, not 32px (against the
            // construction-time default of 16px).
            var (doc, styles) = BuildStyles(
                "<html><body><div id=\"x\" style=\"width: 2rem\"></div></body></html>",
                "html { display: block; font-size: 20px; } body { display: block; } div { display: block; }");
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 800,
                ViewportHeightPx = 600,
                RootFontSizePx = 16, // start at default — engine should overwrite
                DpiPixelsPerInch = 96
            };
            var le = new LayoutEngine(new MonoFontMetrics(), useSnapshot: false);
            var root = le.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx);
            Assert.That(ctx.RootFontSizePx, Is.EqualTo(20).Within(1e-9),
                "LayoutEngine should propagate cascaded html font-size into LayoutContext");
            var xEl = doc.GetElementById("x");
            Box xBox = null;
            foreach (var b in LayoutTestHelpers.AllBoxes(root)) {
                if (b.Element == xEl) { xBox = b; break; }
            }
            Assert.That(xBox, Is.Not.Null);
            Assert.That(xBox.ContentWidth, Is.EqualTo(40).Within(1e-6),
                "2rem should resolve against the cascaded html font-size (20px), not the default 16px");
        }

        [Test]
        public void Body_background_propagates_to_canvas_when_html_has_none() {
            // Author rule: body has a red background; html has none. Per CSS
            // Backgrounds 3 §2.11.2, body's background propagates to the
            // canvas. We surface this by mirroring it onto html's
            // ComputedStyle so paint-side resolvers see it uniformly.
            var (doc, styles) = BuildStyles(
                "<html><body><div></div></body></html>",
                "html { display: block; } body { display: block; background-color: red; } div { display: block; }");
            var html = FindByTag(doc, "html");
            var body = FindByTag(doc, "body");
            Assert.That(html, Is.Not.Null);
            Assert.That(body, Is.Not.Null);

            // Pre-condition: cascade leaves html's background empty.
            Assert.That(styles[html].Get("background-color"), Is.EqualTo("transparent"));
            Assert.That(styles[body].Get("background-color"), Is.EqualTo("red"));

            // Building the box tree triggers BoxBuilder.PropagateBodyBackgroundToHtml.
            var bb = new BoxBuilder(e => styles.TryGetValue(e, out var cs) ? cs : null);
            bb.BuildDocument(doc);

            Assert.That(styles[html].Get("background-color"), Is.EqualTo("red"),
                "Body's background-color should be propagated onto html when html has none");
        }

        [Test]
        public void Font_face_at_rule_registers_font_with_resolver() {
            const string family = "TestRegressionFont";
            FontResolver.UnregisterFont(family);
            try {
                var sheet = CssParser.Parse(
                    "@font-face { font-family: 'TestRegressionFont'; src: url('Assets/Fonts/test.ttf'); }");

                // The parser should produce a FontFaceRule and register the
                // family with FontResolver as a side effect.
                Assert.That(sheet.Rules, Has.Count.EqualTo(1));
                var rule = sheet.Rules[0] as FontFaceRule;
                Assert.That(rule, Is.Not.Null, "@font-face should produce a FontFaceRule");
                Assert.That(rule.FontFamily, Is.EqualTo("TestRegressionFont"));
                Assert.That(rule.Src, Is.EqualTo("Assets/Fonts/test.ttf"));

                Assert.That(FontResolver.TryResolve(family, out var face), Is.True,
                    "FontResolver should resolve the family registered via @font-face");
                Assert.That(face.Path, Is.EqualTo("Assets/Fonts/test.ttf"));
            } finally {
                FontResolver.UnregisterFont(family);
            }
        }
    }
}
