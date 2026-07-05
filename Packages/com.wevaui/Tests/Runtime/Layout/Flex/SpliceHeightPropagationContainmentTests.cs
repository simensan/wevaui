using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using Weva.Parsing;
using Weva.Reactive;

namespace Weva.Tests.Layout.Flex {
    // In-editor finding (2026-07, audit-validation page): content below an
    // animating section crept UPWARD every warm-flip frame and snapped back
    // on each full layout. Root cause (pre-existing, exposed by the page):
    // when the incremental splice re-lays a subtree containing a ROW flex,
    // BlockLayout first stacks the flex children as blocks (pre-flex height =
    // SUM), then FlexLayout flexes it (height = MAX) and its
    // ShiftFollowingSiblingsIfHeightChanged propagated the (sum - max) delta
    // THROUGH the splice root into the stale ancestor chain — ancestors that
    // already carried the post-flex height from the pass that laid them. The
    // double-subtraction accumulated per frame (~the animating child's height
    // per flip). Fixed with LayoutContext.HeightPropagationBoundary: the
    // splice (and the scroll-graft correction path) pin the boundary at the
    // subtree root; BlockFlowAdjuster stops there.
    public class SpliceHeightPropagationContainmentTests {
        const string Html =
            "<div class=\"page\">"
            + "<div class=\"section\">"
            +   "<div class=\"row\">"
            +     "<div class=\"anim\">animates</div>"
            +     "<div class=\"tall\">tall fixed sibling</div>"
            +   "</div>"
            + "</div>"
            + "<div class=\"below\">content below must not creep upward</div>"
            + "</div>";

        // .anim (60-100px) is SHORTER than .tall (130px): the row's pre-flex
        // stacked height (anim+130) differs from its flexed height (130) by
        // exactly .anim's height — the delta that leaked per frame.
        const string Css = @"
            .page { width: 900px; }
            .section { padding: 10px; }
            .row { display: flex; }
            .anim { width: 150px; height: 60px; }
            .tall { width: 200px; height: 130px; }
            .below { height: 40px; }";

        [Test]
        public void Warm_flips_do_not_leak_the_preflex_delta_into_ancestors() {
            var doc = HtmlParser.Parse(Html);
            var engine = new CascadeEngine(new List<OriginatedStylesheet> {
                UserAgentStylesheet.Parse(),
                OriginatedStylesheet.Author(CssParser.Parse(Css)),
            });
            engine.ComputeAll(doc);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ResultMap) styles[kv.Key] = kv.Value;
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 1000, ViewportHeightPx = 700,
                RootFontSizePx = 16, DpiPixelsPerInch = 96,
                Snapshot = engine.LastSnapshot, SnapshotStyles = engine.Styles,
            };
            var le = new LayoutEngine(new MonoFontMetrics());
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            System.Func<Element, ComputedStyle> styleOf =
                e => styles.TryGetValue(e, out var cs) ? cs : null;

            Element anim = null;
            foreach (var e in engine.ResultMap.Keys)
                if (e.GetAttribute("class") == "anim") { anim = e; break; }
            Assert.That(anim, Is.Not.Null);

            var root = le.Layout(doc, styleOf, ctx, tracker);
            tracker.Clear();
            double sectionH0 = FindBlock(root, "section").Height;
            double belowY0 = AbsY(root, "below");
            Assert.That(sectionH0, Is.GreaterThan(130), "sanity: section wraps the 130px row + padding");

            // Warm flips: animate the flex item's height BELOW the fixed
            // sibling's, so the row's flexed height (130) never changes and
            // every frame stays on the incremental path.
            bool sawSubtree = false;
            for (int f = 0; f < 6; f++) {
                styles[anim].Set("height", (60 + (f % 3) * 20) + "px");
                tracker.MarkLayoutForElement(anim, styleOf);
                tracker.MarkDirty(anim, InvalidationKind.Paint);
                root = le.Layout(doc, styleOf, ctx, tracker);
                tracker.Clear();
                if (le.LastPath == LayoutEngine.LayoutPath.Subtree) sawSubtree = true;

                Assert.That(FindBlock(root, "section").Height, Is.EqualTo(sectionH0).Within(0.5),
                    $"frame {f}: the section's height must not absorb the row's pre-flex → " +
                    "post-flex delta again (it already carries the post-flex height)");
                Assert.That(AbsY(root, "below"), Is.EqualTo(belowY0).Within(0.5),
                    $"frame {f}: content below must not creep upward per warm flip");
            }
            Assert.That(sawSubtree, Is.True,
                "the flips must exercise the incremental path or this pin proves nothing");
        }

        static BlockBox FindBlock(Box root, string cls) {
            if (root is BlockBox bb && root.Element != null && (root.Element.ClassName ?? "") == cls) return bb;
            foreach (var c in root.ChildList) {
                var hit = FindBlock(c, cls);
                if (hit != null) return hit;
            }
            return null;
        }

        static double AbsY(Box root, string cls) {
            double found = double.NaN;
            void Walk(Box b, double absY) {
                double y = absY + b.Y;
                if (b is BlockBox && b.Element != null && (b.Element.ClassName ?? "") == cls) found = y;
                foreach (var c in b.ChildList) Walk(c, y);
            }
            Walk(root, 0);
            return found;
        }
    }
}
