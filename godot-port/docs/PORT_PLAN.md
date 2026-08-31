# Port plan

Bottom-up, oracle-gated. Each phase ends with a diff-clean corpus subset; no
phase starts before the previous one is green.

Sizes are the C# source being translated, from the feasibility measurements.
They are *scope indicators*, not schedule estimates — see "Effort" at the end.

---

## Phase 0 — Foundations (no engine code)

* Fix and verify the stale csproj excludes; confirm `BaselineGen` builds and
  runs headlessly (`dotnet build`, then a dump of `randhtml.html`). **The oracle
  must work before anything depends on it.**
* CMake skeleton: `libweva` (static), `weva_dump` (CLI), `hosts/godot` stub
  loading in Godot and printing its ABI version.
* Lock CONVENTIONS.md: `-fno-exceptions`, no fast-math, arena allocator,
  interning table, `Status` enum.
* Build `tools/oracle/` — corpus harvester, diff runner, CI wiring.

**Exit:** an empty C++ engine produces an empty dump, the diff runner reports
"412 elements expected, 0 produced" against a real corpus entry, and CI runs it.

**Status: done, with one item unverified.** CMake skeleton builds clean under
gcc 13 and clang 18; `Arena` / `SymbolTable` / `Status` land with 17 checks
green, also under ASan+UBSan; `weva_dump` emits BaselineGen's format exactly;
`diff.py` self-tests against identical, off-by-0.01, and wrong-identity inputs;
`harvest.py` pulls **3,879 corpus entries** partitioned by feature (block 1563,
cascade 967, flex 450, grid 269, scrolling 169, positioning 159, inline 121,
text 113, multicol 46, tables 22).

Two things worth carrying forward:

* The csproj exclude fix is **applied but unverified** — this container has no
  .NET SDK, so `BaselineGen` has not actually been built. Phase 1 must not
  start until someone runs `dotnet build` on it. There is no oracle until then,
  and `run.sh` exits 2 rather than reporting a vacuous pass over zero entries.
* Two bugs were caught during the phase and are worth remembering as a class.
  `SymbolTable` originally stored `std::vector<std::string>`, whose reallocation
  moves SSO character data and dangles every `string_view` key in the index —
  precisely the lifetime hazard CONVENTIONS.md warns about, hit on the first
  file that could hit it. And `weva_dump` initially formatted with `%.2f`,
  which rounds half-to-even in glibc where C#'s `Round2` rounds away from zero;
  0.125, 2.675 and 16.005 all diverge. Both are now covered by tests.

## Phase 1 — Core infrastructure (~5k LOC)

Arena allocator, string interning, `Status`, the value types Weva's paint layer
already owns host-agnostically (`Rect`, `Transform2D`, `LinearColor`,
`BorderRadii`), and the DOM (`Runtime/Dom`, 420 LOC — small and self-contained).

**Exit:** DOM construction from a hardcoded tree; unit tests on arenas and
interning; zero leaks under ASan.

**Status: done.** 88 checks green under gcc 13, clang 18 (Release, `-Wall
-Wextra -Wpedantic`, no warnings) and ASan+UBSan with LeakSanitizer verified
active. Landed: `Rect` / `Transform2D` / `CornerRadius` / `BorderRadii`
(`Paint/*.cs`), `LinearColor` + the sRGB curve, `RefCounted`/`Ref`, and the DOM
(`Node`, `Element`, `TextNode`, `Document`, `AttributeMap`).

Fidelity notes worth keeping:

* **Field widths were copied, not normalised.** `Rect` is double; `Transform2D`
  holds floats with a double `apply()`. Tidying either to a uniform type would
  surface later as unexplained sub-pixel divergence in the oracle.
* **`srgb_byte_to_linear` matches C#'s exponent exactly.** C# writes
  `Math.Pow(x, 2.4f)`; `Math.Pow` has no float overload, so the literal widens
  to `(double)2.4f` = 2.400000095367431640625, not 2.4. A bare `2.4` here gives
  a scatter of one-ULP color differences that read as a cascade bug. Same class
  of trap as Phase 0's `%.2f` rounding.
* **No RTTI.** `dynamic_cast` in the tree walks was replaced with an explicit
  `NodeType` tag, per CONVENTIONS.md — cheaper on a hot traversal and keeps the
  core RTTI-free.
* **Ownership.** Parent holds `Ref` to children, child holds a raw parent
  pointer, so a well-formed tree has no cycles. `append_child` and
  `remove_child` retain across the unlink, because erasing drops the parent's
  only reference and observers still need a live target.

C# semantics preserved deliberately, each with a test: re-appending the current
last child is a no-op *including the version counter*; `remove_child` fires its
mutation **before** unlinking so the parent chain is intact for bubbling;
detached subtrees report a null owner document; mutations bubble to every
ancestor with `target` always the originally mutated node; no-op attribute
writes bump nothing.

## Phase 2 — HTML + CSS parsing (~7k LOC)

`Runtime/Parsing` (1,100) + `Runtime/Css/Parsing` (~2k) + `Runtime/Css/Values`
(`CssValueParser` alone is 1,866). This is where the `string_view` lifetime rule
gets its first real test.

