using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Weva.Paint;
using Weva.Rendering.URP;
using FontStyle = Weva.Paint.FontStyle;
using Rect = Weva.Paint.Rect;

namespace Weva.Tests.Text {
    public class TextRunPreparationTests {
        [Test]
        public void PrepareText_skips_snapshot_hits_and_prepares_only_misses() {
            var previous = SdfTextRendering.Atlas;
            var atlas = new VersionedUvAtlas { VersionValue = 1 };
            SdfTextRendering.SetAtlas(atlas);
            try {
                var font = new FontHandle("Test", 14, 400, FontStyle.Normal);
                var cached = new DrawTextCommand(
                    new Rect(10, 20, 100, 14),
                    "Cached",
                    font,
                    LinearColor.White,
                    TextDecoration.None);
                var seed = new PaintList();
                seed.Add(cached);

                SdfTextRendering.PrepareText(seed);
                var seedBatcher = new UIBatcher();
                SdfTextRendering.EmitGlyphs(seedBatcher, cached);
                seedBatcher.Finish();

                Assert.That(atlas.PrepareCalls, Is.EqualTo(1));
                Assert.That(atlas.ShapeCalls, Is.EqualTo(1));

                var movedCached = new DrawTextCommand(
                    new Rect(40, 20, 100, 14),
                    "Cached",
                    font,
                    LinearColor.White,
                    TextDecoration.None);
                var uncached = new DrawTextCommand(
                    new Rect(10, 40, 100, 14),
                    "New",
                    font,
                    LinearColor.White,
                    TextDecoration.None);
                var mixed = new PaintList();
                mixed.Add(movedCached);
                mixed.Add(uncached);

                SdfTextRendering.PrepareText(mixed);

                Assert.That(atlas.PrepareCalls, Is.EqualTo(2));

                var mixedBatcher = new UIBatcher();
                SdfTextRendering.EmitGlyphs(mixedBatcher, movedCached);
                SdfTextRendering.EmitGlyphs(mixedBatcher, uncached);
                mixedBatcher.Finish();

                Assert.That(atlas.ShapeCalls, Is.EqualTo(2));
            } finally {
                SdfTextRendering.SetAtlas(previous);
            }
        }

        [Test]
        public void PrepareText_prepares_every_run_when_snapshots_are_disabled() {
            var previous = SdfTextRendering.Atlas;
            var atlas = new VersionedUvAtlas { VersionValue = 1, UseSnapshots = false };
            SdfTextRendering.SetAtlas(atlas);
            try {
                var font = new FontHandle("Test", 14, 400, FontStyle.Normal);
                var list = new PaintList();
                list.Add(new DrawTextCommand(
                    new Rect(10, 20, 100, 14),
                    "One",
                    font,
                    LinearColor.White,
                    TextDecoration.None));
                list.Add(new DrawTextCommand(
                    new Rect(10, 40, 100, 14),
                    "Two",
                    font,
                    LinearColor.White,
                    TextDecoration.None));

                SdfTextRendering.PrepareText(list);

                Assert.That(atlas.PrepareCalls, Is.EqualTo(2));
            } finally {
                SdfTextRendering.SetAtlas(previous);
            }
        }

