using Weva.Paint;

namespace Weva.Layout.Text {
    // InterFontMetrics — the deterministic headless metrics for the BUNDLED
    // package font (Inter, shipped as Weva-Default*.ttf). W1 font-determinism
    // increment 2 (see ROADMAP.md):
    //
    //   headless layout == live Unity layout == Chrome-loading-Inter,
    //
    // because all three measure the SAME face. The constants below are NOT
    // calibrated guesses (contrast MonoFontMetrics.ChromeSansSerif, which
    // approximates "whatever Chrome picked on the capture machine"): they are
    // extracted from the actual TTF tables of the shipped files —
    //
    //   head.unitsPerEm = 2048
    //   hhea.ascender   = 1984   (= OS/2.sTypoAscender; usWinAscent agrees)
    //   hhea.descender  = -494   (= sTypoDescender; usWinDescent agrees)
    //   hhea.lineGap    = 0
    //   → `line-height: normal` = (1984 + 494 + 0) / 2048 = 1.2100 exactly,
    //     IDENTICAL across Regular / Bold / Italic.
    //
    // Per-glyph ASCII advances come from cmap+hmtx of the same files, so
    // Measure() is per-codepoint exact for U+0020–U+007E (the overwhelming
    // bulk of UI text), per-face for Regular vs Bold. Italic shares the
    // Regular advance table (Inter's italic advances differ by <1% on ASCII;
    // revisit if an italic-heavy snippet ever diverges).
    //
    // Non-ASCII fallbacks (deterministic by definition, since Inter itself
    // has no CJK/emoji coverage and the live path would go to a fallback
    // face): CJK flow characters measure 1.0 em (fullwidth), emoji follow
    // the same wide/medium model MonoFontMetrics documents, everything else
    // uses the Regular table's average advance (0.5616 em).
    public sealed class InterFontMetrics : IStyledFontMetrics {
        public const double UnitsPerEm = 2048.0;
        public const double AscentUnits = 1984.0;
        public const double DescentUnits = 494.0;
        public const double LineGapUnits = 0.0;
        // (1984 + 494 + 0) / 2048 — kept as an explicit constant so tests can
        // assert against the same number the class derives from.
        public const double LineHeightEm = (AscentUnits + DescentUnits + LineGapUnits) / UnitsPerEm;
        public const double AscentEm = AscentUnits / UnitsPerEm;
        public const double DescentEm = DescentUnits / UnitsPerEm;
        // Average of the Regular ASCII advance table; the deterministic
        // stand-in for codepoints outside every table below.
        public const double FallbackAdvanceEm = 0.5616;

        // hmtx advances (font units, upem 2048) for U+0020..U+007E.
        static readonly ushort[] RegularAdvances = {
            576, 589, 954, 1297, 1314, 2011, 1319, 614, 747, 747, 1026, 1355, 590, 942, 590, 738,
            1292, 833, 1249, 1265, 1323, 1215, 1270, 1159, 1267, 1270, 590, 618, 1355, 1355, 1355, 1047,
            1978, 1413, 1340, 1496, 1478, 1231, 1209, 1528, 1522, 550, 1169, 1376, 1158, 1850, 1543, 1566,
            1308, 1566, 1318, 1314, 1322, 1524, 1413, 2018, 1397, 1390, 1288, 747, 738, 747, 965, 934,
            661, 1150, 1254, 1170, 1254, 1194, 758, 1256, 1211, 496, 496, 1124, 496, 1794, 1210, 1228,
            1254, 1254, 771, 1081, 670, 1211, 1151, 1676, 1118, 1151, 1131, 873, 681, 873, 1355,
        };
        static readonly ushort[] BoldAdvances = {
            485, 692, 1130, 1329, 1341, 2080, 1376, 694, 772, 772, 1145, 1390, 684, 958, 684, 795,
            1381, 883, 1290, 1322, 1385, 1274, 1330, 1191, 1333, 1330, 684, 702, 1390, 1390, 1390, 1146,
            2081, 1529, 1355, 1515, 1479, 1244, 1202, 1537, 1530, 575, 1197, 1473, 1158, 1908, 1561, 1578,
            1327, 1591, 1345, 1341, 1367, 1499, 1529, 2125, 1512, 1497, 1360, 772, 795, 772, 997, 975,
            748, 1189, 1291, 1205, 1291, 1220, 815, 1294, 1275, 555, 555, 1188, 555, 1869, 1275, 1256,
            1291, 1291, 834, 1147, 750, 1275, 1228, 1741, 1188, 1233, 1173, 960, 761, 960, 1390,
        };

