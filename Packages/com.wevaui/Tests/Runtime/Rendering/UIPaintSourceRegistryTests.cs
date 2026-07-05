using NUnit.Framework;
using Weva.Paint;
using Weva.Rendering;

namespace Weva.Tests.Rendering {
    public class UIPaintSourceRegistryTests {
        sealed class TestSource : IUIPaintSource {
            public int Order { get; set; }
            public int Calls;
            public void EmitPaint(IRenderBackend backend) { Calls++; }
            public bool NeedsRepaint => true;
        }

        [SetUp]
        public void SetUp() {
            UIPaintSourceRegistry.Clear();
        }

        [TearDown]
        public void TearDown() {
            UIPaintSourceRegistry.Clear();
        }

        [Test]
        public void Register_adds_unique_sources() {
            var a = new TestSource();
            var b = new TestSource();
            UIPaintSourceRegistry.Register(a);
            UIPaintSourceRegistry.Register(b);
            Assert.That(UIPaintSourceRegistry.Count, Is.EqualTo(2));
        }

        [Test]
        public void Version_changes_only_when_source_set_changes() {
            var a = new TestSource();
            int start = UIPaintSourceRegistry.Version;

            UIPaintSourceRegistry.Register(a);
            int afterRegister = UIPaintSourceRegistry.Version;
            UIPaintSourceRegistry.Register(a);
            int afterDuplicate = UIPaintSourceRegistry.Version;
            UIPaintSourceRegistry.Unregister(a);
            int afterUnregister = UIPaintSourceRegistry.Version;
            UIPaintSourceRegistry.Unregister(a);
            int afterMissingUnregister = UIPaintSourceRegistry.Version;

            Assert.That(afterRegister, Is.Not.EqualTo(start));
            Assert.That(afterDuplicate, Is.EqualTo(afterRegister));
            Assert.That(afterUnregister, Is.Not.EqualTo(afterDuplicate));
            Assert.That(afterMissingUnregister, Is.EqualTo(afterUnregister));
        }

        [Test]
        public void Register_duplicate_is_a_noop() {
            var a = new TestSource();
            UIPaintSourceRegistry.Register(a);
            UIPaintSourceRegistry.Register(a);
            Assert.That(UIPaintSourceRegistry.Count, Is.EqualTo(1));
        }

        [Test]
        public void Unregister_removes_source() {
            var a = new TestSource();
            UIPaintSourceRegistry.Register(a);
            UIPaintSourceRegistry.Unregister(a);
            Assert.That(UIPaintSourceRegistry.Count, Is.EqualTo(0));
        }

        [Test]
        public void Snapshot_orders_by_Order_ascending() {
            var a = new TestSource { Order = 5 };
            var b = new TestSource { Order = 1 };
            var c = new TestSource { Order = 3 };
            UIPaintSourceRegistry.Register(a);
            UIPaintSourceRegistry.Register(b);
            UIPaintSourceRegistry.Register(c);
            var snap = UIPaintSourceRegistry.Snapshot();
            Assert.That(snap[0], Is.SameAs(b));
            Assert.That(snap[1], Is.SameAs(c));
            Assert.That(snap[2], Is.SameAs(a));
        }

        [Test]
        public void Clear_removes_all_sources() {
            UIPaintSourceRegistry.Register(new TestSource());
            UIPaintSourceRegistry.Register(new TestSource());
            UIPaintSourceRegistry.Clear();
            Assert.That(UIPaintSourceRegistry.Count, Is.EqualTo(0));
        }
    }
}
