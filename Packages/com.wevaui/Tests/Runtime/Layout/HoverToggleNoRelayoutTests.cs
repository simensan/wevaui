using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using Weva.Parsing;
using Weva.Reactive;

namespace Weva.Tests.Layout {
    // Validates the IncrementalLayoutGate end-to-end through the cascade +
    // layout engines for paint-only vs. layout-affecting :hover toggles.
    //
    // v1 contract: the gate keys off the InvalidationTracker only. Pseudo-class
    // state flips report PseudoClassState (which implies Style but never
    // Layout). Therefore:
    //
    //   - :hover changing color/background/border-color/opacity/outline →
    //       layout SKIPPED (correct).
    //   - :hover changing width/border-width/padding/margin/font-size →
    //       caller must mark Layout dirty in addition to PseudoClassState.
    //       If the caller forgets, the gate skips and the result is stale —
    //       this is the v1 "callers must annotate" simplification documented
    //       in PLAN §incremental layout.
    public class HoverToggleNoRelayoutTests {
        sealed class HoverState : IElementStateProvider {
            Element hovered;
            long version;
            public Element Hovered => hovered;
            public void SetHover(Element e) {
                if (!ReferenceEquals(hovered, e)) { hovered = e; version++; }
            }
            public ElementState GetState(Element e) {
                return ReferenceEquals(e, hovered) ? ElementState.Hover : ElementState.None;
            }
            public long Version => version;
        }

        sealed class Harness {
            public Document Doc;
            public CascadeEngine Cascade;
            public LayoutEngine LayoutEngine;
            public LayoutContext Ctx;
            public InvalidationTracker Tracker;
            public HoverState State;

            public Box Layout() {
                var styles = new Dictionary<Element, ComputedStyle>();
                foreach (var kv in Cascade.ComputeAll(Doc, State)) styles[kv.Key] = kv.Value;
                var box = LayoutEngine.Layout(Doc, e => styles.TryGetValue(e, out var cs) ? cs : null, Ctx, Tracker);
                Tracker.Clear();
                return box;
            }

            public void ToggleHover(Element next) {
                Element prev = State.Hovered;
                State.SetHover(next);
                if (prev != null) Tracker.MarkDirty(prev, InvalidationKind.PseudoClassState);
                if (next != null) Tracker.MarkDirty(next, InvalidationKind.PseudoClassState);
            }
        }

        static Harness Build(string html, string css) {
            var doc = HtmlParser.Parse(html);
            var sheets = new List<OriginatedStylesheet> {
                OriginatedStylesheet.UserAgent(CssParser.Parse(LayoutTestHelpers.BuiltinUserAgent)),
                OriginatedStylesheet.Author(CssParser.Parse(css))
            };
            return new Harness {
                Doc = doc,
                Cascade = new CascadeEngine(sheets),
                LayoutEngine = new LayoutEngine(new MonoFontMetrics()),
                Ctx = new LayoutContext(new MonoFontMetrics()) {
                    ViewportWidthPx = 800, ViewportHeightPx = 600
                },
                Tracker = new InvalidationTracker(),
                State = new HoverState()
            };
        }

        [Test]
        public void Hover_changing_color_only_skips_layout() {
            var h = Build(
                "<button id=\"b\">click</button>",
                "button { color: black } button:hover { color: red }");
            var btn = h.Doc.GetElementById("b");
            h.Layout(); // first
            long skipsBefore = h.LayoutEngine.SkipCount;

            h.ToggleHover(btn);
            h.Layout();
            Assert.That(h.LayoutEngine.SkipCount, Is.EqualTo(skipsBefore + 1),
                ":hover { color: red } is paint-only — layout must be skipped");
        }

        // P4 depends on LastPath being a reliable signal (it's now set on
        // every path, not just under CollectStageTimings) so the lifecycle can
        // skip the post-layout ElementToBoxIndex rebuild when the box tree is
        // returned unchanged.
        [Test]
        public void LastPath_is_Skip_on_paint_only_hover_and_Full_on_first_layout() {
            var h = Build(
                "<button id=\"b\">click</button>",
                "button { color: black } button:hover { color: red }");
            var btn = h.Doc.GetElementById("b");
            h.Layout(); // first layout — full
            Assert.That(h.LayoutEngine.LastPath, Is.EqualTo(LayoutEngine.LayoutPath.Full),
                "the first layout (no survivor) must take the Full path");

            h.ToggleHover(btn);
            h.Layout(); // paint-only hover — gate skips
            Assert.That(h.LayoutEngine.LastPath, Is.EqualTo(LayoutEngine.LayoutPath.Skip),
                "a paint-only hover must be a Skip — the box tree is unchanged, so the lifecycle can keep its Element->Box map");
        }

