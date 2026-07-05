using System.Collections.Generic;
using NUnit.Framework;
using Weva.Paint;
using Weva.Text.Sdf;
using Weva.Text.TextCore;

namespace Weva.Tests.Text.Sdf {
    public class SdfTextRunBakerTests {
        [SetUp]
        public void Reset() {
            FontResolver.ClearRegistered();
            FontResolver.SetSystemDefaults(new Dictionary<string, string> {
                ["sans-serif"] = "/test/sans.ttf"
            });
            AtlasRegistry.Clear();
        }

        sealed class StubLoader : FontLoader.IFaceLoader {
            public bool TryLoad(string family, FontStyle style, int weight, out FaceInfo face) {
                int styleFlags = style == FontStyle.Italic ? FaceInfo.StyleItalic : FaceInfo.StyleNormal;
                face = new FaceInfo(family, family + "/" + weight, weight, styleFlags);
                return true;
            }
        }

        static SdfTextRunBaker MakeBaker() {
            var fl = new FontLoader(new StubLoader(), new StubBackend());
            var sdf = new SdfFontMetrics(fl, new StubBackend());
            return new SdfTextRunBaker(sdf);
        }

        static SdfTextRunBaker.Request MakeRequest(string text, double fontSize = 16) {
            return new SdfTextRunBaker.Request {
                Text = text,
                FontFamily = "sans-serif",
                FontSize = fontSize,
                FontStyle = FontStyle.Normal,
                FontWeight = 400,
                Color = LinearColor.White
            };
        }

        [Test]
        public void Bakes_three_glyphs_for_three_chars() {
            var baker = MakeBaker();
            var result = baker.Bake(MakeRequest("abc"));
            Assert.That(result.Glyphs.Count, Is.EqualTo(3));
        }

        [Test]
        public void Empty_text_produces_no_glyphs() {
            var baker = MakeBaker();
            var result = baker.Bake(MakeRequest(""));
            Assert.That(result.Glyphs.Count, Is.EqualTo(0));
            Assert.That(result.AdvanceX, Is.EqualTo(0));
        }

        [Test]
        public void Quad_uvs_are_populated_from_atlas() {
            var baker = MakeBaker();
            var result = baker.Bake(MakeRequest("a"));
            Assert.That(result.Glyphs.Count, Is.EqualTo(1));
            var uv = result.Glyphs[0].Uv;
            Assert.That(uv.U1, Is.GreaterThan(uv.U0));
            Assert.That(uv.V1, Is.GreaterThan(uv.V0));
        }

        [Test]
        public void Subpixel_origins_preserve_fractional_advance() {
            var baker = MakeBaker();
            // StubBackend reports 0.5em per glyph: at fontSize=15.5, advance=7.75.
            // After 3 glyphs the cursor must be at exactly 23.25 px from origin.
            var req = MakeRequest("abc", 15.5);
            req.OriginX = 0;
            var result = baker.Bake(req);
            Assert.That(result.Glyphs[0].X, Is.EqualTo(0).Within(1e-9));
            Assert.That(result.Glyphs[1].X, Is.EqualTo(7.75).Within(1e-9));
            Assert.That(result.Glyphs[2].X, Is.EqualTo(15.5).Within(1e-9));
            Assert.That(result.AdvanceX, Is.EqualTo(23.25).Within(1e-9));
        }

        [Test]
        public void Letter_spacing_pushes_origins_by_extra_pixels() {
            var baker = MakeBaker();
            var req = MakeRequest("abc");
            req.LetterSpacingPx = 4;
            var result = baker.Bake(req);
            // Each glyph 8px wide + 4px tracking => positions: 0, 12, 24.
            Assert.That(result.Glyphs[0].X, Is.EqualTo(0).Within(1e-9));
            Assert.That(result.Glyphs[1].X, Is.EqualTo(12).Within(1e-9));
            Assert.That(result.Glyphs[2].X, Is.EqualTo(24).Within(1e-9));
        }

        [Test]
        public void Letter_spacing_advance_excludes_trailing_gap() {
            // CSS Text §7.2 — letter-spacing inserts space BETWEEN glyphs,
            // not after the last. For "abc" (3 chars) at 8px advance each
            // with letter-spacing 4px:
            //   AdvanceX = 3*8 + 2*4 = 32   (N-1 gaps)
            // NOT
            //   AdvanceX = 3*8 + 3*4 = 36   (N gaps; pre-fix behaviour)
            // The N-version inflates run.AdvanceX (and the underline/strike
            // decoration extent) past the visible glyph row, painting a
            // 4px sliver of trailing decoration past the last character.
            var baker = MakeBaker();
            var req = MakeRequest("abc");
            req.LetterSpacingPx = 4;
            var result = baker.Bake(req);
            Assert.That(result.AdvanceX, Is.EqualTo(32.0).Within(1e-9),
                $"AdvanceX must use N-1 letter-spacing gaps; got {result.AdvanceX}");
        }

