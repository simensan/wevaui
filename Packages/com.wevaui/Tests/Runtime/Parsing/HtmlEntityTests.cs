using NUnit.Framework;
using Weva.Parsing;

namespace Weva.Tests.Parsing {
    public class HtmlEntityTests {
        [TestCase("amp", "&")]
        [TestCase("lt", "<")]
        [TestCase("gt", ">")]
        [TestCase("quot", "\"")]
        [TestCase("apos", "'")]
        [TestCase("shy", "\u00AD")]
        [TestCase("nbsp", " ")]
        [TestCase("copy", "©")]
        [TestCase("mdash", "—")]
        [TestCase("hellip", "…")]
        public void Lookup_returns_known_entity(string name, string expected) {
            Assert.That(HtmlEntities.Lookup(name, out var v), Is.True);
            Assert.That(v, Is.EqualTo(expected));
        }

        [Test]
        public void Lookup_returns_false_for_unknown() {
            Assert.That(HtmlEntities.Lookup("notarealentity", out _), Is.False);
        }

        [Test]
        public void Lookup_is_case_sensitive() {
            Assert.That(HtmlEntities.Lookup("AMP", out _), Is.False);
        }
    }
}
