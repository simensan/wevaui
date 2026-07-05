using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Container;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Parsing;
using Weva.Reactive;

namespace Weva.Tests.Css.Cascade {
    // PI5 — regression suite for the containerResolutionCache inner-dict pool.
    //
    // Before PI5, every cascade pass that wiped containerResolutionCache (via
    // Apply with Style|Layout|Structure dirtiness) dropped the inner
    // Dictionary<int, ContainerContext> instances on the floor; the next pass
    // allocated a fresh inner dict per element with a container-query rule
    // hit. PI5 routes drops through a Stack<Dictionary<int, ContainerContext>>
    // pool so the next allocation pops a Clear()ed instance instead of newing.
    //
    // Both tests use a small fake BoxIndex (same pattern as
    // CascadeContainerIntegrationTests) — the cascade only needs Box.Width /
    // Height / parent-linkage and a Style with container-type set.
    public class ContainerResolutionInnerDictPoolTests {
        sealed class TestBox : BlockBox { }

        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        sealed class FakeBoxIndex {
            readonly Dictionary<Element, Box> map = new();
            public Box Lookup(Element e) => e != null && map.TryGetValue(e, out var b) ? b : null;
            public void Add(Element e, Box b) { map[e] = b; }
            public Func<Element, Box> AsFunc => Lookup;
        }

        // Builds a parent <div> sized as an inline-size container plus N child
        // <span>s. The cascade's container-query rule matches each child via
        // its containing block, so every child triggers a fresh
        // ContainerMatches call on the first cascade pass.
        static (Document doc, Element parent, List<Element> children, FakeBoxIndex index)
            BuildContainerDoc(int childCount) {
            var sb = new System.Text.StringBuilder();
            sb.Append("<div id=\"p\">");
            for (int i = 0; i < childCount; i++) {
                sb.Append("<span id=\"c").Append(i).Append("\">x</span>");
            }
            sb.Append("</div>");
            var doc = Html(sb.ToString());
            var p = doc.GetElementById("p");
            var children = new List<Element>(childCount);
            for (int i = 0; i < childCount; i++) {
                children.Add(doc.GetElementById("c" + i));
            }

            var index = new FakeBoxIndex();
            var parentStyle = new ComputedStyle(p);
            parentStyle.Set("container-type", "inline-size");
            var parentBox = new TestBox { Element = p, Style = parentStyle, Width = 800, Height = 600 };
            index.Add(p, parentBox);
            foreach (var c in children) {
                var cs = new ComputedStyle(c);
                var cb = new TestBox { Element = c, Style = cs };
                parentBox.AddChild(cb);
                index.Add(c, cb);
            }
            return (doc, p, children, index);
        }

        // Reflection accessors for the internal diagnostic counters. Tests
        // assembly is InternalsVisibleTo, so direct access compiles — but
        // reflection keeps the test free of an extra `using` and matches the
        // pattern used elsewhere for counter probes.
        static long AllocCount(CascadeEngine e) {
            var prop = typeof(CascadeEngine).GetProperty(
                "ContainerInnerDictAllocCountForTests",
                BindingFlags.Instance | BindingFlags.NonPublic);
            return (long)prop.GetValue(e);
        }
        static long PoolHits(CascadeEngine e) {
            var prop = typeof(CascadeEngine).GetProperty(
                "ContainerInnerDictPoolHitsForTests",
                BindingFlags.Instance | BindingFlags.NonPublic);
            return (long)prop.GetValue(e);
        }
        static int PoolSize(CascadeEngine e) {
            var prop = typeof(CascadeEngine).GetProperty(
                "ContainerInnerDictPoolSizeForTests",
                BindingFlags.Instance | BindingFlags.NonPublic);
            return (int)prop.GetValue(e);
        }

        [Test]
        public void Hundred_cascade_passes_allocate_near_zero_inner_dicts_after_warmup_PI5() {
            const int childCount = 8;
            var (doc, _, children, index) = BuildContainerDoc(childCount);
            var engine = new CascadeEngine(new[] {
                Author("@container (min-width: 600px) { span { color: red; } }")
            });
            engine.ElementToBoxLookup = index.AsFunc;

            // Warm-up: first pass allocates inner dicts for every element
            // touched by the cascade walk that hits a container-query rule.
            // ContainerMatches fires for every (element, rule) pair in the
            // managed (non-snapshot) path BEFORE the selector match, so each
            // ancestor in the parent chain (html, body, div#p) also gets one
            // inner dict on first visit — plus one per child span.
            foreach (var c in children) engine.Compute(c);
            long warmAlloc = AllocCount(engine);
            long warmHits = PoolHits(engine);
            // Sanity bound: at least childCount fresh dicts (one per span).
            // Upper bound permits the parent-chain ancestors too.
            Assert.That(warmAlloc, Is.GreaterThanOrEqualTo(childCount),
                "warm-up should allocate at least one inner dict per child span");
            Assert.That(warmHits, Is.EqualTo(0),
                "no pool hits expected before the first invalidation drop");

            // Drive 100 invalidate + re-cascade cycles. Each invalidation
            // returns every inner dict to the pool; the next cascade pops
            // them back out. Steady-state alloc count should not grow.
            for (int i = 0; i < 100; i++) {
                // InvalidateAll drops BOTH the cascade style cache AND the
                // container-resolution cache. Real workflow uses Apply(tracker)
                // which targets Style|Layout|Structure dirty elements; for the
                // test we sledgehammer with InvalidateAll. Without dropping
                // the cascade cache the second Compute call hits the cache
                // and never re-enters ContainerMatches.
                engine.InvalidateAll();
                engine.InvalidateContainerResolutions();
                foreach (var c in children) engine.Compute(c);
            }

            long finalAlloc = AllocCount(engine);
            long finalHits = PoolHits(engine);
            long allocDelta = finalAlloc - warmAlloc;
            long hitsDelta = finalHits - warmHits;

            // 100 passes × `warmAlloc` distinct elements per pass = warmAlloc×100
            // ContainerMatches calls that need an inner dict. With the pool
            // warm, every one should be a pool hit. Zero new allocs expected.
            Assert.That(allocDelta, Is.EqualTo(0),
                $"100 invalidate+cascade cycles allocated {allocDelta} fresh inner dicts; expected 0 with pool warm");
            Assert.That(hitsDelta, Is.EqualTo(100 * warmAlloc),
                $"expected {100 * warmAlloc} pool hits across 100 cycles, got {hitsDelta}");
        }

