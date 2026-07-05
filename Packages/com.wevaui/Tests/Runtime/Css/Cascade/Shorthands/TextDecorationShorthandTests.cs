using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Css.Cascade.Shorthands;

namespace Weva.Tests.Css.Cascade.Shorthands {
    public class TextDecorationShorthandTests {
        static Dictionary<string, string> Expand(string value) {
            return new TextDecorationShorthandExpander().Expand(value).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        [Test]
        public void Underline_only_resets_style_and_color() {
            var d = Expand("underline");
            Assert.That(d["text-decoration-line"], Is.EqualTo("underline"));
            Assert.That(d["text-decoration-style"], Is.EqualTo("solid"));
            Assert.That(d["text-decoration-color"], Is.EqualTo("currentcolor"));
        }

        [Test]
        public void Underline_dashed_red() {
            var d = Expand("underline dashed red");
            Assert.That(d["text-decoration-line"], Is.EqualTo("underline"));
            Assert.That(d["text-decoration-style"], Is.EqualTo("dashed"));
            Assert.That(d["text-decoration-color"], Is.EqualTo("red"));
        }

        [Test]
        public void Multiple_lines_combine_into_one_value() {
            var d = Expand("underline overline");
            Assert.That(d["text-decoration-line"], Is.EqualTo("underline overline"));
        }

        [Test]
        public void None_alone() {
            var d = Expand("none");
            Assert.That(d["text-decoration-line"], Is.EqualTo("none"));
        }

        [Test]
        public void Registry_resolves_text_decoration() {
            Assert.That(ShorthandRegistry.IsShorthand("text-decoration"), Is.True);
        }
    }
}
