# Getting Started

[← Back to index](index.md)

This page takes you from an empty Unity project to a rendered HTML/CSS
document on screen.

## Requirements

- Unity **6000.3** or newer (the package manifest pins `unity: "6000.3"`).
- **URP** — the package's render pass is a URP `ScriptableRendererFeature`.
- **Input System** — the document reads the mouse, keyboard, touch, gamepad
  and IME through it (pulled in as a package dependency).
- The native plugin ships for **Windows x64**; other platforms are added as
  their CI jobs go green.
- Scripting backend: IL2CPP-compatible. Binding is reflection over public
  members (no `Reflection.Emit`); keep `[UIBind]` members and handler methods
  out of managed code stripping (they are looked up by name).

## Install

Add the package to `Packages/manifest.json`:

```json
"com.wevaui": "https://github.com/simensan/wevaui.git?path=Packages/com.wevaui"
```

(Pin a release with a `#v*` tag suffix, or drop it to track `main`.)
Or import locally: clone the repo, then Package Manager → **Add package from
disk** → pick `Packages/com.wevaui/package.json`.

The **Phase One Demo** sample (Package Manager → Weva → Samples) is a complete
scene that exercises the whole pipeline end-to-end; import it to confirm the
package works before authoring your own UI.

## Author with AI

Weva's whole premise is that AI models already know web HTML/CSS — there is no
Unity dialect to teach them. So the fastest way to a first screen is to tell
your AI model of choice:

> **"Create a main menu UI using Weva — standard HTML and CSS, no framework,
> one `.html` and one `.css` file."**

