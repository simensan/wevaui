using System.Collections.Generic;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Layout.Flex;

namespace Weva.Tests.Layout.Flex {
    internal static class FlexTestHelpers {
        public static FlexBox FindFlex(Box root, string tag = null) {
            foreach (var b in Weva.Tests.Layout.LayoutTestHelpers.AllBoxes(root)) {
                if (b is FlexBox fb && (tag == null || fb.Element?.TagName == tag)) return fb;
            }
            return null;
        }

        public static List<BlockBox> ChildBlockBoxes(FlexBox container) {
            var list = new List<BlockBox>();
            foreach (var c in container.Children) {
                if (c is BlockBox bb) list.Add(bb);
            }
            return list;
        }

        public static BlockBox ChildAt(FlexBox container, int idx) {
            int seen = 0;
            foreach (var c in container.Children) {
                if (c is BlockBox bb) {
                    if (seen == idx) return bb;
                    seen++;
                }
            }
            return null;
        }
    }
}
