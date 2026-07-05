using System.Collections.Generic;
using Weva.Layout.Boxes;

namespace Weva.Tests.Layout.Positioning {
    internal static class PositioningTestHelpers {
        public static BlockBox FirstBlockByTag(Box root, string tag) {
            foreach (var b in Weva.Tests.Layout.LayoutTestHelpers.AllBoxes(root)) {
                if (b is BlockBox bb && !(b is AnonymousBlockBox) && bb.Element?.TagName == tag) return bb;
            }
            return null;
        }

        public static BlockBox NthBlockByTag(Box root, string tag, int n) {
            int seen = 0;
            foreach (var b in Weva.Tests.Layout.LayoutTestHelpers.AllBoxes(root)) {
                if (b is BlockBox bb && !(b is AnonymousBlockBox) && bb.Element?.TagName == tag) {
                    if (seen == n) return bb;
                    seen++;
                }
            }
            return null;
        }

        public static BlockBox FirstByClass(Box root, string cls) {
            foreach (var b in Weva.Tests.Layout.LayoutTestHelpers.AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element != null && HasClass(bb.Element, cls)) return bb;
            }
            return null;
        }

        public static BlockBox FirstById(Box root, string id) {
            foreach (var b in Weva.Tests.Layout.LayoutTestHelpers.AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element != null && bb.Element.Id == id) return bb;
            }
            return null;
        }

        static bool HasClass(Weva.Dom.Element e, string cls) {
            string c = e.GetAttribute("class");
            if (string.IsNullOrEmpty(c)) return false;
            foreach (var t in c.Split(' ')) if (t == cls) return true;
            return false;
        }

        public static List<BlockBox> ChildBlocks(BlockBox parent) {
            var list = new List<BlockBox>();
            foreach (var c in parent.Children) {
                if (c is BlockBox bb && !(c is AnonymousBlockBox)) list.Add(bb);
            }
            return list;
        }

        public static (double x, double y) AbsoluteOriginOf(Box box) {
            double x = 0, y = 0;
            for (var b = box; b != null; b = b.Parent) {
                x += b.X;
                y += b.Y;
            }
            return (x, y);
        }
    }
}
