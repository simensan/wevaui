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
    // Coverage for the form-control UA defaults shipped in
    // `FormControlStylesheet.Source`. The base `UserAgentStylesheet` ships
    // `input/select/textarea { display: inline-block }` and a `button { padding
    // 2px 6px; display: inline-flex }` rule, and the form-control sheet layers
    // on top: explicit width/height/padding/border for `<input>` /
    // `<textarea>` / `<select>`, special-cased checkbox/radio sizing,
    // `option { display: none }` cloaking, and `:disabled { opacity: 0.5 }`.
    //
    // Mirrors the BuildWithRealUA convention from TableLayoutTests — both UA
    // sheets must be in the cascade for these rules to fire.
    //
    // v1 sizing model:
    //   `Box.Width` / `Box.Height` always report the OUTER (border-box) size.
    //   When CSS `width` is set with the default `box-sizing: content-box`,
    //   the engine adds horizontal padding + border to that value to produce
    //   the outer width. So `<input>` with the UA defaults
    //   (width:200, padding:4 8, border:1) reports Box.Width = 200 + 16 + 2
    //   = 218 and Box.Height = 24 + 8 + 2 = 34. The tests below encode that
    //   actual contract; if Box.Width is ever switched to a content-box
    //   accessor the assertions will need re-baselining.
    public class FormControlSizingTests {
        // Outer width contributed by UA padding (8+8) + border (1+1) on the
        // generic `input { padding:4px 8px; border:1px solid #ccc; }` rule.
        const double InputHorizontalChrome = 16 + 2;
        // Outer height contributed by UA padding (4+4) + border (1+1).
        const double InputVerticalChrome = 8 + 2;
        // Checkbox/radio override resets padding to 0; border stays 1px.
        const double CheckboxChrome = 0 + 2;

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

        // --- <input> defaults ----------------------------------------------

        [Test]
        public void Input_default_width_is_200px() {
            // FormControlStylesheet: `input { width: 200px; ... }`.
            // v1: box-sizing defaults to content-box AND Box.Width reports the
            // OUTER (border-box) edge, so the CSS 200px content width plus
            // horizontal padding (4+8+8+4=16) + horizontal border (1+1=2)
            // surfaces as Box.Width = 218. Assertion encodes that behaviour;
            // see InputHorizontalChrome.
            var (root, _) = BuildWithRealUA("<input>");
            var input = FindFirstByTag(root, "input");
            Assert.That(input, Is.Not.Null, "BlockBox should be created for <input>");
            Assert.That(input.Width, Is.EqualTo(200 + InputHorizontalChrome).Within(0.5));
            // The CSS `width` value itself (content width) is still 200.
            Assert.That(input.ContentWidth, Is.EqualTo(200).Within(0.5));
        }

        [Test]
        public void Input_default_height_is_24px() {
            // FormControlStylesheet: `input { ... height: 24px; ... }`.
            // v1: Box.Height is the outer (border-box) value, so the CSS
            // 24px content height + vertical padding (4+4=8) + border (1+1)
            // surfaces as Box.Height = 34.
            var (root, _) = BuildWithRealUA("<input>");
            var input = FindFirstByTag(root, "input");
            Assert.That(input, Is.Not.Null);
            Assert.That(input.Height, Is.EqualTo(24 + InputVerticalChrome).Within(0.5));
            Assert.That(input.ContentHeight, Is.EqualTo(24).Within(0.5));
        }

        [Test]
        public void Input_default_padding_4_8() {
            // FormControlStylesheet: `input { ... padding: 4px 8px; ... }`.
            // The form-control rule wins the cascade over the base UA's
            // `input, textarea, select { padding: 1px 2px; ... }` because
            // FormControlStylesheet is appended after the base UA sheet at
            // the same origin level, so document order resolves the tie.
            var (root, _) = BuildWithRealUA("<input>");
            var input = FindFirstByTag(root, "input");
            Assert.That(input, Is.Not.Null);
            Assert.That(input.PaddingTop, Is.EqualTo(4).Within(0.001));
            Assert.That(input.PaddingBottom, Is.EqualTo(4).Within(0.001));
            Assert.That(input.PaddingLeft, Is.EqualTo(8).Within(0.001));
            Assert.That(input.PaddingRight, Is.EqualTo(8).Within(0.001));
        }

        [Test]
        public void Input_with_explicit_width_overrides_default() {
            // Author CSS has higher specificity-tier (origin) than UA, so
            // `style="width:400px"` (inline) trumps the UA's 200px default.
            // UA sets box-sizing: border-box, so the author's 400px IS the
            // outer (border-box) width; content = 400 − padding − border.
            var (root, _) = BuildWithRealUA("<input style=\"width:400px\">");
            var input = FindFirstByTag(root, "input");
            Assert.That(input, Is.Not.Null);
            Assert.That(input.Width, Is.EqualTo(400).Within(0.5));
            Assert.That(input.ContentWidth, Is.EqualTo(400 - InputHorizontalChrome).Within(0.5));
        }

        [Test]
        public void Input_type_checkbox_default_size_14() {
            // FormControlStylesheet: `input[type="checkbox"], input[type="radio"]
            // { width: 14px; height: 14px; padding: 0; ... }`. Attribute
            // selector overrides the generic `input` rule by specificity
            // (one type + one attr beats one type).
            // v1: padding override to 0 lands, but the 1px UA border still
            // contributes to the outer box, so Box.Width/Height = 16.
            var (root, _) = BuildWithRealUA("<input type=\"checkbox\">");
            var input = FindFirstByTag(root, "input");
            Assert.That(input, Is.Not.Null);
            Assert.That(input.ContentWidth, Is.EqualTo(14).Within(0.5));
            Assert.That(input.ContentHeight, Is.EqualTo(14).Within(0.5));
            Assert.That(input.Width, Is.EqualTo(14 + CheckboxChrome).Within(0.5));
            Assert.That(input.Height, Is.EqualTo(14 + CheckboxChrome).Within(0.5));
            // Padding must have been reset by the attribute-selector rule.
            Assert.That(input.PaddingLeft, Is.EqualTo(0).Within(0.001));
            Assert.That(input.PaddingTop, Is.EqualTo(0).Within(0.001));
        }

        [Test]
        public void Input_type_radio_default_size_14() {
            // Same rule as checkbox; covers the second selector of the
            // grouped rule and the radio-specific override
            // `input[type="radio"] { border-radius: 7px }`.
            var (root, _) = BuildWithRealUA("<input type=\"radio\">");
            var input = FindFirstByTag(root, "input");
            Assert.That(input, Is.Not.Null);
            Assert.That(input.ContentWidth, Is.EqualTo(14).Within(0.5));
            Assert.That(input.ContentHeight, Is.EqualTo(14).Within(0.5));
            Assert.That(input.Width, Is.EqualTo(14 + CheckboxChrome).Within(0.5));
            Assert.That(input.Height, Is.EqualTo(14 + CheckboxChrome).Within(0.5));
        }

        // --- <textarea> defaults -------------------------------------------

        [Test]
        public void Textarea_default_width_is_200px() {
            // FormControlStylesheet: `textarea { ... width: 200px; ... }`.
            // Same content-box semantics as <input>.
            var (root, _) = BuildWithRealUA("<textarea></textarea>");
            var ta = FindFirstByTag(root, "textarea");
            Assert.That(ta, Is.Not.Null);
            Assert.That(ta.ContentWidth, Is.EqualTo(200).Within(0.5));
            Assert.That(ta.Width, Is.EqualTo(200 + InputHorizontalChrome).Within(0.5));
        }

        [Test]
        public void Textarea_default_height_is_80px() {
            // FormControlStylesheet: `textarea { ... height: 80px; ... }`.
            var (root, _) = BuildWithRealUA("<textarea></textarea>");
            var ta = FindFirstByTag(root, "textarea");
            Assert.That(ta, Is.Not.Null);
            Assert.That(ta.ContentHeight, Is.EqualTo(80).Within(0.5));
            Assert.That(ta.Height, Is.EqualTo(80 + InputVerticalChrome).Within(0.5));
        }

        // --- <select> defaults ---------------------------------------------

        [Test]
        public void Select_default_width_is_200px() {
            // FormControlStylesheet: `select { ... width: 200px; ... }`.
            var (root, _) = BuildWithRealUA("<select></select>");
            var sel = FindFirstByTag(root, "select");
            Assert.That(sel, Is.Not.Null);
            Assert.That(sel.ContentWidth, Is.EqualTo(200).Within(0.5));
            Assert.That(sel.Width, Is.EqualTo(200 + InputHorizontalChrome).Within(0.5));
        }

        [Test]
        public void Select_default_height_is_24px() {
            // FormControlStylesheet: `select { ... height: 24px; ... }`.
            var (root, _) = BuildWithRealUA("<select></select>");
            var sel = FindFirstByTag(root, "select");
            Assert.That(sel, Is.Not.Null);
            Assert.That(sel.ContentHeight, Is.EqualTo(24).Within(0.5));
            Assert.That(sel.Height, Is.EqualTo(24 + InputVerticalChrome).Within(0.5));
        }

        [Test]
        public void Select_with_one_option_does_not_grow_for_option_content() {
            // Regression for issue #240: prior to the
            // `option { display: none }` rule, every <option> laid out as a
            // block child below the <select>, displacing surrounding layout
            // (the bug stretched a <select> stack by hundreds of pixels per
            // dropdown). Confirm the closed select stays at its UA size
            // regardless of how long the option text is — outer dims should
            // match the empty-select case (Select_default_width_is_200px).
            var (root, _) = BuildWithRealUA(
                "<select><option>An extraordinarily long option label</option></select>");
            var sel = FindFirstByTag(root, "select");
            Assert.That(sel, Is.Not.Null);
            Assert.That(sel.Width, Is.EqualTo(200 + InputHorizontalChrome).Within(0.5),
                "Closed <select> must not grow to fit hidden <option> text");
            Assert.That(sel.Height, Is.EqualTo(24 + InputVerticalChrome).Within(0.5),
                "Closed <select> must not grow vertically for hidden <option> either");
        }

        // --- <button> defaults ---------------------------------------------

        [Test]
        public void Button_default_padding_2_6() {
            // Base UA: `button { padding: 2px 6px; display: inline-flex;
            // align-items: center; justify-content: center; }`.
            var (root, _) = BuildWithRealUA("<button>X</button>");
            var btn = FindFirstByTag(root, "button");
            Assert.That(btn, Is.Not.Null);
            Assert.That(btn.PaddingTop, Is.EqualTo(2).Within(0.001));
            Assert.That(btn.PaddingBottom, Is.EqualTo(2).Within(0.001));
            Assert.That(btn.PaddingLeft, Is.EqualTo(6).Within(0.001));
            Assert.That(btn.PaddingRight, Is.EqualTo(6).Within(0.001));
        }

        [Test]
        public void Button_default_display_is_inline_block() {
            // UA: `button { display: inline-block; text-align: center }` —
            // Chrome's default. Horizontal label centring comes from
            // text-align (works at any width); vertical centring inside an
            // explicit height comes from BlockLayout's button content pass.
            var (root, _) = BuildWithRealUA("<button>X</button>");
            var btn = FindFirstByTag(root, "button");
            Assert.That(btn, Is.Not.Null);
            Assert.That(btn.Style.Get("display"), Is.EqualTo("inline-block"));
        }

        [Test]
        public void Button_default_box_sizing_is_border_box() {
            // Chrome UA default for native buttons uses border-box sizing:
            // author min-width/width includes padding and border.
            var (root, _) = BuildWithRealUA(
                "<button style=\"min-width:132px;padding:12px 18px;border:1px solid transparent\">Map</button>");
            var btn = FindFirstByTag(root, "button");
            Assert.That(btn, Is.Not.Null);
            Assert.That(btn.Style.Get("box-sizing"), Is.EqualTo("border-box"));
            Assert.That(btn.Width, Is.EqualTo(132).Within(0.5));
        }

        [Test]
        public void Button_with_text_content_shrinks_to_fit_text() {
            // `<button>` is display: inline-block per UA. Inline-block
            // shrinks to fit its content, so `<button>OK</button>` is ~28px
            // (text 16px + 12px padding), not the parent block width.
            var (root, _) = BuildWithRealUA("<button>OK</button>");
            var btn = FindFirstByTag(root, "button");
            Assert.That(btn, Is.Not.Null);
            Assert.That(btn.Style.Get("display"), Is.EqualTo("inline-block"),
                "Cascade surfaces the UA default `inline-block`.");
            Assert.That(btn.Width, Is.LessThan(50),
                "Inline-block button shrinks to fit its text (~28px), not to the parent block width.");
        }

        // --- :disabled -----------------------------------------------------

        [Test]
        public void Disabled_input_has_50_percent_opacity() {
            // FormControlStylesheet: `:disabled { opacity: 0.5;
            // cursor: not-allowed; }`. The selector matcher recognises
            // `[disabled]` attribute as :disabled state
            // (SelectorMatcher.cs line ~300).
            var (root, _) = BuildWithRealUA("<input disabled>");
            var input = FindFirstByTag(root, "input");
            Assert.That(input, Is.Not.Null);
            Assert.That(input.Style, Is.Not.Null);
            var raw = input.Style.Get("opacity");
            Assert.That(raw, Is.Not.Null.And.Not.Empty,
                "opacity should be set by :disabled UA rule");
            Assert.That(double.Parse(raw, System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(0.5).Within(0.001));
        }
    }
}
