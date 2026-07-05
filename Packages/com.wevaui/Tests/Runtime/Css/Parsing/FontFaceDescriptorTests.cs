using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;

namespace Weva.Tests.Css.Parsing {
    // CSS Fonts L4 §11 — @font-face descriptor parsing tests.
    // Covers the full descriptor set added in B3:
    //   font-family, src (multi-entry / local / format), font-weight (range),
    //   font-style (oblique + angle), font-stretch (keyword + percentage + range),
    //   unicode-range (single / range / wildcard / multi), font-display.
    [TestFixture]
    public class FontFaceDescriptorTests {
        // Helper: parse a stylesheet with one @font-face block and return the rule.
        static FontFaceRule ParseFontFace(string descriptors) {
            var sheet = CssParser.Parse("@font-face { " + descriptors + " }");
            foreach (var rule in sheet.Rules) {
                if (rule is FontFaceRule ffr) return ffr;
            }
            return null;
        }

        // ─── font-family ──────────────────────────────────────────────────────────

        [Test]
        public void FontFamily_unquoted_single_token() {
            var r = ParseFontFace("font-family: Inter; src: url('inter.woff2');");
            Assert.That(r, Is.Not.Null);
            Assert.That(r.FontFamily, Is.EqualTo("Inter"));
        }

        [Test]
        public void FontFamily_double_quoted() {
            var r = ParseFontFace("font-family: \"Helvetica Neue\"; src: url('h.woff2');");
            Assert.That(r, Is.Not.Null);
            Assert.That(r.FontFamily, Is.EqualTo("Helvetica Neue"));
        }

        [Test]
        public void FontFamily_single_quoted() {
            var r = ParseFontFace("font-family: 'My Font'; src: url('my.woff2');");
            Assert.That(r, Is.Not.Null);
            Assert.That(r.FontFamily, Is.EqualTo("My Font"));
        }

        // ─── src — single url ─────────────────────────────────────────────────────

        [Test]
        public void Src_single_url_no_format() {
            var r = ParseFontFace("font-family: F; src: url('foo.woff2');");
            Assert.That(r, Is.Not.Null);
            Assert.That(r.Src, Is.EqualTo("foo.woff2"));
            Assert.That(r.SrcList, Has.Count.EqualTo(1));
            Assert.That(r.SrcList[0].Url, Is.EqualTo("foo.woff2"));
            Assert.That(r.SrcList[0].Format, Is.Null);
            Assert.That(r.SrcList[0].IsLocal, Is.False);
        }

        [Test]
        public void Src_single_url_with_format() {
            var r = ParseFontFace("font-family: F; src: url('foo.woff2') format('woff2');");
            Assert.That(r, Is.Not.Null);
            Assert.That(r.SrcList[0].Format, Is.EqualTo("woff2"));
        }

        // ─── src — multi-entry ────────────────────────────────────────────────────

        [Test]
        public void Src_multi_entry_local_then_two_urls() {
            var r = ParseFontFace(
                "font-family: F; " +
                "src: local('Helvetica Neue'), url('foo.woff2') format('woff2'), url('foo.woff') format('woff');");
            Assert.That(r, Is.Not.Null);
            Assert.That(r.SrcList, Has.Count.EqualTo(3));
            // First entry: local
            Assert.That(r.SrcList[0].IsLocal, Is.True);
            Assert.That(r.SrcList[0].LocalName, Is.EqualTo("Helvetica Neue"));
            // Second: woff2 url
            Assert.That(r.SrcList[1].Url, Is.EqualTo("foo.woff2"));
            Assert.That(r.SrcList[1].Format, Is.EqualTo("woff2"));
            // Third: woff url
            Assert.That(r.SrcList[2].Url, Is.EqualTo("foo.woff"));
            Assert.That(r.SrcList[2].Format, Is.EqualTo("woff"));
            // Back-compat Src = first url()
            Assert.That(r.Src, Is.EqualTo("foo.woff2"));
        }

