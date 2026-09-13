# API stability

This package has **803 public types in `Runtime/`**. Its README describes
twelve. That gap is not an accident of documentation — it is what happens when
an engine is built in one assembly and nothing forces a decision about which
parts are a contract.

This page makes the decision explicit, so a refactor of the engine's internals
is not a breaking change to a package people depend on.

## Supported surface

These are covered by semantic versioning. A breaking change to any of them
requires a major bump and a changelog entry.

| Type | Namespace |
|---|---|
| `WevaDocument` | `Weva` |
| `WevaFonts` | `Weva` |
| `UIElementAttribute` | `Weva` |
| `UIBindAttribute` | `Weva.Binding` |
| `BindingScanner`, `BindingSet` | `Weva.Binding` |
| `IBindingVersion` | `Weva.Binding` |
| `IRenderBackend`, `RecordingBackend`, `NullBackend` | `Weva.Rendering` |
| `UIRendererFeature` | `Weva.Rendering.URP` |
| `IMGUIDocumentRenderer` | `Weva.Rendering` |
| `DevToolsOverlay` | `Weva.DevTools` |

The DOM and event types a handler receives — `Element`, `Node`, `Document`,
`TextNode`, `EventDispatcher` and the `*Event` classes — are supported in the
shapes the authoring guide uses, because you cannot write a handler without
them.

## Unsupported surface

Everything else is engine internals that happen to be `public` because the
whole engine compiles into one assembly. **These may change in any release,
including a patch**, and doing so will not be treated as a breaking change:

`Weva.Css.*` · `Weva.Layout.*` · `Weva.Paint.*` · `Weva.Rendering.URP.*`
(beyond `UIRendererFeature`) · `Weva.Text.*` · `Weva.Forms.*` internals ·
`Weva.Designer.*` · `Weva.Testing.*` · `Weva.Native.*`

If you are reaching into one of these and it is the only way to do something
you need, that is worth an issue — the answer is usually to add a supported
entry point rather than to freeze an internal one.

## `Weva.Native` in particular

`Weva.Native` is the host layer for the shared C++ core: 42 public types, 488
public members, 131 generated P/Invoke declarations in `snake_case`, and a
plugin binary that currently ships for Windows x64 only. It did not exist at
`v0.1.1`.

It is **not** an API. It is the Unity side of an ABI that is still moving —
`WEVA_ABI_VERSION_MINOR` has gone from 25 to 38 in recent development — and
every type in it is either generated from `weva_c.h` or written to talk to
something that is. Nothing in the README, the changelog or the authoring guide
mentions it, because nothing is meant to call it.

Treat it as unsupported. It may become `internal`.

**Direction, decided 2026-09-13.** The C# engine is frozen and the C++ core
is the single source of truth. 1.0 is a breaking release: the supported
surface above is redesigned around the native document, the engine
namespaces listed as unsupported are deleted, and Chrome — not the C# engine —
is what conformance is measured against. Nothing in the unsupported list is
worth building on now.

## Why not simply mark it all `internal`

Because the cost is not zero and the benefit of the policy is nearly all of
it. Making ~790 types internal means re-verifying every file that compiles
against them, and it would break any consumer already reaching into
`Weva.Layout` or `Weva.Paint` — which nothing ever told them not to do. Writing
the boundary down costs nothing, breaks nobody, and is the prerequisite for
narrowing it later with a deprecation cycle rather than a surprise.
