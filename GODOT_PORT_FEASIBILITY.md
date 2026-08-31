# Godot port feasibility

**Target:** pure C++ GDExtension, usable from any Godot project and callable
from GDScript. No C#, no .NET dependency.

**Answer:** feasible, and GDExtension is the only route that actually meets that
goal — but it is a different and much larger project than a C# port. Roughly
**4–8× the effort**, because nothing recompiles: the ~108k LOC engine-neutral
core has to be translated by hand, and the ~183k LOC test suite that currently
guards it does not come with it.

The good news is that this codebase is about as C++-portable as idiomatic C#
ever gets, and there is a clean way to keep the existing tests useful as an
oracle rather than throwing them away.

---

## 1. Why GDExtension is the right call

C# in Godot only runs in .NET builds of the engine, isn't available to GDScript
users without friction, and — as noted in the earlier C# analysis — has no web
export. A C++ GDExtension:

- loads into **every** Godot build, including the standard non-.NET one;
- exposes classes to GDScript automatically via `GDCLASS` + `_bind_methods()`,
  so `WevaDocument` becomes an ordinary node in the scene tree;
- **does** support web export (GDExtension web builds landed in Godot 4.3, with
  the usual threading caveats) — strictly better than the C# path here;
- ships as a per-platform shared library plus a `.gdextension` file, dropped
  into `addons/`.

The costs are the usual native ones: a build matrix (Linux/Windows/macOS/Android/
iOS/web, ×arch, ×debug/release), godot-cpp ABI tracking across Godot minor
versions, and no exceptions across the extension boundary.

## 2. How C++-friendly is the existing core?

Measured over the 595 files / 108,151 LOC that already compile without Unity
(`Tools/BaselineGen`, `PerfBench`, `TestVerifyAll`):

**Almost everything that makes C# hard to translate is absent.**

| Feature | Files using it | Verdict |
|---|---:|---|
| LINQ | **0** | The single biggest win. Nothing to unwind. |
| `dynamic` keyword | **0** | All 6 grep hits are the word in comments. |
| Generic type declarations | **4** | Nearly no template work. |
| `System.Reflection` | 5, all in `Runtime/Binding/` | See §4. |
| `Regex` | 2 | `SelectorMatcher`, `CssImportFlattener`. |
| `async`/`Task` | 6 | Image loading + `@import`. Maps to callbacks. |
| `Span<T>` | 8 | → `std::span` / `string_view`. |
| `unsafe` / `stackalloc` | 4 / 1 | Already pointer code. |
| `Marshal`/`GCHandle` | 0 | No interop assumptions to unpick. |

The shape of the work is therefore *volume*, not *difficulty*. What you're
translating:

```
563 classes   142 structs   24 interfaces   121 enums     (~850 types)
1249 List<    332 Dictionary<   126 HashSet<   101 Stack<
169 Func<     76 event         36 Action<      33 delegate
```

Those map onto `std::vector`, `unordered_map`, `unordered_set`, `vector`-backed
stacks, and `std::function` / small interface vtables with no cleverness
required. The 121 enums and much of the CSS property machinery are tables —
mechanical translation.

### The four things that genuinely need design decisions

1. **509 `throw new` sites.** Throwing across a GDExtension boundary is
   undefined; Godot's own code is exception-averse. These become an error-return
   discipline (`bool Try...(out)` is already the dominant idiom here — 813
   `TryParse`-family call sites — so the codebase leans that way already) plus
   `ERR_FAIL_*` macros at the boundary. Mechanical but pervasive; decide the
   convention on day one.

2. **Ownership.** C# leans on the GC for the DOM tree, the box tree, paint
   lists, and the caches between them. C++ needs an explicit model. The codebase
   already points at the answer: `ElementToBoxIndex` and `PaintListPool` show
   the design is index/pool-oriented rather than pointer-chasing. Arena + stable
   indices for the box and paint trees, intrusive refcount (`Ref<>`) only for
   the DOM nodes GDScript can hold.

3. **String handling.** 1,189 `string.*` calls and 268 `Substring` calls,
   concentrated in the CSS/HTML parsers. `std::string_view` is faster than the
   C# original *and* a lifetime hazard — slices must not outlive the source
   buffer. Pin the input-buffer ownership rule before porting the parsers.
   Number parsing must use `std::from_chars`, never `strtod` (locale).