        [Test]
        public void Letter_spacing_decoration_width_matches_glyph_extent() {
            // Underline (and overline / line-through) width = AdvanceX. With
            // the trailing-LS bug, decoration extended one full LS past the
            // visible glyph row.
            var baker = MakeBaker();
            var req = MakeRequest("abc");
            req.LetterSpacingPx = 4;
            req.Decoration = TextDecoration.Underline;
            var result = baker.Bake(req);
            Assert.That(result.Decorations.Count, Is.GreaterThan(0));
            // Glyph "c" placed at X=24, advance=8 → right edge at 32.
            double lastGlyphRight = result.Glyphs[result.Glyphs.Count - 1].X + 8;
            Assert.That(result.Decorations[0].Width, Is.EqualTo(lastGlyphRight).Within(1e-9),
                $"underline width must match last-glyph right-edge (no trailing LS); " +
                $"got {result.Decorations[0].Width}, expected {lastGlyphRight}");
        }

        [Test]
        public void Underline_emitted_below_baseline() {
            var baker = MakeBaker();
            var req = MakeRequest("abc");
            req.Decoration = TextDecoration.Underline;
            req.OriginY = 0;
            var result = baker.Bake(req);
            Assert.That(result.Decorations.Count, Is.EqualTo(1));
            var dec = result.Decorations[0];
            // Stub backend: ascent at 16px = 0.8 * 16 = 12.8. Baseline at OriginY+ascent=12.8.
            // Underline at baseline + ascent/8 = 12.8 + 1.6 = 14.4.
            Assert.That(dec.Kind, Is.EqualTo(TextDecoration.Underline));
            Assert.That(dec.Y, Is.EqualTo(14.4).Within(1e-9));
            Assert.That(dec.Width, Is.EqualTo(result.AdvanceX).Within(1e-9));
        }

        [Test]
        public void Strikethrough_and_overline_emit_separate_rects() {
            var baker = MakeBaker();
            var req = MakeRequest("abc");
            req.Decoration = TextDecoration.Overline | TextDecoration.LineThrough;
            var result = baker.Bake(req);
            Assert.That(result.Decorations.Count, Is.EqualTo(2));
            bool hasOverline = false, hasStrike = false;
            foreach (var d in result.Decorations) {
                if (d.Kind == TextDecoration.Overline) hasOverline = true;
                if (d.Kind == TextDecoration.LineThrough) hasStrike = true;
            }
            Assert.That(hasOverline, Is.True);
            Assert.That(hasStrike, Is.True);
        }

        [Test]
        public void Tab_and_newline_skipped_no_glyphs_emitted() {
            var baker = MakeBaker();
            var result = baker.Bake(MakeRequest("a\tb\nc"));
            Assert.That(result.Glyphs.Count, Is.EqualTo(3));
        }

        [Test]
        public void Italic_request_resolves_italic_face() {
            var baker = MakeBaker();
            var req = MakeRequest("a");
            req.FontStyle = FontStyle.Italic;
            var result = baker.Bake(req);
            Assert.That(result.Glyphs.Count, Is.EqualTo(1));
            Assert.That(result.Glyphs[0].Face.StyleFlags, Is.EqualTo(FaceInfo.StyleItalic));
        }

        [Test]
        public void Color_propagates_to_each_glyph() {
            var baker = MakeBaker();
            var req = MakeRequest("ab");
            req.Color = new LinearColor(0.2f, 0.4f, 0.6f, 1f);
            var result = baker.Bake(req);
            foreach (var g in result.Glyphs) {
                Assert.That(g.Color.R, Is.EqualTo(0.2f).Within(1e-6));
                Assert.That(g.Color.G, Is.EqualTo(0.4f).Within(1e-6));
                Assert.That(g.Color.B, Is.EqualTo(0.6f).Within(1e-6));
            }
        }

