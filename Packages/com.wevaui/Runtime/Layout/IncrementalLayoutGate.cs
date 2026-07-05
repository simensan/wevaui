using Weva.Reactive;

namespace Weva.Layout {
    // Gatekeeper that decides whether a Layout pass can be skipped wholesale by
    // consulting the per-frame InvalidationTracker. The contract is conservative:
    // skip ONLY when no element has Layout or Structure flags set. Style-only
    // changes (color, background, etc.) are paint-only and never force a layout
    // recompute. PseudoClassState alone implies Style (see InvalidationTracker)
    // but never Layout, so a hover toggle that flips a non-layout-affecting rule
    // (e.g. button:hover { color: red }) lands here as Style-only and skips
    // layout entirely.
    //
    // Layout-affecting properties — i.e. the property names whose change MUST
    // mark Layout dirty so this gate doesn't accidentally skip — per
    // CSS Box Model L3 §8 (margins/borders/padding/sizing), CSS Display L3 §2
    // (display/position), CSS Flexbox L1 §5 (flex-*), CSS Grid L1 §7 (grid-*),
    // CSS Inline L3 §3 (font-size/line-height/letter-spacing), CSS Positioned
    // Layout L3 §6 (top/right/bottom/left), CSS Sizing L3 §5 (width/height/
    // min-/max-/aspect-ratio):
    //
    //   width, height, inline-size, block-size, min/max physical+logical sizes,
    //   margin, margin-{top,right,bottom,left}, margin-inline/block-*,
    //   padding, padding-{top,right,bottom,left}, padding-inline/block-*,
    //   border-width, border-{top,right,bottom,left}-width, logical border widths,
    //   font-size, line-height, letter-spacing, word-spacing, tab-size, font-family,
    //   font-weight, font-style, white-space, text-wrap, word-break, overflow-wrap, hyphens,
    //   display, position, top, right, bottom, left, box-sizing,
    //   flex, flex-direction, flex-wrap, flex-basis, flex-grow, flex-shrink,
    //   justify-content, align-items, align-self, align-content,
    //   gap, row-gap, column-gap, order,
    //   grid-template-columns, grid-template-rows, grid-template-areas,
    //   grid-auto-columns, grid-auto-rows, grid-auto-flow,
    //   grid-{column,row}-start, grid-{column,row}-end, grid-{column,row},
    //   place-items, place-content, place-self,
    //   overflow, overflow-x, overflow-y, scrollbar-gutter,
    //   text-align (affects line splitting on justify), text-align-last, text-indent, direction, writing-mode,
    //   anchor-name, position-anchor (anchor positioning resolves at layout time),
    //   container-type, container-name, contain.
    //
    // NON-layout-affecting properties (paint-only): color, background-*,
    // border-color, border-style, border-radius, opacity, transform, filter,
    // box-shadow, text-shadow, text-decoration, cursor, outline-{color,style,
    // width,offset} — outline does not participate in box sizing per CSS UI L4
    // §4. visibility:hidden does NOT affect layout sizing but DOES affect paint;
    // visibility:collapse on a flex/grid item DOES affect layout, but the
    // cascade currently treats `visibility` as Discrete (not animatable in the
    // layout-affecting sense). Callers that mark visibility:collapse must mark
    // Layout dirty explicitly.
    public static class IncrementalLayoutGate {
        public static bool ShouldSkipLayout(InvalidationTracker tracker) {
            if (tracker == null) return false;
            return !tracker.HasAny(InvalidationKind.Layout | InvalidationKind.Structure);
        }
    }
}
