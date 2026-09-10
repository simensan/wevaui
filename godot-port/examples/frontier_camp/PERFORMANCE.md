# Frontier Camp performance verification

Both local addons now contain build220 (ABI minor25): the stock-engine shaping
workaround, `@font-face` with real bold/italic files, gamepad navigation and
`WevaView` live reload on top of build211. Its qualification (core, sanitizers,
52 host entries, sample, fresh-consumer native export, export smokes and the
desktop timing profile, all passing) is in
[qualification220.json](../../docs/verification/qualification220.json). The
automatic 1080p and 4K 3D profiles pass 276/276 each with whole-frame p95 of
1-4 ms on the same busy scene that measured 8-17 ms in every earlier session
(the earlier waits were external and remain unexplained). The lifecycle soak
was not rerun for it. The build211 record follows.

Build211 (ABI minor24) previously contained incremental
font warmup in the sample loading screen, animation
composition, hidden-panel cancellation/restart, redundant redraw suppression,
and binding attribute allocation fixes. The current Windows RTX 5080 runs pass
72/72 desktop, 276/276 automatic 1080p 3D and 276/276 automatic 4K 3D timing checks
(six focused runs per profile, 600 frames per workload). Desktop API p95 ranges:
inventory sorting 0.846–1.174 ms, settings toggles 1.757–2.304 ms, typing
0.391–0.724 ms, redundant signals 0.100–0.145 ms. These separate profile runs
do not establish a causal speedup from the binding change. Core, sanitizers,
host/rendered, exports and 20 installed smoke suites pass. Core/sanitizer evidence
is inherited from unchanged build210 core source; host changes are tested natively.
Current lifecycle and lower-end
qualification remains open. Earlier failures remain historical
evidence with unresolved causes.
[Current installed qualification](../../docs/verification/font-warmup.json).

Build210's ten-minute 1080p Vulkan 3D lifecycle run passed 200 recreations and
287,158 soak frames with changing mixed-script names. Cold construction costs
92.684 ms CPU (94.517 ms through draw), hidden preparation 29.535 ms, and prepared
reuse with data updates 0.767 ms CPU p95 (12.962 ms through draw p95). Prepared
and cold images match. Prepare during loading and reuse; cold construction is
still unsuitable for an interactive frame. Soak frame p95 is 12.84 ms and max
31.265 ms. Whole-process private-memory minute medians rise from 821.680 to
823.152 MiB, peak 823.621 MiB. Recreation teardown node/object counts are stable;
the final post-soak object count is five higher. This is a finite observation,
not a leak-free claim or general resolution of earlier stalls.
[Build210 lifecycle evidence](../../docs/verification/lifecycle210.json).

The earlier CSS-diagnostics build204 passed all 72 desktop timing checks
at the declared 1280×720/manual-update scope across six focused runs. Core,
sanitizer, host, rendered and export checks also pass. All 276 automatic 1080p
busy-scene timing checks pass across six focused runs. Older 4K and lifecycle
measurements below remain historical and do not qualify this binary.
[Installed qualification](../../docs/verification/css-diagnostics.json).

The preceding at-rule containment build203 passes all 72 desktop timing checks
at the declared 1280×720/manual-update scope, across six focused runs. Core,
sanitizer, host, rendered and export checks pass. Busy-scene, 4K and lifecycle
qualification have not been repeated on this binary. The preceding failures
and diagnostics below remain preserved.
[Installed qualification](../../docs/verification/unknown-at-rules.json).

The preceding popover-transition build202 passes all 72 desktop timing gates
across six runs, with focus retained throughout. Correctness, sanitizers, both
renderer runs, sample/exports and installation smoke checks also pass. Its 1080p busy-scene profile passes 275/276 checks. One OpenGL slider
changed-frame p95 is 17.187 ms against 16.667 ms; all UI API/core limits pass.
The frame-delay cause is unresolved. A four-run onscreen/offscreen diagnostic
measured slider changed-frame p95 at 14.822–15.952 ms, with overlapping values
between positions. This does not clear the original failure. Current 4K checks
remain pending. [Diagnostic](../../docs/verification/presentation202.json).

The current five-minute Unicode lifecycle run passes 200 recreations and 22,103
name updates, with stable node/object counts. Cold construction is 84.389 ms CPU;
prepared reuse with data updates is 0.544 ms CPU p95 (12.447 ms through drawing).
Preload during loading and reuse the UI during gameplay. This observation does
not establish long-term leak freedom.
[Lifecycle evidence](../../docs/verification/lifecycle202.json).
[Current qualification](../../docs/verification/popover-beforetoggle.json).

The preceding build201 includes direct column-spanning headings (build201).
Correctness, sanitizer and export checks pass. All 72 desktop timing gates pass
across six runs, with focus retained throughout. All 276 timing gates also pass in the 1080p busy 3D scene across six runs,
with focus retained throughout. Current 4K and lifecycle qualification remains open.
[Build201 qualification](../../docs/verification/column-span.json).

The preceding build200 includes automatic closed-select sizing.
Correctness, sanitizer and export checks pass. All 72 build200 desktop timing
gates pass across six runs, with focus retained throughout.
[Build200 qualification](../../docs/verification/select-intrinsic.json).

The preceding build199 includes compact select sizing. All 72
desktop timing gates pass across six runs with focus retained throughout.
Changed API p95 is 1.097–1.280 ms for inventory sorting, 1.748–1.919 ms for
settings toggles and 0.395–0.478 ms for vitals at 10Hz. These are separate-run
observations, not causal speedups. The build198 load/lifecycle measurements
below remain historical and do not qualify the current binary.
[Build199 qualification](../../docs/verification/select-width.json).

## Reading timing evidence

Use `--frame-phases` for optional timestamps around Godot rendering callbacks.
Reports include the engine-reported VSync mode, frame cap and low-processor mode.
Callback intervals include waits and are not CPU/GPU attribution; instrumentation
adds overhead, so keep these diagnostics separate from timing qualification.
The first 1,800-sample diagnostic places the long intervals between rendering
callbacks, including UI-disabled frames, with VSync reported disabled. The exact
cause remains unresolved. [Evidence](../../docs/verification/frame-phases202.json).
A separate manual-render diagnostic still sees 20–21 ms worst intervals with
buffer swaps disabled, while reporting the same scene draw count. Buffer swapping
alone does not explain the stalls. It changes the rendering schedule and is not
a timing qualification. [Comparison](../../docs/verification/manual-presentation202.json).

The runner historically requests an offscreen window at `-10000,-10000`.
Focus counts do not establish visible presentation. Use `--window-position=0,0`
to compare onscreen presentation with the same exported build, resolution and
workloads. Reports record `requested_window_position`; this is a requested
position, not proof that the window was unobscured. The default remains unchanged
so existing comparisons keep their original setup. Whole-frame timing includes
presentation and scheduling; UI-disabled failures must not be attributed to UI
cost without further evidence.

Long runs can use `--run-timeout-seconds 900` to allow each benchmark process
more than the default 180 seconds. This changes the process watchdog only;
frame counts, samples and timing-budget limits remain unchanged. Reports record
the selected timeout. A timed-out run retains its failure report and partial
logs, and does not qualify timing. Import/export keep their own 180-second limit.
Each process also writes an `.exit.json` with its return code or timeout.
With `--background-input-isolation`, benchmark windows use no-focus and
mouse-passthrough flags; workload events
still enter through `Viewport.push_input`. Ordinary interactive sample windows
retain their normal input behavior.
The no-focus flag can affect focus acquisition, but does not prove an already
focused window lost focus. Older protected reports without focus counts cannot
establish focused-window coverage. `sync_ime()` requires actual window and
control focus; physical IME verification remains separate and required.
The default no longer sets those flags. Each workload records
`window_focused_samples` alongside `frames` so focus coverage is observable.

New budget reports include each metric's sample count, required minimum and
one-based nearest-rank p95 position. With ten changed frames, p95 selects the
slowest observation; with one hundred it selects rank 95. The same limits and
pass/fail rules apply. Missing sample counts remain invalid evidence.

The preceding gesture build (198) passes 72/72 desktop and 276/276 automatic
1080p 3D gates. Its 4K profile passes 260/276; the 16 failures are whole-frame
or changed-frame limits in the mobile renderer, including two UI-disabled
failures. All API timing gates and functional checks pass, and all measured
frames retain focus. These runs use 600 frames per workload, with only ten
changed clock samples per run. Its lifecycle qualification passes 200
recreations and a five-minute Unicode soak. Prepared reuse costs 0.522 ms CPU
p95 and 10.748 ms through draw p95; cold construction costs 80.612 ms CPU.
Sampled nodes/objects stay constant and minute-median private memory settles
at 862.831 MB. This is not a timing-budget pass or long-term leak proof.
[Build198 lifecycle evidence](../../docs/verification/lifecycle198.json).
[Build198 qualification](../../docs/verification/popover-outside.json) and
[readiness matrix](../../docs/PRODUCT_READINESS.md) track remaining work.

## Historical desktop and lifecycle qualifications

The entries below preserve measurements of earlier binaries. References to
"installed" in these historical entries describe their original checkpoints,
not the addon currently installed in the sample.

The installed stacking/opacity correction passes 72/72 manual desktop timing
checks on the explicit patched Godot release template. Across six 6,000-frame
runs, API p95 is 0.244–0.261 ms for hover, 0.544–0.844 ms for typing,
1.160–1.434 ms for inventory sorting and 2.044–2.479 ms for settings toggles.
These are API timings, not isolated GPU cost or a causal speedup. Earlier
measurements below retain their original binary identities.
[Qualification](../../docs/verification/opacity193.json).


A five-minute lifecycle follow-up passes 200 recreations and 161,306 Unicode
soak frames. Private-memory medians settle near 862.11 MB in the last two
minutes; node/object counts stay constant. Prepared reuse CPU p95 is 0.404 ms,
but reuse through draw p95 is 11.341 ms and cold construction CPU is 83.260 ms.
This is a memory/lifecycle observation, not a new timing-budget pass.
[Follow-up evidence](../../docs/verification/lifecycle185.json).

The installed addon has now been measured with an explicitly selected and hashed
patched Godot release template. The normal desktop profile passes all 72 timing
checks across three OpenGL and three Vulkan runs. API p95 ranges: vitals at 10Hz
0.276–0.707 ms, inventory sorting 0.737–1.216 ms, settings toggles 1.391–1.808 ms,
typing 0.391–0.830 ms, hover 0.202–0.444 ms. The automatic 1080p deterministic
3D profile passes all 276 timing checks. These runs do not prove a causal speedup.

The same installed addon passes 200 UI recreations and a 120-second Unicode soak
(127,069 frames, 21,619 name updates) on the explicit patched runtime. Cold first
construction costs 70.620 ms CPU; hidden preparation costs 19.044 ms. Prepared
reuse with hidden data updates costs 0.658 ms CPU p95 and 1.580 ms through draw
p95. Prepared/cold screenshots match. Private process memory rises from 852.72 MB
at soak start to 861.59 MB at the last soak marker and ends at 859.48 MB after
teardown; this is not proof of long-term memory stability.

The earlier performance exporter selected a cached/default template despite
using the patched editor. Its manual and 3D measurements apply to that recorded
executable, not to the intended patched runtime. Its Unicode lifecycle run
crashed with Windows heap corruption (0xC0000374); the cause remains unproven.
The exporter now accepts `--release-template`, records its SHA256 and restores
export presets even on failure. Both renderer smoke exports and 23 harness
tests pass. Stock-engine compatibility, 4K requalification, longer memory runs
and other hardware remain open.
[Explicit-template qualification](../../docs/verification/template184.json).

## Previous installed build measurements

Previous automatic 1080p 3D measurement: all six functional runs pass, but only
275/276 timing gates pass. OpenGL run 3's clock-update whole-frame p95 is
36.268 ms against 16.667 ms. That workload's maximum core update time is
0.967 ms; the cause of the remaining interval is not established. There are
only ten changed clock samples, so this p95 equals the worst changed interval.
The failed gate is retained. All UI API and core timing limits pass. Current
4K remains unmeasured. Full results are in the class157 verification record.


