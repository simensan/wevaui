# Weva for Unity

Build game HUDs, menus and settings screens with HTML/CSS and C# controllers.
Weva renders through URP and shares its native layout engine with the Godot
host.

**Development preview, package 1.0.0.** Windows x64 editor and render tests pass.
A packaged player, consumer-project migration and other platforms still need
verification. See the [migration notes](CHANGELOG.md#migrating-from-01x) before
upgrading from 0.1.x.

## Requirements

- Unity **6000.3+**, **Windows x64**.
- URP **17.0.0** and Input System **1.7.0**, declared as package dependencies.
- `UIBatchedRendererFeature` on the active URP renderer.

## Quick start

1. In Package Manager, choose **Add package from disk** and select this
   package's `package.json`. If you received a `.tgz`, use **Install package
   from tarball** instead. A Git URL selects the remote revision, which may
   differ from this checkout.
2. Import **Weva → Samples → Phase One Demo** for a working example, or create
   a document with **GameObject → Weva → New WevaDocument**.
3. Use the document inspector's **Add URP Renderer Feature + shader includes**
   button if the renderer feature is missing.
4. Assign an HTML `TextAsset` and register a controller. For example:

```html
<p>Coins: {{ CoinCount }}</p>
<button id="earn" on-click="OnEarn">Earn a coin</button>
```

```csharp
using UnityEngine;
using Weva;
using Weva.Binding;

public sealed class CoinMenu : MonoBehaviour
{
    [UIBind] public int CoinCount;
    void Awake() => GetComponent<WevaDocument>().SetController(this);
    public void OnEarn() => CoinCount++;
}
```

Attach `CoinMenu` to the document's GameObject. Press Play; the button updates
the bound label. Link a stylesheet with `<link rel="stylesheet" href="menu.css">`
and keep it beside the HTML. Saving referenced assets reloads the UI.

## Build your UI

- [Getting started](Documentation~/getting-started.md): installation, rendering,
  viewport sizing and player assets.
- [Authoring guide](Documentation~/AuthoringGuide.md): bindings, lists, forms,
  events and gamepad navigation.
- [HTML](Documentation~/supported-html.md) and [CSS](Documentation~/supported-css.md):
  the supported web subset and its limits.
- [Fonts](Documentation~/text-and-fonts.md), [troubleshooting](Documentation~/troubleshooting.md)
  and [supported API](Documentation~/api-stability.md).

Use **Window → Weva → Elements** to inspect the live tree, styles and layout.
Bindings expose `[UIBind]` members; `doc.Query(selector)` returns a
`WevaElement` for values, attributes, focus and scrolling.

## Limits to plan for

- Screen-space UI only; no built-in world-space mode or JavaScript runtime.
- Unity color emoji and some script shaping are incomplete. Load fonts from
  files or `@font-face` when contextual substitutions are needed.
- Physical touch/gamepad/IME and accessibility acceptance remain incomplete.
  No screen-reader integration is provided.
- Images and font files need deployment through files or `AssetReader`.
  Test an actual player build; editor rendering is not deployment verification.
- Unity performance is not yet measured. Profile screen creation and gameplay
  on your target hardware; Godot benchmark results do not establish Unity FPS.

## License

MIT — see [LICENSE.md](LICENSE.md). Embedded dependency notices are in
[Third Party Notices](<Third Party Notices.md>).
