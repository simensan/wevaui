# Grid browser regressions

These fixtures are versioned separately from the generated `corpus/` directory.
They cover aspect-ratio border feedback, named lines, implicit tracks, area-name
precedence, stretch constraints, and sparse/dense auto placement on both axes.

From the repository root, capture a fresh browser reference and compare it with
the candidate dump tool (substitute its build path):

```powershell
node Tools/Layout/capture-all-chrome-layouts.mjs godot-port/tools/oracle/regressions/grid 800 600 --metrics=mono
python godot-port/tools/oracle/chrome_sweep.py godot-port/tools/oracle/regressions/grid --weva-dump <weva_dump.exe> --width 800 --height 600 --chrome-metrics --out-dir .utmp/grid-regressions
```

The capture uses the synthetic mono font and normalized controls; it checks
geometry rather than rendered appearance. Generated captures are not versioned.
The corresponding core assertions live in `libweva/tests/test_grid.cpp`.
