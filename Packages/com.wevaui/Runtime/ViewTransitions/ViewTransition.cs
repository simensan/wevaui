using System.Collections.Generic;
using Weva.Animation;
using Weva.Paint;

namespace Weva.ViewTransitions {
    public enum ViewTransitionPhase {
        Pending,
        Capturing,
        Animating,
        Finished,
        Skipped
    }

    public enum ViewTransitionPairKind {
        Matched,
        OldOnly,
        NewOnly
    }

    public readonly struct ViewTransitionPair {
        public readonly string Name;
        public readonly ViewTransitionPairKind Kind;
        public readonly ElementSnapshot Old;
        public readonly ElementSnapshot New;

        public ViewTransitionPair(string name, ViewTransitionPairKind kind, ElementSnapshot oldSnap, ElementSnapshot newSnap) {
            Name = name;
            Kind = kind;
            Old = oldSnap;
            New = newSnap;
        }
    }

    public readonly struct ViewTransitionFrame {
        public readonly string Name;
        public readonly ViewTransitionPairKind Kind;
        public readonly Rect Bounds;
        public readonly double OldAlpha;
        public readonly double NewAlpha;

        public ViewTransitionFrame(string name, ViewTransitionPairKind kind, Rect bounds, double oldAlpha, double newAlpha) {
            Name = name;
            Kind = kind;
            Bounds = bounds;
            OldAlpha = oldAlpha;
            NewAlpha = newAlpha;
        }
    }

    public sealed class ViewTransition {
        public const double DefaultDurationSeconds = 0.25;

        public ViewTransitionPhase Phase { get; internal set; }
        public double Duration { get; internal set; }
        public double Elapsed { get; internal set; }
        public double Progress => Duration > 0 ? System.Math.Min(1.0, Elapsed / Duration) : 1.0;
        public bool Done => Phase == ViewTransitionPhase.Finished || Phase == ViewTransitionPhase.Skipped;

        public ViewTransitionSnapshot Before { get; internal set; }
        public ViewTransitionSnapshot After { get; internal set; }
        public IReadOnlyList<ViewTransitionPair> Pairs { get; internal set; }

        public EasingFunction Easing { get; internal set; }

        internal ViewTransition() {
            Phase = ViewTransitionPhase.Pending;
            Duration = DefaultDurationSeconds;
            Easing = EaseInOutEasing.Instance;
        }

        public IReadOnlyList<ViewTransitionFrame> Sample() {
            var result = new List<ViewTransitionFrame>();
            if (Pairs == null) return result;
            double t = Easing != null ? Easing.Evaluate(Progress) : Progress;
            foreach (var p in Pairs) {
                switch (p.Kind) {
                    case ViewTransitionPairKind.Matched: {
                        var b = LerpRect(p.Old.Bounds, p.New.Bounds, t);
                        result.Add(new ViewTransitionFrame(p.Name, p.Kind, b, 1.0 - t, t));
                        break;
                    }
                    case ViewTransitionPairKind.OldOnly:
                        result.Add(new ViewTransitionFrame(p.Name, p.Kind, p.Old.Bounds, 1.0 - t, 0));
                        break;
                    case ViewTransitionPairKind.NewOnly:
                        result.Add(new ViewTransitionFrame(p.Name, p.Kind, p.New.Bounds, 0, t));
                        break;
                }
            }
            return result;
        }

        static Rect LerpRect(Rect a, Rect b, double t) {
            double x = a.X + (b.X - a.X) * t;
            double y = a.Y + (b.Y - a.Y) * t;
            double w = a.Width + (b.Width - a.Width) * t;
            double h = a.Height + (b.Height - a.Height) * t;
            return new Rect(x, y, w, h);
        }
    }
}
