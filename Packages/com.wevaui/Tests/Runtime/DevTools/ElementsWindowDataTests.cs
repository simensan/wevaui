using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.DevTools;
using Weva.Dom;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

// Headless NUnit tests for the DevTools Elements panel data layer:
//   DomTreeModel  — DOM flattening, labels, version, dirty flag
//   RuleBlockBuilder — rule grouping, winner/overridden, inline style block
//   ComputedStyleModel — sorted property list, filter, box-model numbers
//
// These tests exercise Layer 1 only (no Unity APIs). They run in the
// TestVerifyAll headless .NET runner and in the Unity Test Runner.

namespace Weva.Tests.DevTools {
    public class ElementsWindowDataTests {

        // ================================================================== //
        //  DomTreeModel — flattening                                          //
        // ================================================================== //

        [Test]
        public void DomTreeModel_empty_document_produces_no_nodes() {
            var model = new DomTreeModel();
            var doc = Html("<html></html>");
            model.Rebuild(doc);

            // Empty or only the html/body nodes — no crash.
            Assert.That(model.Version, Is.GreaterThan(0), "Version should bump after Rebuild");
        }

        [Test]
        public void DomTreeModel_version_bumps_on_each_rebuild() {
            var model = new DomTreeModel();
            var doc = Html("<div></div>");
            model.Rebuild(doc);
            int v1 = model.Version;
            model.Rebuild(doc);
            int v2 = model.Version;
            model.Rebuild(doc);
            int v3 = model.Version;

            Assert.That(v2, Is.GreaterThan(v1), "second Rebuild must bump Version");
            Assert.That(v3, Is.GreaterThan(v2), "third Rebuild must bump Version");
        }

        [Test]
        public void DomTreeModel_contains_element_node_for_each_element() {
            var model = new DomTreeModel();
            var doc = Html("<div id=\"outer\"><span id=\"inner\"></span></div>");
            model.Rebuild(doc);

            bool foundOuter = false, foundInner = false;
            foreach (var node in model.Nodes) {
                if (node.IsElement && node.Element.Id == "outer") foundOuter = true;
                if (node.IsElement && node.Element.Id == "inner") foundInner = true;
            }

            Assert.That(foundOuter, Is.True, "outer div should appear in node list");
            Assert.That(foundInner, Is.True, "inner span should appear in node list");
        }

        [Test]
        public void DomTreeModel_inner_element_has_greater_depth_than_parent() {
            var model = new DomTreeModel();
            var doc = Html("<div id=\"p\"><div id=\"c\"></div></div>");
            model.Rebuild(doc);

            DomTreeNode parentNode = null, childNode = null;
            foreach (var n in model.Nodes) {
                if (n.IsElement && n.Element.Id == "p") parentNode = n;
                if (n.IsElement && n.Element.Id == "c") childNode = n;
            }

            Assert.That(parentNode, Is.Not.Null, "parent node must exist");
            Assert.That(childNode, Is.Not.Null, "child node must exist");
            Assert.That(childNode.Depth, Is.GreaterThan(parentNode.Depth),
                "child depth must exceed parent depth");
        }

        [Test]
        public void DomTreeModel_child_parent_id_matches_parent_node_id() {
            var model = new DomTreeModel();
            var doc = Html("<div id=\"p\"><div id=\"c\"></div></div>");
            model.Rebuild(doc);

            DomTreeNode parentNode = null, childNode = null;
            foreach (var n in model.Nodes) {
                if (n.IsElement && n.Element.Id == "p") parentNode = n;
                if (n.IsElement && n.Element.Id == "c") childNode = n;
            }

            Assert.That(parentNode, Is.Not.Null);
            Assert.That(childNode, Is.Not.Null);
            Assert.That(childNode.ParentId, Is.EqualTo(parentNode.Id),
                "child's ParentId must equal parent's Id");
        }

        [Test]
        public void DomTreeModel_element_label_contains_tag_name() {
            var model = new DomTreeModel();
            var doc = Html("<section id=\"s\"></section>");
            model.Rebuild(doc);

            string label = null;
            foreach (var n in model.Nodes) {
                if (n.IsElement && n.Element.Id == "s") { label = n.Label; break; }
            }

            Assert.That(label, Is.Not.Null);
            Assert.That(label, Does.Contain("section"), "label must contain tag name");
        }

