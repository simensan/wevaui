// User report (2026-06-01): `.right-col` inside `.play-grid`
// (a grid container pinned by `position:absolute; inset:0`) shrinks
// with the viewport in Chrome but keeps its content-sum height in
// Unity and overflows past the viewport bottom.
//
// Root cause: GridLayout.ApplyItemAlignment stamps `.right-col.Height =
// cellH` and sets `GridStretchedHeight = true`. The post-RepositionAbsolutes
// flex repair pass (added in LayoutEngine.cs to fix PAINT-1) then re-enters
// `FlexLayout.FinalizeContainerMainSize` for `.right-col`. The
// grid-stretched-height guard at FlexLayout.cs:2260 requires
// `inThirdPass=true` — which the new repair pass didn't toggle — so the
// guard misses and Height is rewritten to the content sum, breaking the
// grid-cell constraint.
//
// Fix: LayoutEngine sets `flexLayout.SetInThirdPass(true)` around the
// post-RepositionAbsolutes flex pass.
using NUnit.Framework;
using Weva.Layout.Boxes;
using Weva.Layout.Flex;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Grid {
    public class RightColGridStretchHeightTests {
        const string Css = @"
            html, body { margin: 0; padding: 0; }
            .frame {
                position: relative;
                width: 1200px;
                height: 400px;
            }
            .tab-page {
                position: absolute;
                top: 0;
                left: 0;
                right: 0;
                bottom: 0;
                display: grid;
                grid-template-columns: 360px 420px;
                grid-template-rows: 1fr;
            }
            .right-col {
                display: flex;
                flex-direction: column;
                gap: 12px;
                min-height: 0;
            }
            .filler { height: 200px; flex-shrink: 0; }
        ";

        [Test]
        public void RightCol_grid_stretched_height_preserved_after_RepositionAbsolutes() {
            // .tab-page is position:absolute with inset:0 — height pinned to 400.
            // grid-template-rows: 1fr → row height = 400.
            // .right-col is the column-flex grid item in that row → Height should
            // be stamped to 400 by GridLayout.ApplyItemAlignment.
            //
            // Inside .right-col are 3 × 200px fillers + 2 × 12px gaps = 624px of
            // content. With the bug, the post-RepositionAbsolutes flex pass
            // rewrites .right-col.Height to 624 — overflowing the viewport.
            // With the fix, .right-col.Height stays at 400 (the grid cell).
            var (root, _, _) = Build(
                "<div class=\"frame\">" +
                "  <section class=\"tab-page\">" +
                "    <aside class=\"left-col\"></aside>" +
                "    <aside class=\"right-col\">" +
                "      <div class=\"filler\"></div>" +
                "      <div class=\"filler\"></div>" +
                "      <div class=\"filler\"></div>" +
                "    </aside>" +
                "  </section>" +
                "</div>",
                Css, viewportWidth: 1200, viewportHeight: 400);
            var rightCol = FindByClassName(root, "right-col");
            Assert.That(rightCol, Is.Not.Null, "right-col must build");
            System.Console.WriteLine($"right-col W={rightCol.Width} H={rightCol.Height}");
            Assert.That(rightCol.Height, Is.EqualTo(400.0).Within(1.0),
                $"right-col must keep its grid-stretched Height (=400); got {rightCol.Height}. " +
                $"Regression: post-RepositionAbsolutes flex pass re-inflated to content sum.");
        }

        [Test]
        public void RightCol_grid_stretched_when_viewport_shrinks() {
            // Same topology at half the viewport height — the grid row collapses
            // to 200 and .right-col must follow, even when its content (624px)
            // exceeds the cell. Without the fix, .right-col keeps 624.
            var (root, _, _) = Build(
                "<div class=\"frame\" style=\"height:200px\">" +
                "  <section class=\"tab-page\">" +
                "    <aside class=\"right-col\">" +
                "      <div class=\"filler\"></div>" +
                "      <div class=\"filler\"></div>" +
                "      <div class=\"filler\"></div>" +
                "    </aside>" +
                "  </section>" +
                "</div>",
                Css, viewportWidth: 1200, viewportHeight: 200);
            var rightCol = FindByClassName(root, "right-col");
            Assert.That(rightCol, Is.Not.Null);
            System.Console.WriteLine($"right-col W={rightCol.Width} H={rightCol.Height}");
            Assert.That(rightCol.Height, Is.EqualTo(200.0).Within(1.0),
                $"right-col must follow the shrunken viewport height (200); got {rightCol.Height}");
        }

        static FlexBox FindByClassName(Box root, string className) {
            if (root is FlexBox fb && root.Element != null) {
                string cls = root.Element.ClassName ?? "";
                if (cls == className || cls.Contains(" " + className) || cls.Contains(className + " ")) {
                    return fb;
                }
            }
            foreach (var c in root.ChildList) {
                var hit = FindByClassName(c, className);
                if (hit != null) return hit;
            }
            return null;
        }
    }
}
