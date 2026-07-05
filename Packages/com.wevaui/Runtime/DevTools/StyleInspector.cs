using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Css.Selectors;
using Weva.Layout.Boxes;

namespace Weva.DevTools {
    // DevTools W7 phase 1 — computed-style + cascade-trace dump API.
    //
    // StyleInspector.Dump() produces a StyleInspectorReport containing:
    //   1. Every set property on the element's ComputedStyle (name → value).
    //   2. A per-property cascade trace when CaptureCascadeTrace is true:
    //      the winning declaration's selector text, origin, specificity,
    //      source order, and all overridden declarations for the same
    //      property in cascade order (highest-priority first).
    //   3. Box-model numbers (margin/border/padding/content rects) using the
    //      same coordinate conventions as BoxOutlineRenderer.EmitFour.
    //
    // The flag defaults to false so normal rendering frames pay no allocation
    // cost. The overlay flips it on when the inspector panel is open.
    //
    // Cascade trace implementation note: CSS Cascade 4 §6.2 — winning
    // declarations are selected per property after cascade sort. The engine
    // does not retain the losing declarations after ComputeFor (they are
    // scratch-allocated per pass). To obtain the trace we call
    // CascadeEngine.CollectMatchesFor, which re-runs CollectMatchesManaged
    // for the element (a fresh managed-path pass that bypasses shape/matched-
    // props caches). Cost: one extra CollectMatchesManaged call per Dump();
    // at inspector interaction rate (≤ once per pointer-settle) this is
    // negligible. The matched declaration list is then sorted by CompareForCascade
    // order (same comparator used internally) so the trace reflects actual
    // cascade priority.
    public static class StyleInspector {
        // When true, Dump() calls CascadeEngine.CollectMatchesFor to produce
        // per-property cascade traces. Flip on from the DevTools overlay before
        // calling Dump(); leave false (the default) in production to avoid any
        // allocation on the hot rendering path.
        public static bool CaptureCascadeTrace { get; set; } = false;

        // Produce a StyleInspectorReport for the given element.
        //
        // element   — the DOM element to inspect.
        // style     — its resolved ComputedStyle (from CascadeEngine.GetComposedStyle).
        // box       — the element's primary layout Box (from ElementToBoxIndex).
        //             May be null (the report will have zeroed box-model numbers).
        // cascade   — the engine; only consulted when CaptureCascadeTrace is true.
        // state     — element-state provider forwarded to CollectMatchesFor.
        //
        // Returns an empty report (not null) when element is null.
        public static StyleInspectorReport Dump(
            Element element,
            ComputedStyle style,
            Box box,
            CascadeEngine cascade = null,
            IElementStateProvider state = null) {

            var report = new StyleInspectorReport(element, box);

            // -- 1. Computed-value map -----------------------------------------
            if (style != null) {
                foreach (var kv in style.Enumerate()) {
                    // Skip null values (unset-via-null / CSS-wide keyword
                    // artifacts). Authors never see null in the Styles panel.
                    if (kv.Value == null) continue;
                    report.ComputedValues[kv.Key] = kv.Value;
                }
            }

            // -- 2. Cascade trace (flag-gated) ---------------------------------
            if (CaptureCascadeTrace && cascade != null && element != null) {
                var matches = cascade.CollectMatchesFor(element, state);

                // Sort highest cascade-priority last so we can read the winner
                // as the final entry per property in a single forward pass.
                // CompareForCascade is package-internal; we reconstruct the
                // sort order via the same axes (importance > origin > layer >
                // specificity > source order) exposed on MatchedDeclaration.
                matches.Sort(CascadeDeclarationComparer.Instance);

                // Build per-property winner + losers maps.
                // Because we sorted ascending (lowest → highest priority), the
                // last entry for each property key is the winner.
                var winnerByProp = new Dictionary<string, MatchedDeclaration>(matches.Count);
                var losersByProp  = new Dictionary<string, List<MatchedDeclaration>>();

                for (int i = 0; i < matches.Count; i++) {
                    var m = matches[i];
                    string prop = m.Declaration?.Property;
                    if (prop == null) continue;

                    if (winnerByProp.TryGetValue(prop, out var prev)) {
                        // The previous winner for this property is being
                        // displaced — move it to the losers list.
                        if (!losersByProp.TryGetValue(prop, out var lst)) {
                            lst = new List<MatchedDeclaration>(2);
                            losersByProp[prop] = lst;
                        }
                        lst.Add(prev);
                    }
                    winnerByProp[prop] = m;
                }

                // Emit one CascadePropertyTrace per property that has a winner.
                foreach (var kv in winnerByProp) {
                    string prop = kv.Key;
                    var winner = kv.Value;
                    List<MatchedDeclaration> losers = null;
                    losersByProp.TryGetValue(prop, out losers);

                    // Reverse losers so highest-priority loser is first
                    // (mirrors Chrome DevTools "overridden declarations" panel).
                    if (losers != null) {
                        losers.Reverse();
                    }

                    var trace = new CascadePropertyTrace(prop, winner, losers);
                    report.CascadeTrace[prop] = trace;
                }
            }

            return report;
        }
    }

