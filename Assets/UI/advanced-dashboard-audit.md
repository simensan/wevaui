# Advanced dashboard CSS audit — Weva vs Chrome

Date: 2026-06-01
Probe: `Assets/UI/advanced-dashboard.{html,css}` (1280×800 viewport)
Suite: 6435 tests passing

## Summary

| CSS feature | Weva | Chrome | Status |
|---|---|---|---|
| CSS Grid `grid-template-columns: minmax / 1fr / minmax` | ✓ | ✓ | match |
| CSS Grid `auto-fit` + `minmax(120px, 1fr)` | ✓ | ✓ | match |
| `aspect-ratio: 1 / 1` on grid items | ✓ | ✓ | match (cards 135×135) |
| CSS Flexbox `gap`, `align-items`, `flex: 1 1 auto` | ✓ | ✓ | match |
| `position: sticky` (cascade) | ✓ | ✓ | cascade ok; scroll behaviour not stressed |
| `backdrop-filter: blur(8px)` | ✓ | ✓ | cascaded + applied (URP-path) |
| Custom properties (`--bg-card`, etc.) | ✓ | ✓ | match |
| `color-mix(in oklab, ... )` | ✓ | ✓ | parses + cascades; visual ok |
| Conic-gradient (`conic-gradient(from 90deg, ...)`) | ✓ | ✓ | parses + paints |
| Linear-gradient (multi-stop) | ✓ | ✓ | match |
| Radial-gradient (`circle at 30% 30%`) | ✓ | ✓ | match (achievement medals visible) |
| `border-radius: 50%` round shapes | ✓ | ✓ | match |
| `filter: grayscale(1)` + `opacity: 0.5` | ✓ | ✓ | locked tiles rendered desaturated |
| `text-shadow` on H1 | ✓ | ✓ | match |
| `letter-spacing` on headers | ✓ | ✓ | match |
| `text-transform: uppercase` | ✓ | ✓ | match |
| Multi-class selector `.t-win .t-marker { ... }` | ✓ | ✓ | match (markers per category) |
| `:nth-child(odd)` striping | ✓ | ✓ | match |
| `::before` with literal string `content: "▲ "` | ✓ | ✓ | trend arrows render |
| `::after` with `content: attr(data-badge)` | ✓ | ✓ | Mail "3" badge renders |
| `::before` with `content: counter(name)` | ⚠ partial | ✓ | **see GAP-1** |
| `::before` with `counter()` + `decimal-leading-zero` style | ✗ | ✓ | **see GAP-1 / GAP-2** |
| `counter-reset` + `counter-increment` cascade | ✓ | ✓ | values stored in cascade |
| `@container (max-width: 220px)` (range form) | ✓ | ✓ | parses; doesn't fire at 320px (correct) |
| `container-type: inline-size` | ✓ | ✓ | match |
| `calc(var(--p, 0) * 1%)` | ✓ | ✓ | match (progress bar fill) |
| `inset: 0` / `inset: 4px` shorthand | ✓ | ✓ | match |
| `flex-shrink: 0` on icons | ✓ | ✓ | match |
| `min-width: 0` on flex item | ✓ | ✓ | match |
| `vh` viewport units (`min-height: 100vh`) | ✓ | ✓ | match (800px) |
| `border: none` | ✓ | ✓ | match |
| `<small>` UA default 0.83em | ✓ | ✓ | match |
| `box-shadow` (medals) | ✓ | ✓ | match |
| `overflow: hidden` on stat-card | ✓ | ✓ | match |
| `text-overflow: ellipsis` (chips) | ✓ | ✓ | match |
| Selector `[data-badge]` attribute | ✓ | ✓ | match |
| `:is()` / `:where()` grouping | ✓ | ✓ | match |

Layout numbers (1280-wide viewport):
| Element | Size | Notes |
|---|---|---|
| `.dashboard` | 1280 × 1671.2 | column flex full-bleed |
| `.hdr` | 1280 × 105.0 | sticky, padding 20×28 |
| `.grid` | 1280 × 1518.6 | gap 16, padding 16/28/28/28 |
| `.stats` column | 280 (minmax 0 280) | ✓ matches CSS spec |
| `.activity` column | 592 | 1fr remainder, matches |
| `.achievements` column | 320 (minmax 0 320) | ✓ |
| `.stat-card` | 135 × 135 | aspect-ratio:1/1 honoured |
| `.ach` card | 156 × 109.9 | auto-fit 2-up at 320 |
| `.t-item` | 592 × 40 | content-fit per timeline |
| `.ring` (conic progress) | 120 × 120 | placed at Y=410 in stats column |
| Avatar | 64 × 64 | + level badge 28×28 bottom-right |
| Footer | 1280 × 47.6 | match (padding 16+text 15.6) |

