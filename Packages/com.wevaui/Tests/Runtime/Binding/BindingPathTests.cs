using NUnit.Framework;
using Weva.Binding;

namespace Weva.Tests.Binding {
    public class BindingPathTests {
        [Test]
        public void Single_segment() {
            var p = BindingPath.Parse("Foo");
            Assert.That(p.Count, Is.EqualTo(1));
            Assert.That(p.Segments[0], Is.EqualTo("Foo"));
        }

        [Test]
        public void Dotted_path_two_segments() {
            var p = BindingPath.Parse("Foo.Bar");
            Assert.That(p.Count, Is.EqualTo(2));
            Assert.That(p.Segments[0], Is.EqualTo("Foo"));
            Assert.That(p.Segments[1], Is.EqualTo("Bar"));
        }

        [Test]
        public void Dotted_path_three_segments() {
            var p = BindingPath.Parse("Foo.Bar.Baz");
            Assert.That(p.Count, Is.EqualTo(3));
            Assert.That(p.Segments[2], Is.EqualTo("Baz"));
        }

        [Test]
        public void Whitespace_around_path_is_tolerated() {
            var p = BindingPath.Parse("   Foo  ");
            Assert.That(p.Count, Is.EqualTo(1));
            Assert.That(p.Segments[0], Is.EqualTo("Foo"));
        }

        [Test]
        public void Whitespace_around_segments_is_tolerated() {
            var p = BindingPath.Parse(" Foo . Bar ");
            Assert.That(p.Count, Is.EqualTo(2));
            Assert.That(p.Segments[0], Is.EqualTo("Foo"));
            Assert.That(p.Segments[1], Is.EqualTo("Bar"));
        }

        [Test]
        public void Empty_throws() {
            Assert.Throws<BindingException>(() => BindingPath.Parse(""));
            Assert.Throws<BindingException>(() => BindingPath.Parse("   "));
        }

        [Test]
        public void Null_throws() {
            Assert.Throws<BindingException>(() => BindingPath.Parse(null));
        }

        [Test]
        public void Whitespace_inside_segment_is_invalid() {
            Assert.Throws<BindingException>(() => BindingPath.Parse("Foo Bar"));
        }

        [Test]
        public void Empty_segment_between_dots_is_invalid() {
            Assert.Throws<BindingException>(() => BindingPath.Parse("Foo..Bar"));
        }

        [Test]
        public void Leading_dot_is_invalid() {
            Assert.Throws<BindingException>(() => BindingPath.Parse(".Foo"));
        }

        [Test]
        public void Trailing_dot_is_invalid() {
            Assert.Throws<BindingException>(() => BindingPath.Parse("Foo."));
        }

        [Test]
        public void Special_characters_invalid() {
            Assert.Throws<BindingException>(() => BindingPath.Parse("Foo-Bar"));
            Assert.Throws<BindingException>(() => BindingPath.Parse("Foo$"));
            Assert.Throws<BindingException>(() => BindingPath.Parse("123Foo"));
        }

        [Test]
        public void Underscore_and_digits_after_first_char_allowed() {
            var p = BindingPath.Parse("_foo123");
            Assert.That(p.Segments[0], Is.EqualTo("_foo123"));
        }

        [Test]
        public void ToString_round_trips() {
            var p = BindingPath.Parse("Foo.Bar.Baz");
            Assert.That(p.ToString(), Is.EqualTo("Foo.Bar.Baz"));
        }

        [Test]
        public void ToString_normalizes_whitespace() {
            var p = BindingPath.Parse("  Foo .  Bar  ");
            Assert.That(p.ToString(), Is.EqualTo("Foo.Bar"));
        }

        [Test]
        public void Equality_same_segments() {
            var a = BindingPath.Parse("Foo.Bar");
            var b = BindingPath.Parse("Foo.Bar");
            Assert.That(a == b, Is.True);
            Assert.That(a.Equals(b), Is.True);
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
        }

        [Test]
        public void Inequality_different_segments() {
            var a = BindingPath.Parse("Foo.Bar");
            var b = BindingPath.Parse("Foo.Baz");
            Assert.That(a != b, Is.True);
        }
    }
}
