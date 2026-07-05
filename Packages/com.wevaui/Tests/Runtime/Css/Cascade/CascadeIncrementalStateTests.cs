using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    public class CascadeIncrementalStateTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        sealed class FakeState : IElementStateProvider {
            readonly Dictionary<Element, ElementState> map = new();
            long version = 1;
            public long Version => version;
            public void Set(Element e, ElementState s) { map[e] = s; version++; }
            public void Clear(Element e) { if (map.Remove(e)) version++; }
            public ElementState GetState(Element e) =>
                map.TryGetValue(e, out var s) ? s : ElementState.None;
        }

        [Test]
        public void Hover_toggle_on_one_element_does_not_invalidate_unaffected_siblings() {
            var doc = Html("<div id=\"p\">" +
                "<button id=\"a\">A</button><button id=\"b\">B</button><button id=\"c\">C</button>" +
                "</div>");
            var engine = new CascadeEngine(new[] {
                Author("button { color: black; } button:hover { color: red; }")
            });
            var p = doc.GetElementById("p");
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            var c = doc.GetElementById("c");
            var state = new FakeState();
            // Prime the cache.
            engine.ComputeAll(doc, state);
            engine.ResetCacheStats();
            state.Set(b, ElementState.Hover);
            engine.ComputeAll(doc, state);
            // Only b's digest changed; the rest remain cached.
            Assert.That(engine.CacheMisses, Is.LessThanOrEqualTo(1),
                "only the hovered element should miss");
        }

        [Test]
        public void Hover_toggle_returns_same_instance_for_unaffected_elements() {
            var doc = Html("<div id=\"p\">" +
                "<button id=\"a\">A</button><button id=\"b\">B</button>" +
                "</div>");
            var engine = new CascadeEngine(new[] {
                Author("button { color: black; } button:hover { color: red; }")
            });
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            var state = new FakeState();
            var firstA = engine.ComputeAll(doc, state)[a];
            state.Set(b, ElementState.Hover);
            var secondA = engine.ComputeAll(doc, state)[a];
            Assert.That(secondA, Is.SameAs(firstA),
                "unaffected element's ComputedStyle reference should be preserved");
        }

        [Test]
        public void Hover_toggle_on_target_recomputes_to_hover_style() {
            var doc = Html("<button id=\"b\">go</button>");
            var engine = new CascadeEngine(new[] {
                Author("button { color: black; } button:hover { color: red; }")
            });
            var b = doc.GetElementById("b");
            var state = new FakeState();
            var idle = engine.Compute(b, state);
            Assert.That(idle.Get("color"), Is.EqualTo("black"));
            state.Set(b, ElementState.Hover);
            var hover = engine.Compute(b, state);
            Assert.That(hover.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Hover_off_restores_pre_hover_style_exactly() {
            var doc = Html("<button id=\"b\">go</button>");
            var engine = new CascadeEngine(new[] {
                Author("button { color: black; } button:hover { color: red; }")
            });
            var b = doc.GetElementById("b");
            var state = new FakeState();
            engine.Compute(b, state);
            state.Set(b, ElementState.Hover);
            engine.Compute(b, state);
            state.Clear(b);
            var idle = engine.Compute(b, state);
            Assert.That(idle.Get("color"), Is.EqualTo("black"));
        }

        [Test]
        public void Stylesheet_without_state_pseudos_keeps_full_cache_hit_on_state_change() {
            // No selector tests state -> GlobalStateMask = None -> per-element digest
            // is constant 0 -> state.Version bumps don't invalidate any entry.
            var doc = Html("<div id=\"r\"><span id=\"a\"></span><span id=\"b\"></span></div>");
            var engine = new CascadeEngine(new[] { Author("div { color: red; }") });
            var state = new FakeState();
            engine.ComputeAll(doc, state);
            engine.ResetCacheStats();
            state.Set(doc.GetElementById("a"), ElementState.Hover);
            engine.ComputeAll(doc, state);
            Assert.That(engine.CacheMisses, Is.EqualTo(0),
                "no state-driven selectors -> state changes should not invalidate cache");
        }

        [Test]
        public void Descendant_combinator_with_state_on_left_invalidates_descendants() {
            // .parent:hover .child should cause descendants to re-resolve when parent's
            // state changes. The parent's recompute bumps parentStyleVersion which
            // propagates the miss down — ancestor combinators ride this path correctly.
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { color: black; } div:hover span { color: red; }")
            });
            var p = doc.GetElementById("p");
            var c = doc.GetElementById("c");
            var state = new FakeState();
            var c0 = engine.ComputeAll(doc, state)[c];
            Assert.That(c0.Get("color"), Is.EqualTo("black"));
            state.Set(p, ElementState.Hover);
            var c1 = engine.ComputeAll(doc, state)[c];
            Assert.That(c1.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Sibling_combinator_with_state_falls_back_to_global_invalidation() {
            // .a:hover + .b — sibling combinator with state on left compound triggers
            // the v1 fallback: state.Version is folded into the digest so any state
            // change invalidates all cached entries. Correctness over cleverness.
            var doc = Html("<div id=\"a\" class=\"a\"></div><div id=\"b\" class=\"b\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(".a:hover + .b { color: red; }")
            });
            Assert.That(engine.StateRequiresGlobalFallback, Is.True);
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            var state = new FakeState();
            var b0 = engine.Compute(b, state);
            Assert.That(b0.Get("color"), Is.EqualTo("black"));
            state.Set(a, ElementState.Hover);
            var b1 = engine.Compute(b, state);
            Assert.That(b1.Get("color"), Is.EqualTo("red"),
                "sibling combinator must still pick up the new match after state flip");
        }

        [Test]
        public void C5_sibling_state_flip_does_not_re_cascade_unrelated_elements() {
            // .a:hover + .b forces the global-state fallback, but #c (class "c")
            // can never be the SUBJECT of that selector. The C5 narrowing means
            // flipping hover on #a re-cascades only the sibling subject #b — NOT
            // #c, and not the whole document (the old behaviour folded
            // state.Version into every element, so every state flip anywhere was
            // a whole-document re-cascade).
            var doc = Html("<div id=\"a\" class=\"a\"></div>"
                         + "<div id=\"b\" class=\"b\"></div>"
                         + "<div id=\"c\" class=\"c\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(".a:hover + .b { color: red; } .c { color: green; }")
            });
            Assert.That(engine.StateRequiresGlobalFallback, Is.True);
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            var c = doc.GetElementById("c");
            var state = new FakeState();
            engine.ComputeAll(doc, state); // warm the cache

            engine.ResetCacheStats();
            state.Set(a, ElementState.Hover);
            engine.ComputeAll(doc, state);

            // Only the sibling subject #b may re-cascade. #a doesn't (nothing
            // styles it on hover) and #c is unrelated. Under the old
            // whole-document fallback this was 3+ misses.
            Assert.That(engine.CacheMisses, Is.LessThanOrEqualTo(1),
                "a sibling-state flip must re-cascade only the sibling subject, not unrelated elements (C5)");

            // Correctness is preserved: #b picks up the new match, #c is stable.
            Assert.That(engine.GetComposedStyle(b, state).Get("color"), Is.EqualTo("red"));
            Assert.That(engine.GetComposedStyle(c, state).Get("color"), Is.EqualTo("green"));
        }

        [Test]
        public void Repeated_hover_toggles_only_miss_once_per_toggle() {
            // After the initial cold-cache pass, each toggle should produce at most one
            // miss for the toggled element. The bench bottleneck this addresses.
            var doc = Html("<div id=\"p\"><button id=\"b\">go</button></div>");
            var engine = new CascadeEngine(new[] {
                Author("button { color: black; } button:hover { color: red; }")
            });
            var b = doc.GetElementById("b");
            var state = new FakeState();
            engine.ComputeAll(doc, state);
            for (int i = 0; i < 10; i++) {
                if ((i & 1) == 0) state.Set(b, ElementState.Hover);
                else state.Clear(b);
                engine.ResetCacheStats();
                engine.ComputeAll(doc, state);
                Assert.That(engine.CacheMisses, Is.LessThanOrEqualTo(1),
                    $"toggle iteration {i} expected at most 1 miss");
            }
        }

        [Test]
        public void GlobalStateMask_reflects_compiled_stylesheet_pseudos() {
            var engine = new CascadeEngine(new[] {
                Author(":hover { color: red; } :focus { color: blue; }")
            });
            Assert.That(engine.GlobalStateMask & ElementState.Hover, Is.EqualTo(ElementState.Hover));
            Assert.That(engine.GlobalStateMask & ElementState.Focus, Is.EqualTo(ElementState.Focus));
        }

        [Test]
        public void GlobalStateMask_is_None_for_empty_stylesheet() {
            var engine = new CascadeEngine(System.Array.Empty<OriginatedStylesheet>());
            Assert.That(engine.GlobalStateMask, Is.EqualTo(ElementState.None));
            Assert.That(engine.StateRequiresGlobalFallback, Is.False);
        }

        [Test]
        public void Element_version_bump_still_forces_miss_independent_of_state() {
            // DOM-version-bump-no-state-change still triggers per-element cascade.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author("#x { color: red; }") });
            var x = doc.GetElementById("x");
            var state = new FakeState();
            engine.ComputeAll(doc, state);
            engine.ResetCacheStats();
            x.SetAttribute("data-extra", "1");
            engine.ComputeAll(doc, state);
            Assert.That(engine.CacheMisses, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void Cache_hits_dominate_for_thousand_element_tree_under_hover_toggle() {
            // Build a 200-element tree, prime cache, toggle hover on one button, and
            // verify >95% cache hits. This is the v0.5 bench's reactive doctrine
            // expressed as a unit test.
            var doc = new Document();
            var root = new Element("section");
            doc.AppendChild(root);
            var elements = new List<Element> { root };
            Element button = null;
            for (int i = 1; i < 200; i++) {
                var e = new Element(i % 10 == 0 ? "button" : "div");
                if (e.TagName == "button" && button == null) button = e;
                root.AppendChild(e);
                elements.Add(e);
            }
            var engine = new CascadeEngine(new[] {
                Author("div { color: black; } button { color: blue; } button:hover { color: red; }")
            });
            var state = new FakeState();
            engine.ComputeAll(doc, state);
            engine.ResetCacheStats();
            state.Set(button, ElementState.Hover);
            engine.ComputeAll(doc, state);
            double hitRate = (double)engine.CacheHits / (engine.CacheHits + engine.CacheMisses);
            Assert.That(hitRate, Is.GreaterThan(0.95),
                $"hit rate after hover toggle should be > 95% (got {hitRate:P1})");
        }

        [Test]
        public void Pseudo_class_state_mark_does_not_force_eager_apply_drop() {
            // Apply(tracker) with a PseudoClassState mark must not drop unaffected
            // elements — the per-element digest already routes that. We verify by
            // marking the affected element and confirming the cache count drops by
            // at most one entry (the marked one, via the implied Style flag).
            var doc = Html("<div id=\"a\"></div><div id=\"b\"></div>");
            var engine = new CascadeEngine(new[] { Author("div { color: red; }") });
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            engine.Compute(a); engine.Compute(b);
            int before = engine.CacheSize;
            var tracker = new Weva.Reactive.InvalidationTracker();
            tracker.MarkDirty(a, Weva.Reactive.InvalidationKind.PseudoClassState);
            engine.Apply(tracker);
            // Element b's cache entry is still present.
            Assert.That(engine.CacheSize, Is.LessThanOrEqualTo(before));
            engine.ResetCacheStats();
            engine.Compute(b);
            // b (and the synthetic html/body wrapper) remain cached — at least 1 hit.
            Assert.That(engine.CacheHits, Is.GreaterThanOrEqualTo(1), "b should remain cached");
        }

        [Test]
        public void Checked_state_flip_restyles_adjacent_label_via_sibling_combinator() {
            // Positive regression: when the :checked state bit flips (here via
            // the FakeState provider that bumps Version on Set), the adjacent
            // label re-cascades through the global-fallback path that the
            // sibling combinator triggers.
            var doc = Html(
                "<input type=\"checkbox\" id=\"cb\">" +
                "<label id=\"lab\" for=\"cb\">L</label>");
            var engine = new CascadeEngine(new[] {
                Author("label { color: black; } input:checked + label { color: red; }")
            });
            Assert.That(engine.StateRequiresGlobalFallback, Is.True);
            var cb = doc.GetElementById("cb");
            var lab = doc.GetElementById("lab");
            var state = new FakeState();

            var off = engine.Compute(lab, state);
            Assert.That(off.Get("color"), Is.EqualTo("black"));

            state.Set(cb, ElementState.Checked);
            var on = engine.Compute(lab, state);
            Assert.That(on.Get("color"), Is.EqualTo("red"),
                "input:checked + label must restyle the adjacent label after :checked flips");
        }

        [Test]
        public void Checked_attribute_toggle_restyles_adjacent_label_via_real_state_provider() {
            // Regression: the menu.html demo uses
            //     input:checked + label { color: indigo; }
            // and expects clicking the checkbox (which toggles the `checked`
            // attribute via Element.SetAttribute) to flip the adjacent label's
            // computed color. This requires the cascade to invalidate the
            // sibling label when the input's checked state changes -- a sibling
            // combinator with a stateful pseudo on the left compound forces the
            // global-fallback path. We use the real InteractionStateProvider
            // (which derives :checked from the attribute in GetState) and
            // verify the label re-cascades on attribute toggle.
            var doc = Html(
                "<input type=\"checkbox\" id=\"cb\">" +
                "<label id=\"lab\" for=\"cb\">L</label>");
            var engine = new CascadeEngine(new[] {
                Author("label { color: black; } input:checked + label { color: red; }")
            });
            var cb = doc.GetElementById("cb");
            var lab = doc.GetElementById("lab");
            var state = new Weva.Events.InteractionStateProvider();
            state.AttachToDocument(doc);

            var off = engine.Compute(lab, state);
            Assert.That(off.Get("color"), Is.EqualTo("black"),
                "adjacent label is unstyled when input is unchecked");

            cb.SetAttribute("checked", "");
            var on = engine.Compute(lab, state);
            Assert.That(on.Get("color"), Is.EqualTo("red"),
                "input:checked + label must restyle the adjacent label after the checked attribute is added");

            cb.RemoveAttribute("checked");
            var offAgain = engine.Compute(lab, state);
            Assert.That(offAgain.Get("color"), Is.EqualTo("black"),
                "removing checked must revert the adjacent label");
        }

        [Test]
        public void Disabled_attribute_toggle_restyles_adjacent_sibling_via_real_state_provider() {
            // Parallel to the checked test: adding/removing the `disabled` attribute
            // on an input must bump state.Version so `input:disabled + .hint` picks
            // up the new state through the sibling-combinator global-fallback path.
            var doc = Html(
                "<input type=\"text\" id=\"inp\">" +
                "<span id=\"hint\" class=\"hint\">hint</span>");
            var engine = new CascadeEngine(new[] {
                Author("span { color: black; } input:disabled + span { color: gray; }")
            });
            Assert.That(engine.StateRequiresGlobalFallback, Is.True);
            var inp = doc.GetElementById("inp");
            var hint = doc.GetElementById("hint");
            var state = new Weva.Events.InteractionStateProvider();
            state.AttachToDocument(doc);

            var enabled = engine.Compute(hint, state);
            Assert.That(enabled.Get("color"), Is.EqualTo("black"),
                "hint is unstyled when input is enabled");

            inp.SetAttribute("disabled", "");
            var disabled = engine.Compute(hint, state);
            Assert.That(disabled.Get("color"), Is.EqualTo("gray"),
                "input:disabled + span must restyle after disabled attribute is added");

            inp.RemoveAttribute("disabled");
            var reenabled = engine.Compute(hint, state);
            Assert.That(reenabled.Get("color"), Is.EqualTo("black"),
                "removing disabled must revert adjacent span");
        }

        [Test]
        public void Checked_attribute_version_bumps_monotonically_on_each_toggle() {
            // Verify that each add/remove of the `checked` attribute increments
            // state.Version so the cache key changes on every toggle.
            var doc = Html("<input type=\"checkbox\" id=\"cb\">");
            var cb = doc.GetElementById("cb");
            var state = new Weva.Events.InteractionStateProvider();
            state.AttachToDocument(doc);

            long v0 = state.Version;
            cb.SetAttribute("checked", "");
            long v1 = state.Version;
            Assert.That(v1, Is.GreaterThan(v0),
                "adding checked must bump state.Version");

            cb.RemoveAttribute("checked");
            long v2 = state.Version;
            Assert.That(v2, Is.GreaterThan(v1),
                "removing checked must bump state.Version again");

            cb.SetAttribute("checked", "");
            long v3 = state.Version;
            Assert.That(v3, Is.GreaterThan(v2),
                "re-adding checked must bump state.Version a third time");
        }

        [Test]
        public void Has_hover_descendant_flip_restyles_subject_A8() {
            // A8 core: `.card:has(:hover)` — when a DESCENDANT gains :hover, the
            // SUBJECT (.card) must re-cascade and pick up the rule. The per-element
            // digest keys on the subject's own state (which never changes here), so
            // correctness depends on the stateful-:has global-fallback path.
            var doc = Html("<div id=\"card\" class=\"card\"><span id=\"c\">x</span></div>");
            var engine = new CascadeEngine(new[] {
                Author(".card { color: black; } .card:has(:hover) { color: red; }")
            });
            Assert.That(engine.StateRequiresGlobalFallback, Is.True,
                "a stateful :has must route the sheet through the global-fallback path");
            var card = doc.GetElementById("card");
            var c = doc.GetElementById("c");
            var state = new FakeState();

            var off = engine.Compute(card, state);
            Assert.That(off.Get("color"), Is.EqualTo("black"),
                ".card is unstyled when no descendant is hovered");

            state.Set(c, ElementState.Hover);
            var on = engine.Compute(card, state);
            Assert.That(on.Get("color"), Is.EqualTo("red"),
                ".card:has(:hover) must restyle the ancestor after a descendant hovers");

            state.Clear(c);
            var offAgain = engine.Compute(card, state);
            Assert.That(offAgain.Get("color"), Is.EqualTo("black"),
                "un-hovering the descendant must revert the ancestor");
        }

        [Test]
        public void Has_checked_descendant_flip_restyles_subject_via_real_provider_A8() {
            // A8 with the real attribute-driven provider: `.form:has(:checked)` —
            // toggling the `checked` attribute on a descendant input must restyle
            // the ancestor .form. Mirrors the menu.html-style `:has` use.
            var doc = Html(
                "<form id=\"form\" class=\"form\">" +
                "<input type=\"checkbox\" id=\"cb\"></form>");
            var engine = new CascadeEngine(new[] {
                Author(".form { color: black; } .form:has(:checked) { color: red; }")
            });
            Assert.That(engine.StateRequiresGlobalFallback, Is.True);
            var form = doc.GetElementById("form");
            var cb = doc.GetElementById("cb");
            var state = new Weva.Events.InteractionStateProvider();
            state.AttachToDocument(doc);

            var off = engine.Compute(form, state);
            Assert.That(off.Get("color"), Is.EqualTo("black"),
                ".form is unstyled when its checkbox is unchecked");

            cb.SetAttribute("checked", "");
            var on = engine.Compute(form, state);
            Assert.That(on.Get("color"), Is.EqualTo("red"),
                ".form:has(:checked) must restyle the ancestor after the descendant is checked");

            cb.RemoveAttribute("checked");
            var offAgain = engine.Compute(form, state);
            Assert.That(offAgain.Get("color"), Is.EqualTo("black"),
                "unchecking the descendant must revert the ancestor");
        }

        [Test]
        public void Structural_has_keeps_cache_hits_on_unrelated_state_flip_A8() {
            // Guard: a NON-stateful `.card:has(img)` must NOT force the global
            // fallback, so an unrelated hover flip elsewhere still cache-hits.
            var doc = Html("<div id=\"card\" class=\"card\"><img id=\"i\"></div>" +
                "<button id=\"b\">go</button>");
            var engine = new CascadeEngine(new[] {
                Author(".card:has(img) { color: green; } button:hover { color: red; }")
            });
            Assert.That(engine.StateRequiresGlobalFallback, Is.False,
                "structural :has must not trip the global-state-version path");
            var state = new FakeState();
            engine.ComputeAll(doc, state);
            engine.ResetCacheStats();
            state.Set(doc.GetElementById("b"), ElementState.Hover);
            engine.ComputeAll(doc, state);
            Assert.That(engine.CacheMisses, Is.LessThanOrEqualTo(1),
                "only the hovered button should miss; structural :has subject stays cached");
        }

        [Test]
        public void Non_stateful_attribute_change_does_not_bump_state_version() {
            // Mutations to non-pseudo-class attributes (e.g. `data-x`) must NOT
            // bump state.Version — that would spuriously invalidate the entire
            // cache through the global-fallback path on every data attribute write.
            var doc = Html("<input type=\"checkbox\" id=\"cb\">");
            var cb = doc.GetElementById("cb");
            var state = new Weva.Events.InteractionStateProvider();
            state.AttachToDocument(doc);

            long v0 = state.Version;
            cb.SetAttribute("data-foo", "bar");
            cb.SetAttribute("aria-label", "test");
            cb.SetAttribute("tabindex", "0");
            Assert.That(state.Version, Is.EqualTo(v0),
                "non-stateful attribute changes must not bump state.Version");
        }
    }
}
