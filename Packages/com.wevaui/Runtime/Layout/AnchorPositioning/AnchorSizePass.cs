using System.Collections.Generic;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Layout.Boxes;

namespace Weva.Layout.AnchorPositioning {
    // AnchorSizePass — pre-layout rewrite of `anchor-size(<name>? <axis>?)`
    // function values appearing in width/height/min-*/max-*. Walks the box
    // tree, captures each anchor box's declared width/height (as resolved by
    // the cascade), then for every consumer rewrites the function value to
    // the resolved pixel string before BlockLayout reads it.
    //
    // v1 simplifications:
    //   - The anchor's own width/height must be a fixed CSS length (not auto,
    //     not %, not anchor-size). Declared via `width: 100px` etc. on the
    //     anchor element. If unresolved, the consumer falls back to the
    //     property's initial value (auto / 0).
    //   - Logical axes (block / inline / self-block / self-inline) are mapped
    //     to the same physical axis (LTR-only).
    //   - Implicit anchor name (omitted) reads `position-anchor` from the
    //     consumer's style.
    //   - The pass is idempotent — already-resolved values that no longer
    //     contain `anchor-size(` are skipped.
    //
    // Per-style parsed-cache migration (commit 1b7acc7): reads now flow through
    // ComputedStyle.GetParsed(int propertyId) instead of the string-key
    // style.Get + downstream CssValue.TryParse path. The cached parse tree is
    // dispatched on by type — CssIdentifier / CssValueList of identifiers for
    // anchor-name and position-anchor, CssLength/CssPercentage/CssCalc/keyword
    // for the anchor's declared width/height, CssFunctionCall("anchor-size", …)
    // for consumer width/height/min-*/max-* values. Falls back to the raw
    // string only when the parse tree wasn't recognised (e.g. complex shorthand
    // that still needs string-substring detection of "anchor-size(").
    public sealed class AnchorSizePass {
        // Side properties indexed by id, mirroring the old string arrays. The
        // axis kind for each entry is implicit in the array index — Width[*]
        // resolves an anchor-size(width)/(inline) call, Height[*] resolves
        // (height)/(block). The "Inferred" axis path in RewriteSideProperties
        // uses the array to decide which side's defaults to apply.
        static readonly int[] WidthPropIds = {
            CssProperties.WidthId, CssProperties.MinWidthId, CssProperties.MaxWidthId
        };
        static readonly int[] HeightPropIds = {
            CssProperties.HeightId, CssProperties.MinHeightId, CssProperties.MaxHeightId
        };
        static readonly string[] WidthPropNames = { "width", "min-width", "max-width" };
        static readonly string[] HeightPropNames = { "height", "min-height", "max-height" };

        // Persistent dictionary: cleared at the start of every Apply call,
        // populated by the collect-anchors walk, consumed by the rewrite walk.
        // Reused across Layout passes so there is zero alloc steady-state when
        // the document holds zero or stable anchor sets. The instance is held
        // by LayoutEngine and never escapes a single pass.
        readonly Dictionary<string, AnchorSize> resolvedSizes = new();

        // Diagnostic for tests: count of anchors collected by the most recent
        // ApplyInstance() call. Reset to zero implicitly when the dictionary is
        // re-populated; observable as long as Apply is not re-entered.
        internal int LastResolvedCount => resolvedSizes.Count;

        // Static convenience that owns a fresh AnchorSizePass per call. Kept
        // for the test API surface that constructed AnchorSizePass.Apply(root)
        // directly. Production callers (LayoutEngine) hold a long-lived
        // instance and use the instance Apply.
        public static void Apply(Box root) {
            if (root == null) return;
            new AnchorSizePass().ApplyInstance(root);
        }