Drop whatever it produces into `Assets/UI/` and mount it (next two sections).
Anything the model writes for a browser is either supported or fails loudly —
that's the design rule. For an AI coding agent working inside your project
(Claude Code, Cursor, Copilot), point it at the repo's
[`AI_REFERENCE.md`](https://github.com/simensan/wevaui/blob/main/AI_REFERENCE.md)
so it knows the exact capability envelope and integration API.

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

A `<link href>` resolves **next to the document asset** (the same folder);
`<style>` elements and `@import` inside a sheet work too. You can also assign
stylesheets directly in the inspector instead of, or in addition to, `<link>`
— the page's own sheets apply first, the inspector's after, so a later
declaration wins as in a browser.

## Mount the document

`WevaDocument` (component menu **Weva → UI Document**, or **GameObject →
Weva → New WevaDocument**) is the author-facing MonoBehaviour. It holds your
HTML + stylesheet `TextAsset`s and hosts the engine — the shared C++ core that
parses, cascades, lays out and paints the page.

Inspector fields map to these properties:

| Property | Meaning |
|---|---|
| `DocumentAsset` | The `.html` `TextAsset`. `InlineHtml` is used when none is set. |
| `StylesheetAssets` | Zero or more `.css` `TextAsset`s, applied in order after the page's `<link>`s. |
| `SortingOrder` | Paint order across multiple documents. |
| `PrefersDarkColorScheme` | Answers `@media (prefers-color-scheme: dark)` / `light-dark()`. |
| `Font` / `Bold` / `Italic` / `Fallbacks` | The UI face and its real bold/italic files; faces tried for code points it lacks. The package's Inter and symbol face when empty. |
| `SystemFontFallback` | The machine's fonts: a family the page names in `font-family` resolves to the installed font of that name, and after the fallbacks the platform's UI and symbol fonts serve a script or glyph none of the faces carry (on by default; off for machine-independent output). |
| `BasePath` | Directory that `url()`, `@import` and `@font-face` sources resolve against. Defaults to the document asset's folder in the editor. |
| `UseUserAgentStylesheet` | The browser defaults (`<h1>` size, `<button>` look, …). On. |
| `AutoInput` | Read the Input System every frame and feed the document. On. |
| `FollowScreenSafeArea` | Feed `Screen.safeArea` to `env(safe-area-inset-*)`. Off (a desktop has no insets). |

The inspector also shows the URP renderer-feature check with a one-click fix,
the core's HTML diagnostics (what it recovered from) and its last error, and a
**Reload** button — the document is alive in edit mode too (`[ExecuteAlways]`),
so the Game view shows the page without entering play.

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
  in HTML text and attribute values; a plain controller is read once per
  frame, so mutating the field is enough.
- `on-click="OnStart"` calls the controller's public `OnStart()` — or
  `OnStart(string id)` to receive the element's `id`.
- `SetController(...)` binds without reparsing; `GetController<T>()` reads it
  back.

See [`AuthoringGuide.md`](AuthoringGuide.md) for the full binding, event, and
form story.

## URP render setup

The renderer is a `ScriptableRendererFeature`. Add `UIBatchedRendererFeature`
to your URP Renderer asset's **Renderer Features** list. It injects a render
pass after `RenderPassEvent.AfterRendering` and draws every document's
triangle list directly into the camera colour target.

Three equivalent ways to set it up — all idempotent, and all also add the
Weva shaders to **Always Included Shaders** (required for player builds):

- **Menu:** `Window > Weva > Setup > Add URP Renderer Feature`.
- **Inspector:** if the feature is missing, the `WevaDocument` inspector shows
  a warning with an **Add URP Renderer Feature + shader includes** button.
- **Script / AI agents / CI:** call
  `Weva.EditorTools.Setup.UrpFeatureSetup.ApplyNonInteractive()` from editor
  code, or headless:

  ```
  Unity -batchmode -quit -projectPath <project> -executeMethod Weva.EditorTools.Setup.UrpFeatureSetup.ApplyNonInteractive
  ```

Without the feature nothing draws: the core's draw list has no pass to land
in.

> **Screen-space only.** A `WevaDocument` draws as a screen-space overlay into
> the camera colour target; layering across documents is `SortingOrder`. There
> is no built-in **world-space** mode — UI mapped onto a 3D surface (a
> diegetic in-world screen) would mean rendering the pass into a
> `RenderTexture` and sampling that on a material yourself, which is outside
> the supported surface today.

## Viewport sizing

Weva uses a **logical pixel** model: `1px` = 1 logical pixel, `rem`/`em` derive
from a 16px base font size (matching CSS). The layout viewport — what `vw`/`vh`
and `@media (width)` resolve against — is the render target the URP pass
draws into: the document starts at `Screen.width × Screen.height` and follows
the pass's target size (`PrepareForRenderViewport`) when the Game view resizes.
`FollowScreenSafeArea` pipes `Screen.safeArea` into `env(safe-area-inset-*)`,
scaled to the document's viewport.

## Hot reload

Changes to referenced HTML, assigned or linked CSS, imported sheets, and
requested image/font URLs reload the document after asset import completes,
in play mode and edit mode. Deletions, moves, and creating a previously missing
dependency count too. The controller stays attached and its `[UIBind]` values
survive. From code, `doc.Reload()` also refreshes images and `@font-face` URL data.

## Player builds

Linked stylesheets and their nested imports are baked into scene documents
and prefab assets automatically before a build. Imports from `<style>`,
inspector sheets and `InlineCss` are included too. The core parser discovers
them, preserving source paths, conditions and cycle handling. The editor
prefers live files under `BasePath` (the document asset's folder by default).

Custom build steps can call `doc.BakeLinkedStylesheets(read)`, where `read`
serves document-relative CSS URLs; its return value counts direct links.
Editor tooling can use `WevaDocumentLinkBaker.BakePrefabs(paths)` to bake
selected prefab assets. An edit to an imported sheet updates the bake even
when the linked root has not changed.

`url()` images and `@font-face` files resolve through the core's asset
reader, which reads files relative to `BasePath`. A player that does not ship
its UI as files supplies its own reader
(`doc.AssetReader = path => bytes`) to serve Addressables or bundles.

---

Next: [Supported HTML](supported-html.md) · [Supported CSS](supported-css.md)
