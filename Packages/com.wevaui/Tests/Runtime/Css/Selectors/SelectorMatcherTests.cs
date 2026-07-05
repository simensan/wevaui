using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Selectors {
    public class SelectorMatcherTests {
        static Document Parse(string html) => HtmlParser.Parse(html);

        static Element FirstByTag(Document doc, string tag) {
            foreach (var c in doc.Children) {
                var found = FindByTag(c, tag);
                if (found != null) return found;
            }
            return null;
        }

        static Element FindByTag(Node n, string tag) {
            if (n is Element e && e.TagName == tag) return e;
            foreach (var c in n.Children) {
                var f = FindByTag(c, tag);
                if (f != null) return f;
            }
            return null;
        }

        static Element ById(Document doc, string id) => doc.GetElementById(id);

        static bool Match(string selector, Element e, IElementStateProvider state = null)
            => SelectorMatcher.Matches(SelectorParser.Parse(selector), e, state);

        sealed class FakeState : IElementStateProvider {
            readonly Dictionary<Element, ElementState> map = new();
            public void Set(Element e, ElementState s) { map[e] = s; }
            public ElementState GetState(Element e) => map.TryGetValue(e, out var s) ? s : ElementState.None;
        }

        [Test]
        public void Universal_matches_any_element() {
            var doc = Parse("<div></div>");
            Assert.That(Match("*", FirstByTag(doc, "div")), Is.True);
        }

        [Test]
        public void Tag_matches_same_name() {
            var doc = Parse("<div></div>");
            Assert.That(Match("div", FirstByTag(doc, "div")), Is.True);
        }

        [Test]
        public void Tag_does_not_match_different_name() {
            var doc = Parse("<div></div>");
            Assert.That(Match("span", FirstByTag(doc, "div")), Is.False);
        }

        [Test]
        public void Id_matches() {
            var doc = Parse("<div id=\"x\"></div>");
            Assert.That(Match("#x", ById(doc, "x")), Is.True);
        }

        [Test]
        public void Id_does_not_match_other() {
            var doc = Parse("<div id=\"x\"></div>");
            Assert.That(Match("#y", ById(doc, "x")), Is.False);
        }

        [Test]
        public void Class_matches() {
            var doc = Parse("<div class=\"a b c\" id=\"x\"></div>");
            Assert.That(Match(".b", ById(doc, "x")), Is.True);
        }

        [Test]
        public void Class_does_not_match_partial_token() {
            var doc = Parse("<div class=\"foobar\" id=\"x\"></div>");
            Assert.That(Match(".foo", ById(doc, "x")), Is.False);
        }

        [Test]
        public void Class_does_not_match_missing() {
            var doc = Parse("<div id=\"x\"></div>");
            Assert.That(Match(".foo", ById(doc, "x")), Is.False);
        }

        [Test]
        public void Compound_tag_class_matches() {
            var doc = Parse("<div class=\"foo\" id=\"x\"></div>");
            Assert.That(Match("div.foo", ById(doc, "x")), Is.True);
        }

        [Test]
        public void Attribute_presence_matches() {
            var doc = Parse("<input disabled id=\"x\">");
            Assert.That(Match("[disabled]", ById(doc, "x")), Is.True);
        }

        [Test]
        public void Attribute_presence_negative() {
            var doc = Parse("<input id=\"x\">");
            Assert.That(Match("[disabled]", ById(doc, "x")), Is.False);
        }

        [Test]
        public void Attribute_equals() {
            var doc = Parse("<input type=\"text\" id=\"x\">");
            Assert.That(Match("[type=text]", ById(doc, "x")), Is.True);
            Assert.That(Match("[type=number]", ById(doc, "x")), Is.False);
        }

        [Test]
        public void Attribute_whitespace_contains() {
            var doc = Parse("<div data-tags=\"alpha beta gamma\" id=\"x\"></div>");
            Assert.That(Match("[data-tags~=beta]", ById(doc, "x")), Is.True);
            Assert.That(Match("[data-tags~=delta]", ById(doc, "x")), Is.False);
        }

        [Test]
        public void Attribute_dash_match_exact() {
            var doc = Parse("<div lang=\"en\" id=\"x\"></div>");
            Assert.That(Match("[lang|=en]", ById(doc, "x")), Is.True);
        }

        // a11y regression: HTML parser preserves aria-* / role attributes verbatim
        // and CSS attribute selectors with hyphens match them. Authors commonly write
        // [aria-expanded="true"] { ... } or [role="button"] { ... } for stateful
        // styling of widget toggles.
        [Test]
        public void Attribute_selector_matches_aria_expanded() {
            var doc = Parse("<button id=\"b\" aria-expanded=\"true\"></button>");
            Assert.That(Match("[aria-expanded=\"true\"]", ById(doc, "b")), Is.True);
            Assert.That(Match("[aria-expanded=\"false\"]", ById(doc, "b")), Is.False);
        }

        [Test]
        public void Attribute_selector_matches_role_attribute() {
            var doc = Parse("<div id=\"x\" role=\"button\" aria-label=\"Close\"></div>");
            Assert.That(Match("[role=button]", ById(doc, "x")), Is.True);
            Assert.That(Match("[aria-label]", ById(doc, "x")), Is.True);
        }

        [Test]
        public void Attribute_dash_match_prefix() {
            var doc = Parse("<div lang=\"en-US\" id=\"x\"></div>");
            Assert.That(Match("[lang|=en]", ById(doc, "x")), Is.True);
        }

        [Test]
        public void Attribute_dash_match_negative() {
            var doc = Parse("<div lang=\"english\" id=\"x\"></div>");
            Assert.That(Match("[lang|=en]", ById(doc, "x")), Is.False);
        }

        [Test]
        public void Attribute_prefix_match() {
            var doc = Parse("<a href=\"https://example.com\" id=\"x\"></a>");
            Assert.That(Match("[href^=\"https://\"]", ById(doc, "x")), Is.True);
            Assert.That(Match("[href^=\"http://\"]", ById(doc, "x")), Is.False);
        }

        [Test]
        public void Attribute_suffix_match() {
            var doc = Parse("<img src=\"a.png\" id=\"x\">");
            Assert.That(Match("[src$=\".png\"]", ById(doc, "x")), Is.True);
            Assert.That(Match("[src$=\".jpg\"]", ById(doc, "x")), Is.False);
        }

        [Test]
        public void Attribute_substring_match() {
            var doc = Parse("<div title=\"Hello World\" id=\"x\"></div>");
            Assert.That(Match("[title*=\"lo Wo\"]", ById(doc, "x")), Is.True);
            Assert.That(Match("[title*=zzz]", ById(doc, "x")), Is.False);
        }

        // CSS Selectors L4 §6.3.6: attribute selectors accept a trailing
        // `i` flag (case-insensitive) or `s` flag (explicit case-sensitive).
        [Test]
        public void Attribute_case_insensitive_flag_matches() {
            var doc = Parse("<div data-tag=\"hello\" id=\"x\"></div>");
            Assert.That(Match("[data-tag=Hello i]", ById(doc, "x")), Is.True);
            Assert.That(Match("[data-tag=Hello]", ById(doc, "x")), Is.False);
            Assert.That(Match("[data-tag=hello s]", ById(doc, "x")), Is.True);
        }

        [Test]
        public void Descendant_combinator_matches_grandchild() {
            var doc = Parse("<div><section><span id=\"x\"></span></section></div>");
            Assert.That(Match("div span", ById(doc, "x")), Is.True);
        }

        [Test]
        public void Child_combinator_does_not_match_grandchild() {
            var doc = Parse("<div><section><span id=\"x\"></span></section></div>");
            Assert.That(Match("div > span", ById(doc, "x")), Is.False);
        }

        [Test]
        public void Child_combinator_matches_direct_child() {
            var doc = Parse("<div><span id=\"x\"></span></div>");
            Assert.That(Match("div > span", ById(doc, "x")), Is.True);
        }

        [Test]
        public void Adjacent_sibling_matches_immediate_next() {
            var doc = Parse("<div><h1></h1><p id=\"x\"></p></div>");
            Assert.That(Match("h1 + p", ById(doc, "x")), Is.True);
        }

        [Test]
        public void Adjacent_sibling_does_not_match_skipped() {
            var doc = Parse("<div><h1></h1><span></span><p id=\"x\"></p></div>");
            Assert.That(Match("h1 + p", ById(doc, "x")), Is.False);
        }

        [Test]
        public void General_sibling_matches_any_following() {
            var doc = Parse("<div><h1></h1><span></span><p id=\"x\"></p></div>");
            Assert.That(Match("h1 ~ p", ById(doc, "x")), Is.True);
        }

        [Test]
        public void General_sibling_does_not_match_preceding() {
            var doc = Parse("<div><p id=\"x\"></p><h1></h1></div>");
            Assert.That(Match("h1 ~ p", ById(doc, "x")), Is.False);
        }

        [Test]
        public void First_child_positive() {
            var doc = Parse("<div><p id=\"x\"></p><p></p></div>");
            Assert.That(Match(":first-child", ById(doc, "x")), Is.True);
        }

        [Test]
        public void First_child_negative() {
            var doc = Parse("<div><p></p><p id=\"x\"></p></div>");
            Assert.That(Match(":first-child", ById(doc, "x")), Is.False);
        }

        [Test]
        public void Last_child_positive() {
            var doc = Parse("<div><p></p><p id=\"x\"></p></div>");
            Assert.That(Match(":last-child", ById(doc, "x")), Is.True);
        }

        [Test]
        public void Only_child_positive() {
            var doc = Parse("<div><p id=\"x\"></p></div>");
            Assert.That(Match(":only-child", ById(doc, "x")), Is.True);
        }

        [Test]
        public void Only_child_negative_when_siblings() {
            var doc = Parse("<div><p id=\"x\"></p><p></p></div>");
            Assert.That(Match(":only-child", ById(doc, "x")), Is.False);
        }

        [Test]
        public void Nth_child_odd() {
            var doc = Parse("<div><p id=\"a\"></p><p id=\"b\"></p><p id=\"c\"></p></div>");
            Assert.That(Match(":nth-child(odd)", ById(doc, "a")), Is.True);
            Assert.That(Match(":nth-child(odd)", ById(doc, "b")), Is.False);
            Assert.That(Match(":nth-child(odd)", ById(doc, "c")), Is.True);
        }

        [Test]
        public void Nth_child_even() {
            var doc = Parse("<div><p id=\"a\"></p><p id=\"b\"></p><p id=\"c\"></p></div>");
            Assert.That(Match(":nth-child(even)", ById(doc, "a")), Is.False);
            Assert.That(Match(":nth-child(even)", ById(doc, "b")), Is.True);
        }

        [Test]
        public void Nth_child_2n_plus_1() {
            var doc = Parse("<div><p id=\"a\"></p><p id=\"b\"></p><p id=\"c\"></p></div>");
            Assert.That(Match(":nth-child(2n+1)", ById(doc, "a")), Is.True);
            Assert.That(Match(":nth-child(2n+1)", ById(doc, "c")), Is.True);
        }

        [Test]
        public void Nth_child_integer() {
            var doc = Parse("<div><p id=\"a\"></p><p id=\"b\"></p><p id=\"c\"></p></div>");
            Assert.That(Match(":nth-child(3)", ById(doc, "c")), Is.True);
            Assert.That(Match(":nth-child(3)", ById(doc, "b")), Is.False);
        }

        [Test]
        public void Nth_child_negative_n_plus_3_first_three() {
            var doc = Parse("<div><p id=\"a\"></p><p id=\"b\"></p><p id=\"c\"></p><p id=\"d\"></p></div>");
            Assert.That(Match(":nth-child(-n+3)", ById(doc, "a")), Is.True);
            Assert.That(Match(":nth-child(-n+3)", ById(doc, "b")), Is.True);
            Assert.That(Match(":nth-child(-n+3)", ById(doc, "c")), Is.True);
            Assert.That(Match(":nth-child(-n+3)", ById(doc, "d")), Is.False);
        }

        [Test]
        public void Nth_child_of_filter_counts_only_matching_siblings() {
            var doc = Parse("<ul><li id=\"a\" class=\"item\"></li><li id=\"x\"></li><li id=\"b\" class=\"item\"></li></ul>");
            Assert.That(Match(":nth-child(1 of .item)", ById(doc, "a")), Is.True);
            Assert.That(Match(":nth-child(1 of .item)", ById(doc, "x")), Is.False);
            Assert.That(Match(":nth-child(1 of .item)", ById(doc, "b")), Is.False);
            Assert.That(Match(":nth-child(2 of .item)", ById(doc, "b")), Is.True);
            Assert.That(Match(":nth-child(2 of .item)", ById(doc, "a")), Is.False);

            // Existing unfiltered :nth-child still counts every child.
            Assert.That(Match(":nth-child(2)", ById(doc, "x")), Is.True);
            Assert.That(Match(":nth-child(3)", ById(doc, "b")), Is.True);

            // Compound filter (tag + multiple classes).
            var doc2 = Parse("<ul><li class=\"item\"></li><li id=\"y\" class=\"item active\"></li><li class=\"item active\"></li></ul>");
            Assert.That(Match(":nth-child(1 of li.item.active)", ById(doc2, "y")), Is.True);

            // :nth-last-child(... of S) walks from the end.
            var doc3 = Parse("<ul><li id=\"p\" class=\"item\"></li><li></li><li id=\"q\" class=\"item\"></li></ul>");
            Assert.That(Match(":nth-last-child(1 of .item)", ById(doc3, "q")), Is.True);
            Assert.That(Match(":nth-last-child(2 of .item)", ById(doc3, "p")), Is.True);
        }

        [Test]
        public void Nth_of_type_distinguishes_from_nth_child() {
            var doc = Parse("<div><p id=\"p1\"></p><span></span><p id=\"p2\"></p></div>");
            Assert.That(Match("p:nth-of-type(2)", ById(doc, "p2")), Is.True);
            Assert.That(Match("p:nth-child(2)", ById(doc, "p2")), Is.False);
        }

        [Test]
        public void First_of_type_among_mixed() {
            var doc = Parse("<div><span></span><p id=\"x\"></p><p></p></div>");
            Assert.That(Match("p:first-of-type", ById(doc, "x")), Is.True);
        }

        [Test]
        public void Empty_matches_no_children() {
            var doc = Parse("<div><span id=\"x\"></span></div>");
            Assert.That(Match(":empty", ById(doc, "x")), Is.True);
        }

        [Test]
        public void Empty_does_not_match_when_text_present() {
            var doc = Parse("<div><span id=\"x\">hi</span></div>");
            Assert.That(Match(":empty", ById(doc, "x")), Is.False);
        }

        [Test]
        public void Not_class_matches() {
            var doc = Parse("<div class=\"bar\" id=\"x\"></div>");
            Assert.That(Match(":not(.foo)", ById(doc, "x")), Is.True);
            Assert.That(Match(":not(.bar)", ById(doc, "x")), Is.False);
        }

        [Test]
        public void Is_matches_any_in_list() {
            var doc = Parse("<p id=\"x\"></p>");
            Assert.That(Match(":is(div, p, span)", ById(doc, "x")), Is.True);
            Assert.That(Match(":is(div, span)", ById(doc, "x")), Is.False);
        }

        [Test]
        public void Where_matches_like_is() {
            var doc = Parse("<p id=\"x\"></p>");
            Assert.That(Match(":where(div, p)", ById(doc, "x")), Is.True);
        }

        [Test]
        public void Hover_via_state_provider() {
            var doc = Parse("<div id=\"x\"></div>");
            var e = ById(doc, "x");
            var state = new FakeState();
            state.Set(e, ElementState.Hover);
            Assert.That(Match(":hover", e, state), Is.True);
        }

        [Test]
        public void Hover_default_provider_is_false() {
            var doc = Parse("<div id=\"x\"></div>");
            Assert.That(Match(":hover", ById(doc, "x")), Is.False);
        }

        [Test]
        public void Focus_visible_via_state() {
            var doc = Parse("<input id=\"x\">");
            var e = ById(doc, "x");
            var state = new FakeState();
            state.Set(e, ElementState.FocusVisible);
            Assert.That(Match(":focus-visible", e, state), Is.True);
        }

        [Test]
        public void Disabled_via_attribute() {
            var doc = Parse("<input disabled id=\"x\">");
            Assert.That(Match(":disabled", ById(doc, "x")), Is.True);
        }

        [Test]
        public void Checked_via_attribute() {
            var doc = Parse("<input checked id=\"x\">");
            Assert.That(Match(":checked", ById(doc, "x")), Is.True);
        }

        [Test]
        public void Enabled_matches_form_controls_without_disabled() {
            var doc = Parse("<input id=\"x\"><input disabled id=\"d\"><div id=\"plain\"></div>");
            Assert.That(Match(":enabled", ById(doc, "x")), Is.True);
            Assert.That(Match(":enabled", ById(doc, "d")), Is.False);
            Assert.That(Match(":enabled", ById(doc, "plain")), Is.False);
        }

        [Test]
        public void Required_and_optional_match_form_controls() {
            var doc = Parse("<input required id=\"r\"><input id=\"o\"><textarea required id=\"t\"></textarea><button id=\"b\"></button>");
            Assert.That(Match(":required", ById(doc, "r")), Is.True);
            Assert.That(Match(":optional", ById(doc, "r")), Is.False);
            Assert.That(Match(":optional", ById(doc, "o")), Is.True);
            Assert.That(Match(":required", ById(doc, "t")), Is.True);
            Assert.That(Match(":optional", ById(doc, "b")), Is.False);
        }

        [Test]
        public void Read_only_and_read_write_follow_editability() {
            var doc = Parse("<input id=\"rw\"><input readonly id=\"ro\"><div id=\"d\"></div><div contenteditable id=\"ce\"></div>");
            Assert.That(Match(":read-write", ById(doc, "rw")), Is.True);
            Assert.That(Match(":read-only", ById(doc, "rw")), Is.False);
            Assert.That(Match(":read-only", ById(doc, "ro")), Is.True);
            Assert.That(Match(":read-only", ById(doc, "d")), Is.True);
            Assert.That(Match(":read-write", ById(doc, "ce")), Is.True);
        }

        [Test]
        public void Valid_and_invalid_cover_required_email_and_number_constraints() {
            var doc = Parse(
                "<input required id=\"empty\">" +
                "<input required value=\"ok\" id=\"filled\">" +
                "<input type=\"email\" value=\"bad\" id=\"email\">" +
                "<input type=\"number\" min=\"2\" max=\"4\" value=\"5\" id=\"num\">" +
                "<div id=\"plain\"></div>");
            Assert.That(Match(":invalid", ById(doc, "empty")), Is.True);
            Assert.That(Match(":valid", ById(doc, "filled")), Is.True);
            Assert.That(Match(":invalid", ById(doc, "email")), Is.True);
            Assert.That(Match(":invalid", ById(doc, "num")), Is.True);
            Assert.That(Match(":valid", ById(doc, "plain")), Is.False);
        }

        [Test]
        public void Link_pseudos_match_href_elements_without_history() {
            var doc = Parse("<a href=\"#\" id=\"a\"></a><a id=\"plain\"></a><div href=\"#\" id=\"div\"></div>");
            Assert.That(Match(":link", ById(doc, "a")), Is.True);
            Assert.That(Match(":any-link", ById(doc, "a")), Is.True);
            Assert.That(Match(":visited", ById(doc, "a")), Is.False);
            Assert.That(Match(":link", ById(doc, "plain")), Is.False);
            Assert.That(Match(":any-link", ById(doc, "div")), Is.False);
        }

        [Test]
        public void Link_pseudos_only_match_a_and_area_with_href() {
            var doc = Parse(
                "<a href=\"#\" id=\"anchor\"></a>" +
                "<a id=\"anchor-no-href\"></a>" +
                "<area href=\"#\" id=\"area\">" +
                "<link href=\"style.css\" id=\"link\">");
            Assert.That(Match(":link", ById(doc, "anchor")), Is.True);
            Assert.That(Match(":link", ById(doc, "area")), Is.True);
            Assert.That(Match(":link", ById(doc, "anchor-no-href")), Is.False);
            Assert.That(Match(":link", ById(doc, "link")), Is.False);
            Assert.That(Match(":any-link", ById(doc, "anchor")), Is.True);
            Assert.That(Match(":any-link", ById(doc, "area")), Is.True);
            Assert.That(Match(":any-link", ById(doc, "anchor-no-href")), Is.False);
            Assert.That(Match(":any-link", ById(doc, "link")), Is.False);
        }

        [Test]
        public void Target_pseudo_matches_target_state() {
            var doc = Parse("<section id=\"target\"></section><section id=\"other\"></section>");
            var state = new FakeState();
            state.Set(ById(doc, "target"), ElementState.Target);

            Assert.That(Match(":target", ById(doc, "target"), state), Is.True);
            Assert.That(Match(":target", ById(doc, "other"), state), Is.False);
        }

        [Test]
        public void Scope_pseudo_matches_document_root_or_explicit_scope_root() {
            // Parser synthesises <html><body> wrappers — the actual document
            // root is <html>, not the authored <main id="root">. `:scope`
            // without an explicit scope root matches the document root per
            // CSS Selectors §6.6.
            var doc = Parse("<main id=\"main\"><section id=\"scope\"><p id=\"child\"></p></section></main>");
            var html = FirstByTag(doc, "html");
            var main = ById(doc, "main");
            var scope = ById(doc, "scope");
            var child = ById(doc, "child");

            Assert.That(Match(":scope", html), Is.True);
            Assert.That(Match(":scope", main), Is.False);
            Assert.That(Match(":scope", child), Is.False);
            Assert.That(SelectorMatcher.Matches(SelectorParser.Parse(":scope > p"), child, null, scope), Is.True);
            Assert.That(SelectorMatcher.Matches(SelectorParser.Parse(":scope"), child, null, scope), Is.False);
        }

        [Test]
        public void Lang_pseudo_matches_inherited_language_ranges() {
            var doc = Parse("<section lang=\"en-US\"><p id=\"p\"></p></section><p lang=\"fr\" id=\"fr\"></p>");
            Assert.That(Match(":lang(en)", ById(doc, "p")), Is.True);
            Assert.That(Match(":lang(en-US)", ById(doc, "p")), Is.True);
            Assert.That(Match(":lang(fr)", ById(doc, "p")), Is.False);
            Assert.That(Match(":lang(en, fr)", ById(doc, "fr")), Is.True);
        }

        [Test]
        public void Dir_pseudo_matches_inherited_and_auto_direction() {
            var doc = Parse(
                "<section dir=\"rtl\"><p id=\"rtl\"></p></section>" +
                "<section dir=\"auto\" id=\"auto\">\u05E9\u05DC\u05D5\u05DD text</section>" +
                "<p id=\"ltr\"></p>");
            Assert.That(Match(":dir(rtl)", ById(doc, "rtl")), Is.True);
            Assert.That(Match(":dir(ltr)", ById(doc, "rtl")), Is.False);
            Assert.That(Match(":dir(rtl)", ById(doc, "auto")), Is.True);
            Assert.That(Match(":dir(ltr)", ById(doc, "ltr")), Is.True);
        }

        [Test]
        public void Dir_pseudo_auto_matches_dir_auto_attribute() {
            var doc = Parse(
                "<section dir=\"auto\" id=\"auto\">\u05E9\u05DC\u05D5\u05DD text</section>" +
                "<section dir=\"ltr\"><p id=\"ltr\"></p></section>" +
                "<section dir=\"rtl\"><p id=\"rtl\"></p></section>");
            Assert.That(Match(":dir(auto)", ById(doc, "auto")), Is.True);
            Assert.That(Match(":dir(auto)", ById(doc, "ltr")), Is.False);
            Assert.That(Match(":dir(auto)", ById(doc, "rtl")), Is.False);
            Assert.That(Match(":dir(ltr)", ById(doc, "ltr")), Is.True);
        }

        [Test]
        public void Dir_auto_skips_bidi_isolated_and_dir_attributed_descendants() {
            var doc = Parse(
                "<div dir=\"auto\" id=\"bdi\"><bdi>שלום</bdi>then ltr</div>" +
                "<div dir=\"auto\" id=\"inner\"><span dir=\"rtl\">שלום</span>then</div>" +
                "<div dir=\"auto\" id=\"plain\">hello world</div>" +
                "<div dir=\"auto\" id=\"script\"><script>שלום</script>then</div>" +
                "<div dir=\"auto\" id=\"style\"><style>שלום</style>then</div>");
            Assert.That(Match(":dir(ltr)", ById(doc, "bdi")), Is.True);
            Assert.That(Match(":dir(rtl)", ById(doc, "bdi")), Is.False);
            Assert.That(Match(":dir(ltr)", ById(doc, "inner")), Is.True);
            Assert.That(Match(":dir(rtl)", ById(doc, "inner")), Is.False);
            Assert.That(Match(":dir(ltr)", ById(doc, "plain")), Is.True);
            Assert.That(Match(":dir(ltr)", ById(doc, "script")), Is.True);
            Assert.That(Match(":dir(ltr)", ById(doc, "style")), Is.True);
        }

        [Test]
        public void Dir_auto_classifies_supplementary_plane_codepoints() {
            var doc = Parse(
                "<div dir=\"auto\" id=\"cypriot\">𐠀</div>" +
                "<div dir=\"auto\" id=\"linearb\">𐂀</div>" +
                "<div dir=\"auto\" id=\"hebrew\">שלום</div>");
            Assert.That(Match(":dir(rtl)", ById(doc, "cypriot")), Is.True);
            Assert.That(Match(":dir(ltr)", ById(doc, "cypriot")), Is.False);
            Assert.That(Match(":dir(ltr)", ById(doc, "linearb")), Is.True);
            Assert.That(Match(":dir(rtl)", ById(doc, "linearb")), Is.False);
            Assert.That(Match(":dir(rtl)", ById(doc, "hebrew")), Is.True);
            Assert.That(Match(":dir(ltr)", ById(doc, "hebrew")), Is.False);
        }

        [Test]
        public void Lang_pseudo_does_not_split_inside_quoted_range() {
            var doc = Parse("<p lang=\"en-US\" id=\"p\"></p>");
            Assert.That(Match(":lang(\"en,bogus\", en)", ById(doc, "p")), Is.True);
        }

        [Test]
        public void Range_pseudos_match_number_and_range_controls() {
            var doc = Parse(
                "<input type=\"number\" min=\"2\" max=\"5\" value=\"3\" id=\"ok\">" +
                "<input type=\"number\" min=\"2\" max=\"5\" value=\"7\" id=\"bad\">" +
                "<input type=\"range\" min=\"0\" max=\"10\" value=\"4\" id=\"slider\">" +
                "<input type=\"text\" min=\"2\" max=\"5\" value=\"7\" id=\"text\">");
            Assert.That(Match(":in-range", ById(doc, "ok")), Is.True);
            Assert.That(Match(":out-of-range", ById(doc, "ok")), Is.False);
            Assert.That(Match(":out-of-range", ById(doc, "bad")), Is.True);
            Assert.That(Match(":in-range", ById(doc, "slider")), Is.True);
            Assert.That(Match(":out-of-range", ById(doc, "text")), Is.False);
        }

        [Test]
        public void User_validation_pseudos_require_interaction_state() {
            var doc = Parse(
                "<input required id=\"empty\">" +
                "<input required value=\"ok\" id=\"filled\">" +
                "<input type=\"email\" value=\"bad\" id=\"email\">");
            var state = new FakeState();
            Assert.That(Match(":user-invalid", ById(doc, "empty"), state), Is.False);
            Assert.That(Match(":user-valid", ById(doc, "filled"), state), Is.False);

            state.Set(ById(doc, "empty"), ElementState.UserInteracted);
            state.Set(ById(doc, "filled"), ElementState.UserInteracted);
            state.Set(ById(doc, "email"), ElementState.UserInteracted);

            Assert.That(Match(":user-invalid", ById(doc, "empty"), state), Is.True);
            Assert.That(Match(":user-valid", ById(doc, "filled"), state), Is.True);
            Assert.That(Match(":user-invalid", ById(doc, "email"), state), Is.True);
            Assert.That(Match(":user-valid", ById(doc, "email"), state), Is.False);
        }

        [Test]
        public void Default_pseudo_matches_default_form_controls() {
            var doc = Parse(
                "<form>" +
                "<button id=\"submit1\"></button>" +
                "<button id=\"submit2\"></button>" +
                "<input type=\"checkbox\" checked id=\"check\">" +
                "<input type=\"radio\" name=\"r\" id=\"r1\">" +
                "<input type=\"radio\" name=\"r\" checked id=\"r2\">" +
                "<select><option id=\"plain\"></option><option selected id=\"selected\"></option></select>" +
                "</form>");

            Assert.That(Match(":default", ById(doc, "submit1")), Is.True);
            Assert.That(Match(":default", ById(doc, "submit2")), Is.False);
            Assert.That(Match(":default", ById(doc, "check")), Is.True);
            Assert.That(Match(":default", ById(doc, "r1")), Is.False);
            Assert.That(Match(":default", ById(doc, "r2")), Is.True);
            Assert.That(Match(":default", ById(doc, "plain")), Is.False);
            Assert.That(Match(":default", ById(doc, "selected")), Is.True);
        }

        [Test]
        public void Default_pseudo_matches_first_option_when_none_selected() {
            var doc = Parse(
                "<select>" +
                "<option id=\"a\"></option>" +
                "<option id=\"b\"></option>" +
                "<option id=\"c\"></option>" +
                "</select>");

            Assert.That(Match(":default", ById(doc, "a")), Is.True);
            Assert.That(Match(":default", ById(doc, "b")), Is.False);
            Assert.That(Match(":default", ById(doc, "c")), Is.False);
        }

        [Test]
        public void Default_pseudo_prefers_explicit_selected_over_first_option() {
            var doc = Parse(
                "<select>" +
                "<option id=\"a\"></option>" +
                "<option selected id=\"b\"></option>" +
                "<option id=\"c\"></option>" +
                "</select>");

            Assert.That(Match(":default", ById(doc, "a")), Is.False);
            Assert.That(Match(":default", ById(doc, "b")), Is.True);
            Assert.That(Match(":default", ById(doc, "c")), Is.False);
        }

        [Test]
        public void Default_pseudo_skips_disabled_options() {
            var skipFirst = Parse(
                "<select>" +
                "<option disabled id=\"a\"></option>" +
                "<option id=\"b\"></option>" +
                "</select>");
            Assert.That(Match(":default", ById(skipFirst, "a")), Is.False);
            Assert.That(Match(":default", ById(skipFirst, "b")), Is.True);

            var disabledSelected = Parse(
                "<select>" +
                "<option disabled selected id=\"a\"></option>" +
                "<option id=\"b\"></option>" +
                "</select>");
            Assert.That(Match(":default", ById(disabledSelected, "a")), Is.False);
            Assert.That(Match(":default", ById(disabledSelected, "b")), Is.True);

            var firstEnabled = Parse(
                "<select>" +
                "<option id=\"a\"></option>" +
                "<option disabled id=\"b\"></option>" +
                "</select>");
            Assert.That(Match(":default", ById(firstEnabled, "a")), Is.True);
            Assert.That(Match(":default", ById(firstEnabled, "b")), Is.False);
        }

        [Test]
        public void Default_pseudo_spans_optgroups_in_document_order() {
            var noneSelected = Parse(
                "<select>" +
                "<optgroup><option id=\"x\"></option></optgroup>" +
                "<optgroup><option id=\"y\"></option></optgroup>" +
                "</select>");
            Assert.That(Match(":default", ById(noneSelected, "x")), Is.True);
            Assert.That(Match(":default", ById(noneSelected, "y")), Is.False);

            var selectedInSecondGroup = Parse(
                "<select>" +
                "<optgroup><option id=\"x\"></option></optgroup>" +
                "<optgroup><option selected id=\"y\"></option></optgroup>" +
                "</select>");
            Assert.That(Match(":default", ById(selectedInSecondGroup, "x")), Is.False);
            Assert.That(Match(":default", ById(selectedInSecondGroup, "y")), Is.True);

            var disabledSpansGroups = Parse(
                "<select>" +
                "<optgroup><option disabled id=\"x\"></option></optgroup>" +
                "<optgroup><option id=\"y\"></option></optgroup>" +
                "</select>");
            Assert.That(Match(":default", ById(disabledSpansGroups, "x")), Is.False);
            Assert.That(Match(":default", ById(disabledSpansGroups, "y")), Is.True);

            var mixedDirectAndGrouped = Parse(
                "<select>" +
                "<option id=\"a\"></option>" +
                "<optgroup><option id=\"b\"></option></optgroup>" +
                "</select>");
            Assert.That(Match(":default", ById(mixedDirectAndGrouped, "a")), Is.True);
            Assert.That(Match(":default", ById(mixedDirectAndGrouped, "b")), Is.False);
        }

        [Test]
        public void Pseudo_element_match_returns_false() {
            var doc = Parse("<p id=\"x\"></p>");
            var sel = SelectorParser.Parse("p::before");
            Assert.That(SelectorMatcher.Matches(sel, ById(doc, "x")), Is.False);
            Assert.That(sel.PseudoElement, Is.EqualTo("before"));
        }

        [Test]
        public void Root_matches_first_element_child_of_document() {
            var doc = Parse("<html><body><p id=\"x\"></p></body></html>");
            var html = FirstByTag(doc, "html");
            Assert.That(Match(":root", html), Is.True);
            Assert.That(Match(":root", ById(doc, "x")), Is.False);
        }

        [Test]
        public void Descendant_combinator_does_not_cross_unrelated_branches() {
            var doc = Parse("<div><span></span></div><p id=\"x\"></p>");
            Assert.That(Match("div span", ById(doc, "x")), Is.False);
        }

        [Test]
        public void Compound_after_combinator() {
            var doc = Parse("<div><p class=\"hi\" id=\"x\"></p></div>");
            Assert.That(Match("div > p.hi", ById(doc, "x")), Is.True);
            Assert.That(Match("div > p.bye", ById(doc, "x")), Is.False);
        }

        [Test]
        public void Multi_level_descendant() {
            var doc = Parse("<a><b><c><d id=\"x\"></d></c></b></a>");
            Assert.That(Match("a d", ById(doc, "x")), Is.True);
            Assert.That(Match("a c d", ById(doc, "x")), Is.True);
            Assert.That(Match("b c d", ById(doc, "x")), Is.True);
        }

        [Test]
        public void Nth_last_child_positive() {
            var doc = Parse("<div><p></p><p></p><p id=\"x\"></p></div>");
            Assert.That(Match(":nth-last-child(1)", ById(doc, "x")), Is.True);
        }

        [Test]
        public void Last_of_type_positive() {
            var doc = Parse("<div><p></p><span></span><p id=\"x\"></p></div>");
            Assert.That(Match("p:last-of-type", ById(doc, "x")), Is.True);
        }

        [Test]
        public void Only_of_type_when_one() {
            var doc = Parse("<div><span></span><p id=\"x\"></p><span></span></div>");
            Assert.That(Match("p:only-of-type", ById(doc, "x")), Is.True);
        }
    }
}
