using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;

namespace Weva.Tests.Layout.Flex {
    public class FlexGrowShrinkTests {
        const string Css = @"
            .flex { display: flex; width: 600px; }
        ";

        [Test]
        public void Two_items_flex_one_split_equally() {
            var (root, _, _) = Build(
                "<div class=\"flex\"><div style=\"flex:1\"></div><div style=\"flex:1\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            Assert.That(a.Width, Is.EqualTo(300).Within(0.001));
            Assert.That(b.Width, Is.EqualTo(300).Within(0.001));
            Assert.That(a.X, Is.EqualTo(0).Within(0.001));
            Assert.That(b.X, Is.EqualTo(300).Within(0.001));
        }

        [Test]
        public void Grow_two_versus_one_yields_2_to_1_ratio() {
            var (root, _, _) = Build(
                "<div class=\"flex\"><div style=\"flex:2\"></div><div style=\"flex:1\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            Assert.That(a.Width, Is.EqualTo(400).Within(0.001));
            Assert.That(b.Width, Is.EqualTo(200).Within(0.001));
        }

        [Test]
        public void Shrink_proportional_to_size_when_overflow() {
            var (root, _, _) = Build(
                "<div class=\"flex\"><div style=\"flex:0 1 400px\"></div><div style=\"flex:0 1 400px\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            Assert.That(a.Width, Is.EqualTo(300).Within(0.001));
            Assert.That(b.Width, Is.EqualTo(300).Within(0.001));
        }

        [Test]
        public void Grow_zero_plus_grow_one_takes_remaining_space() {
            var (root, _, _) = Build(
                "<div class=\"flex\"><div style=\"flex:0 0 100px\"></div><div style=\"flex:1 1 0\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            Assert.That(a.Width, Is.EqualTo(100).Within(0.001));
            Assert.That(b.Width, Is.EqualTo(500).Within(0.001));
        }

        [Test]
        public void Mixed_basis_partitions_free_space_correctly() {
            var (root, _, _) = Build(
                "<div class=\"flex\"><div style=\"flex:1 1 100px\"></div><div style=\"flex:2 1 0\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            Assert.That(a.Width, Is.EqualTo(100 + 500.0 / 3.0).Within(0.001));
            Assert.That(b.Width, Is.EqualTo(2 * 500.0 / 3.0).Within(0.001));
        }

        [Test]
        public void Sum_already_at_container_size_no_growth_no_shrink() {
            var (root, _, _) = Build(
                "<div class=\"flex\"><div style=\"flex:0 0 200px\"></div><div style=\"flex:0 0 200px\"></div><div style=\"flex:0 0 200px\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1); var c = ChildAt(fb, 2);
            Assert.That(a.Width, Is.EqualTo(200).Within(0.001));
            Assert.That(b.Width, Is.EqualTo(200).Within(0.001));
            Assert.That(c.Width, Is.EqualTo(200).Within(0.001));
        }

        [Test]
        public void Shrink_zero_keeps_base_size_even_when_overflow() {
            var (root, _, _) = Build(
                "<div class=\"flex\"><div style=\"flex:0 0 400px\"></div><div style=\"flex:0 0 400px\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            Assert.That(a.Width, Is.EqualTo(400).Within(0.001));
            Assert.That(b.Width, Is.EqualTo(400).Within(0.001));
        }

        [Test]
        public void Row_flex_min_width_auto_keeps_plain_block_items_from_overlapping() {
            const string css = @"
                .head { display: flex; width: 210px; gap: 10px; }
                .title { font-size: 16px; }
                .perf { display: flex; gap: 8px; margin-left: auto; }
                .chip { min-width: 72px; height: 20px; }
                .filters { display: flex; width: 74px; height: 20px; flex-shrink: 0; }
            ";
            var (root, _, _) = Build(
                "<div class=\"head\">" +
                "<div class=\"title\">Quest Log</div>" +
                "<div class=\"perf\"><span class=\"chip\">31.0 ms</span><span class=\"chip\">32 FPS</span></div>" +
                "<nav class=\"filters\"><button>Active</button></nav>" +
                "</div>",
                css,
                viewportWidth: 400);

            var fb = FindFlex(root, "div");
            var title = ChildAt(fb, 0);
            var perf = ChildAt(fb, 1);
            var filters = ChildAt(fb, 2);

            Assert.That(title.Width, Is.GreaterThan(0));
            Assert.That(perf.X, Is.GreaterThanOrEqualTo(title.X + title.Width + 10 - 0.001));
            Assert.That(filters.X, Is.GreaterThanOrEqualTo(perf.X + perf.Width + 10 - 0.001),
                "Flex items may overflow the container, but their boxes must not overlap.");
        }

        [Test]
        public void Nested_flex_item_min_width_content_box_contributes_outer_width() {
            const string css = @"
                .head { display: flex; width: 220px; gap: 24px; }
                .left { width: 40px; flex-shrink: 0; }
                .perf { display: flex; gap: 8px; margin-left: auto; }
                .chip {
                    display: inline-flex;
                    min-width: 72px;
                    padding: 6px 9px;
                    border: 1px solid transparent;
                }
                .filters { width: 40px; height: 20px; flex-shrink: 0; }
            ";
            var (root, _, _) = Build(
                "<div class=\"head\">" +
                "<div class=\"left\"></div>" +
                "<div class=\"perf\"><span class=\"chip\">5.0</span><span class=\"chip\">199</span></div>" +
                "<nav class=\"filters\"></nav>" +
                "</div>",
                css,
                viewportWidth: 400);

            var head = FindFlex(root, "div");
            var perf = ChildAt(head, 1);
            var filters = ChildAt(head, 2);

            Assert.That(perf.Width, Is.EqualTo(192).Within(0.001),
                "Each content-box chip is 72px content + 18px padding + 2px border, plus one 8px flex gap.");
            Assert.That(filters.X, Is.GreaterThanOrEqualTo(perf.X + perf.Width + 24 - 0.001));
        }

        [Test]
        public void Column_flex_item_in_row_flex_keeps_parent_min_width_allocation() {
            const string css = @"
                .head { display: flex; width: 500px; gap: 12px; }
                .chip {
                    display: flex;
                    flex-direction: column;
                    min-width: 112px;
                    padding: 8px 12px;
                    border: 1px solid transparent;
                }
                .peer { width: 80px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"head\"><div class=\"chip\"><span>Level</span><strong>18</strong></div><div class=\"peer\"></div></div>",
                css,
                viewportWidth: 700);

            var head = FindFlex(root, "div");
            var chip = ChildAt(head, 0);

            Assert.That(chip.Width, Is.EqualTo(138).Within(0.001),
                "The parent row flex owns the item's main-axis width; the child column flex must not collapse it to text width.");
        }

        [Test]
        public void Grow_zero_keeps_base_size_when_undeflow() {
            var (root, _, _) = Build(
                "<div class=\"flex\"><div style=\"flex:0 1 100px\"></div><div style=\"flex:0 1 100px\"></div></div>",
                Css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0); var b = ChildAt(fb, 1);
            Assert.That(a.Width, Is.EqualTo(100).Within(0.001));
            Assert.That(b.Width, Is.EqualTo(100).Within(0.001));
        }

        // CSS Flexbox L1 §7.2.1: `flex-basis: content` resolves to the
        // item's max-content, NOT the pre-flex BlockLayout stack size.
        // Regression for E8: prior to the fix, the FlexBasisKind.Content
        // case in ComputeBaseSize assigned `natural` (= box.Width post-
        // BlockLayout = container's content width when the item is a
        // plain block) so a child with `flex-basis: content` and text
        // "hello world" inside a 300px container reported width=300 and
        // its non-shrinking flex item overflowed siblings.
        [Test]
        public void Flex_basis_content_resolves_to_max_content_text_width() {
            const string css = @"
                .row { display: flex; width: 300px; font-size: 16px; }
                .a { flex-grow: 0; flex-shrink: 0; flex-basis: content; }
                .b { flex-grow: 0; flex-shrink: 0; width: 50px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"row\"><div class=\"a\">hello world</div><div class=\"b\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            // MonoFontMetrics default: char-width 0.5em × 16px = 8px/char.
            // "hello world" = 11 chars → 88px. `flex-basis: content` must
            // resolve to that max-content, not the pre-flex 300px width.
            Assert.That(a.Width, Is.EqualTo(88).Within(0.5),
                $"flex-basis: content should resolve to max-content (~88px), got {a.Width}");
        }

        // Regression: `flex-basis: auto` continues to use the existing
        // auto path (max-content probe + heuristics). With identical
        // content and an inflated `natural`, auto already lands on
        // max-content; this guards that the goto-default fall-through
        // for `Content` did not regress the `Auto` arm.
        [Test]
        public void Flex_basis_auto_still_uses_auto_path_for_text_item() {
            const string css = @"
                .row { display: flex; width: 300px; font-size: 16px; }
                .a { flex-grow: 0; flex-shrink: 0; flex-basis: auto; }
                .b { flex-grow: 0; flex-shrink: 0; width: 50px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"row\"><div class=\"a\">hello world</div><div class=\"b\"></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            // The pre-existing auto path probes for max-content when
            // BlockLayout inflated `natural` to the container width.
            // Expected: 88px (max-content of "hello world").
            Assert.That(a.Width, Is.EqualTo(88).Within(0.5),
                $"flex-basis: auto should probe max-content (~88px), got {a.Width}");
        }
    }
}
