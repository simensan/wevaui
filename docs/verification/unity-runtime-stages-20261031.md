# Unity retained runtime CPU stages — October 31, 2026

Three fresh Unity editor processes run idle HUD, changing HUD and changing
inventory cases. Each case has 128 explicit warmup iterations followed by
2048 measured iterations: 18,432 measured updates in total. The production
binding, core update, texture/mesh synchronization and command-recording paths
run at 1280×720. No production rendering behavior changed in this pass.

Median of the three per-run p95 values, milliseconds:

| Workload | Binding/mutation/update | Renderer sync | Command recording | Per-iteration CPU total |
| --- | ---: | ---: | ---: | ---: |
| Idle HUD | 0.0003 | 0.0001 | 0.0333 | 0.0338 |
| HUD text and stamina bar | 0.0956 | 0.0469 | 0.0230 | 0.1609 |
| Inventory highlight and quantity | 0.2643 | 0.1808 | 0.0695 | 0.5164 |

Totals are calculated for each iteration before taking percentiles, not by
adding independently calculated stage p95 values. Individual summaries and
raw-sample hashes are in the adjacent receipt. No measured sample was discarded.

HUD changes typically retain 56/60 batches and upload 92 triangles. Inventory
changes typically retain 128/137 batches and upload 1244 triangles. Idle updates
perform no mesh rebuild/upload. A broad static batching rewrite is not justified
by these results. If further CPU work is pursued, profile the inventory update
and changed mesh uploads, then measure an isolated candidate against this baseline.

## Workload boundaries

- HUD: the bound stamina number and width cycle from 30 to 100. Inventory:
  quantity cycles through those values and two slot highlights alternate. This
  is a rendering mutation workload, not the complete inventory click/controller
  flow; details text and the equipped item are not changed by selection.
- Timers include mutation/binding/update, renderer Sync, then command-buffer
  Clear/Draw. Strings and numeric values are prepared outside the measured loop.
- Commands are recorded but **not submitted** during measurement. GPU execution,
  submission backpressure, game logic, input routing, URP scheduling, frame pacing
  and presentation are excluded. These are not frame-time/FPS measurements.
- UI stays hidden and is driven explicitly. The tested fixtures have no backdrop
  filter; target-dependent backdrop recording is outside this diagnostic.
- After each workload, a real offscreen capture of the retained renderer is
  compared pixel-for-pixel with a newly constructed renderer using the same core
  draw list. This checks host cache equivalence, not Chrome layout parity.
- Each measured mutation must change paint and apply the expected bound value;
  idle calls must retain the draw serial and return false from Sync.

Environment: Unity 6000.4.1f1, RTX 5080, the existing survival fixture assets.
Cold creation is excluded. Workloads run in fixed idle/HUD/inventory order in
each process, with separate document/renderer lifetimes and warmup per workload.
Do not transfer these results to Godot or Agentic; their upload paths differ.

## Reproduce and evidence

```powershell
python Tools/performance/run_unity_runtime_audit.py --unity "C:/Program Files/Unity/Hub/Editor/6000.4.1f1/Editor/Unity.exe" --project . --fixtures .utmp/render-audit-20261030/survival-fixtures --output .utmp/render-audit-20261030/unity-runtime-stages-repeated --runs 3
```

The output path must be new. Logs, raw samples and PNGs are retained. The runner
checks Unity exit status, scenario/sample configuration and identical images
across repeats. All three processes completed successfully. The initial
`unity-runtime-stages-0` preflight also passed; its HUD and inventory captures
were inspected and match the repeated outputs. Python compilation and diff
checks passed. No production code changed, so host correctness suites were not
rerun. This is a scoped CPU audit, not a full rendering/release gate.
