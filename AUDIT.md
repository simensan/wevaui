# Technical audit and cleanup

A running ledger, kept on the `worktree-tech-audit` branch. One section per
area: status, findings, what changed. Written so an iteration that starts cold
can resume from it. Base: `5d7cf514` (the shared-core merge).

Rules this audit works under: nothing is pushed; each finished chunk is its own
commit; a claim is made only after the affected suite has been run; failed
evidence is preserved rather than re-run until green; `Assets/` scenes are not
touched.

| # | Area | Status |
|---|------|--------|
| 1 | Repo layout | **proposed — awaiting review**, nothing moved |
| 2 | Dead and stale material | **swept** — 3 fixes landed, 3 findings need a decision |
| 3 | TODO / FIXME / HACK inventory | **done** — 10 found, 2 stale ones fixed, and a broken gate repaired |
| 4 | Test hygiene | **done** — nothing suppressed; all 6 stale headers fixed |
| 5 | Build hygiene | **MSVC done** — 3 warnings fixed; gcc/clang blocked by the worktree |
| 6 | Host duplication | **done** — three behavioural divergences found in one function |
| 7 | ABI surface | **done** — 4 dead entry points, 2 doc gaps, asymmetry explained |
| 8 | Docs accuracy | **done** — the front door never mentioned the engine; 3 files fixed |

---

## 1. Repo layout

**Status: proposal only. No file has been moved.**

### What the tree actually is

`godot-port/` is not a Godot port any more. It is the engine: the C++ core, its
embedded third-party code, its tools, its docs, its verification receipts, and
both hosts. The Godot host is one subdirectory of it; the Unity host is another.
The name is the last thing left over from when the directory really was an
experiment in porting to Godot.

`godot-port/` is also, in build terms, a project root of its own. Its
`CMakeLists.txt` is the top-level CMake project; `check.sh` derives `ROOT` from
its own location and `REPO` as `ROOT/..`; `third_party/`, `tools/` and `docs/`
hang directly off it. Both host `CMakeLists.txt` files reach it with
`add_subdirectory(${CMAKE_CURRENT_SOURCE_DIR}/../..)`.

### Reference inventory

Counted at `5d7cf514` with `git grep`. 108 tracked files mention `godot-port`.

| Kind | Count | Breaks how |
|---|---|---|
| `parents[N]` depth walks in Python under `godot-port/` | 27 | **silently**, at run time, with a wrong path |
| GDScript `res://../../../tools/...` walks | 5 in 4 files | **silently**, at run time |
| CMake `../..` / `../../libweva/include` | 7 (2 Godot host, 5 Unity host) | at configure time |
| `godot-ci.yml` path references | 12 | at CI time |
| `Tools/Layout/*.mjs` | 5 in 2 files | at run time |
| C# source comments and one path constant | 9 files | one real (`FrontierCampNativeTests.UiDir`), eight prose |
| `check.sh` `$ROOT/...` | 74 | not at all if `check.sh` moves with its tree |
| Markdown prose | 30 files | not at all; wrong text only |
| `docs/verification/*.json` receipts | 60 files | **must not be rewritten** — they record what was run |

The 27 Python depth walks and the 5 GDScript ones are the important number.
They encode *how deep the script sits*, not what it is looking for, so a move
leaves them pointing somewhere plausible and wrong. Nothing fails at build time;
a harness just reads the wrong corpus or fails to find a font.

### Option A — rename `godot-port/` in place (recommended)

`godot-port/` → `engine/` (or `core/`, if the name should say what it holds
rather than what it is).

Everything inside keeps its relative position, so all 27 Python depth walks, all
5 GDScript walks, all 7 CMake `../..` and all 74 `check.sh` `$ROOT` references
keep working untouched. What changes:

- `.github/workflows/godot-ci.yml` — 12 references, plus the two `paths:` filters.
- `Tools/Layout/capture-all-chrome-layouts.mjs` (4), `Tools/Layout/check-form-baseline.mjs` (1).
- `Packages/com.wevaui/Tests/Editor/Native/FrontierCampNativeTests.cs` — the `UiDir` constant.
- `gen_bindings.py`'s generated banner, and therefore a regenerated `WevaNative.g.cs`.
- 30 markdown files, as prose.
- The local build directories (`~/weva/build-*`) need one reconfigure; CMake
  caches absolute source paths.

Receipts under `docs/verification/` stay exactly as they are: they are evidence
of runs that happened under the old path.

Cost: roughly 50 real edits, all of which fail loudly if missed. Benefit: the
directory stops claiming to be a Godot port.

### Option B — flatten to `core/` + `hosts/` + `tools/` + `docs/` at the repo root

This is what I suggested in conversation before looking. Having looked, it is
the worse trade:

- All 27 Python depth walks and all 5 GDScript walks change meaning silently.
- `check.sh` loses its footing: `ROOT` becomes the repo root and `REPO="$ROOT/.."`
  then points *outside* the repository.
- Both hosts' `add_subdirectory(../..)` would reach the repo root, where there is
  no CMake project.
