# Golden Test Infrastructure

This directory holds the source-of-truth snippet files and the two separate baseline trees for the software and GPU golden test suites.

## Two complementary golden suites

### Software goldens — `GoldenSuiteTests`

- **Runner**: `GoldenRunner` → `SoftwareRasterizer` (pure-C# CPU renderer).
- **Baselines**: `Baselines/`
- **Run in**: `Tools/TestVerifyAll` headless .NET 8 harness (`dotnet test`).
- **Catches**: paint-emission regressions — wrong box positions, wrong colors, wrong border shapes, missing paint commands.
- **Does NOT catch**: GPU shader bugs, URP command-buffer ordering, RenderGraph pass scheduling, StructuredBuffer packing, blend-mode math on real hardware.

### GPU goldens — `GameUIGpuGoldenTests` (`#if WEVA_URP`)

- **Runner**: `GpuGoldenRunner` → `BatchedURPRenderBackend` + `Hidden/Weva/Quad` shader.
- **Baselines**: `Baselines.GPU/`
- **Failure artifacts**: `Out.GPU/`
- **Run in**: Unity Play mode via the Test Runner (`Window > General > Test Runner > Play Mode`).
- **Catches**: shader regressions, UIBatcher instance-packing bugs, StructuredBuffer layout drift, keyword permutation issues, URP blend / RenderGraph ordering bugs.

Both suites use the **same `Snippets/*.html` + `*.css`** source files as the authoritative layout input. A snippet change that breaks both suites means the expected output changed; a snippet change that breaks only the GPU suite means a shader or backend regression.

---

## Directory layout

```
Tests/Runtime/Goldens/
  Snippets/          ← shared HTML+CSS source files (01-*.html … 38-*.html)
  Baselines/         ← software golden PNGs (committed, auto-seeded by first run)
  Baselines.GPU/     ← GPU golden PNGs (committed AFTER manual visual inspection)
  Out/               ← software golden failure artifacts (not committed)
  Out.GPU/           ← GPU golden failure artifacts (not committed)
  GoldenSuiteTests.cs
  README.md (this file)
```

---

## First-run workflow for GPU baselines

The GPU baselines cannot be generated from the headless CI runner — they require a real GPU, URP, and Unity's shader compilation pipeline.

**Steps to seed `Baselines.GPU/` for the first time (or after a deliberate visual change):**

1. Open the Unity project that consumes `com.wevaui` as a package (e.g. a game project at the repo root).
2. Confirm the `UIBatchedRendererFeature` (or `UIRendererFeature`) is present in the URP Renderer asset. The `Hidden/Weva/Quad` shader must be included in the build — check Player Settings > Graphics > Always Included Shaders if tests see a white-only render.
3. Open `Window > General > Test Runner`, switch to **Play Mode**.
4. Run the tests under `GameUIGpuGoldenTests` (or run the full suite).
5. On **first run**: each test writes its render to `Baselines.GPU/<name>.png` and **passes** (no comparison is done — the baseline is being seeded).
6. Inspect each PNG in `Baselines.GPU/` visually. They should look identical to the corresponding `Baselines/<name>.png` modulo GPU anti-aliasing and alpha-premultiplication differences.
7. Commit the `Baselines.GPU/*.png` files.

**To overwrite a baseline after a deliberate visual change:**

```
WEVA_REGENERATE_GOLDENS=1
```

Set this environment variable before launching the Unity Editor (or in a batch file), then run the tests. Every `GpuGoldenAssert.Match` call will overwrite the baseline file rather than compare against it.

---

## Failure diagnosis

When a GPU golden test fails, two artifacts appear in `Out.GPU/`:

- `<name>.actual.png` — what the GPU rendered this run.
- `<name>.diff.png` — per-pixel diff (red = differing pixels, faded original = matching pixels).

Compare `actual.png` against `Baselines.GPU/<name>.png` and `Baselines/<name>.png` (software):

| actual matches software | actual matches GPU baseline | Diagnosis |
|-------------------------|-----------------------------|-----------|
| Yes | No | GPU regression since last baseline commit (shader / batcher / keyword bug). |
| No | No | Paint regression AND a GPU regression, or snippet changed without updating baselines. |
| No | Yes | Software renderer regressed (SoftwareRasterizer drift). |
| Yes | Yes | Should not happen (both pass). |

---

## Adding a new golden

1. Add `<N>-<name>.html` (and optional `<N>-<name>.css`) to `Snippets/`.
2. Add a test to `GoldenSuiteTests.cs` calling `GoldenAssert.Match(...)`.
3. Add a GPU test to `GameUIGpuGoldenTests.cs` calling `GpuGoldenAssert.Match(...)`.
4. Run `dotnet test` in `Tools/TestVerifyAll` — this seeds the software baseline.
5. Run `GameUIGpuGoldenTests` in Unity Play mode — this seeds the GPU baseline.
6. Inspect both baselines visually and commit.
