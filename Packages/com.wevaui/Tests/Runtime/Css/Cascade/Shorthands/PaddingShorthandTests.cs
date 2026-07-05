using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Css.Cascade.Shorthands;

namespace Weva.Tests.Css.Cascade.Shorthands {
    public class PaddingShorthandTests {
        static Dictionary<string, string> Expand(string value) {
            var ex = MarginShorthandExpander.Padding();
            return ex.Expand(value).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        [Test]
        public void One_value_applies_to_all_four_edges() {
            var d = Expand("24px");
            Assert.That(d["padding-top"], Is.EqualTo("24px"));
            Assert.That(d["padding-right"], Is.EqualTo("24px"));
            Assert.That(d["padding-bottom"], Is.EqualTo("24px"));
            Assert.That(d["padding-left"], Is.EqualTo("24px"));
        }

        [Test]
        public void Two_values_split_vertically_and_horizontally() {
            var d = Expand("4px 12px");
            Assert.That(d["padding-top"], Is.EqualTo("4px"));
            Assert.That(d["padding-bottom"], Is.EqualTo("4px"));
            Assert.That(d["padding-right"], Is.EqualTo("12px"));
            Assert.That(d["padding-left"], Is.EqualTo("12px"));
        }

        [Test]
        public void Three_values_set_top_horizontal_bottom() {
            var d = Expand("1px 2px 3px");
            Assert.That(d["padding-top"], Is.EqualTo("1px"));
            Assert.That(d["padding-right"], Is.EqualTo("2px"));
            Assert.That(d["padding-left"], Is.EqualTo("2px"));
            Assert.That(d["padding-bottom"], Is.EqualTo("3px"));
        }

        [Test]
        public void Four_values_set_top_right_bottom_left() {
            var d = Expand("1px 2px 3px 4px");
            Assert.That(d["padding-top"], Is.EqualTo("1px"));
            Assert.That(d["padding-right"], Is.EqualTo("2px"));
            Assert.That(d["padding-bottom"], Is.EqualTo("3px"));
            Assert.That(d["padding-left"], Is.EqualTo("4px"));
        }

        [Test]
        public void Auto_is_rejected_for_padding() {
            var d = Expand("auto");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Negative_values_are_passed_through() {
            // Padding clamping is a layout concern; the cascade just stores the string.
            var d = Expand("-1px");
            Assert.That(d["padding-top"], Is.EqualTo("-1px"));
        }

        [Test]
        public void Percentage_is_accepted() {
            var d = Expand("25%");
            Assert.That(d["padding-top"], Is.EqualTo("25%"));
        }

        [Test]
        public void Five_values_are_rejected() {
            var d = Expand("1px 2px 3px 4px 5px");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Empty_value_yields_nothing() {
            var d = Expand("");
            Assert.That(d.Count, Is.EqualTo(0));
        }

        [Test]
        public void Registry_resolves_padding() {
            Assert.That(ShorthandRegistry.IsShorthand("padding"), Is.True);
        }
    }
}
