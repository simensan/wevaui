using System.Collections.Generic;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using Weva.Profiling;

namespace Weva.Layout {
    internal sealed class InlineLayout {
        // Pass-reuse invariant: InlineLayout is constructed once per LayoutEngine.
        // The BoxPool and LineBreaker are engine-stable; LineBreaker keeps its
        // own free lists / scratch across calls. ctx is refreshed via Reset() at
        // the start of every Layout pass. Per-pass scratch (sharedItems,
        // segmentBreaks, segmentBlocks) is bounded by stack-discipline within
        // each LayoutInline call — every entry pops back to its snapshot — so
        // the lists are guaranteed empty between Layout passes.
        //
        // W5 bidi scratch: reused across every ApplyBidiReorderToLines call
        // in a pass. Stack-discipline pop not required here because the lists
        // are fully rebuilt per-line (no nesting). Size-bounded by line length.
        readonly List<BidiRuns.Run> bidiLogical = new(16);
        readonly List<BidiRuns.Run> bidiVisual = new(16);
        LayoutContext ctx;
        readonly BoxPool pool;
        readonly LineBreaker breaker;
        readonly LayoutScratch scratch;
        // Per-pass scratch for the inline-items list. Reused across every
        // LayoutInline call within a single Layout pass; cleared on each entry so
        // recursive IFCs (inline-block atoms whose own interior contains text)
        // don't bleed items between contexts. Recursion uses stack discipline:
        // we snapshot the count at entry and pop back to it at exit so a parent
        // IFC's items survive a nested LayoutInline call from its inline-block
        // atom child.
        readonly List<LineBreaker.Item> sharedItems = new(64);

        // Inline-splitting scratch. Per CSS Display Module Level 3 §2, when a
        // block-level descendant is encountered inside an inline element, the
        // inline element is split: the IFC produces a sequence of "before"
        // inline runs, then the block as its own line-stack child, then "after"
        // inline runs. We track segment boundaries as offsets into sharedItems
        // and the embedded block boxes in parallel.
        //   - segmentBreaks[i] is the count of items at boundary i (the boundary
        //     splits items into [..segmentBreaks[i]) inline-segment then a block).
        //   - segmentBlocks[i] is the BlockBox that follows the segment.
        // segmentBreaks.Count == segmentBlocks.Count. A trailing segment after
        // the last break (items[segmentBreaks[^1]..end]) has no following block.
        readonly List<int> segmentBreaks = new(4);
        readonly List<BlockBox> segmentBlocks = new(4);
        readonly Dictionary<FastMeasureKey, double> fastMeasureCache = new(256);
        const int MaxFastMeasureCacheEntries = 4096;

        // Inline-fragment tracking. CSS 2.1 §9.4.2: an inline element generates
        // one or more "inline box" fragments — one per line it occupies. The
        // LineBreaker produces TextRun fragments in the line box's children,
        // but the originating InlineBox (e.g. `<span>` / `<a>`) gets orphaned
        // when `container.ClearChildren()` clears the raw inline children
        // ahead of relayout. Without re-attaching it, the box tree has no
        // record of the span: paint can't apply its background/border, hit
        // testing can't surface clicks on it, and LayoutDiffTests' DOM-order
        // walk pairs the wrong Chrome element to the next non-span box. We
        // collect the encountered InlineBoxes here during CollectInline so
        // AttachInlineFragmentsToLines (below) can synthesize one fragment
        // per line they cover. Stack-discipline pop on exit so a recursing
        // LayoutInline call (inline-block atom's interior) doesn't pollute
        // the parent IFC's slice.
        readonly List<InlineBox> pendingInlineBoxes = new(8);
        // The originating TextNode of the item that was placed immediately
        // AFTER each InlineBox during CollectInline. AttachInlineFragments-
        // ToLines uses it to position empty inlines (e.g. `<a>` whose only
        // content is a block-level child triggering inline-splitting): the
        // empty inline has no TextRun fragments to bbox against, but the
        // first run carrying this SourceNode in the produced LineBox marks
        // the inline's insertion-point X. Null = no following item (inline
        // is the last inline-flow content of the container). Parallel to
        // pendingInlineBoxes — same length.
        readonly List<Weva.Dom.TextNode> pendingInlineNextNode = new(8);

        // CSS 2.1 §9.2.1.1: out-of-flow boxes (position:absolute|fixed) inside an
        // inline-formatting context do not participate in inline flow — they must
        // not trigger anonymous block generation and must not be treated as
        // inline-split blocks. We skip them during CollectInline and re-attach them
        // to the container after the line rebuild so PositioningPass can find them.
        // Stack-discipline pop (like pendingInlineBoxes): nested LayoutInline calls
        // (from inline-block atom interiors) each own their own slice.
        readonly List<BlockBox> pendingOofBoxes = new(4);

        // Set by LayoutEngine after BlockLayout is constructed. InlineLayout
        // needs BlockLayout to perform a recursive block-flow pass on inline-
        // block atoms (so their interior, intrinsic width, and height are all
        // resolved before they're placed on a line).
        internal BlockLayout BlockLayout;

        public InlineLayout(BoxPool pool, LayoutScratch scratch) {
            this.pool = pool;
            this.scratch = scratch;
            this.breaker = new LineBreaker(pool);
        }

        public void Reset(LayoutContext ctx) {
            this.ctx = ctx;
            // sharedItems / segmentBreaks / segmentBlocks are stack-discipline
            // pop'd at the end of each LayoutInline call, but if a prior pass
            // threw mid-LayoutInline they could carry residue. Defensive clear
            // is O(count) and free at steady state.
            sharedItems.Clear();
            segmentBreaks.Clear();
            segmentBlocks.Clear();
            pendingInlineBoxes.Clear();
            pendingInlineNextNode.Clear();
            pendingOofBoxes.Clear();
        }

        public void LayoutInline(BlockBox container, double availableWidth, LayoutContext layoutCtx) {
            LayoutInline(container, availableWidth, layoutCtx, null, 0, 0);
        }

        // CSS 2.1 §9.5 float-aware overload. `floatCtx` is the active BFC's
        // float context; bfcContentLeft / bfcContentTop give the BFC-local
        // coords of this container's inner top-left edge (padding/border-inset
        // position). The line breaker queries float-extents at each line's
        // cumulative Y to narrow the per-line width and shift the line's X.
        // When floatCtx is null this falls through to the no-float fast path.
        public void LayoutInline(BlockBox container, double availableWidth, LayoutContext layoutCtx,
                                  Weva.Layout.Floats.FloatContext floatCtx,
                                  double bfcContentLeft, double bfcContentTop) {
            using (PerfMarkerScope.Auto(UIProfilerMarkers.LayoutInline)) {
                if (TryLayoutSingleRunFast(container, availableWidth, floatCtx)) return;

                // Stack discipline: an inline-block atom inside this container will
                // recursively trigger LayoutInline for its own interior. Snapshot the
                // current item count so we can pop back to it on exit.
                int itemSnapshot = sharedItems.Count;
                int breakSnapshot = segmentBreaks.Count;
                int blockSnapshot = segmentBlocks.Count;
                int inlineBoxSnapshot = pendingInlineBoxes.Count;
                int oofSnapshot = pendingOofBoxes.Count;
                CollectInline(container, container.Style, sharedItems, availableWidth);
                int collectedTotal = sharedItems.Count - itemSnapshot;
                int segCount = segmentBreaks.Count - breakSnapshot;

                container.ClearChildren();

                if (collectedTotal == 0 && segCount == 0) {
                    double parentFs = StyleResolver.FontSizePx(container.Style, container.Parent?.Style, ctx);
                    var fmEmpty = ResolveMetrics(container.Style);
                    var line = pool.AllocateLineBox();
                    line.Width = 0;
                    line.Height = StyleResolver.LineHeightPx(container.Style, parentFs, ctx, fmEmpty);
                    line.Baseline = fmEmpty != null ? fmEmpty.Ascent(parentFs) : parentFs * 0.8;
                    container.AddChild(line);
                    ApplyTextAlign(container, parentFs);
                    ApplyEllipsisIfNeeded(container, availableWidth);
                    // Re-attach out-of-flow children (position:absolute|fixed) that were
                    // skipped during CollectInline. They must be present in the tree so
                    // PositioningPass can locate and position them.
                    int oofCount0 = pendingOofBoxes.Count - oofSnapshot;
                    if (oofCount0 > 0) {
                        for (int oi = oofSnapshot; oi < pendingOofBoxes.Count; oi++)
                            container.AddChild(pendingOofBoxes[oi]);
                        pendingOofBoxes.RemoveRange(oofSnapshot, oofCount0);
                    }
                    return;
                }

                // Construct a per-line float probe when the container's BFC
                // has any active floats. The probe is shared across both
                // segment-loop calls below so the cumulative Y advances
                // monotonically across inline-split blocks. The probe is
                // null when floatCtx is null or empty — the LineBreaker
                // fast-paths the no-floats case in that branch.
                LineBreaker.LineProbe probe = null;
                if (floatCtx != null && floatCtx.Count > 0) {
                    probe = (lineIndex, topY) => {
                        double bfcY = bfcContentTop + topY;
                        double leftIn = floatCtx.LeftExtentAt(bfcY);
                        double rightIn = floatCtx.RightExtentAt(bfcY, bfcContentLeft + availableWidth);
                        double w = availableWidth - leftIn - rightIn;
                        if (w < 0) w = 0;
                        return (leftIn, w);
                    };
                }
                // W5 UAX #9 bidi: check whether any bidi reordering is needed for
                // this container. Fast path: skip entirely when direction is LTR AND
                // no item text contains an R-class codepoint. This keeps LTR-only
                // layouts byte-identical in layout to the pre-bidi code.
                var containerAlignStyle = container.Style ?? container.Parent?.Style;
                bool containerIsRtl = StyleResolver.IsRtl(containerAlignStyle);
                bool needsBidi = containerIsRtl;
                if (!needsBidi) {
                    // Scan item texts for any R-class character.
                    for (int s2 = 0; s2 < collectedTotal && !needsBidi; s2++) {
                        var it = sharedItems[itemSnapshot + s2];
                        if (it.Text != null && BidiClasses.ContainsRtl(it.Text)) needsBidi = true;
                    }
                }

                int segCursor = itemSnapshot;
                bool indentPending = true;
                double firstLineIndent = StyleResolver.TextIndentPx(container.Style, container.Parent?.Style, layoutCtx, availableWidth);
                for (int s = 0; s < segCount; s++) {
                    int segEnd = segmentBreaks[breakSnapshot + s];
                    int len = segEnd - segCursor;
                    if (len > 0) {
                        breaker.BreakInto(sharedItems, segCursor, len, availableWidth, probe, indentPending ? firstLineIndent : 0);
                        indentPending = false;
                        // W5 UAX #9 L2: reorder TextRun X positions per line when
                        // any bidi content is present. Applied BEFORE AddChild so
                        // ApplyTextAlign sees the post-reorder line widths.
                        if (needsBidi) ApplyBidiReorderToLines(breaker.OutLines, containerIsRtl);
                        for (int i = 0; i < breaker.OutLines.Count; i++) {
                            container.AddChild(breaker.OutLines[i]);
                        }
                    }
                    container.AddChild(segmentBlocks[blockSnapshot + s]);
                    segCursor = segEnd;
                }
                // Trailing inline-only segment (after last block, or the only
                // segment when there are no blocks at all).
                int tailLen = (itemSnapshot + collectedTotal) - segCursor;
                if (tailLen > 0) {
                    breaker.BreakInto(sharedItems, segCursor, tailLen, availableWidth, probe, indentPending ? firstLineIndent : 0);
                    // W5 UAX #9 L2: same bidi reorder for the trailing segment.
                    if (needsBidi) ApplyBidiReorderToLines(breaker.OutLines, containerIsRtl);
                    for (int i = 0; i < breaker.OutLines.Count; i++) {
                        container.AddChild(breaker.OutLines[i]);
                    }
                }

                // BreakInto recycled the consumed Item instances for each segment;
                // pop our slots off the shared scratch.
                sharedItems.RemoveRange(itemSnapshot, sharedItems.Count - itemSnapshot);
                segmentBreaks.RemoveRange(breakSnapshot, segmentBreaks.Count - breakSnapshot);
                segmentBlocks.RemoveRange(blockSnapshot, segmentBlocks.Count - blockSnapshot);

                // Has at least one LineBox child? If every produced child is a
                // block (the IFC contained nothing but a single block descendant),
                // we still leave it as-is; an empty LineBox isn't required.
                bool hasAnyChild = container.ChildList.Count > 0;
                if (!hasAnyChild) {
                    double parentFs2 = StyleResolver.FontSizePx(container.Style, container.Parent?.Style, ctx);
                    var fmEmpty2 = ResolveMetrics(container.Style);
                    var emptyLine = pool.AllocateLineBox();
                    emptyLine.Width = 0;
                    emptyLine.Height = StyleResolver.LineHeightPx(container.Style, parentFs2, ctx, fmEmpty2);
                    emptyLine.Baseline = fmEmpty2 != null ? fmEmpty2.Ascent(parentFs2) : parentFs2 * 0.8;
                    container.AddChild(emptyLine);
                }

                // CSS 2.1 §9.2.1.1: anonymous boxes inherit inheritable
                // properties from their parent. AnonymousBlockBox instances
                // produced by BoxFinalize.FlushAnonymous have Style=null; for
                // the line-height resolution that follows, fall back to the
                // anon parent's Style so author values (line-height, font-
                // family, font-size) propagate as the spec requires. Without
                // this, an anonymous block wrapping bare text inside a flex/
                // grid container collapses to the metric-derived line height
                // instead of the author's `line-height: 1.4`.
                var lhStyle = container.Style ?? container.Parent?.Style;
                double containerFs = StyleResolver.FontSizePx(lhStyle, container.Parent?.Style, ctx);
                string lhRaw = lhStyle?.Get(CssProperties.LineHeightId);
                if (!string.IsNullOrEmpty(lhRaw) && lhRaw != "normal") {
                    var fm = ResolveMetrics(lhStyle);
                    double lh = StyleResolver.LineHeightPx(lhStyle, containerFs, ctx, fm);
                    var containerChildren = container.ChildList;
                    for (int i = 0; i < containerChildren.Count; i++) {
                        var child = containerChildren[i];
                        if (child is LineBox line) {
                            // CSS 2.1 §10.8.1: half-leading is signed.
                            // (lh - oldHeight)/2 is added above AND below the
                            // inline contents regardless of sign — a tight
                            // line-height (lh < oldHeight) yields negative
                            // half-leading and shifts content UP, while a
                            // loose line-height shifts it DOWN. Previously
                            // the negative branch was suppressed, leaving
                            // `line-height: 0.9` indistinguishable from the
                            // natural metric line-height.
                            double oldHeight = line.Height;
                            line.Height = lh;
                            double halfLeading = (lh - oldHeight) * 0.5;
                            line.Baseline += halfLeading;
                            var lineChildren = line.ChildList;
                            for (int ci = 0; ci < lineChildren.Count; ci++) {
                                var run = lineChildren[ci];
                                if (run is TextRun tr) tr.Y += halfLeading;
                                else if (run is BlockBox bb) bb.Y += halfLeading;
                            }
                        }
                    }
                }
                ApplyTextAlign(container, containerFs);
                ApplyEllipsisIfNeeded(container, availableWidth);

                // Re-attach element-backed InlineBoxes (spans, anchors, etc.)
                // as children of the LineBox(es) they cover. Runs AFTER
                // ApplyTextAlign / ApplyEllipsisIfNeeded so fragment positions
                // reflect any final-line text-align shifts. Stack-discipline
                // pop our slice so a recursing IFC (nested inline-block
                // atom's interior) doesn't see this container's inline boxes.
                int inlineBoxCount = pendingInlineBoxes.Count - inlineBoxSnapshot;
                if (inlineBoxCount > 0) {
                    AttachInlineFragmentsToLines(container, inlineBoxSnapshot, inlineBoxCount);
                    pendingInlineBoxes.RemoveRange(inlineBoxSnapshot, inlineBoxCount);
                    pendingInlineNextNode.RemoveRange(inlineBoxSnapshot, inlineBoxCount);
                }

                // Re-attach out-of-flow boxes (position:absolute|fixed) that were
                // skipped during CollectInline so PositioningPass can find and
                // place them. These are appended AFTER the lines and inline-split
                // blocks so the BlockLayout post-LayoutInline cursor walk (which
                // advances cursorY for inline-split BlockBox children) does NOT
                // encounter them — the OOF guard in that walk skips them by
                // position type, and the container height stays unaffected.
                // Stack-discipline: pop our slice so nested IFC calls don't leak.
                int oofCount = pendingOofBoxes.Count - oofSnapshot;
                if (oofCount > 0) {
                    for (int oi = oofSnapshot; oi < pendingOofBoxes.Count; oi++)
                        container.AddChild(pendingOofBoxes[oi]);
                    pendingOofBoxes.RemoveRange(oofSnapshot, oofCount);
                }
            }
        }

