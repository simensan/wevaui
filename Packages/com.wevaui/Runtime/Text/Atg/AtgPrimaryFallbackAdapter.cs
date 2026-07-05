#if UNITY_2023_1_OR_NEWER
using System.Collections.Generic;
using Weva.Paint;
using Weva.Rendering.URP;

namespace Weva.Text.Atg {
    // Two-tier text shaper: tries ATG (Unity's Advanced Text Generator — hinted
    // bitmap glyphs, "Chrome-quality" small UI text) first; falls back to the
    // existing SDF adapter on any miss. Both backends produce SdfGlyphQuad so
    // the downstream renderer is unaware of the source.
    //
    // ATG is preferred for the same reason UI Toolkit uses it: glyphs are
    // rasterized via FreeType with the font designer's hinting bytecode
    // applied, so stems snap to whole pixels. The SDF fallback handles every
    // case ATG can't yet — text-shadow blur, transforms / animation, missing
    // bindings, font fallback chains we haven't wired ATG-side, etc.
    public sealed class AtgPrimaryFallbackAdapter : IGlyphAtlasWithId, IGlyphAtlasVersioned, IGlyphAtlasPreparer, IGlyphAtlasTextRunSnapshotPolicy, IGlyphAtlasShapeSource {
        public AtgGlyphAtlasAdapter Atg { get; set; }
        public IGlyphAtlasWithId SdfFallback { get; set; }
        public bool UseTextRunSnapshots => true;
        public long Version {
            get {
                long version = Atg is IGlyphAtlasVersioned atgVersioned ? atgVersioned.Version : 0;
                if (SdfFallback is IGlyphAtlasVersioned sdfVersioned) {
                    version = (version * 397) ^ sdfVersioned.Version;
                }
                return version;
            }
        }

        public bool TryShape(DrawTextCommand command, List<SdfGlyphQuad> output) {
            return TryShape(command, output, out _);
        }

        public void BeginPrepareText() {
            if (Atg is IGlyphAtlasPreparer atgPreparer) atgPreparer.BeginPrepareText();
            if (SdfFallback is IGlyphAtlasPreparer sdfPreparer) sdfPreparer.BeginPrepareText();
        }

        public void PrepareText(DrawTextCommand command) {
            if (Atg is IGlyphAtlasPreparer atgPreparer) atgPreparer.PrepareText(command);
            if (SdfFallback is IGlyphAtlasPreparer sdfPreparer) sdfPreparer.PrepareText(command);
        }

        public void EndPrepareText() {
            if (Atg is IGlyphAtlasPreparer atgPreparer) atgPreparer.EndPrepareText();
            if (SdfFallback is IGlyphAtlasPreparer sdfPreparer) sdfPreparer.EndPrepareText();
        }

        // True when the most recent TryShape was satisfied by the SDF
        // fallback even though an ATG primary exists. The fallback face's
        // advances don't match the layout metrics, so the renderer treats
        // such shapes as provisional (no snapshot pinning — see
        // IGlyphAtlasShapeSource).
        public bool LastShapeUsedSecondaryFallback { get; private set; }

        public bool TryShape(DrawTextCommand command, List<SdfGlyphQuad> output, out int atlasId) {
            atlasId = 0;
            LastShapeUsedSecondaryFallback = false;
            if (command == null || output == null) return false;

            // ATG handles text-shadow blur the same way the SDF path does:
            // AtgGlyphAtlasAdapter forwards command.BlurRadius onto each
            // SdfGlyphQuad and the shader widens its SDF AA band. So we
            // always try ATG first — including for shadow phantoms.
            //
            // Previously this branched on `BlurRadius > 0` and routed
            // shadows through SDF unconditionally. That worked for runs
            // whose codepoints existed in the TMP primary chain, but for
            // glyphs only the ATG mono Symbol asset covers (★ U+2605,
            // ↩ U+21A9, etc.) the SDF chain found nothing, fell through to
            // EmitFallback, and painted a solid colored rectangle in the
            // shadow color. The .star.on rule's `text-shadow: 0 0 8px gold`
            // showed up as a yellow rectangle behind every gold star.
            if (Atg != null) {
                int idStart = output.Count;
                if (Atg.TryShape(command, output, out atlasId)) {
                    // Guard: ATG can claim success but emit zero glyphs when
                    // the FontAsset doesn't cover the run. Treat that as
                    // failure and try SDF — better to render via fallback than
                    // silently drop the text.
                    if (output.Count > idStart) return true;
                }
                // Failed mid-call — drop anything partial the adapter appended.
                if (output.Count > idStart) output.RemoveRange(idStart, output.Count - idStart);
                // The documented ATG failure mode is TRANSIENT: an on-demand
                // TryAddCharacters can repack the atlas mid-GenerateText and the
                // call returns zero glyphs. By the time we're here the missing
                // characters HAVE been rasterized (the add itself completed), so
                // an immediate second shape succeeds. Retry once before
                // surrendering the run to the SDF fallback — the fallback face's
                // advances don't match the layout metrics, so a fallback render
                // garbles the line (weva-landing "Everything you'd reach…").
                if (Atg.TryShape(command, output, out atlasId)) {
                    if (output.Count > idStart) return true;
                }
                if (output.Count > idStart) output.RemoveRange(idStart, output.Count - idStart);
            }

            if (SdfFallback == null) return false;
            bool sdfOk = SdfFallback.TryShape(command, output, out atlasId);
            // Only counts as a "secondary" shape when a primary exists to
            // recover to; a pure-SDF configuration is its own primary.
            if (sdfOk && Atg != null) LastShapeUsedSecondaryFallback = true;
            return sdfOk;
        }

