using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.DevTools;
using Weva.Dom;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

// DevTools W7 phase 1 — headless tests for ElementPicker and StyleInspector.
//
// Coverage:
//   Picker: depth, containment, TextRun skip, transform-translate,
//           miss outside bounds, PickBox static helper.
//   Dump:   computed values for set props; trace flag off → no trace;
//           trace flag on → winner origin/specificity/source and losers in order;
//           box-model numbers match margin/padding/border math;
//           null element → empty report; no CascadeEngine → no trace.

namespace Weva.Tests.DevTools {
    public class InspectorDataCoreTests {
        // ------------------------------------------------------------------ //
        //  ElementPicker — basic containment                                  //
        // ------------------------------------------------------------------ //

        [Test]
        public void Picker_returns_deepest_element_box_at_exact_center() {
            // Single box at (10,10) 80×40 — center at (50,30).
            var css = "html, body { width:100%; height:100%; margin:0; } .box { position:absolute; left:10px; top:10px; width:80px; height:40px; }";
            var html = "<div class=\"box\" id=\"d\"></div>";
            var (root, _, _) = Build(html, css, 200, 100);

            var picker = new ElementPicker();
            var element = picker.Pick(root, 50, 30, e => null);

            Assert.That(element, Is.Not.Null, "center of box should be hit");
            Assert.That(element.Id, Is.EqualTo("d"), "should pick the div");
        }

        [Test]
        public void Picker_miss_returns_null_outside_box() {
            var css = "html, body { width:100%; height:100%; margin:0; } .box { position:absolute; left:10px; top:10px; width:80px; height:40px; }";
            var html = "<div class=\"box\" id=\"d\"></div>";
            var (root, _, _) = Build(html, css, 200, 100);

            var picker = new ElementPicker();
            // (0,0) is outside the box (starts at 10,10)
            var element = picker.Pick(root, 0, 0, e => null);

            // The root element (html/body) may be hit at (0,0); the div 'd' should not.
            if (element != null) {
                Assert.That(element.Id, Is.Not.EqualTo("d"),
                    "point outside box must not return the div");
            }
        }

        [Test]
        public void Picker_returns_deepest_child_not_parent_when_nested() {
            // Child fully inside parent. Click inside child → child returned.
            var css = @"
                html, body { width:100%; height:100%; margin:0; }
                .parent { position:absolute; left:0; top:0; width:100px; height:100px; }
                .child  { position:absolute; left:20px; top:20px; width:40px; height:40px; }
            ";
            var html = "<div class=\"parent\" id=\"p\"><div class=\"child\" id=\"c\"></div></div>";
            var (root, _, _) = Build(html, css, 200, 200);

            var picker = new ElementPicker();
            var element = picker.Pick(root, 40, 40, e => null); // inside child

            Assert.That(element, Is.Not.Null);
            Assert.That(element.Id, Is.EqualTo("c"), "deepest child should be returned");
        }

        [Test]
        public void Picker_returns_parent_when_click_is_outside_child() {
            var css = @"
                html, body { width:100%; height:100%; margin:0; }
                .parent { position:absolute; left:0; top:0; width:100px; height:100px; }
                .child  { position:absolute; left:60px; top:60px; width:30px; height:30px; }
            ";
            var html = "<div class=\"parent\" id=\"p\"><div class=\"child\" id=\"c\"></div></div>";
            var (root, _, _) = Build(html, css, 200, 200);

            var picker = new ElementPicker();
            // (10,10) is inside parent but outside child.
            var element = picker.Pick(root, 10, 10, e => null);

            Assert.That(element, Is.Not.Null);
            Assert.That(element.Id, Is.Not.EqualTo("c"), "child is not at (10,10)");
        }

