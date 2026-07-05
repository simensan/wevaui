using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // Audit CX2: the shape-keyed match cache hashed tag/id/class/attrs/
    // ancestors/state but NOT sibling position, so position-dependent
    // selectors mis-matched across identical-shape siblings — the first
    // sibling's matched-declaration set was served to ALL of them:
    // `li:nth-child(odd)` painted every row, `p + p` matched neither p.
    //
    // NOTE: elements here are deliberately ID-LESS — an id is folded into
    // the shape key and gives every element a distinct key, hiding the
    // collision (that's why earlier suites never caught this).
    //
    // The fix is two-tier (see HasDetector):
    //   - index-positional pseudos (:nth-child/:first/:last/:only-child/
    //     :empty) fold (sibling index, sibling count, own child count) into
    //     the shape key — for the element AND every ancestor in the chain;
    //   - composition-dependent selectors (sibling combinators, of-type
    //     pseudos) disable shape sharing for the sheet outright (their
    //     outcome depends on WHICH tags precede, unrepresentable in a key).
    public class ShapeCachePositionTests {
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        static (CascadeEngine engine, Document doc) Setup(string css, string html) {
            var doc = HtmlParser.Parse(html);
            var engine = new CascadeEngine(new List<OriginatedStylesheet> { Author(css) });
            engine.ComputeAll(doc);
            return (engine, doc);
        }

        // n-th ELEMENT descendant of the document in tree order (0-based) —
        // id-free element lookup so the shape keys stay collision-prone.
        static Element NthElement(Node root, ref int counter, int target) {
            for (int i = 0; i < root.Children.Count; i++) {
                if (root.Children[i] is Element el) {
                    if (counter == target) return el;
                    counter++;
                    var found = NthElement(el, ref counter, target);
                    if (found != null) return found;
                } else {
                    var found = NthElement(root.Children[i], ref counter, target);
                    if (found != null) return found;
                }
            }
            return null;
        }

        static Element Nth(Document doc, int index) {
            int c = 0;
            return NthElement(doc, ref c, index);
        }

        static List<Element> ElementsByTag(Document doc, string tag) {
            var result = new List<Element>();
            void Walk(Node n) {
                for (int i = 0; i < n.Children.Count; i++) {
                    if (n.Children[i] is Element el) {
                        if (string.Equals(el.TagName, tag, System.StringComparison.OrdinalIgnoreCase)) result.Add(el);
                        Walk(el);
                    } else Walk(n.Children[i]);
                }
            }
            Walk(doc);
            return result;
        }

        [Test]
        public void Nth_child_odd_zebra_stripes_identical_siblings() {
            // The audit's run-confirmed repro: pre-CX2 ALL <li> were red
            // (row 1's match set served to every identical sibling).
            var (engine, doc) = Setup(
                "li:nth-child(odd) { color: red; }",
                "<ul><li>a</li><li>b</li><li>c</li><li>d</li></ul>");
            var lis = ElementsByTag(doc, "li");
            Assert.That(lis.Count, Is.EqualTo(4));
            Assert.That(engine.Compute(lis[0]).Get("color"), Is.EqualTo("red"), "row 1 (odd)");
            Assert.That(engine.Compute(lis[1]).Get("color"), Is.EqualTo("black"), "row 2 (even) — pre-CX2 this was red");
            Assert.That(engine.Compute(lis[2]).Get("color"), Is.EqualTo("red"), "row 3 (odd)");
            Assert.That(engine.Compute(lis[3]).Get("color"), Is.EqualTo("black"), "row 4 (even)");
        }

        [Test]
        public void Adjacent_sibling_combinator_distinguishes_identical_siblings() {
            // Pre-CX2: NEITHER p was blue (the first p's empty match set was
            // shared with the second).
            var (engine, doc) = Setup(
                "p + p { color: blue; }",
                "<div><p>a</p><p>b</p></div>");
            var ps = ElementsByTag(doc, "p");
            Assert.That(ps.Count, Is.EqualTo(2));
            Assert.That(engine.Compute(ps[0]).Get("color"), Is.EqualTo("black"), "no preceding sibling");
            Assert.That(engine.Compute(ps[1]).Get("color"), Is.EqualTo("blue"), "has a preceding p — pre-CX2 this was black");
        }

        [Test]
        public void First_and_last_child_pick_exactly_the_edge_rows() {
            var (engine, doc) = Setup(
                "li:first-child { color: red; } li:last-child { color: blue; }",
                "<ul><li>1</li><li>2</li><li>3</li></ul>");
            var lis = ElementsByTag(doc, "li");
            Assert.That(engine.Compute(lis[0]).Get("color"), Is.EqualTo("red"));
            Assert.That(engine.Compute(lis[1]).Get("color"), Is.EqualTo("black"));
            Assert.That(engine.Compute(lis[2]).Get("color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Nth_of_type_respects_preceding_sibling_composition() {
            // Two <p> at the SAME element index (1) whose of-type index
            // differs because the preceding sibling's tag differs. The index
            // fold can't represent this — the sheet must disable sharing.
            var (engine, doc) = Setup(
                "p:nth-of-type(2) { color: red; }",
                "<section><div><p>x</p><p>a</p></div><div><span>x</span><p>b</p></div></section>");
            var ps = ElementsByTag(doc, "p");
            Assert.That(ps.Count, Is.EqualTo(3));
            Assert.That(engine.Compute(ps[1]).Get("color"), Is.EqualTo("red"), "second p of its parent");
            Assert.That(engine.Compute(ps[2]).Get("color"), Is.EqualTo("black"),
                "FIRST p of its parent despite equal element index — composition-dependent");
        }

        [Test]
        public void Empty_pseudo_distinguishes_identical_shape_elements() {
            var (engine, doc) = Setup(
                "div.box:empty { color: red; }",
                "<div><div class=\"box\"></div><div class=\"box\">text</div></div>");
            var boxes = ElementsByTag(doc, "div");
            // boxes[0] is the wrapper; [1] empty box; [2] non-empty box.
            Assert.That(engine.Compute(boxes[1]).Get("color"), Is.EqualTo("red"), "empty box");
            Assert.That(engine.Compute(boxes[2]).Get("color"), Is.EqualTo("black"), "non-empty identical-shape box");
        }

        [Test]
        public void Ancestor_position_pseudo_distinguishes_descendants() {
            // The positional pseudo sits on an ANCESTOR compound: the two
            // <li> are identical AND their parents are identical-shape; only
            // the parents' positions differ. The key must fold ancestor
            // positions too.
            var (engine, doc) = Setup(
                "ul:first-child li { color: red; }",
                "<div><ul><li>a</li></ul><ul><li>b</li></ul></div>");
            var lis = ElementsByTag(doc, "li");
            Assert.That(lis.Count, Is.EqualTo(2));
            Assert.That(engine.Compute(lis[0]).Get("color"), Is.EqualTo("red"), "li under the first ul");
            Assert.That(engine.Compute(lis[1]).Get("color"), Is.EqualTo("black"), "li under the second ul");
        }

        [Test]
        public void Shape_sharing_stays_active_for_position_free_sheets() {
            // The fix must not tax the common case: a sheet with no
            // positional selectors keeps sharing matches across identical
            // siblings (hits > 0 across the 6 identical <li>).
            var (engine, doc) = Setup(
                "li { color: red; } .never { color: blue; }",
                "<ul><li>1</li><li>2</li><li>3</li><li>4</li><li>5</li><li>6</li></ul>");
            Assert.That(engine.ShapeCacheHits, Is.GreaterThan(0),
                "identical siblings must still share match sets when no selector is position-dependent");
        }
    }
}
