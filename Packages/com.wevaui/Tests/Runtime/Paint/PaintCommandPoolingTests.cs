using NUnit.Framework;
using Weva.Css.Values;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Paint.Filters;

namespace Weva.Tests.Paint {
    public class PaintCommandPoolingTests {
        static FontHandle Font() => new FontHandle("system-ui", 16, 400, FontStyle.Normal);
        static LinearColor Red => new LinearColor(1f, 0f, 0f, 1f);
        static LinearColor Blue => new LinearColor(0f, 0f, 1f, 1f);

        [Test]
        public void FillRectCommand_Reset_restores_default_state() {
            var cmd = new FillRectCommand(new Rect(1, 2, 3, 4), Brush.SolidColor(Red), BorderRadii.Uniform(5));
            cmd.Reset();
            Assert.That(cmd.Bounds.Width, Is.EqualTo(0));
            Assert.That(cmd.Brush, Is.Null);
            Assert.That(cmd.Radii.TopLeft.XRadius, Is.EqualTo(0));
        }

        [Test]
        public void StrokeBorderCommand_Reset_restores_default_state() {
            var cmd = new StrokeBorderCommand(new Rect(1, 2, 3, 4), Borders.Uniform(new BorderEdge(BorderStyle.Solid, 2, LinearColor.Black)), BorderRadii.Uniform(5));
            cmd.Reset();
            Assert.That(cmd.Bounds.Width, Is.EqualTo(0));
            Assert.That(cmd.Borders.IsNone, Is.True);
            Assert.That(cmd.Radii.TopLeft.XRadius, Is.EqualTo(0));
        }

        [Test]
        public void DrawTextCommand_Reset_restores_default_state() {
            var cmd = new DrawTextCommand(new Rect(0, 0, 10, 10), "hi", Font(), Red, TextDecoration.Underline);
            cmd.Reset();
            Assert.That(cmd.Text, Is.Null);
            Assert.That(cmd.Bounds.Width, Is.EqualTo(0));
        }

        [Test]
        public void DrawShadowCommand_Reset_restores_default_state() {
            var sh = new BoxShadow(2, 2, 4, 0, LinearColor.Black, false);
            var cmd = new DrawShadowCommand(new Rect(1, 2, 3, 4), BorderRadii.Uniform(2), sh);
            cmd.Reset();
            Assert.That(cmd.Bounds.Width, Is.EqualTo(0));
        }

        [Test]
        public void PushClipCommand_Reset_restores_default_state() {
            var cmd = new PushClipCommand(new Rect(1, 2, 3, 4), BorderRadii.Uniform(2));
            cmd.Reset();
            Assert.That(cmd.Bounds.Width, Is.EqualTo(0));
            Assert.That(cmd.Radii.TopLeft.XRadius, Is.EqualTo(0));
        }

        [Test]
        public void PushOpacityCommand_Reset_restores_default_state() {
            var cmd = new PushOpacityCommand(0.5);
            cmd.Reset();
            Assert.That(cmd.Opacity, Is.EqualTo(0));
        }

        [Test]
        public void PushTransformCommand_Reset_restores_default_state() {
            var cmd = new PushTransformCommand(Transform2D.Translate(10, 20));
            cmd.Reset();
            // Identity is the documented Reset state for transforms.
            Assert.That(cmd.Transform.Equals(Transform2D.Identity), Is.True);
        }

        [Test]
        public void PushFilterCommand_Reset_restores_default_state() {
            var fc = FilterParser.Parse("blur(2px)", LengthContext.Default, LinearColor.Black);
            var cmd = new PushFilterCommand(new Rect(0, 0, 10, 10), fc);
            cmd.Reset();
            Assert.That(cmd.Bounds.Width, Is.EqualTo(0));
            Assert.That(cmd.Filters, Is.Null);
        }

        [Test]
        public void Pool_RentFillRect_reuses_returned_instance() {
            var pool = new PaintCommandPool();
            var cmd = pool.RentFillRect(new Rect(0, 0, 10, 10), Brush.SolidColor(Red), BorderRadii.Zero);
            var list = new PaintList();
            list.Add(cmd);
            pool.ReturnAll(list);

            var second = pool.RentFillRect(new Rect(5, 5, 20, 20), Brush.SolidColor(Blue), BorderRadii.Uniform(3));
            Assert.That(second, Is.SameAs(cmd), "pool should hand the same FillRect instance back");
            Assert.That(second.Bounds.X, Is.EqualTo(5), "Set should overwrite previous Bounds");
            Assert.That(second.Brush, Is.Not.Null);
            Assert.That(second.Radii.TopLeft.XRadius, Is.EqualTo(3));
        }

