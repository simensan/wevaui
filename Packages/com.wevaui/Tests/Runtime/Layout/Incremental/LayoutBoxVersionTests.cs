using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.Incremental.IncrementalLayoutTestHelpers;

namespace Weva.Tests.Layout.Incremental {
    public class LayoutBoxVersionTests {
        [Test]
        public void Box_version_starts_at_zero() {
            var b = new BlockBox();
            Assert.That(b.Version, Is.EqualTo(0));
        }

        [Test]
        public void Box_version_bumps_after_first_layout() {
            var h = Build("<div id=\"a\"></div>", null, 800);
            var root = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            var a = h.Doc.GetElementById("a");
            var box = FindBoxFor(root, a);
            Assert.That(box.Version, Is.GreaterThan(0));
        }

        [Test]
        public void Cached_box_keeps_version_across_relayout() {
            var h = Build("<div id=\"a\"></div>", null, 800);
            var r1 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            var a = h.Doc.GetElementById("a");
            long v1 = FindBoxFor(r1, a).Version;
            var r2 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            long v2 = FindBoxFor(r2, a).Version;
            Assert.That(v2, Is.EqualTo(v1));
        }

        [Test]
        public void Box_version_increases_when_invalidated_and_relaid_out() {
            var h = Build("<div id=\"a\"></div>", null, 800);
            var r1 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            var a = h.Doc.GetElementById("a");
            long v1 = FindBoxFor(r1, a).Version;
            h.Engine.Invalidate(a);
            var r2 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            long v2 = FindBoxFor(r2, a).Version;
            Assert.That(v2, Is.GreaterThan(v1));
        }

        [Test]
        public void Box_version_is_monotonic_across_engine() {
            var h1 = Build("<div></div>");
            var r1 = h1.Engine.Layout(h1.Doc, h1.StyleOf, h1.Ctx);
            long v1 = r1.Version;
            var h2 = Build("<div></div>");
            var r2 = h2.Engine.Layout(h2.Doc, h2.StyleOf, h2.Ctx);
            long v2 = r2.Version;
            Assert.That(v2, Is.GreaterThan(v1));
        }

        [Test]
        public void Cached_box_returns_same_instance_on_relayout() {
            var h = Build("<div id=\"a\"><span id=\"s\">hi</span></div>", null, 800);
            var r1 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            var a = h.Doc.GetElementById("a");
            var box1 = FindBoxFor(r1, a);
            var r2 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            var box2 = FindBoxFor(r2, a);
            Assert.That(box2, Is.SameAs(box1));
        }

        [Test]
        public void Different_elements_have_distinct_versions_after_first_layout() {
            var h = Build("<div id=\"a\"></div><div id=\"b\"></div>", null, 800);
            var root = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            var a = h.Doc.GetElementById("a");
            var b = h.Doc.GetElementById("b");
            long va = FindBoxFor(root, a).Version;
            long vb = FindBoxFor(root, b).Version;
            Assert.That(va, Is.Not.EqualTo(vb));
        }
    }
}
