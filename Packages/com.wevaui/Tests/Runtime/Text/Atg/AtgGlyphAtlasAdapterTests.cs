#if UNITY_2023_1_OR_NEWER
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Weva.Paint;
using Weva.Rendering.URP;
using Weva.Text.Atg;
using FontAsset = UnityEngine.TextCore.Text.FontAsset;
using FontStyle = Weva.Paint.FontStyle;
using Object = UnityEngine.Object;
using PaintRect = Weva.Paint.Rect;

namespace Weva.Tests.Text.Atg {
    public class AtgGlyphAtlasAdapterTests {
        readonly List<Object> cleanup = new();
        readonly List<AtgGlyphAtlasAdapter> adapters = new();

        [TearDown]
        public void TearDown() {
            for (int i = adapters.Count - 1; i >= 0; i--) {
                adapters[i]?.ClearSmallTextCoverageCache();
            }
            adapters.Clear();
            for (int i = cleanup.Count - 1; i >= 0; i--) {
                if (cleanup[i] == null) continue;
                if (Application.isPlaying) Object.Destroy(cleanup[i]);
                else Object.DestroyImmediate(cleanup[i]);
            }
            cleanup.Clear();
        }

        [Test]
        public void Black_star_is_treated_as_css_tinted_text_symbol() {
            Assert.That(AtgPrimaryFallbackAdapter.IsTextDefaultEmoji(0x2605), Is.True);
        }

        [Test]
        public void Atg_adapters_allow_text_run_snapshots() {
            Assert.That(new AtgGlyphAtlasAdapter().UseTextRunSnapshots, Is.True);
            Assert.That(new AtgPrimaryFallbackAdapter().UseTextRunSnapshots, Is.True);
        }

        [Test]
        public void Bold_font_weight_carries_faux_bold_bias_through_atg_path() {
            if (!AtgGlyphAtlasAdapter.IsAvailable) {
                Assert.Ignore("ATG bindings are not available in this Unity version.");
            }

            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                       ?? Font.CreateDynamicFontFromOSFont("Arial", 16);
            Assert.That(font, Is.Not.Null);

            var fontAsset = FontAsset.CreateFontAsset(font);
            Assert.That(fontAsset, Is.Not.Null);
            fontAsset.hideFlags = HideFlags.HideAndDontSave;
            cleanup.Add(fontAsset);

            var adapter = new AtgGlyphAtlasAdapter {
                FontAsset = fontAsset,
                EnableSmallTextCoverage = false
            };
            adapters.Add(adapter);
            var quads = new List<SdfGlyphQuad>();
            var cmd = new DrawTextCommand(
                new PaintRect(0, 0, 120, 24),
                "Bold",
                new FontHandle("sans-serif", 16, 700, FontStyle.Normal),
                LinearColor.White,
                default);

            Assert.That(adapter.TryShape(cmd, quads, out _), Is.True);
            Assert.That(quads.Count, Is.GreaterThan(0));
            Assert.That(quads[0].WeightBias, Is.GreaterThan(0f));
            Assert.That(quads[0].WeightBias, Is.EqualTo(0.075f).Within(0.0001f));
        }

        [Test]
        public void Hinted_sdf_font_asset_shapes_small_regular_text_through_atg_path() {
            if (!AtgGlyphAtlasAdapter.IsAvailable) {
                Assert.Ignore("ATG bindings are not available in this Unity version.");
            }

            var fontAsset = FontAsset.CreateFontAsset(
                "Arial",
                "Regular",
                pointSize: 90,
                padding: 9,
                renderMode: UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA_HINTED);
            Assert.That(fontAsset, Is.Not.Null);
            fontAsset.hideFlags = HideFlags.HideAndDontSave;
            cleanup.Add(fontAsset);
            Assert.That(fontAsset.atlasRenderMode, Is.EqualTo(UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA_HINTED));

            var adapter = new AtgGlyphAtlasAdapter {
                FontAsset = fontAsset,
                EnableSmallTextCoverage = false
            };
            adapters.Add(adapter);
            var quads = new List<SdfGlyphQuad>();
            var cmd = new DrawTextCommand(
                new PaintRect(0, 0, 220, 18),
                "Speak with Captain Ren",
                new FontHandle("sans-serif", 12, 400, FontStyle.Normal),
                LinearColor.White,
                default);

            Assert.That(adapter.TryShape(cmd, quads, out _), Is.True);
            Assert.That(quads.Count, Is.GreaterThan(0));
            Assert.That(quads[0].WeightBias, Is.EqualTo(0f));
            Assert.That(quads[0].Bounds.Width, Is.GreaterThan(0));
            Assert.That(quads[0].Bounds.Height, Is.GreaterThan(0));
        }

