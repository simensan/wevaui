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

namespace Weva.Tests.Layout {
    public class LayoutPersistenceTests {
        const string UA = LayoutTestHelpers.BuiltinUserAgent;

        sealed class Harness {
            public Document Doc;
            public CascadeEngine Cascade;
            public LayoutEngine LayoutEngine;
            public LayoutContext Ctx;
            public InvalidationTracker Tracker;
            public Dictionary<Element, ComputedStyle> Styles;

            public Box Layout() {
                Styles = new Dictionary<Element, ComputedStyle>();
                foreach (var kv in Cascade.ComputeAll(Doc)) Styles[kv.Key] = kv.Value;
                var box = LayoutEngine.Layout(Doc, e => Styles.TryGetValue(e, out var cs) ? cs : null, Ctx, Tracker);
                Tracker.Clear();
                return box;
            }
        }

        static Harness Build(string html, string css = null) {
            var doc = HtmlParser.Parse(html);
            var sheets = new List<OriginatedStylesheet> {
                OriginatedStylesheet.UserAgent(CssParser.Parse(UA))
            };
            if (!string.IsNullOrEmpty(css)) sheets.Add(OriginatedStylesheet.Author(CssParser.Parse(css)));
            return new Harness {
                Doc = doc,
                Cascade = new CascadeEngine(sheets),
                LayoutEngine = new LayoutEngine(new MonoFontMetrics()),
                Ctx = new LayoutContext(new MonoFontMetrics()) {
                    ViewportWidthPx = 800,
                    ViewportHeightPx = 600
                },
                Tracker = new InvalidationTracker()
            };
        }

        [Test]
        public void First_layout_runs_and_populates_lastRoot() {
            var h = Build("<div></div>");
            Assert.That(h.LayoutEngine.LastRoot, Is.Null);
            var box = h.Layout();
            Assert.That(box, Is.Not.Null);
            Assert.That(h.LayoutEngine.LastRoot, Is.SameAs(box));
            Assert.That(h.LayoutEngine.SkipCount, Is.EqualTo(0));
        }

        [Test]
        public void Second_layout_with_no_dirty_skips() {
            var h = Build("<div><span>x</span></div>");
            var first = h.Layout();
            // No tracker dirty bits — gate must skip.
            var second = h.Layout();
            Assert.That(second, Is.SameAs(first), "skipped layout should return identical instance");
            Assert.That(h.LayoutEngine.SkipCount, Is.EqualTo(1));
        }

        [Test]
        public void Box_tree_identity_preserved_across_skipped_frames() {
            var h = Build("<div><p>hello</p></div>");
            var first = h.Layout();
            for (int i = 0; i < 5; i++) {
                var box = h.Layout();
                Assert.That(box, Is.SameAs(first));
            }
            Assert.That(h.LayoutEngine.SkipCount, Is.EqualTo(5));
        }

        [Test]
        public void Layout_dirty_forces_full_pass() {
            var h = Build("<div><span>x</span></div>");
            h.Layout(); // first
            var div = LayoutTestHelpers.FindByTag(h.Doc, "div");
            h.Tracker.MarkDirty(div, InvalidationKind.Layout);
            var skipsBefore = h.LayoutEngine.SkipCount;
            h.Layout();
            Assert.That(h.LayoutEngine.SkipCount, Is.EqualTo(skipsBefore),
                "Layout-flagged frame should not be a skip");
        }

        [Test]
        public void Replaced_scroll_container_preserves_scroll_position() {
            var h = Build(
                "<div id=\"viewport\"><div id=\"content\"></div></div>",
                "#viewport { overflow-y: auto; width: 200px; height: 100px; } #content { height: 500px; }");
            var first = h.Layout();
            var viewport = h.Doc.GetElementById("viewport");
            var firstBox = LayoutTestHelpers.FindBoxFor(first, viewport);
            var firstState = h.LayoutEngine.ScrollContainer.Get(firstBox);
            Assert.That(firstState, Is.Not.Null);
            firstState.ScrollTo(0, 80);

            h.LayoutEngine.Invalidate(viewport);
            h.Tracker.MarkDirty(viewport, InvalidationKind.Layout);
            var second = h.Layout();
            var secondBox = LayoutTestHelpers.FindBoxFor(second, viewport);
            var secondState = h.LayoutEngine.ScrollContainer.Get(secondBox);

            Assert.That(secondBox, Is.Not.SameAs(firstBox));
            Assert.That(secondState, Is.Not.Null);
            Assert.That(secondState.ScrollY, Is.EqualTo(80).Within(0.001));
        }

        [Test]
        public void Structure_dirty_forces_full_pass() {
            var h = Build("<div><span>x</span></div>");
            h.Layout();
            var div = LayoutTestHelpers.FindByTag(h.Doc, "div");
            h.Tracker.MarkDirty(div, InvalidationKind.Structure);
            var skipsBefore = h.LayoutEngine.SkipCount;
            h.Layout();
            Assert.That(h.LayoutEngine.SkipCount, Is.EqualTo(skipsBefore));
        }

        [Test]
        public void Style_only_dirty_skips_layout() {
            var h = Build("<div></div>", "div { color: red }");
            var first = h.Layout();
            var div = LayoutTestHelpers.FindByTag(h.Doc, "div");
            h.Tracker.MarkDirty(div, InvalidationKind.Style);
            var second = h.Layout();
            Assert.That(second, Is.SameAs(first));
            Assert.That(h.LayoutEngine.SkipCount, Is.EqualTo(1));
        }