The installed long class-binding fix passes all 72 desktop timing checks in six
runs (three OpenGL, three Vulkan), 600 measured frames after 120 warmups at 720p.
API p95 ranges: HUD 0.373–0.501 ms, inventory sorting 0.966–1.119 ms, typing
0.439–0.559 ms, toggles 1.429–1.949 ms, redundant signals 0.163–0.202 ms and
hover 0.210–0.334 ms. Typing makes zero audio-setting updates in all six runs.
These current desktop results do not establish a causal speedup or qualify
other hardware. The automatic 1080p result above supersedes the earlier pending status.

The following results describe the preceding build.

The previous core build (checkpoint155) passed all 72 desktop timing checks.
The original 1080p automatic 3D profile passed 275/276 timing checks. After fixing
the sample's unrelated audio updates on name edits, six focused typing runs pass
all 24 unchanged typing limits and record zero `audio_setting_updates`. The
original failed profile remains in the evidence; the full corrected 3D profile
and 4K on this core build have not been repeated. See [current results](../../docs/RUNTIME_PERFORMANCE.md)
and [previous build evidence](../../docs/verification/binding155.json). Sections below
retain their historical scopes.

## Installed preview130 — cascade allocations

All **72/72** ordinary desktop API gates pass. Changed-frame API p95: vitals
**0.336–0.494 ms**, inventory **0.961–1.207 ms**, typing **0.472–0.602 ms**,
toggles **1.811–2.100 ms**; idle **0.007–0.010 ms**. Six runs measure 600 frames
after 120 warmups, manual/static 720p on the RTX 5080 desktop.

A focused allocation gate drops from **4,000 to zero C++ allocations** across
1,000 warmed simple cached cascades. It does not measure every CSS case or the
whole UI pipeline. The 48-field parent-highlight diagnostic measures
**0.914–1.069 ms** API p95; unpaired timings overlap previous results. Full cascade
traversal remains an optimization target. [Evidence](../../docs/verification/cascade-allocations130.json).

## Preview128 (historical) — relational selector cache scope

All **72/72** ordinary desktop API gates pass across six runs. Changed-frame API
p95: vitals **0.256–0.515 ms**, inventory **0.772–1.244 ms**, typing **0.418–0.693 ms**,
toggles **1.677–2.107 ms**; idle **0.006–0.010 ms**. Scope remains manual/static
720p on the RTX 5080 desktop, 600 measured frames after 120 warmups per run.

A 48-field parent-highlight diagnostic measures **0.955–1.279 ms** p95, compared
with **1.175–1.743 ms** previously. Timing ranges overlap. Stage tracing confirms
that 98–99 match lookups hit the cache and only one subject remains uncached,
versus 100 uncached previously. The full cascade still visits 100 elements.
These are API CPU diagnostics, not total UI/GPU measurements.
[Qualification](../../docs/verification/has-cache128.json).

## Preview126 (historical) — stable range updates

All **72/72** ordinary desktop API gates pass. Changed-frame API p95: vitals
**0.250–0.316 ms**, inventory **0.711–1.062 ms**, typing **0.298–0.496 ms**,
toggles **1.257–1.494 ms**; idle **0.006–0.007 ms**. Six runs measure 600 frames
after 120 warmups in the manual/static 720p RTX 5080 profile.

A separate range-form diagnostic measures stable 48-field parent-rule updates at
**0.049–0.061 ms** p95 in the final repeat, versus **0.936–1.318 ms** for preview125.
Boundary-crossing layout remains **2.165–2.199 ms**, with no improvement established.
This diagnostic excludes native input, setup, assertions, drawing and GPU; it is
not a timing acceptance gate. [Full evidence](../../docs/verification/stable-range126.json).

### Highlight-only diagnostic127 (runtime126)

For 48 fields, direct background highlighting measures **0.086–0.098 ms** API p95
across six runs. Parent `:has()` highlighting measures **1.175–1.743 ms**, versus
**1.535–2.201 ms** for parent width changes in the same profile. The stage probe
visits one element for direct rules and 100 for parent rules; all 100 matches are
uncached in the latter case. Stable-range updates visit zero elements.
This identifies broad cascade work as a remaining cost. No runtime change was
made. Color-style and geometry assertions pass; these diagnostics exclude drawing
and GPU. [Evidence](../../docs/verification/range-paint127.json).

## Preview125 (historical) — range selectors

All **72/72** desktop API gates pass across six runs. Changed-frame API p95:
vitals **0.247–0.371 ms**, inventory **0.793–1.086 ms**, typing **0.384–0.533 ms**,
toggles **1.334–1.885 ms**; idle **0.006–0.007 ms**. Each run measures 600 frames
after 120 warmups; scope is manual/static 1280×720 on the RTX 5080 desktop.
These unpaired timings do not establish a speedup, total UI/GPU cost, or the cost
of forms using many range selectors. Browser/core/host checks verify range
matching and live styling. [Qualification](../../docs/verification/range-selectors125.json).

## Preview124 (historical) — validation refresh and focus

All **72/72** unchanged desktop API gates pass across six runs, three per renderer,
600 measured frames after 120 warmups. Changed-frame API p95: vitals
**0.338–0.530 ms**, inventory **0.990–1.221 ms**, typing **0.443–0.639 ms**,
toggles **1.423–1.962 ms**; idle **0.006–0.009 ms**. Scope is manual/static
1280×720 on the RTX 5080 desktop. These are not total UI/GPU measurements or a
causal comparison with earlier builds. Validation-specific tests confirm removal
of redundant updates and correct callback/native focus synchronization.
[Full qualification](../../docs/verification/validation-refresh124.json).

## Preview121 (historical) — validation APIs

All **72/72** unchanged desktop gates pass across six runs, three per renderer,
600 measured frames after120 warmups. Changed-frame API p95: vitals
**0.323–0.397 ms**, inventory **0.772–1.153 ms**, typing **0.402–0.513 ms**,
toggles **1.436–1.881 ms**; idle API **0.006 ms**. Scope is manual/static
1280×720 RTX5080 desktop CPU. These ordinary sample workloads do not benchmark
the newly added explicit validation methods, total UI/GPU cost, or other hardware.
Unpaired results do not establish a causal change from119.
[Full qualification](../../docs/verification/explicit-validity121.json).

## Preview119 (historical) — number value sanitization

All **72/72** unchanged desktop gates pass across six runs (three per renderer,
600 measured frames after120 warmups). Changed-frame API p95: vitals
**0.276–0.376 ms**, inventory **0.739–1.003 ms**, typing **0.312–0.530 ms**,
toggles **1.302–1.692 ms**; idle API **0.006 ms**. Scope remains manual/static
1280×720 RTX5080 desktop CPU. The fix rejects malformed exponent suffixes during
value sanitization. These unpaired results do not establish a causal speedup.
Retained batches stay opt-in; full UI/GPU and newer lifecycle evidence remain open.
[Full qualification](../../docs/verification/exponent-sanitization119.json).

## Preview115 (historical) — DOM attribute reads

All **72/72** unchanged desktop gates pass across six runs (three per renderer,
600 measured frames, 120 warmups). Changed-frame API p95: vitals **0.348–0.395 ms**,
inventory **0.963–1.326 ms**, typing **0.455–0.589 ms**, toggles **1.512–1.791 ms**;
idle API **0.006–0.009 ms**. Scope remains manual/static 1280×720 RTX5080 desktop CPU.

Attribute reads no longer force pending layout. Native tests verify current DOM
readback without an update and exactly one update on the following geometry query.
The first OpenGL modal comparison retained the same update count as114; these
results do not establish a causal modal speedup. Timing variation remains present.
`core_update_count` now uses count keys (`mean`, `median`, `p95`, `p99`, `max`)
instead of the prior misleading `_ms` suffix.
[Full qualification](../../docs/verification/attribute-preview115.json).
Automatic3D evidence remains114; no newer full UI/GPU or lifecycle certification.


## Preview114 (historical) — cumulative core timing

All **72/72** unchanged desktop API timing gates pass across six runs. Changed-frame
API p95: vitals **0.347–0.438 ms**, inventory **1.019–1.357 ms**, typing
**0.464–0.631 ms**, toggles **1.613–2.009 ms**; idle API **0.006–0.007 ms**.
Scope: manual/static 1280×720 RTX5080 desktop, 600 frames and 120 warmups per workload.

`core_cpu` now uses cumulative counters across the frame, including deferred work;
`changed_core_cpu` reports the changed-frame subset. `core_update_count` records
all updates in that interval. Its numeric-stat keys retain the generic `_ms`
suffix, but its values are counts. Historical last-update-only core figures are
not comparable. API timings still exclude deferred work, while whole-frame timing
includes the scene and scheduling. Do not add overlapping API and core metrics.
[Qualification and raw measurements](../../docs/verification/timing-preview114.json).


## Preview113 (historical) — numeric editing

All **72/72** unchanged desktop timing gates pass: three runs per renderer,
600 measured frames and 120 warmups per workload. API p95 ranges across six runs:

| Workload | p95 ms |
| --- | ---: |
| Idle | 0.007–0.011 |
| Continuous vitals | 0.362–0.387 |
| Inventory reorder | 0.984–1.078 |
| Settings typing | 0.415–0.540 |
| Settings toggle | 1.626–2.051 |
| Redundant signal | 0.166–0.176 |

Active workloads use changed-frame API timings. The profile remains manual/static
1280×720 on the RTX5080 development desktop. These measurements do not isolate
a causal speedup, total GPU cost, or a deterministic worst-case bound. Busy 3D,
automatic scheduling, large-form validation and lower-end hardware need further
qualification. [Portable evidence](../../docs/verification/number-edit-preview113.json)
retains every gate and binary identity. Raw results: `.utmp/safe-engine71/number113-budget/`.


## Preview111 — length and temporal validation (historical)

The full repeat passes **72/72** unchanged timing gates across six runs (three
per renderer, 600 measured frames and 120 warmups). Changed-frame API p95 ranges:
continuous vitals **0.472–0.510 ms**, inventory **1.275–1.601 ms**, typing
**0.562–0.892 ms**, toggles **1.479–2.446 ms**, redundant signals **0.171–0.230 ms**.

The initial run passed 71/72: Vulkan clock p95 was 3.027 ms versus 3.0 ms.
Alternating frozen109/111 comparisons showed variable timings; the full repeat
used unchanged source and thresholds. The initial overrun and a diagnostic toggle
outlier remain in [the evidence](../../docs/verification/validation-preview111.json).
These results do not prove a speedup, absence of all regressions or a deterministic
worst-case bound. The scope remains manual/static 1280×720 RTX5080 desktop CPU;
large-form validation, GPU cost, busy 3D and lower-end hardware are not certified.


## Preview109 — form validation (historical)

The unchanged desktop budget passes **72/72 timing checks** across six runs
(three OpenGL, three Vulkan; 600 measured frames, 120 warmups per workload).
Changed-frame API p95 ranges: continuous vitals **0.354–0.438 ms**,
inventory reorder **0.783–1.162 ms**, typing **0.376–0.565 ms**,
settings toggle **1.347–1.936 ms**, redundant signals **0.155–0.213 ms**.

This includes queued form submission and required, number, custom, email and URL
validation. It verifies the ordinary sample workloads after those changes; it
does not benchmark worst-case validation of large forms. These are manual/static
1280×720 RTX 5080 development-desktop CPU measurements, not a speedup claim or
acceptance for total GPU cost, busy 3D, automatic scheduling or lower-end hardware.
[Portable evidence](../../docs/verification/validation-preview109.json) records the
binary and every timing gate. Raw results are in
`.utmp/safe-engine71/validation109-budget/`.

## Preview107 — dialog lifecycle (historical)

