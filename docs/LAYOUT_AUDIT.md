# Layout audit

## Current installed corrections

Direct child headings with `column-span:all` now separate independently balanced
column groups. Consecutive heading margins collapse, and empty headings with
negative margins cannot give the container a negative height. The installed
change passes 216 Chrome cases and native headless/rendered checks. Nested
spanner extraction and paragraph fragmentation remain incomplete.
[Spanning-heading evidence](verification/column-span.json).

The current core completed all 309 frozen geometry fixtures: 285 agree, 24
differ, and none crash. Two newly differing form fixtures compare compact select
widths against the former 218px minimum; fresh captures using the current default
stylesheet agree in both fixtures. Fresh settings captures also agree on select
widths, retaining seven text differences of at most 0.2px. Frozen and fresh
results are recorded separately; the original audit is not regraded.
[Current audit evidence](verification/layout200.json).

The installed addon includes table stacking, pointer-order, overflow-clipping,
numeric-opacity and automatic closed-select sizing corrections. See
[product readiness](PRODUCT_READINESS.md) for the current binary and its
qualification results. Earlier measurements below apply only to their recorded
binaries; they do not establish current timing or lifecycle qualification.

Focused evidence includes the 64-case stacking matrix and the earlier 224 clipping
cases, followed by 14 numeric-opacity cases. For PNG comparisons, use the software
renderer's sRGB byte export; its `pixel()` API returns linear RGB. Comparing those
linear values directly with browser PNG bytes caused two false opacity mismatches.
All 14 opacity cases match when compared in the same color space.

The preceding stacking candidate's two short desktop timing failures, passing
longer desktop run and passing automatic 1080p 3D profile retain their original
binary identity. They are not separate failures in the currently installed build.
Broader table effects and the remaining layout requirements below are still open.
[Stacking evidence and timing history](verification/table-stacking191.json).

## Historical stacking reproduction

The remaining sections preserve earlier investigations and checkpoints. Claims
about what was installed, uncorrected or pending describe that checkpoint;
use [product readiness](PRODUCT_READINESS.md) for current status.

The initial 16-case comparison passed only 12 pixel cases and eight pointer-target
cases. The permanent regression then exposed 22 failing assertions. The partial
auto-z correction reduced those to 14 failures; the subsequent negative-z and
internal-table-target fixes cleared them and passed an expanded 64-case matrix.
Those are resolved stages of the current correction, not outstanding failures.
[Original reproduction](verification/table-stacking190.json).

## Original table content-order regression

A 12-case Chrome comparison covers static/relative table cells containing
static, relative or absolute content, with transparent/yellow adjacent cells.
Only the two fully ordinary-flow cases match all three sampled pixels; 8/12
cases match all sampled hit targets. Positioned content is incorrectly covered
by collapsed borders or a later cell background. A paint-only correction would
leave click routing wrong in four cases. The captured fixtures and expected
pixels/hits are [recorded here](verification/table-content-order186.json).

This defect was subsequently corrected by the installed stacking changes.
The correction preserves ordinary-flow controls, ancestor clips/transforms,
z ordering and version-keyed paint replay ranges. Moving content outside an
ancestor's captured range without changing replay would omit it when reused.

At checkpoint183, both local addons included the table/caption fixes and rounded
paint allocation reductions.
The sections below retain their original qualification history; current addon
status is tracked in [product readiness](PRODUCT_READINESS.md).

## Historical installation at checkpoint183

Both local addons match the qualified runtime. All 16 sanitizer gates, 72 manual
desktop timing gates and 15 installed smoke suites pass. Earlier evidence below
keeps its original scope; automatic 3D and broader table/filter behavior remain
open at that checkpoint. [Checkpoint183 installation](verification/rounded-native183.json).

## Rounded antialias scratch arrays removed

Antialias ring points now go directly into the output vertex buffer. The warmed
rounded-paint case drops from five allocations to three (seven before ownership
transfer), retaining its exact geometry hash and the 56-case combined hash.
Unfeathered rounded fans allocate only their two output buffers. All 14 core
suites and three focused Linux sanitizer suites pass; final added allocation
assertions also pass. Native runtime remeasurement remains pending, and earlier
hover failures remain open. [Evidence](verification/rounded-rings182.json).

