using System.Collections.Generic;
using System.Text;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using Weva.Paint;

namespace Weva.Layout {
    internal sealed class LineBreaker {
        public sealed class Item {
            public string Text;
            public ComputedStyle Style;
            public InlineBox InlineParent;
            public double FontSize;
            public string FontFamily;
            public int FontWeight;
            public FontStyle FontStyle;
            public string Color;
            public string WhiteSpace;
            public IFontMetrics Metrics;
            public Boxes.TextRun SourceRun;
            // When non-null, this item was collected from inside a
            // ::before/::after anonymous InlineBox (Element == null) that is
            // a child of the span identified by OwnerElement. LineBreaker uses
            // it to set the emitted line TextRun's Element to OwnerElement when
            // the SourceRun has no Element of its own — so pseudo-content runs
            // count toward the span's bounding box in AttachInlineFragmentsToLines.
            public Weva.Dom.Element OwnerElement;

            // CSS Text Module Level 3 word-break / overflow-wrap.
            //   word-break: normal     - break only at word boundaries (default).
            //   word-break: break-all  - break between any two characters; long words wrap freely.
            //   word-break: keep-all   - NO break between CJK ideographs; Latin word-break still applies.
            //   overflow-wrap: normal  - no break inside a word (default).
            //   overflow-wrap: break-word - break inside a word only when it would otherwise
            //                                overflow on a fresh line of its own.
            //   overflow-wrap: anywhere - break inside a word whenever it would overflow,
            //                              same as break-all but also affects min-content sizing.
            // We don't yet track the min-content distinction, so `anywhere` and `break-all`
            // are observably identical here. The break-all/anywhere path always splits when
            // the word doesn't fit; the break-word path only splits if the word is alone
            // on the line (the spec's "would otherwise overflow" condition).
            //
            // CSS Text L3 §5.3 line-break: controls kinsoku strictness for CJK.
            //   line-break: auto / normal / loose / strict - kinsoku applies (standard set in v1).
            //   line-break: anywhere                       - kinsoku prohibitions are lifted.
            public string WordBreak;
            public string LineBreak;
            public string OverflowWrap;
            public string Hyphens;
            public double TabSizeSpaces;

            // CSS Text Module Level 3 §10.1: extra advance applied AFTER each
            // grapheme. Value is already em → px resolved by the caller. The line
            // breaker adds this delta when measuring tokens and stores it on the
            // produced TextRun so the paint agent stays consistent.
            public double LetterSpacingPx;

            // CSS Text Module Level 3 §10.2: extra advance applied to each
            // word-separator character (currently just U+0020 in this
            // engine — the full Unicode set in §10.2 is CJK-heavy and v1
            // doesn't shape those scripts independently). Cached on the
            // Item so MeasureCached can scan the measured text for spaces
            // without re-resolving from the style each call.
            public double WordSpacingPx;

            // Non-null when this item represents an inline-block atom (or
            // inline-flex/inline-grid) embedded in the IFC. The atom is placed
            // verbatim on a line; it is NOT split, broken, or text-tokenized.
            public BlockBox AtomBox;
            // Outer width of the atom (border-box width + horizontal margins).
            public double AtomOuterWidth;
            // Atom's first-baseline offset from its top edge. Per spec the
            // baseline of an inline-block is the bottom of its content. We
            // approximate as the box height.
            public double AtomBaseline;

            // CSS 2.1 §10.8.1 vertical-align placement: distance from the
            // line baseline up to the atom's TOP edge. For `baseline` this
            // equals AtomBaseline (atom baseline coincides with line
            // baseline); other vertical-align keywords / lengths translate
            // the atom along the block axis and shift this value. Resolved
            // by MakeAtomItem so AddAtomFragment + FinishLine can grow the
            // line's MaxAscent / MaxDescent uniformly. height - this value
            // is the atom's below-baseline extent.
            //
            // NaN = use the AtomBaseline fallback (baseline alignment).
            // Reset() restores NaN so callers that build Items by hand
            // (e.g. the F6 line-height-includes-text-descent regression
            // in InlineLayoutTests) keep the legacy AtomBaseline path.
            public double AtomAboveBaseline = double.NaN;

            // CSS Fragmentation L3 §6.1 — pure-advance spacer for inline PBM.
            // When SpacerWidth > 0 (and Text == null, AtomBox == null), this
            // item advances state.X by SpacerWidth without producing a TextRun
            // or atom fragment. Used by InlineLayout to reserve inline-axis
            // padding + border + margin on the start/end edges of an InlineBox
            // so the line breaker accounts for PBM in line width measurement.
            public double SpacerWidth;

            // CSS Fragmentation L3 §6.1 — box-decoration-break: clone.
            // Under clone every fragment of the inline box carries BOTH PBM
            // edges; the line breaker must reserve endPbm before closing a
            // mid-span line and startPbm when opening the continuation line.
            //
            // Both fields are set by InlineLayout.CollectInlineInner on every
            // content item (text/atom) that belongs to a clone-mode span. They
            // represent the ACCUMULATED total of all enclosing clone spans so
            // a nested clone-inside-clone span correctly contributes both layers.
            //
            // Spacer items (SpacerWidth > 0) do not carry clone PBM — the clone
            // mechanism is entirely driven by these text/atom item fields so that
            // the spacer injection points remain the same as for slice (start
            // spacer on first line, end spacer on last line via CollectInlineInner).
            public double CloneSpanStartPbm;
            public double CloneSpanEndPbm;

            internal void Reset() {
                Text = null;
                Style = null;
                InlineParent = null;
                FontSize = 0;
                FontFamily = null;
                FontWeight = 400;
                FontStyle = Weva.Paint.FontStyle.Normal;
                Color = null;
                WhiteSpace = null;
                Metrics = null;
                SourceRun = null;
                OwnerElement = null;
                WordBreak = null;
                LineBreak = null;
                OverflowWrap = null;
                Hyphens = null;
                TabSizeSpaces = 0;
                LetterSpacingPx = 0;
                WordSpacingPx = 0;
                AtomBox = null;
                AtomOuterWidth = 0;
                AtomBaseline = 0;
                AtomAboveBaseline = double.NaN;
                SpacerWidth = 0;
                CloneSpanStartPbm = 0;
                CloneSpanEndPbm = 0;
            }

        }

        public sealed class Result {
            public List<LineBox> Lines = new();
        }

        readonly BoxPool boxPool;
        // Pool of reusable Item instances. Rented at MakeItem / MakeAtomItem time
        // and returned at the end of each Break call. The whole list of items is
        // borrowed scratch (sharedItems on InlineLayout); the breaker owns the
        // Item instances themselves so the inline layout doesn't have to manage
        // their lifecycle.
        readonly Stack<Item> itemFree = new(64);

        // Per-Break scratch reused across every BreakInto call:
        //  - OutLines: the public output (lines produced this break).
        //  - currentFragments: per-line accumulator; cleared at FinishLine.
        //  - tokenScratch: collapsing-tokenizer output.
        //  - preTokenScratch: preserving-tokenizer output (pre-wrap / pre-line).
        //  - fragmentFree: pool of PlacedFragment instances (one per emitted text
        //    chunk; e.g. a 50-word paragraph yields ~50 fragments per pass).
        public readonly List<LineBox> OutLines = new(16);
        readonly List<PlacedFragment> currentFragments = new(32);
        readonly List<Token> tokenScratch = new(64);
        readonly List<string> preTokenScratch = new(32);
        readonly Stack<PlacedFragment> fragmentFree = new(64);
        readonly StringBuilder preservedTextBuilder = new(64);

        // Per-breaker (font, fontSize, letterSpacing, text) → width cache.
        // A typical paragraph re-measures common words ("the", " ", "and")
        // dozens of times in a single Break pass — the breaker tokenizes
        // ahead of word fitting and the prefix-fit binary search re-measures
        // partial slices. IFontMetrics implementations are pure on these
        // arguments so cross-pass reuse is also safe; we keep the cache
        // alive across BreakInto calls and only clear when it exceeds a
        // soft cap (working set is typically <500 unique tokens even on
        // large pages).
        readonly Dictionary<MeasureKey, double> measureCache = new(256);
        // Parallel cache keyed on (text-identity, start, length, font key) for
        // the windowed MeasureCached overload introduced by CODE_AUDIT_FINDINGS
        // P7. The wrap binary-search probes O(log n) prefixes of the same
        // word; using the source-string identity (RuntimeHelpers.GetHashCode)
        // means repeat probes against the same logical token share a slot
        // without paying a per-probe Substring alloc. Identity-keying is safe
        // here because LineBreaker.Item.Text is the canonical run text — every
        // probe in a Break pass holds the same reference.
        readonly Dictionary<MeasureWindowKey, double> measureWindowCache = new(256);
        const int MaxMeasureCacheEntries = 4096;

        public LineBreaker(BoxPool boxPool) {
            this.boxPool = boxPool;
        }

