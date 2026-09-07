# Godot product readiness

The goal is an addon that a GDScript user can install in a fresh project,
author ordinary HTML/CSS, connect game state and input, and export a working
desktop game. Passing the development gallery is necessary but does not prove
that workflow. Other platforms need their own builds and export evidence.

## Current evidence (2026-09-07)

The subsequent runtime-performance pass installs **Windows runtime60** in the
development project with a verified preview56 backup. In three native pairs,
ordinary active-HUD API/update time falls 0.442→0.296 ms and redundant HUD
writes fall 1.344→0.020 ms. All 24 workload captures match, 25 host suites pass
8,337 checks, and core Release/ASan/UBSan mutation checks pass. Bound HUD,
inventory, menus and typing now have dedicated runtime measurements; see
[PERFORMANCE.md](PERFORMANCE.md) and [RUNTIME_PERFORMANCE.md](RUNTIME_PERFORMANCE.md).
This establishes ordinary-workload evidence on the tested desktop, not a full
game or lower-end hardware budget. The preview56 installation references in
the earlier checkpoints below are historical; the release59 ZIP is unchanged.

The release-engineering pass in [RELEASE.md](RELEASE.md) adds a root-level CI
workflow, versioned packaging tied to binary/source/dependency hashes and
process-exit/completeness checks for acceptance gates. Linux Release passes all
nine CTest targets; the complete Linux ASan/UBSan rerun passes all 11 targets
after fixing allocator mismatches in two test/benchmark executables. Newly built
Windows/Linux previews each pass 25 host suites / 8,319 checks and fresh-project
and relocated exports with exact example pixels. Stock Godot still fails five
of six text-safety cases on both platforms, including process aborts. An extra
Linux combined-ZIP check also hit an editor abort during fresh import; five
controlled imports and the next full export check passed, so the cause remains
unresolved. The installed development DLL remains preview56. Full product
requirements below remain open.

The development project now uses verified **Windows preview56**, with preview53
backed up. Current actual-project layout-stress runs average **2.489–2.730 ms**
per frame, with median run p95 **4.984 ms** and largest observed frame
**9.240 ms**. The stale DLL that previously produced 30ms-plus frames was
replaced at preview51. These are standalone gallery measurements with the editor
closed, not editor timings or worst-case bounds.

Preview53 simplified two hot gradient clamps. HUD headless cold builds improved
72.512→67.836 ms in five of five alternating pairs; native gallery timings
remain too variable to establish a speedup. Nine CTest targets, 864 frozen-source
raster comparisons, native host checks, four gallery pixel comparisons and
fresh-project/native export checks pass. The installed project also passes 245
host checks and an exact layout-stress capture. Linux and UBSan verification
after preview40 was pending at that checkpoint. Methods and limits are in
[PERFORMANCE.md](PERFORMANCE.md); earlier installation-pending notes below are
historical.

Windows preview56 adds input baselines, type-change layout invalidation,
active-font centering and visible carets/selections in short or empty fields.
It includes candidate54's flex/grid containment and candidate55's border/button
corrections. It passes 636,031 core checks, 25 native host suites / 8,319 checks,
1,944 focused Chrome checks, 2,180 rendered form checks and four unchanged
gallery captures. Standalone layout-stress run means range 2.090–2.145ms,
median 2.109ms, with median run p95 3.807ms; this is similar to candidate55.

The 19-file Windows ZIP passes fresh-project, packed-resource and relocated
native debug/release/embedded exports under Godot 4.7.2, including 17 example
checks in each configuration and exact exported example pixels. It is
**packaged and installed**, with a hash-checked preview53 backup. The actual
project also passes 245 host checks and an exact layout-stress capture.
Fresh Chrome arbitration now leaves five sample and five harvest findings.
Two harvest input failures are resolved; the newly flagged audit checkbox
row is closer to Chrome but retains about 0.1–0.2px residuals. Original captures
and acceptance gates remained unchanged at that checkpoint. Later Linux and
UBSan evidence is recorded above. See [ORACLE.md](ORACLE.md) and
[PERFORMANCE.md](PERFORMANCE.md) for the evidence and limits.

The current core and embedded ICU now also pass **Windows MSVC AddressSanitizer**:
636,031 core checks, including retained/full mutations of all 47 samples, and
all **10 CTest targets**. Allocation guards, blur variants and benchmark CLI
checks pass. The activation probe detects a deliberate heap overflow; a matched
uninstrumented probe is rejected by the gate. The new `WEVA_SANITIZERS` CMake
switch defaults to OFF, and the installed preview56 DLL is unchanged.
Reproduction and scope are in [SANITIZERS.md](SANITIZERS.md). This closes the
current Windows core ASan gap; the later Linux/UBSan core pass is recorded above.
Current native-adapter sanitizer verification remains pending.

### Remaining release work, in order

1. **Text safety.** Resolve the stock-engine long-emoji corruption/crash in
   an engine configuration that can actually be distributed and supported.
   The local diagnostic patch is not part of the addon. On 2026-09-07 the
   latest Windows/Linux probes again found corrupt results in the 33, 65 and
   256-run cases and process aborts in both mixed-script cases. The 32-run
   control now passes cleanly, without the earlier certificate-store error. The main
   check script now includes the reproduction and preserves engine hashes,
   exit codes and unfiltered logs; this detects the blocker, not fixes it.
   With the frozen preview56 DLL, ordinary autoscroll passes 81 assertions;
   enabling its long-emoji stress input fails five selection/editing
   assertions. Both runs retain the same certificate-store diagnostic.
2. **Input and authoring correctness.** Verify real Windows IME and resolve
   the documented stock Linux IME failures. Finish the remaining font-family,
   bidi/editing, form-control and validation behavior, plus touch/gamepad and
   accessibility integration. The five sample and five harvest findings
   include engine differences and a component-capture defect; fix them using
   browser evidence without widening tolerances. Details below remain part
   of the release scope.
3. **Current platform and memory checks.** The current Linux core sanitizer,
   host-suite and relocated-export checks now pass alongside Windows checks.
   Complete current native-adapter instrumentation, the broader rendering and
   real-input matrix, and investigate the extra Linux fresh-import abort.
   A core sanitizer pass does not cover Godot's native adapter or engine. Every
   additional supported platform/configuration needs equivalent evidence against
   its actual packaged binary.
4. **First-open latency and game integration.** HUD cold build is still
   about 68ms headlessly. Verify a preload/reuse strategy or reduce that cost,
   and validate latency and lifecycle behavior in a representative game.
   The installed layout-stress improvement is established; it does not bound
   cold-open time, editor overhead or graphics stalls under shared load.
