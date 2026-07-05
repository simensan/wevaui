using System.Collections.Generic;
using System.Globalization;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Animation;
using Weva.Css.Cascade;
using Weva.Css.Media;
using Weva.Documents;
using Weva.Dom;
using Weva.Events;
using Weva.Parsing;
using Weva.Reactive;

namespace Weva.Tests.Css.Animation {
    public class AnimationLifecycleTests {
        static (CssAnimationRunner runner, FakeUIClock clock, Element el) MakeTransitioning() {
            var doc = HtmlParser.Parse("<div id=\"x\"></div>");
            var sheet = CssParser.Parse(
                "div { color: rgb(0, 0, 0); transition: color 1s linear; } " +
                ".active { color: rgb(255, 255, 255); }");
            var clock = new FakeUIClock();
            var cascade = new CascadeEngine(new[] { OriginatedStylesheet.Author(sheet) });
            var runner = new CssAnimationRunner(cascade, new[] { sheet }, clock);
            cascade.AttachAnimationRunner(runner);
            var el = doc.GetElementById("x");
            cascade.Compute(el);
            el.SetAttribute("class", "active");
            cascade.Compute(el);
            return (runner, clock, el);
        }

        static UIDocumentState NewState(string html, string css, FakeUIClock clock) {
            return new UIDocumentBuilder {
                DocumentSource = html,
                StylesheetSources = new List<string> { css },
                MediaContext = MediaContext.Default(800, 600),
                Clock = clock
            }.Build();
        }

        [Test]
        public void Tick_marks_animating_elements_dirty() {
            // MakeTransitioning sets up a `color` transition. Color is a
            // paint-only property (no layout dependency), and the runner's
            // ClassifyInvalidation correctly returns just Paint — sparing
            // a full layout pass on every animation frame. The earlier
            // version of this test asserted Layout was also marked, which
            // was a conservative over-invalidation that the
            // CssAnimationRunner optimization (commit history around the
            // animation perf pass) deliberately narrowed.
            var (runner, clock, el) = MakeTransitioning();
            var tracker = new InvalidationTracker();
            clock.Set(0.5);
            runner.Tick(0.5, tracker);
            Assert.That(tracker.IsDirty(el, InvalidationKind.Paint), Is.True);
            Assert.That(tracker.IsDirty(el, InvalidationKind.Layout), Is.False,
                "Color transitions must not trigger Layout (paint-only optimization).");
        }

        [Test]
        public void Tick_with_tracker_marks_composite_for_wrapper_only_keyframes() {
            // The keyframe animation here drives `opacity` — a WRAPPER
            // property (PushOpacity, re-resolved fresh every VisitBox). The
            // runner classifies wrapper-only ticks as Composite: the element
            // is dirty (repaint + snapshot eviction) but its cached
            // decoration commands stay valid, and layout is skipped.
            var doc = HtmlParser.Parse("<div id=\"x\"></div>");
            var sheet = CssParser.Parse(
                "@keyframes a { from { opacity: 0; } to { opacity: 1; } } " +
                "#x { animation-name: a; animation-duration: 1s; animation-timing-function: linear; }");
            var clock = new FakeUIClock();
            var cascade = new CascadeEngine(new[] { OriginatedStylesheet.Author(sheet) });
            var runner = new CssAnimationRunner(cascade, new[] { sheet }, clock);
            cascade.AttachAnimationRunner(runner);
            var el = doc.GetElementById("x");
            cascade.Compute(el);
            var tracker = new InvalidationTracker();
            clock.Set(0.5);
            runner.Tick(0.5, tracker);
            Assert.That(tracker.IsDirty(el, InvalidationKind.Composite), Is.True);
            Assert.That(tracker.IsDirty(el, InvalidationKind.Paint), Is.False,
                "Wrapper-only keyframes must not stale the decoration cache.");
            Assert.That(tracker.IsDirty(el, InvalidationKind.Layout), Is.False,
                "Opacity keyframes must not trigger Layout (paint-only optimization).");
        }

        [Test]
        public void Idle_tick_does_not_mark_anything_dirty() {
            var clock = new FakeUIClock();
            var cascade = new CascadeEngine(System.Array.Empty<OriginatedStylesheet>());
            var runner = new CssAnimationRunner(cascade, System.Array.Empty<Stylesheet>(), clock);
            var tracker = new InvalidationTracker();
            runner.Tick(0.0, tracker);
            runner.Tick(0.5, tracker);
            runner.Tick(1.0, tracker);
            Assert.That(tracker.DirtyCount, Is.EqualTo(0));
        }

