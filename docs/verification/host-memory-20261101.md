# Godot and Agentic memory audit — 2026-11-01

Godot's tested document, reload and theme lifetimes remain bounded. Agentic's
rendered-process memory still grows in the preserved SDK, including a 60-update/s
control. No allocation owner has been proved for that growth; no runtime patch
or leak-free claim follows from these counters.

This pass adds reusable Godot lifecycle regressions and fixes a gap in the
Agentic audit workflow: it can now hold an idle inventory, warm before measuring,
and sample process memory without allocating a runtime snapshot every five
seconds. Samples explicitly mark whether their globals were queried or are the
last observed values. Endpoint-only mode verifies mode and reload progress at
the endpoints; it does not claim continuous progress checks during the interval.
The default snapshot mode retains its stall detection.

## Godot: direct rendered lifecycle checks

Qualified patched Linux Godot 4.7.2, compatibility rendering through Mesa llvmpipe,
using the current built shared core/addon. This is a software-rendered test, not
a Windows/NVIDIA driver leak qualification. Four independent processes each run
401 iterations with real font rendering, inputs, gradient/rounded panels and
shadows: idle, full markup reload, create/destroy, and theme/font replacement.

Each case passes **67 checks**, for **268 total**. The checks include live text,
actual panel pixels, stable node/object/cached-resource counts, and at most
256 KiB of added Godot engine allocations after iteration 80. The resource
monitor counts ResourceCache entries; the separate object monitor covers live
Godot objects. Allocations made directly by the C++ standard library are not
all part of Godot's MEMORY_STATIC counter; process RSS is recorded separately.

| Case | Warm → final objects | Nodes | Cached resources | Engine allocation delta |
| --- | ---: | ---: | ---: | ---: |
| idle | 1511 → 1511 | 3 | 2 | 1,564 bytes |
| reload | 1511 → 1511 | 3 | 2 | 1,572 bytes |
| cycle | 1506 → 1506 | 2 | 2 | 13,084 bytes |
| theme | 1515 → 1515 | 3 | 2 | 1,564 bytes |

RenderingServer texture/buffer counters stay at their warm values in all cases.
All four final renders were saved; representative renders were visually inspected.
No Godot retention defect was confirmed in these short stress cases. This does
not cover arbitrary URL churn, every font/content set, or a long hardware soak.

## Agentic: matched rendered controls

All six five-minute processes use **the exact SDK from the previous 30-minute
finding**, `db868f7bb698e366c41911bf6f45e70733c9678e01acdf34b7f41e3730ca5248`,
source commit `6ab7386e36d2f18fdf8c83150b2e9ea1f6e130de`.
Runtime SHA-256: `14eaef088abebae115de2901f9bc5b704ce128e5f2f77a515f6e3a3365fe616b`.
This is a preserved historical SDK, not a build of the current shared-core HEAD.
The purpose is to reproduce and narrow the old finding consistently.

The 1280 x 720 survival UI runs over 240 cubes. Each process warms for 30 seconds,
then records 61 process-memory samples over about five minutes. Idle/reload
snapshot controls run together; process-only controls run sequentially; the two
60 FPS controls run together. Other workspace activity remains allowed, so this
is not an isolated-machine experiment or a performance comparison.

| Workload | Runtime queries during samples | Start → end private MiB | Change |
| --- | ---: | ---: | ---: |
| Uncapped idle | 61 | 848.08 → 852.06 | +3.98 MiB |
| Uncapped reload | 61 | 834.98 → 845.84 | +10.86 MiB |
| Uncapped idle, endpoint queries | 1 | 850.15 → 846.27 | -3.88 MiB |
| Uncapped reload, endpoint queries | 1 | 847.81 → 913.99 | +66.18 MiB |
| 60 FPS idle, endpoint queries | 1 | 839.13 → 854.52 | +15.38 MiB |
| 60 FPS reload, endpoint queries | 1 | 841.37 → 919.57 | +78.20 MiB |