        [Test]
        public void Bold_font_weight_uses_registered_bold_atg_face_when_available() {
            if (!AtgGlyphAtlasAdapter.IsAvailable) {
                Assert.Ignore("ATG bindings are not available in this Unity version.");
            }

            var regular = FontAsset.CreateFontAsset(
                "Segoe UI",
                "Regular",
                pointSize: 90,
                padding: 9,
                renderMode: UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA_HINTED);
            var bold = FontAsset.CreateFontAsset(
                "Segoe UI",
                "Bold",
                pointSize: 90,
                padding: 9,
                renderMode: UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA_HINTED);
            if (regular == null || bold == null || !bold.faceInfo.styleName.Contains("Bold")) {
                Assert.Ignore("Segoe UI Bold is not available on this platform.");
            }
            regular.hideFlags = HideFlags.HideAndDontSave;
            bold.hideFlags = HideFlags.HideAndDontSave;
            cleanup.Add(regular);
            cleanup.Add(bold);

            var adapter = new AtgGlyphAtlasAdapter {
                FontAsset = regular,
                BoldFontAsset = bold,
                EnableSmallTextCoverage = false
            };
            adapters.Add(adapter);
            var normalQuads = new List<SdfGlyphQuad>();
            var boldQuads = new List<SdfGlyphQuad>();
            var normal = new DrawTextCommand(
                new PaintRect(0, 0, 160, 24),
                "Quest Log",
                new FontHandle("sans-serif", 16, 400, FontStyle.Normal),
                LinearColor.White,
                default);
            var heavy = new DrawTextCommand(
                new PaintRect(0, 0, 160, 24),
                "Quest Log",
                new FontHandle("sans-serif", 16, 700, FontStyle.Normal),
                LinearColor.White,
                default);

            Assert.That(adapter.TryShape(normal, normalQuads, out _), Is.True);
            Assert.That(adapter.TryShape(heavy, boldQuads, out _), Is.True);
            Assert.That(UnionWidth(boldQuads), Is.GreaterThan(UnionWidth(normalQuads) + 1.0));
            Assert.That(boldQuads[0].WeightBias, Is.EqualTo(0f));
        }

        [Test]
        public void Small_regular_text_uses_hinted_coverage_atlas_when_available() {
            if (!AtgGlyphAtlasAdapter.IsAvailable) {
                Assert.Ignore("ATG bindings are not available in this Unity version.");
            }

            var fontAsset = FontAsset.CreateFontAsset(
                "Arial",
                "Regular",
                pointSize: 90,
                padding: 9,
                renderMode: UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA_HINTED);
            if (fontAsset == null) {
                Assert.Ignore("Arial Regular is not available on this platform.");
            }
            fontAsset.hideFlags = HideFlags.HideAndDontSave;
            cleanup.Add(fontAsset);

            var adapter = new AtgGlyphAtlasAdapter {
                FontAsset = fontAsset
            };
            adapters.Add(adapter);
            Assert.That(adapter.EnableSmallTextCoverage, Is.True);
            var quads = new List<SdfGlyphQuad>();
            var cmd = new DrawTextCommand(
                new PaintRect(0.35, 0.25, 220, 18),
                "Speak with Captain Ren",
                new FontHandle("sans-serif", 12, 400, FontStyle.Normal),
                LinearColor.White,
                default);

            Assert.That(adapter.TryShape(cmd, quads, out int atlasId), Is.True);
            if (!Weva.Text.Sdf.AtlasRegistry.IsCoverageAtlasId(atlasId)) {
                Assert.Ignore("The platform did not create a hinted coverage atlas for Arial Regular.");
            }
            Assert.That(quads.Count, Is.GreaterThan(0));
            Assert.That(Weva.Text.Sdf.AtlasRegistry.IsColorAtlasId(atlasId), Is.False);
            Assert.That(quads[0].WeightBias, Is.GreaterThan(0f));
            Assert.That(quads[0].WeightBias, Is.LessThan(0.3f));
        }

