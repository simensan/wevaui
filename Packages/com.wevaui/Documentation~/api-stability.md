# API stability

This package is the Unity host for the shared C++ core. Much of what compiles
into `Weva.Runtime` is the generated binding to the core's C ABI. This page
says which types are a contract, so a change behind them is not a breaking
change to a package people depend on.

## Supported surface

These are covered by semantic versioning. A breaking change to any of them
requires a major bump and a changelog entry.

| Type | Namespace | What of it |
|---|---|---|
| `WevaDocument` | `Weva` | The component: `DocumentAsset`, `StylesheetAssets`, `InlineHtml`, `InlineCss`, `SortingOrder`, `PrefersDarkColorScheme`, `Font`, `Bold`, `Italic`, `Fallbacks`, `SystemFontFallback`, `BasePath`, `UseUserAgentStylesheet`, `AutoInput`, `InputConsumed`, `FollowScreenSafeArea`, `WrapTab`, `GamepadTextEntry`, `AcceptsKeyboard`, `AssetReader`, `Cursor`, `LastError`; `Reload()`, `SetController(object)`, `GetController<T>()`, `Controller`, `Bind(model, controller)`, `Data`, `Refresh()`, `RequestRefresh()`, `Query(selector)`, `QueryAll(selector)`, `FocusedElement`, `SetSafeAreaInsets(...)`, `RegisterFontFamily(name, font)`, `LinkedStylesheetHrefs`, `BakeLinkedStylesheets(read)`; the events `ElementClicked`, `HandlerInvoked`, `ValueChanged`, `Changed`, `FormSubmitted`, `Focused`, `DataChanged`, `Event`, `TabbedOut`, `TextEntryRequested`. |
| `WevaElement` | `Weva` | An element as a value: `IsValid`, `None`, `Id`, `Tag`, `Text`, `Value`, `Attribute`, `HasAttribute`, `SetAttribute`, `HasClass`, `Focus()`, `IsFocused`, `Bounds`, `Scroll`, `MaxScroll`, `ScrollTo`, `TryGetRow`, `RowIndex`, `RowKey`, `ShowDialog`, `CloseDialog`, `Parent`, `Children`; equality. Stale after `Reload()`, never throws. |
| `WevaEvent`, `WevaEventKind` | `Weva` | What `WevaDocument.Event` raises: kind, target, position, buttons, modifiers, text, handler. |
| `UIBindAttribute` | `Weva.Binding` | Marks a binding root on a controller. |
| `IBindingVersion` | `Weva.Binding` | Opt-in change signal: bump instead of being polled. |
| `UIBatchedRendererFeature` | `Weva.Rendering.URP` | The URP renderer feature that draws documents. |
| `UrpFeatureSetup.ApplyNonInteractive()` | `Weva.EditorTools.Setup` | The scripted setup entry point. |

The markup contract — `{{ path }}`, `data-class-<name>`, `data-each` /
`data-key` / `$index`, `data-model`, `on-<event>="Name"` with `Name()` or
`Name(string id)` — is part of the supported surface too: it is what a page
and a controller are written against.

## Internal

`Weva.Native` — the generated binding to the C ABI (`WevaNative.g.cs`), the
`NativeDocument` wrapper, the font backend, the renderer, the input feed,
the binding source — is `internal` as of 1.0.0. It follows the ABI minor and
is the host's, not the game's. Everything a game needs is on `WevaDocument`
and `WevaElement`; if something is missing there, that is worth an issue.

## Deleted

The C# engine that 0.1.x shipped — `Weva.Css.*`, `Weva.Layout.*`,
`Weva.Paint.*`, `Weva.Text.*`, `Weva.Dom.*`, `Weva.Events.*`, `Weva.Forms.*`,
`Weva.Designer.*`, `Weva.Testing.*`, `BindingScanner` / `BindingSet`,
`IRenderBackend`, `IMGUIDocumentRenderer`, `DevToolsOverlay`, `WevaFonts`,
`UIRendererFeature` — is gone as of 1.0.0. The CHANGELOG's migration notes say
what replaced each piece.

## Direction (decided 2026-09-13)

The C++ core is the single source of truth and Chrome — not another
implementation — is what its conformance is measured against. 1.0 is a
breaking release from 0.1.1: the controller model carries over unchanged, the
C# DOM does not.