        // Test-only / one-off ctor: when the caller doesn't have a pool to share,
        // build a fresh BoxPool and BeginPass so AllocateLineBox still works. The
        // pool is throwaway, GC'd with this LineBreaker. Production code goes
        // through the BoxPool overload from LayoutEngine.
        public LineBreaker() {
            this.boxPool = new BoxPool();
            this.boxPool.BeginPass();
        }

        public Item RentItem() {
            return itemFree.Count > 0 ? itemFree.Pop() : new Item();
        }

        // Compat shim for the test suite: wraps BreakInto into the legacy
        // Result-returning shape. Does NOT recycle Items because external callers
        // own them.
        public Result Break(List<Item> items, double availableWidth) {
            OutLines.Clear();
            currentFragments.Clear();
            var state = new LineState();
            state.AvailableWidth = availableWidth;
            for (int i = 0; i < items.Count; i++) AppendItem(ref state, items[i], availableWidth);
            FinishLine(ref state, availableWidth, finalLine: true);
            var result = new Result();
            for (int i = 0; i < OutLines.Count; i++) result.Lines.Add(OutLines[i]);
            return result;
        }

        // Allocation-free Break entry point. Reads `count` Items starting at
        // `offset` from `items`, emits LineBoxes into OutLines (which is
        // .Clear()-ed on entry). Returned items go back on the Item free list so
        // the next pass reuses them.
        public void BreakInto(List<Item> items, int offset, int count, double availableWidth) {
            BreakInto(items, offset, count, availableWidth, null);
        }

        public void BreakInto(List<Item> items, int offset, int count, double availableWidth, double firstLineIndent) {
            BreakInto(items, offset, count, availableWidth, null, firstLineIndent);
        }

        // CSS 2.1 §9.5 float-aware overload. `lineProbe`, when non-null, is
        // consulted at the start of every line and after every wrap to query
        // the line's left X-offset and available width based on the float
        // context's intrusion at the CURRENT cumulative Y (accumulated across
        // completed lines in the same paragraph). The probe receives the line
        // index (0-based among produced lines) and the line's tentative
        // top-Y in BFC coords, and returns (leftOffset, lineWidth). When
        // null, the caller's `availableWidth` is used for every line and
        // the left offset is 0 — equivalent to the no-floats fast path.
        public delegate (double leftOffset, double width) LineProbe(int lineIndex, double topY);

        public void BreakInto(List<Item> items, int offset, int count, double availableWidth, LineProbe lineProbe) {
            BreakInto(items, offset, count, availableWidth, lineProbe, 0);
        }

        public void BreakInto(List<Item> items, int offset, int count, double availableWidth, LineProbe lineProbe, double firstLineIndent) {
            OutLines.Clear();
            currentFragments.Clear();

            var state = new LineState();
            state.AvailableWidth = availableWidth;
            state.LineProbe = lineProbe;
            state.NextLineY = 0;
            state.LineLeftOffset = 0;
            if (lineProbe != null) {
                var probe = lineProbe(0, 0);
                state.LineLeftOffset = probe.leftOffset;
                state.AvailableWidth = probe.width;
            }
            state.X = firstLineIndent;
            state.IndentOnThisLine = firstLineIndent;

            for (int i = 0; i < count; i++) {
                // Always pass the LIVE state.AvailableWidth: the float probe
                // may have widened/narrowed the previous line's allowance,
                // and the next item must respect the new line's width.
                AppendItem(ref state, items[offset + i], state.AvailableWidth);
            }
            FinishLine(ref state, state.AvailableWidth, finalLine: true);

            // Recycle the Item instances we just consumed.
            for (int i = 0; i < count; i++) {
                var it = items[offset + i];
                it.Reset();
                itemFree.Push(it);
            }
        }

        // Returns the measured pixel advance of `text` including letter-spacing
        // and word-spacing, caching the result on (Metrics, FontSize,
        // LetterSpacingPx, WordSpacingPx, text). The breaker re-measures the
        // same word and prefix slices many times per paragraph; identity
        // equality on the metrics ref is correct because LayoutContext
        // .GetMetrics() returns a stable instance per font family. Counts
        // per UTF-16 char — surrogate-pair handling lives at the call sites
        // (SnapToGraphemeBoundary). Soft-capped to bound the working set.
        double MeasureCached(Item item, string text) {
            if (string.IsNullOrEmpty(text) || item.Metrics == null) return 0;
            var key = new MeasureKey(item.Metrics, item.FontSize, item.FontFamily, item.FontWeight, item.FontStyle,
                item.LetterSpacingPx, item.WordSpacingPx, text);
            if (measureCache.TryGetValue(key, out double cached)) return cached;
            double natural = item.Metrics is IStyledFontMetrics styled
                ? styled.Measure(text, item.FontSize, item.FontFamily, item.FontStyle, item.FontWeight)
                : item.Metrics.Measure(text, item.FontSize);
            double total = natural;
            // CSS Text 3 §10.1: letter-spacing applies BETWEEN adjacent
            // typographic characters — N chars produce N-1 inter-letter
            // gaps, not N. Chrome / Firefox both suppress the trailing-
            // character spacing for the same reason. The prior
            // `letterSpacingPx * text.Length` over-counted by one full
            // letter-spacing per measured run; corresponding test
            // expectations have been updated.
            if (item.LetterSpacingPx != 0 && text.Length > 1) total += item.LetterSpacingPx * (text.Length - 1);
            // CSS Text 3 §10.2: extra advance on each word-separator
            // character. v1 recognises U+0020 ASCII space only — that
            // matches what the tokenizer treats as a word break in this
            // engine, so this is the entire population of space-class
            // codepoints we measure here.
            if (item.WordSpacingPx != 0) {
                int spaceCount = CountAsciiSpaces(text);
                if (spaceCount > 0) total += item.WordSpacingPx * spaceCount;
            }
            // L15: slice-evict instead of full Clear() — see LayoutCacheEviction.
            LayoutCacheEviction.EnsureRoom(measureCache, MaxMeasureCacheEntries);
            measureCache[key] = total;
            return total;
        }

        // Substring-window MeasureCached: returns the measured pixel advance of
        // text[start .. start+length) without materialising a fresh String.
        // Routes through the windowed IFontMetrics.Measure overload, then
        // applies the same letter-spacing / word-spacing post-pass as the
        // string overload (counted over the window only).
        //
        // Keyed on (RuntimeHelpers.GetHashCode(text), start, length, font key)
        // — identity-hashed because the wrap binary-search probes the same
        // logical token string across calls. See CODE_AUDIT_FINDINGS P7.
        double MeasureCached(Item item, string text, int start, int length) {
            if (string.IsNullOrEmpty(text) || item.Metrics == null || length <= 0) return 0;
            if (start < 0) { length += start; start = 0; }
            if (start >= text.Length) return 0;
            if (start + length > text.Length) length = text.Length - start;
            if (length <= 0) return 0;
            // Full-string fast path: route through the string-keyed cache so
            // we don't double-store the same measurement under two keys.
            if (start == 0 && length == text.Length) return MeasureCached(item, text);
            var key = new MeasureWindowKey(item.Metrics, item.FontSize, item.FontFamily, item.FontWeight, item.FontStyle,
                item.LetterSpacingPx, item.WordSpacingPx, text, start, length);
            if (measureWindowCache.TryGetValue(key, out double cached)) return cached;
            double natural = item.Metrics is IStyledFontMetrics styled
                ? styled.Measure(text, start, length, item.FontSize, item.FontFamily, item.FontStyle, item.FontWeight)
                : item.Metrics.Measure(text, start, length, item.FontSize);
            double total = natural;
            if (item.LetterSpacingPx != 0 && length > 1) total += item.LetterSpacingPx * (length - 1);
            if (item.WordSpacingPx != 0) {
                int spaceCount = CountAsciiSpacesInRange(text, start, length);
                if (spaceCount > 0) total += item.WordSpacingPx * spaceCount;
            }
            // L15: slice-evict instead of full Clear() — see LayoutCacheEviction.
            LayoutCacheEviction.EnsureRoom(measureWindowCache, MaxMeasureCacheEntries);
            measureWindowCache[key] = total;
            return total;
        }

        static int CountAsciiSpacesInRange(string text, int start, int length) {
            int n = 0;
            int end = start + length;
            for (int i = start; i < end; i++) {
                if (text[i] == ' ') n++;
            }
            return n;
        }

