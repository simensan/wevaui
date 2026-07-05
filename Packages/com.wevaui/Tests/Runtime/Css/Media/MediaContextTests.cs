using NUnit.Framework;
using Weva.Css.Media;

namespace Weva.Tests.Css.Media {
    public class MediaContextTests {
        [Test]
        public void Default_factory_sets_sensible_screen_defaults() {
            var ctx = MediaContext.Default(1280, 720);
            Assert.That(ctx.ViewportWidthPx, Is.EqualTo(1280));
            Assert.That(ctx.ViewportHeightPx, Is.EqualTo(720));
            Assert.That(ctx.DpiPixelsPerInch, Is.EqualTo(96));
            Assert.That(ctx.ColorScheme, Is.EqualTo(ColorScheme.Light));
            Assert.That(ctx.Hover, Is.EqualTo(HoverCapability.Hover));
            Assert.That(ctx.Pointer, Is.EqualTo(PointerCapability.Fine));
            Assert.That(ctx.PrefersReducedMotion, Is.False);
            Assert.That(ctx.Type, Is.EqualTo(MediaType.Screen));
        }

        [Test]
        public void Orientation_landscape_when_width_greater_than_height() {
            var ctx = MediaContext.Default(1920, 1080);
            Assert.That(ctx.Orientation, Is.EqualTo(Orientation.Landscape));
        }

        [Test]
        public void Orientation_portrait_when_height_greater_than_width() {
            var ctx = MediaContext.Default(800, 1200);
            Assert.That(ctx.Orientation, Is.EqualTo(Orientation.Portrait));
        }

        [Test]
        public void With_helpers_produce_expected_overrides() {
            var ctx = MediaContext.Default(800, 600)
                .WithDpi(192)
                .WithColorScheme(ColorScheme.Dark)
                .WithHover(HoverCapability.None)
                .WithPointer(PointerCapability.Coarse)
                .WithReducedMotion(true)
                .WithType(MediaType.Print);
            Assert.That(ctx.DpiPixelsPerInch, Is.EqualTo(192));
            Assert.That(ctx.ColorScheme, Is.EqualTo(ColorScheme.Dark));
            Assert.That(ctx.Hover, Is.EqualTo(HoverCapability.None));
            Assert.That(ctx.Pointer, Is.EqualTo(PointerCapability.Coarse));
            Assert.That(ctx.PrefersReducedMotion, Is.True);
            Assert.That(ctx.Type, Is.EqualTo(MediaType.Print));
        }
    }
}