        // CSS 2.1 §9.4.2: an inline element produces one inline-box fragment
        // per line it occupies. CollectInline collapses spans into raw items
        // and the LineBreaker emits per-fragment TextRuns inside its LineBox.
        // The originating InlineBox itself is orphaned by `container.Clear-
        // Children()` and never makes it back into the tree — paint can't
        // apply its background/border, hit testing can't surface clicks on
        // the span, and the LayoutDiffTests DOM-walk pairs the next element
        // against the wrong Chrome rect. We rebuild the connection here by
        // walking each produced line and finding TextRun/atom fragments
        // whose `.Element` matches one of the spans tracked during
        // CollectInline. The InlineBox is repositioned in line-local
        // coords to the bbox of its fragments and parented under the line.
        //
        // Multi-line spans now emit one InlineBox fragment per line they cover
        // (CSS 2.1 §9.4.2 / CSS Inline 3): the first line reuses the original
        // InlineBox instance (preserving PaintCache and BoxBuilder-survivor
        // identity); each subsequent line gets a pool-allocated InlineBox
        // clone that shares Element / Style. This makes background-color,
        // border, and text-decoration paint correctly on every line of a
        // wrapped `<a>` / `<span>` per CSS painting order.
        bool TryLayoutSingleRunFast(BlockBox container, double availableWidth, Weva.Layout.Floats.FloatContext floatCtx) {
            if (container == null || container.ChildList.Count != 1) return false;
            if (floatCtx != null && floatCtx.Count != 0) return false;
            InlineBox inlineFragment = null;
            TextRun source = null;
            if (container.ChildList[0] is TextRun directRun) {
                source = directRun;
            } else if (container.ChildList[0] is InlineBox inline
                && inline.Element != null
                && inline.ChildList.Count == 1
                && inline.ChildList[0] is TextRun inlineRun) {
                inlineFragment = inline;
                source = inlineRun;
            } else {
                return false;
            }
            var style = source.Style ?? container.Style;
            if (style == null) return false;
            // CSS 2.1 §9.2.1.1: anonymous block boxes inherit text-align from their
            // enclosing non-anonymous parent. AnonymousBlockBox has Style=null, so
            // fall back to the parent's Style — mirrors ApplyTextAlign's fallback.
            // Without this, text-align:center/right on a block container that mixed
            // inline text with an abs-pos child (creating an AnonymousBlockBox for
            // the text) resolved text-align as "left" and skipped the shift.
            var alignStyle = container.Style ?? container.Parent?.Style;
            string align = StyleResolver.TextAlign(alignStyle);
            string alignLast = StyleResolver.TextAlignLast(alignStyle, align);
            if (align == "justify" || alignLast == "justify") return false;
            string ws = StyleResolver.WhiteSpace(style);
            if (ws != "normal" && ws != "nowrap") return false;
            string text = source.Text ?? "";
            if (!IsSimpleCollapsibleText(text)) return false;
            // W5 UAX #9 bidi fast-path guard: bail to the slow path when the
            // container is RTL or the text contains any R-class codepoint. The
            // slow path will produce the same single-run result PLUS apply
            // ApplyBidiReorderToLines (which, for a pure-RTL single word, flips
            // it to the right edge, and for LTR-with-R triggers proper reorder).
            if (StyleResolver.IsRtl(alignStyle) || BidiClasses.ContainsRtl(text)) return false;

            // CSS Values L4 §6.2: `em` on font-size resolves against the
            // PARENT element's computed font-size, not the element's own.
            // BoxBuilder propagates the block container's style onto direct
            // TextRun children (run.Style = parentStyle = container.Style),
            // so `style` and `container.Style` are the SAME reference when
            // the text run has no own element (a bare text node inside h1).
            // Passing container.Style as both the element style AND the em-base
            // parent makes the container its own parent — doubling any em-sized
            // font-size (e.g. UA `h1 { font-size: 2em }` → 64px instead of 32px).
            // When `style` differs from container.Style (e.g. an inner <span>
            // wrapped in an InlineBox), the container IS the correct em-parent
            // for the span's own font-size resolution.
            ComputedStyle fontSizeParent = ReferenceEquals(style, container.Style)
                ? container.Parent?.Style
                : container.Style;
            double fs = StyleResolver.FontSizePx(style, fontSizeParent, ctx);
            string fam = style.Get(CssProperties.FontFamilyId);
            var metrics = ctx.GetMetrics(fam);
            if (metrics == null) return false;
            int weight = Paint.Conversion.TextRunResolver.ResolveFontWeight(style);
            Paint.FontStyle fontStyle = Paint.Conversion.TextRunResolver.ResolveFontStyle(style);
            string transformed = Paint.Conversion.TextRunResolver.ApplyTextTransform(style, text);
            // PAINT-1: use the weight/style-aware overload when available so the
            // line-box height matches the face the paint side actually renders
            // with. The unstyled LineHeight(fs) routes through the DEFAULT face
            // (regular/400) regardless of the span's `font-weight`, so a bold
            // span (e.g. `.play-btn-label { font-weight:900 }`) gets a line-box
            // sized for the regular face while paint draws with the 900 face's
            // baseline — visible as a top-heavy or bottom-heavy text block when
            // the two faces have different ascent ratios.
            double metricLineHeight = metrics is IStyledFontMetrics styledM
                ? styledM.LineHeight(fs, fam, fontStyle, weight)
                : metrics.LineHeight(fs);
            double usedLineHeight = StyleResolver.LineHeightPx(style, fs, ctx, metrics, fam, fontStyle, weight);
            var lengthCtx = ctx.ToLengthContext(fs, fs, usedLineHeight);
            double letterSpacing = Paint.Conversion.TextRunResolver.ResolveLetterSpacingPx(style, lengthCtx);
            double wordSpacing = Paint.Conversion.TextRunResolver.ResolveWordSpacingPx(style, lengthCtx);
            double width = MeasureFastCached(metrics, transformed, fs, fam, fontStyle, weight, letterSpacing, wordSpacing);
            // CSS Text L3 §7.1: resolve text-indent for the first (and only)
            // line. The indent shifts the run's start X but must NOT inflate
            // line.Width — centering uses line.Width for its `extra` calculation
            // and inflating it shifts centered text left by indent/2. If the
            // indented content overflows, bail to the slow path so wrapping and
            // indent are both handled correctly.
            double firstIndent = StyleResolver.TextIndentPx(container.Style, container.Parent?.Style, ctx, availableWidth);

            // CSS Fragmentation L3 §6.1 — for a single-line InlineBox the fragment
            // is both first and last so it carries both start and end PBM edges.
            // Resolve the PBM here; bail to the slow path if the PBM + text + indent
            // would overflow (so wrapping + PBM are both handled by the slow path).
            double fastStartPbm = 0, fastEndPbm = 0;
            if (inlineFragment != null) {
                ComputedStyle ibEmParent = container.Style ?? container.Parent?.Style;
                (fastStartPbm, fastEndPbm) = ResolveInlinePbm(inlineFragment, ibEmParent, availableWidth);
                inlineFragment.InlinePbmStart = fastStartPbm;
                inlineFragment.InlinePbmEnd   = fastEndPbm;
            }
            double totalWidth = width + fastStartPbm + fastEndPbm + firstIndent;
            if (ws != "nowrap" && totalWidth > availableWidth + LayoutEpsilons.HalfPixelEqual) return false;
            // CSS 2.1 §10.8.1: half-leading is signed in both directions.
            double halfLeading = (usedLineHeight - metricLineHeight) * 0.5;

            // PAINT-1 diagnostic — fires only when
            // UILayoutDiagnostics.Enabled and the element class matches.
            var containerEl = container?.Element ?? source?.Element;
            if (Weva.Diagnostics.UILayoutDiagnostics.ShouldTrace(containerEl)) {
                Weva.Diagnostics.UILayoutDiagnostics.TraceFor(containerEl, "InlineLayout.FastPath",
                    $"fs={fs} weight={weight} fontStyle={fontStyle} fam={fam} " +
                    $"metricLineHeight={metricLineHeight} usedLineHeight={usedLineHeight} " +
                    $"halfLeading={halfLeading} " +
                    $"width={width} text={transformed}");
            }

            var line = pool.AllocateLineBox();
            line.X = 0;
            line.Y = 0;
            line.Width = width;
            line.Height = usedLineHeight;
            // PAINT-1: weight/style-aware baseline. See LineHeight comment above
            // — paint's SdfTextRunBaker.Bake uses the (family,style,weight)
            // face's ascent for its baselineY; layout must match.
            double baselineAscent = metrics is IStyledFontMetrics styledA
                ? styledA.Ascent(fs, fam, fontStyle, weight)
                : metrics.Ascent(fs);
            line.Baseline = baselineAscent + halfLeading;
            line.IsFinalLine = true;

            var run = pool.AllocateTextRun();
            run.Text = transformed;
            run.Style = style;
            run.Element = source.Element;
            run.SourceNode = source.SourceNode;
            run.FontFamily = fam;
            run.FontSize = fs;
            run.Color = style.Get(CssProperties.ColorId);
            // CSS Fragmentation L3 §6.1: the text run starts after the start-PBM area.
            run.X = firstIndent + fastStartPbm;
            run.Y = halfLeading;
            run.Width = width;
            run.Height = metricLineHeight;

            // line.Width covers the full occupied span including PBM edges.
            // (text-indent is excluded per CSS Text L3 §7.1 — same as no-PBM path.)
            line.Width = width + fastStartPbm + fastEndPbm;

            container.ClearChildren();
            line.AddChild(run);
            if (inlineFragment != null) {
                inlineFragment.ClearChildren();
                // CSS Fragmentation L3 §6.1: the fragment rect encompasses the
                // full padding/border/margin + content area. For a single-line span
                // (first AND last fragment), both start and end PBM are included.
                inlineFragment.X = firstIndent;           // starts before the padding
                inlineFragment.Y = run.Y;
                inlineFragment.Width = fastStartPbm + run.Width + fastEndPbm;
                inlineFragment.Height = run.Height;
                // Fast path produces exactly one fragment — it is both first
                // (IsLineFragment=false, the default) and last. Mark it so the
                // paint layer knows it carries all four border edges.
                inlineFragment.IsLastFragment = true;
                line.AddChild(inlineFragment);
            }

            container.AddChild(line);
            ApplyTextAlign(container, fs);
            ApplyEllipsisIfNeeded(container, availableWidth);
            return true;
        }

