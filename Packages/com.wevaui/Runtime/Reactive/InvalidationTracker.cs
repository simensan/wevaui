using System;
using System.Collections.Generic;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;

namespace Weva.Reactive {
    public sealed class InvalidationTracker {
        readonly Dictionary<Node, InvalidationKind> dirty = new();
        readonly HashSet<Document> attached = new();
        // Set true by callers (CascadeEngine on `:has()` detection) to enable
        // ancestor-walk Style invalidation on DOM-tree mutations. Costs O(depth)
        // per mutation; avoided when no `:has()` selector is in the sheet.
        bool hasSensitive;

        public int DirtyCount => dirty.Count;
        public IReadOnlyCollection<Node> AllDirty => dirty.Keys;

        // Toggled by CascadeEngine when its compiled stylesheet contains any
        // `:has()` selector — enables ancestor-walk Style invalidation on DOM
        // tree mutations. Default off so existing benchmarks observe the
        // historical mutation cost when no `:has()` is in the sheet.
        public bool HasSensitive {
            get => hasSensitive;
            set => hasSensitive = value;
        }

        public void MarkDirty(Node node, InvalidationKind kind) {
            if (node == null || kind == InvalidationKind.None) return;
            // PseudoClassState is a refinement of Style for any downstream consumer
            // that filters by Style: cascade reads them distinctly so it can take
            // the per-element-digest fast path, but layout/paint should respond to
            // either one (a state flip CAN change resolved style if a matching
            // pseudo-class rule exists). We unify by implying Style alongside
            // PseudoClassState; the PseudoClassState bit remains for cascade.
            // Paint is INTENTIONALLY not implied here: the contract tested
            // by InvalidationTrackerTests.PseudoClassState_implies_Style_but_not_Layout
            // is that PseudoClassState surfaces ONLY as Style downstream
            // (Layout and Paint stay clean unless a separate mark adds them).
            // Hover/focus/active that actually change paint properties have
            // their paint dirtiness routed through the Style->Paint path
            // in UIDocumentLifecycle.RefreshPaintOnlyStyles.
            if ((kind & InvalidationKind.PseudoClassState) != 0) {
                kind |= InvalidationKind.Style;
            }
            if (dirty.TryGetValue(node, out var existing)) {
                dirty[node] = existing | kind;
            } else {
                dirty[node] = kind;
            }
        }

        // Marks Layout dirty on the element and propagates UP to the nearest
        // formatting-context boundary. v1 conservatism: when an element's
        // layout properties change, the change can affect:
        //   - the element's own box (always)
        //   - its parent's content-box height (if parent height is auto and
        //     parent is in normal flow), and via the parent's flex/grid
        //     algorithm any sibling whose track sizing depends on this item
        //   - its descendants whose width was percentage-relative
        // Per CSS Box Model L3 §8 + Flexbox L1 §9 + Grid L1 §11, a change to
        // a flex/grid item's main-axis size triggers the parent's main-axis
        // distribution algorithm to re-run; we propagate to the parent so the
        // parent's box is also marked. Walk stops at:
        //   - the document root, OR
        //   - an ancestor with `position: absolute`/`fixed` (its own size is
        //     decoupled from in-flow descendants), OR
        //   - an ancestor with explicit `width: <length>` AND `height:
        //     <length>` (intrinsic sizing cannot bubble through).
        // Returns the count of newly-marked elements (including the original
        // element).
        public int MarkLayoutForElement(Element element, System.Func<Element, Weva.Css.Cascade.ComputedStyle> styleOf) {
            if (element == null) return 0;
            int marked = 0;
            MarkDirty(element, InvalidationKind.Layout);
            marked++;
            // If the element itself is out of flow, stop immediately:
            // in-flow siblings cannot be pushed by its layout animation.
            if (styleOf != null) {
                var ownStyle = styleOf(element);
                if (ownStyle != null && IsOutOfFlowBoundary(ownStyle)) {
                    return marked;
                }
            }
            var parent = element.Parent as Element;
            while (parent != null) {
                MarkDirty(parent, InvalidationKind.Layout);
                marked++;
                if (styleOf != null) {
                    var ps = styleOf(parent);
                    if (ps != null && IsLayoutBoundary(ps)) break;
                }
                parent = parent.Parent as Element;
            }
            return marked;
        }

