using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Paint.Conversion;

namespace Weva.Tests.Paint.Conversion {
    // Locks in CSS Text Module Level 3 §3 (text-transform) and §10.1
    // (letter-spacing) resolution at the paint-conversion seam. The layout-side
    // counterparts in InlineLayoutTests verify the inline formatting context
    // applies the same values; these tests pin the resolver so a regression in
    // either path is caught at the right level.
    public class TextRunResolverTests {
        static ComputedStyle Style() => new ComputedStyle(new Element("p"));
        static LengthContext Ctx() {
            var c = LengthContext.Default;
            c.BaseFontSizePx = 16;
            c.RootFontSizePx = 16;
            return c;
        }

        // letter-spacing -----------------------------------------------------

        // font-kerning (CSS Fonts L4 §6.5) ----------------------------------
        //
        // Only the explicit `none` keyword disables kerning. `auto` (initial),
        // `normal`, the unset state, and unknown future values all leave it
        // on — `auto` defers to UA discretion and the engine kerns by
        // default. The unknown-value fallback is intentional so author code
        // that rolls in new keywords doesn't silently lose kerning.

        [Test]
        public void Font_kerning_unset_defaults_to_enabled() {
            var s = Style();
            Assert.That(TextRunResolver.ResolveKerningEnabled(s), Is.True);
        }

        [Test]
        public void Font_kerning_auto_keeps_kerning_enabled() {
            var s = Style();
            s.Set("font-kerning", "auto");
            Assert.That(TextRunResolver.ResolveKerningEnabled(s), Is.True);
        }

        [Test]
        public void Font_kerning_normal_keeps_kerning_enabled() {
            var s = Style();
            s.Set("font-kerning", "normal");
            Assert.That(TextRunResolver.ResolveKerningEnabled(s), Is.True);
        }

        [Test]
        public void Font_kerning_none_disables_kerning() {
            var s = Style();
            s.Set("font-kerning", "none");
            Assert.That(TextRunResolver.ResolveKerningEnabled(s), Is.False);
        }

        [Test]
        public void Font_kerning_none_case_insensitive() {
            // CSS keywords are ASCII-case-insensitive. Authors who scream-cap
            // their resets shouldn't accidentally re-enable kerning.
            var s = Style();
            s.Set("font-kerning", "NONE");
            Assert.That(TextRunResolver.ResolveKerningEnabled(s), Is.False);
        }

        [Test]
        public void Font_kerning_unknown_value_stays_enabled() {
            // Defensive: an unsupported future value (or a typo) shouldn't
            // silently disable kerning. Auto behaviour wins.
            var s = Style();
            s.Set("font-kerning", "always-please");
            Assert.That(TextRunResolver.ResolveKerningEnabled(s), Is.True);
        }

        [Test]
        public void Font_kerning_null_style_returns_enabled() {
            Assert.That(TextRunResolver.ResolveKerningEnabled(null), Is.True);
        }

        // letter-spacing -----------------------------------------------------

        [Test]
        public void Letter_spacing_empty_resolves_to_zero() {
            var s = Style();
            Assert.That(TextRunResolver.ResolveLetterSpacingPx(s, Ctx()), Is.EqualTo(0));
        }

        [Test]
        public void Letter_spacing_normal_resolves_to_zero() {
            var s = Style();
            s.Set("letter-spacing", "normal");
            Assert.That(TextRunResolver.ResolveLetterSpacingPx(s, Ctx()), Is.EqualTo(0));
        }

        [Test]
        public void Letter_spacing_pixels_passes_through() {
            var s = Style();
            s.Set("letter-spacing", "3px");
            Assert.That(TextRunResolver.ResolveLetterSpacingPx(s, Ctx()), Is.EqualTo(3).Within(1e-9));
        }

        [Test]
        public void Letter_spacing_em_resolves_against_base_font_size() {
            // .25em at 16px font-size → 4 px.
            var s = Style();
            s.Set("letter-spacing", ".25em");
            Assert.That(TextRunResolver.ResolveLetterSpacingPx(s, Ctx()), Is.EqualTo(4).Within(1e-9));
        }

        [Test]
        public void Letter_spacing_negative_value_round_trips() {
            // CSS allows negative letter-spacing for tighter tracking.
            var s = Style();
            s.Set("letter-spacing", "-1px");
            Assert.That(TextRunResolver.ResolveLetterSpacingPx(s, Ctx()), Is.EqualTo(-1).Within(1e-9));
        }

        // text-transform -----------------------------------------------------

        [Test]
        public void Text_transform_none_returns_input_unchanged() {
            var s = Style();
            s.Set("text-transform", "none");
            Assert.That(TextRunResolver.ApplyTextTransform(s, "Hello World"), Is.EqualTo("Hello World"));
        }

        [Test]
        public void Text_transform_unset_returns_input_unchanged() {
            var s = Style();
            Assert.That(TextRunResolver.ApplyTextTransform(s, "Hello World"), Is.EqualTo("Hello World"));
        }

        [Test]
        public void Text_transform_uppercase_uppercases_all_letters() {
            var s = Style();
            s.Set("text-transform", "uppercase");
            Assert.That(TextRunResolver.ApplyTextTransform(s, "Hello"), Is.EqualTo("HELLO"));
        }

        [Test]
        public void Text_transform_lowercase_lowercases_all_letters() {
            var s = Style();
            s.Set("text-transform", "lowercase");
            Assert.That(TextRunResolver.ApplyTextTransform(s, "HELLO World"), Is.EqualTo("hello world"));
        }

        [Test]
        public void Text_transform_capitalize_uppercases_first_letter_of_each_word() {
            var s = Style();
            s.Set("text-transform", "capitalize");
            Assert.That(TextRunResolver.ApplyTextTransform(s, "hello world from css"),
                Is.EqualTo("Hello World From Css"));
        }

        [Test]
        public void Text_transform_capitalize_preserves_existing_casing_inside_words() {
            // CSS Text §3: capitalize only touches the first typographic letter
            // of each word. Inside-word casing is left as-is.
            var s = Style();
            s.Set("text-transform", "capitalize");
            Assert.That(TextRunResolver.ApplyTextTransform(s, "iPhone XS"), Is.EqualTo("IPhone XS"));
        }

        [Test]
        public void Text_transform_handles_empty_string() {
            var s = Style();
            s.Set("text-transform", "uppercase");
            Assert.That(TextRunResolver.ApplyTextTransform(s, ""), Is.EqualTo(""));
        }
    }
}
