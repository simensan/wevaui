# Text & Fonts

[← Back to index](index.md)

The CSS text properties you author with (`font-family`, `font-size`,
`font-weight`, `color`, `text-shadow`, `text-align`, …) are documented on
[CSS Text](css-text.md). This page is the practical guide to **fonts**: using
your own, the bundled default, and what the Unity host can and cannot shape.

## Using your own fonts

The bundled default face is **Inter**, with real bold and italic files, and a
symbol face behind it. To use a different font you have three options — all
standard, no engine changes:

**1. On the component.** Assign a Unity `Font` asset to **Font** (and its
real weights to **Bold** / **Italic**) on the `WevaDocument`. It becomes the
UI face: what unstyled text and the generic families (`sans-serif`, `serif`,
`monospace`, `system-ui`) resolve to. **Fallbacks** are faces tried, in order,
for code points the UI face lacks (CJK, Hebrew, symbols). After them, with
**SystemFontFallback** on (the default), the platform's UI and symbol fonts
(Segoe UI and Segoe UI Symbol; Arial and Apple Symbols; DejaVu Sans) answer
for a script or glyph none of the faces carry, as a browser reaches a system
font; turn it off for output identical on every machine.

**Shaping.** The package shapes each run itself: the core resolves the
line's bidi levels and hands the host one run at a time; the host asks the
core which way it reads and mirrors its brackets, resolves Arabic joining,
runs the font's `ccmp`, `isol`/`init`/`medi`/`fina`, `rlig`, `calt` and
`liga` lookups from the font's own GSUB (single, multiple, ligature and
coverage-based chaining lookups), positions combining marks through
FontEngine's anchors, kerns pairs, and returns a right-to-left run in visual
order. The GSUB is read from the font's bytes, so a face that arrives as a
file, as `@font-face` data or as an installed font gets its substitutions; a
`Font` asset draws its glyphs one per code point. Not run: glyph- and
class-based contextual lookups, Indic reordering, cursive attachment.

**2. `@font-face` in your stylesheet.** `url()` resolves relative to
`BasePath` (the document asset's folder in the editor); `local("Name")` is a
font installed on the machine:

```css
@font-face { font-family: "MyFont"; src: url("Fonts/MyFont.ttf"); }
@font-face { font-family: "MyFont"; src: url("Fonts/MyFont-Bold.ttf"); font-weight: 700; }
@font-face { font-family: "Heading"; src: local("Segoe UI"), url("Fonts/Heading.ttf"); }
h1 { font-family: "Heading", "MyFont", sans-serif; }
```

Faces match by weight and style the way a browser does (a 500 on its own
serves the family; an exact 400 wins over it; a bold-only family serves bold
and synthesises nothing). A `local()` that is not installed falls through to
the `url()` after it. Replacing the stylesheet releases the families it no
longer declares.

**3. From code.** `doc.RegisterFontFamily("MyFont", font)` names a Unity
`Font` for CSS — for a settings screen or a mod loader; it survives a reload
and a disable. A family a game registered is not taken over by a later
`@font-face` of the same name.

## Shaping and scripts

The host answers the core's font callbacks with Unity's `FontEngine`: one
glyph per code point, with kerning. That covers Latin, Cyrillic, Greek, CJK
and symbols. It does **not** produce Arabic contextual forms or reorder
glyphs inside a word — the core's bidi (ICU) orders the runs of a line, the
host draws each run's code points as they are. Colour emoji are not
rasterised on the Unity host yet; symbols and monochrome emoji render from
the bundled **Noto Sans Symbols 2** fallback as CSS-colourable outlines.

## Default-face policy

**The bundled default `sans-serif` face is Inter** (SIL OFL) — chosen because
it's redistributable (Segoe UI is Microsoft-proprietary and can't ship). Inter's
`line-height: normal` metrics differ slightly from Chrome's Arial, so uniform
vertical shifts relative to a Chrome baseline are **accepted divergence, not
bugs**; only structural layout differences are treated as defects. The Chrome
oracle that gates the core's layout is run with the same faces on both sides.
Naming an installed font — `@font-face { src: local("Segoe UI") }` — resolves
to the user's own copy, so designs authored against it still match there
without bundling it.

---

Next: [Samples](samples.md)