## Rounded ownership native qualification

The ownership-transfer build passes all 39 native host entries, survival-sample
headless/rendered checks and native exports. The normal desktop timing profile
passes 70/72 targets: Vulkan hover p95 is 0.506ms in run 1 and 0.501ms in run 3,
against the unchanged 0.5ms limit. All functional checks pass. These failures
remain recorded; allocation savings alone do not establish a latency speedup.
The candidate is not installed. [Native results](verification/rounded-native181.json).

## Rounded paint buffer copies removed

Rounded-rectangle painting now transfers temporary fallback vertex/index buffers
to retaining backends. Existing const-reference backends retain their default
fallback. The warmed regression case drops from seven allocations to five;
geometry hashes are unchanged for that case and the 56-case combined
clip/filter/opacity/transform/control corpus. All 14 core suites and the focused
Linux sanitizer allocation suite pass. Native rebuild and runtime hover
remeasurement remain pending; this does not clear the earlier timing failure.
[Allocation and output evidence](verification/rounded-owned180.json).

## Thread-cycle hover diagnostic

The opt-in buffer now adds raw executing-thread cycles on Windows, with an
unavailable value on other platforms. One measured update takes 3.84 times the
median elapsed duration but 1.08 times the cycles; others increase both duration
and cycles substantially. Scheduling may contribute, but this does not identify
a single cause or clear the original hover target failure. All 14 core CTest
suites and 202 focused Linux sanitizer ABI checks pass. The native diagnostic
run completes all 2,400 measured hover updates successfully; no installation.
[Cycle measurements and limitations](verification/hover-cycles179.json).

## Buffered hover timing

An opt-in thread-local buffer now captures the last 4,096 core update stage
intervals and prints only during teardown. A 4,800-frame Vulkan run produced
4,923 updates, retained exactly 4,096 sequential records, and emitted them after
performance completion. The slowest retained core update was 0.441ms, including
0.3873ms cascade; another spent 0.3412ms in paint. Box/layout work remained near
zero. All 14 core CTest suites pass with tracing disabled. These elapsed timers
still include scheduling pauses; exclusive CPU attribution and the original
uninstrumented hover failure remain unresolved. No installation was performed.
[Implementation and diagnostic evidence](verification/hover-buffered178.json).

## Hover stage trace

The final 1,200 updates of an opt-in Vulkan trace skip box rebuilding and layout.
The observed update reuses 12 painted subtrees and visits three styles. Logged
stage medians are 0.037ms cascade and 0.056ms paint; p95 values are 0.071ms and
0.104ms. Verbose output substantially distorts total frame/update timing, so
these values locate work but do not clear the uninstrumented performance failure.
The next measurement must avoid per-update logging and correlate core/host costs.
[Trace scope and analysis](verification/hover-stages177.json).

## Paired hover diagnostic

Four alternating Vulkan pairs compare the installed runtime and current candidate
using identical sample and test-script hashes. Both exhibit hover p95 values over
0.5ms: installed 0.208–0.519ms, candidate 0.276–0.515ms. All functional checks pass.
This focused diagnostic does not replace the failed complete profile or identify
the cause; it does not establish a regression from the table changes. Per-stage
hover cost and scheduling/render interaction need investigation next.
[Paired measurements](verification/hover-paired176.json).

## Latest native qualification and remaining timing failure

The caption and row/group corrections now pass all 39 native host entries,
the survival sample on both renderers, and native export startup/relocation/pixel
checks. The normal desktop performance profile passes 71/72 targets: Vulkan run
3 hover changed-API p95 is 0.543ms against the unchanged 0.5ms target. Functional
checks all pass. This failure is preserved and unresolved; the candidate is not
installed. Other observed p95 ranges are vitals 0.288–0.775ms, inventory sorting
0.708–1.455ms, typing 0.381–0.797ms and settings toggles 1.324–1.946ms. These runs
do not establish a causal speedup or automatic 3D readiness.
[Native and timing evidence](verification/table-native175.json).

