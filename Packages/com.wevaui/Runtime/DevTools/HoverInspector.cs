using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Events;
using Weva.Layout.Boxes;

namespace Weva.DevTools {
    // Resolves a pointer position to the element under the cursor and
    // assembles the inspect readout. The string output is what the overlay
    // draws via GUI.Label; the resolved Element / Box are exposed separately
    // so the BoxOutlineRenderer can highlight just the hovered subtree.
    public sealed class HoverInspector {
        // The CSS properties most authors care about when a layout looks wrong.
        // Trimmed to ~10 to fit the corner readout without scrolling.
        static readonly string[] InterestingProps = new[] {
            "display", "position", "width", "height",
            "padding", "margin", "border",
            "color", "background-color", "font-size",
        };

        // Parallel int-id table for the same property list. Resolved once at
        // type-init so the Format() loop can hit the per-style array-indexed
        // Get(int) overload instead of paying the string→id dictionary lookup
        // per property per hover frame. Indices align with InterestingProps;
        // any shorthand whose id is -1 (e.g. `border` / `padding` / `margin`
        // when they remain shorthands rather than registered properties)
        // falls back to the string-keyed read below.
        static readonly int[] InterestingPropIds = BuildInterestingPropIds();
        static int[] BuildInterestingPropIds() {
            var ids = new int[InterestingProps.Length];
            for (int i = 0; i < InterestingProps.Length; i++) {
                ids[i] = CssProperties.GetId(InterestingProps[i]);
            }
            return ids;
        }

        public Element CurrentElement { get; private set; }
        public Box CurrentBox { get; private set; }

        public Element Resolve(IHitTester hitTester, double x, double y, System.Func<Element, Box> elementToBox) {
            CurrentElement = null;
            CurrentBox = null;
            if (hitTester == null) return null;
            var e = hitTester.HitTest(x, y);
            if (e == null) return null;
            CurrentElement = e;
            if (elementToBox != null) {
                CurrentBox = elementToBox(e);
            }
            return e;
        }

        public string Format(Element e, Box b, ComputedStyle style) {
            var sb = new StringBuilder(192);
            FormatTagLine(sb, e);
            sb.Append('\n');
            if (b != null) {
                sb.Append("size ");
                sb.Append(((int)b.Width).ToString(CultureInfo.InvariantCulture));
                sb.Append("x");
                sb.Append(((int)b.Height).ToString(CultureInfo.InvariantCulture));
                sb.Append(" px @ ");
                sb.Append(((int)b.X).ToString(CultureInfo.InvariantCulture));
                sb.Append(",");
                sb.Append(((int)b.Y).ToString(CultureInfo.InvariantCulture));
                sb.Append('\n');
            }
            if (style != null) {
                for (int i = 0; i < InterestingProps.Length; i++) {
                    var key = InterestingProps[i];
                    int id = InterestingPropIds[i];
                    // Per-frame inspector readout: hit the array-indexed
                    // overload when the property is registered (id >= 0),
                    // otherwise fall back to the string-keyed path for
                    // unregistered shorthands. DevTools is cold relative to
                    // paint, but the symmetry with the rest of the runtime
                    // resolver pool makes the intent explicit.
                    string val = id >= 0 ? style.Get(id) : style.Get(key);
                    if (string.IsNullOrEmpty(val)) continue;
                    sb.Append(key).Append(": ").Append(val).Append('\n');
                }
            }
            // Trim the trailing newline so the GUI.Label rect we measure
            // doesn't get a phantom blank line at the bottom.
            if (sb.Length > 0 && sb[sb.Length - 1] == '\n') sb.Length--;
            return sb.ToString();
        }

        // Like '<button.btn-primary#start-button>'. Mirrors the way Chrome
        // DevTools renders an element header: tag, '#id' if present, then
        // every class joined by '.'.
        static void FormatTagLine(StringBuilder sb, Element e) {
            if (e == null) {
                sb.Append("<no element>");
                return;
            }
            sb.Append('<');
            sb.Append(e.TagName);
            var id = e.Id;
            if (!string.IsNullOrEmpty(id)) {
                sb.Append('#').Append(id);
            }
            foreach (var c in e.ClassList) {
                sb.Append('.').Append(c);
            }
            sb.Append('>');
        }
    }
}
