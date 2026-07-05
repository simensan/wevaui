using System.Text.RegularExpressions;
using NUnit.Framework;
using Weva.Components.Scoping;

namespace Weva.Tests.Components.Scoping {
    public class ScopeIdTests {
        [Test]
        public void Same_name_produces_same_id() {
            var a = ScopeId.Generate("card");
            var b = ScopeId.Generate("card");
            Assert.That(a.Value, Is.EqualTo(b.Value));
            Assert.That(a, Is.EqualTo(b));
        }

        [Test]
        public void Different_names_produce_different_ids() {
            var a = ScopeId.Generate("card");
            var b = ScopeId.Generate("hero");
            Assert.That(a.Value, Is.Not.EqualTo(b.Value));
            Assert.That(a, Is.Not.EqualTo(b));
        }

        [Test]
        public void Id_value_is_safe_attribute_value() {
            var id = ScopeId.Generate("card");
            Assert.That(Regex.IsMatch(id.Value, "^[a-z0-9_-]+$"), Is.True, "id must consist of alphanumerics, dashes, underscores; got: " + id.Value);
        }

        [Test]
        public void Id_starts_with_uui_sc_prefix() {
            var id = ScopeId.Generate("card");
            Assert.That(id.Value.StartsWith("uui-sc-"), Is.True);
        }

        [Test]
        public void None_is_empty() {
            Assert.That(ScopeId.None.IsEmpty, Is.True);
            var id = ScopeId.Generate("card");
            Assert.That(id.IsEmpty, Is.False);
        }

        [Test]
        public void Generate_with_uppercase_yields_lowercase_segment() {
            var a = ScopeId.Generate("Card");
            // Sanitised name segment is lowercase even if input has caps.
            Assert.That(a.Value, Does.Match("^uui-sc-card-[0-9a-f]+$"));
        }

        [Test]
        public void From_round_trips_value() {
            var id = ScopeId.From("uui-sc-card-deadbeef");
            Assert.That(id.Value, Is.EqualTo("uui-sc-card-deadbeef"));
            Assert.That(id.IsEmpty, Is.False);
        }

        [Test]
        public void Names_with_special_chars_are_sanitised() {
            var id = ScopeId.Generate("foo bar/baz");
            Assert.That(Regex.IsMatch(id.Value, "^[a-z0-9_-]+$"), Is.True);
        }
    }
}
