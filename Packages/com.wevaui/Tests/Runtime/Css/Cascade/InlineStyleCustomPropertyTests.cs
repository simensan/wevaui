using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // Covers the inline `style="..."` x CSS custom-property interaction exercised by
    // the randhtml demo (`<div style="--pct:78%">` consumed by `width: var(--pct)`).
    // The flow has four moving parts that all have to line up:
    //   1. CssParser.Parse (via CascadeEngine.ParseInlineStyle) must tokenize
    //      `--pct: 78%` as a Declaration with Property == "--pct".
    //   2. AddInlineDeclarations must inject those declarations into the cascade
    //      with a high-source-order MatchedDeclaration so they win.
    //   3. ComputeFor seeds custom properties first, then inherits any unseen
    //      customs from parentStyle, so var() lookups on this element resolve
    //      both own-element customs and inherited customs.
    //   4. VariableResolver.Resolve walks `style.TryGet(name)` which already
    //      contains the inherited customs by the time non-custom var()s resolve.
    public class InlineStyleCustomPropertyTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        [Test]
        public void Inline_style_sets_custom_property() {
            var doc = Html("<div id=\"x\" style=\"--pct: 78%\"></div>");
            var engine = new CascadeEngine(System.Array.Empty<OriginatedStylesheet>());
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("--pct"), Is.EqualTo("78%"));
        }

        [Test]
        public void Custom_property_inherits_from_parent() {
            var doc = Html("<a><b><c><d id=\"x\"></d></c></b></a>");
            var engine = new CascadeEngine(new[] {
                Author("a { --x: 50px; } #x { width: var(--x); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("width"), Is.EqualTo("50px"));
        }

        [Test]
        public void Inline_style_var_overrides_stylesheet() {
            // The matching demo shape: a CSS rule sets `width: var(--pct, 100%)`
            // and the inline style on the same element provides `--pct: 50%`.
            // The inline-set custom property must beat the rule's fallback.
            var doc = Html("<div id=\"x\" style=\"--pct: 50%\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { width: var(--pct, 100%); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("width"), Is.EqualTo("50%"));
        }

        [Test]
        public void Inline_custom_property_visible_to_var_with_fallback_when_unset_elsewhere() {
            // Like the bar/.fill pattern: CSS targets a child via descendant
            // combinator while the child's inline style provides --pct.
            var doc = Html(
                "<div class=\"bar\">" +
                "  <div class=\"fill\" id=\"x\" style=\"--pct: 78%\"></div>" +
                "</div>");
            var engine = new CascadeEngine(new[] {
                Author(".bar > .fill { width: var(--pct, 100%); height: 100%; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("width"), Is.EqualTo("78%"));
        }

        [Test]
        public void Inline_custom_property_beats_stylesheet_custom_property_on_same_element() {
            // Specificity check at the custom-property axis: a stylesheet rule
            // sets --pct on the element AND inline style sets a different value.
            // Inline always wins (CompareForCascade x.IsInline tiebreak).
            var doc = Html("<div id=\"x\" class=\"fill\" style=\"--pct: 50%\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(".fill { --pct: 100%; width: var(--pct); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("--pct"), Is.EqualTo("50%"));
            Assert.That(cs.Get("width"), Is.EqualTo("50%"));
        }

        [Test]
        public void Root_custom_property_visible_via_inheritance() {
            // The demo's `:root { --panel: #232730; }` pattern: a deep descendant
            // with no own --panel reads it through the inherited cascade.
            var doc = Html("<div id=\"shell\"><section><div id=\"x\"></div></section></div>");
            var engine = new CascadeEngine(new[] {
                Author("#shell { --panel: #232730; } #x { background-color: var(--panel); }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("background-color"), Is.EqualTo("#232730"));
        }
    }
}