The unchanged development-desktop profile passes **72/72 timing checks** across
six runs (three OpenGL, three Vulkan; 600 measured frames, 120 warmups per workload).
Changed-frame API p95 ranges: continuous vitals **0.320–0.374 ms**,
inventory reorder **0.756–1.077 ms**, typing **0.389–0.484 ms**,
settings toggle **1.302–1.787 ms**, redundant signals **0.150–0.170 ms**.

This includes native dialog cancellation, Escape and result handling, plus the
sample fix that synchronizes game state from dialog close notifications. It is
the manual/static 1280×720 RTX 5080 development-desktop profile, not a speedup
claim, total GPU cost, busy 3D or lower-end hardware acceptance.
[Portable evidence](../../docs/verification/dialog-preview107.json) contains every
timing gate and binary identity; raw results are in `.utmp/safe-engine71/dialog107-budget/`.

## Preview105 — native font families (historical)

The existing desktop profile passes **72/72 timing checks** across six runs
(three OpenGL, three Vulkan; 600 measured frames and 120 warmups per workload).
Changed-frame API p95 ranges: continuous vitals **0.513–0.565 ms**, inventory
reordering **1.328–1.507 ms**, typing **0.680–0.760 ms**, settings toggles
**2.009–2.224 ms**, redundant signals **0.233–0.259 ms**.

These are manual/static 1280×720 measurements on the tested RTX 5080 development
desktop. They pass the unchanged budget; they are not a speedup claim, a total
GPU cost measurement or a benchmark of repeated multi-font resource changes.
The font-family feature has separate native metrics/pixel and lifecycle checks.
[Portable evidence](../../docs/verification/font-family-preview105.json) records
all timing checks and binary identity. Raw results are in
`.utmp/safe-engine71/family105-budget/`.

## Preview104 — default selection parity (historical)

Pristine input/textarea default mutations now preserve or reset selection as
required by the tested Chrome behavior. All 32 new browser-derived cases, ten core
suites, twelve sanitizer gates and 9,024 native checks pass. Packaged exports and
installed-binary checks pass. The unchanged desktop timing profile passes **72/72
checks** across six runs. Changed-frame API p95 ranges: redundant signals
0.190–0.268 ms; continuous vitals 0.432–0.586 ms; inventory sort 0.773–1.190 ms;
typing 0.352–0.585 ms; modal toggles 1.404–1.833 ms.

This is the same manual/static 1280×720 development-desktop profile, not a speedup
claim or certification for other hardware. Ordinary unchanged HUD text retains
its no-op path. [Portable evidence](../../docs/verification/default-selection-preview104.json)
records every timing check and candidate identity; raw measurements are in
`.utmp/safe-engine71/default-selection-budget/`.

## Preview103 — checked binding reads (historical)

Dictionary path segments now perform one checked lookup. Object segments use
Godot's checked property accessor instead of constructing and scanning a complete
property list on every refresh. A regression test first failed on the old binary
and now passes; live object changes, missing/null values and control writes remain
covered. All 8,928 native checks across 27 entries pass, as do fresh sample and
packaged debug/release/embedded export checks. Preview103 includes the LRU change
below and is installed in both local test projects, with preview100 backed up.

The unchanged desktop profile passes **72/72 timing checks** across three runs per
renderer. Changed-frame API CPU p95 ranges across the six runs:

| Workload | p95 range |
|---|---:|
| Redundant signals | 0.165–0.288 ms |
| Continuous vitals | 0.383–0.625 ms |
| Inventory sort | 1.086–1.533 ms |
| Settings typing | 0.490–0.883 ms |
| Modal toggles | 1.370–2.467 ms |

Scope: manual updates, static 1280×720 scene, 600 measured frames after 120 warmups,
RTX 5080 development desktop. This establishes this profile pass, not a statistical
speedup or an explanation for earlier timing variability. Old failed measurements
remain recorded below. [Portable evidence](../../docs/verification/binding-access-preview103.json)
contains every limit/value and source/binary metadata. Raw results are under
`.utmp/safe-engine71/binding-access-budget/`.

## Preview102 — incremental shaping-cache eviction (historical)

The cache now evicts the least recently used run instead of clearing all 4,096
entries. A regression test first failed on the old policy, then passed with this
change: a repeatedly used HUD label remains cached through 4,500 distinct changing
labels, the newest changing label also remains cached, and an old label is evicted
and correctly reshaped. All ten core suites, twelve sanitizer gates and fresh
Godot sample/import/export checks pass. This establishes behavior, not a speedup.

The six-run desktop timing gate **fails one of 72 checks**: Vulkan run 2 redundant
signals reach 0.321 ms p95 against 0.300 ms. All interaction checks pass. Focused
three-run Vulkan follow-up measures preview100 at 0.148–0.195 ms and preview102 at
0.221–0.224 ms. A subsequent reverse-order follow-up measures preview102 at
0.123–0.204 ms and preview100 at 0.182–0.391 ms. The overrun is not consistently
candidate-specific; its cause is unproven, and the original failure is not waived.

[Timing evidence](../../docs/verification/cache-lru-timing-preview102.json) preserves
the failed full profile and all focused follow-ups. Preview102 was not installed.
That candidate remained unqualified pending performance attribution
and the remaining release matrix. Its [retention check](LOAD_AND_LIFECYCLE.md)
confirms individual eviction, with a 13.93 MiB peak reported cache payload; there
is still an entry limit rather than a byte budget.

## Explicit CPU timing budgets

`run_frontier_perf.py --budget godot-port/examples/frontier_camp/tests/performance_budget_desktop.json`
now separates `functional_passed` from `timing_passed`. Without a budget,
`timing_passed` is null and the console says `NOT CHECKED`; the legacy `passed`
flag then covers only behavior. With a budget, either failure makes `passed`
false and the runner exits nonzero after preserving the report.

The checked-in profile contains **provisional engineering targets** for the
measured development desktop at 1280x720, static backdrop, manual updates:
1 ms changed-frame API CPU p95 for frequent HUD updates, typing and sliders;
2 ms for inventory sorting; 3 ms for modal toggles and the infrequent clock path;
0.5 ms hover; 0.3 ms redundant signals; 0.05 ms idle/disabled API calls.
These are targets to test, not a statement that every candidate meets them.
The profile requires both renderers, three runs each, 600 measured frames and
120 warmups. Every run must meet every limit; medians cannot hide an overrun.

The evaluator rejects missing cases, missing/nonfinite timings, insufficient
samples, duplicate runs, debug builds, failed behavior and mismatched measurement
scope. Adapter matching identifies a GPU, not the CPU or the whole machine;
this profile does not certify a hardware class, automatic scheduling, busy 3D,
other resolutions, total UI/GPU cost or long-term tail behavior.

Existing reports can be graded without changing them:

```powershell
python godot-port/hosts/godot/frontier_perf_budget.py --report path/to/summary.json --budget godot-port/examples/frontier_camp/tests/performance_budget_desktop.json --output path/to/budget-result.json
python -m unittest discover -s godot-port/hosts/godot -p test_frontier_perf_budget.py -v
```

The initial preview100 report is rejected for the two toggle overruns and missing
repetitions; its successful behavior flag does not override those findings.
Evidence: `.utmp/safe-engine71/budget-initial100.json`.

Preview100 subsequently passed the explicit profile: **72/72 limits across six
runs** (three per renderer), with the final evaluator independently grading the
saved measurements. Across those runs, changed-frame API p95 ranged from
0.326–0.362 ms for continuous clock updates, 0.339–0.392 ms for continuous vitals,
0.970–1.233 ms for inventory sorting, 0.313–0.719 ms for typing, and
1.241–2.227 ms for modal toggles. Fourteen evaluator tests pass, including negative
cases and nonzero CLI failure. [Portable evidence](../../docs/verification/performance-budget-preview100.json)
records the profile, hashes and every timing check. Raw measurements are under
`.utmp/safe-engine71/budget100-perf/`; no native runtime changes were needed for this gate.

Preview100 also completes a separate 1920×1080 automatic-update benchmark in
the animated 3D scene, one run per renderer. See the current
[load measurements](LOAD_AND_LIFECYCLE.md) for whole-frame observations and scope.
That run has no timing profile; it does not extend the static/manual budget pass.

## Preview100 initial field selection - 2026-09-08

The initial toggle result below prompted a sequential preview99/100 comparison,
with the same engine, unchanged sample-source hashes, renderers, resolution and
600 measured frames after 120 warmups. Toggle API CPU p95 (99 -> 100): OpenGL
2.050 -> 2.111 ms; Vulkan 1.872 -> 1.896 ms. The initial 5.374/3.872 ms result
did not repeat; no cause has been established. Typing was 0.536 -> 0.707 ms in
OpenGL and 0.455 -> 0.567 ms in Vulkan. One sequential comparison cannot establish
statistical equivalence or rule out a smaller regression. Evidence:
`.utmp/safe-engine71/selection-perf-comparison.json` and `selection-compare{99,100}-perf/`.

This initial report predates the explicit timing gate above: its `passed` flag
checks behavior and successful captures, not a latency threshold. The later
profile pass does not erase the initial overrun.

All twelve workloads pass in one OpenGL/Vulkan run, 600 measured frames after
120 warmups, 1280x720 static backdrop. Changed-frame API CPU p95 in ms:

- gl_compatibility: clock_update 0.408, vitals_update 0.386, hover 0.241, inventory_sort 1.182, settings_typing 0.692, settings_slider 0.545, settings_toggle 5.374.
- mobile: clock_update 0.311, vitals_update 0.332, hover 0.211, inventory_sort 0.949, settings_typing 0.977, settings_slider 0.454, settings_toggle 3.872.

Acceptance evidence only; not paired speedup/regression or total UI/GPU cost.
Nine captures match preview99. Evidence: `.utmp/safe-engine71/selection-initial-perf/summary.json`
and `selection-initial-pixel-comparison.json`.

## Preview99 retained field selections - 2026-09-08

All twelve workloads pass in one OpenGL/Vulkan run, 600 measured frames after
120 warmups, 1280x720 static backdrop. Changed-frame API CPU p95 in ms:

- gl_compatibility: clock_update 0.412, vitals_update 0.343, hover 0.207, inventory_sort 1.086, settings_typing 0.559, settings_slider 0.586, settings_toggle 2.370.
- mobile: clock_update 0.480, vitals_update 0.510, hover 0.213, inventory_sort 1.072, settings_typing 0.641, settings_slider 0.475, settings_toggle 2.046.

Acceptance evidence only; not paired speedup/regression or total UI/GPU cost.
Nine captures match preview98. Evidence: `.utmp/safe-engine71/selection-memory-perf/summary.json`
and `selection-memory-pixel-comparison.json`.

## Preview98 clearance and margins - 2026-09-08

All twelve workloads pass in one OpenGL/Vulkan run, 600 measured frames after
120 warmups, 1280x720 static backdrop. Changed-frame API CPU p95 in ms:

- gl_compatibility: clock_update 0.387, vitals_update 0.343, hover 0.217, inventory_sort 0.892, settings_typing 0.510, settings_slider 0.565, settings_toggle 2.100.
- mobile: clock_update 0.444, vitals_update 0.415, hover 0.195, inventory_sort 0.868, settings_typing 0.508, settings_slider 0.451, settings_toggle 1.846.

Acceptance evidence only; not paired speedup/regression or total UI/GPU cost.
Nine captures match preview97. Evidence: `.utmp/safe-engine71/clear-perf/summary.json`
and `clear-pixel-comparison.json`.

## Preview97 wrapped panel float avoidance - 2026-09-08

All twelve workloads pass in one OpenGL/Vulkan run, 600 measured frames after
120 warmups, 1280x720 static backdrop. Changed-frame API CPU p95 in ms:

