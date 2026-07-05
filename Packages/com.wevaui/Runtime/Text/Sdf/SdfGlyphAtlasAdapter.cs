#if UNITY_2023_1_OR_NEWER
using System.Collections.Generic;
using UnityEngine;
using Weva.Paint;
using Weva.Rendering.URP;
using Weva.Text.TextCore;
using Weva.Text.Tmp;
using PaintRect = Weva.Paint.Rect;

namespace Weva.Text.Sdf {
    // SdfGlyphAtlasAdapter implements IGlyphAtlas so the URP backend can call
    // TryShape(DrawTextCommand, output) and receive per-glyph SdfGlyphQuads
    // ready for SubmitGlyphQuads. Internally it drives SdfTextRunBaker, then
    // requests rasterization through SdfGlyphRasterizer for any glyph the
    // GlyphAtlas reports as missing.
    //
    // The adapter is the single point that closes the seam between:
    //   - The pure-C# baker (produces (origin, uv, atlasPage) tuples)
    //   - The URP renderer (consumes SdfGlyphQuads)
    //   - The Texture2D atlas (must contain the actual SDF bytes by the time
    //     the renderer issues DrawMeshInstanced)
    public sealed class SdfGlyphAtlasAdapter : IGlyphAtlasWithId, IGlyphAtlasVersioned, IGlyphAtlasPreparer, IGlyphAtlasTextRunSnapshotPolicy, SdfTextRunBaker.IGlyphRasterHook {
        public SdfTextRunBaker Baker { get; }
        public SdfFontMetrics Metrics { get; }
        public SdfGlyphRasterizer Rasterizer { get; }

        // Optional TMP routing. When TmpSource is non-null, TryShape resolves
        // glyph UV/metrics from the TMP_FontAsset before falling through to
        // the FontEngine rasterizer + GlyphAtlas. The atlasId reported back
        // to the renderer is the one bound to TmpAtlas (whose TextureOverride
        // is the TMP atlas Texture2D).
        public TmpFontAssetSource TmpSource { get; set; }
        public FaceInfo TmpFace { get; set; }
        public GlyphAtlas TmpAtlas { get; set; }

        // Optional TMP fallback chain. When set (along with TmpSource), the
        // shaper tries the primary TmpSource first, then walks each fallback
        // in registration order looking for a face that has the codepoint.
        // The resolved face's atlas may be a different Texture2D, so each
        // emitted SdfGlyphQuad records its own AtlasId — the batcher splits
        // batches at atlas boundaries.
        //
        // Null / empty => single-face behavior preserved bit-for-bit (no
        // chain walk, no per-quad atlas tagging).
        public IReadOnlyList<TmpFontAssetSource> TmpChain { get; set; }
        // Lazily-built parallel array: TmpChain[i] -> registered atlas/atlasId
        // for that fallback face. Populated on first TryShape call after
        // TmpChain is set; reset to null when the caller mutates the chain.
        GlyphAtlas[] tmpChainAtlases;
        int[] tmpChainAtlasIds;
        IReadOnlyList<TmpFontAssetSource> tmpChainSnapshot;

        readonly SdfTextRunBaker.Result scratch = new();

        public Texture2D PrimaryTexture { get; private set; }
        public bool UseTextRunSnapshots => true;
        public long Version {
            get {
                long version = (Metrics?.Atlas?.Revision ?? 0) ^ ((TmpAtlas?.Revision ?? 0) << 1);
                if (tmpChainAtlases != null) {
                    for (int i = 0; i < tmpChainAtlases.Length; i++) {
                        version = (version * 397) ^ (tmpChainAtlases[i]?.Revision ?? 0);
                    }
                }
                return version;
            }
        }

        static class PrepareListPool {
            static readonly Stack<List<SdfGlyphQuad>> pool = new Stack<List<SdfGlyphQuad>>();

            public static List<SdfGlyphQuad> Rent() {
                if (pool.Count == 0) return new List<SdfGlyphQuad>(64);
                var list = pool.Pop();
                list.Clear();
                return list;
            }

            public static void Return(List<SdfGlyphQuad> list) {
                if (list == null) return;
                list.Clear();
                pool.Push(list);
            }
        }

        public SdfGlyphAtlasAdapter(SdfTextRunBaker baker, SdfFontMetrics metrics, SdfGlyphRasterizer rasterizer) {
            Baker = baker;
            Metrics = metrics;
            Rasterizer = rasterizer;
            if (baker != null) baker.RasterHook = this;
        }

        bool SdfTextRunBaker.IGlyphRasterHook.TryEnsureRaster(FaceInfo face, uint codepoint, double fontSize) {
            return TryEnsureRaster(face, codepoint, fontSize);
        }

