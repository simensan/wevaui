using System.Collections.Generic;
using Weva.Paint.Filters;

namespace Weva.Paint.Conversion {
    // Pool of reusable paint command instances. The converter rents commands
    // when emitting and returns the entire batch to the pool when the caller
    // is done with the produced PaintList.
    //
    // Lifetime contract:
    //   - Rent*() returns a command with its fields populated by the caller's
    //     Set() arguments. The instance is owned by whichever PaintList it gets
    //     added to until that list is returned via ReturnAll(list).
    //   - ReturnAll(list) walks the commands in the list, returns each pool-able
    //     command to its per-type stack, and clears the list. Pop singletons
    //     are skipped (they're never pool entries — see PaintCommandSingletons).
    //   - The pool is single-threaded, mirroring PaintConverterPools.
    //
    // Cap rationale: a 5000-box scene emits ~20K commands. Cap at 16K total
    // commands (4K per common type) so the pool never permanently retains the
    // worst-case set yet stays warm for the steady-state 500-1000-box trees the
    // bench targets.
    public sealed class PaintCommandPool {
        public const int DefaultMaxPerType = 4096;

        readonly Stack<FillRectCommand> fillRects;
        readonly Stack<StrokeBorderCommand> strokeBorders;
        readonly Stack<DrawTextCommand> drawTexts;
        readonly Stack<DrawShadowCommand> drawShadows;
        readonly Stack<DrawBackdropFilterCommand> backdropFilters;
        readonly Stack<PushClipCommand> pushClips;
        readonly Stack<PushClipPathCommand> pushClipPaths;
        readonly Stack<PushMaskCommand> pushMasks;
        readonly Stack<PushOpacityCommand> pushOpacities;
        readonly Stack<PushTransformCommand> pushTransforms;
        readonly Stack<PushFilterCommand> pushFilters;
        readonly Stack<PushMixBlendModeCommand> pushMixBlendModes;
        readonly Stack<PushBackgroundBlendCommand> pushBackgroundBlends;
        // Subtree-capture markers. VisitBox emits one Begin + one End per
        // captured subtree, and one ReplaySubtreeSnapshot per replayed
        // subtree. Previously these were freshly allocated each visit
        // — ~50 B each × hundreds of visits per frame.
        readonly Stack<BeginSubtreeCaptureCommand> beginSubtrees;
        readonly Stack<EndSubtreeCaptureCommand> endSubtrees;
        readonly Stack<ReplaySubtreeSnapshotCommand> replaySubtrees;
        readonly int maxPerType;

        public PaintCommandPool() : this(DefaultMaxPerType) { }

        public PaintCommandPool(int maxPerType) {
            this.maxPerType = maxPerType > 0 ? maxPerType : DefaultMaxPerType;
            fillRects = new Stack<FillRectCommand>(64);
            strokeBorders = new Stack<StrokeBorderCommand>(64);
            drawTexts = new Stack<DrawTextCommand>(16);
            drawShadows = new Stack<DrawShadowCommand>(16);
            backdropFilters = new Stack<DrawBackdropFilterCommand>(8);
            pushClips = new Stack<PushClipCommand>(16);
            pushClipPaths = new Stack<PushClipPathCommand>(8);
            pushMasks = new Stack<PushMaskCommand>(8);
            pushOpacities = new Stack<PushOpacityCommand>(16);
            pushTransforms = new Stack<PushTransformCommand>(16);
            pushFilters = new Stack<PushFilterCommand>(16);
            pushMixBlendModes = new Stack<PushMixBlendModeCommand>(8);
            pushBackgroundBlends = new Stack<PushBackgroundBlendCommand>(8);
            beginSubtrees = new Stack<BeginSubtreeCaptureCommand>(32);
            endSubtrees = new Stack<EndSubtreeCaptureCommand>(32);
            replaySubtrees = new Stack<ReplaySubtreeSnapshotCommand>(64);
        }

        public BeginSubtreeCaptureCommand RentBeginSubtreeCapture(Weva.Layout.Boxes.Box box, double anchorX, double anchorY) {
            var cmd = beginSubtrees.Count > 0 ? beginSubtrees.Pop() : new BeginSubtreeCaptureCommand();
            cmd.Set(box, anchorX, anchorY);
            return cmd;
        }

