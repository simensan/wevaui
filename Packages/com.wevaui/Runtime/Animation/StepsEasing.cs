using System;

namespace Weva.Animation {
    public enum StepPosition {
        Start,
        End,
        JumpStart,
        JumpEnd,
        JumpBoth,
        JumpNone
    }

    public sealed class StepsEasing : EasingFunction {
        public int Count { get; }
        public StepPosition Position { get; }

        public StepsEasing(int count, StepPosition position) {
            if (count < 1) throw new ArgumentException("StepsEasing count must be >= 1", nameof(count));
            if (position == StepPosition.JumpNone && count < 2) {
                throw new ArgumentException("StepsEasing with jump-none requires count >= 2", nameof(count));
            }
            Count = count;
            Position = position;
        }

        // CSS specifies steps in terms of (jumps, sub-intervals):
        //   jump-end / end:   jumps at the right of each interval (0 at t=0, 1 at t=1)
        //   jump-start / start: jumps at the left  (1/N at t=0+, 1 at t=1)
        //   jump-both:        N+1 jumps; 1/(N+1) at t=0+, N/(N+1) at t=1-
        //   jump-none:        N-1 jumps; 0 at t=0, 1 at t=1
        public override double Evaluate(double t) {
            if (t < 0) t = 0;
            if (t > 1) t = 1;

            int currentStep = (int)Math.Floor(t * Count);
            // Clamp into [0, Count-1] *before* applying the position-specific shift.
            if (currentStep >= Count) currentStep = Count;

            int jumps;
            int output;
            switch (Position) {
                case StepPosition.Start:
                case StepPosition.JumpStart:
                    jumps = Count;
                    output = currentStep;
                    // Bump by one for the start-position jump, but never past the cap.
                    if (output < Count) output += 1;
                    break;
                case StepPosition.End:
                case StepPosition.JumpEnd:
                    jumps = Count;
                    output = currentStep;
                    if (output > Count) output = Count;
                    break;
                case StepPosition.JumpBoth:
                    jumps = Count + 1;
                    output = currentStep;
                    output += 1;
                    if (output > Count + 1) output = Count + 1;
                    if (t >= 1) output = Count + 1;
                    break;
                case StepPosition.JumpNone:
                    jumps = Count - 1;
                    output = currentStep;
                    if (output > Count - 1) output = Count - 1;
                    break;
                default:
                    jumps = Count;
                    output = currentStep;
                    break;
            }

            if (jumps <= 0) return 0;
            double v = (double)output / jumps;
            if (v < 0) v = 0;
            if (v > 1) v = 1;
            return v;
        }
    }
}