        bool SdfTextRunBaker.IGlyphRasterHook.TryEnsureRaster(FaceInfo rasterFace, uint codepoint, double fontSize, FaceInfo uploadFace) {
            if (Rasterizer == null) return false;
            if (!Rasterizer.TryRasterizeAsRasterizedGlyph(rasterFace, codepoint, fontSize, out var raster)) return false;
            if (Metrics?.Atlas == null) return false;
            return Metrics.Atlas.TryUploadRaster(uploadFace, codepoint, fontSize, raster);
        }

        public bool TryShape(DrawTextCommand command, List<SdfGlyphQuad> output) {
            return TryShape(command, output, out _);
        }

        // Open/close a deferred-upload window on the FontEngine SDF atlas so a
        // prepare pass that rasterizes N fresh glyphs does ONE GPU texture
        // upload instead of one full-texture Apply per glyph (audit T2). The
        // TMP path uses pre-baked atlases (no per-glyph upload), so only
        // Metrics.Atlas needs the window. Driven by SdfTextRendering's
        // try/finally prepare loop, so EndPrepareText (the flush) always runs.
        public void BeginPrepareText() => Metrics?.Atlas?.BeginUploadBatch();
        public void EndPrepareText() => Metrics?.Atlas?.EndUploadBatch();

        public void PrepareText(DrawTextCommand command) {
            if (command == null || string.IsNullOrEmpty(command.Text)) return;
            var glyphs = PrepareListPool.Rent();
            try {
                TryShape(command, glyphs, out _);
            } finally {
                PrepareListPool.Return(glyphs);
            }
        }

        public bool TryShape(DrawTextCommand command, List<SdfGlyphQuad> output, out int atlasId) {
            atlasId = 0;
            if (command == null || output == null) return false;
            if (string.IsNullOrEmpty(command.Text)) return false;

            // TMP routing: when a TMP_FontAsset is registered, all glyph queries
            // for this run are served from the pre-baked atlas. We do NOT go
            // through the SdfTextRunBaker (which assumes a SdfFontMetrics).
            //
            // TMP is authoritative when it covers every renderable codepoint.
            // On a miss, prefer a full FontEngine reshape. If FontEngine cannot
            // draw the run, keep TMP's partial output so one unsupported glyph
            // does not blank the entire string.
            if (TmpSource != null && TmpSource.Asset != null) {
                // Phase 1 diagnostic: warn once per (family, weight, style)
                // when no source in the chain matches the requested variant.
                // The shaping path below silently falls through to the primary
                // face, producing faux-bold or upright output that doesn't
                // match what CSS requested — surface the mismatch in console.
                TmpFontAssetRegistry.ReportLookup(
                    command.Font.Family,
                    command.Font.Weight > 0 ? command.Font.Weight : 400,
                    command.Font.Style);
                int outputStart = output.Count;
                int tmpProduced = ShapeTmp(command, output, out int tmpAtlasId, out int missed);
                if (tmpProduced > 0 && missed == 0) {
                    atlasId = tmpAtlasId;
                    return true;
                }

                int tmpOutputCount = output.Count - outputStart;
                List<SdfGlyphQuad> tmpPartial = null;
                if (tmpOutputCount > 0) {
                    tmpPartial = new List<SdfGlyphQuad>(tmpOutputCount);
                    for (int q = outputStart; q < output.Count; q++) {
                        tmpPartial.Add(output[q]);
                    }
                }

                // Prefer a full FontEngine reshape for partial TMP coverage.
                // But if FontEngine cannot draw the run, keep the TMP glyphs
                // we did have instead of making the whole string disappear.
                if (output.Count > outputStart) {
                    output.RemoveRange(outputStart, output.Count - outputStart);
                }
                atlasId = 0;
                int fallbackStart = output.Count;
                if (TryShapeWithFontEngine(command, output, ref atlasId)) {
                    return true;
                }
                if (output.Count > fallbackStart) {
                    output.RemoveRange(fallbackStart, output.Count - fallbackStart);
                }
                if (tmpPartial != null && tmpPartial.Count > 0) {
                    output.AddRange(tmpPartial);
                    atlasId = tmpAtlasId;
                    return true;
                }
                return false;
            }

            return TryShapeWithFontEngine(command, output, ref atlasId);
        }

