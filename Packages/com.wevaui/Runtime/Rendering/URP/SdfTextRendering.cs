using System.Collections.Generic;
using UnityEngine;
using Weva.Paint;
using PaintRect = Weva.Paint.Rect;

namespace Weva.Rendering.URP {
    // Converts a DrawTextCommand into per-glyph SdfGlyphQuads. The TextCore SDF agent
    // populates GlyphAtlas (a registry of glyph→UV-rect pairs). Until that hook is wired,
    // this falls back to monospace advance-width rectangles so text bounds remain visible
    // and layout is exercised end-to-end.
    public static class SdfTextRendering {
        // Lookup hook installed by the TextCore SDF agent. Returns false when no atlas is
        // available — SdfTextRendering then takes the colored-rectangle fallback path.
        public static IGlyphAtlas Atlas;
        static IGlyphAtlas snapshotAtlas;
        static long snapshotAtlasVersion;

        // When true, every emitted glyph quad's origin (X) and baseline (Y) is rounded
        // to whole pixels just before submission. Hinted bitmap glyphs (ATG coverage)
        // only render crisp on the integer pixel grid; the engine snaps the run ORIGIN
        // but inter-glyph advances are fractional, so letters drift off-grid (uneven
        // stroke weights / baseline jitter). The editor panel sets this around its paint
        // (its surface is 1:1 with screen pixels); off everywhere else so layout-exact
        // sub-pixel positioning is preserved for the game's SDF text.
        public static bool SnapGlyphsToIntegerGrid;

        public static void SetAtlas(IGlyphAtlas atlas) {
            if (!ReferenceEquals(Atlas, atlas)) {
                TextRunSnapshotCache.Clear();
            }
            Atlas = atlas;
            snapshotAtlas = atlas;
            snapshotAtlasVersion = CurrentAtlasVersion;
        }

        public static long CurrentAtlasVersion {
            get {
                if (Atlas == null) return 0;
                return Atlas is IGlyphAtlasVersioned versioned ? versioned.Version : -1;
            }
        }

        public static int CurrentAtlasIdentity {
            get {
                return Atlas != null
                    ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Atlas)
                    : 0;
            }
        }

        // Fallback-shape probation (see EmitGlyphs): keys whose LAST shape fell
        // through to the secondary SDF face exactly once. A second consecutive
        // fallback proves the failure is persistent (missing face/coverage) and
        // the shape gets cached like any other; a transient mid-repack failure
        // never recurs, so its (potentially garbled) quads never enter the
        // cache. Cleared whenever the snapshot cache itself resets.
        static readonly HashSet<TextRunSnapshotKey> fallbackProbation = new HashSet<TextRunSnapshotKey>();

        static void SynchronizeSnapshotCacheWithAtlas() {
            long version = CurrentAtlasVersion;
            if (!ReferenceEquals(snapshotAtlas, Atlas) || snapshotAtlasVersion != version) {
                TextRunSnapshotCache.Clear();
                fallbackProbation.Clear();
                snapshotAtlas = Atlas;
                snapshotAtlasVersion = version;
            }
        }

        static bool UseTextRunSnapshots {
            get {
                if (Atlas == null) return false;
                return !(Atlas is IGlyphAtlasTextRunSnapshotPolicy policy) || policy.UseTextRunSnapshots;
            }
        }

        public static bool PrepareText(PaintList list) {
            if (list == null || Atlas == null || !(Atlas is IGlyphAtlasPreparer preparer)) return false;
            SynchronizeSnapshotCacheWithAtlas();
            int atlasIdentityBefore = CurrentAtlasIdentity;
            long atlasVersionBefore = CurrentAtlasVersion;
            bool useSnapshots = UseTextRunSnapshots;
            var commands = list.Commands;
            bool began = false;
            try {
                for (int i = 0; i < commands.Count; i++) {
                    if (!(commands[i] is DrawTextCommand text)) continue;
                    // A cached snapshot normally means the run is fully
                    // prepared — but one built from the SECONDARY fallback
                    // face must keep feeding the primary preparer: the
                    // fallback happened because the primary atlas couldn't
                    // shape the text yet (cold start / missing glyph), and
                    // skipping preparation here would deadlock the run on the
                    // fallback face forever (the version bump that clears the
                    // cache can only come from the primary atlas ingesting
                    // these characters).
                    if (useSnapshots && TextRunSnapshotCache.TryGet(new TextRunSnapshotKey(text), out var prepared)
                        && !prepared.UsedSecondaryFallback) continue;
                    if (!began) {
                        preparer.BeginPrepareText();
                        began = true;
                    }
                    preparer.PrepareText(text);
                }
            } finally {
                if (began) {
                    preparer.EndPrepareText();
                    SynchronizeSnapshotCacheWithAtlas();
                }
            }
            return began && (atlasIdentityBefore != CurrentAtlasIdentity || atlasVersionBefore != CurrentAtlasVersion);
        }

