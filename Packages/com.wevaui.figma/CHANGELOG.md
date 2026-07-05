# Changelog

All notable changes to the Weva Figma Bridge are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [Unreleased]

### Docs
- Comprehensive README + `Documentation~/FigmaBridge.md` reference (architecture,
  full Figma→CSS mapping, annotation grammar, v1 limitations) and a
  `Samples~/FigmaImportDemo` (hero card) guarded by `HeroCardDemoTests`.

### Added
- Hand-rolled, dependency-free JSON reader (`Weva.Figma.Json`) scoped to the
  Figma REST/plugin payload shape.
- Design-token pipeline: Figma Variables → CSS custom properties
  (`Weva.Figma.Tokens`), with theme modes mapped to `:root`,
  `[data-theme="…"]` blocks, and optional `@media (prefers-color-scheme)` for
  light/dark-named modes. Resolves `VARIABLE_ALIAS` values to `var(--…)`.
- Figma node model (`Weva.Figma.Model`) — FRAME/GROUP/COMPONENT/INSTANCE/
  TEXT/RECTANGLE/VECTOR with Auto Layout, constraints, fills, strokes, effects,
  corners, and text style.
- Node → HTML/CSS mapping (`Weva.Figma.Mapping`): Auto Layout → flexbox,
  constraints → absolute positioning, Fixed/Hug/Fill sizing → fixed size /
  `flex-grow` / `align-self`, and paint → `background`/`border`/`border-radius`/
  `box-shadow`/`backdrop-filter`/typography. `FigmaDocumentExporter` emits a
  deterministic HTML fragment + stylesheet (one class per node, every element
  stamped `data-figma-id`) and records image/vector assets needing rasterization.
  Ellipses map to `border-radius: 50%`.
- Subset linter (`Weva.Figma.Linting`) — flags designs that fall outside the
  Weva subset before export: vectors/images to rasterize, unsupported blend
  modes, multi-layer fills, angular/diamond gradients, gradient strokes,
  rotation, masks, mixed text styles, and unsupported node types.
- Layer-name annotations (`Weva.Figma.Mapping.NameAnnotations`) bridge static
  design to dynamic markup: `{{ binding }}`, `#id`, `<tag>` override,
  `@event=Handler` → `on-*`, `.class?Expr` → `data-class-*`, and
  `*each=Coll:item:Key` → a `<template data-each>` wrapping the first child
  (duplicates dropped).
- Round-trip overlay (`Weva.Figma.RoundTrip`) — a developer-owned dynamic
  layer keyed by `data-figma-id`, persisted as a JSON sidecar and re-applied on
  every export so hand-added tags/ids/bindings/attributes survive design
  regeneration. `OverlayExtractor` reconstructs the overlay from edited HTML
  (export → edit → re-extract → reapply). Exporter de-dups attributes so an
  overlay cleanly overrides an annotation-derived one.
- REST layer (`Weva.Figma.Client`) — endpoint routes, Figma-URL/key parsing,
  and response→model parsing, plus `IFigmaHttp`/`IExportSink` abstractions and
  `HtmlDocumentTemplate`. `Import/FigmaImportService` runs fetch → lint → export
  → write end to end over those interfaces (headless-tested with fakes).
- Editor (`Weva.Figma.EditorTools`, *needs Unity validation*) — `WebClient`
  HTTP adapter, project-folder asset sink, and a `Window ▸ Weva ▸ Figma
  Import` window that imports a frame as an HTML+CSS pair (+ tokens + images)
  and refreshes the AssetDatabase so the engine hot-reloads it.
- Fidelity diff: portable RGBA `ImageDiff` + `FidelityReport`/thresholds/verdict
  in `Weva.Figma.Fidelity` (headless-tested), and an Editor
  `FigmaFidelityChecker` (*needs Unity validation*) that renders the export
  through the engine's `GoldenRunner` rasterizer and pixel-diffs it against the
  frame's Figma reference PNG, optionally writing a diff heatmap.
- Tokenless path: `FigmaImportService.ImportLocal` (headless-tested) imports a
  pre-parsed node + variables JSON without any network. `FigmaPlugin~/` is a
  TypeScript Figma plugin that exports the selected frame to bridge JSON +
  variables + base64 images on-device (no token), translating Plugin-API
  constraints to REST form and matching `RasterNaming` filenames. Editor
  `Assets ▸ Weva ▸ Import Figma JSON…` consumes that export.
