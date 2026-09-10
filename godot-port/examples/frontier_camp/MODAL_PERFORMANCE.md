# Modal layout performance

Runtime66 adds automatic retained layout for ordinary modal opening/closing.
There is no authoring API change: `show_modal_dialog`, `close_dialog`, focus
and `bind_state` continue to work as before. The current supported CSS/top-layer
semantics are unchanged.

The engine rebuilds the modal, its ancestor scaffold and affected panels with
the normal pipeline. Unchanged positioned panels are deferred in a scratch
tree; their complete containing chains must retain equal geometry before the
transaction commits. Paint boundaries validate effects/clipping and keep
stable draw identities. Unsupported shared dependencies still rebuild fully.
A simultaneous layout change outside the modal also uses the full path.

Core comparisons cover 15 CSS variants over 24 opening/closing steps each,
including relative/transformed ancestors, opacity/filter/clipping, a scrolled
panel, `:has` changes, generated content/counters, anchors, sticky positioning,
flex ancestors and a non-positioned backdrop override. Mid-sequence color,
width and content changes are compared with a forced full rebuild. The plain
case additionally asserts actual retained draws, including reuse of all
remaining draws after closing.

## Matched Windows release comparison

Godot 4.7.2, Ryzen 7 9800X3D / RTX 5080, 1280 × 720. Three paired
runs per runtime and renderer, alternating runtime/renderer/workload order,
120 warmup and 600 measured frames per case. No concurrent builds, sanitizer
tests or profiling logs. Values are medians of per-run changing-frame CPU
p95, except idle which includes every frame. The timer includes native input,
game callbacks, bindings and document update; later canvas/GPU work is separate.

| Case | Runtime65 OpenGL | Runtime66 OpenGL | Runtime65 Vulkan | Runtime66 Vulkan |
| --- | ---: | ---: | ---: | ---: |
| Open/close settings | 2.380 | 1.483 | 1.936 | 1.477 |
| Idle tick | 0.005 | 0.006 | 0.006 | 0.005 |
| Continuous meters/labels | 0.285 | 0.340 | 0.356 | 0.307 |
| Inventory reorder | 0.710 | 0.917 | 1.017 | 0.887 |
| Typing | 0.505 | 0.596 | 0.551 | 0.549 |
| Slider | 0.334 | 0.354 | 0.482 | 0.500 |
| Hover | 0.172 | 0.203 | 0.248 | 0.198 |

Modal p95 falls **38% on OpenGL and 24% on Vulkan**. Mean changing-frame
CPU cost falls from 1.774 to 1.061 ms and 1.563 to 1.054 ms respectively.
The runtime65 control is remeasured here; it differs from the earlier report
because timings vary between sessions. These are not worst-case bounds.

All **84 before/after workload screenshots are byte-identical**. The final
build passes 9/9 core CTest suites, 11/11 ASan/UBSan suites (including positive
controls and the 47-sample mutation corpus), 8,440 Godot host checks, fresh
sample-project checks on both renderers and a relocated Windows release
export with exact project pixels.

The short matrix shows higher OpenGL tails for some non-modal workloads;
these are retained above rather than hidden behind the modal improvement.
The longer focused follow-up below investigates them.

## Longer OpenGL follow-up

Three additional matched pairs use 120 warmup and 1,800 measured frames per
case (300 measured changes for sorting, typing and modal toggles). Runtime and
workload order alternate. Values are medians of per-run changing-frame p95,
in milliseconds:

| Case | Runtime65 | Runtime66 |
| --- | ---: | ---: |
| Continuous HUD | 0.388 | 0.283 |
| Inventory reorder | 1.163 | 0.877 |
| Typing | 0.571 | 0.468 |
| Settings toggle | 2.353 | 1.295 |

This follow-up does not reproduce the non-modal regressions in the short
matrix. Non-modal costs were not the optimization target, so do not infer a
general HUD/typing/sorting speedup from their differing results. Modal mean
CPU falls 1.632→1.000 ms and p95 falls 2.353→1.295 ms in this pass.

