using NUnit.Framework;
using Weva.Layout;
using Weva.Layout.AnchorPositioning;
using Weva.Layout.Boxes;
using Weva.Layout.Positioning;
using Weva.Layout.Text;

namespace Weva.Tests.Layout.Positioning {
    // Tracker item TG6 — direct unit coverage for
    // `AnchorResolver.TryResolveSide` / `TryResolveSize` / `EdgeAbsolute`.
    //
    // The CSS Anchor Positioning pipeline funnels every top/right/bottom/left
    // `anchor()` resolution and every width/height `anchor-size()` resolution
    // through this resolver. Existing AnchorIntegrationTests cover it via a
    // full layout pass; these tests poke the resolver directly with
    // hand-built Box trees so a regression in the registry lookup, the
    // ContainingBlock translation, or the edge-to-absolute mapping surfaces
    // as a one-line failure instead of a distant layout/golden diff.
    //
    // Each test builds the minimum Box graph it needs (BlockBox X/Y/W/H set
    // by hand — no cascade or layout pass), registers anchors on an
    // AnchorRegistry, and asserts on the resolver's output for a specific
    // side / size / edge.
    public class AnchorResolverDirectTests {
        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        static BlockBox NewBox(double x, double y, double w, double h) {
            var b = new BlockBox();
            b.X = x; b.Y = y; b.Width = w; b.Height = h;
            return b;
        }

        static LayoutContext NewCtx(double vw = 800, double vh = 600) {
            return new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = vw,
                ViewportHeightPx = vh,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96
            };
        }

        // Builds: viewport-rooted relative-parent containing an absolute
        // positioned box. The parent acts as the containing block for
        // `position: absolute` per the standard CSS resolver chain.
        // Returns (parent, positionedBox).
        static (BlockBox parent, BlockBox positioned) NewAbsScenario(
            double parentX, double parentY, double parentW, double parentH,
            double posW = 50, double posH = 20)
        {
            var parent = NewBox(parentX, parentY, parentW, parentH);
            parent.Position = PositionType.Relative;
            var pos = NewBox(0, 0, posW, posH);
            pos.Position = PositionType.Absolute;
            parent.AddChild(pos);
            return (parent, pos);
        }

        // -------------------------------------------------------------------
        // TryResolveSide — happy path
        // -------------------------------------------------------------------

        [Test]
        public void Top_anchor_bottom_resolves_to_anchor_bottom_edge_relative_to_cb() {
            // Anchor at absolute (0, 30), 100w x 40h → bottom edge at y=70.
            // Containing block is the relative parent at absolute (0, 0).
            // Expected `top` value = 70 - 0 = 70.
            var (parent, pos) = NewAbsScenario(0, 0, 200, 200);
            var anchor = NewBox(0, 30, 100, 40);
            parent.AddChild(anchor);
            var reg = new AnchorRegistry();
            reg.Register("--foo", anchor);

            var ctx = NewCtx();
            bool ok = AnchorResolver.TryResolveSide("top", "anchor(--foo bottom)", reg,
                                                     fallbackAnchorName: null, pos, ctx, out double px);
            Assert.That(ok, Is.True);
            Assert.That(px, Is.EqualTo(70).Within(0.001));
        }

        [Test]
        public void Left_anchor_left_resolves_to_anchor_left_edge_relative_to_cb() {
            // Parent at (50, 0). Anchor sits at parent-local (10, 0) →
            // absolute X = 60. Anchor's `left` edge = 60. CB X = 50.
            // Expected `left` value = 60 - 50 = 10.
            var (parent, pos) = NewAbsScenario(50, 0, 300, 300);
            var anchor = NewBox(10, 0, 80, 30);
            parent.AddChild(anchor);
            var reg = new AnchorRegistry();
            reg.Register("--foo", anchor);

            var ctx = NewCtx();
            bool ok = AnchorResolver.TryResolveSide("left", "anchor(--foo left)", reg,
                                                     null, pos, ctx, out double px);
            Assert.That(ok, Is.True);
            Assert.That(px, Is.EqualTo(10).Within(0.001));
        }

        [Test]
        public void Right_anchor_right_resolves_to_right_inset_from_cb_right_edge() {
            // CSS `right` is the distance from the CB's right edge to the
            // positioned box's right edge. CB is 200w at X=0; anchor right
            // edge = 10 + 80 = 90 → expected `right` value = 200 - 90 = 110.
            var (parent, pos) = NewAbsScenario(0, 0, 200, 200);
            var anchor = NewBox(10, 0, 80, 30);
            parent.AddChild(anchor);
            var reg = new AnchorRegistry();
            reg.Register("--foo", anchor);

            var ctx = NewCtx();
            bool ok = AnchorResolver.TryResolveSide("right", "anchor(--foo right)", reg,
                                                     null, pos, ctx, out double px);
            Assert.That(ok, Is.True);
            Assert.That(px, Is.EqualTo(110).Within(0.001));
        }