        [Test]
        public void Coverage_atlas_sets_direct_alpha_slot_bit_for_shader() {
            Weva.Text.Sdf.AtlasRegistry.Clear();
            try {
                var face = new Weva.Text.TextCore.FaceInfo(
                    "coverage-test",
                    "coverage-test/regular",
                    400,
                    Weva.Text.TextCore.FaceInfo.StyleNormal);
                var atlas = new Weva.Text.TextCore.GlyphAtlas();
                Weva.Text.Sdf.AtlasRegistry.RegisterAtlas(face, atlas);
                Weva.Text.Sdf.AtlasRegistry.MarkCoverageAtlas(atlas);
                int atlasId = Weva.Text.Sdf.AtlasRegistry.GetAtlasId(atlas);

                var batcher = new UIBatcher();
                batcher.SubmitGlyphQuads(new[] {
                    new SdfGlyphQuad(
                        new PaintRect(0, 0, 10, 10),
                        LinearColor.White,
                        new Vector2(0, 0),
                        new Vector2(1, 1),
                        atlasId)
                }, atlasId);
                batcher.Finish();

                Assert.That(batcher.Batches.Count, Is.EqualTo(1));
                Assert.That((int)batcher.Batches[0].Instances[0].BorderColorTop.y, Is.EqualTo(16));
            } finally {
                Weva.Text.Sdf.AtlasRegistry.Clear();
            }
        }

        [Test]
        public void Small_text_pixel_snaps_run_origin_and_baseline() {
            if (!AtgGlyphAtlasAdapter.IsAvailable) {
                Assert.Ignore("ATG bindings are not available in this Unity version.");
            }

            var fontAsset = FontAsset.CreateFontAsset(
                "Arial",
                "Regular",
                pointSize: 90,
                padding: 9,
                renderMode: UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA_HINTED);
            if (fontAsset == null) {
                Assert.Ignore("Arial Regular is not available on this platform.");
            }
            fontAsset.hideFlags = HideFlags.HideAndDontSave;
            cleanup.Add(fontAsset);

            var adapter = new AtgGlyphAtlasAdapter {
                FontAsset = fontAsset,
                EnableSmallTextCoverage = false
            };
            adapters.Add(adapter);

            var integerQuads = new List<SdfGlyphQuad>();
            var fractionalQuads = new List<SdfGlyphQuad>();
            var integer = new DrawTextCommand(
                new PaintRect(10, 5, 220, 18),
                "pixel snap",
                new FontHandle("sans-serif", 12, 400, FontStyle.Normal),
                LinearColor.White,
                default);
            var fractional = new DrawTextCommand(
                new PaintRect(10.35, 5.35, 220, 18),
                "pixel snap",
                new FontHandle("sans-serif", 12, 400, FontStyle.Normal),
                LinearColor.White,
                default);

            Assert.That(adapter.TryShape(integer, integerQuads, out _), Is.True);
            Assert.That(adapter.TryShape(fractional, fractionalQuads, out _), Is.True);
            Assert.That(integerQuads.Count, Is.GreaterThan(0));
            Assert.That(fractionalQuads.Count, Is.GreaterThan(0));
            Assert.That(fractionalQuads[0].Bounds.X, Is.EqualTo(integerQuads[0].Bounds.X).Within(0.01));
            Assert.That(fractionalQuads[0].Bounds.Y, Is.EqualTo(integerQuads[0].Bounds.Y).Within(0.01));
        }

