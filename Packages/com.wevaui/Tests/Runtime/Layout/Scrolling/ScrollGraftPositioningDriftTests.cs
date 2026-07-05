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
    // Audit LY3: scroll-boundary reuse (Box.ReuseContent) freezes a clean
    // scroll container's content across full layouts — BlockLayout and
    // AnalyzeLayoutFeatures honour the freeze, but PositioningPass did NOT:
    // every full layout re-ran CompressOutOfFlow (subtracts each OOF
    // child's extent from following siblings' Y — accumulative) and
    // ApplyRelative (`X += dx` — accumulative) over the frozen content.
    // Under a propagating animation (full layout per frame) grafted
    // relative/abs-adjacent content crept by its offset EVERY FRAME.
    public class ScrollGraftPositioningDriftTests {
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        const string Html =
            "<div class=\"page\">"
            + "<div class=\"anim\">grows</div>"
            + "<div class=\"badge\"></div>" // positioned work OUTSIDE the graft
            + "<div class=\"gw\">"
            +   "<div class=\"top\">top</div>"
            +   "<div class=\"abs\"></div>"     // OOF inside the graft: compress target
            +   "<div class=\"after\">after</div>"
            +   "<div class=\"rel\">rel</div>"  // relative inside the graft: += drift target
            + "</div>"
            + "</div>";

        const string Css = @"
            * { box-sizing: border-box; }
            .page { position: relative; width: 800px; }
            .anim { height: 20px; }
            .badge { position: absolute; top: 4px; right: 4px; width: 10px; height: 10px; }
            .gw { width: 400px; height: 200px; overflow-y: auto; }
            .top { height: 30px; }
            .abs { position: absolute; top: 0; left: 0; width: 20px; height: 40px; }
            .after { height: 25px; }
            .rel { position: relative; top: 10px; height: 25px; }";

        [Test]
        public void Grafted_positioned_content_does_not_drift_across_full_layouts() {
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

                // Propagating animation OUTSIDE the graft: .anim's height
                // changes each frame -> full layout each frame; .gw stays
                // clean -> its content is grafted (ReuseContent).
                double relY = double.NaN, afterY = double.NaN;
                for (int frame = 0; frame < 4; frame++) {
                    styles[anim].Set("height", (22 + frame * 2) + "px");
                    tracker.MarkLayoutForElement(anim, styleOf);
                    tracker.MarkDirty(anim, InvalidationKind.Paint);
                    tracker.MarkDirty(doc, InvalidationKind.Structure); // force the full path
                    ctx.SnapshotStyles = null;
                    root = le.Layout(doc, styleOf, ctx, tracker);
                    tracker.Clear();

                    var rel = FindByClass(root, "rel");
                    var after = FindByClass(root, "after");
                    Assert.That(rel, Is.Not.Null);
                    Assert.That(after, Is.Not.Null);
                    if (frame == 0) { relY = rel.Y; afterY = after.Y; continue; }
                    Assert.That(rel.Y, Is.EqualTo(relY).Within(0.01),
                        $"frame {frame}: .rel (position:relative; top:10px) inside the grafted scroll " +
                        "content must not accumulate its offset per full layout (audit LY3)");
                    Assert.That(after.Y, Is.EqualTo(afterY).Within(0.01),
                        $"frame {frame}: .after must not be re-compressed for the abs sibling " +
                        "on every full layout (audit LY3)");
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
