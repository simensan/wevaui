#if WEVA_URP
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_2023_3_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif
using Weva.Paint;
using Weva.Paint.Filters;
using Rect = Weva.Paint.Rect;
using Gradient = Weva.Paint.Gradient;

namespace Weva.Rendering {
    // Translates PaintCommands into a CommandBuffer. Owns:
    //   - a ShaderResources material pool
    //   - a Mesh that's rebuilt every frame from the MeshBuilder
    //   - clip / opacity / transform stacks
    //
    // Every quad is emitted into the shared MeshBuilder. We flush the mesh and dispatch
    // a draw whenever the active material changes (state-change boundaries), then reset the
    // builder. This trades batching efficiency for simplicity in v1; a future optimization
    // is to defer draws and build per-material sub-meshes in a single mesh upload.
    //
    // Color flow: every LinearColor is premultiplied at submit time, then passed to the
    // shader which performs (One, OneMinusSrcAlpha) blending. This matches PLAN's linear
    // color space requirement and avoids the gamma-vs-linear pre-multiply ambiguity.
    public sealed class URPRenderBackend : IRenderBackend, IDisposable {
        public static readonly int IdEffectParams = Shader.PropertyToID("_WevaEffectParams");
        public static readonly int IdViewport = Shader.PropertyToID("_WevaViewport");
        public static readonly int IdAtlasTex = Shader.PropertyToID("_AtlasTex");
        public static readonly int IdGradientStops = Shader.PropertyToID("_WevaGradientStops");
        public static readonly int IdGradientCount = Shader.PropertyToID("_WevaGradientCount");
        public static readonly int IdGradientAxis = Shader.PropertyToID("_WevaGradientAxis");
        public static readonly int IdShadowParams = Shader.PropertyToID("_WevaShadowParams");
        public static readonly int IdStencilRef = Shader.PropertyToID(StencilClipGeometry.StencilRefProperty);
        public static readonly int IdStencilComp = Shader.PropertyToID(StencilClipGeometry.StencilCompProperty);
        public static readonly int IdStencilWriteRef = Shader.PropertyToID(StencilClipGeometry.StencilWriteRefProperty);

        // CompareFunction.Equal = 3, CompareFunction.Always = 8. Set as the global int that
        // content shaders read via [_StencilComp]. When the clip stack is empty we want
        // Always (no clip); when nested we want Equal against the current ref.
        const int CompareEqual = (int)CompareFunction.Equal;
        const int CompareAlways = (int)CompareFunction.Always;

        // Active command buffer abstraction (legacy CommandBuffer or RasterCommandBuffer
        // wrapped by an adapter). Null when not inside Begin/End frame.
        public IUICommandBuffer CommandBuffer { get; private set; }

        // Backwards-compatible accessor for code that needs the underlying CommandBuffer
        // (Execute path only). Returns null in RenderGraph mode.
        public CommandBuffer LegacyCommandBuffer => (CommandBuffer as LegacyUICommandBuffer)?.Native;

        public ShaderResources Resources { get; }

        readonly MeshBuilder builder = new MeshBuilder();
        readonly MeshBuilder stencilBuilder = new MeshBuilder();
        readonly TransformStack transformStack = new TransformStack();
        readonly OpacityStack opacityStack = new OpacityStack();
        readonly ClipStack clipStack;
        readonly FilterPipeline filterPipeline;
        static bool warnedNoStencil;

        Mesh mesh;
        Mesh stencilMesh;
        Material currentMaterial;
        int viewportWidth;
        int viewportHeight;
        bool hasStencil;
        // Tracks the depth of active filter scopes. While > 0 the FilterPipeline has
        // redirected subsequent draws into an offscreen RT — we translate the transform
        // stack on Push so absolute pixel coords land in RT-local space.
        int filterDepth;
        // Throttles the K5 truncation warning to once per frame. Reset in BeginFrame so a
        // 9+ stop gradient surfaces exactly one Debug.LogWarning per frame regardless of
        // how many times it's submitted. Spec authors get a hint, log doesn't spam.
        bool warnedGradientStopOverflow;

        public URPRenderBackend() : this(new ShaderResources()) { }

