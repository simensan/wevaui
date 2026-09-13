# Weva

HTML and CSS for Unity. AI-friendly UI layer that produces working Unity UI
from HTML and CSS that LLMs have already learned from the web — no UXML
dialect, no `-unity-` prefixes.

The engine is [`libweva/`](./libweva/): a C++ core that parses, cascades,
lays out and paints, with text left to the host, checked against headless
Chrome. Two hosts sit over its C ABI in [`hosts/`](./hosts/) — a Godot addon
and the native Unity plugin the package in
[`Packages/com.wevaui/`](./Packages/com.wevaui/) is built on. `CMakeLists.txt`
and `check.sh` at the root build and gate everything; [`docs/`](./docs/)
holds the architecture notes and verification receipts.

The C# in the package is the Unity host layer — the counterpart of the Godot
addon's GDScript layer: the `WevaDocument` component, `[UIBind]` controllers,
the URP pass that draws the core's draw list, `FontEngine` as the font
backend, the Input System feed, editor tooling. The C# engine that 0.1.x
shipped was frozen and deleted on 2026-09-13.

## Install

Add via **Package Manager ▸ + ▸ Add package from git URL…**:

```
https://github.com/simensan/wevaui.git?path=Packages/com.wevaui
```

Pin a release with a `#v*` tag suffix. Then read the package
[`README.md`](./Packages/com.wevaui/README.md) and
[`Documentation~/getting-started.md`](./Packages/com.wevaui/Documentation~/getting-started.md).

## Documents

* **[`Packages/com.wevaui/README.md`](./Packages/com.wevaui/README.md)** —
  what the package is, quick start, supported subset, API surface.
* **[`Packages/com.wevaui/Documentation~/`](./Packages/com.wevaui/Documentation~/)** —
  getting started, the authoring guide, supported HTML/CSS, fonts,
  troubleshooting, API stability.
* **[`AI_REFERENCE.md`](./AI_REFERENCE.md)** — the orientation document for an
  AI agent asked to use, integrate or reason about Weva.
* **[`AGENTS.md`](./AGENTS.md)** — the engineering contract for an AI tool
  changing the engine or a host.
* **[`docs/ARCHITECTURE.md`](./docs/ARCHITECTURE.md)** — the core as built;
  **[`docs/PRODUCT_READINESS.md`](./docs/PRODUCT_READINESS.md)** — where each
  host stands; **[`docs/INPUT_PARITY.md`](./docs/INPUT_PARITY.md)** — the two
  hosts' input decisions, pinned by tests.
* **[`PLAN.md`](./PLAN.md)** — the design the core implements (history);
  **[`CONFORMANCE.md`](./CONFORMANCE.md)** — spec deltas, property by property.

## Target environment

* Unity **6000.3** or newer
* **URP** render pipeline, **Linear** color space
* **IL2CPP** scripting backend (Mono also supported)
* **Input System** package (`com.unity.inputsystem`)
* Native plugin: **Windows x64** (other platforms as their CI jobs go green)

## Layout

```
weva/
├── libweva/                     The engine: C++ core, C ABI (include/weva_c.h), tests
├── hosts/
│   ├── godot/                   Godot GDExtension addon + scene tests
│   └── unity/                   Unity plugin build, binding generator, notices
├── Packages/com.wevaui/         The UPM package — the Unity host
│   ├── Runtime/Native/          NativeDocument, font backend, renderer, input feed, bindings
│   ├── Runtime/WevaDocument.cs  The component
│   ├── Runtime/Rendering/       URP renderer feature + pass
│   ├── Editor/                  Inspector, Elements window, setup, importers, hot reload
│   ├── Tests/                   EditMode (Native) and PlayMode tests
│   └── Documentation~/          Consumer docs
├── Assets/                      Dev project: sample pages (UI/), scenes, controllers
├── Tools/
│   ├── oracle/                  Chrome captures, chrome_sweep gate, behaviour checks
│   ├── Layout/                  Chrome capture tooling (puppeteer)
│   ├── weva_dump / weva_render / weva_bench   Core CLIs
│   └── RenderGoldens/           GPU golden harness
├── docs/                        Architecture, readiness, receipts
├── CMakeLists.txt, check.sh     Build and the gate
├── AGENTS.md                    AI-tool contract
└── AI_REFERENCE.md              AI orientation
```

## License

MIT — see [`LICENSE.md`](./LICENSE.md).

Third-party components and their licences are listed in
[`THIRD_PARTY_NOTICES.md`](./THIRD_PARTY_NOTICES.md). The engine's native
libraries statically link ICU, the generated Unicode tables, Blink's WTF
Decimal and the ada URL parser; the UPM package ships those notices in its
own `Third Party Notices.md`, and the Godot addon bundles them in its zip.
