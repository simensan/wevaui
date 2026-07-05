using System.Collections.Generic;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Layout.Boxes;
using Weva.Layout.Containment;
using Weva.Layout.Floats;
using Weva.Layout.Positioning;

namespace Weva.Layout {
    internal sealed class BlockLayout {
        // Pass-reuse invariant: a BlockLayout is constructed once per LayoutEngine
        // and reused across every Layout call. The InlineLayout reference is wired
        // once at engine-construction time (forms a cycle with InlineLayout); the
        // LayoutScratch is engine-stable. Only the LayoutContext changes per call,
        // and Reset() is the only state-mutation point — there are no per-call
        // List<>/Dictionary<>/Stack<> fields owned by BlockLayout to clear.
        LayoutContext ctx;
        readonly InlineLayout inline;
        readonly LayoutScratch scratch;

        // CSS 2.1 §9.5 float context for the current BFC. BlockLayout pushes a
        // new context whenever it enters a box that establishes a new BFC (root
        // box, overflow != visible, flow-root, float, inline-block, ...) and
        // restores the previous one on exit. The InlineLayout consults it via
        // CurrentFloats so line boxes can wrap around active floats.
        FloatContext currentFloats;
        readonly Stack<FloatContext> floatContextPool = new(8);
        readonly Dictionary<BoxSidesCacheKey, ResolvedSides> boxSidesCache = new(128);
        readonly Dictionary<BorderCacheKey, ResolvedSides> borderCache = new(128);
        // BFC-local origin of the currently-laying-out container, used to
        // translate child Y coords into BFC-local Y for float queries.
        double bfcOriginX;
        double bfcOriginY;

        // Exposed to InlineLayout for line-box width adjustments. Null when
        // no BFC has been entered yet (rare — the root always establishes one).
        internal FloatContext CurrentFloats => currentFloats;
        internal double BfcOriginX => bfcOriginX;
        internal double BfcOriginY => bfcOriginY;

        public BlockLayout(InlineLayout inline, LayoutScratch scratch) {
            this.inline = inline;
            this.scratch = scratch;
        }

        public void Reset(LayoutContext ctx) {
            this.ctx = ctx;
            this.currentFloats = null;
            this.bfcOriginX = 0;
            this.bfcOriginY = 0;
            boxSidesCache.Clear();
            borderCache.Clear();
        }

        FloatContext RentFloatContext() {
            var fc = floatContextPool.Count > 0 ? floatContextPool.Pop() : new FloatContext();
            fc.Clear();
            return fc;
        }

        void ReturnFloatContext(FloatContext fc) {
            fc.Clear();
            floatContextPool.Push(fc);
        }

        public void LayoutRoot(BlockBox root, double viewportWidth, double viewportHeight) {
            // Stamp Position + ZIndex on every box before we read them below.
            // Without this, the very first BlockLayout call sees `box.Position
            // == Static` on every abs-pos box (PositioningPass.Run is the
            // next-and-only stamper, but it runs AFTER BlockLayout / Flex /
            // Grid), so abs-pos children leak their height into their
            // parent's content extent. Example: `::before { position:
            // absolute }` on a list item stacked its line-box height on
            // top of the LI's own text line, doubling the LI's height.
            // We stamp here (not in LayoutEngine) so every layout entry
            // point — including incremental re-layout triggered by scroll
            // / hover / animation — gets a consistent first read.
            Weva.Layout.Positioning.PositioningPass.Stamp(root);
            root.X = 0;
            root.Y = 0;
            root.Width = viewportWidth;
            // Seed the synthetic root with the viewport height so descendants
            // resolving `height: %` (e.g. `html, body { height: 100% }`) chain
            // viewport → html → body correctly. Without this, percent heights
            // fall through to content-height because the percent basis is null
            // and FinalizeBlockSize ignores LengthKind.Percent results — body
            // then grows to fit overflowing content instead of clamping to the
            // viewport. See FinalizeBlockSize for the symmetric fix.
            root.Height = viewportHeight;
            LayoutContent(root, ctx.RootFontSizePx, viewportWidth, parentStyle: null);
        }

        public void LayoutBlock(BlockBox box, double availableWidth, ComputedStyle parentStyle) {
            double fs;
            if (box.Style != null) {
                // CSS Sizing L3 §5.1: fit-content(<length-percentage>) on a
                // block-level element requires probing min-content / max-content
                // (like float shrink-to-fit). Detect this BEFORE ApplyBoxModel
                // so we can branch to the probe path when needed.
                // Skip for inline-block boxes — InlineLayout.MakeAtomItem
                // manages the probe cycle for inline-block atoms itself.
                var widthParsed = box.Style.GetParsed(CssProperties.WidthId);
                if (!box.IsInlineBlock && widthParsed is CssFunctionCall wfc && wfc.Name == "fit-content") {
                    LayoutFitContentBlock(box, availableWidth, parentStyle, wfc);
                    return;
                }
                fs = ApplyBoxModel(box, availableWidth, parentStyle);
            } else {
                box.Width = availableWidth;
                fs = ctx.RootFontSizePx;
            }
            if (box.ChildList.Count == 0) {
                FinalizeBlockSize(box, fs, parentStyle, box.PaddingTop + box.BorderTop);
                return;
            }
            LayoutContent(box, fs, availableWidth, parentStyle);
        }

        // CSS Sizing L3 §5.1: fit-content(<length-percentage>) on a block-level
        // element. Semantics: min(max-content, max(min-content, arg)).
        // Mirrors LayoutFloatBox's shrink-to-fit probe approach.
        void LayoutFitContentBlock(BlockBox box, double availableWidth, ComputedStyle parentStyle, CssFunctionCall fitFn) {
            // First run ApplyBoxModel with a large probe width so padding/border/
            // margin are resolved before we read the frame size.
            double fs = ApplyBoxModel(box, availableWidth, parentStyle);

            // Resolve the argument from the function call.
            double argPx;
            var argR = StyleResolver.ResolveLengthFromParsed(fitFn, ctx, fs, availableWidth);
            argPx = argR.Kind == StyleResolver.LengthKind.FitContent ? argR.Pixels : availableWidth;
            if (argPx < 0) argPx = 0;

            if (box.ChildList.Count == 0) {
                // Empty box: min/max-content are both the frame, so
                // fitted = min(frame, max(frame, argPx)) = frame (clamped).
                FinalizeBlockSize(box, fs, parentStyle, box.PaddingTop + box.BorderTop);
                return;
            }

            double frame = box.PaddingLeft + box.PaddingRight + box.BorderLeft + box.BorderRight;

            // Probe 1: max-content — lay out at a very wide width so nothing wraps.
            RelayoutContentAt(box, 1_000_000);
            double maxContent = PositioningPass.MaxContentWidth(box, ctx, fs) + frame;
            if (maxContent < frame) maxContent = frame;

            // Probe 2: min-content — lay out at width=1 to force maximum wrapping.
            RelayoutContentAt(box, 1);
            double minContent = PositioningPass.MaxContentWidth(box, ctx, fs) + frame;
            if (minContent < frame) minContent = frame;

            // CSS Sizing L3 §5.1: fit-content(arg) = min(max-content, max(min-content, arg)).
            double fitted = System.Math.Min(maxContent, System.Math.Max(minContent, argPx));
            if (fitted < 0) fitted = 0;

            // FC-1: CSS Sizing L3 §4 requires min-width / max-width to constrain
            // the computed width AFTER the fit-content intrinsic calculation.
            // ApplyBoxModel above bailed out on width (the value is the
            // fit-content function call, not a length) so it never applied
            // the min/max clamps. Apply them here with the same box-sizing
            // and percent-resolution semantics as the regular width path.
            bool borderBox = IsBorderBox(box.Style);
            var maxR = StyleResolver.ResolveLengthFromParsed(
                box.Style.GetParsed(CssProperties.MaxWidthId), ctx, fs, availableWidth);
            var minR = StyleResolver.ResolveLengthFromParsed(
                box.Style.GetParsed(CssProperties.MinWidthId), ctx, fs, availableWidth);
            if (maxR.Kind == StyleResolver.LengthKind.Length) {
                double maxPx = borderBox ? maxR.Pixels : maxR.Pixels + frame;
                if (fitted > maxPx) fitted = maxPx;
            } else if (maxR.Kind == StyleResolver.LengthKind.Percent) {
                double mp = availableWidth * (maxR.Percent * 0.01);
                if (!borderBox) mp += frame;
                if (fitted > mp) fitted = mp;
            }
            if (minR.Kind == StyleResolver.LengthKind.Length) {
                double minPx = borderBox ? minR.Pixels : minR.Pixels + frame;
                if (fitted < minPx) fitted = minPx;
            } else if (minR.Kind == StyleResolver.LengthKind.Percent) {
                double mp = availableWidth * (minR.Percent * 0.01);
                if (!borderBox) mp += frame;
                if (fitted < mp) fitted = mp;
            }
            if (fitted < 0) fitted = 0;

            // Settle at the computed width and do the final layout.
            RelayoutContentAt(box, fitted);
        }

        // Re-lays out the content of a box at a specific border-box width, used
        // by inline-block shrink-to-fit. Box-model-derived padding/border/margin
        // values stay; only the width and the resulting content/height change.
        public void RelayoutContentAt(BlockBox box, double width) {
            // Unwrap LineBox residue from a prior layout so the inline-formatting
            // path can re-collect raw children. InlineLayout.CollectInline only
            // recognises TextRun / InlineBox / BlockBox — a LineBox sitting in
            // place of those (left over from the previous LayoutContent call)
            // makes CollectInline harvest zero items and produce empty lines.
            // Mirrors the snapshot/restore pattern InlineLayout.MakeAtomItem
            // uses for inline-block shrink-to-fit.
            UnwrapLineBoxes(box);
            box.Width = width;
            double fs = FontSize(box, box.Parent?.Style);
            LayoutContent(box, fs, width, box.Parent?.Style);
        }