        static bool IsSimpleCollapsibleText(string text) {
            bool prevSpace = false;
            for (int i = 0; i < text.Length; i++) {
                char c = text[i];
                if (c == '\n' || c == '\r' || c == '\t' || c == '\f') return false;
                bool isSpace = c == ' ';
                if (isSpace && prevSpace) return false;
                prevSpace = isSpace;
            }
            return true;
        }

        double MeasureFastCached(IFontMetrics metrics, string text, double fontSize, string family,
                                 Paint.FontStyle fontStyle, int fontWeight,
                                 double letterSpacingPx, double wordSpacingPx) {
            if (string.IsNullOrEmpty(text)) return 0;
            var key = new FastMeasureKey(metrics, fontSize, family, fontStyle, fontWeight,
                letterSpacingPx, wordSpacingPx, text);
            if (fastMeasureCache.TryGetValue(key, out double cached)) return cached;
            double total = metrics is IStyledFontMetrics styled
                ? styled.Measure(text, fontSize, family, fontStyle, fontWeight)
                : metrics.Measure(text, fontSize);
            if (letterSpacingPx != 0 && text.Length > 1) total += letterSpacingPx * (text.Length - 1);
            if (wordSpacingPx != 0) {
                int spaces = 0;
                for (int i = 0; i < text.Length; i++) if (text[i] == ' ') spaces++;
                total += wordSpacingPx * spaces;
            }
            // L15: slice-evict instead of full Clear() so a high-text-variety
            // layout keeps its working set warm across the cap boundary rather
            // than dropping to a cold cache and re-measuring everything.
            LayoutCacheEviction.EnsureRoom(fastMeasureCache, MaxFastMeasureCacheEntries);
            fastMeasureCache[key] = total;
            return total;
        }

        readonly struct FastMeasureKey : System.IEquatable<FastMeasureKey> {
            readonly IFontMetrics metrics;
            readonly double fontSize;
            readonly string family;
            readonly Paint.FontStyle fontStyle;
            readonly int fontWeight;
            readonly double letterSpacingPx;
            readonly double wordSpacingPx;
            readonly string text;

            public FastMeasureKey(IFontMetrics metrics, double fontSize, string family,
                                  Paint.FontStyle fontStyle, int fontWeight,
                                  double letterSpacingPx, double wordSpacingPx, string text) {
                this.metrics = metrics;
                this.fontSize = fontSize;
                this.family = family;
                this.fontStyle = fontStyle;
                this.fontWeight = fontWeight;
                this.letterSpacingPx = letterSpacingPx;
                this.wordSpacingPx = wordSpacingPx;
                this.text = text;
            }

            public bool Equals(FastMeasureKey other) {
                return ReferenceEquals(metrics, other.metrics)
                    && fontSize == other.fontSize
                    && string.Equals(family, other.family, System.StringComparison.OrdinalIgnoreCase)
                    && fontStyle == other.fontStyle
                    && fontWeight == other.fontWeight
                    && letterSpacingPx == other.letterSpacingPx
                    && wordSpacingPx == other.wordSpacingPx
                    && text == other.text;
            }

            public override bool Equals(object obj) => obj is FastMeasureKey other && Equals(other);

            public override int GetHashCode() {
                unchecked {
                    int h = metrics != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(metrics) : 0;
                    h = (h * 397) ^ fontSize.GetHashCode();
                    h = (h * 397) ^ (family != null ? System.StringComparer.OrdinalIgnoreCase.GetHashCode(family) : 0);
                    h = (h * 397) ^ (int)fontStyle;
                    h = (h * 397) ^ fontWeight;
                    h = (h * 397) ^ letterSpacingPx.GetHashCode();
                    h = (h * 397) ^ wordSpacingPx.GetHashCode();
                    h = (h * 397) ^ (text != null ? text.GetHashCode() : 0);
                    return h;
                }
            }
        }

        void AttachInlineFragmentsToLines(BlockBox container, int snapshotStart, int count) {
            for (int k = 0; k < count; k++) {
                var span = pendingInlineBoxes[snapshotStart + k];
                if (span == null || span.Element == null) continue;
                // Detach the InlineBox from any prior parent (it survives in
                // BoxBuilder's tree across passes but its ParentChild link
                // was severed by `container.ClearChildren()`). ClearChildren
                // is needed for the multi-pass case where a previous attach
                // already gave it line-local children we'd re-process.
                span.ClearChildren();
                var spanElement = span.Element;
                // CSS 2.1 §9.4.2 / CSS Inline 3: an inline element that wraps
                // across N lines generates N inline-box fragments — one per
                // line, each covering only that line's portion of the inline.
                // Paint (background/border/text-decoration), hit-testing, and
                // accessibility all consume the fragment list. We reuse the
                // ORIGINAL InlineBox for the first matching line (preserves
                // Element-keyed PaintCache identity and the BoxBuilder-tree
                // pointer the box-survivor diff expects) and allocate fresh
                // InlineBox clones for subsequent lines — clones share Element
                // / Style so the cascade-resolved decorations match, but get
                // their own rect / Parent / PaintCache.
                bool placedFirst = false;
                InlineBox lastFrag = null;
                var containerChildren = container.ChildList;
                for (int childIndex = 0; childIndex < containerChildren.Count; childIndex++) {
                    var child = containerChildren[childIndex];
                    if (!(child is LineBox line)) continue;
                    double minX = double.PositiveInfinity;
                    double minY = double.PositiveInfinity;
                    double maxX = double.NegativeInfinity;
                    double maxY = double.NegativeInfinity;
                    bool any = false;
                    var lineChildren = line.ChildList;
                    for (int i = 0; i < lineChildren.Count; i++) {
                        var lc = lineChildren[i];
                        // A fragment belongs to this span when its Element
                        // pointer matches. LineBreaker.FinishLine copies
                        // SourceRun.Element verbatim onto emitted TextRuns,
                        // so a `<span>`'s text fragments carry the span
                        // Element. Inline-block atoms inside the span carry
                        // their OWN Element (the atom's element) — they'd
                        // produce a separate principal-box entry of their
                        // own in the DOM walk, so don't fold them in here.
                        if (!(lc is TextRun tr2) || tr2.Element != spanElement) continue;
                        if (tr2.X < minX) minX = tr2.X;
                        if (tr2.Y < minY) minY = tr2.Y;
                        double r = tr2.X + tr2.Width;
                        double b = tr2.Y + tr2.Height;
                        if (r > maxX) maxX = r;
                        if (b > maxY) maxY = b;
                        any = true;
                    }
                    if (!any) continue;
                    InlineBox frag;
                    if (!placedFirst) {
                        // First line: reuse the original InlineBox to preserve
                        // identity (PaintCache, BoxBuilder survivor pointer).
                        frag = span;
                        placedFirst = true;
                    } else {
                        // Subsequent lines: clone — share Element/Style so the
                        // cascade-resolved decoration values match the original.
                        // The clone is marked IsLineFragment so Reconcile's
                        // per-Element cache does NOT collapse it back onto the
                        // canonical (line-1) InlineBox when a wrapped span
                        // visits each fragment's Element. Without the flag,
                        // ReplaceChild on the second LineBox swaps in the
                        // line-1 instance, leaving two LineBoxes sharing one
                        // InlineBox child — the F2 regression for multi-line
                        // wrap.
                        frag = pool.AllocateInlineBox();
                        frag.Element = spanElement;
                        frag.Style = span.Style;
                        frag.IsLineFragment = true;
                    }
                    frag.X = minX;
                    frag.Y = minY;
                    frag.Width = maxX - minX;
                    frag.Height = maxY - minY;
                    // Reset IsLastFragment on the previously-last fragment since
                    // we've now found another one after it (rolling last pointer).
                    if (lastFrag != null) lastFrag.IsLastFragment = false;
                    frag.IsLastFragment = true;
                    lastFrag = frag;
                    // Insert the InlineBox fragment at position 0 of the line so
                    // the document-order walker encounters it BEFORE any same-element
                    // TextRun siblings. The first box to claim an element is the
                    // "principal box" in BuildUnityBoxes; by placing the InlineBox
                    // first, its full-span Width (spanning pseudo + real content) is
                    // what the walker reports — matching Chrome's getBoundingClientRect
                    // which includes ::before/::after content in the element's rect.
                    // CSS 2.1 §12.1: generated content is logically part of the host.
                    line.InsertChildFirst(frag);
                }
                if (!placedFirst) {
                    // Empty inline — e.g. an `<a>` whose only content is a
                    // block-level child that triggered inline-splitting and
                    // lives outside any LineBox. The span has no in-line
                    // fragment to bbox against. Per Chrome's
                    // getBoundingClientRect, an empty inline element returns
                    // its insertion point with zero width but the line's
                    // height. We approximate the insertion point by finding
                    // the LineBox containing the TextRun whose SourceNode
                    // matches pendingInlineNextNode[slot] (the item that
                    // appeared immediately before the empty inline in
                    // CollectInline). The right edge of that TextRun is the
                    // empty inline's start position. If no such TextRun
                    // exists, fall back to the first line's origin.
                    // An empty inline has only one "fragment" (itself), so
                    // it is both first and last.
                    span.IsLastFragment = true;
                    var prevNode = pendingInlineNextNode[snapshotStart + k];
                    LineBox anchorLine = null;
                    double insertionX = 0;
                    if (prevNode != null) {
                        for (int childIndex = 0; childIndex < containerChildren.Count; childIndex++) {
                            var child = containerChildren[childIndex];
                            if (!(child is LineBox lb)) continue;
                            var lbChildren = lb.ChildList;
                            for (int ci = lbChildren.Count - 1; ci >= 0; ci--) {
                                if (lbChildren[ci] is TextRun tr2
                                    && ReferenceEquals(tr2.SourceNode, prevNode)) {
                                    anchorLine = lb;
                                    insertionX = tr2.X + tr2.Width;
                                    break;
                                }
                            }
                            if (anchorLine != null) break;
                        }
                    }
                    if (anchorLine == null) {
                        for (int childIndex = 0; childIndex < containerChildren.Count; childIndex++) {
                            var child = containerChildren[childIndex];
                            if (child is LineBox lb) { anchorLine = lb; break; }
                        }
                    }
                    span.X = insertionX;
                    span.Y = 0;
                    span.Width = 0;
                    span.Height = anchorLine != null ? anchorLine.Height : 0;
                    if (anchorLine != null) anchorLine.AddChild(span);
                    else container.AddChild(span);
                } else {
                    // CSS Fragmentation L3 §6.1 — expand fragment rects to
                    // include the inline-axis PBM edges.
                    //
                    // Under slice (initial value):
                    //   • Start-PBM spacer advanced state.X before the first
                    //     text run → text bbox minX = startPbm. Shift the
                    //     FIRST fragment's X back by startPbm and widen it.
                    //   • End-PBM spacer advanced state.X after the last run
                    //     but produced no TextRun; widen the LAST fragment by
                    //     endPbm to cover the PBM area.
                    //
                    // Under clone (box-decoration-break: clone):
                    //   • EVERY fragment carries BOTH edges. The start-PBM
                    //     spacer on line 1 and the end-PBM spacer on the last
                    //     line handle those cases via the spacer mechanism.
                    //     For mid-span wraps the line breaker advanced X by
                    //     startPbm at the start of each continuation line, so
                    //     every fragment's text bbox already has minX = startPbm.
                    //   • Expansion: for every fragment, shift X back by
                    //     startPbm and add startPbm + endPbm to width. The
                    //     endPbm in the width covers the cloned right edge; the
                    //     last fragment's endPbm also arrives via the end-spacer
                    //     (same spacer as slice) — the expansion formula is
                    //     uniform across all fragments.
                    //
                    // `span` is the first fragment (original InlineBox, holds
                    // InlinePbmStart / InlinePbmEnd). Pool-allocated clone
                    // fragments (IsLineFragment=true) share Element/Style
                    // but not the PBM fields — read them off `span` for all.
                    double pbmStart = span.InlinePbmStart;
                    double pbmEnd   = span.InlinePbmEnd;
                    bool isClone    = IsBoxDecorationBreakClone(span.Style);

                    if (isClone) {
                        // Clone: every fragment (first, middle, and last) gets
                        // both PBM edges. We walk the container's lines again
                        // to find all fragments for this span and expand each.
                        //
                        // Fragment list was accumulated in `lastFrag` pointer
                        // order (first→last) during the loop above. The only
                        // way to visit all of them again is to re-walk the
                        // container's children and collect InlineBoxes whose
                        // Element matches. We reuse the same loop structure.
                        bool cloneStart = pbmStart > 0;
                        bool cloneEnd   = pbmEnd   > 0;
                        if (cloneStart || cloneEnd) {
                            var cloneSpanElement = span.Element;
                            var containerChildren2 = container.ChildList;
                            bool firstCloneFrag = true;
                            for (int ci = 0; ci < containerChildren2.Count; ci++) {
                                if (!(containerChildren2[ci] is LineBox lb2)) continue;
                                var lbChildren2 = lb2.ChildList;
                                for (int li = 0; li < lbChildren2.Count; li++) {
                                    if (!(lbChildren2[li] is InlineBox ibFrag)) continue;
                                    if (ibFrag.Element != cloneSpanElement) continue;
                                    // This fragment belongs to our span.
                                    if (firstCloneFrag) {
                                        // First fragment: its minX was set to
                                        // startPbm by the start spacer (or by
                                        // the continuation-line offset). Shift X
                                        // back by startPbm and add startPbm.
                                        if (cloneStart) {
                                            ibFrag.X     -= pbmStart;
                                            ibFrag.Width += pbmStart;
                                        }
                                        firstCloneFrag = false;
                                    } else {
                                        // Continuation fragments: their text
                                        // minX was set to startPbm by the
                                        // clone start offset in FinishLine.
                                        if (cloneStart) {
                                            ibFrag.X     -= pbmStart;
                                            ibFrag.Width += pbmStart;
                                        }
                                    }
                                    // ALL fragments get the end edge (CSS Fragmentation
                                    // L3 §6.1: clone = both edges on every fragment).
                                    //
                                    // The end-PBM spacer advances state.X (→ line.Width)
                                    // on the last line but produces no TextRun fragment;
                                    // the text bbox maxX stops at the last char's right
                                    // edge. Adding pbmEnd here makes the fragment rect
                                    // cover the full padding/border/margin area and match
                                    // the line.Width. The same logic applies to non-last
                                    // fragments: FinishLine added endPbm to state.X
                                    // (ActiveCloneEndPbm path) so line.Width includes it,
                                    // but the text bbox doesn't — we must expand here.
                                    if (cloneEnd) {
                                        ibFrag.Width += pbmEnd;
                                    }
                                }
                            }
                        }
                    } else {
                        // Slice (initial): start edge on first fragment only,
                        // end edge on last fragment only.
                        if (pbmStart > 0) {
                            span.X     -= pbmStart;
                            span.Width += pbmStart;
                        }
                        if (lastFrag != null && pbmEnd > 0) {
                            lastFrag.Width += pbmEnd;
                        }
                    }
                }
            }
        }

