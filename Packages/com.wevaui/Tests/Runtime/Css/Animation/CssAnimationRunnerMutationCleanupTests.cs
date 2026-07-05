using NUnit.Framework;
using Weva.Css;
using Weva.Css.Animation;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Events;

namespace Weva.Tests.Css.Animation {
    // MS2 leak-regression: CssAnimationRunner holds element refs in eight
    // element-keyed dictionaries (transitions, animations, animationsByElement,
    // transitionsByElement, animatedElements, transitioningElements,
    // elementSpecsCache, composedCache). Pre-fix, removing an element from
    // the DOM mid-animation left every one of those dictionaries holding a
    // strong reference to the orphan — most visible on `animation-iteration-
    // count: infinite` where the per-tick sweep never naturally evicts the
    // record. The fix subscribes the runner to Document.Mutated and routes
    // ChildRemoved events through RemoveElement on every descendant of the
    // removed root.
    public class CssAnimationRunnerMutationCleanupTests {
        static (CssAnimationRunner runner, FakeUIClock clock, Document doc) MakeRunner(string css) {
            var clock = new FakeUIClock();
            var sheet = CssParser.Parse(css);
            var cascade = new CascadeEngine(System.Array.Empty<OriginatedStylesheet>());
            var runner = new CssAnimationRunner(cascade, new[] { sheet }, clock);
            var doc = new Document();
            runner.AttachToDocument(doc);
            return (runner, clock, doc);
        }

        static ComputedStyle Style(Element e, params (string, string)[] kv) {
            var s = new ComputedStyle(e);
            foreach (var pair in kv) s.Set(pair.Item1, pair.Item2);
            return s;
        }

        [Test]
        public void Removing_element_with_active_animation_drops_every_internal_dictionary_entry() {
            // The canonical case: an element with an active keyframe animation
            // is removed from the DOM. Pre-fix every internal dictionary
            // entry was leaked because nothing called Stop(element) on
            // ChildRemoved. Post-fix the mutation listener walks the removed
            // subtree and routes through RemoveElement.
            var (runner, clock, doc) = MakeRunner(
                "@keyframes a { from { opacity: 0; } to { opacity: 1; } }");
            var e = new Element("div");
            doc.AppendChild(e);
            var s = Style(e,
                ("animation-name", "a"),
                ("animation-duration", "1s"),
                ("animation-iteration-count", "2"),
                ("animation-timing-function", "linear"));
            runner.OnStyleChange(e, null, s);
            // Drive Compose once to populate the composedCache for this element.
            clock.Set(0.25);
            runner.Tick(0.25);
            runner.Compose(e, s);
            Assume.That(runner.RunningAnimationCount, Is.EqualTo(1));
            Assume.That(runner.ContainsElementInAnyDictionary(e), Is.True);

            // Now remove mid-flight. The animation has NOT finished; the
            // per-tick sweep would not have evicted this record on its own.
            doc.RemoveChild(e);

            // Every element-keyed dictionary should now be empty for `e`.
            Assert.That(runner.RunningAnimationCount, Is.Zero,
                "primary (Element, string)-keyed animations dict");
            Assert.That(runner.AnimationsByElementCount, Is.Zero,
                "element-indexed animations mirror list");
            Assert.That(runner.AnimatedElementsCount, Is.Zero,
                "animatedElements membership set");
            Assert.That(runner.ElementSpecsCacheCount, Is.Zero,
                "per-element spec-parse cache");
            Assert.That(runner.ComposedCacheCount, Is.Zero,
                "per-element composed-style cache");
            Assert.That(runner.ContainsElementInAnyDictionary(e), Is.False,
                "no dictionary still holds a reference to the removed element");
            Assert.That(runner.HasRunningAnimations(e), Is.False);
            Assert.That(runner.HasActiveCompositions, Is.False);
        }

        [Test]
        public void Removing_element_with_active_transition_drops_every_internal_dictionary_entry() {
            // Same as above but driving the transition side of the runner.
            var (runner, clock, doc) = MakeRunner("");
            var e = new Element("div");
            doc.AppendChild(e);
            var prev = Style(e,
                ("background-color", "rgb(0, 0, 0)"),
                ("transition", "background-color 1s linear"));
            var next = Style(e,
                ("background-color", "rgb(255, 255, 255)"),
                ("transition", "background-color 1s linear"));
            runner.OnStyleChange(e, prev, next);
            // Populate composedCache by composing once.
            clock.Set(0.25);
            runner.Tick(0.25);
            runner.Compose(e, next);
            Assume.That(runner.RunningTransitionCount, Is.EqualTo(1));
            Assume.That(runner.ContainsElementInAnyDictionary(e), Is.True);

            doc.RemoveChild(e);

            Assert.That(runner.RunningTransitionCount, Is.Zero,
                "primary (Element, string)-keyed transitions dict");
            Assert.That(runner.TransitionsByElementCount, Is.Zero,
                "element-indexed transitions mirror list");
            Assert.That(runner.TransitioningElementsCount, Is.Zero,
                "transitioningElements membership set");
            Assert.That(runner.ElementSpecsCacheCount, Is.Zero,
                "per-element spec-parse cache");
            Assert.That(runner.ComposedCacheCount, Is.Zero,
                "per-element composed-style cache");
            Assert.That(runner.ContainsElementInAnyDictionary(e), Is.False,
                "no dictionary still holds a reference to the removed element");
            Assert.That(runner.HasRunningAnimations(e), Is.False);
        }