        [Test]
        public void Origin_offset_translates_all_glyphs() {
            var baker = MakeBaker();
            var req = MakeRequest("abc");
            req.OriginX = 100;
            req.OriginY = 50;
            var result = baker.Bake(req);
            Assert.That(result.Glyphs[0].X, Is.EqualTo(100).Within(1e-9));
            Assert.That(result.Glyphs[1].X, Is.EqualTo(108).Within(1e-9));
            Assert.That(result.Glyphs[2].X, Is.EqualTo(116).Within(1e-9));
        }

        [Test]
        public void Quad_inflation_uses_raster_padding_not_hard_coded() {
            // Bug #1 regression: baker used to hard-code pad=8.0 while the
            // legacy backend stub padded by 9, producing 1 px shifts and 2 px
            // clips. The baker now reads RasterizedGlyph.Padding (threaded via
            // TryGetGlyph). With a backend padding of 5, the quad must be
            // gm.Width + 2*5 wide, not gm.Width + 16.
            const int padding = 5;
            var fl = new FontLoader(new StubLoader(), new StubBackend(0.5, 1.2, 0.8, 0.4, padding));
            var sdf = new SdfFontMetrics(fl, new StubBackend(0.5, 1.2, 0.8, 0.4, padding));
            var baker = new SdfTextRunBaker(sdf);
            var result = baker.Bake(MakeRequest("a", 16));
            Assert.That(result.Glyphs.Count, Is.EqualTo(1));
            var glyph = result.Glyphs[0];
            // Stub: width = 0.5 * 16 = 8, height = (0.8+0.4) * 16 = 19.2 (ceil to 20 in raster, but baker uses gm.Width/Height = 8 / 19.2).
            // Quad width = gm.Width + 2*padding = 8 + 10 = 18.
            Assert.That(glyph.Width, Is.EqualTo(8 + 2 * padding).Within(1e-9));
            Assert.That(glyph.Height, Is.EqualTo(19.2 + 2 * padding).Within(1e-9));
        }

        [Test]
        public void Surrogate_pair_emits_single_glyph_with_combined_codepoint() {
            // 🛡 = U+1F6E1, encoded as surrogate pair (0xD83D, 0xDEE1) in UTF-16.
            // The baker must combine the surrogate halves into one codepoint and
            // emit exactly one glyph entry (not two for the high+low halves).
            var baker = MakeBaker();
            string text = char.ConvertFromUtf32(0x1F6E1);
            Assert.That(text.Length, Is.EqualTo(2), "🛡 must be 2 UTF-16 code units");
            var result = baker.Bake(MakeRequest(text));
            Assert.That(result.Glyphs.Count, Is.EqualTo(1));
            Assert.That(result.Glyphs[0].Codepoint, Is.EqualTo((uint)0x1F6E1));
        }

        [Test]
        public void Surrogate_pairs_intermixed_with_ascii_advance_correctly() {
            // "A🐲B": 4 UTF-16 code units, 3 codepoints, 3 glyphs.
            var baker = MakeBaker();
            string text = "A" + char.ConvertFromUtf32(0x1F432) + "B";
            Assert.That(text.Length, Is.EqualTo(4));
            var result = baker.Bake(MakeRequest(text, 16));
            Assert.That(result.Glyphs.Count, Is.EqualTo(3));
            Assert.That(result.Glyphs[0].Codepoint, Is.EqualTo((uint)'A'));
            Assert.That(result.Glyphs[1].Codepoint, Is.EqualTo((uint)0x1F432));
            Assert.That(result.Glyphs[2].Codepoint, Is.EqualTo((uint)'B'));
            // StubBackend: 0.5em width @ 16px = 8px per glyph; cursor advances
            // by `advance` per shaped codepoint (not per UTF-16 unit).
            Assert.That(result.Glyphs[1].X - result.Glyphs[0].X, Is.EqualTo(8).Within(1e-9));
            Assert.That(result.Glyphs[2].X - result.Glyphs[1].X, Is.EqualTo(8).Within(1e-9));
            Assert.That(result.AdvanceX, Is.EqualTo(24).Within(1e-9));
        }

        [Test]
        public void Lone_high_surrogate_at_string_end_treated_as_single_unit() {
            // Truncated surrogate pair (high surrogate with no low follower) must
            // not crash and must not consume past the buffer. v1 treats the lone
            // surrogate as its own 16-bit codepoint (renders tofu) rather than
            // skipping it, which preserves the source-position invariant.
            var baker = MakeBaker();
            string text = "A" + (char)0xD83D; // truncated 🛡
            var result = baker.Bake(MakeRequest(text));
            Assert.That(result.Glyphs.Count, Is.EqualTo(2));
            Assert.That(result.Glyphs[0].Codepoint, Is.EqualTo((uint)'A'));
            Assert.That(result.Glyphs[1].Codepoint, Is.EqualTo((uint)0xD83D));
        }

