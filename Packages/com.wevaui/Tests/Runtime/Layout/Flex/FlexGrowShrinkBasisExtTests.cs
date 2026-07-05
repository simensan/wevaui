// §5 — flex-grow / flex-shrink / flex-basis extended coverage
// (CSS Flexbox L1 §7.1-§7.3)
// FlexGrowShrinkTests.cs covers 0/1/2 grow, shrink proportional, basis
// auto/content/px. This file adds:
//   - flex-basis as percentage (resolves against container main size)
//   - flex-shrink weighted by hypothetical main size (spec §9.7.4)
//   - min-content floor: item won't shrink below min-content unless min-width:0
//   - flex:1 / flex:auto / flex:none shorthand layout effects
//   - flex-grow with gap interaction
using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;

namespace Weva.Tests.Layout.Flex {
    public class FlexGrowShrinkBasisExtTests {

        // ── flex-basis as percentage ───────────────────────────────────────

        [Test]
        public void Flex_basis_percentage_resolves_against_container_main_size() {
            // flex-basis: 50% in a 600px row container → base size = 300px.
            // Two such items = 600px exactly, no grow/shrink needed.
            var (root, _, _) = Build(
                "<div style=\"display:flex;width:600px\">"
                + "<div style=\"flex:0 0 50%;height:50px\"></div>"
                + "<div style=\"flex:0 0 50%;height:50px\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            Assert.That(a.Width, Is.EqualTo(300).Within(0.001),
                "flex-basis:50% should resolve to 300px against 600px container");
            Assert.That(b.Width, Is.EqualTo(300).Within(0.001));
            Assert.That(a.X, Is.EqualTo(0).Within(0.001));
            Assert.That(b.X, Is.EqualTo(300).Within(0.001));
        }

