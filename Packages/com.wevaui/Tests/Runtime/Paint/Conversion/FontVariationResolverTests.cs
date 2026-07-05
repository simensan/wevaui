using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Paint.Conversion;

namespace Weva.Tests.Paint.Conversion {
    // TG4 — Direct unit coverage for `FontVariationResolver` and its
    // `FontAxis` struct (`Runtime/Paint/Conversion/FontVariationResolver.cs`).
    //
    // FontVariationResolver consumes the PARSED `font-variation-settings`
    // CssValue tree and emits OpenType axis tuples (Tag, Value) for the
    // text-run resolver. These tests stamp parsed CssValueLists directly via
    // `ComputedStyle.SetParsed(CssProperties.FontVariationSettingsId, ...)`
    // so we exercise the resolver in isolation — independent of the
    // cascade-pipeline integration tests that already live in
    // `TextPropertyIntegrationTests.cs`.
    //
    // Grammar (CSS Fonts 4 §6.10):
    //   font-variation-settings: normal | <feature-tag-value>#
    //   <feature-tag-value> = <string> [ <number> | <percentage> | <integer> ]
    public class FontVariationResolverTests {
        static ComputedStyle Style() => new ComputedStyle(new Element("p"));
        static LengthContext Ctx() => LengthContext.Default;

        // Builds one comma-list item: a Space-separated CssValueList whose
        // first child is the axis tag (CssString) and second is the numeric
        // axis value (CssNumber).
        static CssValueList Axis(string tag, double number) {
            var inner = new List<CssValue> {
                new CssString(tag, '"'),
                new CssNumber(number)
            };
            return new CssValueList(inner, CssValueListSeparator.Space);
        }

        // ---------------- Single-axis happy path ----------------

        [Test]
        public void Single_wght_axis_700_parses_to_one_FontAxis_TG4() {
            var style = Style();
            // Authored: font-variation-settings: "wght" 700
            // The parsed form for a single feature-tag-value is the inner
            // Space-list directly (no outer comma-list wrapper required —
            // the resolver handles either shape).
            style.SetParsed(CssProperties.FontVariationSettingsId, Axis("wght", 700));

            var axes = FontVariationResolver.Resolve(style, Ctx());

            Assert.That(axes, Is.Not.Null);
            Assert.That(axes.Count, Is.EqualTo(1));
            Assert.That(axes[0].Tag, Is.EqualTo("wght"));
            Assert.That(axes[0].Value, Is.EqualTo(700f));
        }

        // ---------------- Multi-axis happy path ----------------

        [Test]
        public void Two_axes_wght_and_wdth_parse_in_order_TG4() {
            var style = Style();
            // Authored: font-variation-settings: "wght" 700, "wdth" 125
            var outer = new CssValueList(
                new List<CssValue> { Axis("wght", 700), Axis("wdth", 125) },
                CssValueListSeparator.Comma);
            style.SetParsed(CssProperties.FontVariationSettingsId, outer);

            var axes = FontVariationResolver.Resolve(style, Ctx());

            Assert.That(axes.Count, Is.EqualTo(2));
            Assert.That(axes[0].Tag, Is.EqualTo("wght"));
            Assert.That(axes[0].Value, Is.EqualTo(700f));
            Assert.That(axes[1].Tag, Is.EqualTo("wdth"));
            Assert.That(axes[1].Value, Is.EqualTo(125f));
        }

        // ---------------- `normal` keyword ----------------

        [Test]
        public void Normal_keyword_yields_empty_list_TG4() {
            var style = Style();
            // CSS Fonts 4 §6.10 — initial value `normal` means "no axis
            // overrides". The resolver must short-circuit to the shared
            // empty singleton.
            style.SetParsed(CssProperties.FontVariationSettingsId, new CssKeyword("normal"));

            var axes = FontVariationResolver.Resolve(style, Ctx());

            Assert.That(axes, Is.Not.Null);
            Assert.That(axes.Count, Is.EqualTo(0));
        }