        void ApplyTextAlign(BlockBox container, double parentFontSize) {
            // CSS 2.1 §9.2.1.1: anonymous block boxes inherit inheritable
            // properties (incl. text-align) from their enclosing non-anonymous
            // box. AnonymousBlockBox has Style=null, so fall back to the
            // parent's Style — mirrors the line-height fallback above. Without
            // this, an anonymous block (created when a block container mixes
            // block + inline-level children — e.g. `.r-px` holding a block
            // price div + an inline-flex `.pill`) reads text-align "left" and
            // leaves its line (the shrink-wrapped inline-flex atom) pinned left
            // even though the parent set text-align:right.
            var alignStyle = container.Style ?? container.Parent?.Style;
            string align = StyleResolver.TextAlign(alignStyle);
            string alignLast = StyleResolver.TextAlignLast(alignStyle, align);
            // CSS Text L3 §7.3: text-justify controls the spreading algorithm
            // when text-align:justify is active. Read from the same style
            // source as text-align (inherited, so the container's resolved style).
            string textJustify = StyleResolver.TextJustify(alignStyle);
            // Even when both resolve to "left" we still need to undo any
            // prior align-delta — a FlexLayout/GridLayout re-run after a
            // probe might land here with no extra to apply, but the prior
            // pass already pushed children right. Without this undo, the
            // text-render position stays stale relative to the final box.
            double contentW = container.ContentWidth;
            var children = container.ChildList;
            for (int childIndex = 0; childIndex < children.Count; childIndex++) {
                var child = children[childIndex];
                if (!(child is LineBox line)) continue;
                // Step 1: undo whatever offset a previous ApplyTextAlign
                // stamped onto this line's children. If no offset was applied
                // (AppliedTextAlignDelta == 0) this is a no-op.
                if (line.AppliedTextAlignDelta != 0) {
                    OffsetLine(line, -line.AppliedTextAlignDelta);
                    line.AppliedTextAlignDelta = 0;
                }
                // Step 1b: undo any stale inter-character justify spacing that
                // may have been applied to TextRun.JustifyLetterSpacingPx in a
                // previous pass (e.g. after a probe-triggered relayout). Clearing
                // to 0 is safe because JustifyLineInterCharacter always recomputes
                // and reassigns the value when it runs again below.
                foreach (var lc in line.ChildList) {
                    if (lc is TextRun lcTr) lcTr.JustifyLetterSpacingPx = 0;
                }
                string lineAlign = line.IsFinalLine ? alignLast : align;
                if (lineAlign == "left" || string.IsNullOrEmpty(lineAlign)) continue;
                double extra = contentW - line.Width;
                if (extra <= 0) continue;
                double appliedDelta = 0;
                if (lineAlign == "right") {
                    OffsetLine(line, extra);
                    appliedDelta = extra;
                } else if (lineAlign == "center") {
                    double half = extra * 0.5;
                    OffsetLine(line, half);
                    appliedDelta = half;
                } else if (lineAlign == "justify") {
                    // CSS Text L3 §7.3: text-justify:none suppresses all spreading;
                    // text-justify:inter-character spreads across every character gap;
                    // text-justify:auto and text-justify:inter-word (the default) use
                    // the existing inter-word algorithm.
                    if (textJustify != "none") {
                        if (textJustify == "inter-character") {
                            JustifyLineInterCharacter(line, extra);
                        } else {
                            JustifyLine(line, extra);
                        }
                    }
                    // JustifyLine / JustifyLineInterCharacter mutate child widths +
                    // cumulative X; they can't be undone via a single delta. Track 0.
                }
                line.AppliedTextAlignDelta = appliedDelta;
            }
        }

        // CSS Text Overflow Module Level 3: applies only when the container
        // clips overflow AND has white-space:nowrap (multi-line ellipsis via
        // line-clamp deferred). Truncates each LineBox whose width exceeds the
        // container's content-width by replacing the tail with a single "…"
        // glyph in the line's font. v1 only — single-line.
        void ApplyEllipsisIfNeeded(BlockBox container, double availableWidth) {
            EllipsisHelper.ApplyIfNeeded(container, availableWidth, ctx, pool);
            // CSS Overflow L4 §6 — line-clamp truncates after N lines and
            // appends "…" to the Nth. Runs after single-line ellipsis so
            // each path operates on its own contract; both never fire on
            // the same container in practice.
            LineClampHelper.ApplyIfNeeded(container, ctx, pool);
        }

        static void OffsetLine(LineBox line, double dx) {
            var children = line.ChildList;
            for (int i = 0; i < children.Count; i++) {
                var run = children[i];
                if (run is TextRun tr) tr.X += dx;
                else if (run is BlockBox bb) bb.X += dx;
            }
            // Do NOT add dx to line.Width here. OffsetLine only translates
            // content positions; it does not widen the content span.
            // Contrast with JustifyLine below, where spaces are physically
            // stretched so line.Width += extra IS correct.
            // Inflating line.Width by the centering/right shift causes
            // MakeAtomItem to measure an overestimated max-content for
            // inline-block atoms whose sub-layout uses text-align:center/right,
            // making those atoms wider than their actual content.
        }

        static void JustifyLine(LineBox line, double extra) {
            int gapCount = 0;
            var children = line.ChildList;
            for (int i = 0; i < children.Count - 1; i++) {
                if (children[i] is TextRun tr && tr.Text == " ") gapCount++;
            }
            if (gapCount == 0) return;
            double inc = extra / gapCount;
            double cumulative = 0;
            for (int i = 0; i < children.Count; i++) {
                var child = children[i];
                if (child is TextRun tr) {
                    tr.X += cumulative;
                    if (tr.Text == " " && i < children.Count - 1) {
                        tr.Width += inc;
                        cumulative += inc;
                    }
                } else if (child is BlockBox bb) {
                    // Inline-block atoms on the line must shift with the
                    // accumulated justify spacing too, otherwise they keep
                    // their pre-justify X while the surrounding words spread.
                    bb.X += cumulative;
                }
            }
            line.Width += extra;
        }

