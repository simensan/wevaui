# Weva - Phase One Demo

A minimal scene that exercises the full Weva pipeline through the shared C++ core: HTML parse, CSS cascade, layout, paint, controller binding, hot reload.

## Quick start

1. In the Package Manager, find **Weva** and click **Import** next to **Phase One Demo** in the Samples panel. Unity copies the sample into `Assets/Samples/Weva/<version>/Phase One Demo/`.
2. Open the scene `Scenes/PhaseOneDemo.unity`. It contains a `Main Camera` and an empty `DemoUI` GameObject.
3. Add the components to `DemoUI` (one-time wiring after import):
   - `WevaDocument` - drag `UI/menu.html` onto the **Document Asset** field; drag `UI/menu.css` into the **Stylesheet Assets** array.
   - `PhaseOneDemoController` - the `OnEnable` hook calls `WevaDocument.SetController(this)`.

   Or use the menu shortcut **GameObject > Weva > Phase One Demo** to spawn a fresh, fully-wired `DemoUI` into the active scene.
4. Press **Play**. The core parses `menu.html`, cascades `menu.css` over the DOM, lays it out and paints it; the URP renderer feature draws its draw list onto the camera color target.
5. Click **Start**. The controller logs `Weva demo: Start clicked. Coins=N` and increments `CoinCount`. The `{{ CoinCount }}` text node refreshes on the next frame.

## What's in the box

- `Scenes/PhaseOneDemo.unity` - minimal scene with `Main Camera` and a `DemoUI` GameObject.
- `UI/menu.html` - a minimal menu example (heading, coin count, start button).
- `UI/menu.css` - the menu styles (flex column, button hover/active).
- `UI/card-component.html` - a `<template>` plus slots example demonstrating component composition.
- `Scripts/PhaseOneDemoController.cs` - has `[UIBind] public int CoinCount;` and `public void OnStart()`, the `on-click` handler.
- `Scripts/PhaseOneDemoBootstrap.cs` - editor-only menu item that wires the components programmatically.

## Hot reload

Edit `UI/menu.html` or `UI/menu.css` while Play mode is running. The asset watcher (in the Editor assembly) detects the import and calls `WevaDocument.Reload()` on every document that references the changed asset; the controller stays attached. Saves round-trip to live UI updates in well under a second.

## If the menu does not render

- Confirm the active URP renderer asset has `UIBatchedRendererFeature` in its **Renderer Features** list (the WevaDocument inspector offers a one-click fix).
- The inspector shows the core's last error and its HTML diagnostics under the fields.
- `font-family` names that no assigned `Font` answers fall back to the package's default face (Inter); assign **Font** / **Bold** / **Italic** on the component for your own.