        [Test]
        public void Pool_RentDrawText_reuses_returned_instance() {
            var pool = new PaintCommandPool();
            var cmd = pool.RentDrawText(new Rect(0, 0, 10, 10), "hello", Font(), LinearColor.Black, TextDecoration.None);
            var list = new PaintList();
            list.Add(cmd);
            pool.ReturnAll(list);

            var second = pool.RentDrawText(new Rect(0, 0, 1, 1), "world", Font(), Red, TextDecoration.Underline);
            Assert.That(second, Is.SameAs(cmd));
            Assert.That(second.Text, Is.EqualTo("world"));
        }

        [Test]
        public void Pool_skips_Pop_singletons_in_ReturnAll() {
            // Pop singletons must never be parked in the pool — they are static
            // shared instances. Verify ReturnAll does not crash and does not
            // disturb the singletons.
            var pool = new PaintCommandPool();
            var list = new PaintList();
            list.Add(PaintCommandSingletons.PopClip);
            list.Add(PaintCommandSingletons.PopOpacity);
            list.Add(PaintCommandSingletons.PopTransform);
            list.Add(PaintCommandSingletons.PopFilter);
            pool.ReturnAll(list);
            Assert.That(pool.FillRectStackSize, Is.EqualTo(0));
            // Singletons remain referenceable.
            Assert.That(PaintCommandSingletons.PopClip, Is.Not.Null);
            Assert.That(PaintCommandSingletons.PopOpacity, Is.Not.Null);
        }

        [Test]
        public void Pool_reuse_preserves_correct_Submit_behavior() {
            // Submit produces identical backend output before and after pooling.
            var pool = new PaintCommandPool();
            var first = pool.RentFillRect(new Rect(1, 2, 3, 4), Brush.SolidColor(Red), BorderRadii.Uniform(2));
            var b1 = new RecordingBackend();
            first.Submit(b1);
            Assert.That(b1.Recorded, Has.Count.EqualTo(1));
            Assert.That(((FillRectCommand)b1.Recorded[0]).Bounds.X, Is.EqualTo(1));

            var lst = new PaintList();
            lst.Add(first);
            pool.ReturnAll(lst);

            var second = pool.RentFillRect(new Rect(10, 20, 30, 40), Brush.SolidColor(Blue), BorderRadii.Zero);
            var b2 = new RecordingBackend();
            second.Submit(b2);
            Assert.That(b2.Recorded, Has.Count.EqualTo(1));
            Assert.That(((FillRectCommand)b2.Recorded[0]).Bounds.X, Is.EqualTo(10));
            Assert.That(((FillRectCommand)b2.Recorded[0]).Bounds.Width, Is.EqualTo(30));
        }

        [Test]
        public void Pool_caps_per_type_stack() {
            var pool = new PaintCommandPool(maxPerType: 2);
            var list = new PaintList();
            for (int i = 0; i < 5; i++) {
                list.Add(pool.RentFillRect(new Rect(0, 0, 1, 1), Brush.SolidColor(LinearColor.White), BorderRadii.Zero));
            }
            pool.ReturnAll(list);
            Assert.That(pool.FillRectStackSize, Is.EqualTo(2),
                "PerType cap must drop excess returned commands");
        }

        [Test]
        public void Pool_handles_mixed_command_types_in_one_return() {
            var pool = new PaintCommandPool();
            var list = new PaintList();
            list.Add(pool.RentFillRect(new Rect(0, 0, 1, 1), Brush.SolidColor(LinearColor.White), BorderRadii.Zero));
            list.Add(pool.RentStrokeBorder(new Rect(0, 0, 1, 1), Borders.None, BorderRadii.Zero));
            list.Add(pool.RentPushClip(new Rect(0, 0, 10, 10)));
            list.Add(pool.RentPushOpacity(0.5));
            list.Add(pool.RentPushTransform(Transform2D.Identity));
            pool.ReturnAll(list);
            Assert.That(pool.FillRectStackSize, Is.EqualTo(1));
            Assert.That(pool.StrokeBorderStackSize, Is.EqualTo(1));
            Assert.That(pool.PushClipStackSize, Is.EqualTo(1));
            Assert.That(pool.PushOpacityStackSize, Is.EqualTo(1));
            Assert.That(pool.PushTransformStackSize, Is.EqualTo(1));
        }
    }
}
