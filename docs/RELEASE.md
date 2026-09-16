# Godot release verification

The addon remains a development preview. The
[September 15 full gate](verification/review-three-days-20260915.md) passes with
the qualified patched Linux Godot editor, matching debug/release templates,
Windows Chrome reference and a private display. That configuration is recorded
with exact hashes; it does not qualify every platform or a published release.

Use the [text-safety configuration](GODOT_TEXT_SHAPING.md#stock-godot-472-limitation)
and matching templates when testing native Godot text. Weva's document-text
workaround does not fix the engine's native controls. Broader adoption limits
are in [product readiness](PRODUCT_READINESS.md).

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

A normal CMake build refreshes a `.build.json` sidecar, including when the
extension does not relink. Package only
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
WEVA_EXPORT_RENDER=1 WEVA_PACKAGE_VERSION=0.1.0-preview.0 \
  bash check.sh --release
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
python Tools/godot-text-shaping-repro/check_exports.py \
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
qualification is recorded in [the checkpoint184 receipt](verification/template184.json),
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

Historical [September 7 release checks](RELEASE_HISTORY.md) remain available.
