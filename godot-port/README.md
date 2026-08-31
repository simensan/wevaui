# weva-godot

HTML and CSS UI for Godot. A portable C++ core (`libweva`) behind a stable C
ABI, with a GDExtension host — and a path to serving the Unity package from the
same core rather than forking it.

> **Status: planning.** No engine code yet. This tree holds the decisions,
> sequencing and harness design that must exist before the first line of C++.
> Feasibility analysis lives in [`GODOT_PORT_FEASIBILITY.md`](../GODOT_PORT_FEASIBILITY.md)
> in the `wevaui` repo; section references below (§3, §5, §9…) point there.

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
```

The repo is named for its first deliverable; the core inside it is deliberately
host-agnostic so a second host doesn't require a second engine.

## Start here

1. [`docs/CONVENTIONS.md`](docs/CONVENTIONS.md) — settle these before writing code.
2. [`docs/ORACLE.md`](docs/ORACLE.md) — build this before writing engine code.
3. [`docs/PORT_PLAN.md`](docs/PORT_PLAN.md) — the phase order and what "done" means.