        // -------------------------------------------------------------------
        // TryResolveSide — fallback / missing anchor
        // -------------------------------------------------------------------

        [Test]
        public void Unknown_anchor_name_yields_zero_offset_with_true_result() {
            // Per the resolver's contract (and the in-source comment at
            // AnchorResolver.cs:36-39): when the named anchor cannot be
            // resolved we return `true` with pixels = the parsed in-function
            // offset (0 here). The spec's "invalid value at computed-value
            // time" maps to `auto`; v1 collapses that to 0 — documented.
            var (_, pos) = NewAbsScenario(0, 0, 200, 200);
            var reg = new AnchorRegistry();
            // Intentionally empty registry — `--missing` is not registered.

            var ctx = NewCtx();
            bool ok = AnchorResolver.TryResolveSide("top", "anchor(--missing bottom)", reg,
                                                     null, pos, ctx, out double px);
            Assert.That(ok, Is.True, "missing anchor is a soft-fail, not a hard parse failure");
            Assert.That(px, Is.EqualTo(0).Within(0.001));
        }

        [Test]
        public void Unknown_anchor_name_with_in_function_offset_returns_offset_only() {
            // anchor(--missing bottom + 12px) → name lookup misses, but the
            // +12px offset is preserved and returned standalone.
            var (_, pos) = NewAbsScenario(0, 0, 200, 200);
            var reg = new AnchorRegistry();

            var ctx = NewCtx();
            bool ok = AnchorResolver.TryResolveSide("top", "anchor(--missing bottom + 12px)", reg,
                                                     null, pos, ctx, out double px);
            Assert.That(ok, Is.True);
            Assert.That(px, Is.EqualTo(12).Within(0.001));
        }

        [Test]
        public void Garbage_function_string_returns_false_and_zero() {
            // A string that isn't `anchor(...)` at all must hard-fail — the
            // caller should fall through to the inset-as-length path.
            var (_, pos) = NewAbsScenario(0, 0, 200, 200);
            var reg = new AnchorRegistry();
            var ctx = NewCtx();

            bool ok = AnchorResolver.TryResolveSide("top", "10px", reg, null, pos, ctx, out double px);
            Assert.That(ok, Is.False);
            Assert.That(px, Is.EqualTo(0).Within(0.001));
        }

        // -------------------------------------------------------------------
        // TryResolveSide — implicit anchor (spec: `position-anchor`,
        // historically `anchor-default`)
        // -------------------------------------------------------------------

        [Test]
        public void Anchor_with_no_explicit_name_uses_fallback_position_anchor() {
            // The bare `anchor(bottom)` form — no name token in the
            // function — defers to the caller-supplied fallback (the
            // positioned box's `position-anchor`; historically called
            // `anchor-default` in earlier drafts of CSS Anchor §3.2).
            var (parent, pos) = NewAbsScenario(0, 0, 200, 200);
            var anchor = NewBox(0, 50, 100, 30); // bottom edge at y=80.
            parent.AddChild(anchor);
            var reg = new AnchorRegistry();
            reg.Register("--implicit", anchor);

            var ctx = NewCtx();
            bool ok = AnchorResolver.TryResolveSide("top", "anchor(bottom)", reg,
                                                     fallbackAnchorName: "--implicit",
                                                     pos, ctx, out double px);
            Assert.That(ok, Is.True);
            Assert.That(px, Is.EqualTo(80).Within(0.001),
                "bare anchor(bottom) must follow the position-anchor fallback to the registered '--implicit'");
        }

        [Test]
        public void Explicit_anchor_name_in_function_overrides_fallback() {
            // When both an explicit name and a `position-anchor` fallback
            // are in play, the in-function name wins.
            var (parent, pos) = NewAbsScenario(0, 0, 400, 400);
            var aImplicit = NewBox(0, 10, 50, 20); // not the one we want.
            var aExplicit = NewBox(0, 100, 50, 30); // bottom edge at 130.
            parent.AddChild(aImplicit);
            parent.AddChild(aExplicit);
            var reg = new AnchorRegistry();
            reg.Register("--implicit", aImplicit);
            reg.Register("--explicit", aExplicit);

            var ctx = NewCtx();
            bool ok = AnchorResolver.TryResolveSide("top", "anchor(--explicit bottom)", reg,
                                                     fallbackAnchorName: "--implicit",
                                                     pos, ctx, out double px);
            Assert.That(ok, Is.True);
            Assert.That(px, Is.EqualTo(130).Within(0.001));
        }

        // -------------------------------------------------------------------
        // Multiple anchors with the same name — actual behaviour:
        // last-registration-wins (per AnchorRegistry's documented v1 scope).
        // -------------------------------------------------------------------

