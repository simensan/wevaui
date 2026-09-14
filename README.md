# Weva

![CI](https://github.com/simensan/wevaui/actions/workflows/godot-ci.yml/badge.svg)

HTML and CSS as a game UI layer, for **Godot** and **Unity**. You write the
standard `.html` and `.css` that browsers — and the AI models trained on the
web — already know; one engine lays it out and paints it, and each game engine
draws the result through its own renderer. No UXML or USS dialect, no
`-unity-` or `-godot-` prefixes.

The rule the project is built on: **if a feature has a well-known web
behaviour, Weva matches it or does not ship it.** Conformance is measured
against headless Chrome, not against another implementation.

## Showcase

Pages from [`Assets/UI/`](./Assets/UI/), rendered by the engine (1280×720,
scaled) — plain `.html` and `.css`, no engine-specific markup:

| | |
|---|---|
| ![A glassmorphism dashboard: backdrop-filter, gradients, rounded panels](./docs/images/showcase-glass.png) | ![A match-3 board: grid, gradients, shadows, badges](./docs/images/showcase-match3.png) |
| `glass.html` — `backdrop-filter`, gradients, layered panels | `match3.html` — grid, badges, shadows |
| ![A stock dashboard: flex layout, candlestick chart, watchlist](./docs/images/showcase-stock-dashboard.png) | ![A game HUD: bars, stat cards, ability grid](./docs/images/showcase-hud.png) |
| `stock-dashboard.html` — flex, tables, sparklines | `hud.html` — bars, cards, an ability grid |

The same engine over a game in Godot: the [western survival
sample](./hosts/godot/project/samples/western_survival/)'s HUD, drawn by the
addon through a `Control` on top of the scene.

![The Godot host: a survival game's HUD drawn over the 3D scene](./docs/images/western-survival-hud.png)

## One engine, two hosts

- **[`libweva/`](./libweva/)** — the engine. C++17 behind a C ABI
  ([`weva_c.h`](./libweva/include/weva_c.h)): HTML and CSS parsing, the
  cascade, block / inline / flex / grid / positioned layout, forms and text
  editing, animation, data binding, and painting to textured triangle lists.
  Text is left to the host. Every sample page is checked against Chrome
  ([`Tools/oracle/`](./Tools/oracle/)); the core suite runs ~500,000 checks.
- **[`hosts/godot/`](./hosts/godot/)** — the Godot addon. A GDExtension
  (`WevaDocument` is a `Control`) with TextServer as the font backend, native
  GUI routing, gamepad navigation, IME, live reload. Windows and Linux; a
  standalone game integration lives in
  [`examples/frontier_camp/`](./examples/frontier_camp/).
- **[`Packages/com.wevaui/`](./Packages/com.wevaui/)** — the Unity package
  (UPM). The same core as a native plugin, `WevaDocument` as a MonoBehaviour,
  `[UIBind]` controllers, a URP render pass, `FontEngine` as the font backend,
  the Input System for input. Windows x64 in the package; macOS, Android and
  iOS binaries build in CI.

The C# in the Unity package and the GDScript-facing layer in the Godot addon
are hosts, not engines: they upload triangles, answer font callbacks and feed
input. The C# engine that the Unity package shipped as 0.1.x was deleted on
2026-09-13.

## Godot

Open `hosts/godot/project/project.godot` in Godot 4.7 for the sample gallery,
or install the packaged addon into your own project: extract a
`weva-<platform>.zip` (built by `hosts/godot/package_addon.py`, or the
`extension-build-*` artifact of a CI run) into the project root and run
`addons/weva/example/example.tscn`. No C# runtime is required.

```gdscript
var ui := WevaDocument.new()
ui.document_size = Vector2(640, 360)
ui.css = "body { color: white; background: #18202c } button { padding: 12px }"
ui.html = '<h1>{{ Player.Name }}</h1><button on-click="start_game">Start</button>'
ui.data = {"Player": {"Name": "Ada"}}
ui.set_controller(self)
add_child(ui)
```

Define `start_game(_id: String)` on the controller to handle the button.
Native anchors and Containers size the HTML viewport; native GUI routing owns
focus and overlapping controls; Tab traverses HTML and then native Controls;
the standard controls answer keyboard, gamepad and IME input.

- [Godot host guide](./hosts/godot/README.md) — build, package, integrate,
  drive a document from GDScript.
- [Keyboard behaviour](./docs/KEYBOARD_INPUT.md), [IME evidence and
  limits](./docs/IME.md), [text shaping on the stock
  engine](./docs/GODOT_TEXT_SHAPING.md).
- [Release verification](./docs/RELEASE.md) — pinned builds, versioned
  packaging, acceptance commands.

## Unity

Add via **Package Manager ▸ + ▸ Add package from git URL…**:

```
https://github.com/simensan/wevaui.git?path=Packages/com.wevaui
```

That tracks `main` (1.0.0); pin a release with a `#v*` tag suffix once one is
tagged. Unity 6000.3+, URP, the Input System.

```csharp
public sealed class MainMenu : MonoBehaviour {
    [UIBind] public int CoinCount;                                   // {{ CoinCount }}
    void Awake() => GetComponent<WevaDocument>().SetController(this);
    public void OnStart() => CoinCount++;                            // on-click="OnStart"
}
```

- [Package README](./Packages/com.wevaui/README.md) — quick start, supported
  subset, API surface.
- [Getting started](./Packages/com.wevaui/Documentation~/getting-started.md),
  the [authoring guide](./Packages/com.wevaui/Documentation~/AuthoringGuide.md),
  [supported HTML](./Packages/com.wevaui/Documentation~/supported-html.md) and
  [CSS](./Packages/com.wevaui/Documentation~/supported-css.md),
  [troubleshooting](./Packages/com.wevaui/Documentation~/troubleshooting.md),
  [API stability](./Packages/com.wevaui/Documentation~/api-stability.md).
- [Unity host notes](./hosts/unity/README.md) — the plugin build, the font
  backend, the renderer, the input feed.

## Where things stand

[`docs/PRODUCT_READINESS.md`](./docs/PRODUCT_READINESS.md) says, per host,
what is verified and what evidence is still missing. In short: a development
preview on both. Known gaps shared by both hosts: multi-column layout,
vertical writing modes, `@property`, View Transitions. The Unity host shapes
with its own OpenType layer (right-to-left order, Arabic joining, marks;
not Indic reordering or class-based contextual lookups) and does not
rasterise colour emoji; the Godot host's text is TextServer's.

## For engineers and AI agents

- [`AGENTS.md`](./AGENTS.md) — the contract for changing the engine or a
  host: the rules, the gate, the recipes.
- [`AI_REFERENCE.md`](./AI_REFERENCE.md) — orientation for an agent asked to
  use, integrate or reason about Weva.
- [`docs/ARCHITECTURE.md`](./docs/ARCHITECTURE.md) — the core as built and the
  ABI's history; [`docs/CONVENTIONS.md`](./docs/CONVENTIONS.md) — the C++ rules;
  [`docs/ORACLE.md`](./docs/ORACLE.md) — how Chrome guards the engine;
  [`docs/INPUT_PARITY.md`](./docs/INPUT_PARITY.md) — the two hosts' input
  decisions, each pinned by a test on both.
- [`PLAN.md`](./PLAN.md) — the design the core implements (history);
  [`CONFORMANCE.md`](./CONFORMANCE.md) — spec deltas, property by property.

`check.sh` is the gate — build, the core suite, sanitizers, the Chrome oracle,
the Unity plugin, the Godot extension, the host scenes — and what CI runs on
Ubuntu, Windows and macOS.

## Layout

```
weva/
├── libweva/                     The engine: C++ core, C ABI (include/weva_c.h), tests
├── hosts/
│   ├── godot/                   GDExtension addon, sample gallery, scene tests, packaging
│   └── unity/                   Unity plugin build, binding generator, notices
├── Packages/com.wevaui/         The Unity package (UPM)
├── examples/frontier_camp/      A standalone Godot game integration
├── Assets/                      The Unity dev project: sample pages (UI/), scenes
├── Tools/
│   ├── oracle/                  Chrome captures, chrome_sweep gate, behaviour checks
│   ├── Layout/                  Chrome capture tooling (puppeteer)
│   ├── weva_dump / weva_render / weva_bench   Core CLIs
│   └── RenderGoldens/           GPU golden harness
├── third_party/                 ICU, Unicode tables, WTF Decimal, ada
├── docs/                        Architecture, readiness, receipts
├── CMakeLists.txt, check.sh     Build and the gate
└── AGENTS.md, AI_REFERENCE.md   For engineers and agents
```

## License

MIT — see [`LICENSE.md`](./LICENSE.md). Third-party components and their
licences are in [`THIRD_PARTY_NOTICES.md`](./THIRD_PARTY_NOTICES.md): the
engine statically links ICU, the generated Unicode tables, Blink's WTF Decimal
and the ada URL parser; the Unity package ships those notices in its own
`Third Party Notices.md`, the Godot addon bundles them in its zip.