        // Box-tree variant. Walks the tree once to harvest anchor sizes from
        // boxes that declare `anchor-name`, then a second pass rewrites
        // `anchor-size(...)` consumers in place.
        public void ApplyInstance(Box root) {
            if (root == null) return;
            resolvedSizes.Clear();
            CollectAnchors(root, resolvedSizes);
            if (resolvedSizes.Count == 0) return;
            RewriteConsumers(root, resolvedSizes);
        }

        // Document/style-map variant — used by tests and by callers that
        // operate on cascade output before the box tree is built.
        public static void Apply(Document doc, IDictionary<Element, ComputedStyle> styles) {
            if (doc == null || styles == null || styles.Count == 0) return;
            var anchorSizes = new Dictionary<string, AnchorSize>();
            foreach (var kv in styles) {
                var element = kv.Key;
                var style = kv.Value;
                if (element == null || style == null) continue;
                CollectAnchorNamesInto(style, anchorSizes);
            }
            if (anchorSizes.Count == 0) return;
            foreach (var kv in styles) {
                var style = kv.Value;
                if (style == null) continue;
                RewriteSideProperties(style, anchorSizes, WidthPropIds, WidthPropNames);
                RewriteSideProperties(style, anchorSizes, HeightPropIds, HeightPropNames);
            }
        }

        static void CollectAnchors(Box box, Dictionary<string, AnchorSize> result) {
            var style = box.Style;
            if (style != null) CollectAnchorNamesInto(style, result);
            for (int i = 0; i < box.Children.Count; i++) CollectAnchors(box.Children[i], result);
        }

        // Reads `anchor-name` via GetParsed and records the box's declared
        // width/height under each `--name` token. Skips `none` and any
        // identifier that doesn't start with `--` per CSS Anchor Positioning §3.
        static void CollectAnchorNamesInto(ComputedStyle style, Dictionary<string, AnchorSize> result) {
            var parsed = style.GetParsed(CssProperties.AnchorNameId);
            if (parsed == null) return;
            // The parser routes dashed-ident tokens (`--btn`) into CssKeyword,
            // while plain identifiers come back as CssIdentifier — both are
            // valid anchor-name forms (only dashed-ident is spec-conformant
            // per CSS Anchor Positioning §3, but we accept either). Pull the
            // name through a shared helper that handles both subtypes.
            string nameSingle = ExtractDashedName(parsed);
            if (nameSingle == "none") return;
            string width = ReadAnchorSideRaw(style, CssProperties.WidthId);
            string height = ReadAnchorSideRaw(style, CssProperties.HeightId);
            if (parsed is CssValueList list && list.Separator == CssValueListSeparator.Comma) {
                for (int i = 0; i < list.Items.Count; i++) {
                    string name = ExtractDashedName(list.Items[i]);
                    if (name != null && name.StartsWith("--")) {
                        result[name] = new AnchorSize(width, height);
                    }
                }
                return;
            }
            if (nameSingle != null && nameSingle.StartsWith("--")) {
                result[nameSingle] = new AnchorSize(width, height);
            }
        }

        // Helper: returns the lowercase identifier carried by a CssKeyword or
        // CssIdentifier, or null for anything else. dashed-ident tokens come
        // back as CssKeyword whose Identifier is the original `--name`.
        static string ExtractDashedName(CssValue v) {
            if (v is CssKeyword k) return k.Identifier;
            if (v is CssIdentifier id) return id.Name;
            return null;
        }

        // Returns the anchor's declared length/percent/calc as a raw string
        // suitable for direct injection into the consumer's width/height slot.
        // The downstream layout reads the raw via Get(prop) and re-parses, so
        // we propagate the .Raw form rather than the typed value. `auto` and
        // unresolved slots return null so callers can skip the rewrite.
        static string ReadAnchorSideRaw(ComputedStyle style, int propertyId) {
            var parsed = style.GetParsed(propertyId);
            if (parsed == null) return null;
            if (parsed is CssKeyword k && k.Identifier == "auto") return "auto";
            if (parsed is CssLength || parsed is CssPercentage || parsed is CssCalc || parsed is CssNumber) {
                return parsed.Raw;
            }
            // Anything else (function call, identifier "none", value list) is
            // unsupported as an anchor-side source in v1 and falls back to
            // null, which causes the rewriter to skip the consumer slot.
            return null;
        }

