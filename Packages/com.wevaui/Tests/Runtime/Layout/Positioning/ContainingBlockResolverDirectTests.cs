using NUnit.Framework;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Positioning;
using Weva.Layout.Text;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Positioning.PositioningTestHelpers;

namespace Weva.Tests.Layout.Positioning {
    // Tracker item TG7 — direct unit coverage for
    // `ContainingBlockResolver.ResolveAbsolute` / `ResolveFixed`.
    //
    // The audit flagged that every abspos/fixed offset resolution funnels
    // through this resolver, so a missed positioned-ancestor case would
    // silently render popups, dialogs, and tooltips at the wrong
    // coordinates. Existing tests exercise it indirectly via full layout
    // fixtures; these tests poke the resolver directly so a regression in
    // the ancestor-walk logic surfaces against a one-line failure (not a
    // distant layout golden).
    //
    // Each test builds the smallest two- or three-level box tree it needs,
    // invokes the resolver, and asserts on the returned ContainingBlock
    // record (Box, IsViewport, plus padding-edge rect where it matters).
    public class ContainingBlockResolverDirectTests {
        // -------------------------------------------------------------------
        // position: absolute — ancestor selection
        // -------------------------------------------------------------------

        [Test]
        public void Absolute_with_no_positioned_ancestor_resolves_to_viewport() {
            // The resolver should walk all the way up, find no establishing
            // ancestor, and report the initial containing block (viewport).
            const string css = @"
                .outer { width: 400px; height: 400px; }
                .mid   { width: 200px; height: 200px; }
                .abs   { position: absolute; width: 10px; height: 10px; }
            ";
            var (root, _, ctx) = Build(
                "<div class=\"outer\"><div class=\"mid\"><div class=\"abs\"></div></div></div>",
                css, viewportWidth: 800, viewportHeight: 600);
            var abs = FirstByClass(root, "abs");
            var cb = ContainingBlockResolver.ResolveAbsolute(abs, ctx);
            Assert.That(cb.IsViewport, Is.True);
            Assert.That(cb.Box, Is.Null);
            Assert.That(cb.Width, Is.EqualTo(800).Within(0.001));
            Assert.That(cb.Height, Is.EqualTo(600).Within(0.001));
        }

        [Test]
        public void Absolute_resolves_to_position_relative_ancestor_padding_edge() {
            // Canonical case from the CSS Positioned Layout spec — the
            // nearest position:relative ancestor's padding box is the CB.
            // We add a non-zero border to assert the padding-edge (not
            // border-edge) inset is what the resolver reports.
            const string css = @"
                .rel { position: relative; width: 200px; height: 200px; border: 5px solid #000;
                       margin-top: 10px; margin-left: 20px; }
                .abs { position: absolute; width: 10px; height: 10px; }
            ";
            var (root, _, ctx) = Build(
                "<div class=\"rel\"><div class=\"abs\"></div></div>",
                css, viewportWidth: 800, viewportHeight: 600);
            var rel = FirstByClass(root, "rel");
            var abs = FirstByClass(root, "abs");
            var cb = ContainingBlockResolver.ResolveAbsolute(abs, ctx);
            Assert.That(cb.IsViewport, Is.False);
            Assert.That(cb.Box, Is.SameAs(rel));
            // .rel is at absolute (20, 10) — padding edge sits 5px inside
            // the border, so the CB origin is (25, 15). With the default
            // `box-sizing: content-box`, `width: 200px` means a 200px
            // content area and a 210px border-box; padW = 210 - 5 - 5 =
            // 200, confirming the resolver subtracts borders (not paddings)
            // off the border-box rect.
            Assert.That(cb.X, Is.EqualTo(25).Within(0.001));
            Assert.That(cb.Y, Is.EqualTo(15).Within(0.001));
            Assert.That(cb.Width, Is.EqualTo(200).Within(0.001));
            Assert.That(cb.Height, Is.EqualTo(200).Within(0.001));
        }

