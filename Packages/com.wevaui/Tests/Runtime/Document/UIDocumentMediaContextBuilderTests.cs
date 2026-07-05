using NUnit.Framework;
using Weva.Css.Media;
using Weva.Documents;

namespace Weva.Tests.Documents {
    public class UIDocumentMediaContextBuilderTests {
        [Test]
        public void Override_takes_priority_over_camera() {
            var ctx = UIDocumentMediaContextBuilder.Resolve(640, 480, 1920, 1080, 96, false);
            Assert.That(ctx.ViewportWidthPx, Is.EqualTo(640));
            Assert.That(ctx.ViewportHeightPx, Is.EqualTo(480));
        }

        [Test]
        public void Camera_size_used_when_no_override() {
            var ctx = UIDocumentMediaContextBuilder.Resolve(0, 0, 1920, 1080, 96, false);
            Assert.That(ctx.ViewportWidthPx, Is.EqualTo(1920));
            Assert.That(ctx.ViewportHeightPx, Is.EqualTo(1080));
        }

        [Test]
        public void Default_viewport_used_when_neither_supplied() {
            var ctx = UIDocumentMediaContextBuilder.Resolve(0, 0, 0, 0, 96, false);
            Assert.That(ctx.ViewportWidthPx, Is.EqualTo(UIDocumentDefaults.DefaultViewportWidthPx));
            Assert.That(ctx.ViewportHeightPx, Is.EqualTo(UIDocumentDefaults.DefaultViewportHeightPx));
        }

        [Test]
        public void Color_scheme_hint_propagates_dark() {
            var ctx = UIDocumentMediaContextBuilder.Resolve(800, 600, 0, 0, 96, true);
            Assert.That(ctx.ColorScheme, Is.EqualTo(ColorScheme.Dark));
        }

        [Test]
        public void Color_scheme_hint_propagates_light() {
            var ctx = UIDocumentMediaContextBuilder.Resolve(800, 600, 0, 0, 96, false);
            Assert.That(ctx.ColorScheme, Is.EqualTo(ColorScheme.Light));
        }

        [Test]
        public void Orientation_landscape_when_w_greater_than_h() {
            var ctx = UIDocumentMediaContextBuilder.Resolve(1920, 1080, 0, 0, 96, false);
            Assert.That(ctx.Orientation, Is.EqualTo(Orientation.Landscape));
        }

        [Test]
        public void Orientation_portrait_when_h_greater_than_w() {
            var ctx = UIDocumentMediaContextBuilder.Resolve(720, 1280, 0, 0, 96, false);
            Assert.That(ctx.Orientation, Is.EqualTo(Orientation.Portrait));
        }

        [Test]
        public void Default_dpi_when_zero() {
            var ctx = UIDocumentMediaContextBuilder.Resolve(800, 600, 0, 0, 0, false);
            Assert.That(ctx.DpiPixelsPerInch, Is.EqualTo(UIDocumentDefaults.DefaultDpi));
        }
    }
}