        public URPRenderBackend(ShaderResources resources) {
            Resources = resources ?? throw new ArgumentNullException(nameof(resources));
            clipStack = new ClipStack(resources);
            filterPipeline = new FilterPipeline(resources);
            mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            mesh.MarkDynamic();
            stencilMesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            stencilMesh.MarkDynamic();
        }

        public ClipStack ClipStack => clipStack;

        public void BeginFrame(CommandBuffer cb, int width, int height, bool hasStencilAttachment) {
            if (cb == null) throw new ArgumentNullException(nameof(cb));
            BeginFrame(new LegacyUICommandBuffer(cb), width, height, hasStencilAttachment);
        }

#if UNITY_2023_3_OR_NEWER
        public void BeginFrame(RasterCommandBuffer cb, int width, int height, bool hasStencilAttachment) {
            if (cb == null) throw new ArgumentNullException(nameof(cb));
            BeginFrame(new RasterUICommandBuffer(cb), width, height, hasStencilAttachment);
        }
#endif

        public void BeginFrame(IUICommandBuffer cb, int width, int height, bool hasStencilAttachment) {
            CommandBuffer = cb ?? throw new ArgumentNullException(nameof(cb));
            viewportWidth = width;
            viewportHeight = height;
            hasStencil = hasStencilAttachment;
            builder.Reset();
            transformStack.Reset();
            opacityStack.Reset();
            clipStack.Reset();
            currentMaterial = null;
            filterDepth = 0;
            warnedGradientStopOverflow = false;

            cb.SetGlobalVector(IdViewport, new Vector4(width, height, width > 0 ? 1f / width : 0f, height > 0 ? 1f / height : 0f));
            // FilterPipeline only runs against a legacy CommandBuffer (it switches render
            // targets via SetRenderTarget, which RasterCommandBuffer doesn't expose). Skip
            // binding under RenderGraph; Push/PopFilter degrade to no-ops there.
            var legacy = LegacyCommandBuffer;
            if (legacy != null) {
                filterPipeline.BeginFrame(legacy, width, height);
            }
        }

        public void EndFrame() {
            FlushMesh();
            filterPipeline.EndFrame();
            CommandBuffer = null;
        }

        public void Submit(FillRectCommand command) {
            if (command.Bounds.IsEmpty) return;
            switch (command.Brush.Kind) {
                case BrushKind.SolidColor:
                    EmitSolid(command.Bounds, command.Brush.Color, command.Radii);
                    break;
                case BrushKind.Gradient:
                    EmitGradient(command.Bounds, command.Brush.GradientValue, command.Radii);
                    break;
                case BrushKind.Image:
                    // TODO: image brushes — wire up sprite/texture lookup. For now, fall back
                    // to magenta so missing-asset bugs are visible.
                    EmitSolid(command.Bounds, new LinearColor(1f, 0f, 1f, 1f), command.Radii);
                    break;
            }
        }

        public void Submit(StrokeBorderCommand command) {
            if (command.Bounds.IsEmpty || command.Borders.IsNone) return;

            var b = command.Bounds;
            var borders = command.Borders;

            // Emit four border quads (top, right, bottom, left). For uniform borders this
            // collapses to a frame; for asymmetric per-side widths we emit each side's
            // trapezoidal strip as an axis-aligned rect (mitered corners). True trapezoidal
            // miters are deferred — v1 fills overlap regions with the longer side's color,
            // matching browsers when adjacent border colors differ only at corner pixels.
            float topW = (float)borders.Top.Width;
            float rightW = (float)borders.Right.Width;
            float botW = (float)borders.Bottom.Width;
            float leftW = (float)borders.Left.Width;

            if (borders.Top.Style != BorderStyle.None && borders.Top.Style != BorderStyle.Hidden && topW > 0) {
                var r = new Rect(b.X, b.Y, b.Width, topW);
                EmitSolid(r, borders.Top.Color, BorderRadii.Zero);
            }
            if (borders.Right.Style != BorderStyle.None && borders.Right.Style != BorderStyle.Hidden && rightW > 0) {
                var r = new Rect(b.Right - rightW, b.Y, rightW, b.Height);
                EmitSolid(r, borders.Right.Color, BorderRadii.Zero);
            }
            if (borders.Bottom.Style != BorderStyle.None && borders.Bottom.Style != BorderStyle.Hidden && botW > 0) {
                var r = new Rect(b.X, b.Bottom - botW, b.Width, botW);
                EmitSolid(r, borders.Bottom.Color, BorderRadii.Zero);
            }
            if (borders.Left.Style != BorderStyle.None && borders.Left.Style != BorderStyle.Hidden && leftW > 0) {
                var r = new Rect(b.X, b.Y, leftW, b.Height);
                EmitSolid(r, borders.Left.Color, BorderRadii.Zero);
            }
        }

