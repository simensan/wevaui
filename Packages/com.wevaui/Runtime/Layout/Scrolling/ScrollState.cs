namespace Weva.Layout.Scrolling {
    public sealed class ScrollState {
        // Back-reference to the Box that owns this state. Set by ScrollLayout
        // when the state is first attached (GetOrCreateForBox). Used by the
        // ScrollLeft/Top setters to mirror the new offset onto box.ScrollX/Y
        // so the hit-tester and paint converter (which read box.ScrollX/Y
        // directly) stay consistent without an extra container lookup.
        // Null until the state is first linked; cleared on pool-reset of the owner box.
        internal Boxes.Box OwnerBox { get; set; }
        // Pool-generation of the key box at link time (see Box.PoolGeneration):
        // lets ScrollContainer detect a recycled-and-re-rented instance and
        // treat the stale entry as a miss instead of resurrecting it.
        internal int OwnerGeneration;
        // The ELEMENT this state was linked for, captured at link time. The
        // key box's Element cannot be trusted after a recycle-and-re-rent
        // (live find: the .page scroll rode a box that was re-rented as an
        // anonymous wrapper in the same rebuild — alive by instance, dead by
        // generation, elementless by identity). Re-anchoring resolves the
        // live box for THIS element instead.
        internal Weva.Dom.Element OwnerElement;

        public double ScrollX { get; internal set; }

        public double ScrollY { get; internal set; }

        // CSS CSSOM View §5 — scrollLeft / scrollTop aliases.
        // Settable by game code; the setter clamps to [0, Max], mirrors
        // the offset onto the linked Box's ScrollX/Y fields, and bumps
        // ScrollState.Version so paint-cache entries keyed on Version
        // invalidate this frame. Matches the behaviour of SetImmediate in
        // ScrollEventHandler (the authoritative mutation path for input events).
        public double ScrollLeft {
            get => ScrollX;
            set {
                double clamped = value < 0 ? 0 : (value > MaxScrollX ? MaxScrollX : value);
                if (clamped == ScrollX) return;
                ScrollX = clamped;
                if (OwnerBox != null) OwnerBox.ScrollX = clamped;
                BumpVersion();
            }
        }

        public double ScrollTop {
            get => ScrollY;
            set {
                double clamped = value < 0 ? 0 : (value > MaxScrollY ? MaxScrollY : value);
                if (clamped == ScrollY) return;
                ScrollY = clamped;
                if (OwnerBox != null) OwnerBox.ScrollY = clamped;
                BumpVersion();
            }
        }

        public double ScrollWidth { get; internal set; }
        public double ScrollHeight { get; internal set; }

        public double ViewportWidth { get; internal set; }
        public double ViewportHeight { get; internal set; }

        // CSS CSSOM View §5 — clientWidth / clientHeight: the size of the
        // scroll container's padding box (equivalently, the scrollport).
        public double ClientWidth => ViewportWidth;
        public double ClientHeight => ViewportHeight;

        public ScrollOverflow OverflowX { get; internal set; }
        public ScrollOverflow OverflowY { get; internal set; }

        // CSS Overflow Module Level 3 §3: `clip` forbids ALL scrolling
        // (programmatic and input-driven) — unlike `hidden`, which is
        // still a scroll container that can be scrolled programmatically.
        // Without the explicit `!= Clip` check, a mouse wheel over an
        // `overflow: clip` element silently scrolls content under the
        // clip mask. The ShowsTrackX/Y getters below correctly only
        // expose tracks for Scroll/Auto, so visual scrollbars don't
        // appear — only the wheel-event consume path was wrong.
        public bool CanScrollX => OverflowX != ScrollOverflow.Visible
                                   && OverflowX != ScrollOverflow.Hidden
                                   && OverflowX != ScrollOverflow.Clip
                                   && ScrollWidth > ViewportWidth + 0.0001;

        public bool CanScrollY => OverflowY != ScrollOverflow.Visible
                                   && OverflowY != ScrollOverflow.Hidden
                                   && OverflowY != ScrollOverflow.Clip
                                   && ScrollHeight > ViewportHeight + 0.0001;

        public bool ShowsTrackX {
            get {
                if (OverflowX == ScrollOverflow.Scroll) return true;
                if (OverflowX == ScrollOverflow.Auto) return ScrollWidth > ViewportWidth + 0.0001;
                return false;
            }
        }

        public bool ShowsTrackY {
            get {
                if (OverflowY == ScrollOverflow.Scroll) return true;
                if (OverflowY == ScrollOverflow.Auto) return ScrollHeight > ViewportHeight + 0.0001;
                return false;
            }
        }

        public double MaxScrollX {
            get {
                double m = ScrollWidth - ViewportWidth;
                return m > 0 ? m : 0;
            }
        }

        public double MaxScrollY {
            get {
                double m = ScrollHeight - ViewportHeight;
                return m > 0 ? m : 0;
            }
        }

        // CSS CSSOM View §5 — maxScrollLeft / maxScrollTop convenience aliases.
        // Equivalent to MaxScrollX / MaxScrollY; the "Left"/"Top" naming matches
        // the CSS scrollLeft / scrollTop property convention used by game code.
        public double MaxScrollLeft => MaxScrollX;
        public double MaxScrollTop  => MaxScrollY;

        public long Version { get; internal set; }

        // Per-axis thumb hover flags. Updated each pointer-move frame by
        // ScrollEventHandler when a pointer is over the corresponding thumb rect.
        // ScrollbarPaint reads these to determine which thumb is hovered so that
        // ::-webkit-scrollbar-thumb:hover styles apply correctly (Chrome only
        // activates the rule when the pointer is specifically over the thumb rect,
        // not just the scroll container). Both default to false.
        public bool ThumbHoveredX { get; internal set; }
        public bool ThumbHoveredY { get; internal set; }

        // Per-axis thumb active (drag) flags. Set while a scrollbar drag is in
        // progress on the corresponding axis. ::-webkit-scrollbar-thumb:active
        // matches during drag, matching Chrome's behaviour.
        public bool ThumbActiveX { get; internal set; }
        public bool ThumbActiveY { get; internal set; }

        internal void BumpVersion() {
            Version++;
        }

        // Public programmatic scroll setter for game code. Clamps to the
        // valid range so callers don't have to repeat the [0, MaxScroll]
        // math. Bumps Version so paint/cascade caches that key off scroll
        // position invalidate this frame. Pass `double.PositiveInfinity`
        // to snap to the bottom/right edge.
        public void ScrollTo(double x, double y) {
            double cx = x; if (cx < 0) cx = 0; else if (cx > MaxScrollX) cx = MaxScrollX;
            double cy = y; if (cy < 0) cy = 0; else if (cy > MaxScrollY) cy = MaxScrollY;
            if (cx == ScrollX && cy == ScrollY) return;
            ScrollX = cx;
            ScrollY = cy;
            BumpVersion();
        }
    }

    public enum ScrollOverflow {
        Visible,
        Hidden,
        Scroll,
        Auto,
        Clip
    }
}
