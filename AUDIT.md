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
| 2 | Dead and stale material | not started |
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