        [Test]
        public void Variation_selector_after_emoji_emits_extra_glyph() {
            // "🛡️" is U+1F6E1 + U+FE0F (variation selector-16). v1 has no
            // shaper, so VS-16 is emitted as its own glyph (it just tags the
            // preceding emoji as "emoji presentation" in real shapers).
            // This test pins the v1 gap: we render 2 glyphs, not 1.
            var baker = MakeBaker();
            string text = char.ConvertFromUtf32(0x1F6E1) + "️";
            var result = baker.Bake(MakeRequest(text));
            Assert.That(result.Glyphs.Count, Is.EqualTo(2),
                "v1 limitation: variation selectors are not collapsed into the preceding emoji");
            Assert.That(result.Glyphs[0].Codepoint, Is.EqualTo((uint)0x1F6E1));
            Assert.That(result.Glyphs[1].Codepoint, Is.EqualTo((uint)0xFE0F));
        }

        [Test]
        public void Missing_glyph_falls_back_to_primary_face_when_no_chain_face_has_it() {
            // No font in the fallback chain claims the codepoint — CharacterFallback
            // returns the primary face and the baker emits a glyph against it
            // (the .notdef / tofu rendering is the renderer's responsibility).
            var probe = new CharacterFallbackTests_FixedProbe();
            // Nobody has 0x1F6E1 (🛡).
            var fallback = new CharacterFallback(probe).WithChain(new[] { "Arial" });
            var baker = MakeBaker();
            baker.Fallback = fallback;
            string text = char.ConvertFromUtf32(0x1F6E1);
            var result = baker.Bake(MakeRequest(text));
            Assert.That(result.Glyphs.Count, Is.EqualTo(1));
            // Face equals the primary (resolved by SdfFontMetrics for "sans-serif").
            Assert.That(result.Glyphs[0].Face.Family, Is.EqualTo("sans-serif"));
        }

        [Test]
        public void Missing_glyph_routes_to_fallback_face_when_chain_member_has_glyph() {
            // Primary face lacks the codepoint; "Arial" in the chain claims it.
            // The baked glyph must carry the Arial face, not the primary, so the
            // renderer binds the right atlas page.
            FontResolver.SetSystemDefaults(new Dictionary<string, string> {
                ["sans-serif"] = "/test/sans.ttf",
                ["Arial"] = "/test/arial.ttf"
            });
            var probe = new CharacterFallbackTests_FixedProbe();
            probe.ByFamily["Arial"] = new HashSet<uint> { 0x1F6E1 };
            // sans-serif is NOT in the probe map, so it reports no glyph.
            var fallback = new CharacterFallback(probe).WithChain(new[] { "Arial" });
            var baker = MakeBaker();
            baker.Fallback = fallback;
            string text = char.ConvertFromUtf32(0x1F6E1);
            var result = baker.Bake(MakeRequest(text));
            Assert.That(result.Glyphs.Count, Is.EqualTo(1));
            Assert.That(result.Glyphs[0].Face.Family, Is.EqualTo("Arial"));
        }

        // Local copy of the probe stub used by CharacterFallbackTests, so the
        // baker tests don't depend on the test class layout in a sibling fixture.
        sealed class CharacterFallbackTests_FixedProbe : CharacterFallback.IGlyphProbe {
            public readonly Dictionary<string, HashSet<uint>> ByFamily = new();
            public bool HasGlyph(FaceInfo face, uint codepoint) {
                return ByFamily.TryGetValue(face.Family, out var set) && set.Contains(codepoint);
            }
        }