        [Test]
        public void Flex_basis_percentage_25_pct_leaves_free_space_for_grow() {
            // flex-basis:25% (150px) + flex:1. Two items have base 150 each =
            // 300 used, 300 free split equally → each final = 150+150 = 300.
            var (root, _, _) = Build(
                "<div style=\"display:flex;width:600px\">"
                + "<div style=\"flex:1 1 25%;height:50px\"></div>"
                + "<div style=\"flex:1 1 25%;height:50px\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            Assert.That(a.Width, Is.EqualTo(300).Within(0.001));
            Assert.That(b.Width, Is.EqualTo(300).Within(0.001));
        }

        // ── flex-shrink weighted by hypothetical main size (spec §9.7.4) ──

        [Test]
        public void Shrink_weighted_by_base_size_larger_basis_absorbs_more() {
            // Two items with different flex-shrink=1 but different basis sizes.
            // Item A: basis=400, item B: basis=200. Total=600 in 400px container.
            // Overflow = 200. Weight(A) = 1*400=400, weight(B) = 1*200=200.
            // Total weight = 600. A absorbs 200*(400/600) ≈ 133.33px → width=266.67.
            // B absorbs 200*(200/600) ≈ 66.67px → width=133.33.
            var (root, _, _) = Build(
                "<div style=\"display:flex;width:400px\">"
                + "<div style=\"flex:0 1 400px;height:50px\"></div>"
                + "<div style=\"flex:0 1 200px;height:50px\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            Assert.That(a.Width, Is.EqualTo(400.0 - 200.0 * 400.0 / 600.0).Within(0.5));
            Assert.That(b.Width, Is.EqualTo(200.0 - 200.0 * 200.0 / 600.0).Within(0.5));
        }

        [Test]
        public void Shrink_two_vs_one_absorbs_proportionally() {
            // Item A: shrink=2, basis=200. Item B: shrink=1, basis=200.
            // Total=400 in 300px container. Overflow=100.
            // Weight(A)=2*200=400, weight(B)=1*200=200. Total=600.
            // A absorbs 100*(400/600)=66.67 → width=133.33.
            // B absorbs 100*(200/600)=33.33 → width=166.67.
            var (root, _, _) = Build(
                "<div style=\"display:flex;width:300px\">"
                + "<div style=\"flex:0 2 200px;height:50px\"></div>"
                + "<div style=\"flex:0 1 200px;height:50px\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            Assert.That(a.Width, Is.EqualTo(200.0 - 100.0 * 400.0 / 600.0).Within(0.5));
            Assert.That(b.Width, Is.EqualTo(200.0 - 100.0 * 200.0 / 600.0).Within(0.5));
        }

        // ── flex:none / flex:auto / flex:1 layout effects ─────────────────

        [Test]
        public void Flex_none_item_does_not_grow_or_shrink() {
            // flex:none = flex:0 0 auto. Item stays at its natural width.
            var (root, _, _) = Build(
                "<div style=\"display:flex;width:600px\">"
                + "<div style=\"flex:none;width:200px;height:50px\"></div>"
                + "<div style=\"flex:1;height:50px\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            Assert.That(a.Width, Is.EqualTo(200).Within(0.001), "flex:none item must not shrink or grow");
            Assert.That(b.Width, Is.EqualTo(400).Within(0.001), "flex:1 item takes remaining space");
        }

        [Test]
        public void Flex_auto_item_grows_to_fill_remaining_space() {
            // flex:auto = flex:1 1 auto. With basis=auto (uses natural width),
            // item with explicit width 100px grows to take remaining 400px.
            // Two items: A=auto(100px natural + grows), B=fixed 100px.
            // A grows: 600-100-100=400 extra → A.Width=100+400=500? No, B is
            // not a flex:auto. B is flex:0 0 100px so it won't grow.
            var (root, _, _) = Build(
                "<div style=\"display:flex;width:600px\">"
                + "<div style=\"flex:auto;width:100px;height:50px\"></div>"
                + "<div style=\"flex:0 0 100px;height:50px\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            Assert.That(b.Width, Is.EqualTo(100).Within(0.001), "fixed item keeps its 100px");
            Assert.That(a.Width, Is.EqualTo(500).Within(0.001), "flex:auto item takes remaining space");
        }

        [Test]
        public void Flex_one_shorthand_distributes_remaining_space_equally() {
            // flex:1 = flex:1 1 0. Both items start from 0 basis and split
            // the full 600px container equally.
            var (root, _, _) = Build(
                "<div style=\"display:flex;width:600px\">"
                + "<div style=\"flex:1;height:50px\"></div>"
                + "<div style=\"flex:1;height:50px\"></div>"
                + "<div style=\"flex:1;height:50px\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1); var c = ChildAt(fb, 2);
            Assert.That(a.Width, Is.EqualTo(200).Within(0.001));
            Assert.That(b.Width, Is.EqualTo(200).Within(0.001));
            Assert.That(c.Width, Is.EqualTo(200).Within(0.001));
        }

        // ── min-width:0 allows shrink below content size ──────────────────

        [Test]
        public void Min_width_zero_allows_item_to_shrink_below_content_size() {
            // An item with text content has min-width:auto = min-content by
            // default, which prevents it from shrinking below text width.
            // min-width:0 explicitly removes this floor.
            var css = @"
                .flex { display: flex; width: 200px; font-size: 16px; }
                .a { flex: 1 1 0; min-width: 0; }
                .b { flex: 0 0 100px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\">"
                + "<div class=\"a\">Hello World Long Text</div>"
                + "<div class=\"b\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            // With min-width:0, item a gets 100px (200-100) even if text is wider.
            Assert.That(a.Width, Is.EqualTo(100).Within(0.001),
                "min-width:0 allows shrink below text content size");
            Assert.That(b.Width, Is.EqualTo(100).Within(0.001));
        }

        [Test]
        public void Flex_grow_with_gap_reduces_available_space() {
            // Two flex:1 items with gap:40px in a 600px container.
            // Available after gap: 600-40=560. Each item: 560/2=280.
            const string css = @"
                .flex { display: flex; width: 600px; gap: 40px; }
                .item { flex: 1; height: 50px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"item\"></div><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            Assert.That(a.Width, Is.EqualTo(280).Within(0.001),
                "flex-grow must account for gap in free-space calculation");
            Assert.That(b.Width, Is.EqualTo(280).Within(0.001));
            Assert.That(b.X, Is.EqualTo(280 + 40).Within(0.001));
        }

        // ── Grow zero with explicit basis keeps exact size ─────────────────

        [Test]
        public void Flex_grow_zero_keeps_natural_basis_when_free_space_exists() {
            // Item A has flex:0 0 100px; there's plenty of free space but it
            // must not grow.
            var (root, _, _) = Build(
                "<div style=\"display:flex;width:600px\">"
                + "<div style=\"flex:0 0 100px;height:50px\"></div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            Assert.That(a.Width, Is.EqualTo(100).Within(0.001), "flex-grow:0 must not grow");
        }
    }
}
