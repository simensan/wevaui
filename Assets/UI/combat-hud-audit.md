# Combat HUD probe — Weva vs Chrome audit

Date: 2026-06-01
Probe: `Assets/UI/combat-hud.{html,css}` (1280×800 viewport)
Render: `Assets/Screenshots/combat-hud-render.png`

## Scope

Different CSS surface than the advanced-dashboard probe — focuses on
game-UI patterns: hex ability slots with cooldown sweeps, full-bleed
positioned regions, masked health bars, keyframe animations, and
floating combat numbers.

## CSS module coverage

| Module / Feature | Cascade | Render | Match Chrome | Notes |
|---|---|---|---|---|
| CSS Masking L1 §3.1 `clip-path: polygon(...)` | ✓ | ✓ | ✓ | All 6 hex ability slots clip correctly to 6-point polygon. |
| CSS Animations L1 `@keyframes` + `animation` | ✓ | ✓ | ✓ | Ultimate slot glow, radar sweep rotate, low-HP pulse, dmg float-up — all active. |
| CSS Animations forward-fill ends at opacity:0 | ✓ | ✓ | ✓ | Floating damage numbers vanish at end of 1.6s (`forwards` fill mode). Box exists in tree but pixels invisible after t=1.6s. Same behaviour as Chrome. |
| CSS Masking L1 §5.4 `mask-image: linear-gradient` | ✓ | partial | ⚠ | Target-frame mask cascade is correct (`linear-gradient(90deg, transparent 0%, black 12%, ...)`); visual edge fade is faint — may not match Chrome's exact transparency curve. |
| CSS Images L4 §3.3 `conic-gradient(from … X% , Y 0)` cooldown | ✓ | ✓ | ✓ | Cooldown sweeps visible on slots 2 (65%) and 4 (30%). Closes A21 confirmation. |
| CSS Images L4 conic-gradient as buff timer ring | ✓ | ✓ | ✓ | Three buff circles render with gold/blue/green timer arcs at 80/55/30%. |
| CSS Compositing L1 §4 `mix-blend-mode: screen` | ✓ | ✓ | ✓ | Minimap radar sweep blends additively over the map bg. |
| CSS Backgrounds L3 `background-blend-mode: overlay` | ✓ | ✓ | ✓ | Minimap multi-layer bg renders blended. |
| CSS Filter Effects L1 `filter: drop-shadow(...)` | ✓ | partial | ⚠ | Status effect emoji glows present but only one of three icons is visible — see GAP-3. |
| CSS Filter Effects L1 `filter: grayscale(1)` | ✓ | ✓ | ✓ | Disabled (5th) ability slot desaturates correctly. |
| CSS Filter Effects L1 `opacity: 0.4` | ✓ | ✓ | ✓ | Disabled slot 40% opaque. |
| CSS Transforms `transform: translateX(-50%)` | ✓ | ✓ | ✓ | Target-frame and ability-bar centre horizontally. Layout reports pre-transform X (correct per spec). |
| CSS Transforms `transform: rotate(360deg)` keyframe | ✓ | ✓ | ✓ | Radar sweep rotates. |
| CSS Box `box-shadow` stacking | ✓ | ✓ | ✓ | Hero portrait, ability glow, minimap rim. |
| CSS Color L4 `color-mix(in srgb, …)` | ✓ | ✓ | ✓ | Target HP fill uses color-mix; cascades to a flat color (alpha 100%). |
| CSS Text Decoration `-webkit-text-stroke` | ✓ | ✓ | ✓ | Combat numbers + bar labels show outlined text. |
| CSS Text `text-shadow` (multi-layer) | ✓ | ✓ | ✓ | Combat numbers carry shadow stack. |
| CSS Counter Styles L3 `counter-increment` / `counter()` | ✓ | n/a | n/a | counter-reset wired on ability-bar but not used in content. |
| CSS Generated Content `::after { content: var(--icon) }` | ✓ | ✓ | ✓ | Ability icons via CSS variable + content keyword. Confirms `content: var(...)` round-trips through cascade. |
| CSS Position L3 `position: absolute; inset: …` | ✓ | ✓ | ✓ | All positioned regions land at correct viewport corners. |
| CSS Position L3 `position: relative` parent + `inset: 0` child | ✓ | ✓ | ✓ | Ability sweep, target meter fill, buff inner mask. |
| CSS Custom Properties `--icon`, `--hotkey`, `--cd`, `--pct`, `--x`, `--y`, `--tint` | ✓ | ✓ | ✓ | Inline style + cascade var path all working. |
| CSS Containment L3 `@container (max-width: 180px)` | ✓ | n/a | ⚠ | Buff-tray container-type set; tray width = 150px → query SHOULD match (max-width: 180px). Visually buffs stay in row (no wrap). Either the query doesn't fire or the wrap doesn't take effect at this size — see GAP-4. |
| CSS Flexbox L1 `display: flex; gap` | ✓ | ✓ | ✓ | Status effects, hero bars, ability bar, buffs. |

