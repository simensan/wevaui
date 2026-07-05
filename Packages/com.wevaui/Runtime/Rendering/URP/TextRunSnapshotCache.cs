using System;
using System.Collections.Generic;
using UnityEngine;
using Weva.Paint;
using PaintRect = Weva.Paint.Rect;

namespace Weva.Rendering.URP {
    // Cached glyph-quad output for a DrawTextCommand. The shape result (atlas
    // UVs, per-glyph bounds, atlas slot, blur, faux-bold bias) is fully
    // determined by the visual fields of a DrawTextCommand — text + font +
    // color + decoration + letter-spacing + blur — and is INVARIANT under
    // pure translation of the run's bounds. By caching the shaped quads in
    // bounds-RELATIVE coordinates (subtracting Bounds.X/Y at capture) and
    // adding back the current origin on replay, the same paragraph can move
    // across the screen — or animate via parent transform — without paying
    // the per-frame atlas-walk / glyph-bake cost.
    //
    // Storage: process-static dictionary, soft-capped. Identity is preserved
    // across WevaDocument instances, so multiple painters share the cache; the
    // key is purely visual so there's no cross-document leakage risk.
    //
    // Thread safety (RC6): single-threaded by Unity main-thread convention.
    // The soft-cap `cache.Clear()` at the top of Store is the most dangerous
    // mutation — a concurrent reader would observe a partially-cleared
    // dictionary mid-clear. The public mutation entrypoint (`Store`) calls
    // UIMainThreadGuard.AssertMainThread so a misuse fires a debug-build
    // assertion rather than corrupting the dict. Read-only `TryGet` does NOT
    // assert (the read is a single TryGetValue and the worst-case race is a
    // false miss that re-bakes a glyph run — visually identical).
    internal static class TextRunSnapshotCache {
        // Soft cap on entries. When exceeded, the entire dictionary is
        // cleared rather than evicted one-by-one — simpler and the working
        // set on a typical page (each distinct text run × style) is well
        // below this.
        const int MaxEntries = 4096;
        // Fraction of the cache evicted on overflow. Evicting a slice (not the
        // whole cache) means a document with > MaxEntries distinct visible runs
        // keeps ~75% of its entries warm instead of re-shaping EVERY run every
        // frame (the old full Clear() turned a large scrolling doc into a
        // permanent re-shape storm — ~20 ms/frame + GC).
        const int EvictBatch = MaxEntries / 4;

        // Capacity guess at construction. Sized so a typical document
        // populated with one run per visible glyph hits the cache without
        // intermediate resizing. The dictionary grows as needed.
        static readonly Dictionary<TextRunSnapshotKey, TextRunSnapshot> cache = new(256);
        // Reusable scratch for the eviction key sweep — main-thread-guarded
        // (see Store), so a shared buffer is safe and avoids a per-evict alloc.
        static readonly List<TextRunSnapshotKey> evictScratch = new(EvictBatch);

        public static bool TryGet(in TextRunSnapshotKey key, out TextRunSnapshot snapshot) {
            return cache.TryGetValue(key, out snapshot);
        }

        // Captures the shape result into the cache. `glyphs` may be a pooled
        // list — we copy out into a freshly-sized array, normalizing every
        // glyph's bounds to be relative to (originX, originY) so the same
        // snapshot replays at any translated position.
        public static void Store(in TextRunSnapshotKey key, List<SdfGlyphQuad> glyphs, int atlasId, DrawTextCommand command, bool usedSecondaryFallback = false) {
            // RC6: the soft-cap clear below is the most dangerous mutation in
            // this file — a concurrent reader would observe a partially-
            // cleared dictionary. Fires a debug-build assertion if an
            // off-main-thread caller ever reaches here.
            Weva.Diagnostics.UIMainThreadGuard.AssertMainThread(nameof(Store));
            if (cache.Count >= MaxEntries) EvictBatchEntries();
            double originX = command.Bounds.X;
            double originY = command.Bounds.Y;
            int n = glyphs.Count;
            var relQuads = new SdfGlyphQuad[n];
            for (int i = 0; i < n; i++) {
                var g = glyphs[i];
                var relBounds = new PaintRect(g.Bounds.X - originX, g.Bounds.Y - originY, g.Bounds.Width, g.Bounds.Height);
                relQuads[i] = new SdfGlyphQuad(relBounds, g.Color, g.UvMin, g.UvMax, g.AtlasId, g.BlurRadius, g.WeightBias, g.TintWithFillColor);
            }
            bool snap = UsesPixelSnapCorrection(command);
            double snapDeltaX = snap ? PixelSnapDelta(command.Bounds.X) : 0;
            double snapDeltaY = snap ? PixelSnapDelta(command.Bounds.Y + command.Bounds.Height) : 0;
            cache[key] = new TextRunSnapshot(relQuads, atlasId, snap, snapDeltaX, snapDeltaY, usedSecondaryFallback);
        }

