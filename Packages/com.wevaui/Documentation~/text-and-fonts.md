# Text & Fonts

[← Back to index](index.md)

The CSS text properties you author with (`font-family`, `font-size`,
`font-weight`, `color`, `text-shadow`, `text-align`, …) are documented on
[CSS Text](css-text.md). This page is the practical guide to **fonts**: using
your own, the bundled default, emoji, and monochrome symbols.

## Using your own fonts

The bundled default face is **Inter**. To use a different font you have four
options — all standard, no engine changes:

**1. Drop-in by name (simplest).** Put `MyFont.ttf` (and optional
`MyFont-Bold.ttf` / `MyFont-Italic.ttf`) in `Assets/Resources/Fonts/`, then in
CSS:

```css
body { font-family: "MyFont", sans-serif; }
```

**2. `@font-face` in your stylesheet:**

```css
@font-face { font-family: "MyFont"; src: url("Assets/UI/Fonts/MyFont.ttf"); }
h1 { font-family: "MyFont"; }
```

**3. An OS-installed font** — just name it: `font-family: "Comic Sans MS";`.
On Windows, `font-family: "Segoe UI"` resolves to the user's own installed copy,
no bundling required.

**4. Programmatically** via `Weva.WevaFonts` (handy from a controller / settings
screen / mod loader):

```csharp
// Register a custom family, then reference "MyFont" in CSS:
Weva.WevaFonts.Register("MyFont", "Assets/UI/Fonts/MyFont.ttf");

// Or replace the bundled Inter default for ALL unstyled text + generic
// families (sans-serif / serif / monospace / system-ui):
Weva.WevaFonts.SetDefault(
    "Assets/UI/Fonts/MyFont.ttf",
    boldPath:   "Assets/UI/Fonts/MyFont-Bold.ttf",
    italicPath: "Assets/UI/Fonts/MyFont-Italic.ttf");
```

`WevaFonts` is idempotent and order-independent — call it before or after the
document builds.

## Emoji

Color emoji render via a bundled **Noto Color Emoji** font — no setup, no bake
step. (Segoe UI Emoji and Apple Color Emoji are proprietary and can't be
bundled; Noto is the open, redistributable equivalent.)

To use a different emoji set (Twemoji, OpenMoji, …), drop its TTF at
`Assets/UI/Fonts/NotoColorEmoji.ttf` in your project — that override wins over
the bundled font.

## Monochrome symbols

Monochrome symbols (★ ◆ ▲ ● ♠ ✓ ⚠ … — Geometric Shapes, Dingbats, Misc Symbols)
render from a bundled **Noto Sans Symbols 2** (OFL) so those codepoints come
through as crisp, CSS-colorable outlines rather than emoji chrome. Override
per-project with `Assets/UI/Fonts/NotoSansSymbols2-Regular.ttf`. The emoji and
symbol fonts both work in player builds, not just the editor.

## Default-face policy

**The bundled default `sans-serif` face is Inter** (SIL OFL) — chosen because
it's redistributable (Segoe UI is Microsoft-proprietary and can't ship). Inter's
`line-height: normal` metrics differ slightly from Chrome's Arial, so uniform
vertical shifts relative to a Chrome baseline are **accepted divergence, not
bugs**; only structural layout differences are treated as defects. On Windows,
explicitly naming `font-family: "Segoe UI"` resolves to the user's own installed
copy — so designs authored against Segoe still match there without bundling it.

---

Next: [Samples](samples.md)