        public void Submit(DrawTextCommand command) {
            // Text rendering depends on the TextCore atlas integration (parallel agent).
            // The shader contract expects:
            //   _AtlasTex     : R8 SDF atlas
            //   per-vertex uv : glyph atlas UVs (TL/BL/BR/TR)
            //   per-vertex color : premultiplied LinearColor
            //   tangent       : (sdfPxRange, glyphScale, 0, 0) where sdfPxRange is the
            //                   distance-field spread in pixels at atlas resolution.
            //
            // Until TextCoreFontMetrics provides shaped glyph runs, this emits an opaque
            // box at the bounds so layout still produces visible output. Replace with the
            // real glyph-quad emission once the IFontMetrics shaping API exposes it.
            if (command.Bounds.IsEmpty) return;
            FlushIfMaterialChanges(Resources.GetText());
            // Placeholder: emit a colored rect using the solid path so text bounds are
            // visible during integration. The TextCore agent will replace this with glyph
            // quads via a method we expose: EmitGlyphQuads(...).
            currentMaterial = Resources.GetSolid();
            EmitColoredQuad(command.Bounds, ApplyOpacity(command.Color).Premultiplied(), MeshBuilder.EffectIdSolid, 0f, 0f);
        }

        public void EmitGlyphQuads(IReadOnlyList<GlyphQuad> glyphs) {
            if (glyphs == null || glyphs.Count == 0) return;
            FlushIfMaterialChanges(Resources.GetText());
            var t = transformStack.Current;
            for (int i = 0; i < glyphs.Count; i++) {
                var g = glyphs[i];
                var color = ApplyOpacity(g.Color).Premultiplied();
                builder.AddTexturedQuad(g.Bounds, color, MeshBuilder.EffectIdText,
                    (g.UvTL.x, g.UvTL.y), (g.UvBL.x, g.UvBL.y), (g.UvBR.x, g.UvBR.y), (g.UvTR.x, g.UvTR.y), t);
            }
        }

        public void Submit(DrawShadowCommand command) {
            if (command.Bounds.IsEmpty) return;
            FlushIfMaterialChanges(Resources.GetShadow());
            currentMaterial = Resources.GetShadow();
            // Expand bounds by blur+spread so the gaussian falls off inside the quad.
            var s = command.Shadow;
            double pad = s.BlurRadius + Math.Abs(s.SpreadRadius);
            var r = new Rect(
                command.Bounds.X + s.OffsetX - pad,
                command.Bounds.Y + s.OffsetY - pad,
                command.Bounds.Width + pad * 2,
                command.Bounds.Height + pad * 2);
            var color = ApplyOpacity(s.Color).Premultiplied();
            // Pack shadow params into the tangent: (innerHalfW, innerHalfH, blurRadius, spread).
            var t = transformStack.Current;
            float halfW = (float)(command.Bounds.Width * 0.5);
            float halfH = (float)(command.Bounds.Height * 0.5);
            float effectId = s.Inset ? -MeshBuilder.EffectIdShadow : MeshBuilder.EffectIdShadow;

            double tlx = r.X, tly = r.Y;
            double trx = r.Right, tryy = r.Y;
            double brx = r.Right, bry = r.Bottom;
            double blx = r.X, bly = r.Bottom;
            var (atlx, atly) = t.Apply(tlx, tly);
            var (atrx, atry) = t.Apply(trx, tryy);
            var (abrx, abry) = t.Apply(brx, bry);
            var (ablx, ably) = t.Apply(blx, bly);

            int i0 = builder.Vertices.Count;
            // We can't add via AddQuad (the tangent semantics differ for shadows). Fall
            // back to AddTexturedQuad which lets us choose UVs explicitly; we put pixel
            // offsets relative to the inner rect into UV so the shader knows the geometry.
            builder.AddTexturedQuad(r, color, effectId,
                (-(float)pad, -(float)pad),
                (-(float)pad, halfH * 2 + (float)pad),
                (halfW * 2 + (float)pad, halfH * 2 + (float)pad),
                (halfW * 2 + (float)pad, -(float)pad),
                t);
            // Tangent params for shadow are global per draw — pushed via shader globals.
            CommandBuffer.SetGlobalVector(IdShadowParams, new Vector4(halfW, halfH, (float)s.BlurRadius, (float)s.SpreadRadius));
        }

