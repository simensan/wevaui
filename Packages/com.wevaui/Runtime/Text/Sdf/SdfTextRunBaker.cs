using System.Collections.Generic;
using Weva.Paint;
using Weva.Text.TextCore;

namespace Weva.Text.Sdf {
    // SdfTextRunBaker turns a text + computed-style request into a list of
    // BakedGlyph entries (origin, UV rect, atlas page) ready for the URP backend
    // to emit per-glyph quads. v1 honors:
    //   - font-family / font-size / font-style (italic) / font-weight (bold variants
    //     when a separate font asset is registered, otherwise the regular face is
    //     used and the renderer is expected to weight-bias the SDF threshold).
    //   - letter-spacing (extra px between glyphs at the layout step).
    //   - text-decoration (underline / overline / line-through emitted as
    //     additional axis-aligned BakedDecoration rects).
    //   - subpixel positioning: per-glyph X coordinates stay at full double
    //     precision; the renderer rounds per its own quantization policy.
    //
    // What's deferred:
    //   - per-glyph kerning (we ask SdfFontMetrics.GetKern, which is a 0-stub
    //     hook; the seam exists for when the kerning table wiring lands).
    //   - shaping (ligatures, contextual forms): we map 1 codepoint -> 1 glyph.
    //   - color emoji: glyph color is the run's flat tint.
    public sealed class SdfTextRunBaker {
        public readonly struct BakedGlyph {
            // Pixel-space glyph origin — the cursor + bearing position the
            // line breaker measured. Tests pin this as the logical origin
            // (Letter_spacing_pushes_origins_by_extra_pixels,
            // Origin_offset_translates_all_glyphs,
            // TryShape_preserves_origin_offsets_in_quad_bounds), so we
            // emit the un-shifted origin rather than the padded quad's
            // top-left. Width/Height still include the 2*pad rim
            // inflation so atlas sampling covers the full SDF footprint.
            public readonly double X;
            public readonly double Y;
            public readonly double Width;
            public readonly double Height;

            // Atlas UV rect from the GlyphAtlas (already converted to 0..1 with
            // atlas dimensions baked in; no further normalization needed).
            public readonly GlyphRect Uv;

            // Source codepoint and the FaceInfo this glyph was rasterized from.
            // The renderer indexes AtlasRegistry by Face to bind the right
            // Texture2D for this quad.
            public readonly uint Codepoint;
            public readonly FaceInfo Face;
            public readonly LinearColor Color;

            public BakedGlyph(double x, double y, double w, double h, GlyphRect uv,
                              uint codepoint, FaceInfo face, LinearColor color) {
                X = x; Y = y; Width = w; Height = h;
                Uv = uv; Codepoint = codepoint; Face = face; Color = color;
            }
        }

        public readonly struct BakedDecoration {
            public readonly double X;
            public readonly double Y;
            public readonly double Width;
            public readonly double Height;
            public readonly LinearColor Color;
            public readonly TextDecoration Kind;

            public BakedDecoration(double x, double y, double w, double h, LinearColor color, TextDecoration kind) {
                X = x; Y = y; Width = w; Height = h; Color = color; Kind = kind;
            }
        }

        public sealed class Request {
            public string Text;
            public string FontFamily;
            public double FontSize = 16;
            public FontStyle FontStyle;
            public int FontWeight = 400;
            public double LetterSpacingPx;
            public TextDecoration Decoration;
            public LinearColor Color = LinearColor.White;
            public double OriginX;
            public double OriginY;
            // Decoration thickness override; -1 = derive from font (ascent/12).
            public double DecorationThicknessPx = -1;
            // CSS Text Decoration 4 §3.3: extra offset added to the underline
            // baseline. -1 / 0 = use spec default. Currently only honoured
            // for the underline rect (overline / line-through ignore offset).
            public double DecorationOffsetPx = -1;
            // CSS Text Decoration 4 §3.2 line style. `Solid` is the cascade
            // initial; the baker emits one rect per active flag at this style.
            public DecorationStyle DecorationStyle = DecorationStyle.Solid;
            // text-decoration-color override. null = "use run Color" (the
            // back-compat default that prior tests pin).
            public LinearColor? DecorationColor;

            // CSS Fonts L4 §6.5 — `font-kerning: none` disables the GetKern
            // lookup so authors get the unkerned advance even when the font
            // has a kerning table / GPOS lookups. `auto` (UA's choice) and
            // `normal` (explicit "kern") leave this true; only the explicit
            // `none` flips it to false. Default true matches the spec
            // initial of `auto`.
            public bool KerningEnabled = true;
        }

        public sealed class Result {
            public readonly List<BakedGlyph> Glyphs = new();
            public readonly List<BakedDecoration> Decorations = new();
            public double AdvanceX;

