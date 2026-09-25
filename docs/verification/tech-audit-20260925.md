# Technical audit and performance pass — September 25, 2026

Scope: the whole repository. The core (`libweva/`) was audited, fixed and
measured here. The Unity package, the Godot host and CI were audited by
reading only: this environment has no Unity editor, Godot build or Windows
Chrome, so those findings are recorded as open items with the fix each needs.

Environment: Linux x86_64, 4 cores, gcc 13 and clang, RelWithDebInfo core
(`-O2 -g`), stub font, 1280x720. Absolute times are about 2.5 times the
Ryzen figures in the [September 15 baseline](performance-20260915.md), so only
the ratios between runs here carry over.

## Results

| Area | Before | After |
|---|---:|---:|
| First cold load in a process, layout-stress (instructions) | 225.7 M | **162.1 M** (−28%) |
| Cold load, hud (instructions) | 1,276.7 M | **1,170.0 M** (−8.4%) |
| Cold load, randhtml (instructions) | 1,439.1 M | **1,324.9 M** (−7.9%) |
| Warm cold load, 8 samples (interleaved medians) | — | −0.9% to −3.6%, glass +0.4% (noise) |
| 600-deep nested page, cold | 2,160 ms | **52 ms** |
| 3,000-deep nested page, cold | 23,071 ms | **461 ms** |
| 5,000 unclosed `<b>` | 36.4 s | **1.2 s** |
| `:is(` nested 50,000 deep | segfault | 29 ms, rule dropped |
| `f(` nested 100,000 deep | segfault | 31 ms, declaration dropped |
| `calc()` with 100,000 terms | segfault after 18 s | 140 ms, declaration dropped |
| 30 doubling custom properties | > 60 s (≈1 GB per element) | 167 ms, value invalid |

All 47 corpus renders (`Tools/weva_render`, 1280x720) are **byte-identical**
before and after. The core suite passes **507,471 checks, 0 failures** with
gcc, with clang (Release, 0 warnings) and with ASan and UBSan (all 16
sanitizer suites). The earlier count was 507,328; the difference is the new
regression tests. The Chrome layout oracle passes all 334 tracked captures
(samples 47, hand 62, harvest 225) within 1.5 px, with nothing excused.

## Performance changes

**The property registry indexed itself 334 times.** Its constructor went
through `register_property`, which rebuilt the whole name index, hashing every
name registered so far, after each of the 334 built-in names. It also sorted
a `sorted_` table that nothing read. Together that was a quarter of the
instructions in a process's first cold load of layout-stress. The constructor
now indexes once, and the dead table is gone. `instance()` is inline, and after
the first call it is a single acquire load.

**Gradient stop lookup.** `sample_stops` rebuilt each pair of stops from the
40-byte stop list for every sample, and took two `log` calls per sample for a
colour hint's exponent. `prepare()` now resolves the pairs once into a compact
segment table in the same visiting order and with the same arithmetic. This
changes no pixels: `sample_stops` itself went from 317 M to 290 M instructions
on randhtml. What remains is the premultiplied divides and the compositing,
which a bit-exact change cannot remove (see open item P1).