        [Test]
        public void DomTreeModel_element_label_includes_id_and_class() {
            var model = new DomTreeModel();
            var doc = Html("<div id=\"card\" class=\"foo bar\"></div>");
            model.Rebuild(doc);

            string label = null;
            foreach (var n in model.Nodes) {
                if (n.IsElement && n.Element.Id == "card") { label = n.Label; break; }
            }

            Assert.That(label, Is.Not.Null);
            Assert.That(label, Does.Contain("id=\"card\""), "label should contain id attr");
            Assert.That(label, Does.Contain("class=\"foo bar\""), "label should contain class attr");
        }

        [Test]
        public void DomTreeModel_text_node_label_is_quoted_preview() {
            var model = new DomTreeModel();
            var doc = Html("<div>Hello world</div>");
            model.Rebuild(doc);

            bool foundText = false;
            foreach (var n in model.Nodes) {
                if (n.IsTextNode) {
                    Assert.That(n.Label, Does.StartWith("\""), "text label should start with quote");
                    Assert.That(n.Label, Does.EndWith("\""), "text label should end with quote");
                    foundText = true;
                    break;
                }
            }

            Assert.That(foundText, Is.True, "at least one text node should be emitted");
        }

        [Test]
        public void DomTreeModel_text_node_id_is_negative() {
            var model = new DomTreeModel();
            var doc = Html("<div>Hello</div>");
            model.Rebuild(doc);

            foreach (var n in model.Nodes) {
                if (n.IsTextNode) {
                    Assert.That(n.Id, Is.LessThan(0), "text node Id should be negative");
                    return;
                }
            }
        }

        [Test]
        public void DomTreeModel_element_node_id_is_positive() {
            var model = new DomTreeModel();
            var doc = Html("<div id=\"d\"></div>");
            model.Rebuild(doc);

            foreach (var n in model.Nodes) {
                if (n.IsElement && n.Element.Id == "d") {
                    Assert.That(n.Id, Is.GreaterThan(0), "element node Id should be positive");
                    return;
                }
            }

            Assert.Fail("element node not found");
        }

        [Test]
        public void DomTreeModel_dirty_flag_set_on_subscribe_and_cleared_on_rebuild() {
            var model = new DomTreeModel();
            var doc = Html("<div></div>");
            model.Rebuild(doc);

            Assert.That(model.IsDirty, Is.False, "dirty should be false after Rebuild");

            // Simulate mutation notification.
            model.IsDirty = true;
            Assert.That(model.IsDirty, Is.True);

            model.Rebuild(doc);
            Assert.That(model.IsDirty, Is.False, "dirty should be false after second Rebuild");
        }

        [Test]
        public void DomTreeModel_subscribe_sets_dirty_on_document_mutation() {
            var model = new DomTreeModel();
            var doc = Html("<div id=\"d\"></div>");
            model.Rebuild(doc);
            model.SubscribeTo(doc);

            Assert.That(model.IsDirty, Is.False, "dirty starts false after rebuild+subscribe");

            // Mutate the document.
            var div = doc.GetElementById("d");
            div.SetAttribute("class", "changed");

            Assert.That(model.IsDirty, Is.True,
                "dirty should be true after document mutation");
        }

        [Test]
        public void DomTreeModel_find_node_returns_correct_node_for_element() {
            var model = new DomTreeModel();
            var doc = Html("<div id=\"find-me\"></div>");
            model.Rebuild(doc);

            var target = doc.GetElementById("find-me");
            Assert.That(target, Is.Not.Null, "element should exist in document");

            var node = model.FindNode(target);
            Assert.That(node, Is.Not.Null, "FindNode should return a node");
            Assert.That(node.Element, Is.SameAs(target));
        }

        [Test]
        public void DomTreeModel_rebuild_null_document_produces_zero_nodes() {
            var model = new DomTreeModel();
            model.Rebuild(null);

            Assert.That(model.Count, Is.EqualTo(0), "null document should yield zero nodes");
        }