    // Ascending sort by cascade priority (lowest first) so the last entry
    // per property after iteration is the winner. Mirrors the internal
    // CompareForCascade axes in CascadeEngine.
    //
    // CSS Cascade 4 §6.2 cascade order (ascending = lower priority first):
    //   1. Origin+importance (UA < user < author for normal; inverted for !important)
    //   2. Layer ordinal (lower ordinal = earlier layer = lower priority for normal)
    //   3. Inline style (style="" beats any selector, per CSS Cascade §6.2 step 6)
    //   4. Specificity
    //   5. Source order (lower = earlier in source = lower priority)
    //   6. In-rule index (lower = earlier in rule block = lower priority)
    //
    // Inline styles carry IsInline=true and SourceIndex=1_000_000_000 in the
    // engine, but specificity is stored as (0,0,0) — so the inline axis must
    // be checked explicitly between layer and specificity so that inline beats
    // even a high-specificity selector (#id = 1,0,0) of the same origin.
    sealed class CascadeDeclarationComparer : IComparer<MatchedDeclaration> {
        public static readonly CascadeDeclarationComparer Instance = new();

        public int Compare(MatchedDeclaration x, MatchedDeclaration y) {
            // Importance axis: !important beats normal. For !important rules
            // the origin order inverts (UA !important > author !important).
            bool xi = x.Declaration?.Important ?? false;
            bool yi = y.Declaration?.Important ?? false;
            if (xi != yi) return xi ? 1 : -1; // important > normal

            if (!xi) {
                // Normal declarations: author > user > UA.
                int oc = ((int)x.Origin).CompareTo((int)y.Origin);
                if (oc != 0) return oc;
                // Layer (higher ordinal = later layer = higher priority for normal).
                int lc = x.LayerOrdinal.CompareTo(y.LayerOrdinal);
                if (lc != 0) return lc;
            } else {
                // !important: UA !important > user !important > author !important
                // (origin order inverted per Cascade L4 §6.2 step 4).
                int oc = ((int)y.Origin).CompareTo((int)x.Origin);
                if (oc != 0) return oc;
                // Layer order also inverts for !important per Cascade L5 §6.4.1.
                int lc = y.LayerOrdinal.CompareTo(x.LayerOrdinal);
                if (lc != 0) return lc;
            }

            // Inline style beats any selector-based declaration of the same origin+layer
            // (CSS Cascade §6.2 step 6 — "style attribute" axis). The engine stores
            // inline specificity as (0,0,0), so we must check IsInline explicitly
            // before the specificity comparison to avoid an inline style losing to
            // a selector with (1,0,0) specificity.
            int inline = x.IsInline.CompareTo(y.IsInline); // false<true so inline sorts last = wins
            if (inline != 0) return inline;

            // Specificity.
            int sc = x.Specificity.CompareTo(y.Specificity);
            if (sc != 0) return sc;
            // Source order then in-rule index.
            int src = x.SourceIndex.CompareTo(y.SourceIndex);
            if (src != 0) return src;
            return x.InRuleIndex.CompareTo(y.InRuleIndex);
        }
    }

