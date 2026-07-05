using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Dom;

namespace Weva.Tests.Dom {
    public class AttributeMapTests {
        [Test]
        public void Empty_map_has_zero_count() {
            var m = new AttributeMap();
            Assert.That(m.Count, Is.EqualTo(0));
        }

        [Test]
        public void Set_then_get_returns_value() {
            var m = new AttributeMap();
            m["id"] = "main";
            Assert.That(m["id"], Is.EqualTo("main"));
        }

        [Test]
        public void Get_missing_returns_null() {
            var m = new AttributeMap();
            Assert.That(m["missing"], Is.Null);
        }

        [Test]
        public void Set_increments_count() {
            var m = new AttributeMap();
            m["a"] = "1";
            m["b"] = "2";
            Assert.That(m.Count, Is.EqualTo(2));
        }

        [Test]
        public void Update_existing_does_not_increment_count() {
            var m = new AttributeMap();
            m["a"] = "1";
            m["a"] = "2";
            Assert.That(m.Count, Is.EqualTo(1));
            Assert.That(m["a"], Is.EqualTo("2"));
        }

        [Test]
        public void Insertion_order_preserved_in_iteration() {
            var m = new AttributeMap();
            m["c"] = "3";
            m["a"] = "1";
            m["b"] = "2";
            var names = m.Select(kv => kv.Key).ToList();
            Assert.That(names, Is.EqualTo(new List<string> { "c", "a", "b" }));
        }

        [Test]
        public void Update_does_not_change_position() {
            var m = new AttributeMap();
            m["a"] = "1";
            m["b"] = "2";
            m["a"] = "x";
            var names = m.Select(kv => kv.Key).ToList();
            Assert.That(names, Is.EqualTo(new List<string> { "a", "b" }));
        }

        [Test]
        public void Contains_reports_membership() {
            var m = new AttributeMap();
            m["a"] = "1";
            Assert.That(m.Contains("a"), Is.True);
            Assert.That(m.Contains("b"), Is.False);
        }

        [Test]
        public void Remove_existing_returns_true_and_decrements_count() {
            var m = new AttributeMap();
            m["a"] = "1";
            Assert.That(m.Remove("a"), Is.True);
            Assert.That(m.Count, Is.EqualTo(0));
            Assert.That(m["a"], Is.Null);
        }

        [Test]
        public void Remove_missing_returns_false() {
            var m = new AttributeMap();
            Assert.That(m.Remove("missing"), Is.False);
        }

        [Test]
        public void Remove_preserves_order_of_remaining() {
            var m = new AttributeMap();
            m["a"] = "1"; m["b"] = "2"; m["c"] = "3";
            m.Remove("b");
            var names = m.Select(kv => kv.Key).ToList();
            Assert.That(names, Is.EqualTo(new List<string> { "a", "c" }));
        }

        [Test]
        public void Empty_string_value_is_distinct_from_null() {
            var m = new AttributeMap();
            m["disabled"] = "";
            Assert.That(m.Contains("disabled"), Is.True);
            Assert.That(m["disabled"], Is.EqualTo(""));
        }

        // ── ValueAt (PERF-1): the order-parallel value list must stay in
        // sync with the by-name dictionary through set / update / remove ──

        [Test]
        public void ValueAt_matches_by_name_lookup_in_declaration_order() {
            var m = new AttributeMap();
            m["id"] = "main";
            m["class"] = "card gold";
            m["data-x"] = "1";
            for (int i = 0; i < m.Count; i++) {
                Assert.That(m.ValueAt(i), Is.EqualTo(m[m.NameAt(i)]),
                    "slot " + i + " (" + m.NameAt(i) + ")");
            }
        }

        [Test]
        public void ValueAt_reflects_in_place_updates() {
            var m = new AttributeMap();
            m["class"] = "a";
            m["id"] = "x";
            m["class"] = "b";          // update existing slot
            m["CLASS"] = "c";          // case-insensitive update, same slot
            Assert.That(m.Count, Is.EqualTo(2));
            Assert.That(m.NameAt(0), Is.EqualTo("class"));
            Assert.That(m.ValueAt(0), Is.EqualTo("c"));
            Assert.That(m.ValueAt(1), Is.EqualTo("x"));
        }

        [Test]
        public void ValueAt_stays_aligned_after_remove() {
            var m = new AttributeMap();
            m["a"] = "1";
            m["b"] = "2";
            m["c"] = "3";
            m.Remove("b");
            Assert.That(m.Count, Is.EqualTo(2));
            Assert.That(m.NameAt(0), Is.EqualTo("a"));
            Assert.That(m.ValueAt(0), Is.EqualTo("1"));
            Assert.That(m.NameAt(1), Is.EqualTo("c"));
            Assert.That(m.ValueAt(1), Is.EqualTo("3"));
        }
    }
}
