# C++ conventions

Rules for changes to the shared engine. Read [AGENTS.md](../AGENTS.md) for
verification and host responsibilities, and [architecture](ARCHITECTURE.md)
for implementation details.

## Language and toolchain

- Public core code targets **C++17**, with no RTTI dependence or exceptions.
  The private Ada parser target uses C++20; this does not change the public ABI.
- Build through CMake. Keep core behavior independent of Godot and Unity types.
- Return `Status`, a boolean or an explicit result for recoverable errors.
  Do not throw across the C ABI. Allocation failure is not a recoverable UI error.
- Use separate [sanitizer builds](SANITIZERS.md); do not report their timings as
  runtime performance.

## Ownership and lifetime

- DOM nodes use intrusive `Ref` ownership. Host APIs expose opaque handles,
  not owning C++ node pointers. Removal and reload invalidate associated handles.
- Box trees use reusable storage and stable `BoxId` indices. Do not retain a
  pointer into a growable box vector across allocation or reconstruction.
- Paint, layout and cache buffers retain capacity where useful. A cache owns
  its storage and checks the input versions and lifetime on which it depends.
- A `string_view` borrows its source. Copy or intern strings retained beyond
  that source's lifetime; parsing a temporary buffer does not extend its life.
- Preserve the allocation guarantees pinned by affected tests. Zero allocation
  in a clean idle update is not a claim that document creation or every mutation
  allocates nothing.

## Numbers and layout

- Use `double` for layout. No `-ffast-math` or `-Ofast` in the core.
- New numeric parsing should be locale-independent; prefer `std::from_chars`
  or an existing checked helper. Test malformed and boundary values.
- Use the rounding required by the operation; do not substitute mode-dependent
  rounding for an existing `std::round` contract.
- Chrome is the only HTML/CSS behavior reference. Use the
  [current oracle workflow](../Tools/oracle/README.md), matching fonts and
  capture inputs. The deleted C# engine is not a reference.

## The C ABI

[`libweva/include/weva_c.h`](../libweva/include/weva_c.h) serves both hosts.

- C linkage and C-compatible data only; no STL or C++ classes across the boundary.
- Document ownership, buffer sizing and borrowed-pointer lifetime explicitly.
  Follow each entry point's null-document and stale-handle contract.
- Additive ABI changes bump the minor version, update architecture documentation,
  regenerate Unity bindings and rebuild both hosts.
- Keep shared behavior in the core; hosts translate engine-specific input,
  fonts, resources and draw calls.

## Naming

Use namespace `weva`, `snake_case` functions/variables and `PascalCase` types.
Follow neighboring code and keep host conventions out of the shared core.
