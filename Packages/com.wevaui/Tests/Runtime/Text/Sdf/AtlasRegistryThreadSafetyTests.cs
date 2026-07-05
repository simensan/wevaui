using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Weva.Text.Sdf;
using Weva.Text.TextCore;

namespace Weva.Tests.Text.Sdf {
    // RC2 — AtlasRegistry mutations are guarded by an internal lock. This
    // file pins the concurrent contract: 100 parallel RegisterAtlas calls
    // assign 100 distinct atlas ids and produce a consistent
    // (face -> atlas -> id) view for every input.
    public class AtlasRegistryThreadSafetyTests {
        [SetUp]
        public void SetUp() {
            AtlasRegistry.Clear();
        }

        [TearDown]
        public void TearDown() {
            AtlasRegistry.Clear();
        }

        [Test]
        public void Concurrent_RegisterAtlas_assigns_unique_ids_RC2() {
            const int N = 100;
            // Distinct faces + distinct atlases so every Register call
            // must succeed in allocating a new id. This stresses the
            // (atlasIds, atlasById, nextAtlasId) triple update path —
            // a non-atomic increment under contention would either skip
            // ids or alias two atlases to the same id.
            var faces = new FaceInfo[N];
            var atlases = new GlyphAtlas[N];
            for (int i = 0; i < N; i++) {
                faces[i] = new FaceInfo("F" + i, "/p/" + i, 400, FaceInfo.StyleNormal);
                atlases[i] = new GlyphAtlas();
            }

            var tasks = new Task<bool>[N];
            for (int i = 0; i < N; i++) {
                int idx = i;
                tasks[i] = Task.Run(() => AtlasRegistry.RegisterAtlas(faces[idx], atlases[idx]));
            }
            Task.WaitAll(tasks);

            // Every Register call must report success.
            for (int i = 0; i < N; i++) {
                Assert.That(tasks[i].Result, Is.True, "RegisterAtlas reported failure for face " + i);
            }

            // Every face must resolve to its own atlas.
            for (int i = 0; i < N; i++) {
                Assert.That(AtlasRegistry.GetAtlas(faces[i]), Is.SameAs(atlases[i]),
                    "face " + i + " does not map to its registered atlas");
            }

            // Every atlas must have a unique non-zero id.
            var ids = new HashSet<int>();
            for (int i = 0; i < N; i++) {
                int id = AtlasRegistry.GetAtlasId(atlases[i]);
                Assert.That(id, Is.GreaterThan(0), "atlas " + i + " has no id");
                Assert.That(ids.Add(id), Is.True, "duplicate atlas id assigned");
            }

            Assert.That(AtlasRegistry.Count, Is.EqualTo(N));
            Assert.That(AtlasRegistry.DistinctAtlasCount, Is.EqualTo(N));
        }

        [Test]
        public void Concurrent_MarkColorAtlas_does_not_corrupt_set_RC2() {
            const int N = 100;
            // Pre-register atlases on the main thread, then race to mark
            // them all as color from worker threads. The HashSet<int> Add
            // path must be safely serialised — a contended Add can corrupt
            // the bucket array and either drop entries or NRE on read.
            var atlases = new GlyphAtlas[N];
            for (int i = 0; i < N; i++) {
                atlases[i] = new GlyphAtlas();
                AtlasRegistry.RegisterAtlas(new FaceInfo("F" + i, "/p/" + i, 400, FaceInfo.StyleNormal), atlases[i]);
            }

            var tasks = new Task[N];
            for (int i = 0; i < N; i++) {
                int idx = i;
                tasks[i] = Task.Run(() => AtlasRegistry.MarkColorAtlas(atlases[idx]));
            }
            Task.WaitAll(tasks);

            for (int i = 0; i < N; i++) {
                int id = AtlasRegistry.GetAtlasId(atlases[i]);
                Assert.That(AtlasRegistry.IsColorAtlasId(id), Is.True,
                    "atlas " + i + " was not marked color");
            }
        }
    }
}