    // Full inspection report for one element. All fields are plain C# —
    // no Unity API, no COM dependencies, safe to consume from headless tests
    // and from the overlay renderer alike.
    public sealed class StyleInspectorReport {
        // The element that was inspected. Null when Dump was called with a
        // null element; all other fields will be empty/zeroed in that case.
        public readonly Element Element;

        // Computed value of every property that was set on the element's
        // ComputedStyle (property name → resolved value string). Custom
        // properties (--name) are included; null values are excluded.
        public readonly Dictionary<string, string> ComputedValues = new();

        // Per-property cascade trace. Populated only when
        // StyleInspector.CaptureCascadeTrace is true at the time Dump was
        // called and a CascadeEngine was supplied. Missing entries mean the
        // property was resolved from inheritance or initial-value fill-in,
        // not from an explicit authored rule.
        public readonly Dictionary<string, CascadePropertyTrace> CascadeTrace = new();

        // Box-model geometry. Matches the BoxOutlineRenderer.EmitFour
        // convention: MarginRect is the outer envelope including margins;
        // BorderRect is the border-box (Box.X/Y/Width/Height); PaddingRect
        // is inside the border edges; ContentRect is inside the padding.
        public readonly BoxModelNumbers BoxModel;

        internal StyleInspectorReport(Element element, Box box) {
            Element = element;
            BoxModel = box != null ? new BoxModelNumbers(box) : BoxModelNumbers.Zero;
        }

        // Multi-line human-readable dump. The overlay renders this via its
        // text panel; tests may assert on substring containment.
        public override string ToString() {
            var sb = new StringBuilder(512);
            sb.Append("=== ");
            if (Element != null) {
                sb.Append('<').Append(Element.TagName);
                var id = Element.Id;
                if (!string.IsNullOrEmpty(id)) sb.Append('#').Append(id);
                foreach (var cls in Element.ClassList) sb.Append('.').Append(cls);
                sb.Append('>');
            } else {
                sb.Append("<no element>");
            }
            sb.AppendLine(" ===");

            // Box-model block.
            sb.AppendLine("-- box model --");
            sb.Append("  border-box : ").AppendLine(FmtRect(BoxModel.BorderX,  BoxModel.BorderY,
                                                            BoxModel.BorderW,  BoxModel.BorderH));
            sb.Append("  margin-box : ").AppendLine(FmtRect(BoxModel.MarginX,  BoxModel.MarginY,
                                                            BoxModel.MarginW,  BoxModel.MarginH));
            sb.Append("  padding-box: ").AppendLine(FmtRect(BoxModel.PaddingX, BoxModel.PaddingY,
                                                            BoxModel.PaddingW, BoxModel.PaddingH));
            sb.Append("  content-box: ").AppendLine(FmtRect(BoxModel.ContentX, BoxModel.ContentY,
                                                            BoxModel.ContentW, BoxModel.ContentH));

            // Computed values.
            sb.AppendLine("-- computed --");
            foreach (var kv in ComputedValues) {
                sb.Append("  ").Append(kv.Key).Append(": ").AppendLine(kv.Value ?? "");
            }

            // Cascade trace.
            if (CascadeTrace.Count > 0) {
                sb.AppendLine("-- cascade --");
                foreach (var kv in CascadeTrace) {
                    var t = kv.Value;
                    // Selector text precedes specificity so the author sees the
                    // matching rule identity first (mirrors Chrome DevTools layout).
                    if (!string.IsNullOrEmpty(t.WinnerSelectorText)) {
                        sb.Append("  ").Append(t.WinnerSelectorText).Append(" { ");
                    } else {
                        sb.Append("  <inline> { ");
                    }
                    sb.Append(kv.Key).Append(": ").Append(t.WinnerValue)
                      .Append(" }  [").Append(OriginLabel(t.WinnerOrigin))
                      .Append(" spec=").Append(t.WinnerSpecificity)
                      .Append(" src=").Append(t.WinnerSourceIndex.ToString(CultureInfo.InvariantCulture))
                      .AppendLine("]");
                    if (t.OverriddenDeclarations != null) {
                        foreach (var ov in t.OverriddenDeclarations) {
                            if (!string.IsNullOrEmpty(ov.SelectorText)) {
                                sb.Append("    (overridden) ").Append(ov.SelectorText).Append(" { ");
                            } else {
                                sb.Append("    (overridden) <inline> { ");
                            }
                            sb.Append(ov.Property)
                              .Append(": ").Append(ov.ValueText ?? "")
                              .Append(" }  [").Append(OriginLabel(ov.Origin))
                              .Append(" spec=").Append(ov.Specificity)
                              .Append(" src=").Append(ov.SourceIndex.ToString(CultureInfo.InvariantCulture))
                              .AppendLine("]");
                        }
                    }
                }
            }
            return sb.ToString();
        }

