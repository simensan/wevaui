# Matched Chrome / Weva layout benchmark — October 30, 2026

Corrects the previous benchmark's unequal timing boundaries. Both engines now
time exactly the same operation: set the target's style attribute, resolve
style/layout synchronously, and read target bounds. Strings and element lookup
are prepared outside timing. Neither engine paints in the timed region.

Weva uses its existing `weva_document_update_geometry` API; no engine or ABI
change was required. The native benchmark asserts zero draw serial, draws and
textures. The runner rejects old binaries and retains failed evidence. Panel
and final-cell mutations are reported separately. Cold creation was removed
because browser DOM replacement and native document creation differ in their
lifecycle costs. See [instructions](../../Tools/performance/README.md).

## Results

Windows x64 Release, Chrome 152.0.7977.82, 1280×720. Ten rounds of 1,000
operations per scenario per engine. Initial geometry matches exactly for all
fixtures. Engine order alternates by round. Builds and tests finished before
this final run; the earlier development run is retained but not used below.

Milliseconds, median of ten batch means (not frame p95):

| Scenario | Weva mutation + layout | Chrome mutation + layout |
|---|---:|---:|
| HUD panel | 0.027929 | 0.008700 |
| HUD leaf | 0.013902 | 0.008450 |
| Inventory panel | 0.409282 | 0.049000 |
| Inventory leaf | 0.142477 | 0.047350 |
| Settings panel | 0.082552 | 0.020650 |
| Settings leaf | 0.028009 | 0.011000 |

Chrome is faster on these matched layout operations. The largest gap is the
100-cell inventory panel. This does not measure total UI frame cost: fixtures
are geometry-only, without text, images, input dispatch or binding evaluation.
Chrome's JS-to-DOM call and Weva's C ABI call are included. Browser timer
quantization remains relevant at these small times; summaries retain batch
means rather than asserting individual-operation percentiles.

## Validation

- Windows Release, GCC and Clang builds of `weva_bench` pass without warnings.
- Native benchmark CLI checks pass on all three, including geometry-only
  execution and rejection of incompatible cold/dt options.
- Six comparison runner tests pass. Full Windows Python tooling discovery:
  66 tests, 57 pass and nine Linux-only skips.
- Real ten-round Chrome/native run passes, with zero initial geometry delta.
- No engine/host behavior changed; full host gates were not repeated.

Raw evidence: `.utmp/chrome-matched-20261030-final/`; compiler and test logs:
`.utmp/chrome-matched-*.log`. [Compact receipt](chrome-matched-20261030.json)
retains machine identity, binary/fixture hashes and per-round batch results.
Earlier mismatched results remain preserved and are explicitly superseded.