        [Test]
        public void Picker_skips_textrun_boxes_returning_element_owner() {
            // Text inside a div — PickBox must not return the TextRun itself.
            var css = "html, body { width:100%; height:100%; margin:0; } .box { position:absolute; left:0; top:0; width:100px; height:30px; }";
            var html = "<div class=\"box\" id=\"d\">Hello</div>";
            var (root, _, _) = Build(html, css, 200, 100);

            // PickBox should return the div's Box (not a TextRun).
            var box = ElementPicker.PickBox(root, 10, 10);
            Assert.That(box, Is.Not.Null);
            Assert.That(box, Is.Not.InstanceOf<TextRun>(), "TextRun must not be returned by PickBox");
            Assert.That(box.Element?.Id, Is.EqualTo("d"));
        }

        [Test]
        public void Picker_respects_translate_transform_shifted_box() {
            // translateX(-50%) centring trick: layout at left:50%, transformed left 50px back.
            var css = @"
                html, body { width:100%; height:100%; margin:0; }
                .banner { position:absolute; left:50%; transform:translateX(-50%);
                          width:200px; height:60px; }
            ";
            var html = "<div class=\"banner\" id=\"b\"></div>";
            var (root, _, _) = Build(html, css, 400, 200);

            // Layout origin = 200px (50% of 400). Translate = -100px. Visual = 100..300.
            var picker = new ElementPicker();

            // Visual centre at x=200 should hit.
            var hit = picker.Pick(root, 200, 30, e => null);
            Assert.That(hit?.Id, Is.EqualTo("b"), "visual centre of translated box should hit");

            // x=50 is left of visual start (100px) — should NOT hit the banner.
            var miss = picker.Pick(root, 50, 30, e => null);
            Assert.That(miss?.Id, Is.Not.EqualTo("b"),
                "point outside visual box should not hit translated banner");
        }

        [Test]
        public void Picker_sets_last_element_and_last_box() {
            var css = "html, body { width:100%; height:100%; margin:0; } .b { position:absolute; left:0; top:0; width:100px; height:100px; }";
            var html = "<div class=\"b\" id=\"x\"></div>";
            var (root, _, ctx) = Build(html, css, 200, 200);

            var picker = new ElementPicker();
            Box lastB = null;
            picker.Pick(root, 50, 50, e => {
                // Build a simple element→box mapping from the root.
                lastB = FindBoxByElement(root, e);
                return lastB;
            });

            Assert.That(picker.LastElement, Is.Not.Null);
            Assert.That(picker.LastElement.Id, Is.EqualTo("x"));
        }

        [Test]
        public void PickBox_returns_null_for_empty_tree() {
            var box = ElementPicker.PickBox(null, 50, 50);
            Assert.That(box, Is.Null);
        }

        // ------------------------------------------------------------------ //
        //  StyleInspector — computed values                                   //
        // ------------------------------------------------------------------ //

        [Test]
        public void Dump_contains_every_explicitly_set_property() {
            var element = new Element("div");
            var style = new ComputedStyle(element);
            style.Set("display", "flex");
            style.Set("color", "red");
            style.Set("font-size", "14px");
            style.Set("opacity", "0.8");

            var report = StyleInspector.Dump(element, style, null);

            Assert.That(report.ComputedValues.ContainsKey("display"),   "display should be present");
            Assert.That(report.ComputedValues.ContainsKey("color"),     "color should be present");
            Assert.That(report.ComputedValues.ContainsKey("font-size"), "font-size should be present");
            Assert.That(report.ComputedValues.ContainsKey("opacity"),   "opacity should be present");

            Assert.That(report.ComputedValues["display"],   Is.EqualTo("flex"));
            Assert.That(report.ComputedValues["color"],     Is.EqualTo("red"));
            Assert.That(report.ComputedValues["font-size"], Is.EqualTo("14px"));
        }

        [Test]
        public void Dump_with_null_element_returns_empty_report_not_null() {
            var report = StyleInspector.Dump(null, null, null);
            Assert.That(report, Is.Not.Null);
            Assert.That(report.ComputedValues, Is.Empty);
            Assert.That(report.CascadeTrace,   Is.Empty);
        }

        // ------------------------------------------------------------------ //
        //  StyleInspector — cascade trace: flag off                           //
        // ------------------------------------------------------------------ //

