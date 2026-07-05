using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CascadeEngine.ComputeBefore / ComputeAfter resolve the cascaded style
    // of a ::before / ::after pseudo-element rooted at a given host. Rules
    // with PseudoElement == "before" / "after" are routed into separate
    // buckets at compile time; ComputeBefore / ComputeAfter walk those
    // buckets, match against the host, and apply specificity sort + host
    // inheritance for unset inherited properties.
    //
    // BoxBuilder consumes the resolver to inject anonymous child boxes at
    // index 0 (::before) or final index (::after) of the host's children.
    // Generation is gated on a non-default `content` value: `normal` /
    // `none` / unset suppresses the pseudo box entirely.
    public class CascadePseudoElementTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        static Element FindByTag(Node n, string tag) {
            if (n is Element e && e.TagName == tag) return e;
            foreach (var c in n.Children) {
                var f = FindByTag(c, tag);
                if (f != null) return f;
            }
            return null;
        }

        [Test]
        public void Before_rule_with_string_content_resolves_to_literal_text() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x::before { content: \"X\"; }")
            });
            var x = doc.GetElementById("x");
            var bs = engine.ComputeBefore(x);
            Assert.That(bs, Is.Not.Null);
            Assert.That(CascadeEngine.ResolveContentString(bs.Get("content")), Is.EqualTo("X"));
        }

        [Test]
        public void Legacy_single_colon_before_rule_routes_to_before_bucket() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x:before { content: \"X\"; color: blue; }")
            });
            var bs = engine.ComputeBefore(doc.GetElementById("x"));
            Assert.That(bs, Is.Not.Null);
            Assert.That(CascadeEngine.ResolveContentString(bs.Get("content")), Is.EqualTo("X"));
            Assert.That(bs.Get("color"), Is.EqualTo("blue"));
        }

        [Test]
        public void After_rule_with_empty_string_content_resolves_to_empty_box() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x::after { content: \"\"; }")
            });
            var x = doc.GetElementById("x");
            var bs = engine.ComputeAfter(x);
            Assert.That(bs, Is.Not.Null);
            // Empty string is still a valid content value — the box is
            // generated, just with no text run inside it.
            Assert.That(CascadeEngine.ResolveContentString(bs.Get("content")), Is.EqualTo(""));
        }

        [Test]
        public void Content_none_produces_no_pseudo_style() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x::before { content: none; }")
            });
            var x = doc.GetElementById("x");
            var bs = engine.ComputeBefore(x);
            // The resolver still returns a ComputedStyle (rule matched), but
            // ResolveContentString must return null so no box is generated.
            Assert.That(bs, Is.Not.Null);
            Assert.That(CascadeEngine.ResolveContentString(bs.Get("content")), Is.Null);
        }

        [Test]
        public void No_matching_rule_returns_null() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#y::before { content: \"X\"; }")
            });
            var x = doc.GetElementById("x");
            Assert.That(engine.ComputeBefore(x), Is.Null);
            Assert.That(engine.ComputeAfter(x), Is.Null);
        }

        [Test]
        public void Pseudo_inherits_color_from_host() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { color: red; } #x::before { content: \"X\"; }")
            });
            var x = doc.GetElementById("x");
            var bs = engine.ComputeBefore(x);
            Assert.That(bs, Is.Not.Null);
            // color is inherited; the pseudo-element didn't set it, so it
            // should pull the host's red.
            Assert.That(bs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Pseudo_explicit_color_wins_over_host_inheritance() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { color: red; } #x::before { content: \"X\"; color: blue; }")
            });
            var x = doc.GetElementById("x");
            var bs = engine.ComputeBefore(x);
            Assert.That(bs.Get("color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Default_display_is_inline_when_unspecified() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x::before { content: \"X\"; }")
            });
            var x = doc.GetElementById("x");
            var bs = engine.ComputeBefore(x);
            Assert.That(bs.Get("display"), Is.EqualTo("inline"));
        }

        [Test]
        public void Author_can_set_pseudo_to_block() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x::before { content: \"X\"; display: block; }")
            });
            var x = doc.GetElementById("x");
            var bs = engine.ComputeBefore(x);
            Assert.That(bs.Get("display"), Is.EqualTo("block"));
        }

        [Test]
        public void Specificity_resolves_competing_pseudo_rules() {
            var doc = Html("<div id=\"x\" class=\"c\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(".c::before { content: \"low\"; } #x::before { content: \"high\"; }")
            });
            var bs = engine.ComputeBefore(doc.GetElementById("x"));
            Assert.That(CascadeEngine.ResolveContentString(bs.Get("content")), Is.EqualTo("high"));
        }

        [Test]
        public void ResolveContentString_decodes_quoted_string() {
            Assert.That(CascadeEngine.ResolveContentString("\"hello\""), Is.EqualTo("hello"));
            Assert.That(CascadeEngine.ResolveContentString("'hi'"), Is.EqualTo("hi"));
            Assert.That(CascadeEngine.ResolveContentString("\"\""), Is.EqualTo(""));
        }

        [Test]
        public void ResolveContentString_returns_null_for_normal_and_none() {
            Assert.That(CascadeEngine.ResolveContentString("normal"), Is.Null);
            Assert.That(CascadeEngine.ResolveContentString("none"), Is.Null);
            Assert.That(CascadeEngine.ResolveContentString(""), Is.Null);
            Assert.That(CascadeEngine.ResolveContentString(null), Is.Null);
        }

        [Test]
        public void ResolveContentString_returns_null_for_unsupported_forms() {
            // url() — no text representation, null suppresses the pseudo box.
            // attr() without a host — no element context, null suppresses box.
            // counter() without a host — CSS spec says an unresolvable counter()
            //   produces an EMPTY STRING (the box still exists, just text-less).
            //   The no-host 1-arg overload routes to ResolveContentString(raw,null,null)
            //   which returns "" for counter() per the updated v2 spec contract.
            Assert.That(CascadeEngine.ResolveContentString("attr(data-foo)"), Is.Null);
            Assert.That(CascadeEngine.ResolveContentString("counter(li)"), Is.EqualTo(""));
            Assert.That(CascadeEngine.ResolveContentString("url('a.png')"), Is.Null);
        }

        [Test]
        public void ResolveContentString_resolves_attr_against_host() {
            var doc = Html("<div data-status=\"online\"></div>");
            var div = FindByTag(doc, "div");
            Assert.That(CascadeEngine.ResolveContentString("attr(data-status)", div), Is.EqualTo("online"));
        }

        [Test]
        public void ResolveContentString_attr_missing_attribute_uses_fallback_string() {
            var doc = Html("<div></div>");
            var div = FindByTag(doc, "div");
            Assert.That(CascadeEngine.ResolveContentString("attr(data-x, \"--\")", div), Is.EqualTo("--"));
        }

        [Test]
        public void ResolveContentString_attr_missing_attribute_no_fallback_returns_empty() {
            var doc = Html("<div></div>");
            var div = FindByTag(doc, "div");
            // CSS Values 5: missing attr() with no fallback resolves to "" —
            // we still generate a (text-less) box so the author can style
            // the pseudo element decoratively.
            Assert.That(CascadeEngine.ResolveContentString("attr(data-x)", div), Is.EqualTo(""));
        }

        [Test]
        public void ResolveContentString_attr_fallback_none_suppresses_box() {
            var doc = Html("<div></div>");
            var div = FindByTag(doc, "div");
            // `none` as a fallback is treated as "no box", matching `content: none`.
            Assert.That(CascadeEngine.ResolveContentString("attr(data-x, none)", div), Is.Null);
        }

        [Test]
        public void ResolveContentString_concatenates_string_and_attr_segments() {
            var doc = Html("<div data-name=\"world\"></div>");
            var div = FindByTag(doc, "div");
            Assert.That(CascadeEngine.ResolveContentString("\"Hello \" attr(data-name)", div), Is.EqualTo("Hello world"));
        }

        [Test]
        public void Before_rule_with_attr_renders_attribute_value_through_pipeline() {
            var doc = Html("<div id=\"x\" data-label=\"hello\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x::before { content: attr(data-label); }")
            });
            var x = doc.GetElementById("x");
            var bs = engine.ComputeBefore(x);
            Assert.That(bs, Is.Not.Null);
            // Pipeline: ComputePseudoElement skips the generic AttrResolver
            // pass for `content`, leaving the raw attr() form for the
            // host-aware ResolveContentString overload to decode.
            Assert.That(CascadeEngine.ResolveContentString(bs.Get("content"), x), Is.EqualTo("hello"));
        }

        // --- BoxBuilder injection tests --- //

        static (Box root, CascadeEngine engine, Dictionary<Element, ComputedStyle> styles) BuildWithPseudo(string html, string css) {
            var doc = HtmlParser.Parse(html);
            var sheets = new List<OriginatedStylesheet>();
            sheets.Add(Author(css));
            var engine = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            var bb = new BoxBuilder(e => styles.TryGetValue(e, out var cs) ? cs : null);
            bb.BeforeStyleOf = e => engine.ComputeBefore(e);
            bb.AfterStyleOf = e => engine.ComputeAfter(e);
            bb.MarkerStyleOf = e => engine.ComputeMarker(e);
            return (bb.BuildDocument(doc), engine, styles);
        }


        [Test]
        public void Before_rule_generates_first_child_box_with_text() {
            var (root, _, _) = BuildWithPseudo(
                "<div id=\"x\">hi</div>",
                "div { display: block; } #x::before { content: \"A\"; }");
            Box host = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element != null && b.Element.Id == "x") { host = b; break; }
            }
            Assert.That(host, Is.Not.Null);
            Assert.That(host.Children.Count, Is.GreaterThanOrEqualTo(1));
            var first = host.Children[0];
            Assert.That(first.Element, Is.Null, "::before generated box has no DOM Element");
            // The generated box wraps a TextRun whose text is the content.
            var textRun = FindFirstTextRun(first);
            Assert.That(textRun, Is.Not.Null);
            Assert.That(textRun.Text, Is.EqualTo("A"));
        }

        [Test]
        public void After_rule_with_empty_content_generates_last_child_box_with_no_text() {
            var (root, _, _) = BuildWithPseudo(
                "<div id=\"x\">hi</div>",
                "div { display: block; } #x::after { content: \"\"; display: block; width: 10px; }");
            Box host = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element != null && b.Element.Id == "x") { host = b; break; }
            }
            Assert.That(host, Is.Not.Null);
            Assert.That(host.Children.Count, Is.GreaterThanOrEqualTo(1));
            var last = host.Children[host.Children.Count - 1];
            Assert.That(last.Element, Is.Null, "::after generated box has no DOM Element");
            // Empty content -> no text run inside.
            var textRun = FindFirstTextRun(last);
            Assert.That(textRun, Is.Null);
            // Authored width on the pseudo style is preserved.
            Assert.That(last.Style.Get("width"), Is.EqualTo("10px"));
        }

        [Test]
        public void Content_none_generates_no_pseudo_box() {
            var (root, _, _) = BuildWithPseudo(
                "<div id=\"x\">hi</div>",
                "div { display: block; } #x::before { content: none; }");
            Box host = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element != null && b.Element.Id == "x") { host = b; break; }
            }
            Assert.That(host, Is.Not.Null);
            // No pseudo box should appear in the host's children — just the
            // text node "hi".
            foreach (var c in host.Children) {
                Assert.That(c is BlockBox bb && bb.Element == null, Is.False,
                    "no anonymous BlockBox should be present when content: none");
                Assert.That(c is InlineBox ib && ib.Element == null, Is.False,
                    "no anonymous InlineBox should be present when content: none");
            }
        }

        [Test]
        public void Pseudo_box_has_no_DOM_element_identity() {
            var (root, _, _) = BuildWithPseudo(
                "<div id=\"x\">hi</div>",
                "div { display: block; } #x::before { content: \"A\"; }");
            Box host = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element != null && b.Element.Id == "x") { host = b; break; }
            }
            Assert.That(host.Children[0].Element, Is.Null);
        }

        // --- Pseudo-element rendering integration -----------------------
        //
        // CompileStyleRule routes `::placeholder` and `::selection` into
        // their own rule buckets, mirroring `::before` / `::after`.
        // ComputePlaceholder / ComputeSelection consume those buckets and
        // produce a cascaded ComputedStyle on demand; InputRenderer reads
        // the resolved color (placeholder) and background-color (selection)
        // when emitting overlay paint commands. `::marker` follows the same
        // rejected by IsKnownPseudoElement and silently dropped — separate
        // routed-pseudo path and is consumed by list marker injection.

        [Test]
        public void Placeholder_rule_color_flows_into_placeholder_text_style() {
            var doc = Html("<input id=\"x\" type=\"text\" placeholder=\"name\">");
            var engine = new CascadeEngine(new[] {
                Author("input::placeholder { color: red; }")
            });
            var x = doc.GetElementById("x");
            var ps = engine.ComputePlaceholder(x);
            Assert.That(ps, Is.Not.Null);
            Assert.That(ps.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Selection_rule_background_flows_into_input_selection_paint() {
            var doc = Html("<input id=\"x\" type=\"text\">");
            var engine = new CascadeEngine(new[] {
                Author("input::selection { background-color: yellow; color: black; }")
            });
            var x = doc.GetElementById("x");
            var ss = engine.ComputeSelection(x);
            Assert.That(ss, Is.Not.Null);
            Assert.That(ss.Get("background-color"), Is.EqualTo("yellow"));
            Assert.That(ss.Get("color"), Is.EqualTo("black"));
        }

        [Test]
        public void Marker_rule_color_flows_into_list_marker_style() {
            var doc = Html("<ul><li id=\"x\">item</li></ul>");
            var engine = new CascadeEngine(new[] {
                Author("li { color: black; } li::marker { color: red; }")
            });
            var x = doc.GetElementById("x");
            var ms = engine.ComputeMarker(x);
            Assert.That(ms, Is.Not.Null);
            Assert.That(ms.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Marker_rule_styles_generated_marker_box_and_text() {
            // CSS Lists L3 §2 / C8 fix: markers fire on display:list-item (not tag-gated).
            // li must use display:list-item (not block) to get a marker. The UA stylesheet
            // now sets li { display: list-item }; here we replicate it explicitly since
            // BuildWithPseudo uses author sheets only (no UA sheet).
            var (root, _, _) = BuildWithPseudo(
                "<ul><li id=\"x\">item</li></ul>",
                "ul { display: block; } li { display: list-item; color: black; list-style-type: disc; } li::marker { color: red; }");

            BlockBox marker = null;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element == null && bb.IsInlineBlock && FindFirstTextRun(bb)?.Text == "\u2022") {
                    marker = bb;
                    break;
                }
            }
            Assert.That(marker, Is.Not.Null);
            Assert.That(marker.Style.Get("color"), Is.EqualTo("red"));
            Assert.That(FindFirstTextRun(marker).Style.Get("color"), Is.EqualTo("red"));
        }

        // --- `inset` shorthand on pseudo-elements (regression) -----------
        //
        // `inset: 0` is the modern shorthand for `top:0; right:0; bottom:0;
        // left:0`. The cascade must expand it via ShorthandRegistry's
        // InsetShorthandExpander so that PositioningPass.ApplyAbsolute can
        // see all four offsets pinned and compute width/height = cb minus
        // offsets. Prior to v0.9 this worked for elements but silently
        // failed for ::after pseudos that opt into `display: flex` — the
        // pseudo's cross-axis Height was collapsed back to text-content
        // height on the second flex pass, so `::after { inset: 0;
        // display: flex; align-items: center }` rendered text at the top
        // of the host instead of vertically centred. See PositioningPass
        // (GridStretchedHeight piggyback) for the fix; the test below
        // pins the cascade contract that allows it.

        [Test]
        public void Inset_zero_on_pseudo_expands_to_all_four_offset_longhands() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x::after { content: \"X\"; position: absolute; inset: 0; }")
            });
            var bs = engine.ComputeAfter(doc.GetElementById("x"));
            Assert.That(bs, Is.Not.Null);
            Assert.That(bs.Get("top"), Is.EqualTo("0"));
            Assert.That(bs.Get("right"), Is.EqualTo("0"));
            Assert.That(bs.Get("bottom"), Is.EqualTo("0"));
            Assert.That(bs.Get("left"), Is.EqualTo("0"));
        }

        [Test]
        public void Inset_two_value_on_pseudo_maps_to_vertical_horizontal_longhands() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x::after { content: \"X\"; position: absolute; inset: 10px 20px; }")
            });
            var bs = engine.ComputeAfter(doc.GetElementById("x"));
            Assert.That(bs.Get("top"), Is.EqualTo("10px"));
            Assert.That(bs.Get("bottom"), Is.EqualTo("10px"));
            Assert.That(bs.Get("left"), Is.EqualTo("20px"));
            Assert.That(bs.Get("right"), Is.EqualTo("20px"));
        }

        [Test]
        public void Inset_unset_on_pseudo_keeps_offset_longhands_at_auto_initial() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x::after { content: \"X\"; position: absolute; }")
            });
            var bs = engine.ComputeAfter(doc.GetElementById("x"));
            // Absent `inset` must NOT be silently treated as 0; the four
            // offset longhands stay at their `auto` initial so
            // PositioningPass falls back to static-position placement.
            Assert.That(bs.Get("top"), Is.EqualTo("auto"));
            Assert.That(bs.Get("right"), Is.EqualTo("auto"));
            Assert.That(bs.Get("bottom"), Is.EqualTo("auto"));
            Assert.That(bs.Get("left"), Is.EqualTo("auto"));
        }

        // --- helpers --- //

        static IEnumerable<Box> AllBoxes(Box root) {
            yield return root;
            foreach (var c in root.Children) {
                foreach (var d in AllBoxes(c)) yield return d;
            }
        }

        static TextRun FindFirstTextRun(Box root) {
            foreach (var b in AllBoxes(root)) {
                if (b is TextRun tr) return tr;
            }
            return null;
        }

    }
}
