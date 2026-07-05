using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Tables;
using Weva.Layout.Text;
using Weva.Parsing;

namespace Weva.Tests.Layout.Tables {
    // Regression coverage for CSS_COMPLIANCE_ISSUES.md I7: the spec initial
    // value of `border-spacing` is 0 (CSS 2.1 §17.6.1, CSS Tables L3 §7.1).
    // The 2px visible default for HTML `<table>` elements comes from the UA
    // stylesheet — never from a code-level fallback. Synthetic table boxes
    // (`<div style="display: table">`, anonymous tables, test fixtures that
    // bypass the cascade) must inherit 0, not the UA's 2px.
    public class BorderSpacingDefaultTests {
        static (Box root, Dictionary<Element, ComputedStyle> styles) BuildWithRealUA(
            string html, string authorCss = null, double viewportWidth = 800
        ) {
            var doc = HtmlParser.Parse(html);
            var sheets = new List<OriginatedStylesheet> { UserAgentStylesheet.Parse() };
            if (!string.IsNullOrEmpty(authorCss)) {
                sheets.Add(OriginatedStylesheet.Author(CssParser.Parse(authorCss)));
            }
            var engine = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = viewportWidth,
                ViewportHeightPx = 600,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96
            };
            var le = new LayoutEngine(new MonoFontMetrics());
            var root = le.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx);
            return (root, styles);
        }

        static (Box root, Dictionary<Element, ComputedStyle> styles) BuildWithMinimalUA(
            string html, string authorCss = null, double viewportWidth = 800
        ) {
            // Minimal UA sheet — deliberately omits the `table { border-spacing: 2px }`
            // rule. Any TableBox produced here exercises the spec initial value
            // path rather than the UA-default path.
            const string minimalUa = @"
                html, body, div, section, p { display: block; }
                body { margin: 0; padding: 0; }
            ";
            var doc = HtmlParser.Parse(html);
            var sheets = new List<OriginatedStylesheet> {
                OriginatedStylesheet.UserAgent(CssParser.Parse(minimalUa))
            };
            if (!string.IsNullOrEmpty(authorCss)) {
                sheets.Add(OriginatedStylesheet.Author(CssParser.Parse(authorCss)));
            }
            var engine = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = viewportWidth,
                ViewportHeightPx = 600,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96
            };
            var le = new LayoutEngine(new MonoFontMetrics());
            var root = le.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx);
            return (root, styles);
        }

        static IEnumerable<Box> Walk(Box root) {
            yield return root;
            foreach (var c in root.Children) {
                foreach (var d in Walk(c)) yield return d;
            }
        }

        static T FindFirst<T>(Box root) where T : Box {
            foreach (var b in Walk(root)) if (b is T t) return t;
            return null;
        }

        [Test]
        public void Synthetic_table_box_without_UA_table_rule_defaults_to_zero_border_spacing() {
            // <div style="display: table"> — no UA `table {}` rule applies.
            // Per CSS 2.1 §17.6.1 / CSS Tables L3 §7.1 the initial value of
            // `border-spacing` is 0; the 2px is purely the UA's HTML-element
            // visual default. A synthetic table box must NOT inherit phantom
            // spacing.
            var (root, _) = BuildWithMinimalUA(
                "<div style=\"display: table; width: 200px;\">" +
                "  <div style=\"display: table-row;\">" +
                "    <div style=\"display: table-cell;\">A</div>" +
                "    <div style=\"display: table-cell;\">B</div>" +
                "  </div>" +
                "</div>",
                authorCss: null,
                viewportWidth: 800);

            var table = FindFirst<TableBox>(root);
            Assert.That(table, Is.Not.Null,
                "<div style=\"display:table\"> should produce a TableBox");
            Assert.That(table.BorderSpacingX, Is.EqualTo(0).Within(0.001),
                "Synthetic table box must default to 0 horizontal border-spacing per spec.");
            Assert.That(table.BorderSpacingY, Is.EqualTo(0).Within(0.001),
                "Synthetic table box must default to 0 vertical border-spacing per spec.");
        }

        [Test]
        public void Html_table_with_UA_stylesheet_defaults_to_two_pixel_border_spacing() {
            // <table> element with the real UA sheet applied: the
            // `table { border-spacing: 2px; }` UA rule supplies the
            // visible browser-compatible default. This is independent
            // of the code-level fallback.
            var (root, _) = BuildWithRealUA(
                "<table><tbody><tr><td>A</td><td>B</td></tr></tbody></table>",
                authorCss: null,
                viewportWidth: 800);

            var table = FindFirst<TableBox>(root);
            Assert.That(table, Is.Not.Null);
            Assert.That(table.BorderSpacingX, Is.EqualTo(2).Within(0.001),
                "<table> with UA sheet must get 2px horizontal border-spacing (UA default).");
            Assert.That(table.BorderSpacingY, Is.EqualTo(2).Within(0.001),
                "<table> with UA sheet must get 2px vertical border-spacing (UA default).");
        }

        [Test]
        public void Author_border_spacing_wins_over_both_UA_default_and_spec_initial() {
            // Author rule `border-spacing: 10px` overrides both the UA's
            // 2px default (for <table>) and the spec's 0 initial (for
            // synthetic tables). Verify on both flavours.

            // Real <table>: 10px wins over UA's 2px.
            var (rootHtml, _) = BuildWithRealUA(
                "<table><tbody><tr><td>A</td><td>B</td></tr></tbody></table>",
                "table { border-spacing: 10px; }",
                viewportWidth: 800);
            var htmlTable = FindFirst<TableBox>(rootHtml);
            Assert.That(htmlTable, Is.Not.Null);
            Assert.That(htmlTable.BorderSpacingX, Is.EqualTo(10).Within(0.001),
                "Author border-spacing must beat UA default on <table>.");
            Assert.That(htmlTable.BorderSpacingY, Is.EqualTo(10).Within(0.001),
                "Author border-spacing must beat UA default on <table>.");

            // Synthetic <div style="display: table">: 10px wins over the
            // spec initial of 0.
            var (rootSynth, _) = BuildWithMinimalUA(
                "<div class=\"t\" style=\"display: table; width: 200px;\">" +
                "  <div style=\"display: table-row;\">" +
                "    <div style=\"display: table-cell;\">A</div>" +
                "    <div style=\"display: table-cell;\">B</div>" +
                "  </div>" +
                "</div>",
                ".t { border-spacing: 10px; }",
                viewportWidth: 800);
            var synthTable = FindFirst<TableBox>(rootSynth);
            Assert.That(synthTable, Is.Not.Null);
            Assert.That(synthTable.BorderSpacingX, Is.EqualTo(10).Within(0.001),
                "Author border-spacing must beat spec initial on synthetic table.");
            Assert.That(synthTable.BorderSpacingY, Is.EqualTo(10).Within(0.001),
                "Author border-spacing must beat spec initial on synthetic table.");
        }

        [Test]
        public void Resolve_border_spacing_on_table_with_unset_style_returns_zero_not_two() {
            // Directly exercise the code-level fallback in
            // TableLayout.ResolveBorderSpacing when the TableBox has no
            // populated `border-spacing` slot. Pre-fix the fallback was 2px;
            // per CSS 2.1 §17.6.1 the spec initial is 0. This guards against
            // future regressions where someone "restores" the 2px default in
            // code rather than the UA sheet.
            //
            // Two scenarios:
            //   (a) `table.Style == null` — the early-out branch.
            //   (b) `table.Style.Get("border-spacing")` is null/empty — the
            //       parse-path fallback branch.

            // ResolveBorderSpacing doesn't touch the BlockLayout reference;
            // passing null keeps the unit test focused.
            var tableLayout = new TableLayout(null);
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 800,
                ViewportHeightPx = 600,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96
            };
            tableLayout.Reset(ctx);

            var resolve = typeof(TableLayout).GetMethod(
                "ResolveBorderSpacing",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(resolve, Is.Not.Null,
                "Test relies on private ResolveBorderSpacing method; rename in code requires a test update.");

            // Scenario (a): null Style. Pre-fix this branch wrote 2; spec says 0.
            var nullStyleTable = new TableBox { Style = null };
            resolve.Invoke(tableLayout, new object[] { nullStyleTable });
            Assert.That(nullStyleTable.BorderSpacingX, Is.EqualTo(0).Within(0.001),
                "TableBox with null Style must default to 0 (CSS 2.1 §17.6.1).");
            Assert.That(nullStyleTable.BorderSpacingY, Is.EqualTo(0).Within(0.001),
                "TableBox with null Style must default to 0 (CSS 2.1 §17.6.1).");

            // Scenario (b): Style present but border-spacing slot empty.
            // A bare ComputedStyle has no properties set; Get(...) returns null.
            var emptyStyleTable = new TableBox {
                Style = new ComputedStyle(new Element("div"))
            };
            resolve.Invoke(tableLayout, new object[] { emptyStyleTable });
            Assert.That(emptyStyleTable.BorderSpacingX, Is.EqualTo(0).Within(0.001),
                "Empty border-spacing slot must default to 0 (CSS 2.1 §17.6.1).");
            Assert.That(emptyStyleTable.BorderSpacingY, Is.EqualTo(0).Within(0.001),
                "Empty border-spacing slot must default to 0 (CSS 2.1 §17.6.1).");
        }
    }
}
