using NUnit.Framework;
using Weva.Layout.AnchorPositioning;

namespace Weva.Tests.Layout.AnchorPositioning {
    public class AnchorFunctionParserTests {
        [Test]
        public void Parses_single_edge_keyword() {
            Assert.That(AnchorFunctionParser.TryParse("anchor(bottom)", out var c), Is.True);
            Assert.That(c.AnchorName, Is.Null);
            Assert.That(c.Edge, Is.EqualTo(AnchorEdge.Bottom));
            Assert.That(c.OffsetPx, Is.EqualTo(0));
        }

        [Test]
        public void Parses_named_anchor_with_edge() {
            Assert.That(AnchorFunctionParser.TryParse("anchor(--tip bottom)", out var c), Is.True);
            Assert.That(c.AnchorName, Is.EqualTo("--tip"));
            Assert.That(c.Edge, Is.EqualTo(AnchorEdge.Bottom));
        }

        [Test]
        public void Parses_offset_with_plus_sign() {
            Assert.That(AnchorFunctionParser.TryParse("anchor(bottom + 8px)", out var c), Is.True);
            Assert.That(c.Edge, Is.EqualTo(AnchorEdge.Bottom));
            Assert.That(c.OffsetPx, Is.EqualTo(8));
        }

        [Test]
        public void Parses_offset_with_minus_sign() {
            Assert.That(AnchorFunctionParser.TryParse("anchor(--tip top - 4px)", out var c), Is.True);
            Assert.That(c.AnchorName, Is.EqualTo("--tip"));
            Assert.That(c.Edge, Is.EqualTo(AnchorEdge.Top));
            Assert.That(c.OffsetPx, Is.EqualTo(-4));
        }

        [Test]
        public void Rejects_non_anchor_function() {
            Assert.That(AnchorFunctionParser.TryParse("calc(10px)", out _), Is.False);
            Assert.That(AnchorFunctionParser.TryParse("100px", out _), Is.False);
        }

        [Test]
        public void Rejects_unknown_edge() {
            Assert.That(AnchorFunctionParser.TryParse("anchor(diagonal)", out _), Is.False);
        }

        [Test]
        public void Recognises_all_edges() {
            string[] keywords = { "top", "bottom", "left", "right", "start", "end", "self-start", "self-end", "center" };
            foreach (var k in keywords) {
                Assert.That(AnchorFunctionParser.TryParse($"anchor({k})", out _), Is.True, $"keyword {k}");
            }
        }
    }
}