        // CSS Text L3 §7.3 `text-justify:inter-character`: distribute `extra`
        // space across all inter-character gaps on the line.
        //
        // Gap model: the total number of gaps is the sum of (text.Length - 1) for
        // every non-empty TextRun on the line, plus 1 for each boundary BETWEEN
        // adjacent non-empty TextRuns (space runs count as text too). This exactly
        // matches the spec rule that every adjacent-character boundary — including
        // at word boundaries — is a justification opportunity.
        //
        // Concretely for "ab cd ef" split as ["ab", " ", "cd", " ", "ef"]:
        //   run gaps: (2-1)+(1)+(2-1)+(1)+(2-1) = 1+0+1+0+1 = 3 intra-run gaps
        //   boundary gaps: 4 inter-run boundaries (between 5 runs)
        //   total = 7  (matches the 7 inter-character slots in the 8-char string)
        //
        // The per-character increment `inc = extra / totalGaps` is applied as:
        //   - TextRun.JustifyLetterSpacingPx += inc  (the paint converter adds
        //     this on top of the CSS letter-spacing so the glyph baker spreads
        //     within the run)
        //   - run.Width += inc * (text.Length - 1)   (intra-run gaps only; the
        //     inter-run boundary gap widens the PRECEDING run by `inc` too)
        //   - Subsequent runs shift cumulatively (their X advances by the widening
        //     of all runs that precede them plus all inter-run boundary gaps).
        //   - line.Width += extra
        //
        // Inline-block atoms are shifted by the cumulative spread, matching
        // the inter-word JustifyLine contract.
        static void JustifyLineInterCharacter(LineBox line, double extra) {
            var children = line.ChildList;
            // Pass 1: count total inter-character gap slots.
            int totalGaps = 0;
            bool prevWasText = false;
            for (int i = 0; i < children.Count; i++) {
                if (children[i] is TextRun tr && tr.Text.Length > 0) {
                    if (prevWasText) totalGaps++; // one boundary gap between this and the previous text run
                    totalGaps += tr.Text.Length - 1; // intra-run gaps
                    prevWasText = true;
                } else {
                    // inline-block atom: not a text node; resets the "adjacent text" chain
                    prevWasText = false;
                }
            }
            if (totalGaps <= 0) return;
            double inc = extra / totalGaps;
            // Pass 2: assign JustifyLetterSpacingPx, widen runs, accumulate cumulative shift.
            double cumulative = 0;
            bool firstText = true;
            for (int i = 0; i < children.Count; i++) {
                var child = children[i];
                if (child is TextRun tr && tr.Text.Length > 0) {
                    // One inter-run boundary gap is added BEFORE this run
                    // (except for the very first text run on the line).
                    if (!firstText) {
                        cumulative += inc; // the gap between the previous run's last char and this run's first char
                    }
                    firstText = false;
                    tr.X += cumulative;
                    // Per-glyph letter-spacing increment (adds to CSS letter-spacing at paint time).
                    tr.JustifyLetterSpacingPx = inc;
                    // Width grows by the intra-run gaps only; the inter-run boundary
                    // gap is accounted for by the cumulative shift of the next item.
                    double intraGaps = tr.Text.Length - 1;
                    tr.Width += inc * intraGaps;
                    cumulative += inc * intraGaps;
                } else if (child is BlockBox bb) {
                    bb.X += cumulative;
                    // Atom resets the "adjacent text" chain — an atom gap is NOT
                    // an inter-character opportunity per the spec (atoms are opaque).
                    firstText = true;
                }
            }
            line.Width += extra;
        }

        IFontMetrics ResolveMetrics(ComputedStyle style) {
            if (ctx == null) return null;
            string fam = style?.Get(CssProperties.FontFamilyId);
            return ctx.GetMetrics(fam);
        }

        void CollectInline(Box parent, ComputedStyle inheritedStyle, List<LineBreaker.Item> items, double availableWidth) {
            // Walk up the box tree to find the style that should resolve
            // `em` units for this inline subtree's font-size. CSS resolves
            // em against the *parent element's* computed font-size; for a
            // box whose Style was inherited from its containing block, the
            // parent we want is that block's own parent. Bubbling here once
            // and reusing the result as we recurse keeps em resolution
            // consistent across the whole inline run regardless of how deep
            // the nesting is.
            ComputedStyle emParentStyle = null;
            for (var p = parent.Parent; p != null; p = p.Parent) {
                if (p.Style != null) { emParentStyle = p.Style; break; }
            }
            CollectInlineInner(parent, inheritedStyle, emParentStyle, items, availableWidth, null, 0, 0);
        }

        // spanOwner: the DOM Element of the nearest enclosing element-backed
        // InlineBox — propagated into items so that pseudo-element TextRuns
        // (from ::before/::after InlineBoxes with Element=null) are attributed
        // to the host span's element. LineBreaker.FinishLine then stamps
        // run.Element = OwnerElement for null-SourceRun items, making
        // AttachInlineFragmentsToLines include pseudo content in the span width.
        // CSS 2.1 §12.1: generated content is logically part of its originator.
        //
        // outerCloneStartPbm / outerCloneEndPbm: accumulated inline-axis PBM of
        // all enclosing box-decoration-break:clone spans. These are propagated
        // into every content item (text/atom) produced within a clone span so
        // the line breaker can apply both PBM edges on every mid-span wrap per
        // CSS Fragmentation L3 §6.1. Items outside any clone span carry 0/0 and
        // no clone behaviour is triggered.
        void CollectInlineInner(Box parent, ComputedStyle inheritedStyle, ComputedStyle emParentStyle, List<LineBreaker.Item> items, double availableWidth, Weva.Dom.Element spanOwner, double outerCloneStartPbm, double outerCloneEndPbm) {
            var children = parent.ChildList;
            for (int childIndex = 0; childIndex < children.Count; childIndex++) {
                var child = children[childIndex];
                if (child is TextRun tr) {
                    var style = tr.Style ?? inheritedStyle;
                    var item = MakeItem(tr.Text, style, emParentStyle, tr);
                    // Propagate the enclosing span owner so LineBreaker can
                    // set run.Element for pseudo-content items (SourceRun.Element == null).
                    item.OwnerElement = spanOwner;
                    // CSS Fragmentation L3 §6.1 — propagate the accumulated
                    // clone PBM from all enclosing clone spans. Items outside
                    // any clone span carry 0/0; the line breaker no-ops there.
                    item.CloneSpanStartPbm = outerCloneStartPbm;
                    item.CloneSpanEndPbm   = outerCloneEndPbm;
                    items.Add(item);
                    continue;
                }
                if (child is InlineBox ib) {
                    // HTML spec: <br> is a void element whose sole rendering effect
                    // is a forced line break in inline formatting context. It has
                    // no children and no CSS that produces a break — the break is
                    // unconditional regardless of white-space mode (spec §4.5.27).
                    // We emit a "\n" item with white-space:pre so LineBreaker's
                    // AppendPreserving path hits the preserved-newline branch and
                    // calls FinishLine(forcedBreak:true). The item inherits the
                    // surrounding style for font metrics so the produced (empty)
                    // line box has the correct line height. We do NOT register the
                    // <br> box in pendingInlineBoxes — it has no rect to report.
                    if (ib.Element?.TagName == "br") {
                        var brStyle = ib.Style ?? inheritedStyle;
                        var brItem = breaker.RentItem();
                        brItem.Text = "\n";
                        brItem.Style = brStyle;
                        brItem.FontSize = StyleResolver.FontSizePx(brStyle, emParentStyle, ctx);
                        brItem.FontFamily = brStyle?.Get(CssProperties.FontFamilyId);
                        brItem.FontWeight = Paint.Conversion.TextRunResolver.ResolveFontWeight(brStyle);
                        brItem.FontStyle = Paint.Conversion.TextRunResolver.ResolveFontStyle(brStyle);
                        brItem.Color = brStyle?.Get(CssProperties.ColorId);
                        brItem.WhiteSpace = "pre";
                        brItem.Metrics = ctx.GetMetrics(brItem.FontFamily);
                        brItem.SourceRun = null;
                        brItem.OwnerElement = spanOwner;
                        // CSS Fragmentation L3 §6.1: a forced break (<br>) inside
                        // a clone span also splits the fragment; the continuation
                        // gets both PBM edges, same as a word-wrap break.
                        brItem.CloneSpanStartPbm = outerCloneStartPbm;
                        brItem.CloneSpanEndPbm   = outerCloneEndPbm;
                        items.Add(brItem);
                        // Register the <br> InlineBox in pendingInlineBoxes so
                        // AttachInlineFragmentsToLines places it in the box tree
                        // with a zero-width rect at its break insertion point.
                        // Chrome's DOM walk reports <br> as a zero-width element
                        // with height = line-height; LayoutDiffTests pairs it by
                        // document-order index, so we must emit a box for every
                        // <br> element that Chrome enumerates. The empty-inline
                        // fallback in AttachInlineFragmentsToLines positions it
                        // at the right edge of the last text run before the break
                        // — matching Chrome's getBoundingClientRect for <br>.
                        pendingInlineBoxes.Add(ib);
                        // Record the preceding item's SourceNode (if any) so the
                        // empty-inline path can locate the insertion-point TextRun.
                        var prevNode = items.Count >= 2
                            ? items[items.Count - 2].SourceRun?.SourceNode
                            : null;
                        pendingInlineNextNode.Add(prevNode);
                        continue;
                    }
                    // Track element-backed InlineBoxes so AttachInlineFragments-
                    // ToLines can re-attach them to the produced LineBox(es) with
                    // a computed bbox. Anonymous InlineBoxes (Element == null,
                    // produced by ::before/::after with default display) carry no
                    // DOM identity and don't need a tree presence — skip them.
                    // beforeCountForEmptyCheck: item count BEFORE any PBM spacers
                    // are injected. Used by the empty-inline detection below so
                    // that a span with only padding (no text children) still uses
                    // the fallback insertion-point path in AttachInlineFragmentsToLines.
                    int beforeCountForEmptyCheck = items.Count;
                    int beforeCount = items.Count;
                    if (ib.Element != null) {
                        pendingInlineBoxes.Add(ib);
                        // Placeholder; resolved below once we know what came after.
                        pendingInlineNextNode.Add(null);
                    }

                    // CSS Fragmentation L3 §6.1 — inline-axis PBM injection.
                    // Resolve the span's left/right padding+border+margin and
                    // inject spacer items into the item stream so the line-breaker
                    // accounts for them when measuring line widths.
                    //
                    // Under slice (initial): the start PBM lands on the FIRST line
                    // of the span (because the start spacer is the first item
                    // emitted from this span), and the end PBM lands on the LAST
                    // line (end spacer is the last item). The line breaker naturally
                    // handles this: a break in the middle of the span puts the start
                    // spacer on line 1 and the end spacer on the last line — exactly
                    // what CSS Fragmentation L3 §6.1 (slice) requires.
                    //
                    // Under clone: the start/end spacers handle the first and last
                    // lines respectively (same as slice). For every mid-span wrap,
                    // the line breaker adds the end PBM to the outgoing line and
                    // starts the continuation line at the start PBM offset. This
                    // is driven by the CloneSpanStartPbm / CloneSpanEndPbm fields
                    // on every content item inside the span (see below).
                    //
                    // Only inject for element-backed, non-anonymous InlineBoxes.
                    // Anonymous spans (::before/::after boxes with Element=null)
                    // are not subject to box-decoration-break effects.
                    //
                    // thisSpanIsClone: the PBM injection for start/end spacers is
                    // identical for both slice and clone. The difference is that
                    // clone content items carry clone PBM fields so the line breaker
                    // can handle mid-span wraps with both edges.
                    bool thisSpanIsClone = ib.Element != null && IsBoxDecorationBreakClone(ib.Style);
                    if (ib.Element != null) {
                        var (startPbm, endPbm) = ResolveInlinePbm(ib, emParentStyle, availableWidth);
                        // Store resolved PBM on the InlineBox so AttachInline-
                        // FragmentsToLines can expand fragment rects to cover the
                        // PBM area (the spacer items advance state.X but produce no
                        // fragment, so the text-run bbox alone would miss the PBM area).
                        ib.InlinePbmStart = startPbm;
                        ib.InlinePbmEnd   = endPbm;

                        // Inject start-PBM spacer BEFORE the span's first item
                        // (only when there is a start-edge contribution to reserve).
                        if (startPbm > 0) {
                            items.Add(MakeSpacerItem(startPbm));
                        }
                        // beforeCount tracks the item count AFTER the start spacer
                        // so the empty-inline check for the SourceNode fallback
                        // (which compares against items added WITHIN the span, not
                        // the spacers) remains accurate.
                        beforeCount = items.Count;
                    }

                    // CSS Fragmentation L3 §6.1 — clone mode: compute the
                    // accumulated clone PBMs to pass to the recursive call.
                    // Under clone, THIS span's PBMs are added to any outer
                    // clone PBMs already accumulated from enclosing clone spans.
                    // Under slice, the outer values pass through unchanged.
                    double childCloneStartPbm = outerCloneStartPbm;
                    double childCloneEndPbm   = outerCloneEndPbm;
                    if (thisSpanIsClone) {
                        childCloneStartPbm += ib.InlinePbmStart;
                        childCloneEndPbm   += ib.InlinePbmEnd;
                    }

                    // Crossing into an inline element: the inline's own Style
                    // is the new em-parent for ITS children (an `0.5em` on
                    // the span resolves against the span's parent block's
                    // computed fs, which is what `inheritedStyle` already
                    // reflects). Pass inheritedStyle as the new em-parent.
                    // Propagate the nearest ELEMENT-backed span as the owner for
                    // child items: a non-null ib.Element becomes the new owner
                    // (we're entering a real span); a null ib.Element (anonymous
                    // ::before/::after InlineBox) inherits the current spanOwner.
                    Weva.Dom.Element childSpanOwner = ib.Element ?? spanOwner;
                    CollectInlineInner(ib, ib.Style ?? inheritedStyle, inheritedStyle, items, availableWidth, childSpanOwner, childCloneStartPbm, childCloneEndPbm);

                    // Empty inline (e.g. an `<a>` that only wraps a block-
                    // level child triggering inline-splitting): record the
                    // SourceNode of the item that came BEFORE this inline,
                    // so AttachInlineFragmentsToLines can locate the
                    // insertion point on the produced line by finding that
                    // TextRun's right edge.
                    // We check BEFORE the end-PBM spacer is added (below) so
                    // the "items.Count == beforeCount" test correctly detects
                    // spans with no text-producing children (only spacers).
                    // Use beforeCountForEmptyCheck for the SourceNode lookup so
                    // we find the preceding item that was present BEFORE this span.
                    if (ib.Element != null && items.Count == beforeCount && beforeCountForEmptyCheck > 0) {
                        int slot = pendingInlineBoxes.Count - 1;
                        pendingInlineNextNode[slot] = items[beforeCountForEmptyCheck - 1].SourceRun?.SourceNode;
                    }

                    // Inject end-PBM spacer AFTER the span's last item.
                    if (ib.Element != null && ib.InlinePbmEnd > 0) {
                        items.Add(MakeSpacerItem(ib.InlinePbmEnd));
                    }

                    continue;
                }
                if (child is BlockBox bb) {
                    if (bb.IsFloat) {
                        // CSS 2.1 §9.5: a float inside an inline-flow
                        // context is removed from the inline stream and
                        // placed by BlockLayout via the float context.
                        // Skip it here — BlockLayout's outer
                        // pre-LayoutInline pass has already laid the float
                        // out and added a FloatContext.Entry; the line
                        // breaker's per-line probe will see the intrusion
                        // and narrow line boxes accordingly. No item is
                        // emitted for the float in the inline stream.
                        continue;
                    }
                    // CSS 2.1 §9.2.1.1: out-of-flow boxes (position:absolute|fixed)
                    // do not participate in inline flow and must NOT trigger
                    // inline-splitting. They are tracked here and re-attached to
                    // the container's children after the line rebuild (in LayoutInline)
                    // so PositioningPass can locate and place them. BlockLayout must
                    // NOT be called here — their size/position are PositioningPass's
                    // responsibility; premature LayoutBlock would stomp their
                    // shrink-to-fit cache or set wrong dimensions before positioning.
                    if (bb.Position == Weva.Layout.Positioning.PositionType.Absolute
                        || bb.Position == Weva.Layout.Positioning.PositionType.Fixed) {
                        pendingOofBoxes.Add(bb);
                        continue;
                    }
                    if (bb.IsInlineBlock) {
                        var atom = MakeAtomItem(bb, availableWidth, inheritedStyle);
                        // CSS Fragmentation L3 §6.1: inline-block atoms inside a
                        // clone span also carry clone PBM so the line breaker can
                        // apply both edges if a wrap occurs after this atom.
                        if (atom != null) {
                            atom.CloneSpanStartPbm = outerCloneStartPbm;
                            atom.CloneSpanEndPbm   = outerCloneEndPbm;
                            items.Add(atom);
                        }
                        continue;
                    }
                    // Inline-splitting (CSS Display Module Level 3 §2): a
                    // block-level descendant inside an inline element forces
                    // the IFC to split. The current segment of inline items
                    // becomes one or more lines, the block sits as its own
                    // line-stack child, and any remaining inline items become
                    // a fresh segment after it. For v1 we don't split the
                    // inline element's own decoration (border/background) —
                    // an acceptable simplification per PLAN §11.
                    if (BlockLayout != null) {
                        BlockLayout.LayoutBlock(bb, availableWidth, bb.Parent?.Style ?? inheritedStyle);
                    }
                    segmentBreaks.Add(items.Count);
                    segmentBlocks.Add(bb);
                    continue;
                }
            }
        }

