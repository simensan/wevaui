# Weva CSS Feature Audit

Updated: 2026-05-29 (font-kerning + image-set + font-stretch wdth)

This is the current engine-facing CSS support map. It distinguishes parser/cascade
coverage from actual layout/paint/render behavior; a registered property is not
automatically considered visually supported.

Legend:

- **Supported**: expected to behave like Chrome for the documented subset.
- **Partial**: implemented, but with known browser-parity gaps.
- **Parse-only**: accepted by parser/cascade, but not honored visually.
- **Missing**: not implemented; should warn, fail, or be ignored intentionally.
- **Won't fix (v1)**: explicitly out of scope for the runtime game-UI target — see PLAN.md.

## Intentionally Not Supported (Game UI Non-Goals)

The runtime target is HUD / menu / inventory UI for Unity games, not document
rendering. These features are intentionally outside v1 scope and probably v2 as
well. Authors should expect them to silently no-op or fail; bug reports against
them will be closed as won't-fix.

**Text:**
- `writing-mode: vertical-*` and any vertical-orientation features.
- Full bidi reordering and complex-script shaping (Arabic, Hebrew, Devanagari, CJK joining).
- Dictionary-driven `hyphens: auto` (only `manual` soft-hyphen works).
- `text-wrap: balance` / `pretty` / `stable` — document-grade quality features.
- `text-justify: inter-character` — CJK per-grapheme justification.
- `text-indent: hanging` / `each-line` — printing-press modifiers.
- `text-decoration-skip-ink`, `text-underline-position: under` — typography refinements.
- `::first-line`, `::first-letter` — magazine-style drop-cap pseudo-elements.

**Layout / fragmentation:**
- Multi-column layout (`columns`, `column-count`, `column-width`, `column-rule*`, `column-span`, `column-fill`).
- Page-break / fragmentation properties (`break-before`, `break-after`, `break-inside`).
- `@page` rule — printing.
- Float fragmentation across wrapped paragraphs with multiple float edges.
- Multi-axis sticky positioning (use script-side calculation instead).
- Scroll anchoring (`overflow-anchor`).