- gl_compatibility: clock_update 0.347, vitals_update 0.328, hover 0.216, inventory_sort 0.849, settings_typing 0.546, settings_slider 0.437, settings_toggle 2.185.
- mobile: clock_update 0.416, vitals_update 0.406, hover 0.197, inventory_sort 0.931, settings_typing 0.571, settings_slider 0.454, settings_toggle 1.925.

Acceptance evidence only; not paired speedup/regression or total UI/GPU cost.
Nine captures match preview96. Evidence: `.utmp/safe-engine71/float-height-perf/summary.json`
and `float-height-pixel-comparison.json`.

## Preview96 float/BFC layout - 2026-09-08

All twelve workloads pass in one OpenGL/Vulkan run, 600 measured frames after
120 warmups, 1280x720 static backdrop. Changed-frame API CPU p95 in ms:

- gl_compatibility: clock_update 0.346, vitals_update 0.331, hover 0.214, inventory_sort 0.812, settings_typing 0.592, settings_slider 0.443, settings_toggle 2.060.
- mobile: clock_update 0.469, vitals_update 0.490, hover 0.214, inventory_sort 0.892, settings_typing 0.527, settings_slider 0.399, settings_toggle 2.023.

Acceptance evidence only; this does not establish paired speedup/regression or
total UI/GPU cost. Nine captures match preview94. Evidence:
`.utmp/safe-engine71/float-bfc-final-perf/summary.json` and
`float-bfc-final-pixel-comparison.json`.

## Preview94 skipped control painting - 2026-09-08

All twelve workloads pass in one OpenGL/Vulkan run, 600 measured frames after
120 warmups, 1280x720 static backdrop. Changed-frame API CPU p95 in ms:

- gl_compatibility: clock_update 0.378, vitals_update 0.331, hover 0.211, inventory_sort 0.916, settings_typing 0.589, settings_slider 0.587, settings_toggle 1.909.
- mobile: clock_update 0.261, vitals_update 0.252, hover 0.184, inventory_sort 0.762, settings_typing 0.372, settings_slider 0.413, settings_toggle 2.096.

Acceptance evidence only: this does not establish paired speedup/regression or
total UI/GPU cost. Nine captures match preview93. Hidden overlay text skips glyph
preparation; normal idle paths are unchanged. Evidence: `.utmp/safe-engine71/content-paint-perf/summary.json`
and `content-paint-pixel-comparison.json`.

## Preview93 skipped-content focus - 2026-09-08

All twelve workloads pass in one OpenGL/Vulkan run: 600 measured frames after
120 warmups, 1280x720 static backdrop. Changed-frame API CPU p95 in ms:

- gl_compatibility: clock_update 0.397, vitals_update 0.424, hover 0.244, inventory_sort 1.203, settings_typing 0.800, settings_slider 0.742, settings_toggle 1.937.
- mobile: clock_update 0.491, vitals_update 0.502, hover 0.221, inventory_sort 1.188, settings_typing 0.567, settings_slider 0.422, settings_toggle 1.890.

This acceptance run does not establish a paired speedup/regression or total UI/GPU
cost. Nine integration captures match preview92. Evidence: `.utmp/safe-engine71/
content-focus-perf/summary.json` and `content-focus-pixel-comparison.json`.

## Preview92 dialog focus - 2026-09-08

All twelve workloads pass in one OpenGL/Vulkan run, 600 measured frames after
120 warmups, 1280x720 static backdrop. Changed-frame API CPU p95 in ms:

- gl_compatibility: clock_update 0.402, vitals_update 0.339, hover 0.214, inventory_sort 0.836, settings_typing 0.574, settings_slider 0.461, settings_toggle 2.061.
- mobile: clock_update 0.486, vitals_update 0.503, hover 0.220, inventory_sort 1.009, settings_typing 0.458, settings_slider 0.435, settings_toggle 1.852.

This is acceptance evidence, not paired speedup/regression attribution or total
UI/GPU cost. All nine integration captures match preview91. Dialog delegate
validation resolves pending styles only while choosing focus; no new idle work
is added. Evidence: `.utmp/safe-engine71/dialog-focus-perf/summary.json` and
`dialog-focus-pixel-comparison.json`.



## Preview91 inert panels — 2026-09-08



Installed preview91 passes all twelve workloads in one OpenGL/Vulkan run,

600 measured frames after 120 warmups at 1280×720 with the static backdrop.

Changed-frame API CPU p95 in ms (OpenGL/Vulkan): clock 0.286/0.247,

vitals 0.325/0.263, hover 0.219/0.180, inventory sort 0.798/0.937,

typing 0.450/0.406, slider 0.378/0.429, settings toggle 1.824/1.977.

Sparse 1 Hz clock p95 is 1.159/1.380 ms. This is acceptance evidence, not

paired regression attribution or a total UI/GPU cost measurement.



All nine integration captures match preview90. Explicit inert ancestry is checked

on plausible hit candidates and input operations; cleanup is driven by mutations.

Normal workloads validate the shared input path, not a worst-case deep inert-tree

benchmark. Evidence: `.utmp/safe-engine71/inert-perf/summary.json` and

`inert-pixel-comparison.json`.



## Preview90 hidden-panel focus — 2026-09-08



Installed preview90 passes all 12 workloads in one OpenGL/Vulkan run,

600 measured frames after 120 warmups, 1280×720 static backdrop. Changed-frame

API CPU p95 in ms (OpenGL/Vulkan): clock 0.342/0.441, vitals 0.317/0.495,

hover 0.203/0.209, inventory sort 0.856/0.879, typing 0.570/0.449,

slider 0.376/0.530, settings toggle 1.971/1.882. Sparse 1 Hz clock p95

is 1.988/1.726 ms. This acceptance run does not establish a paired speedup,

regression attribution, or total UI/GPU cost.



All nine integration captures match preview89. Hidden-focus validation uses

already-computed styles on changed passes. An actual blur resolves affected

focus styling before painting; clean-frame early returns remain intact. Evidence

under `.utmp/safe-engine71/`: `panel-focus-perf/summary.json`,

`panel-focus-pixel-comparison.json`, and `panel-focus-asan-tests.log` (12 gates pass).

Earlier measurement limits remain below.



## Preview89 dialog focus restoration — 2026-09-08



Installed preview89 passes all 12 workloads in one OpenGL/Vulkan run,

600 measured frames after 120 warmups, 1280×720 static backdrop. Changed-frame

API CPU p95 in ms (OpenGL/Vulkan): clock 0.457/0.442, vitals 0.501/0.471,

hover 0.321/0.284, inventory sort 1.089/1.100, typing 0.583/0.564,

slider 0.593/0.497, settings toggle 1.591/1.411. Sparse 1 Hz clock p95

is 2.037/1.595 ms. Times moved in both directions relative to preview88;

this acceptance run does not establish a speedup, regression attribution,

or total UI/GPU cost. A paired experiment would be needed for attribution.



All nine integration captures match preview88. Explicit focus with pending style

mutations resolves its ancestor chain; ordinary clean frames do not. Closing

performs one pending focus check. Evidence under `.utmp/safe-engine71/`:

`dialog-restore-perf/summary.json`, `dialog-restore-pixel-comparison.json`, and

`dialog-restore-asan-tests.log` (12 gates pass). Earlier limits remain below.



## Preview88 modal input and top-layer ordering — 2026-09-08



Installed preview88 passes all 12 workloads in one OpenGL/Vulkan run,

600 measured frames after 120 warmups, 1280×720 static backdrop. Changed-frame

API CPU p95 in ms (OpenGL/Vulkan): clock 0.349/0.464, vitals 0.326/0.510,

hover 0.220/0.216, inventory sort 0.830/0.974, typing 0.441/0.573,

slider 0.449/0.417, settings toggle 1.799/1.879. Sparse 1 Hz clock p95

is 1.599/1.655 ms. This is one acceptance run, not a paired speedup claim

or total UI/GPU cost measurement.



All nine integration captures match preview87. Opening sequence lives in lazy

form state; Box size is unchanged. Ordinary input with no modal does not scan

the DOM. The modal layout transaction walks visible DOM hosts when retaining

subtrees, preserving separately promoted popovers; idle frames do not do this work.

Evidence under `.utmp/safe-engine71/`: `top-order-perf/summary.json`,

`top-order-pixel-comparison.json` and `top-order-asan-tests8.log` (12 gates pass).

Earlier timing uncertainties remain documented below.



## Preview87 dialog API state checks — 2026-09-08



Installed preview87 passes all 12 workloads in one OpenGL/Vulkan run,

600 measured frames after 120 warmups, 1280×720 static backdrop. Changed-frame

API CPU p95 in ms (OpenGL/Vulkan): clock 0.358/0.472, vitals 0.323/0.496,

hover 0.202/0.205, inventory sort 0.844/0.821, typing 0.485/0.528,

slider 0.556/0.410, settings toggle 1.779/1.919. Sparse 1 Hz clock p95

is 1.552/1.837 ms. This is one acceptance run, not a paired speedup claim or

total UI/GPU cost measurement.



All nine integration captures match preview86. Dialog guards and popover

dismissal execute on opening calls; they add no unconditional frame scan.

Evidence under `.utmp/safe-engine71/`: `dialog-api-perf/summary.json`,

`dialog-api-pixel-comparison.json`, and `dialog-api-asan-tests.log`

(12 gates pass). Earlier timing uncertainties remain documented below.



## Preview86 popover notifications — 2026-09-08



Installed preview86 passes all 12 workloads in one OpenGL/Vulkan run,

600 measured frames after 120 warmups, 1280×720 static backdrop. Changed-frame

API CPU p95 in ms (OpenGL/Vulkan): clock 0.362/0.434, vitals 0.321/0.464,

hover 0.196/0.207, inventory sort 0.892/0.891, typing 0.515/0.435,

slider 0.402/0.477, settings toggle 1.807/1.843. Sparse 1 Hz clock p95/max

is 1.626/1.861 ms from ten changed samples. This is one acceptance run,

not a paired speedup claim or total UI/GPU cost measurement.



All nine integration captures match preview85. Toggle state fits in the

existing event record; no ABI size change or steady-state allocation is added.

Evidence under `.utmp/safe-engine71/`: `popover-event-perf/summary.json`,

`popover-event-pixel-comparison.json`, and `popover-event-asan-tests.log`

(12 gates pass). The earlier timing uncertainties remain documented below.



## Preview85 modal and popover state — 2026-09-08



The preceding preview85 passed all 12 workloads in one OpenGL/Vulkan run,

600 measured frames after 120 warmups, 1280×720 static backdrop. Changed-frame

API CPU p95 in ms (OpenGL/Vulkan): clock 0.340/0.465, vitals 0.336/0.490,

hover 0.208/0.209, inventory sort 0.773/0.827, typing 0.440/0.518,

slider 0.487/0.454, settings toggle 1.779/1.837. Sparse 1 Hz clock p95/max

is 1.701/1.724 ms from ten changed samples. This is one acceptance run,

not a paired speedup claim or total UI/GPU cost measurement.



All nine integration captures match preview84. Live top-layer state uses

mutation versions; clean-state reads allocate nothing. Evidence under

`.utmp/safe-engine71/`: `top-state-perf/summary.json`,

`top-state-pixel-comparison.json`, and `top-state-asan-tests.log` (12 gates pass).

The earlier timing uncertainties remain documented below.



## Preview84 command-button submission — 2026-09-08



The preceding preview84 passed all 12 workloads in one OpenGL/Vulkan run,

600 measured frames after 120 warmups, 1280×720 static backdrop. Changed-frame

API CPU p95 in ms (OpenGL/Vulkan): clock 0.332/0.238, vitals 0.327/0.252,

hover 0.201/0.171, inventory sort 0.831/0.903, typing 0.530/0.406,

slider 0.423/0.429, settings toggle 2.142/2.108. Sparse 1 Hz clock p95/max

is 1.528/1.496 ms from ten changed samples. This is one acceptance run,

not a paired speedup claim or total UI/GPU cost measurement.



All nine integration captures match preview83. Submission classification now

reuses the allocation-free form helper instead of making a type string.

