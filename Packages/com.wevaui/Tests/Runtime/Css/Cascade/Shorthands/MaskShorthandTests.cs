using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Css.Cascade.Shorthands;

namespace Weva.Tests.Css.Cascade.Shorthands {
    public class MaskShorthandTests {
        static Dictionary<string, string> Expand(string value) {
            return new MaskShorthandExpander().Expand(value).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        [Test]
        public void Expands_image_position_size_repeat_and_boxes() {
            var d = Expand("linear-gradient(90deg, transparent, black) center / 50% 100% no-repeat padding-box content-box alpha add");

            Assert.That(d["mask-image"], Is.EqualTo("linear-gradient(90deg, transparent, black)"));
            Assert.That(d["mask-position"], Is.EqualTo("center"));
            Assert.That(d["mask-size"], Is.EqualTo("50% 100%"));
            Assert.That(d["mask-repeat"], Is.EqualTo("no-repeat"));
            Assert.That(d["mask-origin"], Is.EqualTo("padding-box"));
            Assert.That(d["mask-clip"], Is.EqualTo("content-box"));
            Assert.That(d["mask-mode"], Is.EqualTo("alpha"));
            Assert.That(d["mask-composite"], Is.EqualTo("add"));
        }

        [Test]
        public void Registry_contains_mask_shorthand() {
            Assert.That(ShorthandRegistry.IsShorthand("mask"), Is.True);
        }
    }
}
