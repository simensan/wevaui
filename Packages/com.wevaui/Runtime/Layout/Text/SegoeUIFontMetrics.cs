using Weva.Paint;

namespace Weva.Layout.Text {
    // SegoeUIFontMetrics — deterministic headless metrics for Windows'
    // "Segoe UI", the face Chrome resolves for `font-family: "Segoe UI"`
    // on the capture machine. Companion to InterFontMetrics: same design,
    // different tables — used by the LayoutDiff harness for fixtures that
    // author Segoe UI explicitly (match3), where the Chrome reference
    // measured the REAL Segoe UI and Inter's advances drift every
    // text-bearing box.
    //
    // Unlike Inter (a variable font where the harness only loads two
    // instances), Segoe UI ships as six DISCRETE faces and Chrome's
    // CSS-Fonts §5.2 matching snaps each font-weight to one of them:
    //
    //   <=349 Light(300)  <=399 Semilight(350)  <=500 Regular(400)
    //   <=600 Semibold(600)  <=700 Bold(700)  >700 Black(900)
    //
    // (e.g. match3's `font-weight: 800` resolves to Segoe UI Black —
    // measured with the Bold table the combo banner is 5 px narrow.)
    //
    // The constants are extracted from the actual Windows font tables
    // (segoeuil/segoeuisl/segoeui/seguisb/segoeuib/seguibl.ttf, fontTools
    // dump). ALL six faces share identical vertical metrics:
    //
    //   head.unitsPerEm   = 2048
    //   hhea.ascender     = 2210   (= OS/2.usWinAscent)
    //   hhea.descender    = -514   (= usWinDescent)
    //   hhea.lineGap      = 0
    //   OS/2.fsSelection USE_TYPO_METRICS is NOT set, so Chrome-on-Windows
    //   uses the win/hhea metrics:
    //   -> `line-height: normal` = (2210 + 514 + 0) / 2048 = 1.330078125 —
    //     the well-known Chrome Segoe UI ~1.33 line height.
    //
    // Per-glyph advances cover U+0020..U+00FF (cmap+hmtx of the same
    // files; entries are 0 where the face maps no glyph — the C1 control
    // block — and fall through to the generic fallback). Latin-1 coverage
    // matters beyond ASCII: match3's combo banner contains U+00D7 (×).
    // Non-Latin-1 fallbacks mirror InterFontMetrics' deterministic model
    // (emoji allowlist, CJK 1.0 em), plus the Segoe UI Symbol geometric
    // glyphs Chrome resolves through that system fallback face.
    public sealed class SegoeUIFontMetrics : IStyledFontMetrics {
        public const double UnitsPerEm = 2048.0;
        public const double AscentUnits = 2210.0;
        public const double DescentUnits = 514.0;
        public const double LineGapUnits = 0.0;
        public const double LineHeightEm = (AscentUnits + DescentUnits + LineGapUnits) / UnitsPerEm;
        public const double AscentEm = AscentUnits / UnitsPerEm;
        public const double DescentEm = DescentUnits / UnitsPerEm;
        public const double FallbackAdvanceEm = 0.5133; // Regular-face ASCII average

