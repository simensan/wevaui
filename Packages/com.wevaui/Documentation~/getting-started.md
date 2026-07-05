# Getting Started

[← Back to index](index.md)

This page takes you from an empty Unity project to a rendered HTML/CSS
document on screen.

## Requirements

- Unity **6000.4** (the package manifest pins `unity: "6000.4"`,
  `unityRelease: "1f1"`).
- **URP** for the production render path. The package compiles without URP —
  the URP code is gated behind a `WEVA_URP` `versionDefines` token — but the
  full renderer only activates when URP is present. Without it, the IMGUI
  fallback renders (debug-grade).
- Scripting backend: IL2CPP-compatible. No `Reflection.Emit`; data binding is
  reflection-driven today and source-generator-friendly for IL2CPP players.

## Install

Add the package to `Packages/manifest.json`:

```json
"com.wevaui": "https://github.com/<owner>/weva.git?path=Packages/com.wevaui"
```

Or import locally: clone the repo, then Package Manager → **Add package from
disk** → pick `Packages/com.wevaui/package.json`.

The **Phase One Demo** sample (Package Manager → Weva → Samples) is a complete
scene that exercises the whole pipeline end-to-end; import it to confirm the
package works before authoring your own UI.

## Author the HTML/CSS

Drop `.html` and `.css` files into `Assets/UI/` — Unity imports them as
`TextAsset`s. A document references its stylesheet with a `<link>`:

```html
<!-- Assets/UI/menu.html -->
<link rel="stylesheet" href="menu.css" />
<main class="menu">
  <h1>My Game</h1>
  <button id="start" on-click="OnStart">Start</button>
</main>
```

```css
/* Assets/UI/menu.css */
.menu { display: flex; flex-direction: column; gap: 16px; padding: 24px; }
button { padding: 8px 16px; border-radius: 8px; background: #4f46e5; color: white; }
button:hover { background: #6366f1; }
```

You can also assign stylesheets directly in the inspector (see below) instead
of, or in addition to, `<link>`.

## Mount the document

`WevaDocument` (component menu **Weva → UI Document**) is the author-facing
MonoBehaviour. It holds your HTML + stylesheet `TextAsset`s and runs the
pipeline in `OnEnable`.

Inspector fields map to these properties:

| Property | Meaning |
|---|---|
| `DocumentAsset` | The `.html` `TextAsset`. |
| `StylesheetAssets` | Zero or more `.css` `TextAsset`s, applied in order. |
| `RendererBackend` | `Auto` (URP if present, else IMGUI), `IMGUI`, or `URP`. |
| `SortingOrder` | Paint order across multiple documents (`Order`). |
| `ViewportOverride` | Fixed layout viewport in px; `(0,0)` = track the screen. |
| `PrefersDarkColorScheme` | Seeds `@media (prefers-color-scheme)` / `light-dark()`. |
| `EnableHotReload` | Watch the source files and rebuild on edit (editor-on by default). |
| `AutoRebuildOnChange` | Rebuild when inspector fields or the viewport change. |

`OnEnable` auto-attaches a `Forms.Bridge.UnityInputController` so pointer and
keyboard input work without manual wiring.

## Wire a controller

Attach a controller script next to the `WevaDocument` and register it:

```csharp
using UnityEngine;
using Weva;
using Weva.Binding;

public sealed class MainMenu : MonoBehaviour {
    [UIBind] public int CoinCount;

    void Awake() => GetComponent<WevaDocument>().SetController(this);

    public void OnStart() => SceneManager.LoadScene("Game");
}
```

- `[UIBind]` fields/properties are reachable from `{{ CoinCount }}` placeholders
  in HTML and CSS attribute values; bindings poll once per frame.
- `[UIElement("start")]` captures an `Element` reference at build time.
- `on-click="OnStart"` resolves against the controller. `SetController(...)`
  re-binds without rebuilding the cascade/layout.

See [`AuthoringGuide.md`](AuthoringGuide.md) for the full binding, event, and
form story.

## URP render setup

The production renderer is a `ScriptableRendererFeature`. Add
`UIBatchedRendererFeature` (or `UIRendererFeature`) to your URP Renderer asset's
**Renderer Features** list. It injects a render pass after
`RenderPassEvent.AfterRendering` and draws the UI directly into
the camera color target (zero intermediate blit). Set `RendererBackend = URP`
on the document to force this path.

Without the feature, set `RendererBackend = Auto` or `IMGUI` to fall back to
the IMGUI renderer — fine for editor testing, not for shipping.

> **Screen-space only (v1).** A `WevaDocument` draws as a screen-space overlay
> into the camera color target; layering across documents is controlled by
> `SortingOrder`. There is no built-in **world-space** mode — UI mapped onto a
> 3D surface / quad (a diegetic in-world screen) is not a v1 feature and isn't
> wired through `WevaDocument`. It would require rendering the pass into a
> `RenderTexture` and sampling that on a material yourself, which is outside the
> supported/documented surface today.

## Viewport sizing

Weva uses a **logical pixel** model: `1px` = 1 logical pixel, `rem`/`em` derive
from a 16px base font size (matching CSS). The layout viewport — what `vw`/`vh`
and `@media (width)` resolve against — is the *UI surface*, not the OS window,
which matters for split-screen and embedded UI.

`WevaDocument` resolves the current viewport in this priority order:

1. `ViewportOverride` if both components are `> 0`.
2. `ReferenceCamera.pixelWidth/Height` if a camera is assigned.
3. The current render-target size (pushed by the URP pass via
   `PrepareForRenderViewport`).
4. `Screen.width/height`, then `Camera.main`, then the package default.

When the Game View resizes in Play mode, `Update` detects the delta and reruns
layout against the new viewport — a lighter pass than a full `Rebuild()`. On
mobile, `Screen.safeArea` is piped into `env(safe-area-inset-*)` automatically.

## Hot reload

With `EnableHotReload` on (editor default), editing a watched `.html` or `.css`
file in Play mode reparses and rebuilds without a domain reload; controller
state and `[UIBind]` values survive. For programmatically-built UI with no
source file, call `doc.Rebuild()`.

## Edit-mode preview

`WevaDocument` is `[ExecuteAlways]`: with **Edit Mode Preview** enabled in the
inspector (the default), the Game view renders the document **without entering
Play mode**. Inspector edits, HTML/CSS hot reload, and CSS animations all stay
live via an editor repaint pump. Disable the toggle per-document if a heavy
page slows editor repaints.

Controller-side registrations (fonts, image registries) only reach the preview
if the controller is also `[ExecuteAlways]` — gate per-frame gameplay work on
`Application.isPlaying` and keep `OnEnable` registrations edit-safe.

## Player builds

Three document references resolve from **disk** in the editor and are baked
into the scene automatically at build time (an `IProcessSceneWithReport` hook),
so player builds work without any extra setup:

- `<link rel="stylesheet" href="...">` CSS files,
- `@import` inside those linked sheets (pre-flattened at bake time), and
- `<template src="...">` component templates (transitive closure).

The editor always prefers the live file — a stale bake can never shadow an
edit. Two documented limits: a `WevaDocument` on a prefab **instantiated at
runtime** never passes through the scene hook (call
`LinkedStylesheetBaker.Bake` from a custom build step, or assign
`StylesheetAssets` explicitly), and a `<template src>` nested *inside* another
template body is not resolved on either path.

Assets referenced only through editor APIs (`AssetDatabase` loads in custom
controllers) do **not** ship — use serialized inspector references for
anything a build needs, including TMP font assets and sprites.

---

Next: [Supported HTML](supported-html.md) · [Supported CSS](supported-css.md)