**Exit:** property round-trip dumps diff clean. Every value type the C# parser
accepts parses identically, including the `from_chars` locale behaviour.

**Status: HTML tokenizer done; HTML tree builder and all CSS parsing remain.**
152 checks green under gcc 13, clang 18 and ASan+UBSan+LSan. Tokenizes the
17KB dev demo (`Assets/UI/randhtml.html`) into 1,177 tokens / 27 interned names.

**The encoding change is the substance of this phase.** C# tokenizes UTF-16
`char`s; C++ tokenizes UTF-8 bytes. Three consequences, each deliberate:

* **Whitespace classification is Unicode, not ASCII.** `char.IsWhiteSpace`
  decides where an unquoted attribute value ends, so `is_unicode_whitespace`
  reproduces its BMP set exactly. Narrowing it to ASCII would silently swallow
  a U+00A0 separator into the value — there is a test with a real U+00A0 in it.
  Note U+200B is *not* whitespace to `char.IsWhiteSpace`, and isn't here either.
* **Line/column count code points, C# counts UTF-16 units.** An astral
  character reports one column less. Columns appear only in diagnostics, never
  in a layout dump, so the oracle is unaffected.
* **Surrogate numeric entities are rejected as literal text** (`&#xD800;`).
  C# calls `char.ConvertFromUtf32`, which *throws* — an unhandled
  `ArgumentOutOfRangeException` escaping the tokenizer. This is a deliberate
  deviation, matching browsers and almost certainly what the C# meant.

Tag and attribute names are interned (`Symbol`) rather than held as strings.
Selector matching and the void/optional-close tables compare them constantly.

Also worth noting: the C# comment terminator bound is `pos + 2 < length`, not
`<=`, so a comment ending at the final byte of the buffer reads as
unterminated. Reproduced rather than fixed — the oracle compares against C#
behaviour, and a unilateral fix here is a divergence.

**Tree builder done** (`HtmlParser.cs`, 645 LOC): fragment normalization, the
optional-tag implicit closes, optional-close scope guards, and the AAA-lite
active-formatting-list reconstruction. 193 checks green across gcc / clang /
ASan+UBSan+LSan; the 17KB dev demo parses clean in **strict** mode into 358
elements and 458 text nodes.

### Two places where the C# comments claimed behaviour the code did not deliver

Both were found by porting. Decisions taken:

1. **A body-only fragment got no `<head>` — FIXED in both engines.**
   `EnsureHead()` was only reachable from a head-content element, and
   `EnsureBody() -> CloseHead()` returned immediately when `inHead` was false,
   so `<main>hi</main>` produced `html(body(main))`. Layout was unaffected
   (both stated reasons for wrapper synthesis — `:root` matching and
   html/body background propagation — work either way), but **structural
   selectors diverged from Chrome**: `<body>` was `:nth-child(1)` instead of
   `:nth-child(2)`, breaking `body:nth-child(2)`, `html > *:first-child` and
   top-level sibling combinators.

   `HtmlParser.cs`'s `EnsureBody()` now calls `EnsureHead()` first, and the C++
   mirrors it. `HtmlFragmentWrapperTests` already tolerated both shapes
   ("head is empty so we allow either [body] or [head, body]"), so no C# test
   needed changing — **though the C# side is unverified here: no .NET SDK.**
   The C++ regression test asserts child *positions*, not just the shape, since
   a shape assertion alone would not catch `<head>` being emitted after
   `<body>`.

2. **The adoption-agency fixup does not match Chrome — DEFERRED, deliberately.**
   The comment cites `<p>Click <a><div>here</div></a> to start</p>` as
   "matching the Chrome / Firefox DOM shape". Chrome produces
   `<p>Click <a></a></p> <a><div>here</div></a> <a> to start</a> <p></p>`.
   The C# instead nests the reconstructed `<a>` *inside* the `<div>` and leaves
   `" to start"` unwrapped, because `</a>` clears the active formatting list
   before the trailing text arrives. That is layout-visible — the trailing text
   loses its link styling.

   Fixing it means implementing real HTML5 §13.2.6 adoption agency, which is a
   project rather than a patch, and doing it mid-port would blind the
   differential signal across every formatting-element corpus entry. Reproduced
   exactly for now; revisit once the corpus is green, then fix both engines
   together with the oracle watching.

**Process note.** Eight of the first parser tests failed, and every one was a
wrong expectation on my side — written from memory of Chrome rather than from
the C# source. The port was faithful. That is the failure mode this phase
should expect: for a differential port, "what does the reference actually do"
is a question to answer by reading or running it, never by recall.

## Phase 3 — Cascade and selectors (~12k LOC)

`Runtime/Css/Cascade` (12,340) plus selectors, media and container queries. Port
the version-keyed invalidation contract *as designed* — do not simplify it and
plan to add it back.

