using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Css.Cascade.Shorthands;

namespace Weva.Tests.Css.Cascade.Shorthands {
    public class FlexShorthandTests {
        static Dictionary<string, string> Expand(string value) {
            return new FlexShorthandExpander().Expand(value).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        [Test]
        public void Flex_one_sets_grow_one_shrink_one_basis_zero() {
            var d = Expand("1");
            Assert.That(d["flex-grow"], Is.EqualTo("1"));
            Assert.That(d["flex-shrink"], Is.EqualTo("1"));
            Assert.That(d["flex-basis"], Is.EqualTo("0"));
        }

        [Test]
        public void Flex_grow_and_shrink_pair_defaults_basis_to_zero() {
            var d = Expand("2 3");
            Assert.That(d["flex-grow"], Is.EqualTo("2"));
            Assert.That(d["flex-shrink"], Is.EqualTo("3"));
            Assert.That(d["flex-basis"], Is.EqualTo("0"));
        }

        [Test]
        public void Flex_full_triplet() {
            var d = Expand("1 1 0");
            Assert.That(d["flex-grow"], Is.EqualTo("1"));
            Assert.That(d["flex-shrink"], Is.EqualTo("1"));
            Assert.That(d["flex-basis"], Is.EqualTo("0"));
        }

        [Test]
        public void Flex_auto_keyword() {
            var d = Expand("auto");
            Assert.That(d["flex-grow"], Is.EqualTo("1"));
            Assert.That(d["flex-shrink"], Is.EqualTo("1"));
            Assert.That(d["flex-basis"], Is.EqualTo("auto"));
        }

        [Test]
        public void Flex_none_keyword() {
            var d = Expand("none");
            Assert.That(d["flex-grow"], Is.EqualTo("0"));
            Assert.That(d["flex-shrink"], Is.EqualTo("0"));
            Assert.That(d["flex-basis"], Is.EqualTo("auto"));
        }

        [Test]
        public void Flex_initial_keyword() {
            var d = Expand("initial");
            Assert.That(d["flex-grow"], Is.EqualTo("0"));
            Assert.That(d["flex-shrink"], Is.EqualTo("1"));
            Assert.That(d["flex-basis"], Is.EqualTo("auto"));
        }

        [Test]
        public void Flex_zero_zero_one_hundred_pixels() {
            var d = Expand("0 0 100px");
            Assert.That(d["flex-grow"], Is.EqualTo("0"));
            Assert.That(d["flex-shrink"], Is.EqualTo("0"));
            Assert.That(d["flex-basis"], Is.EqualTo("100px"));
        }

        [Test]
        public void Flex_with_basis_only_defaults_grow_and_shrink() {
            var d = Expand("100px");
            Assert.That(d["flex-grow"], Is.EqualTo("1"));
            Assert.That(d["flex-shrink"], Is.EqualTo("1"));
            Assert.That(d["flex-basis"], Is.EqualTo("100px"));
        }

        [Test]
        public void Flex_with_percentage_basis() {
            var d = Expand("50%");
            Assert.That(d["flex-basis"], Is.EqualTo("50%"));
        }

        [Test]
        public void Empty_value_yields_nothing() {
            var d = Expand("");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Registry_resolves_flex() {
            Assert.That(ShorthandRegistry.IsShorthand("flex"), Is.True);
        }
    }
}
