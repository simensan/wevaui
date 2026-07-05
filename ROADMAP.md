# Weva v1 Roadmap — the seven workstreams

Status legend: 🔵 design · 🟡 in progress · 🟢 landed · ⚪ blocked/queued
Each workstream lands in test-covered increments (headless TestVerifyAll +
in-Unity suites); Chrome is the reference for any observable behaviour.

## W1. Deterministic text — kill font-drift 🟡
**Why:** the last big source of Chrome divergence; masks real bugs (the
baseline bug hid under it) and makes layout machine-dependent.
**What:** bundle a default OFL font (Inter); `@font-face` loading wired
end-to-end; `sans-serif` resolves to the SAME face on ATG + SDF + headless
paths; calibrate `MonoFontMetrics` to the bundled face so headless == live ==
Chrome(with Inter). Closes CSS_OPEN_GAPS LD-1; recalibrate goldens once.
**Order:** design (agent) → bundle+resolve → metrics calibration → golden
recalibration sweep.

## W2. Scrolling & overflow 🟡
**Why:** every real game UI needs scroll containers; only the
automatic-minimum-size half exists today.
**What (phased):**
1. Layout core: scroll state on boxes (`ScrollLeft/Top`), scrollable overflow
   rects (`ScrollWidth/Height`), clamping, `overflow: auto/scroll/clip`
   semantics — headless-testable.
2. Paint: children translated by scroll offset + scrollport clip.
3. Input: wheel/drag delta routing, momentum, keyboard paging.
4. Scrollbars: overlay style first (`scrollbar-width/color`), gutter later.

## W3. Gamepad & spatial navigation 🟡
**Why:** the "this is for games" differentiator; browsers don't have it.
**What (phased):**
1. SpatialNavigator core: direction + focus-candidate scoring over border-box
   geometry (CSS Spatial Navigation heuristics) — pure logic, headless tests.
2. Unity input wiring (pad/keys), `:focus-visible` policy per device.
3. Sticky/wrap policies, `nav-up/down/left/right` style overrides.

## W4. Text input (shipping-grade) ⚪ (after W2/W3 cores)
**What:** selection model (anchor/extent over glyph advances), caret
geometry + blink, clipboard, IME composition spans, undo stack. Core
selection/caret math is headless-testable; IME needs Unity.

## W5. Internationalization 🟡
**What (phased):**
1. UAX #14 line breaking — CJK break-anywhere + prohibition rules
   (kinsoku), `word-break`/`line-break` parity — headless-testable.
