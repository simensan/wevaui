# Clean Chrome CPU-stage comparison — 2026-10-31

After removing five leftover audit-owned Unity players and waiting for all
builds and the lifecycle soak to finish, three alternating native/Chrome rounds
repeated the prepared survival fixtures. No audit render processes remained at
the start or end. This supersedes the earlier current-build CPU comparison;
it is not a before/after optimization result.

Median of per-run means, milliseconds per changing update:

| Workload / stage | Weva | Chrome 152 |
| --- | ---: | ---: |
| HUD style + layout | 0.085 | 0.070 |
| HUD paint recording | 0.058 | 0.084 |
| Inventory style + layout | 0.280 | 0.142 |
| Inventory paint recording | 0.255 | 0.201 |

Inventory style/layout is about **97% higher** and paint recording about
**27% higher** than Chrome in this run. The gap is still open. HUD paint
recording is lower, but this does not establish total renderer superiority.

Weva's separately measured complete CPU path (controller, update, host capture,
resource preparation, encode and submission) is **0.394 ms HUD / 1.146 ms
inventory**. Chrome renderer-process raster-worker CPU is **0.021 / 0.082 ms**,
with different scheduling and cache boundaries. These cannot be combined into
a fair end-to-end ratio. Neither side here measures GPU execution or presented
frame latency. Chrome retained style/layout and raster work are zero in these
traces, with about 0.009–0.010 ms paint-recording events; native idle host work
is separately 0.193–0.217 ms and includes draw submission.

## Controls and limitations

- Identical hashed HTML/CSS/Open Sans/PNG fixtures and 20-step mutation list;
  20 warmup updates, 200 changing updates and 200 retained updates per case.
- Native/Chrome order alternates across three rounds, requested cadence 60 Hz.
  Observed mean native intervals are 16.972–17.018 ms; Chrome 16.597–16.599 ms.
  This is approximately matched cadence, not exact synchronized frame pacing.
- All twelve initial/final geometry comparisons pass the 1.5 px ceiling;
  maximum sampled target difference is **0.7537 px**.
- Windows RTX 5080, pinned Chrome 152, host-engine merge `c2288c71c`, current shared
  core containing `a0943b78`. Native executable hash, source revisions, raw
  stage results, fixture hashes and cadence checks are in the JSON receipt.
- No concurrent audit workloads or builds. This is a normal desktop, not an
  isolated laboratory machine; system CPU usage at the start was 12.1%.
- The native result comes from the host engine's explicit-source SDK tooling. It is
  not a fresh Unity/Godot paired speedup, nor a qualification of the host engine's
  older default prebuilt native pin.

The `boxes` diagnostic includes incremental layout work; summing cascade,
boxes and layout avoids treating a zero separate-layout counter as no layout.
Chrome stage durations use interval unions per thread to avoid nested-event
double counting. Stage architecture still differs across engines.

[Machine-readable receipt](clean-render-comparison-20261031.json).
Raw logs/traces and the run manifest are under
`.utmp/render-audit-20261030/clean-chrome-comparison/`.

## Excluded follow-up

A subsequent four-pair replay of the preserved flex baseline/candidate binaries
is **not accepted as clean performance evidence**. Another workspace's
`engine-6` runtime (PID 20208) started at 08:46:17 local, after this Chrome
comparison finished at 08:45:45, but during the paired replay, which ended at
08:48:08. It was left untouched. Executable hashes matched the original pair
and geometry checks passed, but timing results are retained only as excluded
evidence in the JSON. Historical per-host flex speedup percentages still need
an uncontended rerun. Future timing guards must inspect other workspaces too;
ownership-scoped cleanup must continue to leave them alone.