        [Test]
        public void Same_name_on_two_elements_last_registration_wins() {
            // The registry collapses duplicate names — the second Register
            // call overwrites the first. The resolver therefore reports the
            // edge of the SECOND anchor box. (This is the actual v1 impl
            // behaviour per AnchorRegistry.cs:31-32; the spec's nearest-in-
            // tree scoping is explicitly deferred per the class header.)
            var (parent, pos) = NewAbsScenario(0, 0, 400, 400);
            var first = NewBox(0, 0, 50, 20);   // bottom at y=20
            var second = NewBox(0, 100, 50, 30); // bottom at y=130
            parent.AddChild(first);
            parent.AddChild(second);
            var reg = new AnchorRegistry();
            reg.Register("--dup", first);
            reg.Register("--dup", second);

            var ctx = NewCtx();
            bool ok = AnchorResolver.TryResolveSide("top", "anchor(--dup bottom)", reg,
                                                     null, pos, ctx, out double px);
            Assert.That(ok, Is.True);
            Assert.That(px, Is.EqualTo(130).Within(0.001),
                "v1 AnchorRegistry is last-write-wins; the second Register('--dup', ...) must shadow the first");
        }

        // -------------------------------------------------------------------
        // Anchor on a non-positioned element — discoverable per spec
        // -------------------------------------------------------------------

        [Test]
        public void Anchor_on_static_position_element_is_discoverable_and_resolves() {
            // CSS Anchor Positioning: the `anchor-name` property is valid on
            // ANY element, not just positioned ones. The registry must
            // accept a static-positioned anchor box and the resolver must
            // read its rect like any other.
            var (parent, pos) = NewAbsScenario(0, 0, 200, 200);
            var staticAnchor = NewBox(0, 40, 60, 25); // bottom edge at y=65.
            // staticAnchor.Position left at the BlockBox default (static).
            parent.AddChild(staticAnchor);
            Assert.That(staticAnchor.Position, Is.EqualTo(PositionType.Static),
                "precondition: the anchor element must NOT be positioned for this test to assert what it claims");

            var reg = new AnchorRegistry();
            reg.Register("--static-anchor", staticAnchor);

            var ctx = NewCtx();
            bool ok = AnchorResolver.TryResolveSide("top", "anchor(--static-anchor bottom)", reg,
                                                     null, pos, ctx, out double px);
            Assert.That(ok, Is.True);
            Assert.That(px, Is.EqualTo(65).Within(0.001));
        }

        // -------------------------------------------------------------------
        // TryResolveSide — null guards
        // -------------------------------------------------------------------

        [Test]
        public void Null_registry_returns_false() {
            var (_, pos) = NewAbsScenario(0, 0, 200, 200);
            var ctx = NewCtx();
            bool ok = AnchorResolver.TryResolveSide("top", "anchor(--x bottom)", null,
                                                     null, pos, ctx, out double px);
            Assert.That(ok, Is.False);
            Assert.That(px, Is.EqualTo(0));
        }

        [Test]
        public void Null_positioned_box_returns_false() {
            var reg = new AnchorRegistry();
            var ctx = NewCtx();
            bool ok = AnchorResolver.TryResolveSide("top", "anchor(--x bottom)", reg,
                                                     null, null, ctx, out double px);
            Assert.That(ok, Is.False);
            Assert.That(px, Is.EqualTo(0));
        }

        // -------------------------------------------------------------------
        // TryResolveSide — in-function offset addition
        // -------------------------------------------------------------------

        [Test]
        public void In_function_pixel_offset_is_added_after_edge_resolution() {
            // anchor(bottom + 8px): edge maps to 70, then +8 → 78.
            var (parent, pos) = NewAbsScenario(0, 0, 200, 200);
            var anchor = NewBox(0, 30, 100, 40); // bottom = 70.
            parent.AddChild(anchor);
            var reg = new AnchorRegistry();
            reg.Register("--foo", anchor);

            var ctx = NewCtx();
            bool ok = AnchorResolver.TryResolveSide("top", "anchor(--foo bottom + 8px)", reg,
                                                     null, pos, ctx, out double px);
            Assert.That(ok, Is.True);
            Assert.That(px, Is.EqualTo(78).Within(0.001));
        }

        [Test]
        public void In_function_negative_offset_subtracts_from_edge() {
            var (parent, pos) = NewAbsScenario(0, 0, 200, 200);
            var anchor = NewBox(0, 30, 100, 40); // bottom = 70.
            parent.AddChild(anchor);
            var reg = new AnchorRegistry();
            reg.Register("--foo", anchor);

            var ctx = NewCtx();
            bool ok = AnchorResolver.TryResolveSide("top", "anchor(--foo bottom - 5px)", reg,
                                                     null, pos, ctx, out double px);
            Assert.That(ok, Is.True);
            Assert.That(px, Is.EqualTo(65).Within(0.001));
        }

