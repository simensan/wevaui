# CSS — Layout

[← Back to index](index.md) · [← Supported CSS](supported-css.md)

The shared core implements block, inline, flex, grid, table and positioned
layout. The supported surface and remaining limits are described below.

## `display`

`block`, `inline`, `inline-block`, `flex`, `inline-flex`, `grid`,
`inline-grid`, `none`, `contents`.

A real **inline formatting context** flows prose, mixed `<span>`/`<strong>`/`<a>`
runs, and text-with-icons. Mixed-style inline
runs render correctly within a line.

## Box model

- `width`, `height`, `min-/max-width`, `min-/max-height`.
- Logical sizes: `inline-size`, `block-size`, and logical min/max.
- `padding`, `margin`, `border` — physical and logical longhands + shorthands.
  Shorthands such as `border: 1px solid red` expand into longhands in the core.
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

Row sizing supports `min-content` / `max-content` probes, and the flex
distribution loop freezes constrained items and redistributes remaining space.
Remaining limits include:

- `aspect-ratio` transfer during flex sizing is incomplete.
- Column-flex `baseline` cross-alignment falls back to `flex-start`.

## Grid

Own implementation: track parser, areas parser, placement resolver, two-pass
track sizing, `fr` distribution, gap, alignment, auto-flow. Supports
`grid-template-columns/rows`, `grid-template-areas`, `grid-column`, `grid-row`,
`grid-auto-flow`, `grid-auto-columns/rows`, `place-items`, `place-content`,
`place-self`, `repeat()`, `minmax()`, `fr`, `auto-fill`, `auto-fit`, and
`subgrid` on `grid-template-rows/columns`. Intrinsic track sizing for spanning
items follows the §11.5 growth-limit-priority walk.

## Positioning

`position`: `static`, `relative`, `absolute`, `fixed`, `sticky`, with
`top`/`right`/`bottom`/`left`, `z-index`, and stacking contexts. Anchor
positioning (`anchor-name`, `position-anchor`, `anchor()`,
`position-try-fallbacks`) is implemented in the core.

v1 simplifications:

- `position: sticky` is **single-axis** (top OR bottom — top wins when both are
  set; same for left/right). Sticky offsets recompute on scroll even on
  paint-only frames.
- `position: fixed` normally uses the viewport. Ancestor transforms, filters
  and perspective can establish a containing block instead.
- The absolute-positioning containing block is the nearest positioned
  ancestor's **padding** box; containing-block properties such as transforms
  also qualify. Both-pinned boxes can reflow after their available size resolves.
- Positioned descendants with `z-index: auto` do **not** create their own
  stacking context (older-spec behavior); `fixed`/`sticky` always do.

## Overflow & scrolling

`overflow`, `overflow-x`, `overflow-y`: `visible`, `hidden`, `scroll`, `auto`,
`clip`. Scroll containers, scrollbars, sticky positioning, scroll snap, and
smooth scrolling run in the shared core. `clip` clips painting without
establishing a scroll container: it cannot be scrolled by code, and sticky
elements and snap areas inside it still use an outer scroll container.
`hidden` permits scripted scrolling and establishes a scroll container.
Scroll anchoring is not implemented.

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
each gap) on block and inline-block containers. Text, inline elements and
block children flow into balanced columns; `direction: rtl` reverses the
column order. Lines can fragment across columns, while `break-inside: avoid`
keeps a block together. `break-before: column` forces a new column.
Direct children with `column-span: all` separate independently balanced sets.

Limitations: `column-fill: auto`, nested spanner extraction, authored
`orphans`/`widows`, and per-fragment backgrounds/borders are not implemented.
A fragmented block's decorations cover the union of its fragments. Explicit
container heights do not switch to sequential column filling.

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
`writing-mode: vertical-rl | vertical-lr` lays out orthogonal flows inside a
horizontal document and rotates glyph geometry into the vertical flow. Full
mixed upright/sideways glyph orientation is not implemented. The core uses
ICU for mixed-direction ordering, including `unicode-bidi` embedding,
override, isolate and plaintext behavior; glyph shaping remains host-owned.

## Floats & tables

`float: left/right` with `clear` and per-paragraph exclusion is implemented.
Runtime
tables exist including collapsed-border winner resolution; advanced
fragmentation is out of v1.

---

Next: [CSS Visual](css-visual.md) · [CSS Text](css-text.md)