4. **`double` everywhere.** The layout engine computes in `double` and the
   goldens depend on exact rounding (`Math.Round`, `MidpointRounding.AwayFromZero`).
   C++ `std::round` matches that, but `printf`/`from_chars` round-tripping and
   any `-ffast-math` will not. Compile the layout core without fast-math and
   assert bit-identical dumps against the C# oracle (§5).

## 3. Scope

| Component | LOC | Notes |
|---|---:|---|
| Engine-neutral core → C++ | 108,151 | Hand translation. The bulk. |
| Renderer backend | — | Written fresh for Godot regardless of language |
| Text stack | — | Written fresh; now can use FreeType/HarfBuzz directly |
| Input / clipboard / IME / images | — | Godot `Input`, `DisplayServer`, `Image` |
| Host node + GDScript bindings | new | See §4 |
| Shaders | 3,904 | HLSL → Godot shading language, unchanged from the C# plan |
| Editor tooling | 5,910 | Rewrite as a Godot `EditorPlugin` |

Note that the ~24k LOC of Unity-bound backend code identified in the C# analysis
was *always* going to be rewritten. Choosing C++ doesn't add that cost — it adds
the 108k LOC core that C# would have gotten for free.

The largest single files give a sense of the density:
`BoxToPaintConverter.cs` 4,302 · `FlexLayout.cs` 3,261 · `CascadeEngine.cs`
2,576 · `PositioningPass.cs` 2,038 · `InlineLayout.cs` 1,973 · `CssValueParser.cs`
1,866 · `GridLayout.cs` 1,801. These are spec-implementation files where the
value is in the accumulated edge cases, not the structure. Translation is
line-by-line and the bugs you introduce are subtle.

**Honest effort estimate:** sustained mechanical translation of intricate,
spec-driven code runs maybe 500–1,500 LOC/day including debugging. That puts the
core alone at ~100–200 person-days, and the differential-debugging tail on a
layout engine is historically where the schedule goes — call it **12–24
person-months to parity**, renderer and text stack on top. Transpilation tools
exist but will not produce a codebase you can keep developing in; don't.

## 4. What GDScript actually sees

Do **not** bind all 637 public types. The GDScript-facing surface is small:

- `WevaDocument` (a `Control`/`Node2D`) with exported properties mirroring
  today's: `document`, `stylesheets`, `sorting_order`, `viewport_override`,
  `prefers_dark_color_scheme`, plus `rebuild()`.
- `Document`, `Element`, `Node`, `TextNode` from `Runtime/Dom/` — query,
  attribute get/set, class list, text.
- Signals for the event system (`pressed`, `input_changed`, …) replacing the
  76 C# `event` declarations at the boundary.
- One `set_style_property` / `get_computed_style` pair.

Everything else stays internal C++.

**The `[UIBind]` data-binding system does not port — and shouldn't.** All the
reflection in the codebase lives in `Runtime/Binding/` (5 files) and exists to
read C# object graphs. In a GDScript world that whole feature is replaced by
Godot's native introspection: `Object::get()`/`set()` over `Variant`, plus
`Object::connect` for events. This is a *better* fit than the current design,
and it deletes the reflection problem outright. Note the codebase already has
the reflection-free shape sketched out in
`Runtime/Binding/Generated/IBindingAccessor.cs` — same idea, different host.

## 5. The real risk: the test suite does not come with you

This is the part worth thinking hardest about.

`Tests/` is **183,194 LOC across ~10,500 NUnit tests** — 60k LOC on layout
alone, 52k on CSS, 23k on paint. Only ~143 data fixtures (47 HTML, 46 CSS, 50
JSON) are data-driven; the rest are hand-written asserts in C#. They do not
translate cheaply, and a layout engine without them is not trustworthy.

**Mitigation — use the C# implementation as a differential oracle.**
`Tools/BaselineGen/LayoutDump.cs` already emits a stable JSON dump of every box
(`tag/id/cls/x/y/w/h/depth`) for a given HTML+CSS at a given viewport. That is
exactly the right oracle format. So:

1. Harvest every HTML/CSS snippet the C# tests exercise into a corpus, and
   generate one more from fuzzing property combinations.
2. Run the C# engine over the corpus → reference JSON dumps.
3. Run the C++ engine over the same corpus → diff.

That converts an untranslatable 183k LOC into an automated, growing regression
net, and it lets you port bottom-up with a green signal at every step:
**CSS tokenizer/parser → values → cascade/selectors → block → inline/text →
flex → grid → positioning → paint conversion.** Each stage is validated against
the oracle before the next begins. Do not start the renderer until layout dumps
match.

