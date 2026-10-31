# Windows IL2CPP qualification — 2026-10-31

The first actual IL2CPP run exposed a production interop failure: all seven
rendering tests failed at document creation with `Cannot marshal type
'System.IntPtr'`. IL2CPP generated unconditional marshal exceptions for the
two P/Invoke declarations that accepted C# `delegate*` parameters: the font
shaper and asset reader setters.

The binding generator now emits a private raw-pointer P/Invoke for callback
parameters and a typed managed forwarding method. The C ABI, callback lifetime,
calling convention and typed callers are unchanged. This fixes the owning
interop layer; neither Godot nor the shared native engine needs a workaround.
Generated bindings are regenerated and the import checker now includes private
imports and rejects function-pointer parameters at the P/Invoke boundary.

## Verification

- Windows x64 IL2CPP development player: **7/7** rendering/asset/binding tests.
- Windows x64 IL2CPP non-development player: **7/7**, high managed stripping,
  release C++ compilation. Runtime assertions check IL2CPP and that
  `Debug.isDebugBuild` is false. The build-settings receipt records the actual
  backend, stripping, compiler and build flags; `GameAssembly.dll` is present.
- All sixteen captures from each IL2CPP variant are RGB-identical to the prior
  Mono baseline. The packaged image/font/binding render was inspected.
- Full Unity EditMode: **255 passed, zero failed, two existing inconclusive**.
- Rebuilt Mono development player: **7/7**. Its leftover player was detected
  and stopped by the updated runner, exercising cleanup on a real run.
- Generated-binding drift check passes. All **144 imports** pass the calling
  convention/function-pointer checks for desktop, iOS-player and iOS-editor
  preprocessor configurations. Those compile checks do not qualify iOS runtime.
- Two runner ownership tests pass. The focused release-gate Python suite passes
  16 tests with three platform skips.

This is Unity 6000.4.1f1 on Windows with D3D12/RTX 5080. Test assemblies remain
included; their roots can affect stripping. Consumer games, Addressables,
IL2CPP on other platforms, and actual production stripping graphs still need
their own qualification. These runs collect correctness evidence, not latency
or first-use GPU timings, and overlap unrelated builds/lifecycle checks.

## Reproducible runner and process ownership

`Tools/run_unity_player_tests.py --unity <Unity.exe> --out <new-directory>
--backend il2cpp --release` builds the non-development/high-stripping variant.
Omit `--release` for the development player; `--backend mono` checks Mono.

The non-development player cannot rely on the editor's development connection
to deliver results. The runner uses Unity Test Framework's documented split
build/run workflow: the build modifier suppresses autorun/editor connection,
post-build cleanup exits the editor, and a runtime test callback writes NUnit
XML and exits the launched player. The runner requires successful build and
player exit, at least seven tests and every test passing. An initial connected
release attempt is retained as failed/incomplete evidence, not counted as a pass.

The runner now terminates any remaining process whose executable is inside
that run's newly created player output directory. It leaves neighboring and
unrelated processes alone, handles termination timeouts, and restores the exact
original ProjectSettings file even if cleanup fails. This prevents completed
test runs from continuing to render and changing later measurements.

During this work five older audit-owned `PlayerWithTests` processes were found
still running, from `unity-player-timing-v3/v4/v5`, `unity-player-world-v2`,
and `flex-proof-gate/unity-player`. Their executable paths and creation times
identified them; only those owned processes were stopped. Earlier performance
receipts taken while they were alive contain uncontrolled background rendering
load. Their timing percentages require a clean rerun; this discovery does not
invalidate successful functional assertions or matching captured pixels.

Raw evidence: `.utmp/render-audit-20261030/il2cpp-player/` (original failure),
`il2cpp-player-fixed/`, `il2cpp-player-release/` (connection limitation),
`il2cpp-player-release-local/`, and `il2cpp-editor/`. Preserve all failures.
The Mono regression is under `interop-mono-player/`. The companion JSON records
test summaries, actual release settings and evidence hashes.
