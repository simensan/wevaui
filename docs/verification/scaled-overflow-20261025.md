# Scaled overflow painting — 2026-10-25

Convoy's developer windows use reference-pixel layouts scaled to the game
viewport. The coarse paint cull compared cached layout-space overflow bounds
against device-space clips, dropping visible controls near a scaled window's
bottom and right edges. Transformed subtrees now retain their geometry for the
precise clip. Ordinary untransformed scrolling lists retain the coarse cull.
This is a correctness change; no performance improvement is claimed.

`test_paint_scaled_overflow_culling` checks rendered pixels inside and outside
both rectangular and rounded clips. `63-scaled-overflow` supplies the Chrome
reference (Windows Chrome 153.0.8010.54). This new geometric case does not
requalify or recapture the existing Chrome 152 corpus.

Validation in the `weva-developer-ui` worktree:

- GCC and Clang builds: no compiler warnings; final incremental builds also
  contain no build-system warnings (`layout-build-*-clean.log`). Initial
  Windows/WSL timestamp skew warnings are retained in the earlier build logs.
- CTest: 14/14 suites pass with each compiler (`layout-test-*-final.log`).
- Chrome hand corpus: 63/63 agree, zero gaps or crashes, at the 1.5 px gate
  (`layout-oracle-final.log`).
- Rebuilt Agentic development runtime: inspected the scaled search, colour
  picker, graphs and viewer windows at 1280×800.

PNG evidence is retained at
`E:/Convoy_Agentic/builds/test-evidence/weva-developer-ui/`, including
`scaled-overflow.chrome.png`. Unity/Godot host suites, sanitizer suites and the
other Chrome corpora were not rerun for this paint-only change.