        [Test]
        public void Normal_identifier_form_also_yields_empty_list_TG4() {
            // Some upstream parser paths emit `normal` as a CssIdentifier
            // rather than a CssKeyword. The resolver accepts both shapes —
            // pin that branch explicitly.
            var style = Style();
            style.SetParsed(CssProperties.FontVariationSettingsId, new CssIdentifier("normal"));

            var axes = FontVariationResolver.Resolve(style, Ctx());

            Assert.That(axes.Count, Is.EqualTo(0));
        }

        // ---------------- Custom 4-char axis tag preserved ----------------

        [Test]
        public void Custom_four_letter_axis_tag_GRAD_is_preserved_verbatim_TG4() {
            var style = Style();
            // Authored: font-variation-settings: "GRAD" 100
            // The resolver MUST NOT lowercase / normalise the tag — variable
            // fonts distinguish "GRAD" (grade) from "grad" / "wght" / etc.
            // by exact 4-char ASCII match.
            style.SetParsed(CssProperties.FontVariationSettingsId, Axis("GRAD", 100));

            var axes = FontVariationResolver.Resolve(style, Ctx());

            Assert.That(axes.Count, Is.EqualTo(1));
            Assert.That(axes[0].Tag, Is.EqualTo("GRAD"),
                "Custom axis tags MUST be preserved verbatim (case-sensitive 4-char OpenType tag). " +
                "Lowercasing would silently retag GRAD as the unrelated `grad` slot on a variable font.");
            Assert.That(axes[0].Value, Is.EqualTo(100f));
        }

        // ---------------- Graceful degradation ----------------

        [Test]
        public void Garbage_input_yields_empty_list_TG4() {
            var style = Style();
            // Tag is missing entirely (just a bare number). The resolver
            // should swallow this gracefully and return empty.
            var bad = new CssValueList(
                new List<CssValue> { new CssNumber(700) },
                CssValueListSeparator.Space);
            style.SetParsed(CssProperties.FontVariationSettingsId, bad);

            var axes = FontVariationResolver.Resolve(style, Ctx());

            Assert.That(axes, Is.Not.Null);
            Assert.That(axes.Count, Is.EqualTo(0),
                "A feature-tag-value with no <string> tag must be dropped rather than thrown.");
        }

        [Test]
        public void Wrong_length_tag_string_is_dropped_TG4() {
            // OpenType tags are EXACTLY 4 ASCII characters. The resolver
            // should reject 3-char and 5-char tags rather than passing them
            // through (FontAsset would either crash or silently ignore).
            var style = Style();
            var threeChar = new CssValueList(
                new List<CssValue> { new CssString("wgt", '"'), new CssNumber(700) },
                CssValueListSeparator.Space);
            style.SetParsed(CssProperties.FontVariationSettingsId, threeChar);

            var axes = FontVariationResolver.Resolve(style, Ctx());

            Assert.That(axes.Count, Is.EqualTo(0));
        }

        // ---------------- Null guards ----------------

        [Test]
        public void Null_style_returns_empty_list_TG4() {
            // Documented null-style guard at the top of Resolve.
            var axes = FontVariationResolver.Resolve(null, Ctx());
            Assert.That(axes, Is.Not.Null);
            Assert.That(axes.Count, Is.EqualTo(0));
        }

        [Test]
        public void Unset_property_returns_empty_list_TG4() {
            // A style that never had font-variation-settings set should
            // return the empty singleton (GetParsed returns null and the
            // resolver short-circuits).
            var style = Style();
            var axes = FontVariationResolver.Resolve(style, Ctx());
            Assert.That(axes.Count, Is.EqualTo(0));
        }

        // ---------------- ShouldAutoOpticalSize ----------------

        [Test]
        public void ShouldAutoOpticalSize_defaults_to_true_TG4() {
            var style = Style();
            // Unset = spec default = `auto` per CSS Fonts 4 §6.10.
            Assert.That(FontVariationResolver.ShouldAutoOpticalSize(style), Is.True);
            Assert.That(FontVariationResolver.ShouldAutoOpticalSize(null), Is.True);
        }

        [Test]
        public void ShouldAutoOpticalSize_returns_false_for_none_keyword_TG4() {
            var style = Style();
            style.SetParsed(CssProperties.FontOpticalSizingId, new CssKeyword("none"));
            Assert.That(FontVariationResolver.ShouldAutoOpticalSize(style), Is.False);
        }