        void UnwrapLineBoxes(BlockBox box) {
            // LY4: never unwrap frozen scroll-graft content. LayoutContent
            // early-returns at a ReuseContent container, so lines unwrapped
            // at or below it would NEVER be re-laid — a shrink-to-fit probe
            // on an abs-pos wrapper AROUND a grafted scroll list (the classic
            // scrollable dropdown) shredded the list's text layout into raw
            // coalesced TextRuns with stale line-relative geometry. The
            // graft's content is width-stable by validation, so its line
            // layout is already correct for any width the probe resolves.
            if (box.ReuseContent) return;
            var children = box.ChildList;
            if (box.ContainsInlines && children.Count > 0 && children[0] is Weva.Layout.Boxes.LineBox) {
                var raw = scratch.RentBoxView();
                // Inline decoration shells (InlineBox per inline element, e.g.
                // `<code>`) appended to each LineBox by AttachInlineFragmentsTo-
                // Lines. We keep the first shell per element and re-wrap its
                // runs into it below. Dropping them outright (the old behaviour)
                // loses the only tree representation of an inline element backed
                // solely by bare text runs: the next CollectInline sweep sees
                // just the TextRun and never re-creates the shell, so the
                // element's background / border silently disappears after the
                // first flex / shrink-to-fit re-layout (weva-landing inline
                // `<code>` pills painted no background).
                var shells = scratch.RentBoxView();
                for (int childIndex = 0; childIndex < children.Count; childIndex++) {
                    var ln = children[childIndex];
                    if (ln is Weva.Layout.Boxes.LineBox lb) {
                        for (int j = 0; j < lb.Children.Count; j++) {
                            var c = lb.Children[j];
                            if (c is Weva.Layout.Boxes.InlineBox ib) {
                                if (ib.Element != null && FindShellFor(shells, ib.Element) == null) {
                                    ib.ClearChildren();
                                    shells.Add(ib);
                                }
                                continue;
                            }
                            // raw[] holds actual content (TextRun / inline-block
                            // atom) so unwrap-then-re-collect observes the same
                            // item stream the first pass saw.
                            raw.Add(c);
                        }
                    } else {
                        raw.Add(ln);
                    }
                }
                // Coalesce consecutive TextRun fragments that share the same
                // non-null SourceNode back to the source TextNode's full data.
                // LineBreaker emits one TextRun per token (word/space) and drops
                // the trailing inter-word space when a line wraps via
                // TrimTrailingSpace. On re-layout — shrink-to-fit always probes
                // min-content at width=1, which always wraps — the dropped space
                // is gone, so the next pass at the resolved width sees
                // [word, word] with no space between them instead of
                // [word, " ", word]. Reconstructing from SourceNode.Data
                // restores whatever the HTML actually said.
                var merged = scratch.RentBoxView();
                int i = 0;
                while (i < raw.Count) {
                    if (raw[i] is Weva.Layout.Boxes.TextRun tr && tr.SourceNode != null) {
                        var src = tr.SourceNode;
                        int j = i + 1;
                        while (j < raw.Count && raw[j] is Weva.Layout.Boxes.TextRun tr2 && ReferenceEquals(tr2.SourceNode, src)) j++;
                        tr.Text = src.Data ?? "";
                        merged.Add(tr);
                        i = j;
                    } else {
                        merged.Add(raw[i]);
                        i++;
                    }
                }
                // Re-wrap each run that belongs to an inline decoration shell
                // back into that shell, reconstructing `InlineBox > [runs]` in
                // source order. CollectInline then recurses into the shell
                // (re-registering it for AttachInlineFragmentsToLines) so the
                // element's background / border / radius paints again. Runs with
                // no matching shell (the block's own direct text) are added
                // straight to the block as before. The shell is emitted at its
                // first run's position to preserve source order.
                box.ClearChildren();
                for (int mergedIndex = 0; mergedIndex < merged.Count; mergedIndex++) {
                    var item = merged[mergedIndex];
                    Weva.Layout.Boxes.InlineBox shell = null;
                    if (shells.Count > 0 && item is Weva.Layout.Boxes.TextRun trm && trm.Element != null) {
                        shell = FindShellFor(shells, trm.Element);
                    }
                    if (shell != null) {
                        if (shell.ChildList.Count == 0) box.AddChild(shell);
                        shell.AddChild(item);
                    } else {
                        box.AddChild(item);
                    }
                }
                scratch.ReturnBoxView(merged);
                scratch.ReturnBoxView(shells);
                scratch.ReturnBoxView(raw);
            }
            for (int i = 0; i < children.Count; i++) {
                var c = children[i];
                if (c is BlockBox bb) UnwrapLineBoxes(bb);
            }
        }

        // Linear scan (inline elements per block are few) for the decoration
        // shell whose Element matches `el`. Returns null when none is held.
        static Weva.Layout.Boxes.InlineBox FindShellFor(List<Box> shells, Weva.Dom.Element el) {
            for (int i = 0; i < shells.Count; i++) {
                if (shells[i] is Weva.Layout.Boxes.InlineBox ib && ReferenceEquals(ib.Element, el)) return ib;
            }
            return null;
        }

        double FontSize(Box box, ComputedStyle parentStyle) {
            if (box.Style != null) return StyleResolver.FontSizePx(box.Style, parentStyle, ctx);
            return ctx.RootFontSizePx;
        }

