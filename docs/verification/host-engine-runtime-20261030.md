# Host-engine rendered runtime audit — October 30, 2026

The accepted host-engine renderer changes and current Weva core now run together
in a published, immutable SDK and a live scripted survival UI project.
This adds real runtime binding, input, scene and reload coverage to the earlier
standalone rendering measurements. It introduces no new engine optimization.

## Build and scope

- Weva `faf04f31`; host-engine `be88b3cb2`, leased `engine-9` checkout.
- `WEVA_SOURCE_DIR` points to this Weva checkout. The official SDK builder
  built and published `cli headless runtime`, including the Weva feature.
- Runtime SHA-256:
  `3ea52566cb2b3ae54688bdf6b30dadf480f7856844804666cf8312293651ee67`.
- The managed `engine-main` checkout was not changed. This is validation of
  the published SDK, not integration into the host engine's default upstream branch.
- Windows, RTX 5080, Vulkan driver 617.42, optimized `fast` runtime,
  1280×720, threaded rendering, no VSync. The window was off desktop and
  unfocused; captures confirm actual rendered content.
- Synthetic 240-cube scene plus camera, light and script driver: 243 entities.
  This is not a representative production game or lower-end hardware test.

## CPU measurements

Three interleaved rounds per mode, 120 warmup and 600 measured frames per run:
9,000 measured frames total. Every report has zero marked frames and one
configuration. All 30 report/frame artifact hashes were independently checked.
`cpu_only` preserved normal renderer scheduling; all reports confirm zero GPU
timestamp frames. Numbers below are the median of the three run statistics.

| Mode | Whole-frame interval median | Whole-frame interval p95 | Main UI CPU p95 | Worker UI CPU p95 |
|---|---:|---:|---:|---:|
| UI hidden | 0.516 ms | 0.744 ms | 0.036 ms | 0.035 ms |
| Static HUD | 0.652 ms | 0.861 ms | 0.148 ms | 0.144 ms |
| HUD stamina binding every tick | 0.824 ms | 1.053 ms | 0.364 ms | 0.205 ms |
| Inventory selection every tick | 1.493 ms | 1.932 ms | 1.238 ms | 0.242 ms |
| Static interactive inventory | 0.590 ms | 0.835 ms | 0.167 ms | 0.164 ms |

Main and worker spans can overlap; do not sum them. Whole-frame intervals include
the scene and runtime overhead. Hidden UI still has host bookkeeping. Inventory
cycling changes twelve slot classes and selected-item text, icon and button
bindings every tick, considerably more often than a player normally clicks.
These measurements are neither a before/after speedup nor a matched Chrome
comparison. Small differences between static modes are not optimization claims.

## Interaction and lifecycle

The script polls HTML events and updates bindings. Input injection selected the
stone hatchet, updated its detail text/icon/highlight, invoked its enabled item
action, and closed the inventory. Snapshots confirm the selected index, click
counter, action counter and HUD mode; the selected-item capture was inspected.
All five profile modes were checked through script globals before measurement.

Three short reload batches reached 106, 209 and 339 cumulative `html_load` calls.
Afterward another click reached the controller and a new capture completed.
Process private bytes were 958,013,440 before the batches and 957,124,608 after;
working set was 471,195,648 and 470,069,248 respectively. These are whole-process
snapshots, not allocation tracking or proof of leak freedom. The batches lasted
seconds, not a long soak.

The initial generated scene omitted required transforms and failed to load.
That evidence is retained; its empty-world output is excluded from all results.
The generator now writes explicit transforms. The valid project is `v2`.

## Reproduction and evidence

`Tools/performance/create_host_audit_project.py` creates an owned project from
the prepared survival HTML/CSS, PNG and TTF fixtures used by the
[packed-paint audit](host-engine-packed-20261030.md). It refuses to overwrite an
existing directory. `--direct` generates a non-threaded variant; that variant
was not measured here. Keys 1–5 select the five modes; key 6 reloads every ten
ticks until another mode is selected. CLI injected keys are named `Num1`–`Num6`.

Use the published SDK's `host-tool dev` with explicit `--runtime`, `--engine`
and `--project`; do not use an unrelated executable on PATH. Per-mode profiling:

```text
host-tool profile --session <id> --sampling cpu_only --frames 600 --warmup 120 --label <round-and-mode> --timeout 90 --json
```

Raw launch/build/publication, input, snapshots, captures, profiles, lifecycle
observations and driver scripts are retained under
`.utmp/render-audit-20261030/`. The
[machine-readable receipt](host-engine-runtime-20261030.json) preserves individual
run statistics, hardware/configuration identity and hashed artifact paths.

## Shared-host status and remaining work

Shared layout and paint improvements remain in `libweva`; no duplicate host
implementation was introduced. The latest shared-core build was already
rebuilt and checked in both Unity and Godot; exact suite counts are in the
[baseline receipt](nested-baseline-20261030.md). This runtime-only tooling chunk
does not change either host binary or the ABI.

The host engine's packed Rust/wgpu snapshot improvement remains host-specific. Any
equivalent Unity/Godot change requires evidence that their renderer has the
same cost; copying the implementation is not appropriate.

Still outstanding: matched Chrome rendering comparisons, first-open latency in
the actual runtime, longer reload/memory testing, higher resolutions, lower-end
hardware, production-game load, and the direct rendering path. This receipt
does not change the development-preview release status.