The paint layer has a second oracle: `SoftwareRasterizer` (1,592 LOC, zero
Unity refs) plus 38 baseline PNGs — port it early as the first C++ backend and
diff images.

## 6. The strategic question: one core or two?

A C++ port creates a fork. Every CSS fix would otherwise land twice, forever —
and with 858 lines of `CONFORMANCE.md` tracking spec deltas, that divergence
gets expensive fast.

There is a way out that is worth deciding **before** starting, because it
changes the design: **Unity can P/Invoke a native library.** So the end state
doesn't have to be "C# engine + C++ engine". It can be:

```
            ┌─────────────────────────────┐
            │  libweva  (C++ core)        │
            │  parse · cascade · layout   │
            │  paint display list         │
            └───────┬─────────────┬───────┘
                    │             │
        GDExtension │             │ P/Invoke + C ABI
                    │             │
              Godot host     Unity host (thin C# shim)
```

One implementation, two host bindings, one place to fix a spec bug. It costs
more up front — a stable C ABI and marshalling for the Unity side, and the
existing C# package becomes a shim — but it is the difference between a port
and a permanent second codebase. If the C++ port happens, this is the version
worth doing.

If the Unity package is instead going to be frozen or retired, ignore this and
port straight to GDExtension.

## 7. Prior art: what already exists for Godot

Checked August 2026. There is no shortage of "HTML/CSS UI in Godot" projects,
but they fall into three groups and none of them occupy Weva's position.

### Group 1 — Webview wrappers (real HTML/CSS, not really UI)

[**Godot WRY**](https://github.com/doceazedo/godot_wry) (MIT, native system
webview) and [**godot-webview**](https://godotwebview.com/) (Chromium/Qt,
LGPL-3.0, now free under an indie license) embed a browser and show a web page.

Full CSS fidelity, obviously — it *is* a browser. But it is a separate rendering
surface: it does not composite with game content, does not live in the scene
tree as Control nodes, carries a browser's memory footprint, has input/focus
friction, and is desktop-only (Windows/Mac/Linux — no consoles, no meaningful
mobile). Fine for a launcher, a settings panel, or a tool. Not an in-game HUD.

### Group 2 — Ultralight wrapper