        [Test]
        public void Small_text_coverage_uses_glyph_metrics_instead_of_padded_generator_quad() {
            if (!AtgGlyphAtlasAdapter.IsAvailable) {
                Assert.Ignore("ATG bindings are not available in this Unity version.");
            }

            var fontAsset = FontAsset.CreateFontAsset(
                "Arial",
                "Regular",
                pointSize: 90,
                padding: 9,
                renderMode: UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA_HINTED);
            if (fontAsset == null) {
                Assert.Ignore("Arial Regular is not available on this platform.");
            }
            fontAsset.hideFlags = HideFlags.HideAndDontSave;
            cleanup.Add(fontAsset);

            var adapter = new AtgGlyphAtlasAdapter {
                FontAsset = fontAsset
            };
            adapters.Add(adapter);

            var quads = new List<SdfGlyphQuad>();
            var cmd = new DrawTextCommand(
                new PaintRect(0, 0, 80, 18),
                "Tr",
                new FontHandle("sans-serif", 12, 400, FontStyle.Normal),
                LinearColor.White,
                default);

            Assert.That(adapter.TryShape(cmd, quads, out int atlasId), Is.True);
            if (!Weva.Text.Sdf.AtlasRegistry.IsCoverageAtlasId(atlasId)) {
                Assert.Ignore("The platform did not create a hinted coverage atlas for Arial Regular.");
            }
            Assert.That(quads.Count, Is.EqualTo(2));
            Assert.That(quads[0].Bounds.Width, Is.LessThan(10.0),
                "Coverage glyphs must use visible glyph metrics, not TextGenerator's padded quad.");
            Assert.That(quads[1].Bounds.X, Is.GreaterThanOrEqualTo(quads[0].Bounds.X + quads[0].Bounds.Width - 2.0),
                "The second glyph should not be buried under the first glyph's padded coverage quad.");
        }

        [Test]
        public void Small_bold_letters_and_digits_share_baseline_in_one_run() {
            if (!AtgGlyphAtlasAdapter.IsAvailable) {
                Assert.Ignore("ATG bindings are not available in this Unity version.");
            }

            var fontAsset = FontAsset.CreateFontAsset(
                "Arial",
                "Regular",
                pointSize: 90,
                padding: 9,
                renderMode: UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA_HINTED);
            if (fontAsset == null) {
                Assert.Ignore("Arial Regular is not available on this platform.");
            }
            fontAsset.hideFlags = HideFlags.HideAndDontSave;
            cleanup.Add(fontAsset);

            var adapter = new AtgGlyphAtlasAdapter {
                FontAsset = fontAsset
            };
            adapters.Add(adapter);

            var quads = new List<SdfGlyphQuad>();
            var cmd = new DrawTextCommand(
                new PaintRect(0, 0, 120, 20),
                "COINS 0",
                new FontHandle("sans-serif", 13, 900, FontStyle.Normal),
                LinearColor.White,
                default);

            Assert.That(adapter.TryShape(cmd, quads, out int atlasId), Is.True);
            if (!Weva.Text.Sdf.AtlasRegistry.IsCoverageAtlasId(atlasId)) {
                Assert.Ignore("The platform did not create a hinted coverage atlas for Arial Bold.");
            }
            Assert.That(quads.Count, Is.EqualTo(6));

            double zeroBottom = quads[5].Bounds.Y + quads[5].Bounds.Height;
            for (int i = 0; i < 5; i++) {
                double letterBottom = quads[i].Bounds.Y + quads[i].Bounds.Height;
                Assert.That(zeroBottom, Is.EqualTo(letterBottom).Within(1.0),
                    "Digit glyphs in one text run must sit on the same baseline as uppercase letters.");
            }
        }