        [Test]
        public void LineThrough_emits_red_horizontal_rect_at_xheight_midline() {
            // "Hello" with text-decoration: line-through red.
            // StubBackend ascent at 16px = 0.8 * 16 = 12.8 → cap top at OriginY=0.
            // Baseline = OriginY + ascent = 12.8.
            // Strike Y = baseline - ascent*0.4 = 12.8 - 5.12 = 7.68 (≈ x-height/2 below cap).
            // Thickness = max(1, ascent/12) = 12.8/12 ≈ 1.0667.
            // Width = run advance = 5 glyphs * 8px = 40.
            var baker = MakeBaker();
            var req = MakeRequest("Hello");
            req.Decoration = TextDecoration.LineThrough;
            req.Color = new LinearColor(1f, 0f, 0f, 1f);
            req.OriginY = 0;
            var result = baker.Bake(req);
            Assert.That(result.Decorations.Count, Is.EqualTo(1));
            var d = result.Decorations[0];
            Assert.That(d.Kind, Is.EqualTo(TextDecoration.LineThrough));
            Assert.That(d.Y, Is.EqualTo(7.68).Within(1e-9));
            Assert.That(d.Height, Is.EqualTo(System.Math.Max(1.0, 12.8 / 12.0)).Within(1e-9));
            Assert.That(d.Width, Is.EqualTo(40).Within(1e-9));
            Assert.That(d.Color.R, Is.EqualTo(1f).Within(1e-6));
            Assert.That(d.Color.G, Is.EqualTo(0f).Within(1e-6));
            // Strike must lie strictly between cap-top (0) and baseline (12.8).
            Assert.That(d.Y, Is.GreaterThan(0));
            Assert.That(d.Y, Is.LessThan(12.8));
        }

        [Test]
        public void Decoration_thickness_override_replaces_default() {
            // Pins that DecorationThicknessPx != -1 wins over the ascent/12
            // default. This is the seam the (currently unwired)
            // text-decoration-thickness CSS property would route through —
            // proves the data path exists end-to-end at the baker level.
            var baker = MakeBaker();
            var req = MakeRequest("abc");
            req.Decoration = TextDecoration.Underline;
            req.DecorationThicknessPx = 4;
            var result = baker.Bake(req);
            Assert.That(result.Decorations.Count, Is.EqualTo(1));
            Assert.That(result.Decorations[0].Height, Is.EqualTo(4).Within(1e-9));
        }

        [Test]
        public void Decoration_color_currently_inherits_run_color_pinning_v1_gap() {
            // V1 gap: text-decoration-color is parsed and registered as a CSS
            // property but the baker only sees req.Color (the glyph fill).
            // Until TextRunResolver.ResolveDecoration wires color/style through
            // BoxToPaintConverter and DrawTextCommand, an underline always
            // matches the run color even when the author asks for a different
            // text-decoration-color. This test pins that gap so a fix is a
            // visible, intentional break.
            var baker = MakeBaker();
            var req = MakeRequest("abc");
            req.Decoration = TextDecoration.Underline;
            req.Color = new LinearColor(0f, 1f, 0f, 1f); // green run
            var result = baker.Bake(req);
            Assert.That(result.Decorations.Count, Is.EqualTo(1));
            // Underline currently echoes req.Color, NOT a separate decoration color.
            Assert.That(result.Decorations[0].Color.G, Is.EqualTo(1f).Within(1e-6));
            Assert.That(result.Decorations[0].Color.R, Is.EqualTo(0f).Within(1e-6));
        }

        [Test]
        public void LineThrough_uses_explicit_decoration_color_not_run_color() {
            // <span style="color: black; text-decoration: line-through red">.
            // The glyph color is black (run color), but the decoration rect
            // must paint with the explicit red text-decoration-color override.
            // Closes the v1 gap pinned by
            // Decoration_color_currently_inherits_run_color_pinning_v1_gap
            // — that test stayed valid because DecorationColor remains null
            // for the back-compat path.
            var baker = MakeBaker();
            var req = MakeRequest("Hello");
            req.Decoration = TextDecoration.LineThrough;
            req.Color = LinearColor.Black;                              // run color = black
            req.DecorationColor = new LinearColor(1f, 0f, 0f, 1f);       // text-decoration-color = red
            var result = baker.Bake(req);
            Assert.That(result.Decorations.Count, Is.EqualTo(1));
            var d = result.Decorations[0];
            Assert.That(d.Kind, Is.EqualTo(TextDecoration.LineThrough));
            Assert.That(d.Color.R, Is.EqualTo(1f).Within(1e-6), "decoration must be red, not the run's black color");
            Assert.That(d.Color.G, Is.EqualTo(0f).Within(1e-6));
            Assert.That(d.Color.B, Is.EqualTo(0f).Within(1e-6));
        }

