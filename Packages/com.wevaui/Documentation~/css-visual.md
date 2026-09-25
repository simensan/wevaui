# CSS — Visual

[← Back to index](index.md) · [← Supported CSS](supported-css.md)

Box decoration and effects: backgrounds, borders, shadows, gradients, filters,
and masks.

## Backgrounds

- `background-color`.
- `background-image`: `url(...)`, `linear-gradient(...)`,
  `radial-gradient(...)`, `conic-gradient(...)`, and repeating gradients.
  `image-set()` and `cross-fade()` are not painted by the current core.
- `background-size`, `background-position`, `background-repeat`, and
  `background-clip: text` for gradient text (see [Text & Fonts](text-and-fonts.md)).
  Other background origin/clip boxes are not implemented beyond the default
  border-box behavior.
- `background-blend-mode`: `normal`, `multiply`, `screen`, `overlay`,
  `darken`, `lighten`, `color-dodge`, `color-burn`, `hard-light`, `soft-light`,
  `difference`, and `exclusion`. Layers blend in sRGB against lower image
  layers and the element's background color. Short mode lists repeat.
  `hue`, `saturation`, `color`, and `luminosity` currently fall back to normal.
- Gradient interpolation uses premultiplied **sRGB**. An `in oklab` or other
  interpolation clause is parsed but does not change the gradient's space.
- `background-attachment: fixed` positions against the viewport;
  `local` moves with the element's scrolled content.

`background-position` / `background-size` / `background-repeat` are honored on
gradient layers too (the gradient box is the background positioning area sized
by `background-size`, per CSS Images 3): `no-repeat` clips outside the tile,
`repeat` wraps. These operations apply to linear, radial and conic gradients
through the shared background rasterizer.

## Borders

`border-*` longhands and shorthand. Ordinary box borders currently paint as
solid strips, with inset/outset color shading; `none` and `hidden` suppress them.
Dashed/dotted and other styled borders have specialized support in collapsed
tables, not ordinary boxes. Per-corner
`border-radius`, including the elliptical slash form. URL-backed
`border-image` uses CSS slice, width, outset, fill and repeat settings;
slice numbers refer to source-image pixels, not Unity Sprite border metadata.

## Effects

- `opacity` multiplies descendant paint alpha. It does not create an isolated
  compositing group, so overlapping translucent children can differ from Chrome.
- `box-shadow` with spread and `inset`. (Heavy multi-shadow boxes are among the
  most expensive painters.)
- `transform`: `translate(x,y)`, `translateX/Y`, `scale(s)`, `scale(sx,sy)`,
  `rotate(deg)`, `skew(...)`, `matrix(...)`, plus the `translate`/`rotate`/
  `scale` longhands and `transform-origin`. 3D-transform properties
  (`perspective`, `transform-style`, `backface-visibility`) have no 3D paint
  path; perspective can still establish a containing block for positioning.
- `filter`: `blur()`, `brightness()`, `contrast()`, `grayscale()`, `opacity()`,
  `saturate()`, `hue-rotate()`, `invert()`, `sepia()`, `drop-shadow()`.
  Color filters apply through the subtree. Blur currently affects the box's
  background, leaves its children sharp, and omits the box's border/shadows.
  Drop shadows use the border-box shape rather than the content's alpha shape.
  SVG `url(#id)` filter references are not implemented.
- `backdrop-filter`: blurs/adjusts the content behind the element. The backdrop
  copy is refreshed from the current color target before each composite so it
  includes earlier-painted UI in the same frame.
- `clip-path`: `inset()`, `circle()`, `ellipse()`, and `polygon()` clip the
  element and its descendants through core geometry. `xywh()`, `path()`,
  `shape()`, and SVG references are not implemented. Polygon fill-rule keywords
  are not distinguished, and rounded inset parsing supports only simple forms.
- `mask-image`: gradient and decoded URL layers can mask the background
  raster, with alpha/luminance coverage and per-layer size, position and repeat.
  This is partial masking: borders, text and descendants are not masked, and
  mask layers combine by addition. Use `clip-path` for supported subtree shapes.

## Color & compositing notes

- Core vertex colors are linear and image/gradient textures contain sRGB bytes.
  Unity's camera pass blends in linear space; offscreen pages and Godot's canvas
  blend in gamma space. Translucent edges can therefore differ between paths.
- Unity's `mix-blend-mode` provides GPU states for multiply, screen, darken and
  lighten; other modes draw normally. Godot uses multiply or additive
  approximations for a few modes. Neither host provides full Chrome blend-mode
  parity or isolated blend groups. This is separate from background blending,
  which the shared core rasterizes.

---

Next: [CSS Text](css-text.md) · [Text & Fonts](text-and-fonts.md)
