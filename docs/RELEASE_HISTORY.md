# Release verification history

Recorded September 7 results, preserved with their original scope. Use the
[current release guide](RELEASE.md) and [latest review](verification/review-three-days-20260915.md)
for current commands and configuration.

## Local verification, 2026-09-07

Evidence is retained in `.utmp/release59/` in the development workspace.

- Linux Release: all nine CTest targets pass, including 636,031 main-suite
  checks with mutations of all 47 sample pages.
- The first current Linux sanitizer run exposed mismatched allocation hooks
  in `weva_bench` and the paint-order reference test. Both used a replacement
  `delete` with libstdc++'s original nothrow `new`. Their nothrow/array allocation
  and deletion paths now use the same allocator. Sanitizer diagnostics remain
  enabled. The benchmark now counts these previously missed allocation paths;
  historical measurements are not silently rewritten.
- After that fix, all 11 Linux ASan/UBSan CTest targets pass, including
  636,031 main-suite checks and both sanitizer activation controls. The two
  changed allocation/benchmark targets also pass Windows MSVC ASan with
  allocation/deallocation mismatch detection enabled.
- Nineteen release-tool regressions pass on Linux, including process-crash,
  empty-run, stale-source, dependency and binary-mismatch controls. Three shell
  integration cases run on Linux rather than Windows.
- Windows and Linux previews `0.1.0-preview.59`: fresh project, PCK and relocated native
  debug/release/embedded exports pass. All 17 example assertions pass in each
  configuration; exported example pixels exactly match the project. These runs
  have no certificate-store error and the error checks were not weakened.
  Each rebuilt binary also passes all 25 current host suites / 8,319 checks.
- The combined 20-file desktop ZIP passes Windows and Linux export checks.
  The first additional Linux run aborted during editor import (exit `-6`);
  five controlled fresh imports and the subsequent full check passed. This
  remains an unresolved intermittent failure, not a claimed fix. The earlier
  [Godot headless-import issue](https://github.com/godotengine/godot/issues/111645)
  is relevant background, but the new abort has no backtrace establishing the
  same cause. No automatic retry or longer wait was added to the acceptance gate.
- Stock Windows/Linux Godot 4.7.2 passes the 32-run text control but fails
  the other five cases. Both mixed-script cases exit with heap-corruption code
  `3221226356` on Windows and abort signal `-6` on Linux. Engine/probe hashes
  and logs are retained. This is a
  failed release gate, not a supported Unicode-input configuration.

The existing requirements for broader IME behavior, font-family/bidi and form
semantics, touch/gamepad/accessibility, remaining browser findings and game
latency/lifecycle verification remain open. This document adds reproducible
release checks; it does not narrow those requirements or authorize publication.
