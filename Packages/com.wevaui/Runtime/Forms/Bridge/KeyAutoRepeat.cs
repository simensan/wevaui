using System.Collections.Generic;

namespace Weva.Forms.Bridge {
    // Pure auto-repeat clock for navigation/editing keys (input/selection
    // audit #2). Character keys repeat via the OS text-input path
    // (onTextInput), but Arrow/Backspace/Delete/Home/End/Page keys are
    // forwarded from per-frame edge polling, which fires exactly once per
    // physical press — holding Backspace deleted ONE character while holding
    // a letter repeated, which made editing feel broken. Chrome's cadence:
    // ~500ms initial delay, then ~30Hz.
    //
    // Usage per tick, per key:
    //   pressed edge  -> Press(code, now)   (caller dispatches the initial KeyDown)
    //   held          -> Repeats(code, now) (caller dispatches one repeat KeyDown per count)
    //   released      -> Release(code)
    // Pure C# and deterministic so the cadence is unit-testable headlessly.
    public sealed class KeyAutoRepeat {
        public const double InitialDelaySeconds = 0.5;
        public const double IntervalSeconds = 1.0 / 30.0;

        struct HeldKey {
            public double PressedAt;
            public double NextRepeatAt;
        }
        readonly Dictionary<string, HeldKey> held = new();

        public void Press(string code, double now) {
            held[code] = new HeldKey {
                PressedAt = now,
                NextRepeatAt = now + InitialDelaySeconds,
            };
        }

        public void Release(string code) {
            held.Remove(code);
        }

        // Number of repeats due for a still-held key at `now`. Catches up at
        // most a few intervals after a hitch, but caps the burst so a long
        // frame stall (domain reload, breakpoint) doesn't machine-gun the
        // field with hundreds of buffered repeats — Chrome coalesces the
        // same way.
        public int Repeats(string code, double now) {
            if (!held.TryGetValue(code, out var h)) return 0;
            if (now < h.NextRepeatAt) return 0;
            int count = 1 + (int)((now - h.NextRepeatAt) / IntervalSeconds);
            const int burstCap = 4;
            if (count > burstCap) count = burstCap;
            h.NextRepeatAt += count * IntervalSeconds;
            if (h.NextRepeatAt < now) h.NextRepeatAt = now + IntervalSeconds;
            held[code] = h;
            return count;
        }

        public void Clear() => held.Clear();
    }
}