        [Test]
        public void Underline_double_emits_two_parallel_rects() {
            // text-decoration: underline double — two parallel rects spaced by
            // thickness*1.5 along the y-axis (matches browser rendering).
            var baker = MakeBaker();
            var req = MakeRequest("abc");
            req.Decoration = TextDecoration.Underline;
            req.DecorationStyle = DecorationStyle.Double;
            req.OriginY = 0;
            var result = baker.Bake(req);
            Assert.That(result.Decorations.Count, Is.EqualTo(2),
                "double style produces TWO parallel underline rects");
            var first = result.Decorations[0];
            var second = result.Decorations[1];
            Assert.That(first.Kind, Is.EqualTo(TextDecoration.Underline));
            Assert.That(second.Kind, Is.EqualTo(TextDecoration.Underline));
            // Same width / x / thickness; only Y differs by thickness*1.5.
            Assert.That(second.X, Is.EqualTo(first.X).Within(1e-9));
            Assert.That(second.Width, Is.EqualTo(first.Width).Within(1e-9));
            Assert.That(second.Height, Is.EqualTo(first.Height).Within(1e-9));
            Assert.That(second.Y - first.Y, Is.EqualTo(first.Height * 1.5).Within(1e-9));
        }

        [Test]
        public void BakeInto_clears_existing_results() {
            var baker = MakeBaker();
            var result = new SdfTextRunBaker.Result();
            baker.BakeInto(MakeRequest("abc"), result);
            int firstCount = result.Glyphs.Count;
            baker.BakeInto(MakeRequest("d"), result);
            Assert.That(firstCount, Is.EqualTo(3));
            Assert.That(result.Glyphs.Count, Is.EqualTo(1));
        }

        // ---------------- font-kerning gate (CSS Fonts L4 §6.5) ----------------
        //
        // Request.KerningEnabled gates the Metrics.GetKern call. With kerning
        // ENABLED a -2 pair adjustment shifts the second glyph's origin two
        // pixels to the left of the unkerned position. With kerning DISABLED
        // the gate skips the GetKern call entirely and the second glyph lands
        // at the bare advance position. The fontEngine stub returns 8px per
        // glyph at 16px font-size, so the bare second-glyph X is `originX + 8`.

        static SdfTextRunBaker MakeBakerWithKern(double kernShift) {
            var fl = new FontLoader(new StubLoader(), new StubBackend());
            var sdf = new SdfFontMetrics(fl, new StubBackend());
            sdf.WithKernProvider((face, left, right, fs) =>
                (left == 'A' && right == 'V') ? kernShift : 0.0);
            return new SdfTextRunBaker(sdf);
        }

        [Test]
        public void Kerning_enabled_default_shifts_second_glyph_origin_for_kerned_pair() {
            var baker = MakeBakerWithKern(-2.0);
            var result = baker.Bake(MakeRequest("AV"));
            Assert.That(result.Glyphs.Count, Is.EqualTo(2),
                "expected one glyph per code point even when a kerning shift applies");
            // 8 px bare advance for the first glyph minus the 2 px pair adjustment.
            Assert.That(result.Glyphs[1].X, Is.EqualTo(6.0).Within(1e-9),
                "AV pair should kern by -2 when KerningEnabled (default) is true");
        }

        [Test]
        public void Kerning_disabled_skips_pair_adjustment() {
            // Same kern table, same Request — only KerningEnabled flips.
            // The gate must silence the -2 adjustment so the second glyph
            // lands at the bare advance position.
            var baker = MakeBakerWithKern(-2.0);
            var req = MakeRequest("AV");
            req.KerningEnabled = false;
            var result = baker.Bake(req);
            Assert.That(result.Glyphs.Count, Is.EqualTo(2));
            Assert.That(result.Glyphs[1].X, Is.EqualTo(8.0).Within(1e-9),
                "font-kerning: none should suppress the AV kern; second glyph stays at 8px");
        }

        [Test]
        public void Kerning_disabled_is_noop_when_provider_returns_zero() {
            // Pin: turning kerning off MUST NOT change layout for runs whose
            // pairs aren't kerned. Otherwise content with non-kerned text
            // would jitter when authors set font-kerning: none defensively.
            var baker = MakeBaker(); // no kern provider — returns 0 for every pair
            var resultOn = baker.Bake(MakeRequest("ABC"));
            var off = MakeRequest("ABC");
            off.KerningEnabled = false;
            var resultOff = baker.Bake(off);
            Assert.That(resultOff.Glyphs.Count, Is.EqualTo(resultOn.Glyphs.Count));
            for (int i = 0; i < resultOn.Glyphs.Count; i++) {
                Assert.That(resultOff.Glyphs[i].X, Is.EqualTo(resultOn.Glyphs[i].X).Within(1e-9),
                    "glyph " + i + " X should be identical with kerning on vs off when no pair kerns");
            }
        }
    }
}