        void LayoutContent(BlockBox box, double fontSize, double containingBlockWidth, ComputedStyle parentStyle) {
            // Scroll-boundary content reuse: the grafted subtree from the prior
            // frame is already laid out in parent-relative coordinates, and a
            // scroll container's content is independent of the height it is
            // given, so there is nothing to recompute. The container box's own
            // outer geometry was already assigned by the caller (FinalizeBlock-
            // Size / the parent flex/grid/block pass). See Box.ReuseContent.
            if (box.ReuseContent) return;
            double contentW = box.ContentWidth;
            double topInner = box.PaddingTop + box.BorderTop;
            double leftInner = box.PaddingLeft + box.BorderLeft;
            bool hasInline = box.ContainsInlines;
            var children = box.ChildList;

            // CSS 2.1 §9.4.1: this box establishes a new BFC when overflow !=
            // visible, display: flow-root, or it's itself a float / inline-block
            // / flex / grid / table container. The synthetic document root also
            // establishes one. Push a fresh FloatContext; otherwise inherit the
            // ancestor BFC's context so directly-nested children participate in
            // the same float layout.
            bool establishesBfc = box.Style == null
                                  || MarginCollapsing.EstablishesNewBfc(box)
                                  || box.IsFloat
                                  || box.IsInlineBlock
                                  || box is Weva.Layout.Flex.FlexBox
                                  || box is Weva.Layout.Grid.GridBox
                                  || box is Weva.Layout.Tables.TableBox;
            var prevFloats = currentFloats;
            double prevBfcX = bfcOriginX;
            double prevBfcY = bfcOriginY;
            FloatContext rentedFloats = null;
            if (establishesBfc) {
                rentedFloats = RentFloatContext();
                currentFloats = rentedFloats;
                bfcOriginX = 0;
                bfcOriginY = 0;
            } else {
                // Translate the float-context origin from the parent BFC into
                // this box's local coords. box.X / box.Y in our convention are
                // local-to-parent; the parent's bfcOriginX/Y already says where
                // the parent sits in BFC coords, so the BFC origin for *us* is
                // -(parentBfcX + box.X). Floats added by our children will be
                // recorded in BFC-local space, and queries from within our
                // content (left/right intrusion at a child Y) need the same
                // translation. We store the BFC origin in our own local frame:
                // a BFC-local Y equals our-local Y minus bfcOriginY. (The
                // negation is implicit in how we apply it below.)
                bfcOriginX = prevBfcX + box.X;
                bfcOriginY = prevBfcY + box.Y;
            }

            try {

            if (hasInline) {
                // CSS 2.1 §9.5 / §9.7 — a float nested inside an inline
                // ancestor (e.g. `<span><span style=float:left></span>`)
                // is blockified by BoxBuilder but, without intervention,
                // would remain a child of the InlineBox span; the float
                // pre-scan below only iterates `box.Children` and would
                // never see it, leaving .Float un-stamped and PlaceFloat
                // un-called. Per spec the float's containing block is
                // the nearest block container ancestor (this box), so we
                // hoist any blockified-float descendants out of inline
                // parents into `box.Children` BEFORE the float scan. The
                // inline pass already has `if (bb.IsFloat) continue` skip
                // logic in CollectInline; removing the box from the inline
                // tree merely makes that skip redundant for the hoisted
                // float, which is fine — the surrounding "before" / "after"
                // text gets concatenated contiguously and the per-line
                // FloatContext probe still keeps line boxes off the float
                // margin box.
                var hoistedFloats = scratch.RentBlockBoxView();
                try {
                    HoistInlineFloats(box, hoistedFloats);
                    // CSS 2.1 §9.5 — scan the inline-flow children for floats
                    // FIRST. Floats are removed from the inline stream and laid
                    // out via PlaceFloat against the current float context;
                    // they nominally sit at the top of the inline-content area
                    // (BFC-local cursor = topInner). The line breaker's per-
                    // line probe then sees their intrusion when laying out the
                    // surrounding text. We don't physically reorder the box
                    // children: LayoutInline skips floats (via IsFloat check
                    // in CollectInline) and BlockLayout's outer post-walk
                    // doesn't need to reposition them (PlaceFloat already
                    // wrote their final X/Y).
                    for (int i = 0; i < children.Count; i++) {
                        var child = children[i];
                        if (child is BlockBox cb) {
                            cb.Float = cb.ReadFloatType();
                            cb.Clear = cb.ReadClearType();
                            if (cb.IsFloat) {
                                LayoutFloatBox(cb, contentW);
                                PlaceFloat(box, cb, topInner, contentW);
                            }
                        }
                    }
                    // Inline content: lines wrap around any active floats in the
                    // current BFC. We pass the float-context-aware width helper
                    // through to InlineLayout so each produced LineBox is laid out
                    // against the per-line available width.
                    inline.LayoutInline(box, contentW, ctx, currentFloats, bfcOriginX + leftInner, bfcOriginY + topInner);
                    // Re-attach hoisted floats. LayoutInline calls ClearChildren
                    // and rebuilds the children list from LineBoxes + inline-
                    // split blocks it collected via CollectInline — it never
                    // saw the hoisted floats (they live outside the inline
                    // tree by the time CollectInline runs), so they need to be
                    // reinserted now. Their X/Y/Width/Height were already
                    // written by PlaceFloat in the pre-pass; appending here
                    // keeps them discoverable to FindById / paint / hit-test
                    // walks without changing their placement. The post-walk
                    // below sees IsFloat == true and skips them, so the cursor
                    // arithmetic isn't disturbed.
                    for (int i = 0; i < hoistedFloats.Count; i++) {
                        box.AddChild(hoistedFloats[i]);
                    }
                } finally {
                    scratch.ReturnBlockBoxView(hoistedFloats);
                }
                double maxBottom = topInner;
                double cursorY = topInner;
                for (int i = 0; i < children.Count; i++) {
                    var line = children[i];
                    if (line is BlockBox childBlock && !(line is LineBox)) {
                        // CSS 2.1 §9.5: floats were already placed by the
                        // pre-pass above (PlaceFloat wrote their final X/Y
                        // and added them to the float context). Don't
                        // touch them here — the post-pass cursor walk would
                        // otherwise stomp their carefully-computed Y back
                        // to the inline-flow cursor and pull them off
                        // their float row.
                        if (childBlock.IsFloat) continue;
                        // CSS 2.1 §9.2.1.1: out-of-flow boxes (position:absolute|fixed)
                        // that were re-attached by InlineLayout after the line rebuild
                        // must NOT advance cursorY (they don't occupy in-flow space)
                        // and must NOT have their X/Y overwritten here — PositioningPass
                        // is responsible for placing them against their containing block.
                        if (childBlock.Position == Weva.Layout.Positioning.PositionType.Absolute
                            || childBlock.Position == Weva.Layout.Positioning.PositionType.Fixed) continue;
                        // Inline-splitting placed a real block child here per
                        // CSS Display Module Level 3 §2. Honor its margins on
                        // the block axis; v1 doesn't collapse them with the
                        // surrounding inline flow.
                        cursorY += childBlock.MarginTop;
                        childBlock.X = box.PaddingLeft + box.BorderLeft + childBlock.MarginLeft;
                        childBlock.Y = cursorY;
                        cursorY += childBlock.Height + childBlock.MarginBottom;
                        if (childBlock.Y + childBlock.Height > maxBottom) maxBottom = childBlock.Y + childBlock.Height;
                        continue;
                    }
                    // line.X starts at any left-float intrusion that
                    // LineBreaker stamped on it (LineBox.X = state.LineLeftOffset
                    // — see LineBreaker.FinishLine). Add the container's
                    // inner-left padding/border so the result is local to
                    // the container's box origin. When no float intrudes
                    // (LineLeftOffset == 0) this collapses to the original
                    // pre-float behaviour.
                    line.X = box.PaddingLeft + box.BorderLeft + line.X;
                    line.Y = cursorY;
                    cursorY += line.Height;
                    if (line.Y + line.Height > maxBottom) maxBottom = line.Y + line.Height;
                }
                // CSS 2.1 §10.6.7: when this box establishes a BFC, its height
                // grows to enclose floats it contains. The float bottoms recorded
                // by InlineLayout / nested float calls live in BFC-local Y; for
                // the BFC-root box that's the same as box-local Y, so we can
                // compare directly.
                if (establishesBfc && currentFloats != null) {
                    double floatBottom = currentFloats.MaxBottom();
                    if (floatBottom > maxBottom) maxBottom = floatBottom;
                }
                FinalizeBlockSize(box, fontSize, parentStyle, maxBottom);
                return;
            }

            // Block-flow children with margin collapsing per CSS Box Model §8.3.1.
            // We collect collapsible block children into an ordered list, then for
            // each adjacent pair compute the collapsed margin between them. The
            // first child's top margin can collapse INTO the parent's top margin
            // (when the parent's top is open — no padding/border at the top); the
            // last child's bottom margin can collapse INTO the parent's bottom
            // margin (when the parent's bottom is open AND parent height is auto).

            // Stack-discipline: BlockInflow is a shared scratch that BlockLayout
            // also uses recursively (LayoutBlock above re-enters LayoutContent).
            // Snapshot the count on entry, append our own children, then restore
            // to the snapshot on exit so a parent's inflowChildren range survives
            // its descendants' borrowed slots.
            var sharedInflow = scratch.BlockInflow;
            int inflowStart = sharedInflow.Count;
            // CSS 2.1 §9.5 — float Y placement needs to happen BEFORE we
            // call LayoutBlock on subsequent in-flow children: an in-flow
            // paragraph's inline content reads the parent's float context
            // during its own LayoutInline call to wrap text around floats.
            // We track an "approximate cursor" that advances past each
            // in-flow child's margin-box height; floats consult it to
            // compute their placement Y. The approximation ignores margin
            // collapsing precision (the placement loop below recomputes
            // the exact Y for each in-flow box), which is acceptable
            // because floats in CSS 2.1 don't require pixel-exact
            // pre-placement — they shift downward when they don't fit.
            double approxCursor = topInner;
            for (int i = 0; i < children.Count; i++) {
                var child = children[i];
                if (child is BlockBox cb) {
                    // CSS 2.1 §9.5 — stamp the float / clear cascade values
                    // onto the block box BEFORE we lay it out. The float-aware
                    // placement loop below reads cb.Float / cb.Clear; LayoutBlock
                    // itself only consults the IsFloat predicate (to skip the
                    // auto-margin centering rule for floats per §9.5.1 — a float
                    // ignores `margin: auto` on the inline axis).
                    cb.Float = cb.ReadFloatType();
                    cb.Clear = cb.ReadClearType();
                    // CSS Positioned Layout L3 §10.3.7: an OOF box with one
                    // horizontal pin and `width:auto` is shrink-to-fit. The
                    // PositioningPass resolves that width and stamps the
                    // result on ShrinkFitCachedWidth/Avail. Re-running
                    // LayoutBlock on this child during a 2nd flex/grid pass
                    // (RelayoutContentAt re-entry) would stomp the shrink
                    // width back to `contentW = avail` via ApplyBoxModel —
                    // visible on .aura .t (`right: 1px`, width:auto): width
                    // collapses from intrinsic max-content (~6-10px) back
                    // to the grid-cell content width (~15.74px). Skip the
                    // LayoutBlock here for that specific case so the
                    // PositioningPass-stamped width survives. Horizpinned
                    // (both edges set) OOF boxes still re-flow through here
                    // because their width tracks cb.Width changes between
                    // passes, and shrink-to-fit was never applied to them.
                    bool oof = cb.Position == Weva.Layout.Positioning.PositionType.Absolute
                            || cb.Position == Weva.Layout.Positioning.PositionType.Fixed;
                    bool horizPinnedBoth = cb.OffsetLeft.HasValue && cb.OffsetRight.HasValue;
                    bool shrinkApplied = oof && !horizPinnedBoth && cb.ShrinkFitCachedWidth >= 0;
                    if (shrinkApplied) {
                        sharedInflow.Add(cb);
                        continue;
                    }
                    if (cb.IsFloat) {
                        // CSS 2.1 §9.5.1: a float that has `width: auto`
                        // shrinks to fit (max-content clamped by available
                        // width). LayoutBlock with width:auto would otherwise
                        // give the float its containing block's content
                        // width — which makes the float fill the whole line
                        // and defeats its purpose. Probe intrinsic widths
                        // mirroring the abs-pos shrink-to-fit branch in
                        // PositioningPass.ApplyAbsoluteAgainst.
                        LayoutFloatBox(cb, contentW);
                        // Place the float now (against the running approximate
                        // cursor) so the float context is populated BEFORE
                        // we call LayoutBlock on any subsequent in-flow
                        // sibling — the sibling's inline content reads the
                        // float context inside its own LayoutContent call.
                        // The exact Y is computed in the placement loop
                        // below by re-placing the float, but the float
                        // context's Entry is set once HERE and stays.
                        if (currentFloats != null) {
                            PlaceFloat(box, cb, approxCursor, contentW);
                        }
                    } else {
                        // CSS 2.1 §9.5: descendants of `cb` may consult
                        // currentFloats during their own LayoutInline call
                        // and need a meaningful BFC-local Y. The exact Y
                        // isn't known until the placement loop (margin
                        // collapsing decides it), but `approxCursor` is a
                        // close estimate. Stamp it onto cb.Y BEFORE
                        // recursing so the BFC origin computation inside
                        // LayoutContent picks it up. The placement loop
                        // below resets cb.Y to the exact value; the cost
                        // of any tiny mismatch is at most one float-row
                        // misplacement on text-wrap (which Chrome itself
                        // hits in pathological negative-margin cases).
                        cb.Y = approxCursor + cb.MarginTop;
                        LayoutBlock(cb, contentW, box.Style);
                        // Advance approxCursor by the in-flow child's full
                        // margin-box height — this is just an estimate; the
                        // placement loop recomputes the exact Y including
                        // margin collapsing. Floats that follow this child
                        // will use the estimate, which is correct under the
                        // common case (no negative margins between siblings).
                        approxCursor += cb.MarginTop + cb.Height + cb.MarginBottom;
                    }
                    sharedInflow.Add(cb);
                }
            }
            int inflowEnd = sharedInflow.Count;
            int inflowCount = inflowEnd - inflowStart;

            if (inflowCount == 0) {
                // Nothing to pop (we appended zero); still collapse to start in
                // case some non-block child temporarily borrowed slots — defensive.
                sharedInflow.RemoveRange(inflowStart, sharedInflow.Count - inflowStart);
                FinalizeBlockSize(box, fontSize, parentStyle, topInner);
                return;
            }

            // The document root box (Style == null, no real margins to collapse
            // with) does not participate in collapsing — it is the viewport edge.
            // Anonymous blocks created by BoxBuilder for inline runs have zero
            // margins, so collapsing through them is a no-op anyway.
            bool parentParticipates = box.Style != null && MarginCollapsing.ParticipatesInFlow(box);
            bool parentTopOpen = parentParticipates && MarginCollapsing.ParentTopOpen(box);
            bool parentBottomOpen = parentParticipates && MarginCollapsing.ParentBottomOpen(box) && MarginCollapsing.ParentHeightAuto(box, ctx, fontSize);

            double cursor = topInner;

            // CSS 2.1 §8.3.1: across a chain of N adjoining collapsing margins,
            // the resulting margin is `max(positives) + min(negatives)`. Folding
            // pairwise via Collapse(a,b) is associative for same-sign chains but
            // produces wrong results for mixed-sign chains of length > 2 (e.g.
            // {+20, -15, +10, -25} folds left to -10 but the spec gives -5).
            // Track the running max/min over the active chain instead and combine
            // once when the chain closes (a non-self-collapsing child is placed,
            // or layout reaches the bottom edge).
            double chainMaxPos = 0;
            double chainMinNeg = 0;
            // The leading chain's resolved margin attaches to box.MarginTop when
            // the parent's top is open. Otherwise it sits as a literal edge gap
            // before the first placed child.
            bool chainAttachesToParentTop = parentTopOpen;
            if (parentTopOpen) {
                if (box.MarginTop > 0) chainMaxPos = box.MarginTop;
                else if (box.MarginTop < 0) chainMinNeg = box.MarginTop;
            }
            bool firstCollapsibleSeen = false;

            for (int i = 0; i < inflowCount; i++) {
                var cb = sharedInflow[inflowStart + i];
                // CSS 2.1 §9.5: floats are removed from the in-flow cursor.
                // They place at the leading/trailing edge of the containing
                // block at the CURRENT cursor Y (margin-top applied) but
                // the cursor does NOT advance past them — subsequent in-flow
                // siblings keep their pre-float Y. Floats DO close the
                // current margin-collapse chain (the chain's margin lands
                // at the float's top, then the chain resets), per CSS 2.1
                // §8.3.1 rule 5.
                if (cb.IsFloat) {
                    // Float was already laid out + placed in the pre-collect
                    // loop above (so the float context is populated before
                    // any subsequent in-flow sibling lays out its inline
                    // content). Per CSS 2.1 §8.3.1 rule 5 the float does NOT
                    // participate in the surrounding margin-collapse chain;
                    // the chain continues through to the next in-flow box
                    // as if the float weren't here. No cursor advance.
                    firstCollapsibleSeen = true;
                    continue;
                }
                // CSS 2.1 §9.5.2: `clear` on an in-flow block pushes its
                // TOP-MARGIN edge below the bottom of any earlier float on
                // the cleared side(s) — even if margin collapsing would
                // otherwise place the block higher. Implementation: bump
                // `cursor` up to the clearance line before the normal
                // placement code runs. The collapse chain stays intact;
                // its accumulated margin still applies on top of the
                // cleared cursor.
                if (cb.Clear != ClearType.None && currentFloats != null) {
                    double clearBottomBfc = currentFloats.ClearBottom(cb.Clear);
                    // Translate BFC-local clearBottom to box-local Y.
                    double clearBottomLocal = clearBottomBfc - bfcOriginY;
                    // Cursor + chain represents the would-be top-margin-edge.
                    double wouldBeTopMargin = cursor + chainMaxPos + chainMinNeg;
                    if (clearBottomLocal > wouldBeTopMargin) {
                        // Bump cursor up to land the top-margin-edge at the
                        // clear line. Reset the chain (it's been spent on
                        // moving the box, not contributing to margin) per
                        // the introduced-clearance rule.
                        if (chainAttachesToParentTop) {
                            box.MarginTop = chainMaxPos + chainMinNeg;
                            chainAttachesToParentTop = false;
                        }
                        cursor = clearBottomLocal;
                        chainMaxPos = 0;
                        chainMinNeg = 0;
                    }
                }
                // Out-of-flow children (absolute / fixed) don't collapse with
                // siblings — their margins apply verbatim. We still advance the
                // cursor to preserve the BlockLayout-pre-positioning convention
                // that PositioningPass.CompressOutOfFlow expects (it later shifts
                // following siblings up by the removed child's margin-box height).
                if (MarginCollapsing.IsOutOfFlow(cb)) {
                    // Skip Y-rewrite when PositioningPass has already placed
                    // this OOF child via inset/top/bottom (typed Position is
                    // populated; OffsetTop or OffsetBottom is set). Pass-1
                    // BlockLayout still falls into the original path because
                    // Position is Static and Offsets are null until
                    // PositioningPass runs. RelayoutContentAt re-enters here
                    // during pass-2 flex/grid reflows of an OOF child's
                    // ancestor (e.g. unit-meta column-flex reflow recursing
                    // into .bar > .label with `position:absolute; inset:0`);
                    // resetting Y back to the in-flow cursor would relocate
                    // the label below its bar instead of pinned at the bar's
                    // top edge as PositioningPass had just resolved.
                    bool placedByPositioning = (cb.Position == Weva.Layout.Positioning.PositionType.Absolute
                                                || cb.Position == Weva.Layout.Positioning.PositionType.Fixed)
                        && (cb.OffsetTop.HasValue || cb.OffsetBottom.HasValue
                            || cb.OffsetLeft.HasValue || cb.OffsetRight.HasValue);
                    if (placedByPositioning) {
                        chainMaxPos = 0;
                        chainMinNeg = 0;
                        continue;
                    }
                    double oofGap = chainMaxPos + chainMinNeg;
                    if (chainAttachesToParentTop) {
                        box.MarginTop = oofGap;
                        oofGap = 0;
                        chainAttachesToParentTop = false;
                    }
                    cb.X = box.PaddingLeft + box.BorderLeft + cb.MarginLeft;
                    cb.Y = cursor + oofGap + cb.MarginTop;
                    cursor = cb.Y + cb.Height + cb.MarginBottom;
                    chainMaxPos = 0;
                    chainMinNeg = 0;
                    firstCollapsibleSeen = true;
                    continue;
                }

                if (cb.IsInlineBlock) {
                    // Inline-block at block level participates in flow but its
                    // margins do NOT collapse — they apply verbatim.
                    double ibGap = chainMaxPos + chainMinNeg;
                    if (chainAttachesToParentTop) {
                        box.MarginTop = ibGap;
                        ibGap = 0;
                        chainAttachesToParentTop = false;
                    }
                    cb.X = box.PaddingLeft + box.BorderLeft + cb.MarginLeft;
                    cb.Y = cursor + ibGap + cb.MarginTop;
                    cursor = cb.Y + cb.Height + cb.MarginBottom;
                    chainMaxPos = 0;
                    chainMinNeg = 0;
                    firstCollapsibleSeen = true;
                    continue;
                }

                double childTop = cb.MarginTop;
                double childBottom = cb.MarginBottom;

                // Fold this child's margin-top into the active chain.
                if (childTop > chainMaxPos) chainMaxPos = childTop;
                if (childTop < chainMinNeg) chainMinNeg = childTop;

                // Self-collapsing block: top and bottom margins both join the
                // active chain and the block contributes zero height to flow.
                if (MarginCollapsing.IsSelfCollapsing(cb, ctx, fontSize)) {
                    if (childBottom > chainMaxPos) chainMaxPos = childBottom;
                    if (childBottom < chainMinNeg) chainMinNeg = childBottom;
                    // Position is best-effort: anchor it where the chain currently
                    // sits so DOM-walk consumers see a sane Y. Cursor unchanged.
                    cb.X = box.PaddingLeft + box.BorderLeft + cb.MarginLeft;
                    if (chainAttachesToParentTop) {
                        cb.Y = cursor;
                    } else {
                        cb.Y = cursor + chainMaxPos + chainMinNeg;
                    }
                    firstCollapsibleSeen = true;
                    continue;
                }

                // Closing the chain: realize the collapsed gap and place the child.
                double gap = chainMaxPos + chainMinNeg;
                if (chainAttachesToParentTop) {
                    // Combined margin lives OUTSIDE the parent on box.MarginTop;
                    // child sits flush at the inner edge.
                    box.MarginTop = gap;
                    cb.Y = cursor;
                    chainAttachesToParentTop = false;
                } else {
                    cb.Y = cursor + gap;
                }

                cb.X = box.PaddingLeft + box.BorderLeft + cb.MarginLeft;

                cursor = cb.Y + cb.Height;
                // Seed the next chain with this child's bottom margin.
                chainMaxPos = childBottom > 0 ? childBottom : 0;
                chainMinNeg = childBottom < 0 ? childBottom : 0;
                firstCollapsibleSeen = true;
            }

            // Last-chain disposition: if the parent's bottom is open and height
            // is auto, the trailing chain collapses into box.MarginBottom (and
            // the content bottom stops at `cursor`). Otherwise the chain sits
            // as a literal gap that pushes the content bottom down.
            double trailingGap = chainMaxPos + chainMinNeg;
            double contentBottom;
            if (parentBottomOpen && firstCollapsibleSeen) {
                if (chainAttachesToParentTop) {
                    // Edge case: every in-flow child was self-collapsing AND the
                    // parent-top was open, so the chain spans top→bottom of an
                    // empty-looking parent. Per spec, the parent's own margin-top
                    // and margin-bottom collapse together with the chain.
                    if (box.MarginBottom > chainMaxPos) chainMaxPos = box.MarginBottom;
                    if (box.MarginBottom < chainMinNeg) chainMinNeg = box.MarginBottom;
                    box.MarginTop = chainMaxPos + chainMinNeg;
                    box.MarginBottom = 0;
                } else {
                    if (box.MarginBottom > 0 && box.MarginBottom > chainMaxPos) chainMaxPos = box.MarginBottom;
                    if (box.MarginBottom < 0 && box.MarginBottom < chainMinNeg) chainMinNeg = box.MarginBottom;
                    box.MarginBottom = chainMaxPos + chainMinNeg;
                }
                contentBottom = cursor;
            } else {
                if (chainAttachesToParentTop) {
                    // Top chain never closed (no real children); emit it onto
                    // box.MarginTop. cursor stays at topInner.
                    box.MarginTop = trailingGap;
                    contentBottom = cursor;
                } else {
                    contentBottom = cursor + trailingGap;
                }
            }

            // Pop our slice off the shared inflow stack so the parent's range
            // (the one that called us via LayoutBlock) sees its slots intact.
            sharedInflow.RemoveRange(inflowStart, sharedInflow.Count - inflowStart);

            // CSS 2.1 §10.6.7: a BFC root grows to enclose any floats it
            // contains. Floats added to currentFloats by this box's
            // descendants live in BFC-local coords; since the BFC root is
            // this same box (when establishesBfc is true), BFC-local Y is
            // box-local Y and the comparison is direct.
            if (establishesBfc && currentFloats != null) {
                double floatBottom = currentFloats.MaxBottom();
                if (floatBottom > contentBottom) contentBottom = floatBottom;
            }

            FinalizeBlockSize(box, fontSize, parentStyle, contentBottom);
        } finally {
                if (rentedFloats != null) ReturnFloatContext(rentedFloats);
                currentFloats = prevFloats;
                bfcOriginX = prevBfcX;
                bfcOriginY = prevBfcY;
            }
        }