        bool TryShapeWithFontEngine(DrawTextCommand command, List<SdfGlyphQuad> output, ref int atlasId) {
            if (Metrics == null || Baker == null) return false;

            var req = new SdfTextRunBaker.Request {
                Text = command.Text,
                FontFamily = command.Font.Family,
                FontSize = command.Font.Size > 0 ? command.Font.Size : 14,
                FontStyle = command.Font.Style,
                FontWeight = command.Font.Weight > 0 ? command.Font.Weight : 400,
                Color = command.Color,
                Decoration = command.Decoration,
                LetterSpacingPx = command.LetterSpacingPx,
                OriginX = command.Bounds.X,
                OriginY = command.Bounds.Y,
                // CSS Text Decoration 4 plumbing — propagate the explicit color
                // / style / thickness / offset stamped onto the command so the
                // baker emits the correct shape (double rects, dotted segments,
                // colored underline). null DecorationColor preserves the v0
                // "fall back to glyph color" behaviour pinned by existing tests.
                DecorationColor = command.HasDecorationColor ? command.DecorationColor : (LinearColor?)null,
                DecorationStyle = command.DecorationStyle,
                DecorationThicknessPx = command.DecorationThickness > 0 ? command.DecorationThickness : -1,
                DecorationOffsetPx = command.DecorationOffset > 0 ? command.DecorationOffset : -1,
                KerningEnabled = command.KerningEnabled
            };
            Baker.BakeInto(req, scratch);

            // Resolve atlas id from the first glyph's face. v1 single-atlas-per-doc:
            // all glyphs share one GlyphAtlas; the AtlasId disambiguates between
            // documents that own different atlases.
            if (scratch.Glyphs.Count > 0) {
                var atlas = AtlasRegistry.GetAtlas(scratch.Glyphs[0].Face);
                if (atlas == null) atlas = Metrics.Atlas;
                if (atlas != null) atlasId = AtlasRegistry.GetAtlasId(atlas);
            }

            // Path A — text-shadow blur: when the originating DrawTextCommand
            // carries a non-zero BlurRadius, the per-glyph SdfGlyphQuad is
            // tagged with `blurPx` so the URP shader widens its SDF AA band
            // proportionally and feathers the silhouette outward by `blurPx`
            // screen pixels. The quad geometry itself is NOT additionally
            // inflated here — the BakedGlyph rect already includes the
            // atlas's SDF padding (the spread region where the SDF holds
            // valid out-of-glyph distances). The widened AA band feathers
            // *into* that existing padding ring; pixels that would land
            // beyond the atlas padding sample SDF=0 and render transparent,
            // which produces the documented Path A "blur capped by atlas
            // padding" tradeoff. For typical CSS values (blur ≤ ~6 px) and
            // typical atlas paddings (4–8 atlas-px) the cap is invisible;
            // very wide blur (≥ 12 px) reads as a soft-but-truncated halo
            // rather than a true Gaussian falloff. A v2 RT-Gaussian path can
            // replace this without touching the bake step.
            float blurPx = command.BlurRadius > 0 ? (float)command.BlurRadius : 0f;
            // Faux-bold SDF threshold shift. The cascade may resolve
            // font-weight to anything in [1, 1000]; we only have a single
            // baked atlas per (family, style), so values > 400 widen the
            // glyph silhouette at the shader instead of selecting a baked
            // bold face. See ComputeWeightBias for the mapping.
            float weightBias = ComputeWeightBias(command.Font.Weight);
            int produced = 0;
            for (int i = 0; i < scratch.Glyphs.Count; i++) {
                var g = scratch.Glyphs[i];
                if (g.Uv.U1 <= g.Uv.U0 || g.Uv.V1 <= g.Uv.V0) {
                    if (Rasterizer != null && TryEnsureRaster(g.Face, g.Codepoint, req.FontSize)) {
                        // Re-fetch UV after rasterization populated the atlas.
                        var faceMetrics = Metrics.MetricsFor(g.Face.Family, req.FontStyle, req.FontWeight);
                        if (faceMetrics != null && faceMetrics.TryGetGlyphRect(g.Codepoint, req.FontSize, out var uv2)) {
                            output.Add(BuildQuad(g, uv2, blurPx, weightBias));
                            produced++;
                            continue;
                        }
                    }
                    continue;
                }
                output.Add(BuildQuad(g, g.Uv, blurPx, weightBias));
                produced++;
            }

            // Decorations: add as solid white quads (no atlas sampling). They share the
            // _TEXT keyword but use a synthetic UV inside the atlas's reserved (0..1) →
            // sample of a pre-filled pixel. v1 punts: emit decoration as a degenerate
            // quad whose UV matches a known fully-opaque texel from the rasterizer's
            // padding rim. The renderer treats UV.area==0 as "not text"; we fall back
            // to a flat color quad for decorations via SubmitFillRect on the batcher.
            for (int i = 0; i < scratch.Decorations.Count; i++) {
                var d = scratch.Decorations[i];
                output.Add(new SdfGlyphQuad(
                    new PaintRect(d.X, d.Y, d.Width, d.Height),
                    d.Color,
                    new Vector2(0f, 0f),
                    new Vector2(0f, 0f)
                ));
                produced++;
            }
            return produced > 0;
        }