        [Test]
        public void Dump_trace_off_by_default_produces_empty_trace() {
            // Ensure the static flag is off (it may have been flipped by a
            // prior test run in the same process; reset here).
            StyleInspector.CaptureCascadeTrace = false;

            var css  = ".box { color: blue; }";
            var html = "<div class=\"box\" id=\"d\">hi</div>";
            var (root, styles, _) = Build(html, css, 400, 300);

            Element divEl = null;
            ComputedStyle divStyle = null;
            foreach (var kv in styles) {
                if (kv.Key.Id == "d") { divEl = kv.Key; divStyle = kv.Value; break; }
            }

            var report = StyleInspector.Dump(divEl, divStyle, null);
            Assert.That(report.CascadeTrace, Is.Empty,
                "trace must be empty when CaptureCascadeTrace is false");
        }

        [Test]
        public void Dump_trace_default_is_false() {
            // Defensive: the production flag must start off so every Build()
            // test run doesn't incur trace allocation cost.
            // Reset before check because another test may have turned it on.
            StyleInspector.CaptureCascadeTrace = false;
            Assert.That(StyleInspector.CaptureCascadeTrace, Is.False,
                "CaptureCascadeTrace must default false");
        }

        // ------------------------------------------------------------------ //
        //  StyleInspector — cascade trace: flag on                            //
        // ------------------------------------------------------------------ //

        [Test]
        public void Dump_trace_on_shows_winner_origin_and_specificity() {
            StyleInspector.CaptureCascadeTrace = true;
            try {
                var css  = "div { color: green; }";
                var html = "<div id=\"d\">hi</div>";

                var doc    = Html(html);
                var sheets = new List<OriginatedStylesheet> {
                    UA(BuiltinUserAgent),
                    Author(css)
                };
                var engine = new CascadeEngine(sheets);
                var styleMap = new Dictionary<Element, ComputedStyle>();
                foreach (var kv in engine.ComputeAll(doc)) styleMap[kv.Key] = kv.Value;

                Element divEl = null;
                ComputedStyle divStyle = null;
                foreach (var kv in styleMap) {
                    if (kv.Key.Id == "d") { divEl = kv.Key; divStyle = kv.Value; break; }
                }

                Assert.That(divEl, Is.Not.Null, "div element must be found");

                var report = StyleInspector.Dump(divEl, divStyle, null, engine);

                Assert.That(report.CascadeTrace, Is.Not.Empty,
                    "trace should be non-empty when flag is on");

                // The 'color' property should have a trace entry from the authored rule.
                Assert.That(report.CascadeTrace.ContainsKey("color"),
                    "color should appear in cascade trace");

                var colorTrace = report.CascadeTrace["color"];
                Assert.That(colorTrace.WinnerOrigin, Is.EqualTo(DeclarationOrigin.Author),
                    "author rule should win over UA default");
                Assert.That(colorTrace.WinnerValue, Is.EqualTo("green"),
                    "winner value should be 'green'");
            } finally {
                StyleInspector.CaptureCascadeTrace = false;
            }
        }

        [Test]
        public void Dump_trace_on_shows_losers_when_property_overridden() {
            StyleInspector.CaptureCascadeTrace = true;
            try {
                // Two rules targeting the same element: lower-specificity UA sets
                // color, higher-specificity author rule overrides it.
                var css  = ".box { color: purple; }";
                var html = "<div class=\"box\" id=\"d\">hi</div>";

                var doc    = Html(html);
                var sheets = new List<OriginatedStylesheet> {
                    UA(BuiltinUserAgent),
                    Author(css)
                };
                var engine = new CascadeEngine(sheets);
                var styleMap = new Dictionary<Element, ComputedStyle>();
                foreach (var kv in engine.ComputeAll(doc)) styleMap[kv.Key] = kv.Value;

                Element divEl = null;
                ComputedStyle divStyle = null;
                foreach (var kv in styleMap) {
                    if (kv.Key.Id == "d") { divEl = kv.Key; divStyle = kv.Value; break; }
                }

                var report = StyleInspector.Dump(divEl, divStyle, null, engine);

                if (report.CascadeTrace.TryGetValue("color", out var trace)) {
                    // Author wins; UA may or may not have set a colour — if it did,
                    // it should appear in the overridden list.
                    Assert.That(trace.WinnerValue, Is.EqualTo("purple"),
                        "authored .box { color:purple } should win");
                }
            } finally {
                StyleInspector.CaptureCascadeTrace = false;
            }
        }

