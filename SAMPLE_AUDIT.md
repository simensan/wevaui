# Sample audit — Unity layout vs Chrome (2026-06-05)

Method: fresh Chrome baselines captured for **all 30 samples** via
`Tools/Layout/extract-chrome-layout.mjs` (headless Chrome, puppeteer) at
1434×781 (match3 at its native 1280×720), then the live engine laid out each
sample at the identical viewport and every block-level element's
`getBoundingClientRect` was compared by DOM-order alignment.

Buckets: **ok** ≤2px max deviation (any of x/y/w/h), **minor** ≤6px,
**MAJOR** >6px. `skip` = inline-display elements (fragment rects not
comparable 1:1) + DOM mismatches (template/JS-driven content).

Caveat: Unity's font metrics ≠ headless Chrome's; a few px of line-height
difference near the top of a page cascades into uniform y-shifts for
everything below. Several "MAJOR" columns are one metric divergence
repeated, not N structural bugs — those are tagged `font-drift`.

## Results

| sample | match | ok | minor | MAJOR | verdict |
|---|---|---|---|---|---|
| dialogue | 39 | 36 | 3 | 0 | ✅ clean |
| episode-stats | 45 | 45 | 0 | 0 | ✅ clean (today's flex cross-stretch fix) |
| inputtest | 28 | 21 | 7 | 0 | ✅ clean |
| leaderboard | 83 | 59 | 24 | 0 | ✅ clean (minors = 1-2px text rounding) |
| todo | 20 | 18 | 2 | 0 | ✅ clean |
| match3 | 122 | 119 | 2 | 1 | ✅ near-clean (combo-banner centering Δ81 x — shrink-width difference) |
| quests | 95 | 85 | 7 | 3 | ✅ near-clean (old 320×360 baseline was bogus; replaced) |
| story-bubble | 12 | 10 | 1 | 1 | ✅ near-clean (p.line wraps to 2 lines in Chrome h=83 vs 42 — font width) |
| vendor | 96 | 94 | 0 | 2 | ✅ near-clean (emoji `item-glyph` advance 36 vs 50 — font) |
| hud | 90 | 82 | 6 | 2 | ✅ clean — `.bar-fill` now 269 vs Chrome 272 (whole chain matches ±3px; the 342 was stale, fixed by this session's flex percent/shrink-fit work) |
| 9slice-demo | 35 | 1 | 28 | 6 | 🔶 buttons Δ10-16 x/w — 9-slice button intrinsic width |
| match3-endgame | 55 | 51 | 1 | 3 | ✅ `.reward-text` verified correct headlessly — exact grid→flex→span structure yields W=58.5/58.5/65 (Chrome 57-73). The ≈0-11 was an audit-walker artifact (TextRuns share the span's Element; the walker measured the wrong box). Pinned by InlineSpanFlexItemTests. |
| nook-dialogue | 16 | 6 | 4 | 6 | ✅ `p.say` FIXED — abs `left+right` pinned width now relays content at the derived width (was: children kept the provisional full-CB layout, 1330 vs 1270). Pinned by AbsoluteInsetWidthTests. |
| randhtml | 165 | 124 | 29 | 12 | 🔶 alert cluster Δ75 x + heights; 179 skips (exotic content) |
| settings | 76 | 21 | 7 | 48 | 🟡 font-drift: uniform Δ7-13 y-shift cascade |
| menu | 141 | 0 | 2 | 139 | 🟡 font-drift: uniform Δ46 y (p line-height 19 vs 16) |
| stock-dashboard | 298 | 21 | 41 | 236 | 🟡 font-drift: uniform Δ28 y |
| weva-landing | 113 | 7 | 7 | 99 | 🟡 font-drift: uniform Δ66 y + code-copy w 614 vs 548 (flex basis) |
| level-select | 60 | 33 | 4 | 23 | 🟠 transform measurement artifact (Chrome rects are post-transform; Unity boxes pre-transform) — verify render visually |
| grid-playground | 103 | 78 | 10 | 15 | 🟠 `.ar.as` x=49 vs 655 is a Chrome quirk (auto item with `grid-area:<undefined-name>` → Chrome implicit-column shrink at bottom-right; Unity spec-standard new-row col-1). `.areas` 45px wider (stress-demo card grid). Low priority. |
| load-game | 31 | 0 | 3 | 28 | 🔴 hint row y 776 vs 708 (+68) + sizes — bottom-row positioning + line-height |
| map | 54 | 16 | 2 | 36 | 🟠 travel cluster = transform artifact. FIXED: `.player-fov` glow now flex-static-centered on the marker (92994c9a) — was offset down-right. |
| flex-playground | 202 | 36 | 111 | 55 | 🔴 `div.body` h 715 vs 1359 — page height/wrap divergence cascades |
| stats | 127 | 102 | 0 | 25 | 🟠 gear-grid slots 125 vs 167: aspect-ratio↔grid-row-height interaction. Chrome's grid is taller (342 vs 258), so rows are ~167 and `aspect-ratio:1/1` derives slot WIDTH from row height → 1fr cols grow to 167 and OVERFLOW the 524 container. Unity sizes cols by 1fr-share (125) + derives height. Intricate; Chrome's own result overflows the card. Deferred. |
| combat-hud | 76 | 18 | 4 | 54 | 🟠 `.ability-bar` is `left:50%+translateX(-50%)` — transform measurement artifact; verify visually |
| inventory | 78 | 15 | 44 | 19 | ✅ verified clean headlessly — real sample gives main H=781 (viewport) + `.inv-body` 662.6 vs Chrome 661. The 120/1203 was pre-fix state (flex percent main-size + shrink-fit work landed since). Pinned by PercentHeightChainTests. |
| advanced-dashboard | 98 | 8 | 60 | 30 | ✅ FIXED — page 781 (=Chrome), `.achievements` 586 vs 587. Root cause: plain-block grid items fed a width-stale/stretch-stale bb.Height into the auto-row hint (`.stats`' aspect-ratio cards measured at full container width → row 1625, then stretch locked it in). Row hint now uses children content extent. Pinned by GridFlexAutoRowStretchTests. |

## Decisions & progress

- **Font policy (user decision 2026-06-05): keep Segoe UI as the default face**
  (option B). Bare `sans-serif` intentionally diverges from Chrome/Windows
  (Arial, ~1.15x normal line-height vs Segoe ~1.36x). The `font-drift` rows
  above are ACCEPTED divergence, not bugs. Only structural issues are fixed.
- Fixed since audit: flex percent main-size collapse (7aa025fe — inventory
  container restored to viewport height); chat `.msg` bubbles shrink-to-fit
  (max-width-clamped fill now triggers the column fit-content probe);
  stale fit-content shrink now re-fits when avail changes (9824b66f —
  inputtest tile labels were ~26px right of centre; invisible to this rect
  audit because anonymous boxes aren't DOM-aligned rows); box/text-shadow
  `currentColor` now interpolates (5280c34c — hud dot-pulse glows smoothly
  instead of hard-blinking).
- Samples removed on request (2026-06-05): main-menu, gold-shop, chat — their
  rows and backlog items (chat `.me-name` family, gold-shop ribbon) are gone.
- Transform rows (map, combat-hud, level-select) reclassified: Chrome
  `getBoundingClientRect` includes transforms, the Unity audit reads
  pre-transform boxes, so `left:50% + translateX(-50%)` / rotated elements
  produce false MAJORs. Still need a visual render check on each.
- advanced-dashboard's real issue is `.achievements` ballooning (1629 vs
  587), not the container.

## Ranked structural backlog (one root cause each, by blast radius)

1. ~~**Grid container height containment**~~ — RESOLVED both halves: inventory
   verified clean (main 781, body 662.6 vs Chrome 661 —
   PercentHeightChainTests); advanced-dashboard FIXED (page 781 = Chrome;
   plain-block grid items' auto-row hint now uses children content extent
   instead of the width-stale/stretch-stale bb.Height —
   GridFlexAutoRowStretchTests).
2. **`line-height: normal` resolves taller than Chrome** (≈1.4× vs ≈1.15-1.2×
   for the same font-size) — the single biggest source of deltas: menu (139),
   stock-dashboard (236), settings (48), weva-landing (~90) are mostly this
   one metric cascading. Fixing the normal-line-height derivation (or the
   default face metrics) collapses hundreds of majors.
3. **match3 combo-banner shrink-to-fit** — centering Δ81 x (chat `.msg` family
   fixed; banner may also be covered by the stale-fit refit — re-verify).
4. **Grid named-area/auto placement** — grid-playground `.ar.as` lands in the
   wrong column (x 49 vs 655).
5. **Stats gear grid track sizing** — slots 125×127 vs 167×167 squares.
6. ~~**Inline-element bbox width ~0**~~ — RESOLVED: not an engine bug. The
   exact structure verified headlessly (InlineSpanFlexItemTests, W 58.5/65 vs
   Chrome 57-73); the ≈0-11 reading was the audit walker measuring the wrong
   element-tagged box (TextRuns share the span's Element pointer).
7. **hud `.bar-fill` % width** — resolves ~25% too wide.
8. **Visual verify of transform samples** — map / combat-hud / level-select
   render checks (audit rects can't see transforms).

## Tooling

- Regenerate a Chrome baseline:
  `node Tools/Layout/extract-chrome-layout.mjs Assets/UI/<name>.html 1434 781`
- The audit harness lives in the session notes; it aligns DOM order and
  compares block-level rects with 2px/6px buckets. Worth promoting into
  `Tools/Layout/diff-assets.mjs` -style automation if run regularly.
- quests' previous baseline (320×360 viewport) and main-menu's (h=0 root)
  were stale/mis-captured and have been regenerated.