        [Test]
        public void Absolute_resolves_to_position_absolute_ancestor() {
            // A position:absolute box is itself positioned, so it
            // establishes a CB for its own abspos descendants — the
            // resolver must not require the trigger to be specifically
            // `relative`.
            const string css = @"
                .rel { position: relative; width: 600px; height: 600px; }
                .abs1 { position: absolute; top: 50px; left: 60px; width: 300px; height: 300px; }
                .abs2 { position: absolute; width: 10px; height: 10px; }
            ";
            var (root, _, ctx) = Build(
                "<div class=\"rel\"><div class=\"abs1\"><div class=\"abs2\"></div></div></div>",
                css, viewportWidth: 800, viewportHeight: 600);
            var abs1 = FirstByClass(root, "abs1");
            var abs2 = FirstByClass(root, "abs2");
            var cb = ContainingBlockResolver.ResolveAbsolute(abs2, ctx);
            Assert.That(cb.Box, Is.SameAs(abs1),
                "nearest positioned ancestor (position:absolute) must be the CB for an inner abspos");
            Assert.That(cb.IsViewport, Is.False);
        }

        [Test]
        public void Absolute_resolves_to_position_fixed_ancestor() {
            // Same as above but with a fixed ancestor — fixed counts as
            // positioned for CB-establishment of abspos descendants.
            const string css = @"
                .fix { position: fixed; top: 0; left: 0; width: 300px; height: 300px; }
                .abs { position: absolute; width: 10px; height: 10px; }
            ";
            var (root, _, ctx) = Build(
                "<div class=\"fix\"><div class=\"abs\"></div></div>",
                css, viewportWidth: 800, viewportHeight: 600);
            var fix = FirstByClass(root, "fix");
            var abs = FirstByClass(root, "abs");
            var cb = ContainingBlockResolver.ResolveAbsolute(abs, ctx);
            Assert.That(cb.Box, Is.SameAs(fix));
            Assert.That(cb.IsViewport, Is.False);
        }

        [Test]
        public void Absolute_resolves_to_transform_ancestor_even_when_static() {
            // CSS Transforms L1 §6.1: a transformed ancestor establishes
            // the CB for abspos descendants, even when its `position` is
            // the default `static`. `translateX(0)` is intentionally a
            // no-op transform — the spec keys on the *property*, not its
            // visual effect.
            const string css = @"
                .xf  { width: 200px; height: 200px; transform: translateX(0); }
                .abs { position: absolute; width: 10px; height: 10px; }
            ";
            var (root, _, ctx) = Build(
                "<div class=\"xf\"><div class=\"abs\"></div></div>",
                css, viewportWidth: 800, viewportHeight: 600);
            var xf = FirstByClass(root, "xf");
            var abs = FirstByClass(root, "abs");
            var cb = ContainingBlockResolver.ResolveAbsolute(abs, ctx);
            Assert.That(cb.Box, Is.SameAs(xf));
            Assert.That(cb.IsViewport, Is.False);
        }

        [Test]
        public void Absolute_resolves_to_filter_ancestor_even_when_static() {
            // Same CSS Transforms L1 §6.1 rule for `filter`. `blur(0)` is
            // a no-op visually but the property's mere presence (and
            // non-`none` computed value) is the trigger.
            const string css = @"
                .blurred { width: 200px; height: 200px; filter: blur(0); }
                .abs     { position: absolute; width: 10px; height: 10px; }
            ";
            var (root, _, ctx) = Build(
                "<div class=\"blurred\"><div class=\"abs\"></div></div>",
                css, viewportWidth: 800, viewportHeight: 600);
            var blurred = FirstByClass(root, "blurred");
            var abs = FirstByClass(root, "abs");
            var cb = ContainingBlockResolver.ResolveAbsolute(abs, ctx);
            Assert.That(cb.Box, Is.SameAs(blurred));
            Assert.That(cb.IsViewport, Is.False);
        }

