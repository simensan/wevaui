using System.Collections.Generic;
using Weva.Css.Values;

namespace Weva.Css.Cascade {
    public sealed partial class CascadeEngine {
        static void ApplyLogicalPropertyAliases(Dictionary<string, MatchedDeclaration> winners, ComputedStyle parentStyle) {
            if (winners == null || winners.Count == 0) return;
            var axes = LogicalAxes.From(winners, parentStyle);

            Alias(winners, "inline-size", axes.InlineIsHorizontal ? "width" : "height");
            Alias(winners, "block-size", axes.InlineIsHorizontal ? "height" : "width");
            Alias(winners, "min-inline-size", axes.InlineIsHorizontal ? "min-width" : "min-height");
            Alias(winners, "min-block-size", axes.InlineIsHorizontal ? "min-height" : "min-width");
            Alias(winners, "max-inline-size", axes.InlineIsHorizontal ? "max-width" : "max-height");
            Alias(winners, "max-block-size", axes.InlineIsHorizontal ? "max-height" : "max-width");

            AliasSide(winners, "margin-inline-start", "margin-", axes.InlineStart);
            AliasSide(winners, "margin-inline-end", "margin-", axes.InlineEnd);
            AliasSide(winners, "margin-block-start", "margin-", axes.BlockStart);
            AliasSide(winners, "margin-block-end", "margin-", axes.BlockEnd);

            AliasSide(winners, "padding-inline-start", "padding-", axes.InlineStart);
            AliasSide(winners, "padding-inline-end", "padding-", axes.InlineEnd);
            AliasSide(winners, "padding-block-start", "padding-", axes.BlockStart);
            AliasSide(winners, "padding-block-end", "padding-", axes.BlockEnd);

            AliasSide(winners, "overflow-clip-margin-inline-start", "overflow-clip-margin-", axes.InlineStart);
            AliasSide(winners, "overflow-clip-margin-inline-end", "overflow-clip-margin-", axes.InlineEnd);
            AliasSide(winners, "overflow-clip-margin-block-start", "overflow-clip-margin-", axes.BlockStart);
            AliasSide(winners, "overflow-clip-margin-block-end", "overflow-clip-margin-", axes.BlockEnd);

            AliasSide(winners, "inset-inline-start", "", axes.InlineStart);
            AliasSide(winners, "inset-inline-end", "", axes.InlineEnd);
            AliasSide(winners, "inset-block-start", "", axes.BlockStart);
            AliasSide(winners, "inset-block-end", "", axes.BlockEnd);

            AliasBorder(winners, "border-inline-start", axes.InlineStart);
            AliasBorder(winners, "border-inline-end", axes.InlineEnd);
            AliasBorder(winners, "border-block-start", axes.BlockStart);
            AliasBorder(winners, "border-block-end", axes.BlockEnd);

            AliasBorderComponent(winners, "border-inline-start-width", axes.InlineStart, "width");
            AliasBorderComponent(winners, "border-inline-start-style", axes.InlineStart, "style");
            AliasBorderComponent(winners, "border-inline-start-color", axes.InlineStart, "color");
            AliasBorderComponent(winners, "border-inline-end-width", axes.InlineEnd, "width");
            AliasBorderComponent(winners, "border-inline-end-style", axes.InlineEnd, "style");
            AliasBorderComponent(winners, "border-inline-end-color", axes.InlineEnd, "color");
            AliasBorderComponent(winners, "border-block-start-width", axes.BlockStart, "width");
            AliasBorderComponent(winners, "border-block-start-style", axes.BlockStart, "style");
            AliasBorderComponent(winners, "border-block-start-color", axes.BlockStart, "color");
            AliasBorderComponent(winners, "border-block-end-width", axes.BlockEnd, "width");
            AliasBorderComponent(winners, "border-block-end-style", axes.BlockEnd, "style");
            AliasBorderComponent(winners, "border-block-end-color", axes.BlockEnd, "color");

            AliasCorner(winners, "border-start-start-radius", axes.BlockStart, axes.InlineStart);
            AliasCorner(winners, "border-start-end-radius", axes.BlockStart, axes.InlineEnd);
            AliasCorner(winners, "border-end-start-radius", axes.BlockEnd, axes.InlineStart);
            AliasCorner(winners, "border-end-end-radius", axes.BlockEnd, axes.InlineEnd);
        }