Evidence under `.utmp/safe-engine71/`: `submit-perf/summary.json`,

`submit-pixel-comparison.json`, and `submit-asan-tests.log` (12 gates pass).

The earlier timing uncertainties remain documented below.



## Preview83 default controls — 2026-09-08



The preceding preview83 passed all 12 workloads in one OpenGL/Vulkan run,

600 measured frames after 120 warmups, 1280×720 static backdrop. Changed-frame

API CPU p95 in ms (OpenGL/Vulkan): clock 0.375/0.476, vitals 0.352/0.490,

hover 0.213/0.211, inventory sort 0.835/1.014, typing 0.458/0.429,

slider 0.578/0.420, settings toggle 1.998/1.868. Sparse 1 Hz clock p95/max

is 1.891/2.319 ms from ten changed samples. This is one acceptance run,

not a paired speedup claim or total UI/GPU cost measurement.



All nine integration captures match preview82. Default-button dependencies

are refreshed only for relevant DOM mutations and sheets using `:default`.

This sample benchmark guards ordinary gameplay workloads; focused core/native

tests exercise the new selector and remote mutations. Evidence under

`.utmp/safe-engine71/`: `default-perf/summary.json`,

`default-pixel-comparison.json`, and `default-asan-tests.log` (12 gates pass).

The earlier timing uncertainties remain documented below.



## Preview82 read-only styling — 2026-09-08



The preceding preview82 passed all 12 workloads in one OpenGL/Vulkan run,

600 measured frames after 120 warmups, 1280×720 static backdrop. Changed-frame

API CPU p95 in ms (OpenGL/Vulkan): clock 0.487/0.313, vitals 0.393/0.320,

hover 0.218/0.191, inventory sort 0.986/1.007, typing 0.543/0.680,

slider 0.399/0.569, settings toggle 1.749/1.911. Sparse 1 Hz clock p95/max

is 1.421/1.638 ms from ten changed samples. This is one acceptance run,

not a paired speedup claim or total UI/GPU cost measurement.



All nine integration captures match preview81. Selector state is evaluated

when matching CSS, using existing DOM invalidation without new per-frame work.

Evidence under `.utmp/safe-engine71/`: `readwrite-perf/summary.json`,

`readwrite-pixel-comparison.json`, and `readwrite-asan-tests.log` (12 gates pass).

The earlier timing uncertainties remain documented below.



## Preview81 required/optional styling — 2026-09-08



The preceding preview81 passed all 12 workloads in one OpenGL/Vulkan run,

600 measured frames after 120 warmups, 1280×720 static backdrop. Changed-frame

API CPU p95 in ms (OpenGL/Vulkan): clock 0.394/0.481, vitals 0.329/0.513,

hover 0.200/0.209, inventory sort 0.841/0.941, typing 0.460/0.529,

slider 0.535/0.517, settings toggle 1.757/1.787. Sparse 1 Hz clock p95/max

is 1.834/1.844 ms from ten changed samples. This is one acceptance run,

not a paired speedup claim or total UI/GPU cost measurement.



All nine integration captures match preview80. Required/optional selectors

have focused core/native tests and Chrome comparisons. Evidence under

`.utmp/safe-engine71/`: `required-perf/summary.json`,

`required-pixel-comparison.json`, and `required-asan-tests.log` (12 gates pass).

The earlier timing uncertainties remain documented below.



## Preview80 disabled-action feedback — 2026-09-08



The preceding preview80 passed all 12 workloads in one OpenGL/Vulkan run,

600 measured frames after 120 warmups, 1280×720 static backdrop. Changed-frame

API CPU p95 in ms (OpenGL/Vulkan): clock 0.366/0.488, vitals 0.328/0.529,

hover 0.202/0.211, inventory sort 0.861/0.875, typing 0.469/0.510,

slider 0.480/0.505, settings toggle 1.896/1.866. Sparse 1 Hz clock p95/max

is 2.145/1.925 ms from ten changed samples. This is one acceptance run,

not a paired speedup claim or total UI/GPU cost measurement.



All nine integration captures match preview78. Disabled hover/pressed styling,

title tooltips and drag cancellation have focused core/native coverage.

Evidence under `.utmp/safe-engine71/`: `disabled-hover-perf/summary.json`,

`disabled-hover-pixel-comparison.json`, and `disabled-hover-asan-tests.log`

(12 gates pass). The earlier timing uncertainties remain documented below.



## Preview78 disabled groups — 2026-09-08



The preceding preview78 passed all 12 workloads in one OpenGL/Vulkan run,

600 measured frames after 120 warmups, 1280×720 static backdrop. Changed-frame

API CPU p95 in ms (OpenGL/Vulkan): clock 0.478/0.340, vitals 0.538/0.373,

hover 0.327/0.197, inventory sort 1.123/1.026, typing 0.469/0.572,

slider 0.581/0.443, settings toggle 1.345/1.807. The sparse 1 Hz clock has

ten changed samples with p95/max 2.299/1.670 ms. This is a single-run acceptance

check, not a paired speedup claim or total UI/GPU cost measurement.



All nine integration captures match preview77. Disabledness lookup allocates

no memory; focus validation runs when DOM inputs change. Evidence under

`.utmp/safe-engine71/`: `fieldset-perf/summary.json`,

`fieldset-pixel-comparison.json`, and `fieldset-asan-tests.log` (12 gates pass).



## Preview77 paragraph navigation — 2026-09-08



The preceding preview77 passed all 12 workloads in one OpenGL/Vulkan run,

600 measured frames after 120 warmups, 1280×720 static backdrop. Changed-frame

API CPU p95 in ms (OpenGL/Vulkan): clock 0.451/0.449, vitals 0.483/0.474,

hover 0.338/0.298, inventory sort 1.015/1.088, typing 0.527/0.597,

slider 0.580/0.597, settings toggle 1.569/1.536. The sparse 1 Hz clock has

ten changed samples with p95/max 1.719/1.448 ms. These are single-run checks,

not a paired speedup claim or total UI/GPU cost measurement.



All nine integration captures match preview76. The added paragraph search runs

only for Ctrl+Up/Down in a focused textarea. Evidence under

`.utmp/safe-engine71/`: `paragraph-perf/summary.json`,

`paragraph-pixel-comparison.json`, and `paragraph-asan-tests.log` (12 gates pass).



## Preview76 layout-box memory — 2026-09-08



The preceding preview76 removed the obsolete expanded-tab map and its dead lookup

path. A Linux x64 probe measures `sizeof(Box)` at 592 bytes, down from 608.

Three alternating pairs per renderer pass all 12 workloads, 600 measured

frames and 120 warmups at 1280×720, with 72 byte-identical workload captures.

Median changing-frame API p95 across runs:



| Workload | 75 OpenGL | 76 OpenGL | 75 Vulkan | 76 Vulkan |

| --- | ---: | ---: | ---: | ---: |

| Clock | 0.374 | 0.428 | 0.519 | 0.414 |

| Vitals | 0.400 | 0.437 | 0.455 | 0.450 |

| Hover | 0.275 | 0.274 | 0.248 | 0.203 |

| Inventory sort | 0.939 | 0.984 | 1.145 | 0.941 |

| Typing | 0.557 | 0.502 | 0.640 | 0.527 |

| Slider | 0.467 | 0.526 | 0.493 | 0.455 |

| Settings toggle | 2.064 | 1.968 | 2.001 | 2.082 |



These mixed results do not establish a general speedup. The broad Vulkan

clock whole-frame p95 also varied: 3.438→5.492 ms; UI-disabled medians were

5.484 ms for both versions. A focused three-pair clock run (1,200/240 frames)

measures API p95 0.270→0.274 ms OpenGL and 0.626→0.610 ms Vulkan, with

whole-frame p95 1.286→1.281 and 1.178→1.156 ms respectively. Three headless

pairs (6,000/1,000 frames) measure API p95 0.235→0.219 ms and core p95

0.1009→0.0970 ms. The cause of the broader rendered variation is unproven.



Evidence under `.utmp/safe-engine71/`: `box-map-paired-perf/comparison.json`,

`box-map-clock-only/comparison.json`, and `box-map-clock-headless/`. Correctness

gates pass: 10 core suites, 12 sanitizer gates, 8,659 native checks, packaged

exports and the relocated survival consumer. This is a verified memory

reduction, not a promise of lower frame time or lower-end hardware readiness.



## Preview75 exact tabs — 2026-09-08



The previous preview75 passed all 12 workloads on OpenGL and Vulkan, with 600

measured frames and 120 warmups at 1280×720, static backdrop. Changed-frame

API CPU p95 (OpenGL/Vulkan, ms): clock 0.312/0.267, vitals 0.318/0.333,

hover 0.204/0.182, inventory sort 0.860/0.832, typing 0.407/0.554,

slider 0.398/0.436, settings toggle 1.703/2.170. The sparse 1 Hz clock has

ten changed samples and p95/max 1.488/1.387 ms.



This is one timing recheck, not a paired speedup or total frame/GPU cost claim.

All nine integration captures match preview74. Exact tabs now use individual

fragments without expanded-space maps; ordinary untabbed strings skip tab

metric resolution. Sustained editing of large tabbed documents is not measured.

Evidence: `.utmp/safe-engine71/tab-exact-perf/summary.json`,

`tab75-pixel-comparison.json`, and `tab-block-asan-tests.log` (12 gates pass).



## Preview74 styled text and tab mapping — 2026-09-08



The previous preview74 passed all 12 workloads in one OpenGL/Vulkan run of

600 measured frames after 120 warmups, at 1280×720 with the static backdrop.

Changed-frame API CPU p95 is below; this is a final-export timing recheck,

not a paired speedup claim or a measurement of total frame/GPU cost.



| Workload | OpenGL (ms) | Vulkan (ms) |

| --- | ---: | ---: |

| Continuous clock | 0.450 | 0.474 |

| Continuous vitals | 0.344 | 0.495 |

| Hover | 0.200 | 0.216 |

| Inventory sort | 0.982 | 0.854 |

| Typing | 0.513 | 0.555 |

| Slider | 0.450 | 0.431 |

| Settings toggle | 1.793 | 1.701 |



The sparse 1 Hz clock has ten changed samples and p95/max 1.761/1.715 ms.

Normal runs do not allocate source maps; expanded textarea tabs allocate maps

during layout and retain them with their fragments. The final core allocation

gates and twelve ASan/UBSan gates pass. All nine integration captures match

preview72 across OpenGL, Vulkan and release export. These workloads do not

benchmark sustained editing of large tabbed documents.



Evidence: `.utmp/safe-engine71/tab-utf8-perf/summary.json`,

`tab-utf8-asan-tests.log` and `tab-utf8-pixel-comparison.json`. Earlier clock

timing uncertainty and the isolated hover assertion below remain open.



## Preview72 keyboard and textarea fixes — 2026-09-08



The installed preview72 passes all 12 workloads in one OpenGL/Vulkan run of

600 measured frames after 120 warmups, at 1280×720 with the static backdrop.

This is a functional/timing recheck of the final release export, not a paired

speedup claim. Changed-frame API CPU p95:



| Workload | OpenGL (ms) | Vulkan (ms) |

| --- | ---: | ---: |

| Continuous clock | 0.358 | 0.469 |

| Continuous vitals | 0.377 | 0.478 |

| Hover | 0.224 | 0.210 |

| Inventory sort | 0.823 | 0.966 |

| Typing | 0.488 | 0.577 |

| Slider | 0.394 | 0.445 |

| Settings toggle | 1.799 | 1.843 |



The sparse 1 Hz clock has only ten changed samples: its reported p95/max is

1.700/2.228 ms, so it must not be summarized using the continuous-clock row.

These API timings do not represent total frame or GPU cost. The prior paired

clock investigation and isolated hover failure below remain unresolved.

All nine integration captures match preview71 exactly; debug/release/embedded

exports and the final 12-gate ASan/UBSan run pass. The full timing distributions,

