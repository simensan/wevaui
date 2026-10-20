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

## Follow-up: the open items, resolved

The first pass left the items below open. The follow-up fixes every one, and
checks it as far as this Linux environment allows. It downloaded stock Godot
4.7.2 and the pinned godot-cpp, and built the Unity plugin for Linux. Unity
itself is not available, so the renderer, URP pass, font backend and
component edits were syntax-checked only (Roslyn); see "What could not be
run here".

After the follow-up the core suite passes **507,732 checks, 0 failures** with
gcc and with clang (Release, 0 warnings). Under ASan and UBSan it passes
507,726: the 512 KB small-stack test skips itself there, since the sanitizers
inflate every frame. All 47 corpus renders stay byte-identical, the threaded
raster included, and ThreadSanitizer reports nothing on the raster tests.

### Core

- **P1: threaded rasterization.** Rows of gradient and image backgrounds,
  both blur passes, the blur conversions, rounded coverage and the shadow
  punch-out run on up to four threads. Threads are started and joined inside
  the call, so there is no pool to tear down at unload. Output is
  byte-identical: the 47 renders match, a test compares serial and threaded
  bytes, and ThreadSanitizer is clean on eight samples. Cold load with 1
  thread against the default, on a 4-core machine:

  | Sample | 1 thread | default |
  |---|---:|---:|
  | hud | 139.8 ms | **55.3 ms** |
  | episode-stats | 107.4 ms | **37.6 ms** |
  | randhtml | 121.5 ms | **61.8 ms** |
  | glass | 139.1 ms | **79.5 ms** |
  | map | 31.9 ms | **16.8 ms** |

- **C1:** text nodes replaced by `set_text`/`set_html` are kept until the
  next update, so `weva_box.text` stays valid, as documented.
- **C2:** `set_style` rejects a value that is not exactly one declaration, and
  a property name holding a separator. The getter ignores entries with no
  colon.
- **C3:** `weva_text_direction` scans in chunks that end on character
  boundaries. A test covers every seam position.
- **C4:** a `</p>` finds its paragraph in button scope, and the Noah's Ark
  limit of three identical formatting elements applies. Eight cases now match
  headless Chromium's DOM exactly, where seven differed before. None of the
  337 corpus pages changes DOM.
- **C5, and what measuring it found.** At the depth cap, nested inline-blocks
  and grids did not just overflow the stack; they never finished. Shrink-to-fit
  laid each level out 3^depth times, and grid stretch did 2^depth. Twelve
  nested inline-blocks took 836 ms; twenty nested grids took 2.2 s.
  Shrink-to-fit probes are now memoized per pass. A stretch that would
  reproduce the item's existing height is skipped, unless its subtree reads a
  definite height. Twenty levels of either now take about 3 ms. Boxes stop at
  64 nested elements; the deepest corpus page nests 13. A nested inline-block is two frames of paint recursion,
  at 1.7 KB each with GCC and 2.7 KB with Clang, and 320 levels overflowed
  1 MB. Every layout mode now completes
  600-deep markup on a 512 KB stack, pinned by a test.

Oracle output is identical to the first pass's commit on all 334 captures,
and the renders are byte-identical.

### Unity package

- **U1:** `NativeDocument.SetComposition` takes C# string indices and converts
  them to UTF-8 byte offsets, never splitting a surrogate pair.
- **U2:** a binding refresh no longer re-queries `[data-model]` controls
  unless the structure changed, and no longer re-splits paths. Callback paths
  are decoded through a cache, and values are encoded straight into the
  core's buffer.
- **U3:** runs whose draw versions match the previous frame keep their mesh
  (`RunsReused`). The per-sync collections are reused.
- **U4:** layout uses the camera's pixel size, not the render-scaled target's.
- **U5:** the backdrop copy is allocated only when a document draws a
  backdrop filter. A backdrop that appears mid-frame waits one frame, rather
  than taking the Blit path inside a render graph pass.
- **U6:** document textures are uploaded non-readable.
- **U7:** the document's `GCHandle` is weak, so a forgotten document is
  finalized. All seven unguarded font callbacks catch and record exceptions.