        void FinalizeBlockSize(BlockBox box, double fontSize, ComputedStyle parentStyle, double contentBottomY) {
            if (box.Style == null) {
                // Synthetic document root (Parent == null) is seeded with
                // viewportHeight by LayoutRoot so descendants resolving
                // `height: %` chain through it correctly during the top-down
                // walk. Once children are placed we must collapse back to
                // their actual bottom — otherwise the root reports the full
                // viewport height even when content is shorter (e.g. a page
                // of two 50/75-px divs would report 600 instead of 125).
                // For anonymous block wrappers (Parent != null, Style == null)
                // keep the prior shrink-only-when-zero behaviour: their
                // Height may have been pre-stamped by the inline-block /
                // shrink-to-fit machinery and we must not stomp it.
                if (box.Parent == null) {
                    box.Height = contentBottomY + box.PaddingBottom + box.BorderBottom;
                } else if (box.Height == 0) {
                    box.Height = contentBottomY + box.PaddingBottom + box.BorderBottom;
                }
                return;
            }
            // Percent heights (e.g. `body { height: 100% }`) resolve against
            // the parent's height per CSS 2.1 §10.5. LayoutRoot seeds the
            // synthetic root with viewportHeight so the chain html→viewport,
            // body→html, ... → all definite. Without a basis, percents
            // returned LengthKind.Percent and fell through to content-height,
            // which is why body grew to ~1472 instead of clamping to viewport.
            //
            // Absolute/fixed boxes have a containing block (nearest positioned
            // ancestor or viewport) that BlockLayout can't see yet — the
            // ancestor's Height is still 0 at this point in the top-down walk.
            // Defer percent resolution; PositioningPass.ApplyAbsoluteAgainst
            // resolves it against the real cb.Height. Without this, .cd-shade's
            // `height: 60%` was resolving against a stale parent.Height (or
            // ViewportHeightPx) and fixing at 60%×510 = 306px.
            //
            // For in-flow boxes: per CSS 2.1 §10.5 a percent height resolves
            // only when the containing block has a DEFINITE height. Parent
            // indefinite → percent computes to auto. Pass parent.Height when
            // > 0; null otherwise. Synthetic root is seeded with viewportHeight
            // by LayoutRoot so html→body chain works. Don't fall back to
            // viewport for arbitrary descendants (e.g. `.bar .fill { height: 100% }`
            // would resolve against viewport 781 when .bar is still sizing,
            // inflating fill to viewport height).
            var pos = box.ReadPositionType();
            bool isOOF = pos == PositionType.Absolute || pos == PositionType.Fixed;
            double? heightBasis = isOOF ? null : DefiniteContentHeight(box.Parent);
            // Per-style parsed cache: GetParsed yields the already-built
            // CssValue without re-running CssValue.TryParse on every read.
            // The (length+percentage+calc+auto) dispatch happens via
            // ResolveLengthFromParsed which mirrors the string overload's
            // semantics without the string→parse round-trip.
            var heightParsed = box.Style.GetParsed(CssProperties.HeightId);
            var heightR = StyleResolver.ResolveLengthFromParsed(heightParsed, ctx, fontSize, heightBasis);
            double explicitHeight = -1;
            if (heightR.Kind == StyleResolver.LengthKind.Length) explicitHeight = heightR.Pixels;

            // `box-sizing: content-box` (CSS default) means `height: 200px`
            // sets the content area; the rendered border-box height is
            // 200 + padding + border. With `border-box`, the value already IS
            // the border-box height, so the frame is included. Mirrors the
            // width-resolution branch in ApplyBoxModel.
            bool heightBorderBox = IsBorderBox(box.Style);
            double heightFrame = box.PaddingTop + box.PaddingBottom + box.BorderTop + box.BorderBottom;
            double computedHeight;
            if (explicitHeight >= 0) {
                computedHeight = heightBorderBox ? explicitHeight : explicitHeight + heightFrame;
            } else if (StyleResolver.TryResolveAspectRatio(box.Style, out double aspectRatio) && aspectRatio > 0 && box.Width > 0) {
                // CSS Sizing L4 §5: width set, height auto -> derive height
                // from width via the ratio. v1 simplification ignores
                // box-sizing for ratio derivation; the spec applies the ratio
                // to content-box unless `aspect-ratio` is paired with a
                // non-default sizing.
                computedHeight = box.Width / aspectRatio;
            } else if (isOOF && box.OffsetTop.HasValue && box.OffsetBottom.HasValue && box.Height > 0) {
                // OOF box with inset top+bottom pinned and a Height already
                // resolved by PositioningPass. RelayoutContentAt re-enters
                // FinalizeBlockSize during the post-Positioning flex/grid
                // passes (e.g. unit-meta column flex's reflow recursing into
                // .bar > .label which is `position: absolute; inset: 0`).
                // The block-stack `contentBottomY` is the children's flow
                // sum (~spans' line height), unrelated to the inset-derived
                // cb.Height − top − bottom; overwriting Height back to that
                // sum makes the label render below its bar instead of inside.
                // Preserve the PositioningPass-resolved Height here. Pass 1
                // never lands here (Position is still Static and Height is 0
                // until PositioningPass populates the typed fields).
                computedHeight = box.Height;
            } else {
                // CSS Containment L2 §3.3: `contain: size` (or `strict`) makes
                // the element size as if it had NO in-flow contents.  With auto
                // height, that means the content-height contribution collapses to
                // zero; only padding + border form the border-box height.  Explicit
                // height / min / max values still apply (handled in the branches
                // above and the clamp below).  Contents still lay out inside the
                // box — they just overflow without affecting the box's size.
                //
                // CSS Sizing L4 §5: when `contain-intrinsic-height` (or the height
                // component of `contain-intrinsic-size`) is set to a <length>, use
                // that as the content contribution instead of zero.  `auto <length>`
                // uses the <length> fallback (no last-remembered-size memo in v1 —
                // Chrome parity gap noted in CSS_OPEN_GAPS.md B15).
                if (box.Style != null && ContainmentResolver.HasSize(box.Style)) {
                    double intrinsicH = ContainmentResolver.ResolveContainIntrinsicHeightPx(box.Style, ctx, fontSize);
                    computedHeight = intrinsicH + box.PaddingTop + box.PaddingBottom + box.BorderTop + box.BorderBottom;
                } else {
                    computedHeight = contentBottomY + box.PaddingBottom + box.BorderBottom;
                }
            }

            var minParsed = box.Style.GetParsed(CssProperties.MinHeightId);
            var maxParsed = box.Style.GetParsed(CssProperties.MaxHeightId);
            var minR = StyleResolver.ResolveLengthFromParsed(minParsed, ctx, fontSize, null);
            var maxR = StyleResolver.ResolveLengthFromParsed(maxParsed, ctx, fontSize, null);
            // CSS Sizing L3 §5.2: min/max-height inherit the same box-sizing
            // basis as height. Under content-box the author wrote a content
            // bound; convert it to border-box before clamping the already-
            // border-box `computedHeight`.
            if (minR.Kind == StyleResolver.LengthKind.Length) {
                double minPx = heightBorderBox ? minR.Pixels : minR.Pixels + heightFrame;
                if (computedHeight < minPx) computedHeight = minPx;
            }
            if (maxR.Kind == StyleResolver.LengthKind.Length) {
                double maxPx = heightBorderBox ? maxR.Pixels : maxR.Pixels + heightFrame;
                if (computedHeight > maxPx) computedHeight = maxPx;
            }

            box.Height = computedHeight;
            // Chrome lays a <button>'s contents out in a content box that
            // VERTICALLY CENTERS a single line inside an explicit height. A
            // button that reaches BlockLayout uses its default display
            // (author `display: flex/grid` buttons are laid out by
            // Flex/GridLayout instead), so this is the correct, bleed-free
            // scope — author overrides never hit this path. No-op for
            // auto-height buttons: there `computedHeight - frame` equals the
            // natural content height, so the delta resolves to 0.
            if (box.Element != null && box.Element.TagName == "button" && box.ChildList.Count > 0) {
                double contentBoxH = computedHeight - heightFrame;
                double naturalH = contentBottomY - (box.PaddingTop + box.BorderTop);
                double delta = (contentBoxH - naturalH) * 0.5;
                if (delta > 0.5) {
                    var kids = box.ChildList;
                    for (int i = 0; i < kids.Count; i++) kids[i].Y += delta;
                }
            }
            // Diagnostic — fires when UILayoutDiagnostics.Enabled and the
            // box's element class matches. Captures the final block Height
            // a parent will read for stacking. Combined with the
            // InlineLayout.MakeAtomItem / LineBreaker.FinishLine traces,
            // pins where height computation collapses for a block whose
            // only child is an inline-flex atom (.objective-reward containing
            // .reward-chip).
            if (Weva.Diagnostics.UILayoutDiagnostics.ShouldTrace(box.Element)) {
                Weva.Diagnostics.UILayoutDiagnostics.TraceFor(box.Element, "BlockLayout.FinalizeBlockSize",
                    $"computedHeight={computedHeight} contentBottomY={contentBottomY} " +
                    $"padTop={box.PaddingTop} padBottom={box.PaddingBottom} " +
                    $"borderTop={box.BorderTop} borderBottom={box.BorderBottom} " +
                    $"box.W={box.Width} box.H={box.Height} containsInlines={box.ContainsInlines}");
            }
            // Stamp content-derived cross size ONCE per box's lifetime in
            // the layout pipeline. Re-stamping in subsequent RelayoutContentAt
            // calls captures the post-stretch height as "pre-flex" and
            // breaks the invariant (causes dialogue/quest regressions —
            // see prior attempt notes in task #280). The pool's box-recycle
            // path resets this field; width-changes that warrant a fresh
            // intrinsic also need to clear it (handled via the explicit
            // ShrinkFitCachedAvail-style invalidation elsewhere — TBD).
            if (box.PreFlexCrossHeight == 0) {
                box.PreFlexCrossHeight = computedHeight;
            }
        }

