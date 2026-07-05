using System;
using System.Collections.Generic;
using Weva.Dom;

namespace Weva.Events {
    public enum NavDirection {
        Up,
        Down,
        Left,
        Right,
    }

    // Spatial-focus navigation for D-pad / arrow-key driven UIs. Game code
    // wires the D-pad/arrow buttons to `MoveFocus(NavDirection)`; the
    // class finds the focusable element nearest to the currently focused
    // one in that direction and routes focus through `EventDispatcher`.
    //
    // Coordinate model: callers supply a `rectOf(Element)` callback that
    // returns the laid-out box rect (in document/screen space) for any
    // focusable element. Returning `null` excludes the element from
    // consideration (e.g. if it hasn't been laid out yet, or `display:
    // none` removed its box). The class never walks the box tree itself —
    // game code already has the element-to-box index handy from the
    // layout pipeline; passing it through avoids duplicating layout
    // knowledge here.
    //
    // Algorithm: for each focusable candidate other than the current
    // element, compute the offset from the current center to the
    // candidate center. Reject candidates that don't lie in the requested
    // half-plane (e.g. for `Down`, `dy` must be > 0). Score the rest as
    //     parallel * 1.0 + perpendicular * 2.0
    // where `parallel` is the distance along the movement axis and
    // `perpendicular` is the orthogonal offset. Penalising perpendicular
    // distance more heavily means a button slightly off-axis below the
    // current focus will lose to one directly below it even if the
    // straight-line distance is similar — matches the Smart-TV / console
    // user expectation that pressing Down moves to the thing the user
    // is "currently looking at the bottom of".
    //
    // Wrap-around: disabled by default. Callers that want it should wrap
    // their movement key handler in a "if returned null then jump to the
    // top/bottom-most focusable" fallback. Most console UIs prefer no
    // wrap (so D-pad-spamming at the edge doesn't teleport across the
    // screen).
    public sealed class DirectionalNavigation {
        readonly EventDispatcher dispatcher;
        readonly Document doc;
        readonly Func<Element, NavRect?> rectOf;
        readonly FocusManager focusManager = new();

        // Weight applied to the perpendicular-axis distance when scoring
        // candidates. >1 prefers elements aligned with the movement axis.
        // 2.0 is the default; tweak via the public setter if a UI's
        // layout makes alignment harder than usual.
        public double PerpendicularPenalty { get; set; } = 2.0;

        public Func<Element, bool> IsHidden {
            get => focusManager.IsHidden;
            set => focusManager.IsHidden = value;
        }

        public DirectionalNavigation(EventDispatcher dispatcher, Document doc, Func<Element, NavRect?> rectOf) {
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            this.doc = doc ?? throw new ArgumentNullException(nameof(doc));
            this.rectOf = rectOf ?? throw new ArgumentNullException(nameof(rectOf));
        }

        // Finds the best candidate in `direction` and focuses it. Returns
        // the newly-focused element, or null if no valid candidate exists
        // (no focusable elements on screen, or every focusable lies in
        // the wrong half-plane).
        public Element MoveFocus(NavDirection direction) {
            var current = dispatcher.FocusedElement;
            var pick = FindNearest(direction, current);
            if (pick != null) dispatcher.Focus(pick);
            return pick;
        }

        // Same logic as MoveFocus but does not actually focus the picked
        // element — useful for previewing / highlighting candidates and
        // for tests.
        public Element FindNearest(NavDirection direction, Element from) {
            var fromRect = from != null ? rectOf(from) : null;
            // No anchor: pick the topmost-leftmost focusable so the first
            // D-pad press lights up SOMETHING, matching Smart TV / console
            // out-of-box behavior on a freshly opened menu.
            if (fromRect == null) {
                return PickEntryFocusable();
            }

            double fromCx = fromRect.Value.CenterX;
            double fromCy = fromRect.Value.CenterY;

            Element best = null;
            double bestScore = double.PositiveInfinity;

            EnumerateFocusables(doc, e => {
                if (e == from) return;
                var r = rectOf(e);
                if (!r.HasValue) return;
                double cx = r.Value.CenterX;
                double cy = r.Value.CenterY;
                double dx = cx - fromCx;
                double dy = cy - fromCy;
                double parallel, perpendicular;
                switch (direction) {
                    case NavDirection.Up:
                        if (dy >= 0) return;
                        parallel = -dy; perpendicular = Math.Abs(dx);
                        break;
                    case NavDirection.Down:
                        if (dy <= 0) return;
                        parallel = dy; perpendicular = Math.Abs(dx);
                        break;
                    case NavDirection.Left:
                        if (dx >= 0) return;
                        parallel = -dx; perpendicular = Math.Abs(dy);
                        break;
                    case NavDirection.Right:
                        if (dx <= 0) return;
                        parallel = dx; perpendicular = Math.Abs(dy);
                        break;
                    default:
                        return;
                }
                double score = parallel + perpendicular * PerpendicularPenalty;
                if (score < bestScore) {
                    bestScore = score;
                    best = e;
                }
            });

            return best;
        }

        Element PickEntryFocusable() {
            Element best = null;
            double bestScore = double.PositiveInfinity;
            EnumerateFocusables(doc, e => {
                var r = rectOf(e);
                if (!r.HasValue) return;
                // Topmost-leftmost: smallest `Y * 10 + X`-ish score.
                double score = r.Value.Top * 10000 + r.Value.Left;
                if (score < bestScore) {
                    bestScore = score;
                    best = e;
                }
            });
            return best;
        }

        void EnumerateFocusables(Node n, Action<Element> visit) {
            foreach (var c in n.Children) {
                if (c is Element e) {
                    if (focusManager.IsProgrammaticallyFocusable(e)) visit(e);
                    EnumerateFocusables(e, visit);
                } else {
                    EnumerateFocusables(c, visit);
                }
            }
        }
    }

    // Lightweight rect descriptor used by DirectionalNavigation. Decoupled
    // from `Weva.Paint.Rect` and `Weva.Layout.Boxes.Box` so callers
    // outside the rendering pipeline (e.g. tests, custom focus rings) can
    // build it from any source.
    public readonly struct NavRect {
        public double Left { get; }
        public double Top { get; }
        public double Width { get; }
        public double Height { get; }

        public NavRect(double left, double top, double width, double height) {
            Left = left;
            Top = top;
            Width = width;
            Height = height;
        }

        public double CenterX => Left + Width * 0.5;
        public double CenterY => Top + Height * 0.5;
        public double Right => Left + Width;
        public double Bottom => Top + Height;
    }
}
