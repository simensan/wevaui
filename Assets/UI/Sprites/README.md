# 9-Slice Demo Sprites

Source: **Kenney UI Pack** — <https://kenney.nl/assets/ui-pack>
License: **CC0 1.0 Universal** (public domain — no attribution required, but appreciated)

## Files

| File                    | Size    | Border (L,B,R,T) | Original Source                              |
|-------------------------|---------|------------------|----------------------------------------------|
| `PanelFrame.png`        | 192×64  | 16,16,16,16      | `Blue/Default/button_rectangle_depth_border` |
| `ButtonFrame.png`       | 64×64   | 16,16,16,16      | `Blue/Default/button_square_border`          |
| `ButtonFrameHover.png`  | 192×64  | 16,16,16,16      | `Green/Default/button_rectangle_depth_border`|

Borders are set in the `.meta` files via `spriteBorder`; Unity reads them
in the Sprite Editor and `SpriteImageSource` propagates them to the
engine's 9-slice resolver.

## Usage

Register at runtime:

```csharp
var sprite = Resources.Load<Sprite>("UI/Sprites/PanelFrame");
registry.Register("UI/PanelFrame", new SpriteImageSource(sprite));
```

Then in HTML/CSS:

```html
<!-- Method A: <img> with auto 9-slice -->
<img src="UI/PanelFrame" style="width:300px;height:120px" />

<!-- Method B: CSS border-image -->
<div style="border: 16px solid transparent;
            border-image-source: url(UI/PanelFrame);">
  Panel content
</div>
```

See `Assets/UI/9slice-demo.html` for a full demo.