        public void Submit(PushClipCommand command) {
            FlushMesh();
            int parentRef = clipStack.CurrentStencilRef;
            bool pushed = clipStack.TryPush(command.Bounds, command.Radii, transformStack.Current);
            if (!pushed) return;
            if (!hasStencil) {
                if (!warnedNoStencil) {
                    warnedNoStencil = true;
                    Debug.LogWarning("Weva: stencil attachment unavailable; clip pass degrades to no-op. Configure URP camera depth+stencil format to enable rounded-rect clipping.");
                }
                UpdateStencilGlobals();
                return;
            }
            // Push: test against parent ref, IncrSat. Scopes the increment to the parent's
            // clip region so nested clips intersect their parents.
            DrawStencilMask(command.Bounds, command.Radii, StencilClipGeometry.PushPassIndex, parentRef);
            UpdateStencilGlobals();
        }

        public void Submit(PopClipCommand command) {
            FlushMesh();
            if (clipStack.Depth == 0) return;
            var top = clipStack.Top;
            int currentRef = clipStack.CurrentStencilRef;
            if (hasStencil) {
                // Pop: test against the ref we're popping (== currentRef), DecrSat. Undoes
                // exactly the increment that the matching Push performed.
                DrawStencilMask(top.Bounds, top.Radii, StencilClipGeometry.PopPassIndex, currentRef);
            }
            clipStack.TryPop();
            UpdateStencilGlobals();
        }

        void UpdateStencilGlobals() {
            if (CommandBuffer == null) return;
            int refValue = clipStack.CurrentStencilRef;
            CommandBuffer.SetGlobalInt(IdStencilRef, refValue);
            CommandBuffer.SetGlobalInt(IdStencilComp, refValue > 0 ? CompareEqual : CompareAlways);
        }

        void DrawStencilMask(Rect bounds, BorderRadii radii, int passIndex, int writeRef) {
            var mat = Resources.GetStencilWrite();
            if (mat == null || CommandBuffer == null) return;
            stencilBuilder.Reset();
            StencilClipGeometry.EncodeClipMask(stencilBuilder, bounds, radii, transformStack.Current);
            UploadMeshFrom(stencilMesh, stencilBuilder);
            // The shader's Stencil { Ref [_StencilWriteRef]; Comp Equal; ... } block needs
            // the ref BEFORE the draw. Pass=IncrSat (push) tests against parent ref; Pass=
            // DecrSat (pop) tests against the ref we're popping.
            CommandBuffer.SetGlobalInt(IdStencilWriteRef, writeRef);
            CommandBuffer.DrawMesh(stencilMesh, Matrix4x4.identity, mat, 0, passIndex);
        }

        public void Submit(PushOpacityCommand command) {
            opacityStack.Push((float)command.Opacity);
        }

        public void Submit(PopOpacityCommand command) {
            opacityStack.Pop();
        }

        public void Submit(PushTransformCommand command) {
            FlushMesh();
            transformStack.Push(command.Transform);
        }

        public void Submit(PopTransformCommand command) {
            FlushMesh();
            transformStack.Pop();
        }

        public void Submit(PushFilterCommand command) {
            FlushMesh();
            // FilterPipeline.Push allocates a temp RT sized to the filter's pixel-space
            // bounds + halo padding, redirects the active render target to that RT, and
            // pushes a viewport global keyed to the RT. We then translate the transform
            // stack so subsequent paint commands that emit in absolute pixel coordinates
            // land inside the RT. Pop reverses both.
            if (filterPipeline.IsSupported
                && filterPipeline.Push(command.Bounds, command.Filters, transformStack.Current)) {
                if (filterPipeline.TryPeekFrame(out int offX, out int offY)) {
                    transformStack.Push(Transform2D.Translate(-offX, -offY));
                }
                filterDepth++;
            }
        }

