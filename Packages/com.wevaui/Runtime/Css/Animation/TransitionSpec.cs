using Weva.Animation;
using Weva.Css.Cascade;

namespace Weva.Css.Animation {
    public readonly struct TransitionSpec {
        public string Property { get; }
        // Pre-resolved CssProperties id for `Property`, cached at construction
        // so the per-frame Compose hot path can index ComputedStyle directly
        // (Set(int)/Get(int)/IsImportant(int)) instead of paying for a
        // CssProperties.GetId hashmap probe per overlay-property per frame.
        // -1 when `Property` is a custom property (`--*`), the "all"
        // pseudo-property, or otherwise not in the central registry — the
        // caller falls back to the string-keyed path so unregistered names
        // still flow through. See P14/P15 in CODE_AUDIT_FINDINGS.md.
        public int PropertyId { get; }
        public double DurationSeconds { get; }
        public double DelaySeconds { get; }
        public EasingFunction Easing { get; }
        // CSS Transitions L2 §3.1: transition-behavior: allow-discrete enables
        // discrete-property transitions (step at t=50%). Default `normal` means
        // discrete properties snap instantly — no transition record is created.
        public bool AllowDiscrete { get; }

        public TransitionSpec(string property, double durationSeconds, double delaySeconds, EasingFunction easing, bool allowDiscrete = false) {
            Property = property;
            // Resolve once at spec-build time — TransitionSpecs are constructed
            // by the cascade / shorthand parser when the author's transition-*
            // declarations change, not per frame.
            PropertyId = property != null ? CssProperties.GetId(property) : -1;
            DurationSeconds = durationSeconds;
            DelaySeconds = delaySeconds;
            Easing = easing ?? LinearEasing.Instance;
            AllowDiscrete = allowDiscrete;
        }
    }
}