**Web platform features that don't apply to embedded Unity UI:**
- Web Components: `::part()`, `::slotted()`, `:host`, `:host-context()`.
- Form-control internals: `::file-selector-button`, `::-webkit-*` shadow-DOM pseudos.
- Media controls: `::cue`, `::cue-region` (WebVTT).
- Time-dimension pseudo-classes: `:current`, `:past`, `:future` (HTML5 media).
- `@charset`, `@namespace` — stylesheets always UTF-8, no XML namespacing.
- `background-attachment: fixed` against the viewport (cascade carries the value but viewport-fixed scrolling isn't a thing here).
- SVG inline filters (`filter: url(#id)` targeting inline `<svg><filter>`).
- `cross-fade()` and `image-set()` in image properties.
- `background-blend-mode`, full backdrop sampling for the software path.

**Color / paint refinements:**
- Per-side `border-image-source` (single source stretched across all sides only).
- `mask`-source pixel sampling in the software rasterizer (URL masks use geometry only).
- More than 4 mask layers in the batched URP path.

## Core Cascade

| Feature | Status | Notes |
|---------|--------|-------|
| Specificity, source order, `!important` | Supported | Includes cascade layer ordering. |
| CSS-wide keywords | Partial | `inherit`, `initial`, `unset` work; `revert` and `revert-layer` both fall back to `initial` (the spec rolls back to UA/user origin or lower layer respectively). |
| Custom properties | Supported | `--*`, inheritance, fallback, and cycle detection are implemented. |
| `var()` | Supported | Nested fallback resolution + invalid-at-computed-value semantics: the cascade calls `VariableResolver.TryResolve` and drops the whole declaration on unresolvable references (per CSS Custom Properties L1 §3), letting inheritance / initial values fill in. |
| `attr()` | Partial | Works in cascade/content paths covered by tests; full typed browser behavior remains limited. |
| `light-dark()` / `color-scheme` | Partial | Element `color-scheme` is registered and used for `light-dark()`; platform integration is still limited to `MediaContext`. |
| CSS nesting | Supported | `&` and bare nested selectors are flattened by `NestingExpander`. |

## At-Rules

| At-rule | Status | Notes |
|---------|--------|-------|
| `@import` | Supported | Resolves relative stylesheets through the loader. |
| `@media` | Supported | Width/height/aspect/orientation/resolution/color-scheme/reduced-motion/hover/pointer. |
| `@container` | Partial | Size queries work after layout has produced container boxes; nested container rules keep only the inner condition. |
| `@supports` | Supported | Evaluates property declarations, boolean `not`/`and`/`or`, nested groups, and `selector(...)`. Parse-only stubs report unsupported. |
| `@layer` | Supported | Named/anonymous layer ordering and important inversion are implemented. Dotted sublayers are flattened as one name. |
| `@scope` | Partial | Basic scope windows work; nested scopes keep only the innermost scope. |
| `@keyframes` | Supported | Drives `animation-*` through `CssAnimationRunner`. |
| `@font-face` | Partial | Minimal `font-family` + `src` bridge only. Missing descriptors: weight/style/stretch/unicode-range/display, multiple `src`, `local()`, `format()`. |
| `@property` | Missing | No typed custom property registration or interpolation metadata. |
| `@page`, `@charset`, `@namespace` | Missing | Not needed for current Unity UI surface. |

## Selectors

Supported:

- Type, universal, class, id.
- Attribute selectors: exists, equals, includes, dash-match, prefix, suffix, substring.
- Combinators: descendant, child, adjacent sibling, general sibling.
- Structural: `:first-child`, `:last-child`, `:only-child`, `:first-of-type`, `:last-of-type`, `:only-of-type`, `:empty`.
- Functional: `:nth-child`, `:nth-last-child`, `:nth-of-type`, `:nth-last-of-type`, `:nth-child(An+B of <selector-list>)` / `:nth-last-child(... of ...)` (Selectors L4 §6.6.5), `:not()`, `:is()`, `:where()`, `:has()`, `:lang()`, `:dir()`.
- State: `:hover`, `:focus`, `:focus-visible`, `:focus-within`, `:active`, `:link`, `:visited` (never matches without a host history provider), `:any-link`, `:target` for in-document fragments, `:disabled`, `:enabled`, `:checked`, `:default`, `:required`, `:optional`, `:valid`, `:invalid`, `:user-valid`, `:user-invalid`, `:in-range`, `:out-of-range`, `:read-only`, `:read-write`, `:placeholder-shown`, `:popover-open`, `:modal`, `:root`.
- Pseudo-elements: `::before`, `::after`, `::placeholder`, `::selection`, `::backdrop`, `::marker`.
- Scoping: `:scope`, `:host`, `:host(...)`.

Missing:

- Time/current-state pseudos: `:current`, `:past`, `:future`.
- Pseudo-elements: `::first-line`, `::first-letter`, `::file-selector-button`, `::part()`, `::slotted()`.
- (none currently)

## Values And Color

Supported:

- Length units: `px`, `em`, `rem`, `%`, viewport units, physical units, `ch`, `ex`.
- Grid-only `fr`.
- `calc()`, `min()`, `max()`, `clamp()`.
- Colors: named colors, hex, `rgb()`, `rgba()`, `hsl()`, `hsla()`, `hwb()`, `oklab()`, `oklch()`, `lab()`, `lch()`, `color()` in wide-gamut spaces (`srgb`, `srgb-linear`, `display-p3`, `rec2020`, `a98-rgb`, `prophoto-rgb`, `xyz` / `xyz-d65`, `xyz-d50`), and `color-mix()` in the implemented spaces. CSS Color L5 relative-color forms (e.g. `oklch(from var(--c) calc(l + 0.1) c h)`) are wired through `CalcChannelBindings` for `r`/`g`/`b`/`alpha`/`h`/`s`/`l`/`w`/`c`/`a` channels.

Missing or partial:

- Full typed `attr()` outside the currently supported cascade/content paths (CSS Values L4 §6.3 `attr(name <type>, <fallback>)`).

Supported math / variable functions:

- All Values L4 math: `calc()`, `min()`, `max()`, `clamp()`, `round(<strategy>, A, B)` with `nearest`/`up`/`down`/`to-zero`, `mod()`, `rem()`, `abs()`, `sign()`, `sqrt()`, `pow()`, `hypot()`, `log()`, `exp()`, trig (`sin`, `cos`, `tan`, `asin`, `acos`, `atan`, `atan2`) — see `CssCalc.cs`.
- `env(<custom-ident>[, <fallback>])` with the same invalid-at-computed-value-time semantics as `var()` — see `EnvResolver.cs` / `EnvironmentVariables.cs`. The host wires concrete values (`safe-area-inset-*`, `keyboard-inset-*`, etc.) into the registry.

## Layout

| Area | Status | Notes |
|------|--------|-------|
| Block flow | Supported | Includes margin collapse, shrink-to-fit, inline splitting, and inline-block coverage in current tests. |
| Inline layout | Partial | No bidi/complex shaping; no vertical writing; text decoration fragmentation is simplified. |
| Flexbox | Partial | Broad property support; known gaps remain for exact intrinsic sizing, some baseline cases, and explicit-vs-initial cascade edge cases. |
| Grid | Partial | Broad Level 1 support plus partial subgrid; intrinsic track sizing remains approximate in spanning cases. |
| Subgrid | Partial | Direct-child subgrids and chained lookup exist; no `grid-auto-rows: subgrid` or explicit extra tracks with `subgrid`. |
| Positioning | Partial | Absolute/fixed/sticky exist; sticky is simplified and scroll integration is incomplete. |
| Floats / clear | Partial | Registered and implemented for basic block flow, but not full browser float fragmentation. |
| Tables | Partial | Table boxes, row groups, captions, separate `border-spacing`, collapsed-spacing layout, basic vertical-align, `colspan`/`rowspan`, `col`/`colgroup` width hints, and fixed table layout exist. Missing collapsed border conflict resolution/painting. |
| Scrolling | Partial | Overflow, scroll containers, smooth scroll, and snap exist. Snap areas are direct-child only; scrollbar styling and overscroll are mostly round-trip/no-op. |
| Multi-column | Missing | `columns`, `column-count`, `column-width`, column rules, and fragmentation are absent. |
| Logical layout / RTL | Partial | `direction`, `writing-mode`, `unicode-bidi`, logical sizes/insets/box edges, `text-align: start/end`, `[dir]`, and RTL flex-row main-axis flipping exist. Vertical writing maps logical edges but does not shape vertical text. |
| Containment | Partial | `contain` is registered and can affect stacking-context decisions; actual layout/paint containment is not implemented. |
| `content-visibility` | Missing | No skip/layout containment behavior. |

## Paint And Rendering

Supported:

- Background color and images.
- Linear, radial, conic, repeating radial, and repeating conic gradients where covered by `BackgroundResolver`.
- Multi-layer backgrounds.
- Border radius and common border styles.
- Border image property surface.
- Box shadow and text shadow.
- Outlines.
- `opacity`, 2D transforms, transform origin.
- CSS `filter`: blur, brightness, contrast, grayscale, opacity, saturate, hue-rotate, invert, sepia, drop-shadow.
- `backdrop-filter`: samples the current URP color target and composites blur/color-filter chains inside the element border radius.
- `clip-path`: basic shapes `inset()`, `circle()`, `ellipse()`, and `polygon()` on the paint/render path.
- `mask-image`: solid/gradient masks and URL mask geometry, with layered `mask-mode`, `mask-repeat`, `mask-position`, `mask-size`, `mask-origin`, `mask-clip`, and `mask-composite`.
- `object-fit` / `object-position` for images.

Partial or missing:

- `clip-path`: only basic shapes are implemented; `path()`, `shape()`, `xywh()`, and SVG `url(#…)` clip sources are missing. `polygon()` honors the `nonzero` / `evenodd` keyword on both the CPU hit-test (`PolygonClipPathShape.Contains`) and the URP fragment shader (encoded into `ClipShape0.z`) — see `PolygonClipFillRuleTests`.
- `mask`: layered alpha/luminance compositing renders for normal UI counts. URL masks use resolved geometry but do not sample source pixels in the software rasterizer; the batched URP path uploads the first four mask layers.
- `backdrop-filter`: `blur()` and color filter chains render; exact edge expansion for blur and `drop-shadow()` backdrop behavior remain partial.
- `mix-blend-mode` / full blending and `isolation`: no complete blend pipeline.
- `background-blend-mode`, `cross-fade()` are missing.
- `image-set()` — **Supported.** `image-set(<url-or-string> [<resolution>]?, …)` resolves at paint time, picking the smallest candidate whose resolution ≥ the host DPR (LengthContext.DpiPixelsPerInch / 96), falling back to the highest available when none qualify. Accepts `x` / `dppx` / `dpi` / `dpcm` units and the `-webkit-image-set` alias. Per-layer `background-position` / `background-size` / `background-repeat` flow through unchanged. (`ImageSetResolver.cs`)
- `background-attachment: fixed/local` is carried by cascade but not full browser behavior.
- SVG filters and SVG/vector paint are missing.

## Text And Fonts

Supported:

- Font family/size/weight/style/stretch (variable-font `wdth` axis), line-height, letter/word spacing.
- `font-kerning: auto | normal | none` — `none` gates the SdfTextRunBaker's GetKern call so authors can disable kerning at the run level. `auto` and `normal` both kern when the font's pair table is wired.
- Text align, text-align-last, text-indent, text-wrap nowrap, transform, decoration, overflow ellipsis for single-line clipping.
- `tab-size` in preserved whitespace and manual soft-hyphen breaks via `hyphens: manual`.
- Text shadow and webkit text stroke.
- TMP and SDF font backends.
- Minimal `@font-face`.

Partial:

- `font-feature-settings` is registered but not fully wired into font feature tables.
- `font-variant-numeric` is registered/inherited; real shaping depends on backend support and is not browser-complete.

Parse-only (registered cascade-round-trip, paint/layout doesn't honor yet):
- `font-synthesis` / `-weight` / `-style` / `-small-caps` / `-position` — no faux-bold/italic/small-caps synthesis.
- `font-size-adjust` — metric-adaptive sizing deferred to v2.
- `line-clamp` / `-webkit-line-clamp` — **Supported.** Truncates IFC content after N lines and appends "…" to the Nth line. v1 relaxes the L4 `overflow:hidden` precondition; the trigger fires on the clamp value alone. Standard `line-clamp` wins when both forms are declared. (LineClampHelper.cs)
- `box-decoration-break` — only `slice` (default) honored; `clone` deferred.

Intentionally not supported (game UI non-goals — see PLAN.md):

- Vertical writing modes (`writing-mode: vertical-*`) — horizontal-tb only.
- Full bidi reordering and complex script shaping — Latin + simple scripts only.
- `text-wrap: balance` / `pretty` / `stable` — document-grade quality features; default `wrap` is fine for HUDs/menus.
- `text-justify: inter-character` — CJK per-grapheme justification.
- `text-indent: hanging` / `each-line` — printing-press features.
- Dictionary-driven `hyphens: auto` — only manual soft-hyphen (`&shy;`) supported.

## Animation

Supported:

- `transition-*` and shorthand.
- `@keyframes` and `animation-*`.
- Easing: linear/ease/ease-in/ease-out/ease-in-out, cubic-bezier, steps.
- Type-aware interpolation for key engine value kinds.

Missing or partial:

- Scroll-driven animations: `scroll-timeline`, `view-timeline`, `animation-timeline`.
- `animation-composition` / additive animation.
- Typed custom property animation via `@property`.
- Full cascade ordering for every animation-important edge case.

## Registered But Intentionally No-Op Or Diagnostic

| Property | Current behavior |
|----------|------------------|
| `scrollbar-width`, `scrollbar-color`, `scrollbar-gutter` | Registered for round-trip/layout hooks; visual scrollbar styling is not browser-complete. |
| `overscroll-behavior*` | Registered for round-trip; overscroll chaining behavior is not implemented. |
| `user-select` | Registered for author compatibility; selection behavior is not complete browser parity. |
| `will-change` | Registered mainly for stacking/perf hints; no compositor promotion model. |

## Engine Backlog Priority

1. Keep this audit and `CONFORMANCE.md` generated or checked from the actual property registry, shorthands, at-rule parser, selector parser, and renderer stubs.
2. Finish collapsed table border conflict resolution and painting.
3. Upgrade text shaping: bidi, complex scripts, OpenType feature application, kerning, numeric variants, and vertical glyph flow.
4. Expand remaining selector coverage: first-line/first-letter/part/slotted and time/current-state pseudos.
5. Add `@property` typed custom properties and use it for interpolation.
6. Add multi-column and fragmentation only after layout invalidation rules are pinned.