        bool TryEnsureRaster(FaceInfo face, uint codepoint, double fontSize) {
            if (Rasterizer == null) return false;
            if (!Rasterizer.TryRasterizeAsRasterizedGlyph(face, codepoint, fontSize, out var raster)) return false;
            if (Metrics == null) return false;
            // TryUploadRaster writes raster bytes into the atlas's CPU buffer AND
            // calls Texture2D.Apply so the GPU page reflects the new glyph. This is
            // the primary path for glyphs that miss faceMetrics.TryGetGlyphRect.
            return Metrics.Atlas != null && Metrics.Atlas.TryUploadRaster(face, codepoint, fontSize, raster);
        }

        // Shape a run using the TMP source. Walks the codepoints, consults
        // TmpSource.TryGetGlyph for UV + metrics, and emits SdfGlyphQuads
        // positioned at (cursor + bearingX, baselineY - bearingY) — the same
        // formula SdfTextRunBaker uses for the FontEngine path. Kerning is
        // added via TmpSource.GetKern between adjacent glyphs.
        // Decorations (underline, etc.) are emitted as solid quads using the
        // ascent/descent of the TMP face.
        //
        // Returns the number of quads appended to `output` and reports the
        // number of renderable codepoints that were NOT in the TMP atlas via
        // `missed`. The caller uses missed > 0 as the trigger to try the
        // FontEngine path. ShapeTmp still advances over misses with an
        // estimated width so partial TMP output remains readable when no
        // fallback face can draw the run.
        int ShapeTmp(DrawTextCommand command, List<SdfGlyphQuad> output, out int atlasId, out int missed) {
            atlasId = 0;
            missed = 0;
            string text = command.Text;
            double fontSize = command.Font.Size > 0 ? command.Font.Size : 14;
            double pointSize = TmpSource.PointSize <= 0 ? fontSize : TmpSource.PointSize;
            double scale = fontSize / pointSize;
            double ascent = TmpSource.AscentLine * scale;
            double cursor = command.Bounds.X;
            // CSS Inline Layout §3: prefer layout's baseline (the run's
            // alphabetic baseline within its line box) when provided. The
            // font-metric fallback `Bounds.Y + ascent` top-aligns the run,
            // which disagrees with both layout AND the ATG primary face's
            // baseline (the historic font-flap on face fallback). Honouring
            // the layout baseline keeps text where layout placed it and makes
            // the primary/fallback faces render at the same vertical position.
            double baselineY = !double.IsNaN(command.LayoutBaseline)
                ? command.Bounds.Y + command.LayoutBaseline
                : command.Bounds.Y + ascent;
            double letterSpacing = command.LetterSpacingPx;
            if (Weva.Diagnostics.UILayoutDiagnostics.Enabled) {
                Weva.Diagnostics.UILayoutDiagnostics.Trace("SdfGlyphAtlasAdapter.ShapeTmp",
                    $"text='{text}' fs={fontSize} weight={command.Font.Weight} " +
                    $"LS={letterSpacing} bounds=({command.Bounds.X:F2},{command.Bounds.Y:F2}) " +
                    $"W={command.Bounds.Width:F2} H={command.Bounds.Height:F2}");
            }

            // Path A — text-shadow blur. blurPx is forwarded to each emitted
            // SdfGlyphQuad; the shader widens its AA band by `blurPx` screen
            // pixels so the SDF silhouette feathers outward. Quad bounds are
            // not additionally inflated — the existing TMP `pad` term below
            // (atlasPadding scaled to screen) already covers the atlas's SDF
            // spread ring, which is the only region where the AA band has
            // valid distance information to feather into. See the FontEngine
            // path's TryShape comment for the same tradeoffs.
            float blurPx = command.BlurRadius > 0 ? (float)command.BlurRadius : 0f;
            // Faux-bold SDF threshold shift. TMP-baked atlases only register a
            // single weight per (family, style) face, so `font-weight: 600+`
            // would otherwise render visually identical to 400. Widen the SDF
            // smoothstep band at the shader instead — but only ABOVE the face's
            // natural weight (TmpSource.Face.Weight), so a face that's already
            // bold (Sniglet ExtraBold = 800) isn't double-bolded. See
            // ComputeWeightBias.
            float weightBias = ComputeWeightBias(command.Font.Weight, TmpSource.Face.Weight);

            int produced = 0;
            int i = 0;
            int n = text.Length;
            uint prevCp = 0;

            // Resolve atlasId once. We register on demand in case the bootstrap
            // didn't (e.g. tests that bypass SdfBootstrap.PickBest).
            if (TmpAtlas != null && TmpFace.IsValid) {
                AtlasRegistry.RegisterAtlas(TmpFace, TmpAtlas);
                atlasId = AtlasRegistry.GetAtlasId(TmpAtlas);
            }

            // Materialize the per-fallback atlas ids on first use. Each entry
            // in TmpChain becomes a (GlyphAtlas shell, atlasId) pair so the
            // emitted SdfGlyphQuad can carry the right id when a fallback
            // face produces the glyph. The primary face is NOT in this array
            // (its atlasId is the run-level `atlasId` above); we only build
            // entries for indices >= 1 of the chain (the fallbacks proper).
            EnsureTmpChainAtlases();

            while (i < n) {
                char c = text[i];
                int len = 1;
                uint cp = c;
                if (char.IsHighSurrogate(c) && i + 1 < n && char.IsLowSurrogate(text[i + 1])) {
                    cp = (uint)char.ConvertToUtf32(c, text[i + 1]);
                    len = 2;
                }
                if (cp == '\t' || cp == '\n' || cp == '\r') {
                    i += len;
                    continue;
                }
                // Variation selectors (U+FE00..U+FE0F, U+E0100..U+E01EF) are
                // zero-width modifiers — they instruct the shaper which
                // glyph variant to use for the previous codepoint. They have
                // no glyph of their own; emitting a quad or advancing the
                // cursor for them would insert a phantom gap. Skip silently.
                if ((cp >= 0xFE00 && cp <= 0xFE0F) || (cp >= 0xE0100 && cp <= 0xE01EF)) {
                    i += len;
                    continue;
                }

                if (prevCp != 0) {
                    cursor += TmpSource.GetKern(prevCp, cp, fontSize);
                }

                // Resolve which face in the chain has this codepoint. Index 0
                // means the primary TmpSource; index >= 1 selects a fallback.
                // -1 means no face has the glyph — treat as miss and advance.
                uint glyphCp = cp;
                int chainIndex = ResolveTmpChainIndex(glyphCp, fontSize, out var resolvedSource);
                if (chainIndex >= 0) {
                    // Cursor advance + bearings come from the resolved face.
                    // Padding scale also uses the resolved face's pointSize so
                    // emoji atlases (typically baked at a different point size
                    // than text faces) inflate their quads correctly.
                    double resolvedPointSize = resolvedSource.PointSize <= 0 ? fontSize : resolvedSource.PointSize;
                    double resolvedScale = fontSize / resolvedPointSize;

                    if (resolvedSource.TryGetGlyph(glyphCp, fontSize, out var uv, out var gm)) {
                        if (gm.Width > 0 && gm.Height > 0) {
                            // TMP atlases bake `atlasPadding` pixels of SDF spread
                            // around each visible glyph. Inflate the quad by the
                            // resolved face's atlasPadding scaled by ITS scale so
                            // the SDF rim falls inside the sampled region.
                            double pad = (resolvedSource.Asset != null ? resolvedSource.Asset.atlasPadding : 0) * resolvedScale;
                            // Keep glyphX at sub-pixel precision. Rounding to
                            // integer pixels at small font sizes (11-14px) snaps
                            // each glyph independently, so accumulated 0.5px
                            // errors compress the word and adjacent glyphs
                            // overlap. The shader's bilinear SDF sampling
                            // produces clean edges at any sub-pixel offset, so
                            // there's no quality loss from skipping the snap.
                            double glyphX = cursor + gm.BearingX;
                            double glyphY = baselineY - gm.BearingY;
                            // Per-quad atlas id: 0 (= use the run's atlasId) when
                            // the glyph came from the primary face, otherwise the
                            // fallback's registered atlasId so the batcher splits
                            // at this boundary.
                            int quadAtlasId = chainIndex == 0 ? 0 : ResolveFallbackAtlasId(chainIndex);
                            // text-shadow Path A: emit the same quad, just
                            // tag it with the blur radius so the shader
                            // widens its SDF AA band; the existing `pad`
                            // already covers the atlas SDF spread ring.
                            output.Add(new SdfGlyphQuad(
                                new PaintRect(glyphX - pad, glyphY - pad, gm.Width + 2.0 * pad, gm.Height + 2.0 * pad),
                                command.Color,
                                new Vector2(uv.U0, uv.V0),
                                new Vector2(uv.U1, uv.V1),
                                quadAtlasId,
                                blurPx,
                                weightBias
                            ));
                            produced++;
                        }
                        cursor += gm.AdvanceX;
                    }
                } else {
                    // No face in the chain has this codepoint (e.g. ·, •, emoji
                    // when no emoji fallback is registered). Emit no quad but
                    // DO advance the cursor by an estimated glyph width so
                    // subsequent letters in the run don't stack on top of one
                    // another. Probing the space-glyph advance gives a
                    // reasonable typographic placeholder gap; if even that
                    // fails (degenerate face) we fall back to 0.4*fontSize.
                    if (TmpSource.TryGetGlyph(0x20, fontSize, out _, out var spaceGm) && spaceGm.AdvanceX > 0) {
                        cursor += spaceGm.AdvanceX;
                    } else {
                        cursor += fontSize * 0.4;
                    }
                    // Phase 1 diagnostic: log once per codepoint when an emoji
                    // is missing from every face in the chain. Filtered to
                    // emoji ranges by the registry helper so ordinary ASCII
                    // misses don't spam the console — those typically indicate
                    // a different problem (font load failure).
                    TmpFontAssetRegistry.ReportEmojiMiss(cp);
                    missed++;
                }
                prevCp = glyphCp;
                i += len;
                // CSS Text §7.2: letter-spacing inserts space BETWEEN
                // typographic letter units, not after the last one. Mirror
                // of the fix in SdfTextRunBaker:306 — without this gate the
                // run's decoration extent and AdvanceX include one trailing
                // LS past the final glyph, visible as a sliver of underline/
                // line-through extending past the last character.
                if (i < n) cursor += letterSpacing;
            }

            // Decorations: same recipe as SdfTextRunBaker, scaled for TMP face.
            double runWidth = cursor - command.Bounds.X;
            double thickness = System.Math.Max(1.0, ascent / 12.0);
            if ((command.Decoration & TextDecoration.Underline) != 0) {
                double y = baselineY + ascent / 8.0;
                output.Add(new SdfGlyphQuad(
                    new PaintRect(command.Bounds.X, y, runWidth, thickness),
                    command.Color, new Vector2(0, 0), new Vector2(0, 0)
                ));
                produced++;
            }
            if ((command.Decoration & TextDecoration.Overline) != 0) {
                double y = baselineY - ascent;
                output.Add(new SdfGlyphQuad(
                    new PaintRect(command.Bounds.X, y, runWidth, thickness),
                    command.Color, new Vector2(0, 0), new Vector2(0, 0)
                ));
                produced++;
            }
            if ((command.Decoration & TextDecoration.LineThrough) != 0) {
                double y = baselineY - ascent * 0.4;
                output.Add(new SdfGlyphQuad(
                    new PaintRect(command.Bounds.X, y, runWidth, thickness),
                    command.Color, new Vector2(0, 0), new Vector2(0, 0)
                ));
                produced++;
            }
            return produced;
        }

