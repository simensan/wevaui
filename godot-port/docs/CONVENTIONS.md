# C++ conventions

Settle these before the first file. Each one exists because changing it later
means touching thousands of call sites.

## Language and toolchain

* **C++17.** godot-cpp targets C++17; matching it avoids ABI and build friction.
  (The Unity package's `TestVerifyAll` pins `LangVersion 9.0` to match Unity's
  compiler — that constraint does not follow us here.)
* **No RTTI dependence** in the core. Hosts may use it; `libweva` must not need it.
* **CMake**, one top-level project, core and hosts as separate targets.

## Exceptions: none

The C# core has **509 `throw new` sites**. Throwing across a GDExtension
boundary is undefined, and Godot's own code is exception-averse.

* `libweva` compiles with `-fno-exceptions`.
* Errors return. The C# core already leans this way — 813 `TryParse`-family
  call sites — so most translations are mechanical:

```cpp
// C#:  bool TryParse(string s, out CssLength v)
bool try_parse_length(std::string_view s, CssLength* out);

// Where a reason is needed:
enum class Status { Ok, Syntax, Unsupported, OutOfRange, NotFound };
Status parse_declaration(std::string_view s, Declaration* out);
```

* Allocation failure is **not** a recoverable error; arenas abort. Do not thread
  `bad_alloc` handling through layout.
* At the host boundary, map `Status` onto `ERR_FAIL_*` / a Godot `Error`.

## Ownership: arenas and stable indices

The C# core leans on the GC for the DOM tree, box tree, paint lists and the
caches between them. This is the single largest source of translation risk, and
also the reason to do the port at all (see the allocation numbers in the README).

* **Per-pass arena.** Layout and paint bump-allocate into an arena that is reset
  at the end of the pass. This is the direct replacement for `BoxPool`,
  `PaintListPool`, `ArrayPool` rentals and `CssValuePool` — all of which exist
  only to approximate arenas inside a GC.
* **Boxes and paint commands are referenced by stable index, never by pointer.**
  The C# design already points here (`ElementToBoxIndex`); arenas make pointers
  unsafe across a reset, so indices are load-bearing, not stylistic.
* **DOM nodes are the exception.** GDScript can hold a reference to an `Element`,
  so DOM nodes are intrusively refcounted and outlive any pass.
* **Caches own their storage** and are keyed on input versions (see
  ARCHITECTURE.md). A cache must never hold an arena pointer.

**Steady-state target: zero heap allocations per frame.** This is Weva's stated
goal that C# could not reach (1.42 MB/call layout, 1.10–2.19 MB/call paint).
Here it is achievable, so it is a regression gate, not an aspiration.

## Strings: `string_view` with one owner

1,189 `string.*` calls and 268 `Substring` calls, concentrated in the parsers.
In C++ these become `std::string_view` — faster than the C# original, and a
lifetime hazard.

* **The source buffer is owned by the `Document`** and lives for the document's
  lifetime. Parser slices point into it.
* **A `string_view` never outlives the parse that produced it.** Anything
  retained past the pass — a computed value, an interned identifier — owns its
  storage or is interned.
* **Intern identifiers, property names and keywords.** Compare by id, not by
  bytes. The C# side already has `CssProperties.GetId`.
* **Never `strtod`.** Use `std::from_chars` — locale-independent by definition.
  Locale-dependent parsing is a correctness bug that only appears on a user's
  machine in another country.

## Floating point: exactness is a feature

Layout computes in `double` and the goldens depend on exact rounding.

* `double` throughout layout. Do not "optimise" to `float`.
* **No `-ffast-math`, no `-Ofast`** on the core. Set this in CMake and add a
  comment saying why, because someone will try to add it.
* Round with `std::round` (matches C#'s `MidpointRounding.AwayFromZero`), not
  `std::nearbyint` (banker's rounding, and mode-dependent).
* Bit-identical layout dumps against the C# oracle are the acceptance test. If
  they diverge, that is a bug, not tolerance.

## The C ABI

`include/weva_c.h` is the seam a future Unity host binds through. It is the
narrowest part of the system and the hardest to change once anything depends
on it.

* **C linkage, POD only.** No STL types, no C++ classes, no exceptions, no
  inline functions with C++ semantics.
* **Opaque handles.** `typedef struct weva_document* weva_document_t;`
* **Caller-allocates or explicit free.** Every allocating call has a paired
  release. Never hand back a pointer with implicit lifetime.
* **Versioned.** `weva_abi_version()` first, checked by every host at load.
* Additive changes only after the first host ships against it.

## Naming

* Core namespace `weva::`, `snake_case` for functions and variables,
  `PascalCase` for types. The core is host-agnostic — do not adopt Godot's
  conventions inside `libweva/`, only in `hosts/godot/`.
* Mirror the C# type names where they translate directly. `CascadeEngine`,
  `LayoutEngine`, `BoxToPaintConverter` should be greppable across both
  implementations for as long as the oracle is in use.
