# Container query browser regressions

Capture and compare from the repository root:

```powershell
node Tools/Layout/capture-all-chrome-layouts.mjs godot-port/tools/oracle/regressions/container-queries 800 600 --metrics=mono
python godot-port/tools/oracle/chrome_sweep.py godot-port/tools/oracle/regressions/container-queries --weva-dump <weva_dump.exe> --width 800 --height 600 --chrome-metrics --out-dir .utmp/container-regressions
```

The current source matches all 15 fixtures. `unboxed` now includes the inline
wrapper's anonymous block continuation in browser bounds; the conditional style
result was already correct. Evidence: `.utmp/parity68/inline-container3`.

The fixtures cover named/nested/axis selection, shorthand, relative lengths,
conditional pseudos, containment in flex/grid, and narrow-container overflow.
Captures use the normalized mono-font harness, not stock browser appearance.
Native live-update tests are in `libweva/tests/test_c_abi_incremental.cpp`.
