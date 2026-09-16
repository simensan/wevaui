# 9-Slice Demo Sprites

Source: **Kenney UI Pack** — <https://kenney.nl/assets/ui-pack>
License: **CC0 1.0 Universal** (public domain — no attribution required, but appreciated)

## Files

| File                    | Size    | Border (L,B,R,T) | Original Source                              |
|-------------------------|---------|------------------|----------------------------------------------|
| `PanelFrame.png`        | 192×64  | 16,16,16,16      | `Blue/Default/button_rectangle_depth_border` |
| `ButtonFrame.png`       | 64×64   | 16,16,16,16      | `Blue/Default/button_square_border`          |
| `ButtonFrameHover.png`  | 192×64  | 16,16,16,16      | `Green/Default/button_rectangle_depth_border`|

## Use in Weva

The native engine uses CSS `border-image` slices. Unity Sprite border metadata
is not read by this path. From a stylesheet in `Assets/UI/`:

```css
.panel {
  border: 16px solid transparent;
  border-image-source: url("Sprites/PanelFrame.png");
  border-image-slice: 16 fill;
  border-image-width: 16px;
}
```

See [the demo](../9slice-demo.html) and the
[image-loading guide](../../../Packages/com.wevaui/Documentation~/AuthoringGuide.md#9-images-and-files).
Player builds need the image bytes through files or `AssetReader`.
