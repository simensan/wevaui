using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using Weva.Parsing;
using Weva.Reactive;
using Weva.ViewTransitions;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.ViewTransitions {
    public class ViewTransitionTests {
        sealed class LiveDoc {
            public Document doc;
            public LayoutEngine layout;
            public LayoutContext ctx;
            public List<OriginatedStylesheet> sheets;
            public Box rootBox;

            public void Relayout() {
                var engine = new CascadeEngine(sheets);
                var styles = new Dictionary<Element, ComputedStyle>();
                foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;
                layout.InvalidateAll();
                rootBox = layout.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx);
            }
        }

        static LiveDoc BuildLive(string html, string css) {
            var live = new LiveDoc();
            live.doc = HtmlParser.Parse(html);
            live.sheets = new List<OriginatedStylesheet> {
                Author(BuiltinUserAgent),
                Author(css)
            };
            live.layout = new LayoutEngine(new MonoFontMetrics());
            live.ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 800,
                ViewportHeightPx = 600,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96
            };
            live.Relayout();
            return live;
        }

        [Test]
        public void Snapshot_captures_named_elements() {
            ViewTransitionProperties.EnsureRegistered();
            var live = BuildLive(
                "<div><div class=\"hero\"></div></div>",
                ".hero { width: 100px; height: 50px; view-transition-name: hero; }"
            );
            var snap = SnapshotCapture.Capture(live.rootBox);
            Assert.That(snap.Count, Is.EqualTo(1));
            Assert.That(snap.TryGet("hero", out var hero), Is.True);
            Assert.That(hero.Bounds.Width, Is.EqualTo(100).Within(0.5));
            Assert.That(hero.Bounds.Height, Is.EqualTo(50).Within(0.5));
        }

        [Test]
        public void StartViewTransition_captures_before_snapshot() {
            ViewTransitionProperties.EnsureRegistered();
            var live = BuildLive(
                "<div><div class=\"hero\" id=\"a\"></div></div>",
                ".hero { width: 100px; height: 50px; view-transition-name: hero; }"
            );
            var engine = new ViewTransitionEngine(live.doc, () => live.rootBox, live.Relayout);
            var vt = engine.Start(() => { /* no-op */ });
            Assert.That(vt.Before, Is.Not.Null);
            Assert.That(vt.Before.Count, Is.EqualTo(1));
            Assert.That(vt.Before.TryGet("hero", out _), Is.True);
        }

        [Test]
        public void Matched_name_animates_bounds_when_position_differs() {
            ViewTransitionProperties.EnsureRegistered();
            var live = BuildLive(
                "<div><div class=\"hero\" id=\"a\"></div></div>",
                ".hero { width: 100px; height: 50px; view-transition-name: hero; } .grown { width: 200px; height: 100px; view-transition-name: hero; }"
            );
            var engine = new ViewTransitionEngine(live.doc, () => live.rootBox, live.Relayout);
            var vt = engine.Start(() => {
                var heroes = new List<Element>(live.doc.GetElementsByClassName("hero"));
                heroes[0].SetAttribute("class", "grown");
            });
            Assert.That(vt.Pairs.Count, Is.EqualTo(1));
            Assert.That(vt.Pairs[0].Kind, Is.EqualTo(ViewTransitionPairKind.Matched));
            // After the class swap the hero has different bounds.
            var oldW = vt.Pairs[0].Old.Bounds.Width;
            var newW = vt.Pairs[0].New.Bounds.Width;
            Assert.That(newW, Is.GreaterThan(oldW));
        }

        [Test]
        public void Unmatched_old_fades_out() {
            ViewTransitionProperties.EnsureRegistered();
            var live = BuildLive(
                "<div><div class=\"hero\"></div></div>",
                ".hero { width: 100px; height: 50px; view-transition-name: hero; }"
            );
            var engine = new ViewTransitionEngine(live.doc, () => live.rootBox, live.Relayout);
            var vt = engine.Start(() => {
                // Remove the hero element.
                var heroes = new List<Element>(live.doc.GetElementsByClassName("hero"));
                if (heroes.Count > 0) {
                    var p = heroes[0].Parent;
                    p?.RemoveChild(heroes[0]);
                }
            });
            Assert.That(vt.Pairs.Count, Is.EqualTo(1));
            Assert.That(vt.Pairs[0].Kind, Is.EqualTo(ViewTransitionPairKind.OldOnly));

            // Sample mid-progress — old alpha should be < 1, new alpha == 0.
            vt.Elapsed = vt.Duration * 0.5;
            var frames = vt.Sample();
            Assert.That(frames[0].OldAlpha, Is.LessThan(1).And.GreaterThan(0));
            Assert.That(frames[0].NewAlpha, Is.EqualTo(0));
        }

        [Test]
        public void Unmatched_new_fades_in() {
            ViewTransitionProperties.EnsureRegistered();
            var live = BuildLive(
                "<div><div class=\"existing\"></div></div>",
                ".existing { width: 50px; height: 20px; } .hero { width: 100px; height: 50px; view-transition-name: hero; }"
            );
            var engine = new ViewTransitionEngine(live.doc, () => live.rootBox, live.Relayout);
            var vt = engine.Start(() => {
                // Add a new element with view-transition-name.
                var existings = new List<Element>(live.doc.GetElementsByClassName("existing"));
                var parent = existings[0].Parent;
                var hero = new Element("div");
                hero.SetAttribute("class", "hero");
                parent.AppendChild(hero);
            });
            // The old snapshot has 0 entries; the new has 1.
            Assert.That(vt.Pairs.Count, Is.EqualTo(1));
            Assert.That(vt.Pairs[0].Kind, Is.EqualTo(ViewTransitionPairKind.NewOnly));

            vt.Elapsed = vt.Duration * 0.5;
            var frames = vt.Sample();
            Assert.That(frames[0].OldAlpha, Is.EqualTo(0));
            Assert.That(frames[0].NewAlpha, Is.GreaterThan(0).And.LessThan(1));
        }

        [Test]
        public void Concurrent_transition_skips_first() {
            ViewTransitionProperties.EnsureRegistered();
            var live = BuildLive(
                "<div><div class=\"hero\"></div></div>",
                ".hero { width: 100px; height: 50px; view-transition-name: hero; }"
            );
            var engine = new ViewTransitionEngine(live.doc, () => live.rootBox, live.Relayout);
            var first = engine.Start(() => { });
            // No-op mutate keeps the matched name on both sides → animation in progress.
            Assert.That(first.Phase, Is.EqualTo(ViewTransitionPhase.Animating));
            var second = engine.Start(() => { });
            Assert.That(first.Phase, Is.EqualTo(ViewTransitionPhase.Skipped));
            Assert.That(engine.Active, Is.SameAs(second));
        }

        [Test]
        public void Reduced_motion_skips_animation() {
            ViewTransitionProperties.EnsureRegistered();
            var live = BuildLive(
                "<div><div class=\"hero\"></div></div>",
                ".hero { width: 100px; height: 50px; view-transition-name: hero; }"
            );
            var engine = new ViewTransitionEngine(live.doc, () => live.rootBox, live.Relayout) {
                ReducedMotion = true
            };
            var vt = engine.Start(() => { });
            Assert.That(vt.Phase, Is.EqualTo(ViewTransitionPhase.Finished));
            Assert.That(vt.Done, Is.True);
        }

        [Test]
        public void Tick_progresses_transition() {
            ViewTransitionProperties.EnsureRegistered();
            var live = BuildLive(
                "<div><div class=\"hero\"></div></div>",
                ".hero { width: 100px; height: 50px; view-transition-name: hero; }"
            );
            var engine = new ViewTransitionEngine(live.doc, () => live.rootBox, live.Relayout);
            var vt = engine.Start(() => { });
            // The transition has Pairs.Count==1 after the no-op mutation (same hero name in both).
            Assert.That(vt.Phase, Is.EqualTo(ViewTransitionPhase.Animating));
            engine.Tick(vt.Duration / 2, null);
            Assert.That(vt.Progress, Is.EqualTo(0.5).Within(0.05));
            engine.Tick(vt.Duration, null);
            Assert.That(vt.Done, Is.True);
        }

        [Test]
        public void Tick_marks_paint_invalidation_on_document_subtree() {
            ViewTransitionProperties.EnsureRegistered();
            var live = BuildLive(
                "<div><div class=\"hero\"></div></div>",
                ".hero { width: 100px; height: 50px; view-transition-name: hero; }"
            );
            var engine = new ViewTransitionEngine(live.doc, () => live.rootBox, live.Relayout);
            var tracker = new InvalidationTracker();
            engine.Start(() => { });
            engine.Tick(0.05, tracker);
            Assert.That(tracker.HasAny(InvalidationKind.Paint), Is.True);
        }

        [Test]
        public void Document_extension_routes_to_engine() {
            ViewTransitionProperties.EnsureRegistered();
            var live = BuildLive(
                "<div><div class=\"hero\"></div></div>",
                ".hero { width: 100px; height: 50px; view-transition-name: hero; }"
            );
            var engine = new ViewTransitionEngine(live.doc, () => live.rootBox, live.Relayout);
            DocumentViewTransitionExtensions.AttachViewTransitionEngine(live.doc, engine);
            var vt = live.doc.StartViewTransition(() => { });
            Assert.That(vt, Is.Not.Null);
            Assert.That(engine.Active, Is.SameAs(vt));
        }

        [Test]
        public void Property_view_transition_name_registered() {
            ViewTransitionProperties.EnsureRegistered();
            Assert.That(CssProperties.TryGet("view-transition-name", out var p), Is.True);
            Assert.That(p.InitialValue, Is.EqualTo("none"));
        }

        [Test]
        public void Mutation_without_engine_runs_inline() {
            // doc.StartViewTransition with no engine attached should still invoke mutate.
            var doc = HtmlParser.Parse("<div></div>");
            int called = 0;
            doc.StartViewTransition(() => { called++; });
            Assert.That(called, Is.EqualTo(1));
        }

        [Test]
        public void Sampled_matched_bounds_interpolate_over_progress() {
            ViewTransitionProperties.EnsureRegistered();
            var live = BuildLive(
                "<div><div class=\"hero\"></div></div>",
                ".hero { width: 100px; height: 50px; view-transition-name: hero; } .grown { width: 200px; height: 100px; view-transition-name: hero; }"
            );
            var engine = new ViewTransitionEngine(live.doc, () => live.rootBox, live.Relayout);
            var vt = engine.Start(() => {
                var heroes = new List<Element>(live.doc.GetElementsByClassName("hero"));
                heroes[0].SetAttribute("class", "grown");
            });
            // At t=0 the bounds should equal Old; at t=1, equal New.
            vt.Elapsed = 0;
            var f0 = vt.Sample();
            vt.Elapsed = vt.Duration;
            var f1 = vt.Sample();
            Assert.That(f0[0].Bounds.Width, Is.EqualTo(vt.Pairs[0].Old.Bounds.Width).Within(0.5));
            Assert.That(f1[0].Bounds.Width, Is.EqualTo(vt.Pairs[0].New.Bounds.Width).Within(0.5));
        }
    }
}