        static string FmtRect(double x, double y, double w, double h) {
            return string.Format(CultureInfo.InvariantCulture,
                "{0:F1},{1:F1} {2:F1}×{3:F1}", x, y, w, h);
        }

        static string OriginLabel(DeclarationOrigin origin) {
            switch (origin) {
                case DeclarationOrigin.UserAgent: return "UA";
                case DeclarationOrigin.User:      return "user";
                default:                          return "author";
            }
        }
    }

    // Cascade trace for one property: winner declaration metadata plus a
    // list of overridden (losing) declarations in descending cascade priority.
    public sealed class CascadePropertyTrace {
        public readonly string Property;

        // Selector text of the rule whose declaration won. Null for inline
        // styles (they have no selector).
        public readonly string WinnerSelectorText;

        // Winning declaration value string (pre-shorthand-expansion raw value).
        public readonly string WinnerValue;

        // Whether the winning declaration carried !important.
        public readonly bool WinnerImportant;

        public readonly DeclarationOrigin WinnerOrigin;

        // Specificity of the winning selector (a, b, c). (0,0,0) for inline.
        public readonly Css.Selectors.Specificity WinnerSpecificity;

        // Position in the authored stylesheet ordering (higher = later = higher
        // priority within the same origin+layer+specificity bucket).
        public readonly int WinnerSourceIndex;

        // Overridden (losing) declarations for the same property, ordered from
        // highest-priority loser to lowest-priority loser. May be null when
        // no losing declarations were collected.
        public readonly IReadOnlyList<OverriddenDeclaration> OverriddenDeclarations;

        internal CascadePropertyTrace(string property, MatchedDeclaration winner,
                                      List<MatchedDeclaration> losers) {
            Property = property;
            // WinnerSelectorText: the original authored selector text (e.g.
            // ".card:hover > .title"), sourced from CompiledSelector.SourceText
            // which is captured at parse time. Null for inline styles (no selector).
            WinnerSelectorText = winner.IsInline ? null : winner.SelectorText;
            WinnerValue       = winner.Declaration?.ValueText;
            WinnerImportant   = winner.Declaration?.Important ?? false;
            WinnerOrigin      = winner.Origin;
            WinnerSpecificity = winner.Specificity;
            WinnerSourceIndex = winner.SourceIndex;

            if (losers != null && losers.Count > 0) {
                var list = new List<OverriddenDeclaration>(losers.Count);
                foreach (var l in losers) {
                    list.Add(new OverriddenDeclaration(
                        l.Declaration?.Property ?? property,
                        l.Declaration?.ValueText,
                        l.Declaration?.Important ?? false,
                        l.Origin,
                        l.Specificity,
                        l.SourceIndex,
                        l.IsInline ? null : l.SelectorText));
                }
                OverriddenDeclarations = list;
            }
        }
    }

