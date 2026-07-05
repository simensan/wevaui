using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Forms;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using Weva.Parsing;

namespace Weva.Tests.Layout {
    // CSS Basic User Interface L4 §13 — `field-sizing: content` layout impact.
    //
    // When `field-sizing: content` is set on a textual `<input>`, the box
    // builder overrides the UA's fixed width (218px border-box) with the
    // intrinsic width of the element's current `value` text, plus a UA caret
    // allowance of 4px. `min-width` / `max-width` still clamp the result as
    // normal (tested here). The width override uses the engine's IFontMetrics
    // (MonoFontMetrics in the headless harness — 0.5em/char at 16px ≈ 8px/char).
    //
    // v1 scope: <input type="text"> only. Textarea and select are follow-on work.
    // Measurement is via LayoutEngine's default IFontMetrics; the stub constant
    // (BoxBuilder.StubCharWidthPx = 8px) is an exact match for MonoFontMetrics
    // default at 16px so all assertions below are numerically precise.
    //
    // See FormControlSizingTests for the UA-default (field-sizing: auto) baselines.
    public class FieldSizingLayoutTests {
        // FormControlStylesheet UA constants (from FormControlSizingTests baseline):
        //   border-box width 218px, padding 4px 8px, border 1px => frame = 8+8+1+1 = 18.
        // For field-sizing:content the caret allowance is BoxBuilder.FieldSizingCaretPaddingPx = 4.
        const double PadH = 8.0 + 8.0;           // left + right padding
        const double BordH = 1.0 + 1.0;           // left + right border
        const double Frame = PadH + BordH;         // total horizontal frame = 18px
        const double CaretPad = 4.0;              // UA caret allowance (BoxBuilder constant)
        const double CharPx = 8.0;                // MonoFontMetrics 0.5em at 16px

        // Expected border-box width for `n` chars of value.
        static double ContentBorderBoxWidth(int charCount)
            => charCount * CharPx + CaretPad + Frame;

        // Expected content width (border-box − frame) for `n` chars.
        static double ContentContentWidth(int charCount)
            => charCount * CharPx + CaretPad;