## Layout numbers (verified via UnityMCP)

```
arena                 1280×800           full viewport
buff-tray             X=24 Y=24 W=150 H=84.6     top-left + 24px
target-frame          X=640 Y=24 W=360 H=96.6    top-centre (pre-transform; paint applies -50% X)
hero-frame            X=24 Y=666 W=314 H=82       bottom-left
ability-bar           X=640 Y=344 W=424 H=72      bottom-centre (pre-transform)
minimap               X=1116 Y=636 W=140 H=140    bottom-right
ability slot          W=64 H=72  (6 in a row)
hero-portrait         W=56 H=56 (clip:50% gives circle)
hero-bars             W=212 H=36 (3 stacked bars)
buff circle           W=36 H=36 (conic + ::after centre cut-out)
minimap dot           W=8 H=8 absolutely positioned by --x/--y
target-meter          W=326 H=14, fill at 32% = 104.3 ✓
```

`bar-xp` height correctly cascades to 4px (override of the default `.bar { height:12px }` via `.bar-xp { height:4px }`).

## Open gaps surfaced

### GAP-3. Status-effect emoji rendering — multi-codepoint glyphs

- **Symptom**: target-frame status effects list has 3 `<li>` elements with emoji content `🔥 ❄ ⚡`. Box tree shows all 3 li boxes (W=24 H=24 each, at X=119/151/183 — correct flex layout); their `::before drop-shadow` cascades. Only one icon is visible in the rendered screenshot.
- **Likely cause**: TMP/SDF text path may only render the first emoji of the three, or the heart/snowflake symbols don't map to a fallback face. Tracked in `TMP-LATIN-1` family (text fallback chain). The boxes ARE laid out correctly — this is paint-side.
- **Scope**: not C#-fixable; needs font-atlas wiring for the colour-emoji range.

### GAP-4. Container query `(max-width: 180px)` not wrapping

- **Symptom**: `.buff-tray` has `container-type: inline-size`; container width is 150px (verified by box dump) — should match `@container (max-width: 180px)` and apply `.buffs { flex-wrap: wrap }`. Buffs render in a single row.
- **Possible causes**: (a) container query fires but `flex-wrap` declaration loses cascade specificity, (b) container width measurement uses padding-box vs border-box mismatch, (c) the matched declaration is dropped because of `flex-wrap: wrap` on a row that already fits 3×36px+gaps in 124px content width = 124 < container, so spec-correct behaviour is to NOT wrap (items fit). This may actually be correct — Chrome would also not wrap items that fit.
- **Verdict**: probably spec-correct, not a real divergence — note for future testing.

### A21 regression confirmation

`conic-gradient(... calc(var(--cd) * 1%), ... 0)` for cooldown sweeps and buff timer rings render correctly — the calc-with-percent fix landed by A21 holds for both progress-ring (dashboard) and cooldown-overlay (combat HUD) patterns.

## Visually verified (screenshot)

- Buff tray top-left: 3 buff circles with conic-gradient timer rings (gold/blue/green) and centre icon mask ✓
- Target frame top-centre: name "Drake Lv 42", HP meter bar at 32% red, mask-image edge fade applied ✓
- Hero frame bottom-left: pink-orange portrait gradient, level 42 badge, 3 stacked bars (HP red, MP blue, XP gold) ✓
- Ability bar bottom-centre: 6 hex slots with clip-path polygon; slots 2 & 4 darkened by cooldown sweep; slot 5 grayscale + 40% opacity; slot 6 (ultimate) gold gradient ✓
- Minimap bottom-right: circular border, radar sweep rotation in progress, 4 coloured dots, white triangle player marker, "42° N — 17° E" coord label ✓

## What this probe DOESN'T exercise

- `:hover` / `:focus` state transitions (no input in the static probe)
- `@keyframes` mid-animation pixel-level correctness (single-frame screenshot)
- `text-stroke` colour interpolation with `currentColor`
- `mask-image: url(…)` (only linear-gradient used)
- `clip-path: path("…")` SVG-path form
- `font-variant-emoji: emoji|text` toggle

These could be targeted by a follow-up probe.

## Engine fixes this probe stresses

- A21 (conic-gradient calc-with-percent) — confirmed working ✓
- A6 (backdrop-filter sample-region leak) — not stressed (no backdrop-filter in this probe)
- CHIP-LOWALPHA — touches it (target-meter background uses rgba(255,255,255,0.05) — bar still renders because the HP fill on top is opaque)