        public EndSubtreeCaptureCommand RentEndSubtreeCapture(Weva.Layout.Boxes.Box box) {
            var cmd = endSubtrees.Count > 0 ? endSubtrees.Pop() : new EndSubtreeCaptureCommand();
            cmd.Set(box);
            return cmd;
        }

        public ReplaySubtreeSnapshotCommand RentReplaySubtreeSnapshot(object snapshot, double offsetX, double offsetY) {
            var cmd = replaySubtrees.Count > 0 ? replaySubtrees.Pop() : new ReplaySubtreeSnapshotCommand();
            cmd.Set(snapshot, offsetX, offsetY);
            return cmd;
        }

        public FillRectCommand RentFillRect(Rect bounds, Brush brush, BorderRadii radii) {
            FillRectCommand cmd = fillRects.Count > 0 ? fillRects.Pop() : new FillRectCommand();
            cmd.Set(bounds, brush, radii);
            return cmd;
        }

        public StrokeBorderCommand RentStrokeBorder(Rect bounds, Borders borders, BorderRadii radii) {
            StrokeBorderCommand cmd = strokeBorders.Count > 0 ? strokeBorders.Pop() : new StrokeBorderCommand();
            cmd.Set(bounds, borders, radii);
            return cmd;
        }

        public DrawTextCommand RentDrawText(Rect bounds, string text, FontHandle font, LinearColor color, TextDecoration decoration) {
            return RentDrawText(bounds, text, font, color, decoration, 0);
        }

        public DrawTextCommand RentDrawText(Rect bounds, string text, FontHandle font, LinearColor color, TextDecoration decoration, double letterSpacingPx) {
            DrawTextCommand cmd = drawTexts.Count > 0 ? drawTexts.Pop() : new DrawTextCommand();
            cmd.Set(bounds, text, font, color, decoration, letterSpacingPx);
            return cmd;
        }

        // Text-shadow overload — wires the CSS Text Decoration §6 blur-radius
        // through. The renderer interprets blurRadius as an SDF dilation
        // distance (Path A). Decoration fields default to "no decoration"
        // because text-shadow paints under-but-not-decorated.
        public DrawTextCommand RentDrawText(Rect bounds, string text, FontHandle font, LinearColor color, TextDecoration decoration, double letterSpacingPx, double blurRadius) {
            DrawTextCommand cmd = drawTexts.Count > 0 ? drawTexts.Pop() : new DrawTextCommand();
            cmd.Set(bounds, text, font, color, decoration, letterSpacingPx, blurRadius);
            return cmd;
        }

        // Pool overload that wires CSS Text Decoration 4 fields (color override,
        // style, thickness, offset) onto the rented command. `decorationColor`
        // null = "use the run color", matching the back-compat default ctor.
        public DrawTextCommand RentDrawText(Rect bounds, string text, FontHandle font, LinearColor color,
                                            TextDecoration decoration, double letterSpacingPx,
                                            LinearColor? decorationColor, DecorationStyle decorationStyle,
                                            double decorationThickness, double decorationOffset) {
            DrawTextCommand cmd = drawTexts.Count > 0 ? drawTexts.Pop() : new DrawTextCommand();
            cmd.Set(bounds, text, font, color, decoration, letterSpacingPx,
                    decorationColor, decorationStyle, decorationThickness, decorationOffset);
            return cmd;
        }

        public DrawShadowCommand RentDrawShadow(Rect bounds, BorderRadii radii, BoxShadow shadow) {
            DrawShadowCommand cmd = drawShadows.Count > 0 ? drawShadows.Pop() : new DrawShadowCommand();
            cmd.Set(bounds, radii, shadow);
            return cmd;
        }

        public DrawBackdropFilterCommand RentDrawBackdropFilter(Rect bounds, BorderRadii radii, Weva.Paint.Filters.FilterChain filters) {
            DrawBackdropFilterCommand cmd = backdropFilters.Count > 0 ? backdropFilters.Pop() : new DrawBackdropFilterCommand();
            cmd.Set(bounds, radii, filters);
            return cmd;
        }

        public PushClipCommand RentPushClip(Rect bounds, BorderRadii radii) {
            PushClipCommand cmd = pushClips.Count > 0 ? pushClips.Pop() : new PushClipCommand();
            cmd.Set(bounds, radii);
            return cmd;
        }