        static (Box root, Dictionary<Element, ComputedStyle> styles) BuildWithRealUA(
            string html, string authorCss = null, double viewportWidth = 800
        ) {
            var doc = HtmlParser.Parse(html);
            var sheets = new List<OriginatedStylesheet> {
                UserAgentStylesheet.Parse(),
                FormControlStylesheet.Parse(),
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
                DpiPixelsPerInch = 96,
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

        static BlockBox FindFirstByTag(Box root, string tag) {
            foreach (var b in Walk(root)) {
                if (b is BlockBox bb && bb.Element != null && bb.Element.TagName == tag) return bb;
            }
            return null;
        }

        // -----------------------------------------------------------------------
        // Baseline: field-sizing: auto (default) must leave UA width unchanged
        // -----------------------------------------------------------------------

        [Test]
        public void Input_field_sizing_auto_uses_UA_default_width() {
            // `field-sizing: auto` (the initial value) must not change the UA
            // default 218px border-box width set by FormControlStylesheet.
            // This is both a baseline sanity test and a regression guard: if
            // MaybeApplyFieldSizingWidth fires for `auto`, it would incorrectly
            // shrink every input that has no value attribute.
            var (root, _) = BuildWithRealUA("<input>");
            var input = FindFirstByTag(root, "input");
            Assert.That(input, Is.Not.Null);
            Assert.That(input.Width, Is.EqualTo(218.0).Within(0.5),
                "field-sizing: auto (default) must yield the UA 218px border-box width");
        }

        [Test]
        public void Input_explicit_field_sizing_auto_uses_UA_default_width() {
            // Explicitly set field-sizing: auto in author CSS. Same result as
            // the initial value — UA default must be preserved.
            var (root, _) = BuildWithRealUA("<input value=\"hello\">",
                "input { field-sizing: auto; }");
            var input = FindFirstByTag(root, "input");
            Assert.That(input, Is.Not.Null);
            Assert.That(input.Width, Is.EqualTo(218.0).Within(0.5),
                "Explicit field-sizing: auto must keep the UA 218px width even when value is set");
        }

        // -----------------------------------------------------------------------
        // Core feature: field-sizing: content shrinks to value text
        // -----------------------------------------------------------------------

        [Test]
        public void Input_field_sizing_content_short_value_is_narrow() {
            // `<input value="hi" style="field-sizing: content">` — 2 chars.
            // Expected border-box width = 2 * 8 + 4(caret) + 8 + 8 + 1 + 1 = 38px.
            // Must be substantially narrower than the UA default 218px.
            var (root, _) = BuildWithRealUA(
                "<input value=\"hi\">",
                "input { field-sizing: content; }");
            var input = FindFirstByTag(root, "input");
            Assert.That(input, Is.Not.Null);
            double expected = ContentBorderBoxWidth(2); // = 38
            Assert.That(input.Width, Is.EqualTo(expected).Within(1.0),
                "field-sizing: content with 2-char value should yield narrow border-box width");
            Assert.That(input.Width, Is.LessThan(218.0 - 10),
                "field-sizing: content must produce a width significantly narrower than the UA default");
        }

        [Test]
        public void Input_field_sizing_content_longer_value_is_wider_than_short() {
            // A longer value ("hello world" = 11 chars) must produce a wider box
            // than a shorter value ("hi" = 2 chars) — confirms that the measured
            // width tracks the actual text length.
            var (root1, _) = BuildWithRealUA(
                "<input value=\"hi\">",
                "input { field-sizing: content; }");
            var (root2, _) = BuildWithRealUA(
                "<input value=\"hello world\">",
                "input { field-sizing: content; }");
            var short_ = FindFirstByTag(root1, "input");
            var long_ = FindFirstByTag(root2, "input");
            Assert.That(short_, Is.Not.Null);
            Assert.That(long_, Is.Not.Null);
            double shortW = ContentBorderBoxWidth(2);   // = 38
            double longW = ContentBorderBoxWidth(11);   // = 110
            Assert.That(short_.Width, Is.EqualTo(shortW).Within(1.0),
                "2-char value input should be ~38px");
            Assert.That(long_.Width, Is.EqualTo(longW).Within(1.0),
                "11-char value input should be ~110px");
            Assert.That(long_.Width, Is.GreaterThan(short_.Width),
                "Longer value must produce a wider box");
        }

        [Test]
        public void Input_field_sizing_content_empty_value_uses_caret_width_only() {
            // Empty value and no placeholder: only the caret allowance is added to
            // the frame. Content width = CaretPad = 4px; border-box = Frame + CaretPad.
            var (root, _) = BuildWithRealUA(
                "<input value=\"\">",
                "input { field-sizing: content; }");
            var input = FindFirstByTag(root, "input");
            Assert.That(input, Is.Not.Null);
            double expected = ContentBorderBoxWidth(0); // = 0 + 4 + 18 = 22
            Assert.That(input.Width, Is.EqualTo(expected).Within(1.0),
                "Empty-value input with field-sizing: content should collapse to caret + frame");
            Assert.That(input.ContentWidth, Is.EqualTo(CaretPad).Within(1.0),
                "Content width for empty value should equal the caret allowance only");
        }

        [Test]
        public void Input_field_sizing_content_empty_value_with_placeholder_uses_placeholder_width() {
            // When value is empty but a `placeholder` attribute is present, the
            // spec recommends sizing to the placeholder text so the field is not
            // invisible. Implementation: BoxBuilder substitutes the placeholder
            // when value is empty.
            const string placeholder = "search..."; // 9 chars
            var (root, _) = BuildWithRealUA(
                $"<input placeholder=\"{placeholder}\">",
                "input { field-sizing: content; }");
            var input = FindFirstByTag(root, "input");
            Assert.That(input, Is.Not.Null);
            double expected = ContentBorderBoxWidth(placeholder.Length); // = 9*8+4+18 = 94
            Assert.That(input.Width, Is.EqualTo(expected).Within(1.0),
                "Empty-value input with placeholder should size to the placeholder text width");
        }

        // -----------------------------------------------------------------------
        // min-width / max-width clamping still applies
        // -----------------------------------------------------------------------

        [Test]
        public void Input_field_sizing_content_respects_min_width() {
            // `min-width: 100px` must clamp the intrinsic shrink-to-content
            // result upward. For a 2-char value the natural width is 38px which
            // is less than 100px, so the final width should be exactly 100px.
            // Note: FormControlStylesheet uses border-box, so min-width 100px
            // in author CSS (also border-box since box-sizing inherits) clamps
            // the border-box outer size.
            var (root, _) = BuildWithRealUA(
                "<input value=\"hi\">",
                "input { field-sizing: content; min-width: 100px; }");
            var input = FindFirstByTag(root, "input");
            Assert.That(input, Is.Not.Null);
            double natural = ContentBorderBoxWidth(2); // 38 — below min
            Assert.That(natural, Is.LessThan(100.0),
                "Precondition: natural width for 2-char value must be less than min-width 100px");
            Assert.That(input.Width, Is.EqualTo(100.0).Within(1.0),
                "field-sizing: content + min-width: 100px must clamp up to 100px");
        }

        [Test]
        public void Input_field_sizing_content_respects_max_width() {
            // `max-width: 50px` must clamp the intrinsic shrink-to-content
            // result downward. For "hello world" (11 chars) the natural width
            // is 110px which exceeds 50px, so the final width must be 50px.
            var (root, _) = BuildWithRealUA(
                "<input value=\"hello world\">",
                "input { field-sizing: content; max-width: 50px; }");
            var input = FindFirstByTag(root, "input");
            Assert.That(input, Is.Not.Null);
            double natural = ContentBorderBoxWidth(11); // 110 — above max
            Assert.That(natural, Is.GreaterThan(50.0),
                "Precondition: natural width for 11-char value must exceed max-width 50px");
            Assert.That(input.Width, Is.EqualTo(50.0).Within(1.0),
                "field-sizing: content + max-width: 50px must clamp down to 50px");
        }

        // -----------------------------------------------------------------------
        // Non-textual input types must not be affected
        // -----------------------------------------------------------------------

        [Test]
        public void Input_type_checkbox_field_sizing_content_does_not_override_size() {
            // Checkbox/radio inputs are replaced-element widgets whose size is
            // controlled by the UA attribute-selector rule, not by value text.
            // MaybeApplyFieldSizingWidth must bail out early for these types.
            var (root, _) = BuildWithRealUA(
                "<input type=\"checkbox\">",
                "input { field-sizing: content; }");
            var input = FindFirstByTag(root, "input");
            Assert.That(input, Is.Not.Null);
            // UA checkbox size is 16px border-box (14px content + 1+1 border, padding=0).
            Assert.That(input.Width, Is.EqualTo(16.0).Within(1.0),
                "Checkbox with field-sizing: content must keep the UA 16px size, not shrink to value text");
        }

        // -----------------------------------------------------------------------
        // Content width accurately tracks the value text
        // -----------------------------------------------------------------------

        [Test]
        public void Input_field_sizing_content_width_tracks_char_count() {
            // Verify that content width = charCount * CharPx + CaretPad for
            // several value lengths, confirming the linear text-width model.
            // This pins the measurement contract used by the headless harness.
            foreach (int chars in new[] { 1, 3, 5, 8, 12 }) {
                string value = new string('x', chars);
                var (root, _) = BuildWithRealUA(
                    $"<input value=\"{value}\">",
                    "input { field-sizing: content; }");
                var input = FindFirstByTag(root, "input");
                Assert.That(input, Is.Not.Null,
                    $"input box should exist for {chars}-char value");
                double expectedBox = ContentBorderBoxWidth(chars);
                Assert.That(input.Width, Is.EqualTo(expectedBox).Within(1.0),
                    $"border-box width for {chars}-char value should be {expectedBox}px");
                double expectedContent = ContentContentWidth(chars);
                Assert.That(input.ContentWidth, Is.EqualTo(expectedContent).Within(1.0),
                    $"content width for {chars}-char value should be {expectedContent}px");
            }
        }
    }
}
