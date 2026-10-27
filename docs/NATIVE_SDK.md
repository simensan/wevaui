# Native SDK

The Weva repository builds static libraries for hosts that use the public C ABI.
Consumers link the core and its two ICU archives; they do not configure CMake or
build Weva. ICU data is embedded. The package includes the matching `weva_c.h`
and all dependency licenses. Windows uses the dynamic MSVC CRT; Linux uses
libstdc++ (Ubuntu 22.04 baseline), and macOS uses libc++ (macOS 11 minimum).
The normal host C/C++ linker/toolchain is still required.

Run **Native SDK** manually on a committed revision. It builds and runs the core
suite and a C ABI consumer smoke test on Windows x64, Linux x64, and macOS ARM64
and x64. Enable `publish` to create `native-<full commit>` only after every job
passes. Releases are immutable; the workflow refuses to replace an existing one.
The build-only run also retains the same assets as Actions artifacts.

For a local Windows package:

```text
python Tools/package_native.py --target x86_64-pc-windows-msvc --build-dir build-native-sdk --output build-native-sdk/package
```

`--cmake-arg=-DWEVA_BUILD_TESTS=ON` includes the full core suite.
`--allow-dirty` is only for local experiments and marks the manifest accordingly.
Release consumers must reject dirty packages. No changes to the core ABI or the
Unity/Godot hosts are needed to produce the SDK.

`package/sdk/<target>` is ready for local linking. `package/release` contains the
raw release assets and `<target>.json`. Each manifest records its source commit,
ABI, compiler, target, runtime and every file's SHA-256 and size. Consumers pin
the manifest SHA-256, verify all files before use, and link in this order:
`weva`, `weva_icu_i18n`, `weva_icu_common`. Header and library versions must match.
Windows also links `advapi32`. Unavailable targets require an explicit source
build; they must not silently download or build another version.
