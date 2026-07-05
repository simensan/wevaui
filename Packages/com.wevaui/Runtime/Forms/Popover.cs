using System;
using System.Collections.Generic;
using Weva.Css.Values;
using Weva.Dom;

namespace Weva.Forms {
    // Popover state management. Each element with a `popover` attribute is
    // tracked in a per-document stack; the top of stack is the most-recently
    // shown. PopoverController consults this registry when handling clicks
    // and the Escape key.
    //
    // Popover modes:
    //   `auto`   — light-dismissable; outside click or Escape closes the
    //              top-of-stack auto popover.
    //   `manual` — explicit show/hide only; ignores outside clicks and
    //              Escape.
    // Default (no value or value other than "manual") is "auto".
    //
    // The "open" state is reflected as a `data-popover-open` attribute on the
    // element so the cascade can match `:popover-open` (synthesized) and the
    // UA stylesheet can target it with an attribute selector for the v1 style.
    public static class Popover {
        public const string OpenAttr = "data-popover-open";

        public static string GetMode(Element e) {
            if (e == null) return "auto";
            var v = e.GetAttribute("popover");
            if (v == null) return null;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(v, "manual")) return "manual";
            return "auto";
        }

        public static bool HasPopover(Element e) => e != null && e.HasAttribute("popover");

        public static bool IsOpen(Element e) => e != null && e.HasAttribute(OpenAttr);
    }

    // Per-document popover stack + show/hide/toggle logic. The stack tracks
    // the visibility order so a Hide on the top resets focus correctly and
    // an outside click closes only the top auto popover.
    //
    // V1 simplification: nested popovers (a popover that opens another) all
    // sit in the same stack; the controller does not enforce ancestor
    // relationships during light-dismiss. The Escape handler closes the
    // top popover only, so authors who nest popovers get reasonable
    // behaviour even without the spec's full ancestry analysis.
    public sealed class PopoverStack {
        readonly List<Element> stack = new();

        public int Count => stack.Count;
        public Element Top => stack.Count == 0 ? null : stack[stack.Count - 1];
        public IReadOnlyList<Element> Items => stack;

        public void Show(Element e) {
            if (e == null) return;
            if (!Popover.HasPopover(e)) return;
            if (Popover.IsOpen(e)) return;
            e.SetAttribute(Popover.OpenAttr, "");
            stack.Add(e);
        }

        public void Hide(Element e) {
            if (e == null) return;
            if (!Popover.IsOpen(e)) return;
            e.RemoveAttribute(Popover.OpenAttr);
            stack.Remove(e);
        }

        public void Toggle(Element e) {
            if (e == null) return;
            if (Popover.IsOpen(e)) Hide(e);
            else Show(e);
        }

        // Closes every popover at or above (and including) `e` in the stack.
        // Used by light-dismiss when a click lands outside the top popover —
        // the spec hides only the top, but we expose this so a Cascading
        // Hide is also possible. The Escape handler closes one popover per
        // press, walking down the stack one step at a time.
        public void HideTopAuto() {
            for (int i = stack.Count - 1; i >= 0; i--) {
                if (Popover.GetMode(stack[i]) == "auto") {
                    Hide(stack[i]);
                    return;
                }
            }
        }
    }
}