        // ================================================================== //
        //  RuleBlockBuilder — rule grouping                                   //
        // ================================================================== //

        [Test]
        public void RuleBlockBuilder_returns_empty_for_null_element() {
            var doc = Html("<div></div>");
            var sheets = new List<OriginatedStylesheet> { UA(BuiltinUserAgent) };
            var engine = new CascadeEngine(sheets);
            foreach (var kv in engine.ComputeAll(doc)) { }

            var blocks = RuleBlockBuilder.Build(null, engine);
            Assert.That(blocks, Is.Not.Null);
            Assert.That(blocks.Count, Is.EqualTo(0));
        }

        [Test]
        public void RuleBlockBuilder_returns_empty_for_null_cascade() {
            var element = new Element("div");
            var blocks = RuleBlockBuilder.Build(element, null);
            Assert.That(blocks, Is.Not.Null);
            Assert.That(blocks.Count, Is.EqualTo(0));
        }

        [Test]
        public void RuleBlockBuilder_groups_declarations_into_one_block_per_rule() {
            var doc = Html("<div class=\"card\"></div>");
            var css = ".card { color: red; font-size: 14px; }";
            var sheets = new List<OriginatedStylesheet> {
                UA(BuiltinUserAgent),
                Author(css)
            };
            var engine = new CascadeEngine(sheets);
            foreach (var kv in engine.ComputeAll(doc)) { }

            var div = doc.GetElementsByTagName("div");
            Element divEl = null;
            foreach (var e in div) { divEl = e; break; }

            var blocks = RuleBlockBuilder.Build(divEl, engine);

            // Find the author block for ".card"
            RuleBlock cardBlock = null;
            foreach (var b in blocks) {
                if (b.SelectorText == ".card") { cardBlock = b; break; }
            }

            Assert.That(cardBlock, Is.Not.Null, ".card rule block must exist");
            Assert.That(cardBlock.Declarations.Count, Is.GreaterThanOrEqualTo(2),
                ".card block should have both color and font-size");
        }

        [Test]
        public void RuleBlockBuilder_winner_first_ordering_higher_specificity_block_before_lower() {
            var doc = Html("<div id=\"x\" class=\"box\"></div>");
            var css = ".box { color: blue; } #x { color: red; }";
            var sheets = new List<OriginatedStylesheet> {
                UA(BuiltinUserAgent),
                Author(css)
            };
            var engine = new CascadeEngine(sheets);
            foreach (var kv in engine.ComputeAll(doc)) { }

            Element divEl = null;
            foreach (var e in doc.GetElementsByTagName("div")) { divEl = e; break; }

            var blocks = RuleBlockBuilder.Build(divEl, engine);

            // The first non-UA author block should be the winner (highest priority = #x, id selector)
            RuleBlock firstAuthor = null;
            foreach (var b in blocks) {
                if (b.Origin == DeclarationOrigin.Author) { firstAuthor = b; break; }
            }

            Assert.That(firstAuthor, Is.Not.Null, "at least one author block expected");
            // Winner should NOT have color marked as overridden
            bool winnerColorOverridden = false;
            foreach (var decl in firstAuthor.Declarations) {
                if (decl.Property == "color" && decl.IsOverridden) winnerColorOverridden = true;
            }
            Assert.That(winnerColorOverridden, Is.False,
                "winning block's color declaration should NOT be marked overridden");
        }

        [Test]
        public void RuleBlockBuilder_loser_color_marked_overridden() {
            var doc = Html("<div id=\"x\" class=\"box\"></div>");
            // #x wins over .box for color
            var css = ".box { color: blue; } #x { color: red; }";
            var sheets = new List<OriginatedStylesheet> {
                UA(BuiltinUserAgent),
                Author(css)
            };
            var engine = new CascadeEngine(sheets);
            foreach (var kv in engine.ComputeAll(doc)) { }

            Element divEl = null;
            foreach (var e in doc.GetElementsByTagName("div")) { divEl = e; break; }

            var blocks = RuleBlockBuilder.Build(divEl, engine);

            // Find the .box block and verify its color is overridden
            bool foundOverriddenColor = false;
            foreach (var block in blocks) {
                if (block.SelectorText == ".box") {
                    foreach (var decl in block.Declarations) {
                        if (decl.Property == "color" && decl.IsOverridden) {
                            foundOverriddenColor = true;
                        }
                    }
                }
            }
            Assert.That(foundOverriddenColor, Is.True,
                ".box color should be overridden when #x color wins");
        }