        string NormalizePreservedText(Item item, string text, double lineX, out double width) {
            if (string.IsNullOrEmpty(text)) {
                width = 0;
                return text;
            }
            string withoutSoftHyphens = StripSoftHyphens(text);
            if (withoutSoftHyphens.IndexOf('\t') < 0) {
                width = MeasureCached(item, withoutSoftHyphens);
                return withoutSoftHyphens;
            }

            double spaceW = MeasureCached(item, " ");
            if (spaceW <= 0) spaceW = item.FontSize > 0 ? item.FontSize * 0.5 : 8;
            double tabSpaces = item.TabSizeSpaces > 0 ? item.TabSizeSpaces : 8;
            double tabStop = spaceW * tabSpaces;
            if (tabStop <= 0) tabStop = spaceW * 8;

            var sb = preservedTextBuilder;
            sb.Clear();
            sb.EnsureCapacity(withoutSoftHyphens.Length + 8);
            double cursor = lineX;
            double total = 0;
            int start = 0;
            for (int i = 0; i < withoutSoftHyphens.Length; i++) {
                if (withoutSoftHyphens[i] != '\t') continue;
                if (i > start) {
                    // P8 fix: measure the slice in-place and copy chars to the
                    // builder via Append(string, int, int) — both avoid the
                    // per-segment Substring alloc the prior code paid.
                    double sliceW = MeasureCached(item, withoutSoftHyphens, start, i - start);
                    sb.Append(withoutSoftHyphens, start, i - start);
                    cursor += sliceW;
                    total += sliceW;
                }
                double next = System.Math.Floor(cursor / tabStop + 1) * tabStop;
                double delta = next - cursor;
                if (delta <= 1e-9) delta = tabStop;
                int spaces = System.Math.Max(1, (int)System.Math.Round(delta / spaceW));
                sb.Append(' ', spaces);
                cursor += delta;
                total += delta;
                start = i + 1;
            }
            if (start < withoutSoftHyphens.Length) {
                // P8 fix: same as above — windowed measure + chunked Append
                // avoid the trailing-slice Substring alloc.
                int tailLen = withoutSoftHyphens.Length - start;
                double tailW = MeasureCached(item, withoutSoftHyphens, start, tailLen);
                sb.Append(withoutSoftHyphens, start, tailLen);
                total += tailW;
            }
            width = total;
            return sb.ToString();
        }

        static string StripSoftHyphens(string text) {
            if (string.IsNullOrEmpty(text) || text.IndexOf('\u00AD') < 0) return text;
            return text.Replace("\u00AD", "");
        }

        static int CountAsciiSpaces(string text) {
            int n = 0;
            for (int i = 0; i < text.Length; i++) {
                if (text[i] == ' ') n++;
            }
            return n;
        }

        readonly struct MeasureKey : System.IEquatable<MeasureKey> {
            public readonly IFontMetrics Metrics;
            public readonly double FontSize;
            public readonly string FontFamily;
            public readonly int FontWeight;
            public readonly FontStyle FontStyle;
            public readonly double LetterSpacingPx;
            public readonly double WordSpacingPx;
            public readonly string Text;

            public MeasureKey(IFontMetrics metrics, double fontSize, string fontFamily, int fontWeight, FontStyle fontStyle,
                    double letterSpacingPx, double wordSpacingPx, string text) {
                Metrics = metrics;
                FontSize = fontSize;
                FontFamily = fontFamily;
                FontWeight = fontWeight;
                FontStyle = fontStyle;
                LetterSpacingPx = letterSpacingPx;
                WordSpacingPx = wordSpacingPx;
                Text = text;
            }

            public bool Equals(MeasureKey other) {
                return ReferenceEquals(Metrics, other.Metrics)
                    && FontSize == other.FontSize
                    && string.Equals(FontFamily, other.FontFamily, System.StringComparison.OrdinalIgnoreCase)
                    && FontWeight == other.FontWeight
                    && FontStyle == other.FontStyle
                    && LetterSpacingPx == other.LetterSpacingPx
                    && WordSpacingPx == other.WordSpacingPx
                    && Text == other.Text;
            }

            public override bool Equals(object obj) => obj is MeasureKey k && Equals(k);

            public override int GetHashCode() {
                unchecked {
                    int h = Metrics != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Metrics) : 0;
                    h = (h * 397) ^ FontSize.GetHashCode();
                    h = (h * 397) ^ (FontFamily != null ? System.StringComparer.OrdinalIgnoreCase.GetHashCode(FontFamily) : 0);
                    h = (h * 397) ^ FontWeight;
                    h = (h * 397) ^ (int)FontStyle;
                    h = (h * 397) ^ LetterSpacingPx.GetHashCode();
                    h = (h * 397) ^ WordSpacingPx.GetHashCode();
                    h = (h * 397) ^ (Text != null ? Text.GetHashCode() : 0);
                    return h;
                }
            }
        }

