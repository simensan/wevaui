#if WEVA_URP
using NUnit.Framework;
using Weva.Paint;
using Weva.Rendering.URP;
using Rect = Weva.Paint.Rect;

namespace Weva.Tests.Rendering.URP {
    // G5b — Polygon clip-path `fill-rule` (nonzero / evenodd) must travel from
    // `PolygonClipPathShape.FillRule` through `UIBatcher.EncodeClipPath` into the
    // per-instance `ClipShape0.z` channel so the URP fragment shader can dispatch
    // between winding-count and parity-count algorithms. HLSL has no unit-test
    // path; these tests pin the C# value plumbing. Visual validation of the
    // shader branch must be done in Unity play mode by an engineer.
    public class PolygonClipFillRuleTests {
        // Five-pointed pentagram (self-intersecting). The center pentagon is
        // "inside" under nonzero and "outside" under evenodd. The star tips
        // (e.g. just below the top vertex at (50, 25)) are inside under both.
        static readonly Point2D[] PentagramPoints = {
            new Point2D(50, 10),
            new Point2D(88, 88),
            new Point2D(12, 38),
            new Point2D(88, 38),
            new Point2D(12, 88),
        };

        [Test]
        public void Polygon_clip_packs_nonzero_fill_rule_into_clip_shape0_z() {
            var batcher = new UIBatcher();
            batcher.PushClipPath(new PolygonClipPathShape(PentagramPoints, ClipPathFillRule.Nonzero));
            batcher.SubmitFillRect(new Rect(0, 0, 100, 100), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            batcher.PopClipPath();
            batcher.Finish();

            var inst = batcher.Batches[0].Instances[0];
            Assert.That(inst.ClipShape0.x, Is.EqualTo((float)ClipPathShapeKind.Polygon));
            Assert.That(inst.ClipShape0.y, Is.EqualTo(5f), "point count survives packing");
            Assert.That(inst.ClipShape0.z, Is.EqualTo((float)ClipPathFillRule.Nonzero),
                "fill rule channel encodes nonzero (= 0)");
        }

        [Test]
        public void Polygon_clip_packs_evenodd_fill_rule_into_clip_shape0_z() {
            var batcher = new UIBatcher();
            batcher.PushClipPath(new PolygonClipPathShape(PentagramPoints, ClipPathFillRule.Evenodd));
            batcher.SubmitFillRect(new Rect(0, 0, 100, 100), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            batcher.PopClipPath();
            batcher.Finish();

            var inst = batcher.Batches[0].Instances[0];
            Assert.That(inst.ClipShape0.x, Is.EqualTo((float)ClipPathShapeKind.Polygon));
            Assert.That(inst.ClipShape0.z, Is.EqualTo((float)ClipPathFillRule.Evenodd),
                "fill rule channel encodes evenodd (= 1)");
        }

        [Test]
        public void Polygon_clip_default_fill_rule_is_nonzero() {
            // The single-arg ctor — used by the rect/ellipse polygon fallbacks
            // and by callers that don't pass an explicit rule — must default
            // to CSS' specified `nonzero`, NOT the previous-shader's implicit
            // even-odd behaviour.
            var batcher = new UIBatcher();
            batcher.PushClipPath(new PolygonClipPathShape(PentagramPoints));
            batcher.SubmitFillRect(new Rect(0, 0, 100, 100), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            batcher.PopClipPath();
            batcher.Finish();

            var inst = batcher.Batches[0].Instances[0];
            Assert.That(inst.ClipShape0.z, Is.EqualTo((float)ClipPathFillRule.Nonzero));
        }

        [Test]
        public void Self_intersecting_star_nonzero_marks_center_inside() {
            // The pentagram's center point (50, 50) is enclosed by all five
            // edges going in the same winding direction, so the nonzero rule
            // counts it as inside (winding != 0). This is the CPU-side mirror
            // of the GPU contract; if the C# winding goes wrong the shader
            // can't be right either. Engineer must still visually verify the
            // GPU branch in play mode.
            var shape = new PolygonClipPathShape(PentagramPoints, ClipPathFillRule.Nonzero);
            Assert.That(shape.Contains(50, 50), Is.True, "pentagram center is inside under nonzero");
        }

        [Test]
        public void Self_intersecting_star_evenodd_marks_center_outside() {
            var shape = new PolygonClipPathShape(PentagramPoints, ClipPathFillRule.Evenodd);
            Assert.That(shape.Contains(50, 50), Is.False, "pentagram center is outside under evenodd");
        }

        [Test]
        public void Convex_polygon_renders_identically_under_either_rule() {
            // Regression pin: a simple convex polygon (axis-aligned square)
            // has winding count = ±1 everywhere inside, so nonzero and evenodd
            // agree. If they diverge the implementation is broken.
            var square = new[] {
                new Point2D(10, 10),
                new Point2D(90, 10),
                new Point2D(90, 90),
                new Point2D(10, 90),
            };
            var nonzero = new PolygonClipPathShape(square, ClipPathFillRule.Nonzero);
            var evenodd = new PolygonClipPathShape(square, ClipPathFillRule.Evenodd);

            // Sample a grid of points; convex shape must classify identically.
            for (int y = 0; y <= 100; y += 10) {
                for (int x = 0; x <= 100; x += 10) {
                    Assert.That(nonzero.Contains(x, y), Is.EqualTo(evenodd.Contains(x, y)),
                        $"convex polygon at ({x},{y}) must classify the same under nonzero and evenodd");
                }
            }
        }

        [Test]
        public void Pop_clip_path_resets_polygon_fill_rule_channel_to_zero() {
            // After PopClipPath the next quad inherits "no clip" — the
            // ClipShape0.z fill-rule bit must reset along with the kind, or
            // an evenodd value would silently leak onto an unclipped quad.
            // Without an explicit reset the shader still ignores the value
            // (kind == 0), but a future change might read it speculatively.
            var batcher = new UIBatcher();
            batcher.PushClipPath(new PolygonClipPathShape(PentagramPoints, ClipPathFillRule.Evenodd));
            batcher.PopClipPath();
            batcher.SubmitFillRect(new Rect(0, 0, 10, 10), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            batcher.Finish();

            var inst = batcher.Batches[0].Instances[0];
            Assert.That(inst.ClipShape0.x, Is.EqualTo(0f), "kind is unset after pop");
            Assert.That(inst.ClipShape0.z, Is.EqualTo(0f), "fill-rule channel is unset after pop");
        }
    }
}
#endif