        double ApplyBoxModel(BlockBox box, double containingBlockWidth, ComputedStyle parentStyle) {
            var style = box.Style;
            double fs = StyleResolver.FontSizePx(style, parentStyle, ctx);
            // H5b: resolve the element's line-height once and propagate it
            // through the box-model and size resolution so that `lh`-typed
            // padding / margin / width / height / min-/max- values bind to
            // the cascaded line-height rather than the 1.2 * fs fallback.
            double lh = StyleResolver.LineHeightPx(style, fs, ctx, ctx.GetMetrics(style?.Get(CssProperties.FontFamilyId)));

            var pad = ResolveBoxSidesPx(style,
                CssProperties.PaddingId,
                CssProperties.PaddingTopId, CssProperties.PaddingRightId,
                CssProperties.PaddingBottomId, CssProperties.PaddingLeftId,
                fs, containingBlockWidth, lh);
            box.PaddingTop = pad.Top;
            box.PaddingRight = pad.Right;
            box.PaddingBottom = pad.Bottom;
            box.PaddingLeft = pad.Left;

            var borders = ResolveBorderEdges(style, fs);
            box.BorderTop = borders.Top;
            box.BorderRight = borders.Right;
            box.BorderBottom = borders.Bottom;
            box.BorderLeft = borders.Left;

            var mar = ResolveBoxSidesPx(style,
                CssProperties.MarginId,
                CssProperties.MarginTopId, CssProperties.MarginRightId,
                CssProperties.MarginBottomId, CssProperties.MarginLeftId,
                fs, containingBlockWidth, lh);
            box.MarginTop = mar.Top;
            box.MarginRight = mar.Right;
            box.MarginBottom = mar.Bottom;
            box.MarginLeft = mar.Left;

            // CSS Basic User Interface §4.1: `box-sizing` defaults to
            // `content-box`. The cascade fills the property in for every
            // element so the null branch only fires for synthetic boxes that
            // never went through the cascade — keep them on content-box too.
            bool borderBox = IsBorderBox(style);

            // Per-style parsed cache: cached CssValue per slot eliminates the
            // CssValue.TryParse on every layout pass.
            var widthParsed = style.GetParsed(CssProperties.WidthId);
            // H5b: pass element line-height so `width: 1lh` etc. resolve correctly.
            var widthR = StyleResolver.ResolveLengthFromParsed(widthParsed, ctx, fs, containingBlockWidth, lh);

            double margins = box.MarginLeft + box.MarginRight;
            double avail = containingBlockWidth - margins;
            if (avail < 0) avail = 0;

            double resolvedWidth;
            bool widthIsAuto = widthR.Kind == StyleResolver.LengthKind.Auto;
            // CSS Sizing L4 §5: when one of width/height is auto, aspect-ratio
            // derives the other. If width is auto and height is set, we can
            // resolve width = height * ratio here. The opposite case (height
            // from width) lives in FinalizeBlockSize.
            var heightParsedForRatio = style.GetParsed(CssProperties.HeightId);
            var heightForRatio = StyleResolver.ResolveLengthFromParsed(heightParsedForRatio, ctx, fs, null, lh);
            bool heightIsAutoForRatio = heightForRatio.Kind != StyleResolver.LengthKind.Length;
            bool hasAspectRatio = StyleResolver.TryResolveAspectRatio(style, out double aspectRatio);
            if (widthIsAuto && hasAspectRatio && !heightIsAutoForRatio && heightForRatio.Pixels > 0) {
                double widthFromRatio = heightForRatio.Pixels * aspectRatio;
                if (widthFromRatio < 0) widthFromRatio = 0;
                resolvedWidth = widthFromRatio;
                widthIsAuto = false;
            } else if (widthIsAuto) {
                if (box.IsInlineBlock) {
                    resolvedWidth = avail;
                } else {
                    resolvedWidth = avail;
                }
            } else if (widthR.Kind == StyleResolver.LengthKind.Length) {
                resolvedWidth = borderBox ? widthR.Pixels : widthR.Pixels + box.PaddingLeft + box.PaddingRight + box.BorderLeft + box.BorderRight;
            } else if (widthR.Kind == StyleResolver.LengthKind.Percent) {
                double pxBase = containingBlockWidth * (widthR.Percent * 0.01);
                resolvedWidth = borderBox ? pxBase : pxBase + box.PaddingLeft + box.PaddingRight + box.BorderLeft + box.BorderRight;
            } else {
                resolvedWidth = avail;
            }

            // CSS 2.1 §10.3.3: remember the auto-fill width. If a max-width
            // clamp later shrinks an auto width below this fill width, the
            // over-constrained equation re-solves with the freed space handed
            // to any auto inline margins (the canonical `width:auto;
            // max-width:X; margin:0 auto` centering pattern). NaN when the
            // width is not auto so the centering guard below stays the old
            // explicit-width path.
            double autoFillWidth = widthIsAuto ? resolvedWidth : double.NaN;
            var minWidthParsed = style.GetParsed(CssProperties.MinWidthId);
            var maxWidthParsed = style.GetParsed(CssProperties.MaxWidthId);
            var minR = StyleResolver.ResolveLengthFromParsed(minWidthParsed, ctx, fs, containingBlockWidth, lh);
            var maxR = StyleResolver.ResolveLengthFromParsed(maxWidthParsed, ctx, fs, containingBlockWidth, lh);
            // CSS Sizing L3 §5.2: when min-width > max-width, min wins. We
            // implement this by applying max first then min — if min > max,
            // the min clamp raises the post-max value back up to min.
            // min/max-width inherit the same box-sizing basis as width;
            // under content-box the author wrote a CONTENT bound and we
            // must add frame to compare against the already-border-box
            // `resolvedWidth`. Mirrors the height path at ~line 779.
            double widthFrame = box.PaddingLeft + box.PaddingRight + box.BorderLeft + box.BorderRight;
            if (maxR.Kind == StyleResolver.LengthKind.Length) {
                double maxPx = borderBox ? maxR.Pixels : maxR.Pixels + widthFrame;
                if (resolvedWidth > maxPx) resolvedWidth = maxPx;
            }
            if (maxR.Kind == StyleResolver.LengthKind.Percent) {
                double mp = containingBlockWidth * (maxR.Percent * 0.01);
                if (!borderBox) mp += widthFrame;
                if (resolvedWidth > mp) resolvedWidth = mp;
            }
            if (minR.Kind == StyleResolver.LengthKind.Length) {
                double minPx = borderBox ? minR.Pixels : minR.Pixels + widthFrame;
                if (resolvedWidth < minPx) resolvedWidth = minPx;
            }
            if (minR.Kind == StyleResolver.LengthKind.Percent) {
                double mp = containingBlockWidth * (minR.Percent * 0.01);
                if (!borderBox) mp += widthFrame;
                if (resolvedWidth < mp) resolvedWidth = mp;
            }
            box.Width = resolvedWidth;

            // Set box.Height early when CSS specifies a definite (non-percent)
            // length so descendants resolving `height: %` against this box see
            // the correct basis. Without this, percent heights resolve to 0
            // (or fall through) because parent.Height is 0 until
            // FinalizeBlockSize. Skip percent heights here since they need a
            // parent basis themselves and would otherwise loop.
            //
            // The box.Height field is always the border-box height (paint and
            // hit-test consumers treat it as the outer rect), so under the CSS
            // default `box-sizing: content-box` we add the padding+border frame
            // before stamping. Mirrors the width branch above.
            double heightFrameEarly = box.PaddingTop + box.PaddingBottom + box.BorderTop + box.BorderBottom;
            if (heightParsedForRatio != null && heightForRatio.Kind == StyleResolver.LengthKind.Length) {
                double h = heightForRatio.Pixels;
                if (!borderBox) h += heightFrameEarly;
                if (h < 0) h = 0;
                box.Height = h;
            } else if (heightForRatio.Kind == StyleResolver.LengthKind.Percent) {
                // Percent height resolves against parent's definite height
                // per CSS 2.1 §10.5. Resolve early so descendants see this
                // box's height before FinalizeBlockSize runs. Keeps the
                // html→body chain working (synthetic root has Height = viewport).
                var parentContentHeight = DefiniteContentHeight(box.Parent);
                if (parentContentHeight.HasValue) {
                    double h = parentContentHeight.Value * (heightForRatio.Percent * 0.01);
                    if (!borderBox) h += heightFrameEarly;
                    if (h < 0) h = 0;
                    box.Height = h;
                }
            }

            string ml = mar.LeftRaw;
            string mr = mar.RightRaw;
            // CSS 2.1 §9.5.1: `margin: auto` on a float is treated as 0 — a
            // float ignores the auto-margin centering rule (and there's no
            // line of in-flow content for the float to centre against
            // anyway). Inline-blocks are already excluded; the IsFloat
            // guard here adds the float case.
            //
            // Out-of-flow boxes (absolute / fixed) ALSO ignore this in-flow
            // centering: CSS 2.1 §10.3.7 resolves their inline auto margins
            // against the containing block's edges, which is 0 unless BOTH
            // `left` and `right` are pinned — a case PositioningPass handles
            // on its own (the `<dialog> inset:0 margin:auto` pattern). Without
            // this guard a `margin:0 auto; position:absolute; left:50%` box got
            // the in-flow centering margin (≈ (cb − width)/2) ADDED to its
            // left offset, shifting it far off-centre.
            var posType = box.ReadPositionType();
            bool outOfFlow = posType == PositionType.Absolute || posType == PositionType.Fixed;
            // An auto width that a max-width clamp shrank below its fill width
            // is treated like an explicit width for centering (§10.3.3): the
            // gap between the fill width and the clamped width is free space
            // the auto margins absorb. A plain auto width (no clamp) keeps
            // filling — no centering.
            bool autoWidthClamped = widthIsAuto && !double.IsNaN(autoFillWidth)
                                    && resolvedWidth < autoFillWidth - 0.01;
            if (ml == "auto" && mr == "auto" && (!widthIsAuto || autoWidthClamped)
                && !box.IsInlineBlock && !box.IsFloat && !outOfFlow) {
                double extra = containingBlockWidth - resolvedWidth;
                if (extra > 0) {
                    box.MarginLeft = extra * 0.5;
                    box.MarginRight = extra * 0.5;
                }
            }
            return fs;
        }

