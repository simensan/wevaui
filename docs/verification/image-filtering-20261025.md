# Image filtering and thin rounded borders — 2026-10-25

Ordinary images and decoded background/mask layers now integrate source pixels
when shrinking and interpolate texel centres when enlarging. Premultiplied
sRGB filtering avoids colored fringes from invisible source pixels. This runs
during cached raster preparation; it changes no ABI or host renderer contract.
Border-image slices retain their existing nearest sampling.

Uniform rounded borders at or below 1 CSS pixel now emit the coverage profile
of a thin stroke instead of disabling antialiasing. Existing thick and
nonuniform borders keep their paths and the geometry allocation budget is
preserved.

Validation:

- GCC and Clang final builds: zero warnings; 14/14 CTest suites pass on each.
  Added image checks cover area averages, transparent colored texels, enlarged
  edges, strong minification and unchanged 1:1 opaque pixels. Border checks cover
  fractional widths/positions, straight-edge coverage, rounded edge coverage
  and transparent interiors. The existing allocation tests pass.
- Chrome hand layout oracle: 64/64 pass at 800×600, 1.5px ceiling, no gaps.
- Inspected the same 400×280 icon/border fixture rendered by headless Chrome
  and Agentic Vulkan, plus the live Convoy developer panel at 1280×800.
  The former jagged icon silhouettes and one-pixel rounded outlines are smooth.
- Agentic's `agentic_weva` finished-change gate and doctests pass with this
  core. Runtime compilation passes. No Unity/Godot integration, macOS or
  performance qualification is claimed by this scoped pass.

Logs: `filtering-build-gcc-final.log`, `filtering-build-clang.log`,
`filtering-test-gcc-final.log`, `filtering-test-clang.log`, and
`filtering-oracle-final.log`. Initial clock-skew warnings, a test-helper
signature mistake and an incorrect oracle executable path are retained in
the earlier logs; the final runs pass.

Chrome/Agentic captures and gate report live in Convoy's
`builds/test-evidence/weva-developer-ui/`: `filtered-icons-fixture.png`,
`filtered-icons-chrome.png`, `smooth-search.png`, and
`filtering-engine-gate/report.json`.