**Exit:** computed-style dumps diff clean across the corpus. Incremental
invalidation benchmarked: a `:hover` flip must not re-cascade the document.
(C# reference: 0.08 ms vs 8.3 ms full.)

## Phase 4 — Block and inline layout + software paint (~15k LOC)

Layout root files (11,775) minus the specialised modes, `Layout/Boxes` (590),
plus `Runtime/Paint` command types and `BoxToPaintConverter` (4,302) at the
altitude decided in ARCHITECTURE.md — the core now tessellates, so this is
larger than the C# original by design.

Port `SoftwareRasterizer` here as the first backend.

**Exit:** `corpus/block/` and `corpus/inline/` diff clean; the 38 golden PNGs
match. The software backend should land in the low hundreds of lines, not 1,592
— if it doesn't, the render interface was not lowered enough.

## Phase 5 — Text (~9k LOC, highest uncertainty)

Decide `FontInterface` implementation (FreeType+HarfBuzz vs Godot `TextServer`)
**at the start of this phase, not before** — Phase 4 will have clarified how
much atlas control the paint layer needs.

`Runtime/Layout/Text` (1,501) + the shaping/atlas layer. Note the C# original is
not the reference here: its rasterizer reaches into Unity internals by
reflection and is being replaced, not translated.

**Exit:** `corpus/text/` green on line-break positions, line counts and line-box
heights (tolerance per ORACLE.md); surrounding box geometry stays zero-tolerance.

## Phase 6 — Flex (~4k LOC)

`Layout/Flex` (3,975). Port the documented deviations deliberately — including
the two RmlUi independently arrived at (bare text does not form anonymous flex
items; stretched items are not re-flowed internally) — rather than silently
fixing them, so the oracle stays meaningful. Fix them later, in both engines, as
a conformance change.

**Exit:** `corpus/flex/` diff clean.

## Phase 7 — Grid and subgrid (~5k LOC)

`Layout/Grid` (4,848). The hardest algorithm in the port and the single largest
differentiator — RmlUi has no grid, GTML has no grid, and it is why this project
exists rather than adopting one of them.

**Exit:** `corpus/grid/` diff clean, including `repeat()`, `minmax()`,
`fit-content()`, `auto-fill`/`auto-fit`, named lines and subgrid.

## Phase 8 — Remaining layout (~8k LOC)

`Positioning` (2,603), `Scrolling` (4,071), `Tables` (1,431),
`AnchorPositioning` (1,018), `Multicol` (472), `Floats` (216), `Containment`.

**Exit:** the full corpus diffs clean. **At this point the engine is at parity**
and everything after is host and performance work.

## Phase 9 — Godot host

GDExtension: `WevaDocument` as a `Control`, the ~20 bound types from §4 of the
feasibility doc (`Document`, `Element`, `Node`, `TextNode`, signals), input via
`Input`/`InputEvent`, clipboard and IME via `DisplayServer`, images via
`Image`/`ImageTexture`, resources via `ResourceLoader`.

Read [Godot-RmlUi](https://github.com/ashifolfi/Godot-RmlUi) first — MIT, 19
commits, and it implements exactly this plumbing.

**Exit:** the demo runs in Godot from GDScript with no C# anywhere.

## Phase 10 — GPU backend and shaders

Port the batched renderer as *one* backend: `MultiMeshInstance2D` + an RGBAF
data texture read via `texelFetch` (Godot's shading language has no
`StructuredBuffer`), or `RenderingDevice`/GLSL if profiling demands it. HLSL →
Godot shading language for the 7 shaders + `UIShaderLib.hlsl` (3,904 lines).

Clipping ports cleanly: the batched backend already abandoned stencil for
per-instance `clipRect` (slot 13) and clip-path SDF shapes (slots 16–20), and
Godot 2D exposes no canvas stencil — the codebase sidestepped its own blocker.

**Exit:** perf parity or better against the C# figures, with the allocation
target that C# could not reach: **zero heap allocations per frame in steady
state** (C# baseline: 1.42 MB/call layout, 1.10–2.19 MB/call paint).

## Phase 11 — C ABI and editor plugin

Freeze `weva_c.h`, version it, and only then consider the Unity P/Invoke shim.
Editor plugin (preview panel, DOM/style inspector, importers) — ~5,910 LOC of
C# editor tooling to re-express as a Godot `EditorPlugin`.

---

## Effort

The core (Phases 1–8) is ~65k LOC of translation. Sustained mechanical
translation of intricate, spec-driven code runs perhaps 500–1,500 LOC/day
including debugging, which puts the core alone at ~100–200 person-days — and on
a layout engine the differential-debugging tail is historically where schedules
go. **12–24 person-months to parity**, with the host, GPU backend and editor
plugin on top.

Treat any estimate below that with suspicion, including one that arrives after
Phase 4 goes surprisingly well. Phases 5 and 7 are where the variance lives.

## Kill criteria

Worth agreeing in advance, because sunk cost is the real risk on a port this
size:

* **Phase 4 exit slips badly.** Block and inline are the best-understood part of
  the engine. If they are hard, grid will be worse.
* **The oracle proves unmaintainable** — if corpus divergence is routinely
  "expected", the safety net is gone and the remaining phases are unguarded.
* **The differentiator stops mattering.** If RmlUi ships grid or container
  queries, re-run the §8 decision honestly rather than finishing out of momentum.
