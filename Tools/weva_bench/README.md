# Core performance benchmark

`weva_bench` measures the headless core with its fixed font metrics. Rendered
Godot frame timing is measured separately by the host's
[`layout_stress_probe.gd`](../../hosts/godot/project/layout_stress_probe.gd).

## Windows build

From the repository root, with CMake, Python and Visual Studio C++ tools installed:

```powershell
cmake -S . -B build-core -G "Visual Studio 17 2022" -A x64 -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded
cmake --build build-core --config Release
ctest --test-dir build-core -C Release -R weva_bench_cli --output-on-failure --no-tests=error
```

The initial configuration fetches pinned ICU sources. Offline builds can set
`FETCHCONTENT_SOURCE_DIR_WEVA_ICU_SOURCE` to an existing checkout of that pinned
source, as described in [the ICU build notes](../../third_party/icu/README.md).

## Workloads

The positional arguments are HTML, CSS and pass count. Use an empty CSS argument
when the document needs only the user-agent stylesheet. For example:

```powershell
$bench = '.\build-core\tools\weva_bench\Release\weva_bench.exe'
$html = 'Tools/oracle/corpus/samples/layout-stress.html'
$css = 'Tools/oracle/corpus/samples/layout-stress.css'
& $bench $html $css 100 --cold
& $bench $html $css 500 --full --dt=0.016666666666666666
& $bench $html $css 500 --full --mutate=layout --target=last
& $bench $html $css 500 --full --mutate=paint --target=last
```

| Mode | Timed and counted work |
|---|---|
| Default | Box construction, layout and positioning; parsed DOM and cascade are prepared beforehand. |
| `--cold` | Fresh document creation, CSS/HTML loading and initial update at time zero; destruction is excluded. The cross-document raster cache is off, so every pass is a first opening. |
| `--reopen` | As `--cold`, with the cross-document raster cache on: after the first pass, a screen created again. `best` is the reopen. |
| `--full` | Update after initial document/atlas preparation; the caller's mutation is excluded. `--dt` advances CSS animations. |

Allocation counts cover the final measured pass and report C++ `operator new`
calls and requested bytes. They are not total process memory. Earlier passes
warm reusable storage in default/full mode; every cold pass creates a new
document. An idle full update can correctly report zero work.

## Allocation attribution

Add `--profile` to any workload to group allocations by their captured call
stacks. It records the final pass and limits the run to at most 20 passes.
The printed timings include stack capture; use a separate unprofiled run for
latency comparisons. The table holds up to 4,096 distinct stacks and prints
the largest 12 groups.

For named Windows stacks, build with matching local PDBs:

```powershell
cmake --build build-core --config RelWithDebInfo --target weva_bench
& '.\build-core\tools\weva_bench\RelWithDebInfo\weva_bench.exe' $html $css 3 --cold --profile
```

Windows captures the current thread with
[`CaptureStackBackTrace`](https://learn.microsoft.com/en-us/windows/win32/debug/capturestackbacktrace)
and resolves local symbols after measurement with DbgHelp. Without symbols,
it prints instruction addresses. POSIX builds use `backtrace`.

The optional `--sample` CPU-time profiler uses POSIX `SIGPROF`; Windows rejects
that option explicitly. It cannot be combined with allocation profiling, and
`--sample-depth` must be 1–6. Missing inputs and nonpositive pass counts also
exit with an error instead of reporting a measurement.
