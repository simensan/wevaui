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
| 3 | TODO / FIXME / HACK inventory | not started |
| 4 | Test hygiene | not started |
| 5 | Build hygiene | not started |
| 6 | Host duplication | not started |
| 7 | ABI surface | not started |
| 8 | Docs accuracy | not started |

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