        [Test]
        public void Pool_returns_produce_reused_dictionary_instances_PI5() {
            const int childCount = 3;
            var (doc, _, children, index) = BuildContainerDoc(childCount);
            var engine = new CascadeEngine(new[] {
                Author("@container (min-width: 600px) { span { color: red; } }")
            });
            engine.ElementToBoxLookup = index.AsFunc;

            // First pass: ContainerMatches fires once per (visited-element,
            // container-rule) pair, so first allocations cover every element
            // in the parent chain plus each child span. Capture the count
            // rather than hard-coding it.
            foreach (var c in children) engine.Compute(c);
            long firstAlloc = AllocCount(engine);
            Assert.That(firstAlloc, Is.GreaterThanOrEqualTo(childCount));
            Assert.That(PoolHits(engine), Is.EqualTo(0));
            Assert.That(PoolSize(engine), Is.EqualTo(0),
                "pool should be empty until first invalidation drops something into it");

            // Capture the actual inner-dict instances allocated on first pass
            // so we can pin reference-equality after pool return + re-pop.
            // We probe the cache via reflection on the private field — this
            // is the only way to grab the exact dict instances for SameAs
            // comparison.
            var cacheField = typeof(CascadeEngine).GetField(
                "containerResolutionCache",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var cacheBefore = (Dictionary<Element, Dictionary<int, ContainerContext>>)cacheField.GetValue(engine);
            var firstDicts = new HashSet<Dictionary<int, ContainerContext>>(
                System.Linq.Enumerable.ToArray(cacheBefore.Values),
                ReferenceEqualityComparer<Dictionary<int, ContainerContext>>.Default);

            // Invalidate — every outer-cache entry gets Clear()ed and pushed
            // back to the pool. Pool size should equal firstAlloc. We also
            // drop the cascade style cache so the next Compute call re-enters
            // ContainerMatches (otherwise the cache hit short-circuits).
            engine.InvalidateAll();
            engine.InvalidateContainerResolutions();
            Assert.That(PoolSize(engine), Is.EqualTo(firstAlloc),
                "InvalidateContainerResolutions should return every inner dict to the pool");

            // Second pass: every Compute() call pops a recycled dict. Alloc
            // count stays flat, hits count climbs by firstAlloc, pool drains.
            long allocBefore = AllocCount(engine);
            long hitsBefore = PoolHits(engine);
            foreach (var c in children) engine.Compute(c);
            long allocAfter = AllocCount(engine);
            long hitsAfter = PoolHits(engine);

            Assert.That(allocAfter, Is.EqualTo(allocBefore),
                "post-invalidation Compute calls allocated fresh dicts instead of popping the pool");
            Assert.That(hitsAfter - hitsBefore, Is.EqualTo(firstAlloc),
                $"pool hits should equal {firstAlloc} after second pass, got {hitsAfter - hitsBefore}");
            Assert.That(PoolSize(engine), Is.EqualTo(0),
                "pool should be empty after every element popped a recycled dict");

            // Reference-equality assertion: every dict in the outer cache
            // after the second pass MUST be one of the dicts allocated on
            // the first pass. Pool round-trip preserved the instance.
            var cacheAfter = (Dictionary<Element, Dictionary<int, ContainerContext>>)cacheField.GetValue(engine);
            int reusedCount = 0;
            foreach (var kv in cacheAfter) {
                if (firstDicts.Contains(kv.Value)) reusedCount++;
            }
            Assert.That(reusedCount, Is.EqualTo(cacheAfter.Count),
                $"expected every post-invalidation cache entry to reuse a first-pass dict instance (reused {reusedCount}/{cacheAfter.Count})");
        }

        // Reference-equality comparer used to assert pool round-trip preserves
        // the dictionary instance identity. .NET 5+ exposes
        // ReferenceEqualityComparer.Instance directly, but we redefine a thin
        // one here for portability with the Unity 6 target framework.
        sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class {
            public static readonly ReferenceEqualityComparer<T> Default = new();
            public bool Equals(T x, T y) => ReferenceEquals(x, y);
            public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