        static SdfGlyphQuad BuildQuad(SdfTextRunBaker.BakedGlyph g, GlyphRect uv) {
            return new SdfGlyphQuad(
                new PaintRect(g.X, g.Y, g.Width, g.Height),
                g.Color,
                new Vector2(uv.U0, uv.V0),
                new Vector2(uv.U1, uv.V1)
            );
        }

        // Path A — text-shadow blur tagging overload. The quad geometry is
        // unchanged from the crisp BuildQuad; only the per-quad blurRadius
        // is forwarded so the shader can widen its AA band. See the comment
        // at the call site (TryShape) for why we don't also inflate the
        // quad bounds — the BakedGlyph already includes the atlas SDF
        // padding ring, which is the only region where the AA can feather
        // outside the glyph silhouette.
        static SdfGlyphQuad BuildQuad(SdfTextRunBaker.BakedGlyph g, GlyphRect uv, float blurRadius) {
            return new SdfGlyphQuad(
                new PaintRect(g.X, g.Y, g.Width, g.Height),
                g.Color,
                new Vector2(uv.U0, uv.V0),
                new Vector2(uv.U1, uv.V1),
                0,
                blurRadius
            );
        }

        // Faux-bold overload. Both blurRadius and weightBias ride along on
        // each per-glyph quad. The shader interprets weightBias as an SDF
        // threshold shift (smoothstep midpoint moves from 0.5 to 0.5 - bias)
        // so the glyph silhouette widens without needing a separately baked
        // bold atlas. Bias is zero for weight 400, so emitted quads are
        // bit-identical to the prior (blur, 0) overload for regular text.
        static SdfGlyphQuad BuildQuad(SdfTextRunBaker.BakedGlyph g, GlyphRect uv, float blurRadius, float weightBias) {
            return new SdfGlyphQuad(
                new PaintRect(g.X, g.Y, g.Width, g.Height),
                g.Color,
                new Vector2(uv.U0, uv.V0),
                new Vector2(uv.U1, uv.V1),
                0,
                blurRadius,
                weightBias
            );
        }

