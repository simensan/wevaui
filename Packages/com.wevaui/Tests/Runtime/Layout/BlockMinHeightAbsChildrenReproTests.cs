using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // Repro for the nook-dialogue bug: a plain block with min-height (taller
    // than content) and ONLY absolutely-positioned children collapsed to the
    // abs-children extent instead of being floored to min-height. A bottom-
    // anchored absolute child then sat near the top. Chrome floors to
    // min-height (abs children don't contribute to the parent's height).
    public class BlockMinHeightAbsChildrenReproTests {
        static Box FindByClass(Box root, string cls) {
            foreach (var b in AllBoxes(root)) {
                var c = b.Element?.ClassName;
                if (!string.IsNullOrEmpty(c) && c.IndexOf(cls, System.StringComparison.Ordinal) >= 0)
                    return b;
            }
            return null;
        }

        [Test]
        public void Block_min_height_px_floors_height_with_only_absolute_children() {
            var (root, _, _) = Build(
                "<div class='room'><div class='abs'></div></div>",
                ".room{position:relative;min-height:600px;}" +
                ".abs{position:absolute;bottom:10px;height:40px;width:40px;}",
                viewportWidth: 800, viewportHeight: 600);
            var room = FindByClass(root, "room");
            Assert.That(room, Is.Not.Null);
            Assert.That(room.Height, Is.EqualTo(600.0).Within(1.0),
                $"min-height:600px must floor the block (abs children don't size it); got {room.Height:F1}");
        }

        [Test]
        public void Block_min_height_vh_floors_height_with_only_absolute_children() {
            var (root, _, _) = Build(
                "<div class='room'><div class='abs'></div></div>",
                ".room{position:relative;min-height:100vh;}" +
                ".abs{position:absolute;bottom:10px;height:40px;width:40px;}",
                viewportWidth: 800, viewportHeight: 600);
            var room = FindByClass(root, "room");
            Assert.That(room, Is.Not.Null);
            Assert.That(room.Height, Is.EqualTo(600.0).Within(1.0),
                $"min-height:100vh must floor the block to the viewport (600); got {room.Height:F1}");
        }

        [Test]
        public void Absolute_child_bottom_anchors_to_min_height_floored_block() {
            var (root, _, _) = Build(
                "<div class='room'><div class='abs'></div></div>",
                ".room{position:relative;min-height:600px;}" +
                ".abs{position:absolute;bottom:10px;height:40px;width:40px;}",
                viewportWidth: 800, viewportHeight: 600);
            var abs = FindByClass(root, "abs");
            Assert.That(abs, Is.Not.Null);
            // bottom:10 + height:40 → Y = 600 - 10 - 40 = 550.
            Assert.That(abs.Y, Is.EqualTo(550.0).Within(1.0),
                $"absolute bottom:10px should sit against the floored 600px block bottom; got Y={abs.Y:F1}");
        }
    }
}
