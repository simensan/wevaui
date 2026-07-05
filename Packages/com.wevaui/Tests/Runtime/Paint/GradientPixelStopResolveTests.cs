using System.Collections.Generic;
using NUnit.Framework;
using Weva.Paint;
using Weva.Paint.Conversion;

namespace Weva.Tests.Paint {
    // Absolute-px gradient stops (e.g. the 1px grid-line technique
    // `linear-gradient(rgba(..) 1px, transparent 1px)` tiled at 44px) used to
    // be parsed as raw px and then read as 0–1 fractions, so `1px` became 100%
    // and the whole cell filled solid → no grid. BackgroundResolver.
    // ResolveAbsoluteStops converts px → fraction of the gradient-line length
    // once the tile/box size is known. These pin that conversion.
    public class GradientPixelStopResolveTests {
        const double Eps = 1e-4;

        static LinearGradient PxLineGradient(double angleDeg) {
            // color 1px, transparent 1px  → a 1px hard line at the tile start.
            var stops = new List<GradientStop> {
                new GradientStop(new LinearColor(1f, 1f, 1f, 1f), 1.0, isAbsolutePx: true),
                new GradientStop(new LinearColor(0f, 0f, 0f, 0f), 1.0, isAbsolutePx: true),
            };
            return new LinearGradient(angleDeg, stops);
        }

        [Test]
        public void Vertical_1px_stop_resolves_to_one_over_tile_height() {
            var g = (LinearGradient)BackgroundResolver.ResolveAbsoluteStops(PxLineGradient(180), 44, 44);
            Assert.That(g.Stops[0].Position, Is.EqualTo(1.0 / 44.0).Within(Eps),
                "1px in a 44px tile should resolve to 1/44 of the line");
            Assert.That(g.Stops[0].IsAbsolutePx, Is.False, "resolved stop must be a fraction, not px");
        }

        [Test]
        public void Horizontal_1px_stop_uses_tile_width() {
            // 90deg gradient line runs horizontally → length is the tile WIDTH.
            var g = (LinearGradient)BackgroundResolver.ResolveAbsoluteStops(PxLineGradient(90), 44, 200);
            Assert.That(g.Stops[0].Position, Is.EqualTo(1.0 / 44.0).Within(Eps));
        }

        [Test]
        public void Resolved_gradient_samples_as_a_thin_line_not_a_solid_fill() {
            var g = BackgroundResolver.ResolveAbsoluteStops(PxLineGradient(180), 44, 44);
            // Below 1/44 → solid line color (opaque); above → transparent.
            Assert.That(g.Sample(0.005).A, Is.GreaterThan(0.9f), "inside the 1px line should be opaque");
            Assert.That(g.Sample(0.5).A, Is.LessThan(0.1f), "the rest of the cell should be transparent");
        }

        [Test]
        public void Percent_only_gradient_is_returned_unchanged() {
            var stops = new List<GradientStop> {
                new GradientStop(new LinearColor(1f, 0f, 0f, 1f), 0.0),
                new GradientStop(new LinearColor(0f, 0f, 1f, 1f), 1.0),
            };
            var g = new LinearGradient(180, stops);
            var resolved = BackgroundResolver.ResolveAbsoluteStops(g, 44, 44);
            Assert.That(ReferenceEquals(resolved, g), Is.True,
                "no absolute stops → same instance (keeps cache sharing)");
        }
    }
}