- The repo would hold two `tools/` directories — the existing root `Tools/`
  (C#-side: BaselineGen, TestVerifyAll, PerfBench, RenderGoldens, Layout) and the
  engine's `tools/` (oracle, weva_dump, weva_render, weva_bench) — differing only
  in case, which is a genuine hazard on Windows and macOS.
- The top-level CMake project would sit at the repo root next to a Unity project,
  which is what `godot-port/` currently keeps cleanly separate.

Option B only pays off after Phase 4, when the C# engine is deleted and the
repository stops being a Unity project with an engine inside it.

### Recommendation

Take Option A, and only after the branch is pushed and CI is green on it — the
workflow's `paths:` filters are part of the rename, so a mistake there means CI
silently stops running rather than failing.

Defer Option B to Phase 4, where it is a natural part of "delete the C# layout
engine" rather than a standalone churn commit.

**Awaiting your decision. Nothing will be moved without it.**

### Smaller layout notes found on the way

- `Tools/` (repo root) and `godot-port/tools/` are two different things one
  capital letter apart. Worth renaming one regardless of which option is taken.
- `godot-port/hosts/godot/addon/weva_view.gd` and
  `godot-port/hosts/godot/project/addons/weva/weva_view.gd` are byte-identical.
  Checked: `addon/` is the packaging source (`package_addon.py` reads it into the
  ZIP) and `project/addons/weva/` is the installed copy the dev project runs
  against. Neither is dead, but nothing enforces that they stay equal, so the
  dev project can silently test a stale script. Area 2 picks this up: a check in
  `check.sh` comparing the two is cheaper than a rule nobody remembers.

---

## 2. Dead and stale material

**Status: swept.** Three fixes landed. Three findings need your decision
because each one changes either a test outcome or tracked content.

### 2.1 The GPU goldens have never verified anything — landed nothing yet

This is the biggest thing in the area, and it is not the gitignore gap it
looked like.

`GpuGoldenAssert.Match` (`Runtime/Testing/Goldens/GpuGoldenAssert.cs:53`) does
this when no baseline file exists:

```csharp
if (!File.Exists(baselinePath)) {
    WriteBaseline(baselinePath, actualPng);
    return;                     // ← reports success
}
```

Seeding returns as a **pass**. And no GPU baseline has ever been committed:

| Directory | Baselines tracked |
|---|---|
| `Goldens/Baselines/` (software) | 76 |
| `Goldens/Baselines.GPU/` | 0, only `.gitkeep` |

So on every clean checkout, all ten `Gpu_Golden_29…38` tests render a snippet,
write the result to disk, and report green without comparing it to anything.
They have never once verified a pixel. The PlayMode run in this session counted
them among its 302 passes.

Two aggravating details:

- In batch mode the render happens before the glyph atlases bake, so a seeded
  baseline has no text in it. I looked at two of the ones seeded this session:
  `31-centered-modal` is a white card with no text, `38-hero-picker-scroll-clip`
  is three tiles with no labels. Committing those would freeze a wrong truth.
- The software `GoldenAssert.cs:31` has the same seed-and-return. It bites less
  because 76 baselines are committed, but a newly authored snippet gets a free
  pass there too.

**Recommended fix:** seeding must not report success. Keep writing the file, then
throw with a message saying the baseline was seeded and must be inspected and
committed before the test means anything. Refuse to seed at all under
`Application.isBatchMode`, where the render is known to be text-less.

**Why it is not done yet:** it turns ten vacuous passes into ten honest
failures, which makes the suite redder than the instruction for this audit
allows me to leave it. The failures would be truthful and each message would say
exactly what to do, but the decision is yours. Either:

- **(a)** take the fix and seed the ten baselines from the editor's Test Runner,
  inspect them, and commit them — after which the suite is green *and* the tests
  actually test something; or
- **(b)** take the fix and let the ten stay red until someone gets to (a),
  recorded as a known-failing set; or
- **(c)** leave it, and the ten tests keep passing without testing anything.

I recommend (a).

### 2.2 `Out.GPU/` was not ignored — fixed

`GpuGoldenAssert` writes `.actual.png` and `.diff.png` into
`Goldens/Out.GPU/` on failure. The root `.gitignore` covered the software
rasterizer's `Goldens/Out/` but not the GPU one, so a failing GPU golden left
untracked PNGs in `git status`. Added the missing rule, and checked that
`Baselines.GPU/` stays trackable — the baselines are supposed to be committed,
only the failure artifacts are not.

### 2.3 A 2.3 MB texture is committed twice

`frontier.png` is byte-identical at both:

- `godot-port/examples/frontier_camp/ui/assets/frontier.png`
- `godot-port/hosts/godot/project/samples/western_survival/assets/frontier.png`

4.6 MB of the repository is one image. They belong to two separate Godot
projects that each need the file at a path they control, so this is not a
deletion — it needs either a build step that copies it or a shared assets
directory both projects reference. Flagged, not touched.

### 2.4 The verification receipts are 16 MB

229 files under `godot-port/docs/verification`, totalling 16 MB. The tail is
heavy: `binding-commit.json` is 2.1 MB, `range152.json` 1.1 MB,
`modal-index.json` 860 KB, and three more over 650 KB.

Receipts are evidence and should stay. But a 2 MB JSON receipt is a raw dump
that was pasted in rather than a summary of what was measured, and the whole
directory is now larger than the C++ core's source. Worth a pass that keeps the
conclusions and drops the embedded raw payloads — but that is editing evidence,
so it needs your agreement on the rule before anything is rewritten.

### 2.5 Orphaned scripts — 22 removed

Swept all 94 tracked `.py` / `.mjs` / `.sh` outside `Packages/` and `Assets/`,
looking for ones whose name appears nowhere else. Two heuristics were needed:
matching the filename misses Python modules imported by stem, which is why
`imgio.py` looked orphaned on the first pass and is not.

Three families of false positive, all confirmed live:

- `test_release_gates.py`, `test_exports.py` — found by `unittest discover` in
  CI, by pattern rather than by name.
- `test_frontier_perf_*.py` (3 files) — same, from `check.sh:87`.
- `imgio.py` — imported as a module.

**Removed (22 files, `Tools/Layout/`):** nine `diff-<sample>.mjs`, ten
`extract-<sample>.mjs`, and three scratch scripts (`__probe-lineheight.mjs`,
`__shot-portrait.mjs`, `screenshot-glass.mjs`).

These are one-shots from before the generic tooling existed, and they say so
themselves. `extract-hud.mjs`'s header: *"One-shot extractor for
Assets/UI/hud.html that uses the locally-installed system Chrome (puppeteer's
bundled Chromium download was failing under git-bash on Windows). Mirrors
capture-all-chrome-layouts.mjs's element dump shape."* The generic replacements
take the sample as an argument and are both referenced:
`capture-all-chrome-layouts.mjs` (15 references) and `extract-chrome-layout.mjs`
(2). Nothing referenced any of the 22, and nothing references them now.
`Tools/Layout/` goes from 30 entries to 8, all of them referenced.

**Kept, with a note:** `godot-port/tools/oracle/check_item_context_chrome.py`
and `survey_all.sh` are also unreferenced, but they are not scratch — both carry
proper usage docstrings, `survey_all.sh` wraps `chrome_survey.py`, and they sit
in a directory of 57 sibling `check_*_chrome.*` tools that are run on demand
rather than from a gate. The gap is documentation, not deadness: nothing tells a
reader these exist. Area 8 picks that up.

### 2.6 Unreferenced sources — nothing found, C++ side is clean

- All 72 `.cpp` under `libweva/src/` are named in `libweva/CMakeLists.txt`.
- All 72 headers under `include/weva/` have at least one includer.

Nothing actionable. Recorded because "we checked and it was clean" is worth as
much to the next reader as a finding.

The C# side was not swept for dead classes. `Runtime/` is slated for deletion in
Phase 4, so dead-code analysis there buys little; if Phase 4 slips, it is worth
revisiting.

### 2.7 A stale architectural claim — fixed

`ARCHITECTURE.md` carried a section headed *"Data binding is not ported"*,
ending: *"`hosts/godot/` owns this; `libweva` has no binding layer at all."*

That is no longer true, and the ABI contradicts it in the same repository:

| Evidence | Where |
|---|---|
| `binding.cpp`, ~500 lines | `libweva/src/` |
| `weva_binding_source`, `weva_document_set_binding_source`, `weva_document_refresh_bindings` | `weva_c.h` |
| `binding_tests.gd`, 186 checks | Godot host |
| `NativeBindings.cs`, `NativeBindingTests` | Unity host |

Rewrote the section to record what actually happened, because the split is the
interesting part: the *reflection* was not ported, as planned, and stays
host-side; the *template layer* above it was — `{{ path }}`, `data-each` /
`data-key`, `data-model`, handler dispatch — because that is markup semantics
rather than language reflection, and leaving it per-host meant writing it twice
and watching it drift. The seam is a callback table the host fills; the core
never learns what an object is.

A doc that states the opposite of the shipped design is worse than no doc, and
this one sits in the file a new contributor reads first.

### Still to sweep in this area

Nothing. Area 2 is done apart from the three findings above that need your
decision (2.1 GPU goldens, 2.3 the duplicated texture, 2.4 receipt bloat).

---

## 3. TODO / FIXME / HACK inventory

**Status: done.** Ten markers in the whole repository. Two were stale and are
fixed. Chasing one of them uncovered a broken gate, repaired below.

### The count

| Tree | `TODO`/`FIXME`/`HACK`/`XXX` |
|---|---|
| `libweva/` (C++ core) | 0 |
| `hosts/` (both) | 0 |
| `godot-port/tools/`, `Tools/` | 0 |
| `Packages/com.wevaui/Runtime` | 7 |
| `Packages/com.wevaui/Tests` | 3 |

Zero across ~30k lines of C++ and both hosts is worth stating plainly: the
convention there is to write the reason in prose next to the code instead of
leaving a marker, and it holds without exception.

### 3.1 A stale TODO hid a test that passed for the wrong reason — fixed

`CascadeEngineTests.Media_rule_inner_rules_currently_always_apply` carried
*"TODO: once media-query evaluation lands…"*. Evaluation landed a long time
ago — `Runtime/Css/Media/` holds `MediaQueryEvaluator.cs` and eleven more
files, and `CascadeEngine` takes a `MediaContext`.

What the test actually pinned was an accident. `CascadeEngine`'s one-argument
constructor supplies `MediaContext.Default(10000, 10000)`, and the production
code says why:

> *"Default surface is intentionally larger than any reasonable @media threshold
> so historical CascadeEngineTests authored before the evaluator existed (which
> assume '@media always applies') continue to pass."*

So `@media (min-width: 9999px)` applied because 10000 ≥ 9999, not because
`@media` was ignored — while the test's name asserted the opposite. Renamed it
to `Media_rules_evaluate_against_the_default_10000px_surface`, explained the
default, and added the negative case (`min-width: 10001px` must not apply),
which is the half that proves the evaluator is running at all.

### 3.2 A second stale TODO — fixed

`SelectorParserTests.Nth_child_of_selector_parses_and_drops_filter` carried
*"TODO: the `of <selector>` filter is currently dropped silently"*. It is not:
`SelectorParser.cs:533` parses it into `NthOfFilter` and
`SelectorMatcher.cs:275` honours it through `FilteredChildIndex`.
`SelectorStateDependencies` reasons about it too. Renamed to
`Nth_child_of_selector_keeps_its_filter` and added assertions that the filter
survives parsing.

### 3.3 The headless C# suite did not build — repaired

Found while trying to verify 3.1. `dotnet run --project Tools/TestVerifyAll`
failed to compile:

```
Runtime/Native/WevaNativeDocument.cs: error CS0246: 'Font' could not be found
Runtime/Native/UnityFontBackend.cs:   error CS0246: 'MonoPInvokeCallback' …
```

The runner globs `Runtime/**/*.cs` and excludes the directories that need real
Unity APIs (`Rendering/**`, `Text/**`, `Forms/Bridge/**`). `Runtime/Native/**`
arrived with the shared-core merge, needs `Font`, `Rect`, `TextAreaAttribute`
and `MonoPInvokeCallback`, and nobody added it to the exclude list. So the
headless C# gate has produced **no result at all** since the merge — not a
failure, a build error, which is easy to skim past.

That also means the `9,905 pass / 2 fail` figure carried in project memory was
unreproducible. Added the one exclusion, with a comment saying why, and the gate
came back:

| | Passed | Failed | Skipped |
|---|---|---|---|
| Before | *build error* | — | — |
| After | **9,929** | **2** | 57 |

The two failures are exactly the known pair, `SnapshotLayoutTests` and
`FillInheritedBitsetTests`, both `ArgumentOutOfRangeException`, both predating
this work. The pass count is 24 higher than memory's figure because tests were
added since it was written. Memory should be updated to 9,929 / 2.

The Native host's own tests are unaffected: they live in `Tests/Editor/Native/`
and run in the Unity editor, which the headless runner never touches.

### 3.4 The remaining eight markers — all real, all kept

| Marker | Verdict |
|---|---|
| `TextEditModel.cs:33` wiring point | real, out of headless scope by design |
| `GridContainerProperties.cs:64` GetParsed migration | real, a perf migration |
| `BackgroundResolver.cs:508` CssValue typing | real, a future migration |
| `URPRenderBackend.cs:144` image brushes | real gap in that backend |
| `SoftwareRasterizer.cs:14,862` magenta image brushes, filter list | real, documented scope of the software rasterizer |
| `UnityFontEngineBackend.cs:87` validate against Unity 6 surface | real |
| `PaintAllocationTests.cs:282` drive allocations to 0 | real, a perf goal with a number attached |

None is stale and none claims something already done. Left alone.

---

## 4. Test hygiene

**Status: in progress.** The inventory is done and the answer is better than
expected. One misleading file header is fixed; five remain.

### Nothing is suppressed

| Mechanism | Count |
|---|---|
| `[Ignore]` attributes | **0** |
| `[Explicit]` attributes | 2 |
| `Assert.Inconclusive` | 11 |
| `Assume.That` | 52 |

There is not a single ignored test in the C# suites. The two `[Explicit]`
classes are both capture tools that write PNGs for a human to look at
(`RenderGoldenCaptureTests`, `NativeGameViewCaptureTests`), correctly kept out
of default runs. The `Assume.That` uses are environment guards — no OS font
enumeration, no GPU, no sample file — which is what `Assume` is for.

So the honest answer to "what is switched off and can it come back on" is:
nothing is switched off.

### 4.1 ...but 104 comments still describe `[Ignore]` markers — 1 of 6 fixed

Nine files mention `[Ignore]` in prose. **All nine have zero `[Ignore]`
attributes.** Three of the mentions are accurate history ("un-ignored — now
green", "previously `[Ignore]`'d"). Six actively mislead, in the present tense:

| File | Claim | Reality |
|---|---|---|
| `QuotesAndQuoteContentTests` | "`quotes` is NOT registered ... GetId returns -1 ... marked `[Ignore]`" | registered, inherited, initial `auto`; nothing skipped — **fixed** |
| `FontVariantFeatureSettingsSizeAdjustTests` | "marked with `[Ignore]`" | none |
| `ForcedColorAdjustCascadeTests` | "the `[Ignore]` markers ... should be removed" | already removed |
| `FloatFragmentationTests` | "test is `[Ignore]`'d" | none |
| `MarginCollapsingTests` | "Each `[Ignore]`'d test" | none |
| `NegativeMarginTests` | "the spec-correct test is `[Ignore]`'d" | none |

All six are fixed. Each was checked against the engine before rewriting,
because a green test is not proof the spec behaviour landed — the test could
have been rewritten to assert the divergence instead. In every case the gap had
genuinely closed, and the comment was the only thing left describing it:

- **`QuotesAndQuoteContentTests`** claimed `quotes` was unregistered and its
  lookup returned -1. `CssProperties.cs:1107` registers it inherited with
  initial `auto`, and the file's own test asserts exactly that.
- **`FontVariantFeatureSettingsSizeAdjustTests`** listed six `font-variant`
  longhands as unregistered and non-inheriting. All six are registered
  consecutively at `CssProperties.cs:773-778`, each inherited with initial
  `normal` — which is the CSS Fonts L4 §6.1 behaviour the header said was
  missing.
- **`ForcedColorAdjustCascadeTests`** claimed the property was unregistered and
  spilled to the side dictionary. It is registered at `CssProperties.cs:825`.
  The genuinely unimplemented half — rendering ignores it, because there is no
  forced-colors OS integration — is now stated on its own instead of being
  buried under a registration claim that is no longer true.
- **`NegativeMarginTests`**, **`FloatFragmentationTests`**,
  **`MarginCollapsingTests`** each described a two-tier scheme where divergent
  cases were `[Ignore]`'d and shadowed by anchor tests pinning the wrong
  behaviour. No anchor tests remain in any of the three, and all 25, 14 and 34
  tests respectively run and pass.

Three files still mention `[Ignore]` and should: `TransitionBehaviorTests`,
`ComputedValueSnapshotTests` and `DisplayListItemTests` describe the
un-ignoring in the past tense, which is accurate history. The six rewritten
above now do the same where the history is worth keeping.

Suite after the rewrites: 9,929 passed, 2 failed — unchanged, as expected from
comment-only edits.

### 4.2 Known-failing sets, so a new red is obvious

Re-measured on this branch after the area 3 gate repair:

| Suite | How to run | Result |
|---|---|---|
| Headless C# | `dotnet run --project Tools/TestVerifyAll -c Release` | **9,929 pass / 2 fail / 57 skip** |
| Unity EditMode Native | `-testPlatform EditMode -testFilter Weva.Tests.EditorTests.Native` | 98 pass / 2 inconclusive |
| Unity PlayMode rendering | `-testPlatform PlayMode -testFilter Weva.Tests.Rendering` | 302 pass / 8 fail |
| C++ core (gcc and clang-ASan) | `~/weva/build-{gcc,clang}` | 505,882 checks / 0 |

The two C# failures are `SnapshotLayoutTests.Stylesheet_change_after_layout_then_relayout_parity`
and `FillInheritedBitsetTests.Inherited_property_flows_from_parent`, both
`ArgumentOutOfRangeException`, both long-standing. **A third is new.**

The 57 skips are benchmark classes (`CascadeBench` 11, `LayoutBench` 14,
`PaintBench` 7, `EndToEndBench` 4) plus 21 others — benchmarks are not
assertions and skip by design in the runner.

Note the pass count: project memory records `9,905 / 2`. The real figure is
**9,929 / 2**, and it was unobtainable at all while the runner did not build
(area 3.3). Memory is worth updating.

Caveat on the PlayMode 302/8: ten of those "passes" are the GPU goldens from
2.1, which pass without comparing anything. The honest figure is 292 verified
passes, 10 vacuous, 8 failures.

---

## 5. Build hygiene

**Status: in progress.** MSVC audited end to end and three warnings fixed.
gcc and clang could not be run from here — see the constraint below.

### A constraint worth recording

**WSL is blocked in a worktree-isolated session.** Every `wsl …` invocation is
refused, so `~/weva/build-gcc` and `~/weva/build-clang` — the gcc and
clang-ASan/UBSan trees this project verifies against — are unreachable from
this branch. The gcc, clang and sanitizer halves of this area need a run from
the main checkout.

What does work from here: a fresh MSVC configure and build of the worktree's
own sources, which produces `weva_tests.exe`. That is a real verification path,
and every C++ claim below was checked through it.

### 5.1 The flags, and an asymmetry

| Toolchain | Flags | Suppressions |
|---|---|---|
| MSVC | `/W4 /EHs-c- /utf-8` | **9**: C4100, C4127, C4244, C4267, C4456-4459, C4702 |
| gcc / clang | `-Wall -Wextra -Wpedantic -fno-exceptions` | none |

No `-Werror` and no `/WX` anywhere, including CI. A new warning fails nothing.

Two of the nine suppressions are not cosmetic:

- **C4244 / C4267** — narrowing conversions and `size_t` truncation. This is the
  one warning class that matters most here, because the root `CMakeLists.txt`
  states the correctness property itself: *"layout computes in double and the
  oracle compares bit-identical geometry against the C# implementation"*. A
  silent `double`→`float` or 64-bit→`int` narrowing is exactly how that property
  breaks.
- And the gap is **symmetric, not an MSVC quirk**: gcc's equivalent
  `-Wconversion` is not in `-Wall -Wextra` either. So *neither* toolchain warns
  about narrowing. Turning it on is likely noisy, but it is worth one measured
  look given what it guards.

The shadowing suppressions (C4456-4459) are likewise symmetric — gcc's
`-Wshadow` is not in `-Wall -Wextra`. Shadowing is live in this codebase: the
background rasterizer deliberately shadows `width`/`height` per layer.

### 5.2 MSVC warnings: 3 fixed, 8 left — all verified

A clean MSVC build of this worktree produced 35 warnings. After the fixes:

| Code | Count | Verdict |
|---|---|---|
| D9025 (`/EHs` overridden by `/EHs-`) | 38 | noise; CMake adds `/EHsc` before our `/EHs-c-`. Fixable by clearing the default rather than overriding it. |
| C4190 (C linkage returning a UDT) | 8 | **structural, left alone — see below** |
| C4805 (`uint64_t ^= bool`) | 2 | **fixed** |
| C5030 (`[[gnu::cold]]` unrecognised) | 2 | expected; a gcc attribute seen by MSVC. Worth a guard. |
| C4389 (signed/unsigned `==`) | 1 | **fixed** |

**C4805, fixed.** `cascade.cpp` folds three form-state functions into the
shape-key hash on consecutive lines; two return `int`, `form_is_default`
returns `bool`. Folding one bit into an FNV hash is intended and correct, so
this was not a bug — but an explicit `static_cast<uint64_t>` states the intent
and matches its two neighbours.

**C4389, fixed.** `weva_c.cpp` stored `Element::form_version()` — an `int64_t` —
in a `uint64_t vertical_version`, so every comparison mixed signedness. It works
today because the counter only counts up, and it would stop working quietly the
moment anything returned a negative sentinel. Changed the field to `int64_t` to
match the only thing ever assigned to it.

**C4190, left alone and worth a decision.** Eight internal helpers —
`ascii_lower`, `cursor_keyword_at`, `tooltip_style`, `parse_fragment`,
`key_of`, `split_declarations`, `trim_decl`, `declaration_property` — sit
*inside* the `extern "C" {` block that opens at `weva_c.cpp:3504`, and return
`std::string`, `std::vector` or `std::string_view`. C linkage returning a C++
type is formally not portable; it compiles and runs correctly here because
caller and callee are the same translation unit.

It is not a bug, but it is the ABI boundary being untidy: helpers with nothing
to do with the C API have C linkage by accident of where they were typed. The
fix is to move the eight above the `extern "C" {`, which is mechanical but
touches a 9,000-line file and wants the full gcc + clang-ASan run to land
safely. Flagged rather than done.

**Verification.** MSVC build of this worktree, before and after:

| | C-code warnings | Core suite |
|---|---|---|
| Before | 13 (8 C4190, 2 C4805, 2 C5030, 1 C4389) | 505,882 checks / 0 |
| After | 8 (C4190 only) | **505,882 checks / 0** |

### 5.3 Sanitizers: better than expected

- ASan + UBSan with `-fno-sanitize-recover=all` on gcc/clang; ASan only on MSVC,
  and the CMake says why (MSVC has no UBSan).
- CI runs the core matrix with sanitizers both `ON` and `OFF`, on
  ubuntu-24.04 and windows-2022.
- `weva_asan_active` and `weva_ubsan_active` are probe *tests* that assert the
  sanitizer actually traps — so a misconfigured build that silently drops
  instrumentation fails instead of passing quietly. That is the failure mode
  most sanitizer setups have, and this one guards against it.

No TSan, which is correct: the core is single-threaded by design. The only
concurrency is two `std::atomic` counters for monotonic ids, and eleven files
using `thread_local` for per-thread scratch. Nothing to race.

### Still to do in this area

The gcc and clang-ASan warning counts, from the main checkout where WSL works.

---

## 6. Duplication between the two hosts

**Status: done.** The `@font-face` sync is implemented twice and the two copies
have drifted apart in three ways, one of which is user-visible. Nothing fixed —
each divergence is a behaviour change on one host, and which host is wrong is
worth your call on at least one of them.

### The shape of the duplication

`sync_css_font_faces` (`weva_node.cpp`, ~100 lines of C++) and
`SyncCssFontFaces` (`UnityFontBackend.cs`, ~60 lines of C#) do the same job
from the same ABI data: read `weva_document_font_faces`, group the rules by
family, decide which face is the family's regular one and which are variants,
load each source, and register the results.

They were written independently against the same prose, which is exactly the
setup where two copies agree on the easy cases and disagree on the edges.

### 6.1 A `font-weight: 500` face works on Unity and vanishes on Godot

The rule for "is this the family's regular face" is different:

| Host | Rule |
|---|---|
| Godot | weight is empty, `normal`, or exactly `400`, **and** style is empty or `normal` |
| Unity | `number < 600 && !italic` |

Follow a single `@font-face` at `font-weight: 500` through both:

- **Unity** — `500 < 600` and not italic, so it becomes the family's regular
  face. The family works.
- **Godot** — `normal_face("500", "")` is false, because the weight is not
  `400`. It then falls to the variant branch, where
  `strength = 500 >= 800 ? 2 : 500 >= 600 ? 1 : 0` is `0` and `italic` is
  false, so the guard `if (strength || italic)` rejects it. The face is
  **dropped entirely** — neither regular nor variant. The family is never
  registered and the text falls back to the theme font.

The same hole swallows every weight from 100 to 300, which is exactly where
light and thin faces live. A page shipping only a `@font-face` at 300 renders
in its intended font on Unity and in the fallback on Godot.

**Which is right:** Unity. CSS Fonts 4 font matching picks the closest
available face for the requested weight; with only a 500 face registered,
that face is what `font-weight: 400` should resolve to. Dropping it is wrong.
The Godot side needs the `if (strength || italic)` guard replaced by a rule
that keeps a face at any weight, using it as the regular face when nothing
closer exists.

### 6.2 Unity never releases a family the stylesheet stopped declaring

Godot walks its `css_font_faces_` map and drops registrations the new
stylesheet no longer declares, re-registering a null font to release the
family. `font_face_tests.gd` pins it: *"removing @font-face on CSS replacement
releases the family"*.

Unity's `_cssFamilies` is only ever read and written — there is no `Remove`, no
unregister, no cleanup pass. Replace a stylesheet to drop an `@font-face` and
the family stays registered in Unity for the life of the document.

No Unity test covers this, which is why it went unnoticed: the Godot behaviour
is pinned and the Unity behaviour is not.

### 6.3 Unity has no "the game's own registration wins" rule

Godot guards explicitly:

```cpp
if (existing == css_font_faces_.end() && family_fonts_.count(entry.first))
    continue; // the game's own registration wins over @font-face
```

so a font the game registered through `register_font_family` is not overwritten
by a stylesheet rule for the same family name. `font_face_tests.gd` pins this
too. Unity has no equivalent, so a page's `@font-face` silently takes over a
family the host registered itself.

### What to do

These are three independent decisions, and only 6.1 needs a judgement call:

1. **6.1** — fix Godot to match Unity, which is the spec-correct side.
2. **6.2** — add the release pass to Unity, mirroring Godot's.
3. **6.3** — add the precedence guard to Unity, mirroring Godot's.

Beyond the fixes, the structural point: this function is the third place the
same rules are written down, after the prose in both READMEs. The weight
classification (`bold`/`bolder` → 700, strength thresholds at 600 and 800,
`italic`/`oblique` prefix matching) is pure data manipulation on strings the
ABI already hands over. It belongs in the core behind one more ABI call
returning already-classified faces, at which point both hosts shrink to "load
this source, register it under this family and variant slot" and cannot drift
again.

### Not examined

Input mapping and event pumping were not compared this iteration. The font-face
finding took the time, and it is the richer seam anyway — input goes through
the ABI's event queue on both sides, which leaves much less room to disagree.

---

## 7. ABI surface

**Status: done.** 135 entry points cross-referenced against what each host
actually calls. Nothing fixed — every finding is either a deletion (needs your
call) or a documentation gap best written with the history to hand.

### Method, and a trap worth recording

The obvious cross-reference is wrong. Searching `hosts/unity/` for each symbol
reports **every** entry point as used, because `gen_bindings.py` generates
`WevaNative.g.cs` naming all 135. The generated file and the generator have to
be excluded, leaving only hand-written call sites, or the Unity column is
meaningless. The first run of this check said "0 unused"; the corrected run
says 4.

### The surface, split by caller

| | Count |
|---|---|
| Called by both hosts | 80 |
| Called by Godot only | 41 |
| Called by Unity only | 10 |
| **Called by neither** | **4** |

### 7.1 Four entry points no host calls

| Entry point | Tests | Tools |
|---|---|---|
| `weva_document_set_render_backend` | 2 | 0 |
| `weva_element_show_popover` | 5 | 1 |
| `weva_element_hide_popover` | 3 | 0 |
| `weva_element_toggle_popover` | 1 | 0 |

All four are covered by core tests, so none is untested — they are simply not
reachable from a game. The three popover calls are the *imperative* half of the
HTML popover API; both hosts drive popovers declaratively instead, through
`weva_document_set_popover_request_events` and the attribute handler. That is a
reasonable state (a host may want them later) but it should be a decision, not
an accident: either a host adopts them, or they are marked as deliberately
host-optional in the header so the next reader does not assume they are load-
bearing.

### 7.2 The 41/10 asymmetry is real, and mostly expected

**Godot-only (41)** is the Phase 2/3 state showing through: forms, validation,
IME composition, select controls, popover request events, transient dismissal,
CSS diagnostics, missing-asset reporting. The Godot host has had years; the
Unity host is months old. Nothing to fix, but it is the concrete measure of how
far behind the Unity host is — 41 entry points it has never called.

**Unity-only (10)** is the inspector surface plus the oracle:
`element_box_model`, `element_matched_rules`, `element_computed_style_all`,
`element_parent`, `element_children`, `element_contains`,
`element_at_devtools`, `layout_dump`, `set_font_leading_rounding`,
`is_animating`. Deliberate: the Unity host has the Elements panel and the
oracle-from-Unity path, and Godot has neither.

One of those ten deserves a note rather than a shrug. `weva_document_is_animating`
tells a host whether a frame is still needed — CSS animations, caret blink, snap
settling, a smooth scroll in flight. Unity asks it through `NeedsRepaint`. The
Godot host never calls it, because `_process` ticks every frame regardless and
leans on `weva_document_needs_input_tick` plus its own `dirty_` /
`paint_pending_` flags. Two defensible strategies — Unity skips work, Godot
keeps it simple — but the difference is undocumented, and a reader comparing the
hosts would reasonably assume one of them has a bug.

### 7.3 Ten ABI minors are documented nowhere

`ARCHITECTURE.md` documents minors 11-14 and 25-37. Minors **15 through 24** are
absent from it entirely; 15 and 16 get one line each in the Godot host's README,
and 17-24 appear in neither file.

The header is patchier still: only 13 of the 37 minors carry an
`Available since ABI minor N` note, so for most entry points there is no way to
tell when they appeared without reading history.

Neither gap breaks anything today. Both bite the moment someone has to support
an older host binary against a newer core, which is exactly what a minor version
is for. Worth one pass reconstructing 15-24 from the log while the history is
still recent.

---

## 8. Docs accuracy

**Status: done.** One finding dominates, and one of the dangling references was
created by this audit's own cleanup.

### 8.1 None of the seven top-level docs knew the engine exists

| File | Lines | Mentions of `libweva` | Mentions of Godot |
|---|---|---|---|
| README.md | 84 | 0 | 0 |
| PLAN.md | 597 | 0 | 0 |
| ROADMAP.md | 81 | 0 | 0 |
| CONFORMANCE.md | 858 | 0 | 0 |
| CSS_FEATURE_AUDIT.md | 233 | 0 | 0 |
| AI_REFERENCE.md | 356 | 0 | 0 |
| AGENTS.md | 295 | 0 | 0 |

Zero, across 2,504 lines. The repository had just merged 430 commits delivering
a C++ core and two hosts over a C ABI, and every document at the front door
still described a Unity-only C# project.

The README was explicit about it. Its orientation paragraph enumerated the
repository's contents — package, demo project, tooling, spec docs — and omitted
`godot-port/` entirely: over 1,100 tracked files, the core, both hosts, the
oracle and 16 MB of receipts. The single largest thing in the repository after
the Unity package, invisible to anyone reading the front page.

**Fixed in the three files where it costs a reader most:**

- **README.md** — the orientation paragraph now names `godot-port/`, says
  plainly that despite the name it is the engine, and states which engine you
  actually get by default (the C# one) and that it remains the oracle's
  reference.
- **AGENTS.md** — `godot-port/` added to the repository tree, with the core,
  both hosts, the oracle and the docs broken out. An agent reading this file to
  orient itself previously could not learn the core existed.
- **AI_REFERENCE.md** — a pointer to the three-way oracle in the tooling
  section, which is the tool to reach for now.

**Not touched:** PLAN.md, ROADMAP.md, CONFORMANCE.md and CSS_FEATURE_AUDIT.md.
These are product and spec documents whose scope is a decision, not a fact —
whether CONFORMANCE.md should describe one engine or two, and whether ROADMAP
should carry the shared-core phases, is yours to make. Flagged, not guessed at.

### 8.2 A dangling reference this audit created

`AI_REFERENCE.md` documented `extract-*.mjs` and `diff-*.mjs` as the
Chrome-versus-Unity workflow. Area 2.5 deleted those 22 scripts as superseded
one-shots, which was right, but the doc still pointed at them — a reference
broken by this audit, three areas earlier.

Rewritten to name the two generic scripts that replaced them, with a note that
the per-sample pairs were removed. Worth recording as a process point: deleting
files is not finished until the prose that named them is checked, and a sweep
for orphaned *scripts* does not catch orphaned *references to* them.

### 8.3 Checked and clean

No remaining references anywhere in the Markdown to any of the 22 deleted
scripts.
