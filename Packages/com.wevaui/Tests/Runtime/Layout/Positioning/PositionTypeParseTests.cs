using NUnit.Framework;
using Weva.Layout.Positioning;

namespace Weva.Tests.Layout.Positioning {
    public class PositionTypeParseTests {
        [Test]
        public void Static_keyword_parses() {
            Assert.That(PositionedExtensions.ParsePositionType("static"), Is.EqualTo(PositionType.Static));
        }

        [Test]
        public void Relative_keyword_parses() {
            Assert.That(PositionedExtensions.ParsePositionType("relative"), Is.EqualTo(PositionType.Relative));
        }

        [Test]
        public void Absolute_keyword_parses() {
            Assert.That(PositionedExtensions.ParsePositionType("absolute"), Is.EqualTo(PositionType.Absolute));
        }

        [Test]
        public void Fixed_keyword_parses() {
            Assert.That(PositionedExtensions.ParsePositionType("fixed"), Is.EqualTo(PositionType.Fixed));
        }

        [Test]
        public void Sticky_keyword_parses() {
            Assert.That(PositionedExtensions.ParsePositionType("sticky"), Is.EqualTo(PositionType.Sticky));
        }

        [Test]
        public void Default_and_unknown_values_fall_back_to_static() {
            Assert.That(PositionedExtensions.ParsePositionType(null), Is.EqualTo(PositionType.Static));
            Assert.That(PositionedExtensions.ParsePositionType(""), Is.EqualTo(PositionType.Static));
            Assert.That(PositionedExtensions.ParsePositionType("not-a-real-value"), Is.EqualTo(PositionType.Static));
            Assert.That(PositionedExtensions.ParsePositionType("RELATIVE"), Is.EqualTo(PositionType.Relative));
        }
    }
}