        [Test]
        public void Absolute_resolves_to_will_change_transform_ancestor() {
            // `will-change: transform` opts the box into the same
            // CB-establishment rule a transformed ancestor would have,
            // letting authors pre-arm compositor-friendly behaviour
            // without a no-op transform.
            const string css = @"
                .promoted { width: 200px; height: 200px; will-change: transform; }
                .abs      { position: absolute; width: 10px; height: 10px; }
            ";
            var (root, _, ctx) = Build(
                "<div class=\"promoted\"><div class=\"abs\"></div></div>",
                css, viewportWidth: 800, viewportHeight: 600);
            var promoted = FirstByClass(root, "promoted");
            var abs = FirstByClass(root, "abs");
            var cb = ContainingBlockResolver.ResolveAbsolute(abs, ctx);
            Assert.That(cb.Box, Is.SameAs(promoted));
            Assert.That(cb.IsViewport, Is.False);
        }

        [Test]
        public void Absolute_resolves_to_contain_paint_ancestor() {
            // CSS Containment Module Level 2: `contain: paint` (and
            // layout/strict/content) establishes a CB. Distinct from the
            // transform/filter trigger and worth its own test so a
            // narrowed `HasContainingBlockEstablishingProperty` check
            // doesn't silently drop containment support.
            const string css = @"
                .contained { width: 200px; height: 200px; contain: paint; }
                .abs       { position: absolute; width: 10px; height: 10px; }
            ";
            var (root, _, ctx) = Build(
                "<div class=\"contained\"><div class=\"abs\"></div></div>",
                css, viewportWidth: 800, viewportHeight: 600);
            var contained = FirstByClass(root, "contained");
            var abs = FirstByClass(root, "abs");
            var cb = ContainingBlockResolver.ResolveAbsolute(abs, ctx);
            Assert.That(cb.Box, Is.SameAs(contained));
            Assert.That(cb.IsViewport, Is.False);
        }

        [Test]
        public void Absolute_resolves_to_perspective_ancestor_per_transforms_l2() {
            // CSS Transforms L2: a non-`none` `perspective` value also
            // establishes the CB. Mirrors transform/filter — the property
            // changes how viewport coordinates map to local, so the local
            // box must be the reference frame for fixed descendants too.
            const string css = @"
                .scene { width: 200px; height: 200px; perspective: 1000px; }
                .abs   { position: absolute; width: 10px; height: 10px; }
            ";
            var (root, _, ctx) = Build(
                "<div class=\"scene\"><div class=\"abs\"></div></div>",
                css, viewportWidth: 800, viewportHeight: 600);
            var scene = FirstByClass(root, "scene");
            var abs = FirstByClass(root, "abs");
            var cb = ContainingBlockResolver.ResolveAbsolute(abs, ctx);
            Assert.That(cb.Box, Is.SameAs(scene));
            Assert.That(cb.IsViewport, Is.False);
        }

        // -------------------------------------------------------------------
        // position: fixed — viewport vs. transform-escape
        // -------------------------------------------------------------------

        [Test]
        public void Fixed_with_no_transform_ancestor_resolves_to_viewport() {
            // Without any CB-establishing property in the chain, fixed
            // pins to the viewport — even a position:relative parent does
            // NOT capture fixed descendants.
            const string css = @"
                .rel { position: relative; width: 200px; height: 200px; }
                .fix { position: fixed; width: 10px; height: 10px; }
            ";
            var (root, _, ctx) = Build(
                "<div class=\"rel\"><div class=\"fix\"></div></div>",
                css, viewportWidth: 800, viewportHeight: 600);
            var fix = FirstByClass(root, "fix");
            var cb = ContainingBlockResolver.ResolveFixed(fix, ctx);
            Assert.That(cb.IsViewport, Is.True);
            Assert.That(cb.Box, Is.Null);
            Assert.That(cb.Width, Is.EqualTo(800).Within(0.001));
            Assert.That(cb.Height, Is.EqualTo(600).Within(0.001));
        }