        [Test]
        public void Small_bold_digit_split_from_letters_stays_on_text_face() {
            if (!AtgGlyphAtlasAdapter.IsAvailable) {
                Assert.Ignore("ATG bindings are not available in this Unity version.");
            }

            var fontAsset = FontAsset.CreateFontAsset(
                "Arial",
                "Regular",
                pointSize: 90,
                padding: 9,
                renderMode: UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA_HINTED);
            if (fontAsset == null) {
                Assert.Ignore("Arial Regular is not available on this platform.");
            }
            fontAsset.hideFlags = HideFlags.HideAndDontSave;
            cleanup.Add(fontAsset);

            var adapter = new AtgGlyphAtlasAdapter {
                FontAsset = fontAsset
            };
            adapters.Add(adapter);

            var letters = new List<SdfGlyphQuad>();
            var digit = new List<SdfGlyphQuad>();
            var lettersCmd = new DrawTextCommand(
                new PaintRect(0, 2.25, 120, 18),
                "COINS",
                new FontHandle("sans-serif", 13, 900, FontStyle.Normal),
                LinearColor.White,
                default);
            var digitCmd = new DrawTextCommand(
                new PaintRect(43, 2.25, 20, 18),
                "0",
                new FontHandle("sans-serif", 13, 900, FontStyle.Normal),
                LinearColor.White,
                default);

            Assert.That(adapter.TryShape(lettersCmd, letters, out int lettersAtlas), Is.True);
            if (!Weva.Text.Sdf.AtlasRegistry.IsCoverageAtlasId(lettersAtlas)) {
                Assert.Ignore("The platform did not create a hinted coverage atlas for Arial Bold.");
            }
            Assert.That(adapter.TryShape(digitCmd, digit, out int digitAtlas), Is.True);
            Assert.That(Weva.Text.Sdf.AtlasRegistry.IsCoverageAtlasId(digitAtlas), Is.True,
                "Standalone ASCII digits are text, not symbol/keycap emoji roots.");

            Assert.That(letters.Count, Is.GreaterThan(0));
            Assert.That(digit.Count, Is.EqualTo(1));
            double letterBottom = letters[0].Bounds.Y + letters[0].Bounds.Height;
            double digitBottom = digit[0].Bounds.Y + digit[0].Bounds.Height;
            Assert.That(digitBottom, Is.EqualTo(letterBottom).Within(1.0),
                "Binding-split digit runs must keep the same baseline as the adjacent text run.");
        }

        [Test]
        public void Small_bold_letters_and_digits_share_baseline_without_coverage_face() {
            if (!AtgGlyphAtlasAdapter.IsAvailable) {
                Assert.Ignore("ATG bindings are not available in this Unity version.");
            }

            var fontAsset = FontAsset.CreateFontAsset(
                "Arial",
                "Regular",
                pointSize: 90,
                padding: 9,
                renderMode: UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA_HINTED);
            if (fontAsset == null) {
                Assert.Ignore("Arial Regular is not available on this platform.");
            }
            fontAsset.hideFlags = HideFlags.HideAndDontSave;
            cleanup.Add(fontAsset);

            var adapter = new AtgGlyphAtlasAdapter {
                FontAsset = fontAsset,
                EnableSmallTextCoverage = false
            };
            adapters.Add(adapter);

            var quads = new List<SdfGlyphQuad>();
            var cmd = new DrawTextCommand(
                new PaintRect(0, 0, 120, 20),
                "COINS 0",
                new FontHandle("sans-serif", 13, 900, FontStyle.Normal),
                LinearColor.White,
                default);

            Assert.That(adapter.TryShape(cmd, quads, out _), Is.True);
            Assert.That(quads.Count, Is.EqualTo(6));

            double zeroBottom = quads[5].Bounds.Y + quads[5].Bounds.Height;
            for (int i = 0; i < 5; i++) {
                double letterBottom = quads[i].Bounds.Y + quads[i].Bounds.Height;
                Assert.That(zeroBottom, Is.EqualTo(letterBottom).Within(1.0),
                    "Digit glyphs in one text run must sit on the same baseline as uppercase letters.");
            }
        }

        [Test]
        public void Sdf_atlas_padding_does_not_expand_normal_text_geometry() {
            if (!AtgGlyphAtlasAdapter.IsAvailable) {
                Assert.Ignore("ATG bindings are not available in this Unity version.");
            }

            var tightAsset = FontAsset.CreateFontAsset(
                "Arial",
                "Regular",
                pointSize: 90,
                padding: 1,
                renderMode: UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA_HINTED);
            var paddedAsset = FontAsset.CreateFontAsset(
                "Arial",
                "Regular",
                pointSize: 90,
                padding: 32,
                renderMode: UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA_HINTED);
            if (tightAsset == null || paddedAsset == null) {
                Assert.Ignore("Arial Regular is not available on this platform.");
            }
            tightAsset.hideFlags = HideFlags.HideAndDontSave;
            paddedAsset.hideFlags = HideFlags.HideAndDontSave;
            cleanup.Add(tightAsset);
            cleanup.Add(paddedAsset);

            var tightAdapter = new AtgGlyphAtlasAdapter {
                FontAsset = tightAsset,
                EnableSmallTextCoverage = false
            };
            var paddedAdapter = new AtgGlyphAtlasAdapter {
                FontAsset = paddedAsset,
                EnableSmallTextCoverage = false
            };
            adapters.Add(tightAdapter);
            adapters.Add(paddedAdapter);

            var tightQuads = new List<SdfGlyphQuad>();
            var paddedQuads = new List<SdfGlyphQuad>();
            var cmd = new DrawTextCommand(
                new PaintRect(0, 0, 40, 18),
                "F",
                new FontHandle("sans-serif", 12, 400, FontStyle.Normal),
                LinearColor.White,
                default);

            Assert.That(tightAdapter.TryShape(cmd, tightQuads, out _), Is.True);
            Assert.That(paddedAdapter.TryShape(cmd, paddedQuads, out _), Is.True);
            Assert.That(tightQuads.Count, Is.EqualTo(1));
            Assert.That(paddedQuads.Count, Is.EqualTo(1));
            Assert.That(paddedQuads[0].Bounds.Width, Is.LessThan(tightQuads[0].Bounds.Width + 2.0));
            Assert.That(paddedQuads[0].Bounds.Height, Is.LessThan(tightQuads[0].Bounds.Height + 2.0));
        }