        // True when the element's computed style implies its OUTER box size
        // does not depend on its content. v1 detection:
        //   - position: absolute / fixed (out-of-flow; sized by containing block)
        //   - both width and height are explicit lengths (not auto, not %)
        // Conservative: any case we can't recognise returns false, which keeps
        // the propagation walking up.
        //
        // Each property read goes through the per-style parsed cache so the
        // keyword / length dispatch is a typed pattern match on an already-
        // parsed CssValue — no string compare, no Trim/ToLowerInvariant, no
        // CssValue.TryParse round trip on the steady-state hot path. Layout-
        // mark propagation walks the parent chain on every mutation, so this
        // matters proportionally to tree depth.
        static bool IsLayoutBoundary(ComputedStyle style) {
            if (IsOutOfFlowBoundary(style)) return true;
            var w = style.GetParsed(CssProperties.WidthId);
            var h = style.GetParsed(CssProperties.HeightId);
            if (IsExplicitLengthParsed(w) && IsExplicitLengthParsed(h)) return true;
            return false;
        }

        static bool IsOutOfFlowBoundary(ComputedStyle style) {
            var posParsed = style.GetParsed(CssProperties.PositionId);
            if (IsAbsoluteOrFixed(posParsed)) return true;
            return false;
        }

        // `position` is a keyword grammar — the parser emits CssKeyword for
        // recognized values. `absolute` / `fixed` are the two we treat as
        // out-of-flow boundaries; other keywords (`static` / `relative` /
        // `sticky`) fall through to false because their box size still
        // depends on in-flow descendants.
        static bool IsAbsoluteOrFixed(CssValue parsed) {
            if (parsed is CssKeyword k) {
                string id = k.Identifier;
                return id == "absolute" || id == "fixed";
            }
            if (parsed is CssIdentifier i) {
                string name = i.Name;
                return CssStringUtil.EqualsIgnoreCase(name, "absolute")
                    || CssStringUtil.EqualsIgnoreCase(name, "fixed");
            }
            return false;
        }

        // Explicit length means: the cascade produced a CssLength
        // (px/em/rem/etc) or a CssNumber (a unitless 0 — valid CSS length).
        // Percentages bubble per the original conservative rule because the
        // containing block can ripple; `auto` / CSS-wide keywords
        // (`inherit`, `initial`, `unset`) also bubble — they don't pin the
        // box size locally. CssCalc is treated as non-explicit here because
        // IsLayoutBoundary has no LengthContext and the prior string-keyed
        // predicate didn't honour calc() either.
        static bool IsExplicitLengthParsed(CssValue parsed) {
            if (parsed == null) return false;
            if (parsed is CssLength) return true;
            if (parsed is CssNumber) return true;
            return false;
        }

        public void MarkSubtreeDirty(Node root, InvalidationKind kind) {
            if (root == null || kind == InvalidationKind.None) return;
            MarkDirty(root, kind);
            for (int i = 0; i < root.Children.Count; i++) {
                MarkSubtreeDirty(root.Children[i], kind);
            }
        }

        // Marks every descendant of `root` (NOT root itself) with `kind`. Used
        // by the class/id-mutation path in OnMutation: the target element gets
        // the broader Style|Layout|Paint mark, while descendants are narrowed
        // to Style only. The cascade's per-element re-cascade (driven by the
        // target's bumped ComputedStyle.Version flowing through descendants'
        // parentStyleVersion in the IncrementalCacheKey) re-resolves each
        // descendant's style; downstream Layout / Paint caches embed
        // box.Style.Version in their cache keys (LayoutCacheKey,
        // PaintCacheKey), so a real value change naturally invalidates them
        // without us paying for a Layout|Paint mark on every descendant that
        // wouldn't have been affected anyway.
        public void MarkDescendantsDirty(Node root, InvalidationKind kind) {
            if (root == null || kind == InvalidationKind.None) return;
            for (int i = 0; i < root.Children.Count; i++) {
                MarkSubtreeDirty(root.Children[i], kind);
            }
        }