        public void Submit(PopFilterCommand command) {
            FlushMesh();
            if (filterDepth > 0) {
                transformStack.Pop();
                filterPipeline.Pop();
                CommandBuffer.SetGlobalVector(IdViewport,
                    new Vector4(viewportWidth, viewportHeight,
                        viewportWidth > 0 ? 1f / viewportWidth : 0f,
                        viewportHeight > 0 ? 1f / viewportHeight : 0f));
                filterDepth--;
            }
        }

        public void Submit(BeginSubtreeCaptureCommand command) { }
        public void Submit(EndSubtreeCaptureCommand command) { }
        public void Submit(ReplaySubtreeSnapshotCommand command) { }

        void EmitSolid(Rect bounds, LinearColor color, BorderRadii radii) {
            FlushIfMaterialChanges(Resources.GetSolid());
            currentMaterial = Resources.GetSolid();
            var c = ApplyOpacity(color).Premultiplied();
            float rx, ry;
            (rx, ry) = RoundRectSdf.PackUniform(radii);
            EmitColoredQuad(bounds, c, MeshBuilder.EffectIdSolid, rx, ry);
        }

        void EmitGradient(Rect bounds, Gradient gradient, BorderRadii radii) {
            FlushIfMaterialChanges(Resources.GetGradient());
            currentMaterial = Resources.GetGradient();
            // Upload gradient stops as a small array (max 8 stops in v1; longer gradients
            // get truncated with a runtime warning).
            var stops = gradient.Stops;
            // SHADER COUPLING: this value MUST match the shader uniform array
            // dimension in Weva_Gradient.shader. Search the shader for
            // "ColorStops[8]" / "StopPositions[8]" and update both sites in
            // lockstep if the cap changes.
            const int MaxStops = 8;
            int count = Math.Min(stops.Count, MaxStops);
            // K5: surface a one-shot warning when the author hands us more stops than the
            // legacy backend's fixed-size shader array can hold. Throttled to once per
            // frame so the log doesn't spam when the same gradient re-submits each frame.
            // The active batched RenderGraph path (UIBatcher) packs stop positions per
            // instance and does not share this cap — mention that so authors know which
            // path to migrate to.
            if (stops.Count > MaxStops && !warnedGradientStopOverflow) {
                warnedGradientStopOverflow = true;
                var firstColor = stops[0].Color;
                Debug.LogWarning(
                    $"Weva: gradient has {stops.Count} stops but the legacy URPRenderBackend " +
                    $"shader uniform caps at {MaxStops}; stops beyond index {MaxStops - 1} are dropped " +
                    $"(first stop color rgba=({firstColor.R:0.##},{firstColor.G:0.##},{firstColor.B:0.##},{firstColor.A:0.##}) " +
                    $"at position {stops[0].Position:0.###}). The batched RenderGraph backend " +
                    $"(BatchedURPRenderBackend / UIBatcher) handles more stops per instance.");
            }
            var stopArray = new Vector4[MaxStops];
            for (int i = 0; i < count; i++) {
                var s = stops[i];
                var color = ApplyOpacity(s.Color).Premultiplied();
                stopArray[i] = new Vector4(color.R, color.G, color.B, color.A);
            }
            CommandBuffer.SetGlobalVectorArray(IdGradientStops, stopArray);
            CommandBuffer.SetGlobalInt(IdGradientCount, count);

            // Linear gradient axis: convert CSS angle to a unit vector in box-
            // local UV space (y-down). CSS Images 3 §3.1: 0deg points "to top",
            // angle increases clockwise — so 0deg=(0,-1), 90deg=(1,0),
            // 180deg=(0,1). Encoded as (sin θ, -cos θ). Previously used
            // (cos θ, sin θ) which is the math-convention "right at 0°",
            // rotating every linear-gradient by 90° counter-clockwise (visible
            // as match3's `.goal-fill { 90deg pink→gold }` painting top-to-
            // bottom instead of left-to-right and the bomb tile's 135deg
            // gradient running along the wrong diagonal). Radial uses
            // (cx, cy, rx, ry).
            if (gradient is LinearGradient lin) {
                double rad = lin.AngleDegrees * Math.PI / 180.0;
                CommandBuffer.SetGlobalVector(IdGradientAxis,
                    new Vector4((float)Math.Sin(rad), -(float)Math.Cos(rad), 0f, 0f));
            } else if (gradient is RadialGradient rad) {
                CommandBuffer.SetGlobalVector(IdGradientAxis,
                    new Vector4((float)rad.CenterX, (float)rad.CenterY, (float)rad.RadiusX, (float)rad.RadiusY));
            }

            float rx, ry;
            (rx, ry) = RoundRectSdf.PackUniform(radii);
            EmitColoredQuad(bounds, LinearColor.White.Premultiplied(), MeshBuilder.EffectIdGradient, rx, ry);
        }