[**Godot-HTML**](https://github.com/Decapitated/Godot-HTML) (LGPL-3.0, 125
stars, Godot 4.3+) wraps [Ultralight](https://ultralig.ht/), a WebKit-derived
renderer. Real CSS and JS, much lighter than Chromium.

The blocker is commercial: Ultralight is free under $100K annual revenue, then
**$3,000/year per application**, and the standard tier is PC-only (consoles need
enterprise licensing). The Godot binding additionally lists no GPU-accelerated
rendering, no WebP, and no HTML5 video/audio.

### Group 3 — Native reimplementations (Weva's actual category)

[**RmlUi**](https://github.com/mikke89/RmlUi) — MIT, 4.4k stars, ~2,740 commits.
The serious one, and the closest thing to prior art for exactly what §1–6
proposes: a mature C++ HTML/CSS-like UI library that emits vertices, indices and
draw commands for you to render. There is a
[**Godot-RmlUi**](https://github.com/ashifolfi/Godot-RmlUi) GDExtension, but at
11 stars / 19 commits it is explicitly WIP — its own TODO still lists GDScript
element modification, the font system, and editor rendering.

**RmlUi is more capable than its README summary suggests.** Reading the actual
[RCSS property index](https://mikke89.github.io/RmlUiDoc/pages/rcss/property_index.html),
it supports `block`/`inline`/`inline-block`/`flow-root`/`flex`/`inline-flex` plus
the full `table` display family, `float`, all four `position` values, transforms,
transitions, `filter` **and** `backdrop-filter`, and `box-shadow`. That is a
serious layout engine, not a toy.

What it does **not** have: **CSS Grid** (and therefore subgrid), **container
queries** / `@container`, `aspect-ratio`, and multicol.

**And it is a dialect, not the web.** It implements RML and RCSS, describes
itself as based on "XHTML1 and CSS2 with features from HTML5 and CSS3", and
explicitly disclaims compliance: *"We do not aim to be fully compliant with CSS
or HTML, in particular when it conflicts with lightness and performance."* The
deviations are structural, not cosmetic — `background` "excludes images, use
decorators instead", `border` "excludes border style", and there are RCSS-only
properties (`drag`, `focus`, `tab-index`, `scrollbar-margin`, `image-color`,
`fill-image`).

[**GTML**](https://github.com/Niekvdm/godot-plugins-gtml) — a GDScript plugin
(88 stars) that maps HTML/CSS onto native Control nodes with Vue-style
reactivity. Advertises 20+ elements, 80+ CSS properties, flexbox, transitions,
`:hover`/`:focus`, gradients, SVG, `v-if`/`v-for`/`v-model`. Genuinely nice
ergonomics, and the reactivity story is ahead of Weva's. But it is GDScript
(performance ceiling), Control-node-based (layout is Godot's, approximated to
look like CSS), and flexbox-only.

### Aside: RmlUi's flexbox is good, and that matters

Worth stating plainly, because it narrows the differentiation story. RmlUi's
[documented flexbox deviations](https://mikke89.github.io/RmlUiDoc/pages/rcss/flexboxes.html)
and Weva's own (`CONFORMANCE.md` §"Layout (flex)") are close to the same list:

| Deviation | RmlUi | Weva |
|---|---|---|
| Text in a flex container doesn't become an anonymous flex item | yes | **yes** — "falls into normal anonymous-block flow" |
| Stretched/resized items not re-formatted internally | yes | **yes** — "the box is resized but its interior is not re-flowed" |
| Automatic minimum sizing simplified | column-mode only | single-pass, no clamp loop |
| Baseline alignment | "only approximate" | real baselines in row-flex; synthesised in column |
| `order` | **not supported** | supported |
| `flex-basis: content` | **not supported** | supported |

So flexbox is roughly a wash — Weva is somewhat ahead on `order`, `flex-basis:
content` and baselines, and has 278+ flex tests behind it, but this is not the
axis on which the two projects differ. **The differentiators are narrower than
"better CSS": real HTML/CSS instead of a dialect, Grid and subgrid, container
queries, and the conformance corpus.** Argue the case on those, not on general
CSS quality.

### Where that leaves Weva

The gap is narrower than it first looks, but it is real. Nothing in the Godot
ecosystem is **native + MIT + actual HTML/CSS + grid-capable**:

| | Real HTML/CSS | Flex / float / table | Grid + subgrid | Container queries | Native | License |
|---|---|---|---|---|---|---|
| Godot WRY / godot-webview | yes | yes | yes | yes | **no** | MIT / LGPL |
| Godot-HTML (Ultralight) | yes | yes | yes | yes | yes | **$3k/yr >$100k rev** |
| RmlUi | **no** (RML/RCSS) | **yes** | **no** | **no** | yes | MIT |
| GTML | partial | flex only | **no** | **no** | yes (GDScript) | open source |
| Weva | yes | yes | **yes** | **yes** | yes | MIT |

Weva additionally carries the conformance work none of these have: ~10,500
tests, `CONFORMANCE.md` tracking spec deltas property by property, and
Chrome-diffed layout baselines. And the README's core pitch — HTML/CSS *that
LLMs already know*, no dialect to learn — is precisely what RmlUi's RML/RCSS
gives up.

**Read the competitive signal both ways, though.** RmlUi at 4.4k stars proves
there is real demand for HTML/CSS-style game UI. It also proves that a
*dialect with no grid* satisfies most of that demand. The people who need real
CSS Grid, subgrid, container queries and Chrome-matching layout are a subset —
and the AI-authoring angle is the argument that the subset is growing.

**One concrete tactical win:** Godot-RmlUi is MIT and does exactly the Godot-side
plumbing the port needs — draw commands onto CanvasItem layers, input via
`_gui_input`, textures via Godot resources, filesystem integration. Nineteen
commits is small enough to read in an afternoon and it de-risks the backend
layer. Read it before writing any of §3.

## 8. Should we build on RmlUi instead?

Tempting, and worth taking seriously — RmlUi is MIT, mature (4.4k stars, ~2,740
commits), already emits vertices and draw commands for a host renderer, and has
a WIP Godot binding. But "build on top of it" resolves into three quite
different projects, and the middle one — the one that sounds like the shortcut —
is the worst of them.

### What Weva would be discarding

| Subsystem | LOC | RmlUi equivalent? |
|---|---:|---|
| `Runtime/Layout` | 34,029 | Mostly yes — block, inline, flex, float, table, position |
| ├ `Layout/Grid` | 4,848 | **No** |
| ├ `Layout/AnchorPositioning` | 1,018 | **No** |
| ├ `Layout/Multicol` | 472 | **No** |
| `Runtime/Css` (cascade, selectors, values, container queries) | 30,236 | Partly — RCSS, no `@container` |
| `Runtime/Paint` | 17,757 | Different model (decorators) |
| `Runtime/Forms` | 6,609 | Partly |

Building on RmlUi means throwing away ~64k LOC of layout + CSS and adopting a
different one. That is the whole point of the option — but it is also the whole
cost, because that 64k LOC *is* the product.

### Option A — Finish Godot-RmlUi, ship RmlUi-based UI

Cheapest by a wide margin: months, not years. Implement the four interfaces
(`RenderInterface`, `SystemInterface`, `FontEngineInterface`, `FileInterface`)
against Godot, finish what Godot-RmlUi's TODO lists, polish, ship.

You get a good Godot UI addon. You do **not** get Weva: authors write RML/RCSS,
there is no grid, no container queries, `background-image` becomes decorators.
Weva's engine is not involved. This is a legitimate choice — it is just a
different product that happens to be built by the same people.

### Option B — Fork RmlUi and add grid + real HTML/CSS on top

This is the option that sounds like "best of both" and isn't. To get Weva's
differentiators you would have to:

1. **Implement CSS Grid inside someone else's layout engine.** Grid is the
   hardest layout algorithm in CSS — 4,848 LOC in Weva, written by whoever knows
   Weva's box model. Calibration on *effort, not quality*: RmlUi's flexbox
   support "has been in the works for a few months" and shipped alongside "a big
   rewrite of the layout engine... to ensure improved CSS conformance and much
   better maintainability" — and that was the maintainer working in his own
   code. Grid is a harder algorithm and you would be the outsider. (On quality,
   see the flexbox comparison in §7 — RmlUi's is good.)
2. **Add container queries**, which touch the cascade, not just layout.
3. **Replace RML/RCSS parsing with real HTML/CSS**, including reconciling the
   structural deviations (decorators vs `background-image`, `border` excluding
   style, the RCSS-only properties).
4. **Maintain a permanent fork** of a 2,740-commit library. Upstream will not
   take this work: the maintainer has stated non-compliance is a deliberate
   design position, so "full CSS conformance" contributions are against the
   project's stated goals, not merely unreviewed.
5. **Still write the Godot backend** — the four interfaces don't come free.

And you lose Weva's ~10,500-test conformance suite, because those tests assert
against Weva's APIs, not RmlUi's. You would be doing the hardest part of the
work (grid, container queries, real CSS parsing) in unfamiliar code, with no
test net, on a fork you own forever. That is strictly worse than doing it in
code you wrote, where the tests already exist.

### Option C — Port Weva, borrow RmlUi's integration design

RmlUi is MIT, so the parts genuinely worth taking are free to take:

- Its **interface decomposition** (`RenderInterface` / `SystemInterface` /
  `FontEngineInterface` / `FileInterface`) is a proven factoring of exactly the
  seam Weva needs, and maps closely onto Weva's existing `IRenderBackend` /
  `IImageSource` / `IUIClock` / `FontLoader.IFaceLoader`.
- Its **backends concept** (RmlUi 5.0 refactored samples into renderer+platform
  backend pairs) is a good model for shipping Godot and Unity hosts off one core.
- **Godot-RmlUi's plumbing** — draw commands onto CanvasItem layers, input via
  `_gui_input`, textures via Godot resources, filesystem integration — is a
  working reference for the exact backend layer §3 requires, at 19 commits.

This is the §1–6 plan with a few months of unknowns removed from the backend.

### The decisive question

RmlUi is a dialect. Weva's stated reason to exist, from its own README, is
*"HTML and CSS that LLMs have already learned from the web — no UXML dialect."*

Building on RmlUi means adopting RCSS, which is exactly the problem Weva was
built to solve. You would be reintroducing the dialect to avoid the port.

So the question is not really "RmlUi or port?" It is: **is real HTML/CSS
fidelity — grid, container queries, Chrome-matching layout, and a syntax LLMs
already emit correctly — actually the point of this project?**

- **Yes** → RmlUi cannot host that without being rewritten into Weva. Port
  Weva (Option C), and take RmlUi's integration design for free.
- **No** → then RmlUi already won, it shipped years ago, and porting 108k LOC to
  compete with it on features it mostly has is hard to justify. Do Option A.

There is no coherent middle. Option B pays the full cost of both.

## 9. Where RmlUi is architecturally better than Weva

Worth being honest about, because two of these directly inflate the cost of §3,
and the port is the moment to fix them.

### 9.1 The renderer abstraction is at the right altitude — Weva's is not

This is the big one.

RmlUi decomposes *everything* — border-radius, gradients, box-shadow, filters —
into **indexed triangles** plus optional layer/filter/shader operations. The
required `RenderInterface` is 8 methods: `CompileGeometry`, `RenderGeometry`,
`ReleaseGeometry`, `LoadTexture`, `GenerateTexture`, `ReleaseTexture`,
`EnableScissorRegion`, `SetScissorRegion`. Everything else — clip masks,
transforms, layers, filters, shaders — is optional and degrades gracefully.

Weva's `IRenderBackend` has **12 required methods at a semantic altitude**:
`FillRect`, `StrokeBorder`, `DrawText`, `DrawShadow`, `PushClip`/`Pop`,
`PushOpacity`/`Pop`, `PushTransform`/`Pop`, `PushFilter`/`Pop` (12 more are
optional with default bodies). Each backend must itself implement rounded-rect
SDF coverage, gradient evaluation, shadow blur, and per-edge border styles.
There are shared helpers (`RoundRectSdf` 130, `MeshBuilder` 188, `FilterPipeline`
502, `ColorMatrices` 210), but they don't remove the burden.

The cost shows up in the numbers:

| Backend | LOC | Fidelity |
|---|---:|---|
| `SoftwareRasterizer` | 1,592 | low — flat gradients, glyphs as blocks |
| `IMGUIDocumentRenderer` | 308 | debug-grade |
| `SoftwarePainter` (editor) | 226 | preview-grade |
| URP batched | 9,082 + 3,904 shader lines | production |

**RmlUi ships 6 renderers (GL2, GL3, DX11, DX12, Vulkan, SDL GPU) × 6 platforms
(Win32, X11, Wayland, GLFW, SDL, SFML).** Weva has exactly one production
backend. An abstraction with a single real consumer has not been tested as an
abstraction — it has been tested as an indirection.

This is not a mistake so much as a different bet: Weva traded backend
portability for a single very fast target (one draw call per batch, a 57-float4
über-shader instance, per-instance clip rects). That bet pays inside Unity. It
is exactly the bet that makes a Godot backend expensive.

### 9.2 The font engine is a real interface, not a reflection hack

RmlUi's `FontEngineInterface` is a first-class, swappable interface with a
default FreeType implementation.

Weva has `IFontMetrics` / `IGlyphMetrics` / `ITextCoreBackend` / 
`FontLoader.IFaceLoader` — the right shape — but the actual rasterization path
binds Unity's `FontEngine.TryRenderGlyphsToTexture` **by reflection into
undocumented internals of `UnityEngine.TextCoreTextEngineModule.dll`**, with a
fallback ladder through TextMeshPro. §3.2 calls the text stack the port's
hardest problem; it is hardest because of an architectural choice RmlUi got
right and Weva didn't.

### 9.3 Data binding is host-language agnostic

RmlUi registers data models explicitly from C++ and drives views from `data-*`
attributes in markup — no reflection, and the design survives any host language.

Weva's binding is C# reflection over C# object graphs (`Runtime/Binding`,
5 files). §4 concludes it should simply be deleted and replaced with Godot's
introspection. That is the correct call, but it is a feature that does not
survive the port because it was designed against the host language rather than
against the markup.

### 9.4 Zero engine coupling by construction, not by discipline

Weva's 82%-neutral core with a 104-line Unity stub is a genuinely impressive
result. But it is a *maintained property* — `.Unity.cs` partials, asmdef
references, csproj exclude lists that have already drifted once (the stale
`UIDocument.cs` in the appendix). RmlUi never had a host engine to decouple
from. Structural beats disciplined.

### Where Weva is architecturally ahead

To be even-handed:

- **Incremental invalidation.** Version-keyed caching at every pipeline stage
  (`Reactive/InvalidationTracker`; cache keys are input versions, not heuristic
  dirty bits; clean subtrees skip every stage), plus `Layout/Snapshot` (934) and
  `Compiled` (1,458). AGENTS.md treats the cache invariants as a documented
  contract. RmlUi's own docs advise authors to avoid content-based sizing to
  prevent "multiple formatting cycles of flex items", which hints at less
  machinery here — though I have not read its source, so treat that as a weak
  signal rather than a finding.
- **Conformance infrastructure as architecture.** ~10,500 tests,
  `CONFORMANCE.md` property-by-property deltas, `BaselineGen` with Chrome
  diffing, golden PNGs. This is what makes layout changes safe to land. RmlUi
  doesn't aim at conformance so it needs less of this — but it also cannot make
  the fidelity claim.
- **A real cascade.** 12,340 LOC of cascade engine, container queries, full
  selector matching. RCSS is a deliberately smaller model.
- **Batching.** The single-draw-call über-shader is a more aggressive
  performance architecture than triangle soup — which is the other side of §9.1's
  trade, not a free win.

### The actionable part

The two projects made opposite bets at the same fork: RmlUi optimized for *any
renderer, cheap to integrate*, and accepted a dialect and a simpler layout
model. Weva optimized for *real CSS on one very fast target*, and accepted an
expensive backend contract and an engine-coupled text stack.

**A Godot port is the moment to buy back §9.1 and §9.2**, because both layers
are being rewritten anyway:

1. Lower `IRenderBackend` toward geometry + effect ops, closer to RmlUi's
   factoring, so backends three and four are cheap. Keep the batched über-shader
   as *one* backend, not as the shape of the interface.
2. Rebuild the text stack behind a genuinely pluggable font interface
   (FreeType/HarfBuzz directly, or Godot's `TextServer`) — never reflection.

Doing the port without fixing these ports the mistakes too.

## 10. Which option maximises performance and CSS quality?

### CSS quality — Weva, clearly, with one honest caveat

Ranked on fidelity alone:

1. **Webview / Ultralight** — actual Chromium and WebKit. Nothing else comes
   close and it is dishonest to pretend otherwise. Disqualified on integration
   (separate surface, no scene-tree compositing), footprint, platform reach, and
   in Ultralight's case $3k/yr/app above $100k revenue.
2. **Weva** — real HTML/CSS, Grid and subgrid, container queries, anchor
   positioning, multicol, `order`, `flex-basis: content`, and ~10,500 tests plus
   Chrome-diffed baselines backing the claim.
3. **RmlUi** — genuinely solid: CSS2 plus flex, floats, the full table family,
   transforms, transitions, filters, `backdrop-filter`, box-shadow. But a
   dialect (RML/RCSS), no Grid, no container queries, decorators instead of
   `background-image`.
4. **GTML** — flexbox approximated over Control nodes.

Among *natively-integrated* options this is not close. Grid and container
queries are not things you add later to a design system.

### Performance — the measured answer points the same way, for a reason worth reading

Weva's own numbers (`Tools/PerfBench/`, M1-tier laptop, `PLAN.md` §perf):

| | median | p95 |
|---|---:|---:|
| Cascade.ComputeAll, 1001 elements (forms) | 8.3 ms | 29.1 ms |
| Cascade.IncrementalApply, `:hover` flip | **0.08 ms** | 0.13 ms |
| Cascade.IncrementalApply, attribute change | 0.21 ms | 0.28 ms |
| Layout.LayoutAll, 1001 elements (forms) | 10.8 ms | 12.8 ms |
| Paint.Convert, 1000 boxes | 0.99 ms | 1.38 ms |

**The incremental path is excellent** — a `:hover` flip is 100× cheaper than a
full cascade, because cache keys are input versions and a state flip invalidates
only the element that flipped (7.5 ms → 0.083 ms across v0.4→v0.5). That is the
number that governs steady-state UI, and it is Weva's real performance
architecture. Add the batched über-shader (one draw call per batch) and the
render side is aggressive too.

**The weakness is allocation, and it is a C# problem.** From `PLAN.md`:

| | allocs/call | target |
|---|---:|---:|
| Layout.LayoutAll, 1001 elements | 1.42 MB | 50 KB |
| Paint.Convert, 500 boxes | 1.10 MB | 0 |
| Paint.Convert, 1000 boxes | 2.19 MB | 0 |

The stated design goal is "**zero** per-frame allocations in steady state". The
project has fought for this across several versions — `BoxPool`, `PaintListPool`,
`ArrayPool` rentals, and a `CssValuePool` with interned constants that cut layout
from 7.79 MB/call to 1.42 MB (5.5×) and dropped the 1000-forms median 2.4×
(18 ms → 7.6 ms). Allocation was costing *throughput*, not just GC pauses.

And it is still 28× above target — with the remainder attributed to "DomSnapshot
rebuild + PaintList".

Note what that pooling actually is: `CssValuePool` carries the contract *"rented
values must NOT outlive the pool scope."* That is manual memory management,
hand-written in C#, to work around the GC. **The project is already paying C++'s
costs without receiving C++'s benefits.**

In C++ this entire category of work disappears: bump-allocate the pass into an
arena, reset the arena at the end. The 1.42 MB and 2.19 MB figures don't get
optimised, they stop existing — along with the lifetime contracts written to
approximate them.

### Verdict

**Porting Weva to C++ (Option C) is the answer that maximises both** — and
uniquely, the port is not merely a distribution change: it is the fix for the one
performance gap the project has measured and not been able to close in C#. You
keep the incremental invalidation architecture and the batching, delete the GC
pressure, and keep Grid and container queries.

Three caveats, stated plainly:

1. **This is only true if it ships.** A half-finished port performs worse than
   shipped RmlUi at everything. Twelve to twenty-four person-months is the price
   of the verdict, not an aside to it.
2. **I have no measured RmlUi numbers.** The comparison above is Weva's
   benchmarks against RmlUi's architecture and documentation, not a head-to-head.
   Do not quote it as one. If this decision hinges on performance, benchmark
   Godot-RmlUi against a Weva scene before committing.
3. **Backend quality will dominate either choice.** A naive Godot backend loses
   to a good RmlUi GL3 backend regardless of whose layout engine is better —
   which is another argument for re-cutting `IRenderBackend` per §9.1 before
   writing it.

## 11. Recommendation

Feasible, correct choice of technology for the stated goal, but go in knowing it
is a 12–24 person-month core translation, not a backend swap — and that §8's
question comes first. Do not start until "is real CSS fidelity the point?" has a
firm answer, because a "no" makes RmlUi the better project and a "maybe" makes
Option B look attractive when it is the most expensive path available.

If it proceeds:

1. **Decide §6 first** — single C ABI core, or accept the fork. It shapes every
   interface.
2. **Build the differential harness before writing engine C++.** Corpus +
   BaselineGen dumps + a diff runner. This is the whole safety net.
3. **Set conventions on day one**: no exceptions across the boundary, error
   returns, arena+index ownership, `string_view` lifetime rule, no fast-math.
4. **Port bottom-up** in the order in §5, oracle-green at each stage.
5. **Re-cut `IRenderBackend` before writing any backend** (§9.1) — lower it
   toward geometry + effect ops so backends three and four are cheap. Then
   `SoftwareRasterizer` first as the C++ backend: pixels on screen early, and it
   diffs against the 38 existing baseline PNGs.
6. Text stack behind a genuinely pluggable font interface (§9.2) —
   FreeType/HarfBuzz directly, or Godot's `TextServer`, never reflection. Then
   the batched GPU backend + shaders, then the editor plugin.

§7 changes the framing of the cheap-intermediate option. RmlUi's 4.4k stars
already answer "do Godot users want HTML/CSS UI?" — yes. The open question is
narrower: *do enough of them need real CSS (grid, container queries,
Chrome-matching layout) to justify 12–24 person-months over an existing MIT
dialect that ships today?* A C# port doesn't answer that question either, since
it can't reach GDScript or non-.NET users. Better probes: put a Weva-vs-RmlUi
feature comparison in front of the Godot UI community, or ship a small
grid-and-container-query demo, before funding the translation.

---

## Appendix: Unity coupling measurement

Retained from the C# analysis, because it's what makes the core translatable at
all — the 108k LOC is genuinely standalone, not Unity code with the serial
numbers filed off.

Over `Packages/com.wevaui/Runtime` (682 files, 132,467 LOC):

| | files | LOC |
|---|---:|---:|
| Reference `UnityEngine` | 47 | 18,716 |
| Do not | 635 | 113,751 |

Only 9 files touch `MonoBehaviour`/`ScriptableObject`. Burst appears in one
method. No `NativeArray`, no Jobs, no `Unity.Mathematics` — paint carries its own
`Rect`, `Transform2D`, `LinearColor`. The entire Unity API surface the core
needs is covered by `Tools/TestVerifyAll/UnityEngineStub.cs` — **104 lines**.

*(Unrelated bug spotted while measuring: `BaselineGen.csproj` and
`PerfBench.csproj` still exclude a stale `Runtime/UIDocument.cs`; the file is now
`WevaDocument.cs`, which only `TestVerifyAll.csproj` excludes. Those two headless
builds are likely pulling in the `MonoBehaviour`. Worth fixing regardless — and
especially before BaselineGen becomes the port's oracle.)*
