using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.DevTools;
using Weva.Dom;
using static Weva.Tests.Layout.LayoutTestHelpers;

// W7 DevTools: assert that the cascade trace shows the human-readable selector
// text for matched rules. Tests pair with SelectorSourceTextTests.cs which
// checks the lower-level CompiledSelector.SourceText storage.
//
// Test taxonomy:
//   - Simple class/type/id selectors → selector text in WinnerSelectorText.
//   - Complex selector with combinator ("> ") → full complex text preserved.
//   - Selector list (comma-separated) → each compiled selector carries its
//     own slice, so the winning slice is shown, not the full comma list.
//   - Multiple rules → loser's SelectorText surfaced in OverriddenDeclaration.
//   - Inline style → WinnerSelectorText is null (no selector).
//   - ToString() → selector text appears in the dump string.
//
// NUnit pitfalls avoided:
//   - Does.Not.Contain is substring-only on strings (ok here — we use it
//     for substring checks which is correct usage).
//   - Within does not chain off comparisons other than EqualTo.

namespace Weva.Tests.DevTools {
    public class CascadeTraceSelectorTextTests {
        [SetUp]
        public void SetUp() {
            StyleInspector.CaptureCascadeTrace = true;
        }

        [TearDown]
        public void TearDown() {
            StyleInspector.CaptureCascadeTrace = false;
        }

        // ------------------------------------------------------------------ //
        //  Simple class selector                                               //
        // ------------------------------------------------------------------ //

        [Test]
        public void Trace_winner_selector_text_class_selector() {
            var css  = ".box { color: blue; }";
            var html = "<div class=\"box\" id=\"d\">hi</div>";

            var (element, style, engine) = GetElementStyleEngine("d", html, css);

            var report = StyleInspector.Dump(element, style, null, engine);

            Assert.That(report.CascadeTrace.TryGetValue("color", out var trace), Is.True,
                "color should appear in cascade trace");
            Assert.That(trace.WinnerSelectorText, Is.EqualTo(".box"),
                "class selector text should be '.box'");
        }

        // ------------------------------------------------------------------ //
        //  Type selector                                                       //
        // ------------------------------------------------------------------ //

        [Test]
        public void Trace_winner_selector_text_type_selector() {
            var css  = "div { background-color: red; }";
            var html = "<div id=\"d\">hi</div>";

            var (element, style, engine) = GetElementStyleEngine("d", html, css);

            var report = StyleInspector.Dump(element, style, null, engine);

            Assert.That(report.CascadeTrace.TryGetValue("background-color", out var trace), Is.True);
            Assert.That(trace.WinnerSelectorText, Is.EqualTo("div"),
                "type selector text should be 'div'");
        }

        // ------------------------------------------------------------------ //
        //  ID selector                                                         //
        // ------------------------------------------------------------------ //

        [Test]
        public void Trace_winner_selector_text_id_selector() {
            var css  = "#hero { opacity: 0.5; }";
            var html = "<div id=\"hero\">hi</div>";

            var (element, style, engine) = GetElementStyleEngine("hero", html, css);

            var report = StyleInspector.Dump(element, style, null, engine);

            Assert.That(report.CascadeTrace.TryGetValue("opacity", out var trace), Is.True);
            Assert.That(trace.WinnerSelectorText, Is.EqualTo("#hero"),
                "id selector text should be '#hero'");
        }

        // ------------------------------------------------------------------ //
        //  Complex selector with child combinator                              //
        // ------------------------------------------------------------------ //

        [Test]
        public void Trace_winner_selector_text_complex_child_combinator() {
            var css  = ".card > .title { color: green; }";
            var html = "<div class=\"card\"><span class=\"title\" id=\"t\">hi</span></div>";

            var (element, style, engine) = GetElementStyleEngine("t", html, css);

            var report = StyleInspector.Dump(element, style, null, engine);

            Assert.That(report.CascadeTrace.TryGetValue("color", out var trace), Is.True);
            Assert.That(trace.WinnerSelectorText, Is.EqualTo(".card > .title"),
                "complex selector with child combinator should retain full text");
        }

        // ------------------------------------------------------------------ //
        //  Complex selector with descendant combinator                         //
        // ------------------------------------------------------------------ //