        // Regression (inputtest "Open Menu ▸"): a run whose shape fell back to
        // the secondary SDF face twice gets its FALLBACK quads cached — and the
        // old PrepareText skipped every snapshot hit, so the primary atlas was
        // never fed the run again. The version bump that clears the snapshot
        // cache can only come from the primary atlas ingesting the missing
        // characters, so a cold-start miss became a session-permanent font flap
        // (fallback face + the ▸ dropped). Fallback-tagged snapshots must keep
        // preparing until the primary heals, then re-shape on the primary face.
        [Test]
        public void Fallback_cached_run_keeps_preparing_until_primary_heals() {
            var previous = SdfTextRendering.Atlas;
            var atlas = new HealingFallbackAtlas();
            SdfTextRendering.SetAtlas(atlas);
            try {
                var font = new FontHandle("Test", 16, 700, FontStyle.Normal);
                DrawTextCommand Cmd() => new DrawTextCommand(
                    new Rect(10, 20, 120, 16), "Open Menu ▸", font,
                    LinearColor.White, TextDecoration.None);

                // Frame 1: cold primary — shape falls back; probation defers caching.
                var l1 = new PaintList(); l1.Add(Cmd());
                SdfTextRendering.PrepareText(l1);
                var b1 = new UIBatcher(); SdfTextRendering.EmitGlyphs(b1, Cmd()); b1.Finish();
                Assert.That(atlas.ShapeCalls, Is.EqualTo(1));

                // Frame 2: still cold — second consecutive fallback caches (tagged).
                var l2 = new PaintList(); l2.Add(Cmd());
                SdfTextRendering.PrepareText(l2);
                var b2 = new UIBatcher(); SdfTextRendering.EmitGlyphs(b2, Cmd()); b2.Finish();
                Assert.That(atlas.ShapeCalls, Is.EqualTo(2));

                // Frame 3: the cache replays (no re-shape) — but PrepareText
                // must KEEP feeding the primary preparer. The old code skipped
                // on any cache hit and deadlocked the run on the fallback face.
                int prepBefore = atlas.PrepareCalls;
                var l3 = new PaintList(); l3.Add(Cmd());
                SdfTextRendering.PrepareText(l3);
                Assert.That(atlas.PrepareCalls, Is.EqualTo(prepBefore + 1),
                    "fallback-cached run must still be prepared");
                var b3 = new UIBatcher(); SdfTextRendering.EmitGlyphs(b3, Cmd()); b3.Finish();
                Assert.That(atlas.ShapeCalls, Is.EqualTo(2), "replayed from cache, no re-shape");

                // Frame 4: the primary heals — preparation ingests the missing
                // glyphs and bumps the atlas version; the snapshot cache clears
                // and the run re-shapes on the primary face.
                atlas.PrimaryReady = true;
                var l4 = new PaintList(); l4.Add(Cmd());
                SdfTextRendering.PrepareText(l4);
                var b4 = new UIBatcher(); SdfTextRendering.EmitGlyphs(b4, Cmd()); b4.Finish();
                Assert.That(atlas.ShapeCalls, Is.EqualTo(3), "re-shaped after the primary healed");
                Assert.That(atlas.LastShapeUsedSecondaryFallback, Is.False);

                // Frame 5: the healthy snapshot now skips preparation entirely.
                int prepAfter = atlas.PrepareCalls;
                var l5 = new PaintList(); l5.Add(Cmd());
                SdfTextRendering.PrepareText(l5);
                Assert.That(atlas.PrepareCalls, Is.EqualTo(prepAfter),
                    "healthy snapshot skips preparation");
                var b5 = new UIBatcher(); SdfTextRendering.EmitGlyphs(b5, Cmd()); b5.Finish();
                Assert.That(atlas.ShapeCalls, Is.EqualTo(3));
            } finally {
                SdfTextRendering.SetAtlas(previous);
                TextRunSnapshotCache.Clear();
            }
        }

        sealed class HealingFallbackAtlas : IGlyphAtlasWithId, IGlyphAtlasVersioned, IGlyphAtlasPreparer, IGlyphAtlasTextRunSnapshotPolicy, IGlyphAtlasShapeSource {
            public bool PrimaryReady;
            public long VersionValue = 1;
            public int ShapeCalls { get; private set; }
            public int PrepareCalls { get; private set; }
            public long Version => VersionValue;
            public bool UseTextRunSnapshots => true;
            public bool LastShapeUsedSecondaryFallback { get; private set; }

            public void PrepareText(DrawTextCommand command) {
                PrepareCalls++;
                // Ingesting the previously-missing characters bumps the
                // version — exactly what the real ATG adapter does once the
                // glyph lands in its atlas.
                if (PrimaryReady) VersionValue++;
            }

            public bool TryShape(DrawTextCommand command, List<SdfGlyphQuad> output) {
                return TryShape(command, output, out _);
            }

            public bool TryShape(DrawTextCommand command, List<SdfGlyphQuad> output, out int atlasId) {
                ShapeCalls++;
                LastShapeUsedSecondaryFallback = !PrimaryReady;
                atlasId = 0;
                output.Add(new SdfGlyphQuad(
                    command.Bounds,
                    command.Color,
                    new Vector2(0.1f, 0),
                    new Vector2(0.15f, 1)));
                return true;
            }
        }

        sealed class VersionedUvAtlas : IGlyphAtlasWithId, IGlyphAtlasVersioned, IGlyphAtlasPreparer, IGlyphAtlasTextRunSnapshotPolicy {
            public long VersionValue;
            public bool UseSnapshots = true;
            public int ShapeCalls { get; private set; }
            public int PrepareCalls { get; private set; }
            public long Version => VersionValue;
            public bool UseTextRunSnapshots => UseSnapshots;

            public void PrepareText(DrawTextCommand command) {
                PrepareCalls++;
                VersionValue = 2;
            }

            public bool TryShape(DrawTextCommand command, List<SdfGlyphQuad> output) {
                return TryShape(command, output, out _);
            }

            public bool TryShape(DrawTextCommand command, List<SdfGlyphQuad> output, out int atlasId) {
                ShapeCalls++;
                atlasId = 0;
                float u0 = VersionValue == 1 ? 0.1f : 0.2f;
                output.Add(new SdfGlyphQuad(
                    command.Bounds,
                    command.Color,
                    new Vector2(u0, 0),
                    new Vector2(u0 + 0.05f, 1)));
                return true;
            }
        }
    }
}