## Caption-area table painting corrected

The table's decoration rectangle now excludes top and bottom caption extents.
Transparent captions no longer receive the table background, and separated
borders surround the grid frame. The regression had 12 failed assertions before
the fix; all 24 sampled pixel checks across eight fixtures now agree with Chrome.
These are sampled assertions, not full-image parity. All 14 rebuilt core CTest
suites and focused table ASAN/UBSAN checks pass. Native candidate171 predates this
fix; broader filter/shadow and positioned-child paint behavior remains open.
[Browser samples and verification](verification/table-caption-paint174.json).

## Separated row and group rectangles corrected

Row and row-group bounds now exclude the outer horizontal spacing; group height
also excludes the trailing row spacing. Cell-local positions compensate so cell
placement remains unchanged. All 16 Chrome geometry cases agree, including the
earlier captions and a second matrix with multiple body rows and a footer group.
Core and focused table sanitizers pass; all 309 broad reports remain unchanged.
Native candidate171 predates these source changes. Caption-region painting still
needs pixel qualification. [Cases and checks](verification/table-groups173.json).

## Caption wrapping and placement corrected

Captions now use the final table wrapper width and sit outside the grid frame.
Caption, table and following-element geometry agrees in all eight Chrome cases;
the three 65px blocks fit a single row in the 200px caption. Four separate-border
cases retain row/group bounds differences around border spacing; these are
preserved in the receipt. Main core and focused table sanitizers pass, and the
309-fixture broad reports are unchanged. Native candidate171 predates this fix.
Table painting around captions and broader caption margins remain unqualified.
[Correction and remaining differences](verification/table-captions172.json).

## Native candidate qualification

The table changes through the narrow-width correction now build as a native
Godot addon. All 39 existing host entries pass (28,127 checks). The survival
sample passes import, headless integration (95 checks), both rendered backends
(101 each), and release export checks. Native export startup, relocation and
rendered project/export pixel comparisons pass. This candidate is packaged but
not installed. Existing suites do not certify every new table paint case, and
its runtime performance profile has not been rerun.
[Native qualification](verification/table-native171.json).

## Caption geometry reproduction (before correction)

Eight text-free Chrome cases expose incorrect caption width and placement for
both separate and collapsed borders, top/bottom captions, and zero/20px table
padding. Three 65px inline blocks fit one row in a 200px caption in Chrome;
the engine wraps them because it measures inside the table frame. Captions also
inherit the table's inner offset instead of sitting outside its grid frame.
The geometry correction above covers caption wrapping and placement. [Original cases](verification/table-captions171.json).

## Narrow-table width floor corrected

The block sizing stage now defers a collapsed table's width floor until its
shared borders are resolved. This removes ignored padding and full authored
borders from that floor while retaining normal block border-box behavior.
All 56 explicit-width cases (including zero width) and 24 percentage/min/max
cases agree with fresh Chrome geometry. The main core runner passes 499,431
checks; focused table ASAN/UBSAN passes 13,804 checks. All 309 broad frozen
geometry reports are unchanged from the preceding audit. Native Godot and
caption qualification remain open; this correction is source-only.
[Cases and verification](verification/table-narrow170.json).

## Narrow-table sizing reproduction (before correction)

A fresh 24-case Chrome comparison adds 10px and 60px widths to the earlier
200px sizing matrix. Twenty-one cases agree; three narrow border-box cases
fail. A requested 10px table becomes 16px with 8px authored borders, 40px with
20px padding, or 56px with both. Chrome keeps the requested 10px in these cases.
The ordinary block width floor runs before collapsed-table layout discards
padding and replaces borders with shared half-borders. The correction above now covers this defect; the broad corpus did not exercise it. Caption sizing is still untested
in this matrix. [Cases and differences](verification/table-narrow169.json).

## Broad regression check after table corrections

