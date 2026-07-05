// Gate matches the test assembly's URP versionDefine (see
// UIRenderGraphFilterBlurPlanTests for the full rationale: bare WEVA_URP is
// undefined in Weva.Tests.Runtime and silently drops the file).
#if WEVA_URP_BATCHER_TESTS
using NUnit.Framework;
using Weva.Paint;
using Weva.Rendering.URP;
using Rect = Weva.Paint.Rect;

namespace Weva.Tests.Rendering.URP {
    // Coverage for the per-batch screen-space AABB (UIQuadBatch.PixelBounds)
    // accumulated by UIBatcher.AppendInstance and consumed by the shared-
    // backdrop shareability demotion. The engine's Transform2D is row-major
    // with the point on the LEFT: x' = A*x + C*y + Tx, y' = B*x + D*y + Ty,
    // with rows packed as TransformRow0.xy = (A, B), TransformRow1.xy = (C, D).
    // Rendering audit NEW-2: an earlier accumulator applied the TRANSPOSED
    // linear part — invisible for axis-aligned content (B = C = 0, which is
    // all the glass.html calibration ever exercised) but wrong for rotate/
    // skew, poisoning the shareability check with bogus AABBs. The rotation
    // tests below discriminate the two conventions.
    public class UIBatcherPixelBoundsTests {
        [Test]
        public void Identity_transform_bounds_match_the_rect() {
            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(10, 20, 30, 40),
                Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            batcher.Finish();
            var b = batcher.Batches[0].PixelBounds;
            Assert.That(b.x, Is.EqualTo(10f).Within(1e-3f));
            Assert.That(b.y, Is.EqualTo(20f).Within(1e-3f));
            Assert.That(b.z, Is.EqualTo(40f).Within(1e-3f));
            Assert.That(b.w, Is.EqualTo(60f).Within(1e-3f));
        }

        [Test]
        public void Rotate90_maps_the_rect_into_the_correct_quadrant() {
            // rotate(90deg) about the origin: (x, y) -> (-y, x).
            // Rect (100, 0, 20, 10) -> x in [-10, 0], y in [100, 120].
            // The transposed convention lands at (0, -120, 10, -100) —
            // wrong quadrant entirely.
            var batcher = new UIBatcher();
            batcher.PushTransform(Transform2D.Rotate(90));
            batcher.SubmitFillRect(new Rect(100, 0, 20, 10),
                Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            batcher.PopTransform();
            batcher.Finish();
            var b = batcher.Batches[0].PixelBounds;
            Assert.That(b.x, Is.EqualTo(-10f).Within(1e-3f));
            Assert.That(b.y, Is.EqualTo(100f).Within(1e-3f));
            Assert.That(b.z, Is.EqualTo(0f).Within(1e-3f));
            Assert.That(b.w, Is.EqualTo(120f).Within(1e-3f));
        }

        [Test]
        public void Rotated_bounds_equal_the_transformed_corner_hull() {
            // Self-checking against Transform2D.Apply — the same convention
            // the GPU uses (wpos.x = R0.x*x + R1.x*y + R2.x). Any transpose
            // or sign slip in the accumulator diverges from this hull.
            var t = Transform2D.Rotate(30).Multiply(Transform2D.Translate(15, -7));
            var rect = new Rect(50, 60, 80, 40);
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            foreach (var (cx, cy) in new[] {
                (rect.X, rect.Y), (rect.X + rect.Width, rect.Y),
                (rect.X, rect.Y + rect.Height), (rect.X + rect.Width, rect.Y + rect.Height)
            }) {
                var (px, py) = t.Apply(cx, cy);
                if (px < minX) minX = (float)px;
                if (py < minY) minY = (float)py;
                if (px > maxX) maxX = (float)px;
                if (py > maxY) maxY = (float)py;
            }

            var batcher = new UIBatcher();
            batcher.PushTransform(t);
            batcher.SubmitFillRect(rect, Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            batcher.PopTransform();
            batcher.Finish();
            var b = batcher.Batches[0].PixelBounds;
            Assert.That(b.x, Is.EqualTo(minX).Within(1e-2f));
            Assert.That(b.y, Is.EqualTo(minY).Within(1e-2f));
            Assert.That(b.z, Is.EqualTo(maxX).Within(1e-2f));
            Assert.That(b.w, Is.EqualTo(maxY).Within(1e-2f));
        }

        [Test]
        public void Bounds_union_across_instances_in_one_batch() {
            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 10, 10),
                Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            batcher.SubmitFillRect(new Rect(90, 40, 10, 10),
                Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            batcher.Finish();
            Assert.That(batcher.Batches.Count, Is.EqualTo(1));
            var b = batcher.Batches[0].PixelBounds;
            Assert.That(b.x, Is.EqualTo(0f).Within(1e-3f));
            Assert.That(b.y, Is.EqualTo(0f).Within(1e-3f));
            Assert.That(b.z, Is.EqualTo(100f).Within(1e-3f));
            Assert.That(b.w, Is.EqualTo(50f).Within(1e-3f));
        }
    }
}
#endif