        public bool IsDirty(Node node, InvalidationKind kind) {
            if (node == null) return false;
            if (!dirty.TryGetValue(node, out var existing)) return false;
            return (existing & kind) == kind && kind != InvalidationKind.None;
        }

        public bool HasAny(InvalidationKind kind) {
            if (kind == InvalidationKind.None) return false;
            foreach (var v in dirty.Values) {
                if ((v & kind) != 0) return true;
            }
            return false;
        }

        // Allocates a state-machine class per call (yield return). The
        // caller pattern is `foreach (var n in tracker.GetDirty(...))` —
        // for hot per-frame consumers (BoxToPaintConverter.Convert,
        // LayoutEngine), prefer the allocation-free DirtyEntries below
        // and filter by kind in-line.
        public IEnumerable<Node> GetDirty(InvalidationKind kind) {
            foreach (var kv in dirty) {
                if ((kv.Value & kind) != 0) yield return kv.Key;
            }
        }

        // Direct dictionary access for allocation-free iteration. foreach
        // over Dictionary<TKey,TValue> uses the struct enumerator, so the
        // caller pays no heap alloc — unlike the IEnumerable<> path on
        // GetDirty which boxes the state machine.
        public Dictionary<Node, InvalidationKind> DirtyEntries => dirty;

        public InvalidationKind GetKinds(Node node) {
            if (node == null) return InvalidationKind.None;
            return dirty.TryGetValue(node, out var v) ? v : InvalidationKind.None;
        }

        public void Clear() {
            dirty.Clear();
        }

        public void Clear(InvalidationKind kind) {
            if (kind == InvalidationKind.None) return;
            var mask = ~kind;
            // Reuse a scratch buffer instead of \`new List<Node>(dirty.Keys)\` —
            // this fires once per Convert per frame and the old version
            // allocated a fresh list sized to the entire dirty set.
            clearScratch.Clear();
            foreach (var k in dirty.Keys) clearScratch.Add(k);
            for (int i = 0; i < clearScratch.Count; i++) {
                var k = clearScratch[i];
                var next = dirty[k] & mask;
                if (next == InvalidationKind.None) {
                    dirty.Remove(k);
                } else {
                    dirty[k] = next;
                }
            }
            clearScratch.Clear();
        }
        readonly List<Node> clearScratch = new List<Node>(64);

        public void Clear(Node node) {
            if (node == null) return;
            dirty.Remove(node);
        }

        public void Attach(Document doc) {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (!attached.Add(doc)) return;
            doc.Mutated += OnMutation;
        }

        public void Detach(Document doc) {
            if (doc == null) return;
            if (!attached.Remove(doc)) return;
            doc.Mutated -= OnMutation;
        }

