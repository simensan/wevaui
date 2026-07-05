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
    // Audit LY5 + LY6 — the two remaining scroll-graft validation gaps:
    //
    // LY5: ValidateScrollBoundaryReuse compared CONTENT WIDTH only. The
    // design assumption "content is independent of the height it is given"
    // fails for %-height descendants: when a sibling animation changes the
    // container's height at constant width, the graft was declared valid
    // and the child kept last frame's resolved height forever.
    //
    // LY6: the width-correction path (RelayoutScrollContentFresh) rebuilt
    // and reflowed content but never ran positioning: fresh boxes default
    // Position=Static, so `position:absolute` content inside a corrected
    // scroll subtree rendered as plain in-flow blocks for that frame — and
    // every following frame while the width kept animating.
    public class ScrollGraftValidationTests {
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        static (Document doc, CascadeEngine engine, Dictionary<Element, ComputedStyle> styles,
                LayoutContext ctx, LayoutEngine le, InvalidationTracker tracker) Setup(string html, string css) {
            var doc = HtmlParser.Parse(html);
            var engine = new CascadeEngine(new List<OriginatedStylesheet> {
                UserAgentStylesheet.Parse(), Author(css),
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
            return (doc, engine, styles, ctx, le, tracker);
        }

        static Element ByClass(CascadeEngine engine, string cls) {
            foreach (var e in engine.ResultMap.Keys)
                if (e.GetAttribute("class") == cls) return e;
            return null;
        }

        static Box FindBlockByClass(Box root, string className) {
            if (root is BlockBox && root.Element != null && (root.Element.ClassName ?? "") == className) return root;
            foreach (var c in root.ChildList) {
                var hit = FindBlockByClass(c, className);
                if (hit != null) return hit;
            }
            return null;
        }

        [Test]
        public void Percent_height_child_tracks_container_height_across_grafts_LY5() {
            bool saved = LayoutEngine.EnableScrollBoundaryReuse;
            try {
                LayoutEngine.EnableScrollBoundaryReuse = true;
                var (doc, engine, styles, ctx, le, tracker) = Setup(
                    "<div class=\"wrap\"><div class=\"panel\"><div class=\"child\">c</div>"
                    + "<div class=\"filler\">f</div></div></div>",
                    @"* { box-sizing: border-box; }
                      .wrap { width: 300px; height: 400px; }
                      .panel { width: 100%; height: 100%; overflow-y: auto; }
                      .child { height: 100%; }
                      .filler { height: 600px; }");
                System.Func<Element, ComputedStyle> styleOf =
                    e => styles.TryGetValue(e, out var cs) ? cs : null;
                var wrap = ByClass(engine, "wrap");
                Assert.That(wrap, Is.Not.Null);

                var root = le.Layout(doc, styleOf, ctx, tracker);
                tracker.Clear();
                var panel0 = (BlockBox)FindBlockByClass(root, "panel");
                var child0 = FindBlockByClass(root, "child");
                Assert.That(child0.Height, Is.EqualTo(panel0.ContentHeight).Within(0.5),
                    "sanity: height:100% child fills the panel's content box");

                // Sibling-driven height change at CONSTANT width: .wrap's
                // height animates; .panel (100%/100%) stays subtree-clean ->
                // grafted. Pre-LY5 validation passed on width alone and the
                // child kept the 400px-era height.
                for (int frame = 0; frame < 3; frame++) {
                    styles[wrap].Set("height", (300 - frame * 50) + "px");
                    tracker.MarkLayoutForElement(wrap, styleOf);
                    tracker.MarkDirty(wrap, InvalidationKind.Paint);
                    tracker.MarkDirty(doc, InvalidationKind.Structure);
                    ctx.SnapshotStyles = null;
                    root = le.Layout(doc, styleOf, ctx, tracker);
                    tracker.Clear();

                    var panel = (BlockBox)FindBlockByClass(root, "panel");
                    var child = FindBlockByClass(root, "child");
                    Assert.That(panel.ContentHeight, Is.LessThan(400), "sanity: the panel actually shrank");
                    Assert.That(child.Height, Is.EqualTo(panel.ContentHeight).Within(0.5),
                        $"frame {frame}: height:100% child must track the panel's new height — a " +
                        "width-only graft validation freezes it at the old height (audit LY5)");
                }
            } finally {
                LayoutEngine.EnableScrollBoundaryReuse = saved;
            }
        }

        [Test]
        public void Width_corrected_graft_positions_absolute_content_LY6() {
            bool saved = LayoutEngine.EnableScrollBoundaryReuse;
            try {
                LayoutEngine.EnableScrollBoundaryReuse = true;
                var (doc, engine, styles, ctx, le, tracker) = Setup(
                    "<div class=\"outer\"><div class=\"gw\">"
                    + "<div class=\"badge\"></div>"
                    + "<div class=\"row\">content row</div>"
                    + "</div></div>",
                    @"* { box-sizing: border-box; }
                      .outer { width: 200px; }
                      .gw { position: relative; width: 100%; height: 150px; overflow-y: auto; }
                      .badge { position: absolute; top: 5px; left: 5px; width: 10px; height: 10px; }
                      .row { height: 30px; }");
                System.Func<Element, ComputedStyle> styleOf =
                    e => styles.TryGetValue(e, out var cs) ? cs : null;
                var outer = ByClass(engine, "outer");
                Assert.That(outer, Is.Not.Null);

                var root = le.Layout(doc, styleOf, ctx, tracker);
                tracker.Clear();
                var badge0 = FindBlockByClass(root, "badge");
                var row0 = FindBlockByClass(root, "row");
                Assert.That(badge0.X, Is.EqualTo(5).Within(0.5), "sanity: fresh layout positions the badge");
                double rowY = row0.Y;

                // Widen .outer -> .gw (width:100%) width changes -> the graft
                // is corrected (RelayoutScrollContentFresh). Pre-LY6 the
                // rebuilt badge stayed Position=Static: in-flow at the top,
                // displacing .row.
                styles[outer].Set("width", "500px");
                tracker.MarkLayoutForElement(outer, styleOf);
                tracker.MarkDirty(outer, InvalidationKind.Paint);
                tracker.MarkDirty(doc, InvalidationKind.Structure);
                ctx.SnapshotStyles = null;
                root = le.Layout(doc, styleOf, ctx, tracker);
                tracker.Clear();
                Assert.That(le.LastScrollReuseCorrectCount, Is.GreaterThanOrEqualTo(1),
                    "sanity: the width change must have taken the graft-correction path");

                var badge = FindBlockByClass(root, "badge");
                var row = FindBlockByClass(root, "row");
                Assert.That(badge.X, Is.EqualTo(5).Within(0.5),
                    "corrected content must run positioning — a Static badge lays out in-flow (audit LY6)");
                Assert.That(badge.Y, Is.EqualTo(5).Within(0.5),
                    "abs badge must pin to top:5px inside the corrected subtree (audit LY6)");
                Assert.That(row.Y, Is.EqualTo(rowY).Within(0.5),
                    "the in-flow row must not be displaced by an unpositioned badge (audit LY6)");
            } finally {
                LayoutEngine.EnableScrollBoundaryReuse = saved;
            }
        }
    }
}
