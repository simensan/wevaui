using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Animation;
using Weva.Css.Cascade;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Events;
using Weva.Parsing;
using Weva.Reactive;

namespace Weva.Tests.Css.Animation {
    public class CascadeAnimationIntegrationTests {
        const double Eps = 0.05;

        sealed class HoverState : IElementStateProvider {
            readonly Dictionary<Element, ElementState> map = new();
            long version = 1;
            public long Version => version;
            public void Set(Element e, ElementState s) { map[e] = s; version++; }
            public void Clear(Element e) { if (map.Remove(e)) version++; }
            public ElementState GetState(Element e) =>
                map.TryGetValue(e, out var s) ? s : ElementState.None;
        }

        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        [Test]
        public void Hover_transition_produces_interpolated_value() {
            var doc = Html("<button id=\"b\">go</button>");
            var sheet = Css(
                "button { background-color: rgb(0, 0, 0); transition: background-color 100ms linear; } " +
                "button:hover { background-color: rgb(255, 255, 255); }");
            var clock = new FakeUIClock();
            var cascade = new CascadeEngine(new[] { OriginatedStylesheet.Author(sheet) });
            var runner = new CssAnimationRunner(cascade, new[] { sheet }, clock);
            cascade.AttachAnimationRunner(runner);

            var btn = doc.GetElementById("b");
            var state = new HoverState();
            // Compute idle.
            cascade.Compute(btn, state);
            // Switch to hover -> triggers transition.
            state.Set(btn, ElementState.Hover);
            cascade.Compute(btn, state);

            clock.Set(0.05);
            runner.Tick(0.05);
            var composed = cascade.GetComposedStyle(btn, state);
            string color = composed.Get("background-color");
            Assert.That(color, Does.StartWith("rgb"));
            // Not yet at white nor at black.
            Assert.That(color, Is.Not.EqualTo("rgb(0, 0, 0)"));
            Assert.That(color, Is.Not.EqualTo("rgb(255, 255, 255)"));
        }

        // C4: recycling previousStyle in place was disabled for EVERY element
        // whenever a runner was attached (i.e. always, in production), making
        // each cache miss allocate a fresh ~1.7 KB ComputedStyle. It is now
        // suppressed only for elements that need a transition diff. A
        // transition-free element must recycle its style in place even with a
        // runner attached — the dominant per-interaction allocation win.
        [Test]
        public void Transition_free_element_recycles_computed_style_under_runner() {
            var doc = Html("<div id=\"x\" class=\"a\">hi</div>");
            // Two transition-free classes — switching between them forces a
            // genuine cache MISS (SetAttribute bumps the element version) while
            // the stale cache entry is retained, which is exactly the path that
            // recycles previousStyle in place.
            var sheet = Css(".a { color: rgb(0, 0, 0); } .b { color: rgb(10, 20, 30); }");
            var clock = new FakeUIClock();
            var cascade = new CascadeEngine(new[] { OriginatedStylesheet.Author(sheet) });
            var runner = new CssAnimationRunner(cascade, new[] { sheet }, clock);
            cascade.AttachAnimationRunner(runner);

            var x = doc.GetElementById("x");
            var s1 = cascade.Compute(x);
            x.SetAttribute("class", "b");
            var s2 = cascade.Compute(x);

            Assert.That(s2.Get("color"), Is.EqualTo("rgb(10, 20, 30)"),
                "the re-cascade must actually recompute (cache miss)");
            Assert.That(ReferenceEquals(s1, s2), Is.True,
                "a transition-free element must recycle its ComputedStyle in place even with a runner attached (C4)");
            Assert.That(runner.RunningTransitionCount, Is.EqualTo(0));
        }

        // The flip side: an element WITH a transition must NOT recycle, so
        // OnStyleChange still sees a distinct `previous` to diff against and
        // the transition starts. (Hover_transition_produces_interpolated_value
        // covers the interpolation; this pins the no-recycle + start contract.)
        [Test]
        public void Transitioning_element_keeps_distinct_previous_and_starts_transition() {
            var doc = Html("<button id=\"b\">go</button>");
            var sheet = Css(
                "button { background-color: rgb(0, 0, 0); transition: background-color 100ms linear; } " +
                "button:hover { background-color: rgb(255, 255, 255); }");
            var clock = new FakeUIClock();
            var cascade = new CascadeEngine(new[] { OriginatedStylesheet.Author(sheet) });
            var runner = new CssAnimationRunner(cascade, new[] { sheet }, clock);
            cascade.AttachAnimationRunner(runner);

            var btn = doc.GetElementById("b");
            var state = new HoverState();
            var s1 = cascade.Compute(btn, state);   // idle; transition declared
            state.Set(btn, ElementState.Hover);
            var s2 = cascade.Compute(btn, state);   // value changes -> transition starts

            Assert.That(ReferenceEquals(s1, s2), Is.False,
                "an element with a transition must NOT recycle previousStyle, so OnStyleChange can diff (C4)");
            Assert.That(runner.RunningTransitionCount, Is.GreaterThan(0),
                "the transition must still start");
        }

