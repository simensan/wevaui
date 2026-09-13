# API stability

This package is the Unity host for the shared C++ core. Most of what compiles
into `Weva.Runtime` is either the frozen C# engine awaiting deletion or the
generated binding to the core's C ABI. This page says which types are a
contract, so a refactor of the engine's internals is not a breaking change to
a package people depend on.

## Supported surface

These are covered by semantic versioning. A breaking change to any of them
requires a major bump and a changelog entry.

| Type | Namespace | What of it |
|---|---|---|
| `WevaDocument` | `Weva` | The component: `DocumentAsset`, `StylesheetAssets`, `InlineHtml`, `InlineCss`, `SortingOrder`, `PrefersDarkColorScheme`, `Font`, `Bold`, `Italic`, `Fallbacks`, `BasePath`, `UseUserAgentStylesheet`, `AutoInput`, `InputConsumed`, `FollowScreenSafeArea`, `LastError`; `Reload()`, `SetController(object)`, `GetController<T>()`, `Controller`, `Bind(model, controller)`, `Data`, `Refresh()`, `RequestRefresh()`, `LinkedStylesheetHrefs`, `BakeLinkedStylesheets(read)`; the events `ElementClicked`, `HandlerInvoked`, `ValueChanged`, `Changed`, `FormSubmitted`, `Focused`, `DataChanged`. |
| `UIBindAttribute` | `Weva.Binding` | Marks a binding root on a controller. |
| `IBindingVersion` | `Weva.Binding` | Opt-in change signal: bump instead of being polled. |
| `UIBatchedRendererFeature` | `Weva.Rendering.URP` | The URP renderer feature that draws documents. |
| `UrpFeatureSetup.ApplyNonInteractive()` | `Weva.EditorTools.Setup` | The scripted setup entry point. |

The markup contract — `{{ path }}`, `data-class-<name>`, `data-each` /
`data-key` / `$index`, `data-model`, `on-<event>="Name"` with `Name()` or
`Name(string id)` — is part of the supported surface too: it is what a page
and a controller are written against.

## Public, but not supported

`WevaDocument.Document` hands you the core document itself
(`Weva.Native.NativeDocument`: `Query`, element attributes and values, focus,
scroll, dialogs, the inspector surface) and `WevaDocument.Event` hands you
every core event as a `NativeEvent`. They are public because a game sometimes
needs them, and unsupported because they follow the C ABI: `Weva.Native` is
generated from `weva_c.h` (`WevaNative.g.cs`) or written to talk to it, and it
moves with the ABI minor. Reaching into it is fine; expecting it not to change
in a minor release is not. If something there is the only way to do a thing
you need, that is worth an issue — the answer is usually a supported entry
point on `WevaDocument`.

## Unsupported and going away

Everything else. The frozen C# engine — `Weva.Css.*`, `Weva.Layout.*`,
`Weva.Paint.*`, `Weva.Text.*`, `Weva.Dom.*`, `Weva.Events.*`, `Weva.Forms.*`,
`Weva.Designer.*`, `Weva.Testing.*`, `BindingScanner` / `BindingSet`,
`IRenderBackend`, `IMGUIDocumentRenderer`, `DevToolsOverlay`, `WevaFonts`,
`UIRendererFeature` (the pre-batching URP feature) — and its component
`WevaLegacyDocument` (the `WevaDocument` of 0.1.1, renamed 2026-09-13) compile
into the package until Phase 4.3 deletes them. **Nothing in this list is worth
building on now.** They may change or vanish in any release.

## Direction (decided 2026-09-13)

The C++ core is the single source of truth and Chrome — not the C# engine —
is what its conformance is measured against. 1.0 is a breaking release from
0.1.1: the controller model (`[UIBind]`, `on-<event>`, `SetController`)
carries over unchanged; the C# DOM does not. The frozen engine is deleted at
4.3 and `package.json` moves to 1.0.0 with a migration note.
