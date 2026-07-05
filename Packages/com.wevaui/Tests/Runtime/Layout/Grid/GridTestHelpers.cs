using System.Collections.Generic;
using Weva.Layout.Boxes;
using Weva.Layout.Grid;

namespace Weva.Tests.Layout.Grid {
    internal static class GridTestHelpers {
        public static GridBox FindGrid(Box root, string tag = null) {
            foreach (var b in Weva.Tests.Layout.LayoutTestHelpers.AllBoxes(root)) {
                if (b is GridBox gb && (tag == null || gb.Element?.TagName == tag)) return gb;
            }
            return null;
        }

        public static GridBox FindGridByClass(Box root, string cls) {
            foreach (var b in Weva.Tests.Layout.LayoutTestHelpers.AllBoxes(root)) {
                if (b is GridBox gb && HasClass(gb, cls)) return gb;
            }
            return null;
        }

        public static List<BlockBox> ChildBlockBoxes(GridBox container) {
            var list = new List<BlockBox>();
            foreach (var c in container.Children) {
                if (c is BlockBox bb) list.Add(bb);
            }
            return list;
        }

        public static BlockBox ChildAt(GridBox container, int idx) {
            int seen = 0;
            foreach (var c in container.Children) {
                if (c is BlockBox bb) {
                    if (seen == idx) return bb;
                    seen++;
                }
            }
            return null;
        }

        public static BlockBox FindByClass(Box root, string cls) {
            foreach (var b in Weva.Tests.Layout.LayoutTestHelpers.AllBoxes(root)) {
                if (b is BlockBox bb && HasClass(bb, cls)) return bb;
            }
            return null;
        }

        public static bool HasClass(Box b, string cls) {
            if (b.Element == null) return false;
            var raw = b.Element.ClassName;
            if (string.IsNullOrEmpty(raw)) return false;
            foreach (var c in raw.Split(' ')) if (c == cls) return true;
            return false;
        }
    }
}