        [Test]
        public void Animation_runs_while_attached() {
            var doc = Html("<div id=\"x\"></div>");
            var sheet = Css(
                "@keyframes spin { from { opacity: 0; } to { opacity: 1; } } " +
                "#x { animation-name: spin; animation-duration: 1s; animation-timing-function: linear; }");
            var clock = new FakeUIClock();
            var cascade = new CascadeEngine(new[] { OriginatedStylesheet.Author(sheet) });
            var runner = new CssAnimationRunner(cascade, new[] { sheet }, clock);
            cascade.AttachAnimationRunner(runner);

            var x = doc.GetElementById("x");
            cascade.Compute(x);
            Assert.That(runner.RunningAnimationCount, Is.EqualTo(1));
            clock.Set(0.5);
            runner.Tick(0.5);
            var composed = cascade.GetComposedStyle(x);
            Assert.That(double.Parse(composed.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture),
                Is.EqualTo(0.5).Within(Eps));
        }

        [Test]
        public void Stop_on_element_removes_animation() {
            var doc = Html("<div id=\"x\"></div>");
            var sheet = Css(
                "@keyframes spin { from { opacity: 0; } to { opacity: 1; } } " +
                "#x { animation-name: spin; animation-duration: 1s; }");
            var clock = new FakeUIClock();
            var cascade = new CascadeEngine(new[] { OriginatedStylesheet.Author(sheet) });
            var runner = new CssAnimationRunner(cascade, new[] { sheet }, clock);
            cascade.AttachAnimationRunner(runner);

            var x = doc.GetElementById("x");
            cascade.Compute(x);
            Assert.That(runner.RunningAnimationCount, Is.EqualTo(1));
            runner.Stop(x);
            Assert.That(runner.RunningAnimationCount, Is.EqualTo(0));
        }