        // Performs a block layout on the inline-block child to determine its
        // size, then packages it as a LineBreaker atom item. When the inline-
        // block has no explicit width, we shrink-to-fit using BlockLayout's
        // intrinsic-content-width helper.
        LineBreaker.Item MakeAtomItem(BlockBox atom, double availableWidth, ComputedStyle inheritedStyle) {
            if (BlockLayout == null) return null;

            bool widthIsExplicit = false;
            // CSS Sizing L3 §5.1: fit-content(<length-percentage>) function form.
            // Resolve now so we can route to the shrink-to-fit branch (not
            // widthIsExplicit) and have the argument available for clamping.
            double fitContentArgPx = -1; // negative = not fit-content
            if (atom.Style != null) {
                string w = atom.Style.Get(CssProperties.WidthId);
                // CSS Sizing L3 §9.1-§9.2 — `min-content` / `max-content` /
                // `fit-content` keywords on an inline-block must take the
                // shrink-to-fit path. StyleResolver already collapses these
                // to auto (intrinsic sizing isn't generally implemented), so
                // the explicit-width branch's eventual width resolution
                // would have evaluated them as `auto` anyway — but THIS
                // check ran on the raw string and treated them as explicit,
                // pushing the atom through LayoutBlock with availableWidth
                // as the constraint and ballooning it to container width.
                // Treat intrinsic-keyword widths as auto so the proper
                // shrink-to-fit branch runs. Also treat fit-content(...)
                // function form as requiring shrink-to-fit (not explicit).
                if (!string.IsNullOrEmpty(w) && w != "auto"
                    && w != "min-content" && w != "max-content" && w != "fit-content") {
                    // Check for fit-content(<length-percentage>) function form.
                    var widthParsed = atom.Style.GetParsed(CssProperties.WidthId);
                    if (widthParsed is CssFunctionCall wfn && wfn.Name == "fit-content") {
                        // Route to shrink-to-fit with argument clamp.
                        double fs = StyleResolver.FontSizePx(atom.Style, atom.Parent?.Style ?? inheritedStyle, ctx);
                        var r = StyleResolver.ResolveLengthFromParsed(widthParsed, ctx, fs, availableWidth);
                        if (r.Kind == StyleResolver.LengthKind.FitContent) {
                            fitContentArgPx = r.Pixels;
                        }
                        // widthIsExplicit stays false → shrink-to-fit path.
                    } else {
                        widthIsExplicit = true;
                    }
                }
            }

            if (widthIsExplicit) {
                BlockLayout.LayoutBlock(atom, availableWidth, atom.Parent?.Style ?? inheritedStyle);
            } else {
                // Shrink-to-fit: lay out at the unconstrained width, measure max
                // intrinsic content, then re-layout at that width.
                //
                // The first LayoutBlock pass invokes LayoutInline which replaces
                // atom.Children with the produced LineBox-es (CollectInline only
                // recognizes TextRun / InlineBox / BlockBox — not LineBox). If we
                // simply called RelayoutContentAt on the lined-out atom, the second
                // CollectInline pass would walk the LineBox-es, find no inline
                // items, and produce an empty LineBox — the badge text disappears
                // even though the box itself still has padding+background.
                //
                // Snapshot the atom's raw inline children before pass 1 so we can
                // restore them before pass 2. Stack-discipline against the shared
                // scratch list lets nested inline-block atoms (atom-inside-atom)
                // each own their own slice without colliding.
                var snapshotBuf = scratch.AtomShrinkToFitSnapshot;
                int snapshotStart = snapshotBuf.Count;
                bool atomContainedInlines = atom.ContainsInlines;
                if (atomContainedInlines) {
                    var rawKids = atom.ChildList;
                    for (int i = 0; i < rawKids.Count; i++) snapshotBuf.Add(rawKids[i]);
                }

                BlockLayout.LayoutBlock(atom, availableWidth, atom.Parent?.Style ?? inheritedStyle);
                double frame = atom.PaddingLeft + atom.PaddingRight + atom.BorderLeft + atom.BorderRight;
                // Resolve the atom's font-size once; needed for both the
                // inline-size containment placeholder and ClampWidthByMinMax.
                double atomFs = itemFsFor(atom, inheritedStyle);
                double maxContent = 0;
                if (atom is Weva.Layout.Flex.FlexBox || atom is Weva.Layout.Grid.GridBox) {
                    // CSS Flexbox L1 §9.9 / CSS Grid L1: inline-flex / inline-grid
                    // containers must shrink to their own max-content intrinsic, not
                    // to max(child.Width). For row inline-flex, max-content = sum of
                    // items + gaps; PositioningPass.MaxContentWidth already implements
                    // this via FlexIntrinsicInline/GridIntrinsicInline and returns the
                    // border-box width. Subtract the frame to get content-only so the
                    // `fitted = maxContent + frame` line below remains correct. B7d fix.
                    //
                    // Pass ctx + atomFs so MaxContentWidth can activate the
                    // HasInlineSize guard and resolve contain-intrinsic-width
                    // (CSS Containment L2 §3.3 + CSS Sizing L4 §5). Without
                    // ctx the guard fires but returns 0 unconditionally.
                    maxContent = Weva.Layout.Positioning.PositioningPass.MaxContentWidth(atom, ctx, atomFs) - frame;
                    if (maxContent < 0) maxContent = 0;
                } else if (atom.Style != null &&
                           Weva.Layout.Containment.ContainmentResolver.HasInlineSize(atom.Style)) {
                    // CSS Containment L2 §3.3 / inline-size: the element's
                    // intrinsic inline-size contribution is treated as if it
                    // were empty.  For a plain-block inline-block atom this
                    // means maxContent = contain-intrinsic-width (default 0)
                    // rather than the widths of its laid-out line boxes.
                    // (CSS Sizing L4 §5 — placeholder hint.)
                    // The frame is added by the `fitted = maxContent + frame`
                    // path below, matching the plain-block caller convention.
                    maxContent = Weva.Layout.Containment.ContainmentResolver
                        .ResolveContainIntrinsicWidthPx(atom.Style, ctx, atomFs);
                } else if (atom.ContainsInlines) {
                    var atomChildren = atom.ChildList;
                    for (int i = 0; i < atomChildren.Count; i++) {
                        var line = atomChildren[i];
                        if (line is LineBox lb && lb.Width > maxContent) maxContent = lb.Width;
                    }
                } else {
                    var atomChildren = atom.ChildList;
                    for (int i = 0; i < atomChildren.Count; i++) {
                        var c = atomChildren[i];
                        if (c is BlockBox cb) {
                            double childOuter = cb.Width + cb.MarginLeft + cb.MarginRight;
                            if (childOuter > maxContent) maxContent = childOuter;
                        }
                    }
                }

                double fitted;
                if (fitContentArgPx >= 0) {
                    // CSS Sizing L3 §5.1: fit-content(<arg>) = min(max-content, max(min-content, arg)).
                    // Probe min-content at width=1 (forces all-word wrapping).
                    BlockLayout.RelayoutContentAt(atom, 1);
                    double minContent = 0;
                    if (atom.ContainsInlines) {
                        var atomChildren = atom.ChildList;
                        for (int i = 0; i < atomChildren.Count; i++) {
                            var line = atomChildren[i];
                            if (line is LineBox lb && lb.Width > minContent) minContent = lb.Width;
                        }
                    } else {
                        var atomChildren = atom.ChildList;
                        for (int i = 0; i < atomChildren.Count; i++) {
                            var c = atomChildren[i];
                            if (c is BlockBox cbb) {
                                double childOuter = cbb.Width + cbb.MarginLeft + cbb.MarginRight;
                                if (childOuter > minContent) minContent = childOuter;
                            }
                        }
                    }
                    double maxContentBB = maxContent + frame;
                    double minContentBB = minContent + frame;
                    if (minContentBB < frame) minContentBB = frame;
                    if (maxContentBB < frame) maxContentBB = frame;
                    fitted = System.Math.Min(maxContentBB, System.Math.Max(minContentBB, fitContentArgPx));
                    if (fitted < 0) fitted = 0;
                } else {
                    fitted = maxContent + frame;
                    if (fitted < 0) fitted = 0;
                    if (fitted > availableWidth) fitted = availableWidth;
                }
                fitted = ClampWidthByMinMax(atom, fitted, availableWidth, atomFs);
                atom.Width = fitted;

                // Restore the raw inline children before the second pass.
                // Pass 2's LayoutInline allocates fresh LineBox-es and TextRuns;
                // the pass-1 boxes orphaned here are not in the survivor tree, so
                // BoxPool.EndPass will recycle them along with the rest of the
                // pass's misses. ContainsInlines is preserved across the swap.
                if (atomContainedInlines) {
                    atom.ClearChildren();
                    for (int i = snapshotStart; i < snapshotBuf.Count; i++) {
                        atom.AddChild(snapshotBuf[i]);
                    }
                    atom.ContainsInlines = true;
                    snapshotBuf.RemoveRange(snapshotStart, snapshotBuf.Count - snapshotStart);
                }

                BlockLayout.RelayoutContentAt(atom, fitted);
                // CSS Containment L2 §3.3: for inline-flex/inline-grid atoms
                // with HasInlineSize, FlexLayout.FinalizeContainerMainSize (or
                // GridLayout's equivalent) may overwrite the atom's width back
                // to the item-sum intrinsic during the second RelayoutContentAt
                // pass.  Re-stamp the containment-derived fitted width after the
                // second layout completes so the atom's reported Width matches
                // the contained border-box, not the content-derived flex sum.
                if (atom.Style != null &&
                    Weva.Layout.Containment.ContainmentResolver.HasInlineSize(atom.Style)) {
                    atom.Width = fitted;
                }
            }

            double itemFs = atom.Style != null ? StyleResolver.FontSizePx(atom.Style, atom.Parent?.Style, ctx) : ctx.RootFontSizePx;
            var fm = ctx.GetMetrics(atom.Style?.Get(CssProperties.FontFamilyId));
            // CSS 2.1 §10.8.1: the baseline of an `inline-block` is the
            // baseline of its last in-flow line box, measured from the atom's
            // outer top edge. That's `last.Y + last.Baseline` — last.Y already
            // includes the atom's padding-top + border-top (BlockLayout sets
            // it from `topInner`), and last.Baseline is the line's MaxAscent.
            //
            // Fallbacks (in order): if the atom has no LineBox child (block-
            // only content, or the LineBox-detection missed), use the bottom
            // of the box's content area; if even that is unavailable, the
            // atom's full height. Using full height is the wrong default — it
            // pins the bottom of the pill to the surrounding text baseline,
            // pushing the pill UP relative to its siblings.
            double baseline;
            // CSS 2.1 §10.8.1 exception: when the inline-block's `overflow`
            // (or overflow-x/-y) computes to anything other than `visible`,
            // the baseline is the bottom margin edge — measured from the
            // atom's top border-edge, that's Height + MarginBottom.
            bool clipsOverflow = Weva.Layout.Scrolling.ScrollContainerLookup.HasNonVisibleOverflow(atom);
            LineBox last = null;
            if (!clipsOverflow) {
                var finalChildren = atom.ChildList;
                for (int ci = finalChildren.Count - 1; ci >= 0; ci--) {
                    if (finalChildren[ci] is LineBox lb) { last = lb; break; }
                }
            }
            if (clipsOverflow) {
                baseline = atom.Height + atom.MarginBottom;
            } else if (last != null) {
                baseline = last.Y + last.Baseline;
            } else {
                // No line box (e.g. atom holds only block-level children).
                // Synthesize a baseline at content-bottom: the bottom of the
                // content area sits below the surrounding text baseline by
                // the atom's bottom padding/border, which matches Blink.
                baseline = atom.Height - atom.PaddingBottom - atom.BorderBottom;
                if (baseline < 0) baseline = atom.Height;
            }

            var it = breaker.RentItem();
            it.Text = null;
            it.Style = atom.Style ?? inheritedStyle;
            it.FontSize = itemFs;
            it.FontFamily = atom.Style?.Get(CssProperties.FontFamilyId);
            it.Color = atom.Style?.Get(CssProperties.ColorId);
            it.WhiteSpace = "normal";
            it.Metrics = fm;
            it.AtomBox = atom;
            it.AtomOuterWidth = atom.Width + atom.MarginLeft + atom.MarginRight;
            it.AtomBaseline = baseline;
            it.AtomAboveBaseline = ResolveAtomAboveBaseline(atom, inheritedStyle, itemFs, baseline);
            // Diagnostic — fires when UILayoutDiagnostics.Enabled and the
            // atom's element class matches MatchClassContains. Captures the
            // INLINE-LEVEL contribution of an inline-block / inline-flex
            // atom (e.g. .reward-chip inside .objective-reward) so we can
            // verify the atom is reporting its full outer height to the
            // surrounding line box. If atom.Height is correct here but
            // line.Height ends up smaller in FinishLine, the bug is in
            // LineBreaker. If atom.Height is wrong here, the bug is
            // upstream in BlockLayout.LayoutBlock(atom).
            if (Weva.Diagnostics.UILayoutDiagnostics.ShouldTrace(atom.Element)) {
                Weva.Diagnostics.UILayoutDiagnostics.TraceFor(atom.Element, "InlineLayout.MakeAtomItem",
                    $"atom.Width={atom.Width} atom.Height={atom.Height} " +
                    $"padding=({atom.PaddingTop},{atom.PaddingRight},{atom.PaddingBottom},{atom.PaddingLeft}) " +
                    $"border=({atom.BorderTop},{atom.BorderRight},{atom.BorderBottom},{atom.BorderLeft}) " +
                    $"margin=({atom.MarginTop},{atom.MarginRight},{atom.MarginBottom},{atom.MarginLeft}) " +
                    $"baseline={baseline} aboveBaseline={it.AtomAboveBaseline}");
            }
            return it;
        }