        [Test]
        public void Trace_winner_selector_text_complex_descendant_combinator() {
            var css  = ".wrapper .label { color: purple; }";
            var html = "<div class=\"wrapper\"><span class=\"label\" id=\"l\">hi</span></div>";

            var (element, style, engine) = GetElementStyleEngine("l", html, css);

            var report = StyleInspector.Dump(element, style, null, engine);

            Assert.That(report.CascadeTrace.TryGetValue("color", out var trace), Is.True);
            Assert.That(trace.WinnerSelectorText, Is.EqualTo(".wrapper .label"),
                "descendant combinator selector should retain full text");
        }

        // ------------------------------------------------------------------ //
        //  Selector list — each rule carries its own slice                    //
        // ------------------------------------------------------------------ //

        [Test]
        public void Trace_selector_list_rule_winner_carries_its_slice_not_full_list() {
            // Two selectors in a comma list. The .target rule matches our element.
            // The other rule (.other) does not match. The winner's SelectorText
            // should be the slice for our element's matching selector only.
            //
            // CSS parser expands "h1, .target { color: teal }" into two entries
            // in StyleRule.Selectors: ["h1", ".target"]. CascadeEngine compiles
            // each separately. So WinnerSelectorText will be ".target", not
            // "h1, .target".
            var css  = "h1, .target { color: teal; }";
            var html = "<div class=\"target\" id=\"d\">hi</div>";

            var (element, style, engine) = GetElementStyleEngine("d", html, css);

            var report = StyleInspector.Dump(element, style, null, engine);

            Assert.That(report.CascadeTrace.TryGetValue("color", out var trace), Is.True);
            // The matched selector is ".target", not "h1, .target"
            Assert.That(trace.WinnerSelectorText, Is.EqualTo(".target"),
                "selector list: winner should carry only the matching slice");
            // Ensure "h1" is not in the winner text (it's the other branch)
            Assert.That(trace.WinnerSelectorText, Does.Not.Contain("h1"),
                "the full comma list should not appear in the winner selector text");
        }

        // ------------------------------------------------------------------ //
        //  Overridden declaration also carries selector text                  //
        // ------------------------------------------------------------------ //

        [Test]
        public void Trace_overridden_declaration_carries_selector_text() {
            // Two author rules: lower specificity (div) loses to higher (.box).
            var css  = "div { color: red; } .box { color: blue; }";
            var html = "<div class=\"box\" id=\"d\">hi</div>";

            var (element, style, engine) = GetElementStyleEngine("d", html, css);

            var report = StyleInspector.Dump(element, style, null, engine);

            Assert.That(report.CascadeTrace.TryGetValue("color", out var trace), Is.True);
            Assert.That(trace.WinnerSelectorText, Is.EqualTo(".box"),
                "winner selector should be '.box'");
            Assert.That(trace.OverriddenDeclarations, Is.Not.Null,
                "overridden list should exist");

            // Find the losing 'div { color:red }' in overridden entries.
            bool foundDivSelector = false;
            foreach (var ov in trace.OverriddenDeclarations) {
                if (ov.SelectorText == "div" && ov.ValueText == "red") {
                    foundDivSelector = true;
                    break;
                }
            }
            Assert.That(foundDivSelector, Is.True,
                "overridden declarations must include 'div' selector text for the losing rule");
        }

        // ------------------------------------------------------------------ //
        //  Inline style — WinnerSelectorText is null                          //
        // ------------------------------------------------------------------ //

        [Test]
        public void Trace_inline_style_winner_selector_text_is_null() {
            // Inline style wins over authored rule by cascade precedence.
            var css  = ".box { color: blue; }";
            var html = "<div class=\"box\" id=\"d\" style=\"color:red\">hi</div>";

            var (element, style, engine) = GetElementStyleEngine("d", html, css);

            var report = StyleInspector.Dump(element, style, null, engine);

            Assert.That(report.CascadeTrace.TryGetValue("color", out var trace), Is.True);
            // Inline style has no selector.
            Assert.That(trace.WinnerSelectorText, Is.Null,
                "inline style winner should have null selector text");
        }

        // ------------------------------------------------------------------ //
        //  Pseudo-class in selector text                                       //
        // ------------------------------------------------------------------ //