        // hmtx advances (font units, upem 2048) for U+0020..U+00FF.
        static readonly ushort[] LightAdvances = {
            561, 582, 686, 1237, 1055, 1606, 1427, 438, 578, 578, 831, 1331, 455, 815, 455, 750,
            1055, 730, 1055, 1055, 1087, 1055, 1055, 1022, 1055, 1055, 455, 455, 1331, 1331, 1331, 881,
            1933, 1288, 1114, 1272, 1380, 1024, 952, 1368, 1386, 467, 664, 1079, 938, 1706, 1452, 1559,
            1114, 1544, 1137, 1018, 1047, 1327, 1237, 1833, 1153, 1106, 1167, 578, 750, 578, 1331, 850,
            508, 1012, 1147, 909, 1147, 1034, 565, 1147, 1096, 420, 420, 909, 420, 1683, 1096, 1149,
            1147, 1147, 676, 795, 608, 1096, 928, 1386, 862, 928, 950, 578, 463, 578, 1331, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            561, 582, 1055, 1055, 1139, 1055, 463, 918, 817, 1804, 745, 952, 1331, 815, 1804, 850,
            791, 1331, 674, 674, 508, 1116, 971, 455, 412, 674, 811, 952, 1513, 1622, 1513, 881,
            1288, 1288, 1288, 1288, 1288, 1288, 1706, 1272, 1024, 1024, 1024, 1024, 467, 467, 467, 467,
            1380, 1452, 1559, 1559, 1559, 1559, 1559, 1331, 1559, 1327, 1327, 1327, 1327, 1106, 1114, 1061,
            1012, 1012, 1012, 1012, 1012, 1012, 1718, 909, 1034, 1034, 1034, 1034, 420, 420, 420, 420,
            1090, 1096, 1149, 1149, 1149, 1149, 1149, 1331, 1149, 1096, 1096, 1096, 1096, 928, 1147, 928,
        };
        static readonly ushort[] SemilightAdvances = {
            561, 582, 744, 1223, 1079, 1641, 1436, 454, 598, 598, 842, 1366, 449, 817, 449, 774,
            1079, 768, 1079, 1079, 1124, 1079, 1079, 1034, 1079, 1079, 449, 449, 1366, 1366, 1366, 899,
            1944, 1303, 1144, 1270, 1408, 1030, 976, 1386, 1420, 506, 697, 1133, 951, 1772, 1492, 1551,
            1130, 1551, 1181, 1053, 1060, 1367, 1254, 1873, 1180, 1119, 1167, 598, 763, 598, 1366, 850,
            528, 1026, 1175, 927, 1176, 1052, 603, 1176, 1127, 458, 458, 963, 458, 1723, 1127, 1174,
            1175, 1176, 694, 832, 651, 1128, 954, 1433, 902, 959, 938, 598, 476, 598, 1366, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            561, 582, 1079, 1079, 1138, 1079, 476, 918, 832, 1813, 774, 994, 1366, 817, 1813, 850,
            781, 1366, 712, 712, 543, 1149, 954, 449, 416, 712, 846, 994, 1513, 1647, 1513, 899,
            1303, 1303, 1303, 1303, 1303, 1304, 1734, 1270, 1030, 1030, 1030, 1030, 506, 506, 506, 506,
            1408, 1492, 1551, 1551, 1551, 1551, 1551, 1366, 1551, 1367, 1367, 1367, 1367, 1119, 1130, 1087,
            1026, 1026, 1026, 1026, 1026, 1026, 1711, 927, 1052, 1052, 1052, 1052, 458, 458, 458, 458,
            1117, 1127, 1174, 1174, 1174, 1174, 1174, 1366, 1174, 1128, 1128, 1128, 1128, 959, 1175, 959,
        };
        static readonly ushort[] RegularAdvances = {
            561, 582, 803, 1210, 1104, 1676, 1639, 471, 618, 618, 854, 1401, 444, 819, 444, 798,
            1104, 1104, 1104, 1104, 1104, 1104, 1104, 1104, 1104, 1104, 444, 444, 1401, 1401, 1401, 918,
            1956, 1321, 1174, 1268, 1436, 1036, 1000, 1405, 1454, 545, 731, 1188, 964, 1839, 1532, 1544,
            1147, 1544, 1225, 1088, 1073, 1407, 1272, 1913, 1208, 1132, 1168, 618, 776, 618, 1401, 850,
            549, 1042, 1204, 946, 1206, 1071, 641, 1206, 1159, 496, 496, 1018, 496, 1764, 1159, 1200,
            1204, 1206, 712, 869, 694, 1159, 981, 1480, 940, 991, 926, 618, 490, 618, 1401, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            561, 582, 1104, 1104, 1138, 1104, 490, 918, 848, 1823, 803, 1036, 1401, 819, 1823, 850,
            772, 1401, 750, 750, 578, 1182, 938, 444, 420, 719, 882, 1036, 1856, 1906, 1950, 918,
            1321, 1321, 1321, 1321, 1321, 1321, 1762, 1268, 1036, 1036, 1036, 1036, 545, 545, 545, 545,
            1436, 1532, 1544, 1544, 1544, 1544, 1544, 1401, 1544, 1407, 1407, 1407, 1407, 1132, 1147, 1114,
            1042, 1042, 1042, 1042, 1042, 1042, 1704, 946, 1071, 1071, 1071, 1071, 496, 496, 496, 496,
            1145, 1159, 1200, 1200, 1200, 1200, 1200, 1401, 1200, 1159, 1159, 1159, 1159, 991, 1204, 991,
        };
        static readonly ushort[] SemiboldAdvances = {
            563, 622, 896, 1211, 1137, 1721, 1465, 529, 680, 680, 889, 1422, 494, 823, 494, 847,
            1137, 824, 1137, 1137, 1180, 1137, 1143, 1098, 1137, 1143, 494, 494, 1422, 1422, 1422, 909,
            1955, 1375, 1237, 1272, 1469, 1060, 1029, 1428, 1506, 597, 812, 1251, 1001, 1893, 1571, 1548,
            1197, 1548, 1275, 1115, 1130, 1440, 1314, 1978, 1268, 1182, 1202, 680, 829, 680, 1422, 850,
            591, 1069, 1234, 963, 1234, 1088, 706, 1234, 1192, 535, 535, 1075, 535, 1814, 1195, 1223,
            1234, 1234, 758, 883, 740, 1195, 1039, 1549, 1026, 1041, 951, 680, 570, 680, 1422, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            563, 622, 1137, 1137, 1138, 1137, 570, 952, 892, 1808, 820, 1105, 1422, 823, 1808, 850,
            775, 1422, 785, 785, 597, 1215, 985, 494, 429, 759, 905, 1105, 1898, 1938, 1975, 909,
            1375, 1375, 1375, 1375, 1375, 1375, 1831, 1272, 1060, 1060, 1060, 1060, 597, 597, 597, 597,
            1469, 1571, 1548, 1548, 1548, 1548, 1548, 1422, 1548, 1440, 1440, 1440, 1440, 1182, 1197, 1191,
            1069, 1069, 1069, 1069, 1069, 1069, 1700, 963, 1088, 1088, 1088, 1088, 535, 535, 535, 535,
            1176, 1195, 1223, 1223, 1223, 1223, 1223, 1422, 1223, 1195, 1195, 1195, 1195, 1041, 1234, 1041,
        };
        static readonly ushort[] BoldAdvances = {
            565, 670, 1010, 1213, 1178, 1776, 1740, 600, 756, 756, 932, 1448, 555, 828, 555, 908,
            1178, 1178, 1178, 1178, 1178, 1178, 1178, 1178, 1178, 1178, 555, 555, 1448, 1448, 1448, 897,
            1954, 1440, 1313, 1278, 1510, 1090, 1065, 1456, 1569, 649, 912, 1329, 1047, 1960, 1618, 1553,
            1258, 1553, 1337, 1148, 1200, 1481, 1366, 2058, 1342, 1243, 1243, 756, 893, 756, 1448, 850,
            643, 1102, 1270, 983, 1268, 1108, 785, 1268, 1233, 582, 582, 1145, 582, 1876, 1239, 1252,
            1270, 1268, 815, 901, 797, 1239, 1110, 1633, 1131, 1102, 981, 756, 668, 756, 1448, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            565, 670, 1178, 1178, 1139, 1178, 668, 994, 946, 1790, 840, 1190, 1448, 828, 1790, 850,
            778, 1448, 828, 828, 621, 1255, 1043, 555, 441, 807, 934, 1190, 1950, 1977, 2005, 897,
            1440, 1440, 1440, 1440, 1440, 1440, 1915, 1278, 1090, 1090, 1090, 1090, 649, 649, 649, 649,
            1510, 1618, 1552, 1552, 1552, 1552, 1552, 1448, 1553, 1481, 1481, 1481, 1481, 1243, 1258, 1286,
            1102, 1102, 1102, 1102, 1102, 1102, 1696, 983, 1108, 1108, 1108, 1108, 582, 582, 582, 582,
            1215, 1239, 1251, 1251, 1251, 1251, 1251, 1448, 1252, 1239, 1239, 1239, 1239, 1102, 1270, 1102,
        };
        static readonly ushort[] BlackAdvances = {
            569, 750, 1188, 1227, 1227, 1839, 1513, 705, 862, 862, 991, 1464, 655, 834, 655, 987,
            1227, 990, 1227, 1227, 1271, 1227, 1227, 1227, 1227, 1227, 655, 655, 1464, 1464, 1464, 866,
            1944, 1536, 1415, 1290, 1554, 1130, 1108, 1489, 1643, 714, 1047, 1413, 1108, 2021, 1663, 1567,
            1343, 1567, 1397, 1171, 1303, 1516, 1438, 2157, 1438, 1331, 1313, 862, 987, 862, 1464, 850,
            711, 1149, 1315, 1014, 1315, 1147, 883, 1315, 1264, 631, 631, 1214, 631, 1946, 1282, 1298,
            1315, 1315, 893, 989, 854, 1282, 1204, 1731, 1272, 1176, 1042, 862, 815, 862, 1464, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            561, 750, 1227, 1227, 1139, 1227, 815, 1063, 1024, 1755, 852, 1294, 1464, 834, 1755, 741,
            791, 1464, 758, 758, 633, 1294, 1147, 655, 457, 758, 950, 1294, 1950, 1976, 2005, 866,
            1536, 1536, 1536, 1536, 1536, 1536, 2030, 1290, 1130, 1130, 1130, 1130, 714, 714, 772, 772,
            1554, 1663, 1567, 1567, 1567, 1567, 1567, 1464, 1567, 1516, 1516, 1516, 1516, 1331, 1343, 1417,
            1149, 1149, 1149, 1149, 1149, 1149, 1716, 1014, 1147, 1147, 1147, 1147, 631, 631, 631, 631,
            1278, 1282, 1298, 1298, 1298, 1298, 1298, 1464, 1298, 1282, 1282, 1282, 1282, 1176, 1315, 1176,
        };

