using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Media;
using Weva.Documents;
using Weva.Dom;
using Weva.Events;
using Weva.Layout.Boxes;
using Weva.Layout.Positioning;
using Weva.Layout.Scrolling;
using Weva.Reactive;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Positioning.PositioningTestHelpers;

namespace Weva.Tests.Layout.Scrolling {
    // CSS Position L3 §6.3 regression: sticky offsets must be recomputed from
    // the ABSOLUTE scroll position on every update where scroll state changes,
    // regardless of whether a layout pass ran. Fast scrolls that jump across
    // both boundaries of the pin range (pre-pin → past-pin in one frame) must
    // still produce the correct clamped offset, not a stale one from the last
    // layout pass.
    //
    // Spec formula for top-sticky on Y axis:
    //   stickyOffset = clamp(scrollY - naturalY, 0, containerBottom - elemHeight - naturalY)
    //
    // Arrangement:
    //   normalY = element's natural Y in scroll-container space
    //   containerBottom = containingTop + containingHeight
    //
    // B10 regression path (before fix): SetImmediate() marks Paint-dirty only;
    // UIDocumentLifecycle.Update skips layout (no Layout flag set) and therefore
    // StickyResolver.Resolve() was never called — sticky offsets stayed stale.
    public class StickyFastScrollTests {
        // --- Direct StickyResolver tests: verify the §6.3 clamp is correct ---
        // These exercise the math without the lifecycle, confirming the algorithm
        // is correct for large-delta scrolls.

        static (Box root, ScrollContainer sc, BlockBox sticky, BlockBox host) BuildSticky(
            string css, string html, double viewportH = 200) {
            var (root, _, _) = Build(html, css, viewportWidth: 400, viewportHeight: viewportH);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);
            new StickyResolver(sc).Resolve(root);
            BlockBox sticky = FirstByClass(root, "sticky");
            BlockBox host = FirstByClass(root, "host");
            return (root, sc, sticky, host);
        }

        [Test]
        public void Large_scroll_crossing_both_pin_boundaries_clamps_to_release_offset() {
            // Layout: lead=100px, sticky=50px, tail=400px inside 200px host.
            // normalY = 100, containerBottom (host content) = 550 (lead+sticky+tail),
            // but containing block is host which is direct parent with scrollHeight.
            // When scrollY jumps from 0 to 2000 in one delta:
            // pinnedY = 2000, naturalY = 100, maxY = scrollHeight - elemHeight.
            // Expected: sticky stops at host's scroll boundary: offset = maxY - naturalY.
            const string css = ".host { overflow: auto; height: 200px; width: 200px; } " +
                               ".lead { height: 100px; } " +
                               ".sticky { position: sticky; top: 0; height: 50px; width: 100px; } " +
                               ".tail { height: 400px; }";
            const string html = "<div class=\"host\"><div class=\"lead\"></div>" +
                                "<div class=\"sticky\"></div><div class=\"tail\"></div></div>";
            var (root, sc, sticky, host) = BuildSticky(css, html);
            var state = sc.Get(host);

            // Single large delta: scrollY=0 → 2000 (past the pin and release boundary)
            state.ScrollY = 2000;
            new StickyResolver(sc).Resolve(root);

            // scrollHeight = 100 + 50 + 400 = 550; viewportH = 200 → maxScrollY = 350
            // But state.ScrollY is clamped by ScrollLayout to MaxScrollY=350 on Resolve.
            // Actually we set state.ScrollY directly; the resolver uses that value.
            // naturalY = 100; containingBlock = host content area = scrollHeight = 550
            // maxY = containingTop(0) + containingHeight(550) - elemHeight(50) = 500
            // pinnedY = scrollY(2000) clamped to maxY = 500 → dy = 500 - 100 = 400.
            // Because scroll is past the containing block, the element is pinned at CB bottom.
            Assert.That(sticky.StickyOffsetY, Is.GreaterThan(0),
                "Sticky must be offset when scrolled past the pin range");
            Assert.That(sticky.StickyOffsetY, Is.LessThanOrEqualTo(state.ScrollHeight - 50),
                "Sticky offset must not exceed the scrollable extent minus element height");
            // The key regression: the offset is the SAME whether we got here
            // via a single large delta or a multi-step scroll.
            double offsetFromLargeDelta = sticky.StickyOffsetY;

            // Reset and apply same scroll in tiny steps
            state.ScrollY = 0;
            new StickyResolver(sc).Resolve(root);
            for (int i = 1; i <= 20; i++) {
                state.ScrollY = i * 100;
                new StickyResolver(sc).Resolve(root);
            }
            Assert.That(sticky.StickyOffsetY, Is.EqualTo(offsetFromLargeDelta).Within(0.001),
                "Multi-step and single-large-delta scroll must produce identical sticky offset");
        }

