// Gate matches the test assembly's URP versionDefine (see
// UIRenderGraphFilterBlurPlanTests for the full rationale: bare WEVA_URP is
// undefined in Weva.Tests.Runtime and silently drops the file).
#if WEVA_URP_BATCHER_TESTS
using System.Collections.Generic;
using NUnit.Framework;
using Weva.Paint;
using Weva.Paint.Filters;
using Weva.Rendering.URP;

namespace Weva.Tests.Rendering.URP {
    // Coverage for the shared-backdrop-blur eligibility logic
    // (UIRenderGraphPass.ComputeBackdropShareability). A backdrop scope may
    // crop from a single shared whole-screen scene blur ONLY if its backdrop
    // IS that pristine scene — i.e. it is disjoint from every EARLIER-painted
    // backdrop scope. A nested glass child (search inside topbar; a button
    // inside the player) overlaps its glass parent and must stay on the
    // per-panel path so it blurs the parent's already-glassed surface.
    public class BackdropShareabilityTests {
        static BackdropFilterEvent Ev(double x, double y, double w, double h) =>
            new BackdropFilterEvent(new Rect(x, y, w, h), BorderRadii.Zero,
                FilterChain.Empty, Transform2D.Identity, 0);

        static List<bool> Run(int vw, int vh, params BackdropFilterEvent[] events) {
            var shareable = new List<bool>();
            UIRenderGraphPass.ComputeBackdropShareability(events, vw, vh, shareable);
            return shareable;
        }

        [Test]
        public void Disjoint_scopes_are_all_shareable() {
            // Three non-overlapping panels (topbar / board / dock layout).
            var r = Run(1000, 800,
                Ev(0, 0, 1000, 60),       // topbar
                Ev(0, 100, 1000, 500),    // board
                Ev(400, 700, 200, 60));   // dock
            Assert.That(r, Is.EqualTo(new[] { true, true, true }));
        }

        [Test]
        public void Nested_child_over_earlier_parent_is_not_shareable() {
            // search (1) sits INSIDE topbar (0): topbar shares the scene,
            // search must NOT (its backdrop includes topbar's glass).
            var r = Run(1000, 800,
                Ev(0, 0, 1000, 60),       // topbar (earlier)
                Ev(800, 10, 150, 40));    // search inside topbar
            Assert.That(r[0], Is.True, "top-level topbar blurs the scene");
            Assert.That(r[1], Is.False, "nested search overlaps an earlier scope");
        }

        [Test]
        public void Overlap_with_a_LATER_scope_does_not_disqualify_the_earlier_one() {
            // player (0) is painted before its child buttons (1,2). The player
            // still blurs the pristine scene (children come after); only the
            // children are disqualified for overlapping the earlier player.
            var r = Run(1000, 800,
                Ev(100, 100, 400, 300),   // player (earlier, large)
                Ev(150, 350, 40, 40),     // button inside player
                Ev(220, 350, 40, 40));    // button inside player
            Assert.That(r[0], Is.True, "player blurs the scene; its children are painted later");
            Assert.That(r[1], Is.False);
            Assert.That(r[2], Is.False);
        }

        [Test]
        public void Mixed_layout_matches_glass_html_topology() {
            // topbar, search⊂topbar, player, btn⊂player, two disjoint tiles.
            var r = Run(1600, 900,
                Ev(50, 30, 1500, 56),     // 0 topbar        → share
                Ev(1300, 40, 200, 34),    // 1 search⊂topbar → no
                Ev(50, 120, 450, 500),    // 2 player        → share
                Ev(100, 560, 40, 40),     // 3 btn⊂player    → no
                Ev(520, 120, 200, 160),   // 4 tile          → share
                Ev(740, 120, 200, 160));  // 5 tile          → share
            Assert.That(r, Is.EqualTo(new[] { true, false, true, false, true, true }));
        }

        [Test]
        public void Touching_but_not_overlapping_edges_stay_shareable() {
            // Adjacent panels sharing an edge (x=200 right edge meets x=200
            // left edge) do NOT overlap — half-open interval test.
            var r = Run(1000, 800,
                Ev(0, 0, 200, 100),
                Ev(200, 0, 200, 100));
            Assert.That(r, Is.EqualTo(new[] { true, true }));
        }

        [Test]
        public void Overlap_helper_is_symmetric_and_strict() {
            Assert.That(UIRenderGraphPass.BackdropRectsOverlap((0, 0, 100, 100), (50, 50, 100, 100)), Is.True);
            Assert.That(UIRenderGraphPass.BackdropRectsOverlap((50, 50, 100, 100), (0, 0, 100, 100)), Is.True);
            // Edge-touching is not overlap.
            Assert.That(UIRenderGraphPass.BackdropRectsOverlap((0, 0, 100, 100), (100, 0, 100, 100)), Is.False);
            // Fully separate.
            Assert.That(UIRenderGraphPass.BackdropRectsOverlap((0, 0, 50, 50), (200, 200, 50, 50)), Is.False);
        }
    }
}
#endif