**Intrinsic contributions in the incremental index were quadratic in depth.**
`IncrementalLayout::index` asked every local block for its min- and
max-content contribution from scratch. Each ask walked the block's whole
subtree, so a chain of nested blocks cost about depth³. The index is under
1 ms on the corpus samples (0.8 ms of layout-stress's 9.3 ms layout), but a
page 600 blocks deep spent 2.1 s there. `IntrinsicContributionScope` now
memoizes contributions for the duration of one index pass, while the tree is
const. The incremental mutation corpus (full against incremental geometry)
still passes.

## Robustness fixes: hostile markup and stylesheets

Each fix has a test in `libweva/tests/test_hostile_input.cpp`, and all of
them pass under ASan and UBSan.

- **HTML depth.** Past 512 open elements a new node attaches to the current
  node's parent, which is Chrome's `kMaximumHTMLParserDOMTreeDepth`. Before
  this, `<div>` repeated 200,000 times overflowed the stack in the Document
  destructor.
- **Formatting-element reconstruction.** It ran before every text token and
  scanned the open stack once per list entry. The stack now keeps a count of
  its open nodes, so membership is a lookup. Parse results are unchanged, but
  the worst case is now quadratic, not cubic. Chrome's "Noah's Ark" limit
  would make it linear; see open item C4.
- **Selector nesting.** `:is`/`:not`/`:where`/`:has`/`of S` are limited to
  64 levels, and a deeper selector drops the rule.
- **Value nesting and `calc()` chains.** Generic functions and parenthesis
  groups are limited to 64 levels of nesting, and a `calc()` expression to 512
  binary operators. Beyond either, the declaration is dropped. A chain had
  built a left-deep tree that evaluation, type classification (quadratic) and
  destruction all walked recursively.
- **`var()` expansion.** Substituted values are capped at 2 MiB, Chrome's
  `kMaxVariableBytes`, and a longer one is invalid at computed-value time.
  Depth was already capped at 32, but width was not.
- **PNG inflate.** Output stops at the size the header declares, as libpng
  ignores data past the last row. The up-front reservation is capped by what
  the compressed bytes could produce. A 1×1 PNG could previously inflate
  gigabytes before the size check, and an 8192×8192 header reserved 320 MB
  before reading any data.
- **`@import` fan-out.** A document's import graph is limited to 256 loads.
  Depth 8 and the cycle check did not bound breadth: nine sheets that each
  import the next 20 times would be 20⁸ loads.
- **`:nth-*()` coefficients** saturate to `int` and match in 64-bit
  arithmetic. `atoi` overflow and `INT_MIN` negation were undefined behaviour.
- **`&#0;`** becomes U+FFFD, as in Chrome. It used to insert a NUL that
  truncated the value wherever the ABI returned it as a C string.

## C ABI lifetime fixes

These are tested in `libweva/tests/test_c_abi_lifetime.cpp` and
`test_c_abi_binding.cpp`. Against the unmodified `weva_c.cpp` under ASan, the
first five tests report heap-use-after-free and the next two fail their
checks. With the fixes, all of them pass under ASan and UBSan.

| Bug | Old behaviour |
|---|---|
| `load_html` with markup the tokenizer rejects | freed the live tree, then returned with every handle, the focus and the box tree still pointing into it |
| `load_html` then pointer or hit test before `update` | box tree read freed styles and elements |
| `weva_element_remove` then pointer before `update` | hover and event dispatch read the freed element |
| scroll-snap animation whose container is removed or reloaded | `snap_advance` raised an event on a freed element |
| `set_viewport` / `set_color_scheme` | freed published `weva_texture.rgba` pixels before the next update |
| `select_word_at` over a disabled field | rewrote the selection of the field that had focus |
| getters on a miss (`attribute`, `text`, `value`, `selected_text`, `event_text`) | left the caller's old buffer contents in place |
| binding value callback reporting 2⁶² bytes / asset reader reporting 2⁶² | `resize` threw `bad_alloc` with exceptions disabled, which aborts the host. Now capped at 64 MiB and 512 MiB and treated as unavailable or missing, as documented in `weva_c.h` |

No entry points were added or removed. The header only documents the two
limits.

## Open items

These were found and verified by reading the code but are not fixed here.
They need a decision, or an engine installation this environment lacks.

### Core

- **P1: rasterize cold paint on more than one thread.** Gradients and blurs
  are still 65–75% of a cold load on hud and randhtml. Each texel is
  independent, so splitting rows across a small pool would be byte-identical.
  But the core has no threads today, and hosts would have to agree to it.
- **C1:** `weva_element_set_text` frees the text node that `Box::text` views.
  Only the tooling call `weva_document_boxes` reads it before the next update.
  The structural cases are fixed here.
- **C2:** `weva_element_set_style(e, "color", "red; display:none")` injects a
  second declaration.
- **C3:** `weva_text_direction` truncates its length to `int32_t`. It returns
  the wrong answer but never reads out of bounds.
- **C4:** Chrome's "Noah's Ark" limit of three identical active formatting
  elements would make reconstruction linear. It changes the tree for pages
  with more than three identical open formatting tags, so it needs Chrome
  captures first.
- **C5:** recursive layout at the 512-depth cap is untested on 1 MB Windows
  main-thread stacks.

### Unity package (not compiled here)

- **U1 (bug):** `NativeInputFeed` → `SetComposition` passes UTF-16
  `text.Length` where the ABI takes UTF-8 byte offsets, so a CJK preedit caret
  lands mid-character. Godot converts correctly.
- **U2 (perf):** every frame, the binding refresh allocates per binding: path
  strings, `Split`, boxing, UTF-8 encoding, a `QueryAll("[data-model]")`, and a
  closure per `ReadString`. Cache the split paths and model elements by
  `StructureVersion`.
- **U3 (perf):** `NativeDocumentRenderer` recopies and re-uploads every
  mesh on any draw-serial change and ignores `weva_document_draw_versions`,
  which the Godot host already uses.
- **U4:** the URP pass sizes the viewport from the render-scaled camera
  target, while input uses screen pixels. With two cameras of different sizes,
  the document lays out twice per frame.
- **U5:** a full-screen backdrop copy is allocated every frame even when no
  `backdrop-filter` exists.
- **U6:** `texture.Apply(false, false)` keeps CPU copies of every document
  texture.
- **U7:** strong `GCHandle`s make `NativeDocument`'s finalizer unreachable,
  and seven of the eight font callbacks lack exception guards, which aborts
  under IL2CPP.

### Godot host

- **G1 (bug):** blended runs and SDF rects draw on child canvas items, so
  later normal content paints beneath them.
- **G2 (bug):** `pump_events` keeps using handles after a handler reloads the
  document. Unity guards against this with `IsCurrent`.
- **G3 (perf):** canvas items, materials and packed arrays are recreated on
  every `_draw`.

### Tooling and CI

- **T1:** `check.sh` runs every Godot gate against whatever
  `libweva_godot.so` already exists when the extension build is skipped.
- **T2:** several scene steps check only the summary line, not the exit
  status or `SCRIPT ERROR`. Logs go to fixed `/tmp` paths.
- **T3:** workflow actions are pinned by tag, not SHA, including
  `ilammy/msvc-dev-cmd@v1`, and `persist-credentials` is left on.
- **T4:** gcc 13 reports `-Wmismatched-new-delete` in `weva_bench`'s
  allocation hook. This is a known false positive for a replacement
  `operator delete` that calls `free`, and it predates this pass.

Fixed in tooling: under gcc 13 the `weva_asan_active` probe failed because
UBSan's object-size check reported the deliberate heap overflow before ASan
could. The probe is now built with `-fno-sanitize=object-size`. The UBSan
control is a signed overflow and is unaffected.

## Reproduce

```sh
cmake -S . -B build-rel -G Ninja -DCMAKE_BUILD_TYPE=RelWithDebInfo
cmake --build build-rel
./build-rel/libweva/tests/weva_tests                     # 507,471 checks
valgrind --tool=callgrind build-rel/Tools/weva_bench/weva_bench \
    Tools/oracle/corpus/samples/hud.html Tools/oracle/corpus/samples/hud.css 1 --cold
WEVA_STAGE_LOG=1 build-rel/Tools/weva_bench/weva_bench deep600.html "" 1 --cold
```

`deep600.html` is `<div>` repeated 600 times, followed by `deep`. The
sanitizer build is `-DWEVA_SANITIZERS=ON`.