            public void Clear() {
                Glyphs.Clear();
                Decorations.Clear();
                AdvanceX = 0;
            }
        }

        public SdfFontMetrics Metrics { get; }
        public CharacterFallback Fallback { get; set; }

        // Optional rasterizer hook. When set, missing-glyph requests are routed
        // here before the baker emits the BakedGlyph so the atlas has the SDF
        // bytes by the time the renderer issues a draw. The hook signature is
        // intentionally narrow (delegate, not interface) so headless tests can
        // wire a no-op without pulling SdfGlyphRasterizer through.
        public IGlyphRasterHook RasterHook { get; set; }

        public interface IGlyphRasterHook {
            bool TryEnsureRaster(FaceInfo face, uint codepoint, double fontSize);
            bool TryEnsureRaster(FaceInfo rasterFace, uint codepoint, double fontSize, FaceInfo uploadFace);
        }

        public SdfTextRunBaker(SdfFontMetrics metrics) {
            Metrics = metrics;
        }

        public Result Bake(Request request) {
            var result = new Result();
            BakeInto(request, result);
            return result;
        }

        public void BakeInto(Request req, Result result) {
            if (result == null) return;
            result.Clear();
            if (req == null || string.IsNullOrEmpty(req.Text) || Metrics == null) return;

            var primaryMetrics = Metrics.MetricsFor(req.FontFamily, req.FontStyle, req.FontWeight);
            if (primaryMetrics == null) return;
            var primaryFace = primaryMetrics.Face;

            // Run-origin pixel snap. Layout produces fractional X/Y (centered
            // flex children, padding rounding, scroll offsets). When a stem
            // lands at x=12.3 the SDF AA spreads its edge over columns 11+12,
            // halving apparent contrast — that's the soft-button-text bite
            // visible in 18px font-weight:700 UI labels. Snapping the run
            // origin shifts the whole run by ≤0.5px so the first glyph's
            // leading edge lands on a pixel column; inter-glyph spacing is
            // preserved (we don't round per-glyph — that accumulated up to
            // 0.5px of error per glyph and compressed words). Cost: animated
            // text moving sub-pixel will quantize to integer steps; for now
            // we accept that — virtually all UI text is static.
            double originX = System.Math.Round(req.OriginX);
            double originY = System.Math.Round(req.OriginY);

            double cursor = originX;
            double baselineY = originY + primaryMetrics.Ascent(req.FontSize);
            double ascent = primaryMetrics.Ascent(req.FontSize);

            // PAINT-1 diagnostic — fires only when UILayoutDiagnostics.Enabled
            // is true. The SDF baker doesn't carry an Element reference so we
            // log unconditionally when the flag is on; the caller is expected
            // to set/clear Enabled around the diagnostic session.
            if (Weva.Diagnostics.UILayoutDiagnostics.Enabled) {
                Weva.Diagnostics.UILayoutDiagnostics.Trace("SdfTextRunBaker.Bake",
                    $"text='{req.Text}' fs={req.FontSize} weight={req.FontWeight} " +
                    $"LS={req.LetterSpacingPx} " +
                    $"origin=({req.OriginX:F2},{req.OriginY:F2}) → rounded=({originX:F2},{originY:F2}) " +
                    $"ascent={ascent:F2} → baselineY={baselineY:F2}");
            }

            int i = 0;
            int n = req.Text.Length;
            uint prevCp = 0;
            FaceInfo prevFace = FaceInfo.Empty;

            while (i < n) {
                char c = req.Text[i];
                int len = 1;
                uint cp = c;
                if (char.IsHighSurrogate(c) && i + 1 < n && char.IsLowSurrogate(req.Text[i + 1])) {
                    cp = (uint)char.ConvertToUtf32(c, req.Text[i + 1]);
                    len = 2;
                }
                if (cp == '\t' || cp == '\n' || cp == '\r') {
                    i += len;
                    continue;
                }

                // Resolve which face actually has this codepoint.
                var face = ResolveFace(primaryFace, cp, req.FontWeight, req.FontStyle);
                bool isFallbackFace = !face.Equals(primaryFace);
                var faceMetrics = isFallbackFace
                    ? Metrics.MetricsFor(face.Family, req.FontStyle, req.FontWeight)
                    : primaryMetrics;
                if (faceMetrics == null) faceMetrics = primaryMetrics;

                // Apply kerning between the previous and current glyph (only if
                // they share a face; cross-face kerning is undefined). The
                // gate honours `font-kerning: none` per CSS Fonts L4 §6.5 —
                // when an author opts out, the shaper emits the bare per-
                // glyph advances even on faces with a kerning table wired in
                // via WithKernProvider.
                if (req.KerningEnabled && prevCp != 0 && prevFace.Equals(face)) {
                    cursor += Metrics.GetKern(faceMetrics, prevCp, cp, req.FontSize);
                }

                if (faceMetrics.TryGetAdvance(cp, req.FontSize, out double advance)) {
                    GlyphRect uv = GlyphRect.Empty;
                    GlyphMetrics gm = GlyphMetrics.Zero;
                    int paddingPx = 0;
                    bool haveGlyph = faceMetrics.TryGetGlyph(cp, req.FontSize, out uv, out gm, out paddingPx);
                    if (!haveGlyph && RasterHook != null) {
                        // Rasterize from the resolved face (may be a fallback font).
                        // Upload under the PRIMARY face key so primaryMetrics can
                        // find it — the atlas keys by (face, codepoint, size), and
                        // faceMetrics may have fallen back to primaryMetrics when
                        // MetricsFor returned null for the fallback family.
                        var rasterFace = isFallbackFace ? face : primaryFace;
                        var uploadFace = primaryFace;
                        if (RasterHook.TryEnsureRaster(rasterFace, cp, req.FontSize, uploadFace)) {
                            haveGlyph = faceMetrics.TryGetGlyph(cp, req.FontSize, out uv, out gm, out paddingPx);
                        }
                    }
                    if (haveGlyph && gm.Width > 0 && gm.Height > 0) {
                        // Quad spans the rasterized footprint plus padding rim so
                        // the SDF-spread bytes fall inside the sampled region.
                        // Position via bearing: glyph image's top-left = (cursor +
                        // bearingX, baselineY - bearingY); pad by the EXACT padding
                        // the rasterizer used (threaded through TryGetGlyph as
                        // RasterizedGlyph.Padding) — Bug #1 fix. Previously this
                        // was hard-coded to 8.0 while UnityFontEngineBackend's
                        // legacy stub used 9, producing 1 px shifts and 2 px clips.
                        double pad = paddingPx;
                        // Sub-pixel glyph origin: rounding to integer pixels at
                        // small sizes accumulated up to 0.5px per glyph and
                        // compressed words enough that adjacent letters
                        // touched. Bilinear SDF sampling produces clean edges
                        // at any sub-pixel offset; cursor still advances by
                        // exact `advance` so run width stays accurate.
                        double glyphX = cursor + gm.BearingX;
                        double glyphY = baselineY - gm.BearingY;
                        // BakedGlyph.X/Y carry the glyph ORIGIN (cursor + bearing),
                        // not the padded quad's top-left — that's what the
                        // line-breaker, hit-tester, and Letter_spacing /
                        // Origin_offset tests assert. Width/Height stay inflated
                        // by the SDF rim so atlas sampling covers the full
                        // raster footprint when the renderer draws a rect
                        // anchored at (X, Y).
                        double quadW = gm.Width + 2.0 * pad;
                        double quadH = gm.Height + 2.0 * pad;
                        result.Glyphs.Add(new BakedGlyph(
                            x: glyphX,
                            y: glyphY,
                            w: quadW,
                            h: quadH,
                            uv: uv,
                            codepoint: cp,
                            face: face,
                            color: req.Color
                        ));
                    } else if (haveGlyph && (gm.Width == 0 || gm.Height == 0)) {
                        // Invisible glyph (space, NBSP, ZWJ, etc.) — the metrics
                        // table reports zero width/height but the rasterizer may
                        // have allocated a placeholder UV rect anyway. Don't
                        // emit a quad: that placeholder rect would render as a
                        // rectangular ghost the size of the glyph cell, which
                        // visibly wrecked text whenever a string had a space
                        // (every space rendered as a 10×43 dark stripe).
                        // Cursor still advances via the AdvanceX path above.
                    } else if (uv.U1 > uv.U0 && uv.V1 > uv.V0) {
                        // No metrics available but we have a UV — fall back to the
                        // legacy advance-cell placement so something still draws.
                        result.Glyphs.Add(new BakedGlyph(
                            x: cursor,
                            y: baselineY - ascent,
                            w: advance,
                            h: faceMetrics.LineHeight(req.FontSize),
                            uv: uv,
                            codepoint: cp,
                            face: face,
                            color: req.Color
                        ));
                    }
                    cursor += advance;
                }

                prevCp = cp;
                prevFace = face;
                i += len;
                // CSS Text §7.2: letter-spacing inserts space BETWEEN
                // typographic letter units, not after the last one. Adding
                // an unconditional `cursor += LetterSpacingPx` here inflates
                // AdvanceX (and the run's painted decoration extent) by one
                // extra LS past the last glyph — visible as trailing
                // whitespace beyond the final character (e.g. "PLAY" with
                // letter-spacing:4 painted an extra 4px after the Y inside
                // the run's background/decoration rect). Layout already uses
                // N-1 gaps (InlineLayout.MeasureFastCached:418), so the paint
                // and layout extents now agree.
                if (i < n) cursor += req.LetterSpacingPx;
            }

            double runWidth = cursor - originX;
            result.AdvanceX = runWidth;

            // Decorations: emit a horizontal rect per active flag. Y positions:
            //   - underline: just below baseline (baselineY + ascent/8).
            //   - overline:  at the top of the cap-height (baselineY - ascent).
            //   - line-through: at the x-height midline (baselineY - ascent*0.4).
            // Thickness: ascent / 12 by default (≈8% of cap height), clamped to 1px.
            double thickness = req.DecorationThicknessPx > 0
                ? req.DecorationThicknessPx
                : System.Math.Max(1.0, ascent / 12.0);

            // CSS Text Decoration 4 §3 — color override. When the cascade
            // produced a non-currentcolor `text-decoration-color`, paint the
            // rect with THAT color instead of the run's glyph color.
            LinearColor decoColor = req.DecorationColor ?? req.Color;
            // text-underline-offset push-down (auto resolves to 0 at the
            // resolver, so DecorationOffsetPx <= 0 means "no extra offset").
            double underlineExtraOffset = req.DecorationOffsetPx > 0 ? req.DecorationOffsetPx : 0;

            if ((req.Decoration & TextDecoration.Underline) != 0) {
                double y = baselineY + ascent / 8.0 + underlineExtraOffset;
                EmitDecorationLine(result, originX, y, runWidth, thickness, decoColor,
                                   TextDecoration.Underline, req.DecorationStyle);
            }
            if ((req.Decoration & TextDecoration.Overline) != 0) {
                double y = baselineY - ascent;
                EmitDecorationLine(result, originX, y, runWidth, thickness, decoColor,
                                   TextDecoration.Overline, req.DecorationStyle);
            }
            if ((req.Decoration & TextDecoration.LineThrough) != 0) {
                double y = baselineY - ascent * 0.4;
                EmitDecorationLine(result, originX, y, runWidth, thickness, decoColor,
                                   TextDecoration.LineThrough, req.DecorationStyle);
            }
        }