        [Test]
        public void PseudoClassState_dirty_skips_layout() {
            var h = Build("<button>btn</button>", "button { color: black }");
            var first = h.Layout();
            var btn = LayoutTestHelpers.FindByTag(h.Doc, "button");
            h.Tracker.MarkDirty(btn, InvalidationKind.PseudoClassState);
            var second = h.Layout();
            Assert.That(second, Is.SameAs(first));
            Assert.That(h.LayoutEngine.SkipCount, Is.EqualTo(1));
        }

        [Test]
        public void Skipped_box_marked_as_cached_from_last_frame() {
            var h = Build("<div></div>");
            var first = h.Layout();
            Assert.That(first.IsCachedFromLastFrame, Is.False, "first pass is fresh");
            var second = h.Layout();
            Assert.That(second.IsCachedFromLastFrame, Is.True, "second pass is gate-skipped");
        }

        [Test]
        public void Full_layout_resets_cached_flag() {
            var h = Build("<div></div>");
            h.Layout();
            h.Layout(); // skip → flag set
            Assert.That(h.LayoutEngine.LastRoot.IsCachedFromLastFrame, Is.True);
            // Force a Layout-dirty frame
            var div = LayoutTestHelpers.FindByTag(h.Doc, "div");
            h.Tracker.MarkDirty(div, InvalidationKind.Layout);
            var box = h.Layout();
            Assert.That(box.IsCachedFromLastFrame, Is.False, "fresh pass must clear the cached-flag");
        }

        [Test]
        public void Viewport_change_forces_full_pass_even_with_clean_tracker() {
            var h = Build("<div></div>");
            h.Layout();
            var skipsBefore = h.LayoutEngine.SkipCount;
            h.Ctx.ViewportWidthPx = 1024;
            h.Layout();
            Assert.That(h.LayoutEngine.SkipCount, Is.EqualTo(skipsBefore),
                "viewport change must not be skipped — sizing depends on the viewport");
        }

        [Test]
        public void Null_tracker_overload_always_runs_full_pass() {
            // The 3-arg legacy overload disables incremental skip; useful for
            // bench/test code that doesn't track dirty bits.
            var h = Build("<div></div>");
            var first = h.LayoutEngine.Layout(h.Doc, _ => h.Cascade.Compute(_), h.Ctx);
            var second = h.LayoutEngine.Layout(h.Doc, _ => h.Cascade.Compute(_), h.Ctx);
            Assert.That(h.LayoutEngine.SkipCount, Is.EqualTo(0),
                "null-tracker overload should never report a skip");
            Assert.That(second, Is.Not.Null);
        }

        [Test]
        public void Style_change_without_layout_property_skips() {
            // Simulate a cascade that marks Style only (e.g., color flipped).
            // The gate must still skip layout because Layout flag is absent.
            var h = Build("<div>x</div>", "div { color: black }");
            var first = h.Layout();
            var div = LayoutTestHelpers.FindByTag(h.Doc, "div");
            h.Tracker.MarkDirty(div, InvalidationKind.Style | InvalidationKind.Paint);
            var second = h.Layout();
            Assert.That(second, Is.SameAs(first));
        }

        [Test]
        public void Layout_property_change_must_be_tagged_layout_for_correctness() {
            // Caller contract: when a layout-affecting property changes (width
            // here), it must be marked as Layout dirty for the gate to fire a
            // re-layout. This test documents the contract: marking only Style
            // would be a bug in the caller, but the gate would skip — which is
            // *correct* given the caller's claim.
            var h = Build("<div>x</div>", "div { width: 50px }");
            var first = h.Layout();
            var div = LayoutTestHelpers.FindByTag(h.Doc, "div");
            // Caller correctly marks Layout — gate runs full pass.
            h.Tracker.MarkDirty(div, InvalidationKind.Layout);
            var skipsBefore = h.LayoutEngine.SkipCount;
            h.Layout();
            Assert.That(h.LayoutEngine.SkipCount, Is.EqualTo(skipsBefore));
        }

        [Test]
        public void Many_skipped_frames_accumulate_skip_count() {
            var h = Build("<div></div>");
            h.Layout(); // first
            for (int i = 0; i < 100; i++) h.Layout();
            Assert.That(h.LayoutEngine.SkipCount, Is.EqualTo(100));
            Assert.That(h.LayoutEngine.LastRoot, Is.Not.Null);
        }

        [Test]
        public void InvalidateAll_clears_lastRoot_and_forces_pass() {
            var h = Build("<div></div>");
            h.Layout();
            Assert.That(h.LayoutEngine.LastRoot, Is.Not.Null);
            h.LayoutEngine.InvalidateAll();
            Assert.That(h.LayoutEngine.LastRoot, Is.Null);
            // Next Layout call with empty tracker must NOT skip — lastRoot is null.
            var skipsBefore = h.LayoutEngine.SkipCount;
            h.Layout();
            Assert.That(h.LayoutEngine.SkipCount, Is.EqualTo(skipsBefore),
                "InvalidateAll forces a full pass on the next call");
            Assert.That(h.LayoutEngine.LastRoot, Is.Not.Null);
        }
    }
}
