using NUnit.Framework;
using Weva.Documents;

namespace Weva.Tests.Documents {
    public class UIDocumentLifecycleResolveBackendTests {
        const int Auto = 0;
        const int IMGUI = 1;
        const int URP = 2;

        [Test]
        public void Auto_in_play_mode_with_urp_picks_urp() {
            var r = UIDocumentLifecycle.ResolveBackend(Auto, isPlaying: true, isUrpActive: true);
            Assert.That(r, Is.EqualTo(UIDocumentLifecycle.ResolvedBackend.URP));
        }

        [Test]
        public void Auto_in_edit_mode_falls_back_to_imgui() {
            var r = UIDocumentLifecycle.ResolveBackend(Auto, isPlaying: false, isUrpActive: true);
            Assert.That(r, Is.EqualTo(UIDocumentLifecycle.ResolvedBackend.IMGUI));
        }

        [Test]
        public void Auto_without_urp_falls_back_to_imgui() {
            var r = UIDocumentLifecycle.ResolveBackend(Auto, isPlaying: true, isUrpActive: false);
            Assert.That(r, Is.EqualTo(UIDocumentLifecycle.ResolvedBackend.IMGUI));
        }

        [Test]
        public void Explicit_imgui_overrides_play_mode_and_urp() {
            var r = UIDocumentLifecycle.ResolveBackend(IMGUI, isPlaying: true, isUrpActive: true);
            Assert.That(r, Is.EqualTo(UIDocumentLifecycle.ResolvedBackend.IMGUI));
        }

        [Test]
        public void Explicit_urp_overrides_edit_mode() {
            var r = UIDocumentLifecycle.ResolveBackend(URP, isPlaying: false, isUrpActive: false);
            Assert.That(r, Is.EqualTo(UIDocumentLifecycle.ResolvedBackend.URP));
        }
    }
}