All 309 frozen Chrome geometry fixtures completed without crashes. The comparison
reports no new differences against the preceding broad audit: 287 fixtures agree,
and 22 retain differences. Only the leaderboard report changes, with its table-row
geometry differences removed; two differences outside the table remain. All 14
core CTest suites pass, including the existing allocation gates. This checks the
normalized geometry corpus, not native rendering or full browser parity. The table
changes remain source-only pending broader table and Godot qualification.
[Comparison and source identity](verification/table-corpus168.json).

## Explicit content-box sizing correction

Collapsed table sizing now preserves the requested content width while replacing
the original padding/border contribution with resolved half-borders. The browser
HTML-table `box-sizing: border-box` default is also present in the UA stylesheet;
authors can still choose content-box. All eight Chrome geometry cases agree for
both sizing modes, zero/20px padding and zero/8px table borders. The 256px result
for a requested 200px content box now correctly measures 208px. Main core and
focused sanitizer tests pass. The broad geometry regression check above now passes without new differences.
Wider sizing constraints and native qualification remain open; nothing is installed.
[Current sizing evidence](verification/table-sizing167.json).

## Unequal solid-border intersections

Segment caps now use the intersecting border's half-width, preventing overhangs
when horizontal and vertical widths differ. Stronger borders paint later at
junctions; the tested equal-width side order also follows Chrome. Three complete
220x90 image masks (59,400 pixels) agree for 8/4, 4/8 and 4/4 red-horizontal,
blue-vertical borders. Translucent coverage and live-update comparisons still
pass. The main runner passes 499,095 checks; focused ASAN/UBSAN table checks pass.
This does not certify all color/style combinations, and the addon is unchanged.
[Browser fixtures and verification](verification/table-junctions166.json).

## Live span mutation correction

The incremental/full comparison exposed missing layout invalidation for changes
to `rowspan` and `colspan`. These attributes can change placement without changing
computed CSS. Host and binding writes now queue the affected element through the
existing content-input version path; `span` on columns follows the same rule.
All 32 live/full-frame comparisons pass, including bound spans, row removal,
colors, widths, clipping and separate/collapse switches. The focused sanitizer
run passes 202 checks; the main core runner passes 499,086. Browser parity is
still assessed separately, and these changes remain uninstalled.
[Regression evidence](verification/table-live165.json).

## Translucent shared-border correction

Chrome renders two overlapping perpendicular layers at the tested solid-border
junctions. The first engine paint path added third/fourth layers where collinear
segments overlapped as well, reaching alpha .875/.9375 instead of .75. Segments
now meet at their grid line and only exposed ends extend. The full 13,200-pixel
opacity pattern is checked, allowing the documented float/byte quantization
interpretation. Main core tests pass; unequal borders, live mutations and native
qualification remain pending. This source change is not installed.
[Browser capture and regression evidence](verification/table-alpha164.json).

## In-progress shared-border painting

Resolved border segments now paint from table-relative coordinates, with source
identity based on DOM elements rather than scratch-tree BoxIds. BoxTree stores
data only for tables with visible segments; subtree import copies that data,
and release/reset clears it. The ordinary table/cell border pass is suppressed.
Solid-border pixel checks and scratch-tree replacement/reset pass. The main
core runner passes 485,676 checks, and 251 table checks pass under ASAN/UBSAN.

The change remains uninstalled. Transparent intersections, border junction
precedence, positioned content, blur, live paint-only mutations and broader
Chrome pixels still need verification/correction. The implemented dash/dot/double
and bevel drawing paths are not yet browser-pixel-qualified. Earlier statements
that painting is unconnected describe prior checkpoints.
[Current implementation evidence](verification/table-paint163.json).

## In-progress collapsed-border layout fix

The core now collects borders from table/cells/rows/groups/columns and applies
winning half-widths before final cell measurement. Table outer half-borders
also participate in its final height. All 12 text-free Chrome geometry cases
now agree, including the 56px-to-52px regression. The main core runner passes
485,656 checks; 231 focused table checks pass under ASAN/UBSAN.

This source change is not installed. Shared-border painting still uses the old
path and must be integrated before claiming the visible defect is fixed. The
grid is currently local to layout; retaining its source identities safely during
subtree replacement, live mutations, captions and broader table constraints are
still pending. [Current evidence](verification/table-layout162.json). Earlier
checkpoints below preserve their original state and scope.

