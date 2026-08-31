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

**But RmlUi is a dialect, not the web.** It implements RML and RCSS, states it
supports "most of CSS2 with some CSS3 features", and explicitly disclaims
compliance: *"We do not aim to be fully compliant with CSS or HTML, in
particular when it conflicts with lightness and performance."* Its feature list
covers flexbox and media queries. It does **not** do CSS Grid, floats, tables,
multicol, or container queries.

[**GTML**](https://github.com/Niekvdm/godot-plugins-gtml) — a GDScript plugin
(88 stars) that maps HTML/CSS onto native Control nodes with Vue-style
reactivity. Advertises 20+ elements, 80+ CSS properties, flexbox, transitions,
`:hover`/`:focus`, gradients, SVG, `v-if`/`v-for`/`v-model`. Genuinely nice
ergonomics, and the reactivity story is ahead of Weva's. But it is GDScript
(performance ceiling), Control-node-based (layout is Godot's, approximated to
look like CSS), and flexbox-only.

### Where that leaves Weva

The gap is specific and real. Nothing in the Godot ecosystem is
**native + MIT + actual HTML/CSS + grid-capable**:

| | Real HTML/CSS | Grid / floats / tables / multicol | Container queries | Native (no browser) | License |
|---|---|---|---|---|---|
| Godot WRY / godot-webview | yes | yes | yes | **no** | MIT / LGPL |
| Godot-HTML (Ultralight) | yes | yes | yes | yes | **$3k/yr >$100k rev** |
| RmlUi | **no** (RML/RCSS) | **no** | **no** | yes | MIT |
| GTML | partial | **no** (flexbox only) | **no** | yes (GDScript) | open source |
| Weva | yes | **yes** | **yes** | yes | MIT |

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

## 8. Recommendation

Feasible, correct choice of technology for the stated goal, but go in knowing it
is a 12–24 person-month core translation, not a backend swap.

If it proceeds:

1. **Decide §6 first** — single C ABI core, or accept the fork. It shapes every
   interface.
2. **Build the differential harness before writing engine C++.** Corpus +
   BaselineGen dumps + a diff runner. This is the whole safety net.
3. **Set conventions on day one**: no exceptions across the boundary, error
   returns, arena+index ownership, `string_view` lifetime rule, no fast-math.
4. **Port bottom-up** in the order in §5, oracle-green at each stage.
5. **`SoftwareRasterizer` first** as the C++ backend — pixels on screen early,
   and it diffs against the 38 existing baseline PNGs.
6. Text stack (FreeType/HarfBuzz directly, or Godot's `TextServer`), then the
   batched GPU backend + shaders, then the editor plugin.

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