- **Minor:** handler reflection is cached per (type, name). `Cursor` returns a
  cached string while the page's cursor is unchanged.

The Unity-free wrappers (`NativeDocument`, `NativeBindings`,
`UIBindResolver`) were compiled with .NET 8 and run against the Linux
`weva_core.so`. With the fixes, 21 of 21 checks pass. The same program on the
previous sources fails 3:

| Check | Before | After |
|---|---|---|
| caret x for a preedit at indices 0–3 of 日本語 | 9, 9, 9, 19 | 9, 19, 29, 39 |
| bytes allocated per unchanged binding refresh | 1,032 | 560 |
| a forgotten document is finalized | no | yes |
| repeated `Cursor` reads return one string | no | yes |

A new EditMode test, `Composition_CaretOffsetsAreStringIndices`, pins U1.

### Godot host

- **G1:** blended runs and SDF rounded rects go through the ordered layer path.
  Content painted after them is on a higher layer.
- **G2:** `pump_events` stops using the event's handle once a handler has
  replaced the document.
- **G3:** layer canvas items and backdrop materials are pooled across frames.
  They are cleared and reused, and only the surplus is freed.

`layer_order_tests.tscn` pins G1 (pixel readback under Xvfb with OpenGL) and
G2 (headless).

### Tooling and CI

- **T1:** when the extension build is skipped, the gates that load the addon
  skip too. The engine-only text-safety gate still runs.
- **T2:** the demo, inventory, bindings, hover and gallery-hover steps now
  require a clean Godot exit and no `SCRIPT ERROR`, as well as the summary
  line. Every log goes to a per-run `mktemp` directory, which is printed and
  kept.
- **T3:** every action is pinned to a commit SHA, with its tag in a comment.
  Checkouts no longer persist credentials; no job pushes.
- **T4:** the known false positive is suppressed at the definition. The
  benchmark builds without warnings.

### Still open

- **Nested flex** is polynomial, roughly depth⁴: 40 levels take 7 ms, 80 take
  67 ms. The 64-level box cap bounds it, but intrinsic sizes inside flex
  layout are recomputed per level. Caching them needs invalidation while
  layout mutates the tree.
- **Floats in intrinsic sizing.** A float containing a float measures 0 wide;
  Chrome gives it the inner float's width. The engine deliberately excludes
  floats from min/max-content (inline_layout.cpp). Changing that needs new
  Chrome captures.
- **Corpus DOM against Chromium.** 117 of 337 pages differ, and every sampled
  difference is inert: whitespace after `</body>`, or `<link>` placement
  between head and body.
- **Glass** still has 23 small shadow textures that are each too small to
  split. Rasterizing independent textures concurrently would help. (Since
  done: see [screen opening](screen-open-20260925.md).)
- **Two cameras of different sizes** still lay a Unity document out twice per
  frame. Which camera should own the viewport is a product decision.

### What could not be run here

The Unity editor suites could not run. `NativeDocumentRenderer`,
`UnityFontBackend`, `UIRenderGraphPass`, `UIBatchedRendererFeature`,
`WevaDocument` and the new EditMode test parse cleanly with Roslyn, but were
not compiled against UnityEngine. Run the Native EditMode suite before
releasing the package.

## Reproduce

```sh
cmake -S . -B build-rel -G Ninja -DCMAKE_BUILD_TYPE=RelWithDebInfo
cmake --build build-rel
./build-rel/libweva/tests/weva_tests                     # 507,732 checks
valgrind --tool=callgrind build-rel/Tools/weva_bench/weva_bench \
    Tools/oracle/corpus/samples/hud.html Tools/oracle/corpus/samples/hud.css 1 --cold
WEVA_STAGE_LOG=1 build-rel/Tools/weva_bench/weva_bench deep600.html "" 1 --cold
```

`deep600.html` is `<div>` repeated 600 times, followed by `deep`. The
sanitizer build is `-DWEVA_SANITIZERS=ON`.
