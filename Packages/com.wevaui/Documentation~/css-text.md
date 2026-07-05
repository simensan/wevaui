# CSS — Text

[← Back to index](index.md) · [← Supported CSS](supported-css.md)

Typography properties and inline text behavior. Using your own fonts (and
emoji/symbols) is covered in [Text & Fonts](text-and-fonts.md); this page is the
CSS surface.

## Fonts

- `font-family`, `font-size`, `font-weight`, `font-style`, `font-variant`
  (small-caps).
- `letter-spacing`, `word-spacing`.
- `@font-face` registers families (see [Supported CSS](supported-css.md#at-rules)).

The **bundled default face is Inter** (SIL OFL), not Arial. Bare `sans-serif`
resolves to it; on Windows, naming `"Segoe UI"` uses the user's installed copy.
Inter's metrics diverge slightly from Chrome's Arial — intentional, accepted.

## Line box & spacing

- `line-height` — number, length, percentage, and `normal`. `normal` resolves
  taller than Chrome (≈1.21× vs. ≈1.143×) because of the default-face metrics;
  this is accepted divergence.
- `text-indent` — bare length/percentage honored; `hanging` / `each-line`
  modifiers parse but are inert.
- `tab-size`.

## Alignment & decoration

- `text-align`: `left`, `right`, `center`, `justify`, `start`, `end`.
- `text-align-last`.
- `text-decoration`: underline, overline, and line-through.
- `text-transform`: `uppercase`, `lowercase`, `capitalize`.
- `color` (the text fill; honored in linear space).
- `box-decoration-break: slice | clone` for inline boxes wrapping across
  lines — paint AND layout per CSS Fragmentation L3 §6.1. `slice` (default)
  suppresses decorations at break edges and reserves inline-axis
  padding/border/margin only on the first fragment's start and last
  fragment's end; `clone` decorates every fragment with full borders,
  radius, and background, and every fragment reserves both edges (wrap
  points shift exactly as in Chrome).

Out-of-flow (absolute/fixed) children no longer disturb inline alignment —
`text-align: center/right/justify` applies per CSS 2.1 §9.2.1.1 even when the
block also contains positioned decorations.

**Quotes**: the `quotes` property (`auto | none | [<open> <close>]+`) and the
`open-quote` / `close-quote` / `no-open-quote` / `no-close-quote` content
keywords work with document-order nesting depth; `<q>` carries Chrome's UA
rules. `auto` resolves to the English typographic pairs (locale-aware pairs
are a v1 simplification).

## White-space, wrapping & overflow

- `white-space`: `normal`, `nowrap`, `pre`, `pre-wrap`.
- `text-wrap: nowrap | wrap`. The `balance | pretty | stable` values parse but
  are inert.
- Long-word breaking: `word-break: break-all`, `overflow-wrap: break-word`,
  `overflow-wrap: anywhere`. `word-break: keep-all` falls back to `normal`
  (CJK is a v2 concern).
- Manual `hyphens` (soft-hyphen breaks) work; dictionary `hyphens: auto` is v2.
- `text-overflow: ellipsis` truncates single-line clipping containers
  (`overflow: hidden|scroll|clip|auto` + `white-space: nowrap`). Multi-line
  `line-clamp` ellipsis is v2.
- `text-justify: auto` and `inter-word` (the Latin default) are honored;
  `inter-character` (CJK) is inert.

## Inline formatting

A real inline formatting context lays out mixed-style runs
(`<span>`/`<strong>`/`<a>`/`<code>`) within a line, with correct baselines for
inline-block atoms. LTR only — no bidi reordering, no vertical writing-mode
glyph flow (both are v1 non-goals).

**Single-fragment decoration (v1 simplification):** an inline element whose text
wraps across multiple lines paints its border/background on the **first line's
bbox only** — decoration is not split across line fragments.

## Font weight & synthesis

`font-weight` is honored: when the resolved face lacks the requested weight,
the SDF text path synthesizes **faux-bold** by shifting the coverage threshold
(weight 700 ≈ +1.5px stroke at 24px, scaling with font size; capped near
weight 900). An already-bold face asked for its own weight is not
double-bolded. Faux-**italic** (shear) and small-caps synthesis are not done —
`font-synthesis-style`/`-small-caps` cascade but don't yet take effect.

## Registered-but-not-shaped

These cascade and inherit but have no real shaping support in v1:
`font-feature-settings`, `font-variant-numeric`, `font-size-adjust`. Pin
explicit values if a design depends on them; expect no visible effect.

---

Next: [Text & Fonts](text-and-fonts.md) · [Animations & Transitions](animations-transitions.md)
