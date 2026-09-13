# The oracle: how the C# engine guards the C++ one

**Build this before writing engine C++.** It is the single highest-leverage
thing in the plan.

## Game UI grid follow-up (2026-09-08)

The in-progress candidate corrects aspect-ratio border feedback, named grid
placement, stretch constraints and implicit/dense auto placement. The grid showcase agrees with the browser;
inventory's accumulating position error is removed. Versioned [regression cases 48–56](../tools/oracle/regressions/grid/)
cover these fixes; all nine match fresh Chrome captures. See the [current work and validation](../examples/frontier_camp/CHROME_PARITY.md).
Size container queries now settle in the same update. Combat HUD geometry agrees
with Chrome; the container corpus now has 15/15 agreements. Seven additional
inline-bounds fixtures cover wrapping, anonymous continuations, overflow and empty
decorated fragments. The current broad comparison has 281/304 agreements. See the
same report for live mutation, native verification and allocation checks.

Installed runtime67 and its historical 304-case counts below remain separate
from this expanded corpus and candidate.

## Runtime67 browser fixes (2026-09-07)

Frontier Camp now passes all 1,276 geometry and 33 interaction checks from the
runtime66 audit. See the [current report](../examples/frontier_camp/CHROME_PARITY.md).
`weva_dump --chrome-metrics` enables browser-oriented fragment unions, paint
transforms and font extents; the default mode preserves the C# reference walk.
`chrome_sweep.py --chrome-metrics` prefers DOM paths and reports unmatched
visible elements. The capture helper expands component templates and avoids
forcing layout inside skipped hidden subtrees. Synthetic broad results remain
investigation leads, not a stock-browser conformance percentage.

## Fresh Chrome audit — runtime66 (2026-09-07)

A fresh 304-page Chrome 152 capture and rebuilt runtime66 dump reproduce the
candidate56 arbitration totals: 258 engine agreements, 36 reference bugs/leans,
and 10 unresolved cases. All candidate layout dumps are unchanged from56.
**This is not a Chrome pass:** direct comparison flags 67 pages for investigation,
including shared engine differences that the three-way oracle does not inspect.
The broad harness still normalizes fonts and overlays the engine UA stylesheet.

A new live Frontier Camp comparison uses the exact Godot font bytes, native
Chrome defaults and eleven interactive/resize states. Focus, value and scrolling
match in 33/33 comparisons; 274 of 1,276 geometry values differ beyond the
existing tolerance. Modal positioning is off by 71.5px at 720p and 431.5px at 4K;
1024px responsive rules also fail. Runtime media context is not wired to viewport
updates. See [the complete audit and repeatable failing check](../examples/frontier_camp/CHROME_PARITY.md).

## Input baselines and short fields — candidate56 (2026-09-07)

Text-entry inputs now expose the baseline of their centered value, including
empty fields and clipped overflow. Checkbox, radio and range baselines use the
bottom border edge; image inputs use the bottom margin edge. Inline atoms now
include their top margin once in the baseline metric. Type changes invalidate
layout through the existing form-input version tracker, even when author CSS
keeps all computed declarations identical.

Control text and caret geometry use the active font's metrics when no family
metrics are registered. Short fields keep text centered with negative leading;
caret and selection painting are clipped to the visible content. Empty inputs
paint a caret, including when a placeholder is visible. UA widths are unchanged.

`Tools/Layout/check-form-baseline.mjs` passes **1,944 Chrome 152 checks** without
the engine UA overlay, comparing native controls with independent CSS models
at a 1/64px tolerance. The core passes **636,031 checks / nine CTest targets**,
including type mutations compared with fresh full frames. Godot passes **2,022
headless / 2,180 rendered checks** with both fonts; frozen candidate55 fails
303 headless checks. All **25 host suites / 8,319 headless checks** pass, and
the four gallery PNGs remain identical to candidate55.

All 304 cases were compared again using the private candidate55 Chrome
captures; the UA stylesheet and fixture inputs are unchanged:

| Corpus | Agree | Differ | Reference bugs |
|---|---:|---:|---:|
| Samples | 27 | 5 | 15 |
| Hand | 45 | 0 | 2 |
| Harvest | 186 | 5 | 19 |

