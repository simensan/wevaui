using NUnit.Framework;
using Weva.Paint;

namespace Weva.Tests.Paint {
    public class BackendDispatchTests {
        static FontHandle Font() => new FontHandle("system-ui", 16, 400, FontStyle.Normal);

        [Test]
        public void FillRectCommand_dispatches_to_fill_overload() {
            var backend = new RecordingBackend();
            var cmd = new FillRectCommand(new Rect(0, 0, 10, 10), Brush.SolidColor(LinearColor.White));
            cmd.Submit(backend);
            Assert.That(backend.Recorded, Has.Count.EqualTo(1));
            Assert.That(backend.Recorded[0], Is.InstanceOf<FillRectCommand>());
        }

        [Test]
        public void StrokeBorderCommand_dispatches_to_stroke_overload() {
            var backend = new RecordingBackend();
            var cmd = new StrokeBorderCommand(new Rect(0, 0, 10, 10), Borders.None);
            cmd.Submit(backend);
            Assert.That(backend.Recorded[0], Is.InstanceOf<StrokeBorderCommand>());
        }

        [Test]
        public void DrawTextCommand_dispatches_to_text_overload() {
            var backend = new RecordingBackend();
            var cmd = new DrawTextCommand(new Rect(0, 0, 10, 10), "hello", Font(), LinearColor.Black, TextDecoration.None);
            cmd.Submit(backend);
            Assert.That(backend.Recorded[0], Is.InstanceOf<DrawTextCommand>());
        }

        [Test]
        public void DrawShadowCommand_dispatches_to_shadow_overload() {
            var backend = new RecordingBackend();
            var shadow = new BoxShadow(2, 2, 4, 0, LinearColor.Black, false);
            var cmd = new DrawShadowCommand(new Rect(0, 0, 10, 10), BorderRadii.Zero, shadow);
            cmd.Submit(backend);
            Assert.That(backend.Recorded[0], Is.InstanceOf<DrawShadowCommand>());
        }

        [Test]
        public void Push_and_Pop_clip_dispatch_independently() {
            var backend = new NullBackend();
            new PushClipCommand(new Rect(0, 0, 10, 10)).Submit(backend);
            new PopClipCommand().Submit(backend);
            Assert.That(backend.PushClipCount, Is.EqualTo(1));
            Assert.That(backend.PopClipCount, Is.EqualTo(1));
        }

        [Test]
        public void Push_and_Pop_opacity_dispatch_independently() {
            var backend = new NullBackend();
            new PushOpacityCommand(0.5).Submit(backend);
            new PopOpacityCommand().Submit(backend);
            Assert.That(backend.PushOpacityCount, Is.EqualTo(1));
            Assert.That(backend.PopOpacityCount, Is.EqualTo(1));
        }

        [Test]
        public void Push_and_Pop_transform_dispatch_independently() {
            var backend = new NullBackend();
            new PushTransformCommand(Transform2D.Translate(1, 1)).Submit(backend);
            new PopTransformCommand().Submit(backend);
            Assert.That(backend.PushTransformCount, Is.EqualTo(1));
            Assert.That(backend.PopTransformCount, Is.EqualTo(1));
        }

        [Test]
        public void Mixed_paint_list_dispatches_all_kinds() {
            var list = new PaintList();
            list.Add(new PushClipCommand(new Rect(0, 0, 100, 100)));
            list.Add(new PushOpacityCommand(0.5));
            list.Add(new PushTransformCommand(Transform2D.Translate(10, 10)));
            list.Add(new DrawShadowCommand(new Rect(0, 0, 50, 50), BorderRadii.Zero,
                new BoxShadow(0, 0, 4, 0, LinearColor.Black, false)));
            list.Add(new FillRectCommand(new Rect(0, 0, 50, 50), Brush.SolidColor(LinearColor.White)));
            list.Add(new StrokeBorderCommand(new Rect(0, 0, 50, 50), Borders.None));
            list.Add(new DrawTextCommand(new Rect(0, 0, 50, 50), "x", Font(), LinearColor.Black, TextDecoration.None));
            list.Add(new PopTransformCommand());
            list.Add(new PopOpacityCommand());
            list.Add(new PopClipCommand());

            var backend = new NullBackend();
            ((IRenderBackend)backend).Submit(list);

            Assert.That(backend.PushClipCount, Is.EqualTo(1));
            Assert.That(backend.PopClipCount, Is.EqualTo(1));
            Assert.That(backend.PushOpacityCount, Is.EqualTo(1));
            Assert.That(backend.PopOpacityCount, Is.EqualTo(1));
            Assert.That(backend.PushTransformCount, Is.EqualTo(1));
            Assert.That(backend.PopTransformCount, Is.EqualTo(1));
            Assert.That(backend.FillRectCount, Is.EqualTo(1));
            Assert.That(backend.StrokeBorderCount, Is.EqualTo(1));
            Assert.That(backend.DrawTextCount, Is.EqualTo(1));
            Assert.That(backend.DrawShadowCount, Is.EqualTo(1));
        }
    }
}
