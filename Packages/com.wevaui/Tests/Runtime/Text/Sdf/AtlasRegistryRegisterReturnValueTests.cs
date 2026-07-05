using NUnit.Framework;
using Weva.Text.Sdf;
using Weva.Text.TextCore;

namespace Weva.Tests.Text.Sdf {
    // NG4 — AtlasRegistry.RegisterAtlas previously returned void on a skip
    // (invalid face / null atlas), so callers had no signal their atlas
    // binding was dropped. Now returns bool: true on success, false on skip.
    public class AtlasRegistryRegisterReturnValueTests {
        [SetUp]
        public void Reset() {
            AtlasRegistry.Clear();
        }

        [Test]
        public void RegisterAtlas_returns_true_on_happy_path_NG4() {
            var face = new FaceInfo("F", "/p", 400, FaceInfo.StyleNormal);
            var ok = AtlasRegistry.RegisterAtlas(face, new GlyphAtlas());
            Assert.That(ok, Is.True);
            Assert.That(AtlasRegistry.Count, Is.EqualTo(1));
        }

        [Test]
        public void RegisterAtlas_returns_false_on_invalid_face_NG4() {
            var ok = AtlasRegistry.RegisterAtlas(FaceInfo.Empty, new GlyphAtlas());
            Assert.That(ok, Is.False);
            Assert.That(AtlasRegistry.Count, Is.EqualTo(0));
        }

        [Test]
        public void RegisterAtlas_returns_false_on_null_atlas_NG4() {
            var face = new FaceInfo("F", "/p", 400, FaceInfo.StyleNormal);
            var ok = AtlasRegistry.RegisterAtlas(face, null);
            Assert.That(ok, Is.False);
            Assert.That(AtlasRegistry.Count, Is.EqualTo(0));
        }
    }
}