        public static readonly SegoeUIFontMetrics Instance = new SegoeUIFontMetrics();

        // ---- IFontMetrics (unstyled = Regular face) ----
        public double LineHeight(double fontSize) => fontSize * LineHeightEm;
        public double Ascent(double fontSize) => fontSize * AscentEm;
        public double Descent(double fontSize) => fontSize * DescentEm;
        public double Measure(string text, double fontSize) =>
            MeasureCore(text, 0, text?.Length ?? 0, fontSize, RegularAdvances);
        public double Measure(string text, int start, int length, double fontSize) =>
            MeasureCore(text, start, length, fontSize, RegularAdvances);

        // ---- IStyledFontMetrics ----
        // All six faces share identical vertical metrics (verified from the
        // Windows TTFs); only advances differ per weight.
        public double LineHeight(double fontSize, string family, FontStyle style, int weight) => fontSize * LineHeightEm;
        public double Ascent(double fontSize, string family, FontStyle style, int weight) => fontSize * AscentEm;
        public double Descent(double fontSize, string family, FontStyle style, int weight) => fontSize * DescentEm;
        public double Measure(string text, double fontSize, string family, FontStyle style, int weight) =>
            MeasureCore(text, 0, text?.Length ?? 0, fontSize, TableFor(weight));
        public double Measure(string text, int start, int length, double fontSize, string family, FontStyle style, int weight) =>
            MeasureCore(text, start, length, fontSize, TableFor(weight));