        [Test]
        public void Fixed_is_captured_by_transform_ancestor() {
            // CSS Transforms L1 §6.1: a transformed ancestor escapes
            // position:fixed from the viewport — the transform's
            // descendants live in the transformed frame, so the fixed
            // box's reference origin must be the transformed ancestor.
            const string css = @"
                .xf  { width: 200px; height: 200px; transform: translateX(0); }
                .fix { position: fixed; width: 10px; height: 10px; }
            ";
            var (root, _, ctx) = Build(
                "<div class=\"xf\"><div class=\"fix\"></div></div>",
                css, viewportWidth: 800, viewportHeight: 600);
            var xf = FirstByClass(root, "xf");
            var fix = FirstByClass(root, "fix");
            var cb = ContainingBlockResolver.ResolveFixed(fix, ctx);
            Assert.That(cb.IsViewport, Is.False);
            Assert.That(cb.Box, Is.SameAs(xf),
                "transform ancestor must capture position:fixed per CSS Transforms L1 §6.1");
        }

        // -------------------------------------------------------------------
        // Edge cases
        // -------------------------------------------------------------------

        [Test]
        public void Detached_box_with_no_parent_resolves_to_viewport_safely() {
            // A freshly constructed BlockBox with no Document, no styles
            // and no Parent must not crash the resolver. The ancestor
            // walk terminates immediately and returns the viewport.
            var orphan = new BlockBox();
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 800,
                ViewportHeightPx = 600,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96
            };
            var cbAbs = ContainingBlockResolver.ResolveAbsolute(orphan, ctx);
            Assert.That(cbAbs.IsViewport, Is.True);
            Assert.That(cbAbs.Box, Is.Null);
            Assert.That(cbAbs.Width, Is.EqualTo(800).Within(0.001));
            Assert.That(cbAbs.Height, Is.EqualTo(600).Within(0.001));

            var cbFix = ContainingBlockResolver.ResolveFixed(orphan, ctx);
            Assert.That(cbFix.IsViewport, Is.True);
            Assert.That(cbFix.Box, Is.Null);
        }

        [Test]
        public void Absolute_picks_nearest_positioned_ancestor_not_farthest() {
            // Stack two positioned ancestors and confirm the resolver
            // halts on the nearer one rather than walking all the way to
            // the outer container. A missed "break" in the loop would
            // surface here as the outer being returned.
            const string css = @"
                .outer { position: relative; width: 600px; height: 600px;
                         margin-top: 5px; margin-left: 7px; }
                .inner { position: relative; width: 300px; height: 300px;
                         margin-top: 11px; margin-left: 13px; }
                .abs   { position: absolute; width: 10px; height: 10px; }
            ";
            var (root, _, ctx) = Build(
                "<div class=\"outer\"><div class=\"inner\"><div class=\"abs\"></div></div></div>",
                css, viewportWidth: 1000, viewportHeight: 800);
            var outer = FirstByClass(root, "outer");
            var inner = FirstByClass(root, "inner");
            var abs = FirstByClass(root, "abs");
            var cb = ContainingBlockResolver.ResolveAbsolute(abs, ctx);
            Assert.That(cb.Box, Is.SameAs(inner),
                "resolver must stop at the nearest positioned ancestor");
            Assert.That(cb.Box, Is.Not.SameAs(outer));
            Assert.That(cb.IsViewport, Is.False);
        }

        [Test]
        public void Fixed_picks_nearest_capturing_ancestor_not_farthest() {
            // Same nearest-wins guarantee for ResolveFixed. Outer has a
            // transform too; the inner transform must win.
            const string css = @"
                .outer { width: 600px; height: 600px; transform: translateX(0); }
                .inner { width: 300px; height: 300px; transform: translateY(0); }
                .fix   { position: fixed; width: 10px; height: 10px; }
            ";
            var (root, _, ctx) = Build(
                "<div class=\"outer\"><div class=\"inner\"><div class=\"fix\"></div></div></div>",
                css, viewportWidth: 1000, viewportHeight: 800);
            var outer = FirstByClass(root, "outer");
            var inner = FirstByClass(root, "inner");
            var fix = FirstByClass(root, "fix");
            var cb = ContainingBlockResolver.ResolveFixed(fix, ctx);
            Assert.That(cb.Box, Is.SameAs(inner));
            Assert.That(cb.Box, Is.Not.SameAs(outer));
            Assert.That(cb.IsViewport, Is.False);
        }
    }
}
