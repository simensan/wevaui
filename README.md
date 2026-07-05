# Weva

HTML and CSS for Unity. AI-friendly UI layer that produces working Unity UI
from HTML and CSS that LLMs have already learned from the web — no UXML
dialect, no `-unity-` prefixes.

The actual package lives in [`Packages/com.wevaui/`](./Packages/com.wevaui/).
This repo also contains the demo project (`Assets/`), the headless tooling
(`Tools/`), and the design / spec docs at the root.

## Install

Add via **Package Manager ▸ + ▸ Add package from git URL…**:

```
https://github.com/simensan/wevaui.git?path=Packages/com.wevaui#v0.1.1
```

or add it to `Packages/manifest.json` directly:

```json
{
  "dependencies": {
    "com.wevaui": "https://github.com/simensan/wevaui.git?path=Packages/com.wevaui#v0.1.1"
  }
}
```

Drop the `#v0.1.1` suffix to track `main` instead of a pinned release
(releases are tagged `v*`).

## Where to start

* **[Package README](./Packages/com.wevaui/README.md)** — install
  instructions, supported HTML/CSS subset, architecture, performance numbers,
  API surface, DevTools.
* **[Authoring guide](./Packages/com.wevaui/Documentation~/AuthoringGuide.md)**
  — practical cookbook for building UI: data binding with `[UIBind]`, events,
  forms, gestures, virtualized lists, components, layout patterns, theming,
  hot reload.
* **[`AI_REFERENCE.md`](./AI_REFERENCE.md)** — orientation map for AI agents
  *using* the library: capability audit (what it can/can't do, incl. GPU-render
  limits), integration quickstart, architecture map, tooling, and a "which doc
  for which task" index.
* **[`AGENTS.md`](./AGENTS.md)** — guidance for AI coding tools (Claude Code,
  Cursor, Copilot) when modifying the engine itself: pipeline overview, cache
  invariants, conventions, things never to do.
* **[`PLAN.md`](./PLAN.md)** — locked architectural decisions and roadmap.
* **[`CONFORMANCE.md`](./CONFORMANCE.md)** — spec-vs-impl deltas, property by
  property.

## Target environment

* Unity **6000.3** or newer
* **URP** render pipeline, **Linear** color space
* **IL2CPP** scripting backend (Mono also supported)
* **Input System** package (`com.unity.inputsystem`)

## Layout

```
weva/
├── AGENTS.md                    AI-tool contract
├── PLAN.md                      Design + roadmap
├── CONFORMANCE.md               Spec deltas
├── Assets/                      Demo project + sample assets
│   ├── UI/                      randhtml.html / .css — the dev demo
│   ├── Scripts/                 Demo controllers
│   └── Settings/                URP renderer + pipeline assets
├── Packages/com.wevaui/        UPM package — the engine itself
│   ├── Runtime/                 Headless-testable core (HTML, CSS, layout, paint)
│   │   ├── Rendering/URP/       URP renderer feature + batched über-shader
│   │   └── Forms/               Inputs, range slider, tooltip, context menu, …
│   ├── Tests/                   ~10,500 NUnit tests (EditMode + PlayMode)
│   ├── Editor/                  Preview window, asset importers
│   └── Documentation~/          Authoring guide
└── Tools/
    ├── BaselineGen/             Headless layout dump + Chrome compare
    └── PerfBench/               Cascade / layout / paint benchmarks
```

## License

MIT — see [`LICENSE.md`](./LICENSE.md).