        [Test]
        public void Tick_overload_uses_passed_tracker_not_field() {
            var (runner, clock, el) = MakeTransitioning();
            var fieldTracker = new InvalidationTracker();
            var passedTracker = new InvalidationTracker();
            runner.InvalidationTracker = fieldTracker;
            clock.Set(0.5);
            runner.Tick(0.5, passedTracker);
            Assert.That(passedTracker.IsDirty(el, InvalidationKind.Paint), Is.True);
            Assert.That(fieldTracker.DirtyCount, Is.EqualTo(0));
        }

        [Test]
        public void Lifecycle_drives_animations_each_frame() {
            var clock = new FakeUIClock();
            var s = NewState(
                "<p id=\"t\">hi</p>",
                "p { color: rgb(0, 0, 0); transition: color 1s linear; } " +
                ".active { color: rgb(255, 255, 255); }",
                clock);
            UIDocumentLifecycle.Update(s, null, 0.0);
            var el = s.Doc.GetElementById("t");
            // Toggle -> kicks off transition on next Update.
            el.SetAttribute("class", "active");
            UIDocumentLifecycle.Update(s, null, 0.0);

            clock.Set(0.25);
            UIDocumentLifecycle.Update(s, null, 0.25);
            var c1 = s.Cascade.GetComposedStyle(el, s.State).Get("color");

            clock.Set(0.75);
            UIDocumentLifecycle.Update(s, null, 0.75);
            var c2 = s.Cascade.GetComposedStyle(el, s.State).Get("color");

            Assert.That(c1, Does.StartWith("rgb"));
            Assert.That(c2, Does.StartWith("rgb"));
            Assert.That(c1, Is.Not.EqualTo("rgb(0, 0, 0)"));
            Assert.That(c2, Is.Not.EqualTo("rgb(255, 255, 255)"));
            Assert.That(c1, Is.Not.EqualTo(c2));
        }

        [Test]
        public void Lifecycle_paint_uses_composed_style() {
            // Color transitions are paint-only — see Tick_marks_animating_elements_dirty
            // above for the rationale. So this test verifies the *composed*
            // style flows through to the cascade at midpoint without
            // requiring a layout pass. The earlier expectation that
            // LayoutRan==true was a stale invariant from before the paint-
            // only animation optimization narrowed dirty-kind classification.
            var clock = new FakeUIClock();
            var s = NewState(
                "<p id=\"t\">hi</p>",
                "p { color: rgb(0, 0, 0); transition: color 1s linear; } " +
                ".active { color: rgb(255, 255, 255); }",
                clock);
            UIDocumentLifecycle.Update(s, null, 0.0);
            var el = s.Doc.GetElementById("t");
            el.SetAttribute("class", "active");
            UIDocumentLifecycle.Update(s, null, 0.0);

            clock.Set(0.5);
            UIDocumentLifecycle.Update(s, null, 0.5);
            // Composed style at midpoint should not equal either endpoint.
            // This is the contract that matters: the cascade reflects the
            // running animation regardless of whether layout actually ran.
            var color = s.Cascade.GetComposedStyle(el, s.State).Get("color");
            Assert.That(color, Is.Not.EqualTo("rgb(0, 0, 0)"));
            Assert.That(color, Is.Not.EqualTo("rgb(255, 255, 255)"));
        }

        [Test]
        public void Animation_completes_and_clears() {
            var (runner, clock, el) = MakeTransitioning();
            var tracker = new InvalidationTracker();

            clock.Set(0.5);
            runner.Tick(0.5, tracker);
            Assert.That(tracker.IsDirty(el, InvalidationKind.Paint), Is.True);
            tracker.Clear();

            // Past the 1s duration: completion frame still marks dirty so
            // paint converges on the final value.
            clock.Set(1.5);
            runner.Tick(1.5, tracker);
            Assert.That(tracker.IsDirty(el, InvalidationKind.Paint), Is.True);
            Assert.That(runner.HasRunningAnimations(el), Is.False);
            tracker.Clear();

            // Subsequent idle tick adds nothing.
            clock.Set(2.0);
            runner.Tick(2.0, tracker);
            Assert.That(tracker.DirtyCount, Is.EqualTo(0));
        }