        // CSS Fonts L4 §4.2: 600 is the conventional regular/bold face split
        // when only two weights exist (the ATG path's semibold variant kicks
        // in at the same threshold — keep them aligned).
        const int BoldWeightThreshold = 600;

        public static readonly InterFontMetrics Instance = new InterFontMetrics();

        // ---- IFontMetrics (unstyled = Regular face) ----
        public double LineHeight(double fontSize) => fontSize * LineHeightEm;
        public double Ascent(double fontSize) => fontSize * AscentEm;
        public double Descent(double fontSize) => fontSize * DescentEm;
        public double Measure(string text, double fontSize) =>
            MeasureCore(text, 0, text?.Length ?? 0, fontSize, RegularAdvances);
        public double Measure(string text, int start, int length, double fontSize) =>
            MeasureCore(text, start, length, fontSize, RegularAdvances);

        // ---- IStyledFontMetrics ----
        // Inter's three faces share identical vertical metrics (verified from
        // the shipped TTFs), so the styled line-box queries are face-invariant;
        // only advances differ per weight.
        public double LineHeight(double fontSize, string family, FontStyle style, int weight) => fontSize * LineHeightEm;
        public double Ascent(double fontSize, string family, FontStyle style, int weight) => fontSize * AscentEm;
        public double Descent(double fontSize, string family, FontStyle style, int weight) => fontSize * DescentEm;
        public double Measure(string text, double fontSize, string family, FontStyle style, int weight) =>
            MeasureCore(text, 0, text?.Length ?? 0, fontSize, TableFor(weight));
        public double Measure(string text, int start, int length, double fontSize, string family, FontStyle style, int weight) =>
            MeasureCore(text, start, length, fontSize, TableFor(weight));

        static ushort[] TableFor(int weight) => weight >= BoldWeightThreshold ? BoldAdvances : RegularAdvances;

        static double MeasureCore(string text, int start, int length, double fontSize, ushort[] advances) {
            if (string.IsNullOrEmpty(text) || length <= 0) return 0;
            if (start < 0) { length += start; start = 0; }
            if (start >= text.Length) return 0;
            int end = start + length;
            if (end > text.Length) end = text.Length;

            double units = 0;          // accumulated font units (exact path)
            double emExtras = 0;       // accumulated em-denominated fallbacks
            for (int i = start; i < end; i++) {
                int cp;
                if (char.IsHighSurrogate(text[i]) && i + 1 < end && char.IsLowSurrogate(text[i + 1])) {
                    cp = char.ConvertToUtf32(text[i], text[i + 1]);
                    i++;
                } else {
                    cp = text[i];
                }
                if (cp >= 0x20 && cp <= 0x7E) {
                    units += advances[cp - 0x20];
                    continue;
                }
                // Same emoji wide/medium model as MonoFontMetrics (see its
                // Measure doc for the allowlist rationale) — Chrome routes
                // these to the system emoji font at ~1.3 em.
                bool isWideEmoji = (cp >= 0x1F000 && cp <= 0x1FAFF)
                            || cp == 0x26A1 || cp == 0x26D4 || cp == 0x2600
                            || cp == 0x2614 || cp == 0x2615 || cp == 0x2618
                            || cp == 0x2620 || cp == 0x2705;
                bool isMediumEmoji = (cp >= 0x2700 && cp <= 0x27BF)
                            || cp == 0x2699 || cp == 0x2298;
                if (isWideEmoji) { emExtras += 1.3; continue; }
                if (isMediumEmoji) { emExtras += 1.0; continue; }
                // CJK flow characters are fullwidth (1 em) in every CJK
                // fallback face Chrome would pick; LineBreakClasses already
                // owns the classification (W5).
                if (LineBreakClasses.IsCjkFlowChar(cp)) { emExtras += 1.0; continue; }
                emExtras += FallbackAdvanceEm;
            }
            return (units / UnitsPerEm + emExtras) * fontSize;
        }
    }
}