        public static void EmitGlyphs(UIBatcher batcher, DrawTextCommand command) {
            if (batcher == null || command == null) return;
            if (command.Bounds.IsEmpty) return;
            // Whitespace-only runs — the inter-word space TextRuns that make up
            // ~40% of a text-heavy page's draw commands — have no glyph ink.
            // Without this guard each of them re-entered the FULL shape pipeline
            // on EVERY repaint frame: a space yields zero quads, so there is
            // never a snapshot to cache, and the run fell through the ATG
            // attempt (reflection GenerateText + a TextInfo allocation) and the
            // SDF fallback again and again — measured 157 shapes / ~0.9 MB GC /
            // frame while scrolling weva-landing. Only a text-decoration makes
            // pure whitespace drawable (the underline/strike segment continues
            // across the gap), so decorated runs still shape.
            if (command.Decoration == TextDecoration.None && IsWhitespaceOnly(command.Text)) return;

            // Shaped-run snapshot fast path. Only consulted on the
            // atlas-backed path: the fallback (no-atlas) path always
            // re-emits fresh placeholder rects, so the cache must not
            // intercept it. Cache entries are produced exclusively from
            // successful atlas shapes, so a non-null Atlas is both the
            // necessary and sufficient precondition for replay.
            // `background-clip: text` gradient fill is applied as a recolour
            // AFTER shaping/replay (RecolorGlyphsWithGradient below and inside
            // ReplaySnapshot), so gradient runs cache shaped quads like any
            // other text. An earlier version bypassed the snapshot cache for
            // them, which forced a full reflection-based ATG re-shape (+ a
            // fresh TextInfo allocation) EVERY frame per gradient run — a
            // measurable per-frame CPU/GC cost during scrolling.
            bool useTextRunSnapshots = UseTextRunSnapshots;
            if (useTextRunSnapshots) {
                SynchronizeSnapshotCacheWithAtlas();
                var snapKey = new TextRunSnapshotKey(command);
                if (TextRunSnapshotCache.TryGet(snapKey, out var snap)) {
                    ReplaySnapshot(batcher, snap, command);
                    return;
                }
            }

            var glyphs = ListPool.Rent();
            try {
                if (Atlas != null) {
                    int atlasId = 0;
                    bool ok;
                    if (Atlas is IGlyphAtlasWithId withId) {
                        ok = withId.TryShape(command, glyphs, out atlasId);
                    } else {
                        ok = Atlas.TryShape(command, glyphs);
                    }
                    if (ok) {
                        SynchronizeSnapshotCacheWithAtlas();
                        // Center a lone icon glyph (emoji/symbol) on both axes in
                        // its run box, regardless of which shaping path produced
                        // it (color-emoji vs mono-SDF land in different atlases
                        // with different bearings, so advance-based placement
                        // leaves them visually off-centre in a centered chip).
                        // Applied here so it covers every adapter AND bakes into
                        // the stored snapshot below. Scoped to single symbol/emoji
                        // glyphs so text — including lone punctuation like '·' —
                        // keeps strict baseline alignment.
                        if (IsIconGlyphRun(command.Text, glyphs.Count)) {
                            CenterIconGlyph(glyphs, command);
                        }
                        // A shape that fell through to the secondary SDF face
                        // renders with advances that don't match the layout
                        // metrics (the primary face measured the run). Caching
                        // it carries a risk: a TRANSIENT primary failure (the
                        // mid-repack zero-glyph race) would pin a garbled
                        // render for the whole session. But never caching is
                        // pathological the other way — when the primary face is
                        // genuinely unavailable, EVERY run re-shapes (two
                        // reflection GenerateText attempts + a TextInfo
                        // allocation) EVERY repaint frame (~20 ms + megabytes
                        // of garbage while scrolling). Probation resolves both:
                        // skip the store on a key's FIRST fallback shape; if
                        // the SAME key falls back again on a later emit, the
                        // failure is persistent — cache it like any other
                        // shape. Transient races don't recur, so their quads
                        // never enter the cache.
                        bool fallbackShape = Atlas is IGlyphAtlasShapeSource shapeSource
                            && shapeSource.LastShapeUsedSecondaryFallback;
                        if (useTextRunSnapshots) {
                            var storeKey = new TextRunSnapshotKey(command);
                            if (!fallbackShape) {
                                fallbackProbation.Remove(storeKey);
                                TextRunSnapshotCache.Store(storeKey, glyphs, atlasId, command);
                            } else if (fallbackProbation.Remove(storeKey)) {
                                // Cache the fallback shape (stops the per-frame
                                // re-shape cost) but TAG it: PrepareText keeps
                                // feeding this text to the primary atlas so a
                                // cold-start miss can heal — see PrepareText.
                                TextRunSnapshotCache.Store(storeKey, glyphs, atlasId, command, usedSecondaryFallback: true);
                            } else {
                                fallbackProbation.Add(storeKey);
                            }
                        }
                        if (command.TextFillGradient != null) RecolorGlyphsWithGradient(glyphs, command);
                        SubmitSplitByAtlas(batcher, glyphs, atlasId);
                        return;
                    }
                }
                EmitFallback(command, glyphs);
                if (glyphs.Count > 0) {
                    // Fallback is a series of colored rectangles at glyph-advance positions.
                    // We submit them as solid-color batches rather than as text-keyword quads
                    // because there's no atlas to sample; this keeps output visible while the
                    // SDF agent ships.
                    if (command.TextFillGradient != null) RecolorGlyphsWithGradient(glyphs, command);
                    for (int i = 0; i < glyphs.Count; i++) {
                        var g = glyphs[i];
                        batcher.SubmitFillRect(g.Bounds, Brush.SolidColor(g.Color), BorderRadii.Zero);
                    }
                }
            } finally {
                ListPool.Return(glyphs);
            }
        }

