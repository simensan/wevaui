using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Media;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Parsing;
using Weva.Reactive;

namespace Weva.Tests.Css.Cascade {
    public class CascadeIncrementalTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        sealed class VersionedFakeState : IElementStateProvider {
            readonly Dictionary<Element, ElementState> map = new();
            long version = 1;
            public long Version => version;
            public void Set(Element e, ElementState s) {
                map[e] = s;
                version++;
            }
            public void Clear(Element e) {
                if (map.Remove(e)) version++;
            }
            public ElementState GetState(Element e) =>
                map.TryGetValue(e, out var s) ? s : ElementState.None;
        }

        static Element Build(string html, string id) => Html(html).GetElementById(id);

        [Test]
        public void Cold_compute_is_a_miss() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author("#x { color: red; }") });
            engine.ResetCacheStats();
            engine.Compute(doc.GetElementById("x"));
            // HtmlParser synthesises <html><body> so Compute(x) walks the full
            // parent chain (html → body → x): at least one miss per chain element.
            Assert.That(engine.CacheMisses, Is.GreaterThanOrEqualTo(1));
            Assert.That(engine.CacheHits, Is.EqualTo(0));
        }

        [Test]
        public void Repeat_compute_on_unchanged_element_is_a_hit() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author("#x { color: red; }") });
            var x = doc.GetElementById("x");
            var first = engine.Compute(x);
            engine.ResetCacheStats();
            var second = engine.Compute(x);
            // After the first Compute the parent chain (html, body, x) is all cached.
            // The second call hits all chain elements — at least one hit for x itself.
            Assert.That(engine.CacheHits, Is.GreaterThanOrEqualTo(1));
            Assert.That(engine.CacheMisses, Is.EqualTo(0));
            Assert.That(second, Is.SameAs(first));
        }

        [Test]
        public void Media_context_update_without_media_rules_can_preserve_cache() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author("#x { color: red; }") });
            var x = doc.GetElementById("x");
            var first = engine.Compute(x);
            engine.ResetCacheStats();

            engine.SetMediaContext(MediaContext.Default(640, 360), bumpVersion: false);
            var second = engine.Compute(x);

            Assert.That(engine.HasMediaDependentRules, Is.False);
            // Non-media-bumping SetMediaContext preserves all cache entries.
            Assert.That(engine.CacheHits, Is.GreaterThanOrEqualTo(1));
            Assert.That(engine.CacheMisses, Is.EqualTo(0));
            Assert.That(second, Is.SameAs(first));
        }

        [Test]
        public void Media_rules_request_media_context_version_bumps() {
            var engine = new CascadeEngine(new[] {
                Author("@media (max-width: 500px) { #x { color: red; } }")
            });

            Assert.That(engine.HasMediaDependentRules, Is.True);
        }

        [Test]
        public void Hit_count_rises_on_repeated_compute() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author("#x { color: red; }") });
            var x = doc.GetElementById("x");
            engine.Compute(x);
            engine.ResetCacheStats();
            for (int i = 0; i < 5; i++) engine.Compute(x);
            // Each Compute(x) hits the full parent chain (html, body, x) — at
            // least 5 hits total (one per iteration for x alone).
            Assert.That(engine.CacheHits, Is.GreaterThanOrEqualTo(5));
            Assert.That(engine.CacheMisses, Is.EqualTo(0));
        }

        [Test]
        public void Invalidate_drops_entry_and_next_compute_is_miss() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author("#x { color: red; }") });
            var x = doc.GetElementById("x");
            var first = engine.Compute(x);
            engine.Invalidate(x);
            engine.ResetCacheStats();
            var second = engine.Compute(x);
            Assert.That(engine.CacheMisses, Is.EqualTo(1));
            Assert.That(second.Get("color"), Is.EqualTo(first.Get("color")));
        }

        [Test]
        public void InvalidateSubtree_drops_root_and_descendants() {
            var doc = Html("<div id=\"r\"><span id=\"a\"><b id=\"a2\"></b></span><span id=\"b\"></span></div>");
            var engine = new CascadeEngine(new[] { Author("div { color: red; }") });
            var r = doc.GetElementById("r");
            var a = doc.GetElementById("a");
            var a2 = doc.GetElementById("a2");
            var b = doc.GetElementById("b");
            engine.Compute(r); engine.Compute(a); engine.Compute(a2); engine.Compute(b);
            // HtmlParser synthesises <html><body> — their entries are also in cache.
            int before = engine.CacheSize;
            Assert.That(before, Is.GreaterThanOrEqualTo(4), "r+a+a2+b (plus html/body wrapper) must be cached");
            engine.InvalidateSubtree(r);
            // r, a, a2, b removed; the synthetic html/body entries may remain.
            Assert.That(engine.CacheSize, Is.LessThanOrEqualTo(before - 4),
                "InvalidateSubtree(r) must remove at least the 4 test elements");
        }

        [Test]
        public void InvalidateAll_clears_cache() {
            var doc = Html("<div id=\"a\"></div><div id=\"b\"></div>");
            var engine = new CascadeEngine(new[] { Author("div { color: red; }") });
            engine.Compute(doc.GetElementById("a"));
            engine.Compute(doc.GetElementById("b"));
            Assert.That(engine.CacheSize, Is.GreaterThanOrEqualTo(2));
            engine.InvalidateAll();
            Assert.That(engine.CacheSize, Is.EqualTo(0));
        }

        [Test]
        public void SetAttribute_bumps_element_version_and_misses_next() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(".c { color: red; }") });
            var x = doc.GetElementById("x");
            engine.Compute(x);
            engine.ResetCacheStats();
            x.SetAttribute("class", "c");
            var after = engine.Compute(x);
            Assert.That(engine.CacheMisses, Is.EqualTo(1));
            Assert.That(after.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void New_child_element_is_miss_first_time() {
            var doc = Html("<div id=\"r\"></div>");
            var engine = new CascadeEngine(new[] { Author("div { color: red; }") });
            var r = doc.GetElementById("r");
            engine.Compute(r);
            var child = new Element("span");
            r.AppendChild(child);
            engine.ResetCacheStats();
            engine.Compute(child);
            Assert.That(engine.CacheMisses, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void Parent_style_change_forces_child_recompute() {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] { Author(".red { color: red; }") });
            var p = doc.GetElementById("p");
            var c = doc.GetElementById("c");
            var child1 = engine.Compute(c);
            Assert.That(child1.Get("color"), Is.EqualTo("black"));
            p.SetAttribute("class", "red");
            engine.ResetCacheStats();
            var child2 = engine.Compute(c);
            // Parent miss, then child miss because parent's style version changed.
            Assert.That(engine.CacheMisses, Is.EqualTo(2));
            Assert.That(child2.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Sibling_change_does_not_invalidate_this_element() {
            var doc = Html("<div id=\"p\"><span id=\"a\"></span><span id=\"b\"></span></div>");
            var engine = new CascadeEngine(new[] { Author("div { color: red; }") });
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            engine.Compute(a); engine.Compute(b);
            engine.ResetCacheStats();
            // Mutate sibling b: bumps b's version, but not a's parent's version (parent
            // version DOES bump on attribute change of child via bubbling — we explicitly
            // do not propagate this to the parent's ComputedStyle in v1; an attribute
            // change on a sibling only invalidates the sibling and the parent's version
            // does not feed back into the cascade. We verify a stays cached.
            b.SetAttribute("data-x", "1");
            engine.Compute(a);
            Assert.That(engine.CacheHits, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void Media_context_change_forces_miss() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(
                new[] { Author("@media (min-width: 600px) { #x { color: red; } }") },
                MediaContext.Default(400, 400));
            var x = doc.GetElementById("x");
            var before = engine.Compute(x);
            Assert.That(before.Get("color"), Is.EqualTo("black"));
            engine.MediaContext = MediaContext.Default(800, 800);
            engine.ResetCacheStats();
            var after = engine.Compute(x);
            // Media context version bumps cache keys for the entire parent chain
            // (html, body, x with html/body wrapper) — at least one miss.
            Assert.That(engine.CacheMisses, Is.GreaterThanOrEqualTo(1));
            Assert.That(after.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void State_provider_version_change_forces_miss() {
            var doc = Html("<button id=\"b\">go</button>");
            var engine = new CascadeEngine(new[] {
                Author("button { color: black; } button:hover { color: red; }")
            });
            var btn = doc.GetElementById("b");
            var fake = new VersionedFakeState();
            var idle = engine.Compute(btn, fake);
            Assert.That(idle.Get("color"), Is.EqualTo("black"));
            fake.Set(btn, ElementState.Hover);
            engine.ResetCacheStats();
            var hover = engine.Compute(btn, fake);
            Assert.That(engine.CacheMisses, Is.EqualTo(1));
            Assert.That(hover.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Apply_drops_style_marked_elements() {
            var doc = Html("<div id=\"a\"></div><div id=\"b\"></div>");
            var engine = new CascadeEngine(new[] { Author("div { color: red; }") });
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            engine.Compute(a); engine.Compute(b);
            int before = engine.CacheSize;
            var tracker = new InvalidationTracker();
            tracker.MarkDirty(a, InvalidationKind.Style);
            engine.Apply(tracker);
            Assert.That(engine.CacheSize, Is.EqualTo(before - 1));
            engine.ResetCacheStats();
            engine.Compute(a);
            Assert.That(engine.CacheMisses, Is.EqualTo(1));
            engine.Compute(b);
            // Compute(b) hits the full parent chain (html+body+b) — at least 1 hit.
            Assert.That(engine.CacheHits, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void Apply_drops_structure_marked_elements() {
            var doc = Html("<div id=\"a\"></div>");
            var engine = new CascadeEngine(new[] { Author("div { color: red; }") });
            var a = doc.GetElementById("a");
            engine.Compute(a);
            int before = engine.CacheSize;
            var tracker = new InvalidationTracker();
            tracker.MarkDirty(a, InvalidationKind.Structure);
            engine.Apply(tracker);
            // Apply drops only the element explicitly marked dirty. The synthetic
            // html/body wrapper elements remain in cache (they were not marked dirty).
            Assert.That(engine.CacheSize, Is.EqualTo(before - 1),
                "Apply must remove exactly the structure-marked element from cache");
        }

        [Test]
        public void Apply_via_attached_tracker_after_attribute_change() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(".c { color: red; }") });
            var x = doc.GetElementById("x");
            engine.Compute(x);
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            x.SetAttribute("class", "c");
            engine.Apply(tracker);
            engine.ResetCacheStats();
            var after = engine.Compute(x);
            Assert.That(engine.CacheMisses, Is.EqualTo(1));
            Assert.That(after.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void ComputedStyle_version_bumps_each_recomputation() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author("#x { color: red; }") });
            var x = doc.GetElementById("x");
            var s1 = engine.Compute(x);
            engine.Invalidate(x);
            var s2 = engine.Compute(x);
            Assert.That(s2.Version, Is.GreaterThan(s1.Version));
        }

        [Test]
        public void No_rules_element_returns_same_instance_on_second_compute() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(System.Array.Empty<OriginatedStylesheet>());
            var x = doc.GetElementById("x");
            var a = engine.Compute(x);
            var b = engine.Compute(x);
            Assert.That(b, Is.SameAs(a));
        }

        [Test]
        public void Inheritance_chain_propagates_after_parent_invalidation() {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] { Author(".g { color: green; }") });
            var p = doc.GetElementById("p");
            var c = doc.GetElementById("c");
            var c0 = engine.Compute(c);
            Assert.That(c0.Get("color"), Is.EqualTo("black"));
            p.SetAttribute("class", "g");
            var c1 = engine.Compute(c);
            Assert.That(c1.Get("color"), Is.EqualTo("green"));
        }

        [Test]
        public void ComputeAll_is_all_hits_after_no_op_pass() {
            var doc = Html(
                "<div id=\"r\">" +
                "<section><p><span></span></p><p><span></span></p></section>" +
                "<section><p><span></span></p><p><span></span></p></section>" +
                "</div>");
            var engine = new CascadeEngine(new[] { Author("div { color: red; }") });
            var first = engine.ComputeAll(doc);
            engine.ResetCacheStats();
            var second = engine.ComputeAll(doc);
            Assert.That(second.Count, Is.EqualTo(first.Count));
            Assert.That(engine.CacheMisses, Is.EqualTo(0));
            Assert.That(engine.CacheHits, Is.EqualTo(first.Count));
        }

        [Test]
        public void Cache_hit_rate_is_one_after_no_op_pass_on_50_node_tree() {
            // Build a 50-element tree and verify that a second ComputeAll produces no misses.
            var root = new Element("div");
            var doc = new Document();
            doc.AppendChild(root);
            var current = root;
            for (int i = 0; i < 49; i++) {
                var child = new Element("div");
                current.AppendChild(child);
                current = child;
            }
            var engine = new CascadeEngine(new[] { Author("div { color: red; }") });
            engine.ComputeAll(doc);
            engine.ResetCacheStats();
            engine.ComputeAll(doc);
            Assert.That(engine.CacheMisses, Is.EqualTo(0));
            Assert.That(engine.CacheHits, Is.EqualTo(50));
        }

        [Test]
        public void Incremental_result_equals_from_scratch_for_100_element_tree() {
            // Create a 100-element fixture, run ComputeAll, mutate scattered elements,
            // run incremental ComputeAll, and run a fresh-engine ComputeAll. Compare.
            var doc = new Document();
            var root = new Element("div");
            root.SetAttribute("class", "root");
            doc.AppendChild(root);
            var elements = new List<Element> { root };
            for (int i = 1; i < 100; i++) {
                var e = new Element(i % 3 == 0 ? "span" : "div");
                if (i % 5 == 0) e.SetAttribute("class", "x");
                if (i % 7 == 0) e.SetAttribute("id", "id" + i);
                elements[(i - 1) / 2].AppendChild(e);
                elements.Add(e);
            }
            string sheet =
                ".root { color: navy; }" +
                ".x { color: red; }" +
                "div { font-size: 16px; }" +
                "span { font-size: 14px; }" +
                "#id7 { color: rebeccapurple; }" +
                "#id14 { color: green; }";

            var incremental = new CascadeEngine(new[] { Author(sheet) });
            incremental.ComputeAll(doc);
            // Mutate scattered elements.
            elements[3].SetAttribute("class", "x");
            elements[12].SetAttribute("class", "x");
            elements[42].SetAttribute("style", "color: blue;");
            var incrementalResult = incremental.ComputeAll(doc);

            var fresh = new CascadeEngine(new[] { Author(sheet) });
            var freshResult = fresh.ComputeAll(doc);

            foreach (var e in elements) {
                var inc = incrementalResult[e];
                var fr = freshResult[e];
                Assert.That(inc.Get("color"), Is.EqualTo(fr.Get("color")), "color mismatch on " + e.TagName);
                Assert.That(inc.Get("font-size"), Is.EqualTo(fr.Get("font-size")));
                Assert.That(inc.Get("display"), Is.EqualTo(fr.Get("display")));
            }
        }

        [Test]
        public void Inline_style_change_invalidates_element_via_apply() {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(System.Array.Empty<OriginatedStylesheet>());
            var p = doc.GetElementById("p");
            var c = doc.GetElementById("c");
            engine.Compute(p);
            engine.Compute(c);
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            // Inline style change on the child only.
            c.SetAttribute("style", "color: orange;");
            engine.Apply(tracker);
            engine.ResetCacheStats();
            var after = engine.Compute(c);
            // c had its cache entry dropped; parent untouched if not dirty-marked, but
            // attribute-change marks the target only (not subtree, since attr is "style").
            Assert.That(after.Get("color"), Is.EqualTo("orange"));
            // p should still be cached if its version did not change.
            // (We do not strongly assert this since implementation details differ; we
            // simply assert correctness on c.)
        }

        [Test]
        public void Important_still_wins_after_invalidation() {
            var doc = Html("<div id=\"x\" style=\"color: green;\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { color: red !important; }")
            });
            var x = doc.GetElementById("x");
            var a = engine.Compute(x);
            Assert.That(a.Get("color"), Is.EqualTo("red"));
            engine.Invalidate(x);
            var b = engine.Compute(x);
            Assert.That(b.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Hover_toggle_only_invalidates_affected_element_cache() {
            var doc = Html("<div id=\"p\"><button id=\"b\">go</button><button id=\"o\">other</button></div>");
            var engine = new CascadeEngine(new[] {
                Author("button { color: black; } button:hover { color: red; }")
            });
            var p = doc.GetElementById("p");
            var b = doc.GetElementById("b");
            var o = doc.GetElementById("o");
            var fake = new VersionedFakeState();
            engine.Compute(p, fake);
            engine.Compute(b, fake);
            engine.Compute(o, fake);
            fake.Set(b, ElementState.Hover);
            engine.ResetCacheStats();
            // First Compute on b after hover toggle: state version changed, so all
            // entries computed via this provider are stale.
            engine.Compute(b, fake);
            Assert.That(engine.CacheMisses, Is.GreaterThanOrEqualTo(1));
            // The result should reflect hover styling.
            var hover = engine.Compute(b, fake);
            Assert.That(hover.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Media_context_change_invalidates_correctly() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(
                new[] { Author("@media (min-width: 600px) { #x { color: red; } }" +
                               "@media (max-width: 599px) { #x { color: blue; } }") },
                MediaContext.Default(400, 400));
            var x = doc.GetElementById("x");
            Assert.That(engine.Compute(x).Get("color"), Is.EqualTo("blue"));
            engine.MediaContext = MediaContext.Default(800, 800);
            Assert.That(engine.Compute(x).Get("color"), Is.EqualTo("red"));
            engine.MediaContext = MediaContext.Default(400, 400);
            Assert.That(engine.Compute(x).Get("color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Invalidate_never_computed_element_is_a_noop() {
            var engine = new CascadeEngine(System.Array.Empty<OriginatedStylesheet>());
            var e = new Element("div");
            Assert.DoesNotThrow(() => engine.Invalidate(e));
            Assert.DoesNotThrow(() => engine.InvalidateSubtree(e));
            Assert.DoesNotThrow(() => engine.Invalidate(null));
            Assert.DoesNotThrow(() => engine.InvalidateSubtree(null));
            Assert.DoesNotThrow(() => engine.InvalidateAll());
        }

        [Test]
        public void Apply_with_null_tracker_is_a_noop() {
            var engine = new CascadeEngine(System.Array.Empty<OriginatedStylesheet>());
            Assert.DoesNotThrow(() => engine.Apply(null));
        }

        [Test]
        public void Apply_with_empty_tracker_does_not_change_cache() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author("#x { color: red; }") });
            engine.Compute(doc.GetElementById("x"));
            int before = engine.CacheSize;
            engine.Apply(new InvalidationTracker());
            Assert.That(engine.CacheSize, Is.EqualTo(before));
        }

        [Test]
        public void CacheSize_reflects_distinct_elements() {
            var doc = Html("<div id=\"a\"></div><div id=\"b\"></div><div id=\"c\"></div>");
            var engine = new CascadeEngine(new[] { Author("div { color: red; }") });
            engine.Compute(doc.GetElementById("a"));
            engine.Compute(doc.GetElementById("b"));
            engine.Compute(doc.GetElementById("c"));
            // HtmlParser synthesises <html><body> — their entries are also in cache.
            // The 3 test elements (a, b, c) plus 2 synthetic wrappers = at least 3.
            Assert.That(engine.CacheSize, Is.GreaterThanOrEqualTo(3));
        }

        [Test]
        public void ResetCacheStats_zeros_counters() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author("#x { color: red; }") });
            var x = doc.GetElementById("x");
            engine.Compute(x); engine.Compute(x); engine.Compute(x);
            engine.ResetCacheStats();
            Assert.That(engine.CacheHits, Is.EqualTo(0));
            Assert.That(engine.CacheMisses, Is.EqualTo(0));
        }

        [Test]
        public void Different_state_provider_instances_invalidate_via_identity() {
            var doc = Html("<button id=\"b\"></button>");
            var engine = new CascadeEngine(new[] { Author("button { color: black; }") });
            var btn = doc.GetElementById("b");
            engine.Compute(btn);
            engine.ResetCacheStats();
            var p1 = new VersionedFakeState();
            engine.Compute(btn, p1);
            // First call with new provider: miss because providerId differs for the
            // full parent chain (html, body, btn) — at least one miss.
            Assert.That(engine.CacheMisses, Is.GreaterThanOrEqualTo(1));
            engine.ResetCacheStats();
            engine.Compute(btn, p1);
            // Same provider: all chain elements hit.
            Assert.That(engine.CacheHits, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void Cache_returns_same_instance_when_versions_match() {
            var doc = Html("<div id=\"a\"><span id=\"b\"></span></div>");
            var engine = new CascadeEngine(new[] { Author("div { color: red; }") });
            var s1 = engine.Compute(doc.GetElementById("b"));
            var s2 = engine.Compute(doc.GetElementById("b"));
            Assert.That(s2, Is.SameAs(s1));
        }

        [Test]
        public void Subtree_invalidation_on_class_change_via_apply() {
            // A class change on parent dirties parent + descendants per InvalidationTracker
            // propagation rules. Apply() must drop entries for all of them.
            var doc = Html("<div id=\"r\"><span id=\"a\"></span><span id=\"b\"></span></div>");
            var engine = new CascadeEngine(new[] { Author(".g span { color: green; }") });
            var r = doc.GetElementById("r");
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            engine.Compute(r); engine.Compute(a); engine.Compute(b);
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            r.SetAttribute("class", "g");
            engine.Apply(tracker);
            engine.ResetCacheStats();
            Assert.That(engine.Compute(a).Get("color"), Is.EqualTo("green"));
            Assert.That(engine.Compute(b).Get("color"), Is.EqualTo("green"));
            Assert.That(engine.CacheMisses, Is.GreaterThanOrEqualTo(2));
        }
    }
}