        // CSS 2.1 §9.5/§9.7 — walks every InlineBox descendant of the
        // containing block looking for blockified floats and reparents
        // them into the block container's children list, recording each
        // hoisted float in `hoistedOut`. The hoist is a pure tree edit;
        // LayoutFloatBox / PlaceFloat run on the reparented float in the
        // direct-child scan that follows. Floats keep their original
        // document order amongst themselves (we append) so two `<span>`-
        // nested floats in source order place left-to-right against the
        // float context just as direct-child floats would.
        //
        // Caller is responsible for re-attaching the hoisted floats after
        // InlineLayout.LayoutInline, which calls container.ClearChildren()
        // and would otherwise drop the freshly-hoisted boxes from the tree
        // (their X/Y are placed in the pre-pass but the parent reference
        // would dangle, breaking FindById / hit testing / paint walks).
        void HoistInlineFloats(BlockBox container, List<BlockBox> hoistedOut) {
            CollectAndReparentInlineFloats(container, container, hoistedOut);
        }

        void CollectAndReparentInlineFloats(BlockBox container, Box node, List<BlockBox> hoistedOut) {
            var kids = node.ChildList;
            // Build a small index list of float-block descendants to lift
            // out of THIS node, plus recurse into inline children. We
            // collect first and mutate after because RemoveChildAt shifts
            // subsequent indices.
            List<int> floatIdx = null;
            for (int i = 0; i < kids.Count; i++) {
                var ch = kids[i];
                if (ch is InlineBox) {
                    CollectAndReparentInlineFloats(container, ch, hoistedOut);
                } else if (node is InlineBox && ch is BlockBox cbb) {
                    // Only hoist when the BlockBox is in fact a float —
                    // BoxBuilder's blockification path turns a floated
                    // inline into a BlockBox, but other BlockBoxes (e.g.
                    // anonymous display:block descendants of an inline,
                    // though rare in practice) must stay where they are.
                    cbb.Float = cbb.ReadFloatType();
                    cbb.Clear = cbb.ReadClearType();
                    if (cbb.IsFloat) {
                        floatIdx ??= scratch.RentIntView();
                        floatIdx.Add(i);
                    }
                }
            }
            if (floatIdx == null) return;
            try {
                for (int j = floatIdx.Count - 1; j >= 0; j--) {
                    int idx = floatIdx[j];
                    var bb = (BlockBox)kids[idx];
                    node.RemoveChildAt(idx);
                    container.AddChild(bb);
                    hoistedOut.Add(bb);
                }
            } finally {
                scratch.ReturnIntView(floatIdx);
            }
        }

