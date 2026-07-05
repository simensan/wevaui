using NUnit.Framework;
using Weva.Css.Values;
using Weva.Layout.Flex;

namespace Weva.Tests.Layout.Flex {
    public class FlexShorthandTests {
        static FlexShorthand.Result Parse(string raw) {
            return FlexShorthand.Parse(raw, LengthContext.Default);
        }

        [Test]
        public void Flex_none_becomes_0_0_auto() {
            var r = Parse("none");
            Assert.That(r.HasValue, Is.True);
            Assert.That(r.Grow, Is.EqualTo(0).Within(0.001));
            Assert.That(r.Shrink, Is.EqualTo(0).Within(0.001));
            Assert.That(r.Basis.Kind, Is.EqualTo(FlexBasisKind.Auto));
        }

        [Test]
        public void Flex_auto_becomes_1_1_auto() {
            var r = Parse("auto");
            Assert.That(r.HasValue, Is.True);
            Assert.That(r.Grow, Is.EqualTo(1).Within(0.001));
            Assert.That(r.Shrink, Is.EqualTo(1).Within(0.001));
            Assert.That(r.Basis.Kind, Is.EqualTo(FlexBasisKind.Auto));
        }

        [Test]
        public void Flex_one_becomes_1_1_0() {
            var r = Parse("1");
            Assert.That(r.HasValue, Is.True);
            Assert.That(r.Grow, Is.EqualTo(1).Within(0.001));
            Assert.That(r.Shrink, Is.EqualTo(1).Within(0.001));
            Assert.That(r.Basis.Kind, Is.EqualTo(FlexBasisKind.Length));
            Assert.That(r.Basis.Value, Is.EqualTo(0).Within(0.001));
        }

        [Test]
        public void Flex_grow_shrink_basis_triple() {
            var r = Parse("1 0 auto");
            Assert.That(r.Grow, Is.EqualTo(1).Within(0.001));
            Assert.That(r.Shrink, Is.EqualTo(0).Within(0.001));
            Assert.That(r.Basis.Kind, Is.EqualTo(FlexBasisKind.Auto));
        }

        [Test]
        public void Flex_two_zero_auto() {
            var r = Parse("2 0 auto");
            Assert.That(r.Grow, Is.EqualTo(2).Within(0.001));
            Assert.That(r.Shrink, Is.EqualTo(0).Within(0.001));
            Assert.That(r.Basis.Kind, Is.EqualTo(FlexBasisKind.Auto));
        }

        [Test]
        public void Flex_with_only_length_means_1_1_length() {
            var r = Parse("100px");
            Assert.That(r.Grow, Is.EqualTo(1).Within(0.001));
            Assert.That(r.Shrink, Is.EqualTo(1).Within(0.001));
            Assert.That(r.Basis.Kind, Is.EqualTo(FlexBasisKind.Length));
            Assert.That(r.Basis.Value, Is.EqualTo(100).Within(0.001));
        }

        [Test]
        public void Flex_grow_with_basis_length() {
            var r = Parse("0 0 100px");
            Assert.That(r.Grow, Is.EqualTo(0).Within(0.001));
            Assert.That(r.Shrink, Is.EqualTo(0).Within(0.001));
            Assert.That(r.Basis.Kind, Is.EqualTo(FlexBasisKind.Length));
            Assert.That(r.Basis.Value, Is.EqualTo(100).Within(0.001));
        }

        [Test]
        public void Flex_initial_is_0_1_auto() {
            var r = Parse("initial");
            Assert.That(r.Grow, Is.EqualTo(0).Within(0.001));
            Assert.That(r.Shrink, Is.EqualTo(1).Within(0.001));
            Assert.That(r.Basis.Kind, Is.EqualTo(FlexBasisKind.Auto));
        }

        [Test]
        public void Flex_grow_shrink_no_basis_defaults_to_zero_length() {
            var r = Parse("2 3");
            Assert.That(r.Grow, Is.EqualTo(2).Within(0.001));
            Assert.That(r.Shrink, Is.EqualTo(3).Within(0.001));
            Assert.That(r.Basis.Kind, Is.EqualTo(FlexBasisKind.Length));
            Assert.That(r.Basis.Value, Is.EqualTo(0).Within(0.001));
        }

        [Test]
        public void Flex_basis_percentage() {
            var r = Parse("1 1 50%");
            Assert.That(r.Grow, Is.EqualTo(1).Within(0.001));
            Assert.That(r.Shrink, Is.EqualTo(1).Within(0.001));
            Assert.That(r.Basis.Kind, Is.EqualTo(FlexBasisKind.Percentage));
            Assert.That(r.Basis.Value, Is.EqualTo(50).Within(0.001));
        }
    }
}