        [Test]
        public void Src_local_only_entry_has_no_url_in_Src() {
            var r = ParseFontFace("font-family: F; src: local('Helvetica');");
            Assert.That(r, Is.Not.Null);
            Assert.That(r.SrcList, Has.Count.EqualTo(1));
            Assert.That(r.SrcList[0].IsLocal, Is.True);
            // No url() means Src should be null
            Assert.That(r.Src, Is.Null);
        }

        [Test]
        public void Src_local_unquoted_name() {
            var r = ParseFontFace("font-family: F; src: local(Inter);");
            Assert.That(r, Is.Not.Null);
            Assert.That(r.SrcList[0].LocalName, Is.EqualTo("Inter"));
        }

        // ─── font-weight ──────────────────────────────────────────────────────────

        [Test]
        public void FontWeight_normal_keyword_maps_to_400() {
            var r = ParseFontFace("font-family: F; src: url('f.woff2'); font-weight: normal;");
            Assert.That(r.WeightMin, Is.EqualTo(400f));
            Assert.That(r.WeightMax, Is.EqualTo(400f));
        }

        [Test]
        public void FontWeight_bold_keyword_maps_to_700() {
            var r = ParseFontFace("font-family: F; src: url('f.woff2'); font-weight: bold;");
            Assert.That(r.WeightMin, Is.EqualTo(700f));
            Assert.That(r.WeightMax, Is.EqualTo(700f));
        }

        [Test]
        public void FontWeight_single_number() {
            var r = ParseFontFace("font-family: F; src: url('f.woff2'); font-weight: 600;");
            Assert.That(r.WeightMin, Is.EqualTo(600f));
            Assert.That(r.WeightMax, Is.EqualTo(600f));
        }

        [Test]
        public void FontWeight_range_two_numbers() {
            var r = ParseFontFace("font-family: F; src: url('f.woff2'); font-weight: 400 700;");
            Assert.That(r.WeightMin, Is.EqualTo(400f));
            Assert.That(r.WeightMax, Is.EqualTo(700f));
        }

        [Test]
        public void FontWeight_absent_leaves_null() {
            var r = ParseFontFace("font-family: F; src: url('f.woff2');");
            Assert.That(r.WeightMin, Is.Null);
            Assert.That(r.WeightMax, Is.Null);
        }

        // ─── font-style ───────────────────────────────────────────────────────────

        [Test]
        public void FontStyle_normal_is_default() {
            var r = ParseFontFace("font-family: F; src: url('f.woff2'); font-style: normal;");
            Assert.That(r.FontStyle, Is.EqualTo(FontFaceStyleValue.Normal));
        }

        [Test]
        public void FontStyle_italic() {
            var r = ParseFontFace("font-family: F; src: url('f.woff2'); font-style: italic;");
            Assert.That(r.FontStyle, Is.EqualTo(FontFaceStyleValue.Italic));
        }

        [Test]
        public void FontStyle_oblique_no_angle_defaults_14deg() {
            var r = ParseFontFace("font-family: F; src: url('f.woff2'); font-style: oblique;");
            Assert.That(r.FontStyle, Is.EqualTo(FontFaceStyleValue.Oblique));
            Assert.That(r.ObliqueAngleMin, Is.EqualTo(14f));
            Assert.That(r.ObliqueAngleMax, Is.EqualTo(14f));
        }

        [Test]
        public void FontStyle_oblique_with_single_angle() {
            var r = ParseFontFace("font-family: F; src: url('f.woff2'); font-style: oblique 14deg;");
            Assert.That(r.FontStyle, Is.EqualTo(FontFaceStyleValue.Oblique));
            Assert.That(r.ObliqueAngleMin, Is.EqualTo(14f).Within(0.001f));
            Assert.That(r.ObliqueAngleMax, Is.EqualTo(14f).Within(0.001f));
        }

