using System;
using NUnit.Framework;
using Weva.Compiled;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    public class StyleArrayTests {
        static ComputedStyle MakeStyle(string id) {
            var e = new Element("div");
            e.SetAttribute("id", id);
            var cs = new ComputedStyle(e);
            cs.Set("color", "red");
            return cs;
        }

        [Test]
        public void Default_constructor_starts_empty() {
            var arr = new StyleArray();
            Assert.That(arr.Capacity, Is.EqualTo(0));
            Assert.That(arr.Count, Is.EqualTo(0));
            Assert.That(arr.Get(0), Is.Null);
        }

        [Test]
        public void Constructed_with_initial_capacity() {
            var arr = new StyleArray(100);
            Assert.That(arr.Capacity, Is.EqualTo(100));
            Assert.That(arr.Count, Is.EqualTo(0));
            Assert.That(arr.Get(50), Is.Null);
        }

        [Test]
        public void EnsureCapacity_grows_when_needed() {
            var arr = new StyleArray(8);
            arr.EnsureCapacity(64);
            Assert.That(arr.Capacity, Is.GreaterThanOrEqualTo(64));
        }

        [Test]
        public void EnsureCapacity_no_op_when_already_large() {
            var arr = new StyleArray(128);
            int before = arr.Capacity;
            arr.EnsureCapacity(64);
            Assert.That(arr.Capacity, Is.EqualTo(before));
        }

        [Test]
        public void Set_then_Get_round_trips() {
            var arr = new StyleArray(8);
            var cs = MakeStyle("a");
            arr.Set(3, cs);
            Assert.That(arr.Get(3), Is.SameAs(cs));
        }

        [Test]
        public void Set_grows_capacity_when_index_exceeds_capacity() {
            var arr = new StyleArray(4);
            var cs = MakeStyle("x");
            arr.Set(50, cs);
            Assert.That(arr.Capacity, Is.GreaterThanOrEqualTo(51));
            Assert.That(arr.Get(50), Is.SameAs(cs));
        }

        [Test]
        public void Get_negative_or_out_of_range_returns_null() {
            var arr = new StyleArray(8);
            arr.Set(2, MakeStyle("x"));
            Assert.That(arr.Get(-1), Is.Null);
            Assert.That(arr.Get(1000), Is.Null);
        }

        [Test]
        public void Set_negative_throws() {
            var arr = new StyleArray();
            Assert.Throws<ArgumentOutOfRangeException>(() => arr.Set(-1, MakeStyle("x")));
        }

        [Test]
        public void Resize_preserves_existing_entries_within_range() {
            var arr = new StyleArray(8);
            var a = MakeStyle("a");
            var b = MakeStyle("b");
            arr.Set(2, a);
            arr.Set(5, b);
            arr.Resize(10);
            Assert.That(arr.Get(2), Is.SameAs(a));
            Assert.That(arr.Get(5), Is.SameAs(b));
        }

        [Test]
        public void Resize_smaller_clears_entries_past_new_count() {
            var arr = new StyleArray(8);
            arr.Set(2, MakeStyle("a"));
            arr.Set(5, MakeStyle("b"));
            arr.Resize(3);
            Assert.That(arr.Get(2), Is.Not.Null, "kept entry inside new size");
            Assert.That(arr.Get(5), Is.Null, "dropped entry past new size");
            Assert.That(arr.Count, Is.EqualTo(3));
        }

        [Test]
        public void Clear_drops_all_entries() {
            var arr = new StyleArray(8);
            arr.Set(0, MakeStyle("a"));
            arr.Set(2, MakeStyle("b"));
            arr.Clear();
            Assert.That(arr.Count, Is.EqualTo(0));
            Assert.That(arr.Get(0), Is.Null);
            Assert.That(arr.Get(2), Is.Null);
        }

        [Test]
        public void Clear_preserves_capacity() {
            var arr = new StyleArray(64);
            int cap = arr.Capacity;
            arr.Set(10, MakeStyle("x"));
            arr.Clear();
            Assert.That(arr.Capacity, Is.EqualTo(cap), "Clear must not free the backing array");
        }

        [Test]
        public void AlignTo_snapshot_grows_to_node_count() {
            var doc = HtmlParser.Parse("<div><span></span><span></span></div>");
            var snap = DomSnapshot.Build(doc, new SymbolTable());
            var arr = new StyleArray();
            arr.AlignTo(snap);
            Assert.That(arr.Count, Is.EqualTo(snap.NodeCount));
            Assert.That(arr.Capacity, Is.GreaterThanOrEqualTo(snap.NodeCount));
        }

        [Test]
        public void AlignTo_smaller_snapshot_drops_stale_entries_past_new_count() {
            var doc = HtmlParser.Parse("<div></div>");
            var bigSnap = DomSnapshot.Build(doc, new SymbolTable());
            var arr = new StyleArray(64);
            arr.Set(50, MakeStyle("ghost"));
            arr.AlignTo(bigSnap);
            Assert.That(arr.Get(50), Is.Null,
                "AlignTo to a smaller snapshot must wipe entries past the new node count");
        }

        [Test]
        public void Engine_Styles_property_populated_after_ComputeAll() {
            var doc = HtmlParser.Parse("<div id=\"a\"></div><div id=\"b\"></div>");
            var sheet = OriginatedStylesheet.Author(CssParser.Parse("div { color: red; }"));
            var engine = new CascadeEngine(new[] { sheet }, true);
            engine.ComputeAll(doc);
            Assert.That(engine.Styles.Count, Is.GreaterThan(0));
            // Element NodeIds populated.
            int populated = 0;
            for (int i = 0; i < engine.Styles.Count; i++) {
                if (engine.Styles.Get(i) != null) populated++;
            }
            Assert.That(populated, Is.GreaterThanOrEqualTo(2));
        }
    }
}