        // CSS Backgrounds 4 `background-clip: text`: fill the shaped glyphs with
        // the command's gradient mapped over the run bounds. Each glyph quad is
        // a single GPU instance with ONE colour, so a per-glyph sample steps the
        // gradient letter-by-letter. For horizontal-dominant gradients (within
        // 45° of `to right` — the overwhelmingly common gradient-text case) we
        // instead SLICE each glyph into ~2px vertical strips with
        // proportionally-mapped UVs, each strip sampled at its own centre: the
        // result is visually continuous at any glyph size. Vertical-dominant
        // gradients keep the per-glyph sample (on a single-line run they vary
        // mostly line-to-line anyway). Glyph counts here are tiny (stat numbers
        // / headline spans), so the extra instances are noise.
        static void RecolorGlyphsWithGradient(List<SdfGlyphQuad> glyphs, DrawTextCommand command) {
            var grad = command.TextFillGradient;
            if (grad == null) return;
            var b = command.Bounds;
            LinearColor text = command.Color;
            bool horizontalDominant = true;
            if (grad is LinearGradient lgAxis) {
                double th = lgAxis.AngleDegrees * System.Math.PI / 180.0;
                horizontalDominant = System.Math.Abs(System.Math.Sin(th)) >= System.Math.Abs(System.Math.Cos(th));
            }
            if (!horizontalDominant) {
                for (int i = 0; i < glyphs.Count; i++) {
                    var g = glyphs[i];
                    double cx = g.Bounds.X + g.Bounds.Width * 0.5;
                    double cy = g.Bounds.Y + g.Bounds.Height * 0.5;
                    double t = GradientParamAt(grad, b.X, b.Y, b.Width, b.Height, cx, cy);
                    LinearColor col = SourceOver(text, grad.Sample(t));
                    glyphs[i] = new SdfGlyphQuad(g.Bounds, col, g.UvMin, g.UvMax,
                        g.AtlasId, g.BlurRadius, g.WeightBias, g.TintWithFillColor);
                }
                return;
            }
            var sliced = ListPool.Rent();
            try {
                for (int i = 0; i < glyphs.Count; i++) {
                    var g = glyphs[i];
                    // Decoration quads (degenerate UV) and hairline glyphs stay
                    // whole — a single centre sample.
                    int slices = (int)System.Math.Ceiling(g.Bounds.Width / 2.0);
                    if (slices < 1) slices = 1;
                    if (slices > 24) slices = 24;
                    if (slices == 1 || g.UvMax.x <= g.UvMin.x) {
                        double cx0 = g.Bounds.X + g.Bounds.Width * 0.5;
                        double cy0 = g.Bounds.Y + g.Bounds.Height * 0.5;
                        LinearColor col0 = SourceOver(text, grad.Sample(
                            GradientParamAt(grad, b.X, b.Y, b.Width, b.Height, cx0, cy0)));
                        sliced.Add(new SdfGlyphQuad(g.Bounds, col0, g.UvMin, g.UvMax,
                            g.AtlasId, g.BlurRadius, g.WeightBias, g.TintWithFillColor));
                        continue;
                    }
                    double cy = g.Bounds.Y + g.Bounds.Height * 0.5;
                    for (int s = 0; s < slices; s++) {
                        float f0 = (float)s / slices;
                        float f1 = (float)(s + 1) / slices;
                        double x0 = g.Bounds.X + g.Bounds.Width * f0;
                        double x1 = g.Bounds.X + g.Bounds.Width * f1;
                        var rect = new PaintRect(x0, g.Bounds.Y, x1 - x0, g.Bounds.Height);
                        // The quad maps Bounds.X..Right onto UvMin.x..UvMax.x
                        // linearly; slicing the bounds slices the u range at the
                        // same fractions (v untouched — horizontal strips only).
                        var uvMin = new Vector2(g.UvMin.x + (g.UvMax.x - g.UvMin.x) * f0, g.UvMin.y);
                        var uvMax = new Vector2(g.UvMin.x + (g.UvMax.x - g.UvMin.x) * f1, g.UvMax.y);
                        double t = GradientParamAt(grad, b.X, b.Y, b.Width, b.Height,
                            rect.X + rect.Width * 0.5, cy);
                        LinearColor col = SourceOver(text, grad.Sample(t));
                        sliced.Add(new SdfGlyphQuad(rect, col, uvMin, uvMax,
                            g.AtlasId, g.BlurRadius, g.WeightBias, g.TintWithFillColor));
                    }
                }
                glyphs.Clear();
                for (int i = 0; i < sliced.Count; i++) glyphs.Add(sliced[i]);
            } finally {
                ListPool.Return(sliced);
            }
        }

