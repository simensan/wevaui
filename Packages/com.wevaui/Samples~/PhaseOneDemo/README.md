# Weva - Phase One Demo

A minimal scene that exercises the full Weva pipeline: HTML parse, CSS cascade, layout, paint, controller binding, hot reload.

## Quick start

1. In the Package Manager, find **Weva** and click **Import** next to **Phase One Demo** in the Samples panel. Unity copies the sample into `Assets/Samples/Weva/<version>/Phase One Demo/`.
2. Open the scene `Scenes/PhaseOneDemo.unity`. It contains a `Main Camera` and an empty `DemoUI` GameObject.
3. Add the components to `DemoUI` (one-time wiring after import):
   - `WevaDocument` - drag `UI/menu.html` onto the **Document Asset** field; drag `UI/menu.css` into the **Stylesheet Assets** array.
   - `PhaseOneDemoController` - the `OnEnable` hook calls `WevaDocument.SetController(this)`.

   Or use the menu shortcut **GameObject > Weva > Phase One Demo** to spawn a fresh, fully-wired `DemoUI` into the active scene.
4. Press **Play**. The HTML/CSS pipeline parses `menu.html`, cascades `menu.css` over the DOM, lays it out, paints it, and the URP backend draws it onto the camera color target.
5. Click **Start**. The controller logs `Weva demo: Start clicked. Coins=N` and increments `CoinCount`. The `{{ CoinCount }}` text node refreshes on the next pipeline tick.

## What's in the box

- `Scenes/PhaseOneDemo.unity` - minimal scene with `Main Camera` and a `DemoUI` GameObject.
- `UI/menu.html` - a minimal menu example (heading, coin count, start button).
- `UI/menu.css` - the menu styles (flex column, button hover/active).
- `UI/card-component.html` - a `<template>` plus slots example demonstrating component composition.
- `Scripts/PhaseOneDemoController.cs` - has `[UIBind] public int CoinCount;` and `public void OnStart()`.
- `Scripts/PhaseOneDemoBootstrap.cs` - editor-only menu item that wires the components programmatically.

## Hot reload

Edit `UI/menu.html` or `UI/menu.css` while Play mode is running. The asset watcher (in the Editor assembly) detects the import and calls `WevaDocument.Rebuild()` on every active document that references the changed asset. Saves round-trip to live UI updates in well under a second.

## If the menu does not render

- Confirm the active URP renderer asset has the `UIRendererFeature` in its **Renderer Features** list.
- Confirm the camera's depth target includes stencil bits (depth format >= 24).
- Confirm a TextCore SDF font asset exists for the fonts in `menu.css`'s `font-family` chain. The `TextCoreBootstrap` registers a system-default font face; if the platform default is missing, text rendering falls back to `MonoFontMetrics` (fixed-width filler).
- Check the console for `Weva: TextCoreBootstrap failed...` warnings.

## Caveats (v1 simplifications)

See the package README's "v1 simplifications worth knowing" section for the list. The most likely surprises in this demo:

- `font-family` not declared in `menu.css` falls through to the platform default registered by `TextCoreBootstrap`.
- The animation engine is wired in, but `transition` declarations on `button:hover` will only animate if the target property has a registered interpolator.
