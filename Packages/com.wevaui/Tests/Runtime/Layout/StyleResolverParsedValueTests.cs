using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Text;

namespace Weva.Tests.Layout {
    public class StyleResolverParsedValueTests {
        static LayoutContext Ctx() {
            return new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 1000,
                ViewportHeightPx = 800,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96
            };
        }

        [Test]
        public void Font_size_uses_parsed_value_without_raw_reparse() {
            var parent = new ComputedStyle(new Element("div"));
            parent.SetParsed(CssProperties.FontSizeId, new CssLength(20, CssLengthUnit.Px));

            var style = new ComputedStyle(new Element("span"));
            style.SetParsed(CssProperties.FontSizeId, new CssLength(2, CssLengthUnit.Em));

            Assert.That(StyleResolver.FontSizePx(style, parent, Ctx()), Is.EqualTo(40).Within(0.001));
        }

        [Test]
        public void Line_height_uses_parsed_number_as_multiplier() {
            var style = new ComputedStyle(new Element("span"));
            style.SetParsed(CssProperties.LineHeightId, new CssNumber(1.5));

            Assert.That(StyleResolver.LineHeightPx(style, 20, Ctx(), new MonoFontMetrics()), Is.EqualTo(30).Within(0.001));
        }

        [Test]
        public void Border_width_uses_parsed_value() {
            var ctx = Ctx();

            Assert.That(
                StyleResolver.ResolveBorderWidth(new CssLength(0.5, CssLengthUnit.Em), 20, ctx),
                Is.EqualTo(10).Within(0.001));
            Assert.That(
                StyleResolver.ResolveBorderWidth(new CssKeyword("thick"), 20, ctx),
                Is.EqualTo(5).Within(0.001));
        }

        // CSS Text L3 §6.1: `tab-size` accepts either `<number>` (count of space
        // advance widths) or `<length>` (literal tab-stop distance). The
        // length form must NOT be re-interpreted as a count of spaces.
        [Test]
        public void Tab_size_length_resolves_to_literal_pixel_tab_stop() {
            var fm = new MonoFontMetrics();
            double fs = 32;
            double spaceW = fm.Measure(" ", fs);

            var style = new ComputedStyle(new Element("span"));
            style.SetParsed(CssProperties.TabSizeId, new CssLength(16, CssLengthUnit.Px));

            double spacesCount = StyleResolver.TabSizeSpaces(style, fm, fs, Ctx().ToLengthContext(fs, fs));
            double tabStopPx = spacesCount * spaceW;

            Assert.That(tabStopPx, Is.EqualTo(16).Within(0.001));
        }

        [Test]
        public void Tab_size_unit_less_number_resolves_to_space_count() {
            var fm = new MonoFontMetrics();
            double fs = 32;

            var style = new ComputedStyle(new Element("span"));
            style.SetParsed(CssProperties.TabSizeId, new CssNumber(4));

            double spacesCount = StyleResolver.TabSizeSpaces(style, fm, fs, Ctx().ToLengthContext(fs, fs));

            Assert.That(spacesCount, Is.EqualTo(4).Within(0.001));
        }

        // CSS Text L3 §7.1: text-indent grammar is
        // [ <length-percentage> ] && hanging? && each-line?
        // The keyword modifiers must not cause the indent term to be dropped,
        // even when v1 doesn't implement their semantics.
        static ComputedStyle WithTextIndent(CssValue parsed) {
            var s = new ComputedStyle(new Element("p"));
            s.SetParsed(CssProperties.TextIndentId, parsed);
            return s;
        }

        static CssValueList Space(params CssValue[] items) {
            return new CssValueList(items, CssValueListSeparator.Space);
        }

        [Test]
        public void Text_indent_length_resolves_without_keyword() {
            var style = WithTextIndent(new CssLength(20, CssLengthUnit.Px));
            Assert.That(StyleResolver.TextIndentPx(style, null, Ctx(), 200), Is.EqualTo(20).Within(0.001));
        }

        [Test]
        public void Text_indent_length_with_hanging_keyword_keeps_indent() {
            var style = WithTextIndent(Space(new CssLength(20, CssLengthUnit.Px), new CssIdentifier("hanging")));
            Assert.That(StyleResolver.TextIndentPx(style, null, Ctx(), 200), Is.EqualTo(20).Within(0.001));
        }

        [Test]
        public void Text_indent_length_with_each_line_keyword_keeps_indent() {
            var style = WithTextIndent(Space(new CssLength(20, CssLengthUnit.Px), new CssIdentifier("each-line")));
            Assert.That(StyleResolver.TextIndentPx(style, null, Ctx(), 200), Is.EqualTo(20).Within(0.001));
        }

        [Test]
        public void Text_indent_length_with_both_modifier_keywords_keeps_indent() {
            var style = WithTextIndent(Space(
                new CssLength(20, CssLengthUnit.Px),
                new CssIdentifier("hanging"),
                new CssIdentifier("each-line")));
            Assert.That(StyleResolver.TextIndentPx(style, null, Ctx(), 200), Is.EqualTo(20).Within(0.001));
        }

        [Test]
        public void Text_indent_percentage_with_hanging_keyword_keeps_indent() {
            var bare = WithTextIndent(new CssLength(25, CssLengthUnit.Percent));
            var withKeyword = WithTextIndent(Space(new CssLength(25, CssLengthUnit.Percent), new CssIdentifier("hanging")));
            Assert.That(StyleResolver.TextIndentPx(bare, null, Ctx(), 200), Is.EqualTo(50).Within(0.001));
            Assert.That(StyleResolver.TextIndentPx(withKeyword, null, Ctx(), 200), Is.EqualTo(50).Within(0.001));
        }

        // DC4: LengthKind.Invalid was collapsed into Auto — every caller
        // already treated the two identically (BlockLayout's widthIsAuto and
        // PositionedExtensions' default-null branch), so the dedicated variant
        // carried no signal. These tests pin that contract.
        [Test]
        public void Resolve_length_unparseable_input_collapses_to_auto() {
            var style = new ComputedStyle(new Element("div"));
            var r = StyleResolver.ResolveLength("not-a-length", style, Ctx(), 16, 100);
            Assert.That(r.Kind, Is.EqualTo(StyleResolver.LengthKind.Auto));
        }

        [Test]
        public void Resolve_length_factory_invalid_now_returns_auto() {
            // The factory is kept for callsite clarity (meaning "couldn't
            // parse"), but the kind it stamps is Auto, not a separate variant.
            var r = StyleResolver.ResolvedLength.Invalid();
            Assert.That(r.Kind, Is.EqualTo(StyleResolver.LengthKind.Auto));
        }

        [Test]
        public void Resolve_length_from_parsed_unknown_keyword_collapses_to_auto() {
            var r = StyleResolver.ResolveLengthFromParsed(
                new CssIdentifier("definitely-not-a-keyword"), Ctx(), 16, 100);
            Assert.That(r.Kind, Is.EqualTo(StyleResolver.LengthKind.Auto));
        }
    }
}