The endpoint runs also query final state after the interval. Intermediate samples
mark their globals as last-observed rather than fresh observations. Every run
verifies selection/use/close and a selection after the soak, then stops its owned
session successfully. Counters report requested loads, not a guarantee that each
request was separately presented by the GPU.

The no-query idle case ranges only 846.16–846.27 MiB after minute two. By contrast,
the queried idle case rises 850.98–852.06 MiB over that late window. This is evidence
that observation changes the measurement; separate process histories and other
activity prevent assigning the entire difference to one allocator or cache.

Removing queries does not resolve the reload growth. Its uncapped control shows
a large allocation step and ends +66.18 MiB. Normal cadence does not resolve it
either: the capped reload process ends +78.20 MiB. Its UI assets hash-identically
to the original fixture. Only the generated driver's FPS setting differs in the
relevant workload. Observed script update rates are 59.997/s idle and 59.999/s
reload; these rates do not measure GPU presentation. The capped reload counter
ends at 2,005 requested loads including warmup. The FPS cap is a control, not a
workaround claimed to fix the growth. Step-shaped private-memory changes must
not be extrapolated as a linear leak rate.

## Script allocation control

The same SDK's headless script runner executes the original driver for 120,000
frames each in idle and reload modes, with `AGENTIC_ALLOC_TRACE=1` and six samples.
Both retain a **1 MiB guest heap**, zero tracked live guest allocations at every
sample, and the same host-container census (22 entries: 16 lookup plus six
published globals). Total allocation/free counters both end at 1,920,000.
This verifies balanced guest temporaries in this control; the headless host does
not render/rebuild the native HTML UI or include the 240-cube rendered world.
It narrows the driver hypothesis without substituting for rendered allocation
attribution. Untraced controls are preserved too; their zero tracing counters
were not interpreted as proof of zero allocations.

## Remaining finding and next investigation

The old Agentic process-growth finding remains open. The measurements cannot yet
distinguish native shared-core retention, Rust renderer/font/paint ownership,
allocator high-water state, GPU upload retirement, driver storage, or runtime
observation caches. Missing low-interference live/capacity accounting was recorded
through Agentic's local API-feedback workflow as
`weva-runtime-memory-ownership-census`, scoped to this measured SDK.
The next useful step is allocation attribution across those owners in the
rendered runtime, including a cooldown/retirement measurement. Private-memory
growth alone is insufficient evidence for a safe runtime code change.

## Reproduction and validation

Generate a normal-cadence fixture with
`Tools/performance/create_agentic_audit_project.py <fixtures> <new-project> --max-fps 60`.
Run `Tools/performance/run_agentic_runtime_audit.py --project <project> --output
<new-output> --engine same=<SDK> --rounds 1 --skip-profiles --reload-seconds 300
--lifecycle-warmup 30 --lifecycle-mode idle --lifecycle-observation endpoints`;
repeat with `--lifecycle-mode reload`. Omit `--lifecycle-observation endpoints`
for continuous runtime snapshots and progress checks.

Godot regression: run `memory_lifetime_tests.gd` with the project and a real
renderer, passing `-- --mode=idle`, `reload`, `cycle` or `theme`. Set
`WEVA_MEMORY_OUT` to an existing output directory to save render evidence.
The regression emits JSON monitor samples. The retained diagnostic Python runner
also records Linux process RSS from `/proc` and enforces an owned-process deadline.

Tooling passes **79 tests on Linux**, and **70 pass / nine platform skips on
Windows**. Seven new tests pin lifecycle progress/control and endpoint validation.
Both snapshot and endpoint idle/reload smoke invocations pass, with successful
session cleanup. An initial CLI validation test omitted required `--output`;
its argument setup was corrected. No shared-core, Unity, Godot native or Agentic
runtime source changed. Previous runtime qualification applies to those unchanged
sources; this audit is not a new complete cross-host release gate.

Raw process samples, queries, owned-session stop receipts, script traces, renders
and diagnostic reproducers are retained under `.utmp/host-memory-20261031/`.
The adjacent JSON hashes inputs and evidence and contains the measured summaries.