        [Test]
        public void Lifecycle_idle_after_animation_completes_keeps_caches_steady() {
            var clock = new FakeUIClock();
            var s = NewState(
                "<p id=\"t\">hi</p>",
                "p { color: rgb(0, 0, 0); transition: color 0.1s linear; } " +
                ".active { color: rgb(255, 255, 255); }",
                clock);
            UIDocumentLifecycle.Update(s, null, 0.0);
            var el = s.Doc.GetElementById("t");
            el.SetAttribute("class", "active");
            UIDocumentLifecycle.Update(s, null, 0.0);

            // Drive past completion.
            clock.Set(0.05);
            UIDocumentLifecycle.Update(s, null, 0.05);
            clock.Set(0.2);
            UIDocumentLifecycle.Update(s, null, 0.2);
            Assert.That(s.Animator.RunningTransitionCount, Is.EqualTo(0));

            // Idle frame after completion: tracker should be empty after
            // Update clears it, and no further work should be scheduled.
            s.LayoutEngine.ResetCacheStats();
            clock.Set(0.5);
            var r = UIDocumentLifecycle.Update(s, null, 0.5);
            Assert.That(r.LayoutRan, Is.False);
            Assert.That(s.LayoutEngine.CacheMisses, Is.EqualTo(0));
            Assert.That(s.Invalidation.DirtyCount, Is.EqualTo(0));
        }

        [Test]
        public void Lifecycle_color_interpolates_monotonically() {
            var clock = new FakeUIClock();
            var s = NewState(
                "<p id=\"t\">hi</p>",
                "p { color: rgb(0, 0, 0); transition: color 1s linear; } " +
                ".active { color: rgb(255, 255, 255); }",
                clock);
            UIDocumentLifecycle.Update(s, null, 0.0);
            var el = s.Doc.GetElementById("t");
            el.SetAttribute("class", "active");
            UIDocumentLifecycle.Update(s, null, 0.0);

            double Sample(double t) {
                clock.Set(t);
                UIDocumentLifecycle.Update(s, null, t);
                var color = s.Cascade.GetComposedStyle(el, s.State).Get("color");
                return ParseRgbR(color);
            }

            double v1 = Sample(0.1);
            double v2 = Sample(0.4);
            double v3 = Sample(0.7);
            Assert.That(v2, Is.GreaterThan(v1));
            Assert.That(v3, Is.GreaterThan(v2));
        }

        static double ParseRgbR(string rgb) {
            int open = rgb.IndexOf('(');
            int close = rgb.IndexOf(')');
            if (open < 0 || close < 0) return 0;
            var inner = rgb.Substring(open + 1, close - open - 1);
            var parts = inner.Split(',');
            return double.Parse(parts[0].Trim(), CultureInfo.InvariantCulture);
        }

        static ComputedStyle Style(Element e, params (string, string)[] kv) {
            var s = new ComputedStyle(e);
            foreach (var pair in kv) s.Set(pair.Item1, pair.Item2);
            return s;
        }

        [Test]
        public void Animation_iteration_count_zero_is_removed_on_first_tick() {
            var clock = new FakeUIClock();
            var sheet = CssParser.Parse("@keyframes a { from { opacity: 0; } to { opacity: 1; } }");
            var cascade = new CascadeEngine(System.Array.Empty<OriginatedStylesheet>());
            var runner = new CssAnimationRunner(cascade, new[] { sheet }, clock);
            var e = new Element("div");
            var s = Style(e,
                ("animation-name", "a"),
                ("animation-duration", "1s"),
                ("animation-iteration-count", "0"),
                ("animation-timing-function", "linear"));
            runner.OnStyleChange(e, null, s);
            Assert.That(runner.RunningAnimationCount, Is.EqualTo(1));
            clock.Set(0);
            runner.Tick(0);
            Assert.That(runner.RunningAnimationCount, Is.EqualTo(0));
            Assert.That(runner.HasRunningAnimations(e), Is.False);
        }

        [Test]
        public void Animation_iteration_count_one_runs_once_then_removed() {
            var clock = new FakeUIClock();
            var sheet = CssParser.Parse("@keyframes a { from { opacity: 0; } to { opacity: 1; } }");
            var cascade = new CascadeEngine(System.Array.Empty<OriginatedStylesheet>());
            var runner = new CssAnimationRunner(cascade, new[] { sheet }, clock);
            var e = new Element("div");
            var s = Style(e,
                ("animation-name", "a"),
                ("animation-duration", "1s"),
                ("animation-iteration-count", "1"),
                ("animation-timing-function", "linear"));
            runner.OnStyleChange(e, null, s);
            Assert.That(runner.RunningAnimationCount, Is.EqualTo(1));
            clock.Set(0.5);
            runner.Tick(0.5);
            Assert.That(runner.RunningAnimationCount, Is.EqualTo(1));
            clock.Set(1.5);
            runner.Tick(1.5);
            Assert.That(runner.RunningAnimationCount, Is.EqualTo(0));
            Assert.That(runner.HasRunningAnimations(e), Is.False);
        }
    }
}
