# Animations and transitions

[← Back to index](index.md)

CSS transitions animate changed styles; `@keyframes` animations run on the
document's animation clock. The current core supports a limited interpolation
surface, so check the value types below before relying on smooth motion.

## Transitions

```css
.button { opacity: 0.7; transition: opacity 0.2s ease; }
.button:hover { opacity: 1; }
```

Use `transition-property`, `transition-duration`, `transition-delay` and
`transition-timing-function`, or the `transition` shorthand. A state/style
change starts the transition without controller code.

## Keyframes

```css
@keyframes pulse {
  from { opacity: 0.4; }
  50% { opacity: 1; }
  to { opacity: 0.4; }
}
.ping { animation: pulse 1s ease-in-out infinite; }
```

The `animation` shorthand and name, duration, delay, timing-function,
iteration-count, direction, fill-mode and play-state longhands are supported.
Additive/accumulating animation composition is not implemented.

## Easing and interpolation limits

Easings: `linear`, `ease`, `ease-in`, `ease-out`, `ease-in-out`,
`cubic-bezier(...)`, `steps(...)`, `step-start` and `step-end`.
The multi-point `linear(...)` function is not implemented, including in longhands.

| Values | Current interpolation |
|---|---|
| Numbers and percentages | Numeric interpolation |
| Lengths and angles | Smooth when both endpoints use the same supported unit; mixed units switch discretely |
| Colors | Premultiplied sRGB interpolation, not OKLab |
| Matching comma/space lists | Each item follows these rules; unsupported items switch discretely |
| Function values such as `transform`, gradients, filters and `clip-path` | Discrete; no transform matrix decomposition or per-stop gradient interpolation |

Unsupported or mismatched values switch at the midpoint. Start with opacity,
same-unit dimensions and colors when you need smooth transitions. A property
accepting CSS values does not imply that those values interpolate smoothly.

Browser animation/transition lifecycle events are not dispatched, and the
supported Unity API does not expose browser-style animation state queries.
Use your controller's own state and timing for gameplay actions.
