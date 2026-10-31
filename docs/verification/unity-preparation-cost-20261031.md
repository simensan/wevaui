# Unity preparation cost — October 31, 2026

Six fresh Unity 6000.4.1f1 editor processes measured the survival inventory on
an NVIDIA GeForce RTX 5080. Three cold/prepared pairs alternate execution
order. All six exited successfully, applied the quantity binding, and produced
identical PNGs. The prepared capture was visually inspected.

| CPU stage (ms, median of 3) | Cold | Prepared |
| --- | ---: | ---: |
| Activation and CPU preparation | 225.93 | 270.36 |
| Opening, including renderer synchronization | 238.69 | 12.37 |
| Renderer synchronization alone | 11.88 | 12.13 |
| First offscreen render plus synchronous readback | 17.76 | 16.95 |

Prepared activation happens during loading. It is not included in prepared
opening. Cold opening spans activation, binding validation and synchronization;
prepared opening spans showing, repeated CPU preparation and synchronization.
Prepared opening ranges from 12.33 to 20.82 ms; cold opening from 212.74 to
265.48 ms. Prepared activation ranges from 194.99 to 394.73 ms. Three samples
with this variability do not establish a general speedup percentage.

The remaining opening cost is almost entirely first renderer synchronization.
The next experiment should isolate or move that resource setup into loading,
then verify normal player rendering. This pass adds diagnostics only.

## Boundaries

- File reads occur before timing. Activation includes first-use managed/font
  work and an initial 640×480 editor viewport, then adjustment to 1280×720.
- These are fresh-editor diagnostics, not player FPS, pure core creation cost,
  GPU execution time, or a matched Chrome comparison.
- Offscreen render/readback is timed separately and includes synchronization.
  It is not representative of ordinary asynchronous URP rendering.
- Preparation moves work; it does not eliminate it. Showing preserves the
  prepared draw serial in every prepared run.
- Godot's hidden preparation behavior was already verified with 39 checks on
  both qualified hosts in the [preceding receipt](unity-hidden-preparation-20261031.md).
  This Unity renderer diagnostic changes no shared behavior or Godot code.

## Reproduction and evidence

Run from the repository with a new output directory:

```powershell
python Tools/performance/run_unity_preparation_audit.py --unity "C:/Program Files/Unity/Hub/Editor/6000.4.1f1/Editor/Unity.exe" --project . --fixtures .utmp/render-audit-20261030/survival-fixtures --output .utmp/render-audit-20261030/unity-preparation-bound --rounds 3
```

The fixture directory must contain inventory.html, inventory.css and their
referenced resources. The inventory must expose #item-quantity bound to Depth.
The diagnostic creates and destroys its own object without opening or saving
scene assets. Each run validates binding, dimensions, mode and retained paint;
the runner checks exit status and identical final images. Unity compilation
and all six diagnostic invocations passed. Python compilation and diff checks
also passed. No production code changed, so existing host suites were not rerun.

Raw logs, captures and results remain in
`.utmp/render-audit-20261030/unity-preparation-bound`.
The adjacent JSON preserves individual measurements and evidence hashes.
The earlier `unity-preparation-paired` exploratory batch is retained but
excluded: its fixture binding was missing, leaving literal braces in the UI.