5. **Release engineering.** Pinned godot-cpp/ICU inputs, compiler/build metadata,
   versioned previews and root-level CI are now implemented. Finish the
   compatibility/API documentation and remaining acceptance matrix.
   `bash godot-port/check.sh --release` rejects skipped gates, process failures,
   incomplete oracle runs and outstanding layout findings; text safety is
   required whenever Godot is available. A successful script alone cannot
   substitute for the unimplemented requirements and real-device checks.

The development host has working layout/paint, forms, animations, keyed data
binding, controller events and a sample gallery. Before the product work below,
the complete mutation corpus passed 576,944 checks in Release and ASan/UBSan;
47 backend comparisons and 12 interactive states passed. Layout-stress's
animated update measures 3.52 ms through Godot with its engine font. Detailed
methods and limits are in [PERFORMANCE.md](PERFORMANCE.md).

After native Control and keyboard integration, the complete mutation corpus
passes 577,673 checks in both Release and ASan/UBSan. All 47 backend comparisons
match the preceding build's results;
12 interactive render states have 0.00% structural and color differences.
Windows 4.7.1 and Linux 4.7.2 each pass 40 native input integration checks,
35 keyboard integration checks, 245 host checks, 75 binding checks, 10 hover
checks and five gallery hover checks, plus the demo and inventory suites.
Windows uses an isolated project with a rebuilt DLL. Fresh-project and packed-resource
smokes pass 8 and 10 checks on both Windows 4.7.1 and Linux 4.7.2, covering
imported PNG/SVG and markup. A packaged addon also passes six example checks
in the project and six more in its pack on both platforms, including native
keyboard activation through its controller and bindings.

Native debug, release and embedded-pack exports pass on Windows and Linux
x86_64 with standard Godot 4.7.2 templates. Each configuration passes 13
resource/runtime checks and six example checks after the source project is
hidden and the export relocated. The example's rendered pixels exactly match
its editor-project baseline within each platform. Details and limits are in
[DESKTOP_EXPORTS.md](DESKTOP_EXPORTS.md).

After the long-field and Unicode editing work, the complete mutation corpus
passes 582,194 checks in both Release and ASan/UBSan. Windows/Linux Godot 4.7.2
also pass the new 86-check text-editing suite alongside all preceding host
checks. The 47 backend comparisons retain their preceding results; all 12
interactive states remain at 0.00% structural and color differences. The new
addon libraries pass native debug, release and embedded-pack exports on both
platforms, with exported example pixels matching the project.

After maxlength and paste integration, the complete mutation corpus passes
582,689 checks in Release and ASan/UBSan. Chrome passes 150 matching behavior
checks; Windows/Linux Godot each pass 44 new GUI checks alongside all preceding
host suites. A separate Linux X11 run exercises the actual clipboard shortcut.
The packaged example now verifies its existing 40-unit name limit using pasted
emoji and undo. Its eight checks and the 13 native runtime/resource checks
pass in Windows/Linux debug, release and embedded-pack exports; rendered
example pixels still match the project within each platform.

After live/default form state and reset integration, the complete mutation
corpus passes **582,903 checks** in Release and ASan/UBSan. Chrome passes 80
form-state checks; Windows/Linux Godot each pass 30 new reset integration
checks alongside every preceding host suite. The 47 backend comparisons are
unchanged and all 12 interactive states have 0.00% structural/color differences.
Real Linux IME passes nine checks with the previously verified private
Godot/IBus fixes. The addon does not include those upstream fixes.

The packaged example now exercises a native reset button, default preservation
and model/controller restoration. Its ten checks and all 13 resource/runtime
checks pass in Windows/Linux debug, release and embedded-pack exports, with
example pixels matching the project on each platform. Focused idle inputs
still report zero allocations. See [FORM_STATE.md](FORM_STATE.md).

## Work in progress

