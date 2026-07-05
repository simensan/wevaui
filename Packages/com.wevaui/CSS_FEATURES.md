# Weva — CSS Feature Support Matrix

This document lists every CSS feature the engine recognises and the v1 simplifications / known unsupported sub-features. It is the canonical reference for what authors can rely on, and what to avoid.

Three support levels:

- **✅ Supported** — fully implemented and tested.
- **⚠ Partial** — recognised by the parser and laid out / painted with documented limitations.
- **❌ Unsupported** — either parser-only (no effect) or completely absent.

Last reviewed against `Packages/com.wevaui/Runtime/Css/` and `Packages/com.wevaui/Runtime/Layout/`.

---

## 1. Selectors

| Selector | Status | Notes |
|----------|--------|-------|
| Type (`div`) | ✅ | |
| Class (`.x`) | ✅ | |
| ID (`#x`) | ✅ | |
| Universal (`*`) | ✅ | |
| Attribute `[attr]` `[attr=v]` `[attr~=v]` `[attr\|=v]` `[attr^=v]` `[attr$=v]` `[attr*=v]` | ✅ | |
| Descendant (` `) child (`>`) adjacent (`+`) general sibling (`~`) | ✅ | |
| `:hover` `:focus` `:active` `:checked` `:disabled` `:empty` `:root` | ✅ | |
| `:first-child` `:last-child` `:only-child` `:first-of-type` `:last-of-type` `:only-of-type` | ✅ | |
| `:nth-child(an+b)` `:nth-last-child` `:nth-of-type` `:nth-last-of-type` | ✅ | |
| `:not()` `:is()` `:where()` `:has()` | ✅ | |
| `:focus-visible` `:focus-within` `:placeholder-shown` | ✅ | |
| `:popover-open` `:modal` | ✅ | |
| `::before` `::after` `::backdrop` | ✅ | `::backdrop` paints for open `<dialog>`. |
| `::first-line` `::first-letter` | ❌ | Not implemented. |
| `::placeholder` | ✅ | Cascade computes `::placeholder` style; `InputRenderer` applies color/opacity. |
| `::marker` | ✅ | Dedicated pseudo-element in cascade (not `::before` synthesis); `ComputeMarker()` feeds box builder. |
| `::selection` | ✅ | Cascade computes `::selection` style; `InputRenderer` uses background-color + color for selection paint. |
| `[dir=rtl]` matching | ⚠ | Selector matches; RTL layout is partial — see §13 / §17. |

---

## 2. Box model

