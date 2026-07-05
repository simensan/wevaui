using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    public class LayoutEngineTests {
        [Test]
        public void End_to_end_html_css_produces_laid_out_box_tree() {
            var (root, _, _) = Build(
                "<div style=\"padding:8px\"><p>hello</p></div>",
                null, 800);
            Assert.That(root, Is.Not.Null);
            Assert.That(root.Children.Count, Is.GreaterThan(0));
            // Find the paragraph and check it has at least one line box.
            BlockBox p = null;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && !(b is AnonymousBlockBox) && bb.Element?.TagName == "p") p = bb;
            }
            Assert.That(p, Is.Not.Null);
            int lines = 0;
            foreach (var c in p.Children) if (c is LineBox) lines++;
            Assert.That(lines, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void Root_box_takes_viewport_width() {
            var (root, _, _) = Build("<div></div>", null, 1024, 768);
            Assert.That(root.Width, Is.EqualTo(1024).Within(0.001));
        }

        [Test]
        public void Coordinate_origin_is_top_left() {
            // First child's Y is 0, second is below.
            var (root, _, _) = Build(
                "<div id=\"a\" style=\"height:50px\"></div><div id=\"b\" style=\"height:30px\"></div>",
                null, 800);
            BlockBox a = null, b = null;
            int seen = 0;
            foreach (var box in AllBoxes(root)) {
                if (box is BlockBox bb && !(box is AnonymousBlockBox) && bb.Element?.TagName == "div") {
                    seen++;
                    if (seen == 1) a = bb;
                    else if (seen == 2) b = bb;
                }
            }
            Assert.That(a.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(b.Y, Is.GreaterThan(a.Y));
        }

        [Test]
        public void Realistic_small_page_lays_out() {
            var html = "<header><h1>Title</h1></header><main><p>Hello <strong>world</strong></p></main>";
            var css = "header { padding: 10px; } h1 { font-size: 16px; } main { padding: 20px; }";
            var (root, _, _) = Build(html, css, 800);
            BlockBox header = null;
            BlockBox main = null;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element?.TagName == "header") header = bb;
                if (b is BlockBox bb2 && bb2.Element?.TagName == "main") main = bb2;
            }
            Assert.That(header, Is.Not.Null);
            Assert.That(main, Is.Not.Null);
            Assert.That(main.Y, Is.GreaterThanOrEqualTo(header.Y + header.Height));
            Assert.That(header.PaddingTop, Is.EqualTo(10).Within(0.001));
        }

        [Test]
        public void Phase1_demo_full_layout() {
            var (root, _, _) = Build(
                "<p>Click <a href=\"#\"><strong>here</strong></a> to start</p>",
                "a { color: blue; } strong { font-weight: bold; }",
                200);
            BlockBox p = null;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element?.TagName == "p") p = bb;
            }
            Assert.That(p, Is.Not.Null);
            // Verify the link's text run carries blue color and bold.
            TextRun hereRun = null;
            foreach (var b in AllBoxes(p)) {
                if (b is TextRun tr && tr.Text.Contains("here")) { hereRun = tr; break; }
            }
            Assert.That(hereRun.Style.Get("color"), Is.EqualTo("blue"));
            Assert.That(hereRun.Style.Get("font-weight"), Is.EqualTo("bold"));
        }

        [Test]
        public void Layout_stable_when_called_twice() {
            var html = "<div style=\"padding:10px;width:200px\"><p>hi</p></div>";
            var (root1, _, _) = Build(html, null, 800);
            var (root2, _, _) = Build(html, null, 800);
            // Compare top-level structure widths/heights.
            Assert.That(root1.Width, Is.EqualTo(root2.Width).Within(0.001));
            Assert.That(root1.Height, Is.EqualTo(root2.Height).Within(0.001));
        }

        [Test]
        public void Display_none_root_returns_null_when_using_element_overload() {
            var doc = Html("<div style=\"display:none\"></div>");
            var sheets = new[] {
                OriginatedStylesheet.UserAgent(ParseCss(LayoutTestHelpers.BuiltinUserAgent))
            };
            var engine = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            var ctx = new LayoutContext(new MonoFontMetrics());
            var le = new LayoutEngine(new MonoFontMetrics());
            var div = FindByTag(doc, "div");
            var laid = le.Layout(div, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx);
            Assert.That(laid, Is.Null);
        }

        [Test]
        public void Document_root_height_grows_to_contain_block_children() {
            var (root, _, _) = Build(
                "<div style=\"height:50px\"></div><div style=\"height:75px\"></div>",
                null, 800);
            Assert.That(root.Height, Is.EqualTo(125).Within(0.001));
        }
    }
}
