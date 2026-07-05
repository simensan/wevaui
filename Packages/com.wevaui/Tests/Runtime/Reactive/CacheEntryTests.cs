using NUnit.Framework;
using Weva.Reactive;

namespace Weva.Tests.Reactive {
    public class CacheEntryTests {
        [Test]
        public void Default_struct_is_invalid() {
            var c = new CacheEntry<string>();
            Assert.That(c.IsValid, Is.False);
            Assert.That(c.TryGet(0, out _), Is.False);
        }

        [Test]
        public void Set_then_TryGet_with_matching_version_hits() {
            var c = new CacheEntry<int>();
            c.Set(7, 42);
            Assert.That(c.IsValid, Is.True);
            Assert.That(c.TryGet(7, out var v), Is.True);
            Assert.That(v, Is.EqualTo(42));
        }

        [Test]
        public void TryGet_with_different_version_misses() {
            var c = new CacheEntry<int>();
            c.Set(7, 42);
            Assert.That(c.TryGet(8, out _), Is.False);
        }

        [Test]
        public void Invalidate_clears_IsValid_and_makes_TryGet_miss() {
            var c = new CacheEntry<int>();
            c.Set(7, 42);
            c.Invalidate();
            Assert.That(c.IsValid, Is.False);
            Assert.That(c.TryGet(7, out _), Is.False);
        }

        [Test]
        public void Set_updates_value_and_version() {
            var c = new CacheEntry<string>();
            c.Set(1, "a");
            c.Set(2, "b");
            Assert.That(c.IsValid, Is.True);
            Assert.That(c.InputVersion, Is.EqualTo(2));
            Assert.That(c.Value, Is.EqualTo("b"));
            Assert.That(c.TryGet(2, out var v), Is.True);
            Assert.That(v, Is.EqualTo("b"));
        }

        [Test]
        public void TryGet_returns_default_on_miss() {
            var c = new CacheEntry<int>();
            c.Set(1, 99);
            c.TryGet(2, out var v);
            Assert.That(v, Is.EqualTo(0));
        }

        [Test]
        public void Reference_type_value_is_returned_on_hit() {
            var c = new CacheEntry<string>();
            c.Set(3, "hello");
            Assert.That(c.TryGet(3, out var v), Is.True);
            Assert.That(v, Is.EqualTo("hello"));
        }
    }
}
