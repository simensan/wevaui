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

namespace Weva.Tests.Layout.Scrolling {
    // In-editor find (2026-07, audit-validation page §6): with a FLEX row
    // containing an animating-height box and a scroll container, the gap
    // between the scroll list's first in-flow item and the item following an
    // absolutely-positioned child CHANGED WITH THE ANIMATOR HEIGHT — the abs
    // child was intermittently treated as in-flow (gap grew by roughly the
    // badge height), depending on which relayout path ran that frame. The
    // abs child is out-of-flow: the following sibling must sit at the same
    // fixed offset under the first item on EVERY frame, equal to the initial
    // full-layout value.
    public class ScrollGraftAbsFlowSpacingTests {
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        // Mirrors audit-validation.html §6: flex row, animating sibling,
        // scroll container with top item / abs badge / following items.
        const string Html =
            "<div class=\"page\">"
            + "<div class=\"row\">"
            +   "<div class=\"anim\">grows</div>"
            +   "<div class=\"scroll\">"
            +     "<div class=\"top\">top item</div>"
            +     "<div class=\"abs\">ABS</div>"
            +     "<div class=\"after\">after the abs badge</div>"
            +     "<div class=\"rel\">relative</div>"
            +     "<div class=\"fill\">filler</div>"
            +     "<div class=\"fill\">filler</div>"
            +   "</div>"
            + "</div>"
            + "</div>";

        const string Css = @"
            * { box-sizing: border-box; }
            .page { width: 800px; }
            .row { display: flex; gap: 12px; }
            .anim { width: 180px; height: 90px; }
            .scroll { position: relative; width: 300px; height: 130px; overflow-y: auto; }
            .scroll div { padding: 5px 10px; }
            .top { height: 26px; }
            .abs { position: absolute; top: 4px; right: 4px; width: 46px; height: 40px; }
            .after { height: 26px; }
            .rel { position: relative; top: 10px; height: 26px; }
            .fill { height: 26px; }";

        [Test]
        public void Gap_after_abs_badge_is_independent_of_the_animating_flex_sibling() {
            bool saved = LayoutEngine.EnableScrollBoundaryReuse;
            try {
                LayoutEngine.EnableScrollBoundaryReuse = true;
                var doc = HtmlParser.Parse(Html);
                var engine = new CascadeEngine(new List<OriginatedStylesheet> {
                    UserAgentStylesheet.Parse(), Author(Css),
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
                foreach (var e in engine.ResultMap.Keys) {
                    if (e.GetAttribute("class") == "anim") { anim = e; break; }
                }
                Assert.That(anim, Is.Not.Null);

                var root = le.Layout(doc, styleOf, ctx, tracker);
                tracker.Clear();

                // Reference geometry from the initial full layout: the abs
                // badge is out-of-flow, so .after sits directly under .top.
                var top0 = FindByClass(root, "top");
                var after0 = FindByClass(root, "after");
                Assert.That(top0, Is.Not.Null);
                Assert.That(after0, Is.Not.Null);
                double expectedGap = after0.Y - (top0.Y + top0.Height);

                // Animate the flex sibling's height like the validation page
                // (90px -> 150px and back), full layout per frame.
                double[] heights = { 100, 118, 136, 150, 132, 96 };
                for (int frame = 0; frame < heights.Length; frame++) {
                    styles[anim].Set("height", heights[frame] + "px");
                    tracker.MarkLayoutForElement(anim, styleOf);
                    tracker.MarkDirty(anim, InvalidationKind.Paint);
                    ctx.SnapshotStyles = null;
                    root = le.Layout(doc, styleOf, ctx, tracker);
                    tracker.Clear();

                    var top = FindByClass(root, "top");
                    var after = FindByClass(root, "after");
                    Assert.That(top, Is.Not.Null);
                    Assert.That(after, Is.Not.Null);
                    double gap = after.Y - (top.Y + top.Height);
                    Assert.That(gap, Is.EqualTo(expectedGap).Within(0.01),
                        $"frame {frame} (anim height {heights[frame]}px): the gap between .top and " +
                        ".after must not depend on the animating flex sibling — the abs badge is " +
                        "out-of-flow and must never push .after down");
                }
            } finally {
                LayoutEngine.EnableScrollBoundaryReuse = saved;
            }
        }

        static Box FindByClass(Box root, string className) {
            if (root.Element != null) {
                string cls = root.Element.ClassName ?? "";
                if (cls == className) return root;
            }
            foreach (var c in root.ChildList) {
                var hit = FindByClass(c, className);
                if (hit != null) return hit;
            }
            return null;
        }
    }
}
