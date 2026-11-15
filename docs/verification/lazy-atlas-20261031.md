# Lazy glyph-atlas storage — 2026-10-31

Every document previously allocated a 512 x 512 RGBA glyph bitmap immediately,
even with no text. Font setup marked the empty atlas dirty, causing a blank
texture to be generated on the next paint. The ABI recording backend retained
another copy of those pixels.

The shared core now allocates atlas pixels only after a glyph with a nonempty
bitmap passes the packing checks. An unallocated atlas never uploads an empty
texture. Spaces, failed rasters and oversized glyphs leave storage unallocated.
Atlas dimensions, packing, UVs and all populated-atlas behavior stay the same.
Once populated, storage is retained across clears to avoid allocation churn.
This is one shared-core fix for all hosts, with no C ABI change.

## Measurements

Baseline `25027414`; isolated native probe using the default font and recording
backend. The same process creates a document, installs the default font backend,
loads a 100 x 100 red panel, loads preformatted spaces, then loads `Hello world`.
The counter tracks requested live C++ `new` storage, not process-private or GPU
memory. Before creation both processes contain 47,177 tracked bytes.

| State | Baseline bytes | Candidate bytes | Saving |
| --- | ---: | ---: | ---: |
| Document created | 1,108,599 | 60,023 | 1,048,576 |
| Text-free panel after font setup | 2,212,410 | 115,162 | 2,097,248 |
| Preformatted spaces | 2,220,188 | 122,940 | 2,097,248 |
| Visible text | 2,225,993 | 2,225,993 | 0 |
| After destruction | 65,335 | 65,335 | 0 |

Creation saves exactly 1 MiB; the panel saves 2 MiB plus 96 bytes of texture
bookkeeping. This benefits unused documents and text-free overlays. It does not
reduce a normal text-bearing HUD's atlas, establish a GPU-memory number, or
attribute the earlier host-engine process-memory growth. First text paint now pays
the deferred allocation. No startup/frame-time speedup is claimed.

The regression pins zero pixel capacity and no texture before the first glyph,
including after clear, renderer release, spaces, failed rasters and rejected
oversized glyphs. It also verifies that the first visible glyph allocates the
expected full bitmap. Existing tests cover raster coverage, gutter pixels,
texture replacement, font changes, shadows and color glyphs.

## Verification

Final results and evidence hashes are in the adjacent JSON receipt. Raw probe
source, baseline/candidate binaries and CSVs are preserved under
`.utmp/lazy-atlas-20261031/`. The added regression is in
`libweva/tests/test_text_render.cpp`.

The qualified release gate passes without retry: 18 GCC suites and 20 Clang
sanitizer suites. The main suite passes 808,791 checks (808,785 with sanitizers),
including ten new atlas checks. Chrome layout and behavior gates pass with the
existing known IME exception. Godot font/Unicode, backend, interaction, reload
and debug/release export checks pass. Both Windows hosts rebuilt; Unity full
EditMode passes 255 with two environment-gated inconclusive, and the Mono player
passes 7/7. All 16 Unity renders and one Godot render match `25027414` exactly;
representative renders were visually inspected. Geometry hashes are unchanged.
