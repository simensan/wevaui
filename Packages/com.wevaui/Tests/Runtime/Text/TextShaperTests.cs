using NUnit.Framework;
using Weva.Layout.Text;
using Weva.Text.TextCore;

namespace Weva.Tests.Text {
    public class TextShaperTests {
        static readonly MonoFontMetrics Mono = new MonoFontMetrics();

        [Test]
        public void Empty_string_produces_no_glyphs() {
            var glyphs = TextShaper.Shape("", Mono, 16);
            Assert.That(glyphs.Count, Is.EqualTo(0));
        }

        [Test]
        public void Null_string_produces_no_glyphs() {
            var glyphs = TextShaper.Shape(null, Mono, 16);
            Assert.That(glyphs.Count, Is.EqualTo(0));
        }

        [Test]
        public void Ascii_string_one_glyph_per_char() {
            var glyphs = TextShaper.Shape("hello", Mono, 16);
            Assert.That(glyphs.Count, Is.EqualTo(5));
            Assert.That(glyphs[0].Codepoint, Is.EqualTo((uint)'h'));
            Assert.That(glyphs[1].Codepoint, Is.EqualTo((uint)'e'));
            Assert.That(glyphs[2].Codepoint, Is.EqualTo((uint)'l'));
            Assert.That(glyphs[3].Codepoint, Is.EqualTo((uint)'l'));
            Assert.That(glyphs[4].Codepoint, Is.EqualTo((uint)'o'));
        }

        [Test]
        public void Each_glyph_carries_advance() {
            var glyphs = TextShaper.Shape("abc", Mono, 20);
            // MonoFontMetrics: 0.5em width → 0.5 * 20 = 10.
            foreach (var g in glyphs) Assert.That(g.AdvancePx, Is.EqualTo(10).Within(1e-9));
        }

        [Test]
        public void Total_advance_matches_metrics_measure() {
            var glyphs = TextShaper.Shape("hello world", Mono, 16);
            double sum = TextShaper.MeasureShaped(glyphs);
            // Shaper skips no characters from "hello world" (space is a glyph),
            // and Mono measures each char as 8px → 11 * 8 = 88.
            Assert.That(sum, Is.EqualTo(11 * 8).Within(1e-9));
        }

        [Test]
        public void Surrogate_pair_counted_once() {
            string text = char.ConvertFromUtf32(0x1F600);
            Assert.That(text.Length, Is.EqualTo(2));
            var glyphs = TextShaper.Shape(text, Mono, 16);
            Assert.That(glyphs.Count, Is.EqualTo(1));
            Assert.That(glyphs[0].Codepoint, Is.EqualTo(0x1F600u));
            Assert.That(glyphs[0].SourceCharIndex, Is.EqualTo(0));
            Assert.That(glyphs[0].SourceCharLength, Is.EqualTo(2));
        }

        [Test]
        public void Ascii_glyph_has_length_one() {
            var glyphs = TextShaper.Shape("a", Mono, 16);
            Assert.That(glyphs[0].SourceCharLength, Is.EqualTo(1));
        }

        [Test]
        public void Tab_and_newline_skipped() {
            var glyphs = TextShaper.Shape("a\tb\nc\rd", Mono, 16);
            Assert.That(glyphs.Count, Is.EqualTo(4));
            Assert.That(glyphs[0].Codepoint, Is.EqualTo((uint)'a'));
            Assert.That(glyphs[1].Codepoint, Is.EqualTo((uint)'b'));
            Assert.That(glyphs[2].Codepoint, Is.EqualTo((uint)'c'));
            Assert.That(glyphs[3].Codepoint, Is.EqualTo((uint)'d'));
        }

        [Test]
        public void Whitespace_other_than_tab_newline_is_shaped() {
            var glyphs = TextShaper.Shape("a b", Mono, 16);
            Assert.That(glyphs.Count, Is.EqualTo(3));
            Assert.That(glyphs[1].Codepoint, Is.EqualTo((uint)' '));
        }

        [Test]
        public void ShapeInto_appends_to_existing_list() {
            var list = new System.Collections.Generic.List<TextShaper.ShapedGlyph>();
            TextShaper.ShapeInto("ab", Mono, 16, list);
            TextShaper.ShapeInto("cd", Mono, 16, list);
            Assert.That(list.Count, Is.EqualTo(4));
            Assert.That(list[0].Codepoint, Is.EqualTo((uint)'a'));
            Assert.That(list[3].Codepoint, Is.EqualTo((uint)'d'));
        }

        [Test]
        public void Source_indices_track_through_surrogates() {
            string text = "a" + char.ConvertFromUtf32(0x1F600) + "b";
            var glyphs = TextShaper.Shape(text, Mono, 16);
            Assert.That(glyphs.Count, Is.EqualTo(3));
            Assert.That(glyphs[0].SourceCharIndex, Is.EqualTo(0));
            Assert.That(glyphs[1].SourceCharIndex, Is.EqualTo(1));
            Assert.That(glyphs[1].SourceCharLength, Is.EqualTo(2));
            Assert.That(glyphs[2].SourceCharIndex, Is.EqualTo(3));
        }
    }
}
