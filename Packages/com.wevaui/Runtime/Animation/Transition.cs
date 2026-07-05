using System;

namespace Weva.Animation {
    public sealed class Transition {
        public string Property { get; }
        public double DurationSeconds { get; }
        public double DelaySeconds { get; }
        public EasingFunction Easing { get; }

        public Transition(string property, double durationSeconds, double delaySeconds, EasingFunction easing) {
            Property = property ?? throw new ArgumentNullException(nameof(property));
            DurationSeconds = durationSeconds;
            DelaySeconds = delaySeconds;
            Easing = easing ?? LinearEasing.Instance;
        }

        public Transition(string property, double durationSeconds) : this(property, durationSeconds, 0, LinearEasing.Instance) { }
    }
}