        // Cascade-miss hot path used to allocate ~60 strings per cascade
        // pass from `prefix + physicalSide` concats — quickly the second-
        // largest source of GC pressure during animated paint-only refresh
        // (~12 KB / frame on the HUD). Pre-interned tables eliminate the
        // concat entirely for every standard prefix × side combination.
        static readonly Dictionary<string, string[]> physicalNameByPrefix = new() {
            [""] = new[] { "top", "right", "bottom", "left" },
            ["margin-"] = new[] { "margin-top", "margin-right", "margin-bottom", "margin-left" },
            ["padding-"] = new[] { "padding-top", "padding-right", "padding-bottom", "padding-left" },
            ["overflow-clip-margin-"] = new[] { "overflow-clip-margin-top", "overflow-clip-margin-right", "overflow-clip-margin-bottom", "overflow-clip-margin-left" },
        };

        static readonly string[] borderSideByIndex = { "border-top", "border-right", "border-bottom", "border-left" };

        static readonly string[][] borderComponentByIndex = {
            new[] { "border-top-width",    "border-right-width",    "border-bottom-width",    "border-left-width"    },
            new[] { "border-top-style",    "border-right-style",    "border-bottom-style",    "border-left-style"    },
            new[] { "border-top-color",    "border-right-color",    "border-bottom-color",    "border-left-color"    },
        };

        static int SideIndex(string side) {
            // String reference identity covers the common case (literal
            // "top"/"right"/"bottom"/"left" produced by LogicalAxes.From).
            if (ReferenceEquals(side, "top")) return 0;
            if (ReferenceEquals(side, "right")) return 1;
            if (ReferenceEquals(side, "bottom")) return 2;
            if (ReferenceEquals(side, "left")) return 3;
            // Value-equality fallback for non-interned inputs.
            return side switch {
                "top" => 0,
                "right" => 1,
                "bottom" => 2,
                "left" => 3,
                _ => -1,
            };
        }

        static int ComponentIndex(string component) {
            if (ReferenceEquals(component, "width")) return 0;
            if (ReferenceEquals(component, "style")) return 1;
            if (ReferenceEquals(component, "color")) return 2;
            return component switch {
                "width" => 0,
                "style" => 1,
                "color" => 2,
                _ => -1,
            };
        }

        static void AliasSide(Dictionary<string, MatchedDeclaration> winners, string logical, string prefix, string physicalSide) {
            int sideIdx = SideIndex(physicalSide);
            string physical = (sideIdx >= 0 && physicalNameByPrefix.TryGetValue(prefix, out var table))
                ? table[sideIdx]
                : prefix + physicalSide;
            Alias(winners, logical, physical);
        }

        static void AliasBorder(Dictionary<string, MatchedDeclaration> winners, string logical, string physicalSide) {
            int sideIdx = SideIndex(physicalSide);
            string physical = sideIdx >= 0 ? borderSideByIndex[sideIdx] : "border-" + physicalSide;
            Alias(winners, logical, physical);
        }

        static void AliasBorderComponent(Dictionary<string, MatchedDeclaration> winners, string logical, string physicalSide, string component) {
            int sideIdx = SideIndex(physicalSide);
            int compIdx = ComponentIndex(component);
            string physical = (sideIdx >= 0 && compIdx >= 0)
                ? borderComponentByIndex[compIdx][sideIdx]
                : "border-" + physicalSide + "-" + component;
            Alias(winners, logical, physical);
        }