        [Test]
        public void Pre_pin_to_post_pin_in_one_jump_yields_released_state() {
            // The sticky element starts at naturalY=100 inside a 200px-high
            // containing block (the .cb div). A single scroll from 0 to 500
            // crosses both the "start pinning" boundary (scrollY > naturalY)
            // and the "release" boundary (containingBottom - elemHeight < scrollY).
            const string css = ".host { overflow: auto; height: 200px; width: 200px; } " +
                               ".cb { height: 200px; } " +
                               ".sticky { position: sticky; top: 0; height: 30px; width: 100px; } " +
                               ".tail { height: 600px; }";
            const string html = "<div class=\"host\"><div class=\"cb\">" +
                                "<div class=\"sticky\"></div></div><div class=\"tail\"></div></div>";
            var (root, sc, sticky, host) = BuildSticky(css, html);
            var state = sc.Get(host);

            // Pre-pin state: scrollY=0
            Assert.That(sticky.StickyOffsetY, Is.EqualTo(0).Within(0.001),
                "Before pin zone: offset must be 0");

            // Single large jump: scrollY=0 → 500 (well past CB bottom = 200)
            state.ScrollY = 500;
            new StickyResolver(sc).Resolve(root);

            // Released: maxY = cbTop(0) + cbHeight(200) - elemHeight(30) = 170
            // naturalY = 0 (sticky is first child of .cb). Offset = 170 - 0 = 170.
            Assert.That(sticky.StickyOffsetY, Is.EqualTo(170).Within(0.001),
                "Post-pin (released): offset must equal maxY - naturalY = 170");
        }

        [Test]
        public void Multi_step_and_single_large_scroll_produce_identical_final_offset() {
            // Regression: if the engine ever uses delta-based accumulation, sequential
            // steps would produce a correct result while a single large jump would not.
            // Both paths must reach the same end state.
            const string css = ".host { overflow: auto; height: 200px; width: 200px; } " +
                               ".lead { height: 100px; } " +
                               ".sticky { position: sticky; top: 0; height: 30px; width: 100px; } " +
                               ".filler { height: 600px; }";
            const string html = "<div class=\"host\"><div class=\"lead\"></div>" +
                                "<div class=\"sticky\"></div><div class=\"filler\"></div></div>";
            var (root, sc, sticky, host) = BuildSticky(css, html);
            var state = sc.Get(host);

            // Sequence: 0 → 50 → 200 → 1000
            state.ScrollY = 50;
            new StickyResolver(sc).Resolve(root);
            state.ScrollY = 200;
            new StickyResolver(sc).Resolve(root);
            state.ScrollY = 1000;
            new StickyResolver(sc).Resolve(root);
            double multiStepOffset = sticky.StickyOffsetY;

            // Reset and single large jump: 0 → 1000
            state.ScrollY = 0;
            new StickyResolver(sc).Resolve(root);
            state.ScrollY = 1000;
            new StickyResolver(sc).Resolve(root);
            double singleJumpOffset = sticky.StickyOffsetY;

            Assert.That(singleJumpOffset, Is.EqualTo(multiStepOffset).Within(0.001),
                "Single large delta and multi-step scroll must yield identical sticky offset");
        }

