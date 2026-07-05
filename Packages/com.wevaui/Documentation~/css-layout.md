# CSS — Layout

[← Back to index](index.md) · [← Supported CSS](supported-css.md)

Weva implements its own block, inline, flex, grid, and positioned layout. Flex
and grid are full reimplementations (not Yoga), matching CSS behavior.

## `display`

`block`, `inline`, `inline-block`, `flex`, `inline-flex`, `grid`,
`inline-grid`, `none`, `contents`.

A real **inline formatting context** flows prose, mixed `<span>`/`<strong>`/`<a>`
runs, and text-with-icons — the thing UI Toolkit cannot do. Mixed-style inline
runs render correctly within a line.

## Box model

- `width`, `height`, `min-/max-width`, `min-/max-height`.
- Logical sizes: `inline-size`, `block-size`, and logical min/max.
- `padding`, `margin`, `border` — physical and logical longhands + shorthands.
  **Note:** the paint converter reads longhand border properties; the
  `border: 1px solid red` shorthand is expanded by the cascade's
  `BorderShorthandExpander`.
- `box-sizing`: `content-box` (default, per spec) and `border-box`.
- `border-radius` per-corner, including the elliptical `h / v` slash form.

Margin collapsing is implemented per CSS Box Model §8.3.1 (sibling pairs,
parent-first/last collapse, self-collapsing empty blocks). `inline-block`
shrink-to-fits and participates in the IFC.

## Flexbox

Full property surface: `flex`, `flex-direction`, `flex-wrap`, `flex-basis`,
`flex-grow`, `flex-shrink`, `justify-content`, `align-items`, `align-self`,
`align-content`, `gap`/`row-gap`/`column-gap`, `order`. Row-flex `baseline`
cross-axis alignment uses each item's first-line ascent.

v1 simplifications:

- `min-content` / `max-content` sizing keywords are treated as `auto`.
- `aspect-ratio` is not honored in flex sizing.
- Column-flex `baseline` cross-alignment falls back to `flex-start`.
- Item min/max main-size constraints are single-pass (no clamp loop).
- Longhand flex initial values don't override a user-set `flex` shorthand (the
  cascade doesn't yet track explicit-vs-initial).
- Text directly inside a flex container falls into anonymous-block flow rather
  than becoming an anonymous flex item.

## Grid

Own implementation: track parser, areas parser, placement resolver, two-pass
track sizing, `fr` distribution, gap, alignment, auto-flow. Supports
`grid-template-columns/rows`, `grid-template-areas`, `grid-column`, `grid-row`,
`grid-auto-flow`, `grid-auto-columns/rows`, `place-items`, `place-content`,
`place-self`, `repeat()`, `minmax()`, `fr`, `auto-fill`, `auto-fit`, and
`subgrid` on `grid-auto-rows/columns`. Intrinsic track sizing for spanning
items follows the §11.5 growth-limit-priority walk.

## Positioning

`position`: `static`, `relative`, `absolute`, `fixed`, `sticky`, with
`top`/`right`/`bottom`/`left`, `z-index`, and stacking contexts. Anchor
positioning (`anchor-name`, `position-anchor`, `anchor()`,
`position-try-fallbacks`) is implemented under `Layout/AnchorPositioning/`.

v1 simplifications:

- `position: sticky` is **single-axis** (top OR bottom — top wins when both are
  set; same for left/right). Sticky offsets recompute on scroll even on
  paint-only frames.
- `position: fixed` uses the viewport (unaffected by ancestor scroll).
- The absolute-positioning containing block is the nearest positioned
  ancestor's **border**-box.
- Both-pinned absolute boxes (`top: 0; bottom: 0`) don't iterate to reconcile
  with intrinsic sizes, and don't re-flow their interior.
- Positioned descendants with `z-index: auto` do **not** create their own
  stacking context (older-spec behavior); `fixed`/`sticky` always do.

## Overflow & scrolling

