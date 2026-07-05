using NUnit.Framework;
using Weva.Dom;
using Weva.Events;
using Weva.Layout.Boxes;

namespace Weva.Tests.Events {
    public class HitTesterTests {
        static BlockBox MakeBox(Element e, double x, double y, double w, double h) {
            var b = new BlockBox();
            b.Element = e;
            b.X = x; b.Y = y; b.Width = w; b.Height = h;
            return b;
        }

        [Test]
        public void Hits_root_when_inside_root_only() {
            var root = new Element("div");
            var rb = MakeBox(root, 0, 0, 100, 100);
            var ht = new BoxTreeHitTester(rb);
            Assert.That(ht.HitTest(50, 50), Is.SameAs(root));
        }

        [Test]
        public void Outside_root_returns_null() {
            var root = new Element("div");
            var rb = MakeBox(root, 0, 0, 100, 100);
            var ht = new BoxTreeHitTester(rb);
            Assert.That(ht.HitTest(150, 50), Is.Null);
            Assert.That(ht.HitTest(-1, 0), Is.Null);
        }

        [Test]
        public void Deepest_visible_wins() {
            var root = new Element("div");
            var inner = new Element("span");
            var rb = MakeBox(root, 0, 0, 100, 100);
            var ib = MakeBox(inner, 10, 10, 20, 20);
            rb.AddChild(ib);
            var ht = new BoxTreeHitTester(rb);
            Assert.That(ht.HitTest(15, 15), Is.SameAs(inner));
            Assert.That(ht.HitTest(50, 50), Is.SameAs(root));
        }

        [Test]
        public void Three_level_deepest_wins() {
            // Box.X / Box.Y are local-to-parent (matches BlockLayout's
            // emitted coordinate model). Children's absolute screen rect
            // is the cumulative sum of ancestors' X/Y. So:
            //   ab → screen (0,0) 100x100
            //   bb → screen (10,10) 50x50    (10 + 0)
            //   cb → screen (20,20) 10x10    (10 + 10) with cb.X=cb.Y=10
            // HitTest then exercises each level:
            //   (25, 25) inside cb → c  (deepest)
            //   (40, 40) outside cb but inside bb → b
            //   (70, 70) outside bb but inside ab → a
            var a = new Element("div");
            var b = new Element("div");
            var c = new Element("span");
            var ab = MakeBox(a, 0, 0, 100, 100);
            var bb = MakeBox(b, 10, 10, 50, 50);
            var cb = MakeBox(c, 10, 10, 10, 10);
            ab.AddChild(bb);
            bb.AddChild(cb);
            var ht = new BoxTreeHitTester(ab);
            Assert.That(ht.HitTest(25, 25), Is.SameAs(c));
            Assert.That(ht.HitTest(40, 40), Is.SameAs(b));
            Assert.That(ht.HitTest(70, 70), Is.SameAs(a));
        }

        [Test]
        public void Last_sibling_wins_when_overlapping() {
            var root = new Element("div");
            var a = new Element("div");
            var b = new Element("div");
            var rb = MakeBox(root, 0, 0, 100, 100);
            var ab = MakeBox(a, 10, 10, 50, 50);
            var bb = MakeBox(b, 20, 20, 50, 50);
            rb.AddChild(ab);
            rb.AddChild(bb);
            var ht = new BoxTreeHitTester(rb);
            Assert.That(ht.HitTest(30, 30), Is.SameAs(b));
        }

        [Test]
        public void Point_only_inside_outer_returns_outer() {
            var outer = new Element("div");
            var inner = new Element("span");
            var ob = MakeBox(outer, 0, 0, 100, 100);
            var ib = MakeBox(inner, 50, 50, 10, 10);
            ob.AddChild(ib);
            var ht = new BoxTreeHitTester(ob);
            Assert.That(ht.HitTest(5, 5), Is.SameAs(outer));
        }

        [Test]
        public void Edge_case_left_top_inclusive_right_bottom_exclusive() {
            var root = new Element("div");
            var rb = MakeBox(root, 10, 10, 100, 100);
            var ht = new BoxTreeHitTester(rb);
            Assert.That(ht.HitTest(10, 10), Is.SameAs(root));
            Assert.That(ht.HitTest(110, 10), Is.Null);
            Assert.That(ht.HitTest(10, 110), Is.Null);
            Assert.That(ht.HitTest(109.99, 109.99), Is.SameAs(root));
        }

        [Test]
        public void Transparent_overlay_still_hits() {
            var root = new Element("div");
            var underlay = new Element("button");
            var overlay = new Element("div");
            var rb = MakeBox(root, 0, 0, 100, 100);
            var ub = MakeBox(underlay, 10, 10, 50, 50);
            var ob = MakeBox(overlay, 0, 0, 100, 100);
            rb.AddChild(ub);
            rb.AddChild(ob);
            var ht = new BoxTreeHitTester(rb);
            Assert.That(ht.HitTest(20, 20), Is.SameAs(overlay));
        }

        [Test]
        public void Null_root_returns_null() {
            var ht = new BoxTreeHitTester(null);
            Assert.That(ht.HitTest(0, 0), Is.Null);
        }
    }
}
