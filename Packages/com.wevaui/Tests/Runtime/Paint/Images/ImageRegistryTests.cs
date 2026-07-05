using NUnit.Framework;
using Weva.Paint.Images;

namespace Weva.Tests.Paint.Images {
    public class ImageRegistryTests {
        sealed class StubSource : IImageSource {
            public StubSource(int w, int h) { Width = w; Height = h; }
            public int Width { get; }
            public int Height { get; }
        }

        [Test]
        public void Register_then_resolve_returns_same_instance() {
            var reg = new InMemoryImageRegistry();
            var src = new StubSource(64, 64);
            reg.Register("ui/heart", src);
            Assert.That(reg.TryResolve("ui/heart", out var got), Is.True);
            Assert.That(got, Is.SameAs(src));
        }

        [Test]
        public void Mutations_increment_version() {
            var reg = new InMemoryImageRegistry();
            Assert.That(reg.Version, Is.EqualTo(0));

            reg.Register("a", new StubSource(1, 1));
            Assert.That(reg.Version, Is.EqualTo(1));

            reg.Register("a", new StubSource(2, 2));
            Assert.That(reg.Version, Is.EqualTo(2));

            Assert.That(reg.Unregister("missing"), Is.False);
            Assert.That(reg.Version, Is.EqualTo(2));

            Assert.That(reg.Unregister("a"), Is.True);
            Assert.That(reg.Version, Is.EqualTo(3));

            reg.Clear();
            Assert.That(reg.Version, Is.EqualTo(3));

            reg.Register("b", new StubSource(3, 3));
            reg.Clear();
            Assert.That(reg.Version, Is.EqualTo(5));
        }

        [Test]
        public void Register_same_source_again_does_not_increment_version() {
            var reg = new InMemoryImageRegistry();
            var src = new StubSource(1, 1);
            reg.Register("a", src);
            Assert.That(reg.Version, Is.EqualTo(1));

            reg.Register("a", src);
            Assert.That(reg.Version, Is.EqualTo(1));
        }

#if UNITY_5_3_OR_NEWER
        [Test]
        public void Register_equivalent_texture_source_again_does_not_increment_version() {
            var tex = new UnityEngine.Texture2D(8, 8);
            try {
                var reg = new InMemoryImageRegistry();
                reg.Register("a", new Texture2DImageSource(tex));
                Assert.That(reg.Version, Is.EqualTo(1));

                reg.Register("a", new Texture2DImageSource(tex));
                Assert.That(reg.Version, Is.EqualTo(1));
            } finally {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }
#endif

        [Test]
        public void Resolve_unknown_handle_returns_false() {
            var reg = new InMemoryImageRegistry();
            Assert.That(reg.TryResolve("missing", out var got), Is.False);
            Assert.That(got, Is.Null);
        }

        [Test]
        public void Resolve_null_or_empty_handle_returns_false() {
            var reg = new InMemoryImageRegistry();
            reg.Register("x", new StubSource(1, 1));
            Assert.That(reg.TryResolve(null, out _), Is.False);
            Assert.That(reg.TryResolve("", out _), Is.False);
        }

        [Test]
        public void Register_overwrites_existing() {
            var reg = new InMemoryImageRegistry();
            var a = new StubSource(1, 1);
            var b = new StubSource(2, 2);
            reg.Register("k", a);
            reg.Register("k", b);
            reg.TryResolve("k", out var got);
            Assert.That(got, Is.SameAs(b));
        }

        [Test]
        public void Unregister_removes_entry() {
            var reg = new InMemoryImageRegistry();
            reg.Register("k", new StubSource(1, 1));
            Assert.That(reg.Unregister("k"), Is.True);
            Assert.That(reg.TryResolve("k", out _), Is.False);
        }

        [Test]
        public void Unregister_unknown_returns_false() {
            var reg = new InMemoryImageRegistry();
            Assert.That(reg.Unregister("missing"), Is.False);
        }

        [Test]
        public void Clear_removes_all_entries() {
            var reg = new InMemoryImageRegistry();
            reg.Register("a", new StubSource(1, 1));
            reg.Register("b", new StubSource(2, 2));
            Assert.That(reg.Count, Is.EqualTo(2));
            reg.Clear();
            Assert.That(reg.Count, Is.EqualTo(0));
            Assert.That(reg.TryResolve("a", out _), Is.False);
        }

        [Test]
        public void Register_null_or_empty_handle_throws() {
            var reg = new InMemoryImageRegistry();
            Assert.Throws<System.ArgumentException>(() => reg.Register(null, new StubSource(1, 1)));
            Assert.Throws<System.ArgumentException>(() => reg.Register("", new StubSource(1, 1)));
        }

        [Test]
        public void Register_null_source_throws() {
            var reg = new InMemoryImageRegistry();
            Assert.Throws<System.ArgumentNullException>(() => reg.Register("k", null));
        }

        [Test]
        public void Handles_are_case_sensitive() {
            // Image handles are opaque keys; case sensitivity matches Unity's
            // own asset addressing (which is case-sensitive on most platforms).
            var reg = new InMemoryImageRegistry();
            reg.Register("UI/Heart", new StubSource(1, 1));
            Assert.That(reg.TryResolve("UI/Heart", out _), Is.True);
            Assert.That(reg.TryResolve("ui/heart", out _), Is.False);
        }
    }
}