        // CSS 2.1 §10.8.1: resolve `vertical-align` on an inline-level atom
        // into the distance from the line baseline up to the atom's TOP edge
        // (the value `LineBreaker.AddAtomFragment` feeds into the line's
        // MaxAscent and `FinishLine` uses for atom placement). Keywords
        // resolve against the parent IFC's font metrics; length / percentage
        // resolve against the atom's own font-size / line-height.
        //   baseline   - atom baseline coincides with line baseline.
        //   sub        - lower by ~0.2 x parent font-size.
        //   super      - raise by ~0.3 x parent font-size.
        //   middle     - atom midpoint at parent baseline + x-height/2.
        //   text-top   - atom top at parent content-area top.
        //   text-bottom- atom bottom at parent content-area bottom.
        //   <length>   - raise baseline by that length.
        //   <percentage> - percent of the atom's used line-height.
        double ResolveAtomAboveBaseline(BlockBox atom, ComputedStyle inheritedStyle, double atomFs, double atomBaseline) {
            string raw = atom.Style?.Get("vertical-align");
            if (string.IsNullOrEmpty(raw) || raw == "baseline") return atomBaseline;
            ComputedStyle parentStyle = atom.Parent?.Style ?? inheritedStyle;
            double parentFs = parentStyle != null
                ? StyleResolver.FontSizePx(parentStyle, atom.Parent?.Parent?.Style, ctx)
                : ctx.RootFontSizePx;
            var parentFm = ResolveMetrics(parentStyle);
            double parentAscent = parentFm != null ? parentFm.Ascent(parentFs) : parentFs * 0.8;
            double parentDescent = parentFm != null ? parentFm.Descent(parentFs) : parentFs * 0.2;
            double height = atom.Height;
            string s = CssStringUtil.ToLowerInvariantOrSame(raw.Trim());
            switch (s) {
                case "baseline": return atomBaseline;
                case "sub": return atomBaseline - parentFs * 0.2;
                case "super": return atomBaseline + parentFs * 0.3;
                case "middle": return height * 0.5 - parentFs * 0.5 * 0.5;
                case "text-top": return parentAscent;
                case "text-bottom": return height - parentDescent;
            }
            if (CssValue.TryParse(raw, out var v)) {
                if (v is CssPercentage p) {
                    double lh = StyleResolver.LineHeightPx(atom.Style, atomFs, ctx, ResolveMetrics(atom.Style));
                    return atomBaseline + lh * p.Value * 0.01;
                }
                if (v is CssLength l) {
                    if (l.Unit == CssLengthUnit.Percent) {
                        double lh = StyleResolver.LineHeightPx(atom.Style, atomFs, ctx, ResolveMetrics(atom.Style));
                        return atomBaseline + lh * l.Value * 0.01;
                    }
                    // H5b: include line-height so a vertical-align value
                    // expressed in `lh` resolves against the atom's cascaded
                    // line-height, not the 1.2 * atomFs fallback.
                    double atomLh = StyleResolver.LineHeightPx(atom.Style, atomFs, ctx, ResolveMetrics(atom.Style));
                    var lc = ctx.ToLengthContext(atomFs, atomFs, atomLh);
                    return atomBaseline + l.ToPixels(lc);
                }
                if (v is CssNumber n) return atomBaseline + n.Value;
            }
            return atomBaseline;
        }

        double ClampWidthByMinMax(BlockBox atom, double width, double containingBlockWidth, double fs) {
            if (atom?.Style == null) return width;
            bool borderBox = UsesBorderBox(atom.Style);
            double frame = atom.PaddingLeft + atom.PaddingRight + atom.BorderLeft + atom.BorderRight;
            var minR = StyleResolver.ResolveLengthFromParsed(
                atom.Style.GetParsed(CssProperties.MinWidthId), ctx, fs, containingBlockWidth);
            var maxR = StyleResolver.ResolveLengthFromParsed(
                atom.Style.GetParsed(CssProperties.MaxWidthId), ctx, fs, containingBlockWidth);
            if (maxR.Kind == StyleResolver.LengthKind.Length) {
                double maxPx = borderBox ? maxR.Pixels : maxR.Pixels + frame;
                if (width > maxPx) width = maxPx;
            } else if (maxR.Kind == StyleResolver.LengthKind.Percent) {
                double maxPx = containingBlockWidth * (maxR.Percent * 0.01);
                if (!borderBox) maxPx += frame;
                if (width > maxPx) width = maxPx;
            }
            if (minR.Kind == StyleResolver.LengthKind.Length) {
                double minPx = borderBox ? minR.Pixels : minR.Pixels + frame;
                if (width < minPx) width = minPx;
            } else if (minR.Kind == StyleResolver.LengthKind.Percent) {
                double minPx = containingBlockWidth * (minR.Percent * 0.01);
                if (!borderBox) minPx += frame;
                if (width < minPx) width = minPx;
            }
            return width;
        }

        double itemFsFor(BlockBox atom, ComputedStyle inheritedStyle) {
            return atom.Style != null
                ? StyleResolver.FontSizePx(atom.Style, atom.Parent?.Style ?? inheritedStyle, ctx)
                : ctx.RootFontSizePx;
        }

        static bool UsesBorderBox(ComputedStyle style) {
            var parsed = style?.GetParsed(CssProperties.BoxSizingId);
            if (parsed is Weva.Css.Values.CssKeyword k) return k.Identifier == "border-box";
            if (parsed is Weva.Css.Values.CssIdentifier id) return id.Name == "border-box";
            return style?.Get(CssProperties.BoxSizingId) == "border-box";
        }

        // CSS Fragmentation L3 §6.1 — true when box-decoration-break is `clone`.
        // The initial value is `slice`; any other computed value (including
        // missing / null) also resolves to slice.
        static bool IsBoxDecorationBreakClone(ComputedStyle style) {
            if (style == null) return false;
            var raw = style.Get(CssProperties.BoxDecorationBreakId);
            return raw == "clone";
        }

        // CSS Fragmentation L3 §6.1 — resolve the inline-axis PBM for an
        // InlineBox element (padding + border + margin on left and right edges).
        // `availableWidth` is the containing block width for percentage resolution.
        // `emParentStyle` is the em-parent for font-size resolution on this element.
        //
        // Returns (startPbm, endPbm) where:
        //   startPbm = PaddingLeft + BorderLeft + MarginLeft
        //   endPbm   = PaddingRight + BorderRight + MarginRight
        (double startPbm, double endPbm) ResolveInlinePbm(InlineBox ib, ComputedStyle emParentStyle, double availableWidth) {
            var style = ib.Style;
            if (style == null) return (0, 0);

            double fs = StyleResolver.FontSizePx(style, emParentStyle, ctx);

            // Padding (never negative; percentage resolves against containing-block width).
            // StyleResolver.BoxSides handles the shorthand expansion (padding: 10px 20px).
            var padSides = StyleResolver.BoxSides(style,
                CssProperties.PaddingId,
                CssProperties.PaddingTopId, CssProperties.PaddingRightId,
                CssProperties.PaddingBottomId, CssProperties.PaddingLeftId);
            double padLeft  = StyleResolver.ResolveLengthPx(padSides.left,  0, style, ctx, fs, availableWidth);
            double padRight = StyleResolver.ResolveLengthPx(padSides.right, 0, style, ctx, fs, availableWidth);

            // Border (zero when border-style is none/hidden).
            double bordLeft  = ResolveBorderEdgePx(style, CssProperties.BorderLeftStyleId,  CssProperties.BorderLeftWidthId,  fs);
            double bordRight = ResolveBorderEdgePx(style, CssProperties.BorderRightStyleId, CssProperties.BorderRightWidthId, fs);

            // Margin (`auto` resolves to 0 for inline-level elements per CSS 2.1 §10.3.1).
            var marSides = StyleResolver.BoxSides(style,
                CssProperties.MarginId,
                CssProperties.MarginTopId, CssProperties.MarginRightId,
                CssProperties.MarginBottomId, CssProperties.MarginLeftId);
            double marLeft  = StyleResolver.ResolveLengthPx(marSides.left,  0, style, ctx, fs, availableWidth);
            double marRight = StyleResolver.ResolveLengthPx(marSides.right, 0, style, ctx, fs, availableWidth);

            double startPbm = padLeft  + bordLeft  + marLeft;
            double endPbm   = padRight + bordRight + marRight;
            // Negative margins are valid CSS; clamp to 0 so we never subtract
            // space from the line (negative inline margins are a v2 concern).
            if (startPbm < 0) startPbm = 0;
            if (endPbm   < 0) endPbm   = 0;
            return (startPbm, endPbm);
        }