        // CSS 2.1 §9.5.1 float sizing: when `width: auto`, the float
        // shrinks to fit (min(max-content, max(min-content, available))).
        // Otherwise the resolved width is used verbatim. We probe the
        // intrinsic widths the same way PositioningPass does for abs-pos
        // shrink-to-fit (RelayoutContentAt at large and small widths),
        // then settle the box at the fitted width.
        void LayoutFloatBox(BlockBox floatBox, double containingBlockWidth) {
            ApplyBoxModel(floatBox, containingBlockWidth, floatBox.Parent?.Style);
            // Per-style parsed cache: a missing slot / "auto" keyword both
            // surface here without touching the raw string.
            var widthParsed = floatBox.Style?.GetParsed(CssProperties.WidthId);
            bool widthIsAuto = widthParsed == null
                || (widthParsed is CssKeyword wk && wk.Identifier == "auto")
                || (widthParsed is CssIdentifier wid && wid.Name == "auto");
            // Float content width / height: lay out interior first so the
            // shrink-to-fit probe can read line metrics + child extents.
            double fs = FontSize(floatBox, floatBox.Parent?.Style);
            if (!widthIsAuto) {
                // Explicit width — width was set by ApplyBoxModel; just lay
                // out the interior at that width.
                LayoutContent(floatBox, fs, containingBlockWidth, floatBox.Parent?.Style);
                return;
            }
            // Auto width: shrink-to-fit. Pass max-content and min-content
            // probes to get the intrinsic widths.
            double frame = floatBox.PaddingLeft + floatBox.PaddingRight + floatBox.BorderLeft + floatBox.BorderRight;
            double marginX = floatBox.MarginLeft + floatBox.MarginRight;
            double avail = containingBlockWidth - marginX;
            if (avail < 0) avail = 0;
            // Probe 1: max-content (large probe).
            RelayoutContentAt(floatBox, 1_000_000);
            double maxContent = Weva.Layout.Positioning.PositioningPass.MaxContentWidth(floatBox, ctx, fs) + frame;
            // Probe 2: min-content (width = 1 forces wrapping at every word).
            RelayoutContentAt(floatBox, 1);
            double minContent = Weva.Layout.Positioning.PositioningPass.MaxContentWidth(floatBox, ctx, fs) + frame;
            if (maxContent < frame) maxContent = frame;
            if (minContent < frame) minContent = frame;
            double fitted = System.Math.Min(maxContent, System.Math.Max(minContent, avail));
            if (fitted > avail) fitted = avail;
            if (fitted < 0) fitted = 0;
            // CSS 2.1 §10.3.5: clamp the float's shrink-to-fit width by any
            // author-supplied min-width / max-width, mirroring the abs-pos path
            // in PositioningPass.ApplyAbsoluteAgainst. Box-sizing is respected:
            // under content-box the author's bound is a CONTENT bound, so we
            // add the frame to compare against the already-border-box `fitted`.
            if (floatBox.Style != null) {
                bool borderBox = IsBorderBox(floatBox.Style);
                var minR = StyleResolver.ResolveLength(floatBox.Style.Get(Weva.Css.Cascade.CssProperties.MinWidthId),
                    floatBox.Style, ctx, fs, containingBlockWidth);
                var maxR = StyleResolver.ResolveLength(floatBox.Style.Get(Weva.Css.Cascade.CssProperties.MaxWidthId),
                    floatBox.Style, ctx, fs, containingBlockWidth);
                // CSS Sizing L3 §5.2: apply max first, then min.  When min > max,
                // the min clamp raises the post-max result, making min win.
                if (maxR.Kind == StyleResolver.LengthKind.Length) {
                    double maxPx = borderBox ? maxR.Pixels : maxR.Pixels + frame;
                    if (fitted > maxPx) fitted = maxPx;
                } else if (maxR.Kind == StyleResolver.LengthKind.Percent) {
                    double mp = containingBlockWidth * maxR.Percent * 0.01;
                    if (!borderBox) mp += frame;
                    if (fitted > mp) fitted = mp;
                }
                if (minR.Kind == StyleResolver.LengthKind.Length) {
                    double minPx = borderBox ? minR.Pixels : minR.Pixels + frame;
                    if (fitted < minPx) fitted = minPx;
                } else if (minR.Kind == StyleResolver.LengthKind.Percent) {
                    double mp = containingBlockWidth * minR.Percent * 0.01;
                    if (!borderBox) mp += frame;
                    if (fitted < mp) fitted = mp;
                }
                if (fitted < 0) fitted = 0;
            }
            RelayoutContentAt(floatBox, fitted);
        }

        // Places a fully-laid-out float at its target Y inside its
        // containing block. `topY` is the box-local Y at which the float's
        // TOP margin-edge would sit if no other floats were active; the
        // routine adjusts for clear + overlapping floats per CSS 2.1
        // §9.5.1 rules 1–9. After placement the float's box-local X/Y are
        // written, and a FloatContext.Entry is recorded in BFC-local
        // coords so subsequent siblings (and our inline lines) can avoid
        // overlapping it.
        void PlaceFloat(BlockBox container, BlockBox floatBox, double topY, double contentW) {
            // Step 1: honour `clear`. The float's TOP margin-edge sits
            // below any matching active float in the parent BFC.
            double clearBottomBfc = currentFloats != null ? currentFloats.ClearBottom(floatBox.Clear) : 0;
            double clearBottomLocal = clearBottomBfc - bfcOriginY;
            if (clearBottomLocal > topY) topY = clearBottomLocal;
            // Step 2: convert to BFC-local coords so the float-stack queries
            // operate in a single consistent frame.
            double bfcContentLeft = bfcOriginX + container.PaddingLeft + container.BorderLeft;
            double bfcCbWidth = contentW; // BFC content width at container's edge
            double marginBoxW = floatBox.MarginLeft + floatBox.Width + floatBox.MarginRight;
            double marginBoxH = floatBox.MarginTop + floatBox.Height + floatBox.MarginBottom;
            double bfcTopY = bfcOriginY + topY;
            // Step 3: find the lowest Y at which the float's margin box
            // fits horizontally against already-placed floats. If avail
            // is less than the float's outer width AND no row clears
            // enough space, the float still sits at the lowest examined Y
            // and overflows — matching CSS 2.1 behaviour.
            bfcTopY = currentFloats != null
                ? currentFloats.FindFloatPlacementY(bfcTopY, marginBoxW, floatBox.Float, bfcCbWidth)
                : bfcTopY;
            // Step 4: compute the float's BFC-local X based on side + the
            // current intrusion.
            double leftIn = currentFloats != null ? currentFloats.LeftExtentAt(bfcTopY) : 0;
            double rightIn = currentFloats != null ? currentFloats.RightExtentAt(bfcTopY, bfcCbWidth) : 0;
            double bfcX;
            if (floatBox.Float == FloatType.Left) {
                bfcX = bfcContentLeft + leftIn + floatBox.MarginLeft;
            } else {
                bfcX = bfcContentLeft + bfcCbWidth - rightIn - marginBoxW + floatBox.MarginLeft;
            }
            // Step 5: write back box-local coords. floatBox.X is local
            // to its parent (the container), so we subtract bfcOriginX
            // (which is the container's position in BFC coords) to
            // produce the container-local X.
            floatBox.X = bfcX - bfcOriginX;
            floatBox.Y = bfcTopY - bfcOriginY + floatBox.MarginTop;
            // Step 6: record the entry in BFC-local coords. The margin
            // box (not the border box) is what subsequent floats / lines
            // must not overlap, per CSS 2.1 §9.5.1 rule 1.
            if (currentFloats != null) {
                double entryLeft = bfcX - floatBox.MarginLeft;
                double entryRight = entryLeft + marginBoxW;
                double entryTop = bfcTopY;
                double entryBottom = entryTop + marginBoxH;
                currentFloats.Add(new FloatContext.Entry(floatBox, floatBox.Float, entryTop, entryBottom, entryLeft, entryRight));
            }
        }

        readonly struct ResolvedSides {
            public readonly double Top;
            public readonly double Right;
            public readonly double Bottom;
            public readonly double Left;
            public readonly string RightRaw;
            public readonly string LeftRaw;

            public ResolvedSides(double top, double right, double bottom, double left,
                                 string rightRaw = null, string leftRaw = null) {
                Top = top;
                Right = right;
                Bottom = bottom;
                Left = left;
                RightRaw = rightRaw;
                LeftRaw = leftRaw;
            }
        }

        readonly struct BoxSidesCacheKey : System.IEquatable<BoxSidesCacheKey> {
            readonly ComputedStyle style;
            readonly int shorthandId;
            readonly double fontSize;
            readonly double basis;

            public BoxSidesCacheKey(ComputedStyle style, int shorthandId, double fontSize, double basis) {
                this.style = style;
                this.shorthandId = shorthandId;
                this.fontSize = fontSize;
                this.basis = basis;
            }

            public bool Equals(BoxSidesCacheKey other) {
                return ReferenceEquals(style, other.style)
                    && shorthandId == other.shorthandId
                    && fontSize == other.fontSize
                    && basis == other.basis;
            }

            public override bool Equals(object obj) => obj is BoxSidesCacheKey other && Equals(other);

            public override int GetHashCode() {
                unchecked {
                    int h = style != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(style) : 0;
                    h = (h * 397) ^ shorthandId;
                    h = (h * 397) ^ fontSize.GetHashCode();
                    h = (h * 397) ^ basis.GetHashCode();
                    return h;
                }
            }
        }

