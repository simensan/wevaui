using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Media;
using Weva.Documents;
using Weva.Layout.Text;
using Weva.Reactive;

namespace Weva.Tests.Reactive {
    // Audit CX3: InvalidationTracker.HasSensitive (the :has() ancestor-walk
    // switch) was NEVER set anywhere in production — the tracker-side
    // machinery and CascadeEngine.HasAnyHasSelector both existed with zero
    // callers connecting them. Structural `:has()` therefore never
    // re-matched on DOM/attribute mutation: `.card:has(.open)` stayed stale
    // forever after adding the class to a descendant (run-confirmed).
    // The pin runs the REAL wiring: UIDocumentBuilder.Build().
    public class HasSensitiveWiringTests {
        static UIDocumentState Build(string html, string css) {
            return new UIDocumentBuilder {
                DocumentSource = html,
                StylesheetSources = new List<string> { css },
                MediaContext = MediaContext.Default(400, 300),
                FontMetricsOverride = new MonoFontMetrics(),
            }.Build();
        }

        [Test]
        public void Builder_wires_HasSensitive_when_sheet_contains_has() {
            var state = Build(
                "<div class=\"card\" id=\"k\"><div><div id=\"g\">x</div></div></div>",
                ".card:has(.open) { color: red; }");
            Assert.That(state.Invalidation.HasSensitive, Is.True,
                "a sheet with :has() must enable the tracker's ancestor-walk invalidation (audit CX3)");
        }

        [Test]
        public void Builder_leaves_HasSensitive_off_without_has() {
            // The perf guard: the O(depth)-per-mutation ancestor walk must
            // stay off for the overwhelmingly common :has()-free sheet.
            var state = Build(
                "<div class=\"card\">x</div>",
                ".card { color: red; }");
            Assert.That(state.Invalidation.HasSensitive, Is.False);
        }

        [Test]
        public void Descendant_class_flip_restyles_a_has_ancestor() {
            // The audit's run-confirmed repro, end-to-end through the real
            // builder wiring: add class `open` to a grandchild → `.card` must
            // pick up the :has() match on the next cascade pass.
            var state = Build(
                "<div class=\"card\" id=\"k\"><div><div id=\"g\">x</div></div></div>",
                ".card:has(.open) { color: red; }");
            var card = state.Doc.GetElementById("k");
            var grandchild = state.Doc.GetElementById("g");

            state.Cascade.ComputeAll(state.Doc, state.State);
            Assert.That(state.Cascade.Compute(card, state.State).Get("color"), Is.EqualTo("black"),
                "sanity: no .open descendant yet");
            state.Invalidation.Clear();

            grandchild.SetAttribute("class", "open");
            Assert.That(state.Invalidation.DirtyCount, Is.GreaterThan(0),
                "the mutation must produce marks");

            // The lifecycle's cascade pass: apply the dirty set, recompute.
            state.Cascade.Apply(state.Invalidation);
            state.Cascade.ComputeAll(state.Doc, state.State);
            Assert.That(state.Cascade.Compute(card, state.State).Get("color"), Is.EqualTo("red"),
                ".card:has(.open) must re-match after the descendant class flip — " +
                "without the HasSensitive wiring the card's cache entry is never dropped (audit CX3)");
        }
    }
}
