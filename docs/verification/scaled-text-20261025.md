# Scaled text — 2026-10-25

Uniform axis-aligned CSS scales now rasterize horizontal text and form labels
at their final pixel size. Glyph quads snap in screen coordinates, including
fractional window translations; shaping, wrapping and caret advances remain
in layout coordinates. Nested scales compose before glyph preparation. Rotated,
skewed, nonuniform and vertical runs retain the existing raster policy.

The atlas keys and raster requests use the same 1/64px quantization. Distinct
fractional sizes no longer share an unrelated bitmap. Transparent gutters retain
white RGB for coverage glyphs and extended edge RGB for color glyphs, avoiding
dark fringes under straight-alpha linear sampling. No C ABI change.

Validation on Windows/WSL:

- GCC and Clang: 14/14 CTest suites pass. Final builds have zero compiler
  warnings. The first GCC build reported filesystem clock skew; its subsequent
  final build and test run are clean.
- Direct geometry tests cover .75, 1.5 and mirrored scales with fractional
  translation. Atlas tests cover distinct fractional sizes and padding.
- Retained/full paint comparisons cover changing ancestor scales, nested
  transforms, form labels, individual scale/translate properties and removal.
- Chrome hand corpus: 64/64 agree at the 1.5px ceiling, without exceptions.
  Case 64 uses Chrome 153.0.8010.54 at 800×600 with mono metrics.
- Agentic Vulkan renders compare Open Sans SemiBold and Rye at native 12/24px
  against 16/32px scaled to .75. Before/after and Chrome screenshots were
  inspected. The scaled labels now preserve native-size bitmap clarity.
- Agentic's `agentic_weva` gate passes 76 tests (2 intentional ignores) and
  doctests. The rebuilt Convoy runtime was inspected at 1280×800.

Logs: `text-build-gcc-final.log`, `text-test-gcc-final.log`,
`text-build-clang.log`, `text-test-clang.log`, `text-oracle-800.log`.
Render evidence is in the Convoy workspace's
`builds/test-evidence/weva-developer-ui/fonts-{before,after,chrome}.png`.
The initial oracle invocation used the wrong viewport; its failure log is
retained separately. This is a core/Agentic check, not a new full Unity/Godot,
sanitizer, or macOS gate.