        // Test/diagnostic hook. Not called from production code.
        public static void Clear() => cache.Clear();

        // Evicts a slice of the cache instead of wiping it whole. Eviction is
        // correctness-neutral — an evicted run just re-shapes once on its next
        // frame — so WHICH entries go is arbitrary; we take the first EvictBatch
        // the dictionary enumerates. Dropping a slice (vs Clear) keeps the cache
        // at ~75% after overflow so a > MaxEntries document doesn't thrash to
        // zero every frame.
        static void EvictBatchEntries() {
            evictScratch.Clear();
            int target = EvictBatch;
            foreach (var k in cache.Keys) {
                evictScratch.Add(k);
                if (evictScratch.Count >= target) break;
            }
            for (int i = 0; i < evictScratch.Count; i++) cache.Remove(evictScratch[i]);
            evictScratch.Clear();
        }
        public static int Count => cache.Count;
        internal static int MaxEntriesForTest => MaxEntries;
        internal static int EvictBatchForTest => EvictBatch;

        internal static bool UsesPixelSnapCorrection(DrawTextCommand cmd) {
            if (cmd == null) return false;
            if (cmd.BlurRadius > 0) return false;
            double size = cmd.Font.Size > 0 ? cmd.Font.Size : 14;
            return size <= 20;
        }

        internal static double PixelSnapDelta(double value) {
            return Math.Floor(value + 0.5) - value;
        }
    }

