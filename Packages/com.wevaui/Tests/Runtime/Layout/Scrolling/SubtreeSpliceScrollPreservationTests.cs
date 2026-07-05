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
    // Round 10 of the typing-scrolls-to-top hunt. The incremental SUBTREE
    // path returns before every full-layout prune site; a scroll container
    // recycled inside a splice would leave the alive-by-instance /
    // dead-by-generation phantom from the live state tables with nothing to
    // sweep it — hence the generation-only ReanchorStaleGenerations sweep on
    // that path. This pins the observable invariants across mixed
    // subtree/full warm flips around a scrolled container (a flex-item
    // height animation next to a scroller): the offset survives every
    // frame, and NO entry is ever left generation-stale (a stale entry
    // means some layout path skipped its re-anchor sweep).
    public class SubtreeSpliceScrollPreservationTests {
        // .anim is a FLEX ITEM whose outer height changes — its own splice
        // bails (flex items require unchanged outer geometry), .row's auto
        // height depends on it (unstable), so the splice promotes to the
        // fixed-height .section and replaces the WHOLE section subtree,
        // .scroller included.
        const string Html =
            "<div class=\"page\">"
            + "<div class=\"section\">"
            +   "<div class=\"row\"><div class=\"anim\">animates</div></div>"
            +   "<div class=\"scroller\"><div class=\"content\">tall content</div></div>"
            + "</div>"
            + "<div class=\"below\">below</div>"
            + "</div>";

        const string Css = @"
            .page { width: 900px; }
            .section { height: 300px; }
            .row { display: flex; }
            .anim { width: 150px; height: 60px; }
            .scroller { height: 100px; overflow-y: auto; }
            .content { height: 500px; }
            .below { height: 40px; }";

        [Test]
        public void Descendant_scroller_survives_a_subtree_splice() {
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

            Element anim = null, scrollerEl = null;
            foreach (var e in engine.ResultMap.Keys) {
                var cls = e.GetAttribute("class");
                if (cls == "anim") anim = e;
                if (cls == "scroller") scrollerEl = e;
            }
            Assert.That(anim, Is.Not.Null);
            Assert.That(scrollerEl, Is.Not.Null);

            var root = le.Layout(doc, styleOf, ctx, tracker);
            tracker.Clear();

            var scrollerBox = FindBoxFor(root, scrollerEl);
            Assert.That(scrollerBox, Is.Not.Null);
            var scrolls = le.ScrollContainer;
            var state = scrolls.GetOrCreate(scrollerBox);
            Assert.That(state.CanScrollY, Is.True, "sanity: .scroller must overflow");
            Assert.That(state.MaxScrollY, Is.GreaterThanOrEqualTo(300), "sanity: enough overflow");
            state.ScrollTop = 300;
            Assert.That(state.ScrollY, Is.EqualTo(300).Within(0.5));

            // Warm flips on the sibling: .section has a fixed height so the
            // splice promotes there, and .scroller rides inside the replaced
            // subtree as a descendant.
            bool sawSubtree = false;
            for (int f = 0; f < 6; f++) {
                styles[anim].Set("height", (60 + (f % 3) * 20) + "px");
                tracker.MarkLayoutForElement(anim, styleOf);
                tracker.MarkDirty(anim, InvalidationKind.Paint);
                root = le.Layout(doc, styleOf, ctx, tracker);
                tracker.Clear();
                if (le.LastPath == LayoutEngine.LayoutPath.Subtree) sawSubtree = true;

                var boxNow = FindBoxFor(root, scrollerEl);
                Assert.That(boxNow, Is.Not.Null, $"frame {f}: .scroller box must exist");
                var stateNow = scrolls.Get(boxNow);
                Assert.That(stateNow, Is.Not.Null,
                    $"frame {f}: the live .scroller box must have a readable scroll state");
                Assert.That(stateNow.ScrollY, Is.EqualTo(300).Within(0.5),
                    $"frame {f}: the descendant scroller's offset must survive the splice");

                // No phantom: every entry must be readable at its box's
                // current generation (a STALE entry means a path skipped the
                // re-anchor sweep).
                foreach (var kv in scrolls.All) {
                    Assert.That(kv.Value.OwnerGeneration, Is.EqualTo(kv.Key.PoolGeneration),
                        $"frame {f}: generation-stale entry left behind on " +
                        $"<{kv.Key.Element?.TagName ?? "anon"}.{kv.Key.Element?.ClassName ?? ""}>");
                }
            }
            Assert.That(sawSubtree, Is.True,
                "the flips must exercise the incremental subtree path or this pin proves nothing");
        }

        static Box FindBoxFor(Box root, Element el) {
            if (ReferenceEquals(root.Element, el)) return root;
            foreach (var c in root.ChildList) {
                var hit = FindBoxFor(c, el);
                if (hit != null) return hit;
            }
            return null;
        }
    }
}
