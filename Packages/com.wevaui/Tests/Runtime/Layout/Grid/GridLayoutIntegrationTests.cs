using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Grid.GridTestHelpers;

namespace Weva.Tests.Layout.Grid {
    public class GridLayoutIntegrationTests {
        [Test]
        public void Holy_grail_layout_with_template_areas() {
            const string css = @"
                .page {
                    display: grid;
                    grid-template-columns: 200px 1fr 200px;
                    grid-template-rows: 60px 1fr 40px;
                    grid-template-areas:
                        ""header header header""
                        ""nav main aside""
                        ""footer footer footer"";
                    width: 1000px;
                    height: 600px;
                }
                .header { grid-area: header; }
                .nav { grid-area: nav; }
                .main { grid-area: main; }
                .aside { grid-area: aside; }
                .footer { grid-area: footer; }
            ";
            var (root, _, _) = Build(
                @"<div class=""page""><div class=""header""></div><div class=""nav""></div><div class=""main""></div><div class=""aside""></div><div class=""footer""></div></div>",
                css, viewportWidth: 1200);

            var header = FindByClass(root, "header");
            var nav = FindByClass(root, "nav");
            var main = FindByClass(root, "main");
            var aside = FindByClass(root, "aside");
            var footer = FindByClass(root, "footer");

            Assert.That(header.X, Is.EqualTo(0).Within(0.01));
            Assert.That(header.Y, Is.EqualTo(0).Within(0.01));
            Assert.That(header.Width, Is.EqualTo(1000).Within(0.01));
            Assert.That(header.Height, Is.EqualTo(60).Within(0.01));

            Assert.That(nav.X, Is.EqualTo(0).Within(0.01));
            Assert.That(nav.Y, Is.EqualTo(60).Within(0.01));
            Assert.That(nav.Width, Is.EqualTo(200).Within(0.01));
            Assert.That(nav.Height, Is.EqualTo(500).Within(0.01));

            Assert.That(main.X, Is.EqualTo(200).Within(0.01));
            Assert.That(main.Width, Is.EqualTo(600).Within(0.01));

            Assert.That(aside.X, Is.EqualTo(800).Within(0.01));
            Assert.That(aside.Width, Is.EqualTo(200).Within(0.01));

            Assert.That(footer.Y, Is.EqualTo(560).Within(0.01));
            Assert.That(footer.Width, Is.EqualTo(1000).Within(0.01));
            Assert.That(footer.Height, Is.EqualTo(40).Within(0.01));
        }

        [Test]
        public void Twelve_column_grid() {
            const string css = @"
                .grid { display: grid; grid-template-columns: repeat(12, 1fr); width: 1200px; }
                .col-3 { grid-column: span 3; }
                .col-9 { grid-column: span 9; }
            ";
            var (root, _, _) = Build(
                @"<div class=""grid""><div class=""col-3""></div><div class=""col-9""></div></div>",
                css, viewportWidth: 1400);
            var c3 = FindByClass(root, "col-3");
            var c9 = FindByClass(root, "col-9");
            Assert.That(c3.Width, Is.EqualTo(300).Within(0.01));
            Assert.That(c9.Width, Is.EqualTo(900).Within(0.01));
            Assert.That(c9.X, Is.EqualTo(300).Within(0.01));
        }

