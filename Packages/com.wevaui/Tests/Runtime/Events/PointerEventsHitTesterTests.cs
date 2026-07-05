using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Events;
using Weva.Layout.Boxes;

namespace Weva.Tests.Events {
    public class PointerEventsHitTesterTests {
        static BlockBox MakeBox(Element e, double x, double y, double w, double h, string pointerEvents = null) {
            var b = new BlockBox();
            b.Element = e;
            b.X = x; b.Y = y; b.Width = w; b.Height = h;
            var style = new ComputedStyle(e);
            if (pointerEvents != null) style.Set("pointer-events", pointerEvents);
            b.Style = style;
            return b;
        }

        [Test]
        public void Pointer_events_none_skips_box_as_hit_target() {
            // .world { pointer-events: none }  — a click on world-only pixels
            // should pass through (HitTest returns null).
            // .hud > * { pointer-events: auto } — a click on the hud child
            // hits as before.
            var root = new Element("div");
            var world = new Element("div");
            var hudChild = new Element("button");

            var rb = MakeBox(root, 0, 0, 200, 200);
            // World covers the whole root and is transparent to hit-testing.
            var wb = MakeBox(world, 0, 0, 200, 200, pointerEvents: "none");
            // The hud child sits in a corner with explicit auto.
            var hb = MakeBox(hudChild, 150, 150, 40, 40, pointerEvents: "auto");
            rb.AddChild(wb);
            rb.AddChild(hb);

            var ht = new BoxTreeHitTester(rb);

            // (50, 50) — inside root and inside `world` only. World is the
            // last sibling considered by the painter-order walk, but pointer-
            // events:none means it cannot be the target. Root has no opt-out,
            // so the hit should fall back to root.
            Assert.That(ht.HitTest(50, 50), Is.SameAs(root));

            // (160, 160) — inside the hud child. pointer-events:auto means
            // the child remains selectable as the hit target.
            Assert.That(ht.HitTest(160, 160), Is.SameAs(hudChild));
        }

        [Test]
        public void Pointer_events_none_on_root_with_no_hit_children_returns_null() {
            // When the only candidate is a pointer-events:none box, the hit
            // test passes through entirely (web behavior).
            var root = new Element("div");
            var rb = MakeBox(root, 0, 0, 100, 100, pointerEvents: "none");
            var ht = new BoxTreeHitTester(rb);
            Assert.That(ht.HitTest(50, 50), Is.Null);
        }

        static BlockBox MakeBoxWithVisibility(Element e, double x, double y, double w, double h, string visibility) {
            var b = new BlockBox();
            b.Element = e;
            b.X = x; b.Y = y; b.Width = w; b.Height = h;
            var style = new ComputedStyle(e);
            style.Set("visibility", visibility);
            b.Style = style;
            return b;
        }

        [Test]
        public void Visibility_hidden_skips_box_as_hit_target_but_opacity_zero_does_not() {
            // CSS UI 4 §9: visibility:hidden removes the box from the hit-test
            // target set. CSS Pointer Events 1: opacity:0 keeps the box
            // hittable (authors opt out via pointer-events:none).
            var root = new Element("div");
            var hidden = new Element("div");
            var faded = new Element("div");

            var rb = MakeBox(root, 0, 0, 200, 200);
            var hb = MakeBoxWithVisibility(hidden, 0, 0, 100, 100, "hidden");
            // Build the faded box manually to stamp opacity:0 directly on its style.
            var fb = new BlockBox();
            fb.Element = faded;
            fb.X = 100; fb.Y = 0; fb.Width = 100; fb.Height = 100;
            var fadedStyle = new ComputedStyle(faded);
            fadedStyle.Set("opacity", "0");
            fb.Style = fadedStyle;

            rb.AddChild(hb);
            rb.AddChild(fb);
            var ht = new BoxTreeHitTester(rb);

            // Inside `hidden`: visibility:hidden suppresses it as a target;
            // hit falls back to root.
            Assert.That(ht.HitTest(50, 50), Is.SameAs(root));
            // Inside `faded`: opacity:0 is still hittable.
            Assert.That(ht.HitTest(150, 50), Is.SameAs(faded));
        }

        [Test]
        public void Visibility_hidden_parent_with_visible_child_lets_child_be_hit() {
            // visibility is inherited; a child that opts back in with
            // `visibility: visible` MUST remain hittable even though its
            // ancestor declares `visibility: hidden`.
            var root = new Element("div");
            var parent = new Element("div");
            var child = new Element("button");

            var rb = MakeBox(root, 0, 0, 200, 200);
            var pb = MakeBoxWithVisibility(parent, 0, 0, 200, 200, "hidden");
            var cb = MakeBoxWithVisibility(child, 50, 50, 40, 40, "visible");
            pb.AddChild(cb);
            rb.AddChild(pb);

            var ht = new BoxTreeHitTester(rb);
            // Hit inside the visible child: child wins.
            Assert.That(ht.HitTest(60, 60), Is.SameAs(child));
            // Hit inside parent-only region: parent is hidden, so we fall
            // back to root.
            Assert.That(ht.HitTest(150, 150), Is.SameAs(root));
        }
    }
}