        // Key for the substring-window measure cache. Uses RuntimeHelpers
        // .GetHashCode on the source text (identity hash) instead of the
        // content hash because wrap probes hold the same string reference
        // across calls — content-hashing every probe would defeat the point
        // of avoiding the Substring alloc. Equality also uses ReferenceEquals
        // on Text for the same reason. See CODE_AUDIT_FINDINGS P7.
        readonly struct MeasureWindowKey : System.IEquatable<MeasureWindowKey> {
            public readonly IFontMetrics Metrics;
            public readonly double FontSize;
            public readonly string FontFamily;
            public readonly int FontWeight;
            public readonly FontStyle FontStyle;
            public readonly double LetterSpacingPx;
            public readonly double WordSpacingPx;
            public readonly string Text;
            public readonly int Start;
            public readonly int Length;

            public MeasureWindowKey(IFontMetrics metrics, double fontSize, string fontFamily, int fontWeight, FontStyle fontStyle,
                    double letterSpacingPx, double wordSpacingPx, string text, int start, int length) {
                Metrics = metrics;
                FontSize = fontSize;
                FontFamily = fontFamily;
                FontWeight = fontWeight;
                FontStyle = fontStyle;
                LetterSpacingPx = letterSpacingPx;
                WordSpacingPx = wordSpacingPx;
                Text = text;
                Start = start;
                Length = length;
            }

            public bool Equals(MeasureWindowKey other) {
                return ReferenceEquals(Metrics, other.Metrics)
                    && FontSize == other.FontSize
                    && string.Equals(FontFamily, other.FontFamily, System.StringComparison.OrdinalIgnoreCase)
                    && FontWeight == other.FontWeight
                    && FontStyle == other.FontStyle
                    && LetterSpacingPx == other.LetterSpacingPx
                    && WordSpacingPx == other.WordSpacingPx
                    && ReferenceEquals(Text, other.Text)
                    && Start == other.Start
                    && Length == other.Length;
            }

            public override bool Equals(object obj) => obj is MeasureWindowKey k && Equals(k);

            public override int GetHashCode() {
                unchecked {
                    int h = Metrics != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Metrics) : 0;
                    h = (h * 397) ^ FontSize.GetHashCode();
                    h = (h * 397) ^ (FontFamily != null ? System.StringComparer.OrdinalIgnoreCase.GetHashCode(FontFamily) : 0);
                    h = (h * 397) ^ FontWeight;
                    h = (h * 397) ^ (int)FontStyle;
                    h = (h * 397) ^ LetterSpacingPx.GetHashCode();
                    h = (h * 397) ^ WordSpacingPx.GetHashCode();
                    h = (h * 397) ^ (Text != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Text) : 0);
                    h = (h * 397) ^ Start;
                    h = (h * 397) ^ Length;
                    return h;
                }
            }
        }

        struct LineState {
            public double X;
            public double MaxAscent;
            public double MaxDescent;
            public double MaxLineHeight;
            // CSS 2.1 §10.8.1 half-leading. For each fragment with line-height
            // L and font ascent/descent A/D, half-leading = (L - A - D) / 2 is
            // added BOTH above the ascent and below the descent. The line box
            // top is max(A + halfLeading) above the baseline; the line box
            // bottom is max(D + halfLeading) below. Tracking these separately
            // from MaxAscent/MaxDescent lets the line size to the font's full
            // line-height (which includes LineGap) instead of just A+D —
            // without that, descenders that the font reserves space for via
            // LineGap get clipped by ancestor `overflow: hidden` because the
            // next block stacks immediately at line.Y + A + D.
            public double MaxAscentWithLeading;
            public double MaxDescentWithLeading;
            public double AvailableWidth;
            public int FragStart;
            // CSS 2.1 §9.5 float-aware fields. Null when no float probe is
            // active; otherwise FinishLine calls it after each completed line
            // to re-query (leftOffset, width) for the next line based on the
            // cumulative Y. NextLineY accumulates the produced LineBox
            // heights so the probe sees the float intrusion at the right
            // vertical position. LineLeftOffset is the BFC-local X at which
            // the current line's first fragment should sit (stamped onto
            // line.X in FinishLine).
            public LineProbe LineProbe;
            public double NextLineY;
            public double LineLeftOffset;
            // CSS Text L3 §7.1: text-indent shifts the first line's content
            // start. The cursor starts at `firstLineIndent`, so fragment
            // XOffsets include it, but line.Width must NOT include it —
            // ApplyTextAlign uses line.Width to compute the centering extra
            // (= contentW - line.Width) and including the indent undercounts
            // the extra by indent, shifting centered text left by indent/2.
            // Stored here so FinishLine can subtract it from state.X.
            // Reset to 0 after FinishLine emits the first line so subsequent
            // wrapped lines are unaffected.
            public double IndentOnThisLine;
            // CSS Text L3 §4.1.2 (A14 fix): true when the active whitespace
            // mode collapses spaces (normal / nowrap / pre-line). FinishLine
            // uses this to strip trailing collapsible spaces from the final
            // line, matching the spec requirement that "any collapsible white
            // space at the end of a line is removed."
            public bool CollapseTrailingSpace;

            // CSS Fragmentation L3 §6.1 — box-decoration-break: clone.
            // Active clone PBM for the item currently being processed. Set by
            // AppendItem from the item's CloneSpanEndPbm / CloneSpanStartPbm
            // fields. Used by FinishLine: on every non-final line break,
            //   state.X += ActiveCloneEndPbm   (end edge on outgoing fragment)
            //   state.X  = ActiveCloneStartPbm  (start edge on continuation)
            // This ensures every mid-span fragment carries both PBM edges per
            // CSS Fragmentation L3 §6.1 without modifying every wrap call site.
            // Both fields reset to 0 at the start of each BreakInto so they do
            // not bleed from one paragraph into the next.
            public double ActiveCloneEndPbm;
            public double ActiveCloneStartPbm;
        }

        sealed class PlacedFragment {
            public Item Source;
            public string Text;
            public double Width;
            public double XOffset;
            public bool IsAtom;

            public void Reset() {
                Source = null;
                Text = null;
                Width = 0;
                XOffset = 0;
                IsAtom = false;
            }
        }

        PlacedFragment RentFragment() {
            return fragmentFree.Count > 0 ? fragmentFree.Pop() : new PlacedFragment();
        }

        void AppendItem(ref LineState state, Item item, double availableWidth) {
            // CSS Fragmentation L3 §6.1 — pure-advance spacer (inline PBM edge).
            // SpacerWidth > 0 with no text/atom: advance the cursor without
            // emitting a fragment or affecting line metrics (ascent/descent).
            // The spacer never wraps on its own; it rides the preceding content.
            if (item.SpacerWidth > 0 && item.AtomBox == null && item.Text == null) {
                state.X += item.SpacerWidth;
                return;
            }

            // CSS Fragmentation L3 §6.1 — clone mode: update the active clone
            // PBM state from the current item. Every text/atom item inside a
            // clone-mode span carries the accumulated start/end PBM of all
            // enclosing clone spans. FinishLine consults these values when it
            // emits a non-final line break: it appends the end PBM to the
            // outgoing line and starts the continuation at the start PBM.
            // Items outside any clone span carry 0/0 — harmless no-ops.
            state.ActiveCloneEndPbm   = item.CloneSpanEndPbm;
            state.ActiveCloneStartPbm = item.CloneSpanStartPbm;

            if (item.AtomBox != null) {
                AppendAtom(ref state, item, availableWidth);
                return;
            }
            string ws = item.WhiteSpace ?? "normal";
            if (ws == "pre" || ws == "pre-wrap" || ws == "pre-line" || ws == "break-spaces") {
                // CSS Text L3 §4.1.2 (A14): pre / pre-wrap / break-spaces preserve trailing
                // spaces (they "hang"); only pre-line collapses spaces so trailing-trim applies.
                state.CollapseTrailingSpace = (ws == "pre-line");
                AppendPreserving(ref state, item, ws, availableWidth);
            } else {
                // normal / nowrap: collapse=collapse — trailing space on the final line
                // must be stripped per §4.1.2.
                state.CollapseTrailingSpace = true;
                AppendCollapsing(ref state, item, ws, availableWidth);
            }
        }

        void AppendAtom(ref LineState state, Item item, double availableWidth) {
            double w = item.AtomOuterWidth;
            // Atoms wrap as a unit (never split). If the atom would overflow and
            // the line already has content, break first.
            // CSS Fragmentation L3 §6.1 — clone mode: include CloneSpanEndPbm in
            // the fit test so the end edge reservation is honoured (same as the
            // word-wrap fit test in AppendCollapsing).
            double cloneEndR = item.CloneSpanEndPbm;
            if (state.X + w + cloneEndR > availableWidth + 1e-9 && HasAnyNonSpaceFragment(state)) {
                TrimTrailingSpace(ref state);
                FinishLine(ref state, availableWidth, finalLine: false);
            }
            AddAtomFragment(ref state, item, w);
        }

        void AppendCollapsing(ref LineState state, Item item, string ws, double availableWidth) {
            string text = item.Text ?? "";
            tokenScratch.Clear();
            TokenizeCollapsing(text, tokenScratch);
            bool nowrap = ws == "nowrap";
            string wb = NormalizeWordBreak(item.WordBreak);
            string lb = NormalizeLineBreak(item.LineBreak);
            string ow = NormalizeOverflowWrap(item.OverflowWrap);
            string hy = NormalizeHyphens(item.Hyphens);
            bool breakAll = !nowrap && (wb == "break-all" || ow == "anywhere");
            bool breakWord = !nowrap && !breakAll && (ow == "break-word" || wb == "break-word");
            // CSS Text L3 §5.2: `word-break: keep-all` suppresses break-between-
            // ideographs. `word-break: break-all` already handles CJK via EmitBreakAll.
            bool keepAll = !nowrap && !breakAll && wb == "keep-all";

            for (int i = 0; i < tokenScratch.Count; i++) {
                var t = tokenScratch[i];
                if (t.IsSpace) {
                    if (state.FragStart == currentFragments.Count) continue;
                    if (LastFragmentEndsInSpace(state)) continue;
                    AddFragment(ref state, item, " ", MeasureCached(item, " "));
                    continue;
                }
                if (!nowrap && hy != "none" && t.Word.IndexOf('\u00AD') >= 0 && !breakAll) {
                    EmitSoftHyphenWord(ref state, item, t.Word, availableWidth, breakWord);
                    continue;
                }
                string word = StripSoftHyphens(t.Word);

                // W5 UAX #14 — CJK ideographic break-between. When word-break is not
                // break-all (handled by EmitBreakAll), and not keep-all (CJK breaks
                // suppressed), and the token contains CJK, split at every allowed
                // inter-ideograph seam. CSS Text L3 §5.2.
                if (!nowrap && !breakAll && !keepAll
                        && Weva.Layout.Text.LineBreakClasses.ContainsCjk(word)) {
                    EmitCjkRun(ref state, item, word, availableWidth, breakWord, lb);
                    continue;
                }

                double wWidth = MeasureCached(item, word);
                if (nowrap) {
                    AddFragment(ref state, item, word, wWidth);
                    continue;
                }

                // break-all: a word boundary is just one of many break opportunities;
                // when the word doesn't fit at the current line position, place what
                // fits and continue with the remainder on the next line.
                if (breakAll) {
                    EmitBreakAll(ref state, item, word, availableWidth);
                    continue;
                }

                // CSS Fragmentation L3 §6.1 — clone mode end-PBM reservation.
                // Under box-decoration-break:clone the end edge appears on EVERY
                // fragment, including this line's fragment if we were to break here.
                // The fit test must reserve CloneSpanEndPbm: if the word + the
                // cloned end edge doesn't fit on the current line, we must wrap
                // before this word so the outgoing fragment has room for its end
                // edge. This is what the prompt means by "pending if-we-break-after-
                // this-item reservation" — it narrows the effective available width.
                // Items outside any clone span carry CloneSpanEndPbm=0 → no-op.
                double cloneEndReserve = item.CloneSpanEndPbm;
                bool fitsHere = state.X + wWidth + cloneEndReserve <= availableWidth + 1e-9;
                if (!fitsHere && HasAnyNonSpaceFragment(state)) {
                    TrimTrailingSpace(ref state);
                    FinishLine(ref state, availableWidth, finalLine: false);
                    // FinishLine may have re-queried the float probe and
                    // updated state.AvailableWidth for the new line; refit
                    // against that (potentially wider/narrower) width
                    // before deciding whether the word still overflows.
                    availableWidth = state.AvailableWidth;
                    fitsHere = wWidth + cloneEndReserve <= availableWidth + 1e-9;
                }

                // break-word: the word is alone on a fresh line and STILL doesn't fit.
                // Per CSS Text §6.2 this is the trigger to break inside the word.
                if (!fitsHere && breakWord) {
                    EmitBreakAll(ref state, item, word, availableWidth);
                    continue;
                }

                AddFragment(ref state, item, word, wWidth);
            }
        }

        // W5 UAX #14 — CJK run emitter. `word` is a single whitespace-delimited
        // token that contains at least one CJK ideograph. We iterate codepoint-by-
        // codepoint, accumulating a "chunk" until a break opportunity is found, at
        // which point we try to place the chunk on the current line; if it doesn't
        // fit we wrap first.
        //
        // Break opportunities arise at each seam where BOTH adjacent codepoints are
        // in the CJK ideographic class AND kinsoku does not prohibit the break (see
        // LineBreakClasses.IsCjkBreakOpportunity). Latin/ASCII sequences embedded in
        // the run are kept whole as a single chunk.
        //
        // CSS Text L3 §5.3: `lineBreak` is threaded through for kinsoku strictness.
        // `breakWord` is true when overflow-wrap:break-word is active.
        void EmitCjkRun(ref LineState state, Item item, string word, double availableWidth,
                        bool breakWord, string lineBreak) {
            int n = word.Length;
            if (n == 0) return;

            int chunkStart = 0;
            int prevCp = -1;

            for (int i = 0; i < n; ) {
                int cp = Weva.Layout.Text.LineBreakClasses.CodepointAt(word, i);
                int cc = Weva.Layout.Text.LineBreakClasses.CodepointCharCount(word, i);

                if (prevCp >= 0) {
                    // UAX #14: break opportunity is BEFORE `cp` (at position `i`).
                    if (Weva.Layout.Text.LineBreakClasses.IsCjkBreakOpportunity(prevCp, cp, lineBreak)) {
                        int chunkLen = i - chunkStart;
                        if (chunkLen > 0) {
                            EmitCjkChunk(ref state, item, word, chunkStart, chunkLen, availableWidth, breakWord);
                            // Float probe may have updated the line width after the wrap.
                            availableWidth = state.AvailableWidth;
                        }
                        chunkStart = i;
                    }
                }
                prevCp = cp;
                i += cc;
            }

            // Emit any remaining tail chunk.
            int tailLen = n - chunkStart;
            if (tailLen > 0) {
                EmitCjkChunk(ref state, item, word, chunkStart, tailLen, availableWidth, breakWord);
            }
        }

        // Place a CJK chunk substring [start..start+length) as a single fragment.
        // Wraps to a new line if the chunk overflows the current line and there is
        // already content present; falls back to EmitBreakAll if overflow-wrap:
        // break-word applies and the chunk is still too wide on a fresh line.
        void EmitCjkChunk(ref LineState state, Item item, string word, int start, int length,
                           double availableWidth, bool breakWord) {
            string chunk = word.Substring(start, length);
            double chunkW = MeasureCached(item, chunk);

            bool fitsHere = state.X + chunkW <= availableWidth + 1e-9;
            if (!fitsHere && HasAnyNonSpaceFragment(state)) {
                TrimTrailingSpace(ref state);
                FinishLine(ref state, availableWidth, finalLine: false);
                availableWidth = state.AvailableWidth;
                fitsHere = chunkW <= availableWidth + 1e-9;
            }
            if (!fitsHere && breakWord) {
                EmitBreakAll(ref state, item, chunk, availableWidth);
                return;
            }
            AddFragment(ref state, item, chunk, chunkW);
        }

        void EmitSoftHyphenWord(ref LineState state, Item item, string word, double availableWidth, bool breakWord) {
            string cleaned = StripSoftHyphens(word);
            double fullWidth = MeasureCached(item, cleaned);
            bool fitsHere = state.X + fullWidth <= availableWidth + 1e-9;
            if (!fitsHere && HasAnyNonSpaceFragment(state)) {
                TrimTrailingSpace(ref state);
                FinishLine(ref state, availableWidth, finalLine: false);
                availableWidth = state.AvailableWidth;
                fitsHere = fullWidth <= availableWidth + 1e-9;
            }
            if (fitsHere) {
                AddFragment(ref state, item, cleaned, fullWidth);
                return;
            }

            int cursor = 0;
            while (cursor < word.Length) {
                string remainingClean = StripSoftHyphens(word.Substring(cursor));
                double remainingWidth = MeasureCached(item, remainingClean);
                if (state.X + remainingWidth <= availableWidth + 1e-9) {
                    AddFragment(ref state, item, remainingClean, remainingWidth);
                    return;
                }

                SoftBreak best = FindSoftHyphenBreak(item, word, cursor, availableWidth - state.X);
                if (best.NextIndex > cursor) {
                    AddFragment(ref state, item, best.Text, best.Width);
                    FinishLine(ref state, availableWidth, finalLine: false);
                    availableWidth = state.AvailableWidth;
                    cursor = best.NextIndex;
                    continue;
                }

                if (HasAnyNonSpaceFragment(state)) {
                    TrimTrailingSpace(ref state);
                    FinishLine(ref state, availableWidth, finalLine: false);
                    availableWidth = state.AvailableWidth;
                    continue;
                }

                if (breakWord) {
                    EmitBreakAll(ref state, item, remainingClean, availableWidth);
                } else {
                    AddFragment(ref state, item, remainingClean, remainingWidth);
                }
                return;
            }
        }

        struct SoftBreak {
            public int NextIndex;
            public string Text;
            public double Width;
        }

        SoftBreak FindSoftHyphenBreak(Item item, string word, int cursor, double maxWidth) {
            var best = new SoftBreak();
            if (maxWidth <= 0) return best;
            for (int i = cursor; i < word.Length; i++) {
                if (word[i] != '\u00AD') continue;
                if (i == cursor) continue;
                string candidate = StripSoftHyphens(word.Substring(cursor, i - cursor)) + "-";
                double w = MeasureCached(item, candidate);
                if (w <= maxWidth + 1e-9) {
                    best.NextIndex = i + 1;
                    best.Text = candidate;
                    best.Width = w;
                }
            }
            return best;
        }

        // Consumes `word`, splitting it across as many lines as needed. On each line
        // we use a binary-style search (linear here for clarity; word lengths are
        // small) over the prefix length to find the largest prefix whose measured
        // width fits in the remaining space. Surrogate pairs are kept whole.
        void EmitBreakAll(ref LineState state, Item item, string word, double availableWidth) {
            int idx = 0;
            int n = word.Length;
            while (idx < n) {
                double remaining = availableWidth - state.X;
                if (remaining <= 1e-9 && HasAnyNonSpaceFragment(state)) {
                    TrimTrailingSpace(ref state);
                    FinishLine(ref state, availableWidth, finalLine: false);
                    remaining = availableWidth;
                }

                int take = LargestPrefixThatFits(item, word, idx, n, remaining);
                if (take == 0) {
                    // Nothing fits in the remaining space. If the line already has content,
                    // break to a new line and try again. Otherwise we must take at least one
                    // grapheme - the line will overflow but progress is required.
                    if (HasAnyNonSpaceFragment(state)) {
                        TrimTrailingSpace(ref state);
                        FinishLine(ref state, availableWidth, finalLine: false);
                        continue;
                    }
                    take = NextGraphemeLength(word, idx);
                }

                string slice = word.Substring(idx, take);
                double sliceW = MeasureCached(item, slice);
                AddFragment(ref state, item, slice, sliceW);
                idx += take;

                if (idx < n) {
                    // More characters remaining - wrap to a fresh line.
                    FinishLine(ref state, availableWidth, finalLine: false);
                }
            }
        }

        // Returns the largest prefix length (in chars) of `word[idx..end]` whose
        // measured width is <= maxWidth. Never splits a UTF-16 surrogate pair.
        // Returns 0 if even one grapheme overflows the available width.
        int LargestPrefixThatFits(Item item, string word, int idx, int end, double maxWidth) {
            if (maxWidth <= 0) return 0;
            int total = end - idx;
            // Binary search on prefix length, then snap to a grapheme boundary.
            int lo = 0;
            int hi = total;
            int best = 0;
            while (lo <= hi) {
                int mid = (lo + hi) >> 1;
                int snapped = SnapToGraphemeBoundary(word, idx, idx + mid) - idx;
                if (snapped <= 0) {
                    lo = mid + 1;
                    continue;
                }
                // P7 fix: use the substring-window MeasureCached overload so
                // each binary-search probe avoids materialising a fresh
                // String. The cache is identity-keyed on `word`, so repeat
                // probes against the same logical token share cache slots.
                double w = MeasureCached(item, word, idx, snapped);
                if (w <= maxWidth + 1e-9) {
                    if (snapped > best) best = snapped;
                    lo = mid + 1;
                } else {
                    hi = mid - 1;
                }
            }
            return best;
        }

        // Walks back from `pos` to the nearest UTF-16 boundary that doesn't
        // sit between a high and low surrogate. Returns a value in [start, pos].
        static int SnapToGraphemeBoundary(string s, int start, int pos) {
            if (pos <= start) return start;
            if (pos >= s.Length) return s.Length;
            // If we're between a high and low surrogate, step back one.
            if (pos > start && char.IsHighSurrogate(s[pos - 1]) && char.IsLowSurrogate(s[pos])) {
                return pos - 1;
            }
            return pos;
        }

        static int NextGraphemeLength(string s, int idx) {
            if (idx >= s.Length) return 0;
            if (idx + 1 < s.Length && char.IsHighSurrogate(s[idx]) && char.IsLowSurrogate(s[idx + 1])) {
                return 2;
            }
            return 1;
        }

        static string NormalizeWordBreak(string raw) {
            if (string.IsNullOrEmpty(raw)) return "normal";
            string s = CssStringUtil.ToLowerInvariantOrSame(raw.Trim());
            // CSS Text L3 §6.2: legacy `word-break: break-word` aliases to
            // `overflow-wrap: break-word`. Surface it under the canonical name
            // so the downstream breakWord trigger picks it up.
            if (s == "break-word") return "break-word";
            // keep-all suppresses CJK breaks (W5 UAX #14 impl). Pass through
            // so AppendCollapsing / AppendCjkRun can see the value and suppress
            // break-between-ideographs. Latin word breaks still apply normally.
            // break-all, normal fall through unchanged.
            return s;
        }

        static string NormalizeLineBreak(string raw) {
            if (string.IsNullOrEmpty(raw)) return "normal";
            return CssStringUtil.ToLowerInvariantOrSame(raw.Trim());
        }

        static string NormalizeOverflowWrap(string raw) {
            if (string.IsNullOrEmpty(raw)) return "normal";
            return CssStringUtil.ToLowerInvariantOrSame(raw.Trim());
        }

        static string NormalizeHyphens(string raw) {
            if (string.IsNullOrEmpty(raw)) return "manual";
            return CssStringUtil.ToLowerInvariantOrSame(raw.Trim());
        }

        void AppendPreserving(ref LineState state, Item item, string ws, double availableWidth) {
            string text = item.Text ?? "";
            bool wrap = ws == "pre-wrap" || ws == "pre-line" || ws == "break-spaces";
            bool collapseSpaces = ws == "pre-line";
            // CSS Text L3 §4: break-spaces — every preserved space is its own soft-wrap
            // opportunity (it wraps instead of hanging). Unlike pre-wrap, trailing spaces
            // at EOL do NOT hang; they wrap to the next line.
            bool breakSpaces = ws == "break-spaces";
            // Tracks whether THIS item has contributed any fragment yet. A text
            // node that is purely forced break(s) — e.g. a lone "\n" between two
            // inline elements under white-space:pre — produces breaks but no
            // glyph fragment, so it leaves no TextRun in the line. The
            // UnwrapLineBoxes coalescing pass (BlockLayout) rebuilds inline
            // content from the surviving runs' SourceNode.Data on flex /
            // shrink-to-fit re-layout; with no run carrying this node the break
            // is silently lost on the next pass ("}" and ".health {" collapse
            // onto one line). When we hit the first forced break with nothing
            // emitted, drop a zero-width anchor fragment so the source node
            // round-trips and the break is reconstructed from src.Data.
            bool itemEmitted = false;
            int i = 0;
            while (i < text.Length) {
                int j = i;
                while (j < text.Length && text[j] != '\n') j++;
                string segment = text.Substring(i, j - i);
                if (collapseSpaces) {
                    segment = CollapseSpacesPreserveSpaces(segment);
                }
                if (segment.Length > 0) {
                    itemEmitted = true;
                    if (wrap) {
                        preTokenScratch.Clear();
                        TokenizePreservingWithBreakpoints(segment, preTokenScratch);
                        for (int t = 0; t < preTokenScratch.Count; t++) {
                            string tk = preTokenScratch[t];
                            // A14b fix (pre-line): after collapsing, a leading single-space
                            // token at the very start of a line must be dropped — the same
                            // guard AppendCollapsing applies to its space tokens.
                            if (IsSpaceOnly(tk) && collapseSpaces &&
                                    state.FragStart == currentFragments.Count) {
                                continue;
                            }
                            string rendered = NormalizePreservedText(item, tk, state.X, out double w);
                            if (breakSpaces && IsSpaceOnly(tk)) {
                                // break-spaces: spaces are preserved (visible) and are soft-wrap
                                // opportunities. If the space itself overflows, it wraps to the
                                // next line (unlike pre-wrap where it hangs invisibly).
                                if (state.X + w > availableWidth + 1e-9 && HasAnyNonSpaceFragment(state)) {
                                    // Space overflows — wrap first, then place the space on the
                                    // new line (unlike pre-wrap's "discard" path).
                                    FinishLine(ref state, availableWidth, finalLine: false);
                                    rendered = NormalizePreservedText(item, tk, state.X, out w);
                                }
                                // Place the space on the current line (visible).
                                AddFragment(ref state, item, rendered, w);
                                // The space is now a wrap point: if the next token won't fit
                                // on this line, the next FinishLine call will happen when
                                // the word's overflow is detected. We do NOT force a wrap
                                // unconditionally — only when the next content overflows.
                                continue;
                            }
                            if (breakSpaces && !IsSpaceOnly(tk)) {
                                // break-spaces: a non-space word that overflows — wrap at the
                                // preceding space (which is already on the line, so it will
                                // appear as a trailing visible space on the wrapped line).
                                if (state.X + w > availableWidth + 1e-9 && HasAnyNonSpaceFragment(state)) {
                                    FinishLine(ref state, availableWidth, finalLine: false);
                                    rendered = NormalizePreservedText(item, tk, state.X, out w);
                                }
                                AddFragment(ref state, item, rendered, w);
                                continue;
                            }
                            if (state.X + w > availableWidth + 1e-9 && HasAnyNonSpaceFragment(state) && IsSpaceOnly(tk)) {
                                // pre-wrap / pre-line: space at EOL that overflows — discard
                                // (the space hangs/wraps, but is not rendered on the next line).
                                FinishLine(ref state, availableWidth, finalLine: false);
                                continue;
                            }
                            if (state.X + w > availableWidth + 1e-9 && HasAnyNonSpaceFragment(state) && !IsSpaceOnly(tk)) {
                                // CSS Text L3 §3: in pre-wrap / pre-line, U+0020
                                // spaces at the end of a line that would otherwise
                                // wrap "hang" — they stay on the outgoing line
                                // rather than being trimmed. Only normal / nowrap
                                // collapse-strip trailing spaces.
                                FinishLine(ref state, availableWidth, finalLine: false);
                                rendered = NormalizePreservedText(item, tk, state.X, out w);
                            }
                            AddFragment(ref state, item, rendered, w);
                        }
                    } else {
                        string rendered = NormalizePreservedText(item, segment, state.X, out double w);
                        AddFragment(ref state, item, rendered, w);
                    }
                }
                if (j < text.Length) {
                    // text[j] == '\n' — this is a preserved forced line break.
                    // CSS Text L3 §7.2: lines terminated by a forced break
                    // are not justified and use text-align-last instead of
                    // text-align. Flag via forcedBreak so FinishLine marks
                    // the emitted LineBox as IsFinalLine.
                    if (!itemEmitted) {
                        // Pure forced-break item (lone "\n" — from white-space:pre
                        // text or a <br> element): drop a zero-width anchor onto
                        // the OUTGOING line. Two purposes:
                        // 1. Font-metrics contribution: AddFragment grows MaxAscent
                        //    / MaxDescent for this item's font, so the empty line
                        //    produced by consecutive <br><br> (or a lone <br>) gets
                        //    the correct line height instead of collapsing to 0.
                        // 2. Round-trip source tracking: when SourceRun is non-null,
                        //    the TextRun's SourceNode survives the UnwrapLineBoxes
                        //    coalescing pass (reconstructed from src.Data) so the
                        //    break is not lost on flex / shrink-to-fit re-layout.
                        // Placing it before FinishLine attaches it to the line the
                        // break terminates, so it never spawns a spurious trailing
                        // line for a final "\n".
                        AddFragment(ref state, item, "", 0);
                        itemEmitted = true;
                    }
                    FinishLine(ref state, availableWidth, finalLine: false, forcedBreak: true);
                    i = j + 1;
                } else {
                    i = j;
                }
            }
        }

        static bool IsSpaceOnly(string s) {
            for (int k = 0; k < s.Length; k++) {
                if (s[k] != ' ' && s[k] != '\t') return false;
            }
            return s.Length > 0;
        }

        static string CollapseSpacesPreserveSpaces(string s) {
            var sb = new StringBuilder();
            bool prevSpace = false;
            for (int i = 0; i < s.Length; i++) {
                char c = s[i];
                if (c == ' ' || c == '\t') {
                    if (!prevSpace) sb.Append(' ');
                    prevSpace = true;
                } else {
                    sb.Append(c);
                    prevSpace = false;
                }
            }
            return sb.ToString();
        }

        static void TokenizePreservingWithBreakpoints(string s, List<string> output) {
            int i = 0;
            while (i < s.Length) {
                int j = i;
                if (s[i] == ' ' || s[i] == '\t') {
                    while (j < s.Length && (s[j] == ' ' || s[j] == '\t')) j++;
                    output.Add(s.Substring(i, j - i));
                } else {
                    while (j < s.Length && s[j] != ' ' && s[j] != '\t') j++;
                    output.Add(s.Substring(i, j - i));
                }
                i = j;
            }
        }

        struct Token {
            public bool IsSpace;
            public string Word;
        }

        static void TokenizeCollapsing(string text, List<Token> output) {
            int i = 0;
            int n = text.Length;
            while (i < n) {
                if (IsCollapsibleWs(text[i])) {
                    while (i < n && IsCollapsibleWs(text[i])) i++;
                    output.Add(new Token { IsSpace = true });
                } else {
                    int start = i;
                    while (i < n && !IsCollapsibleWs(text[i])) i++;
                    string word = start == 0 && i == n ? text : text.Substring(start, i - start);
                    output.Add(new Token { IsSpace = false, Word = word });
                }
            }
        }

        static bool IsCollapsibleWs(char c) {
            return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
        }

        bool LastFragmentEndsInSpace(LineState state) {
            if (currentFragments.Count == state.FragStart) return false;
            var last = currentFragments[currentFragments.Count - 1];
            string t = last.Text;
            if (string.IsNullOrEmpty(t)) return false;
            char c = t[t.Length - 1];
            return c == ' ' || c == '\t';
        }

        bool HasAnyNonSpaceFragment(LineState state) {
            for (int i = state.FragStart; i < currentFragments.Count; i++) {
                var f = currentFragments[i];
                if (f.IsAtom) return true;
                if (!IsSpaceOnly(f.Text)) return true;
            }
            return false;
        }

        void TrimTrailingSpace(ref LineState state) {
            for (int i = currentFragments.Count - 1; i >= state.FragStart; i--) {
                var f = currentFragments[i];
                if (f.IsAtom) break;
                if (f.Text == " " || (f.Text.Length > 0 && IsSpaceOnly(f.Text))) {
                    state.X -= f.Width;
                    f.Reset();
                    fragmentFree.Push(f);
                    currentFragments.RemoveAt(i);
                } else {
                    break;
                }
            }
        }

        void AddFragment(ref LineState state, Item item, string text, double width) {
            // CSS Text 3 §10.1: MeasureCached only contributes (length-1) gaps
            // per fragment; the seam between two same-source fragments needs
            // one more gap, otherwise total advance drops one LS per join.
            state.X += SeamLetterSpacing(state, item, text);
            var frag = RentFragment();
            frag.Source = item;
            frag.Text = text;
            frag.Width = width;
            frag.XOffset = state.X;
            frag.IsAtom = false;
            currentFragments.Add(frag);
            state.X += width;
            // PAINT-1b: route per-item line-metrics through the styled
            // overload when available so a bold/italic span's MaxAscent /
            // MaxDescent / MaxLineHeight reflect the actual face the paint
            // baker will render with. The fast path (InlineLayout
            // .TryLayoutSingleRunFast) already does this; without parity here
            // a multi-item line containing a `<span style="font-weight:700">`
            // gets sized to the regular face's metrics while paint uses the
            // bold face, producing visible glyph clipping at the top/bottom
            // of the line box (a long skill title in a bold face).
            double a, d, lh;
            if (item.Metrics is Weva.Layout.Text.IStyledFontMetrics styledLB) {
                a = styledLB.Ascent(item.FontSize, item.FontFamily, item.FontStyle, item.FontWeight);
                d = styledLB.Descent(item.FontSize, item.FontFamily, item.FontStyle, item.FontWeight);
                lh = styledLB.LineHeight(item.FontSize, item.FontFamily, item.FontStyle, item.FontWeight);
            } else {
                a = item.Metrics.Ascent(item.FontSize);
                d = item.Metrics.Descent(item.FontSize);
                lh = item.Metrics.LineHeight(item.FontSize);
            }
            if (a > state.MaxAscent) state.MaxAscent = a;
            if (d > state.MaxDescent) state.MaxDescent = d;
            if (lh > state.MaxLineHeight) state.MaxLineHeight = lh;
            // CSS 2.1 §10.8.1 half-leading: distribute (lh - a - d) evenly
            // above ascent and below descent. line.Height = max(ascentWithLeading)
            // + max(descentWithLeading) honors the font's LineGap (baked into
            // metric line-height) so descenders aren't clipped by an ancestor
            // overflow:hidden when a sibling block stacks right below.
            double halfLeading = (lh - a - d) * 0.5;
            if (halfLeading < 0) halfLeading = 0;
            double aL = a + halfLeading;
            double dL = d + halfLeading;
            if (aL > state.MaxAscentWithLeading) state.MaxAscentWithLeading = aL;
            if (dL > state.MaxDescentWithLeading) state.MaxDescentWithLeading = dL;
        }

        // Same-Source check (prev.Source == item) keeps the bridge inside a
        // single inline box; cross-element joins (different Items) are left
        // unbridged, which also covers the "different letter-spacing values"
        // case. Returns 0 at the start of a line (FragStart bookkeeping after
        // FinishLine), preserving the "no LS at line edges" spec rule.
        double SeamLetterSpacing(LineState state, Item item, string nextText) {
            if (item == null) return 0;
            if (item.LetterSpacingPx == 0) return 0;
            if (string.IsNullOrEmpty(nextText)) return 0;
            if (currentFragments.Count <= state.FragStart) return 0;
            var prev = currentFragments[currentFragments.Count - 1];
            if (prev.IsAtom) return 0;
            if (prev.Source != item) return 0;
            if (string.IsNullOrEmpty(prev.Text)) return 0;
            return item.LetterSpacingPx;
        }

        void AddAtomFragment(ref LineState state, Item item, double width) {
            var frag = RentFragment();
            frag.Source = item;
            frag.Text = null;
            frag.Width = width;
            frag.XOffset = state.X;
            frag.IsAtom = true;
            currentFragments.Add(frag);
            state.X += width;
            // CSS 2.1 §10.8.1: AtomAboveBaseline encodes the atom's vertical
            // placement after vertical-align resolution (= distance from the
            // line baseline up to the atom top). NaN falls back to
            // AtomBaseline for the legacy baseline-alignment path. The
            // effective ascent contribution = above-baseline extent; the
            // effective descent contribution = height - above.
            double height = item.AtomBox.Height;
            double a = double.IsNaN(item.AtomAboveBaseline) ? item.AtomBaseline : item.AtomAboveBaseline;
            double d = height - a;
            if (a > state.MaxAscent) state.MaxAscent = a;
            if (d > state.MaxDescent) state.MaxDescent = d;
            if (height > state.MaxLineHeight) state.MaxLineHeight = height;
            // Atom has no font-level LineGap to distribute — its half-leading
            // contributions equal its raw above/below-baseline values.
            if (a > state.MaxAscentWithLeading) state.MaxAscentWithLeading = a;
            if (d > state.MaxDescentWithLeading) state.MaxDescentWithLeading = d;
        }

        void FinishLine(ref LineState state, double availableWidth, bool finalLine, bool forcedBreak = false) {
            // CSS Text L3 §4.1.2 (A14 fix): strip trailing collapsible whitespace at the
            // end of every line when the active mode collapses spaces (normal / nowrap /
            // pre-line). For pre / pre-wrap / break-spaces the spaces "hang" and must NOT
            // be stripped. We apply this unconditionally (not just on finalLine) so the
            // final line matches mid-line wrapped lines in behaviour; the existing mid-wrap
            // callers already call TrimTrailingSpace before their FinishLine(finalLine:false)
            // calls, so calling it here is a no-op for those paths (there's nothing left to
            // trim) but it correctly handles the finalLine:true case that was previously skipped.
            if (state.CollapseTrailingSpace) TrimTrailingSpace(ref state);

            // CSS Fragmentation L3 §6.1 — box-decoration-break: clone.
            // On every non-final line break (= a mid-span wrap), append the
            // active clone end-PBM to the line's cursor so that the line width
            // includes the clone span's end edge. The end-PBM spacer injected by
            // CollectInlineInner handles the LAST fragment; this path handles every
            // INTERMEDIATE fragment. We do NOT apply it on the final line because
            // the end-PBM spacer already advanced state.X there.
            // forcedBreak (e.g. <br>) is also a mid-span line-end when !finalLine,
            // so it gets the same treatment.
            if (!finalLine && state.ActiveCloneEndPbm > 0) {
                state.X += state.ActiveCloneEndPbm;
            }

            int fragCount = currentFragments.Count - state.FragStart;
            bool hasFragments = fragCount > 0;
            if (!hasFragments && finalLine) return;

            var line = boxPool.AllocateLineBox();
            // CSS 2.1 §10.8.1: line box height = max(extAscent) + max(extDescent)
            // where each fragment contributes ext{Ascent,Descent} = raw A/D plus
            // half-leading (= (lh - A - D)/2). This honours the font's LineGap
            // (baked into metric line-height) without clipping descenders the
            // way the previous `MaxAscent + MaxDescent` formula did — the slow
            // path was 1-2px short of the font's full line-height, and a
            // sibling block stacking at line.Y + line.Height covered the
            // descender area. Matches InlineLayout.TryLayoutSingleRunFast.
            // Inline-block atoms get half-leading=0 in AddAtomFragment so
            // their above/below-baseline values still establish the floor,
            // preserving the previous fix for tall inline-block + text-with-
            // descender mixes.
            double aboveBaseline = state.MaxAscentWithLeading;
            double belowBaseline = state.MaxDescentWithLeading;
            // Defensive minimum: never let leading distribution shrink below
            // the raw ascent/descent contribution. (Equal in the common case;
            // a guard against future changes that might compute a negative
            // halfLeading and zero it.)
            if (aboveBaseline < state.MaxAscent) aboveBaseline = state.MaxAscent;
            if (belowBaseline < state.MaxDescent) belowBaseline = state.MaxDescent;
            double height = aboveBaseline + belowBaseline;
            // CSS Text L3 §7.1 fix: text-indent shifts the first line's
            // start cursor (state.X) but must not inflate line.Width.
            // ApplyTextAlign computes centering extra = contentW - line.Width;
            // if indent were included, extra shrinks by indent and centered
            // text ends up indent/2 px to the left of its correct position.
            // Subtract IndentOnThisLine (0 for subsequent lines) so the
            // centering calculation sees only the glyph content width.
            line.Width = state.X - state.IndentOnThisLine;
            line.Height = height;
            // CSS 2.1 §10.8.1: baseline sits `aboveBaseline` (= max
            // ascent-with-leading) from the line box top. Switching from the
            // raw MaxAscent to the leading-aware value here is what lets the
            // descender area at the line bottom = (line.Height - line.Baseline)
            // match the font's reserved descender + half-leading-below.
            line.Baseline = aboveBaseline;
            // CSS Text L3 §7.2: justification is suppressed on the last
            // line of a block AND on any line terminated by a forced
            // break (e.g. <br> or a preserved \n). Both cases also
            // switch alignment from `text-align` to `text-align-last`.
            // IsFinalLine is the gate ApplyTextAlign consults for both.
            line.IsFinalLine = finalLine || forcedBreak;
            // CSS 2.1 §9.5: when floats narrow this line, line.X carries
            // the leading-edge offset (cumulative left-float intrusion at
            // the line's Y). BlockLayout's inline-flow placement code
            // honours line.X verbatim, so floats wrap correctly without
            // a second placement pass. The default is 0 (no float
            // intrusion) — matches the pre-float behaviour where every
            // line sits at the container's inner-left edge.
            line.X = state.LineLeftOffset;

            // Diagnostic — fires when UILayoutDiagnostics.Enabled and the
            // FIRST fragment's source element class matches. Captures the
            // line-box dimensions a parent block will inherit as one of its
            // line boxes. If the line.Height is correct but the parent
            // block's Height is shorter, the bug is in FinalizeBlockSize.
            if (hasFragments && Weva.Diagnostics.UILayoutDiagnostics.Enabled
                && state.FragStart < currentFragments.Count) {
                var firstFrag = currentFragments[state.FragStart];
                var firstEl = firstFrag.Source.AtomBox?.Element
                              ?? firstFrag.Source.SourceRun?.Element;
                if (Weva.Diagnostics.UILayoutDiagnostics.ShouldTrace(firstEl)) {
                    Weva.Diagnostics.UILayoutDiagnostics.TraceFor(firstEl, "LineBreaker.FinishLine",
                        $"line.W={line.Width} line.H={line.Height} line.Baseline={line.Baseline} " +
                        $"MaxAscent={state.MaxAscent} MaxDescent={state.MaxDescent} " +
                        $"MaxAscWLead={state.MaxAscentWithLeading} MaxDescWLead={state.MaxDescentWithLeading} " +
                        $"MaxLineHeight={state.MaxLineHeight} fragsOnLine={currentFragments.Count - state.FragStart}");
                }
            }

            if (hasFragments) {
                for (int i = state.FragStart; i < currentFragments.Count; i++) {
                    var f = currentFragments[i];
                    if (f.IsAtom) {
                        var atom = f.Source.AtomBox;
                        // Place the atom on the line. AtomAboveBaseline is the
                        // distance from line baseline up to atom top (post
                        // vertical-align resolution); NaN falls back to the
                        // legacy AtomBaseline (= baseline alignment). The atom's
                        // outer-left margin sits at f.XOffset; the box itself
                        // starts after MarginLeft.
                        double above = double.IsNaN(f.Source.AtomAboveBaseline) ? f.Source.AtomBaseline : f.Source.AtomAboveBaseline;
                        // Position relative to line.Baseline (= aboveBaseline)
                        // not the raw MaxAscent. Atom contributed `above` to
                        // MaxAscentWithLeading with halfLeading=0, so its top
                        // sits at line.Baseline - above. Using MaxAscent
                        // instead would place atoms `(aboveBaseline -
                        // MaxAscent)` px too high (= half-leading-above of the
                        // tallest text fragment), so they'd appear to "float"
                        // up out of the line on a font with non-zero LineGap.
                        double atomTop = aboveBaseline - above;
                        atom.X = f.XOffset + atom.MarginLeft;
                        atom.Y = atomTop;
                        line.AddChild(atom);
                    } else {
                        var run = boxPool.AllocateTextRun();
                        run.Text = f.Text;
                        run.Style = f.Source.Style;
                        // Use the SourceRun's own Element when available.
                        // For pseudo-element TextRuns (::before/::after) the
                        // SourceRun has Element=null; fall back to OwnerElement
                        // (the host span's element set by CollectInlineInner)
                        // so the emitted run is attributed to the originating
                        // span and counts toward its bounding-box in
                        // AttachInlineFragmentsToLines. CSS 2.1 §12: generated
                        // content is logically part of the host element.
                        run.Element = f.Source.SourceRun?.Element ?? f.Source.OwnerElement;
                        run.SourceNode = f.Source.SourceRun?.SourceNode;
                        run.FontFamily = f.Source.FontFamily;
                        run.FontSize = f.Source.FontSize;
                        run.Color = f.Source.Color;
                        run.X = f.XOffset;
                        // PAINT-1b mirror — use the styled ascent/line-height
                        // for this fragment's per-run Y offset and Height,
                        // matching the bold/italic face the paint baker uses.
                        double fAscent, fLineHeight;
                        if (f.Source.Metrics is Weva.Layout.Text.IStyledFontMetrics styledF) {
                            fAscent = styledF.Ascent(f.Source.FontSize, f.Source.FontFamily, f.Source.FontStyle, f.Source.FontWeight);
                            fLineHeight = styledF.LineHeight(f.Source.FontSize, f.Source.FontFamily, f.Source.FontStyle, f.Source.FontWeight);
                        } else {
                            fAscent = f.Source.Metrics.Ascent(f.Source.FontSize);
                            fLineHeight = f.Source.Metrics.LineHeight(f.Source.FontSize);
                        }
                        // run.Y is the per-fragment top within the line.
                        // Baseline sits at `aboveBaseline` (which equals
                        // line.Baseline). The fragment's own baseline lines
                        // up there, with the fragment's own ascent above it,
                        // so the fragment top = aboveBaseline - fAscent
                        // -PLUS- the fragment's half-leading above (which is
                        // already subsumed into how line.Baseline was placed
                        // when this fragment was the dominant contributor).
                        // For non-dominant fragments, run.Y stays positive
                        // (their ascent < aboveBaseline). Mirrors FastPath at
                        // InlineLayout.cs:374 (`run.Y = halfLeading` for the
                        // single-run case where the fragment IS dominant).
                        run.Y = aboveBaseline - fAscent;
                        run.Width = f.Width;
                        run.Height = fLineHeight;
                        line.AddChild(run);
                    }
                }
            }

            // Recycle the consumed fragments back to the free list and trim the
            // active region of currentFragments back to FragStart.
            for (int i = state.FragStart; i < currentFragments.Count; i++) {
                var f = currentFragments[i];
                f.Reset();
                fragmentFree.Push(f);
            }
            currentFragments.RemoveRange(state.FragStart, currentFragments.Count - state.FragStart);

            OutLines.Add(line);
            // CSS Fragmentation L3 §6.1 — clone mode: after a non-final line
            // break, start the continuation line at the clone span's start-PBM
            // offset so the next fragment's text runs begin after the cloned
            // start edge. For final lines and items outside clone spans,
            // ActiveCloneStartPbm is 0, so this is equivalent to `state.X = 0`.
            state.X = finalLine ? 0 : state.ActiveCloneStartPbm;
            state.MaxAscent = 0;
            state.MaxDescent = 0;
            state.MaxLineHeight = 0;
            state.MaxAscentWithLeading = 0;
            state.MaxDescentWithLeading = 0;
            // CSS Text L3 §7.1: indent only applies to the first line. Clear
            // so subsequent lines' FinishLine calls don't subtract it again.
            state.IndentOnThisLine = 0;
            // Advance the float-probe cursor and re-query the next line's
            // (leftOffset, width). The cursor is BFC-local Y of the next
            // line's TOP edge — line.Height accumulates whatever was just
            // emitted. Probe nulls = single-width run, no per-line change.
            if (state.LineProbe != null) {
                state.NextLineY += height;
                var probe = state.LineProbe(OutLines.Count, state.NextLineY);
                state.LineLeftOffset = probe.leftOffset;
                state.AvailableWidth = probe.width;
            }
        }
    }
}