        static void AliasCorner(Dictionary<string, MatchedDeclaration> winners, string logical, string blockSide, string inlineSide) {
            string physical = PhysicalCorner(blockSide, inlineSide);
            if (physical != null) Alias(winners, logical, physical);
        }

        static void Alias(Dictionary<string, MatchedDeclaration> winners, string logical, string physical) {
            if (string.IsNullOrEmpty(logical) || string.IsNullOrEmpty(physical)) return;
            if (!winners.TryGetValue(logical, out var logicalWinner)) return;
            var synthetic = new MatchedDeclaration(
                new Declaration(physical, logicalWinner.Declaration.ValueText, logicalWinner.Declaration.Important),
                logicalWinner.Origin,
                logicalWinner.Specificity,
                logicalWinner.SourceIndex,
                logicalWinner.IsInline,
                logicalWinner.InRuleIndex,
                logicalWinner.LayerOrdinal,
                logicalWinner.SelectorText);

            if (winners.TryGetValue(physical, out var existing) && CompareForCascade(existing, synthetic) > 0) {
                return;
            }
            winners[physical] = synthetic;
        }

        static string PhysicalCorner(string a, string b) {
            bool top = a == "top" || b == "top";
            bool right = a == "right" || b == "right";
            bool bottom = a == "bottom" || b == "bottom";
            bool left = a == "left" || b == "left";
            if (top && left) return "border-top-left-radius";
            if (top && right) return "border-top-right-radius";
            if (bottom && right) return "border-bottom-right-radius";
            if (bottom && left) return "border-bottom-left-radius";
            return null;
        }

        readonly struct LogicalAxes {
            public readonly string InlineStart;
            public readonly string InlineEnd;
            public readonly string BlockStart;
            public readonly string BlockEnd;
            public readonly bool InlineIsHorizontal;

            LogicalAxes(string inlineStart, string inlineEnd, string blockStart, string blockEnd, bool inlineIsHorizontal) {
                InlineStart = inlineStart;
                InlineEnd = inlineEnd;
                BlockStart = blockStart;
                BlockEnd = blockEnd;
                InlineIsHorizontal = inlineIsHorizontal;
            }

            public static LogicalAxes From(Dictionary<string, MatchedDeclaration> winners, ComputedStyle parentStyle) {
                string direction = ResolveKeyword("direction", winners, parentStyle, "ltr");
                string writingMode = ResolveKeyword("writing-mode", winners, parentStyle, "horizontal-tb");
                bool rtl = direction == "rtl";

                if (writingMode == "vertical-rl" || writingMode == "sideways-rl") {
                    return new LogicalAxes(
                        rtl ? "bottom" : "top",
                        rtl ? "top" : "bottom",
                        "right",
                        "left",
                        false);
                }
                if (writingMode == "vertical-lr") {
                    return new LogicalAxes(
                        rtl ? "bottom" : "top",
                        rtl ? "top" : "bottom",
                        "left",
                        "right",
                        false);
                }
                if (writingMode == "sideways-lr") {
                    return new LogicalAxes(
                        rtl ? "top" : "bottom",
                        rtl ? "bottom" : "top",
                        "left",
                        "right",
                        false);
                }

                return new LogicalAxes(
                    rtl ? "right" : "left",
                    rtl ? "left" : "right",
                    "top",
                    "bottom",
                    true);
            }

            static string ResolveKeyword(string property, Dictionary<string, MatchedDeclaration> winners,
                                         ComputedStyle parentStyle, string fallback) {
                string raw = null;
                if (winners != null && winners.TryGetValue(property, out var winner)) {
                    raw = winner.Declaration.ValueText;
                } else if (parentStyle != null) {
                    raw = parentStyle.Get(property);
                }
                if (string.IsNullOrEmpty(raw)) raw = fallback;
                raw = KeywordResolver.Resolve(property, raw, parentStyle);
                if (string.IsNullOrEmpty(raw)) return fallback;
                return CssStringUtil.ToLowerInvariantOrSame(raw.Trim());
            }
        }
    }
}
