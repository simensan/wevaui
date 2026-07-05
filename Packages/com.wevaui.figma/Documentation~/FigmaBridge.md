# Weva Figma Bridge — reference

## Architecture

The bridge is split so the substantive logic is engine-independent and tested
without Unity:

```
Runtime/  (Weva.Figma.Runtime, noEngineReferences — pure C#)
  Json/        hand-rolled JSON reader
  Model/       FigmaNode + paint/effect/text-style/primitives
  Mapping/     LayoutMapper, StyleMapper, NameAnnotations, FigmaDocumentExporter
  Tokens/      FigmaVariables, VariablesToCss
  Linting/     SubsetLinter
  RoundTrip/   FigmaOverlay, OverlayExtractor
  Client/      FigmaApiRoutes/Url/Responses/NodeQuery, IFigmaHttp, IExportSink
  Import/      FigmaImportService (Import + ImportLocal)
  Fidelity/    ImageDiff, FidelityReport

Editor/   (Weva.Figma.Editor, Editor-only) — thin Unity adapters
  Client/EditorFigmaHttp (WebClient), AssetFolderSink
  FigmaImportWindow, FigmaJsonImportMenu, Fidelity/FigmaFidelityChecker

FigmaPlugin~/   TypeScript Figma plugin (tokenless export)
```

The Editor layer only supplies `IFigmaHttp` (HTTP) and `IExportSink` (disk) and
wires the engine's `GoldenRunner` for fidelity; all decisions live in the core.

## Figma → CSS mapping

| Figma | CSS |
|---|---|
| Auto Layout H/V | `display:flex` + `flex-direction:row|column` |
| `primaryAxisAlignItems` | `justify-content` (MIN omitted, SPACE_BETWEEN supported) |
| `counterAxisAlignItems` | `align-items` (MIN→flex-start emitted to beat CSS's stretch default) |
| `itemSpacing` | `gap` (omitted under SPACE_BETWEEN) |
| `padding*` | `padding` shorthand |
| Fixed / Hug / Fill (main axis) | fixed size / auto / `flex-grow:1; flex-basis:0%; min:0` |
| Fixed / Hug / Fill (cross axis) | fixed size / auto / `align-self:stretch` |
| Non-auto-layout frame | `position:relative`; children `position:absolute` from constraints |
| Solid fill | `background-color` (paint opacity folds into alpha) |
| Linear / radial gradient | `linear-gradient()` / `radial-gradient()` |
| Image fill | `background-image:url("images/<ref>.png")` + raster request |
| Stroke (solid) | `border` (or per-side from `individualStrokeWeights`) |
| `cornerRadius` / `rectangleCornerRadii` | `border-radius` (uniform or 4-value) |
| ELLIPSE | `border-radius:50%` |
| Drop/inner shadow | `box-shadow` (inset for inner) |
| Layer blur / background blur | `filter:blur()` / `backdrop-filter:blur()` |
| Text style | `color`, `font-family/size/weight/style`, `line-height`, `letter-spacing`, `text-align/transform/decoration` |
| Vector/boolean/star/line | rasterized to PNG, referenced as `background-image` |

Output prepends `* { box-sizing: border-box; }` so widths match Figma's
border-box geometry. Every element is stamped `data-figma-id`.

## Annotation grammar

Directives are tokens in a layer name; the rest becomes the CSS class.

- `{{ Expr }}` — bound text (may contain spaces).
- `#id` — element id.
- `<tag>` — element tag override.
- `@event=Handler` — `on-event="Handler"` (e.g. `@click`, `@change`, `@input`).
- `.class?Expr` — `data-class-class="Expr"`.
- `*each=Collection:item:Key` — wrap the first child in
  `<template data-each="Collection as item" data-key="Key">` and drop the rest.

A token that starts with a sigil but doesn't match its grammar stays as plain
name text (so `.5` or a lone `@` won't be mistaken for a directive).

## Round-trip

The design owns HTML structure + CSS and regenerates every export. The developer
owns a `FigmaOverlay` — a `data-figma-id`-keyed set of tag/id/text/attribute
overrides persisted as a JSON sidecar and re-applied on export, so hand edits
survive. `OverlayExtractor` rebuilds the overlay from edited HTML.

## v1 simplifications

- Only the topmost visible fill becomes the background (no multi-layer stacks);
  the linter warns on multi-fill.
- Gradient strokes are dropped; angular/diamond gradients aren't exported (linter
  warns). The plugin path defaults gradient angle to top→bottom; the REST path is
  precise.
- Rotation, masks, and blend modes aren't exported (linter warns).
- `strokeAlign` is treated as INSIDE (matches `box-sizing:border-box`).
- Mixed per-character text styles export as the base style.
- Constraint mapping pins one/two edges; SCALE/CENTER are approximated.
