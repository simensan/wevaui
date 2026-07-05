using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Flex;
using Weva.Layout.Text;
using Weva.Parsing;
using Weva.Reactive;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;

namespace Weva.Tests.Layout.Flex {
    // Audit LY2: the incremental layout path called
    // RepositionAbsolutes(..., pinOnly: false) at both of its call sites —
    // whole-tree (after a subtree splice, gated on LastOutOfFlowCount) and
    // the splice's final re-pin — with NO flex/grid restoration afterwards.
    // The pass's own header documents that contract: the shrink-to-fit
    // RelayoutContentAt re-stacks a flex/grid abs container's children as
    // block flow, so a reposition not followed by restoration must be
    // pinOnly. Net effect pre-fix: any warm-flip animation ANYWHERE in the
    // document block-stacked every `position:absolute > display:flex`
    // subtree (the tower's canonical .play-btn case) until the next full
    // layout.
    public class WarmFlipAbsoluteFlexPinTests {
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        [Test]
        public void Warm_flip_does_not_block_stack_flex_children_of_abs_container() {
            const string html =
                "<div class=\"page\">"
                + "<div class=\"bar\"><div class=\"fill\"></div></div>"
                + "<div class=\"play-action\">"
                +   "<button class=\"play-btn\">"
                +     "<span class=\"play-btn-label\">PLAY</span>"
                +     "<span class=\"play-btn-sub\">PRESS TO START</span>"
                +   "</button>"
                + "</div>"
                + "</div>";
            const string css = @"
                .page { position: relative; width: 1000px; height: 600px; }
                .bar { height: 12px; overflow: hidden; }
                .fill { height: 100%; width: 50%; }
                .play-action { position: absolute; bottom: 32px; left: 50%; }
                .play-btn { display: flex; flex-direction: column; align-items: center;
                            justify-content: center; gap: 2px; min-width: 260px; height: 76px; }
                .play-btn-label { font-size: 28px; }
                .play-btn-sub { font-size: 11px; }";

            var doc = HtmlParser.Parse(html);
            var engine = new CascadeEngine(new List<OriginatedStylesheet> {
                UserAgentStylesheet.Parse(), Author(css),
            });
            engine.ComputeAll(doc);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ResultMap) styles[kv.Key] = kv.Value;
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 1000, ViewportHeightPx = 600,
                RootFontSizePx = 16, DpiPixelsPerInch = 96,
            };
            var le = new LayoutEngine(new MonoFontMetrics());
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            System.Func<Element, ComputedStyle> styleOf =
                e => styles.TryGetValue(e, out var cs) ? cs : null;

            var root = le.Layout(doc, styleOf, ctx, tracker);
            tracker.Clear();
            var btn = FindFirstByClass<FlexBox>(root, "play-btn");
            Assert.That(btn, Is.Not.Null);
            var label0 = ChildAt(btn, 0);
            var sub0 = ChildAt(btn, 1);
            Assert.That(label0, Is.Not.Null);
            Assert.That(sub0, Is.Not.Null);
            double labelX = label0.X, labelY = label0.Y, subY = sub0.Y;
            // Sanity: column flex + align-items:center puts the label well
            // inside the 260px-wide button, not at the content-box left edge
            // where block flow would stack it.
            Assert.That(labelX, Is.GreaterThan(10),
                "sanity: label must start flex-centred inside the 260px button");

            Element fill = null;
            foreach (var e in engine.ResultMap.Keys) {
                if (e.GetAttribute("class") == "fill") { fill = e; break; }
            }
            Assert.That(fill, Is.Not.Null);

            // Warm flips on the clipped progress fill — the production
            // animation pattern that drives the incremental path.
            for (int i = 0; i < 3; i++) {
                styles[fill].Set("width", (i % 2 == 0) ? "90%" : "40%");
                tracker.MarkLayoutForElement(fill, styleOf);
                tracker.MarkDirty(fill, InvalidationKind.Paint);
                root = le.Layout(doc, styleOf, ctx, tracker);
                tracker.Clear();
            }
            Assert.That(le.LastPath, Is.EqualTo(LayoutEngine.LayoutPath.Subtree),
                "the flips must exercise the incremental path or this pin proves nothing");

            btn = FindFirstByClass<FlexBox>(root, "play-btn");
            Assert.That(btn, Is.Not.Null);
            var label = ChildAt(btn, 0);
            var sub = ChildAt(btn, 1);
            Assert.That(label, Is.Not.Null);
            Assert.That(sub, Is.Not.Null);
            Assert.That(label.X, Is.EqualTo(labelX).Within(0.5),
                "flex centring must survive warm flips — block-stacking pins the label to the " +
                "content-box left edge (audit LY2: destructive reposition with no restoration)");
            Assert.That(label.Y, Is.EqualTo(labelY).Within(0.5),
                "justify-content:center vertical placement must survive warm flips");
            Assert.That(sub.Y, Is.EqualTo(subY).Within(0.5),
                "the sub-label must stay in its flex slot, not re-stack as block flow");
        }

        static T FindFirstByClass<T>(Box root, string className) where T : Box {
            if (root is T t && root.Element != null) {
                string cls = root.Element.ClassName ?? "";
                if (cls == className || cls.Contains(" " + className) || cls.Contains(className + " ")) {
                    return t;
                }
            }
            foreach (var c in root.ChildList) {
                var hit = FindFirstByClass<T>(c, className);
                if (hit != null) return hit;
            }
            return null;
        }
    }
}