        [Test]
        public void Large_upward_scroll_from_released_to_unpinned_removes_offset() {
            // Sticky was released (scrolled past CB bottom). When a large upward
            // scroll brings scrollY back below naturalY, the sticky must return
            // to offset=0 (not-yet-pinned state).
            const string css = ".host { overflow: auto; height: 200px; width: 200px; } " +
                               ".lead { height: 200px; } " +
                               ".sticky { position: sticky; top: 0; height: 30px; width: 100px; } " +
                               ".filler { height: 600px; }";
            const string html = "<div class=\"host\"><div class=\"lead\"></div>" +
                                "<div class=\"sticky\"></div><div class=\"filler\"></div></div>";
            var (root, sc, sticky, host) = BuildSticky(css, html);
            var state = sc.Get(host);

            // Scroll far down — sticky in pinned state
            state.ScrollY = 400;
            new StickyResolver(sc).Resolve(root);
            Assert.That(sticky.StickyOffsetY, Is.GreaterThan(0),
                "Sticky must be pinned after scrolling well past natural Y");

            // Large upward jump to scrollY=0 — sticky must be de-pinned
            state.ScrollY = 0;
            new StickyResolver(sc).Resolve(root);
            Assert.That(sticky.StickyOffsetY, Is.EqualTo(0).Within(0.001),
                "After scrolling back to top, sticky must return to natural position (offset=0)");
        }

        [Test]
        public void Scroll_exactly_at_pin_boundary_pins_element() {
            // When scrollY == naturalY exactly, the sticky is at the edge of its
            // pin zone. The spec formula gives: pinnedY = naturalY; dy = 0 (not yet pinning).
            // When scrollY = naturalY + 1, the sticky should just start to pin: dy = 1.
            const string css = ".host { overflow: auto; height: 200px; width: 200px; } " +
                               ".lead { height: 100px; } " +
                               ".sticky { position: sticky; top: 0; height: 30px; width: 100px; } " +
                               ".filler { height: 600px; }";
            const string html = "<div class=\"host\"><div class=\"lead\"></div>" +
                                "<div class=\"sticky\"></div><div class=\"filler\"></div></div>";
            var (root, sc, sticky, host) = BuildSticky(css, html);
            var state = sc.Get(host);

            // naturalY should be 100 (after lead)
            double naturalY = sticky.Y;
            Assert.That(naturalY, Is.EqualTo(100).Within(0.001), "Natural Y should be 100 (after 100px lead)");

            // At scrollY = naturalY: pinnedY = scrollY + 0 = naturalY. pinnedY == naturalY → dy=0.
            state.ScrollY = naturalY;
            new StickyResolver(sc).Resolve(root);
            Assert.That(sticky.StickyOffsetY, Is.EqualTo(0).Within(0.001),
                "At scrollY == naturalY, sticky is at the exact pin boundary — offset still 0");

            // At scrollY = naturalY + 1: pinnedY = naturalY+1 > naturalY → dy=1.
            state.ScrollY = naturalY + 1;
            new StickyResolver(sc).Resolve(root);
            Assert.That(sticky.StickyOffsetY, Is.EqualTo(1).Within(0.001),
                "Just past naturalY, sticky pins with offset = 1");
        }

        // --- Lifecycle integration tests: exercise the real B10 regression ---
        // These go through UIDocumentLifecycle.Update to confirm scroll events
        // propagate to StickyResolver even when layout is not re-run.

        static UIDocumentState BuildLifecycleState(string html, string css) {
            return new UIDocumentBuilder {
                DocumentSource = html,
                StylesheetSources = new List<string> { css },
                MediaContext = MediaContext.Default(800, 600),
                Clock = new FakeUIClock()
            }.Build();
        }