        // Chrome semantics for background-clip:text — the glyph shows the text
        // colour painted OVER the clipped background gradient. The canonical
        // pattern sets `color: transparent`, leaving pure gradient; a partially
        // transparent colour tints it; an opaque colour would cover it (that
        // case never reaches here — the converter skips attaching the gradient).
        // Straight (non-premultiplied) source-over.
        static LinearColor SourceOver(LinearColor src, LinearColor dst) {
            float sa = src.A;
            float da = dst.A * (1f - sa);
            float outA = sa + da;
            if (outA <= 0f) return new LinearColor(0f, 0f, 0f, 0f);
            return new LinearColor(
                (src.R * sa + dst.R * da) / outA,
                (src.G * sa + dst.G * da) / outA,
                (src.B * sa + dst.B * da) / outA,
                outA);
        }

        // CSS Images 3 §3.2 linear-gradient parameterisation: project the point
        // (px,py) onto the gradient line through the box centre at angle θ
        // (0deg = to top, 90deg = to right), normalised to the line length
        // (= |W·sinθ| + |H·cosθ|). Returns t in [0,1]. Non-linear gradients fall
        // back to the midpoint (both weva-landing gradients are linear).
        static double GradientParamAt(Weva.Paint.Gradient grad, double bx, double by, double bw, double bh, double px, double py) {
            if (grad is LinearGradient lg) {
                double theta = lg.AngleDegrees * System.Math.PI / 180.0;
                double ux = System.Math.Sin(theta);
                double uy = -System.Math.Cos(theta);
                double cx = bx + bw * 0.5;
                double cy = by + bh * 0.5;
                double len = System.Math.Abs(bw * ux) + System.Math.Abs(bh * uy);
                if (len < 1e-6) return 0.5;
                double t = 0.5 + ((px - cx) * ux + (py - cy) * uy) / len;
                return t < 0.0 ? 0.0 : (t > 1.0 ? 1.0 : t);
            }
            return 0.5;
        }