        // -------------------------------------------------------------------
        // TryResolveSize — width / height of the anchor box
        // -------------------------------------------------------------------

        [Test]
        public void AnchorSize_explicit_width_returns_anchor_width() {
            var anchor = NewBox(0, 0, 123, 45);
            var reg = new AnchorRegistry();
            reg.Register("--btn", anchor);

            bool ok = AnchorResolver.TryResolveSize("width", "anchor-size(--btn width)",
                                                     reg, null, out double px);
            Assert.That(ok, Is.True);
            Assert.That(px, Is.EqualTo(123).Within(0.001));
        }

        [Test]
        public void AnchorSize_inferred_axis_follows_property_name() {
            // anchor-size(--btn) with no explicit axis — the resolver must
            // pick width for a width-class property and height for a
            // height-class one.
            var anchor = NewBox(0, 0, 200, 80);
            var reg = new AnchorRegistry();
            reg.Register("--btn", anchor);

            Assert.That(AnchorResolver.TryResolveSize("min-width", "anchor-size(--btn)",
                                                       reg, null, out double w), Is.True);
            Assert.That(w, Is.EqualTo(200).Within(0.001));

            Assert.That(AnchorResolver.TryResolveSize("max-height", "anchor-size(--btn)",
                                                       reg, null, out double h), Is.True);
            Assert.That(h, Is.EqualTo(80).Within(0.001));
        }

        [Test]
        public void AnchorSize_unknown_name_yields_zero_with_true_result() {
            // Mirrors TryResolveSide: missing anchor is a soft-fail with
            // pixels = 0 (per AnchorResolver.cs:95-98).
            var reg = new AnchorRegistry();
            bool ok = AnchorResolver.TryResolveSize("width", "anchor-size(--gone width)",
                                                     reg, null, out double px);
            Assert.That(ok, Is.True);
            Assert.That(px, Is.EqualTo(0).Within(0.001));
        }

        // -------------------------------------------------------------------
        // EdgeAbsolute — pure-function coverage of the side/edge matrix
        // -------------------------------------------------------------------

        [Test]
        public void EdgeAbsolute_maps_all_physical_edges_to_rect_coordinates() {
            // Rect at (10, 20), 100w x 50h.
            const double x = 10, y = 20, w = 100, h = 50;
            Assert.That(AnchorResolver.EdgeAbsolute("top", AnchorEdge.Top, x, y, w, h),
                Is.EqualTo(20).Within(0.001));
            Assert.That(AnchorResolver.EdgeAbsolute("top", AnchorEdge.Bottom, x, y, w, h),
                Is.EqualTo(70).Within(0.001));
            Assert.That(AnchorResolver.EdgeAbsolute("left", AnchorEdge.Left, x, y, w, h),
                Is.EqualTo(10).Within(0.001));
            Assert.That(AnchorResolver.EdgeAbsolute("left", AnchorEdge.Right, x, y, w, h),
                Is.EqualTo(110).Within(0.001));
        }

        [Test]
        public void EdgeAbsolute_center_axis_depends_on_side() {
            // Center collapses onto the relevant axis according to which
            // side property we are resolving. Same rect, two distinct outputs.
            const double x = 10, y = 20, w = 100, h = 50;
            Assert.That(AnchorResolver.EdgeAbsolute("top", AnchorEdge.Center, x, y, w, h),
                Is.EqualTo(45).Within(0.001),
                "center on vertical side → y midpoint = 20 + 50/2");
            Assert.That(AnchorResolver.EdgeAbsolute("left", AnchorEdge.Center, x, y, w, h),
                Is.EqualTo(60).Within(0.001),
                "center on horizontal side → x midpoint = 10 + 100/2");
        }

        [Test]
        public void EdgeAbsolute_logical_start_end_map_LTR_only_v1() {
            // The v1 mapping (per AnchorResolver.cs:132-137) is LTR-only:
            // start → top/left, end → bottom/right depending on side axis.
            const double x = 10, y = 20, w = 100, h = 50;
            Assert.That(AnchorResolver.EdgeAbsolute("top", AnchorEdge.Start, x, y, w, h),
                Is.EqualTo(20).Within(0.001));
            Assert.That(AnchorResolver.EdgeAbsolute("top", AnchorEdge.End, x, y, w, h),
                Is.EqualTo(70).Within(0.001));
            Assert.That(AnchorResolver.EdgeAbsolute("left", AnchorEdge.SelfStart, x, y, w, h),
                Is.EqualTo(10).Within(0.001));
            Assert.That(AnchorResolver.EdgeAbsolute("left", AnchorEdge.SelfEnd, x, y, w, h),
                Is.EqualTo(110).Within(0.001));
        }
    }
}
