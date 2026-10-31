# Scripted hidden preparation — October 31, 2026

Native Agentic scripts can now call `engine.html_prepare()` after loading a
screen, setting its data and hiding it. The runtime and editor apply the
request once after that tick's load, bindings and visibility requests, using
the existing `Overlay::prepare_snapshot`. Preparation does not show the UI,
claim input or advance animation time. It builds CPU layout, glyph and paint
resources; first GPU upload/pipeline work remains on first display.

```typescript
engine.html_load("assets/Ui/inventory.html");
engine.html_set("Title", "Inventory");
engine.html_show(false);
engine.html_prepare(); // synchronous: run during loading
// Later, after loading:
engine.html_show(true);
```

Repeated preparation requests coalesce within the tick and merge across script
passes. Draining/resetting clears the request. The editor consumes it even
when no screen is available, preventing a stale request from affecting a later
screen. Browser DOM hosts explicitly warn that this native operation is not
supported. Runtime `ui_prepare` records the synchronous preparation separately
in the main-thread section sum; it is not nested within normal `ui`.

## Runtime comparison

Three alternating cold/prepared process pairs use the same SDK, project and
machine. Each cold process opens normally. Each prepared process first loads
and prepares while hidden, then opens. Every operation retains 6,000 CPU-only
frames with the actual input tick inside the profile. Median of three
operation-level observations, milliseconds:

| Measurement | Cold opening | Hidden preparation | Prepared opening |
| --- | ---: | ---: | ---: |
| Main CPU | 20.4203 | 19.5915 | 0.5451 |
| Screen creation/loading | 2.7574 | 2.6911 | 0 |
| Explicit CPU preparation | 0 | 16.6611 | 0 |
| Main UI update | 17.5602 | 0.0011 | 0.0051 |
| Worker UI CPU | 1.2845 | 0.0260 | 1.2752 |
| Following interval | 20.5427 | 19.6475 | 0.6031 |
| Maximum of seven following intervals | 20.5427 | 19.6475 | 1.7858 |

The main-thread opening observation is 97.3% lower; the nearby interval maximum
is 91.3% lower. **This schedules roughly 20 ms of work during loading; it does
not eliminate that work or make cold rasterization faster.** Worker GPU setup
is still evident: one prepared opening's next main frame takes 2.094 ms and
its subsequent interval is 2.156 ms. Reporting only the input tick would miss
that cost. Worker spans overlap the main thread, and separate medians must not
be summed. Neither statistic measures GPU execution or input-to-photon latency.

The first cold sample is 91.334 ms and is preserved; the other two are 20.370
and 20.420 ms. This small batch does not establish a tail bound. Later new
glyphs, data, layout, viewport or fonts can require more work. The comparison
uses one Windows/RTX 5080 machine, no VSync, an off-desktop 720p window and the
same synthetic 240-cube scene. No builds/tests ran concurrently with profiles.

## Correctness and evidence

- 432 script API tests pass, with 2 existing ignored tests. Coverage includes
  preparation visibility independence, one-shot draining and request merging.
  Receipt: Agentic `builds/verify/20261031-015445`.
- Five capture-trace tests verify preparation is counted once in the main
  sum (`20261031-015453`). Nine render-thread tests and the runtime production
  compile pass (`20261031-014950`). The prior 44 Overlay tests cover the
  preparation primitive, retained frames, bindings, input and reloads.
- Three optional-Weva editor queue tests pass. Enabling this feature exposed
  old tests still using four-argument `record` calls and string-only events;
  those tests were corrected to the existing production API. The focused
  verifier omitted the optional feature, so a scoped Cargo invocation was
  required. Logs: `prepare-editor-tests.log` and `prepare-editor-tests-fixed.log`.
  API feedback records `verifier-optional-feature-selection`.
- The first runtime launch found an omitted generated API-table entry.
  `f0a91651a` fixes the export after `741203e31` adds the request/host integration.
  Failed evidence remains in `hidden-prepare-paired/`; its session stopped.
  The corrected official CLI/headless/runtime SDK builds and executes the
  TypeScript API successfully.
- Nine paired profiles retain 54,000 unmarked frames, 243 entities throughout,
  verified immutable artifact hashes and six successful session stops. All
  three hidden captures equal their pre-preparation world captures. All six
  visible inventory captures are identical, and match the previously inspected
  inventory image.
- A separate two-profile smoke session adds 12,000 frames and verifies real
  selection, equip and close through controller globals after hidden preparation.
  That session also stops. These samples are excluded from paired statistics.

The generator's opt-in `--prepare-input` enables key 8. The cold runner accepts
`--prepare-hidden` or per-label `--prepare-engine LABEL`, allowing alternating
fresh-process comparison within one SDK. `--check-actions` exercises the 720p
fixture after profiling. New receipts retain eight-frame opening windows.

[All paired observations, runtime hash and SDK identity](script-preparation-20261031.json).
Raw evidence: `.utmp/render-audit-20261030/hidden-prepare-paired-v2/`,
`hidden-prepare-actions/` and `hidden-prepare-sdk-exported.log`.

Shared core remains `d863b7d3`; no ABI, CSS or font output changed. Godot already
has explicit manual document updating; this Agentic scripting request is not a
new Godot/Unity API or performance result. Unity's public preparation workflow
and other-host timing still need separate validation. No full release gate,
live editor render, browser execution, upstream merge, push or configured-SDK
switch is claimed. The rendering audit remains open.