        // True when the run is a single icon glyph (emoji or symbol) — the case
        // that benefits from ink-box centering. Restricted to the symbol/geometric/
        // dingbat/arrow blocks and the emoji plane; deliberately EXCLUDES Latin-1
        // and General Punctuation (·, –, …) which are text and must baseline-align.
        static bool IsIconGlyphRun(string s, int quadCount) {
            if (quadCount != 1 || string.IsNullOrEmpty(s)) return false;
            int cp = (char.IsHighSurrogate(s[0]) && s.Length >= 2 && char.IsLowSurrogate(s[1]))
                ? char.ConvertToUtf32(s[0], s[1])
                : s[0];
            return (cp >= 0x2190 && cp <= 0x2BFF) || (cp >= 0x1F000 && cp <= 0x1FAFF);
        }

        // Center the single icon glyph's ink box on both axes within the run box,
        // so mixed-font icon rows (mono ▶ vs color 🔄) line up consistently.
        static void CenterIconGlyph(List<SdfGlyphQuad> glyphs, DrawTextCommand command) {
            if (glyphs.Count != 1) return;
            var q = glyphs[0];
            double w = q.Bounds.Width, h = q.Bounds.Height;
            if (w <= 0 || h <= 0) return;
            var b = command.Bounds;
            double x = b.X + (b.Width - w) * 0.5;
            double y = b.Y + (b.Height - h) * 0.5;
            glyphs[0] = new SdfGlyphQuad(new PaintRect(x, y, w, h), q.Color, q.UvMin, q.UvMax,
                q.AtlasId, q.BlurRadius, q.WeightBias, q.TintWithFillColor);
        }

        static bool IsWhitespaceOnly(string text) {
            if (string.IsNullOrEmpty(text)) return true;
            for (int i = 0; i < text.Length; i++) {
                char c = text[i];
                if (c != ' ' && c != '\t' && c != '\n' && c != '\r' && c != ' ') return false;
            }
            return true;
        }

        // Submits a cached snapshot, applying the current origin as a pure
        // translation to each quad's bounds. Reuses the same pooled list the
        // miss path uses so the batcher's `SubmitGlyphQuads(IReadOnlyList,
        // atlasId)` entry stays the lone consumer of glyph batches.
        static void ReplaySnapshot(UIBatcher batcher, TextRunSnapshot snap, DrawTextCommand command) {
            var glyphs = ListPool.Rent();
            try {
                double dx = command.Bounds.X;
                double dy = command.Bounds.Y;
                double snapCorrectionX = 0;
                double snapCorrectionY = 0;
                if (snap.AppliesPixelSnapCorrection) {
                    snapCorrectionX = TextRunSnapshotCache.PixelSnapDelta(command.Bounds.X) - snap.SnapDeltaX;
                    snapCorrectionY = TextRunSnapshotCache.PixelSnapDelta(command.Bounds.Y + command.Bounds.Height) - snap.SnapDeltaY;
                }
                var arr = snap.Quads;
                for (int i = 0; i < arr.Length; i++) {
                    var g = arr[i];
                    var translated = new PaintRect(
                        g.Bounds.X + dx + snapCorrectionX,
                        g.Bounds.Y + dy + snapCorrectionY,
                        g.Bounds.Width,
                        g.Bounds.Height);
                    glyphs.Add(new SdfGlyphQuad(translated, g.Color, g.UvMin, g.UvMax, g.AtlasId, g.BlurRadius, g.WeightBias, g.TintWithFillColor));
                }
                // background-clip:text — the snapshot holds the shaped solid
                // quads; the gradient recolour (+ slicing) is position-dependent
                // so it runs on the translated quads at replay time.
                if (command.TextFillGradient != null) RecolorGlyphsWithGradient(glyphs, command);
                SubmitSplitByAtlas(batcher, glyphs, snap.AtlasId);
            } finally {
                ListPool.Return(glyphs);
            }
        }

