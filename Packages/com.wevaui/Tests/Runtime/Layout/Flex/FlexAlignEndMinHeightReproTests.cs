using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Flex {
    // Repro for the story-bubble bug: a row flex container whose height comes
    // from min-height (taller than content) must still let align-items:flex-end
    // push the single child to the cross-end (bottom). Chrome does; the engine
    // pinned the child to the top — symptom of the flex line taking its
    // cross-size from content instead of the min-height-expanded container box.
    public class FlexAlignEndMinHeightReproTests {

        static Box FindByClass(Box root, string cls) {
            foreach (var b in AllBoxes(root)) {
                var c = b.Element?.ClassName;
                if (!string.IsNullOrEmpty(c) && c.IndexOf(cls, System.StringComparison.Ordinal) >= 0)
                    return b;
            }
            return null;
        }

        [Test]
        public void Align_items_flex_end_honors_min_height_container() {
            // Container is 600px tall via min-height (content is only 100px).
            // align-items:flex-end → child bottom-aligned at Y ≈ 500.
            const string html =
                "<div class='wrap'><div class='item'></div></div>";
            const string css =
                ".wrap { display:flex; align-items:flex-end; min-height:600px; width:800px; }" +
                ".item { width:200px; height:100px; }";
            var (root, _, _) = Build(html, css, viewportWidth: 800);

            var wrap = FindByClass(root, "wrap");
            var item = FindByClass(root, "item");
            Assert.That(wrap, Is.Not.Null);
            Assert.That(item, Is.Not.Null);

            Assert.That(wrap.Height, Is.GreaterThanOrEqualTo(600.0 - 0.5),
                $"min-height should expand the flex container to 600 (got {wrap.Height:F1})");
            Assert.That(item.Y, Is.EqualTo(500.0).Within(1.0),
                $"align-items:flex-end must bottom-align the child against the min-height-expanded " +
                $"container (expected Y≈500, got {item.Y:F1})");
        }

        // Control: with an EXPLICIT height the same alignment must hold (this is
        // the path that already works — pins it so the fix doesn't regress it).
        [Test]
        public void Align_items_flex_end_honors_explicit_height_container() {
            const string html =
                "<div class='wrap'><div class='item'></div></div>";
            const string css =
                ".wrap { display:flex; align-items:flex-end; height:600px; width:800px; }" +
                ".item { width:200px; height:100px; }";
            var (root, _, _) = Build(html, css, viewportWidth: 800);

            var item = FindByClass(root, "item");
            Assert.That(item, Is.Not.Null);
            Assert.That(item.Y, Is.EqualTo(500.0).Within(1.0),
                $"align-items:flex-end with explicit height must bottom-align (got {item.Y:F1})");
        }
    }
}