        public void OnMutation(DomMutation m) {
            switch (m.Kind) {
                case DomMutationKind.ChildAdded:
                    MarkDirty(m.Target, InvalidationKind.Layout);
                    MarkSubtreeDirty(m.Subject, InvalidationKind.Structure | InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint);
                    // CSS Selectors L4 §17 (`:has()`): a child mutation can flip
                    // the match result of an ancestor selector that uses
                    // `:has()`. v1 conservatively walks each ancestor and marks
                    // Style when HasSensitive is set by the cascade engine on
                    // sheets that actually contain `:has()` selectors. Default
                    // false keeps existing benchmarks at parity for sheets
                    // without `:has()`.
                    for (var a = m.Target; a != null; a = a.Parent) {
                        MarkDirty(a, InvalidationKind.Composite);
                        if (hasSensitive) MarkDirty(a, InvalidationKind.Style);
                    }
                    break;

                case DomMutationKind.ChildRemoved:
                    MarkDirty(m.Target, InvalidationKind.Layout | InvalidationKind.Paint);
                    MarkSubtreeDirty(m.Subject, InvalidationKind.Structure | InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint);
                    // Mirror ChildAdded's ancestor walk: a removed child also
                    // changes each ancestor's content extent / stacking
                    // context, so their cached paint snapshots must drop.
                    // Previously only ChildAdded propagated Composite up the
                    // chain; ChildRemoved left ancestors holding paint caches
                    // that still referenced the removed subtree.
                    for (var a = m.Target; a != null; a = a.Parent) {
                        MarkDirty(a, InvalidationKind.Composite);
                        if (hasSensitive) MarkDirty(a, InvalidationKind.Style);
                    }
                    break;

                case DomMutationKind.AttributeAdded:
                case DomMutationKind.AttributeRemoved:
                case DomMutationKind.AttributeChanged: {
                    var name = m.AttributeName;
                    if (name == "class" || name == "id") {
                        // PI7 (Strategy B): narrow descendants to Style only.
                        // The target itself keeps Style|Layout|Paint because
                        // its own computed style is what changed and the
                        // IncrementalLayoutGate needs a Layout mark somewhere
                        // to NOT skip the layout pass. Descendants get Style
                        // only because:
                        //   - LayoutEngine.Apply drops layout cache entries
                        //     for any element marked Style (kind mask =
                        //     Layout|Structure|Style), so subsequent layout
                        //     re-resolves them via the parent's bumped
                        //     ComputedStyle.Version flowing through the
                        //     descendant's IncrementalCacheKey.
                        //   - BoxToPaintConverter.Apply drops paint cache
                        //     entries for any element marked Style (kind mask
                        //     includes Style), so re-paint sees the new style.
                        //   - LayoutCacheKey / PaintCacheKey both embed
                        //     box.Style.Version, so cache misses fire only
                        //     where the descendant's computed style actually
                        //     changed.
                        // Net effect: the dictionary entry count for a class
                        // flip on a 1000-element subtree is unchanged (1000
                        // entries), but each descendant entry holds 1 bit
                        // (Style) instead of 3 (Style|Layout|Paint), and the
                        // downstream re-layout / re-paint work narrows to
                        // descendants whose computed style actually flipped.
                        MarkDirty(m.Target, InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint);
                        MarkDescendantsDirty(m.Target, InvalidationKind.Style);
                    } else if (name == "value" || name == "placeholder") {
                        // LY11: mutated on EVERY keystroke in a text control.
                        // The value text is painted by the input overlay, not
                        // laid out from the box tree, so the unconditional
                        // Layout mark forced a relayout per keystroke — FULL
                        // layout when the input has inline siblings (the
                        // ContainsInlines splice bail) plus a document-wide
                        // snapshot clear. Style stays so `[value=…]` /
                        // `:placeholder-shown` rules re-match; if a re-matched
                        // rule actually changes a layout-affecting property,
                        // the cascade's narrow diff (ApplyLayoutInvalidation,
                        // wired in the lifecycle) marks Layout for exactly
                        // that element.
                        MarkDirty(m.Target, InvalidationKind.Style | InvalidationKind.Paint);
                    } else {
                        MarkDirty(m.Target, InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint);
                    }
                    // `:has(...[attr=v])` ancestor invalidation. A child's
                    // attribute change can flip the match result of an
                    // ancestor selector that uses `:has()`. The ChildAdded/
                    // ChildRemoved branches already walk ancestors when
                    // `hasSensitive` is set; the attribute branches
                    // previously only marked the target, leaving stale
                    // styles on ancestors whose :has() keyed off the
                    // changed attribute.
                    if (hasSensitive) {
                        for (var a = m.Target?.Parent; a != null; a = a.Parent) {
                            MarkDirty(a, InvalidationKind.Style);
                        }
                    }
                    break;
                }

                case DomMutationKind.TextChanged:
                    // Text nodes do not own boxes in the element->box cache.
                    // Dirtiness belongs to the nearest element that contains
                    // the inline formatting context. Full ancestor bubbling is
                    // handled by layout's geometry-change fallback, so stable
                    // counters can relayout locally instead of poisoning the
                    // whole document every frame.
                    if (m.Target?.Parent is Element parent) {
                        MarkDirty(parent, InvalidationKind.Layout | InvalidationKind.Paint);
                    }
                    break;
            }
        }
    }
}
