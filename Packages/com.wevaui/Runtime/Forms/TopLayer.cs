using Weva.Dom;

namespace Weva.Forms {
    // CSS Fullscreen / HTML Living Standard "top layer" host detection. In v1
    // we promote two element shapes to the top layer:
    //
    //   * `<dialog>` with `data-modal` — set by `DialogElement.ShowModal()`.
    //     Non-modal `Show()` does NOT promote, matching the spec where only
    //     `showModal()` puts the dialog in the top layer.
    //
    //   * Any element with `popover` attribute and `data-popover-open` — set
    //     by `PopoverController` when a popover is open.
    //
    // BoxBuilder consults this predicate during box-tree construction to decide
    // whether to inject a synthetic `::backdrop` sibling box before the host.
    // Top-layer status is an attribute-driven view, not a stored flag, so
    // mutations to `data-modal`/`data-popover-open` propagate through the next
    // box-build pass without any extra wiring.
    public static class TopLayer {
        public static bool IsHost(Element e) {
            if (e == null) return false;
            // Modal dialogs.
            if (e.TagName == "dialog" && e.HasAttribute("data-modal")) return true;
            // Open popovers (any element with the popover attribute when its
            // controller has marked it open). The HTML spec gives <button> the
            // popovertarget attribute; the popover itself is the target. We
            // match the target (the open popover element), not the trigger.
            if (e.HasAttribute("popover") && e.HasAttribute("data-popover-open")) return true;
            return false;
        }
    }
}