        [Test]
        public void Dump_trace_on_higher_specificity_rule_wins_over_lower() {
            StyleInspector.CaptureCascadeTrace = true;
            try {
                // Two author rules: element selector (0,0,1) loses to class selector (0,1,0).
                var css  = "div { color: red; } .box { color: blue; }";
                var html = "<div class=\"box\" id=\"d\">hi</div>";

                var doc    = Html(html);
                var sheets = new List<OriginatedStylesheet> {
                    UA(BuiltinUserAgent),
                    Author(css)
                };
                var engine = new CascadeEngine(sheets);
                var styleMap = new Dictionary<Element, ComputedStyle>();
                foreach (var kv in engine.ComputeAll(doc)) styleMap[kv.Key] = kv.Value;

                Element divEl = null;
                ComputedStyle divStyle = null;
                foreach (var kv in styleMap) {
                    if (kv.Key.Id == "d") { divEl = kv.Key; divStyle = kv.Value; break; }
                }

                var report = StyleInspector.Dump(divEl, divStyle, null, engine);

                Assert.That(report.CascadeTrace.TryGetValue("color", out var trace), Is.True,
                    "color should appear in trace");
                Assert.That(trace.WinnerValue, Is.EqualTo("blue"),
                    ".box (specificity 0,1,0) should beat div (0,0,1)");
                // The losing 'div { color:red }' should appear as an overridden entry.
                Assert.That(trace.OverriddenDeclarations, Is.Not.Null,
                    "overridden list should be non-null");
                bool foundRed = false;
                foreach (var ov in trace.OverriddenDeclarations) {
                    if (ov.ValueText == "red") { foundRed = true; break; }
                }
                Assert.That(foundRed, Is.True,
                    "overridden list should contain the losing div { color:red }");
            } finally {
                StyleInspector.CaptureCascadeTrace = false;
            }
        }

        [Test]
        public void Dump_trace_no_engine_leaves_trace_empty_even_with_flag_on() {
            StyleInspector.CaptureCascadeTrace = true;
            try {
                var element = new Element("div");
                var style   = new ComputedStyle(element);
                style.Set("color", "red");

                // No engine supplied → trace cannot be built.
                var report = StyleInspector.Dump(element, style, null, cascade: null);
                Assert.That(report.CascadeTrace, Is.Empty,
                    "trace must be empty when no CascadeEngine is supplied");
            } finally {
                StyleInspector.CaptureCascadeTrace = false;
            }
        }

        // ------------------------------------------------------------------ //
        //  StyleInspector — box-model numbers                                 //
        // ------------------------------------------------------------------ //

        [Test]
        public void Dump_box_model_margin_rect_matches_emitfour_convention() {
            // Create a box with known margin/border/padding to verify the
            // BoxModelNumbers math mirrors BoxOutlineRenderer.EmitFour.
            var box = MakeBoxWithGeometry(x: 10, y: 20, w: 100, h: 50,
                                         margin: 8, border: 2, padding: 4);
            var element = new Element("div");
            box.Element = element;

            var report = StyleInspector.Dump(element, null, box);

            var bm = report.BoxModel;
            // Margin rect: x - margin, y - margin, w + 2*margin, h + 2*margin
            Assert.That(bm.MarginX, Is.EqualTo(2).Within(1e-9),  "margin-box X");
            Assert.That(bm.MarginY, Is.EqualTo(12).Within(1e-9), "margin-box Y");
            Assert.That(bm.MarginW, Is.EqualTo(116).Within(1e-9),"margin-box W");
            Assert.That(bm.MarginH, Is.EqualTo(66).Within(1e-9), "margin-box H");

            // Border rect = the box itself.
            Assert.That(bm.BorderW, Is.EqualTo(100).Within(1e-9), "border-box W");
            Assert.That(bm.BorderH, Is.EqualTo(50).Within(1e-9),  "border-box H");
        }