        // Splits a flat glyph list into contiguous runs by effective atlas id
        // (per-quad AtlasId overrides the run-level primary; 0 means "use
        // primary") and submits each run as a separate SubmitGlyphQuads call.
        // This guarantees the runwide `atlasId` argument passed to the batcher
        // matches the quads in that submission, so emoji and other fallback
        // glyphs route to the correct atlas texture instead of inheriting the
        // primary face's id. Approach #2 (sequential split): preserves the
        // original glyph order — only flushes the in-flight sub-list on an
        // atlas-id boundary, so overlapping/composed glyphs draw in shape order.
        // Decoration quads (degenerate UV) are kept with the in-flight run; the
        // batcher's degenerate-UV branch already routes them to the run-level
        // atlas without sampling.
        static void SubmitSplitByAtlas(UIBatcher batcher, List<SdfGlyphQuad> glyphs, int primaryAtlasId) {
            int n = glyphs.Count;
            if (n == 0) return;
            // Pixel-snap each glyph onto the integer grid (editor-panel only). Hinted
            // coverage bitmaps blur at fractional offsets; rounding the per-glyph origin
            // + baseline lands every letter on a whole pixel → crisp, even strokes,
            // aligned baseline. Width/UV unchanged (only the placement moves).
            if (SnapGlyphsToIntegerGrid) {
                for (int i = 0; i < n; i++) {
                    var g = glyphs[i];
                    // Decoration quads (underline/strike) sample no bitmap — leave them.
                    if (g.UvMax.x <= g.UvMin.x || g.UvMax.y <= g.UvMin.y) continue;
                    var tex = Weva.Text.Sdf.AtlasRegistry.GetTextureById(EffectiveAtlasId(g, primaryAtlasId));
                    if (tex == null) continue;
                    var b = g.Bounds;
                    double texelW = (g.UvMax.x - g.UvMin.x) * tex.width;
                    double texelH = (g.UvMax.y - g.UvMin.y) * tex.height;
                    // Snap ONLY glyphs already rendered ~1:1 — hinted coverage / small text
                    // rasterized at display px (quad px ≈ atlas texel count). Scaled glyphs
                    // (large SDFAA text + icons rasterized at a 90px reference and scaled to
                    // a different display size) are left untouched: forcing them to the atlas
                    // footprint mangles them (overlapping logo letters / oversized symbols).
                    if (System.Math.Abs(texelW - b.Width) > 3.0 || System.Math.Abs(texelH - b.Height) > 3.0) continue;
                    // Position on whole pixels, size to the exact texel extent → true 1:1
                    // sample (crisp, no per-glyph stretch / X smear).
                    double sx = System.Math.Round(b.X);
                    double sy = System.Math.Round(b.Y);
                    double sw = System.Math.Max(1, System.Math.Round(texelW));
                    double sh = System.Math.Max(1, System.Math.Round(texelH));
                    if (sx != b.X || sy != b.Y || sw != b.Width || sh != b.Height) {
                        glyphs[i] = new SdfGlyphQuad(new PaintRect(sx, sy, sw, sh),
                            g.Color, g.UvMin, g.UvMax, g.AtlasId, g.BlurRadius, g.WeightBias, g.TintWithFillColor);
                    }
                }
            }
            // Common case (no fallback): every quad's effective id equals the
            // primary. Submit in one call without renting a sub-list.
            bool allPrimary = true;
            for (int i = 0; i < n; i++) {
                int qid = glyphs[i].AtlasId;
                bool degenerate = glyphs[i].UvMax.x <= glyphs[i].UvMin.x || glyphs[i].UvMax.y <= glyphs[i].UvMin.y;
                if (qid != 0 && !degenerate && qid != primaryAtlasId) { allPrimary = false; break; }
            }
            if (allPrimary) {
                batcher.SubmitGlyphQuads(glyphs, primaryAtlasId);
                return;
            }
            var run = ListPool.Rent();
            try {
                int runAtlasId = EffectiveAtlasId(glyphs[0], primaryAtlasId);
                for (int i = 0; i < n; i++) {
                    var g = glyphs[i];
                    int eff = EffectiveAtlasId(g, primaryAtlasId);
                    if (eff != runAtlasId && run.Count > 0) {
                        batcher.SubmitGlyphQuads(run, runAtlasId);
                        run.Clear();
                        runAtlasId = eff;
                    }
                    run.Add(g);
                }
                if (run.Count > 0) batcher.SubmitGlyphQuads(run, runAtlasId);
            } finally {
                ListPool.Return(run);
            }
        }