frame metrics, identities and captures are in

`.utmp/safe-engine71/fragment-perf/summary.json`; screenshot comparison is in

`fragment-pixel-comparison.json` in the same parent evidence directory.



## Preview71 transformed input — 2026-09-08



Preview71 fixes transformed pointer targets, text selection/autoscroll, range

dragging, scrollbar dragging and dropdown anchoring. Three alternating pairs

against preview70 use identical benchmark/game/UI sources and the same patched

engine with ICU. All 12 workloads pass on OpenGL and Vulkan; all 72 paired

captures are byte-identical. Median changed-frame API p95 across three runs:



| Workload | OpenGL 70 → 71 (ms) | Vulkan 70 → 71 (ms) |

| --- | ---: | ---: |

| Continuous clock | 0.354 → 0.424 | 0.363 → 0.428 |

| Continuous vitals | 0.417 → 0.401 | 0.431 → 0.456 |

| Hover | 0.254 → 0.292 | 0.283 → 0.285 |

| Inventory sort | 1.058 → 1.037 | 1.036 → 1.055 |

| Typing | 0.474 → 0.554 | 0.598 → 0.478 |

| Slider | 0.543 → 0.538 | 0.577 → 0.554 |

| Settings toggle | 1.454 → 1.494 | 1.627 → 1.472 |



The clock increase also appears in a focused rendered run (0.376 → 0.426 ms

OpenGL; 0.377 → 0.451 ms Vulkan). A stage trace shows the same work counts and

cache reuse with similar median stage times; it does not identify the cause.

Three alternating headless pairs with 6,000 measured / 1,000 warmup frames give

API p95 0.236 → 0.243 ms and core p95 0.1035 → 0.1076 ms. Rendering/window

scheduling therefore remains a possible contributor, not a demonstrated cause.

These results do not establish zero performance cost or a speedup.



One earlier focused hover/clock attempt failed its final hover assertion on

preview71 OpenGL. Subsequent hover-first runs pass three times on each version,

with all three capture pairs identical. The isolated failure remains unexplained;

the fixture now logs hovered IDs, mouse position and target rectangles on failure

without relaxing the assertion. That failed run is excluded from timing claims.



Evidence directories under `.utmp/safe-engine71/`: `transform-paired-perf`,

`transform-clock-only`, `transform-clock-profile`, `transform-clock-headless`,

`transform-clock-perf` (failed attempt), `hover-first-control70` and

`hover-first-candidate71`. Preview71 is installed with manifests matching its

binary; `installed71.json` records the installation and preview70 backup.



## Patched engine and embedded ICU export — 2026-09-08



The verified consumer export now uses the patched Windows release template,

preview70 addon and embedded Godot TextServer data. All twelve workloads pass

on OpenGL and Vulkan (one run each, 600 measured frames, 120 warmups, 1280×720).

Changed-frame synchronous API p95:



| Workload | OpenGL | Vulkan |

| --- | ---: | ---: |

| Continuous clock | 0.469 ms | 0.583 ms |

| Continuous vitals | 0.526 ms | 0.648 ms |

| Inventory sort | 1.923 ms | 1.606 ms |

| Settings typing | 0.898 ms | 0.786 ms |

| Settings slider | 0.909 ms | 0.804 ms |

| Settings toggle | 2.791 ms | 2.502 ms |



This is a functional/performance recheck of the actual exported game, not a

paired estimate of the engine patch or ICU data's cost. Earlier runs show

substantial variation, including on the same old binary. These numbers are

update costs, not total UI rendering/frame budgets. Evidence:

`.utmp/safe-engine71/frontier-integrated-perf/summary.json`; engine/template

identities and 92/98-check native integration results are in

`.utmp/safe-engine71/frontier-integrated/verification.json`.



## Repeated typing comparison — 2026-09-08



The suspected typing penalty from the single pair below did not reproduce in

three rotated-order runs per binary and renderer. Each uses 1,200 measured

frames, 240 warmups and the synchronous settings-typing workload. Median run

changed-frame API p95:



| Binary | OpenGL | Vulkan |

| --- | ---: | ---: |

| Previous runtime68 (`362354fb…`) | 0.977 ms | 0.940 ms |

| Installed preview70 (`7f37bcfa…`) | 0.967 ms | 0.962 ms |

| Experimental merged glyph walk (`09fe066f…`) | 1.182 ms | 1.013 ms |



The merged-walk experiment passes the font and host checks but is slower here,

so it was reverted. Source hashing confirms the worktree again matches the

installed preview70's build metadata. All nine captured typing images per

renderer are byte-identical across the three binaries. This does not establish

a zero-cost guarantee, but it does not support the earlier ~0.13 ms regression.

No new DLL is installed for this follow-up.



Evidence: `.utmp/parity68/fontwalk-comparison/{runs,medians,pixels}.json` and

the nine retained release-run reports. These are focused typing measurements;

the full-workload evidence below remains separate.



## Preview70 font correction recheck — 2026-09-08



Installed binary `7f37bcfa8497eb0b9834adf49a301d311836c74b21003ee88a78961caf94309f`

preserves styled fallback glyphs and accent placement while using regular primary

advances. Ordinary primary labels shape once; fallback/positioned-mark misses may

shape twice. Native dictionary keys are reused during the synthesis check.



A short comparison uses the previous installed runtime68 (`362354fb…`) and

preview70, the same sample scripts, 1280×720 native release exports, 600 measured

frames and 120 warmups per workload. Each renderer has one run per binary; order

is reversed for the second binary. Synchronous changed-frame API p95:



| Workload | OpenGL previous → current | Vulkan previous → current |

| --- | ---: | ---: |

| Continuous vitals | 0.499 → 0.511 ms | 0.567 → 0.563 ms |

| Inventory sort | 1.565 → 1.538 ms | 1.624 → 1.649 ms |

| Settings typing | 1.013 → 1.186 ms | 0.835 → 0.965 ms |

| Settings toggle | 2.680 → 2.799 ms | 2.710 → 2.785 ms |



All workloads pass. Both binaries are slower than earlier sessions, including

idle (0.011–0.015 ms previously; 0.013–0.015 ms currently). This single pair is

insufficient to attribute small differences to the correction. Typing is higher

in both renderers here; the subsequent repeated comparison above does not

reproduce that difference. These are measured update costs, not full frame

costs or guaranteed budgets.



Evidence under `.utmp/parity68`: `fontfinal-before-perf/summary.json` and

`fontkeys-perf/summary.json`. The intermediate `09dfc806…` binary additionally

passes all twelve synchronous workloads on both renderers (`fontfinal-perf`).

Six automatic-schedule runs pass on intermediate `8cf07563…` (`fontfix-perf`);

their API timers exclude deferred work and must not be quoted as total UI cost.



## Latest control-text / synthetic-bold candidate — 2026-09-08



Candidate `705e0278a006bbcbecf3dd949b1885ee0ee3fb1df5373fa2c30e603c18baad00`

passes all twelve workloads across three OpenGL and three Vulkan release runs,

600 measured frames and 120 warmups per workload at 1280×720. Median run p95

synchronous API CPU (changed frames, except idle):



| Workload | OpenGL | Vulkan |

| --- | ---: | ---: |

| Idle | 0.005 ms | 0.006 ms |

| Continuous clock | 0.332 ms | 0.284 ms |

| Continuous vitals | 0.319 ms | 0.293 ms |

| Inventory sort | 0.742 ms | 0.896 ms |

| Settings typing | 0.474 ms | 0.331 ms |

| Settings slider | 0.381 ms | 0.416 ms |

| Settings toggle | 1.364 ms | 1.801 ms |



Evidence: `.utmp/parity68/bold-perf/summary.json` and `medians.json`. These

standalone repetitions do not establish a causal speedup over earlier runs.

The subsequent slider-only candidate is installed as runtime68 after package,

relocation and rendered-export checks. Its default range decoration is removed;

these timings identify the preceding synthetic-bold binary. Full-release text

safety and real-device/platform requirements remain open.



## Runtime68 candidate — verification in progress (2026-09-08)



The candidate is not installed. The new query-input guard measures about 0.036 ms

for 200 descendants with zero warm allocations; this is one stage, not total UI

cost. Live container-query mutations match fresh-frame rendering and skip idle work.



The initial three-run GL/Vulkan comparison suggested a slowdown, but longer

counterbalanced OpenGL runs did not reproduce it for sorting, typing or modals.

Six runs per runtime, alternating runtime order, use 1,200 measured frames and

240 warmups per workload with identical UI source hashes. Median changed-frame

synchronous API CPU p95 (runtime67 → runtime68):



| Workload | Runtime67 | Runtime68 |

| --- | ---: | ---: |

| Vitals | 0.378 ms | 0.399 ms |

| Inventory sort | 1.152 ms | 1.123 ms |

| Settings typing | 0.879 ms | 0.601 ms |

| Settings toggle | 2.242 ms | 1.900 ms |



All runs passed functional checks. Variation remains substantial; these numbers

are not proof of a speedup or a lower-end hardware budget. Stage profiling also

found no core increase sufficient to explain the earlier typing difference.

Evidence: `.utmp/parity68/recheck/comparison.json`, the twelve adjacent run

summaries, and `.utmp/parity68/profiling/stages.json`. The measured candidate hash

is `cdada6bd99d47ca1257e78f982fae60cf37f22c2059683e90786e65c3ec3a11e`;

the subsequent inline-fragment candidate is measured separately below.

Automatic-update timings use a different API boundary and exclude deferred work.





The subsequent inline-bounds candidate

`58ab825f3570c5583a0eb2d506a639d6aa4567594801f76e860692bc60901a00`

passes a fresh release export and three runs per renderer (600 measured frames,

120 warmups, 1280×720). Median synchronous API CPU p95:



| Workload | OpenGL | Vulkan |

| --- | ---: | ---: |

| Idle (all frames) | 0.006 ms | 0.007 ms |

| Continuous clock updates | 0.429 ms | 0.405 ms |

| Continuous vitals updates | 0.374 ms | 0.461 ms |

| Inventory sort | 1.326 ms | 1.287 ms |

| Settings typing | 0.664 ms | 0.624 ms |

| Settings slider | 0.778 ms | 0.547 ms |

| Settings toggle | 2.410 ms | 1.523 ms |



Changed frames are used except for idle. These are standalone repetitions,

not a new paired baseline comparison; do not infer a regression or improvement

from differences between the tables. All twelve workload checks passed in all

six runs. Full evidence: `.utmp/parity68/inline-perf/summary.json` and

`medians.json`. The 120-second busy-scene lifecycle check passes; a longer

memory run is pending. See [lifecycle evidence](LOAD_AND_LIFECYCLE.md).





## Runtime67 Chrome fixes: performance check (2026-09-07)



The installed runtime67 resolves the sampled Frontier geometry differences; see

[CHROME_PARITY.md](CHROME_PARITY.md). A fresh alternating runtime66/runtime67

comparison uses three exported release runs per renderer, 600 measured frames

and 120 warmups per workload at 1280×720 on the same RTX 5080 machine.

Values below are median **changed-frame API CPU p95**, in milliseconds. This

includes synchronous UI work, not the whole frame or GPU time.



| Workload | OpenGL 66 → 67 | Vulkan 66 → 67 |

|---|---:|---:|

| Continuous HUD | 0.402 → 0.388 | 0.440 → 0.459 |

| Inventory reorder | 1.035 → 1.044 | 1.108 → 1.162 |

| Typing | 0.632 → 0.593 | 0.584 → 0.642 |

| Modal toggle | 1.611 → 1.691 | 1.594 → 1.580 |



The measured increases are at most 0.080 ms, with improvements in other cells.

These short paired runs show similar costs; they do not establish statistical

non-regression or lower-end hardware readiness. Evidence and binary hashes:

`.utmp/parity67/paired/comparison.json`, with complete per-run CPU/frame/GPU

metrics beside it. The previous runtime66 report below remains historical.