| Property | Status | Notes |
|----------|--------|-------|
| `box-sizing` (`content-box`, `border-box`) | ✅ | `content-box` default; table cells get content-box default. |
| `margin`, `margin-*` | ✅ | Vertical margin collapsing per CSS 2.1. |
| `padding`, `padding-*` | ✅ | |
| `border`, `border-*-width` `border-*-style` `border-*-color` | ✅ | |
| `border-radius` (4 corners + elliptical) | ✅ | |
| `border-image` (source, slice, width, outset, repeat) | ✅ | |
| `outline`, `outline-*` | ✅ | Does not affect layout. `outline: invert` falls back to `currentColor` — see [#outline-invert]. |
| `box-shadow` (multi-shadow, inset, spread) | ⚠ | Animated `box-shadow` shows rectangular glow artifact — engine bug #226. |
| `mix-blend-mode` | ✅ | Registered in `CssProperties.cs`; GPU blend (`MixBlendMode` + `MixBlendModeResolver` + Push/PopMixBlendModeCommand) against everything painted before it. Isolation-bounding gap remains. |

---

## 3. Sizing

| Property | Status | Notes |
|----------|--------|-------|
| `width` `height` (length, %, `auto`) | ✅ | |
| `min-width` `min-height` `max-width` `max-height` | ✅ | Honored in shrink-to-fit for abs-pos. |
| `aspect-ratio` (`<number> / <number>`, `auto`) | ✅ | Multi-pass derivation with flex/grid interaction (bugs #234, #237). |
| `min-content` `max-content` `fit-content()` keywords | ⚠ | Recognised in grid track sizing. In `width`/`height` they are treated like `auto`. |

---

## 4. Positioning

| Property | Status | Notes |
|----------|--------|-------|
| `position: static` `relative` `absolute` `fixed` | ✅ | |
| `position: sticky` | ⚠ | Basic offsetting; scroll-tracking is limited. |
| `top` `right` `bottom` `left` `inset` | ✅ | |
| `z-index` (auto, int) | ✅ | Stacking contexts honored. |
| `float: left` `right` `none` | ⚠ | Float box layout is supported; **zero dedicated test coverage**. |
| `clear: left` `right` `both` `none` | ⚠ | Same as `float`. |
| `shape-outside` | ❌ | |

### Anchor positioning

| Feature | Status | Notes |
|---------|--------|-------|
| `anchor-name`, `position-anchor` | ✅ | |
| `anchor()` function | ✅ | |
| `position-try-fallbacks` (flip-block, flip-inline) | ❌ | Parsed but not evaluated (v2). |

---

## 5. Display modes

| `display` value | Status | Notes |
|-----------------|--------|-------|
| `block` `inline` `inline-block` `flow-root` `none` | ✅ | |
| `flex` `inline-flex` | ✅ | CSS Flexbox L1 — see §6. |
| `grid` `inline-grid` | ✅ | CSS Grid L1 + subgrid — see §7. |
| `table` `inline-table` `table-row-group` `table-header-group` `table-footer-group` `table-row` `table-cell` `table-caption` | ✅ | CSS 2.1 — see §8. |
| `contents` | ✅ | `BoxBuilder` hoists children, generates no box for the element. |
| `list-item` | ✅ | `BoxBuilder` generates the list-item marker. |
| `ruby` | ❌ | |

---

## 6. Flex (`display: flex`)

All eight flex properties implemented:

`flex-direction` (row, row-reverse, column, column-reverse) · `flex-wrap` (nowrap, wrap, wrap-reverse) · `flex-basis` · `flex-grow` · `flex-shrink` · `justify-content` (flex-start, flex-end, center, space-between, space-around, space-evenly, start, end, normal) · `align-items` (flex-start, flex-end, center, stretch, baseline) · `align-self` · `align-content` · `gap` `row-gap` `column-gap` · `order`.

**v1 simplifications:**
- `min-content` / `max-content` / `fit-content` keyword sizes in `flex-basis` are treated as `auto`.
- Item min/max main-size constraints resolved in a single pass after grow distribution (#246) — spec mandates iterative freeze-and-redistribute.
- Percentage `flex-basis` resolves against the container's resolved main size with no definite/indefinite distinction.
- Text directly inside a flex container is wrapped in the normal anonymous-block flow rather than an anonymous flex item.
- `align-self: stretch` only stretches when the item has no explicit cross-axis dimension set.
- Inline-flex shrink-to-fit (#254): main-axis collapses to sum of item extents + gaps + frame, single-line single-pass (no multi-line shrink-to-fit).

---

## 7. Grid (`display: grid`)

All sixteen grid properties implemented. Subgrid is supported via `display: subgrid`.

- `grid-template-columns` / `grid-template-rows` with length, %, `fr`, `min-content`, `max-content`, `fit-content()`, `repeat()` including `auto-fit` and `auto-fill`.
- `grid-template-areas` with ASCII art.
- `grid-auto-flow` (row, column, dense).
- `grid-auto-columns` / `grid-auto-rows`.
- Explicit placement (`grid-column-start/end`, `grid-row-start/end`, `grid-area`) with `span` syntax.
- Alignment: `justify-items`, `justify-self`, `align-items`, `align-self`, `justify-content`, `align-content`, `place-*` shorthands.

**v1 simplifications:**
- `subgrid` track inheritance is computed but not all CSS Grid L2 edge cases honored.
- 1fr column min-content does not consider aspect-ratio in deeply nested grid items (#236).

---

## 8. Table (`display: table`)

CSS 2.1 separated-borders model:

- All `display` table sub-values are recognised.
- Both `border-collapse: separate` and `collapse` are supported. Collapse implements CSS 2.2 §17.6.2.1 border conflict resolution (`CollapsedBorderWinnerResolver`), wired into `BoxToPaintConverter` + `TableLayout`, with a table-edge-border approximation.
- `border-spacing` (one or two lengths).
- Table cell `width` is treated as content-box (CSS 2.1 default) — bug #239.
- Row groups (thead/tbody/tfoot) are properly sized and positioned (#238).

**v1 simplifications:**
- `border-collapse: collapse` uses a table-edge-border approximation (per `CollapsedBorderWinnerResolver`).
- `<col>` / `<colgroup>` width hints collected and fed into column track resolution.
- `vertical-align` on cells is always treated as `top`.
- `rowspan`/`colspan` cells span visually; spanned column widths are summed including border-spacing, row heights are redistributed.

---

## 9. Cascade, inheritance, `@`-rules

| Feature | Status | Notes |
|---------|--------|-------|
| Specificity, `!important`, origin order (UA → User → Author) | ✅ | |
| Custom properties (`--name`, `var(--name, fallback)`) | ✅ | Inline styles supported. |
| `attr()` in `content` and other properties | ✅ | |
| `light-dark()` function | ✅ | |
| `@media` | ✅ | Features: width, height, aspect-ratio, orientation, resolution, prefers-color-scheme, prefers-reduced-motion, hover, pointer. |
| `@container` (`container-type`, `container-name`) | ✅ | Size and inline-size types. |
| `@keyframes` | ✅ | |
| `@font-face` | ⚠ | Consumed at runtime via `FontResolver.RegisterFontFace`: family + first `url()` src + weight-range + italic/style are honored. Finer descriptors (`unicode-range`, `font-display`, `format()`/`local()`) are parse-only. |
| `@import` | ✅ | With media. |
| `@layer` | ⚠ | Named/anonymous layer ordering and `!important` inversion implemented. Dotted sub-layers (`@layer base.utilities`) are flattened to a single name — no CSS Cascade 5 §6.4.2 hierarchy. |
| `@scope` | ✅ | Basic selector scoping. |
| `@supports` | ✅ | `SupportsEvaluator` gates `CompileRuleNested` — evaluates property declarations, boolean `not`/`and`/`or`, nested groups, and `selector(...)`. Unsupported properties correctly report false. |
| `@property` | ❌ | Parsed for forward-compat; no registration enforced. |
| `@page` | ❌ | Not applicable (no paged media). |

### `@media` feature gaps

`prefers-contrast`, `prefers-transparency`, `dynamic-range`, `color-gamut` not evaluated.

---

## 10. Values & functions

| Family | Status | Notes |
|--------|--------|-------|
| Lengths (`px`, `em`, `rem`, `%`, `vw/vh/vmin/vmax`, `ch`, `ex`, `cm/mm/in/pt/pc`) | ✅ | |
| Dynamic viewport units (`dvh`, `lvh`, `svh`) | ✅ | |
| Colours: hex, named, `rgb()`, `rgba()`, `hsl()`, `hsla()`, `hwb()`, `lab()`, `lch()`, `oklab()`, `oklch()`, `color()`, `color-mix()` | ✅ | sRGB, sRGB-linear, OkLab, OkLch, HSL, HWB interpolation spaces. |
| `currentColor`, `transparent` | ✅ | |
| `calc()` | ✅ | |
| `min()` `max()` `clamp()` | ✅ | |
| `var()` with fallback | ✅ | |
| `url()` | ✅ | |
| Linear / radial / conic gradients | ✅ | Multi-stop, repeating gradients. |
| Angles (`deg`, `rad`, `turn`, `grad`) | ⚠ | Angles in `transform` go through the legacy string parser; other angle-using properties use the typed parser. See [#angle-types]. |

---

## 11. Transforms

`translate()` `translateX/Y` · `rotate()` · `scale()` `scaleX/Y` · `skew()` `skewX/Y` · `matrix()` · `transform-origin` (keywords + %/px, fixed in #248).

3D transforms (`rotate3d`, `translate3d`, etc.) parse but project to 2D — no perspective.

---

## 12. Filters & effects

| Feature | Status | Notes |
|---------|--------|-------|
| `filter` (`blur`, `brightness`, `contrast`, `grayscale`, `hue-rotate`, `invert`, `opacity`, `saturate`, `sepia`, `drop-shadow`) | ⚠ | All ten functions parse and run; under batched URP there is a known compositing bug — engine bug #228. |
| `backdrop-filter` | ⚠ | `blur()` and color-filter chains render via URP temp-RT composite; exact edge expansion for blur and `drop-shadow()` backdrop remain partial. |
| `mask` (image, mode, position, repeat, size, origin, clip, composite) | ⚠ | Multi-layer alpha/luminance masks with gradient and URL sources render in the batched URP path (up to 4 layers). `mask-composite` operators (add, subtract, intersect, exclude) work. URL masks use resolved geometry but the software rasterizer does not sample source pixels. |
| `clip-path` (inset, circle, ellipse, polygon) | ⚠ | Basic shapes clip per-fragment on GPU with anti-aliasing (inset/circle/ellipse) or hard coverage (polygon). `path()`, `shape()`, `xywh()`, SVG clip sources, and fill-rule parity beyond even-odd for polygon are missing. |
| `mix-blend-mode` | ✅ | GPU blend against everything painted before it (`MixBlendMode` + resolver + Push/PopMixBlendModeCommand). Isolation-bounding gap remains. |
| `isolation: isolate` | ✅ | Creates a stacking context. |

---

## 13. Text & fonts

| Feature | Status | Notes |
|---------|--------|-------|
| `font-family` (with fallbacks) | ✅ | |
| `font-size` (length, %, `em`/`rem`) | ✅ | |
| `font-weight` (100–900, `normal`, `bold`) | ✅ | |
| `font-style` (`normal`, `italic`, `oblique`) | ✅ | |
| `font-variation-settings` | ✅ | Variable font axes (`"wght"`, `"opsz"` etc.). |
| `font-optical-sizing` | ✅ | |
| `font-variant: small-caps` | ⚠ | Parsed; not rendered. |
| `font-feature-settings` | ⚠ | Parsed; only opt-in features applied via TMP. |
| `line-height` (`normal`, number, length, %) | ✅ | |
| `letter-spacing` | ⚠ | Measured as `N × spacing` instead of `(N-1) × spacing` per CSS Text 3 §10.1 — engine bug #262. |
| `word-spacing` | ✅ | Applies to U+0020 ASCII space per CSS Text 3 §10.2. Other space-class codepoints (U+00A0, etc.) not yet recognized. |
| `text-align` (`left`, `right`, `center`, `justify`) | ✅ | |
| `text-transform` (`none`, `uppercase`, `lowercase`, `capitalize`) | ✅ | |
| `white-space` (`normal`, `pre`, `pre-wrap`, `nowrap`) | ✅ | |
| `word-break` (`normal`, `break-all`, `keep-all`) | ✅ | |
| `overflow-wrap` / `word-wrap` (`normal`, `break-word`) | ✅ | |
| `text-overflow: clip \| ellipsis` | ✅ | |
| `text-decoration` (line, style, color, thickness, underline-offset) | ✅ | |
| `text-shadow` | ✅ | |
| `-webkit-text-stroke` | ⚠ | Stroke painted as phantom under fill; not a true stroke primitive. |
| `text-indent` | ✅ | Length/percentage first-line indent applied during inline layout. |
| `text-align-last` | ✅ | RTL-aware; `auto` defers to `text-align`; `justify` last-line uses opposite-side alignment. |
| `text-justify` | ❌ | |
| `hyphens` | ⚠ | `manual` works (soft-hyphen U+00AD breaking); `auto` (dictionary) not implemented. |
| `direction`, `unicode-bidi` (RTL) | ⚠ | `direction` drives logical-property aliasing, flex-row reversal, and `text-align: start/end`. No bidi reordering or complex shaping. |
| `quotes` | ❌ | |
| `orphans`, `widows` | ❌ | No paged media. |

---

## 14. Background & images

`background` shorthand · `background-image` (url, linear/radial/conic gradient, multi-layer) · `background-color` · `background-position` · `background-size` · `background-repeat` · `background-clip` · `background-origin` · `background-attachment` · `object-fit` · `object-position` · `image-rendering`.

---

## 15. Animations & transitions

All animation and transition longhands + shorthands supported. Timing functions: `linear`, `ease`, `ease-in`, `ease-out`, `ease-in-out`, `cubic-bezier(x1,y1,x2,y2)`, `steps(N, jump-…)`. Keyframes use `%` offsets or `from`/`to`.

---

## 16. Overflow & scrolling

| Property | Status | Notes |
|----------|--------|-------|
| `overflow`, `overflow-x`, `overflow-y` (`visible`, `hidden`, `scroll`, `auto`) | ✅ | |
| `scroll-snap-type`, `scroll-snap-align` | ⚠ | Basic snap behavior. **No test coverage.** |
| `scroll-behavior: smooth` | ⚠ | Animation-based smooth scroll. **No test coverage.** |
| `overscroll-behavior*` | ❌ | Parsed but no enforcement. |
| `scrollbar-width`, `scrollbar-color`, `scrollbar-gutter` | ❌ | Parser-only. |
| `overflow-clip-margin` | ❌ | |
| `resize` | ❌ | |

---

## 17. Other properties

| Property | Status | Notes |
|----------|--------|-------|
| `opacity`, `visibility`, `z-index` | ✅ | |
| `cursor` | ⚠ | CSS 2.1 keywords parsed; engine emits hint for host. **No behavior test coverage.** |
| `pointer-events: auto \| none` | ✅ | |
| `user-select` | ❌ | Parser-only. |
| `caret-color`, `accent-color` | ✅ | |
| `contain`, `will-change` | ❌ | Parser-only — **no containment enforcement**. |
| `list-style-type` | ✅ | Counter Styles 3 §6 predefined identifiers: `disc`, `circle`, `square`, `decimal`, `decimal-leading-zero`, `lower-/upper-roman` (1..3999), `lower-/upper-alpha` (alias `latin`), `none`. |
| `list-style-position` | ⚠ | Longhand round-trips through the cascade (`inside`/`outside`); both render in-flow as the first inline-block child — no negative-margin outside-positioning pass. |
| `list-style-image` | ✅ | URL replaces the text glyph; marker BlockBox stores `ListMarkerImage` for the paint pass. |
| `content` | ✅ | Used by `::before`/`::after`. |
| `tab-size` | ✅ | Number (space count) and length forms; tab expansion in `LineBreaker`. |

---

## Coverage gaps that warrant test work

Empty / thin test categories (1700+ existing tests across the engine but these are weak):

1. **Floats** — engine supports `float`/`clear` but has zero dedicated tests.
2. **Table layout** — only 5 tests for 7 display modes plus border-spacing, content-box widths, row-group positioning.
3. **Sizing edge cases** — only 30 tests covering min/max-width/height interactions with flex, grid, aspect-ratio.
4. **Transforms** — 21 tests for 8 functions + transform-origin + composition.
5. **scroll-behavior / scroll-snap** — parser tests only.
6. **`pointer-events`** — covered for events, not for `pointer-events: none/auto` on layout-affecting properties.
7. **`clip-path`** — GPU clipping implemented but polygon fill-rule parity and advanced shapes (`path()`, `xywh()`) untested.
8. **`mask`** — multi-layer rendering exists but URL mask pixel sampling and edge cases need coverage.

The unsupported (❌) features above intentionally remain out of scope for v1.

---

## Open engine bugs (for cross-reference)

These behaviours are correct on input but render incorrectly on output. They are tracked in the task queue:

- **#226** Animated `box-shadow` shows a rectangular glow artifact.
- **#227** STRIKE / BLOCK glyph rendering ~20% smaller than Chrome (text-metric).
- **#228** `filter` RT-composite drops content at non-identity values under batched URP.
- **#235** Portrait-glyph centering 41px off horizontally (text-metric).
- **#236** Grid `1fr` track min-content does not honor nested aspect-ratio items.

[#outline-invert]: see CssProperties.cs comment on `outline-color` initial value.
[#angle-types]: see TransformResolver.cs preamble.