        // Per-quad atlas resolution mirrors UIBatcher.SubmitGlyphQuads: a
        // quad's own AtlasId wins when non-zero and UV-rect is non-degenerate;
        // decoration quads (degenerate UV) inherit the primary so we don't
        // shatter a run for an underline that doesn't sample anyway.
        static int EffectiveAtlasId(in SdfGlyphQuad g, int primaryAtlasId) {
            bool degenerateUv = g.UvMax.x <= g.UvMin.x || g.UvMax.y <= g.UvMin.y;
            if (degenerateUv) return primaryAtlasId;
            return g.AtlasId != 0 ? g.AtlasId : primaryAtlasId;
        }

        static void EmitFallback(DrawTextCommand command, List<SdfGlyphQuad> output) {
            string text = command.Text;
            if (string.IsNullOrEmpty(text)) return;
            // Approximate advance width = 0.55 em — close enough to a sans-serif monospace
            // mean that the visible bounds line up with the layout-pass calculation in
            // FontMetrics.MeasureText for the dev fallback case.
            double size = command.Font.Size > 0 ? command.Font.Size : 14;
            double advance = size * 0.55;
            double cursor = command.Bounds.X;
            double top = command.Bounds.Y;
            double height = command.Bounds.Height > 0 ? command.Bounds.Height : size;
            double maxX = command.Bounds.Right;
            for (int i = 0; i < text.Length; i++) {
                if (cursor >= maxX) break;
                if (text[i] == ' ') { cursor += advance; continue; }
                double w = System.Math.Min(advance * 0.7, maxX - cursor);
                if (w <= 0) break;
                var bounds = new PaintRect(cursor, top + height * 0.2, w, height * 0.7);
                output.Add(new SdfGlyphQuad(bounds, command.Color, Vector2.zero, Vector2.one));
                cursor += advance;
            }
        }

        static class ListPool {
            static readonly Stack<List<SdfGlyphQuad>> pool = new Stack<List<SdfGlyphQuad>>();
            public static List<SdfGlyphQuad> Rent() {
                if (pool.Count == 0) return new List<SdfGlyphQuad>(64);
                var l = pool.Pop();
                l.Clear();
                return l;
            }
            public static void Return(List<SdfGlyphQuad> list) {
                if (list == null) return;
                list.Clear();
                pool.Push(list);
            }
        }
    }

    // Implemented by the TextCore SDF agent. TryShape returns true when atlas-backed
    // glyph quads were appended to `output`; false to signal the caller should fall
    // back to the no-atlas placeholder rendering.
    public interface IGlyphAtlas {
        bool TryShape(DrawTextCommand command, List<SdfGlyphQuad> output);
    }

    // Extension carrying the atlas id so the renderer can break batches per atlas
    // texture. Implemented by SdfGlyphAtlasAdapter (the v1 production path).
    public interface IGlyphAtlasWithId : IGlyphAtlas {
        bool TryShape(DrawTextCommand command, List<SdfGlyphQuad> output, out int atlasId);
    }

    // Atlas-backed text snapshots cache UV rectangles. Any backend that can
    // grow, repack, or swap atlas textures must advance this version so stale
    // snapshots are dropped before replay.
    public interface IGlyphAtlasVersioned {
        long Version { get; }
    }

    // Optional frame preflight hook. Backends that can mutate atlas UVs while
    // shaping should populate their atlases before the renderer emits any
    // glyph instances, otherwise a later run can repack pixels that earlier
    // instances already referenced.
    public interface IGlyphAtlasPreparer {
        void BeginPrepareText() { }
        void PrepareText(DrawTextCommand command);
        void EndPrepareText() { }
    }

    public interface IGlyphAtlasTextRunSnapshotPolicy {
        bool UseTextRunSnapshots { get; }
    }

    // Reports whether the LAST TryShape call satisfied the run on the primary
    // (ATG) path or fell through to the secondary SDF face. The secondary face
    // is a DIFFERENT font from the one the layout metrics measured with, so a
    // fallback-shaped run renders with mismatched advances (garbled spacing).
    // The renderer must therefore never pin such a shape in the
    // TextRunSnapshotCache: re-shaping on later frames lets the run recover to
    // the primary face once its atlas has settled (the documented ATG failure
    // is a transient mid-repack zero-glyph result, not missing coverage).
    public interface IGlyphAtlasShapeSource {
        bool LastShapeUsedSecondaryFallback { get; }
    }
}