        public PushClipCommand RentPushClip(Rect bounds) => RentPushClip(bounds, BorderRadii.Zero);

        public PushClipPathCommand RentPushClipPath(ClipPathShape shape) {
            PushClipPathCommand cmd = pushClipPaths.Count > 0 ? pushClipPaths.Pop() : new PushClipPathCommand();
            cmd.Set(shape);
            return cmd;
        }

        public PushMaskCommand RentPushMask(Rect bounds, MaskDefinition mask) {
            PushMaskCommand cmd = pushMasks.Count > 0 ? pushMasks.Pop() : new PushMaskCommand();
            cmd.Set(bounds, mask);
            return cmd;
        }

        public PushOpacityCommand RentPushOpacity(double opacity) {
            PushOpacityCommand cmd = pushOpacities.Count > 0 ? pushOpacities.Pop() : new PushOpacityCommand();
            cmd.Set(opacity);
            return cmd;
        }

        public PushTransformCommand RentPushTransform(Transform2D transform) {
            PushTransformCommand cmd = pushTransforms.Count > 0 ? pushTransforms.Pop() : new PushTransformCommand();
            cmd.Set(transform);
            return cmd;
        }

        public PushFilterCommand RentPushFilter(Rect bounds, FilterChain filters) {
            return RentPushFilter(bounds, filters, Transform2D.Identity);
        }

        // Pool overload that wires the box's own CSS `transform` through to the
        // composite step (see PushFilterCommand.ScopeBoxTransform). Identity =
        // no transform → behaves identically to the 2-arg overload.
        public PushFilterCommand RentPushFilter(Rect bounds, FilterChain filters, Transform2D scopeBoxTransform) {
            PushFilterCommand cmd = pushFilters.Count > 0 ? pushFilters.Pop() : new PushFilterCommand();
            cmd.Set(bounds, filters, scopeBoxTransform);
            return cmd;
        }

        public PushMixBlendModeCommand RentPushMixBlendMode(MixBlendMode mode) {
            PushMixBlendModeCommand cmd = pushMixBlendModes.Count > 0 ? pushMixBlendModes.Pop() : new PushMixBlendModeCommand();
            cmd.Set(mode);
            return cmd;
        }

        // CSS Compositing 1 §9 — element-local background-blend-mode scope.
        public PushBackgroundBlendCommand RentPushBackgroundBlend(MixBlendMode mode, LinearColor baseColor) {
            PushBackgroundBlendCommand cmd = pushBackgroundBlends.Count > 0
                ? pushBackgroundBlends.Pop()
                : new PushBackgroundBlendCommand();
            cmd.Set(mode, baseColor);
            return cmd;
        }

        // ReturnAll walks every command in the list and parks pool-able subtypes
        // in their per-type stacks. Pop singletons are skipped; they live forever
        // as static fields on PaintCommandSingletons. After this call the list is
        // unmodified — callers (PaintListPool.Return / WevaDocument.EmitPaint) clear
        // the list themselves so the test ordering is deterministic.
        public void ReturnAll(PaintList list) {
            if (list == null) return;
            var cmds = list.Commands;
            for (int i = 0; i < cmds.Count; i++) {
                ReturnOne(cmds[i]);
            }
        }