## Runtime66: modal layout (2026-09-07)



Settings opening/closing now retains unchanged positioned HUD panels.

In three matched Windows release pairs per renderer, changing-frame CPU p95

falls from **2.380 to 1.483 ms on OpenGL** and **1.936 to 1.477 ms on Vulkan**

(38% and 24%). All 84 before/after workload screenshots match. Shared or

unsupported layout dependencies retain the full rebuild path.



See [modal measurements and verification](MODAL_PERFORMANCE.md) for the full

table, non-modal follow-up, limits and reproduction. The runtime65 sections

below retain the earlier cold/load/lifecycle evidence and historical costs.



## Runtime65: interactions and load (2026-09-07)



Windows release exports, Godot 4.7.2, Ryzen 7 9800X3D and RTX 5080.

A new matched comparison against runtime64 reduces inventory reordering by

50–54% and typing by 50–56%. Settings toggles remain the largest measured

ordinary interaction at **2.39–2.63 ms p95**. They still rebuild layout and

paint when the modal/backdrop enters or leaves the box tree. The small

settings difference is not evidence of a substantial optimization.



Three paired runs per runtime and renderer at 1280 × 720, 120 warmup and

600 measured frames per case, with runtime, renderer and workload order

alternating. Numbers are medians of per-run p95 CPU times in milliseconds;

non-idle rows count only changing frames. The CPU scope includes native

input, game actions, bindings, document update and texture preparation,

excluding later canvas submission and GPU execution. No builds, other tests

or diagnostic profiling ran concurrently with these measurements.



| Case | Runtime64 OpenGL | Runtime65 OpenGL | Runtime64 Vulkan | Runtime65 Vulkan |

| --- | ---: | ---: | ---: | ---: |

| Idle document tick | 0.006 | 0.007 | 0.008 | 0.006 |

| Two meters + labels, continuous | 0.334 | 0.346 | 0.469 | 0.487 |

| Two meters + labels, simulated 10 Hz | 0.445 | 0.455 | 0.511 | 0.529 |

| Inventory reverse-order click | 1.841 | 0.840 | 1.933 | 0.970 |

| Typing in the bound name field | 1.090 | 0.482 | 1.124 | 0.562 |

| Bound volume slider | 0.516 | 0.417 | 0.550 | 0.455 |

| Open/close settings | 2.692 | 2.630 | 2.535 | 2.393 |



These are newly measured controls, so runtime64 numbers differ somewhat

from its earlier run below. Continuous HUD cost remains sub-millisecond on

this desktop; the small differences between runs do not establish a HUD

speedup. This is not a worst-case bound.



### Changes and correctness



- Reordering the same unique explicit `data-key` set moves existing rows.

  Controls retain handles, focus, live values and undo history. Membership

  changes, duplicate/missing identities and interleaved rows retain the

  conservative rebuilding path. Selector and layout invalidation follows

  the changed parent; unsupported layout dependencies still rebuild.

- Queries follow current DOM order through a cache keyed by the document's

  structure version. Stable element handles no longer imply query order.

  The lifecycle check caught this distinction after row preservation was added.

- Windows keeps an active IME context between caret repaints. Reassociation

  is limited to target/window changes; caret positioning still updates when

  needed. Focus/visibility/window teardown closes the context. Non-Windows

  focus handling is unchanged. Real installed IME/device testing remains open.



All **84 before/after workload screenshots are byte-identical**. Core checks

pass 9/9 CTest suites and 11/11 ASan/UBSan suites, including positive controls

and the 47-sample mutation corpus. Godot host checks pass **8,440 assertions**.

New tests compare retained and forced-full draws across seven layout/selector

variants and repeated reorders; they also verify DOM query order, input

identity, focus, live model updates and undo history.



The Windows addon ZIP passes Frontier Camp's fresh-project checks: 89

headless assertions, 95 on each renderer with captures, and a relocated

release export with project-identical pixels. Packaging/build receipts tie

the library to its source and dependency hashes.



### Cold opening, 3D load and lifecycle



See [load and lifecycle verification](LOAD_AND_LIFECYCLE.md) for actual 1080p

and 4K runs, hidden preparation, retained-screen reopening, process-memory

sampling and repeated destruction. The runner adds a deterministic animated

3D scene and measures the normal `_process`/deferred binding schedule. This

extends coverage beyond the old static backdrop; target-game and lower-end

physical hardware measurements remain required.



Fresh-process Frontier Camp UI construction is still 57–75 ms, plus 28–36 ms

for scene resources. Preparing once and retaining the screen gives measured

reopen CPU p95 of 0.365–0.424 ms. All 12 3D load runs, 900 measured reopens,

900 recreations and nine minutes of sustained updates pass. Node counts remain stable; object counts do not accumulate across

recreation. Process-memory variation during the soak is reported explicitly. The original generic HUD cold fixture now measures

62.863 ms median of five run means, with substantial variation.



### Reproduce current checks



Install the matching addon and Godot export templates, then run from the

repository root. Each output directory must be new:



```sh

python godot-port/hosts/godot/run_frontier_perf.py --godot /path/to/godot \

  --output /new/interactions --runs 3 --frames 600 --warmups 120 --capture



python godot-port/hosts/godot/run_frontier_perf.py --godot /path/to/godot \

  --output /new/load-1080p --automatic --world 3d --resolution 1920x1080 \

  --runs 3 --frames 600 --warmups 120 --capture \

  --cases ui_disabled,idle,vitals_10hz,inventory_sort,settings_typing,settings_slider,settings_toggle,hover



python godot-port/hosts/godot/run_frontier_lifecycle.py \

  --executable /new/load-1080p/export/FrontierCamp.exe \

  --project godot-port/examples/frontier_camp --output /new/lifecycle \

  --renderer mobile --resolution 1920x1080 --world 3d \

  --cycles 500 --seconds 300 --first-hidden

```



Repeat the load command with `--resolution 3840x2160`. `--executable` can

reuse an export built from an unchanged matching project; both runners

verify its native library and record source hashes. The lifecycle runner

also checks the requested renderer, resolution, mode, cycles and elapsed

soak duration, rejects assertion errors and compares cold/prepared pixels.

Preparation starts in `boot.tscn` before any UI is constructed. It invokes

`-- --lifecycle`; export templates cannot use the editor's `--script` runner.



Local receipts are under `.utmp/runtime65/`: `paired/comparison.json`,

`host-checks/verification.json`, `verified/verification.json`,

`core-tests2.log`, `asan-tests.log`, and the load/lifecycle paths in the

linked report. The paired receipt includes all individual runs and 84 PNG

comparisons. Diagnostic settings attribution is kept separately in

`profile-settings2/profile.log` and is excluded from timing tables.



Runtime65 is installed in the development and Frontier Camp projects, with

hash-checked runtime64 backups. Actual-project binding checks and Frontier

Camp headless/OpenGL/Vulkan checks pass (`.utmp/runtime65/installed.json`).



Native library SHA-256:



- Runtime64: `44b046f2768074844623f7f4b34df93638bbd55504c965e6260ef5a8e63f549f`

- Runtime65: `4504fdf5c5fc2712b1d87dbe97b4246541dcbf61b5babdf1ef23cb60136a8e9f`



## Historical runtime64 checkpoint



The remainder records the earlier runtime63 → runtime64 comparison and its

coverage at that time. Current costs and additional coverage are above.



Measured 2026-09-07 using Windows **release exports**, Godot 4.7.2,

Ryzen 7 9800X3D and RTX 5080, at 1280 × 720. This report compares the

previous runtime63 with the installed runtime64 using the actual sample.



**Ordinary bindings now use incremental updates.** Continuous two-meter

updates take 0.33–0.44 ms p95; at the sample’s 10 Hz cadence, changing-frame

p95 is 0.52–0.58 ms and average update cost is 0.059–0.067 ms per frame.

Idle CPU document ticks remain about 0.006–0.007 ms p95.



### Matched release-build measurements



Three runs per runtime and renderer, 120 warmup and 600 measured frames per

case. Runtime order alternates between pairs; renderer and workload order

also alternate. Values are medians of the three per-run p95s, in milliseconds.

Except idle, the table uses **changing frames only**. No concurrent builds,

other tests or profiling logs run during the measurements.



| Case | Before OpenGL | After OpenGL | Before Vulkan | After Vulkan |

| --- | ---: | ---: | ---: | ---: |

| Idle document tick | 0.007 | 0.007 | 0.007 | 0.006 |

| Clock label, continuous | 1.563 | 0.427 | 1.628 | 0.432 |

| Two meters + labels, continuous | 1.451 | 0.331 | 1.459 | 0.440 |

| Two meters + labels, simulated 10 Hz | 1.692 | 0.515 | 1.732 | 0.580 |

| Typing in the bound name field | 2.897 | 1.207 | 2.890 | 1.208 |

| Bound volume slider | 2.064 | 0.528 | 2.193 | 0.524 |

| Open/close settings | 3.769 | 2.816 | 3.807 | 2.640 |

| Inventory reverse-order click | 2.061 | 2.001 | 2.017 | 2.009 |

| Pointer hover change | 0.224 | 0.209 | 0.203 | 0.203 |

| Redundant signal, unchanged data | 0.181 | 0.160 | 0.185 | 0.163 |

| Clock, simulated 1 Hz (10 events/run) | 2.474 | 1.763 | 2.612 | 1.608 |



The CPU timer includes game actions, native Godot viewport mouse/keyboard

input where applicable, binding refresh, layout/paint update and texture

preparation. It excludes later canvas submission and GPU work. This pass

flushes the helper’s deferred binding work synchronously and explicitly ticks

once. The separate normal-schedule pass below leaves `_process` and deferred

callbacks enabled. Controls, typed model values and displayed content are

asserted after each workload.



Continuous meter p95 improves by **77% on OpenGL and 70% on Vulkan**.

Typing improves by about 58%; slider edits by 74–76%; settings toggles by

25–31%. Inventory sorting remains about 2 ms because reordered rows still

rebuild layout. Settings transitions remain about 2.6–2.8 ms. Those are

interaction costs, not an idle per-frame charge.



The low-frequency clock still has a 1.6–1.8 ms p95 tail in this short run;

it has only ten measured changes per run. Its per-run p95 ranges from

0.504 to 2.153 ms across the two renderers. A separate diagnostic profile

confirms local layout updates, with occasional paint-cache misses while new

text is prepared. The 600-event continuous cases are stronger evidence for

steady update cost; this is not an all-events-under-1-ms claim.



### What changed



- Binding substitutions record actual DOM mutations. Text and image sources

  queue content inputs; attributes restyle their selector scope. Repeat

  structure keeps the full rebuild and handle cleanup it requires.

- Caret, selection and composition updates invalidate the affected controls,

  including both controls on focus changes, instead of the whole paint cache.

- Dialog box changes retain their cascade origin. Dialog/focus writes can

  share the final layout, and focus reveal waits for updated geometry.

- Parsed Godot binding paths are cached with bounded storage. Live Dictionary

  values are still read each refresh. Small forms find model controls in one

  scan; larger forms use the dynamic fallback.

- Removed rows release their computed styles after the previous box/paint pass

  is replaced. Reordering no longer accumulates dead styles indefinitely.

- A retained-grid margin-collapse bug found by the new comparisons is fixed.

  The HUD name has an explicit 170px slot, preserving its previous pixels

  while avoiding intrinsic-size changes around the name on each edit.



### Pixel and correctness evidence



All **84 before/after workload screenshots are byte-identical**. Each runtime

also has 12 byte-identical bound/direct screenshot pairs. The sample still

uses the same `bind_state`, change signal and `data-model` authoring API.



Final checks: 9/9 core CTest suites; 11/11 ASan/UBSan suites, including positive

controls and the 47-sample mutation corpus; **8,436 Godot host checks**; the

packaged addon’s 17-check example; and Frontier Camp’s 89 checks headlessly,

on OpenGL and on Vulkan. Screenshot runs add six checks. A relocated Windows