## Open gaps surfaced

### GAP-1. `counter()` in `::before` `content` produces an empty pseudo box
- **Severity**: runtime (CSS Lists L3 §2.1 / CSS 2.1 §12.4 — counter() in
  generated content must format the active counter value as text)
- **Symptom**: each `.stat-card::before` resolves to a `BlockBox X=124 Y=9
  W=0 H=0`. The cascade has `content: counter(stat, decimal-leading-zero)`
  correctly stored on the pseudo style and `counter-increment: stat` /
  `counter-reset: stat` correctly set on the ancestry, but the pseudo
  box has zero size — meaning the resolved text is empty and no text
  glyphs render. Expected (Chrome): "01", "02", "03", "04" tags in the
  top-right corner of each stat card.
- **Root cause hypothesis**: `BoxBuilder.MaybeInjectPseudoElement` calls
  `CascadeEngine.ResolveContentString(raw, host, counterCtx)` with a
  `CounterContext.BuildFor(host, styleOf)` context. The counter value
  for "stat" is being returned as `ICounterContext.NotFound` (and
  thus the empty string per `ResolveCounterSegment`). This means
  `CounterContext.BuildFor` is not finding the `counter-increment` value
  on the host's own style — likely walking only ancestors instead of
  also considering the host itself, or the increment value is being
  consumed before the pseudo content is resolved.
- **Spec §**: CSS Lists L3 §2.1, CSS 2.1 §12.4.
- **Tested by audit probe**: open `Assets/UI/advanced-dashboard.html` —
  stat-card top-right corners should show `01`/`02`/`03`/`04`; currently
  blank.

### GAP-2. `decimal-leading-zero` counter style not supported
- **Severity**: runtime (CSS Counter Styles L3 §6 — 13+ predefined
  counter-style names; Weva supports only 6 of them)
- **Symptom**: `counter(name, decimal-leading-zero)` falls through to
  `default: return value.ToString()` in `FormatCounterValue`. The
  resolved text becomes "1" instead of "01" even when the upstream
  counter() resolution works.
- **Root cause**: `CascadeEngine.PseudoElements.FormatCounterValue` only
  switches on `upper-roman`, `lower-roman`, `upper-alpha/latin`, and
  `lower-alpha/latin`. CSS Counter Styles L3 §6 also defines
  `decimal-leading-zero`, `disc`, `circle`, `square`, `lower-greek`,
  `armenian`, `georgian`, `hebrew`, `cjk-decimal`, `arabic-indic`, and
  more. v1 minimum should at minimum include `decimal-leading-zero`
  (very common for paginated content).
- **Fix path**: extend the switch in `FormatCounterValue` with the
  remaining L3 §6 predefined styles. `decimal-leading-zero` is a 2-line
  fix: `value.ToString("D2")` for 1-99, or `value.ToString()` otherwise.
- **Spec §**: CSS Counter Styles L3 §6.
- **Composes with GAP-1**: even if GAP-1 is fixed, the audit dashboard
  would render `1`/`2`/`3`/`4` rather than the authored `01`/`02`/`03`/`04`.

## Visually verified (screenshot `Assets/Screenshots/dashboard-full.png`)

- 3-column main grid spans full width without overflow
- Stat cards 2×2 grid inside narrow column with correct aspect-ratio
- Activity timeline markers tinted per category class
  (green/red/gold/violet/accent)
- Achievement medals render as radial gradients; locked tiles
  desaturated + 50% opaque
- Mail chip shows red ::after "3" badge with attr()
- Avatar conic-gradient ring visible at the top-left
- Header sticky background blends via gradient + backdrop-filter

## Not exercised by this probe (acknowledged gaps in audit scope)

- Animation / transition resolution at runtime
- `:hover` / `:focus-visible` state behaviour
- Sticky scroll behaviour past first viewport
- Scrollbar-styling on overflowing list
- `mask-image` (no use)
- `clip-path` (no use)
- `prefers-reduced-motion` / other media queries
- `@supports` feature queries

The reference game's `main-menu.html` exercises many of those orthogonally.