    // Visually-keyed identity tuple. Two DrawTextCommands with the same key
    // produce byte-identical glyph quads (up to bounds translation). Includes
    // every field that drives the baker's output.
    //
    // Wiring note for future feature work — when `font-variation-settings`
    // (FontVariationResolver) or `font-stretch` is plumbed end-to-end through
    // to the glyph baker, this key must grow a corresponding field. Today
    // those CSS properties are parsed but their resolved axes never reach
    // the baker, so two runs that differ only by axis value still produce
    // identical quads — no cache collision in practice. The moment the
    // baker picks up axis values, runs with different `wght`/`wdth`/etc.
    // will diverge visually and the key needs to distinguish them.
    // `text-stroke` and `text-shadow` are already keyed correctly via
    // separate DrawTextCommands carrying different (Color, BlurRadius).
    internal readonly struct TextRunSnapshotKey : IEquatable<TextRunSnapshotKey> {
        public readonly string Text;
        public readonly FontHandle Font;
        public readonly LinearColor Color;
        public readonly TextDecoration Decoration;
        public readonly double LetterSpacingPx;
        public readonly LinearColor DecorationColor;
        public readonly bool HasDecorationColor;
        public readonly DecorationStyle DecorationStyle;
        public readonly double DecorationThickness;
        public readonly double DecorationOffset;
        public readonly double BlurRadius;
        // Layout baseline (DrawTextCommand.LayoutBaseline) drives the glyphs'
        // vertical placement, so two runs with the same text/font but a
        // different baseline (different line-height) produce DIFFERENT quads
        // and must not share a cache entry. Including it also self-invalidates
        // any quads cached before the baseline was plumbed (key changes from
        // NaN to the real baseline → cache miss → re-shape). NaN normalises to
        // a stable sentinel so NaN-keyed entries still match each other.
        public readonly double LayoutBaseline;
        // font-kerning toggles glyph X positions in the baker; two runs that
        // differ only in KerningEnabled must not share one snapshot.
        public readonly bool KerningEnabled;

        public TextRunSnapshotKey(DrawTextCommand cmd) {
            Text = cmd.Text;
            Font = cmd.Font;
            Color = cmd.Color;
            Decoration = cmd.Decoration;
            LetterSpacingPx = cmd.LetterSpacingPx;
            DecorationColor = cmd.DecorationColor;
            HasDecorationColor = cmd.HasDecorationColor;
            DecorationStyle = cmd.DecorationStyle;
            DecorationThickness = cmd.DecorationThickness;
            DecorationOffset = cmd.DecorationOffset;
            BlurRadius = cmd.BlurRadius;
            // Normalise NaN → a fixed sentinel so the legacy "no layout
            // baseline" case keys consistently (NaN != NaN otherwise).
            LayoutBaseline = double.IsNaN(cmd.LayoutBaseline) ? double.MinValue : cmd.LayoutBaseline;
            KerningEnabled = cmd.KerningEnabled;
        }

        public bool Equals(TextRunSnapshotKey other) {
            return Text == other.Text
                && Font.Equals(other.Font)
                && Color.Equals(other.Color)
                && Decoration.Equals(other.Decoration)
                && LetterSpacingPx == other.LetterSpacingPx
                && HasDecorationColor == other.HasDecorationColor
                && (!HasDecorationColor || DecorationColor.Equals(other.DecorationColor))
                && DecorationStyle == other.DecorationStyle
                && DecorationThickness == other.DecorationThickness
                && DecorationOffset == other.DecorationOffset
                && BlurRadius == other.BlurRadius
                && LayoutBaseline == other.LayoutBaseline
                && KerningEnabled == other.KerningEnabled;
        }

        public override bool Equals(object obj) => obj is TextRunSnapshotKey k && Equals(k);

        public override int GetHashCode() {
            unchecked {
                int h = Text != null ? Text.GetHashCode() : 0;
                h = (h * 397) ^ Font.GetHashCode();
                h = (h * 397) ^ Color.GetHashCode();
                h = (h * 397) ^ Decoration.GetHashCode();
                h = (h * 397) ^ LetterSpacingPx.GetHashCode();
                h = (h * 397) ^ BlurRadius.GetHashCode();
                // Decoration fields only fold in when they matter — the
                // common case is no separate decoration color or style.
                if (HasDecorationColor) h = (h * 397) ^ DecorationColor.GetHashCode();
                h = (h * 397) ^ (int)DecorationStyle;
                h = (h * 397) ^ DecorationThickness.GetHashCode();
                h = (h * 397) ^ DecorationOffset.GetHashCode();
                h = (h * 397) ^ LayoutBaseline.GetHashCode();
                if (!KerningEnabled) h = (h * 397) ^ 1;
                return h;
            }
        }

    }

    // Captured shape result for one (key) entry. `Quads` are stored with
    // bounds RELATIVE to the original origin; the replay path adds the
    // current Bounds.X/Y back when submitting to the batcher.
    internal readonly struct TextRunSnapshot {
        public readonly SdfGlyphQuad[] Quads;
        public readonly int AtlasId;
        public readonly bool AppliesPixelSnapCorrection;
        public readonly double SnapDeltaX;
        public readonly double SnapDeltaY;
        // True when the cached shape fell through to the secondary SDF face
        // (post-probation). Such entries replay normally, but PrepareText
        // must NOT treat them as "done": the primary (ATG) atlas still needs
        // to be fed this text so it can ingest the characters the cold shape
        // missed — once it does, the atlas version bumps, the cache clears,
        // and the run re-shapes on the primary face. Without this, a
        // cold-start fallback was a session-permanent font flap (inputtest
        // "Open Menu ▸" stuck on the fallback face with the ▸ dropped).
        public readonly bool UsedSecondaryFallback;

        public TextRunSnapshot(SdfGlyphQuad[] quads, int atlasId, bool appliesPixelSnapCorrection, double snapDeltaX, double snapDeltaY, bool usedSecondaryFallback = false) {
            Quads = quads;
            AtlasId = atlasId;
            AppliesPixelSnapCorrection = appliesPixelSnapCorrection;
            SnapDeltaX = snapDeltaX;
            SnapDeltaY = snapDeltaY;
            UsedSecondaryFallback = usedSecondaryFallback;
        }
    }
}