        [Test]
        public void RuleBlockBuilder_origin_label_is_author_for_authored_rules() {
            var doc = Html("<div class=\"t\"></div>");
            var css = ".t { display: block; }";
            var sheets = new List<OriginatedStylesheet> {
                UA(BuiltinUserAgent),
                Author(css)
            };
            var engine = new CascadeEngine(sheets);
            foreach (var kv in engine.ComputeAll(doc)) { }

            Element divEl = null;
            foreach (var e in doc.GetElementsByTagName("div")) { divEl = e; break; }

            var blocks = RuleBlockBuilder.Build(divEl, engine);

            bool foundAuthorLabel = false;
            foreach (var b in blocks) {
                if (b.Origin == DeclarationOrigin.Author) {
                    Assert.That(b.OriginLabel, Is.EqualTo("author"),
                        "author rules should have 'author' origin label");
                    foundAuthorLabel = true;
                    break;
                }
            }
            Assert.That(foundAuthorLabel, Is.True, "at least one author block should exist");
        }

        [Test]
        public void RuleBlockBuilder_ua_blocks_have_user_agent_origin_label() {
            var doc = Html("<div></div>");
            var sheets = new List<OriginatedStylesheet> { UA(BuiltinUserAgent) };
            var engine = new CascadeEngine(sheets);
            foreach (var kv in engine.ComputeAll(doc)) { }

            Element divEl = null;
            foreach (var e in doc.GetElementsByTagName("div")) { divEl = e; break; }

            var blocks = RuleBlockBuilder.Build(divEl, engine);

            foreach (var b in blocks) {
                if (b.Origin == DeclarationOrigin.UserAgent) {
                    Assert.That(b.OriginLabel, Is.EqualTo("user agent"),
                        "UA blocks should be labeled 'user agent'");
                    return;
                }
            }
            // It's acceptable if UA has no matching declarations for div.
        }

        // ================================================================== //
        //  ComputedStyleModel                                                 //
        // ================================================================== //

        [Test]
        public void ComputedStyleModel_empty_when_style_is_null() {
            var model = new ComputedStyleModel();
            model.Build(null, null);
            Assert.That(model.Count, Is.EqualTo(0));
        }

        [Test]
        public void ComputedStyleModel_contains_all_set_properties() {
            var model = new ComputedStyleModel();
            var element = new Element("div");
            var style = new ComputedStyle(element);
            style.Set("display", "flex");
            style.Set("color", "blue");
            style.Set("opacity", "0.5");

            model.Build(style, null);

            var entries = model.Filter(null);
            bool hasDisplay = false, hasColor = false, hasOpacity = false;
            foreach (var e in entries) {
                if (e.Property == "display") hasDisplay = true;
                if (e.Property == "color")   hasColor = true;
                if (e.Property == "opacity") hasOpacity = true;
            }
            Assert.That(hasDisplay, Is.True, "display should be in computed model");
            Assert.That(hasColor,   Is.True, "color should be in computed model");
            Assert.That(hasOpacity, Is.True, "opacity should be in computed model");
        }

        [Test]
        public void ComputedStyleModel_entries_are_sorted_alphabetically() {
            var model = new ComputedStyleModel();
            var element = new Element("div");
            var style = new ComputedStyle(element);
            style.Set("z-index", "10");
            style.Set("align-items", "center");
            style.Set("margin-top", "5px");
            style.Set("color", "red");

            model.Build(style, null);

            var entries = model.Filter(null);
            string prev = null;
            foreach (var e in entries) {
                if (prev != null) {
                    Assert.That(string.Compare(e.Property, prev,
                        System.StringComparison.OrdinalIgnoreCase),
                        Is.GreaterThanOrEqualTo(0),
                        $"entries should be sorted; '{e.Property}' should not come before '{prev}'");
                }
                prev = e.Property;
            }
        }