`HtmlParserTests-00` and `SelectorCombinatorialTests-16` now arbitrate to the
candidate. `SnapshotMatcherTests-00` retains a root-height finding, and the
four SpatialNavigator baseline residuals remain. A newly reported sample,
`audit-validation`, is closer to Chrome but remains outside the strict
tolerance: its checkbox card height changes from 43.102 to 44.102px, versus
Chrome's 44px; its enclosing section changes from 134.413 to 135.413px, versus
135.28px. The existing component-capture, container-query and other inline
findings remain. Original captures give samples 27/5/15, hand 45/0/2 and
harvest 186/8/16. No captures, tolerances or acceptance gates were changed.

The Windows preview56 ZIP passes fresh-project, packed-resource and relocated
native debug/release/embedded exports with Godot 4.7.2. All exported example
pixels match the fresh project. Preview56 is **packaged and installed**, with a
hash-checked preview53 backup. The actual project passes 245 host checks and
the exact layout-stress capture. Evidence is under
`.utmp/input-baseline56/`, including the focused Chrome result, native logs,
corpus summaries, export fixture and package manifest.

## Button defaults and border painting — candidate55 (2026-09-07)

Candidate55 keeps adjacent border colors constant up to width-proportional
mitres, instead of interpolating a gradient along straight edges. Uniform-color
borders keep their previous geometry path. Inset/outset borders now shade each
side in sRGB, preserving alpha and hue and adding contrast for near-black
colors. CSS permits UA-dependent bevel shading; this implementation follows
the colors measured in Chrome 152. See
[CSS Backgrounds border styles](https://www.w3.org/TR/css-backgrounds-3/#border-style).

The button UA rule now supplies a 2px outset border, 1px vertical padding,
system color names and the desktop small-control font (13 1/3 CSS px).
Enabled pressed buttons use inset borders; authored solid borders still win.
An authored `font:inherit` follows parent font changes, while an unstyled
button keeps its control font. System color names use the engine's static
palette: for example, ButtonFace is #ddd, whereas the measured Windows Chrome
light palette is #f0f0f0. This is not a claim of native widget appearance parity.

`Tools/Layout/check-button-border.mjs` passes **107 Chrome checks**, using
browser defaults without the engine UA overlay. These include edge pixels,
padding/border dimensions, native pointer presses and default/inherited fonts.
The core passes **629,818 checks / nine CTest targets**, including repeated
incremental border mutations versus fresh rebuilds. The geometry guard retains
its ten-allocation budget; multicolor geometry intentionally changes its digest
to `79cfeab848768fc3`. Clip and draw-mesh guard digests are unchanged.
Godot passes **1,086 headless / 2,542 rendered border checks** with both fonts;
frozen candidate54 fails 386 of the rendered checks. All **24 host suites /
6,297 headless checks** pass. Four gallery PNGs remain identical to candidate54.

Candidate55 is **not packaged or installed**. All 304 Chrome cases were
recaptured privately with the current UA overlay and corrected metric helper:

| Corpus | Agree | Differ | Reference bugs |
|---|---:|---:|---:|
| Samples | 27 | 4 | 16 |
| Hand | 45 | 0 | 2 |
| Harvest | 186 | 7 | 17 |

Original captures instead give samples 27/4/16, hand 45/0/2 and harvest
186/8/16. Neither the original captures nor oracle tolerances/gates were changed.
The remaining findings are not all regressions introduced by this candidate:

- `card-component` is not expanded by the Chrome helper, so its article and
  footer have no browser partner. An explicit slot-projected control has zero
  findings: article height is C# 90.864, candidate 89.816 and Chrome 89.78px;
  the candidate's button is 52 by 21.24px versus Chrome's 52 by 21.22px.
- `menu` had cancelling errors. Chrome's container query makes card headings
  22px, while both headless engines use 18px. The old port button font added
  4.576px to the following line, hiding the missing heading height. With an
  explicit 18px heading control, the two affected card heights are candidate
  108.574 and Chrome 108.56px; C# is 113.15px. Other page offsets remain.
- `sample-menu` now has the correct 36px button, but inline spans/code retain
  approximately 1px vertical residuals. `form-metrics`, `HtmlParserTests-00`,
  `SelectorCombinatorialTests-16` and `SnapshotMatcherTests-00` retain form/inline
  alignment differences. Four SpatialNavigator cases now match the empty
  button's 6px height, with an approximately 0.6px baseline residual.

Evidence is in `.utmp/button55/`: `fresh-oracle-summary.json`, `controls/`,
`inspect/`, `chrome-font-checks/`, `test-details.log`, `host-suite/` and `pixels/`.
The corrected controls are diagnostic copies, not substitutions for corpus
inputs or claims that the remaining acceptance findings have passed.

## Flex/grid item containment — candidate54 (2026-09-07)

The working candidate establishes independent formatting contexts for flex and
grid items, including through `display:contents`. Child margins stay inside the
item, child floats contribute to its auto height, and outside floats do not
intrude into its inline content. This follows
[Flexbox §4](https://www.w3.org/TR/css-flexbox-1/#flex-items).
The test uses the actual box parent, including retained ancestors during
incremental layout; it adds no cached flag or public API requirement.

The historical `QuestGridAutoRowTests-02` height difference was a margin bug:
the heading's **16.38px** top margin escaped its parent flex item. Candidate54
reduces the row from **148.38 to 132px**, matching Chrome, while the C# reference
reports 151.668px. This correction does not change font metrics or UA styles.

The core passes **616,681 checks / nine CTest targets**, including repeated
live-versus-rebuild mutations of parent display, item height and child margins.
The focused Chrome 152 fixture passes **96 checks**. Godot passes **1,176
headless / 1,272 rendered checks**, using both fonts and independent
`display:flow-root` controls; frozen preview53 fails 552 headless checks.
All **23 host suites / 5,211 headless checks** pass, and HUD, layout-stress,
stats-dashboard and landing pixels match preview53 exactly.

Candidate54 is **not packaged or installed**. Against the original stored Chrome
captures, samples has 28 agree / **1 differ** / 18 reference bugs, hand has
47 agree, and harvest has 192 agree / **3 differ** / 15 reference bugs. The
new sample failure is `sample-menu`: fixing its footer's escaping child margin
exposes the browser button's missing 4px border contribution. The port's
button is 32.002px high; Chrome's is 36px. No oracle tolerance or gate was
relaxed. Raw dumps, candidate library and comparisons are in `.utmp/oracle54/`.

All **304 cases** were then recaptured in a private copy at their proper
viewports, using the corrected helper and current UA overlay. The fresh verdict
is samples **28 agree / 1 differ / 18 reference bugs**, hand **47 / 0 / 0**,
and harvest **192 / 2 / 16**. `QuestGridAutoRowTests-02` now arbitrates to the
candidate; its remaining approximately 0.51px inline offset is reported through
the existing lean rule. `SelectorCombinatorialTests-16` and
`SnapshotMatcherTests-00` still fail. The original corpus captures and gates
remain unchanged; fresh captures and verdicts are retained under
`.utmp/oracle54/fresh-captures/` and `fresh-oracle-summary.json`.

For `sample-menu`, two diagnostic copies supply the same explicit button
border to all three implementations. With `border:0`, footer y is C# 426.33,
candidate 410.33 and Chrome 410.27; with `border:2px solid #767676`, they are
430.33, 414.33 and 414.27. Both controls have **zero oracle findings**. Chrome's
footer height of 50.28px also agrees with the candidate's 50.288px, while the
reference excludes the child margins and reports 18.288px. This isolates the
remaining unstyled-button gap without changing the original sample or UA rules.

### Capture metrics correction

`Tools/Layout/capture-all-chrome-layouts.mjs --metrics=mono` previously injected
`*{line-height:1.143}`. This overwrote authored inherited line heights: a 20px
parent with `line-height:150%` and a 10px child measured 11.42px instead of
30px. The helper now normalizes only computed `normal`, after the author
cascade. It also snapshots font families before remapping them: otherwise
replacing a monospace parent's family caused descendants to switch to the
synthetic sans face. A six-character inherited monospace run at 20px now
measures 72px instead of 54px.

`Tools/Layout/check-capture-metrics.mjs` exercises the real helper and passes
**30 Chrome checks** for inherited percentage/em/math/number/normal line
heights, font shorthands, nested font families and root cascade. The frozen old
helper fails eight geometry checks (and lacks the nine provenance fields the
guard also checks). Captures now
record browser version, metric mode and normalization. The UA sheet is explicitly
described as an **overlay on browser defaults**, not a complete replacement;
unspecified browser borders and author-origin rollback remain relevant when
interpreting those captures. This is a synthetic-metric layout instrument,
not a claim of stock-browser form appearance or general font parity.
In particular, fresh mono captures give text inputs the port overlay's 218px
width. That is not a fix for the historical 136px browser-UA width under the
synthetic fonts; native UA form sizing still needs a separate conformance correction.

## Line-height inheritance (2026-09-07)

Preview51 fixes inherited relative line-height lengths. A parent at 20px with
`line-height:150%` computes 30px, which remains 30px on a 10px child; the old
resolver reapplied the percentage and returned 15px. A unitless `1.5` still
uses each descendant's font size. Numeric calc/min/clamp expressions now
follow that multiplier rule, and negative math results clamp to zero.
This follows [CSS 2.2 §10.8.1](https://www.w3.org/TR/CSS22/visudet.html#propdef-line-height).

`Tools/oracle/check_line_height_chrome.py` passes **508 Chrome 152 checks**:
percentage/em/rem/pixel/math values, explicit inheritance and rollback,
invalid-variable fallback, `display:contents`, pseudo-elements, font shorthand,
normal/initial and repeated ancestor-font/class changes. These are computed
values, not a calibration of Chrome's font metrics. Matching resolver checks
also cover source-version changes, reparenting, moves/clear, deep inheritance,
and viewport/root/DPI changes. Incremental paint sequences compare live
line-height/font mutations with full document rebuilds.

The Godot fixture has **400 states**, using both the stub and engine fonts,
block/contents ancestors, changing parent font sizes and own/inherit/unset
styles. Preview50 fails **960 of 2,000 headless checks**. Preview51 passes
**2,400 rendered checks**, including exact pixels against independent explicit
pixel line-height controls, pseudo-elements and following block positions.
The full headless corpus passes **616,389 core checks**.

These checks do not revalidate the three historical harvest form/font calibration
differences. Initial/root metrics, parent-relative `lh`/`rlh` resolution and
other documented font/inline-layout gaps remain separate work. In particular,
this change does not calibrate `normal` or change the first-line strut policy.

## Corner-radius parsing (2026-09-07)

Corner painting now reads the existing style parse cache instead of splitting
raw values at a literal space and reparsing both axes. This also fixes tabs,
newlines, comments and leading whitespace in radius pairs. The longhands take
one or two length/percentage components; the axes resolve against the current
border-box width and height, as defined in
[CSS Backgrounds §4.1](https://www.w3.org/TR/css-backgrounds-3/#border-radius).

`Tools/oracle/check_corner_radius_chrome.py` passes **52 checks in Chrome 152**
for all four longhands, whitespace/comment separators, changing font-relative
values, percentage pairs, replacement and removal. These are computed-value
checks, not screenshot comparisons of percentage radii. The core's separate
**6,823-check** radius guard covers used dimensions, viewport/font/root/DPI
inputs, cache invalidation and allocation budgets; linking it against frozen
preview47 yields **1,215 failures**, while the new implementation passes.
Incremental paint sequences also compare radius/box/font changes against fresh
document rebuilds. Length evaluation and negative-result clamping are unchanged.

## Nested font inheritance (2026-09-07)

The C++ resolver now follows computed font inheritance through the DOM style
chain. Three nested `font-size:2em` declarations under a 16px base resolve to
32, 64 and 128px; an undeclared child stays at 128px. The old two-level resolver
returned 64px for the third level. Explicit `inherit` and `unset` preserve the
parent's computed size even when its stored syntax is relative. Generated
content inherits from its originating element, including through
`display:contents` ancestors.

This intentionally departs from the C# chain limit. It follows the
[CSS Fonts computed-size definition](https://www.w3.org/TR/css-fonts-4/#font-size-prop)
and [CSS Cascade inheritance](https://www.w3.org/TR/css-cascade-5/#inheriting).
`Tools/oracle/check_font_inheritance_chrome.py` records **134 passing Chrome
152 checks** for nested em/percentage/calc, inherited and CSS-wide values,
layer rollback, generated content, and repeated ancestor/class changes.
It uses an isolated profile and writes its fixture and results to the selected
output directory. Core resolver/C ABI and native Godot tests cover the same
dimensions, version changes and incremental-versus-fresh output.

Absolute font-size keywords, root-element rem context and inherited relative
line-height remain separate conformance work. This fix does not claim browser
parity for those cases or general pseudo-element CSS-wide rollback.

## The problem

`Packages/com.wevaui/Tests/` is **183,194 LOC across ~10,500 NUnit tests** —
60k LOC on layout alone, 52k on CSS, 23k on paint. Only ~143 files are data
fixtures (47 HTML, 46 CSS, 50 JSON); the rest are hand-written C# asserts
against Weva's own APIs. They do not translate cheaply, and a layout engine
without them is not trustworthy.

Losing that net is the largest risk in the whole port — larger than any
individual subsystem.

## The solution

Don't translate the tests. Translate the *behaviour they pin*, by keeping the
C# implementation as a reference oracle and diffing against it.

`Tools/BaselineGen/LayoutDump.cs` already emits exactly the right artifact: a
stable JSON dump of every box for a given HTML + CSS at a given viewport.

```json
{ "source": "...", "width": 1280, "height": 720, "count": 412,
  "elements": [ {"i":0,"depth":0,"tag":"div","id":"root","cls":"page",
                 "x":0,"y":0,"w":1280,"h":2140}, ... ] }
```

Per-element `x/y/w/h` at a fixed viewport is the entire contract of a layout
engine. If both implementations agree on that across a large corpus, they agree.

## Pipeline

```
corpus/*.html + *.css
        │
        ├─── C# BaselineGen ──────► reference/*.json      (generated once per corpus change)
        │
        └─── C++ weva_dump ───────► candidate/*.json
                                          │
                                    Tools/oracle/diff ──► pass / per-element deltas
```

Three pieces to build:

1. **`Tools/oracle/corpus/`** — HTML+CSS snippets with a viewport size.
   Sources, in priority order:
   * Harvest every inline HTML/CSS string from the C# test suite. 291 test files
     across Layout and Css contain them; extraction is a scripted pass, not
     manual work.
   * The 47 existing HTML / 46 CSS fixtures.
   * `Assets/UI/randhtml.html` + `.css` — the dev demo, already Chrome-diffed.
   * Generated combinatorial cases per property group once the harness runs.

2. **`weva_dump`** — a C++ CLI mirroring `BaselineGen`'s output byte-for-byte.
   Build it in Phase 1 with a stub layout that emits nothing; it grows with the
   engine.

3. **`Tools/oracle/diff`** — compares two dumps, reports per-element deltas,
   exits non-zero on any mismatch. Tolerance is **zero** (see CONVENTIONS.md on
   floating point). A tolerance knob is a way to hide bugs.

## Gates

Each phase in PORT_PLAN.md is done when its slice of the corpus diffs clean.
The corpus is partitioned by feature so a phase can gate on its own subset:

| Phase | Corpus subset gated |
|---|---|
| 2 CSS parse + values | property round-trip dumps, no layout |
| 3 cascade + selectors | computed-style dumps |
| 4 block + inline | `corpus/block/`, `corpus/inline/` |
| 5 text | `corpus/text/` (needs font parity — see below) |
| 6 flex | `corpus/flex/` |
| 7 grid | `corpus/grid/` |
| 8 positioning, floats, tables | remainder |

Never advance a phase on a partially-green subset. The whole value of the oracle
is that it refuses to let divergence accumulate.

## Paint has a second oracle

`Runtime/Testing/Goldens/SoftwareRasterizer.cs` (1,592 LOC, zero Unity refs)
plus **38 baseline PNGs** in `Tests/Runtime/Goldens/Baselines/`. Port the
software rasterizer early (Phase 4) and diff images directly against those
baselines. `PngReader`/`PngWriter` in the same directory are also Unity-free and
port with it.

## Text is the one place exact parity is not the goal

Glyph rasterization will differ between Unity's `FontEngine` and whatever
`FontInterface` implementation is chosen. Do not chase bit-identical text.

Gate text on **layout-level** properties instead: line-break positions, line
count, line box heights, and advance-width sums within a tolerance stated in the
corpus metadata. Box geometry for everything *around* the text stays zero-
tolerance.

## Prerequisite: fix the stale csproj excludes first

`Tools/BaselineGen/BaselineGen.csproj` and `Tools/PerfBench/PerfBench.csproj`
still exclude `Runtime/UIDocument.cs`, which no longer exists — the file is now
`WevaDocument.cs`, and only `TestVerifyAll.csproj` excludes it. Those two
headless builds are therefore likely pulling in a `MonoBehaviour`.

This was noted as a minor cleanup in the feasibility analysis. It stops being
minor here: **BaselineGen is the oracle**, so it must build cleanly and
deterministically outside Unity before anything else in this plan is worth
starting. Verify with an actual `dotnet build` — this has not been confirmed on
a machine with the SDK.

## All three corpora are gated now

### Conformance target decided: Chrome (2026-09-05)

For standard HTML/CSS behavior, browser conformance leads when the C#
reference disagrees. The reference remains a regression oracle; reproducing
its bugs is not an acceptance criterion. Calibrations must compare the same
viewport, font metrics and author/UA styles, and retain the three-way evidence.
Being closer to Chrome alone does not make a case pass.

The three remaining harvest cases are `QuestGridAutoRowTests-02`,
`SelectorCombinatorialTests-16` and `SnapshotMatcherTests-00`. The fresh run
still reports **192 agree, 3 differ, 15 reference bugs**. Their form-control
and line-height differences remain recorded failures; this performance change
does not adjust UA styles, font metrics, Chrome captures or oracle tolerance.
Future calibration should fix those inputs against Chrome and record the
resulting C# divergence explicitly.

`check.sh` runs the oracle over `samples` (47, at 1280x720), `hand` (47) and
`harvest` (210, both at 800x600). It used to run only `samples`, and two real
bugs lived comfortably underneath it -- a subgrid growing implicit rows it
should have clamped, and a list marker taking inline space so every inline
child of every `<li>` sat a marker-width too far right.

The marker one could not have been caught by `samples` at any size: a list item
holding only text has no element after the marker for the dump to compare, and
every item in every sample holds only text. It took an `<input>` inside an
`<li>`, in a case lifted from the reference's own suite, to make it visible.

`hand` and `harvest` gate at 0 and 3 differ respectively. The three need font
and form-control calibration against Chrome under the decision above; on
those, Chrome agrees with neither engine. The number is written into check.sh
so a fourth breaks the build.

The oracle step now takes about four minutes rather than one. That is the price
of the two bugs above, and it is worth paying.

## The gate is Chrome-only (2026-09-13)

The C# engine is frozen and Chrome is the only oracle. `run_oracle.py`'s
three-way — C# reference against the core, Chrome arbitrating where they
disagree — is retired from `check.sh` and CI; the reference leg goes with the
engine. What gates now is `chrome_sweep.py` in gate mode, with the dump in its
browser-semantics walk (`--chrome-metrics`: full transforms on every corner
and inline fragments unioned, as `getBoundingClientRect` does):

    python3 Tools/oracle/chrome_sweep.py Tools/oracle/corpus/harvest         --weva-dump <build>/Tools/weva_dump/weva_dump --width 800 --height 600         --chrome-metrics --max-worst 1.5 --known-gaps Tools/oracle/known-gaps/chrome-sweep.txt

A case passes when its worst value is within the ceiling of Chrome and every
element pairs. Anything over must be named in `known-gaps/chrome-sweep.txt`
with a cause, and the gate reports entries there that are no longer needed, so
the file cannot quietly accumulate. The exit code is real.

**The corpus and its captures are tracked.** With the C# tests going, the
reason for ignoring the corpus — harvested cases going stale when a test
changes — is gone. `hand/` (52), `harvest/` (225, a fresh harvest of the tests
as of the freeze) and `samples/` (47, with the Chrome screenshots the render
gate uses) carry a `.chrome-layout.json` beside every case, captured with
`--metrics=mono` and the bundled Inter at the corpus's viewport, and stamped
with the browser version inside the file. Regenerate deliberately, the way
`docs/verification/` receipts are treated, never as a side effect of a run.

**The ceiling is measured.** In the browser walk, hand agrees on 52 of 52,
harvest on 220 of 225 with the five differing cases inside 1.0px, and the
samples' text-baseline rounding tops out at 1.3px at 1280 wide; the first
genuine divergence in any corpus is hundreds of pixels (the two known gaps).
So 1.5px separates the classes with margin on both sides.

**What the two-way surfaced that the three-way hid.** Two samples exceed the
ceiling, by 356 and 465px, and they exceed it identically against the captures
that were already on disk. The three-way passed them because the C# reference
agreed with the core, which is the `known-gaps/` definition of a gap. Both are
real unported features — multicol and vertical writing mode — and are in
`chrome-sweep.txt` with their cause. That file is the backlog.

A note against repeating a mistake: run without `--chrome-metrics`, the dump
reports the C# reference's box semantics and nine more samples fail for
reasons that are the mode, not the engine — rotated boxes read untransformed,
inline fragments unreported. The first version of the known-gaps file recorded
those as tool limits. They were not.

Baseline receipt: `docs/verification/chrome-oracle-baseline.json`.

## The harvest pool, and chrome_sweep.py

`corpus/samples` is the gate: 47 cases, three-way, run by `check.sh`.
`corpus/harvest` holds 210 more, each with a Chrome capture already beside it,
and nothing ran them -- the three-way needs BaselineGen for every case, which
is slow enough that the gate deliberately does not.

`chrome_sweep.py` does the cheap two-way instead: lay each case out and ask
whether Chrome agrees. No reference, so it cannot tell a port bug from a place
where the reference and Chrome differ by design. Its output is a list of
**leads**, not a verdict.

    python3 Tools/oracle/chrome_sweep.py Tools/oracle/corpus/harvest         --weva-dump <build>/tools/weva_dump/weva_dump --width 800 --height 600

The width matters: the harvest captures were taken at 800x600 and the samples
at 1280x720. Running harvest at 1280 reports 138 of 210 differing, all of them
`ours 1280, chrome 800` on some full-width block. At the right width it is 34,
and most of those are the font-metrics divergence -- the harvest captures were
not taken with `--metrics=mono`, so any line height reads 18.288 against
Chrome's 19. Read past those.

What it found on its first run was real: a subgrid with more items than
subgridded tracks grew implicit rows instead of clamping into the last one.
Confirmed by promoting the lead to the three-way, which returned REF! on four
cases -- 0 differ, chrome sides with us on every value.

### Sort by the LARGEST disagreement, not by how many

`chrome_sweep.py` orders both its cases and the values within each case by how
far apart they are. That ordering is the whole technique, and it is what found
`contain: inline-size` and the border-box floor:

* **1 to 10px** is the font-metrics gap. Every page has it, it is on almost
  every value, and it means nothing until the decision in
  `known-gaps/README.md` is taken.
* **100px and up** is a real divergence, and there are only ever a handful.

Listing the first few differences in DOCUMENT order buries the second kind
under the first. `9slice-demo`'s largest disagreement is 827px; the six values
an element-ordered listing showed were all 2px font drift.

Three things in its output are the TOOL, not the engine, and are worth knowing
before chasing one:

* **Transformed elements.** Chrome's `getBoundingClientRect` returns the
  transformed axis-aligned bounding box; `weva_dump` applies only the
  translation part of a transform. A `rotate(45deg)` on a 21px box reads as
  21 against Chrome's 29.7, which is 21 times root two and not a bug.
  `level-select` and `neon` are entirely this.
* **Inline elements (historical).** The current browser dump unions inline
  fragments and anonymous block continuations. `card-component` now agrees;
  older captures and the legacy reference walk use the original box semantics.
* **Runs of same-identity siblings.** The two sides do not always list them in
  the same order. `repair_runs` re-pairs them by position now -- it took
  `9slice-demo` from 53 differing values to 45 and `menu`'s worst from 135px
  to 36 -- but it only handles CONSECUTIVE runs.

### What the harvest pool found

Running the full three-way over all 210 harvest cases takes a few minutes and
is worth doing when the gate has been green for a while. The first run read
**181 agree, 14 differ, 15 reference bugs**. The 14:

* Nine are CSS anchor positioning (`anchor-name`, `position-anchor`,
  `position-try-fallbacks`), which `positioning.cpp` does not implement at all.
  Not a bug -- a missing feature, and the largest one left.
* Two were list markers taking inline space, fixed.
* One is a subgrid case, fixed.
* The rest (`QuestGridAutoRow-02`, `SnapshotMatcher-00`) are cases where Chrome
  agrees with NEITHER engine: both differ from the browser on text-driven
  heights, and from each other by a few pixels on top. Those need the font
  decision in known-gaps/README.md before they mean anything.

The 15 reference bugs are the C# reference being wrong with Chrome siding with
us, which is the verdict working as designed and needs nothing.
