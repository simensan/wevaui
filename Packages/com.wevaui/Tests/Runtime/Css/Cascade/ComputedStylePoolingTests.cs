using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    public class ComputedStylePoolingTests {
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        [Test]
        public void Reset_clears_all_properties() {
            var cs = new ComputedStyle(new Element("div"));
            cs.Set("color", "red");
            cs.Set("font-size", "14px");
            cs.Set("--accent", "blue");
            Assert.That(cs.Count, Is.EqualTo(3));
            cs.Reset();
            Assert.That(cs.Count, Is.EqualTo(0));
            Assert.That(cs.Get("color"), Is.Null);
            Assert.That(cs.Get("font-size"), Is.Null);
            Assert.That(cs.Get("--accent"), Is.Null);
        }

        [Test]
        public void Reset_preserves_element_identity() {
            var e = new Element("div");
            var cs = new ComputedStyle(e);
            cs.Set("color", "red");
            cs.Reset();
            Assert.That(cs.Element, Is.SameAs(e));
        }

        [Test]
        public void CopyFrom_replicates_property_bag() {
            var src = new ComputedStyle(new Element("div"));
            src.Set("color", "red");
            src.Set("padding", "8px");
            var dst = new ComputedStyle(new Element("div"));
            dst.Set("color", "blue");
            dst.Set("border", "1px solid black");
            dst.CopyFrom(src);
            Assert.That(dst.Get("color"), Is.EqualTo("red"));
            Assert.That(dst.Get("padding"), Is.EqualTo("8px"));
            Assert.That(dst.Get("border"), Is.Null,
                "CopyFrom should overwrite, not merge");
        }

        [Test]
        public void CopyFrom_null_is_no_op() {
            var cs = new ComputedStyle(new Element("div"));
            cs.Set("color", "red");
            cs.CopyFrom(null);
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void ValueEquals_identifies_identical_styles() {
            var a = new ComputedStyle(new Element("div"));
            a.Set("color", "red");
            a.Set("padding", "8px");
            var b = new ComputedStyle(new Element("span"));
            b.Set("color", "red");
            b.Set("padding", "8px");
            Assert.That(a.ValueEquals(b), Is.True);
            Assert.That(b.ValueEquals(a), Is.True);
        }

        [Test]
        public void ValueEquals_detects_value_difference() {
            var a = new ComputedStyle(new Element("div"));
            a.Set("color", "red");
            var b = new ComputedStyle(new Element("div"));
            b.Set("color", "blue");
            Assert.That(a.ValueEquals(b), Is.False);
        }

        [Test]
        public void ValueEquals_detects_property_count_difference() {
            var a = new ComputedStyle(new Element("div"));
            a.Set("color", "red");
            var b = new ComputedStyle(new Element("div"));
            b.Set("color", "red");
            b.Set("padding", "8px");
            Assert.That(a.ValueEquals(b), Is.False);
        }

        [Test]
        public void ValueEquals_self_reference_is_true() {
            var cs = new ComputedStyle(new Element("div"));
            cs.Set("color", "red");
            Assert.That(cs.ValueEquals(cs), Is.True);
        }

        [Test]
        public void ValueEquals_null_other_is_false() {
            var cs = new ComputedStyle(new Element("div"));
            Assert.That(cs.ValueEquals(null), Is.False);
        }

        [Test]
        public void Two_consecutive_ComputeAll_runs_produce_structurally_identical_results() {
            var doc = HtmlParser.Parse(
                "<section><div id=\"a\" class=\"item\"></div><div id=\"b\"></div></section>");
            var sheet = Author(".item { color: red; padding: 8px; } div { font-size: 14px; }");
            var engine = new CascadeEngine(new[] { sheet }, true);

            var first = engine.ComputeAll(doc);
            var firstA = first[doc.GetElementById("a")];
            var firstB = first[doc.GetElementById("b")];

            var second = engine.ComputeAll(doc);
            var secondA = second[doc.GetElementById("a")];
            var secondB = second[doc.GetElementById("b")];

            // Cache hit: cascade returns the same instance both calls.
            Assert.That(secondA, Is.SameAs(firstA));
            Assert.That(secondB, Is.SameAs(firstB));
            Assert.That(secondA.ValueEquals(firstA), Is.True);
            Assert.That(secondB.ValueEquals(firstB), Is.True);
        }

        [Test]
        public void ResultMap_returns_same_dictionary_instance_across_calls() {
            var doc = HtmlParser.Parse("<div></div>");
            var engine = new CascadeEngine(new[] { Author("div { color: red; }") }, true);
            var first = engine.ComputeAll(doc);
            var second = engine.ComputeAll(doc);
            Assert.That(second, Is.SameAs(first),
                "ComputeAll must return the engine-owned reusable result map");
        }

        [Test]
        public void Persistent_result_map_drops_invalidated_entries() {
            var doc = HtmlParser.Parse("<div id=\"a\"></div><div id=\"b\"></div>");
            var engine = new CascadeEngine(new[] { Author("div { color: red; }") }, true);
            engine.ComputeAll(doc);
            var a = doc.GetElementById("a");
            engine.Invalidate(a);
            // Per-element Invalidate must remove the element from the persistent
            // result map so consumers don't observe a stale ComputedStyle.
            Assert.That(engine.ResultMap.ContainsKey(a), Is.False);
        }
    }
}
