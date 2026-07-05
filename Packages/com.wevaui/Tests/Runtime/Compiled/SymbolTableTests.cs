using NUnit.Framework;
using Weva.Compiled;

namespace Weva.Tests.Compiled {
    public class SymbolTableTests {
        [Test]
        public void Empty_string_is_reserved_at_zero() {
            var t = new SymbolTable();
            Assert.That(t.Intern(""), Is.EqualTo(0));
            Assert.That(t.Get(0), Is.EqualTo(""));
        }

        [Test]
        public void Null_input_returns_zero() {
            var t = new SymbolTable();
            Assert.That(t.Intern(null), Is.EqualTo(0));
        }

        [Test]
        public void First_intern_returns_one() {
            var t = new SymbolTable();
            Assert.That(t.Intern("div"), Is.EqualTo(1));
        }

        [Test]
        public void Same_string_returns_same_id() {
            var t = new SymbolTable();
            int a = t.Intern("foo");
            int b = t.Intern("foo");
            Assert.That(a, Is.EqualTo(b));
        }

        [Test]
        public void Slice_intern_returns_existing_id_without_adding_symbol() {
            var t = new SymbolTable();
            int item = t.Intern("item");
            int before = t.Count;

            int sliced = t.Intern("item selected", 0, 4);

            Assert.That(sliced, Is.EqualTo(item));
            Assert.That(t.Count, Is.EqualTo(before));
        }

        [Test]
        public void Slice_intern_adds_missing_symbol_once() {
            var t = new SymbolTable();
            int first = t.Intern("item selected", 5, 8);
            int second = t.Intern("selected");

            Assert.That(first, Is.EqualTo(second));
        }

        [Test]
        public void Distinct_strings_get_distinct_ids() {
            var t = new SymbolTable();
            int a = t.Intern("foo");
            int b = t.Intern("bar");
            int c = t.Intern("baz");
            Assert.That(a, Is.Not.EqualTo(b));
            Assert.That(b, Is.Not.EqualTo(c));
            Assert.That(a, Is.Not.EqualTo(c));
        }

        [Test]
        public void Reverse_lookup_round_trips() {
            var t = new SymbolTable();
            int id = t.Intern("hello");
            Assert.That(t.Get(id), Is.EqualTo("hello"));
        }

        [Test]
        public void Get_out_of_range_returns_null() {
            var t = new SymbolTable();
            t.Intern("only");
            Assert.That(t.Get(9999), Is.Null);
            Assert.That(t.Get(-1), Is.Null);
        }

        [Test]
        public void Intern_is_stable_across_calls() {
            var t = new SymbolTable();
            int id1 = t.Intern("class");
            t.Intern("other");
            t.Intern("more");
            int id2 = t.Intern("class");
            Assert.That(id2, Is.EqualTo(id1));
        }

        [Test]
        public void TryGet_finds_existing() {
            var t = new SymbolTable();
            int id = t.Intern("abc");
            Assert.That(t.TryGet("abc", out var found), Is.True);
            Assert.That(found, Is.EqualTo(id));
        }

        [Test]
        public void TryGet_missing_returns_false() {
            var t = new SymbolTable();
            Assert.That(t.TryGet("never", out _), Is.False);
        }

        [Test]
        public void Count_grows_with_new_interns() {
            var t = new SymbolTable();
            int before = t.Count;
            t.Intern("a");
            t.Intern("b");
            t.Intern("a");
            Assert.That(t.Count, Is.EqualTo(before + 2));
        }

        // ── PERF-1 hash-index contracts ─────────────────────────────────
        // The substring overload used to linearly scan every interned string
        // per call (Unity Mono Refill was 11× slower than CoreCLR on the
        // same code). It now probes an FNV-1a bucket; these pin the index's
        // correctness and the alloc-free warm path.

        [Test]
        public void Same_length_different_content_get_distinct_ids() {
            var t = new SymbolTable();
            int a = t.Intern("abc");
            int b = t.Intern("xxabdyy", 2, 3); // "abd" — same length as "abc"
            Assert.That(b, Is.Not.EqualTo(a));
            Assert.That(t.Get(b), Is.EqualTo("abd"));
        }

        [Test]
        public void Slice_resolved_id_is_reused_by_later_full_intern() {
            var t = new SymbolTable();
            int sub = t.Intern("aabadgezz", 2, 5); // "badge"
            int full = t.Intern("badge");
            Assert.That(full, Is.EqualTo(sub));
            Assert.That(t.Get(sub), Is.EqualTo("badge"));
        }

        [Test]
        public void Slices_round_trip_across_many_symbols() {
            var t = new SymbolTable();
            string[] words = { "card", "icon", "body", "name", "footer", "badge", "kind-0", "kind-1" };
            foreach (var w in words) {
                int id = t.Intern("__" + w + "__", 2, w.Length);
                Assert.That(t.Get(id), Is.EqualTo(w));
            }
            Assert.That(t.Count, Is.EqualTo(words.Length + 1), "8 words + the sentinel");
        }

        [Test]
        public void Warm_slice_hits_allocate_nothing() {
            var t = new SymbolTable();
            string[] tokens = { "card", "icon", "body", "name", "footer", "badge" };
            const string source = "card icon body name footer badge";
            int[] starts = { 0, 5, 10, 15, 20, 27 };
            int[] lengths = { 4, 4, 4, 4, 6, 5 };
            for (int i = 0; i < tokens.Length; i++) t.Intern(tokens[i]);
            for (int i = 0; i < tokens.Length; i++) t.Intern(source, starts[i], lengths[i]);

            long before = System.GC.GetAllocatedBytesForCurrentThread();
            for (int iter = 0; iter < 10000; iter++) {
                for (int i = 0; i < tokens.Length; i++) {
                    t.Intern(source, starts[i], lengths[i]);
                }
            }
            long allocated = System.GC.GetAllocatedBytesForCurrentThread() - before;
            // 60k warm probes must be hit-path only (no Substring). Slack for
            // runtime noise.
            Assert.That(allocated, Is.LessThan(16 * 1024),
                "warm slice interning must not allocate (got " + allocated + " bytes over 60k probes)");
        }
    }
}
