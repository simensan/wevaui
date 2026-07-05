using NUnit.Framework;
using Weva.Documents;

namespace Weva.Tests.Documents {
    // Input/selection audit #3 — the caret blink clock anchors to the last
    // caret activity (keystroke / caret move), so the caret is SOLID for the
    // first ~530ms after every edit, like Chrome. Pre-fix the phase was a
    // free-running wall-clock modulo: typing fast during the off half left
    // the caret invisible while editing.
    public class CaretBlinkActivityTests {
        [Test]
        public void Solid_for_the_on_half_after_activity_regardless_of_wall_phase() {
            // Pick a wall time deep in the free-running OFF phase (0.6 mod 1.06).
            double activity = 100.0;
            Assert.That(UIDocumentLifecycle.ComputeCaretBlinkOn(activity, activity), Is.True);
            Assert.That(UIDocumentLifecycle.ComputeCaretBlinkOn(activity + 0.3, activity), Is.True);
            Assert.That(UIDocumentLifecycle.ComputeCaretBlinkOn(activity + 0.52, activity), Is.True);
        }

        [Test]
        public void Off_phase_follows_the_on_half() {
            double activity = 100.0;
            Assert.That(UIDocumentLifecycle.ComputeCaretBlinkOn(activity + 0.54, activity), Is.False);
            Assert.That(UIDocumentLifecycle.ComputeCaretBlinkOn(activity + 1.05, activity), Is.False);
            // Next period's on half.
            Assert.That(UIDocumentLifecycle.ComputeCaretBlinkOn(activity + 1.07, activity), Is.True);
        }

        [Test]
        public void New_activity_reanchors_the_phase() {
            double first = 100.0;
            // 0.6s after the first keystroke the caret is off…
            Assert.That(UIDocumentLifecycle.ComputeCaretBlinkOn(first + 0.6, first), Is.False);
            // …but a new keystroke at that moment makes it solid again.
            double second = first + 0.6;
            Assert.That(UIDocumentLifecycle.ComputeCaretBlinkOn(second, second), Is.True);
            Assert.That(UIDocumentLifecycle.ComputeCaretBlinkOn(second + 0.5, second), Is.True);
        }

        [Test]
        public void No_activity_falls_back_to_the_free_running_phase() {
            Assert.That(UIDocumentLifecycle.ComputeCaretBlinkOn(0.1, double.NaN), Is.True);
            Assert.That(UIDocumentLifecycle.ComputeCaretBlinkOn(0.6, double.NaN), Is.False);
        }
    }
}