## Confirmed current defect: collapsed table borders

A fresh text-free Chrome comparison isolates geometry errors in ordinary
leaderboard/stats tables. All six `border-collapse: separate` controls match;
four of six `collapse` cases differ. A two-row table with 20px content per cell
and 4px borders measures 56px high in the engine versus Chrome's 52px. Its row
group starts at (0,0) instead of (2,2), and its width is 200px instead of 196px.
Both 1px and 4px borders reproduce the error. Conflict-only cases in this matrix
match geometry; they do not verify border colors or conflict painting.

The shared conflict resolver is now implemented and covered by 85 new checks;
the main core runner passes 485,591 checks. It preserves the winning source/side
for color lookup and applies hidden/none, width, style and origin precedence.
It is not yet connected to layout or paint, so these tests do not fix or waive
the geometry failures. Grid collection, spanning edges, used half-widths,
shared paint and incremental validation remain. [Implementation checkpoint](verification/table-conflicts160.json).

The shared segment grid now handles rowspan/colspan interior suppression,
per-segment winners, half-width queries and clearing reused storage. All 209
focused table checks pass under ASAN/UBSAN. Layout and paint do not consume it
yet; the installed geometry remains unchanged. [Grid checkpoint](verification/table-grid161.json).

The current table code suppresses spacing for collapse but retains separate-cell
border sizing. A complete fix must resolve shared borders for layout and paint,
including outer half-borders and conflicting widths/styles, then check live
changes, spans and row/column groups. Simply shifting rows would leave those
contracts wrong. The installed build is unchanged; the defect remains open.
[Text-free cases and differences](verification/table-borders159.json).
[CSS collapsed-border model](https://www.w3.org/TR/CSS22/tables.html#collapsing-borders).

Reproduce the browser fixture from the repository root:

```sh
node Tools/oracle/check_table_borders_chrome.cjs table-borders.json
```

## Candidate preview135: normal-flow auto margins

A single auto margin now absorbs the positive space left after the block width
and fixed opposite margin. The containing block's direction selects the aligned
edge when both sides are specified or the child overflows. The shared placement
helper applies to normal block flow and column children; other formatting contexts
retain their own rules. See [CSS block width constraints](https://www.w3.org/TR/CSS22/visudet.html#blockwidth).

All 96 Chrome152 cases and 30 live steps pass through Godot (1,009 checks),
covering LTR/RTL, blocks, flow-root panels, columns and fixed/min/max widths.
The installed preview134 reproduced 44 failures at the matching 1280×720 viewport.
Thirty new incremental/full render comparisons pass (271 total). Both oracle
Godot scenes now set document_size explicitly from the captured viewport; an
intermediate inherited-RTL failure came from the old scene's 64px default.

The first probe allowed column fragmentation and is preserved separately.
Unbreakable cards isolate this alignment test; fragmentation remains unresolved.
The broad 309-fixture audit is unchanged: 287 agree, 22 differ.
[Candidate evidence](verification/block-margins135.json). Runtime qualification
passed 71/72 gates: Vulkan hover reached 0.521 ms p95 against 0.500 ms.
The failure is retained and preview134 remains installed. Paired diagnostics also
reproduced a hover overrun on preview134; the cause remains under investigation.

## Installed preview134: RTL column order

Column order now follows the container's inherited inline direction. Right-to-left
columns mirror the allocated column boxes, including fractional remainders;
fixed-width cards align from each column's right edge and retain their margins.
This follows the [multicolumn model](https://www.w3.org/TR/css-multicol-1/#the-multi-column-model).

The expanded Chrome152 fixture has 28 initial cases and 16 live steps, all passing
through Godot (2,289 checks). It covers inherited direction, live RTL/LTR changes,
uneven widths, preferred-width constraints, padding, fixed-width cards and margins.
The original added cases reproduced 52 coordinate failures; fixed-width coverage
then exposed another 24. Both failure logs are retained. Four additional live
render comparisons match full rebuilds (241 total). The broad audit stays
287/309; no individual fixture report changes from preview133. Paragraph
fragmentation and vertical writing remain open. The candidate passed all 72 timing gates and is installed in both projects.
[Qualification evidence](verification/multicol-rtl134.json).

## Installed preview133: multicolumn sizing

The used column count now honors `column-width` as well as `column-count`.
Normal gaps use the owning block's resolved font size (1em), and zero/subpixel
preferred widths use the 1px minimum. Column widths and origins follow Chrome's
1/64 CSS-pixel allocation: floor the width and round each origin from the ideal
stride, avoiding accumulated drift. These are the
[CSS column sizing rules](https://www.w3.org/TR/css-multicol-1/#pseudo-algorithm)
and [normal-gap behavior](https://www.w3.org/TR/css-multicol-1/#column-gap).
The rounding behavior is specifically verified against Chrome152.

All 17 focused cases and 12 live resizing/font/count/width/gap/restoration steps
pass (1,509 Godot checks). The original 14-case baseline passed only 3 cases;
the additional uneven-width cases preserve the subpixel issue discovered during
live testing rather than widening tolerances. Twelve incremental draw lists also
match full rebuilds, including following content displaced by column height.

A fresh before/after pass over the same 309 frozen Chrome152 captures remains
**287/309**, with 22 differences and no newly failing fixtures. Paragraph
fragmentation, column spanning/rules/fill behavior and the broader findings below
remain open; these fixes do not establish full multicolumn conformance.

[Installed qualification](verification/multicol133.json) · [Broad comparison](verification/multicol-layout133.json).

## Historical preview98

Preview98 fixes the clearance/margin failures recorded below. Clearance now tests
the hypothetical collapsed border position, absorbs the clearing block's margin
and distinguishes an absent matching float from a float ending at zero. Empty
blocks record their border position before adjoining their bottom margin.

All **64 Chrome152 clearance cases agree**, up from 34/64. They cover ordinary
and flow-root parents, signed margins, preceding siblings, matching/nonmatching
sides and zero-height clearing blocks. Live margin changes/restoration also pass
in the Godot host and browser suites. [Portable evidence](verification/float-clear-preview98.json).
The broad result remains **287/309**, with no new differences.
[Current broad result](verification/layout-audit-preview98.json).

This proves the measured combinations, not every nested margin-collapse chain or
writing mode. Previous statements that the two minimized clearance bugs remain
open are historical. The remaining broad findings and product checklist still apply.

## Historical preview97 evidence

Preview97 additionally fixes a panel growing into a lower float after its width
shrinks and its contents wrap. The second inline pass now preserves the same
float isolation and owning style as the initial pass. Initial, changed-height and
restored-height browser/native checks verify panel and child coordinates. The broad
result remains **287/309**, with no new differences; captures are unchanged.
[Preview97 result](verification/layout-audit-preview97.json).

Two minimized clearance cases remain confirmed defects: an 80px float followed by
a clearing block with 30px top margin places that block at 110px instead of Chrome's
80px; a 40px float with 100px top margin places it at 140px instead of 100px.
Current core tests explicitly pin those old divergences. They require correction,
including margin-collapse interactions, rather than a test tolerance change.
[Portable inputs and current comparison](verification/float-clear-preview97.json)
preserve these failures. The overlap fix does not resolve the clearance cases.

## Preview96 evidence

The updated engine agrees with **287/309** unchanged Chrome152 captures from the
preview94 audit below: samples 29/47, hand 52/52, harvest 206/210. There are
**22 differences, zero crashes, and no new differing fixtures**. `cov-float` now
agrees. [Current machine-readable result](verification/layout-audit-preview96.json).

Float placement now follows source order and the available float band without
double-counting parent padding. Adjacent flow-root/overflow BFC panels shrink or
move below floats while respecting authored widths, percentages, margins, minimum
sizes and definite-height aspect ratios. Twelve focused cases pass both initially
and after live float-width changes: 215 host form checks and 493 Chrome checks
pass overall. Normal no-float layout retains its existing fast path.

The remaining vertical-writing, multicolumn and smaller findings are still open.
The synthetic comparison does not establish native font or full browser parity.
Preview95 was an intermediate package, superseded before installation by preview96
to correct auto-height aspect-ratio sizing.

## Historical preview94 audit

Fresh Chrome152 captures and the current headless Release build compared all 309
fixtures from `corpus/samples`, `corpus/hand` and `corpus/harvest` at 1280x720.
**286 agree, 23 differ, zero crashed, zero captures missing.**

This is the existing synthetic-font profile: normalized line-height and a Weva UA
overlay in Chrome. It isolates layout behavior, not native font/control appearance,
interactive state, GPU cost, or full browser conformance. The comparison was not
relaxed to hide small differences. Error magnitudes below are rounded by the
existing reporter; 0.0 means a nonzero difference rounded to one decimal place.

| Group | Agree | Differ | Total |
|---|---:|---:|---:|
| samples | 28 | 19 | 47 |
| hand | 52 | 0 | 52 |
| harvest | 206 | 4 | 210 |

## Findings

| Fixture | Largest reported difference | Differing values |
|---|---:|---:|
| samples/audit-validation | 0.1 px | 1 |
| samples/cov-float | 136.0 px | 18 |
| samples/cov-logical | 465.5 px | 23 |
| samples/cov-multicol | 462.7 px | 35 |
| samples/dialogue | 0.9 px | 17 |
| samples/glass | 0.4 px | 28 |
| samples/hud | 0.8 px | 5 |
| samples/inventory | 0.8 px | 37 |
| samples/layout-stress | 0.4 px | 19 |
| samples/leaderboard | 1.2 px | 68 |
| samples/map | 0.2 px | 11 |
| samples/match3-endgame | 0.0 px | 4 |
| samples/menu | 0.1 px | 2 |
| samples/quests | 0.8 px | 41 |
| samples/randhtml | 0.2 px | 68 |
| samples/settings | 0.2 px | 7 |
| samples/stats | 0.3 px | 20 |
| samples/vendor | 0.2 px | 29 |
| samples/weva-landing | 0.1 px | 3 |
| harvest/HtmlParserTests-00 | 0.5 px | 2 |
| harvest/SelectorCombinatorialTests-16 | 0.5 px | 2 |
| harvest/SnapshotMatcherTests-00 | 0.4 px | 1 |
| harvest/UpgradeMeterHeightTests-07 | 0.0 px | 1 |

The three large coverage fixtures exercise float/BFC placement, vertical writing
and multicolumn layout. Their differences are not ordinary rounding. The smaller
findings need attribution before being called harmless: synthetic font rounding,
table geometry and cumulative line layout can all contribute. None has been
waived, and the new five hand fixtures all pass.

The next layout investigation should minimize `cov-float` into avatar/text-flow
and BFC cases relevant to game chat or descriptions. Vertical writing and
multicolumn support retain their existing scope until separately resolved.

## Reproduction and provenance

[Machine-readable findings and fixture hashes](verification/layout-audit-preview94.json)
record the exact result, headless executable hash and source-content hash. Local
raw captures, generated engine coordinates and scripts are in
.utmp/safe-engine71/layout-audit94/. Source fixtures and checked-in golden captures
were not overwritten. The capture process completed normally and all 309 captures
succeeded; the comparator completed normally with 23 findings. Its successful
process exit is not a conformance pass.

The current fixture preparation expands declarative component templates before
measuring. It does not assert browser-native custom-element support. This audit
does not rerun every separate regression directory or the three-way C# arbitration
release gate. See [product readiness](PRODUCT_READINESS.md) for the full checklist.

## Minimized float/BFC defect

[Four text-free reproductions](verification/float-bfc-preview94.json) confirm a
float-avoidance defect independently of font metrics. In a 400px parent, a 100px
left float should leave the adjacent flow-root at x=100 with width=300; the engine
places it at x=0 with width=400. Right floats and overflow-hidden BFCs show the
same missing width exclusion; two left floats produce a 200px overlap.
All four reproduce without crashes. This is a confirmed next implementation task,
not a fixed or waived finding.
