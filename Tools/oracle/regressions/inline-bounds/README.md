# Inline client bounds browser regressions

These seven fixtures compare wrapped lines, nested block continuations, descendant
overflow, relatively positioned blocks, generated blocks and decorated empty
split fragments, plus a zero-size continuation. All seven match Chrome with the normalized mono-font harness.

Capture and compare from the repository root:

```powershell
node Tools/Layout/capture-all-chrome-layouts.mjs Tools/oracle/regressions/inline-bounds 800 600 --metrics=mono
python Tools/oracle/chrome_sweep.py Tools/oracle/regressions/inline-bounds --weva-dump <weva_dump.exe> --width 800 --height 600 --chrome-metrics --out-dir .utmp/inline-bounds
```

Evidence: `.utmp/parity68/inline-fixtures5`.

Native C ABI resize and relative-position checks live in
`test_abi_inline_fragment_bounds`. Native bounds retain their existing transform
semantics; the browser dump applies transforms to the continuation's parent.