    // One overridden declaration in a cascade trace. Lightweight value object.
    public sealed class OverriddenDeclaration {
        public readonly string Property;
        public readonly string ValueText;
        public readonly bool Important;
        public readonly DeclarationOrigin Origin;
        public readonly Css.Selectors.Specificity Specificity;
        public readonly int SourceIndex;
        // Selector text of the rule that authored this (overridden) declaration.
        // Null for inline styles (they have no selector).
        public readonly string SelectorText;

        internal OverriddenDeclaration(string property, string valueText, bool important,
                                       DeclarationOrigin origin,
                                       Css.Selectors.Specificity specificity,
                                       int sourceIndex,
                                       string selectorText = null) {
            Property    = property;
            ValueText   = valueText;
            Important   = important;
            Origin      = origin;
            Specificity = specificity;
            SourceIndex = sourceIndex;
            SelectorText = selectorText;
        }
    }

    // Box-model geometry for one element, using the same conventions as
    // BoxOutlineRenderer.EmitFour. All coordinates are absolute (root-relative).
    // Zero-initialised when no Box is available.
    public readonly struct BoxModelNumbers {
        // Outer margin envelope.
        public readonly double MarginX, MarginY, MarginW, MarginH;
        // Border-box (Box.X/Y/Width/Height in absolute coords).
        public readonly double BorderX, BorderY, BorderW, BorderH;
        // Inside the border edges.
        public readonly double PaddingX, PaddingY, PaddingW, PaddingH;
        // Inside the padding edges.
        public readonly double ContentX, ContentY, ContentW, ContentH;

        internal BoxModelNumbers(Box box) {
            // Compute absolute origin by summing the parent chain.
            // (ElementPicker and DevToolsOverlay pass the primary box
            // from the ElementToBoxIndex which holds X/Y LOCAL to parent.
            // Walk the chain to get the document-space origin.)
            double absX = 0, absY = 0;
            for (var b = box; b != null; b = b.Parent) {
                absX += b.X + b.StickyOffsetX;
                absY += b.Y + b.StickyOffsetY;
                if (b.Style != null) {
                    var xf = Paint.Conversion.TransformResolver.ResolveTransform(
                        b.Style, b.Width, b.Height);
                    if (xf.Tx != 0f || xf.Ty != 0f) {
                        absX += xf.Tx;
                        absY += xf.Ty;
                    }
                }
            }

            // Mirror BoxOutlineRenderer.EmitFour exactly.
            MarginX = absX - box.MarginLeft;
            MarginY = absY - box.MarginTop;
            MarginW = box.Width + box.MarginLeft + box.MarginRight;
            MarginH = box.Height + box.MarginTop + box.MarginBottom;

            BorderX = absX;
            BorderY = absY;
            BorderW = box.Width;
            BorderH = box.Height;

            PaddingX = absX + box.BorderLeft;
            PaddingY = absY + box.BorderTop;
            PaddingW = System.Math.Max(0, box.Width  - box.BorderLeft - box.BorderRight);
            PaddingH = System.Math.Max(0, box.Height - box.BorderTop  - box.BorderBottom);

            ContentX = PaddingX + box.PaddingLeft;
            ContentY = PaddingY + box.PaddingTop;
            ContentW = System.Math.Max(0, PaddingW - box.PaddingLeft - box.PaddingRight);
            ContentH = System.Math.Max(0, PaddingH - box.PaddingTop  - box.PaddingBottom);
        }

        // Zero-initialised variant for when no box is available.
        internal BoxModelNumbers(bool _zero) {
            MarginX = MarginY = MarginW = MarginH = 0;
            BorderX = BorderY = BorderW = BorderH = 0;
            PaddingX = PaddingY = PaddingW = PaddingH = 0;
            ContentX = ContentY = ContentW = ContentH = 0;
        }

        // No parameterless ctor: Unity compiles the package at C# 9, where
        // parameterless struct constructors don't exist (the headless runner
        // builds at `latest` and masked this). `default(BoxModelNumbers)` is
        // already all-zero for the no-box case; the bool overload above stays
        // for explicit-intent call sites.
        public static BoxModelNumbers Zero => new BoxModelNumbers(false);
    }
}
