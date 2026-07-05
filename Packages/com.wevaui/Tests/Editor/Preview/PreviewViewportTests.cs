using NUnit.Framework;
using Weva.Css.Media;
using Weva.EditorTools.Preview;

namespace Weva.Tests.EditorTests.Preview {
    public class PreviewViewportTests {
        [Test]
        public void Default_is_desktop_light() {
            var vp = PreviewViewport.Default;
            Assert.That(vp.Preset, Is.EqualTo(PreviewViewportPreset.Desktop));
            Assert.That(vp.ColorScheme, Is.EqualTo(PreviewColorScheme.Light));
            Assert.That(vp.Width, Is.EqualTo(1280));
            Assert.That(vp.Height, Is.EqualTo(720));
        }

        [Test]
        public void Mobile_preset_matches_expected_390x844() {
            var vp = PreviewViewport.FromPreset(PreviewViewportPreset.Mobile, PreviewColorScheme.Light);
            Assert.That(vp.Width, Is.EqualTo(390));
            Assert.That(vp.Height, Is.EqualTo(844));
        }

        [Test]
        public void Tablet_preset_matches_expected_820x1180() {
            var vp = PreviewViewport.FromPreset(PreviewViewportPreset.Tablet, PreviewColorScheme.Dark);
            Assert.That(vp.Width, Is.EqualTo(820));
            Assert.That(vp.Height, Is.EqualTo(1180));
            Assert.That(vp.ColorScheme, Is.EqualTo(PreviewColorScheme.Dark));
        }

        [Test]
        public void WithSize_marks_preset_custom() {
            var vp = PreviewViewport.Default.WithSize(640, 480);
            Assert.That(vp.Width, Is.EqualTo(640));
            Assert.That(vp.Height, Is.EqualTo(480));
            Assert.That(vp.Preset, Is.EqualTo(PreviewViewportPreset.Custom));
        }

        [Test]
        public void ToMediaContext_carries_size_and_color_scheme() {
            var vp = PreviewViewport.FromPreset(PreviewViewportPreset.Mobile, PreviewColorScheme.Dark);
            var ctx = vp.ToMediaContext();
            Assert.That(ctx.ViewportWidthPx, Is.EqualTo(390));
            Assert.That(ctx.ViewportHeightPx, Is.EqualTo(844));
            Assert.That(ctx.ColorScheme, Is.EqualTo(ColorScheme.Dark));
            Assert.That(ctx.Type, Is.EqualTo(MediaType.Screen));
        }
    }
}
