using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Weva.Rendering.URP {
    // Per-quad instance data uploaded to the batched UI über-shader. Layout matches
    // the HLSL constant-buffer entry in Weva-Quad.shader; the StructLayout pin
    // keeps the field order identical between C# and HLSL.
    //
    // Float layout packed the same as the shader StructuredBuffer:
    // shader CBUFFER:
    //   [0]  posSize        : centerX, centerY, halfW, halfH
    //   [1]  radii          : rTL, rTR, rBR, rBL  (uniform per-corner radius)
    //   [2]  color          : r, g, b, a (premultiplied)
    //   [3]  brushParams    : brushIndex, gradStart, gradEnd, padding
    //   [4]  borderWidths   : top, right, bottom, left
    //   [5]  borderColorTop : r, g, b, a
    //   [6]  borderColorRight : r, g, b, a
    //   [7]  borderColorBot : r, g, b, a
    //   [8]  borderColorLeft : r, g, b, a
    //   [9]  borderStyles   : packed bitfield (top|right|bottom|left, 8 bits each)
    //  [10..12]  transform 2x3 : rows of the model matrix (R0=A,B; R1=C,D; R2=Tx,Ty)
    //  [13]  clipRect       : xmin, ymin, xmax, ymax (axis-aligned scissor in
    //                          pixel space; the shader discards fragments
    //                          outside this rect). Sentinel (-1e9..1e9) =
    //                          no clip. Encodes the intersection of all
    //                          PushClip rects active when the quad was
    //                          submitted, replacing the fragile FF-stencil
    //                          path that silently failed on Unity 6 / URP RG
    //                          (the stencil bits never reached the post-FX
    //                          depth target). Repurposes the previously-
    //                          unused TransformRow3 slot — no instance-
    //                          stride change.
    //  [14]  gradientStop4  : 5th gradient stop color (used only by conic
    //                          gradients with stopCount > 4). Zero for every
    //                          other quad kind. The renderer still uploads
    //                          all 16 float4s per instance — the over-pay
    //                          for non-gradient quads is one extra Vector4
    //                          copy + GPU upload, ~3% of the stride.
    //  [15]  gradientStop5  : 6th gradient stop color (used only by conic
    //                          gradients with stopCount > 5). The shader
    //                          reads stops 5/6 positions from
    //                          BorderStyles.y/.z (slot 9), which is free for
    //                          non-bordered conic quads.
    //
    //  [16]  clipShape0     : clip-path type + first parameters
    //  [17..20] clipShape1-4: clip-path parameters / up to 8 polygon points
    //  [21..29] mask layer 0: params0, bounds, tile, params1, colors0-3, positions
    //  [30..38] mask layer 1
    //  [39..47] mask layer 2
    //  [48..56] mask layer 3
    //  mask params0        : mask type, packed(mode/composite/layerCount), repeatX, repeatY
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct UIQuadInstance : IEquatable<UIQuadInstance> {
        public Vector4 PosSize;
        public Vector4 Radii;
        public Vector4 Color;
        public Vector4 BrushParams;
        public Vector4 BorderWidths;
        public Vector4 BorderColorTop;
        public Vector4 BorderColorRight;
        public Vector4 BorderColorBottom;
        public Vector4 BorderColorLeft;
        public Vector4 BorderStyles;
        public Vector4 TransformRow0;
        public Vector4 TransformRow1;
        public Vector4 TransformRow2;
        // Slot 13: axis-aligned clip rect in pixel space (xmin, ymin, xmax, ymax).
        // The shader discards fragments outside the rect. Replaces the stencil-
        // based clip path which silently failed on Unity 6 / URP RG.
        public Vector4 ClipRect;
        // Slot 14: 5th gradient stop color (conic > 4 stops only). Zero
        // otherwise. See top-of-file slot map.
        public Vector4 GradientStop4;
        // Slot 15: 6th gradient stop color (conic > 5 stops only). Positions
        // for stops 5/6 piggyback on BorderStyles.y/.z (slot 9).
        public Vector4 GradientStop5;
        public Vector4 ClipShape0;
        public Vector4 ClipShape1;
        public Vector4 ClipShape2;
        public Vector4 ClipShape3;
        public Vector4 ClipShape4;
        public Vector4 MaskParams0;
        public Vector4 MaskBounds;
        public Vector4 MaskTile;
        public Vector4 MaskParams1;
        public Vector4 MaskColor0;
        public Vector4 MaskColor1;
        public Vector4 MaskColor2;
        public Vector4 MaskColor3;
        public Vector4 MaskPositions;
        public Vector4 Mask1Params0;
        public Vector4 Mask1Bounds;
        public Vector4 Mask1Tile;
        public Vector4 Mask1Params1;
        public Vector4 Mask1Color0;
        public Vector4 Mask1Color1;
        public Vector4 Mask1Color2;
        public Vector4 Mask1Color3;
        public Vector4 Mask1Positions;
        public Vector4 Mask2Params0;
        public Vector4 Mask2Bounds;
        public Vector4 Mask2Tile;
        public Vector4 Mask2Params1;
        public Vector4 Mask2Color0;
        public Vector4 Mask2Color1;
        public Vector4 Mask2Color2;
        public Vector4 Mask2Color3;
        public Vector4 Mask2Positions;
        public Vector4 Mask3Params0;
        public Vector4 Mask3Bounds;
        public Vector4 Mask3Tile;
        public Vector4 Mask3Params1;
        public Vector4 Mask3Color0;
        public Vector4 Mask3Color1;
        public Vector4 Mask3Color2;
        public Vector4 Mask3Color3;
        public Vector4 Mask3Positions;
        // Slot 57: per-corner VERTICAL radii (rTL, rTR, rBR, rBL). The Radii
        // slot (1) carries the horizontal radii; together they describe
        // elliptical corners (`border-radius: 70px / 48px`). For circular
        // corners RadiiY == Radii and the shader's per-axis SDF collapses to
        // the exact circular path. A zero component means "fall back to the
        // matching Radii component" so quads that never set RadiiY (default
        // zero) stay circular — backward compatible with every prior caller.
        public Vector4 RadiiY;

        public const int Float4Count = 58;
        public const int FloatCount = Float4Count * 4;

        public bool Equals(UIQuadInstance other) {
            return PosSize == other.PosSize
                && Radii == other.Radii
                && Color == other.Color
                && BrushParams == other.BrushParams
                && BorderWidths == other.BorderWidths
                && BorderColorTop == other.BorderColorTop
                && BorderColorRight == other.BorderColorRight
                && BorderColorBottom == other.BorderColorBottom
                && BorderColorLeft == other.BorderColorLeft
                && BorderStyles == other.BorderStyles
                && TransformRow0 == other.TransformRow0
                && TransformRow1 == other.TransformRow1
                && TransformRow2 == other.TransformRow2
                && ClipRect == other.ClipRect
                && GradientStop4 == other.GradientStop4
                && GradientStop5 == other.GradientStop5
                && ClipShape0 == other.ClipShape0
                && ClipShape1 == other.ClipShape1
                && ClipShape2 == other.ClipShape2
                && ClipShape3 == other.ClipShape3
                && ClipShape4 == other.ClipShape4
                && MaskParams0 == other.MaskParams0
                && MaskBounds == other.MaskBounds
                && MaskTile == other.MaskTile
                && MaskParams1 == other.MaskParams1
                && MaskColor0 == other.MaskColor0
                && MaskColor1 == other.MaskColor1
                && MaskColor2 == other.MaskColor2
                && MaskColor3 == other.MaskColor3
                && MaskPositions == other.MaskPositions
                && Mask1Params0 == other.Mask1Params0
                && Mask1Bounds == other.Mask1Bounds
                && Mask1Tile == other.Mask1Tile
                && Mask1Params1 == other.Mask1Params1
                && Mask1Color0 == other.Mask1Color0
                && Mask1Color1 == other.Mask1Color1
                && Mask1Color2 == other.Mask1Color2
                && Mask1Color3 == other.Mask1Color3
                && Mask1Positions == other.Mask1Positions
                && Mask2Params0 == other.Mask2Params0
                && Mask2Bounds == other.Mask2Bounds
                && Mask2Tile == other.Mask2Tile
                && Mask2Params1 == other.Mask2Params1
                && Mask2Color0 == other.Mask2Color0
                && Mask2Color1 == other.Mask2Color1
                && Mask2Color2 == other.Mask2Color2
                && Mask2Color3 == other.Mask2Color3
                && Mask2Positions == other.Mask2Positions
                && Mask3Params0 == other.Mask3Params0
                && Mask3Bounds == other.Mask3Bounds
                && Mask3Tile == other.Mask3Tile
                && Mask3Params1 == other.Mask3Params1
                && Mask3Color0 == other.Mask3Color0
                && Mask3Color1 == other.Mask3Color1
                && Mask3Color2 == other.Mask3Color2
                && Mask3Color3 == other.Mask3Color3
                && Mask3Positions == other.Mask3Positions
                && RadiiY == other.RadiiY;
        }

        public override bool Equals(object obj) => obj is UIQuadInstance other && Equals(other);
        public override int GetHashCode() {
            unchecked {
                int h = PosSize.GetHashCode();
                h = (h * 397) ^ Radii.GetHashCode();
                h = (h * 397) ^ Color.GetHashCode();
                h = (h * 397) ^ BrushParams.GetHashCode();
                h = (h * 397) ^ BorderWidths.GetHashCode();
                h = (h * 397) ^ BorderColorTop.GetHashCode();
                h = (h * 397) ^ BorderColorRight.GetHashCode();
                h = (h * 397) ^ BorderColorBottom.GetHashCode();
                h = (h * 397) ^ BorderColorLeft.GetHashCode();
                h = (h * 397) ^ BorderStyles.GetHashCode();
                h = (h * 397) ^ TransformRow0.GetHashCode();
                h = (h * 397) ^ TransformRow1.GetHashCode();
                h = (h * 397) ^ TransformRow2.GetHashCode();
                h = (h * 397) ^ ClipRect.GetHashCode();
                h = (h * 397) ^ GradientStop4.GetHashCode();
                h = (h * 397) ^ GradientStop5.GetHashCode();
                h = (h * 397) ^ ClipShape0.GetHashCode();
                h = (h * 397) ^ ClipShape1.GetHashCode();
                h = (h * 397) ^ ClipShape2.GetHashCode();
                h = (h * 397) ^ ClipShape3.GetHashCode();
                h = (h * 397) ^ ClipShape4.GetHashCode();
                h = (h * 397) ^ MaskParams0.GetHashCode();
                h = (h * 397) ^ MaskBounds.GetHashCode();
                h = (h * 397) ^ MaskTile.GetHashCode();
                h = (h * 397) ^ MaskParams1.GetHashCode();
                h = (h * 397) ^ MaskColor0.GetHashCode();
                h = (h * 397) ^ MaskColor1.GetHashCode();
                h = (h * 397) ^ MaskColor2.GetHashCode();
                h = (h * 397) ^ MaskColor3.GetHashCode();
                h = (h * 397) ^ MaskPositions.GetHashCode();
                h = (h * 397) ^ Mask1Params0.GetHashCode();
                h = (h * 397) ^ Mask1Bounds.GetHashCode();
                h = (h * 397) ^ Mask1Tile.GetHashCode();
                h = (h * 397) ^ Mask1Params1.GetHashCode();
                h = (h * 397) ^ Mask1Color0.GetHashCode();
                h = (h * 397) ^ Mask1Color1.GetHashCode();
                h = (h * 397) ^ Mask1Color2.GetHashCode();
                h = (h * 397) ^ Mask1Color3.GetHashCode();
                h = (h * 397) ^ Mask1Positions.GetHashCode();
                h = (h * 397) ^ Mask2Params0.GetHashCode();
                h = (h * 397) ^ Mask2Bounds.GetHashCode();
                h = (h * 397) ^ Mask2Tile.GetHashCode();
                h = (h * 397) ^ Mask2Params1.GetHashCode();
                h = (h * 397) ^ Mask2Color0.GetHashCode();
                h = (h * 397) ^ Mask2Color1.GetHashCode();
                h = (h * 397) ^ Mask2Color2.GetHashCode();
                h = (h * 397) ^ Mask2Color3.GetHashCode();
                h = (h * 397) ^ Mask2Positions.GetHashCode();
                h = (h * 397) ^ Mask3Params0.GetHashCode();
                h = (h * 397) ^ Mask3Bounds.GetHashCode();
                h = (h * 397) ^ Mask3Tile.GetHashCode();
                h = (h * 397) ^ Mask3Params1.GetHashCode();
                h = (h * 397) ^ Mask3Color0.GetHashCode();
                h = (h * 397) ^ Mask3Color1.GetHashCode();
                h = (h * 397) ^ Mask3Color2.GetHashCode();
                h = (h * 397) ^ Mask3Color3.GetHashCode();
                h = (h * 397) ^ Mask3Positions.GetHashCode();
                h = (h * 397) ^ RadiiY.GetHashCode();
                return h;
            }
        }
    }

    // Brush kind used by the quad shader. Mirrors `_BRUSH_*` keywords; the BrushParams.x
    // value lets the shader switch without a keyword change inside a single material.
    public enum UIQuadBrush {
        Solid = 0,
        LinearGradient = 1,
        RadialGradient = 2,
        ConicGradient = 3,
        Image = 4,
        Shadow = 5,
        ShadowInset = 6,
        Text = 7
    }

    // Border style bits packed into BorderStyles.x (top), .y (right), .z (bottom), .w (left).
    // Values mirror Weva.Paint.BorderStyle ordinal so the conversion is a cast.
    public enum UIQuadBorderStyle {
        None = 0,
        Solid = 1,
        Dashed = 2,
        Dotted = 3,
        Double = 4
    }
}