`overflow`, `overflow-x`, `overflow-y`: `visible`, `hidden`, `scroll`, `auto`,
`clip`. Scroll containers, scrollbars, `position: sticky` integration, scroll
snap, and smooth scrolling exist (`Layout/Scrolling/`). No overscroll chaining
or scroll anchoring.

**Scrollbar styling** (CSS Scrollbars L1): `scrollbar-color: <thumb> <track>`
(inherited; `currentColor` and full color syntax) and `scrollbar-width:
auto | thin | none` (12px / 8px overlay-style / hidden-but-scrollable).

**WebKit scrollbar pseudo-elements**: `::-webkit-scrollbar { width/height }`,
`::-webkit-scrollbar-thumb { background-color, border-radius }` with `:hover`
and `:active` variants (active persists through a drag, like Chrome),
`::-webkit-scrollbar-track { background-color }`, and
`::-webkit-scrollbar-corner { background-color }` (painted when both axes
show scrollbars). When any webkit scrollbar rule matches an element, the
L1 `scrollbar-color`/`scrollbar-width` properties are ignored for it —
Chrome's precedence. `-button` / `-resizer` parse but don't paint.

**Inertial scrolling**: dragging inside a scroll container scrolls it live and
a flick release glides with iOS-style exponential decay, landing on a snap
point when the container declares `scroll-snap-type`. A drag only *arms* past
an 8px slop, so taps on controls inside scrollables click through untouched;
an armed drag takes pointer capture and suppresses the click on release
(Chrome touch semantics). Wheel and keyboard scrolling are unaffected.
Dragging past an edge rubber-bands with iOS-style diminishing resistance and
springs back critically damped; a glide into an edge overshoots (capped) and
springs back the same way. Wheel, keyboard, and programmatic scrolls still
clamp hard.

## Multi-column

`column-count`, `column-width`, the `columns` shorthand, `column-gap`
(`normal` = 1em, Chrome's default), and `column-rule` (painted centered in
each gap; dashed/dotted render solid in v1) on block containers. Auto-height
containers balance column heights; explicit heights fill sequentially.
A block child taller than the column height is sliced across columns
(paint-level fragmentation, matching Chrome). Remaining divergences: a
child taller than the whole multi-column span overflows the last column
downward; margin collapsing across column boundaries isn't performed.
`column-span` and forced breaks parse but are ignored.

## Containment & content-visibility

`contain: layout | paint | size | inline-size | strict | content` applies
real containment (CSS Containment L2): `paint` clips descendants to the
padding box, establishes a stacking context, and becomes the containing
block for absolute/fixed descendants; `layout` is a margin-collapse barrier
and abs/fixed containing block; `size` / `inline-size` make the contained
axis contribute zero intrinsic size (auto sizes collapse to the frame) —
`contain-intrinsic-size` / `-width` / `-height` supply a placeholder size
instead (`auto <length>` uses the fallback length; no last-remembered-size
memo in v1).

`content-visibility: hidden` skips the contents in paint and hit-testing
while the element's own box still lays out and paints (boxes are kept, not
discarded — DevTools can still inspect them). `content-visibility: auto`
applies containment and skips painting descendants of elements entirely
outside the viewport.

## Logical axes & RTL

`direction: rtl` flips horizontal inline-start/end mapping, `text-align:
start/end`, logical insets/sizes/box edges, and row-flex main-axis order.
`writing-mode` remaps logical properties for vertical/sideways modes, **but
glyph flow stays horizontal** — vertical text layout is a v1 non-goal.
`unicode-bidi` is registered but no bidi reordering is performed.

## Floats & tables

`float: left/right` with `clear` and per-paragraph exclusion is implemented
(`Layout/Floats/`), though we recommend flex/grid for new UI. Runtime
tables exist including collapsed-border winner resolution; advanced
fragmentation is out of v1.

---

Next: [CSS Visual](css-visual.md) · [CSS Text](css-text.md)
