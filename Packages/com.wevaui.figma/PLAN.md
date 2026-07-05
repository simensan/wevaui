# Weva Figma Bridge — plan & progress tracker

A **strippable, optional** layer that turns Figma designs into Weva-conformant
HTML/CSS. Lives in its own embedded package (`com.wevaui.figma`) so it can be
deleted wholesale or extracted into a standalone plugin. Nothing in
`com.wevaui` references it.

## Design rules

- **Target the subset, not "HTML/CSS".** Output must fall inside the Weva
  conformance subset (see root `PLAN.md` §3–4) or be flagged by the linter. The
  engine has a full shorthand-expansion subsystem (`Css/Cascade/Shorthands/`),
  so the emitter writes idiomatic shorthands (`border`, `border-radius`,
  `box-shadow`, `background`, `padding`) matching the hand-authored CSS style.
- **Figma is border-box.** Figma width/height include border, so the emitter
  prepends `* { box-sizing: border-box; }` (Weva defaults to content-box).
- **Portable core.** Everything except the REST client and the Editor window is
  pure C# with `noEngineReferences: true` — it compiles and tests without Unity
  (mirrors the engine's "headless above the render backend" discipline) and runs
  via `Tools/FigmaVerify`.
- **Invariant culture everywhere.** All number parse/format uses
  `CultureInfo.InvariantCulture` (the engine is developed on a comma-decimal
  locale).
- **Deterministic output.** Same input → byte-identical HTML/CSS, so exports
  diff cleanly and feed golden tests.

## Parts

| # | Part | Module | Status |
|---|------|--------|--------|
| 1 | Hand-rolled JSON reader | `Runtime/Json` | ✅ done |
| 2 | Design tokens: Variables → CSS custom properties (+ theme modes) | `Runtime/Tokens` | ✅ done |
| 3 | Figma node model + JSON binding (FRAME/TEXT/RECTANGLE/VECTOR/COMPONENT/INSTANCE…) | `Runtime/Model` | ✅ done |
| 4 | Layout mapper: Auto Layout → flex; constraints → absolute | `Runtime/Mapping` | ✅ done |
| 5 | Style mapper: fills/strokes/effects/corner/text → CSS (idiomatic shorthands) | `Runtime/Mapping` | ✅ done |
| 6 | HTML + CSS emitters (deterministic, `data-figma-id` stamping) | `Runtime/Mapping` | ✅ done |
| 7 | Exporter orchestrator: node tree → {html, css, raster asset requests} | `Runtime/Mapping` | ✅ done |
| 8 | Subset linter (validate design against the conformance subset) | `Runtime/Linting` | ✅ done |
| 9 | Annotation conventions: layer names → `data-each` / `{{ }}` / `data-class-*` / `on-*` / `#id` / `<tag>` | `Runtime/Mapping` | ✅ done |
| 10 | Round-trip overlay: preserve hand-authored dynamic layer keyed on `data-figma-id` | `Runtime/RoundTrip` | ✅ done |
| 11 | REST client — pure routes/URL/response parsing + injectable import service (`Runtime/Client`, `Runtime/Import`); Editor `WebClient` adapter (`Editor/Client`) | core ✅ · adapter 🟡 |
| 12 | Editor import window + asset generation + hot-reload trigger | `Editor` | 🟡 needs Unity validation |
| 13 | Fidelity diff: pixel-diff core (`Runtime/Fidelity`) + Editor render-vs-Figma-PNG checker (`Editor/Fidelity`) | core ✅ · checker 🟡 |
| 14 | Figma plugin (TS): tokenless export to bridge JSON + images; Unity JSON-import menu (`Runtime/Import.ImportLocal`) | `FigmaPlugin~` + `Editor` | core ✅ · plugin 🟡 |
| 15 | Docs (README + `Documentation~/FigmaBridge.md`) + sample + final verification | — | ✅ done |

**v0.1 status:** all 15 parts implemented. The portable core is complete and
headless-tested (**125 tests green** via `Tools/FigmaVerify`). The `Editor/`
adapters + the `FigmaPlugin~/` TypeScript are written and await validation in a
real Unity/Figma session (see below) — the same bar the engine ships its
Unity-bridge skeletons at.

## Test harness

`Tools/FigmaVerify/` is a plain .NET 8 exe that compiles the portable core +
the test sources + a reflection NUnit runner (mirrors `Tools/TestVerifyAll`).
Run all Figma tests headless:

```
dotnet run --project Tools/FigmaVerify
```

## Unity validation pending

Everything in `Runtime/` (the portable core) is headless-tested. The `Editor/`
layer is written but **not yet compiled/run in Unity** — it needs validation in
a real Unity 6000.4 project:

- `Editor/Client/EditorFigmaHttp` — `WebClient`-based `IFigmaHttp` (verify
  `System.Net` HTTPS + token header behavior in the Editor).
- `Editor/Client/AssetFolderSink` — writes under the project folder.
- `Editor/FigmaImportWindow` — `Window ▸ Weva ▸ Figma Import` (verify IMGUI,
  `EditorPrefs` token storage, `AssetDatabase.Refresh`, progress bar).
- `Editor/Fidelity/FigmaFidelityChecker` — verify the `Texture2D` row-flip
  (Unity bottom-up vs GoldenRunner top-down) against a known reference.
- `Editor/FigmaJsonImportMenu` — `Assets ▸ Weva ▸ Import Figma JSON…`.
- `FigmaPlugin~/` — the TS plugin needs building (`npm i && npm run build`) and
  loading in Figma; verify the Plugin-API field reads and image export.

The core import logic (`Runtime/Import/FigmaImportService`) is tested with fake
HTTP + sink, so only the two thin adapters + the window need eyes-on in Unity.

## How to strip

Delete `Packages/com.wevaui.figma/` and `Tools/FigmaVerify/`. The engine is
unaffected (it never references this package).
