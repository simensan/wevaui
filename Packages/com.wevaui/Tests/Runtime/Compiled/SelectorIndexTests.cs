using System.Collections.Generic;
using NUnit.Framework;
using Weva.Compiled;
using Weva.Css.Selectors;

namespace Weva.Tests.Compiled {
    public class SelectorIndexTests {
        static (SelectorIndex idx, SymbolTable sym, List<CompiledSelector> sels) BuildIndex(params string[] selectors) {
            var sym = new SymbolTable();
            var sels = new List<CompiledSelector>();
            foreach (var s in selectors) sels.Add(SelectorParser.Parse(s));
            return (new SelectorIndex(sym, sels), sym, sels);
        }

        static IReadOnlyList<int> CandidatesFor(SelectorIndex idx, SymbolTable sym, string tag, string id, params string[] classes) {
            int tagSym = sym.Intern(tag);
            int idSym = string.IsNullOrEmpty(id) ? 0 : sym.Intern(id);
            var classSyms = new int[classes.Length];
            for (int i = 0; i < classes.Length; i++) classSyms[i] = sym.Intern(classes[i]);
            return idx.CandidateSelectors(tagSym, idSym, classSyms);
        }

        [Test]
        public void Id_keyed_selector_lands_in_id_bucket() {
            var (idx, sym, _) = BuildIndex("#hero");
            Assert.That(idx.TryGetIdBucket(sym.Intern("hero"), out var list), Is.True);
            Assert.That(list, Has.Count.EqualTo(1));
            Assert.That(list[0], Is.EqualTo(0));
        }

        [Test]
        public void Class_keyed_selector_lands_in_class_bucket() {
            var (idx, sym, _) = BuildIndex(".btn");
            Assert.That(idx.TryGetClassBucket(sym.Intern("btn"), out var list), Is.True);
            Assert.That(list, Has.Count.EqualTo(1));
        }

        [Test]
        public void Tag_keyed_selector_lands_in_tag_bucket() {
            var (idx, sym, _) = BuildIndex("div");
            Assert.That(idx.TryGetTagBucket(sym.Intern("div"), out var list), Is.True);
            Assert.That(list, Has.Count.EqualTo(1));
        }

        [Test]
        public void Universal_selector_lands_in_universal_bucket() {
            var (idx, _, _) = BuildIndex("*");
            Assert.That(idx.UniversalBucket, Has.Count.EqualTo(1));
        }

        [Test]
        public void Compound_selector_uses_id_when_present() {
            // div#hero.btn -> rightmost compound is "div#hero.btn"; key should be id (#hero).
            var (idx, sym, _) = BuildIndex("div#hero.btn");
            Assert.That(idx.TryGetIdBucket(sym.Intern("hero"), out _), Is.True);
            Assert.That(idx.TryGetClassBucket(sym.Intern("btn"), out _), Is.False);
            Assert.That(idx.TryGetTagBucket(sym.Intern("div"), out _), Is.False);
        }

        [Test]
        public void Compound_selector_prefers_class_over_tag() {
            var (idx, sym, _) = BuildIndex("div.foo");
            Assert.That(idx.TryGetClassBucket(sym.Intern("foo"), out _), Is.True);
            Assert.That(idx.TryGetTagBucket(sym.Intern("div"), out _), Is.False);
        }

        [Test]
        public void Descendant_combinator_keyed_on_rightmost_compound() {
            // ".sidebar a" — rightmost is "a"; should land in tag bucket for "a".
            var (idx, sym, _) = BuildIndex(".sidebar a");
            Assert.That(idx.TryGetTagBucket(sym.Intern("a"), out var list), Is.True);
            Assert.That(list, Has.Count.EqualTo(1));
            Assert.That(idx.TryGetClassBucket(sym.Intern("sidebar"), out _), Is.False);
        }

        [Test]
        public void Universal_bucket_returned_alongside_tag_candidates() {
            var (idx, sym, _) = BuildIndex("*", "div");
            var cands = CandidatesFor(idx, sym, "div", null);
            Assert.That(cands, Has.Count.EqualTo(2));
            Assert.That(cands, Contains.Item(0));
            Assert.That(cands, Contains.Item(1));
        }

        [Test]
        public void Candidate_lookup_unions_buckets_and_dedups() {
            var (idx, sym, _) = BuildIndex("div", ".foo", "div.foo", "*");
            // Element matches all four: tag div, class foo, compound div.foo (keyed by class foo), universal.
            var cands = CandidatesFor(idx, sym, "div", null, "foo");
            Assert.That(cands.Count, Is.EqualTo(4));
        }

        [Test]
        public void Class_keyed_selectors_only_returned_when_class_matches() {
            var (idx, sym, _) = BuildIndex(".alpha", ".beta");
            var cands = CandidatesFor(idx, sym, "div", null, "alpha");
            Assert.That(cands, Contains.Item(0));
            Assert.That(cands, Has.No.Member(1));
        }

        [Test]
        public void Pseudo_class_only_selector_falls_to_universal_bucket() {
            // ":hover" alone has no tag/class/id; lands in universal so it's still considered.
            var (idx, _, _) = BuildIndex(":hover");
            Assert.That(idx.UniversalBucket, Has.Count.EqualTo(1));
        }

        [Test]
        public void Attribute_only_selector_lands_in_attribute_bucket() {
            var (idx, sym, _) = BuildIndex("[disabled]");
            Assert.That(idx.UniversalBucket, Has.Count.EqualTo(0));
            Assert.That(idx.TryGetAttributeBucket(sym.Intern("disabled"), out var list), Is.True);
            Assert.That(list, Has.Count.EqualTo(1));
        }
    }
}
