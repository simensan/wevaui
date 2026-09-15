# Supported CSS

[← Back to index](index.md)

Property names and values match the CSS spec exactly — no prefixes, no
`-unity-` renames. Both parsers (tokenizer and rule/property grammar) are
hand-rolled for full control over the supported subset and error reporting.

This page covers selectors, the cascade, values/units, and at-rules. The
property surface is split across three sub-pages:

- [CSS Layout](css-layout.md) — `display`, box model, flexbox, grid,
  positioning, overflow/scrolling, logical axes.
- [CSS Visual](css-visual.md) — backgrounds, borders, gradients, shadows,
  `opacity`, `transform`, `filter`, `clip-path`, masks.
- [CSS Text](css-text.md) — fonts, `line-height`, alignment, decoration,
  `white-space`, wrapping, ellipsis.

These pages describe the current core's authoring surface, including
[balanced multi-column layout](css-layout.md#multi-column).
[`CSS_FEATURES.md`](../CSS_FEATURES.md) is a historical matrix for the retired
C# engine; its implementation references and status rows are not current.

## Selectors

| Type | Examples |
|---|---|
| Simple | `*`, `tag`, `.class`, `#id` |
| Attribute | `[name]`, `[name=val]`, `[name~=val]`, `[name^=val]`, `[name$=val]`, `[name*=val]` |
| Combinators | descendant (space), child `>`, adjacent `+`, general sibling `~` |
| Structural pseudo | `:first-child`, `:last-child`, `:only-child`, `:nth-child(an+b)`, `:nth-of-type`, `:empty`, `:not(...)`, `:is(...)`, `:where(...)`, `:has(...)`, `:lang(...)`, `:dir(...)` |
| State pseudo | `:hover`, `:focus`, `:focus-visible`, `:focus-within`, `:active`, `:link`, `:visited`, `:any-link`, `:target`, `:scope`, `:disabled`, `:enabled`, `:checked`, `:default`, `:required`, `:optional`, `:valid`, `:invalid`, `:user-valid`, `:user-invalid`, `:in-range`, `:out-of-range`, `:read-only`, `:read-write`, `:placeholder-shown`, `:autofill`, `:root` |
| Pseudo-elements | `::before` / `:before`, `::after` / `:after`, `::placeholder`, `::selection`, `::backdrop`, `::marker` |

Specificity follows the spec; `!important` is honored. State pseudo-classes
flip automatically off event-driven interaction state — no controller code.
`:visited` parses but never matches (`IsVisited` is hard-coded `false` — there
is no browsing-history store).

**Known divergence:** the `of <selector>` filter is honored for `:nth-child` /
`:nth-last-child` (via `SelectorParser` + `FilteredChildIndex`) but dropped for
`:nth-of-type` / `:nth-last-of-type` — there the index counts all matching-type
siblings. Use a more specific selector instead.

## Cascade & inheritance

- Real selector matching, specificity sorting, and source order.
- `inherit`, `initial`, `unset` resolve per spec.
- `revert` / `revert-layer` roll back to the appropriate lower-priority match
  across origin and layer (up to 4 chained hops; `!important` inversion is not
  honored in v1).
- `var(--name, fallback)` with cycle detection; a cycle member refuses its own
  definition-level fallback (consumer-side fallback still rescues).
- **Cascade layers** (`@layer`) and **nested rules** (`& > .child`, `.child`,
  `&.active`, `.ancestor &`). Nested selectors use their parent's selector
  list and its maximum specificity, as `:is(...)` does. Declarations may
  appear between nested rules and inside nested media/supports/layer/container
  groups. Declaration runs retain their parent's pseudo-elements and individual
  specificities. Expanded selector text is bounded to 64 KiB per selector;
  rules beyond that bound are dropped, as with the parser's 128-rule depth cap.
- **Shorthand expansion** for `font`, `background`, `border`, `border-radius`,
  `flex`, `flex-flow`, `gap`, `inset`, `margin`/`padding`, `list-style`,
  `animation`, the logical box/border shorthands, and `all`.

## Values & units

- **Lengths:** `px`, `em`, `rem`, `%`, `vw`, `vh`, `vmin`, `vmax`, and the
  other standard absolute/relative units. `1px` = 1 logical pixel; `rem`/`em`
  derive from a 16px base.
- **Functions:** `calc()`, `min()`, `max()`, `clamp()` — full arithmetic over
  lengths, percentages, and numbers. `attr()` typed forms work in any
  property-value context (substituted at computed-value time like `var()`,
  including inside `calc()`, through `var()` indirection, and in shorthands),
  `env()` (the host supplies per-document safe-area inset values through the
  native API; the retired `EnvironmentVariables.Register` C# API is unavailable).
- **Colors:** `#hex`, `rgb()`/`rgba()`, `hsl()`/`hsla()`, `hwb()`, `lab()`,
  `lch()`, `oklab()`, `oklch()`, `color()`, named colors, `currentColor`,
  `color-mix()`, and `light-dark()`. Modern RGB syntax permits mixed numbers
  and percentages; comma syntax requires a consistent channel type.
  `color-mix()` uses the requested interpolation space. Output is reduced
  to sRGB bytes; wide-gamut colors are clipped rather than gamut-mapped.
- **Container query units** (`cqw`, `cqi`, …) are *not* registered — use
  viewport units or explicit lengths.

## At-rules

- `@import "path.css"` — fetched through the document's asset reader.
  Nested imports resolve from the importing stylesheet's directory; cycles
  are stopped and missing files appear in CSS diagnostics. Named/anonymous
  `layer`, `supports()` and media qualifiers wrap the imported rules.
  Imported image/font URLs retain that stylesheet's origin, including image
  URLs substituted from variables at their use site.
- `@font-face` — family, ordered `url()`/`local()` sources, weight and style
  are exposed to the host's font loader. URLs resolve from the declaring
  stylesheet. Stretch, Unicode ranges and font-display do not currently
  influence font selection or loading.
- `@media` — full feature set: `width`/`height`, `orientation`,
  `aspect-ratio`, `resolution`, `prefers-color-scheme`,
  `prefers-reduced-motion`, `hover`, `pointer`, with `and`/`or`/`not`.
  Evaluated against the UI surface, not the OS window.
- `@container` — `container-type: inline-size | size`, named and unnamed,
  width/height/inline-size/block-size/orientation/aspect-ratio features, range
  form (`width >= 320px`), `style(--prop)` queries, nested conditions, and
  `and`/`or`/`not`. A fresh `container-type` assignment may take 1–2 frames to
  settle (the v1 chicken-and-egg: it reads layout-after-previous-cascade size).
- `@keyframes` — see [Animations & Transitions](animations-transitions.md).
- `@supports` — reports Weva's actual support.
- `@scope` (CSS Cascade Level 6) — root/limit lists, nested scope chains,
  relative selectors, `:scope`/`&`, and proximity after specificity. An
  omitted root uses the inline `<style>` parent's subtree; host-supplied
  stylesheets without an owner use the document element.
- `@property` — descriptor parsing with `inherits` / `initial-value` /
  `syntax`; `initial-value` containing `var()`/`env()` is rejected per spec.
- `@layer` — cascade layers.
- `@namespace` — parses but no XML namespace map (prefixed selectors match on
  the local name).

## Known divergences from Chrome

These are intentional or phase-scoped known v1 simplifications.

- **Bundled default font is Inter (SIL OFL), not Arial — intentional.** Bare
  `sans-serif` resolves to it (Segoe UI is proprietary and can't ship); on
  Windows, naming `"Segoe UI"` uses the user's installed copy. Inter's
  `normal` line-height differs slightly from Chrome's Arial, so uniform y-shifts
  vs. Chrome are accepted divergence, not bugs. See
  [Text & Fonts](text-and-fonts.md).
- **`box-sizing` default is `content-box`** (the CSS initial value). Use
  `* { box-sizing: border-box }` for the common reset.
- **`line-height: normal`** resolves taller than Chrome for the same font-size
  (≈1.21× for bundled Inter's actual `normal` vs. Chrome's ≈1.143× for Inter —
  a consequence of the default-face metrics above).
- Layout-specific simplifications (`position: sticky` single-axis,
  `min-content`/`max-content` treated as `auto` in flex, etc.) are listed on
  [CSS Layout](css-layout.md).
- A set of properties parse cleanly but are intentionally inert or reduced —
  `isolation: isolate` (mix-blend always blends against everything painted
  so far; isolated groups aren't bounded yet), `animation-composition: add`,
  3D-transform properties, `font-feature-settings`, `font-variant-numeric`,
  `font-size-adjust`, `font-synthesis-*`, container-query units, and others.
  See [`AuthoringGuide.md`](AuthoringGuide.md) §17 for the full
  "parses-but-inert" and "partial-support" lists.

---

Next: [CSS Layout](css-layout.md) · [CSS Visual](css-visual.md) · [CSS Text](css-text.md)
