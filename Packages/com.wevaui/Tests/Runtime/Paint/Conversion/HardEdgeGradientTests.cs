using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Paint;
using Weva.Paint.Conversion;

namespace Weva.Tests.Paint.Conversion {
    // CSS Images 3 §3.4.4 — hard-edge gradient stops (two stops sharing
    // the same position) produce an instant color change. The pattern
    // `linear-gradient(180deg, c 0%, c 50%, transparent 50%)` paints
    // the top half a solid color and the bottom half transparent.
    //
    // The bug this file pins: map.html's `.compass-needle` uses exactly
    // this pattern; the needle is positioned correctly (rotated 35° at
    // the compass center) but the gradient paints fully transparent, so
    // the needle is invisible. These tests trace the encoding through
    // BackgroundResolver to verify the stop list reaches the painter
    // intact.
    public class HardEdgeGradientTests {
        static ComputedStyle Style() => new ComputedStyle(new Element("div"));
        static Rect Bounds(double w = 3, double h = 32) => new Rect(0, 0, w, h);

        [Test]
        public void Three_stop_half_fill_keeps_all_three_stops() {
            var s = Style();
            s.Set("background-image",
                "linear-gradient(180deg, rgb(179, 136, 255) 0%, rgb(179, 136, 255) 50%, transparent 50%)");
            var output = new List<Brush>();
            BackgroundResolver.ResolveBackgroundLayersInto(s, Bounds(), output, null);
            Assert.That(output.Count, Is.EqualTo(1));
            Assert.That(output[0].Kind, Is.EqualTo(BrushKind.Gradient));
            var lg = (LinearGradient)output[0].GradientValue;
            Assert.That(lg.Stops.Count, Is.EqualTo(3),
                "Three explicit stops must survive the resolver — duplicate-position dedup would break the hard-edge pattern.");
            Assert.That(lg.Stops[0].Position, Is.EqualTo(0.0).Within(1e-6));
            Assert.That(lg.Stops[1].Position, Is.EqualTo(0.5).Within(1e-6));
            Assert.That(lg.Stops[2].Position, Is.EqualTo(0.5).Within(1e-6));
            Assert.That(lg.Stops[0].Color.A, Is.GreaterThan(0.5f));
            Assert.That(lg.Stops[1].Color.A, Is.GreaterThan(0.5f));
            Assert.That(lg.Stops[2].Color.A, Is.LessThan(0.01f),
                "Third stop is transparent — alpha near 0.");
        }

        [Test]
        public void Three_stop_half_fill_angle_is_180() {
            var s = Style();
            s.Set("background-image",
                "linear-gradient(180deg, rgb(179, 136, 255) 0%, rgb(179, 136, 255) 50%, transparent 50%)");
            var output = new List<Brush>();
            BackgroundResolver.ResolveBackgroundLayersInto(s, Bounds(), output, null);
            var lg = (LinearGradient)output[0].GradientValue;
            Assert.That(lg.AngleDegrees, Is.EqualTo(180.0).Within(1e-6));
        }

        // End-to-end: build a box that mirrors the compass needle's
        // style (3×32 abs-pos with the half-fill gradient and the
        // translate+rotate transform) and confirm the converter emits a
        // FillRect with a Gradient brush. If this fails, the gradient
        // never reaches the painter — the bug is in the converter. If
        // it passes, the bug is in EmitGradient / shader / quad geometry.
        [Test]
        public void Compass_needle_emits_fillrect_with_gradient_brush() {
            var element = new Element("span");
            var style = new ComputedStyle(element);
            style.Set("position", "absolute");
            style.Set("top", "50%");
            style.Set("left", "50%");
            style.Set("width", "3px");
            style.Set("height", "32px");
            style.Set("background-image",
                "linear-gradient(180deg, rgb(179, 136, 255) 0%, rgb(179, 136, 255) 50%, transparent 50%)");
            style.Set("transform", "translate(-50%, -50%) rotate(35deg)");
            style.Set("transform-origin", "center center");

            var box = new BlockBox();
            box.Element = element;
            box.Style = style;
            box.X = 0; box.Y = 0;
            box.Width = 3; box.Height = 32;

            var converter = new BoxToPaintConverter();
            var cmds = converter.Convert(box).Commands;

            FillRectCommand fr = null;
            foreach (var c in cmds) {
                if (c is FillRectCommand x && x.Brush != null && x.Brush.Kind == BrushKind.Gradient) {
                    fr = x;
                    break;
                }
            }
            Assert.That(fr, Is.Not.Null,
                "Converter must emit a FillRect with a Gradient brush for the needle's background.");
            Assert.That(fr.Bounds.Width, Is.EqualTo(3).Within(1e-6));
            Assert.That(fr.Bounds.Height, Is.EqualTo(32).Within(1e-6));
            var lg = fr.Brush.GradientValue as LinearGradient;
            Assert.That(lg, Is.Not.Null);
            Assert.That(lg.Stops.Count, Is.EqualTo(3));
        }

        [Test]
        public void Three_stop_half_fill_stops_stay_monotonic() {
            // NormalizeStopPositions enforces pos[i] >= pos[i-1]. The two
            // 50% stops are equal, not decreasing, so the normalizer must
            // leave them alone — clamping `pos[2] = pos[1] = 0.5` is a
            // no-op (the clamp triggers only when pos[i] < pos[i-1]).
            var s = Style();
            s.Set("background-image",
                "linear-gradient(180deg, rgb(179, 136, 255) 0%, rgb(179, 136, 255) 50%, transparent 50%)");
            var output = new List<Brush>();
            BackgroundResolver.ResolveBackgroundLayersInto(s, Bounds(), output, null);
            var lg = (LinearGradient)output[0].GradientValue;
            for (int i = 1; i < lg.Stops.Count; i++) {
                Assert.That(lg.Stops[i].Position, Is.GreaterThanOrEqualTo(lg.Stops[i - 1].Position - 1e-9));
            }
        }
    }
}