        [Test]
        public void Hover_changing_background_only_skips_layout() {
            var h = Build(
                "<button id=\"b\">click</button>",
                "button { background: white } button:hover { background: yellow }");
            var btn = h.Doc.GetElementById("b");
            h.Layout();
            long skipsBefore = h.LayoutEngine.SkipCount;

            h.ToggleHover(btn);
            h.Layout();
            Assert.That(h.LayoutEngine.SkipCount, Is.EqualTo(skipsBefore + 1));
        }

        [Test]
        public void Hover_changing_border_color_only_skips_layout() {
            // border-color is paint-only; only border-width affects layout.
            var h = Build(
                "<button id=\"b\">click</button>",
                "button { border: 2px solid black } button:hover { border-color: red }");
            var btn = h.Doc.GetElementById("b");
            h.Layout();
            long skipsBefore = h.LayoutEngine.SkipCount;

            h.ToggleHover(btn);
            h.Layout();
            Assert.That(h.LayoutEngine.SkipCount, Is.EqualTo(skipsBefore + 1));
        }

        [Test]
        public void Focus_adding_outline_skips_layout() {
            // outline does not participate in box sizing per CSS UI L4 §4.
            var h = Build(
                "<button id=\"b\">click</button>",
                "button:focus { outline: 2px solid blue }");
            var btn = h.Doc.GetElementById("b");
            h.Layout();
            long skipsBefore = h.LayoutEngine.SkipCount;

            // Use PseudoClassState as a stand-in for the focus flip; the gate
            // doesn't distinguish between Hover/Focus/Active — they all map to
            // PseudoClassState | Style, never Layout.
            h.Tracker.MarkDirty(btn, InvalidationKind.PseudoClassState);
            h.Layout();
            Assert.That(h.LayoutEngine.SkipCount, Is.EqualTo(skipsBefore + 1));
        }

        [Test]
        public void Hover_changing_opacity_skips_layout() {
            var h = Build(
                "<button id=\"b\">click</button>",
                "button:hover { opacity: 0.5 }");
            var btn = h.Doc.GetElementById("b");
            h.Layout();
            long skipsBefore = h.LayoutEngine.SkipCount;

            h.ToggleHover(btn);
            h.Layout();
            Assert.That(h.LayoutEngine.SkipCount, Is.EqualTo(skipsBefore + 1));
        }

        [Test]
        public void Hover_changing_box_shadow_skips_layout() {
            var h = Build(
                "<button id=\"b\">click</button>",
                "button:hover { box-shadow: 0 4px 8px black }");
            var btn = h.Doc.GetElementById("b");
            h.Layout();
            long skipsBefore = h.LayoutEngine.SkipCount;

            h.ToggleHover(btn);
            h.Layout();
            Assert.That(h.LayoutEngine.SkipCount, Is.EqualTo(skipsBefore + 1));
        }

        [Test]
        public void Hover_changing_transform_skips_layout() {
            // transform creates a new stacking context but does NOT affect
            // box-tree layout positions/sizes; gate must skip.
            var h = Build(
                "<button id=\"b\">click</button>",
                "button:hover { transform: scale(1.05) }");
            var btn = h.Doc.GetElementById("b");
            h.Layout();
            long skipsBefore = h.LayoutEngine.SkipCount;

            h.ToggleHover(btn);
            h.Layout();
            Assert.That(h.LayoutEngine.SkipCount, Is.EqualTo(skipsBefore + 1));
        }

        [Test]
        public void Active_changing_width_must_be_marked_layout_to_relayout() {
            // v1 contract: caller must explicitly mark Layout when a hover/active
            // rule touches a layout-affecting property. Without that, the gate
            // skips. This test documents the contract by demonstrating both
            // paths.
            var h = Build(
                "<button id=\"b\">click</button>",
                "button { width: 100px } button:hover { width: 120px }");
            var btn = h.Doc.GetElementById("b");
            h.Layout();
            long skipsBeforeImplicit = h.LayoutEngine.SkipCount;

            // Caller marks ONLY PseudoClassState — gate skips.
            h.ToggleHover(btn);
            h.Layout();
            Assert.That(h.LayoutEngine.SkipCount, Is.EqualTo(skipsBeforeImplicit + 1),
                "without explicit Layout mark, gate skips even on a width change");

            // Now caller marks Layout explicitly — gate runs full pass.
            long skipsBeforeExplicit = h.LayoutEngine.SkipCount;
            h.ToggleHover(null);
            h.Tracker.MarkDirty(btn, InvalidationKind.Layout);
            h.Layout();
            Assert.That(h.LayoutEngine.SkipCount, Is.EqualTo(skipsBeforeExplicit),
                "explicit Layout mark forces a full pass");
        }