        double ResolveBorderEdgePx(ComputedStyle style, int styleId, int widthId, double fs) {
            string styleVal = style?.Get(styleId);
            if (string.IsNullOrEmpty(styleVal) || styleVal == "none" || styleVal == "hidden") return 0;
            var parsedWidth = style.GetParsed(widthId);
            if (parsedWidth != null) return StyleResolver.ResolveBorderWidth(parsedWidth, fs, ctx);
            string widthRaw = style.Get(widthId);
            return StyleResolver.ResolveBorderWidth(widthRaw, fs, ctx);
        }

        // Create a pure-advance spacer item that reserves `width` px on the
        // line without producing a TextRun or atom fragment. Used to reserve
        // inline-axis PBM edges per CSS Fragmentation L3 §6.1.
        LineBreaker.Item MakeSpacerItem(double width) {
            var it = breaker.RentItem();
            // Text=null and AtomBox=null signals the spacer path in AppendItem.
            it.SpacerWidth = width;
            return it;
        }

        LineBreaker.Item MakeItem(string text, ComputedStyle style, ComputedStyle parentStyle, TextRun source) {
            // Pass the inline subtree's em-parent style so an `em` value on
            // the run's font-size resolves against the parent element's
            // computed fs. Passing null falls back to the document root,
            // which silently shrinks every em-sized text run by `<root>/parent`
            // and only shows up downstream as visibly-too-small glyphs.
            double fs = StyleResolver.FontSizePx(style, parentStyle, ctx);
            string fam = style?.Get(CssProperties.FontFamilyId);
            int fontWeight = Paint.Conversion.TextRunResolver.ResolveFontWeight(style);
            Paint.FontStyle fontStyle = Paint.Conversion.TextRunResolver.ResolveFontStyle(style);
            string color = style?.Get(CssProperties.ColorId);
            string ws = StyleResolver.WhiteSpace(style);
            // word-wrap is the legacy alias for overflow-wrap; if both are set, the
            // CSS spec says overflow-wrap wins (it's the canonical name).
            string ow = style?.Get(CssProperties.OverflowWrapId);
            if (string.IsNullOrEmpty(ow) || ow == "normal") {
                string ww = style?.Get(CssProperties.WordWrapId);
                if (!string.IsNullOrEmpty(ww) && ww != "normal") ow = ww;
            }
            var fm = ctx.GetMetrics(fam);
            // text-transform is applied PRIOR to shaping per CSS Text Module L3 §3,
            // so the line breaker's measurements and the rendered glyph stream agree.
            string transformedText = Paint.Conversion.TextRunResolver.ApplyTextTransform(style, text);
            // letter-spacing resolves against the run's font-size (em context). The
            // resolved px value is propagated to the LineBreaker.Item so MeasureWith-
            // Spacing accounts for it, and to the produced TextRun so the paint agent
            // can keep its glyph baking aligned with the line's intrinsic width.
            // H5b: thread the run's cascaded line-height so `lh`-typed
            // letter-spacing / word-spacing / tab-size resolve correctly.
            double runLh = StyleResolver.LineHeightPx(style, fs, ctx, fm);
            var lengthCtx = ctx.ToLengthContext(fs, fs, runLh);
            double letterSpacingPx = Paint.Conversion.TextRunResolver.ResolveLetterSpacingPx(style, lengthCtx);
            double wordSpacingPx = Paint.Conversion.TextRunResolver.ResolveWordSpacingPx(style, lengthCtx);
            var it = breaker.RentItem();
            it.Text = transformedText;
            it.Style = style;
            it.FontSize = fs;
            it.FontFamily = fam;
            it.FontWeight = fontWeight;
            it.FontStyle = fontStyle;
            it.Color = color;
            it.WhiteSpace = ws;
            it.WordBreak = style?.Get(CssProperties.WordBreakId);
            it.LineBreak = style?.Get(CssProperties.LineBreakId);
            it.OverflowWrap = ow;
            it.Hyphens = style?.Get(CssProperties.HyphensId);
            it.TabSizeSpaces = StyleResolver.TabSizeSpaces(style, fm, fs, lengthCtx);
            it.LetterSpacingPx = letterSpacingPx;
            it.WordSpacingPx = wordSpacingPx;
            it.Metrics = fm;
            it.SourceRun = source;
            return it;
        }

        // --- W5 UAX #9 bidi visual-order reordering -------------------------
        //
        // Called after LineBreaker.BreakInto has produced a set of LineBoxes.
        // Each LineBox has a flat list of children that are either TextRun or
        // BlockBox (inline-block atoms). This method reorders the X positions
        // of those children so that they appear in visual (left-to-right screen)
        // order even when the underlying text is right-to-left.
        //
        // Integration point rationale: the reordering is applied after line
        // breaking (not before) because UAX #9 rule L2 is specified per line,
        // not per paragraph: "Starting from the highest level found in the
        // text and lowering one level at a time, reverse any contiguous
        // sequence of characters that are at that level or higher." Applying
        // this per-paragraph (before breaking) would produce wrong results at
        // line boundaries where an RTL word wraps to a new line.
        //
        // Only TextRun and atom (BlockBox) X values are mutated; Text content
        // and SourceNode are preserved so selection/editing code, which
        // operates on logical order, is unaffected.
        //
        // Fast-path guarantee: when baseIsRtl is false AND no item text
        // contains an R-class codepoint, the caller has already skipped this
        // method (needsBidi == false). This ensures LTR-only text is
        // BYTE-IDENTICAL in layout to the pre-bidi code path.
        void ApplyBidiReorderToLines(List<LineBox> lines, bool baseIsRtl) {
            if (lines == null || lines.Count == 0) return;

            for (int li = 0; li < lines.Count; li++) {
                var line = lines[li];
                var children = line.ChildList;
                int childCount = children.Count;
                if (childCount == 0) continue;

                // --- Build a flat list of (x, width, childIndex) slots ------
                // We treat every TextRun and atom BlockBox as one "bidi slot".
                // A slot has a text payload for classification; atoms are treated
                // as L (inline-block content is independently bidi-ed inside the
                // atom's own layout pass).
                //
                // UAX #9 L2 per-line: build the full line text by concatenating
                // TextRun text values, run BidiRuns.Analyze, then remap X offsets.

                // Build concatenated line text and slot index map.
                // slotTextStart[k] = char offset of children[k] in lineText.
                // slotTextLen[k]   = number of chars contributed by children[k].
                int[] slotTextStart = new int[childCount];
                int[] slotTextLen   = new int[childCount];
                var lineSb = new System.Text.StringBuilder(32);
                for (int ci = 0; ci < childCount; ci++) {
                    slotTextStart[ci] = lineSb.Length;
                    var ch = children[ci];
                    if (ch is TextRun tr) {
                        string t = tr.Text ?? "";
                        slotTextLen[ci] = t.Length;
                        lineSb.Append(t);
                    } else {
                        // Atom: contribute a single L-class placeholder ' '.
                        slotTextLen[ci] = 1;
                        lineSb.Append(' ');
                    }
                }
                string lineText = lineSb.ToString();
                if (lineText.Length == 0) continue;

                // Classify the ENTIRE line text at once so cross-slot bidi
                // context (e.g. an RTL digit following a Hebrew word that was
                // split across two adjacent TextRuns) resolves correctly.
                BidiRuns.Analyze(lineText, baseIsRtl, bidiLogical);
                if (bidiLogical.Count == 0) continue;

                // Check fast path: if every logical run is level 0 (LTR)
                // and base is LTR, nothing moves.
                if (!baseIsRtl) {
                    bool anyRtl = false;
                    for (int ri = 0; ri < bidiLogical.Count; ri++) {
                        if (bidiLogical[ri].Level != 0) { anyRtl = true; break; }
                    }
                    if (!anyRtl) continue;
                }

                // Reorder the logical runs into visual order (UAX #9 L2).
                BidiRuns.Reorder(bidiLogical, baseIsRtl, bidiVisual);

                // Map each child slot to its dominant bidi level by finding
                // which logical run contains the majority of its characters.
                // For single-script TextRuns (the common case) the slot sits
                // entirely within one logical run.
                int[] slotLevel = new int[childCount];
                for (int ci = 0; ci < childCount; ci++) {
                    int sStart = slotTextStart[ci];
                    int sLen   = slotTextLen[ci];
                    // Find the logical run that contains the slot's start.
                    slotLevel[ci] = 0;
                    for (int ri = 0; ri < bidiLogical.Count; ri++) {
                        var lr = bidiLogical[ri];
                        if (sStart >= lr.Start && sStart < lr.Start + lr.Length) {
                            slotLevel[ci] = lr.Level;
                            break;
                        }
                    }
                }

                // Build a "visual slot order" from the visual run list.
                // Each child slot maps to one bidi-visual-run slot.  Slots
                // that span multiple bidi runs get the first run's level for
                // the purpose of visual ordering (inter-run splits are
                // uncommon in word-level tokenized output).
                //
                // Algorithm: group consecutive same-level child slots then
                // apply the L2 reversal.  We do this by sorting slot indices
                // in the order the visual runs traverse character space.
                //
                // Build a reordered child array by following visual run char
                // ranges and picking the child slots whose text lives in that
                // range.
                var reorderedSlots = new int[childCount];
                for (int ci = 0; ci < childCount; ci++) reorderedSlots[ci] = ci;

                // Build ordered slot list by visual-run character ranges.
                int visualIdx = 0;
                for (int ri = 0; ri < bidiVisual.Count && visualIdx < childCount; ri++) {
                    var vr = bidiVisual[ri];
                    // Pick all child slots whose text starts within this visual run.
                    for (int ci = 0; ci < childCount; ci++) {
                        int sStart = slotTextStart[ci];
                        if (sStart >= vr.Start && sStart < vr.Start + vr.Length) {
                            reorderedSlots[visualIdx++] = ci;
                        }
                    }
                }

                // Compute new X positions from the reordered slot sequence.
                // Preserve the line's leftmost X (line.X is the float offset;
                // fragments start at 0 relative to the line, and the line's
                // own X is the BFC offset placed by BlockLayout).
                double cursor = 0;
                // Find the starting X: the leftmost fragment's existing X.
                // In a standard LTR line all fragments start at >= 0; in a
                // text-indent line the first fragment may start at indent.
                // We preserve the indent by starting cursor at the first
                // fragment's original X (if any).
                if (childCount > 0) {
                    var firstChild = children[0];
                    cursor = (firstChild is TextRun tr0) ? tr0.X : (firstChild is BlockBox bb0) ? bb0.X : 0;
                }
                // Collect widths per slot.
                var slotWidths = new double[childCount];
                for (int ci = 0; ci < childCount; ci++) {
                    var ch = children[ci];
                    slotWidths[ci] = (ch is TextRun tr2) ? tr2.Width :
                                     (ch is BlockBox bb2) ? (bb2.Width + bb2.MarginLeft + bb2.MarginRight) : 0;
                }
                // Apply new X positions in visual order.
                for (int vi = 0; vi < childCount; vi++) {
                    int ci = reorderedSlots[vi];
                    var ch = children[ci];
                    double w = slotWidths[ci];
                    if (ch is TextRun tr3) {
                        tr3.X = cursor;
                    } else if (ch is BlockBox bb3) {
                        bb3.X = cursor + bb3.MarginLeft;
                    }
                    cursor += w;
                }
            }
        }
    }
}