        [Test]
        public void Trace_winner_selector_text_with_pseudo_class() {
            // :first-child matches the element; text should include ":first-child".
            var css  = "div:first-child { color: orange; }";
            var html = "<div id=\"d\">hi</div>";

            var (element, style, engine) = GetElementStyleEngine("d", html, css);

            var report = StyleInspector.Dump(element, style, null, engine);

            Assert.That(report.CascadeTrace.TryGetValue("color", out var trace), Is.True);
            Assert.That(trace.WinnerSelectorText, Is.EqualTo("div:first-child"),
                "selector with pseudo-class should retain the pseudo-class in text");
        }

        // ------------------------------------------------------------------ //
        //  ToString() output includes selector text                           //
        // ------------------------------------------------------------------ //

        [Test]
        public void ToString_cascade_section_shows_selector_text() {
            var css  = ".card { color: navy; }";
            var html = "<div class=\"card\" id=\"d\">hi</div>";

            var (element, style, engine) = GetElementStyleEngine("d", html, css);

            var report = StyleInspector.Dump(element, style, null, engine);
            var text   = report.ToString();

            // The cascade section should show the selector text ".card" in the dump.
            Assert.That(text, Does.Contain(".card"),
                "ToString dump should include the matching selector text");
        }

        [Test]
        public void ToString_cascade_section_shows_selector_text_before_specificity() {
            var css  = ".box { opacity: 0.8; }";
            var html = "<div class=\"box\" id=\"d\">hi</div>";

            var (element, style, engine) = GetElementStyleEngine("d", html, css);

            var report = StyleInspector.Dump(element, style, null, engine);
            var text   = report.ToString();

            // The dump must contain both the selector and "spec=" (in that order).
            int selectorPos   = text.IndexOf(".box");
            int specificityPos = text.IndexOf("spec=");
            Assert.That(selectorPos,    Is.GreaterThanOrEqualTo(0), ".box must appear in dump");
            Assert.That(specificityPos, Is.GreaterThanOrEqualTo(0), "spec= must appear in dump");
            Assert.That(selectorPos, Is.LessThan(specificityPos),
                "selector text should appear before specificity in the dump");
        }

        [Test]
        public void ToString_cascade_overridden_entry_shows_selector_text() {
            var css  = "div { color: red; } .box { color: blue; }";
            var html = "<div class=\"box\" id=\"d\">hi</div>";

            var (element, style, engine) = GetElementStyleEngine("d", html, css);

            var report = StyleInspector.Dump(element, style, null, engine);
            var text   = report.ToString();

            // The losing "div" rule should appear somewhere in the overridden section.
            Assert.That(text, Does.Contain("div"),
                "overridden section should contain the losing selector 'div'");
            Assert.That(text, Does.Contain(".box"),
                "winning section should contain the selector '.box'");
        }

        // ------------------------------------------------------------------ //
        //  Attribute selector in selector text                                 //
        // ------------------------------------------------------------------ //

        [Test]
        public void Trace_winner_selector_text_with_attribute_selector() {
            var css  = "[data-theme=\"dark\"] { color: white; }";
            var html = "<div data-theme=\"dark\" id=\"d\">hi</div>";

            var (element, style, engine) = GetElementStyleEngine("d", html, css);

            var report = StyleInspector.Dump(element, style, null, engine);

            Assert.That(report.CascadeTrace.TryGetValue("color", out var trace), Is.True);
            // Selector text should include the attribute selector.
            Assert.That(trace.WinnerSelectorText, Does.Contain("data-theme"),
                "attribute selector should retain attribute name in source text");
        }

        // ------------------------------------------------------------------ //
        //  Helper                                                              //
        // ------------------------------------------------------------------ //

        static (Element, ComputedStyle, CascadeEngine) GetElementStyleEngine(
            string id, string html, string css) {

            var doc    = Html(html);
            var sheets = new System.Collections.Generic.List<OriginatedStylesheet> {
                UA(BuiltinUserAgent),
                Author(css)
            };
            var engine = new CascadeEngine(sheets);
            var styleMap = new System.Collections.Generic.Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styleMap[kv.Key] = kv.Value;

            Element foundEl = null;
            ComputedStyle foundStyle = null;
            foreach (var kv in styleMap) {
                if (kv.Key.Id == id) { foundEl = kv.Key; foundStyle = kv.Value; break; }
            }
            Assert.That(foundEl, Is.Not.Null, $"element with id='{id}' must be found");
            return (foundEl, foundStyle, engine);
        }
    }
}