        // Emits 1..N BakedDecoration rects for a single decoration line at
        // the requested style. Solid produces one rect (the v0 shape). Double
        // emits two parallel rects spaced by thickness*1.5 along the
        // y-axis. Dotted/Dashed produce a row of segmented rects (round-cap
        // segments are approximated by short solid rects). Wavy v1 falls
        // back to dashed — a sin-pattern needs the renderer to support
        // triangle / curve primitives we don't have yet.
        static void EmitDecorationLine(Result result, double x, double y, double width, double thickness,
                                       LinearColor color, TextDecoration kind, DecorationStyle style) {
            if (width <= 0 || thickness <= 0) return;
            switch (style) {
                case DecorationStyle.Solid:
                    result.Decorations.Add(new BakedDecoration(x, y, width, thickness, color, kind));
                    break;
                case DecorationStyle.Double: {
                    // Two parallel lines separated by thickness*1.5 (matches
                    // browser rendering for `text-decoration: underline double`).
                    double gap = thickness * 1.5;
                    result.Decorations.Add(new BakedDecoration(x, y, width, thickness, color, kind));
                    result.Decorations.Add(new BakedDecoration(x, y + gap, width, thickness, color, kind));
                    break;
                }
                case DecorationStyle.Dotted: {
                    // Square dots one thickness wide separated by one thickness
                    // gap — period = 2 * thickness. Final dot may overrun by
                    // up to one period; clamp to width.
                    double dot = System.Math.Max(1.0, thickness);
                    double period = dot * 2.0;
                    double cursor = x;
                    while (cursor < x + width) {
                        double seg = System.Math.Min(dot, x + width - cursor);
                        if (seg > 0) result.Decorations.Add(new BakedDecoration(cursor, y, seg, thickness, color, kind));
                        cursor += period;
                    }
                    break;
                }
                case DecorationStyle.Dashed:
                case DecorationStyle.Wavy: {
                    // Wavy is approximated as dashed in v1 (see spec note above).
                    double dash = System.Math.Max(2.0, thickness * 3.0);
                    double period = dash * 2.0;
                    double cursor = x;
                    while (cursor < x + width) {
                        double seg = System.Math.Min(dash, x + width - cursor);
                        if (seg > 0) result.Decorations.Add(new BakedDecoration(cursor, y, seg, thickness, color, kind));
                        cursor += period;
                    }
                    break;
                }
            }
        }

        FaceInfo ResolveFace(FaceInfo primary, uint codepoint, int weight, FontStyle style) {
            if (Fallback == null) return primary;
            return Fallback.Resolve(primary, codepoint, weight, style);
        }
    }
}
