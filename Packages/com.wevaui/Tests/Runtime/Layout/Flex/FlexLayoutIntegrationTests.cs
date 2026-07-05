using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using Weva.Parsing;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;

namespace Weva.Tests.Layout.Flex {
    public class FlexLayoutIntegrationTests {
        [Test]
        public void Header_main_footer_column_layout() {
            const string css = @"
                .page { display: flex; flex-direction: column; height: 600px; }
                header { height: 80px; }
                main { flex: 1; }
                footer { height: 60px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"page\"><header></header><main></main><footer></footer></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var header = ChildAt(fb, 0);
            var main = ChildAt(fb, 1);
            var footer = ChildAt(fb, 2);
            Assert.That(header.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(header.Height, Is.EqualTo(80).Within(0.001));
            Assert.That(main.Y, Is.EqualTo(80).Within(0.001));
            Assert.That(main.Height, Is.EqualTo(460).Within(0.001));
            Assert.That(footer.Y, Is.EqualTo(540).Within(0.001));
            Assert.That(footer.Height, Is.EqualTo(60).Within(0.001));
        }

        [Test]
        public void Equal_width_nav_buttons_with_gap() {
            const string css = @"
                nav { display: flex; gap: 10px; width: 530px; }
                nav > div { flex: 1; height: 40px; }
            ";
            var (root, _, _) = Build(
                "<nav><div></div><div></div><div></div><div></div><div></div></nav>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "nav");
            // Available: 530 - 4*10 = 490; each button = 98; positions stride 108.
            for (int i = 0; i < 5; i++) {
                var c = ChildAt(fb, i);
                Assert.That(c.Width, Is.EqualTo(98).Within(0.01));
                Assert.That(c.X, Is.EqualTo(i * 108).Within(0.01));
            }
        }

        [Test]
        public void Sidebar_plus_main_with_fixed_sidebar() {
            const string css = @"
                .layout { display: flex; width: 1000px; }
                .sidebar { flex: 0 0 240px; height: 600px; }
                .content { flex: 1; height: 600px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"layout\"><div class=\"sidebar\"></div><div class=\"content\"></div></div>",
                css, viewportWidth: 1200);
            var fb = FindFlex(root, "div");
            var sidebar = ChildAt(fb, 0);
            var content = ChildAt(fb, 1);
            Assert.That(sidebar.Width, Is.EqualTo(240).Within(0.001));
            Assert.That(content.Width, Is.EqualTo(760).Within(0.001));
            Assert.That(content.X, Is.EqualTo(240).Within(0.001));
        }

        [Test]
        public void Center_child_both_axes() {
            const string css = @"
                .center { display: flex; justify-content: center; align-items: center; width: 800px; height: 600px; }
                .child { width: 200px; height: 100px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"center\"><div class=\"child\"></div></div>",
                css, viewportWidth: 1024);
            var fb = FindFlex(root, "div");
            var child = ChildAt(fb, 0);
            Assert.That(child.X, Is.EqualTo(300).Within(0.001));
            Assert.That(child.Y, Is.EqualTo(250).Within(0.001));
        }

        // Positive regression test: row-flex containing nested column-flex
        // children produces correct cross size (no multi-pass convergence
        // inflation). The narrow #19 fix in PositioningPass.IntrinsicCross-
        // OfPureInlineBlock handles this simple case correctly. The
        // deeper #19b case (stats.html-style ancestor stretching where the
        // row tracks of an inner grid don't re-fire after a parent flex
        // resizes it) needs a separate fix and is not covered by this test.
        [Test]
        public void Row_flex_with_nested_column_flex_children_does_not_inflate_cross() {
            const string css = @"
                .outer { display: flex; flex-direction: row; gap: 8px; padding: 8px; background: black; width: 400px }
                .col { display: flex; flex-direction: column; gap: 4px; padding: 4px; background: gray }
                .col span { font-size: 14px; line-height: 1 }
            ";
            var (root, _, _) = Build(
                @"<div class=""outer"">
                    <div class=""col""><span>A</span></div>
                    <div class=""col""><span>B</span></div>
                </div>", css, viewportWidth: 500);
            var outer = FindFlex(root, "div");
            // Inner col content cross: padding(8) + max(span line-height 14) = 22.
            // Outer row line cross: max(col-outer-height 22) = 22.
            // Outer padding: 16 (8+8). Expected outer.Height ≈ 22 + 16 = 38.
            // Pre-#19b inflates by ~14 per nested level — typically lands at
            // ~52 or more.
            Assert.That(outer.Height, Is.EqualTo(38).Within(2.0),
                $"row-flex outer should sum to ~38 (padding 16 + col 22); got {outer.Height} — multi-pass convergence inflation");
        }

        [Test]
        public void Row_flex_item_inside_auto_height_column_collapses_to_line_cross_size() {
            const string css = @"
                .list { display: flex; flex-direction: column; gap: 4px; width: 326px; }
                .card { display: flex; align-items: center; gap: 12px; padding: 10px; }
                .thumb { width: 40px; height: 40px; flex-shrink: 0; }
                .body { display: flex; flex-direction: column; gap: 2px; flex: 1; }
                .name { height: 18px; }
                .sub { height: 16px; }
                .status { width: 20px; height: 20px; flex-shrink: 0; }
            ";
            var (root, _, _) = Build(
                @"<div class=""list"">
                    <div class=""card"">
                        <div class=""thumb""></div>
                        <div class=""body""><div class=""name""></div><div class=""sub""></div></div>
                        <div class=""status""></div>
                    </div>
                    <div class=""card"">
                        <div class=""thumb""></div>
                        <div class=""body""><div class=""name""></div><div class=""sub""></div></div>
                        <div class=""status""></div>
                    </div>
                </div>", css, viewportWidth: 500);

            var firstCard = FindByClass(root, "card");
            Assert.That(firstCard, Is.Not.Null);
            Assert.That(firstCard.Height, Is.EqualTo(60).Within(0.001));
        }

        [Test]
        public void Absolute_grid_column_flex_child_grows_after_positioning_sets_grid_height() {
            const string css = @"
                .page {
                    position: absolute;
                    top: 64px; left: 0; right: 0; bottom: 0;
                    display: grid;
                    grid-template-columns: 360px 1fr 420px;
                    grid-template-rows: 1fr;
                    gap: 24px;
                    padding: 24px 28px;
                }
                .right { grid-column: 3; display: flex; flex-direction: column; min-height: 0; }
                .detail { flex: 1 1 auto; display: flex; flex-direction: column; padding: 20px; min-height: 0; }
                .head { height: 50px; }
            ";
            var (root, _, _) = Build(
                @"<section class=""page"">
                    <aside class=""right"">
                        <div class=""detail""><div class=""head""></div></div>
                    </aside>
                </section>", css, viewportWidth: 1729, viewportHeight: 1080);

            var right = FindByClass(root, "right");
            var detail = FindByClass(root, "detail");
            Assert.That(right, Is.Not.Null);
            Assert.That(detail, Is.Not.Null);
            Assert.That(right.Height, Is.EqualTo(968).Within(0.001));
            Assert.That(detail.Height, Is.EqualTo(968).Within(0.001));
        }

        static BlockBox FindByClass(Box root, string className) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && HasClass(bb.Element?.ClassName, className)) return bb;
            }
            return null;
        }

        static bool HasClass(string classList, string className) {
            if (string.IsNullOrEmpty(classList)) return false;
            var parts = classList.Split(' ');
            for (int i = 0; i < parts.Length; i++) {
                if (parts[i] == className) return true;
            }
            return false;
        }

        [Test]
        public void Flex_item_border_box_honored_when_box_sizing_stored_as_non_keyword_parsed_value() {
            const string css = @"
                .row { display: flex; width: 600px; }
                .item { width: 100px; padding: 10px; flex: 0 0 auto; }
            ";
            var doc = HtmlParser.Parse(
                "<div class=\"row\"><div class=\"item\"></div></div>");
            var sheets = new List<OriginatedStylesheet> { UA(BuiltinUserAgent), Author(css) };
            var engine = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;

            Element itemEl = null;
            foreach (var kv in styles) {
                if (HasClass(kv.Key.ClassName, "item")) { itemEl = kv.Key; break; }
            }
            Assert.That(itemEl, Is.Not.Null);
            var itemStyle = styles[itemEl];
            itemStyle.SetParsed(CssProperties.BoxSizingId, new CssString("border-box", "border-box"));
            var parsed = itemStyle.GetParsed(CssProperties.BoxSizingId);
            Assert.That(parsed, Is.Not.InstanceOf<CssKeyword>());
            Assert.That(parsed, Is.Not.InstanceOf<CssIdentifier>());

            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 800,
                ViewportHeightPx = 600,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96,
            };
            var le = new LayoutEngine(new MonoFontMetrics());
            var root = le.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx);

            var fb = FindFlex(root, "div");
            var item = ChildAt(fb, 0);
            Assert.That(item.Width, Is.EqualTo(100).Within(0.001));
            Assert.That(item.ContentWidth, Is.EqualTo(80).Within(0.001));
        }

        [Test]
        public void Column_flex_child_width_100pct_resolves_against_container() {
            var css = @"
                .col { display: flex; flex-direction: column; width: 300px; height: 200px; }
                .child { width: 100%; height: 50px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"col\"><div class=\"child\">X</div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var child = ChildAt(fb, 0);
            System.Console.WriteLine($"child W={child.Width:F1} parent W={fb.Width:F1} contentW={fb.ContentWidth:F1}");
            Assert.That(child.Width, Is.EqualTo(300).Within(1),
                "width:100% in column flex should resolve against flex container, not viewport");
        }

        [Test]
        public void Column_flex_child_width_50pct_resolves_against_container() {
            var css = @"
                .col { display: flex; flex-direction: column; width: 400px; }
                .child { width: 50%; height: 30px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"col\"><div class=\"child\">X</div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var child = ChildAt(fb, 0);
            Assert.That(child.Width, Is.EqualTo(200).Within(1));
        }

        [Test]
        public void Column_flex_child_width_100pct_no_explicit_container_width() {
            var css = @"
                .wrapper { width: 400px; }
                .col { display: flex; flex-direction: column; }
                .child { width: 100%; height: 50px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"wrapper\"><div class=\"col\"><div class=\"child\">X</div></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var child = ChildAt(fb, 0);
            System.Console.WriteLine($"[no-explicit] child W={child.Width:F1} parent W={fb.Width:F1} contentW={fb.ContentWidth:F1}");
            Assert.That(child.Width, Is.EqualTo(400).Within(1),
                "width:100% should resolve against flex container (which inherited 400px from wrapper)");
        }

        [Test]
        public void Column_flex_nested_child_width_100pct() {
            var css = @"
                .outer { width: 500px; }
                .col { display: flex; flex-direction: column; width: 300px; }
                .inner { width: 100%; height: 30px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"outer\"><div class=\"col\"><div class=\"inner\">X</div></div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var child = ChildAt(fb, 0);
            System.Console.WriteLine($"[nested] child W={child.Width:F1} parent W={fb.Width:F1}");
            Assert.That(child.Width, Is.EqualTo(300).Within(1),
                "width:100% should resolve against 300px flex container, not 500px outer");
        }

        [Test]
        public void Flex_direction_row_lays_out_horizontally() {
            var css = @"
                .box { width: 100px; height: 100px; }
                .row { display: flex; flex-direction: row; gap: 16px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"row\"><div class=\"box\">A</div><div class=\"box\">B</div><div class=\"box\">C</div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            var b = ChildAt(fb, 1);
            var c = ChildAt(fb, 2);
            System.Console.WriteLine($"A: X={a.X:F0} Y={a.Y:F0}  B: X={b.X:F0} Y={b.Y:F0}  C: X={c.X:F0} Y={c.Y:F0}");
            Assert.That(a.Y, Is.EqualTo(b.Y).Within(1), "row flex items should be on the same Y");
            Assert.That(b.X, Is.GreaterThan(a.X + 50), "B should be to the right of A");
            Assert.That(c.X, Is.GreaterThan(b.X + 50), "C should be to the right of B");
        }

        [Test]
        public void Flex_default_direction_is_row() {
            var css = @"
                .box { width: 100px; height: 100px; }
                .row { display: flex; gap: 16px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"row\"><div class=\"box\">A</div><div class=\"box\">B</div><div class=\"box\">C</div></div>",
                css, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var a = ChildAt(fb, 0);
            var b = ChildAt(fb, 1);
            System.Console.WriteLine($"[default] A: X={a.X:F0} Y={a.Y:F0}  B: X={b.X:F0} Y={b.Y:F0}");
            Assert.That(a.Y, Is.EqualTo(b.Y).Within(1), "default flex direction should be row (horizontal)");
            Assert.That(b.X, Is.GreaterThan(a.X + 50), "B should be to the right of A");
        }

        [Test]
        public void Flex_row_inside_column_flex_parent() {
            var css = @"
                * { box-sizing: border-box; margin: 0; padding: 0; }
                .test-case {
                  display: flex;
                  flex-direction: column;
                  gap: 8px;
                  padding: 16px;
                }
                .flex-row-explicit {
                  display: flex;
                  flex-direction: row;
                  gap: 16px;
                }
                .box { width: 100px; height: 100px; }
            ";
            var html = @"
                <div class=""test-case"">
                    <div class=""flex-row-explicit"">
                        <div class=""box"">A</div>
                        <div class=""box"">B</div>
                        <div class=""box"">C</div>
                    </div>
                </div>
            ";
            var (root, _, _) = Build(html, css, viewportWidth: 800);
            // Find the inner row flex
            Weva.Layout.Flex.FlexBox rowFlex = null;
            foreach (var bx in AllBoxes(root)) {
                if (bx is Weva.Layout.Flex.FlexBox fb && fb.Element?.ClassName == "flex-row-explicit") { rowFlex = fb; break; }
            }
            Assert.That(rowFlex, Is.Not.Null, "row flex container must exist");
            var a = ChildAt(rowFlex, 0);
            var b = ChildAt(rowFlex, 1);
            var c = ChildAt(rowFlex, 2);
            System.Console.WriteLine($"[flex-row] A: X={a.X:F0} Y={a.Y:F0}  B: X={b.X:F0} Y={b.Y:F0}  C: X={c.X:F0} Y={c.Y:F0}");
            System.Console.WriteLine($"[flex-row] rowFlex dir={rowFlex.Style?.Get("flex-direction")} W={rowFlex.Width:F0} H={rowFlex.Height:F0}");
            Assert.That(a.Y, Is.EqualTo(b.Y).Within(1), "row flex items should be on same Y (horizontal layout)");
            Assert.That(b.X, Is.GreaterThan(a.X + 50), "B should be to the right of A");
        }

        sealed class UpgradeCtx {
            public List<UpgradeOption> Options { get; set; } = new() {
                new() { Name = "Fireball" },
                new() { Name = "Shield" },
                new() { Name = "Heal" }
            };
        }
        sealed class UpgradeOption { public string Name { get; set; } }

        [Test]
        public void RepeatBinding_children_participate_in_parent_grid() {
            var css = @"
                .options { display: grid; grid-template-columns: 100px 100px 100px; gap: 16px; }
                .card { height: 80px; background: red; }
            ";
            var html = @"
                <div class=""options"">
                    <template data-each=""Options as opt"">
                        <div class=""card"">{{ opt.Name }}</div>
                    </template>
                </div>
            ";
            var ctx = new UpgradeCtx();
            var (root, _, _) = BuildWithBindings(html, css, ctx);
            // Find the grid container
            Weva.Layout.Grid.GridBox grid = null;
            foreach (var bx in AllBoxes(root)) {
                if (bx is Weva.Layout.Grid.GridBox gb && gb.Element?.ClassName == "options") { grid = gb; break; }
            }
            Assert.That(grid, Is.Not.Null, "grid container must exist");
            // Count visible children (not display:none template)
            int visibleChildren = 0;
            for (int i = 0; i < grid.Children.Count; i++) {
                if (grid.Children[i].Width > 0) visibleChildren++;
            }
            // Check template box style
            for (int i = 0; i < grid.Children.Count; i++) {
                var ch = grid.Children[i];
                System.Console.WriteLine($"  box[{i}] tag={ch.Element?.TagName} display={ch.Style?.Get("display")} W={ch.Width:F0}");
            }
            System.Console.WriteLine($"[repeat-grid] grid children={grid.Children.Count} visible={visibleChildren} W={grid.Width:F0}");
            for (int i = 0; i < grid.Children.Count; i++) {
                var c = grid.Children[i];
                System.Console.WriteLine($"  child[{i}]: X={c.X:F0} Y={c.Y:F0} W={c.Width:F0} H={c.Height:F0} tag={c.Element?.TagName} cls={c.Element?.ClassName}");
            }
            Assert.That(visibleChildren, Is.EqualTo(3), "3 repeat items should be visible grid children");
            // All 3 should be on the same row (same Y)
            if (grid.Children.Count >= 3) {
                var c0 = grid.Children[0];
                var c1 = grid.Children[1];
                Assert.That(c0.Y, Is.EqualTo(c1.Y).Within(1), "grid items should be on same row (horizontal)");
                Assert.That(c1.X, Is.GreaterThan(c0.X + 50), "second grid item should be to the right");
            }
        }

        [Test]
        public void Inline_style_width_and_height_in_column_flex() {
            var (root, _, _) = Build(
                "<div style=\"display:flex;flex-direction:column;width:200px;height:400px\">" +
                "<div style=\"height:50%;width:100%\">X</div></div>",
                null, viewportWidth: 800);
            var fb = FindFlex(root, "div");
            var child = ChildAt(fb, 0);
            System.Console.WriteLine($"child W={child.Width:F1} H={child.Height:F1}");
            Assert.That(child.Width, Is.EqualTo(200).Within(1));
        }
    }
}