        [Test]
        public void Card_grid_auto_fit_minmax() {
            const string css = @"
                .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); width: 800px; }
                .card { height: 100px; }
            ";
            var (root, _, _) = Build(
                @"<div class=""grid""><div class=""card""></div><div class=""card""></div><div class=""card""></div><div class=""card""></div></div>",
                css, viewportWidth: 1024);
            var grid = FindGridByClass(root, "grid");
            // 800 / 200 = 4 tracks. Each gets 200px (no extra fr).
            for (int i = 0; i < 4; i++) {
                Assert.That(ChildAt(grid, i).Width, Is.EqualTo(200).Within(0.01));
                Assert.That(ChildAt(grid, i).X, Is.EqualTo(i * 200).Within(0.01));
            }
        }

        [Test]
        public void Sidebar_plus_main_two_column() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 200px 1fr; width: 1000px; height: 600px; }
                .sidebar { background: red; }
                .main { background: blue; }
            ";
            var (root, _, _) = Build(
                @"<div class=""grid""><div class=""sidebar""></div><div class=""main""></div></div>",
                css, viewportWidth: 1200);
            var sb = FindByClass(root, "sidebar");
            var main = FindByClass(root, "main");
            Assert.That(sb.Width, Is.EqualTo(200).Within(0.01));
            Assert.That(main.X, Is.EqualTo(200).Within(0.01));
            Assert.That(main.Width, Is.EqualTo(800).Within(0.01));
        }

        [Test]
        public void Centered_modal_with_place_items_center() {
            const string css = @"
                .overlay { display: grid; place-items: center; width: 800px; height: 600px; }
                .modal { width: 200px; height: 100px; }
            ";
            var (root, _, _) = Build(
                @"<div class=""overlay""><div class=""modal""></div></div>",
                css, viewportWidth: 1024);
            var modal = FindByClass(root, "modal");
            Assert.That(modal.X, Is.EqualTo(300).Within(0.01));
            Assert.That(modal.Y, Is.EqualTo(250).Within(0.01));
            Assert.That(modal.Width, Is.EqualTo(200).Within(0.01));
            Assert.That(modal.Height, Is.EqualTo(100).Within(0.01));
        }

        [Test]
        public void Mixed_explicit_and_auto_placement() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 100px 100px 100px; grid-template-rows: 50px 50px; width: 300px; }
                .pinned { grid-area: 1 / 3 / 2 / 4; }
            ";
            var (root, _, _) = Build(
                @"<div class=""grid""><div></div><div class=""pinned""></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            var pinned = FindByClass(root, "pinned");

            // .pinned is at row 1 col 3. The two unmarked items auto-place at (1,1) and (1,2).
            var c0 = ChildAt(grid, 0);
            var c2 = ChildAt(grid, 2);
            Assert.That(c0.X, Is.EqualTo(0).Within(0.01));
            Assert.That(c0.Y, Is.EqualTo(0).Within(0.01));
            Assert.That(pinned.X, Is.EqualTo(200).Within(0.01));
            Assert.That(pinned.Y, Is.EqualTo(0).Within(0.01));
            // Third item lands at (1,2) since (1,3) is pinned.
            Assert.That(c2.X, Is.EqualTo(100).Within(0.01));
            Assert.That(c2.Y, Is.EqualTo(0).Within(0.01));
        }

        [Test]
        public void Auto_height_grid_updates_following_block_siblings_after_flex_children_shrink() {
            const string css = @"
                .card { width: 240px; padding: 10px; }
                .grid { display: grid; grid-template-columns: 1fr; row-gap: 10px; padding: 5px; width: 200px; box-sizing: border-box; }
                .row { display: flex; }
                .row span { display: block; height: 20px; width: 40px; }
                .after { height: 30px; margin-top: 18px; }
            ";
            var (root, _, _) = Build(
                @"<div class=""card""><div class=""grid""><div class=""row""><span></span><span></span></div><div class=""row""><span></span><span></span></div></div><div class=""after""></div></div>",
                css, viewportWidth: 400);

            var grid = FindGridByClass(root, "grid");
            var after = FindByClass(root, "after");

            Assert.That(grid.Height, Is.EqualTo(60).Within(0.01));
            Assert.That(after.Y, Is.EqualTo(grid.Y + grid.Height + 18).Within(0.01));
        }

        [Test]
        public void Implicit_auto_column_does_not_double_count_flex_item_frame() {
            const string css = @"
                * { box-sizing: border-box; }
                .thread {
                    display: grid;
                    grid-template-rows: auto 1fr auto;
                    width: 1350px;
                    height: 885px;
                }
                .head { display: flex; justify-content: space-between; padding: 12px 20px; }
                .messages {
                    display: flex;
                    flex-direction: column;
                    padding: 20px 24px;
                    min-width: 0;
                }
                .wide { width: 100%; height: 20px; }
                .composer {
                    display: flex;
                    padding: 12px 16px;
                    min-width: 0;
                }
                .input { flex: 1; min-width: 0; height: 40px; }
            ";
            var (root, _, _) = Build(
                @"<main class=""thread""><header class=""head""><span>Title</span><span>Actions</span></header><section class=""messages""><div class=""wide""></div></section><footer class=""composer""><div class=""input""></div><button>Send</button></footer></main>",
                css,
                viewportWidth: 1670,
                viewportHeight: 885);

            var thread = FindByClass(root, "thread");
            var messages = FindByClass(root, "messages");
            var composer = FindByClass(root, "composer");

            Assert.That(thread.Width, Is.EqualTo(1350).Within(0.01));
            Assert.That(messages.Width, Is.EqualTo(1350).Within(0.01));
            Assert.That(composer.Width, Is.EqualTo(1350).Within(0.01));
        }
    }
}
