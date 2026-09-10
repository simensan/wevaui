# Godot release verification

The addon is a development preview. Stock Godot 4.7.2 still corrupts long emoji
runs and crashes on mixed-script inputs. A local patched Windows editor/template
bundle now passes the crash reproductions in actual exports with ICU embedded.
Use the matching editor and both templates; a passing addon build alone cannot
establish engine text safety. See
[text shaping](GODOT_TEXT_SHAPING.md) and the full
[product requirements](PRODUCT_READINESS.md).

As checked on 2026-09-07, the [official release archive](https://godotengine.org/download/archive/)
still lists 4.7.2 as the latest stable 4.x release. Both the
[4.7 source](https://github.com/godotengine/godot/blob/4.7/modules/text_server_adv/script_iterator.cpp)
and [development source](https://github.com/godotengine/godot/blob/master/modules/text_server_adv/script_iterator.cpp)
retain the missing stack copies and emoji buffer free inside the script loop.
No official fixed configuration was established in this verification.

## Build and package

Use the pinned godot-cpp revision and commands in the
[host build guide](../hosts/godot/README.md#building). ICU sources are pinned
by URL and SHA-256 in `third_party/icu/CMakeLists.txt`. The root GitHub workflow
`.github/workflows/godot-ci.yml` builds the core and extension on Ubuntu 24.04
and Windows Server 2022. It uses Python 3.12, Clang 18 for the Linux core and
the VS 2022 x64 toolchain on Windows. The generated build metadata records the
actual compiler and CMake versions; the runner image does not pin every compiler
patch or promise bit-identical binaries across toolchain updates.

The workflow also runs complete sample mutations, allocation guards, benchmark
CLI checks, sanitizer activation controls and release-tool regressions. It lives
at the repository root: the previous nested workflow was not discovered by
GitHub. Workflow build artifacts are previews, with no publication or release
approval step. Running this workflow remotely still requires committing/pushing
the changes through the normal repository workflow.

CMake emits a `.build.json` sidecar after linking the extension. Package only
libraries whose sidecars match the actual binary and current source/dependency
inputs. `package_addon.py` requires an explicit preview version, verifies those
hashes and the native shared-library format, and embeds the metadata in the ZIP.
Do not copy a current sidecar beside an old binary. Source changes require a
rebuild. Dirty working trees are identified as such and retain a content hash;
their Git commit alone does not identify the built source.

## Acceptance

Run from the repository root, configuring the build paths and matching engine
and export templates for the selected platform:

```sh
WEVA_EXPORT_RENDER=1 WEVA_PACKAGE_VERSION=0.1.0-preview.59 \
  bash godot-port/check.sh --release
```

`check.sh` currently orchestrates Linux builds. A Windows package needs its own
core/sanitizer checks, exported Unicode gate below, and
`check_export.py --native --render --keep` run. Use
the actual packaged DLL; a Linux pass does not verify it. The
[desktop export guide](DESKTOP_EXPORTS.md) describes the fixtures and limits.

For custom engines, set `WEVA_GODOT_DEBUG_TEMPLATE` and
`WEVA_GODOT_RELEASE_TEMPLATE` together. `check.sh` passes the same paths to the
exported Unicode and addon export gates. With neither variable set, both use
the editor's installed templates, which must still pass the actual tests.

On Windows or Linux, run the Unicode export gate directly with:

```sh
python godot-port/tools/godot-text-shaping-repro/check_exports.py \
  --godot /path/to/editor --debug-template /path/to/debug-template \
  --release-template /path/to/release-template --logs /new/artifact/directory
```

It exports both modes with ICU embedded, hides the source project, relocates
the games, verifies their build modes and runs all six cases separately. No
external ICU file or template path override is used at runtime. A patched
editor with stock templates fails this gate. The same explicit template flags
are supported by `check_export.py` and `check_frontier_camp.py`.

Performance re-exports also need the matching template. Pass
`run_frontier_perf.py --godot /path/to/editor --release-template /path/to/release-template`
with the chosen output and timing profile. The runner records the template hash
and restores project presets after export. An editor hash alone does not identify
the engine in an exported game. The explicit-template desktop and 1080p 3D
qualification is recorded in [the current receipt](verification/template184.json),
alongside the retained default-template Unicode lifecycle failure.

Release mode rejects skipped checks, any layout-oracle findings and missing
native export pixel checks. Core verification uses CTest's full target list,
including process exit status. Oracle verification requires exactly the expected
fixture count, no runner errors, and a consistent process exit. Backend and
interactive comparisons reject crashes even when metrics were printed first.
Build failure stops dependent checks so an old installed binary cannot supply
evidence. `--clean` uses CMake's clean target and preserves configured dependencies;
it does not recursively delete environment-supplied directories.

Export checks validate the packaged library against `build.json`, reject engine
errors and retain per-command logs with `--keep`. Preserve the ZIP, build
sidecars, engine/template identities, CTest logs, text-safety report and rendered
comparisons for every claimed platform. Current gates cover substantial behavior,
but the product requirements also include real-device input and game integration
checks that this script does not implement.

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
