using Weva.Animation;

namespace Weva.Css.Animation {
    // CSS Animations L2 §5 / §10: how the animation's effective value is
    // combined with the underlying (un-animated) value for the property.
    // `Replace` is the L1 behaviour and the property's initial value.
    public enum AnimationCompositionMode {
        Replace,
        Add,
        Accumulate,
    }

    public readonly struct AnimationSpec {
        public string Name { get; }
        public double DurationSeconds { get; }
        public double DelaySeconds { get; }
        public EasingFunction Easing { get; }
        public double IterationCount { get; }
        public PlaybackDirection Direction { get; }
        public FillMode FillMode { get; }
        public bool Paused { get; }
        public AnimationCompositionMode Composition { get; }

        public AnimationSpec(
            string name,
            double durationSeconds,
            double delaySeconds,
            EasingFunction easing,
            double iterationCount,
            PlaybackDirection direction,
            FillMode fillMode,
            bool paused)
            : this(name, durationSeconds, delaySeconds, easing, iterationCount, direction, fillMode, paused, AnimationCompositionMode.Replace) { }

        public AnimationSpec(
            string name,
            double durationSeconds,
            double delaySeconds,
            EasingFunction easing,
            double iterationCount,
            PlaybackDirection direction,
            FillMode fillMode,
            bool paused,
            AnimationCompositionMode composition) {
            Name = name;
            DurationSeconds = durationSeconds;
            DelaySeconds = delaySeconds;
            Easing = easing ?? EaseEasing.Instance;
            IterationCount = iterationCount;
            Direction = direction;
            FillMode = fillMode;
            Paused = paused;
            Composition = composition;
        }
    }
}
