using System.Collections.Generic;
using NUnit.Framework;
using Weva.Paint;

namespace Weva.Tests.Paint {
    public class PaintListTests {
        static FillRectCommand Fill() =>
            new FillRectCommand(new Rect(0, 0, 1, 1), Brush.SolidColor(LinearColor.White));

        static StrokeBorderCommand Stroke() =>
            new StrokeBorderCommand(new Rect(0, 0, 1, 1), Borders.None);

        [Test]
        public void Add_appends_in_order() {
            var list = new PaintList();
            var a = Fill();
            var b = Stroke();
            list.Add(a);
            list.Add(b);
            Assert.That(list.Commands, Has.Count.EqualTo(2));
            Assert.That(list.Commands[0], Is.SameAs(a));
            Assert.That(list.Commands[1], Is.SameAs(b));
        }

        [Test]
        public void AddRange_appends_in_iteration_order() {
            var list = new PaintList();
            var items = new List<PaintCommand> { Fill(), Stroke(), Fill() };
            list.AddRange(items);
            Assert.That(list.Commands, Has.Count.EqualTo(3));
            for (int i = 0; i < items.Count; i++) {
                Assert.That(list.Commands[i], Is.SameAs(items[i]));
            }
        }

        [Test]
        public void Clear_empties_the_list() {
            var list = new PaintList();
            list.Add(Fill());
            list.Add(Stroke());
            list.Clear();
            Assert.That(list.Commands, Has.Count.EqualTo(0));
        }

        [Test]
        public void Submitting_to_NullBackend_invokes_each_command_once() {
            var list = new PaintList();
            list.Add(Fill());
            list.Add(Fill());
            list.Add(Stroke());
            var backend = new NullBackend();
            ((IRenderBackend)backend).Submit(list);
            Assert.That(backend.FillRectCount, Is.EqualTo(2));
            Assert.That(backend.StrokeBorderCount, Is.EqualTo(1));
            Assert.That(backend.TotalCount, Is.EqualTo(3));
        }

        [Test]
        public void RecordingBackend_captures_commands_in_order() {
            var list = new PaintList();
            var a = Fill();
            var b = Stroke();
            list.Add(a);
            list.Add(b);
            var backend = new RecordingBackend();
            ((IRenderBackend)backend).Submit(list);
            Assert.That(backend.Recorded, Has.Count.EqualTo(2));
            Assert.That(backend.Recorded[0], Is.SameAs(a));
            Assert.That(backend.Recorded[1], Is.SameAs(b));
        }

        [Test]
        public void Add_null_throws() {
            var list = new PaintList();
            Assert.Throws<System.ArgumentNullException>(() => list.Add(null));
        }
    }
}
