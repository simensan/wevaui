# CSS — Visual

[← Back to index](index.md) · [← Supported CSS](supported-css.md)

Box decoration and effects: backgrounds, borders, shadows, gradients, filters,
and masks.

## Backgrounds

- `background-color`.
- `background-image`: `url(...)`, `linear-gradient(...)`,
  `radial-gradient(...)`, `conic-gradient(...)`, `image-set(...)`, and
  `cross-fade(<image>, <image>, <percentage>?)` (Chrome's two-image form,
  `-webkit-` alias included). Operands mix urls, gradients, and colors —
  note that gradient operands are an engine EXTENSION: stable Chrome only
  accepts url() images in cross-fade and treats gradient operands as an
  invalid image.
- `background-size`, `background-position`, `background-repeat`,
  `background-clip` (including `background-clip: text` for gradient text — see
  [Text & Fonts](text-and-fonts.md)).
- `background-blend-mode`: all 15 `<blend-mode>` keywords, comma-list with
  per-layer cycling. Blends element-locally per CSS Compositing 1 §9 — each
  image layer against the element's own `background-color` (the compositing
  base), in sRGB space, matching Chrome pixel-for-pixel. v1 note: a non-normal
  layer above another *image* layer blends against the background-color only
  (the lower image's pixels are not sampled).
- Gradient color interpolation defaults to **sRGB** (the spec default is
  oklab); pass `in oklab` explicitly if you need oklab mid-stops.

`background-position` / `background-size` / `background-repeat` are honored on
gradient layers too (the gradient box is the background positioning area sized
by `background-size`, per CSS Images 3): `no-repeat` clips outside the tile,
`repeat` wraps. Scope note: conic/radial and ≥5-stop linear gradients keep the
full-box fill for now — their instance channels carry gradient data.

## Borders

`border-*` longhands and shorthand. `border-style`: `solid`, `dashed`,
`dotted`, `none` (other styles map to `solid` with a warning). Per-corner
`border-radius`, including the elliptical slash form. `border-image` with
sprite-border-derived slices (9-slice) is supported.

## Effects

- `opacity`.
- `box-shadow` with spread and `inset`. (Heavy multi-shadow boxes are among the
  most expensive painters.)
- `transform`: `translate(x,y)`, `translateX/Y`, `scale(s)`, `scale(sx,sy)`,
  `rotate(deg)`, `skew(...)`, `matrix(...)`, plus the `translate`/`rotate`/
  `scale` longhands and `transform-origin`. 3D-transform properties
  (`perspective`, `transform-style`, `backface-visibility`) cascade but have no
  visible effect — there is no 3D paint path.
- `filter`: `blur()`, `brightness()`, `contrast()`, `grayscale()`, `opacity()`,
  `saturate()`, `hue-rotate()`, `invert()`, `sepia()`, `drop-shadow()`. SVG
  `url(#id)` filter references are parsed and skipped.
- `backdrop-filter`: blurs/adjusts the content behind the element. The backdrop
  copy is refreshed from the current color target before each composite so it
  includes earlier-painted UI in the same frame.
- `clip-path`: basic shapes — `inset()`, `circle()`, `ellipse()`, `polygon()`
  (with `nonzero`/`evenodd`), `xywh()` (full `round` border-radius shorthand
  including the `/` elliptical form), `path()` with full SVG 1.1 path
  data, and `shape()` (CSS Shapes 2 — `from` + line/curve/arc/close
  commands with `by`/`to` and CSS `<length-percentage>` coordinates).
  Both `path()` and `shape()` are GPU-clipped via a rasterized coverage
  mask. Missing: `url(#…)` to SVG sources.
- `mask` / mask layers: solid and gradient masks composite correctly
  (alpha/luminance). URL-sourced masks resolve geometry but the software path
  doesn't sample source pixels; the URP path uploads the first 4 mask layers.

## Color & compositing notes

- All color math runs in **linear** space (the project color space). `color`,
  gradients, and interpolation use the proper IEC 61966-2-1 sRGB curve.
- `mix-blend-mode`: all values blend on the GPU against everything painted
  before the element — 3D scene, post-FX, and same-frame UI (the backdrop
  copy refreshes per blend batch) — in sRGB, matching Chrome. Remaining gap:
  `isolation: isolate` and opacity/filter-induced isolated groups don't bound
  the blend yet (the backdrop is always "everything so far").
- A documented GPU-side gap (**CHIP-LOWALPHA**): sub-~10% alpha fills on
  rounded-corner rects can fail to register visibly. Use ≥10% alpha for visible
  tints.

---

Next: [CSS Text](css-text.md) · [Text & Fonts](text-and-fonts.md)
