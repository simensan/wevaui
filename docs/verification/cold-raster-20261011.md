# Cold background raster follow-up — October 11, 2026

Cold HUD creation is **35.5% faster** in the paired GCC core benchmark:
51.755 → 33.368 ms, exceeding the requested 30% reduction for this workload.
This is a reduction in wall latency through parallel execution. It is not a
30% reduction in total CPU work, every UI operation, or host frame time.

## Change

The shared core prepares background tiles on the calling thread, then divides
expensive independent pixel rows between that caller and three temporary
workers. Each worker writes separate rows and joins before the call returns.
Sampling, arithmetic order, blend operations, masks and texture resolution are
unchanged. The complete HUD render is byte-identical to the previous commit.

At least four reported hardware threads, 16 raster rows and an estimated
262,144 pixel-sample-layer operations are required. Cheap flat-axis paths and
cross-row copy optimizations stay serial. A permit bounds worker groups; there
is no persistent thread pool or retained document work. The presence of
`WEVA_RASTER_SERIAL` at first use disables workers. The benchmark's POSIX CPU
sampler selects that path because its attribution table is single-threaded.

Public engine calls remain single-caller. An early test attempted concurrent
complete raster calls and failed because preparation uses existing static
parser scratch storage. That test was corrected to exercise repeated calls
with internally parallel pixel rows. Failed logs are preserved with the raw
evidence; concurrent document use is not supported by this change.

There is no ABI, CSS or host API change. Both hosts use the shared fix.

## Paired GCC measurements

Release GCC in WSL on the same Ryzen 7 9800X3D, 1280×720, baseline `56a10bc6`.
Three pairs per sample, each creating 40 fresh documents per executable.
The middle pair reverses before/after order. Builds and tests did not overlap
the measurements. The saved baseline executable includes the preceding blur
optimization. Reported values are medians of the three run means.

| Cold sample | Before | After | Reduction |
|---|---:|---:|---:|
| HUD | 51.755 ms | 33.368 ms | 35.5% |
| Glass | 53.291 ms | 43.594 ms | 18.2% |
| Menu | 17.985 ms | 13.480 ms | 25.0% |

HUD requested C++ allocations increase from 20,920 / 46,604,942 bytes to
20,929 / 46,605,230 bytes: nine worker control allocations and 288 bytes.
OS thread stacks are outside these allocation counts. The worker pixel loops
allocate nothing. The measurements exclude document destruction, host font
and asset loading, host draw submission and GPU execution.

These results do not qualify Unity or Godot frame times, busy game scenes,
lower-end hardware, all resolutions or aggregate CPU savings. CPU contention
can change the benefit. Even 33 ms is longer than a 60 Hz frame budget;
preparing and reusing screens remains relevant.

## Windows core cross-check

The rebuilt MSVC DLL also exceeds 30% on this HUD: median run means decrease
from **66.001 to 45.345 ms (31.3%)**. Three paired runs use the same viewport,
markup and CSS through the C ABI, with 40 fresh documents per run after one
discarded warmup and alternating order. The Python harness loads input bytes
outside the measured interval and destroys each document after timing. This
is a core DLL experiment, not a Unity frame measurement.

Median process CPU means increase from **64.844 to 72.656 ms (12.0%)**.
Windows process CPU accounting is coarse per iteration; the receipt includes
all 40 values per run. The result confirms the tradeoff: lower opening latency
uses more aggregate CPU time. Busy-game contention still needs measurement.

## Reproduce

Save a baseline `weva_bench` executable before rebuilding, then alternate it
with the candidate using:

```sh
weva_bench Tools/oracle/corpus/samples/hud.html Tools/oracle/corpus/samples/hud.css 40 --cold
```

Repeat with `glass` and `menu`, three pairs each. Do not overlap builds or
other benchmarks. The exact run logs and Windows harness are preserved under
`.utmp/cold30-20261011/`; the tracked JSON receipt retains results and hashes.

## Validation

The raster regression adds 24 exact-byte comparisons with a forced serial
implementation: odd dimensions, hard stops, repeated calls, blending, masks
and tile boundaries. The existing 192 portable/SSE2 blur comparisons remain.
These test implementation equivalence; Chrome remains the CSS reference.

The complete HUD PPM matches the previous commit byte for byte; its PNG was
also inspected. Windows Unity and Godot libraries rebuilt without warnings.
Unity Native EditMode passes 242 tests with the same two environment-gated
inconclusive tests. The complete `check.sh --release` gate passes: 14 GCC
suites, 16 Clang sanitizer suites, all 334 Chrome layout captures, 66 Chrome
behavior scripts plus the existing known IME failure, Godot scene checks,
Unicode safety and debug/release native exports. There are no skipped gates.
See the [verification receipt](cold-raster-20261011.json)
for the final release gate, paired measurements and evidence hashes.