        static void RewriteConsumers(Box box, Dictionary<string, AnchorSize> anchorSizes) {
            var style = box.Style;
            if (style != null) {
                RewriteSideProperties(style, anchorSizes, WidthPropIds, WidthPropNames);
                RewriteSideProperties(style, anchorSizes, HeightPropIds, HeightPropNames);
            }
            for (int i = 0; i < box.Children.Count; i++) RewriteConsumers(box.Children[i], anchorSizes);
        }

        static void RewriteSideProperties(ComputedStyle style, Dictionary<string, AnchorSize> anchorSizes, int[] propIds, string[] propNames) {
            for (int i = 0; i < propIds.Length; i++) {
                int id = propIds[i];
                // GetParsed gives us the typed parse tree directly. The
                // consumer slot is a candidate only when the parsed value is a
                // CssFunctionCall named "anchor-size". For anything else
                // (length, percent, keyword, …) the property never contained
                // an anchor-size() call and we skip without touching the raw.
                // anchor-size() args contain a dashed-ident (--name) and a bare
                // axis keyword, which the CssValueParser may reject (returns
                // null from GetParsed) on dialects it doesn't fully support.
                // Fall back to raw-string substring detection so an unparseable
                // anchor-size() still triggers the rewrite. The downstream
                // AnchorFunctionParser.TryParseSize does its own tokenizing.
                var parsed = style.GetParsed(id);
                string raw;
                if (parsed is CssFunctionCall fn && fn.Name == "anchor-size") {
                    raw = parsed.Raw;
                } else {
                    raw = style.Get(id);
                    if (string.IsNullOrEmpty(raw) || raw.IndexOf("anchor-size(", System.StringComparison.Ordinal) < 0) continue;
                }
                if (string.IsNullOrEmpty(raw)) continue;
                if (!AnchorFunctionParser.TryParseSize(raw, out var call)) continue;
                string fallback = ReadAnchorIdentifier(style, CssProperties.PositionAnchorId);
                string name = call.AnchorName ?? fallback;
                if (string.IsNullOrEmpty(name) || !anchorSizes.TryGetValue(name, out var anchor)) continue;
                AnchorSizeAxis axis = call.Axis;
                if (axis == AnchorSizeAxis.Inferred) {
                    string prop = propNames[i];
                    axis = (prop == "height" || prop == "min-height" || prop == "max-height")
                        ? AnchorSizeAxis.Height
                        : AnchorSizeAxis.Width;
                }
                string rawSide = axis == AnchorSizeAxis.Height ? anchor.Height : anchor.Width;
                if (string.IsNullOrEmpty(rawSide) || rawSide == "auto") continue;
                style.Set(propNames[i], rawSide);
            }
        }

        // position-anchor is a single dashed-ident per CSS Anchor Positioning
        // §6.1. We dispatch via GetParsed so the typical case — an already-
        // cached CssIdentifier — bypasses the raw-string path entirely.
        // Returns null when the slot is unset, "auto", or "none".
        static string ReadAnchorIdentifier(ComputedStyle style, int propertyId) {
            var parsed = style.GetParsed(propertyId);
            if (parsed == null) return null;
            if (parsed is CssIdentifier id) {
                if (id.Name == null || id.Name == "auto" || id.Name == "none") return null;
                return id.Name;
            }
            if (parsed is CssKeyword k) {
                if (k.Identifier == "auto" || k.Identifier == "none") return null;
                return k.Identifier;
            }
            return null;
        }

        readonly struct AnchorSize {
            public string Width { get; }
            public string Height { get; }
            public AnchorSize(string width, string height) {
                Width = width;
                Height = height;
            }
        }
    }
}
