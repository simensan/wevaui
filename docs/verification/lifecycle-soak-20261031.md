# Host-engine reload soak — 2026-10-31

A 30-minute survival-UI reload run completed **111,587 reloads**, five UI
modes, selection/use/close actions, and selection after the reload loop.
The owned runtime shut down successfully. This extends the earlier short
reload check; it does **not** establish leak-free operation.

The 317 samples span 1,800.4 seconds. Every observed reload counter advances
and every soak sample remains in reload mode. Windows process-private memory
rose from **835.2 to 848.9 MiB** (+13.7 MiB). Five-minute median private memory
continued rising: 840.7, 842.5, 844.3, 846.6, 847.5, 848.9 MiB. Allocation
attribution and a matched no-reload control remain necessary; these counters
alone cannot distinguish live leaks, bounded caches, allocator high-water
behavior, script/runtime allocations, or observation overhead.

Windows per-process GPU performance counters, summed across reported adapters,
show dedicated allocation falling from **531.5 to 523.6 MiB**, then remaining
near 523.6 MiB. Shared GPU allocation has five-minute medians of 125.1 MiB.
These are process allocation counters, not individual UI resource lifetimes;
shared allocations must not be added to process memory as independent usage.

## Scope and reproduction

The immutable SDK is
`db868f7bb698e366c41911bf6f45e70733c9678e01acdf34b7f41e3730ca5248`,
the pre-merge host-engine audit build with the accepted shared-core optimization.
It is not the later merged SDK. The Windows/RTX 5080 fixture contains the
survival UI and 240 cubes at 1280×720. It requests a page load every ten ticks.
The script counter records requested loads; successful interaction afterward
also verifies the resulting document remains usable.

```
python Tools/performance/run_host_runtime_audit.py --project <audit-project> --output <new-directory> --engine current=<sdk> --rounds 1 --reload-seconds 1800 --gpu-memory --skip-profiles
```

The runner records CPU/GPU samples, checks reload progress, and stops its
session in cleanup. The live run began before the explicit progress guard was
added; all saved samples were subsequently checked for mode and increasing
counters. Other builds ran concurrently, so no speed claim is made here.
The current runner also passed the merged SDK's short interaction/reload check.

Evidence: [summary and sample hash](lifecycle-soak-20261031.json), with raw
samples, session artifacts, captures and successful stop receipt under
`.utmp/render-audit-20261030/lifecycle-soak-30min/`. CPU-memory growth remains
an open qualification issue, as do longer runs on target shipping hardware.