        // Lookup of codepoints where Emoji=Yes but Emoji_Presentation=No.
        // Derived from Unicode emoji-data.txt; covers the standard set of
        // dual-presentation codepoints up to Unicode 16. Codepoints NOT in
        // this set that are still emoji default to color (e.g. 🔨 U+1F528,
        // 😀 U+1F600). Non-emoji codepoints (Latin, CJK, etc.) aren't here
        // either — they don't go through the emoji branch.
        // Public so AtgGlyphAtlasAdapter can flag per-glyph for tinting.
        public static bool IsTextDefaultEmoji(uint cp) {
            return TextDefaultEmojiSet.Contains(cp);
        }

        static readonly System.Collections.Generic.HashSet<uint> TextDefaultEmojiSet = new System.Collections.Generic.HashSet<uint> {
            // ASCII keycap roots (#, *, 0-9) are intentionally excluded here.
            // As standalone text they must stay on the active text face; routing
            // a binding-produced "0" run through Segoe UI Symbol put it on a
            // different baseline from the preceding "COINS" run. Full keycap
            // emoji sequences need sequence-aware handling rather than treating
            // bare ASCII characters as symbols.
            // Latin-1 supplement
            0x00A9, 0x00AE,
            // General Punctuation
            0x203C, 0x2049,
            // Letterlike Symbols
            0x2122, 0x2139,
            // Arrows
            0x2194, 0x2195, 0x2196, 0x2197, 0x2198, 0x2199,
            0x21A9, 0x21AA,
            // Misc Technical
            0x2328, 0x23CF,
            0x23ED, 0x23EE, 0x23EF, 0x23F1, 0x23F2, 0x23F8, 0x23F9, 0x23FA,
            // Enclosed Alphanumerics
            0x24C2,
            // Geometric Shapes — including the non-emoji shapes commonly
            // used as UI icons (●▲▼■◆ etc.). These are NOT classified as
            // Emoji by Unicode but browsers render them as monochrome text
            // that takes CSS color, so we route them through SDF to match.
            0x25A0, 0x25A1, 0x25AA, 0x25AB,
            0x25B2, 0x25B3, 0x25B6, 0x25B7,
            0x25BC, 0x25BD, 0x25C0, 0x25C1,
            0x25C6, 0x25C7,
            0x25CB, 0x25CF,
            0x25FB, 0x25FC,
            // Misc Symbols (a large dual-presentation subset)
            0x2600, 0x2601, 0x2602, 0x2603, 0x2604, 0x2605, 0x2606,
            0x260E, 0x2611, 0x2618, 0x261D,
            0x2620, 0x2622, 0x2623, 0x2626,
            0x262A, 0x262E, 0x262F,
            0x2638, 0x2639, 0x263A,
            0x2640, 0x2642,
            0x265F, 0x2660, 0x2663, 0x2665, 0x2666, 0x2668,
            0x267B, 0x267E,
            0x2692, 0x2694, 0x2695, 0x2696, 0x2697,
            0x2699, 0x269B, 0x269C,
            0x26A0, 0x26A7,
            0x26B0, 0x26B1,
            0x26C8, 0x26CF, 0x26D1, 0x26D3, 0x26E9,
            0x26F0, 0x26F1, 0x26F4, 0x26F7, 0x26F8, 0x26F9,
            // Dingbats
            0x2702, 0x2708, 0x2709,
            0x270C, 0x270D, 0x270F,
            0x2712, 0x2714, 0x2716,
            0x271D, 0x2721,
            0x2733, 0x2734,
            0x2744, 0x2747,
            0x2763, 0x2764,
            0x27A1,
            // Supplemental Arrows
            0x2934, 0x2935,
            0x2B05, 0x2B06, 0x2B07,
            // CJK Symbols and Punctuation
            0x3030, 0x303D,
            // Enclosed CJK
            0x3297, 0x3299,
            // U+2605/U+2606 BLACK/WHITE STAR are included even though they
            // (primary Segoe UI + SDF emoji) doesn't have a ★ glyph, so
            // renders them monochrome/tinted by CSS color.
            // fallback ("Emojis - Win") DOES have it — so we let it render
            // color there for now. To match Chrome's monochrome behavior
            // we'd need to bundle a font that has ★ as a regular glyph.
        };
    }
}
#endif