        // CSS Fonts §5.2 weight matching against Segoe UI's discrete face
        // set {300, 350, 400, 600, 700, 900}, collapsed to a ladder. For a
        // desired weight above 500 the algorithm picks the nearest equal-or-
        // heavier face first (701-899 -> Black, 601-699 -> Bold, 501-599 ->
        // Semibold); 400-500 snap to Regular; below 400 the nearest equal-
        // or-lighter face wins (351-399 -> Semilight, <=350 -> Light /
        // Semilight exact).
        internal static ushort[] TableFor(int weight) {
            if (weight > 700) return BlackAdvances;
            if (weight > 600) return BoldAdvances;
            if (weight > 500) return SemiboldAdvances;
            if (weight >= 400) return RegularAdvances;
            if (weight >= 350) return SemilightAdvances;
            return LightAdvances;
        }

        // Geometric symbols Chrome-on-Windows resolves through the system
        // fallback face "Segoe UI Symbol" (C:\Windows\Fonts\seguisym.ttf,
        // upem 2048 — exact hmtx advances below). Segoe UI itself has no
        // glyphs for these, so without this branch they'd hit the generic
        // 0.5133 em average and every star-rating span drifts ~4 px/glyph.
        static double SymbolAdvanceEm(int cp) {
            switch (cp) {
                case 0x2605:               // ★ BLACK STAR
                case 0x2606:               // ☆ WHITE STAR
                    return 1706.0 / 2048;
                case 0x25CB:               // ○ WHITE CIRCLE
                case 0x25CF:               // ● BLACK CIRCLE
                    return 1764.0 / 2048;
                case 0x2660:               // ♠ ♣ ♥ ♦ card suits
                case 0x2663:
                case 0x2665:
                case 0x2666:
                    return 1402.0 / 2048;
                default:
                    return 0;
            }
        }

        // Identical fallback model to InterFontMetrics.MeasureCore (emoji
        // wide/medium allowlist, CJK via LineBreakClasses) so the two
        // calibrated faces only ever differ by their real advance tables.
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
                if (cp >= 0x20 && cp <= 0xFF) {
                    int a = advances[cp - 0x20];
                    if (a != 0) { units += a; continue; }
                    // a == 0: unmapped C1 control — fall through to fallback.
                }
                double symbolEm = SymbolAdvanceEm(cp);
                if (symbolEm > 0) { emExtras += symbolEm; continue; }
                bool isWideEmoji = (cp >= 0x1F000 && cp <= 0x1FAFF)
                            || cp == 0x26A1 || cp == 0x26D4 || cp == 0x2600
                            || cp == 0x2614 || cp == 0x2615 || cp == 0x2618
                            || cp == 0x2620 || cp == 0x2705;
                bool isMediumEmoji = (cp >= 0x2700 && cp <= 0x27BF)
                            || cp == 0x2699 || cp == 0x2298;
                if (isWideEmoji) { emExtras += 1.3; continue; }
                if (isMediumEmoji) { emExtras += 1.0; continue; }
                if (LineBreakClasses.IsCjkFlowChar(cp)) { emExtras += 1.0; continue; }
                emExtras += FallbackAdvanceEm;
            }
            return (units / UnitsPerEm + emExtras) * fontSize;
        }
    }
}