        // ---------------- font-stretch → wdth axis ----------------
        //
        // CSS Fonts 4 §6.3 specifies that font-stretch maps to the variable-
        // font `wdth` axis as a percentage. Keywords have normative
        // percentage equivalents:
        //   ultra-condensed=50, extra-condensed=62.5, condensed=75,
        //   semi-condensed=87.5, normal=100 (omitted from axis list),
        //   semi-expanded=112.5, expanded=125, extra-expanded=150,
        //   ultra-expanded=200.

        [Test]
        public void Font_stretch_condensed_emits_wdth_75() {
            var style = Style();
            style.Set("font-stretch", "condensed");
            var axes = FontVariationResolver.Resolve(style, Ctx());
            Assert.That(axes.Count, Is.EqualTo(1));
            Assert.That(axes[0].Tag, Is.EqualTo("wdth"));
            Assert.That(axes[0].Value, Is.EqualTo(75f).Within(1e-3f));
        }

        [Test]
        public void Font_stretch_ultra_expanded_emits_wdth_200() {
            var style = Style();
            style.Set("font-stretch", "ultra-expanded");
            var axes = FontVariationResolver.Resolve(style, Ctx());
            Assert.That(axes.Count, Is.EqualTo(1));
            Assert.That(axes[0].Value, Is.EqualTo(200f).Within(1e-3f));
        }

        [Test]
        public void Font_stretch_normal_emits_no_axis() {
            var style = Style();
            style.Set("font-stretch", "normal");
            var axes = FontVariationResolver.Resolve(style, Ctx());
            Assert.That(axes.Count, Is.EqualTo(0),
                "normal = 100% (axis default); should not appear in the override list");
        }

        [Test]
        public void Font_stretch_percentage_value_passes_through() {
            var style = Style();
            style.SetParsed(CssProperties.GetId("font-stretch"), new CssPercentage(110));
            // Set() needs the raw too for the keyword check path.
            style.Set("font-stretch", "110%");
            var axes = FontVariationResolver.Resolve(style, Ctx());
            Assert.That(axes.Count, Is.EqualTo(1));
            Assert.That(axes[0].Value, Is.EqualTo(110f).Within(1e-3f));
        }

        [Test]
        public void Explicit_wdth_in_variation_settings_wins_over_font_stretch() {
            // Cascade order: an author who writes BOTH font-variation-settings
            // with a `wdth` AND font-stretch should see the explicit axis
            // value win. font-stretch only synthesises when no `wdth`
            // exists in the variation-settings list.
            var style = Style();
            style.Set("font-stretch", "condensed"); // would be 75
            style.SetParsed(CssProperties.FontVariationSettingsId,
                new CssValueList(new List<CssValue> { Axis("wdth", 90.0) },
                                 CssValueListSeparator.Comma));
            var axes = FontVariationResolver.Resolve(style, Ctx());
            Assert.That(axes.Count, Is.EqualTo(1));
            Assert.That(axes[0].Value, Is.EqualTo(90f).Within(1e-3f),
                "explicit wdth=90 should win over keyword condensed (75)");
        }

        [Test]
        public void Font_stretch_keyword_coexists_with_unrelated_axis() {
            // font-stretch synthesises wdth; an unrelated wght axis stays
            // intact alongside it.
            var style = Style();
            style.Set("font-stretch", "expanded");
            style.SetParsed(CssProperties.FontVariationSettingsId,
                new CssValueList(new List<CssValue> { Axis("wght", 350.0) },
                                 CssValueListSeparator.Comma));
            var axes = FontVariationResolver.Resolve(style, Ctx());
            Assert.That(axes.Count, Is.EqualTo(2));
            bool hasWght = false, hasWdth = false;
            foreach (var a in axes) {
                if (a.Tag == "wght") { hasWght = true; Assert.That(a.Value, Is.EqualTo(350f).Within(1e-3f)); }
                if (a.Tag == "wdth") { hasWdth = true; Assert.That(a.Value, Is.EqualTo(125f).Within(1e-3f)); }
            }
            Assert.That(hasWght, Is.True);
            Assert.That(hasWdth, Is.True);
        }
    }
}