        [Test]
        public void ReCompute_after_class_change_triggers_transition() {
            var doc = Html("<div id=\"x\"></div>");
            var sheet = Css(
                "div { background-color: rgb(0, 0, 0); transition: background-color 100ms linear; } " +
                ".active { background-color: rgb(255, 255, 255); }");
            var clock = new FakeUIClock();
            var cascade = new CascadeEngine(new[] { OriginatedStylesheet.Author(sheet) });
            var runner = new CssAnimationRunner(cascade, new[] { sheet }, clock);
            cascade.AttachAnimationRunner(runner);

            var x = doc.GetElementById("x");
            cascade.Compute(x);
            x.SetAttribute("class", "active");
            cascade.Compute(x);
            Assert.That(runner.RunningTransitionCount, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void Compose_returns_new_style_when_active_and_same_when_not() {
            var doc = Html("<div id=\"x\"></div>");
            var sheet = Css(
                "@keyframes a { from { opacity: 0; } to { opacity: 1; } } " +
                "#x { animation-name: a; animation-duration: 1s; }");
            var clock = new FakeUIClock();
            var cascade = new CascadeEngine(new[] { OriginatedStylesheet.Author(sheet) });
            var runner = new CssAnimationRunner(cascade, new[] { sheet }, clock);
            cascade.AttachAnimationRunner(runner);

            var x = doc.GetElementById("x");
            var baseStyle = cascade.Compute(x);
            clock.Set(0.5);
            runner.Tick(0.5);
            var composedActive = runner.Compose(x, baseStyle);
            Assert.That(composedActive, Is.Not.SameAs(baseStyle));
            // After stop, no animations -> Compose returns the same style.
            runner.StopAll();
            var composedIdle = runner.Compose(x, baseStyle);
            Assert.That(composedIdle, Is.SameAs(baseStyle));
        }

        [Test]
        public void Tick_advances_in_lockstep_with_provided_clock() {
            var doc = Html("<div id=\"x\"></div>");
            var sheet = Css(
                "@keyframes a { from { opacity: 0; } to { opacity: 1; } } " +
                "#x { animation-name: a; animation-duration: 1s; animation-timing-function: linear; }");
            var clock = new FakeUIClock();
            var cascade = new CascadeEngine(new[] { OriginatedStylesheet.Author(sheet) });
            var runner = new CssAnimationRunner(cascade, new[] { sheet }, clock);
            cascade.AttachAnimationRunner(runner);

            var x = doc.GetElementById("x");
            cascade.Compute(x);
            clock.Set(0.25);
            runner.Tick(0.25);
            var c1 = cascade.GetComposedStyle(x);
            double v1 = double.Parse(c1.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture);
            clock.Set(0.75);
            runner.Tick(0.75);
            var c2 = cascade.GetComposedStyle(x);
            double v2 = double.Parse(c2.Get("opacity"), System.Globalization.CultureInfo.InvariantCulture);
            Assert.That(v2, Is.GreaterThan(v1));
        }

        [Test]
        public void Animation_composition_is_registered_and_parses() {
            // CSS Animations L2 §10: animation-composition must parse and
            // resolve. H2b lights up `add` / `accumulate` semantics in the
            // runner — the longhand carries the authored value through the
            // cascade either way.
            Assert.That(CssProperties.All.ContainsKey("animation-composition"), Is.True);
            Assert.That(CssProperties.InitialValueOf("animation-composition"), Is.EqualTo("replace"));
            Assert.That(CssProperties.IsInherited("animation-composition"), Is.False);

            var doc = Html("<div id=\"x\"></div>");
            var sheet = Css(
                "@keyframes spin { from { opacity: 0; } to { opacity: 1; } } " +
                "#x { animation-name: spin; animation-duration: 1s; " +
                "animation-timing-function: linear; animation-composition: add; }");
            var clock = new FakeUIClock();
            var cascade = new CascadeEngine(new[] { OriginatedStylesheet.Author(sheet) });
            var runner = new CssAnimationRunner(cascade, new[] { sheet }, clock);
            cascade.AttachAnimationRunner(runner);

            var x = doc.GetElementById("x");
            var baseStyle = cascade.Compute(x);
            Assert.That(baseStyle.Get("animation-composition"), Is.EqualTo("add"));
            Assert.That(runner.RunningAnimationCount, Is.EqualTo(1));

            clock.Set(0.5);
            Assert.DoesNotThrow(() => runner.Tick(0.5));
        }

        [Test]
        public void Animation_composition_accumulate_is_accepted_and_runs() {
            var doc = Html("<div id=\"x\"></div>");
            var sheet = Css(
                "@keyframes spin { from { opacity: 0; } to { opacity: 1; } } " +
                "#x { animation-name: spin; animation-duration: 1s; " +
                "animation-composition: accumulate; }");
            var clock = new FakeUIClock();
            var cascade = new CascadeEngine(new[] { OriginatedStylesheet.Author(sheet) });
            var runner = new CssAnimationRunner(cascade, new[] { sheet }, clock);
            cascade.AttachAnimationRunner(runner);

            var x = doc.GetElementById("x");
            var baseStyle = cascade.Compute(x);
            Assert.That(baseStyle.Get("animation-composition"), Is.EqualTo("accumulate"));
            Assert.DoesNotThrow(() => { clock.Set(0.25); runner.Tick(0.25); });
        }

        [Test]
        public void Animated_elements_emit_invalidation_via_tracker() {
            // opacity is a WRAPPER property (painter re-resolves PushOpacity
            // fresh every frame) — a wrapper-only animation tick marks
            // Composite, NOT Paint, so the element's cached decoration
            // commands survive (particles.html: 420 wrapper-only animations
            // were rebuilding their radial-gradient decorations every frame
            // through the Paint mark).
            var doc = Html("<div id=\"x\"></div>");
            var sheet = Css(
                "@keyframes a { from { opacity: 0; } to { opacity: 1; } } " +
                "#x { animation-name: a; animation-duration: 1s; }");
            var clock = new FakeUIClock();
            var cascade = new CascadeEngine(new[] { OriginatedStylesheet.Author(sheet) });
            var runner = new CssAnimationRunner(cascade, new[] { sheet }, clock);
            cascade.AttachAnimationRunner(runner);
            var tracker = new InvalidationTracker();
            runner.InvalidationTracker = tracker;

            var x = doc.GetElementById("x");
            cascade.Compute(x);
            tracker.Clear();
            clock.Set(0.5);
            runner.Tick(0.5);
            Assert.That(tracker.IsDirty(x, InvalidationKind.Composite), Is.True,
                "wrapper-only animation must still flag the element dirty (repaint + snapshot eviction)");
            Assert.That(tracker.IsDirty(x, InvalidationKind.Paint), Is.False,
                "wrapper-only animation must NOT stale the decoration cache via Paint");
        }

        [Test]
        public void Transform_and_opacity_animation_marks_Composite_only() {
            // The exact particles.html shape: transform + opacity keyframes.
            // Both run through AnimationInstance's TYPED overlay (sampleResult
            // stays empty; keys live in TypedSample) — classification must
            // read the typed dict or the tick would classify as "no props".
            var doc = Html("<div id=\"x\"></div>");
            var sheet = Css(
                "@keyframes rise { from { transform: translate(0, 0) scale(0.5); opacity: 0; } " +
                "                  to { transform: translate(40px, -500px) scale(1); opacity: 1; } } " +
                "#x { animation-name: rise; animation-duration: 8s; }");
            var clock = new FakeUIClock();
            var cascade = new CascadeEngine(new[] { OriginatedStylesheet.Author(sheet) });
            var runner = new CssAnimationRunner(cascade, new[] { sheet }, clock);
            cascade.AttachAnimationRunner(runner);
            var tracker = new InvalidationTracker();
            runner.InvalidationTracker = tracker;

            var x = doc.GetElementById("x");
            cascade.Compute(x);
            tracker.Clear();
            clock.Set(2.0);
            runner.Tick(2.0);
            Assert.That(tracker.IsDirty(x, InvalidationKind.Composite), Is.True);
            Assert.That(tracker.IsDirty(x, InvalidationKind.Paint), Is.False,
                "transform+opacity tick must not invalidate cached decorations");
            Assert.That(tracker.IsDirty(x, InvalidationKind.Layout), Is.False,
                "transform+opacity tick must not force layout");
        }

        [Test]
        public void Non_wrapper_animation_still_marks_Paint() {
            // background-color changes the DECORATION output — the cache must
            // be staled, so the classification stays Paint.
            var doc = Html("<div id=\"x\"></div>");
            var sheet = Css(
                "@keyframes glow { from { background-color: #ff0000; } to { background-color: #0000ff; } } " +
                "#x { animation-name: glow; animation-duration: 1s; }");
            var clock = new FakeUIClock();
            var cascade = new CascadeEngine(new[] { OriginatedStylesheet.Author(sheet) });
            var runner = new CssAnimationRunner(cascade, new[] { sheet }, clock);
            cascade.AttachAnimationRunner(runner);
            var tracker = new InvalidationTracker();
            runner.InvalidationTracker = tracker;

            var x = doc.GetElementById("x");
            cascade.Compute(x);
            tracker.Clear();
            clock.Set(0.5);
            runner.Tick(0.5);
            Assert.That(tracker.IsDirty(x, InvalidationKind.Paint), Is.True,
                "decoration-affecting animation keeps the Paint mark");
        }

        [Test]
        public void Mixed_wrapper_and_decoration_animation_marks_Paint() {
            // transform (wrapper) + background-color (decoration) in ONE
            // keyframes block: the decoration property forces Paint.
            var doc = Html("<div id=\"x\"></div>");
            var sheet = Css(
                "@keyframes mix { from { transform: scale(0.5); background-color: #ff0000; } " +
                "                 to { transform: scale(1); background-color: #0000ff; } } " +
                "#x { animation-name: mix; animation-duration: 1s; }");
            var clock = new FakeUIClock();
            var cascade = new CascadeEngine(new[] { OriginatedStylesheet.Author(sheet) });
            var runner = new CssAnimationRunner(cascade, new[] { sheet }, clock);
            cascade.AttachAnimationRunner(runner);
            var tracker = new InvalidationTracker();
            runner.InvalidationTracker = tracker;

            var x = doc.GetElementById("x");
            cascade.Compute(x);
            tracker.Clear();
            clock.Set(0.5);
            runner.Tick(0.5);
            Assert.That(tracker.IsDirty(x, InvalidationKind.Paint), Is.True,
                "a mixed sample (wrapper + decoration props) must stale the decoration cache");
        }
    }
}
