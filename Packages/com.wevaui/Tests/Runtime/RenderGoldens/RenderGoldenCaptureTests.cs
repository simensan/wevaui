#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Weva.Tests.RenderGoldens {
    // W6 — GPU golden capture (ROADMAP.md). Renders every Assets/UI sample
    // through the REAL pipeline (URP RenderGraph pass, ATG text, filter
    // compositor — everything the SoftwareRasterizer goldens can't see) to
    // 1280×720 PNGs under Tools/RenderGoldens/unity/, plus a manifest the
    // compare script (Tools/RenderGoldens/compare.mjs) joins against Chrome
    // screenshots. The session that built this watched a backdrop Y-flip, a
    // truncated blur kernel, and a text-baseline shift all hide from the
    // CPU-side suite — this harness exists so the GPU path has ground truth.
    //
    // Run via the test runner filtered on this class (bridge:
    // run_tests test_names=[...RenderGoldenCaptureTests], or CLI:
    // Unity -batchmode -runTests -testPlatform PlayMode
    //       -testFilter RenderGoldenCaptureTests).
    // [Explicit] keeps it OUT of normal suite runs — capture is a tool, not
    // an assertion; the pixel comparison lives in compare.mjs where the
    // thresholds are data, not code.
    [Explicit("GPU golden capture — run on demand; compare.mjs judges output")]
    public class RenderGoldenCaptureTests {
        const int Width = 1280;
        const int Height = 720;
        // Settle frames per sample: cold atlases need a few paints before
        // glyphs stop healing (see TextRunSnapshotCache / fallback-heal).
        const int SettleUpdates = 8;

        static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        static string OutDir => Path.Combine(RepoRoot, "Tools", "RenderGoldens", "unity");

        [UnityTest]
        public IEnumerator Capture_all_samples() {
            Directory.CreateDirectory(OutDir);
            var samples = ListSamples();
            Assert.That(samples.Count, Is.GreaterThan(0), "no Assets/UI/*.html samples found");

            // One camera + document rig reused across samples.
            var camGo = new GameObject("golden-camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 1f);

            var docGo = new GameObject("golden-document");
            var doc = docGo.AddComponent<Weva.WevaDocument>();
            var bf = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
            var docAssetField = doc.GetType().GetField("documentAsset", bf);
            var cssAssetsField = doc.GetType().GetField("stylesheetAssets", bf);
            var prepare = doc.GetType().GetMethod("PrepareForRenderViewport", bf);
            var update = doc.GetType().GetMethod("Update", bf);

            var manifest = new List<string>();
            var rt = new RenderTexture(Width, Height, 24);
            var tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            try {
                foreach (var (name, htmlPath, cssPath) in samples) {
                    var html = UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>(htmlPath);
                    var css = string.IsNullOrEmpty(cssPath)
                        ? null
                        : UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>(cssPath);
                    if (html == null) continue;

                    docAssetField.SetValue(doc, html);
                    cssAssetsField.SetValue(doc, css != null ? new[] { css } : new TextAsset[0]);
                    doc.Rebuild();
                    EnsureRegistered(doc, bf);
                    prepare.Invoke(doc, new object[] { Width, Height });
                    for (int i = 0; i < SettleUpdates; i++) {
                        update.Invoke(doc, null);
                        // Real frames between updates so the render-graph pass
                        // executes and atlases bake — the difference between
                        // this harness and the CPU goldens.
                        yield return null;
                    }

                    var prevTarget = cam.targetTexture;
                    float prevAspect = cam.aspect;
                    cam.targetTexture = rt;
                    cam.aspect = (float)Width / Height;
                    cam.Render();
                    cam.Render();
                    RenderTexture.active = rt;
                    tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                    tex.Apply();
                    RenderTexture.active = null;
                    cam.targetTexture = prevTarget;
                    cam.aspect = prevAspect;

                    string outPath = Path.Combine(OutDir, name + ".png");
                    File.WriteAllBytes(outPath, tex.EncodeToPNG());
                    manifest.Add("{\"name\":\"" + name + "\",\"png\":\"unity/" + name + ".png\"}");
                    Debug.Log($"[RenderGoldens] captured {name}");
                }
            } finally {
                File.WriteAllText(Path.Combine(RepoRoot, "Tools", "RenderGoldens", "manifest.json"),
                    "{\"width\":" + Width + ",\"height\":" + Height + ",\"samples\":[\n  "
                    + string.Join(",\n  ", manifest) + "\n]}\n");
                Object.Destroy(tex);
                Object.Destroy(rt);
                Object.Destroy(docGo);
                Object.Destroy(camGo);
            }
        }

        static void EnsureRegistered(Weva.WevaDocument doc, BindingFlags bf) {
            // Reflection Rebuild() doesn't re-run the component lifecycle, so
            // the paint-source registration flag can be stale (same dance the
            // session's manual probes used).
            var regField = doc.GetType().GetField("registered", bf);
            if (regField == null || (bool)regField.GetValue(doc)) return;
            var reg = typeof(Weva.Rendering.UIPaintSourceRegistry)
                .GetMethod("Register", BindingFlags.Public | BindingFlags.Static);
            reg.Invoke(null, new object[] { doc });
            regField.SetValue(doc, true);
        }

        static List<(string name, string html, string css)> ListSamples() {
            var result = new List<(string, string, string)>();
            foreach (string guid in UnityEditor.AssetDatabase.FindAssets("t:TextAsset", new[] { "Assets/UI" })) {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".html")) continue;
                string name = Path.GetFileNameWithoutExtension(path);
                string css = path.Substring(0, path.Length - 5) + ".css";
                if (!File.Exists(Path.Combine(RepoRoot, css))) css = null;
                result.Add((name, path, css));
            }
            result.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
            return result;
        }
    }
}
#endif
