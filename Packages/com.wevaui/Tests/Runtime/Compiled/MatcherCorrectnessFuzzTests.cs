using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Weva.Compiled;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Compiled {
    public class MatcherCorrectnessFuzzTests {
        // Random DOM + random selectors, fixed seed so failures reproduce.
        // The snapshot matcher is required to agree with the managed matcher on
        // every (element, selector) pair.

        static readonly string[] Tags = { "div", "span", "p", "a", "ul", "li", "section", "header" };
        static readonly string[] ClassNames = { "alpha", "beta", "gamma", "delta", "epsilon", "selected", "active" };
        static readonly string[] Ids = { "one", "two", "three", "four" };

        static string BuildRandomHtml(Random rng, int targetNodes) {
            var sb = new StringBuilder();
            int nodes = 0;
            int depth = 0;
            int maxDepth = 6;
            void Recurse(int budget) {
                if (budget <= 0 || depth >= maxDepth) return;
                int siblings = 1 + rng.Next(4);
                for (int i = 0; i < siblings && nodes < targetNodes; i++) {
                    string tag = Tags[rng.Next(Tags.Length)];
                    sb.Append('<').Append(tag);
                    if (rng.NextDouble() < 0.5) {
                        int n = 1 + rng.Next(2);
                        sb.Append(" class=\"");
                        for (int k = 0; k < n; k++) {
                            if (k > 0) sb.Append(' ');
                            sb.Append(ClassNames[rng.Next(ClassNames.Length)]);
                        }
                        sb.Append('"');
                    }
                    if (rng.NextDouble() < 0.2) {
                        sb.Append(" id=\"").Append(Ids[rng.Next(Ids.Length)]).Append('"');
                    }
                    sb.Append('>');
                    nodes++;
                    if (rng.NextDouble() < 0.6 && nodes < targetNodes) {
                        depth++;
                        Recurse(budget / 2);
                        depth--;
                    }
                    sb.Append("</").Append(tag).Append('>');
                }
            }
            Recurse(targetNodes);
            return sb.ToString();
        }

        static List<string> BuildRandomSelectors(Random rng, int count) {
            var result = new List<string>();
            for (int i = 0; i < count; i++) {
                int kind = rng.Next(8);
                switch (kind) {
                    case 0: result.Add(Tags[rng.Next(Tags.Length)]); break;
                    case 1: result.Add("." + ClassNames[rng.Next(ClassNames.Length)]); break;
                    case 2: result.Add("#" + Ids[rng.Next(Ids.Length)]); break;
                    case 3: result.Add(Tags[rng.Next(Tags.Length)] + "." + ClassNames[rng.Next(ClassNames.Length)]); break;
                    case 4: result.Add("." + ClassNames[rng.Next(ClassNames.Length)] + "." + ClassNames[rng.Next(ClassNames.Length)]); break;
                    case 5: result.Add(Tags[rng.Next(Tags.Length)] + " " + Tags[rng.Next(Tags.Length)]); break;
                    case 6: result.Add(Tags[rng.Next(Tags.Length)] + " > " + Tags[rng.Next(Tags.Length)]); break;
                    case 7: result.Add("*"); break;
                }
            }
            return result;
        }

        static List<int> ManagedMatchSet(Element e, IReadOnlyList<CompiledSelector> sels) {
            var hits = new List<int>();
            for (int i = 0; i < sels.Count; i++) {
                if (SelectorMatcher.Matches(sels[i], e)) hits.Add(i);
            }
            return hits;
        }

        static void RunTrial(int seed, int targetNodes, int selectorCount) {
            var rng = new Random(seed);
            string html = BuildRandomHtml(rng, targetNodes);
            // Ensure the parser sees something valid:
            if (string.IsNullOrWhiteSpace(html)) html = "<div></div>";

            var doc = HtmlParser.Parse(html);
            var sym = new SymbolTable();
            var snap = DomSnapshot.Build(doc, sym);

            var selStrings = BuildRandomSelectors(rng, selectorCount);
            var sels = new List<CompiledSelector>();
            foreach (var s in selStrings) sels.Add(SelectorParser.Parse(s));
            var idx = new SelectorIndex(sym, sels);

            for (int nodeId = 0; nodeId < snap.NodeCount; nodeId++) {
                if (snap.Kinds[nodeId] != NodeKind.Element) continue;
                var elem = (Element)snap.ManagedNodes[nodeId];
                var managed = ManagedMatchSet(elem, sels);
                managed.Sort();
                var fast = SnapshotMatcher.Match(snap, nodeId, idx, sels);
                fast.Sort();
                if (!ListsEqual(fast, managed)) {
                    Assert.Fail($"seed={seed} node={nodeId}<{elem.TagName}>: managed=[{string.Join(",", managed)}] fast=[{string.Join(",", fast)}]\nselectors=[{string.Join(", ", selStrings)}]\nhtml={html}");
                }
            }
        }

        static bool ListsEqual(List<int> a, List<int> b) {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        [Test]
        public void Fuzz_seed_1() => RunTrial(1, 80, 30);

        [Test]
        public void Fuzz_seed_42() => RunTrial(42, 120, 50);

        [Test]
        public void Fuzz_seed_2024() => RunTrial(2024, 200, 60);

        [Test]
        public void Fuzz_seed_stress() => RunTrial(7777, 400, 80);
    }
}