        readonly struct BorderCacheKey : System.IEquatable<BorderCacheKey> {
            readonly ComputedStyle style;
            readonly double fontSize;

            public BorderCacheKey(ComputedStyle style, double fontSize) {
                this.style = style;
                this.fontSize = fontSize;
            }

            public bool Equals(BorderCacheKey other) {
                return ReferenceEquals(style, other.style) && fontSize == other.fontSize;
            }

            public override bool Equals(object obj) => obj is BorderCacheKey other && Equals(other);

            public override int GetHashCode() {
                unchecked {
                    int h = style != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(style) : 0;
                    h = (h * 397) ^ fontSize.GetHashCode();
                    return h;
                }
            }
        }

        ResolvedSides ResolveBoxSidesPx(ComputedStyle style, int shorthandId,
                                        int topId, int rightId, int bottomId, int leftId,
                                        double fs, double containingBlockWidth, double lineHeightPx = 0) {
            // lh is uniquely determined by `style` (it's the cascaded
            // line-height of this element), so the existing reference-keyed
            // cache stays valid without adding lh to the key.
            var key = new BoxSidesCacheKey(style, shorthandId, fs, containingBlockWidth);
            if (boxSidesCache.TryGetValue(key, out var cached)) return cached;

            string topRaw = style?.Get(topId);
            string rightRaw = style?.Get(rightId);
            string bottomRaw = style?.Get(bottomId);
            string leftRaw = style?.Get(leftId);
            bool topInitial = IsInitialZero(topRaw);
            bool rightInitial = IsInitialZero(rightRaw);
            bool bottomInitial = IsInitialZero(bottomRaw);
            bool leftInitial = IsInitialZero(leftRaw);

            CssValue topValue = null;
            CssValue rightValue = null;
            CssValue bottomValue = null;
            CssValue leftValue = null;
            bool haveParsed = false;

            if (topInitial && rightInitial && bottomInitial && leftInitial) {
                string shRaw = style?.Get(shorthandId);
                if (!string.IsNullOrEmpty(shRaw) && shRaw != "0") {
                    if (TryExpandBoxShorthand(style.GetParsed(shorthandId),
                            out topValue, out rightValue, out bottomValue, out leftValue)) {
                        // Per PA1: route each side through RawOrToString so the
                        // `?.Raw ?? ?.ToString() ?? "0"` chain materialises in
                        // exactly one place. Raw is populated for parsed values
                        // and only the rare programmatic-CssValue path (animation
                        // interpolation, calc() evaluation) falls through to
                        // ToString — when it does, the helper guarantees at most
                        // one allocation per side rather than two property
                        // accesses inlined per call site.
                        topRaw = RawOrToString(topValue);
                        rightRaw = RawOrToString(rightValue);
                        bottomRaw = RawOrToString(bottomValue);
                        leftRaw = RawOrToString(leftValue);
                        haveParsed = true;
                    } else {
                        // String-keyed fallback path: this branch only fires
                        // when the parser couldn't expand the shorthand into
                        // typed CssValue terms. Going through the string→parse
                        // round-trip means lh wiring is best-effort here; the
                        // dominant typed path below is the one that authors
                        // hit, and it does honour lh.
                        var rawSides = StyleResolver.BoxSides(style, shorthandId, topId, rightId, bottomId, leftId);
                        var rawResult = new ResolvedSides(
                            StyleResolver.ResolveLengthPx(rawSides.top, 0, style, ctx, fs, containingBlockWidth),
                            StyleResolver.ResolveLengthPx(rawSides.right, 0, style, ctx, fs, containingBlockWidth),
                            StyleResolver.ResolveLengthPx(rawSides.bottom, 0, style, ctx, fs, containingBlockWidth),
                            StyleResolver.ResolveLengthPx(rawSides.left, 0, style, ctx, fs, containingBlockWidth),
                            rawSides.right ?? "0",
                            rawSides.left ?? "0");
                        boxSidesCache[key] = rawResult;
                        return rawResult;
                    }
                }
            }

            ResolvedSides result;
            if (haveParsed) {
                result = new ResolvedSides(
                    ResolveParsedLengthPx(topValue, fs, containingBlockWidth, lineHeightPx),
                    ResolveParsedLengthPx(rightValue, fs, containingBlockWidth, lineHeightPx),
                    ResolveParsedLengthPx(bottomValue, fs, containingBlockWidth, lineHeightPx),
                    ResolveParsedLengthPx(leftValue, fs, containingBlockWidth, lineHeightPx),
                    rightRaw ?? "0",
                    leftRaw ?? "0");
            } else {
                result = new ResolvedSides(
                    ResolveParsedOrRawLengthPx(style, topId, topRaw, fs, containingBlockWidth, lineHeightPx),
                    ResolveParsedOrRawLengthPx(style, rightId, rightRaw, fs, containingBlockWidth, lineHeightPx),
                    ResolveParsedOrRawLengthPx(style, bottomId, bottomRaw, fs, containingBlockWidth, lineHeightPx),
                    ResolveParsedOrRawLengthPx(style, leftId, leftRaw, fs, containingBlockWidth, lineHeightPx),
                    rightRaw ?? "0",
                    leftRaw ?? "0");
            }

            boxSidesCache[key] = result;
            return result;
        }

        static bool IsInitialZero(string raw) {
            return string.IsNullOrEmpty(raw) || raw == "0";
        }

        // Returns the value's Raw source string if the parser populated one,
        // otherwise materialises a fresh string via ToString(), or "0" when
        // the value itself is null. Centralises the per-side fallback used by
        // ResolveBoxSidesPx so the `?.Raw ?? ?.ToString() ?? "0"` chain lives
        // in exactly one place — Raw is null only for programmatically
        // constructed CssValues (animation interpolation, calc() evaluation,
        // pooled allocations), so this is the cold path for shorthand-expanded
        // sides. See CODE_AUDIT_FINDINGS.md PA1.
        static string RawOrToString(CssValue value) {
            if (value == null) return "0";
            var raw = value.Raw;
            if (raw != null) return raw;
            return value.ToString() ?? "0";
        }

        static bool TryExpandBoxShorthand(CssValue parsed,
                                          out CssValue top, out CssValue right,
                                          out CssValue bottom, out CssValue left) {
            top = right = bottom = left = null;
            if (parsed == null) return false;
            if (parsed is CssValueList list && list.Separator == CssValueListSeparator.Space) {
                var items = list.Items;
                if (items.Count == 1) {
                    top = right = bottom = left = items[0];
                    return true;
                }
                if (items.Count == 2) {
                    top = bottom = items[0];
                    right = left = items[1];
                    return true;
                }
                if (items.Count == 3) {
                    top = items[0];
                    right = left = items[1];
                    bottom = items[2];
                    return true;
                }
                if (items.Count == 4) {
                    top = items[0];
                    right = items[1];
                    bottom = items[2];
                    left = items[3];
                    return true;
                }
                return false;
            }
            top = right = bottom = left = parsed;
            return true;
        }

        double ResolveParsedOrRawLengthPx(ComputedStyle style, int propertyId, string raw, double fs, double basis, double lineHeightPx = 0) {
            var parsed = style?.GetParsed(propertyId);
            if (parsed != null) return ResolveParsedLengthPx(parsed, fs, basis, lineHeightPx);
            // No lh wiring on the raw-string fallback — same constraint as the
            // shorthand expansion branch above.
            return StyleResolver.ResolveLengthPx(raw, 0, style, ctx, fs, basis);
        }

        double ResolveParsedLengthPx(CssValue parsed, double fs, double basis, double lineHeightPx = 0) {
            var resolved = StyleResolver.ResolveLengthFromParsed(parsed, ctx, fs, basis, lineHeightPx);
            return resolved.Kind == StyleResolver.LengthKind.Length ? resolved.Pixels : 0;
        }

        ResolvedSides ResolveBorderEdges(ComputedStyle style, double fs) {
            var key = new BorderCacheKey(style, fs);
            if (borderCache.TryGetValue(key, out var cached)) return cached;
            var result = new ResolvedSides(
                ResolveBorderEdge(style, CssProperties.BorderTopStyleId, CssProperties.BorderTopWidthId, fs),
                ResolveBorderEdge(style, CssProperties.BorderRightStyleId, CssProperties.BorderRightWidthId, fs),
                ResolveBorderEdge(style, CssProperties.BorderBottomStyleId, CssProperties.BorderBottomWidthId, fs),
                ResolveBorderEdge(style, CssProperties.BorderLeftStyleId, CssProperties.BorderLeftWidthId, fs));
            borderCache[key] = result;
            return result;
        }

        double ResolveBorderEdge(ComputedStyle style, int styleId, int widthId, double fs) {
            string styleVal = style.Get(styleId);
            if (string.IsNullOrEmpty(styleVal) || styleVal == "none" || styleVal == "hidden") return 0;
            var parsedWidth = style.GetParsed(widthId);
            if (parsedWidth != null) return StyleResolver.ResolveBorderWidth(parsedWidth, fs, ctx);
            string widthRaw = style.Get(widthId);
            return StyleResolver.ResolveBorderWidth(widthRaw, fs, ctx);
        }

        // CSS Basic User Interface §4.1: `box-sizing` is keyword-typed.
        // Consults the per-style parsed cache to skip the string compare on
        // the hot ApplyBoxModel + FinalizeBlockSize paths. Initial value is
        // `content-box`; only `border-box` flips the result. Missing slot /
        // unknown identifier / null parse all map to content-box per spec.
        static bool IsBorderBox(ComputedStyle style) {
            if (style == null) return false;
            var v = style.GetParsed(CssProperties.BoxSizingId);
            if (v is CssKeyword k) return k.Identifier == "border-box";
            if (v is CssIdentifier id) return id.Name == "border-box";
            return style.Get(CssProperties.BoxSizingId) == "border-box";
        }

        static double? DefiniteContentHeight(Box parent) {
            if (parent == null || parent.Height <= 0) return null;
            double contentHeight = parent.Height
                - parent.PaddingTop - parent.PaddingBottom
                - parent.BorderTop - parent.BorderBottom;
            return contentHeight > 0 ? contentHeight : 0;
        }
    }
}