After select interaction work, the complete mutation corpus passes **583,691
checks** in both Release and ASan/UBSan. Chrome passes 69 matching selection,
event and display-mode checks; Windows/Linux Godot each pass 63 new native
select checks alongside every preceding host suite. All 47 backend comparisons
retain their preceding results; all 12 interactive comparisons remain at
0.00% structural/color difference. A rendered listbox confirms the separate
keyboard-row cue, disabled rows and multiple selection. Fresh installation and
debug, release and embedded-pack exports pass on both platforms, with example
pixels unchanged from the preceding build. A 1,000-option idle listbox and a
focused 4,096-character input each allocate zero bytes over 500 updates.
See [FORM_STATE.md](FORM_STATE.md#select-interaction) for semantics and limits.

Unicode select typeahead and label rendering pass **584,432 core checks** in
Release and ASan/UBSan. Chrome passes 78 matching behavior checks; Windows/Linux Godot each
pass 48 native typeahead, label, popup and binding checks alongside all
preceding suites. A rendered 49-check run additionally verifies the captured
listbox and styled popup. All 47 backend comparisons retain their preceding
results and all 12 interactive comparisons remain at zero difference.
The installed example now exercises accented-label search through native
input. All 12 example checks and 13 runtime checks pass in debug, release and
embedded-pack exports on both platforms, with rendered pixels unchanged.
The ICU license/data notices and source pin accompany the 19-file addon ZIP.
See [PERFORMANCE.md](PERFORMANCE.md) for idle allocation and binary-size costs.
That stress benchmark exposed slow 1,000-option selection updates:
approximately 37–46 ms through Godot, with cascade the largest measured stage.
The subsequent scope/color change reduces the same back-to-back benchmark
from **38.044 ms to 2.066 ms**. Selection restyles two changed options, consumes
caption/keyboard-row versions independently, and repaints without layout.
Overlapping dirty scopes are coalesced, and HTML reload clears stale focus
ancestors before they can become dirty roots. Release and ASan/UBSan each pass
**584,528 checks**, including inherited colors, sibling rules, `:has()`, reset,
removal and reload compared with complete rendering. Windows/Linux host suites
and all native export modes pass; the example and rendered select fixture
retain their preceding pixels. The 47 backend and 12 interactive comparisons
are unchanged. Broad focus changes in
large controls remain an optimization opportunity; see the benchmark details
in [PERFORMANCE.md](PERFORMANCE.md).

Held listbox autoscroll passes **585,327 core checks** in Release and
ASan/UBSan, including retained/full comparisons while nested scroll geometry
moves and resizes. Chrome passes 23 matching autoscroll behavior checks.
Windows/Linux Godot each pass all 15 host suites, including 33 new native
capture, paused-clock and cancellation checks. A rendered 34-check run confirms
the scrolled selection, active row, disabled row and clipping. The 47 backend
comparisons retain their preceding results and all 12 interactive states
remain at zero difference.

Fresh installation and native debug, release and embedded-pack exports pass
on both platforms. The example now includes autoscroll and delayed binding
commit: all 14 example checks and 13 runtime checks pass, with exported pixels
matching the project on each platform. A 1,000-option autoscroll update averages
1.416 ms through Windows Godot with its engine font; idle allocation checks
remain at zero. See [FORM_STATE.md](FORM_STATE.md) and
[PERFORMANCE.md](PERFORMANCE.md) for clock semantics, reproduction and limits.

Continuous text selection now passes **588,811 core checks** in Release and
ASan/UBSan, plus 35 Chrome checks and 81 native Godot checks on each desktop
platform. All 16 host suites pass. Text inputs and passwords scroll
horizontally; textarea selection scrolls both axes, preserves Unicode source
anchors and creates no edit or undo events. Monotonic input time keeps held
selection responsive while CSS is paused or `Engine.time_scale` is zero.
A rendered 82-check run verifies the clipped selection. Retained/full frames
agree through wrapping, nested scrolling, resizing and font changes.

The export example exposed missing model initialization after HTML replacement.
The host now reapplies existing bindings after creating the new document,
including controls inside repeated rows and callable data sources. The binding
suite grows from 75 to 85 checks; the preceding DLL fails six reload assertions,
while the fix passes all 85 on Windows/Linux. Explicit empty data stays active
through reload, while unconfigured corpus markup remains literal. Reset defaults
and edit-event behavior remain covered.

Fresh install, packed resources and native debug/release/embedded exports pass
on Windows/Linux, including all 17 example and 13 native runtime checks.
Exported example pixels match each platform's preceding preview. Idle
allocation remains zero; active 4,096-byte field scrolling measures roughly
4 ms in the recorded Windows run. See [PERFORMANCE.md](PERFORMANCE.md).

**An upstream text-shaping defect remains a product release blocker for
unrestricted Unicode input.** Stock Godot 4.7.2 corrupts glyph ranges or crashes
above 32 separate emoji runs within one script run. An addon-free reproduction
fails five of six cases on Windows/Linux. A matched candidate engine patch
passes all six (18 checks) and the original 81-check Weva stress suite. The
patch is separate from the addon; regular interaction coverage stays below
the trigger and does not claim to fix it. See
[GODOT_TEXT_SHAPING.md](GODOT_TEXT_SHAPING.md).

Layout-stress's rendered Windows gallery now advances the document once per
frame. Its native clock test fails with doubled animation speed before the
fix and passes afterward. Three interleaved sweeps measure 23.327 ms mean
frame time with the repository's older DLL and duplicate update, versus
8.781 ms with the current DLL, one update and reused RGB conversion. See
[PERFORMANCE.md](PERFORMANCE.md) for the breakdown and measurement limits.
All 17 host suites, including the 85-check binding suite and gallery clock
regression, pass on Windows/Linux Godot 4.7.2 and the user's Windows Godot 4.7
executable. The 47 backend results and 12 interactive comparisons are unchanged.

Consecutive Godot triangle uploads further reduce layout-stress frame time
from **8.618 to 7.435 ms** on the user's Godot 4.7 executable, using five
interleaved comparisons of the same DLL with batching on/off. Core update
time stays about 4.5 ms. A separate scope reduces 281 submissions to 87 with
the same vertices. Six dedicated images are byte-identical across batching
modes on Windows Vulkan/OpenGL and Linux OpenGL; the 47 sample comparisons,
12 interactive states, all 17 host suites and desktop export checks retain
their passing results. Occasional long frame stalls remain. See
[PERFORMANCE.md](PERFORMANCE.md) for methods and limits.

Native sampling now attributes the observed long Windows frames to a Vulkan
GPU-fence wait after Weva's submission. The GPU was also at 99% utilization
from other work after the test exited. Shared GPU load therefore remains a
confounding factor for frame-latency claims; a controlled comparison without
that load is outstanding. Per-frame traces and optional GPU timing are now
available in the gallery probe. This investigation does not claim a runtime fix.

Passing contained solid triangles through rounded clips reduces the next
matched Windows comparison from **7.271 to 6.311 ms per frame**, with core
update time **4.410 to 3.899 ms**. It also removes a one-pixel hole inside a
solid progress bar in native rendering. Varying UV/color/coverage retains the
previous interpolation path. Release and ASan/UBSan each pass **589,083 checks**;
the 47 backend results, 12 interactive comparisons, 17 host suites and native
Windows/Linux export checks retain their passing results. Preview20 contains
this change and the frame tracing above. The open development editor still
uses the older repository DLL; the preview has been validated in isolated
projects. See [PERFORMANCE.md](PERFORMANCE.md) for matched measurements and
the remaining GPU-stall limitation.

Preview21 reuses parsed colors and the decoration values already resolved by
the paint walk. The next matched comparison improves **6.101 -> 5.632 ms per
frame**, with core updates **3.853 -> 3.365 ms**. A headless animated update
makes 31.9% fewer allocation calls. All static/animated sample pixels and eight
real-font Windows snapshots match preview20. Release and ASan/UBSan each pass
**589,097 checks**; backend, interactive, host and desktop export checks pass
with their preceding results. This build also exposes fill/border construction
and color resolution in the paint profile. The repository's Windows DLL has
not been replaced; the new package is tested in isolated projects.

Preview22 preserves native shaped-glyph offsets, converts TextServer clusters
to UTF-8 byte offsets and resolves automatic fallback glyphs against the exact
selected font. The additive minor-11 callback leaves the existing font table
unchanged. Native adapter/document geometry checks pass **547 checks on Windows
and 457 on Linux**. Release and ASan/UBSan each pass **589,134 checks** with the
full mutation corpus. All 17 host suites, the unchanged 47 backend comparisons,
12 interactive states and Windows/Linux native exports pass. Eight real-font
layout-stress snapshots are byte-identical to preview21. The matched gallery
comparison measures **6.236 -> 6.211 ms/frame**, within run variation, with
long GPU waits still present in both builds. The stock Godot shaping limitation
and broader bidi/font-family work remain open. See [GODOT_TEXT_SHAPING.md](GODOT_TEXT_SHAPING.md).

Preview23 preserves textured, colored and antialiased triangles wholly inside
convex clips. The matched Windows gallery comparison improves **5.709 ->
5.079 ms/frame**, with core updates **3.474 -> 3.200 ms** and 12% fewer uploaded
vertices in the profile. Forty-eight direct-render comparisons prove that an
irrelevant clip leaves the original triangle's pixels unchanged; the previous
clipper fails 21 of them. Native snapshots also show five formerly missing
gradient-bar pixels filled correctly. Small interpolation/sampling differences
are recorded in [PERFORMANCE.md](PERFORMANCE.md). Release and ASan/UBSan each
pass **588,998 checks**; backend, interactive, host, batching and desktop export
checks pass on Windows/Linux. The previously observed GPU waits remain open.

Preview24 keeps shared source vertices shared when interior triangles pass
through polygon clips, reducing native layout-stress uploads by **10.1%**.
The five-pair gallery comparison measures **5.073 -> 4.820 ms/frame**, with
core updates **3.206 -> 3.134 ms**; system-load variation limits that timing
comparison. Headless core time slightly increases while allocated bytes fall
3.9%; [PERFORMANCE.md](PERFORMANCE.md) records the tradeoff. All 47 software
samples at two animation times and eight native snapshots remain byte-identical.
Release and ASan/UBSan each pass **589,078 checks**. Backend, interactive, host,
batching and desktop export checks retain their preceding results on both
platforms. The repository Windows DLL has not been replaced.

Preview26 supports inherited Godot theme fonts, node overrides and type
variations, including live resource/fallback changes and paused documents.
It fixes stale glyph bitmaps when a font provider changes, atlas ownership
across renderer replacement/destruction, and a reproduced use-after-free in
published texture views. Both platforms pass 35 headless and 39 rendered theme
checks. Release and ASan/UBSan each pass **589,105 checks**; all 18 host suites,
unchanged backend/interactive comparisons and desktop exports pass. Eight
native layout-stress snapshots remain byte-identical to preview24. Typical
frames and core updates remain about 4.9 and 3.2 ms respectively; long frame
stalls recur and remain unresolved. See [PERFORMANCE.md](PERFORMANCE.md) for
the whole-frame measurements. Per-element CSS font-family selection remains
unfinished. The repository Windows DLL has not been replaced.

Preview27 gives the triangle cutter bounded stack scratch storage and appends
each output polygon's indices in one resize. Exact interpolation and geometry
remain unchanged. Five interleaved Windows comparisons measure **5.794 ->
5.553 ms mean frame**, **4.896 -> 4.734 ms median frame** and **3.195 -> 3.082 ms
core update**, as medians across runs. Headless time improves 6.6%, with 285
fewer allocations in the measured update. Candidate frames still reach 31 ms;
the stall issue remains open. Release and ASan/UBSan each pass **589,105 checks**,
including the existing exact differential clip oracle. Eight native snapshots,
47 backend comparison rows and 12 interactive states retain their previous
results; Windows/Linux host suites and desktop exports pass. The old DLL is
still loaded in the repository editor. Details are in [PERFORMANCE.md](PERFORMANCE.md).

Preview28 preserves draw buffers across ancestor layout when a retained grid's
incoming origin, transform, opacity, filter, scissor and clip inputs stay equal.
The Windows engine-font gallery replays that grid on 277 of 300 measured
frames. Five interleaved comparisons measure **5.305 -> 3.667 ms mean frame**,
**4.728 -> 2.867 ms median frame** and **3.045 -> 1.177 ms mean core update**,
as medians across runs. A frame still reaches 86.693 ms; the stall issue is
unresolved. Stub-font headless time slightly regresses, with unchanged allocation
counts/bytes. Release passes **589,873 checks**; the sanitizer corpus and all
768 expanded retention checks pass. Exact full-repaint comparisons cover
origin/clip/transform changes, and disabled-key controls reproduce stale output.
Native snapshots, backend comparisons, interactive states, Windows/Linux host
suites and desktop exports retain their preceding results. The repository's
Windows DLL remains unchanged. See [PERFORMANCE.md](PERFORMANCE.md).

Preview29 reuses Godot's packed vertex/color/UV/index arrays through immutable
command versions, added separately in ABI minor 12. Five interleaved Windows
comparisons measure **4.338 -> 3.685 ms mean frame** and **2.982 -> 2.553 ms
median frame**, with mean core update unchanged at **1.237 ms**. A separate
logged run reduces packing from 0.488 to 0.101 ms; GPU submissions/uploads
remain unchanged. Long stalls still reach 92.748 ms. Release and ASan/UBSan
each pass **589,995 checks**. All 26 cache enabled/disabled pixel comparisons
match on Windows/Linux, including native material/modulation and changing
batch inputs. Eight stress snapshots, 47 backend rows, 12 interactive states,
18 host suites per platform and desktop exports retain their prior results.
The repository editor still loads the old DLL. See [PERFORMANCE.md](PERFORMANCE.md).

Preview30 skips glyph preparation in unchanged subtrees while their atlas
slots remain valid. The engine-font stress grid skips this walk on all 300
profiled updates; glyph preparation drops from 0.167 to 0.015 ms. Five paired
native runs measure **1.347 -> 1.202 ms mean core update**, with whole-frame
means nearly unchanged at **2.925 -> 2.895 ms**. The headless measured update
makes 1,207 fewer allocations. Release and ASan/UBSan each pass **590,075
checks**; a control build fails the new skip assertions. Cache enabled/disabled
pixels, native snapshots, backend/interactive comparisons, host suites and
desktop exports retain their previous results. Previously observed long stalls
remain unresolved, and the editor still uses its old DLL. See [PERFORMANCE.md](PERFORMANCE.md).

Preview31 computes backdrops only for the box builder's eligible top-layer
hosts, eliminating 719 unused styles on layout-stress. Five interleaved
Windows engine-font comparisons reduce mean cold gallery build time from
**30.730 to 23.437 ms** (medians of run means); animated frame time is nearly
unchanged. Release and ASan/UBSan each pass **590,185 checks**, including
backdrop lifecycle and mutation regressions. Eight native snapshots, backend
comparisons, host suites and native exports preserve their previous results.
Normal layout flow remains the largest cold stage, and the repository editor
still loads the old DLL. See [PERFORMANCE.md](PERFORMANCE.md).

Preview32 reuses bounded plain-text line results across sizing probes within
each layout pass, skipping 1,256 of layout-stress's 1,774 inline-layout calls.
Five new interleaved Windows engine-font comparisons improve cold build time
**24.309 -> 22.063 ms** against preview31 (medians of run means). Animated
frame time is nearly unchanged; allocated bytes rise by 30,784 per measured
headless update, with allocation count unchanged. Release and ASan/UBSan each
pass **590,330 checks**, including changing widths and button/table alignment.
Native snapshots, backend comparisons, host suites and desktop exports retain
their previous results. Cold builds still exceed 16.7 ms, long rendering
stalls remain unresolved, and the editor still loads its older DLL.
See [PERFORMANCE.md](PERFORMANCE.md) for measurements and the preview32 ZIP.

Preview33 shares immutable synthetic fonts in a bounded native pool and fixes
a weight-cache error that made 700/800 depend on request order. Five final
paired comparisons improve median cold run means **22.750 -> 20.523 ms**
against preview32; four pairs improve, with substantial machine-load variation.
Core Release/ASan/UBSan retain **590,330 passing checks**. Native font tests
pass **797 Windows / 707 Linux checks**, including exact native weight/italic
coverage and ownership through eviction; an instrumented Linux adapter also
passes 707 checks. Cache-enabled/disabled native stress pixels match on both
platforms, and backend, host and export checks retain their preceding results.
Mixed-weight text intentionally changes from preview32's incorrect synthesis.
The editor still uses the old DLL, cold builds still exceed 16.7 ms, and long
rendering stalls remain open. See [PERFORMANCE.md](PERFORMANCE.md) for the
memory tradeoff, measurement limits and preview33 artifact.

Preview34 allocates raw computed-style values in stable pages instead of
constructing 334 strings per element. Five final interleaved Windows native
comparisons improve cold run means **19.354 -> 17.415 ms** against preview33;
all five pairs improve. Animated core timing is nearly flat and measured
steady allocations are unchanged. GCC headless timing remains flat within
variation. Release and ASan/UBSan each pass **590,698 checks**, including
growth, view lifetimes, move/clear/refill and insertion-order-independent diffs.
Native stress pixels match on both platforms, and backend, host and export
checks retain their previous results. Cold builds still exceed 16.7 ms,
rendering spikes remain open, and the editor still uses the old DLL.
See [PERFORMANCE.md](PERFORMANCE.md) for the tradeoffs and preview34 artifact.

Preview35 enumerates declarations through occupancy words and constructs
Godot glyph dictionary keys once per text run. Five final paired comparisons
improve native cold run means **19.182 -> 17.522 ms** against preview34; four
pairs improve. Animated core time is nearly flat and measured steady
allocations are unchanged. Release and ASan/UBSan each pass **591,033 checks**;
native font tests pass **797 Windows / 707 Linux checks**. Stress pixels,
backend comparisons and exports retain their previous results. Both platforms
pass 18 host suites, after one Linux fresh import exits without a diagnostic
and a new fixture passes. A 95.933 ms whole-frame stall recurs, and cold builds
remain above 16.7 ms. The independent viewport-font memo failure led to
preview36 below. The editor still uses the old DLL. See [PERFORMANCE.md](PERFORMANCE.md)
for the measurements, import limitation and preview35 artifact.

Preview36 fixes stale viewport-relative font sizes after resize by including
viewport dimensions, root metrics and DPI in the memo key. Explicit pixel
values retain a versioned fast path. Release and ASan/UBSan each pass
**591,870 checks**; Chrome passes 80 geometry checks, and both desktop platforms
pass 208 native geometry checks, 336 rendered checks and all 19 host suites.
Stress pixels, backend comparisons and native exports retain their preceding
results. Five paired native comparisons measure **18.175 -> 18.838 ms** cold
and **1.157 -> 1.201 ms** animated core time against preview35. This correctness
fix shows a small measured cost; a 94.249 ms whole-frame stall also recurs.
Performance remains work, as does nested relative-font inheritance. The editor
still uses the old DLL. See [PERFORMANCE.md](PERFORMANCE.md) for the protocol,
variation and preview36 artifact.

Preview37 reuses parsed, directly declared border widths while resolving their
relative units against current context inputs. It also removes locale-dependent
numeric parsing from the raw resolver. A reproduced registry-initial-value
cache error is guarded by keeping inherited/default widths on fresh resolution.
Release and ASan/UBSan each pass **593,497 checks**. Native stress pixels,
all 47 backend results, 12 live states, 19 host suites per platform and native
exports retain their preceding results. Five final native pairs measure
**19.205 -> 18.874 ms** cold and **1.069 -> 1.063 ms** animated core against
preview36. Whole-frame means regress **2.743 -> 3.853 ms**, and a 95.750 ms
stall recurs. Cold allocations rise by 1,236 / 36,256 bytes; measured animated
allocations stay unchanged. This is a small reduction in repeated parsing,
not a solution to cold-opening cost or frame stalls. The editor still uses
the older DLL. See [PERFORMANCE.md](PERFORMANCE.md) for the final preview37 ZIP,
measurement limits and rejected optimization experiments.

Preview38 combines min/max intrinsic-size walks during flex/grid parent input
capture. All 772,629 measurements checked against the previous routines agree
exactly; native pixels, host suites and exported examples retain their results.
Headless cold means improve **10.035 -> 8.914 ms** and animated core means
**2.216 -> 2.104 ms**, with unchanged allocations. Native cold means remain
effectively flat (**19.370 -> 19.292 ms**), and whole-frame means regress
**2.400 -> 2.688 ms** despite slightly lower native core time. A 213.181 ms
rendered stall and an intermittent Linux fresh-import abort remain unresolved.
The repository Windows DLL is unchanged. See [PERFORMANCE.md](PERFORMANCE.md)
for the measurements, final artifact and preformatted-newline sizing follow-up.

Preview39 stops native font adoption from modifying the default Font's shared
RID array. Each document previously appended another copy of the compatibility
fallbacks; the observed chain grew 9, 17, 25, 33. An owned list now keeps the
source resource unchanged across opening, font toggles and destruction.
Preview38 fails 20 new ownership checks on both platforms; preview39 passes
75 headless and 79 rendered theme checks. Native cold means remain mixed
(**17.249 -> 17.505 ms**), and rendered stalls persist under uncontrolled shared
graphics load. The font list bug is fixed; the broader performance goal remains
open. See [PERFORMANCE.md](PERFORMANCE.md) for evidence and the preview39 artifact.

Preview40 shares bounded shaped runs from immutable synthetic fonts and remaps
glyphs into each receiving document's handles. Later fresh stress documents
avoid 116 of 205 native shaping calls. Final paired fresh-document means improve
**19.335 -> 17.927 ms**, but first-open timing regresses and whole-frame means
remain flat (**15.683 -> 15.920 ms**). A 91.017 ms rendered stall remains under
uncontrolled shared graphics load. All 10,236 Windows / 10,226 Linux native
font checks pass, including Linux adapter ASan/UBSan; host suites, stress pixels
and native exports retain their results. A fresh Linux test-import failure
requires a successful fresh retry, and its cause remains open. The preview ZIP
is available; the running repository Windows editor still uses its previous DLL.
See [PERFORMANCE.md](PERFORMANCE.md) for the measured scope and artifact.

A resumed three-pair native comparison against the DLL still installed in the
development project measures **15.849 -> 2.561 ms** whole frames and
**10.094 -> 1.117 ms** core updates with preview40. Every pair improves; its
worst measured frame is 9.405 ms. This measures the accumulated improvements
since that installed binary, not preview40's shaping change alone. The updated
DLL still awaits installation/editor reload. Cold opening and the long stalls
seen in earlier workloads remain open; three runs establish no latency bound.

The complete core mutation suite now also builds and passes **593,614 checks**
with Windows MSVC Release. Its existing background test needed an explicit
`<algorithm>` include for `std::clamp`. A separately tested inline style-read
optimization regressed cold openings and was reverted; the full suite passes
again after restoring the runtime sources. Preview40 remains the latest addon.

Preview41 for Windows includes the preserved-newline intrinsic-sizing fix:
`pre` text `aa bbbb\ncc` with 8px advances measures 56px rather than joining
the forced lines into 72px. The complete Windows Release suite passes
**593,783 checks**, including flex/grid sizing and live whitespace changes;
24 local Chrome intrinsic-width assertions agree. All 20 native host suites
pass, including 336 new headless / 432 rendered intrinsic-sizing checks.
Four native corpus captures match preview40 exactly, and the Windows-only ZIP
passes fresh import, packed resources and debug/release/embedded native exports
with matching example pixels. Native layout-stress medians are **2.226 ms**
whole frames and **0.956 ms** core updates; cold fresh-document means remain
around **16.200 ms**, with **22.247 ms** first opens. Differences from preview40
are small and mixed; the broader latency work remains open. Linux and sanitizer
checks have not been repeated for this core change. Preview40 remains the latest
combined-platform ZIP, and the development editor still has its older DLL.
Details and the Windows artifact are in [PERFORMANCE.md](PERFORMANCE.md).

The default Windows core build now also builds every command-line tool;
the benchmark's unconditional POSIX headers previously broke that target.
Allocation attribution works in layout, full-update and cold modes, including
named Windows stacks with local PDBs. Its 15 CLI cases pass in Release and
RelWithDebInfo. This tooling change leaves preview41 as the runtime artifact;
the POSIX sampling path still needs its own validation. See the
[benchmark guide](../tools/weva_bench/README.md).

Preview42 for Windows removes temporary strings from name-based inherited style
reads. **593,800 core checks** pass, plus a guard proving 8,000 reads through a
32-level chain allocate nothing. Cold headless allocation counts drop **58,273
to 54,567**. Native timing is mixed in the short comparison; longer paired runs
improve median means **2.296 -> 2.256 ms** whole frames and **0.991 -> 0.953 ms**
core updates. All 20 host suites, four native page pixel comparisons and Windows
debug/release/embedded export checks pass. First opens still exceed a frame
budget, and earlier long stalls remain unresolved. Linux and sanitizer checks
remain pending for this change; preview40 is still the latest combined-platform
package. The development editor's older DLL has not been replaced. See
[PERFORMANCE.md](PERFORMANCE.md) for the measurements and preview42 ZIP.

Preview43 for Windows prepares gradient interpolation work once per texture.
The minimap texture benchmark improves **22.192 -> 20.892 ms** with exact pixels;
whole-HUD cold-open comparisons remain mixed, so this is not evidence of a
whole-document speedup. **614,330 core checks**, all 20 host suites, four native
page pixel comparisons and Windows import/pack/native export checks pass.
The span data adds 1,640 temporary requested bytes per cold HUD build with no
extra allocations. Linux/sanitizer validation and the editor reload remain
pending. Cold HUD and first-open latency remain product work; see
[PERFORMANCE.md](PERFORMANCE.md) for the preview43 artifact and full evidence.

Preview44 for Windows removes redundant blur-buffer clearing, interleaves shadow
rows and uses exact SSE2 channel arithmetic where available. The HUD blur kernel
improves **12.058 -> 10.267 ms**; five native cold-open pairs improve median run
means **72.526 -> 71.457 ms**. Cold opening remains over budget. **614,536 core
checks**, a **192-case portable/normal kernel comparison**, all 20 host suites,
four native page pixel comparisons and Windows import/pack/export checks pass.
Pixels remain unchanged. Linux/sanitizer validation and the editor reload are
still pending; the latest combined-platform package remains preview40. See
[PERFORMANCE.md](PERFORMANCE.md) for the preview44 artifact and measurement limits.

Preview45 for Windows fixes nested relative font-size inheritance, including
explicit inheritance, generated content and live changes with equal raw CSS
strings but different computed sizes. **615,403 core checks**, **134 Chrome
checks**, all **21 host suites / 2,035 headless checks**, and **896 rendered
inheritance checks** pass. Four gallery PNGs remain exact; Windows import,
pack and all three native export configurations pass. The paired layout-stress
timings are mixed: median run means are **2.705 -> 2.885 ms** whole frame and
**1.234 -> 1.283 ms** core, with three of five native pairs improving. This is
a correctness fix with no demonstrated speedup. Linux/sanitizer verification,
editor installation/reload, and cold-open latency work remain pending. See
[PERFORMANCE.md](PERFORMANCE.md) for the preview45 artifact and measured costs.

Preview46 for Windows reserves geometry buffers before emitting rounded shapes
and text, while preserving geometric growth for repeated appends and avoiding
empty-glyph reservations. Layout-stress's measured animated allocations fall
**14,448 -> 9,004**, and headless update means improve **3.021 -> 2.787 ms**.
Native frame means improve slightly, **2.232 -> 2.215 ms**, with four of five
pairs faster. The new boundary trace confirms the retained grid needs repaint
when its fractional position and rounded clip change. **615,403 core checks**,
all five CTest targets, 21 host suites and **1,664 rendered regression checks**
pass. Four native gallery captures are unchanged; Windows import/PCK and all
three native export configurations pass. Editor installation/reload and Linux/
sanitizer verification remain pending. See [PERFORMANCE.md](PERFORMANCE.md).

Preview47 for Windows removes temporary paint-order lists when the sibling
links already follow paint order, and shares the traversal with hit testing.
Median sampled animated allocations fall **8,969 -> 8,160**. Native frame
means improve **2.921 -> 2.484 ms** across five pairs, with substantial baseline
variation; headless animated latency is effectively flat. All six CTest
targets pass, including **113,803 ordering/allocation checks**, alongside all
21 host suites. Four native gallery captures and native exported example
captures are unchanged. Windows import/PCK/debug/release/embedded exports pass.
Editor installation/reload and Linux/sanitizer verification remain pending.
See [PERFORMANCE.md](PERFORMANCE.md) for the measurements and artifact.

Preview48 for Windows reuses the existing corner-radius parse cache while
resolving used radii from current geometry and length context. It also fixes
tabs/newlines/comments in radius pairs, verified by 52 Chrome comparisons and
a 6,823-check core guard. Sampled animated allocations fall **8,160 -> 4,776**;
headless updates improve **2.694 -> 2.456 ms** and native frames improve slightly
**2.186 -> 2.164 ms**, with all five pairs faster. All seven CTest targets and
21 Godot host suites pass. Four gallery captures are unchanged, and Windows
import/PCK/debug/release/embedded exports pass with matching example pixels.
Editor installation/reload and Linux/sanitizer verification remain pending.
See [PERFORMANCE.md](PERFORMANCE.md) for scope and artifacts.

Preview49 for Windows compacts clip-preparation scratch in place and reserves
outline/piece buffers without changing geometry. The 1,076-polygon guard
matches frozen preview48's geometry digest and removes 2,155 budget failures.
Sampled animated allocations fall **4,776 -> 3,796**. Focused clip preparation
improves, but overall latency evidence is mixed: the initial headless comparison
regresses, a same-CPU diagnostic has nearly flat wall means, and native pairs
mostly improve. No consistent overall speedup is claimed. All eight CTest
targets, 21 host suites and Windows import/PCK/native exports pass; four gallery
captures are unchanged. Editor installation/reload and Linux/sanitizer checks
remain pending. [PERFORMANCE.md](PERFORMANCE.md) records all timing results.

Preview50 for Windows transfers temporary meshes into paint submission and
the collecting backend without the previous vertex/index copies. Input text
decoration measurements finish before transfer. The 56-case ownership guard
matches frozen preview49's geometry digest and passes all allocation budgets.
Sampled animated allocations fall **3,796 -> 3,290**. Headless animated means
improve slightly, but cold and native frame means regress slightly; this is an
allocation reduction, not evidence of a reliable overall latency gain. All
nine CTest targets, 21 host suites and Windows import/PCK/native exports pass;
four gallery captures remain exact. The development project still contains
its older DLL. The reported 30ms editor case, installation/reload, and Linux/
sanitizer verification remain pending. Details are in
[PERFORMANCE.md](PERFORMANCE.md).

Preview51 for Windows fixes computed inheritance of percentage/em line heights,
including explicit inheritance, pseudo values and numeric math expressions.
Chrome passes **508 checks**, and the new Godot fixture passes **2,400 rendered
checks** against explicit-pixel controls; preview50 fails 960 headless checks
in the same fixture. All nine CTest targets / **616,389 core checks** and all
22 host suites / **4,035 checks** pass. Layout-stress mean timings are nearly
flat, with mixed pair outcomes; this is a fidelity fix. Four existing gallery
captures remain exact, and Windows fresh-project/PCK/native export checks pass.
The old development-project DLL, the reported 30ms
editor result, Linux/sanitizer coverage and historical harvest calibration
differences remain open. Parent-relative line-height units and initial/root
metrics are not addressed by the inheritance fix.

The verified preview51 DLL has now also been installed in the actual development
project after confirming Godot was closed. The prior DLL is preserved for
rollback. Its matched standalone gallery runs drop from 18.530 to 2.668 ms
median whole-frame means, with p95 falling from 26.362 to 5.005 ms. Actual-project
host/line-height checks and a capture comparison pass. This closes the stale
Windows development-DLL issue; editor-specific overhead, cold-path costs and
the remaining product requirements still need their own work and evidence.

| Requirement | Evidence / remaining work |
|---|---|
| Live stylesheet replacement | Fixed `doc.css` appending old rules and the core ignoring newly added stylesheets until another mutation. Core and host regressions cover removal, empty CSS, focus/value/selection preservation, keyframes and custom-property registrations. |
| Fresh-project import | Fixed a Windows crash caused by compiling the host without its `godot-cpp` target's definitions. Fresh-cache import succeeds with normal editor initialization. Linux 4.7.2 immediate headless import reproduces [Godot #111645](https://github.com/godotengine/godot/issues/111645); the smoke lets the editor initialize for 60 frames. |
| Exported artwork and markup | Fixed imported textures disappearing from PCKs when the original file is absent. Project textures now resolve through `ResourceLoader`; raw PNGs retain the file reader. The isolated fixture covers HTML/CSS export filters, PNG/SVG intrinsic size, draws, stylesheet replacement and missing-artwork diagnostics. |
| Reproducible native builds | CMake now builds and consumes the `godot-cpp` target, inheriting its generated headers and ABI definitions. Fixed MSVC rejecting the blur kernel's captured array-bound constant. The Windows build and fresh-project/PCK smoke pass. Pin dependency/toolchain revisions and verify configuration variants before release. |
| Installable addon | A local Windows/Linux x86_64 preview ZIP installs into a fresh project and exports its HTML/CSS/SVG example. Includes licenses, checksums and installation instructions. Both platforms pass button/controller and two-way binding checks in the installed project and PCK. `check.sh` now packages and checks Linux; repeat on each shipped platform. |
| Native desktop exports | Standard Godot 4.7.2 debug, release and embedded-pack executables pass on Windows and Linux x86_64. Godot exports the exact selected library without manual copying. Relocated builds run with the source project hidden; example rendering matches the project. `check.sh` now requires native export checks and matching desktop templates. Other platforms, renderers and toolchain configurations still need their own evidence. |
| Replaced images in flex layouts | Visual review of the standalone example exposed zero-size images despite successful loading. Fixed intrinsic contributions and aspect-ratio sizing during flex reflow, including frames and percentage widths. Nine regression cases agree with Chrome; the example's render now shows its SVG and unclipped input text. |
| Native GUI integration | `WevaDocument` now derives from `Control`. Fixed duplicate typing across documents/native fields, clicks through native overlays, hidden-document input, lost Unicode, held Backspace and pointer focus. Native tests cover Tab/Shift+Tab, CSS click-through, CanvasLayer transforms, container/anchor resize, dropdown rows, outside popup dismissal and scene cleanup. This is an API migration for scripts typed as `Node2D`. |
| Text editing and popup lifecycle | Added consumed-text and non-wrapping focus APIs; readonly fields reject edits and undo. Native shortcut routing supports select/copy/cut/paste/undo/redo. Outside-press observers use a transient input version so a native handler can open a new popup without an older dismissal closing it. Manual popovers remain open. |
| Keyboard form actions | Buttons, links, checkbox/radio Space, radio arrows and Tab groups, range keys, summaries, popover triggers and implicit submission share native activation with pointer input. Focus changes cancel held Space. A nested button in a summary does not toggle its details. Chrome 151 passes 134 browser oracle checks; matching core cases and 35 native Godot checks cover event timing, ownership, value normalization and callbacks. See [KEYBOARD_INPUT.md](KEYBOARD_INPUT.md) for the implemented subset. |
| Two-way input bindings | Fixed a checkbox bound to a false boolean immediately reverting when checked, and Unicode text-input signals being decoded as Latin-1. Native tests cover both checkbox directions and non-ASCII text reaching the signal and bound field. |
| IME composition | Added preedit rendering, selection, lifecycle signals, one-step undo, full queued Unicode text and candidate caret geometry. Chrome passes 40 oracle checks; Windows/Linux Godot 4.7.2 pass 28 integration checks. Real Linux X11 IBus Pinyin passes nine checks in asynchronous mode. Default synchronous IBus mode still fails commit/cancel; real Windows and other IME sessions remain unverified. The full mutation corpus now passes 577,867 checks in Release and ASan/UBSan. See [IME.md](IME.md). |
| Long fields and Unicode editing | Single-line fields scroll with the caret; password masking and clicks map Unicode clusters to source bytes. Carets count trailing spaces, and transformed inputs clip their overlays correctly. Unicode 17 passes all 766 official segmentation cases; Chrome passes 91 editing checks and Windows/Linux Godot pass 86 checks with both fonts. Idle focused values no longer allocate a copy each frame. See [TEXT_EDITING.md](TEXT_EDITING.md) for browser tailoring and remaining bidi, wrapped navigation and pointer limits. |
| Native font positioning and themes | Shaped placement offsets and UTF-8 source clusters survive the C ABI, and automatic fallback glyphs use their actual native font. The Control's theme/default Font resource, overrides, variations and live resource changes now reach layout and paint. Native checks cover glyph placement, bitmap refresh, fallback changes, shared-resource teardown and texture ownership. Full paragraph bidi, bidi carets, vertical text, per-element CSS font-family selection and the documented stock-engine long-emoji bug remain open. |
| Viewport-relative font-size cache | Fixed stale font sizes after viewport/context changes. An empty `div` with `font-size:10vw;width:1em;height:1em` now changes from 10x10 to 20x20 when width doubles. The memo includes viewport dimensions, root metrics and DPI. Direct resolver and C ABI regressions, 80 Chrome checks, and Windows/Linux native geometry and rendered comparisons cover repeated resize and return to the original size. Preview45 fixes the nested relative-font chain limit and computed inheritance. Preview51 fixes inherited relative line-height lengths and numeric math expressions with 508 Chrome checks and Windows native controls. Absolute size keywords, root rem context and parent-relative line-height units still need conformance work. |
| Text length limits and paste | `maxlength` counts UTF-16 units for supported text inputs and textarea, including selection replacement, normalized pasted line endings and composition commit/blur. Number inputs ignore it; existing script values and undo are not length-truncated. Rejected typing creates no edit signal or undo step and stays consumed. Added `paste_text` (C ABI minor 6) so native clipboard insertion accepts leading whitespace and forms its own undo step. Fixed textarea Enter inserting beside selected text. Chrome passes 150 checks, Windows/Linux Godot pass 44 new checks, and the installed/exported example verifies Unicode limits and binding undo. Full form validation and the remaining picker/number editing behavior remain work. |
| Native IME comparison | Isolated the intermittent loss to Godot 4.7.2's X11 key handler suppressing an XIM commit before preedit completion. A standalone LineEdit reproduces it without Weva; matched unpatched builds fail 3/3 delayed sessions, while a local engine patch passes 3/3 for each control. Synchronous IBus 1.5.29 also lacks preedit hide/show handlers; a private backport of the upstream fix makes both controls pass commit, cancel and undo. The stock engine remains affected. Patches, traces and reproduction instructions are in [the standalone fixture](../tools/godot-ime-repro/README.md); these are diagnostic builds, not a shipped engine fix. |
| Select interaction | Native listbox click/drag, Ctrl/Meta toggling, Shift ranges, navigation and select-all are implemented. Parsed `size`/`multiple` changes invalidate child boxes independently of selectedness; `size=1` uses a dropdown. Disabled rows are skipped and no-op pointer choices emit no value event. Unicode typeahead follows the documented English ICU profile and Chrome event timing. Labels and group headings render and update through input versions. Held listbox drags autoscroll beyond the Control, including while CSS time is paused, with selection extended by real pointer movement and committed on release. Language tailoring and arbitrary popup row layout remain work. |
| Remaining game integration | Verify broader IME compatibility, touch-device and gamepad behavior, and accessibility. Picker-specific value sanitization, number stepping, remaining select behavior, picker controls, range direction/orientation, `minlength`, validity reporting and submission constraints remain release work. Basic editing and activation are not complete browser form behavior. |
| Browser behavior | Chrome leads when the C# reference is wrong. Preview56's fresh arbitration leaves five sample and five harvest findings, including the component-capture defect, along with documented unsupported features. Keep these visible and fix against browser evidence. See [ORACLE.md](ORACLE.md). |
| Release verification | Exercise installation and export on each declared supported platform. Keep compatibility declarations, binaries, public docs and automated gates consistent. |

This is an evidence ledger, not a reduced definition of completion. The addon
is not release-ready while any required installation, authoring, interaction,
export or compatibility behavior remains unverified or broken.