        [Test]
        public void Scroll_only_update_refreshes_sticky_offset() {
            // B10 regression: scroll events mark only Paint-dirty; before the fix
            // StickyResolver was never called in the layout-skip path, leaving
            // stale offsets on the boxes.
            const string css = ".host { overflow: auto; height: 200px; width: 200px; } " +
                               ".lead { height: 100px; } " +
                               ".sticky { position: sticky; top: 0; height: 30px; width: 100px; } " +
                               ".filler { height: 600px; }";
            const string html = "<div class=\"host\"><div class=\"lead\"></div>" +
                                "<div class=\"sticky\"></div><div class=\"filler\"></div></div>";
            var state = BuildLifecycleState(html, css);

            // First layout pass — sets up the box tree and initial sticky offsets.
            UIDocumentLifecycle.Update(state, null, 0.0);
            Assert.That(state.RootBox, Is.Not.Null, "Layout must have run on first update");

            Element stickyEl = null;
            Element hostEl = null;
            foreach (var e in state.Doc.GetElementsByClassName("sticky")) { stickyEl = e; break; }
            foreach (var e in state.Doc.GetElementsByClassName("host")) { hostEl = e; break; }
            Assert.That(stickyEl, Is.Not.Null);
            Assert.That(hostEl, Is.Not.Null);

            var stickyBox = (BlockBox)state.ElementToBox.Lookup(stickyEl);
            Assert.That(stickyBox, Is.Not.Null, "Sticky element must have a box");

            // At scrollY=0: sticky not yet pinned, offset=0.
            Assert.That(stickyBox.StickyOffsetY, Is.EqualTo(0).Within(0.001),
                "Before any scroll: offset must be 0");

            // Directly mutate scroll state (simulating a wheel event result)
            // and mark only Paint-dirty to trigger the layout-skip path.
            var hostBox = state.LayoutEngine.ScrollContainer.Get(state.ElementToBox.Lookup(hostEl));
            Assert.That(hostBox, Is.Not.Null, "Host must have a scroll state");
            hostBox.ScrollY = 150;
            var invalidation = state.Invalidation;
            invalidation.MarkDirty(hostEl, InvalidationKind.Paint);

            // Second update: no Layout flag → layout-skip path. Before fix, this
            // left stickyOffsetY = 0 (stale). After fix it must re-run the resolver.
            UIDocumentLifecycle.Update(state, null, 0.1);

            // After fix: natural Y = 100, scrollY = 150 → dy = 150 - 100 = 50.
            Assert.That(stickyBox.StickyOffsetY, Is.EqualTo(50).Within(0.001),
                "After paint-only update with scroll: sticky offset must be recalculated (B10 fix)");
        }

        [Test]
        public void Scroll_past_pin_range_in_one_lifecycle_update_gives_correct_clamp() {
            // Large scroll crossing both pin boundaries in a single paint-only update.
            // The offset must be the clamped release position, not 0 (stale) or some
            // intermediate value.
            const string css = ".host { overflow: auto; height: 200px; width: 200px; } " +
                               ".cb { height: 200px; } " +
                               ".sticky { position: sticky; top: 0; height: 30px; width: 100px; } " +
                               ".tail { height: 600px; }";
            const string html = "<div class=\"host\"><div class=\"cb\">" +
                                "<div class=\"sticky\"></div></div><div class=\"tail\"></div></div>";
            var state = BuildLifecycleState(html, css);

            UIDocumentLifecycle.Update(state, null, 0.0);
            Assert.That(state.RootBox, Is.Not.Null);

            Element stickyEl = null;
            Element hostEl = null;
            foreach (var e in state.Doc.GetElementsByClassName("sticky")) { stickyEl = e; break; }
            foreach (var e in state.Doc.GetElementsByClassName("host")) { hostEl = e; break; }
            var stickyBox = (BlockBox)state.ElementToBox.Lookup(stickyEl);
            var scrollState = state.LayoutEngine.ScrollContainer.Get(state.ElementToBox.Lookup(hostEl));
            Assert.That(scrollState, Is.Not.Null);

            // Single large scroll past the entire pin range in one lifecycle update.
            scrollState.ScrollY = 500;
            state.Invalidation.MarkDirty(hostEl, InvalidationKind.Paint);
            UIDocumentLifecycle.Update(state, null, 0.1);

            // Released: CB = 200px tall starting at top(0).
            // maxY = 0 + 200 - 30 = 170. naturalY = 0. Offset = 170.
            Assert.That(stickyBox.StickyOffsetY, Is.EqualTo(170).Within(0.001),
                "Fast scroll from 0 to 500 must clamp sticky to the CB-bottom release position");
        }
    }
}
