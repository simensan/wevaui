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

namespace Weva.Tests.Layout {
    public class BlockLayoutTests {
        static BlockBox FindFirstBlock(Box root, string tag) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && !(b is AnonymousBlockBox) && bb.Element?.TagName == tag) return bb;
            }
            return null;
        }

        static BlockBox FindById(Box root, string id) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element?.GetAttribute("id") == id) return bb;
            }
            return null;
        }

        [Test]
        public void Single_block_takes_full_viewport_width() {
            var (root, _, _) = Build("<div></div>", null, viewportWidth: 800);
            var div = FindFirstBlock(root, "div");
            Assert.That(div, Is.Not.Null);
            Assert.That(div.Width, Is.EqualTo(800).Within(0.001));
        }

        [Test]
        public void Empty_block_has_zero_content_height() {
            var (root, _, _) = Build("<div></div>", null, viewportWidth: 800);
            var div = FindFirstBlock(root, "div");
            Assert.That(div.Height, Is.EqualTo(0).Within(0.001));
        }

        [Test]
        public void Two_blocks_stack_vertically() {
            var (root, _, _) = Build(
                "<div id=\"a\" style=\"height:50px\"></div><div id=\"b\" style=\"height:30px\"></div>",
                null, 800);
            var a = FindFirstBlock(root, "div");
            BlockBox b = null;
            int count = 0;
            foreach (var box in AllBoxes(root)) {
                if (box is BlockBox bb && !(box is AnonymousBlockBox) && bb.Element?.TagName == "div") {
                    count++;
                    if (count == 2) { b = bb; break; }
                }
            }
            Assert.That(a.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(b.Y, Is.EqualTo(50).Within(0.001));
            Assert.That(a.Height, Is.EqualTo(50).Within(0.001));
            Assert.That(b.Height, Is.EqualTo(30).Within(0.001));
        }

        [Test]
        public void Content_box_default_expands_total_width_for_padding() {
            // CSS Basic User Interface §4.1: `box-sizing` defaults to
            // `content-box`, so `width: 200` is the content-area size and
            // padding adds to it. Outer border-box width = 200 + 10 + 10.
            var (root, _, _) = Build(
                "<div style=\"width:200px;padding:10px\"></div>", null, 800);
            var div = FindFirstBlock(root, "div");
            Assert.That(div.Width, Is.EqualTo(220).Within(0.001));
            Assert.That(div.PaddingLeft, Is.EqualTo(10).Within(0.001));
            Assert.That(div.ContentWidth, Is.EqualTo(200).Within(0.001));
        }

        [Test]
        public void Border_box_keeps_total_width_when_padding_added() {
            // Opt-in border-box: `width` is the outer rect and padding eats
            // into the content area.
            var (root, _, _) = Build(
                "<div style=\"box-sizing:border-box;width:200px;padding:10px\"></div>", null, 800);
            var div = FindFirstBlock(root, "div");
            Assert.That(div.Width, Is.EqualTo(200).Within(0.001));
            Assert.That(div.PaddingLeft, Is.EqualTo(10).Within(0.001));
            Assert.That(div.ContentWidth, Is.EqualTo(180).Within(0.001));
        }

        [Test]
        public void Padding_increases_interior_offset() {
            var (root, _, _) = Build(
                "<div style=\"width:200px;padding:10px\"><div style=\"height:30px\"></div></div>",
                null, 800);
            var outer = FindFirstBlock(root, "div");
            BlockBox inner = null;
            foreach (var c in outer.Children) if (c is BlockBox bb && !(c is AnonymousBlockBox) && bb.Element?.TagName == "div") inner = bb;
            Assert.That(inner.X, Is.EqualTo(10).Within(0.001));
            Assert.That(inner.Y, Is.EqualTo(10).Within(0.001));
        }

        [Test]
        public void Margin_offsets_position() {
            var (root, _, _) = Build(
                "<div style=\"margin-top:20px;margin-left:15px;height:30px;width:100px\"></div>",
                null, 800);
            var div = FindFirstBlock(root, "div");
            // With the HTML5 fragment wrapper (Document > <html> > <body> > <div>)
            // the div's margin-top collapses through the wrapper onto the body,
            // so the div's own X/Y is relative to the body's content box. The
            // semantic check is "the div ends up offset by 15/20 from the page
            // origin" — use absolute coordinates.
            var (absX, absY) = AbsoluteOrigin(div);
            Assert.That(absX, Is.EqualTo(15).Within(0.001));
            Assert.That(absY, Is.EqualTo(20).Within(0.001));
        }

        [Test]
        public void Explicit_width_applies() {
            var (root, _, _) = Build("<div style=\"width:300px\"></div>", null, 800);
            var div = FindFirstBlock(root, "div");
            Assert.That(div.Width, Is.EqualTo(300).Within(0.001));
        }

        [Test]
        public void Explicit_height_applies_overriding_content() {
            var (root, _, _) = Build("<div style=\"height:200px\"><div style=\"height:30px\"></div></div>", null, 800);
            var outer = FindFirstBlock(root, "div");
            Assert.That(outer.Height, Is.EqualTo(200).Within(0.001));
        }

        [Test]
        public void Percent_width_against_parent() {
            var (root, _, _) = Build(
                "<div style=\"width:400px\"><div style=\"width:50%\"></div></div>",
                null, 800);
            var outer = FindFirstBlock(root, "div");
            BlockBox inner = null;
            foreach (var c in outer.Children) if (c is BlockBox bb && !(c is AnonymousBlockBox)) inner = bb;
            Assert.That(inner.Width, Is.EqualTo(200).Within(0.001));
        }

        [Test]
        public void Percent_height_uses_parent_content_box_not_border_box() {
            const string css = @"
                #outer {
                    width: 200px;
                    height: 12px;
                    border-top-style: solid; border-top-width: 1px;
                    border-right-style: solid; border-right-width: 1px;
                    border-bottom-style: solid; border-bottom-width: 1px;
                    border-left-style: solid; border-left-width: 1px;
                }
                #fill { height: 100%; }
            ";
            var (root, _, _) = Build("<div id=\"outer\"><div id=\"fill\"></div></div>", css, 800);
            var outer = FindById(root, "outer");
            var fill = FindById(root, "fill");

            Assert.That(outer.Height, Is.EqualTo(14).Within(0.001));
            Assert.That(outer.ContentHeight, Is.EqualTo(12).Within(0.001));
            Assert.That(fill.Y, Is.EqualTo(1).Within(0.001));
            Assert.That(fill.Height, Is.EqualTo(12).Within(0.001));
        }

        [Test]
        public void Min_width_clamps_smaller_values() {
            var (root, _, _) = Build(
                "<div style=\"width:50px;min-width:100px\"></div>", null, 800);
            var div = FindFirstBlock(root, "div");
            Assert.That(div.Width, Is.EqualTo(100).Within(0.001));
        }

        [Test]
        public void Max_width_clamps_larger_values() {
            var (root, _, _) = Build(
                "<div style=\"width:500px;max-width:300px\"></div>", null, 800);
            var div = FindFirstBlock(root, "div");
            Assert.That(div.Width, Is.EqualTo(300).Within(0.001));
        }

        [Test]
        public void Border_increases_visual_size_under_border_box() {
            // Use longhands; v1 layout reads only longhand border-*-style/-width.
            // Pin `box-sizing: border-box` explicitly so this test stays
            // independent of the engine default (now `content-box` per spec).
            var (root, _, _) = Build(
                "<div style=\"box-sizing:border-box;width:200px;border-top-style:solid;border-top-width:5px;border-right-style:solid;border-right-width:5px;border-bottom-style:solid;border-bottom-width:5px;border-left-style:solid;border-left-width:5px\"></div>",
                null, 800);
            var div = FindFirstBlock(root, "div");
            Assert.That(div.Width, Is.EqualTo(200).Within(0.001));
            Assert.That(div.BorderLeft, Is.EqualTo(5).Within(0.001));
            Assert.That(div.ContentWidth, Is.EqualTo(190).Within(0.001));
        }

        [Test]
        public void Nested_blocks_compute_positions_correctly() {
            var (root, _, _) = Build(
                "<div style=\"padding:10px\"><div style=\"height:20px\"></div><div style=\"height:30px\"></div></div>",
                null, 800);
            var outer = FindFirstBlock(root, "div");
            BlockBox a = null, b = null;
            foreach (var c in outer.Children) {
                if (c is BlockBox bb && !(c is AnonymousBlockBox)) {
                    if (a == null) a = bb;
                    else if (b == null) b = bb;
                }
            }
            Assert.That(a.Y, Is.EqualTo(10).Within(0.001));
            Assert.That(b.Y, Is.EqualTo(30).Within(0.001));
            Assert.That(outer.Height, Is.EqualTo(70).Within(0.001));
        }

        [Test]
        public void Auto_margins_center_when_width_fixed() {
            var (root, _, _) = Build(
                "<div style=\"width:200px;margin-left:auto;margin-right:auto\"></div>",
                null, 800);
            var div = FindFirstBlock(root, "div");
            Assert.That(div.MarginLeft, Is.EqualTo(300).Within(0.001));
            Assert.That(div.MarginRight, Is.EqualTo(300).Within(0.001));
        }

        [Test]
        public void Auto_margins_center_auto_width_clamped_by_max_width() {
            // CSS 2.1 §10.3.3: an auto width clamped by max-width re-solves the
            // over-constrained equation handing the freed space to auto inline
            // margins — the canonical `width:auto; max-width:X; margin:0 auto`
            // page-centering pattern (regression: weva-landing sections sat at
            // X=0 instead of centered). Container 1000, max-width 600 => 400
            // free, 200 to each margin.
            var (root, _, _) = Build(
                "<div style=\"max-width:600px;margin-left:auto;margin-right:auto\"></div>",
                null, 1000);
            var div = FindFirstBlock(root, "div");
            Assert.That(div.Width, Is.EqualTo(600).Within(0.001));
            Assert.That(div.MarginLeft, Is.EqualTo(200).Within(0.001));
            Assert.That(div.MarginRight, Is.EqualTo(200).Within(0.001));
        }

        [Test]
        public void Auto_width_without_max_width_fills_container_not_centered() {
            // A plain auto width with auto margins fills the container (auto
            // margins resolve to 0) — it must NOT be "centered" (that would
            // wrongly shrink it). Guards the §10.3.3 fix against over-firing.
            var (root, _, _) = Build(
                "<div style=\"margin-left:auto;margin-right:auto\"></div>",
                null, 1000);
            var div = FindFirstBlock(root, "div");
            Assert.That(div.Width, Is.EqualTo(1000).Within(0.001));
            Assert.That(div.MarginLeft, Is.EqualTo(0).Within(0.001));
            Assert.That(div.MarginRight, Is.EqualTo(0).Within(0.001));
        }

        [Test]
        public void Margin_collapses_between_adjacent_block_siblings() {
            // CSS Box Model §8.3.1: adjacent block siblings' vertical margins
            // collapse. With margin-bottom:20px and margin-top:30px, the gap is
            // max(20, 30) = 30, not 50. Old (uncollapsed) value was 80.
            var (root, _, _) = Build(
                "<div id=\"a\" style=\"margin-bottom:20px;height:30px\"></div><div id=\"b\" style=\"margin-top:30px;height:30px\"></div>",
                null, 800);
            int seen = 0;
            BlockBox a = null, b = null;
            foreach (var box in AllBoxes(root)) {
                if (box is BlockBox bb && !(box is AnonymousBlockBox) && bb.Element?.TagName == "div") {
                    seen++;
                    if (seen == 1) a = bb;
                    else if (seen == 2) b = bb;
                }
            }
            // a.Y = 0; a bottom = 30; collapsed gap = max(20, 30) = 30 => b.Y = 60.
            Assert.That(b.Y, Is.EqualTo(60).Within(0.001));
        }

        [Test]
        public void Min_height_extends_block_height() {
            var (root, _, _) = Build("<div style=\"min-height:80px\"></div>", null, 800);
            var div = FindFirstBlock(root, "div");
            Assert.That(div.Height, Is.EqualTo(80).Within(0.001));
        }

        [Test]
        public void Border_box_honored_when_box_sizing_stored_as_non_keyword_parsed_value() {
            // Regression for the IsBorderBox dispatch gap: GetParsed only
            // recognised CssKeyword / CssIdentifier shapes, so a slot whose
            // parsed cache held any other CssValue (e.g. CssString from a
            // typed setter that bypassed the keyword parser) silently fell
            // back to content-box. The fix added a raw-string fallback.
            var doc = HtmlParser.Parse("<div style=\"width:200px;padding:10px\"></div>");
            var sheets = new List<OriginatedStylesheet> { UA(BuiltinUserAgent) };
            var engine = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;

            var div = FindByTag(doc, "div");
            Assert.That(div, Is.Not.Null);
            var style = styles[div];
            style.SetParsed(CssProperties.BoxSizingId, new CssString("border-box", "border-box"));
            var parsed = style.GetParsed(CssProperties.BoxSizingId);
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

            var box = FindFirstBlock(root, "div");
            Assert.That(box.Width, Is.EqualTo(200).Within(0.001));
            Assert.That(box.ContentWidth, Is.EqualTo(180).Within(0.001));
        }

        [Test]
        public void Inline_style_background_does_not_disrupt_class_height() {
            var css = ".meter { width: 100%; height: 3px; border-radius: 999px; }";
            var html = "<div class=\"meter\" style=\"background: linear-gradient(to right, #ffd22f 50%, rgba(255,255,255,0.1) 50%)\"></div>";
            var (root, styles, _) = Build(html, css, viewportWidth: 400);

            var box = FindFirstBlock(root, "div");
            Assert.That(box, Is.Not.Null);
            System.Console.WriteLine($"box H={box.Height:F1} W={box.Width:F1} style height={box.Style?.Get("height")}");
            Assert.That(box.Height, Is.EqualTo(3).Within(0.5),
                "inline style='background:...' should not override class height:3px");
        }

        [Test]
        public void Template_element_is_display_none() {
            var (root, styles, _) = Build(
                "<div><template><p>hidden</p></template><p>visible</p></div>",
                null, viewportWidth: 400);
            Element tmpl = null;
            foreach (var bx in AllBoxes(root)) {
                if (bx.Element?.TagName == "template") { tmpl = bx.Element; break; }
            }
            if (tmpl == null) {
                System.Console.WriteLine("[TEMPLATE] no box created for <template> — correct (display:none skipped it)");
            } else {
                var style = styles.ContainsKey(tmpl) ? styles[tmpl] : null;
                System.Console.WriteLine($"[TEMPLATE] tag={tmpl.TagName} display={style?.Get("display")} — BUG: should not have a box");
                Assert.Fail("template element should not create a box (display: none)");
            }
        }

        [Test]
        public void Width_100pct_in_column_flex_resolves_against_container() {
            var css = @"
                .card {
                    width: 140px;
                    display: flex;
                    flex-direction: column;
                    align-items: center;
                    padding: 8px;
                }
                .track { width: 100%; height: 3px; }
            ";
            var html = "<div class=\"card\"><div class=\"track\">X</div></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 800);
            var card = FindFirstBlock(root, "div");
            Assert.That(card, Is.Not.Null);
            BlockBox track = null;
            foreach (var b in AllBoxes(card)) {
                if (b is BlockBox bb && bb.Element?.ClassName == "track") { track = bb; break; }
            }
            Assert.That(track, Is.Not.Null);
            double expectedTrackW = card.ContentWidth;
            System.Console.WriteLine($"card W={card.Width:F1} contentW={card.ContentWidth:F1} track W={track.Width:F1}");
            Assert.That(track.Width, Is.EqualTo(expectedTrackW).Within(1),
                "width:100% should resolve against flex container content width, not viewport");
        }
    }
}