An initial longer after-run failed the no-gameplay-input assertion during the
meter-only case, which sends no input. Its logs remain under
`.utmp/runtime66/focused/` and are excluded from the results. The complete
rerun with unchanged checks passes; all six accepted receipts are under
`.utmp/runtime66/focused2/`, alongside `comparison.json`.

## 4K 3D load

Three runs per renderer use the same 480-instance animated 3D fixture at
3840 × 2160 with native `_process` and deferred bindings, 120 warmup and
600 measured frames per case. All six runs pass, and all **18 workload PNGs
match runtime65's corresponding 4K captures exactly**. The table shows medians
of per-run mean whole-frame intervals, in milliseconds:

| Case | OpenGL | Vulkan |
| --- | ---: | ---: |
| UI disabled | 2.586 | 1.502 |
| Idle UI | 2.799 | 1.440 |
| Settings toggle | 2.665 | 1.384 |

Changing-frame settings whole-frame p95 is 5.418 ms on OpenGL and 3.341 ms on
Vulkan. These include the entire 3D scene, rendering and platform waits; they
are not UI-only CPU times. Offscreen uncapped scheduling can make an enabled
case faster than its disabled baseline. No target-game FPS claim follows.

## Lifecycle, installation and reproduction

Both 4K lifecycle runs pass 100 measured reopens and 100 recreations each,
plus warmups and 30 seconds of sustained updates per renderer. Native UI
weak references are released and state signals disconnected after destruction;
prepared/cold images match. The sustained phases cover 10,773 OpenGL frames
and 20,956 Vulkan frames. Nodes stay at 496 during the soak and return to 491
after teardown. Object counts stay fixed during each soak. Whole-process
private memory changes 465.53→465.34 MiB on OpenGL and 1007.29→936.61 MiB on
Vulkan. These short runs are not multi-hour leak proofs or lower-end-device
budgets; the earlier longer runtime65 results remain separate evidence.

Runtime66 is installed in both the development project and Frontier Camp,
with hash-checked runtime65 backups. Actual-project binding tests and sample
headless/OpenGL/Vulkan checks pass. The original cold-construction cost and
broader platform/device release requirements are not closed by this change.

Reproduce with a matching installed addon and export templates; use new output
directories. `--project` and `--executable` can select frozen matching projects
and release exports for alternating before/after comparisons:

```sh
python godot-port/hosts/godot/run_frontier_perf.py --godot /path/to/godot \
  --output /new/modal-results --runs 3 --frames 600 --warmups 120 --capture

python godot-port/hosts/godot/run_frontier_perf.py --godot /path/to/godot \
  --output /new/modal-4k --automatic --world 3d --resolution 3840x2160 \
  --runs 3 --frames 600 --warmups 120 --capture \
  --cases ui_disabled,idle,settings_toggle

python godot-port/hosts/godot/run_frontier_lifecycle.py \
  --executable /new/modal-4k/export/FrontierCamp.exe \
  --project godot-port/examples/frontier_camp --output /new/modal-lifecycle \
  --renderer mobile --resolution 3840x2160 --cycles 100 --seconds 30 --first-hidden
```

Local evidence under `.utmp/runtime66/`: `paired/comparison.json` and all
per-run receipts/PNGs; `focused2/comparison.json`; `load-4k/summary.json` and
`pixel-comparison.json`; `lifecycle-{gl_compatibility,mobile}/verification.json`
and process-memory samples; `core-tests-final.log`, `asan-tests-final.log`,
`final-host-checks/verification.json`, `final-verified/verification.json` and
`installed.json`. The diagnostic profile under `profile/` is instrumented and
excluded from timing tables. `paired_perf.py` records the alternating order.

Native library SHA-256:

- Runtime65: `4504fdf5c5fc2712b1d87dbe97b4246541dcbf61b5babdf1ef23cb60136a8e9f`
- Runtime66: `df1285043854ec8887da980ff94e8bae1fa5f2eba3710cf7b7cb571085a10b66`