        void EmitColoredQuad(Rect bounds, LinearColor color, float effectId, float rx, float ry) {
            builder.AddQuad(bounds, color, effectId, rx, ry, transformStack.Current);
        }

        LinearColor ApplyOpacity(LinearColor c) {
            float a = opacityStack.Current;
            if (a >= 1f) return c;
            return new LinearColor(c.R, c.G, c.B, c.A * a);
        }

        void FlushIfMaterialChanges(Material next) {
            if (currentMaterial != null && next != null && currentMaterial != next) {
                FlushMesh();
            }
        }

        void FlushMesh() {
            if (CommandBuffer == null) return;
            int vCount = builder.VertexCount;
            int iCount = builder.IndexCount;
            if (vCount == 0 || iCount == 0 || currentMaterial == null) {
                builder.Reset();
                return;
            }

            UploadMeshFrom(mesh, builder);
            CommandBuffer.DrawMesh(mesh, Matrix4x4.identity, currentMaterial);
            builder.Reset();
        }

        void UploadMeshFrom(Mesh target, MeshBuilder b) {
            int n = b.VertexCount;
            if (n == 0) return;

            var positions = new Vector3[n];
            var uvs = new Vector2[n];
            var colors = new Color[n];
            var tangents = new Vector4[n];
            for (int i = 0; i < n; i++) {
                var v = b.Vertices[i];
                positions[i] = new Vector3(v.Px, v.Py, v.Pz);
                uvs[i] = new Vector2(v.Uvx, v.Uvy);
                colors[i] = new Color(v.Color.R, v.Color.G, v.Color.B, v.Color.A);
                tangents[i] = new Vector4(v.Tx, v.Ty, v.Tz, v.Tw);
            }
            int iCount = b.IndexCount;
            var idx = new int[iCount];
            for (int i = 0; i < iCount; i++) idx[i] = b.Indices[i];

            target.Clear();
            target.indexFormat = IndexFormat.UInt32;
            target.SetVertices(positions);
            target.SetUVs(0, uvs);
            target.SetColors(colors);
            target.SetTangents(tangents);
            target.SetIndices(idx, MeshTopology.Triangles, 0);
            target.UploadMeshData(false);
        }

        public void Dispose() {
            if (mesh != null) {
#if UNITY_EDITOR
                if (Application.isPlaying) UnityEngine.Object.Destroy(mesh);
                else UnityEngine.Object.DestroyImmediate(mesh);
#else
                UnityEngine.Object.Destroy(mesh);
#endif
                mesh = null;
            }
            if (stencilMesh != null) {
#if UNITY_EDITOR
                if (Application.isPlaying) UnityEngine.Object.Destroy(stencilMesh);
                else UnityEngine.Object.DestroyImmediate(stencilMesh);
#else
                UnityEngine.Object.Destroy(stencilMesh);
#endif
                stencilMesh = null;
            }
            filterPipeline?.Dispose();
            Resources?.Dispose();
        }

        public readonly struct GlyphQuad {
            public readonly Rect Bounds;
            public readonly LinearColor Color;
            public readonly Vector2 UvTL;
            public readonly Vector2 UvBL;
            public readonly Vector2 UvBR;
            public readonly Vector2 UvTR;

            public GlyphQuad(Rect bounds, LinearColor color, Vector2 uvTL, Vector2 uvBL, Vector2 uvBR, Vector2 uvTR) {
                Bounds = bounds; Color = color; UvTL = uvTL; UvBL = uvBL; UvBR = uvBR; UvTR = uvTR;
            }
        }
    }
}
#endif
