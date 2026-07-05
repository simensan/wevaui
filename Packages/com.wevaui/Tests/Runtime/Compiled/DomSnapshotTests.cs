using NUnit.Framework;
using Weva.Compiled;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Compiled {
    public class DomSnapshotTests {
        static DomSnapshot Build(string html, out SymbolTable symbols) {
            var doc = HtmlParser.Parse(html);
            symbols = new SymbolTable();
            return DomSnapshot.Build(doc, symbols);
        }

        // Fragment HTML (no explicit `<html>`) is wrapped in synthetic
        // `<html><body>` by the parser. These tests assert tree shape
        // relative to "the place where the author's content lives" — i.e.
        // the synthetic `<body>` element. Return its NodeId, or RootId
        // if the snapshot is empty (no wrapper synthesised).
        static int ContentParent(DomSnapshot snap, SymbolTable sym) {
            int html = snap.FirstChild[snap.RootId];
            if (html < 0) return snap.RootId;
            int sBody = sym.Intern("body");
            for (int c = snap.FirstChild[html]; c >= 0; c = snap.NextSibling[c]) {
                if (snap.TagSymbols[c] == sBody) return c;
            }
            return html;
        }

        [Test]
        public void Empty_document_yields_single_node() {
            var snap = Build("", out _);
            Assert.That(snap.NodeCount, Is.EqualTo(1));
            Assert.That(snap.Kinds[snap.RootId], Is.EqualTo(NodeKind.Document));
            Assert.That(snap.Parent[snap.RootId], Is.EqualTo(-1));
        }

        [Test]
        public void Single_root_element_links() {
            var snap = Build("<div></div>", out var sym);
            int root = snap.RootId;
            Assert.That(snap.Kinds[root], Is.EqualTo(NodeKind.Document));
            int body = ContentParent(snap, sym);
            int firstChild = snap.FirstChild[body];
            Assert.That(firstChild, Is.GreaterThan(-1));
            Assert.That(snap.Kinds[firstChild], Is.EqualTo(NodeKind.Element));
            Assert.That(snap.Parent[firstChild], Is.EqualTo(body));
            Assert.That(snap.NextSibling[firstChild], Is.EqualTo(-1));
        }

        [Test]
        public void Tag_symbol_round_trips_through_table() {
            var snap = Build("<section></section>", out var sym);
            int firstChild = snap.FirstChild[ContentParent(snap, sym)];
            Assert.That(sym.Get(snap.TagSymbols[firstChild]), Is.EqualTo("section"));
        }

        [Test]
        public void Sibling_links_form_chain_in_document_order() {
            var snap = Build("<div></div><span></span><p></p>", out var sym);
            int body = ContentParent(snap, sym);
            int a = snap.FirstChild[body];
            int b = snap.NextSibling[a];
            int c = snap.NextSibling[b];
            Assert.That(snap.NextSibling[c], Is.EqualTo(-1));
            Assert.That(sym.Get(snap.TagSymbols[a]), Is.EqualTo("div"));
            Assert.That(sym.Get(snap.TagSymbols[b]), Is.EqualTo("span"));
            Assert.That(sym.Get(snap.TagSymbols[c]), Is.EqualTo("p"));
        }

        [Test]
        public void Parent_pointers_correct() {
            var snap = Build("<div><span><b></b></span></div>", out var sym);
            int body = ContentParent(snap, sym);
            int divId = snap.FirstChild[body];
            int spanId = snap.FirstChild[divId];
            int bId = snap.FirstChild[spanId];
            Assert.That(snap.Parent[bId], Is.EqualTo(spanId));
            Assert.That(snap.Parent[spanId], Is.EqualTo(divId));
            Assert.That(snap.Parent[divId], Is.EqualTo(body));
        }

        [Test]
        public void Id_attribute_populates_id_symbol() {
            var snap = Build("<div id=\"hero\"></div>", out var sym);
            int divId = snap.FirstChild[ContentParent(snap, sym)];
            Assert.That(snap.IdSymbols[divId], Is.Not.EqualTo(0));
            Assert.That(sym.Get(snap.IdSymbols[divId]), Is.EqualTo("hero"));
        }

        [Test]
        public void Element_without_id_has_zero_id_symbol() {
            var snap = Build("<div></div>", out var sym);
            int divId = snap.FirstChild[ContentParent(snap, sym)];
            Assert.That(snap.IdSymbols[divId], Is.EqualTo(0));
        }

        [Test]
        public void Class_list_extracts_tokens_in_order() {
            var snap = Build("<div class=\"a b c\"></div>", out var sym);
            int divId = snap.FirstChild[ContentParent(snap, sym)];
            var classes = snap.ClassesOf(divId);
            Assert.That(classes.Length, Is.EqualTo(3));
            Assert.That(sym.Get(classes[0]), Is.EqualTo("a"));
            Assert.That(sym.Get(classes[1]), Is.EqualTo("b"));
            Assert.That(sym.Get(classes[2]), Is.EqualTo("c"));
        }

        [Test]
        public void Class_list_handles_extra_whitespace() {
            var snap = Build("<div class=\"  foo   bar  \"></div>", out var sym);
            int divId = snap.FirstChild[ContentParent(snap, sym)];
            var classes = snap.ClassesOf(divId);
            Assert.That(classes.Length, Is.EqualTo(2));
            Assert.That(sym.Get(classes[0]), Is.EqualTo("foo"));
            Assert.That(sym.Get(classes[1]), Is.EqualTo("bar"));
        }

        [Test]
        public void Attributes_are_stored_with_symbol_ids() {
            var snap = Build("<a href=\"about\" data-x=\"1\"></a>", out var sym);
            int aId = snap.FirstChild[ContentParent(snap, sym)];
            Assert.That(snap.AttributeCount[aId], Is.GreaterThanOrEqualTo(2));
            int hrefSym = sym.Intern("href");
            int v = snap.GetAttributeValue(aId, hrefSym);
            Assert.That(v, Is.Not.EqualTo(0));
            Assert.That(sym.Get(v), Is.EqualTo("about"));
        }

        [Test]
        public void Text_node_data_preserved_in_text_values() {
            var snap = Build("<p>hello world</p>", out var sym);
            int pId = snap.FirstChild[ContentParent(snap, sym)];
            int textId = snap.FirstChild[pId];
            Assert.That(snap.Kinds[textId], Is.EqualTo(NodeKind.Text));
            Assert.That(snap.TextValues[textId], Is.EqualTo("hello world"));
        }

        [Test]
        public void Non_text_nodes_have_null_text_value() {
            var snap = Build("<div></div>", out var sym);
            int divId = snap.FirstChild[ContentParent(snap, sym)];
            Assert.That(snap.TextValues[divId], Is.Null);
        }

        [Test]
        public void Document_order_preserved_for_deeper_tree() {
            var snap = Build("<a><b></b><c><d></d></c><e></e></a>", out var sym);
            int aId = snap.FirstChild[ContentParent(snap, sym)];
            int bId = snap.FirstChild[aId];
            int cId = snap.NextSibling[bId];
            int eId = snap.NextSibling[cId];
            int dId = snap.FirstChild[cId];
            Assert.That(sym.Get(snap.TagSymbols[bId]), Is.EqualTo("b"));
            Assert.That(sym.Get(snap.TagSymbols[cId]), Is.EqualTo("c"));
            Assert.That(sym.Get(snap.TagSymbols[dId]), Is.EqualTo("d"));
            Assert.That(sym.Get(snap.TagSymbols[eId]), Is.EqualTo("e"));
            Assert.That(snap.NextSibling[eId], Is.EqualTo(-1));
        }

        [Test]
        public void Managed_node_sidecar_populated_for_elements() {
            var doc = HtmlParser.Parse("<div></div>");
            var sym = new SymbolTable();
            var snap = DomSnapshot.Build(doc, sym);
            int divId = snap.FirstChild[ContentParent(snap, sym)];
            Assert.That(snap.ManagedNodes[divId], Is.InstanceOf<Element>());
            Assert.That(((Element)snap.ManagedNodes[divId]).TagName, Is.EqualTo("div"));
        }
    }
}