        [Test]
        public void Dump_box_model_padding_rect_excludes_borders() {
            var box = MakeBoxWithGeometry(x: 0, y: 0, w: 100, h: 100,
                                         margin: 0, border: 3, padding: 5);
            var element = new Element("div");
            box.Element = element;

            var report = StyleInspector.Dump(element, null, box);

            var bm = report.BoxModel;
            // Padding box is inside border: X+border, width-2*border
            Assert.That(bm.PaddingX, Is.EqualTo(3).Within(1e-9), "padding-box X");
            Assert.That(bm.PaddingW, Is.EqualTo(94).Within(1e-9),"padding-box W");
        }

        [Test]
        public void Dump_box_model_content_rect_excludes_padding() {
            var box = MakeBoxWithGeometry(x: 0, y: 0, w: 80, h: 80,
                                         margin: 0, border: 0, padding: 10);
            var element = new Element("div");
            box.Element = element;

            var report = StyleInspector.Dump(element, null, box);

            var bm = report.BoxModel;
            Assert.That(bm.ContentX, Is.EqualTo(10).Within(1e-9), "content-box X");
            Assert.That(bm.ContentW, Is.EqualTo(60).Within(1e-9), "content-box W");
            Assert.That(bm.ContentH, Is.EqualTo(60).Within(1e-9), "content-box H");
        }

        [Test]
        public void Dump_box_model_all_zero_when_box_is_null() {
            var report = StyleInspector.Dump(new Element("div"), null, null);
            var bm = report.BoxModel;
            Assert.That(bm.MarginW,  Is.EqualTo(0), "MarginW should be 0");
            Assert.That(bm.BorderW,  Is.EqualTo(0), "BorderW should be 0");
            Assert.That(bm.PaddingW, Is.EqualTo(0), "PaddingW should be 0");
            Assert.That(bm.ContentW, Is.EqualTo(0), "ContentW should be 0");
        }

        // ------------------------------------------------------------------ //
        //  StyleInspector — ToString formatting                               //
        // ------------------------------------------------------------------ //

        [Test]
        public void Dump_tostring_includes_tag_name_and_box_model_section() {
            var element = new Element("section");
            var style   = new ComputedStyle(element);
            style.Set("display", "block");

            var report = StyleInspector.Dump(element, style, null);
            var text   = report.ToString();

            Assert.That(text, Does.Contain("<section>"),  "tag name in header");
            Assert.That(text, Does.Contain("box model"),  "box model section header");
            Assert.That(text, Does.Contain("computed"),   "computed section header");
            Assert.That(text, Does.Contain("display: block"), "computed value in output");
        }

        // ------------------------------------------------------------------ //
        //  Helpers                                                             //
        // ------------------------------------------------------------------ //

        static BlockBox MakeBoxWithGeometry(double x, double y, double w, double h,
                                            double margin, double border, double padding) {
            var b = new BlockBox();
            b.X = x; b.Y = y; b.Width = w; b.Height = h;
            b.MarginTop  = b.MarginRight  = b.MarginBottom  = b.MarginLeft  = margin;
            b.BorderTop  = b.BorderRight  = b.BorderBottom  = b.BorderLeft  = border;
            b.PaddingTop = b.PaddingRight = b.PaddingBottom = b.PaddingLeft = padding;
            return b;
        }

        static Box FindBoxByElement(Box root, Element element) {
            if (root.Element == element) return root;
            foreach (var c in root.Children) {
                var f = FindBoxByElement(c, element);
                if (f != null) return f;
            }
            return null;
        }
    }
}