        [Test]
        public void Symbol_sdf_padding_does_not_expand_css_text_geometry() {
            if (!AtgGlyphAtlasAdapter.IsAvailable) {
                Assert.Ignore("ATG bindings are not available in this Unity version.");
            }

            var tightAsset = FontAsset.CreateFontAsset(
                "Segoe UI Symbol",
                "Regular",
                pointSize: 90,
                padding: 1,
                renderMode: UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA);
            var paddedAsset = FontAsset.CreateFontAsset(
                "Segoe UI Symbol",
                "Regular",
                pointSize: 90,
                padding: 32,
                renderMode: UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA);
            if (tightAsset == null || paddedAsset == null) {
                Assert.Ignore("Segoe UI Symbol is not available on this platform.");
            }
            tightAsset.hideFlags = HideFlags.HideAndDontSave;
            paddedAsset.hideFlags = HideFlags.HideAndDontSave;
            cleanup.Add(tightAsset);
            cleanup.Add(paddedAsset);

            var tightAdapter = new AtgGlyphAtlasAdapter {
                FontAsset = tightAsset,
                EnableSmallTextCoverage = false
            };
            var paddedAdapter = new AtgGlyphAtlasAdapter {
                FontAsset = paddedAsset,
                EnableSmallTextCoverage = false
            };
            adapters.Add(tightAdapter);
            adapters.Add(paddedAdapter);

            var tightQuads = new List<SdfGlyphQuad>();
            var paddedQuads = new List<SdfGlyphQuad>();
            var cmd = new DrawTextCommand(
                new PaintRect(0, 0, 120, 120),
                "\u2605",
                new FontHandle("sans-serif", 64, 800, FontStyle.Normal),
                LinearColor.White,
                default);

            Assert.That(tightAdapter.TryShape(cmd, tightQuads, out _), Is.True);
            Assert.That(paddedAdapter.TryShape(cmd, paddedQuads, out _), Is.True);
            Assert.That(tightQuads.Count, Is.EqualTo(1));
            Assert.That(paddedQuads.Count, Is.EqualTo(1));
            Assert.That(paddedQuads[0].Bounds.Width, Is.LessThan(tightQuads[0].Bounds.Width + 4.0),
                "Atlas padding is sampling data, not CSS text geometry.");
            Assert.That(paddedQuads[0].Bounds.Height, Is.LessThan(tightQuads[0].Bounds.Height + 4.0),
                "Large text-default symbols should stay web-sized even when their SDF atlas has generous padding.");
        }

