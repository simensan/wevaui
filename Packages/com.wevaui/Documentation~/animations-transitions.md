# Animations & Transitions

[← Back to index](index.md)

Weva supports CSS transitions and keyframe animations. Transitions fire when a
cascade-resolved property changes; animations run from `@keyframes`. Color
interpolation uses OKLab; gradient stops lerp in linear-RGB.

## Transitions

```css
.btn { background: #4f46e5; transition: background 0.2s ease, transform 0.15s; }
.btn:hover { background: #6366f1; transform: scale(1.03); }
```

Supported: `transition`, `transition-property`, `transition-duration`,
`transition-timing-function`, `transition-delay`. A style change to a
transitioned property (e.g. a `:hover` flip) auto-starts the tween — no
controller code.

## Keyframe animations

```css
@keyframes pulse {
  from { opacity: 0.4; }
  50%  { opacity: 1; }
  to   { opacity: 0.4; }
}
.ping { animation: pulse 1s ease-in-out infinite; }
```

Supported: `@keyframes`, the `animation` shorthand, and the `animation-*`
longhands (`animation-name`, `-duration`, `-timing-function`, `-delay`,
`-iteration-count`, `-direction`, `-fill-mode`, `-play-state`).
`KeyframesResolver` composes the active keyframe value into the cascade each
frame.

## Easing functions

`linear`, `ease`, `ease-in`, `ease-out`, `ease-in-out`, `cubic-bezier(...)`,
`steps(...)`, and the `linear(...)` multi-point function.

**Shorthand caveat:** the `animation:` / `transition:` shorthand parsers do not
recognize `linear(...)` and silently drop it. Bare `linear` works in the
shorthand; use the `animation-timing-function` / `transition-timing-function`
**longhand** for `linear(...)`.

## What interpolates smoothly

Interpolation is type-aware. These kinds tween smoothly:

| Kind | Examples |
|---|---|
| Length / Percentage | `width`, `padding`, `top`, `font-size`, `%` values |
| Number / Integer | `opacity`, `flex-grow`; `z-index` rounds to integer |
| Color | `color`, `background-color`, `border-color` (OKLab lerp) |
| Transform | `transform` shorthand (per-function; matrix-decompose on mismatch) |
| Translate / Rotate / Scale | the individual transform longhands |
| Filter | `filter` function lists |
| Gradient | gradient `background-image` — per-stop when type/angle/stop-count match |
| BackgroundPosition / BackgroundSize | per-layer, per-axis when numeric |
| BoxShadow / TextShadow | per-shadow per-component when lists match |
| ClipPath | same-shape basic shapes lerp per component |

Mismatched shapes (different gradient type, mismatched stop/shadow/point
counts, keyword vs. numeric) fall back to **discrete** (`t < 0.5 ? from : to`).
Everything else is `Discrete` by default.

**Practical note (from the Authoring Guide):** in practice, treat
`box-shadow` / `text-shadow` / `clip-path` / `background-position` /
`background-size` as discrete-snapping for smooth-tween purposes, and prefer
`opacity` / `transform` / `color` when you need guaranteed-smooth motion.
`animation-composition: add | accumulate` registers but composes as `replace`.

## Transition/animation DOM events

`transitionstart`, `transitionend`, `animationstart`, etc. are **not**
dispatched. Read animation state directly from C# instead of subscribing.

---

Next: [Text & Fonts](text-and-fonts.md)