        // Maps CSS font-weight (typically 100..900, but the spec allows 1..1000)
        // to an SDF-threshold shift in [0, BiasMax]. Weight 400 (regular)
        // returns 0 — paint is byte-for-byte identical to pre-faux-bold
        // output. Weight 700 (bold) returns ≈0.075, which moves the
        // smoothstep midpoint inward by 0.075 SDF units and widens the
        // glyph by ≈1.5 px at typical 24-pt rasterizations (the precise
        // pixel gain scales with fwidth(d), so it stays proportional to
        // font size). Weight 900 (black) returns the cap, ≈0.10 — visibly
        // chunkier than 700.
        //
        // Weights below 400 are NOT mapped to negative bias here: thinning
        // glyphs via SDF threshold shift produces brittle artifacts at small
        // sizes (the smoothstep band collapses past the SDF padding ring).
        // Faux-thin is a deferred feature; weights < 400 render as regular.
        // Faux-bold from a regular (400) face. Kept for the FontEngine path,
        // whose loaded face carries no reliable natural weight.
        internal static float ComputeWeightBias(int weight) => ComputeWeightBias(weight, 400);

        // Faux-bold that synthesizes only the gap ABOVE the face's natural
        // weight, so an already-bold face (e.g. a Sniglet ExtraBold registered
        // as weight 800) asked to render 800 gets NO synthesis — it was being
        // double-bolded, fattening stems and closing counters vs Chrome (which
        // matches the real bold face). A 400 face asked for 700 still gets the
        // full bold synthesis. The gap is mapped as if synthesizing from
        // regular, reusing the calibrated 400→0 / 700→0.075 / 1000→0.10 ramp.
        internal static float ComputeWeightBias(int requestedWeight, int faceWeight) {
            int over = requestedWeight - System.Math.Max(1, faceWeight);
            if (over <= 0) return 0f;
            int synth = 400 + over;
            if (synth > 1000) synth = 1000;
            const float BiasMax = 0.10f;
            if (synth <= 700) {
                return (synth - 400) / 300f * (BiasMax * 0.75f);
            }
            return BiasMax * 0.75f + (synth - 700) / 300f * (BiasMax * 0.25f);
        }

