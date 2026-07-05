# Weva Export — Figma plugin

A small Figma plugin that exports the selected frame to **Weva-bridge JSON**
(plus local variables and rasterized images) entirely on-device — no Figma REST
token or paid plan required. The companion Unity menu turns the JSON into a
Weva HTML+CSS document.

This folder ends in `~` so Unity ignores it. It is the same artifact whether
shipped inside the package or extracted to its own repo.

## Build

```
cd Packages/com.wevaui.figma/FigmaPlugin~
npm install
npm run build        # compiles code.ts -> code.js
```

## Load in Figma

Figma desktop → **Plugins ▸ Development ▸ Import plugin from manifest…** → pick
`manifest.json` in this folder. Run it from **Plugins ▸ Development ▸ Weva
Export**.

## Use

1. Select a frame or component (or run with nothing selected to grab the first
   top-level frame on the page).
2. **Export selection**. Download the `*.figma.json`, the `*.variables.json`
   (if you use Figma Variables), and the image(s).
3. In Unity: **Assets ▸ Weva ▸ Import Figma JSON…**, pick the
   `*.figma.json`. The bridge writes `name.html`, `name.css`, `tokens.css`, and
   (if you placed them in an `images/` subfolder) references your images.

## Annotate dynamics in layer names

The exporter reads directives from layer names to bridge the static design to
Weva's dynamic markup:

| In the layer name | Becomes |
|---|---|
| `Player {{ PlayerName }}` | bound text `{{ PlayerName }}` |
| `Card #stage-card` | `id="stage-card"` |
| `Play <button>` | a `<button>` element |
| `Play @click=OnPlay` | `on-click="OnPlay"` |
| `Card .selected?IsSelected` | `data-class-selected="IsSelected"` |
| `List *each=Stages:stage:Number` | first child wrapped in `<template data-each="Stages as stage" data-key="Number">` |

Everything else in the name becomes the element's CSS class.

## What's approximated

The plugin path defaults gradient angles to top→bottom (the REST import path
produces precise angles from gradient handles). Mixed per-character text styles
export as the base style. Run the in-Unity subset linter (shown on import) to
see what falls outside the Weva subset.