        [Test]
        public void Hover_border_width_change_with_explicit_layout_mark_relayouts() {
            var h = Build(
                "<button id=\"b\">click</button>",
                "button { border: 2px solid black } button:hover { border-width: 4px }");
            var btn = h.Doc.GetElementById("b");
            h.Layout();
            long skipsBefore = h.LayoutEngine.SkipCount;

            h.ToggleHover(btn);
            h.Tracker.MarkDirty(btn, InvalidationKind.Layout);
            h.Layout();
            Assert.That(h.LayoutEngine.SkipCount, Is.EqualTo(skipsBefore),
                "border-width change with explicit Layout mark must relayout");
        }

        [Test]
        public void Repeated_paint_only_hovers_all_skip() {
            var h = Build(
                "<button id=\"b\">click</button>",
                "button:hover { background: yellow }");
            var btn = h.Doc.GetElementById("b");
            h.Layout(); // first pass

            for (int i = 0; i < 20; i++) {
                h.ToggleHover((i & 1) == 0 ? btn : null);
                h.Layout();
            }
            Assert.That(h.LayoutEngine.SkipCount, Is.EqualTo(20));
        }

        // Audit LY1 (2026-07): LastPath = Subtree was assigned only inside the
        // `if (timing)` diagnostics block, so with CollectStageTimings off
        // (production default) a successful subtree splice left LastPath at
        // whatever the previous frame set. The dangerous sequence is
        // Skip -> Subtree: the lifecycle keys the ElementToBoxIndex rebuild on
        // `LastPath != Skip` (UIDocumentLifecycle), so a stale Skip left the
        // Element->Box map pointing at boxes RecycleSubtree had just pooled.
        // The two pre-existing Subtree assertions both ran with
        // CollectStageTimings = true, which is exactly how this hid.
        [Test]
        public void LastPath_is_Subtree_after_a_skip_with_stage_timings_off() {
            var h = Build(
                "<div class=\"app\"><div class=\"bar\"><div id=\"f\" class=\"fill\"></div></div>" +
                "<div class=\"cell\">content</div></div>",
                ".bar { height: 12px; overflow: hidden; } " +
                ".fill { height: 100%; width: 50%; } " +
                ".cell { padding: 10px; }");
            Assert.That(h.LayoutEngine.CollectStageTimings, Is.False,
                "this pin must run with diagnostics OFF — the regression only fires there");
            var fill = h.Doc.GetElementById("f");
            System.Func<Element, ComputedStyle> styleOf =
                e => h.Cascade.ResultMap.TryGetValue(e, out var cs) ? cs : null;

            h.Layout(); // first layout — Full
            Assert.That(h.LayoutEngine.LastPath, Is.EqualTo(LayoutEngine.LayoutPath.Full));

            // Frame 2: paint-only — Skip (this is what goes stale).
            h.Tracker.MarkDirty(fill, InvalidationKind.Paint);
            h.Layout();
            Assert.That(h.LayoutEngine.LastPath, Is.EqualTo(LayoutEngine.LayoutPath.Skip));

            // Frame 3: layout-dirty width flip on the clipped fill — the
            // subtree splice runs the layout algorithm, so LastPath must say
            // so even without CollectStageTimings.
            h.Cascade.ResultMap[fill].Set("width", "90%");
            h.Tracker.MarkLayoutForElement(fill, styleOf);
            h.Tracker.MarkDirty(fill, InvalidationKind.Paint);
            h.Layout();
            Assert.That(h.LayoutEngine.LastPath, Is.EqualTo(LayoutEngine.LayoutPath.Subtree),
                "a successful subtree splice must record LastPath = Subtree with diagnostics off — " +
                "a stale Skip makes the lifecycle keep an Element->Box map full of recycled boxes");
        }
    }
}