        public void ReturnOne(PaintCommand cmd) {
            // Dispatch on the Kind discriminator so the JIT lowers this to an
            // int-switch jump table. The previous type-pattern cascade did up
            // to 8 sequential isinst checks per command — measurable on the
            // ~hundreds-of-commands-per-frame return path.
            if (cmd == null) return;
            switch (cmd.Kind) {
                case PaintCommandKind.FillRect: {
                    var fr = (FillRectCommand)cmd;
                    if (fillRects.Count < maxPerType) { fr.Reset(); fillRects.Push(fr); }
                    break;
                }
                case PaintCommandKind.DrawText: {
                    var dt = (DrawTextCommand)cmd;
                    if (drawTexts.Count < maxPerType) { dt.Reset(); drawTexts.Push(dt); }
                    break;
                }
                case PaintCommandKind.StrokeBorder: {
                    var sb = (StrokeBorderCommand)cmd;
                    if (strokeBorders.Count < maxPerType) { sb.Reset(); strokeBorders.Push(sb); }
                    break;
                }
                case PaintCommandKind.DrawShadow: {
                    var ds = (DrawShadowCommand)cmd;
                    if (drawShadows.Count < maxPerType) { ds.Reset(); drawShadows.Push(ds); }
                    break;
                }
                case PaintCommandKind.DrawBackdropFilter: {
                    var db = (DrawBackdropFilterCommand)cmd;
                    if (backdropFilters.Count < maxPerType) { db.Reset(); backdropFilters.Push(db); }
                    break;
                }
                case PaintCommandKind.PushClip: {
                    var pc = (PushClipCommand)cmd;
                    if (pushClips.Count < maxPerType) { pc.Reset(); pushClips.Push(pc); }
                    break;
                }
                case PaintCommandKind.PushClipPath: {
                    var pc = (PushClipPathCommand)cmd;
                    if (pushClipPaths.Count < maxPerType) { pc.Reset(); pushClipPaths.Push(pc); }
                    break;
                }
                case PaintCommandKind.PushMask: {
                    var pm = (PushMaskCommand)cmd;
                    if (pushMasks.Count < maxPerType) { pm.Reset(); pushMasks.Push(pm); }
                    break;
                }
                case PaintCommandKind.PushOpacity: {
                    var po = (PushOpacityCommand)cmd;
                    if (pushOpacities.Count < maxPerType) { po.Reset(); pushOpacities.Push(po); }
                    break;
                }
                case PaintCommandKind.PushTransform: {
                    var pt = (PushTransformCommand)cmd;
                    if (pushTransforms.Count < maxPerType) { pt.Reset(); pushTransforms.Push(pt); }
                    break;
                }
                case PaintCommandKind.PushFilter: {
                    var pf = (PushFilterCommand)cmd;
                    if (pushFilters.Count < maxPerType) { pf.Reset(); pushFilters.Push(pf); }
                    break;
                }
                case PaintCommandKind.PushMixBlendMode: {
                    var pm = (PushMixBlendModeCommand)cmd;
                    if (pushMixBlendModes.Count < maxPerType) { pm.Reset(); pushMixBlendModes.Push(pm); }
                    break;
                }
                case PaintCommandKind.PushBackgroundBlend: {
                    var pb = (PushBackgroundBlendCommand)cmd;
                    if (pushBackgroundBlends.Count < maxPerType) { pb.Reset(); pushBackgroundBlends.Push(pb); }
                    break;
                }
                case PaintCommandKind.BeginSubtreeCapture: {
                    var bs = (BeginSubtreeCaptureCommand)cmd;
                    if (beginSubtrees.Count < maxPerType) { bs.Reset(); beginSubtrees.Push(bs); }
                    break;
                }
                case PaintCommandKind.EndSubtreeCapture: {
                    var es = (EndSubtreeCaptureCommand)cmd;
                    if (endSubtrees.Count < maxPerType) { es.Reset(); endSubtrees.Push(es); }
                    break;
                }
                case PaintCommandKind.ReplaySubtreeSnapshot: {
                    var rs = (ReplaySubtreeSnapshotCommand)cmd;
                    if (replaySubtrees.Count < maxPerType) { rs.Reset(); replaySubtrees.Push(rs); }
                    break;
                }
                // Pop* and Unknown fall through: pop singletons have no fields
                // to reset and are never sourced from the pool.
            }
        }

        public int FillRectStackSize => fillRects.Count;
        public int StrokeBorderStackSize => strokeBorders.Count;
        public int DrawTextStackSize => drawTexts.Count;
        public int DrawShadowStackSize => drawShadows.Count;
        public int DrawBackdropFilterStackSize => backdropFilters.Count;
        public int PushClipStackSize => pushClips.Count;
        public int PushClipPathStackSize => pushClipPaths.Count;
        public int PushMaskStackSize => pushMasks.Count;
        public int PushOpacityStackSize => pushOpacities.Count;
        public int PushTransformStackSize => pushTransforms.Count;
        public int PushFilterStackSize => pushFilters.Count;
        public int PushMixBlendModeStackSize => pushMixBlendModes.Count;
        public int PushBackgroundBlendStackSize => pushBackgroundBlends.Count;
    }
}