release export also passes, with pixels identical to the project.



New core comparisons cover binding text/style/boolean/class updates, list

changes, removal/reload before update, selector dependencies, multiple layout

modes, blink, selection, IME, undo/redo and focus. They compare draws against

forced full rebuilds and assert retained draws on local updates. Godot checks

also cover more models than the stack buffer and more parsed paths than the

bounded cache, including shared-data mutation and reload.



### Renderer and normal scheduling



For idle UI, median per-run mean viewport CPU submission rises from

0.097 to 0.176 ms on OpenGL and 0.016 to 0.029 ms on Vulkan when UI is enabled.

GPU time rises from 0.038 to 0.051 ms and 0.017 to 0.020 ms, respectively.

The disabled baseline hides UI and disables its processing while retaining

its resources. These are rendering deltas, not memory savings.



The visible idle UI contains 116 DOM elements, 169 Weva draw commands and

2,932 triangles. Godot reports 44 canvas draw calls including the backdrop

scene, versus two with UI disabled. Commands are not hardware draw calls.



Three additional runs per renderer keep Godot’s native `_process` and the

helper’s deferred refresh schedule. Median of per-run mean complete-frame

intervals, in milliseconds:



| Case | OpenGL | Vulkan |

| --- | ---: | ---: |

| UI disabled | 2.203 | 2.933 |

| Idle UI | 2.284 | 2.937 |

| 10 Hz vitals | 2.300 | 2.878 |

| Typing | 2.501 | 2.953 |

| Slider | 2.382 | 2.877 |

| Settings toggles | 2.786 | 2.973 |



Windows offscreen presentation and scheduling are noisy; some intervals

decrease despite added work. Do not turn these differences into a promised

FPS impact for a 3D game. VSync is disabled, presentation is uncapped and

simulation steps are fixed at 1/60 second. No busy 3D scene, lower-end device,

1080p/4K GPU load or cold startup is covered. Godot’s static-memory monitor

is unavailable in this release template; receipts report null. No zero-

allocation claim is made.



### Reproduce



Install the matching addon and Godot export templates, then run from the

repository root:



```sh

python godot-port/hosts/godot/run_frontier_perf.py --godot /path/to/godot \

  --output /new/results --runs 3 --frames 600 --warmups 120 --capture



python godot-port/hosts/godot/run_frontier_perf.py --godot /path/to/godot \

  --output /new/native-schedule --automatic --runs 3 \

  --cases ui_disabled,idle,vitals_10hz,settings_typing,settings_slider,settings_toggle



python godot-port/hosts/godot/run_frontier_perf.py --godot /path/to/godot \

  --output /new/direct-comparison --runs 3 --capture \

  --cases clock_update,clock_direct,vitals_update,vitals_direct

```



Use `--project` to target an isolated sample and `--run-offset` to continue

alternating order across paired invocations. `--executable` reuses an already

exported matching project; the caller must keep its scripts/assets unchanged.

The runner verifies the native library, captures source/build fingerprints,

records all runs and fails on workload assertions or bound/direct pixel diffs.



Local evidence: `.utmp/runtime64/paired/comparison.json` and the adjacent

per-run JSON/logs/PNGs; `.utmp/runtime64/automatic/summary.json`; core and

sanitizer logs; `host-checks/verification.json`; `verified/verification.json`;

and `installed.json`. `paired_perf.py` records the alternating runtime order.

The earlier diagnostic report’s receipts remain under `.utmp/frontier-perf/`.



Native library SHA-256:



- Before: `ec183f695f331bc4de585bc7b829b535110ba83eccaf4c8dd17094a93beaaa20`

- After: `44b046f2768074844623f7f4b34df93638bbd55504c965e6260ef5a8e63f549f`


## Preview115 draw diagnostic

Eight logging-enabled diagnostic runs (four workloads per renderer) show median
packing around 0.005–0.033 ms and submission around 0.16–0.26 ms per draw callback.
Continuous vitals typically submit 42 batches while reusing 37 packed batches.
The current root canvas path resubmits retained geometry whenever it redraws;
retaining unchanged canvas commands is a candidate optimization, requiring
painter-order, clipping, lifetime and pixel verification before adoption.

These conditional draw samples include initialization and warmups; logging can
perturb scheduling. They are neither per-frame total UI cost nor timing acceptance
gates, and do not prove a speedup. Cold callback maxima remain in the receipt.
[Diagnostic evidence](../../docs/verification/draw-profile115.json).


## Retained canvas batches — experimental prototype116, not installed

An opt-in native prototype (`WEVA_GODOT_RETAIN_BATCHES`) retains child canvas
commands when command versions and texture RID are unchanged. It frees removed
batches and disables retention for the existing backdrop/SDF paths. Same-build
on/off diagnostics use three alternating repetitions per renderer/workload.
HUD triangle submissions fall from 42 to 5; the six camp/crafted/settings image
comparisons are pixel-identical under OpenGL and Vulkan, with interaction checks
passing. These are logging-enabled diagnostic results, not acceptance gates.

A separate property fixture exposes a **self_modulate mismatch** in both renderers,
even after forcing redraw. Retained child commands do not inherit the root's
self-only tint. This blocks enabling the prototype; material/filter/light-mask,
ordering, clipping, lifetime and broader visual coverage also need qualification.
Preview115 remains installed and uses the existing renderer.
[Prototype evidence, including failures](../../docs/verification/retained-batches-prototype116.json).


## Retained property fixes — experimental117d, not installed

The retained path now mirrors root `self_modulate` and `light_mask` on internal
items before rendering, including frames with no redraw. User-owned children keep
their own state. A stored callback identity fixes reconnection across fallback
paths; destruction tolerates Godot's automatic disconnection.

The reusable `hosts/godot/check_retained_canvas.py` verifier passes all **36** exact
pixel comparisons: OpenGL/Vulkan tint, forced redraw, parent modulation, transform,
clipping, visibility, empty/reloaded documents, lighting masks, SDF toggles,
backdrop transitions and reparenting. Every process exits without engine errors.
[Property evidence](../../docs/verification/retained-properties117.json).

The prototype remains opt-in and preview115 remains installed. Custom material,
texture filtering, longer resource lifetime tests, fresh sample and performance
qualification remain necessary. The116 timing results do not certify117d overhead.


## Retained shader compatibility — experimental118b, not installed

The opt-in retained renderer now mirrors per-instance shader parameters to its
internal canvas items and refreshes their material dependencies when the material
owner changes. This fixes four reproduced OpenGL/Vulkan image mismatches where
switching to an instance-uniform shader made retained UI transparent. Copying
values alone did not fix the failure; the material dependency refresh is required.
Shader-free documents skip parameter enumeration. Custom material documents
currently enumerate parameters during synchronization; inherited materials also
refresh dependencies conservatively. That path needs separate cost measurement.

The canonical retained-canvas verifier passes **62/62** exact image comparisons
across OpenGL and Vulkan. Added cases cover material replacement/removal,
nearest/linear filtering, instance parameter changes without redraw, resetting to
default, document reload, and inherited material replacement/removal. These extend
the previous tint, lighting, clipping, visibility, reparenting and fallback checks.
Installed preview115 remains unchanged; `WEVA_GODOT_RETAIN_BATCHES` stays opt-in.
[Compatibility and diagnostic evidence](../../docs/verification/retained-materials118.json).

Same-binary opt-in/off diagnostics (three alternating repetitions per renderer,
600 measured frames after 120 warmups, automatic static 1280×720 sample) preserve
the draw savings: median HUD callback time **0.322–0.347 → 0.198–0.206 ms**;
settings **0.258–0.326 → 0.139–0.154 ms**. Median triangle submissions drop from
42 to 5 and 15 respectively. Logged callback statistics include startup/warmups
and exclude the separate `frame_pre_draw` synchronization and GPU time; these
are diagnostic results, not total UI overhead or release timing gates.
The frozen sample with the candidate DLL passes 98 interaction checks in each
of four renderer/mode combinations, and all six camp/crafted/settings image
comparisons match. Its old package manifest is intentionally not release evidence.


## Explicit validation cost on preview121 — diagnostic122

The new `hosts/godot/run_validation_perf.py` benchmark uses mixed text, number
and email settings forms. Six processes (three per OpenGL/Vulkan renderer) pass
all functional event/result/focus checks for12- and48-field forms. Each scenario
has240 timed samples after60 warmups; vsync is disabled. Invalid handlers count
events and optionally cancel default reporting. This is not a timing gate.

| Form size | Valid check p95 | Invalid check p95 | Report with focus p95 | Canceled report p95 |
|---|---:|---:|---:|---:|
| 12 fields | 0.021–0.058 ms | 0.034–0.087 ms | 1.990–6.644 ms | 0.035–0.071 ms |
| 48 fields | 0.060–0.094 ms | 0.094–0.136 ms | 2.445–2.829 ms | 0.133–0.151 ms |

Single-field snapshots are0.010–0.015 ms p95. Disabled-form checks are
0.004–0.013 ms. The follow-up explicit host update is at most0.013 ms p95;
its percentile is not added to the API percentile. Counts include that forced
update: one for valid/snapshot/disabled scenarios, two for invalid scenarios.

Each report starts with focus reset to Save outside the measured interval.
Reporting therefore repeatedly transitions to a text input. The measurements
expose a significant focus/reporting cost; they do not yet isolate layout,
text-input integration, OS input-method activation, or scheduling as the cause.
This is not continuous HUD cost or total UI/GPU cost. The raw six runs and source,
library, engine and fixture fingerprints are preserved in
[diagnostic evidence](../../docs/verification/validation-performance122.json).


### Reporting cost isolation — diagnostic123

A same-preview121 comparison with native input integration disabled only in the
benchmark preserves explicit core focus and validation checks. Normal API p95 is
0.747–0.836 ms for12 fields and1.256–1.329 ms for48; diagnostic-disabled API p95
is0.065–0.070 and0.135–0.147 ms. However, the follow-up update then rises from
about0.010 ms to0.256–0.265 and0.574–0.611 ms. Disabling native input moves work;
it is not a production optimization. These are one run per renderer/mode.

An experimental host build adds opt-in `WEVA_GODOT_IME_PROFILE` stage logging.
Across600 openings per renderer (including warmups), update/target-lookup median
is about0.52 ms, caret lookup about0.004 ms, native activation0.164–0.187 ms,
and positioning0.023–0.028 ms. Update p95 is0.622–0.664 ms and activation p95
0.393–0.427 ms. Logging perturbs whole-call timings; percentiles are not summed.
The update stage has not yet separated core layout from other host update work.
The previous6.644 ms p95 was not reproduced here and is not relabeled resolved.
Installed preview121 retains native input behavior. [Isolation evidence](../../docs/verification/validation-ime-isolation123.json).

## Slow-frame diagnostics

Each new workload result retains its eight slowest measured intervals in
`slow_frames`, pairing the frame index and changed flag with API/core CPU time,
core update count and viewport render measurements. Selection and serialization
happen after the measured loop. Viewport counters can lag; these fields are not
an additive breakdown of wall time. This helps investigate the recorded 36.268 ms
clock interval without inferring a cause from unrelated percentile samples.
The diagnostic passed a 120-frame headless clock smoke; it has not reproduced or
explained that rendered stall. Original qualification results remain unchanged.

## Focused frame-stall investigation

Six focused release runs retained paired measurements for clock and UI-disabled
workloads at automatic 1080p 3D. OpenGL captured 37.188 and 33.111 ms stalls on
frames with no clock change and only 0.0005/0.0006 ms measured UI core work.
Five UI-disabled runs also exceeded 16.667 ms at their worst interval, with zero
core updates. These observations justify investigating host/render/scheduling
costs; they do not identify a cause or isolate total UI rendering cost.
The original failed full-profile clock gate remains open. No timing acceptance
profile was applied to this focused diagnostic. [Paired evidence](../../docs/verification/slowframe158.json).
