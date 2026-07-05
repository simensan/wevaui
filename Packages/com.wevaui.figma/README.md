# Weva Figma Bridge (`com.wevaui.figma`)

Turns Figma designs into **Weva-conformant HTML/CSS**. Because Weva speaks
standard HTML/CSS, "Figma integration" reduces to *"Figma → the Weva
subset"* — this package is that mapping, plus the tooling around it: design-token
sync, subset linting, an in-Editor importer, a tokenless Figma plugin, and a
fidelity diff.

> **Optional & strippable by design.** This package depends on `com.wevaui`,
> but nothing in `com.wevaui` depends on it. Remove it by deleting
> `Packages/com.wevaui.figma/` (and `Tools/FigmaVerify/`). The portable core is
> pure C# (`noEngineReferences`), so it also runs and tests outside Unity.

## What it does

| Capability | Where |
|---|---|
| Auto Layout → flexbox, constraints → absolute, Fixed/Hug/Fill → sizing | `Mapping` |
| Fills/strokes/effects/corners/text → idiomatic CSS (shorthands) | `Mapping` |
| Figma Variables → CSS custom properties (`:root` + theme modes) | `Tokens` |
| Layer-name annotations → `{{ }}` / `on-*` / `data-class-*` / `#id` / `<tag>` / `data-each` | `Mapping` |
| Round-trip overlay: hand-edits survive re-export (keyed on `data-figma-id`) | `RoundTrip` |
| Subset linter: flags what won't export faithfully | `Linting` |
| Fidelity diff: render vs Figma PNG, pixel-compare | `Fidelity` + Editor |
| REST import (token) and tokenless plugin export | `Client`/`Import` + `FigmaPlugin~` |

## Two ways to import

**A. In-Editor (Figma REST token).** `Window ▸ Weva ▸ Figma Import`: paste a
frame URL (or file key), a [personal access token](https://www.figma.com/developers/api#access-tokens),
pick an output folder, **Import**. Writes `name.html` + `name.css` (+ `tokens.css`
+ images), and the engine hot-reloads any live `UIDocument`.

**B. Tokenless plugin.** Build & run the Figma plugin in `FigmaPlugin~/`
(see its README), **Export selection**, then in Unity: `Assets ▸ Weva ▸
Import Figma JSON…`. No token or paid plan required.

## Design tokens

```csharp
using Weva.Figma.Tokens;
TokenCssResult result = VariablesToCss.Build(variablesJson); // GET /v1/files/:key/variables/local
File.WriteAllText("Assets/UI/tokens.css", result.Css);
```

COLOR→`rgb()/rgba()`, FLOAT→`px`/unitless by scope, alias→`var(--…)`, modes →
`:root` (default) + `@media (prefers-color-scheme)` (light/dark) + `[data-theme]`.

## Annotate dynamics in layer names

A Figma frame is one frozen state; Weva is templated and data-bound. Bridge
the gap by naming layers with directives:

| Layer name | Output |
|---|---|
| `Player {{ PlayerName }}` | bound text |
| `Card #stage-card` / `Play <button>` | `id` / tag override |
| `Play @click=OnPlay` | `on-click="OnPlay"` |
| `Card .selected?IsSelected` | `data-class-selected="IsSelected"` |
| `List *each=Stages:stage:Number` | first child wrapped in `<template data-each="Stages as stage" data-key="Number">` |

See [`Documentation~/FigmaBridge.md`](Documentation~/FigmaBridge.md) for the full
mapping reference, architecture, and v1 limitations.

## Running the tests (headless, no Unity)

```
dotnet run --project Tools/FigmaVerify          # all tests
dotnet run --project Tools/FigmaVerify Token    # filter by fixture name
```

The same files also run inside the Unity Test Runner via the
`Weva.Figma.Tests` assembly. The `Editor/` adapters + the `FigmaPlugin~/`
TypeScript are written but need validation in a real Unity/Figma session — see
`PLAN.md`.
