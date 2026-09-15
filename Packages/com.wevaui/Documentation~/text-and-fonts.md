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
**SystemFontFallback** on (the default), the platform's UI, Indic and symbol
fonts (Segoe UI, Nirmala UI and Segoe UI Symbol; Arial, Kohinoor Devanagari
and Apple Symbols; DejaVu Sans) answer
for a script or glyph none of the faces carry, as a browser reaches a system
font; turn it off for output identical on every machine.

**A family the page names.** `font-family: "Segoe UI", sans-serif` with no
`@font-face` for Segoe UI draws with the installed Segoe UI where the
machine has it, as it would in a browser: after each stylesheet the host
asks the core which families the styles name (`FontFamilyNames`), and with
**SystemFontFallback** on registers the installed fonts among them, with
their bold and italic files. A family your `@font-face` declares or your
`RegisterFontFamily` claims is never taken over; a name nobody has falls
through the stack to the next family, then the UI face.

**Shaping.** The package shapes each run itself: the core resolves the
line's bidi levels and hands the host one run at a time; the host asks the
core which way it reads and mirrors its brackets, resolves Arabic joining,
runs the font's `ccmp`, `isol`/`init`/`medi`/`fina`, `rlig`, `calt` and
`liga` lookups from the font's own GSUB (single, multiple, ligature,
contextual and chaining contextual lookups in every format), joins a
cursive script at its `curs` anchors (Nastaliq, swash forms), positions
combining marks through FontEngine's anchors, kerns pairs, and returns a
right-to-left run in visual order. The layout tables are read from the
font's bytes, so a face that arrives as a file, as `@font-face` data or as
an installed font gets its substitutions; a `Font` asset draws its glyphs
one per code point. An Indic run (Devanagari, Bengali, Gurmukhi, Gujarati,
Oriya, Tamil, Telugu, Kannada, Malayalam) is shaped by syllable in the
OpenType "2" model: the base consonant is found, `rphf`, `half`, `blwf`,
`pstf` and the other basic features run on the glyphs they are for, a left
matra moves before its consonant and the reph to the syllable's end, then
the presentation features run. Not done: Sinhala, Khmer, Myanmar and
Tibetan, reverse chaining and alternate substitutions.

**2. `@font-face` in your stylesheet.** `url()` resolves next to a linked or
imported stylesheet; inline/inspector CSS uses `BasePath` (the document asset's
folder in the editor). `local("Name")` is a font installed on the machine:

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

Identical font bytes share one private buffer within the current Unity script
domain. Kerning tables are read once per font and kept as compact arrays, so
switching between regular and bold text does not repeatedly expand native
tables. In the editor, imported font assets also use private buffers so a
reimport cannot invalidate a face still held by FontEngine. These tables,
buffers and FontEngine's own caches remain alive after a document is disposed;
this is not a per-document memory release guarantee.

**3. From code.** `doc.RegisterFontFamily("MyFont", font)` names a Unity
`Font` for CSS — for a settings screen or a mod loader; it survives a reload
and a disable. A family a game registered is not taken over by a later
`@font-face` of the same name.

## Shaping and scripts

File and byte-backed fonts use the OpenType shaping described above, including
Arabic contextual forms and the listed Indic scripts. A Unity `Font` asset
currently supplies glyphs and kerning without those substitutions because its
font bytes are unavailable to the shaper. Colour emoji are not rasterised on
the Unity host yet; available symbol outlines use the CSS text colour.

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