2. UAX #9 bidi runs + RTL inline direction.
3. Fallback font chains for CJK (depends on W1's @font-face).

## W6. Render-level golden CI ⚪ (needs Unity)
**Why:** the backdrop flip, truncated blur, and baseline bug were all
invisible to the suite — goldens run the SoftwareRasterizer, not the GPU path.
**What:** batchmode Unity player renders every sample at 1280×720 →
perceptual pixel-diff vs Chrome captures (existing Tools/Layout/__shot.mjs)
with per-sample tolerances; one command locally, CI-runnable.

## W7. DevTools overlay 🟡 (core headless, visuals need Unity)
**What:** in-game inspector built ON Weva: element picking, box-model
overlay, computed-style + cascade-origin panel, invalidation heat-map.
Phase 1: style/cascade dump API (headless-testable) + picker; Phase 2 UI.

---

### Verification debt — CLEARED 2026-06-06 (editor back online)
1. ~~FontResolverFaceSelectionTests~~ — passed in the full sweep.
2. ~~W7 Styles panel visual~~ — verified live (needed a GUILayout→GUI fix, d296f86d; found A-BUTTON-BOXINDEX along the way).
3. ~~Full PlayMode sweep~~ — 8912 tests, only the 7 documented pre-existing Paint reds.
4. ~~GPU golden sweep~~ — green after it CAUGHT a real regression (minmax over-floor, fixed in e2bbcdfd) — W6 working as designed.
5. Reference-game main-menu blur — still user-eyeball whenever convenient (`[Weva] backdrop copy orientation` console line).

### Progress log
| Workstream | Increment | Status |
|---|---|---|
| W1 | design (agent) + Inter bundled + Chrome harness loads Inter (incs 0+1, `7adf5626`) | 🟢 inc 2 (InterFontMetrics class) |
| W1 | incs 3+6: `GoldenRunner`+`LayoutDiffTests` swapped to `InterFontMetrics`; Chrome refs regenerated; 6/9 LD-1 tests un-ignored (6780 pass / 2 pre-ex fail). 3 remaining ignores: MARGINCOLAPSE-RELATIVE (08), H1-EM-FONTSIZE (12), FIXED-DIALOG-HEIGHT (25). Closes CSS_OPEN_GAPS LD-1 (narrowed). | 🟢 |
| W2 | scroll state API, 20 tests (`86f4eacd`). DISCOVERY: paint translate/clip, SCROLLBARS (ScrollbarPaint), wheel routing (ScrollEventHandler.ScrollBy/To) all pre-existed — W2 is materially COMPLETE; remaining: momentum polish + styled scrollbars | 🟢 |
| W3 | SpatialNavigator core (`c0e3f4e5`) + phase 2 input wiring (`0e151359`): arrows in EventDispatcher (editable-guarded, preventDefault opt-out) + InputSystemGamepadNavSource (d-pad/stick repeat, south=Enter) | 🟢 COMPLETE |
| W1 | inc 4: @font-face weight/style face selection. `FontFaceMatcher` (CSS Fonts L4 §5.2 simplified, headless-safe). `FontResolver` upgraded to per-family face list keyed by (weightMin, weightMax, isItalic) with directional weight matching. `CssParser.RegisterFontFace` passes weight range + italic through (no longer drops them). `SdfBootstrap.EnsurePackageDefaultRegistered` registers Weva-Default-Bold.ttf (700–1000) + Weva-Default-Italic.ttf alongside Regular. 17 new headless tests (6960 pass / 2 pre-ex fail). Unity-side `FontResolverFaceSelectionTests` (14 tests) requires Unity test bridge. ATG Bold/Semibold variant selection unchanged (uses TextCore FontAsset name-lookup, independent of FontResolver). | 🟢 |
| W5 | UAX #14 CJK (`70a35286`) + phase 2 simplified UAX #9 bidi (`d8383880`): RTL runs, W7 digits, per-line L2, LTR fast-path byte-identical | 🟢 phases 1+2 — next: CJK fallback font chains (W1 inc 4 now landed) |
| LD-1 sweep | 3 excavated bugs fixed by parallel agents: MARGINCOLAPSE-RELATIVE (`f63e8f68`), H1-EM-FONTSIZE (`c999a869`), FIXED-DIALOG-HEIGHT (`b8ce7614`); all 9 former LD-1 LayoutDiff tests run green | 🟢 |
| W4 | phase 1 — selection/caret geometry core landed: `CaretGeometry` (CaretXForIndex/IndexForX, surrogate-pair-safe), `WordBoundary` (CJK-per-codepoint + ASCII), `LineCaretNavigator` + `MultiLineCaretNavigator` (goal-column up/down). 50 new headless tests (6849 pass / 2 pre-ex fail). Runtime/Forms/Text/; tests in Tests/Runtime/Forms/Text/. — next: Unity input wiring, caret blink, clipboard | 🟢 phase 1 |
| W6 | queued on Unity bridge | ⚪ |
| W7 | phase 1 — headless data backbone landed: `ElementPicker` (box-tree hit-test reusing `BoxTreeHitTester`; translate-transform-aware; `PickBox` static helper), `StyleInspector.Dump()` (computed-value map, cascade-trace gated on `CaptureCascadeTrace`, `BoxModelNumbers`). `MatchedDeclaration` promoted public; `CascadeEngine.CollectMatchesFor` hook added. `Weva.Tests.DevTools` namespace + `Runner.cs` entry added. 21 new headless tests (6870 pass / 2 pre-ex fail). — next: phase 2 overlay UI | 🟢 phase 1 |
