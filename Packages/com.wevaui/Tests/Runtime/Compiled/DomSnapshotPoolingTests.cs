using System;
using NUnit.Framework;
using Weva.Compiled;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Compiled {
    // DomSnapshot's Refill API recycles the 11 typed arrays + the symbol table
    // across rebuilds. These tests verify behavioural equivalence with a fresh
    // Build, capacity reuse semantics, and zero-alloc steady state on a stable
    // tree shape.
    [Category("compiled")]
    public class DomSnapshotPoolingTests {
        static DomSnapshot Snap(string html, SymbolTable sym) {
            var doc = HtmlParser.Parse(html);
            return DomSnapshot.Build(doc, sym);
        }

        // Fragment HTML is wrapped in synthetic `<html><body>` by the parser
        // — return the body's NodeId so tests can index author content
        // without caring about the wrapper.
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
        public void Refill_on_fresh_snapshot_matches_static_Build() {
            var doc = HtmlParser.Parse("<div class=\"a\"><span>hi</span></div>");
            var sym = new SymbolTable();
            var fresh = DomSnapshot.Build(doc, sym);

            var sym2 = new SymbolTable();
            var refill = DomSnapshot.Build(HtmlParser.Parse(""), sym2);
            refill.Refill(doc, sym2);

            Assert.That(refill.NodeCount, Is.EqualTo(fresh.NodeCount));
            for (int i = 0; i < fresh.NodeCount; i++) {
                Assert.That(refill.Kinds[i], Is.EqualTo(fresh.Kinds[i]), $"kind@{i}");
                Assert.That(refill.Parent[i], Is.EqualTo(fresh.Parent[i]), $"parent@{i}");
                Assert.That(refill.FirstChild[i], Is.EqualTo(fresh.FirstChild[i]), $"firstChild@{i}");
                Assert.That(refill.NextSibling[i], Is.EqualTo(fresh.NextSibling[i]), $"nextSibling@{i}");
            }
        }

        [Test]
        public void Refill_after_dom_mutation_produces_correct_snapshot() {
            var sym = new SymbolTable();
            var snap = Snap("<div><span>x</span></div>", sym);
            int firstNodeCount = snap.NodeCount;

            // Replace the document with a larger one.
            var biggerDoc = HtmlParser.Parse("<div><span>x</span><span>y</span><p>z</p></div>");
            snap.Refill(biggerDoc, sym);
            Assert.That(snap.NodeCount, Is.GreaterThan(firstNodeCount));

            // Walk the snapshot and ensure tag symbols round-trip.
            int divId = snap.FirstChild[ContentParent(snap, sym)];
            Assert.That(sym.Get(snap.TagSymbols[divId]), Is.EqualTo("div"));
            int firstSpan = snap.FirstChild[divId];
            int secondSpan = snap.NextSibling[firstSpan];
            int p = snap.NextSibling[secondSpan];
            Assert.That(sym.Get(snap.TagSymbols[firstSpan]), Is.EqualTo("span"));
            Assert.That(sym.Get(snap.TagSymbols[secondSpan]), Is.EqualTo("span"));
            Assert.That(sym.Get(snap.TagSymbols[p]), Is.EqualTo("p"));
        }

        [Test]
        public void Refill_to_smaller_doc_keeps_capacity() {
            var sym = new SymbolTable();
            var bigDoc = HtmlParser.Parse(BuildHtml(50));
            var snap = DomSnapshot.Build(bigDoc, sym);
            int bigCap = snap.Kinds.Length;
            int bigNodeCount = snap.NodeCount;

            var smallDoc = HtmlParser.Parse("<div></div>");
            snap.Refill(smallDoc, sym);
            Assert.That(snap.NodeCount, Is.LessThan(bigNodeCount));
            // Backing arrays must NOT shrink — that's the whole point of pooling.
            Assert.That(snap.Kinds.Length, Is.EqualTo(bigCap));
            Assert.That(snap.TagSymbols.Length, Is.EqualTo(bigCap));
            Assert.That(snap.Parent.Length, Is.EqualTo(bigCap));
        }

        [Test]
        public void Refill_grows_capacity_when_doc_expands() {
            var sym = new SymbolTable();
            var smallDoc = HtmlParser.Parse("<div></div>");
            var snap = DomSnapshot.Build(smallDoc, sym);
            int smallCap = snap.Kinds.Length;

            var bigDoc = HtmlParser.Parse(BuildHtml(100));
            snap.Refill(bigDoc, sym);
            Assert.That(snap.Kinds.Length, Is.GreaterThan(smallCap));
            Assert.That(snap.NodeCount, Is.GreaterThan(smallCap));
        }

        [Test]
        public void Multiple_refills_with_same_doc_allocate_zero_after_warmup() {
            var sym = new SymbolTable();
            var doc = HtmlParser.Parse(BuildHtml(20));
            var snap = DomSnapshot.Build(doc, sym);
            // Warmup so any first-time array growth is paid before measuring.
            for (int i = 0; i < 5; i++) snap.Refill(doc, sym);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long before = GC.GetTotalMemory(false);
            const int iters = 100;
            for (int i = 0; i < iters; i++) snap.Refill(doc, sym);
            long after = GC.GetTotalMemory(false);
            long perCall = (after - before) / iters;

            // Steady-state target: zero allocation per call. Allow a small
            // ceiling so JIT/test-harness noise doesn't flake the gate.
            Assert.That(perCall, Is.LessThan(1024),
                $"Refill allocated {perCall} B/call; expected < 1 KB");
        }

        [Test]
        public void SymbolTable_interning_preserved_across_refills() {
            var sym = new SymbolTable();
            var doc1 = HtmlParser.Parse("<div class=\"foo\"></div>");
            var snap = DomSnapshot.Build(doc1, sym);
            int divId1 = snap.FirstChild[ContentParent(snap, sym)];
            int divTag1 = snap.TagSymbols[divId1];

            // Refill against a different doc that re-uses "div" and "foo".
            var doc2 = HtmlParser.Parse("<div class=\"foo\"><div class=\"foo\"></div></div>");
            snap.Refill(doc2, sym);
            int divId2 = snap.FirstChild[ContentParent(snap, sym)];
            int divTag2 = snap.TagSymbols[divId2];

            // Same intern table => same symbol id for "div".
            Assert.That(divTag2, Is.EqualTo(divTag1));
            Assert.That(sym.Get(divTag2), Is.EqualTo("div"));
        }

        [Test]
        public void Refill_resets_link_arrays_for_smaller_doc() {
            var sym = new SymbolTable();
            // First fill with a deep tree so link arrays have non-(-1) entries
            // beyond the new node range.
            var doc1 = HtmlParser.Parse(BuildHtml(20));
            var snap = DomSnapshot.Build(doc1, sym);

            // Capture the pre-shrink links beyond what the small doc will use.
            var doc2 = HtmlParser.Parse("<div></div>");
            snap.Refill(doc2, sym);
            // For every live node, links should be valid (-1 sentinel or in-range).
            for (int i = 0; i < snap.NodeCount; i++) {
                int p = snap.Parent[i];
                int fc = snap.FirstChild[i];
                int ns = snap.NextSibling[i];
                Assert.That(p == -1 || (p >= 0 && p < snap.NodeCount), $"parent@{i}={p}");
                Assert.That(fc == -1 || (fc >= 0 && fc < snap.NodeCount), $"firstChild@{i}={fc}");
                Assert.That(ns == -1 || (ns >= 0 && ns < snap.NodeCount), $"nextSibling@{i}={ns}");
            }
        }

        [Test]
        public void Refill_then_fresh_Build_produces_independent_snapshots() {
            var sym = new SymbolTable();
            var doc = HtmlParser.Parse("<div><span></span></div>");
            var pooled = DomSnapshot.Build(doc, sym);
            pooled.Refill(doc, sym);

            var fresh = DomSnapshot.Build(doc, sym);
            Assert.That(pooled.NodeCount, Is.EqualTo(fresh.NodeCount));
            // Fresh and pooled are distinct instances.
            Assert.That(fresh, Is.Not.SameAs(pooled));
        }

        [Test]
        public void Concurrent_pooled_snapshots_dont_interfere() {
            var sym1 = new SymbolTable();
            var sym2 = new SymbolTable();
            var doc1 = HtmlParser.Parse("<a></a><b></b>");
            var doc2 = HtmlParser.Parse("<x><y></y></x>");
            var snap1 = DomSnapshot.Build(doc1, sym1);
            var snap2 = DomSnapshot.Build(doc2, sym2);

            // Refill each independently with a different document.
            snap1.Refill(HtmlParser.Parse("<p></p>"), sym1);
            snap2.Refill(HtmlParser.Parse("<q><r></r></q>"), sym2);

            int s1RootChild = snap1.FirstChild[ContentParent(snap1, sym1)];
            int s2RootChild = snap2.FirstChild[ContentParent(snap2, sym2)];
            Assert.That(sym1.Get(snap1.TagSymbols[s1RootChild]), Is.EqualTo("p"));
            Assert.That(sym2.Get(snap2.TagSymbols[s2RootChild]), Is.EqualTo("q"));
        }

        [Test]
        public void SnapshotMatcher_works_against_refilled_snapshot() {
            var sym = new SymbolTable();
            var sel = SelectorParser.Parse(".bar");
            var index = new SelectorIndex(sym, new[] { sel });

            var doc1 = HtmlParser.Parse("<div class=\"foo\"></div>");
            var snap = DomSnapshot.Build(doc1, sym);
            var doc2 = HtmlParser.Parse("<div class=\"bar\"></div><span class=\"bar\"></span>");
            snap.Refill(doc2, sym);

            int firstChild = snap.FirstChild[ContentParent(snap, sym)];
            int secondChild = snap.NextSibling[firstChild];
            var buf = new IntsBuffer();
            var output = new System.Collections.Generic.List<int>();
            SnapshotMatcher.MatchInto(snap, firstChild, index,
                new[] { sel }, null, buf, output);
            Assert.That(output, Has.Count.EqualTo(1));
            output.Clear();
            buf.Reset();
            SnapshotMatcher.MatchInto(snap, secondChild, index,
                new[] { sel }, null, buf, output);
            Assert.That(output, Has.Count.EqualTo(1));
        }

        [Test]
        public void Refill_zeros_class_and_attribute_bookkeeping_for_pruned_nodes() {
            var sym = new SymbolTable();
            // First doc has many classes/attrs; second has none.
            var doc1 = HtmlParser.Parse("<div class=\"a b c\" data-x=\"1\" data-y=\"2\"></div>");
            var snap = DomSnapshot.Build(doc1, sym);
            int divId1 = snap.FirstChild[ContentParent(snap, sym)];
            Assert.That(snap.ClassRangeCount[divId1], Is.EqualTo(3));
            Assert.That(snap.AttributeCount[divId1], Is.GreaterThan(0));

            var doc2 = HtmlParser.Parse("<p></p>");
            snap.Refill(doc2, sym);
            int pId = snap.FirstChild[ContentParent(snap, sym)];
            Assert.That(snap.ClassRangeCount[pId], Is.EqualTo(0));
            Assert.That(snap.AttributeCount[pId], Is.EqualTo(0));
            Assert.That(snap.FirstAttribute[pId], Is.EqualTo(-1));
        }

        [Test]
        public void Refill_text_value_propagation() {
            var sym = new SymbolTable();
            var snap = Snap("<p>before</p>", sym);
            var doc2 = HtmlParser.Parse("<p>after</p>");
            snap.Refill(doc2, sym);
            int pId = snap.FirstChild[ContentParent(snap, sym)];
            int textId = snap.FirstChild[pId];
            Assert.That(snap.Kinds[textId], Is.EqualTo(NodeKind.Text));
            Assert.That(snap.TextValues[textId], Is.EqualTo("after"));
        }

        [Test]
        public void Refill_clears_text_value_when_node_becomes_element() {
            var sym = new SymbolTable();
            var snap = Snap("<p>hello</p>", sym);

            // Replace the leaf text with an element so the same NodeId switches kind.
            var doc2 = HtmlParser.Parse("<p><span></span></p>");
            snap.Refill(doc2, sym);
            int pId = snap.FirstChild[ContentParent(snap, sym)];
            int childId = snap.FirstChild[pId];
            Assert.That(snap.Kinds[childId], Is.EqualTo(NodeKind.Element));
            Assert.That(snap.TextValues[childId], Is.Null);
        }

        static string BuildHtml(int n) {
            var sb = new System.Text.StringBuilder();
            sb.Append("<div class=\"root\">");
            for (int i = 0; i < n; i++) {
                sb.Append("<span class=\"item\" data-i=\"").Append(i).Append("\">item ").Append(i).Append("</span>");
            }
            sb.Append("</div>");
            return sb.ToString();
        }
    }
}
