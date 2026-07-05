#if UNITY_5_3_OR_NEWER
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using Weva.Rendering.URP;

namespace Weva.Tests.Rendering.URP {
    // RC1 — UIImageAtlasRegistry mutations are guarded by an internal lock
    // mirroring UIPaintSourceRegistry. This file pins the concurrent
    // contract: 100 parallel Register calls from worker threads produce a
    // valid mapping with no duplicate ids and no dropped textures.
    public class UIImageAtlasRegistryThreadSafetyTests {
        [SetUp]
        public void SetUp() {
            UIImageAtlasRegistry.ClearAll();
        }

        [TearDown]
        public void TearDown() {
            UIImageAtlasRegistry.ClearAll();
        }

        [Test]
        public void Concurrent_Register_assigns_unique_ids_and_preserves_all_textures_RC1() {
            const int N = 100;
            // Pre-allocate Texture2D instances on the main thread (Texture2D
            // construction itself is not thread-safe). The Register call
            // path is what we're stressing — it must safely accept these
            // instances from any thread.
            var textures = new Texture2D[N];
            for (int i = 0; i < N; i++) {
                textures[i] = new Texture2D(1, 1);
            }

            try {
                var tasks = new Task<int>[N];
                for (int i = 0; i < N; i++) {
                    int idx = i;
                    tasks[i] = Task.Run(() => UIImageAtlasRegistry.Register(textures[idx]));
                }
                Task.WaitAll(tasks);

                // Every Register call must have returned a unique id (Id 0
                // is reserved for "no image"; no parallel Register on a
                // non-null texture should ever return 0).
                var ids = new HashSet<int>();
                for (int i = 0; i < N; i++) {
                    int id = tasks[i].Result;
                    Assert.That(id, Is.GreaterThan(0), "id 0 is reserved");
                    Assert.That(ids.Add(id), Is.True, "duplicate id assigned to two textures");
                }

                // Every input texture must round-trip through GetTextureById.
                // A racy bookkeeping would lose entries or alias the wrong
                // texture under an id.
                for (int i = 0; i < N; i++) {
                    int id = tasks[i].Result;
                    Assert.That(UIImageAtlasRegistry.GetTextureById(id), Is.SameAs(textures[i]));
                }
            } finally {
                for (int i = 0; i < N; i++) {
                    if (textures[i] != null) Object.DestroyImmediate(textures[i]);
                }
            }
        }

        [Test]
        public void Concurrent_Register_same_texture_returns_same_id_RC1() {
            // Two threads racing to register the SAME texture must converge
            // on a single id — the second-arriving Register must see the
            // first's mapping rather than allocate a new one.
            var tex = new Texture2D(1, 1);
            try {
                const int N = 100;
                var tasks = new Task<int>[N];
                for (int i = 0; i < N; i++) tasks[i] = Task.Run(() => UIImageAtlasRegistry.Register(tex));
                Task.WaitAll(tasks);

                int first = tasks[0].Result;
                Assert.That(first, Is.GreaterThan(0));
                for (int i = 1; i < N; i++) {
                    Assert.That(tasks[i].Result, Is.EqualTo(first), "concurrent same-texture register diverged");
                }
            } finally {
                Object.DestroyImmediate(tex);
            }
        }
    }
}
#endif