        // Walks the TMP chain (primary at index 0, fallbacks at >=1) and
        // returns the first face whose character table contains `codepoint`.
        // Returns -1 + null source when no face has it. Cheap path: when no
        // chain is configured, this just probes TmpSource directly so the
        // single-face hot path stays a single dictionary lookup.
        //
        // Color-vs-mono routing for symbol codepoints: when the codepoint is
        // in a Unicode range with NO Emoji_Presentation=Yes characters (e.g.
        // Geometric Shapes U+25A0-U+25FF, plus the BLACK/WHITE STAR pair),
        // the bake script's broad emoji ranges sweep these glyphs into the
        // COLOR atlas anyway — which then ignores CSS `color` and renders the
        // baked color. Authors using `▲ ● ★` in HTML expect CSS tinting to
        // work, so for these codepoints we walk the chain preferring a
        // monochrome (SDF) fallback over a color one. The rule is narrow on
        // purpose: ❤ (U+2764 in Dingbats), ⚡ (U+26A1 in Misc Symbols), and
        // every supplementary-plane emoji keep their color presentation.
        int ResolveTmpChainIndex(uint codepoint, double fontSize, out TmpFontAssetSource source) {
            // Probe primary first.
            if (TmpSource != null && TmpSource.TryGetGlyph(codepoint, fontSize, out _, out _)) {
                source = TmpSource;
                return 0;
            }
            // No fallback chain registered: stop here.
            if (TmpChain == null || TmpChain.Count <= 1) {
                source = null;
                return -1;
            }
            // Author-intended monochrome geometric symbol: walk the chain
            // looking for a non-color face FIRST so CSS `color` can tint the
            // glyph. Only fall back to a color face if no mono face has it.
            // The chain stores the primary at [0]; we already probed it above.
            bool preferMono = IsAuthorMonoSymbol(codepoint);
            if (preferMono) {
                for (int j = 1; j < TmpChain.Count; j++) {
                    var s = TmpChain[j];
                    if (s == null || s.Asset == null) continue;
                    if (s.IsColor) continue; // skip color faces on the first pass
                    if (s.TryGetGlyph(codepoint, fontSize, out _, out _)) {
                        source = s;
                        return j;
                    }
                }
            }
            // TMP-LATIN-1: accented Latin missing from the PRIMARY face must
            // not borrow from fallback atlases at all. The fallbacks are
            // symbol/emoji bakes (SegoeUIEmoji, the runtime Segoe UI Symbol
            // face) — a borrowed å/é/ü renders in a mismatched typeface. The
            // old guard skipped only assets NAMED "*Emoji*", which the symbol
            // font slips past. Returning -1 marks the codepoint a TMP miss,
            // and TryShape's existing miss machinery reshapes the WHOLE run
            // through FontEngine with the real font face — consistent
            // typography for the entire word (with TMP partial output as the
            // last-resort keep if FontEngine can't draw the run).
            if (IsLatinLetterOrMark(codepoint)) {
                source = null;
                return -1;
            }
            // Default walk: first face in chain order that has the codepoint.
            for (int j = 1; j < TmpChain.Count; j++) {
                var s = TmpChain[j];
                if (s == null || s.Asset == null) continue;
                if (s.TryGetGlyph(codepoint, fontSize, out _, out _)) {
                    source = s;
                    return j;
                }
            }
            source = null;
            return -1;
        }

