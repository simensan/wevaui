# Sanitizer verification

Configure a separate build with `-DWEVA_SANITIZERS=ON`. The switch instruments
the core, its Tools/tests and embedded ICU. It defaults to OFF and does not
change the installed addon or the normal shipping build.

GCC and Clang use AddressSanitizer plus UndefinedBehaviorSanitizer, with
recovery disabled so a diagnostic fails the process. MSVC uses
AddressSanitizer; a Windows MSVC pass is not UBSan or Linux evidence.

## Windows MSVC

Run from an x64 Developer PowerShell so the compiler's matching ASan runtime
DLL is on `PATH`. Use Release or RelWithDebInfo: MSVC's usual Debug `/RTC`
checks conflict with ASan. The diagnostic build retains symbols and disables
incremental linking. These requirements follow the
[MSVC sanitizer build reference](https://learn.microsoft.com/en-us/cpp/sanitizers/asan-building?view=msvc-170)
and [compatibility notes](https://learn.microsoft.com/en-us/cpp/sanitizers/asan?view=msvc-170).

From the repository root:

```powershell
cmake -S . -B build-sanitize-msvc -G "Visual Studio 17 2022" -A x64 -DWEVA_SANITIZERS=ON -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded
cmake --build build-sanitize-msvc --config Release --parallel 8
$env:WEVA_INCREMENTAL_CORPUS = (Resolve-Path Tools/oracle/corpus/samples).Path
$env:ASAN_OPTIONS = "alloc_dealloc_mismatch=1:halt_on_error=1"
ctest --test-dir build-sanitize-msvc -C Release --output-on-failure --no-tests=error
```

The first build needs the pinned ICU source archive. Offline builds can add
`-DFETCHCONTENT_SOURCE_DIR_WEVA_ICU_SOURCE=/path/to/icu`; see
[the ICU source/data pin](../third_party/icu/README.md).

## GCC / Clang

```sh
cmake -S . -B build-sanitize -G Ninja \
  -DCMAKE_BUILD_TYPE=RelWithDebInfo -DCMAKE_CXX_COMPILER=clang++ \
  -DWEVA_SANITIZERS=ON
cmake --build build-sanitize --parallel 8
WEVA_INCREMENTAL_CORPUS="$PWD/Tools/oracle/corpus/samples" \
  ctest --test-dir build-sanitize --output-on-failure --no-tests=error
```

`check.sh` reconfigures its existing `WEVA_BUILD_CLANG` directory with the
sanitizer switch and runs the full CTest suite with the sample mutations
enabled. It uses the real process results, including allocation guards and
CLI checks. A missing configured directory is skipped in development mode
and fails `check.sh --release`.

## What proves the checks ran

`weva_asan_active` launches a separate executable containing a deliberate
heap overflow. It passes only when that process fails with the expected ASan
diagnostic. GCC/Clang also run `weva_ubsan_active`, which requires the expected
signed-overflow diagnostic and a failing exit. Raw positive-control output
is retained under the build's `sanitizer-probes/` directory. These intentional
failures never execute inside the core tests or a distributed addon.

The deliberate ASan probe disables symbol lookup for that child process only.
An observed WSL run detected the overflow immediately but stalled while printing
its stack, causing the 30-second probe timeout. Raw addresses are sufficient for
this gate: it still requires the exact overflow diagnostic and a nonzero exit.
The uninstrumented negative control remains rejected. Ordinary test reports
retain their normal symbolization settings.

The remaining tests must pass normally. `WEVA_INCREMENTAL_CORPUS` is necessary
to include mutations of the shipped samples compared with full recomputation;
without it, the main test executable runs only its ordinary fixtures. Keep
`Testing/Temporary/LastTest.log` and identify the source/toolchain for each
verification. A green run without active instrumentation, complete fixtures
or the intended binary is insufficient.

Sanitizers affect timing and allocation behavior. These runs verify memory
safety under the exercised workloads; they are not performance measurements
or a proof that no unexercised defect exists. Native Godot/font integration,
exported games and each supported platform still need their own checks.

## Current evidence — 2026-09-07

The current-source Linux rerun in `.utmp/release59/linux/` now passes all
11 CTest targets with ASan/UBSan, including 636,031 core checks and mutations
of all 47 samples. Its first run found mismatched allocator replacements in
`weva_bench` and the paint-order reference test: libstdc++ called nothrow `new`
while their replacement `delete` called `free`. Matching nothrow/array hooks
fix the mismatch and count those allocations. Both changed targets also pass
Windows MSVC ASan with mismatch detection enabled. No sanitizer suppression
was added. This updates current core evidence; native-adapter instrumentation
and real engine safety remain separate checks. See [RELEASE.md](RELEASE.md).

Windows x64 MSVC 14.44.35207, Release `/MT`, passes all 10 CTest targets with
AddressSanitizer enabled in the core and ICU. The main suite passes 636,031
checks and logs mutations of all 47 sample pages. All allocation guards,
blur-variant comparisons and benchmark CLI cases pass. Geometry digests match
the preceding Release build. The entire run takes 231.44 seconds; this is
diagnostic test duration, not product latency.

The activation gate accepts the instrumented overflow probe and rejects a
matched uninstrumented executable that returns zero. A fresh default configure
reports `WEVA_SANITIZERS=OFF` and no sanitizer flags in its generated projects.
Source/runtime/binary hashes, test output and control evidence are retained in
`.utmp/release-sanitizers58/`. The installed preview56 DLL remains unchanged.
At that checkpoint, current Linux/UBSan and native-adapter instrumentation
checks were pending; the later core rerun is recorded above.
