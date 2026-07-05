using NUnit.Framework;
using Weva.Forms.Bridge;

namespace Weva.Tests.Forms {
    // Input/selection audit #2 — the auto-repeat cadence for polled editing
    // keys (Arrow/Backspace/Delete/Page). Chrome: ~500ms initial delay, then
    // ~30Hz. Character keys already repeat via the OS text-input path; this
    // clock covers the keys the bridge forwards from per-frame edge polling.
    public class KeyAutoRepeatTests {
        [Test]
        public void No_repeat_before_the_initial_delay() {
            var r = new KeyAutoRepeat();
            r.Press("Backspace", 10.0);
            Assert.That(r.Repeats("Backspace", 10.0), Is.EqualTo(0));
            Assert.That(r.Repeats("Backspace", 10.49), Is.EqualTo(0));
        }

        [Test]
        public void First_repeat_fires_at_the_initial_delay_then_30hz() {
            var r = new KeyAutoRepeat();
            r.Press("ArrowLeft", 0.0);
            Assert.That(r.Repeats("ArrowLeft", 0.5), Is.EqualTo(1), "first repeat at +500ms");
            Assert.That(r.Repeats("ArrowLeft", 0.51), Is.EqualTo(0), "next not due yet");
            Assert.That(r.Repeats("ArrowLeft", 0.5 + KeyAutoRepeat.IntervalSeconds), Is.EqualTo(1));
        }

        [Test]
        public void Sustained_hold_averages_the_repeat_rate() {
            var r = new KeyAutoRepeat();
            r.Press("Delete", 0.0);
            int total = 0;
            // Simulate a 60fps hold for 1.5s after the press.
            for (double t = 1.0 / 60; t <= 1.5; t += 1.0 / 60) total += r.Repeats("Delete", t);
            // 1.0s of repeat time at 30Hz ≈ 30 repeats (±2 for frame quantization).
            Assert.That(total, Is.InRange(28, 32));
        }

        [Test]
        public void Release_stops_repeats_and_a_new_press_restarts_the_delay() {
            var r = new KeyAutoRepeat();
            r.Press("ArrowRight", 0.0);
            Assert.That(r.Repeats("ArrowRight", 0.6), Is.GreaterThan(0));
            r.Release("ArrowRight");
            Assert.That(r.Repeats("ArrowRight", 1.0), Is.EqualTo(0), "released key never repeats");
            r.Press("ArrowRight", 2.0);
            Assert.That(r.Repeats("ArrowRight", 2.3), Is.EqualTo(0), "fresh press waits the full delay again");
            Assert.That(r.Repeats("ArrowRight", 2.52), Is.EqualTo(1));
        }

        [Test]
        public void Frame_stall_bursts_are_capped() {
            var r = new KeyAutoRepeat();
            r.Press("Backspace", 0.0);
            // A 3-second stall would owe ~75 repeats; the cap keeps the field
            // from being machine-gunned when the frame resumes.
            Assert.That(r.Repeats("Backspace", 3.0), Is.LessThanOrEqualTo(4));
            // And the clock re-anchors: the next frame owes at most a burst,
            // not the rest of the backlog.
            Assert.That(r.Repeats("Backspace", 3.016), Is.LessThanOrEqualTo(4));
        }

        [Test]
        public void Keys_repeat_independently() {
            var r = new KeyAutoRepeat();
            r.Press("ArrowLeft", 0.0);
            r.Press("ArrowRight", 0.4);
            Assert.That(r.Repeats("ArrowLeft", 0.5), Is.EqualTo(1));
            Assert.That(r.Repeats("ArrowRight", 0.5), Is.EqualTo(0), "second key still in its delay");
            Assert.That(r.Repeats("ArrowRight", 0.9), Is.GreaterThan(0));
        }
    }
}