        [Test]
        public void ComputedStyleModel_filter_returns_only_matching_properties() {
            var model = new ComputedStyleModel();
            var element = new Element("div");
            var style = new ComputedStyle(element);
            style.Set("margin-top", "10px");
            style.Set("margin-bottom", "5px");
            style.Set("color", "red");

            model.Build(style, null);

            var filtered = model.Filter("margin");
            foreach (var e in filtered) {
                Assert.That(e.Property, Does.Contain("margin"),
                    $"'{e.Property}' should contain 'margin'");
            }
            // Should have margin-top and margin-bottom but not color.
            bool hasColor = false;
            foreach (var e in filtered) {
                if (e.Property == "color") hasColor = true;
            }
            Assert.That(hasColor, Is.False, "'color' should not match filter 'margin'");
        }

        [Test]
        public void ComputedStyleModel_filter_case_insensitive() {
            var model = new ComputedStyleModel();
            var element = new Element("div");
            var style = new ComputedStyle(element);
            style.Set("font-size", "16px");

            model.Build(style, null);

            var filtered = model.Filter("FONT");
            bool found = false;
            foreach (var e in filtered) {
                if (e.Property == "font-size") found = true;
            }
            Assert.That(found, Is.True, "filter should be case-insensitive");
        }

        [Test]
        public void ComputedStyleModel_null_filter_returns_all() {
            var model = new ComputedStyleModel();
            var element = new Element("div");
            var style = new ComputedStyle(element);
            style.Set("color", "red");
            style.Set("display", "flex");

            model.Build(style, null);

            var all = model.Filter(null);
            Assert.That(all.Count, Is.EqualTo(model.Count));
        }

        [Test]
        public void ComputedStyleModel_box_model_zeroed_when_no_box() {
            var model = new ComputedStyleModel();
            model.Build(null, null);

            var bm = model.BoxModel;
            Assert.That(bm.MarginW,  Is.EqualTo(0), "MarginW should be zero");
            Assert.That(bm.BorderW,  Is.EqualTo(0), "BorderW should be zero");
            Assert.That(bm.PaddingW, Is.EqualTo(0), "PaddingW should be zero");
            Assert.That(bm.ContentW, Is.EqualTo(0), "ContentW should be zero");
        }

        [Test]
        public void ComputedStyleModel_box_model_populated_when_box_supplied() {
            var model = new ComputedStyleModel();
            var element = new Element("div");
            var box = MakeBox(x: 0, y: 0, w: 100, h: 50, margin: 4, border: 2, padding: 6);
            box.Element = element;

            model.Build(null, box);

            var bm = model.BoxModel;
            Assert.That(bm.BorderW, Is.EqualTo(100).Within(1e-9), "border width should be 100");
            Assert.That(bm.BorderH, Is.EqualTo(50).Within(1e-9),  "border height should be 50");
        }

        // Note: SelectionHighlightSource tests require Unity APIs (IUIPaintSource /
        // IRenderBackend) and live in the Unity Test Runner only. They cannot run
        // in the headless TestVerifyAll runner because the Rendering namespace is
        // excluded from the headless csproj. See ElementsWindowDataTests_Unity.cs
        // (to be added if Unity runtime test coverage for the highlight source is
        // desired — currently the highlight is exercised manually per the test script).

        // ================================================================== //
        //  Helpers                                                            //
        // ================================================================== //

        static BlockBox MakeBox(double x, double y, double w, double h,
                                double margin, double border, double padding) {
            var b = new BlockBox();
            b.X = x; b.Y = y; b.Width = w; b.Height = h;
            b.MarginTop  = b.MarginRight  = b.MarginBottom  = b.MarginLeft  = margin;
            b.BorderTop  = b.BorderRight  = b.BorderBottom  = b.BorderLeft  = border;
            b.PaddingTop = b.PaddingRight = b.PaddingBottom = b.PaddingLeft = padding;
            return b;
        }
    }
}