        static bool IsLatinLetterOrMark(uint codepoint) {
            return (codepoint >= 0x00C0 && codepoint <= 0x00FF && codepoint != 0x00D7 && codepoint != 0x00F7)
                || (codepoint >= 0x0100 && codepoint <= 0x024F)
                || (codepoint >= 0x0300 && codepoint <= 0x036F)
                || (codepoint >= 0x1E00 && codepoint <= 0x1EFF);
        }

        // True for codepoints that are reliably monochrome per Unicode — the
        // block has no Emoji_Presentation=Yes characters — but which a broad
        // emoji-range bake can drag into the COLOR atlas. CSS authors using
        // these symbols expect `color` to tint them, so the shaper routes
        // around the color atlas when a monochrome fallback also has the glyph.
        //
        // Whitelist (narrow on purpose):
        //   - U+25A0..U+25FF Geometric Shapes (▲ ● ◆ ■ ◇ ▼ etc.)
        //   - U+2605, U+2606 BLACK STAR / WHITE STAR (Misc Symbols block,
        //     but not in Unicode's emoji set — authors use them for ratings,
        //     bullet markers, etc., and tint via CSS).
        // Everything else (Misc Technical, Misc Symbols at large, Dingbats,
        // Supplementary Plane) keeps its first-fallback-wins behaviour so
        // ❤ ⚡ ✨ 📞 📚 etc. stay color-emoji.
        static bool IsAuthorMonoSymbol(uint codepoint) {
            if (codepoint >= 0x25A0 && codepoint <= 0x25FF) return true;
            if (codepoint == 0x2605 || codepoint == 0x2606) return true;
            return false;
        }

        // Builds the parallel array of GlyphAtlas shells / atlasIds for the
        // fallback chain on first use. Each fallback face needs its own
        // GlyphAtlas-shell-with-TextureOverride registered against
        // AtlasRegistry so the URP renderer can resolve the right Texture2D
        // when SubmitGlyphQuads splits batches at atlas boundaries. We rebuild
        // the cache when TmpChain instance changes.
        void EnsureTmpChainAtlases() {
            if (TmpChain == null || TmpChain.Count == 0) {
                tmpChainAtlases = null;
                tmpChainAtlasIds = null;
                tmpChainSnapshot = null;
                return;
            }
            if (object.ReferenceEquals(tmpChainSnapshot, TmpChain)
                && tmpChainAtlases != null
                && tmpChainAtlases.Length == TmpChain.Count) return;
            tmpChainSnapshot = TmpChain;
            tmpChainAtlases = new GlyphAtlas[TmpChain.Count];
            tmpChainAtlasIds = new int[TmpChain.Count];
            // Index 0 (primary) reuses the run-level TmpAtlas; we leave the
            // entries as null/0 since ResolveFallbackAtlasId is only called
            // for chainIndex >= 1.
            for (int j = 1; j < TmpChain.Count; j++) {
                var s = TmpChain[j];
                if (s == null || s.Asset == null || s.Atlas == null) continue;
                var face = s.Face;
                if (!face.IsValid) continue;
                // Reuse the existing per-face atlas if it was already
                // registered (e.g. a previous TryShape created it). Otherwise
                // wrap the TMP texture in a fresh GlyphAtlas shell.
                var atlas = AtlasRegistry.GetAtlas(face);
                if (atlas == null) {
                    atlas = new GlyphAtlas { TextureOverride = s.Atlas };
                    AtlasRegistry.RegisterAtlas(face, atlas);
                } else if (atlas.TextureOverride != s.Atlas) {
                    // Existing shell points elsewhere — refresh so the URP
                    // pass binds the right texture.
                    atlas.TextureOverride = s.Atlas;
                }
                tmpChainAtlases[j] = atlas;
                tmpChainAtlasIds[j] = AtlasRegistry.GetAtlasId(atlas);
                // Tag color-baked emoji atlases so the renderer can flip to
                // the _TEXT_COLOR shader variant (RGBA sampling) when the
                // batch routes through this fallback's atlasId.
                if (s.IsColor) {
                    AtlasRegistry.MarkColorAtlas(atlas);
                }
            }
        }

        int ResolveFallbackAtlasId(int chainIndex) {
            if (tmpChainAtlasIds == null) return 0;
            if (chainIndex < 0 || chainIndex >= tmpChainAtlasIds.Length) return 0;
            return tmpChainAtlasIds[chainIndex];
        }
    }
}
#endif
