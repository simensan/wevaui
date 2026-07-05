using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using NUnit.Framework;
using Weva.Compiled;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Compiled {
    public class SnapshotMatcherTests {
        static List<int> ManagedMatchSet(Element e, IReadOnlyList<CompiledSelector> sels) {
            var hits = new List<int>();
            for (int i = 0; i < sels.Count; i++) {
                if (SelectorMatcher.Matches(sels[i], e)) hits.Add(i);
            }
            return hits;
        }

        static (DomSnapshot, SelectorIndex, List<CompiledSelector>, SymbolTable) Setup(string html, params string[] selectors) {
            var doc = HtmlParser.Parse(html);
            var sym = new SymbolTable();
            var snap = DomSnapshot.Build(doc, sym);
            var sels = new List<CompiledSelector>();
            foreach (var s in selectors) sels.Add(SelectorParser.Parse(s));
            var idx = new SelectorIndex(sym, sels);
            return (snap, idx, sels, sym);
        }

        // HtmlParser synthesizes <html><body> around fragments. Navigate through
        // the synthetic wrapper to the <body> node so tests get the author's
        // first real element instead of the synthetic <html>.
        static int BodyNode(DomSnapshot snap, SymbolTable sym) {
            int html = snap.FirstChild[snap.RootId];
            if (html < 0) return snap.RootId;
            int sBody = sym.Intern("body");
            for (int c = snap.FirstChild[html]; c >= 0; c = snap.NextSibling[c]) {
                if (snap.TagSymbols[c] == sBody) return c;
            }
            return html;
        }

        [Test]
        public void Single_tag_selector_matches_element() {
            var (snap, idx, sels, sym) = Setup("<div></div>", "div");
            int divId = snap.FirstChild[BodyNode(snap, sym)];
            var hits = SnapshotMatcher.Match(snap, divId, idx, sels);
            Assert.That(hits, Is.EquivalentTo(new[] { 0 }));
        }

        [Test]
        public void Tag_selector_does_not_match_other_tag() {
            var (snap, idx, sels, sym) = Setup("<div></div>", "span");
            int divId = snap.FirstChild[BodyNode(snap, sym)];
            var hits = SnapshotMatcher.Match(snap, divId, idx, sels);
            Assert.That(hits, Is.Empty);
        }

        [Test]
        public void Class_selector_matches_correct_element() {
            var (snap, idx, sels, sym) = Setup("<div class=\"btn\"></div>", ".btn", ".other", "div");
            int divId = snap.FirstChild[BodyNode(snap, sym)];
            var hits = SnapshotMatcher.Match(snap, divId, idx, sels);
            Assert.That(hits, Is.EquivalentTo(new[] { 0, 2 }));
        }

        [Test]
        public void Id_selector_matches() {
            var (snap, idx, sels, sym) = Setup("<div id=\"hero\"></div>", "#hero", "#nope");
            int divId = snap.FirstChild[BodyNode(snap, sym)];
            var hits = SnapshotMatcher.Match(snap, divId, idx, sels);
            Assert.That(hits, Is.EquivalentTo(new[] { 0 }));
        }

        [Test]
        public void Descendant_combinator_verified_through_managed_matcher() {
            var (snap, idx, sels, sym) = Setup("<section><a></a></section>", "section a", "header a");
            // find the <a>
            int sectionId = snap.FirstChild[BodyNode(snap, sym)];
            int aId = snap.FirstChild[sectionId];
            var hits = SnapshotMatcher.Match(snap, aId, idx, sels);
            Assert.That(hits, Is.EquivalentTo(new[] { 0 }));
        }

        [Test]
        public void Pseudo_class_first_child_handled_via_verifier() {
            var (snap, idx, sels, sym) = Setup("<ul><li></li><li></li></ul>", "li:first-child");
            int ulId = snap.FirstChild[BodyNode(snap, sym)];
            int li1 = snap.FirstChild[ulId];
            int li2 = snap.NextSibling[li1];
            Assert.That(SnapshotMatcher.Match(snap, li1, idx, sels), Is.EquivalentTo(new[] { 0 }));
            Assert.That(SnapshotMatcher.Match(snap, li2, idx, sels), Is.Empty);
        }

        [Test]
        public void Attribute_selector_matches() {
            var (snap, idx, sels, sym) = Setup("<input disabled />", "[disabled]");
            int inputId = snap.FirstChild[BodyNode(snap, sym)];
            var hits = SnapshotMatcher.Match(snap, inputId, idx, sels);
            Assert.That(hits, Is.EquivalentTo(new[] { 0 }));
        }

        [Test]
        public void Match_on_text_node_returns_nothing() {
            var (snap, idx, sels, sym) = Setup("<p>hi</p>", "p", "*");
            int pId = snap.FirstChild[BodyNode(snap, sym)];
            int textId = snap.FirstChild[pId];
            var hits = SnapshotMatcher.Match(snap, textId, idx, sels);
            Assert.That(hits, Is.Empty);
        }

        [Test]
        public void Attribute_substring_operators_use_ordinal_comparison_under_turkish_culture() {
            // Regression for M7: AttributeMatches must use StringComparison.Ordinal
            // (matching SelectorMatcher's contract) rather than the default CurrentCulture
            // overloads. Under tr-TR, `I`.ToLower() == `ı` (dotless), so culture-sensitive
            // comparisons would treat `userId` as if it ended in `ıd` and `IMG` as a prefix
            // of `img`, diverging from spec-mandated code-point comparison.
            var (snap, idx, sels, sym) = Setup(
                "<a href=\"IMG/photo.PNG\" lang=\"EN-us\" name=\"userID\" id=\"x\"></a>",
                "[href^=IMG]", "[href^=img]",
                "[href$=.PNG]", "[href$=.png]",
                "[href*=oto.P]", "[href*=oto.p]",
                "[lang|=EN]", "[lang|=en]");

            var originalCulture = Thread.CurrentThread.CurrentCulture;
            try {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("tr-TR");
                int aId = snap.FirstChild[BodyNode(snap, sym)];
                var hits = SnapshotMatcher.Match(snap, aId, idx, sels);
                hits.Sort();
                Assert.That(hits, Is.EqualTo(new List<int> { 0, 2, 4, 6 }));
            } finally {
                Thread.CurrentThread.CurrentCulture = originalCulture;
            }
        }

        [Test]
        public void Snapshot_matches_managed_matcher_byte_for_byte_on_50_rule_fixture() {
            // 50-rule fixture covering tags, classes, ids, attributes, combinators,
            // and pseudo-classes — exercises the universal-bucket fall-through path,
            // multi-class compounds, and combinators that the index can't pre-filter.
            string[] selectors = {
                "div", "span", "p", "a", "ul", "li", "section", "header", "footer", "nav",
                ".container", ".btn", ".btn-primary", ".sidebar", ".panel",
                "#hero", "#main", "#footer-id",
                "[disabled]", "[type=text]", "[data-active]",
                "div.container", "ul.list li", "section > p", ".btn.btn-primary",
                "header nav a", "section a:first-child", "li:last-child", "p:empty",
                ":hover", ":focus", ":root", "a:not(.disabled)",
                "section header h1", ".panel > .title", ".grid .cell.selected",
                ".btn:hover", "input[type=checkbox]", "*", "ul li.item",
                "section ~ footer", "header + nav", "nav a + a",
                "[data-x*=foo]", "[lang|=en]", "[name^=usr]", "[id$=el]",
                "div#main > .panel", "ul.list", "ol", "h1"
            };
            string html = @"
                <section id=""hero"" class=""container"">
                    <header><h1>Title</h1><nav><a href=""#"">A</a><a class=""disabled"">B</a></nav></header>
                    <main id=""main"">
                        <ul class=""list"">
                            <li class=""item""></li>
                            <li class=""item selected""></li>
                            <li class=""item""></li>
                        </ul>
                        <div class=""panel""><div class=""title""></div><p>Hello</p></div>
                        <div class=""btn btn-primary""></div>
                        <input type=""text"" name=""username"" />
                        <input type=""checkbox"" disabled data-active />
                    </main>
                    <footer id=""footer-id""><p></p></footer>
                </section>";

            var doc = HtmlParser.Parse(html);
            var sym = new SymbolTable();
            var snap = DomSnapshot.Build(doc, sym);
            var sels = new List<CompiledSelector>();
            foreach (var s in selectors) sels.Add(SelectorParser.Parse(s));
            var idx = new SelectorIndex(sym, sels);

            for (int nodeId = 0; nodeId < snap.NodeCount; nodeId++) {
                if (snap.Kinds[nodeId] != NodeKind.Element) continue;
                var elem = (Element)snap.ManagedNodes[nodeId];
                var managed = ManagedMatchSet(elem, sels);
                managed.Sort();
                var fast = SnapshotMatcher.Match(snap, nodeId, idx, sels);
                fast.Sort();
                Assert.That(fast, Is.EqualTo(managed),
                    $"Mismatch at node {nodeId} <{elem.TagName}>");
            }
        }
    }
}