        [Test]
        public void FontStyle_oblique_with_angle_range() {
            var r = ParseFontFace("font-family: F; src: url('f.woff2'); font-style: oblique 0deg 20deg;");
            Assert.That(r.FontStyle, Is.EqualTo(FontFaceStyleValue.Oblique));
            Assert.That(r.ObliqueAngleMin, Is.EqualTo(0f).Within(0.001f));
            Assert.That(r.ObliqueAngleMax, Is.EqualTo(20f).Within(0.001f));
        }

        // ─── font-stretch ─────────────────────────────────────────────────────────

        [Test]
        public void FontStretch_condensed_keyword_maps_to_75pct() {
            var r = ParseFontFace("font-family: F; src: url('f.woff2'); font-stretch: condensed;");
            Assert.That(r.StretchMin, Is.EqualTo(75f));
            Assert.That(r.StretchMax, Is.EqualTo(75f));
        }

        [Test]
        public void FontStretch_normal_keyword_maps_to_100pct() {
            var r = ParseFontFace("font-family: F; src: url('f.woff2'); font-stretch: normal;");
            Assert.That(r.StretchMin, Is.EqualTo(100f));
            Assert.That(r.StretchMax, Is.EqualTo(100f));
        }

        [Test]
        public void FontStretch_expanded_keyword_maps_to_125pct() {
            var r = ParseFontFace("font-family: F; src: url('f.woff2'); font-stretch: expanded;");
            Assert.That(r.StretchMin, Is.EqualTo(125f));
            Assert.That(r.StretchMax, Is.EqualTo(125f));
        }

        [Test]
        public void FontStretch_percentage_75() {
            var r = ParseFontFace("font-family: F; src: url('f.woff2'); font-stretch: 75%;");
            Assert.That(r.StretchMin, Is.EqualTo(75f));
            Assert.That(r.StretchMax, Is.EqualTo(75f));
        }

        [Test]
        public void FontStretch_percentage_range() {
            var r = ParseFontFace("font-family: F; src: url('f.woff2'); font-stretch: 75% 125%;");
            Assert.That(r.StretchMin, Is.EqualTo(75f));
            Assert.That(r.StretchMax, Is.EqualTo(125f));
        }

        [Test]
        public void FontStretch_absent_leaves_null() {
            var r = ParseFontFace("font-family: F; src: url('f.woff2');");
            Assert.That(r.StretchMin, Is.Null);
            Assert.That(r.StretchMax, Is.Null);
        }

        // ─── unicode-range ────────────────────────────────────────────────────────

        [Test]
        public void UnicodeRange_single_codepoint() {
            var r = ParseFontFace("font-family: F; src: url('f.woff2'); unicode-range: U+0041;");
            Assert.That(r.UnicodeRange, Has.Count.EqualTo(1));
            Assert.That(r.UnicodeRange[0].Start, Is.EqualTo(0x41));
            Assert.That(r.UnicodeRange[0].End, Is.EqualTo(0x41));
        }

        [Test]
        public void UnicodeRange_explicit_range() {
            var r = ParseFontFace("font-family: F; src: url('f.woff2'); unicode-range: U+0025-00FF;");
            Assert.That(r.UnicodeRange, Has.Count.EqualTo(1));
            Assert.That(r.UnicodeRange[0].Start, Is.EqualTo(0x25));
            Assert.That(r.UnicodeRange[0].End, Is.EqualTo(0xFF));
        }

        [Test]
        public void UnicodeRange_wildcard_4question() {
            // U+4?? expands to U+400–U+4FF
            var r = ParseFontFace("font-family: F; src: url('f.woff2'); unicode-range: U+4??;");
            Assert.That(r.UnicodeRange, Has.Count.EqualTo(1));
            Assert.That(r.UnicodeRange[0].Start, Is.EqualTo(0x400));
            Assert.That(r.UnicodeRange[0].End, Is.EqualTo(0x4FF));
        }

