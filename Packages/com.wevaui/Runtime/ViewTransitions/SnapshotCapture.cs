using System.Collections.Generic;
using Weva.Css.Cascade;
using Weva.Layout.Boxes;
using Weva.Paint;

namespace Weva.ViewTransitions {
    public readonly struct ElementSnapshot {
        public readonly string Name;
        public readonly Rect Bounds;
        public readonly double Opacity;

        public ElementSnapshot(string name, Rect bounds, double opacity) {
            Name = name;
            Bounds = bounds;
            Opacity = opacity;
        }
    }

    public sealed class ViewTransitionSnapshot {
        readonly Dictionary<string, ElementSnapshot> byName;

        public ViewTransitionSnapshot() {
            byName = new Dictionary<string, ElementSnapshot>();
        }

        public ViewTransitionSnapshot(IDictionary<string, ElementSnapshot> map) {
            byName = new Dictionary<string, ElementSnapshot>(map);
        }

        public IReadOnlyDictionary<string, ElementSnapshot> ByName => byName;

        public int Count => byName.Count;

        public bool TryGet(string name, out ElementSnapshot snap) {
            return byName.TryGetValue(name, out snap);
        }

        internal void Add(string name, ElementSnapshot snap) {
            byName[name] = snap;
        }
    }

    public static class SnapshotCapture {
        // `view-transition-name` is registered lazily by
        // ViewTransitionProperties.EnsureRegistered() rather than in the
        // central CssProperties static ctor, so there's no compile-time int
        // constant for it. Cache the id once on first read (id is stable
        // after registration) to drop the per-call string lookup, matching
        // the int-id hot path used by the rest of the project. The id can
        // be -1 if SnapshotCapture is ever invoked before the engine has
        // registered the property; in that case we re-resolve on the next
        // call (a successful registration will fix the cache).
        static int viewTransitionNameId = -1;

        public static ViewTransitionSnapshot Capture(Box root) {
            var snapshot = new ViewTransitionSnapshot();
            if (root == null) return snapshot;
            Walk(root, 0, 0, 0, 0, snapshot);
            return snapshot;
        }

        // (parentAbsX, parentAbsY) is the parent's absolute origin in
        // document coordinates; (parentScrollX, parentScrollY) is the
        // already-accumulated scroll subtraction. Without threading
        // the absolute origin down the walk, sibling subtrees at the
        // same local Box.X collapsed onto the same snapshot bounds —
        // the crossfade then interpolated between meaningless rects.
        static void Walk(Box box, double parentAbsX, double parentAbsY,
                         double parentScrollX, double parentScrollY,
                         ViewTransitionSnapshot snapshot) {
            if (box == null) return;
            // Convert box's parent-local position to absolute document
            // coordinates, then apply the accumulated scroll offset
            // (sign matches the original code: scrolled-down content
            // appears at a smaller Y in the visible viewport).
            // Include the sticky offset per Box.StickyOffsetX/Y's
            // contract — both paint (BoxToPaintConverter:555-556) and
            // hit-testing (BoxTreeHitTester:42-43, 108-109) add the
            // sticky translation. Snapshotting the visible rect for a
            // view-transition crossfade requires the same correction,
            // else a sticky `<header>` carrying view-transition-name
            // records its in-flow Y (potentially off-screen) instead
            // of its stuck visible Y and the crossfade jumps.
            double absX = parentAbsX + box.X + box.StickyOffsetX - parentScrollX;
            double absY = parentAbsY + box.Y + box.StickyOffsetY - parentScrollY;
            string name = ResolveName(box);
            if (!string.IsNullOrEmpty(name) && name != "none") {
                // Spec: duplicate view-transition-name on multiple
                // elements is undefined behavior; we deterministically
                // pick the FIRST encountered (in document order) and
                // skip subsequent duplicates so a stray repeated name
                // doesn't silently overwrite the intended target.
                if (!snapshot.ByName.ContainsKey(name)) {
                    var bounds = new Rect(absX, absY, box.Width, box.Height);
                    double opacity = ResolveOpacity(box);
                    snapshot.Add(name, new ElementSnapshot(name, bounds, opacity));
                }
            }
            double sx = box.ScrollX;
            double sy = box.ScrollY;
            foreach (var c in box.Children) {
                // Children are positioned in our local space; pass
                // OUR absolute origin (absX/absY) as their parent
                // origin, plus our scroll offsets.
                Walk(c, absX, absY, sx, sy, snapshot);
            }
        }

        static string ResolveName(Box box) {
            if (box?.Style == null) return null;
            int id = viewTransitionNameId;
            if (id < 0) {
                id = CssProperties.GetId("view-transition-name");
                if (id >= 0) viewTransitionNameId = id;
            }
            string raw = id >= 0
                ? box.Style.Get(id)
                : box.Style.Get("view-transition-name");
            if (string.IsNullOrEmpty(raw)) return null;
            string s = raw.Trim();
            if (s == "none") return null;
            return s;
        }

        static double ResolveOpacity(Box box) {
            if (box?.Style == null) return 1.0;
            string raw = box.Style.Get(CssProperties.OpacityId);
            if (string.IsNullOrEmpty(raw)) return 1.0;
            if (double.TryParse(raw.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)) {
                if (v < 0) v = 0;
                if (v > 1) v = 1;
                return v;
            }
            return 1.0;
        }
    }
}