        [Test]
        public void Removing_subtree_drops_state_for_parent_and_descendants() {
            // The DOM removal fires a single ChildRemoved event on the
            // subtree ROOT — the runner must walk descendants itself.
            // Without the recursive walk, only the parent's state would be
            // dropped and every animated descendant would leak.
            var (runner, clock, doc) = MakeRunner(
                "@keyframes a { from { opacity: 0; } to { opacity: 1; } }");
            var parent = new Element("div");
            var child = new Element("span");
            doc.AppendChild(parent);
            parent.AppendChild(child);

            var parentStyle = Style(parent,
                ("animation-name", "a"),
                ("animation-duration", "1s"),
                ("animation-iteration-count", "infinite"),
                ("animation-timing-function", "linear"));
            var childStyle = Style(child,
                ("animation-name", "a"),
                ("animation-duration", "1s"),
                ("animation-iteration-count", "infinite"),
                ("animation-timing-function", "linear"));
            runner.OnStyleChange(parent, null, parentStyle);
            runner.OnStyleChange(child, null, childStyle);
            clock.Set(0.25);
            runner.Tick(0.25);
            runner.Compose(parent, parentStyle);
            runner.Compose(child, childStyle);
            Assume.That(runner.RunningAnimationCount, Is.EqualTo(2));
            Assume.That(runner.ContainsElementInAnyDictionary(parent), Is.True);
            Assume.That(runner.ContainsElementInAnyDictionary(child), Is.True);

            // Remove the parent — child is implicitly torn down with it.
            doc.RemoveChild(parent);

            Assert.That(runner.RunningAnimationCount, Is.Zero,
                "both parent's and child's animation records dropped");
            Assert.That(runner.AnimationsByElementCount, Is.Zero);
            Assert.That(runner.AnimatedElementsCount, Is.Zero);
            Assert.That(runner.ComposedCacheCount, Is.Zero);
            Assert.That(runner.ContainsElementInAnyDictionary(parent), Is.False,
                "parent is fully evicted");
            Assert.That(runner.ContainsElementInAnyDictionary(child), Is.False,
                "child (descendant of removed subtree root) is fully evicted");
        }

        [Test]
        public void Removing_element_with_infinite_animation_no_longer_pins_it() {
            // The canonical MS2 leak: animation-iteration-count: infinite
            // means the per-tick sweep at TickInternal NEVER evicts the
            // record (the >0 / IsPositiveInfinity guard skips it on every
            // tick), so without a removal hook the orphan stays pinned for
            // the lifetime of the runner. This test pins exactly that
            // scenario: an element with an infinite animation, removed mid-
            // flight, plus a long Tick to prove the natural sweep doesn't
            // help — only the mutation hook can free it.
            var (runner, clock, doc) = MakeRunner(
                "@keyframes spin { from { opacity: 0; } to { opacity: 1; } }");
            var e = new Element("div");
            doc.AppendChild(e);
            var s = Style(e,
                ("animation-name", "spin"),
                ("animation-duration", "1s"),
                ("animation-iteration-count", "infinite"),
                ("animation-timing-function", "linear"));
            runner.OnStyleChange(e, null, s);
            clock.Set(0.5);
            runner.Tick(0.5);
            runner.Compose(e, s);
            Assume.That(runner.RunningAnimationCount, Is.EqualTo(1),
                "infinite animation is live before removal");

            doc.RemoveChild(e);

            // Crucial assertion: the record is gone IMMEDIATELY on removal,
            // not eventually-on-completion (it would never complete).
            Assert.That(runner.RunningAnimationCount, Is.Zero,
                "infinite-iteration animation evicted on DOM removal");
            Assert.That(runner.ContainsElementInAnyDictionary(e), Is.False,
                "no dictionary still holds the removed element");

            // Drive a long tick to prove the orphan would otherwise have
            // survived — but it's already gone, so the tick is a no-op.
            clock.Set(1000);
            runner.Tick(1000);
            Assert.That(runner.RunningAnimationCount, Is.Zero);
            Assert.That(runner.HasActiveCompositions, Is.False);
        }

        [Test]
        public void Dispose_detaches_mutation_subscription_and_drops_all_state() {
            // Dispose must release the Document.Mutated subscription so a
            // teardown / rebuild cycle (HotReload, WevaDocument.OnDisable)
            // doesn't double-subscribe or pin the prior doc via the
            // mutationListener delegate. It must also drop every
            // element-keyed dictionary so the runner releases its own
            // references for GC.
            var (runner, clock, doc) = MakeRunner(
                "@keyframes a { from { opacity: 0; } to { opacity: 1; } }");
            var e = new Element("div");
            doc.AppendChild(e);
            var s = Style(e,
                ("animation-name", "a"),
                ("animation-duration", "1s"),
                ("animation-iteration-count", "infinite"),
                ("animation-timing-function", "linear"));
            runner.OnStyleChange(e, null, s);
            runner.Compose(e, s);
            Assume.That(runner.ContainsElementInAnyDictionary(e), Is.True);

            runner.Dispose();

            Assert.That(runner.RunningAnimationCount, Is.Zero);
            Assert.That(runner.RunningTransitionCount, Is.Zero);
            Assert.That(runner.ComposedCacheCount, Is.Zero);
            Assert.That(runner.ContainsElementInAnyDictionary(e), Is.False);

            // Removing an element AFTER Dispose must not throw — the
            // subscription is detached so OnDomMutation never fires; even
            // if it did, the disposed guard at the top of OnDomMutation
            // short-circuits before touching any dictionary.
            var other = new Element("span");
            doc.AppendChild(other);
            Assert.DoesNotThrow(() => doc.RemoveChild(other));
        }
    }
}