        [Test]
        public void UnicodeRange_multi_entry() {
            var r = ParseFontFace(
                "font-family: F; src: url('f.woff2'); unicode-range: U+0025-00FF, U+1F00-1FFF;");
            Assert.That(r.UnicodeRange, Has.Count.EqualTo(2));
            Assert.That(r.UnicodeRange[0].Start, Is.EqualTo(0x25));
            Assert.That(r.UnicodeRange[0].End, Is.EqualTo(0xFF));
            Assert.That(r.UnicodeRange[1].Start, Is.EqualTo(0x1F00));
            Assert.That(r.UnicodeRange[1].End, Is.EqualTo(0x1FFF));
        }

        [Test]
        public void UnicodeRange_absent_is_empty() {
            var r = ParseFontFace("font-family: F; src: url('f.woff2');");
            Assert.That(r.UnicodeRange, Has.Count.EqualTo(0));
        }

        [Test]
        public void UnicodeRange_lowercase_u_plus_accepted() {
            // The spec says U+ but authors sometimes write lowercase; parser normalises
            var r = ParseFontFace("font-family: F; src: url('f.woff2'); unicode-range: u+0041;");
            // parser upper-cases the token so this should still parse
            Assert.That(r.UnicodeRange, Has.Count.EqualTo(1));
            Assert.That(r.UnicodeRange[0].Start, Is.EqualTo(0x41));
        }

        // ─── font-display ─────────────────────────────────────────────────────────

        [Test]
        public void FontDisplay_swap() {
            var r = ParseFontFace("font-family: F; src: url('f.woff2'); font-display: swap;");
            Assert.That(r.FontDisplay, Is.EqualTo(FontDisplayValue.Swap));
        }

        [Test]
        public void FontDisplay_block() {
            var r = ParseFontFace("font-family: F; src: url('f.woff2'); font-display: block;");
            Assert.That(r.FontDisplay, Is.EqualTo(FontDisplayValue.Block));
        }

        [Test]
        public void FontDisplay_fallback() {
            var r = ParseFontFace("font-family: F; src: url('f.woff2'); font-display: fallback;");
            Assert.That(r.FontDisplay, Is.EqualTo(FontDisplayValue.Fallback));
        }

        [Test]
        public void FontDisplay_optional() {
            var r = ParseFontFace("font-family: F; src: url('f.woff2'); font-display: optional;");
            Assert.That(r.FontDisplay, Is.EqualTo(FontDisplayValue.Optional));
        }

        [Test]
        public void FontDisplay_auto_is_default() {
            var r = ParseFontFace("font-family: F; src: url('f.woff2'); font-display: auto;");
            Assert.That(r.FontDisplay, Is.EqualTo(FontDisplayValue.Auto));
        }

        [Test]
        public void FontDisplay_invalid_value_treated_as_auto() {
            // Unknown value should be silently dropped / treated as auto
            var r = ParseFontFace("font-family: F; src: url('f.woff2'); font-display: INVALID-VALUE;");
            Assert.That(r.FontDisplay, Is.EqualTo(FontDisplayValue.Auto));
        }

        [Test]
        public void FontDisplay_absent_defaults_to_auto() {
            var r = ParseFontFace("font-family: F; src: url('f.woff2');");
            Assert.That(r.FontDisplay, Is.EqualTo(FontDisplayValue.Auto));
        }

        // ─── whole-rule round-trips ───────────────────────────────────────────────