        [Test]
        public void Text_shadow_blur_does_not_scale_atg_symbol_geometry() {
            if (!AtgGlyphAtlasAdapter.IsAvailable) {
                Assert.Ignore("ATG bindings are not available in this Unity version.");
            }

            var fontAsset = FontAsset.CreateFontAsset(
                "Segoe UI Symbol",
                "Regular",
                pointSize: 90,
                padding: 9,
                renderMode: UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA);
            if (fontAsset == null || !fontAsset.TryAddCharacters("\u2605", out _, includeFontFeatures: false)) {
                Assert.Ignore("Segoe UI Symbol is not available on this platform.");
            }
            fontAsset.hideFlags = HideFlags.HideAndDontSave;
            cleanup.Add(fontAsset);

            var adapter = new AtgGlyphAtlasAdapter {
                FontAsset = fontAsset,
                EnableSmallTextCoverage = false
            };
            adapters.Add(adapter);

            var crisp = new List<SdfGlyphQuad>();
            var blurred = new List<SdfGlyphQuad>();
            var font = new FontHandle("sans-serif", 78, 800, FontStyle.Normal);
            var bounds = new PaintRect(0, 0, 120, 120);
            var crispCommand = new DrawTextCommand(bounds, "\u2605", font, LinearColor.White, default);
            var blurCommand = new DrawTextCommand();
            blurCommand.Set(bounds, "\u2605", font, LinearColor.White, default, 0, 20);

            Assert.That(adapter.TryShape(crispCommand, crisp, out _), Is.True);
            Assert.That(adapter.TryShape(blurCommand, blurred, out _), Is.True);
            Assert.That(crisp.Count, Is.EqualTo(1));
            Assert.That(blurred.Count, Is.EqualTo(1));
            Assert.That(blurred[0].Bounds.X, Is.EqualTo(crisp[0].Bounds.X).Within(0.01));
            Assert.That(blurred[0].Bounds.Y, Is.EqualTo(crisp[0].Bounds.Y).Within(0.01));
            Assert.That(blurred[0].Bounds.Width, Is.EqualTo(crisp[0].Bounds.Width).Within(0.01));
            Assert.That(blurred[0].Bounds.Height, Is.EqualTo(crisp[0].Bounds.Height).Within(0.01));
            Assert.That(blurred[0].BlurRadius, Is.EqualTo(20f).Within(0.01f));
        }

        [Test]
        public void Text_default_symbol_runs_use_symbol_font_instead_of_emoji_fallback() {
            if (!AtgGlyphAtlasAdapter.IsAvailable) {
                Assert.Ignore("ATG bindings are not available in this Unity version.");
            }

            var primary = FontAsset.CreateFontAsset(
                "Segoe UI",
                "Regular",
                pointSize: 90,
                padding: 9,
                renderMode: UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA_HINTED);
            var symbolProbe = FontAsset.CreateFontAsset(
                "Segoe UI Symbol",
                "Regular",
                pointSize: 64,
                padding: 8,
                renderMode: UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA_HINTED);
            if (primary == null || symbolProbe == null || !symbolProbe.TryAddCharacters("\u2605", out _, includeFontFeatures: false)) {
                Assert.Ignore("Segoe UI / Segoe UI Symbol is not available on this platform.");
            }
            primary.hideFlags = HideFlags.HideAndDontSave;
            symbolProbe.hideFlags = HideFlags.HideAndDontSave;
            cleanup.Add(primary);
            cleanup.Add(symbolProbe);

            var adapter = new AtgGlyphAtlasAdapter {
                FontAsset = primary,
                EnableSmallTextCoverage = false
            };
            adapters.Add(adapter);

            var quads = new List<SdfGlyphQuad>();
            var cmd = new DrawTextCommand(
                new PaintRect(0, 0, 120, 120),
                "\u2605",
                new FontHandle("sans-serif", 64, 800, FontStyle.Normal),
                LinearColor.White,
                default);

            Assert.That(adapter.TryShape(cmd, quads, out _), Is.True);
            Assert.That(quads.Count, Is.EqualTo(1));
            Assert.That(Weva.Text.Sdf.AtlasRegistry.IsColorAtlasId(quads[0].AtlasId), Is.False);
            Assert.That(quads[0].Bounds.Width, Is.GreaterThan(45.0));
            Assert.That(quads[0].Bounds.Height, Is.GreaterThan(45.0));
        }

        static double UnionWidth(List<SdfGlyphQuad> quads) {
            double min = double.PositiveInfinity;
            double max = double.NegativeInfinity;
            for (int i = 0; i < quads.Count; i++) {
                min = System.Math.Min(min, quads[i].Bounds.X);
                max = System.Math.Max(max, quads[i].Bounds.Right);
            }
            return double.IsInfinity(min) ? 0 : max - min;
        }
    }
}
#endif
