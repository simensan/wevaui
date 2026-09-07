# weva-godot

HTML and CSS UI for Godot. A portable C++ core (`libweva`) behind a stable C
ABI, with a GDExtension host — and a path to serving the Unity package from the
same core rather than forking it.

> **Status: development preview.** The C++ engine and GDScript host run today,
> with layout, rendering, forms, animations and data binding. Desktop builds
> are exercised on Linux and Windows. Fresh-project installation and exported
> resource packs and native debug/release exports pass on both. Broader IME compatibility, remaining
> form behavior and other game integration work are still required before release.
> See [product readiness](docs/PRODUCT_READINESS.md) for current evidence and
> remaining work. The original [feasibility analysis](../GODOT_PORT_FEASIBILITY.md)
> and [port plan](docs/PORT_PLAN.md) are historical engineering references.

## Use it in Godot

Build the extension using the [Godot host guide](hosts/godot/README.md), then
open `hosts/godot/project/project.godot` to run the sample gallery. No C# runtime
is required. A document is a `WevaDocument` Control:

```gdscript
var ui := WevaDocument.new()
ui.document_size = Vector2(640, 360)
ui.css = "body { color: white; background: #18202c } button { padding: 12px }"
ui.html = '<h1>{{ Player.Name }}</h1><button on-click="start_game">Start</button>'
ui.data = {"Player": {"Name": "Ada"}}
ui.set_controller(self)
add_child(ui)
```

Define `start_game(_id: String)` on the controller to handle the button. The host guide
covers bindings, editable forms, keyed lists, input, scrolling and resource
paths. `demo.tscn` and `inventory.tscn` provide complete GDScript examples.

Native anchors and Containers size the HTML viewport. Unsized documents fill
their parent; `document_size` aliases `Control.size`. Native GUI routing owns
focus and overlapping controls. Tab traverses HTML and then native Controls;
gameplay actions can use `_unhandled_input` to avoid handling accepted UI keys.
Buttons, checkboxes, radio groups and sliders support native keyboard actions,
including form submission through the default button. See the
[keyboard behavior matrix](docs/KEYBOARD_INPUT.md) for supported controls and
remaining form work.

IME composition includes preedit, commit/cancel, undo and bindings. See the
[IME evidence and limits](docs/IME.md) before choosing a target input method.

## Why this exists

Weva's engine-neutral core is ~108k LOC of C# that already compiles headlessly.
Godot's C# builds could run it as-is, but C# in Godot reaches neither GDScript
users nor non-.NET builds, and has no web export. A C++ GDExtension reaches all
three — at the cost of hand-translating the core.

Two findings shaped this plan:

* **The port is the fix for Weva's measured performance gap, not just a
  distribution change.** Layout allocates 1.42 MB/call against a 50 KB target
  and paint 1.10–2.19 MB against a target of zero, after several versions of
  pooling work. `CssValuePool` already carries a *"rented values must not
  outlive the pool scope"* contract — manual memory management written in C# to
  work around the GC. Arenas delete that category rather than optimising it.
* **Two of Weva's abstractions are worth re-cutting on the way over** (§9):
  the render backend sits too high (12 semantic methods, so every backend
  reimplements SDF coverage, gradients, shadow blur), and the text stack binds
  Unity internals by reflection. Both layers are being rewritten anyway.

## Layout

```
libweva/            Host-agnostic C++ core. No Godot types, no Unity types.
  include/weva/     Public C++ headers
  include/weva_c.h  Stable C ABI — the seam Unity would later bind through
  src/              Implementation, staged by phase
hosts/
  godot/            GDExtension host (first host)
  unity/            P/Invoke shim over the C ABI (later; placeholder)
tools/
  oracle/           Differential test harness against the C# implementation
docs/
  PORT_PLAN.md      Phases, sequencing, exit criteria
  ARCHITECTURE.md   Layering, the re-cut render backend, the C ABI shape
  CONVENTIONS.md    C++ rules — decided up front, not per-file
  ORACLE.md         How the C# engine guards the C++ one
  PERFORMANCE.md    What a frame costs, what a load costs, and what is
                    still slow with the reason it has not been fixed
```

The repo is named for its first deliverable; the core inside it is deliberately
host-agnostic so a second host doesn't require a second engine.

## Start here

1. [Godot host guide](hosts/godot/README.md) — build, run and integrate a document.
2. [Product readiness](docs/PRODUCT_READINESS.md) — verified behavior and remaining release work.
   [Release verification](docs/RELEASE.md) covers pinned builds, versioned packaging and acceptance commands.
3. [Architecture](docs/ARCHITECTURE.md), [conventions](docs/CONVENTIONS.md), and
   [oracle](docs/ORACLE.md) — read before changing the engine.
4. [Core benchmark](tools/weva_bench/README.md) — Windows build, workloads and allocation profiles.