        [Test]
        public void FullRule_all_descriptors_parse_together() {
            const string css = @"
@font-face {
  font-family: 'Roboto';
  src: local('Roboto'), url('roboto.woff2') format('woff2'), url('roboto.woff') format('woff');
  font-weight: 100 900;
  font-style: oblique 0deg 12deg;
  font-stretch: 75% 125%;
  unicode-range: U+0000-00FF, U+1F00-1FFF;
  font-display: swap;
}";
            var sheet = CssParser.Parse(css);
            FontFaceRule r = null;
            foreach (var rule in sheet.Rules)
                if (rule is FontFaceRule ffr) { r = ffr; break; }

            Assert.That(r, Is.Not.Null);
            Assert.That(r.FontFamily, Is.EqualTo("Roboto"));
            // src
            Assert.That(r.SrcList, Has.Count.EqualTo(3));
            Assert.That(r.SrcList[0].IsLocal, Is.True);
            Assert.That(r.SrcList[1].Url, Is.EqualTo("roboto.woff2"));
            Assert.That(r.SrcList[2].Format, Is.EqualTo("woff"));
            // weight range
            Assert.That(r.WeightMin, Is.EqualTo(100f));
            Assert.That(r.WeightMax, Is.EqualTo(900f));
            // style
            Assert.That(r.FontStyle, Is.EqualTo(FontFaceStyleValue.Oblique));
            Assert.That(r.ObliqueAngleMin, Is.EqualTo(0f).Within(0.001f));
            Assert.That(r.ObliqueAngleMax, Is.EqualTo(12f).Within(0.001f));
            // stretch range
            Assert.That(r.StretchMin, Is.EqualTo(75f));
            Assert.That(r.StretchMax, Is.EqualTo(125f));
            // unicode-range
            Assert.That(r.UnicodeRange, Has.Count.EqualTo(2));
            Assert.That(r.UnicodeRange[0].Start, Is.EqualTo(0));
            Assert.That(r.UnicodeRange[0].End, Is.EqualTo(0xFF));
            // display
            Assert.That(r.FontDisplay, Is.EqualTo(FontDisplayValue.Swap));
        }

        [Test]
        public void FontFaceRule_returned_in_stylesheet_rules() {
            var sheet = CssParser.Parse("@font-face { font-family: X; src: url('x.woff2'); }");
            Assert.That(sheet.Rules, Has.Count.EqualTo(1));
            Assert.That(sheet.Rules[0], Is.InstanceOf<FontFaceRule>());
        }

        [Test]
        public void Multiple_font_face_rules_both_parsed() {
            const string css = @"
@font-face { font-family: A; src: url('a.woff2'); }
@font-face { font-family: B; src: url('b.woff2'); font-display: block; }";
            var sheet = CssParser.Parse(css);
            var rules = new List<FontFaceRule>();
            foreach (var rule in sheet.Rules)
                if (rule is FontFaceRule ffr) rules.Add(ffr);
            Assert.That(rules, Has.Count.EqualTo(2));
            Assert.That(rules[0].FontFamily, Is.EqualTo("A"));
            Assert.That(rules[1].FontFamily, Is.EqualTo("B"));
            Assert.That(rules[1].FontDisplay, Is.EqualTo(FontDisplayValue.Block));
        }

        [Test]
        public void Src_format_uppercased_input_normalised_to_lowercase() {
            var r = ParseFontFace("font-family: F; src: url('foo.woff2') format('WOFF2');");
            Assert.That(r.SrcList[0].Format, Is.EqualTo("woff2"));
        }

        [Test]
        public void FontWeight_range_400_700_parses_both_bounds() {
            var r = ParseFontFace("font-family: F; src: url('f.woff2'); font-weight: 400 700;");
            Assert.That(r.WeightMin, Is.EqualTo(400f));
            Assert.That(r.WeightMax, Is.EqualTo(700f));
        }

        [Test]
        public void All_stretch_keywords_map_to_correct_percentages() {
            var cases = new (string keyword, float expected)[] {
                ("ultra-condensed", 50f),
                ("extra-condensed", 62.5f),
                ("condensed", 75f),
                ("semi-condensed", 87.5f),
                ("normal", 100f),
                ("semi-expanded", 112.5f),
                ("expanded", 125f),
                ("extra-expanded", 150f),
                ("ultra-expanded", 200f),
            };
            foreach (var (keyword, expected) in cases) {
                var r = ParseFontFace("font-family: F; src: url('f.woff2'); font-stretch: " + keyword + ";");
                Assert.That(r.StretchMin, Is.EqualTo(expected),
                    "font-stretch: " + keyword + " should map to " + expected + "%");
            }
        }
    }
}
