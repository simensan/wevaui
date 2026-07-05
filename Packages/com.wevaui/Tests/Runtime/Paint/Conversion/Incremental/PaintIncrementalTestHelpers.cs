using System.Collections.Generic;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout.Boxes;

namespace Weva.Tests.Paint.Conversion.Incremental {
    internal static class PaintIncrementalTestHelpers {
        public static ComputedStyle Style(Element e = null) {
            return new ComputedStyle(e ?? new Element("div"));
        }

        public static BlockBox Block(double x, double y, double w, double h, ComputedStyle style, Element element = null) {
            var bb = new BlockBox();
            bb.Style = style;
            bb.Element = element ?? (style != null ? style.Element : null);
            bb.X = x; bb.Y = y; bb.Width = w; bb.Height = h;
            bb.Version = NextBoxVersion();
            return bb;
        }

        public static BlockBox Block(ComputedStyle style, Element element = null) {
            return Block(0, 0, 10, 10, style, element);
        }

        // Simulates a layout-engine-style version bump for the box. In production
        // this happens in LayoutEngine.Reconcile when the LayoutCacheKey changes.
        public static void BumpBoxVersion(Box box) {
            box.Version = NextBoxVersion();
        }

        // Simulates the cascade producing a new ComputedStyle for an element. The
        // returned style has a fresh Version. Property values are copied from the
        // input so the new style "looks the same" except for Version.
        public static ComputedStyle CloneWithNewVersion(ComputedStyle src, params (string, string)[] overrides) {
            var s = new ComputedStyle(src.Element);
            foreach (var kv in src.Enumerate()) s.Set(kv.Key, kv.Value);
            if (overrides != null) {
                foreach (var (k, v) in overrides) s.Set(k, v);
            }
            return s;
        }

        public static IEnumerable<Box> AllBoxes(Box root) {
            yield return root;
            foreach (var c in root.Children) {
                foreach (var d in AllBoxes(c)) yield return d;
            }
        }

        public static int CountCacheableBoxes(Box root) {
            int n = 0;
            foreach (var b in AllBoxes(root)) {
                if (b is TextRun) continue;
                n++;
            }
            return n;
        }

        static long boxVersionCounter;

        static long NextBoxVersion() {
            return System.Threading.Interlocked.Increment(ref boxVersionCounter);
        }
    }
}
